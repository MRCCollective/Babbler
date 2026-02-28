using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Babbler.Web.Hubs;
using Babbler.Web.Models;
using Babbler.Web.Options;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace Babbler.Web.Services;

public sealed class TranslationSessionService : IAsyncDisposable
{
    private const string DefaultTargetLanguage = "en";
    private const string AccessCookiePrefix = "babbler_display_access_";
    private static readonly TimeSpan StoppedRoomRetentionWindow = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan UsagePersistInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan UsageMonitorTickInterval = TimeSpan.FromSeconds(1);
    private static readonly string[] SupportedTargetLanguages =
    [
        "en",
        "sv",
        "es",
        "fr",
        "de",
        "it",
        "ja"
    ];

    private static readonly char[] RoomIdAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789".ToCharArray();

    private readonly IHubContext<TranslationHub> _hubContext;
    private readonly ILogger<TranslationSessionService> _logger;
    private readonly IMonthlyUsageStore _monthlyUsageStore;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly SpeechOptions _speechOptions;
    private readonly TimeSpan _freeMinutesLimit;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, RoomSession> _rooms =
        new(StringComparer.OrdinalIgnoreCase);

    private TimeSpan _freeMinutesUsed = TimeSpan.Zero;
    private bool _usageLoaded;
    private string _usageMonthCode = GetCurrentMonthCode();
    private CancellationTokenSource? _usagePersistCts;
    private string? _speechAuthToken;
    private DateTimeOffset _speechAuthTokenExpiresAtUtc;

    public TranslationSessionService(
        IHubContext<TranslationHub> hubContext,
        ILogger<TranslationSessionService> logger,
        IMonthlyUsageStore monthlyUsageStore,
        IHttpClientFactory httpClientFactory,
        IOptions<SpeechOptions> speechOptions,
        IOptions<SessionLimitsOptions> sessionLimitsOptions)
    {
        _hubContext = hubContext;
        _logger = logger;
        _monthlyUsageStore = monthlyUsageStore;
        _httpClientFactory = httpClientFactory;
        _speechOptions = speechOptions.Value;
        _freeMinutesLimit = TimeSpan.FromMinutes(Math.Max(0, sessionLimitsOptions.Value.FreeMinutesLimit));
    }

    public async Task<RoomAccessInfo> CreateRoomAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            PruneExpiredStoppedRooms(DateTimeOffset.UtcNow);
            var room = CreateRoomInternal();
            return new RoomAccessInfo(room.RoomId, room.Pin);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<RoomAccessInfo> GetRoomAccessInfoAsync(
        string roomId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var room = GetRoomOrThrow(roomId);
            return new RoomAccessInfo(room.RoomId, room.Pin);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<RoomSummary>> GetRoomsAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            PruneExpiredStoppedRooms(DateTimeOffset.UtcNow);
            return _rooms.Values
                .OrderByDescending(room => room.IsRunning)
                .ThenByDescending(room => room.LastStateChangedAtUtc)
                .Select(BuildRoomSummary)
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<BrowserSpeechToken> GetBrowserSpeechTokenAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureSpeechConfigured();

            var now = DateTimeOffset.UtcNow;
            if (!string.IsNullOrWhiteSpace(_speechAuthToken) &&
                _speechAuthTokenExpiresAtUtc > now.AddMinutes(1))
            {
                return new BrowserSpeechToken(
                    _speechAuthToken,
                    _speechOptions.Region,
                    _speechAuthTokenExpiresAtUtc);
            }
        }
        finally
        {
            _gate.Release();
        }

        var token = await FetchSpeechTokenAsync(cancellationToken);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            _speechAuthToken = token.Token;
            _speechAuthTokenExpiresAtUtc = token.ExpiresAtUtc;
            return token;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> VerifyRoomPinAsync(
        string roomId,
        string pin,
        HttpContext httpContext,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var room = GetRoomOrThrow(roomId);
            var normalizedPin = NormalizePin(pin);
            if (!string.Equals(room.Pin, normalizedPin, StringComparison.Ordinal))
            {
                return false;
            }

            GrantRoomAccess(room, httpContext);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task PublishClientTranslationAsync(
        string roomId,
        ClientTranslationUpdate payload,
        CancellationToken cancellationToken = default)
    {
        TranslationUpdate? update;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var room = GetRoomOrThrow(roomId);
            if (!room.IsRunning)
            {
                return;
            }

            var sourceText = payload.SourceText?.Trim();
            var targetLanguage = NormalizeTargetLanguage(room.TargetLanguage);
            var translations = NormalizeTranslations(payload.Translations);
            var translatedText = GetTranslatedTextForTarget(translations, targetLanguage);
            if (string.IsNullOrWhiteSpace(translatedText) &&
                !string.IsNullOrWhiteSpace(sourceText))
            {
                // Fallback to source text (caption mode) when target translation is absent.
                translatedText = sourceText;
            }

            if (string.IsNullOrWhiteSpace(sourceText) &&
                string.IsNullOrWhiteSpace(translatedText) &&
                (translations is null || translations.Count == 0))
            {
                return;
            }

            update = new TranslationUpdate(
                string.IsNullOrWhiteSpace(sourceText) ? null : sourceText,
                string.IsNullOrWhiteSpace(translatedText) ? null : translatedText,
                payload.SourceLanguage ?? room.SourceLanguage,
                targetLanguage,
                translations,
                payload.IsFinal,
                DateTimeOffset.UtcNow,
                null);

            room.LastClientPublishAtUtc = update.TimestampUtc;
            room.LastClientSourceText = update.SourceText;
            room.LastClientTranslatedText = update.TranslatedText;
        }
        finally
        {
            _gate.Release();
        }

        if (update is not null)
        {
            await _hubContext.Clients.Group(roomId)
                .SendAsync("translationUpdate", update, cancellationToken);
        }
    }

    public bool HasDisplayAccess(string roomId, HttpContext httpContext)
    {
        if (!TryNormalizeRoomId(roomId, out var normalizedRoomId))
        {
            return false;
        }

        if (!_rooms.TryGetValue(normalizedRoomId, out var room))
        {
            return false;
        }

        var cookieName = BuildAccessCookieName(normalizedRoomId);
        if (!httpContext.Request.Cookies.TryGetValue(cookieName, out var providedToken) ||
            string.IsNullOrWhiteSpace(providedToken))
        {
            return false;
        }

        var provided = Encoding.UTF8.GetBytes(providedToken);
        var expected = Encoding.UTF8.GetBytes(room.AccessToken);
        if (provided.Length != expected.Length)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(provided, expected);
    }

    public async Task<SessionStatus> GetStatusAsync(
        string roomId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureUsageLoadedAsync(cancellationToken);
            EnsureCurrentUsageMonth();

            var room = GetRoomOrThrow(roomId);
            return BuildStatus(room);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<RoomDiagnostics> GetRoomDiagnosticsAsync(
        string roomId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureUsageLoadedAsync(cancellationToken);
            EnsureCurrentUsageMonth();

            var room = GetRoomOrThrow(roomId);
            var status = BuildStatus(room);

            return new RoomDiagnostics(
                room.RoomId,
                room.IsRunning,
                room.SourceLanguage,
                room.TargetLanguage,
                room.LastStateChangedAtUtc,
                room.LastStoppedAtUtc,
                room.LastStopReason,
                room.LastClientPublishAtUtc,
                room.LastClientSourceText,
                room.LastClientTranslatedText,
                TranslationHub.GetRoomConnectionCount(room.RoomId),
                status.FreeMinutesUsed,
                status.FreeMinutesRemaining,
                DateTimeOffset.UtcNow);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task PublishTestCaptionAsync(
        string roomId,
        string? text,
        CancellationToken cancellationToken = default)
    {
        TranslationUpdate update;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var room = GetRoomOrThrow(roomId);
            var safeText = string.IsNullOrWhiteSpace(text)
                ? $"Test caption {DateTimeOffset.UtcNow:HH:mm:ss}"
                : text.Trim();

            var sourceLanguage = room.SourceLanguage ?? "en-US";
            var targetLanguage = NormalizeTargetLanguage(room.TargetLanguage);
            var now = DateTimeOffset.UtcNow;
            update = new TranslationUpdate(
                safeText,
                safeText,
                sourceLanguage,
                targetLanguage,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [targetLanguage] = safeText
                },
                true,
                now,
                null);

            room.LastClientPublishAtUtc = now;
            room.LastClientSourceText = safeText;
            room.LastClientTranslatedText = safeText;
        }
        finally
        {
            _gate.Release();
        }

        await _hubContext.Clients.Group(roomId)
            .SendAsync("translationUpdate", update, cancellationToken);
    }

    public async Task StartAsync(
        string roomId,
        string sourceLanguage,
        string? targetLanguage,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureUsageLoadedAsync(cancellationToken);
            EnsureCurrentUsageMonth();

            var room = GetRoomOrThrow(roomId);
            await StartRoomInternalAsync(
                room,
                sourceLanguage,
                NormalizeTargetLanguage(targetLanguage),
                cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetTargetLanguageAsync(
        string roomId,
        string targetLanguage,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureUsageLoadedAsync(cancellationToken);
            EnsureCurrentUsageMonth();

            var room = GetRoomOrThrow(roomId);
            var normalizedTarget = NormalizeTargetLanguage(targetLanguage);

            if (room.IsRunning)
            {
                if (string.Equals(room.TargetLanguage, normalizedTarget, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                room.TargetLanguage = normalizedTarget;
                await PublishSystemMessageAsync(room.RoomId, $"Target language switched to {normalizedTarget}.");
                return;
            }

            room.TargetLanguage = normalizedTarget;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync(
        string roomId,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureUsageLoadedAsync(cancellationToken);
            EnsureCurrentUsageMonth();

            var room = GetRoomOrThrow(roomId);
            var normalizedReason = NormalizeStopReason(reason);
            await StopRoomInternalAsync(
                room,
                BuildStopMessage(normalizedReason),
                normalizedReason);
        }
        finally
        {
            _gate.Release();
        }
    }

    private SessionStatus BuildStatus(RoomSession room)
    {
        var used = GetFreeMinutesUsed();
        var remaining = GetFreeMinutesRemaining(used);

        return new SessionStatus(
            room.IsRunning,
            room.SourceLanguage,
            room.TargetLanguage,
            Math.Round(used.TotalMinutes, 2),
            Math.Round(_freeMinutesLimit.TotalMinutes, 2),
            Math.Round(remaining.TotalMinutes, 2),
            remaining <= TimeSpan.Zero);
    }

    private static RoomSummary BuildRoomSummary(RoomSession room) =>
        new(
            room.RoomId,
            room.IsRunning,
            room.SourceLanguage,
            room.TargetLanguage,
            room.LastStateChangedAtUtc,
            room.LastStoppedAtUtc);

    private RoomSession CreateRoomInternal()
    {
        var now = DateTimeOffset.UtcNow;
        for (var attempt = 0; attempt < 1000; attempt++)
        {
            var roomId = GenerateRoomId();
            var session = new RoomSession
            {
                RoomId = roomId,
                Pin = GeneratePin(),
                AccessToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
                TargetLanguage = DefaultTargetLanguage,
                LastStateChangedAtUtc = now
            };

            if (_rooms.TryAdd(roomId, session))
            {
                return session;
            }
        }

        throw new InvalidOperationException("Unable to allocate a unique room ID.");
    }

    private RoomSession GetRoomOrThrow(string roomId)
    {
        var normalizedRoomId = NormalizeRoomId(roomId);
        if (_rooms.TryGetValue(normalizedRoomId, out var room))
        {
            return room;
        }

        throw new InvalidOperationException($"Room '{normalizedRoomId}' was not found.");
    }

    private void PruneExpiredStoppedRooms(DateTimeOffset nowUtc)
    {
        var cutoffUtc = nowUtc - StoppedRoomRetentionWindow;
        foreach (var pair in _rooms)
        {
            var room = pair.Value;
            if (room.IsRunning)
            {
                continue;
            }

            if (room.LastStoppedAtUtc is null || room.LastStoppedAtUtc > cutoffUtc)
            {
                continue;
            }

            _rooms.TryRemove(pair.Key, out _);
        }
    }

    private async Task StartRoomInternalAsync(
        RoomSession room,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        EnsureSpeechConfigured();
        EnsureSupportedTargetLanguage(targetLanguage);

        if (room.IsRunning)
        {
            await StopRoomInternalAsync(room, stopReason: "restart");
        }

        var remainingFreeTime = GetFreeMinutesRemaining();
        if (remainingFreeTime <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                "Free translation minutes are exhausted. Start is blocked.");
        }
        room.IsRunning = true;
        room.SourceLanguage = sourceLanguage;
        room.TargetLanguage = targetLanguage;
        var startedAt = DateTimeOffset.UtcNow;
        room.SessionStartedAtUtc = startedAt;
        room.LastStateChangedAtUtc = startedAt;
        room.LastStoppedAtUtc = null;
        room.LastStopReason = null;

        StartUsagePersistMonitor();

        try
        {
            await PublishSystemMessageAsync(
                room.RoomId,
                $"Microphone stream connected ({sourceLanguage} -> {targetLanguage}). " +
                $"Free time left: {remainingFreeTime.TotalMinutes:F2} minutes.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to publish start message for room {RoomId}.",
                room.RoomId);
        }
    }

    private async Task StopRoomInternalAsync(
        RoomSession room,
        string stopMessage = "Microphone translation stopped.",
        string? stopReason = null,
        bool persistUsage = true)
    {
        var usageChanged = CaptureRoomUsedFreeMinutes(room);
        var stoppedAt = DateTimeOffset.UtcNow;
        room.IsRunning = false;
        room.SessionStartedAtUtc = null;
        room.LastStateChangedAtUtc = stoppedAt;
        room.LastStoppedAtUtc = stoppedAt;
        room.LastStopReason = NormalizeStopReason(stopReason);

        try
        {
            await PublishSystemMessageAsync(room.RoomId, stopMessage);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to publish stop message for room {RoomId}.",
                room.RoomId);
        }

        if (usageChanged && persistUsage)
        {
            await PersistUsageAsync(GetFreeMinutesUsed(), CancellationToken.None);
        }
    }

    private static string BuildStopMessage(string? reason)
    {
        const string defaultMessage = "Microphone translation stopped.";
        if (string.IsNullOrWhiteSpace(reason))
        {
            return defaultMessage;
        }

        var normalizedReason = reason.Trim();
        return $"{defaultMessage} (reason: {normalizedReason})";
    }

    private static string? NormalizeStopReason(string? reason)
    {
        return string.IsNullOrWhiteSpace(reason)
            ? null
            : reason.Trim();
    }

    private void EnsureSpeechConfigured()
    {
        if (string.IsNullOrWhiteSpace(_speechOptions.Key) ||
            string.IsNullOrWhiteSpace(_speechOptions.Region))
        {
            throw new InvalidOperationException(
                "Speech.Key and Speech.Region must be set in configuration before starting.");
        }
    }

    private async Task<BrowserSpeechToken> FetchSpeechTokenAsync(CancellationToken cancellationToken)
    {
        var httpClient = _httpClientFactory.CreateClient();
        var endpoint = $"https://{_speechOptions.Region}.api.cognitive.microsoft.com/sts/v1.0/issueToken";

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Add("Ocp-Apim-Subscription-Key", _speechOptions.Key);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/plain"));
        using var response = await httpClient.SendAsync(request, cancellationToken);
        var token = (await response.Content.ReadAsStringAsync(cancellationToken)).Trim();
        if (!response.IsSuccessStatusCode || string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException(
                $"Failed to fetch browser speech token ({(int)response.StatusCode}).");
        }

        return new BrowserSpeechToken(
            token,
            _speechOptions.Region,
            DateTimeOffset.UtcNow.AddMinutes(9));
    }

    private static string? GetTranslatedTextForTarget(
        IReadOnlyDictionary<string, string>? translations,
        string targetLanguage)
    {
        if (translations is null || translations.Count == 0)
        {
            return null;
        }

        foreach (var pair in translations)
        {
            if (string.Equals(pair.Key, targetLanguage, StringComparison.OrdinalIgnoreCase))
            {
                return pair.Value;
            }
        }

        foreach (var pair in translations)
        {
            if (pair.Key.StartsWith(targetLanguage, StringComparison.OrdinalIgnoreCase))
            {
                return pair.Value;
            }
        }

        foreach (var pair in translations)
        {
            if (!string.IsNullOrWhiteSpace(pair.Value))
            {
                return pair.Value;
            }
        }

        return null;
    }

    private static IReadOnlyDictionary<string, string>? NormalizeTranslations(
        IReadOnlyDictionary<string, string>? translations)
    {
        if (translations is null || translations.Count == 0)
        {
            return null;
        }

        var normalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in translations)
        {
            var key = pair.Key?.Trim();
            var value = pair.Value?.Trim();
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            normalized[key] = value;
        }

        return normalized.Count == 0 ? null : normalized;
    }

    private static string NormalizeTargetLanguage(string? targetLanguage)
    {
        if (!string.IsNullOrWhiteSpace(targetLanguage))
        {
            var normalizedTargetLanguage = targetLanguage.Trim().ToLowerInvariant();
            EnsureSupportedTargetLanguage(normalizedTargetLanguage);
            return normalizedTargetLanguage;
        }

        return DefaultTargetLanguage;
    }

    private static void EnsureSupportedTargetLanguage(string targetLanguage)
    {
        if (!SupportedTargetLanguages.Contains(targetLanguage, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Target language '{targetLanguage}' is not supported.");
        }
    }

    private bool CaptureRoomUsedFreeMinutes(RoomSession room)
    {
        if (room.SessionStartedAtUtc is not { } startedAtUtc)
        {
            return false;
        }

        _freeMinutesUsed += DateTimeOffset.UtcNow - startedAtUtc;
        room.SessionStartedAtUtc = null;

        if (_freeMinutesUsed > _freeMinutesLimit)
        {
            _freeMinutesUsed = _freeMinutesLimit;
        }

        if (_freeMinutesUsed < TimeSpan.Zero)
        {
            _freeMinutesUsed = TimeSpan.Zero;
        }

        return true;
    }

    private TimeSpan GetFreeMinutesUsed()
    {
        var used = _freeMinutesUsed;
        var now = DateTimeOffset.UtcNow;
        foreach (var room in _rooms.Values)
        {
            if (!room.IsRunning || room.SessionStartedAtUtc is not { } startedAtUtc)
            {
                continue;
            }

            used += now - startedAtUtc;
        }

        if (used > _freeMinutesLimit)
        {
            used = _freeMinutesLimit;
        }

        return used < TimeSpan.Zero ? TimeSpan.Zero : used;
    }

    private TimeSpan GetFreeMinutesRemaining(TimeSpan? used = null)
    {
        if (_freeMinutesLimit <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        var remaining = _freeMinutesLimit - (used ?? GetFreeMinutesUsed());
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    private void StartUsagePersistMonitor()
    {
        if (_usagePersistCts is not null || !HasRunningRooms())
        {
            return;
        }

        var cts = new CancellationTokenSource();
        _usagePersistCts = cts;
        _ = PersistUsagePeriodicallyAsync(cts.Token);
    }

    private void CancelUsagePersistMonitor()
    {
        if (_usagePersistCts is null)
        {
            return;
        }

        _usagePersistCts.Cancel();
        _usagePersistCts.Dispose();
        _usagePersistCts = null;
    }

    private async Task PersistUsagePeriodicallyAsync(CancellationToken cancellationToken)
    {
        var nextPersistAt = DateTimeOffset.UtcNow.Add(UsagePersistInterval);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(UsageMonitorTickInterval, cancellationToken);

                TimeSpan usedSnapshot = TimeSpan.Zero;
                var shouldPersist = false;
                var shouldContinue = true;
                List<RoomSession>? roomsToStop = null;

                await _gate.WaitAsync(cancellationToken);
                try
                {
                    EnsureCurrentUsageMonth();

                    if (!HasRunningRooms())
                    {
                        shouldContinue = false;
                        continue;
                    }

                    usedSnapshot = GetFreeMinutesUsed();
                    if (usedSnapshot >= _freeMinutesLimit)
                    {
                        roomsToStop = _rooms.Values.Where(r => r.IsRunning).ToList();
                        foreach (var room in roomsToStop)
                        {
                            await StopRoomInternalAsync(
                                room,
                                "Free translation minutes are used up. Session stopped.",
                                stopReason: "free-limit",
                                persistUsage: false);
                        }

                        usedSnapshot = GetFreeMinutesUsed();
                        shouldPersist = true;
                        shouldContinue = HasRunningRooms();
                        nextPersistAt = DateTimeOffset.UtcNow.Add(UsagePersistInterval);
                    }
                    else if (DateTimeOffset.UtcNow >= nextPersistAt)
                    {
                        shouldPersist = true;
                        nextPersistAt = DateTimeOffset.UtcNow.Add(UsagePersistInterval);
                    }
                }
                finally
                {
                    _gate.Release();
                }

                if (shouldPersist)
                {
                    await PersistUsageAsync(usedSnapshot, cancellationToken);
                }

                if (!shouldContinue)
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on service shutdown.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Background usage persistence loop failed.");
        }
        finally
        {
            await _gate.WaitAsync();
            try
            {
                if (_usagePersistCts?.Token == cancellationToken)
                {
                    _usagePersistCts.Dispose();
                    _usagePersistCts = null;
                }

                if (HasRunningRooms() && _usagePersistCts is null)
                {
                    StartUsagePersistMonitor();
                }
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    private Task PublishSystemMessageAsync(string roomId, string message)
    {
        _rooms.TryGetValue(roomId, out var room);
        var update = new TranslationUpdate(
            null,
            null,
            room?.SourceLanguage,
            room?.TargetLanguage,
            null,
            true,
            DateTimeOffset.UtcNow,
            message);

        return _hubContext.Clients.Group(roomId).SendAsync("translationUpdate", update);
    }

    private async Task EnsureUsageLoadedAsync(CancellationToken cancellationToken)
    {
        if (_usageLoaded)
        {
            return;
        }

        _freeMinutesUsed = await _monthlyUsageStore.GetUsedAsync(cancellationToken);
        if (_freeMinutesUsed < TimeSpan.Zero)
        {
            _freeMinutesUsed = TimeSpan.Zero;
        }
        else if (_freeMinutesUsed > _freeMinutesLimit)
        {
            _freeMinutesUsed = _freeMinutesLimit;
        }

        _usageMonthCode = GetCurrentMonthCode();
        _usageLoaded = true;
    }

    private async Task PersistUsageAsync(TimeSpan usedSnapshot, CancellationToken cancellationToken)
    {
        if (!_usageLoaded)
        {
            return;
        }

        await _monthlyUsageStore.SaveUsedAsync(usedSnapshot, cancellationToken);
    }

    private void EnsureCurrentUsageMonth()
    {
        var currentMonthCode = GetCurrentMonthCode();
        if (string.Equals(_usageMonthCode, currentMonthCode, StringComparison.Ordinal))
        {
            return;
        }

        _usageMonthCode = currentMonthCode;
        _freeMinutesUsed = TimeSpan.Zero;

        var now = DateTimeOffset.UtcNow;
        foreach (var room in _rooms.Values.Where(r => r.IsRunning))
        {
            room.SessionStartedAtUtc = now;
        }
    }

    private bool HasRunningRooms() => _rooms.Values.Any(r => r.IsRunning);

    private static string GetCurrentMonthCode() =>
        DateTimeOffset.UtcNow.ToString("yyMM");

    private static string GeneratePin() =>
        RandomNumberGenerator.GetInt32(100000, 1000000).ToString("D6");

    private static string NormalizePin(string pin) =>
        new(pin.Where(char.IsDigit).ToArray());

    private static string GenerateRoomId()
    {
        Span<char> chars = stackalloc char[6];
        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = RoomIdAlphabet[RandomNumberGenerator.GetInt32(RoomIdAlphabet.Length)];
        }

        return new string(chars);
    }

    private static bool TryNormalizeRoomId(string roomId, out string normalizedRoomId)
    {
        normalizedRoomId = (roomId ?? string.Empty).Trim().ToUpperInvariant();
        if (normalizedRoomId.Length is < 4 or > 24)
        {
            return false;
        }

        foreach (var character in normalizedRoomId)
        {
            if (!char.IsLetterOrDigit(character))
            {
                return false;
            }
        }

        return true;
    }

    private static string NormalizeRoomId(string roomId)
    {
        if (TryNormalizeRoomId(roomId, out var normalizedRoomId))
        {
            return normalizedRoomId;
        }

        throw new InvalidOperationException("Room ID is invalid.");
    }

    private static string BuildAccessCookieName(string roomId) =>
        $"{AccessCookiePrefix}{roomId.ToLowerInvariant()}";

    private static void GrantRoomAccess(RoomSession room, HttpContext httpContext)
    {
        httpContext.Response.Cookies.Append(
            BuildAccessCookieName(room.RoomId),
            room.AccessToken,
            new CookieOptions
            {
                HttpOnly = true,
                Secure = httpContext.Request.IsHttps,
                SameSite = SameSiteMode.Lax,
                IsEssential = true,
                Expires = DateTimeOffset.UtcNow.AddHours(12)
            });
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            foreach (var room in _rooms.Values.Where(r => r.IsRunning))
            {
                await StopRoomInternalAsync(room, persistUsage: false);
            }

            await PersistUsageAsync(GetFreeMinutesUsed(), CancellationToken.None);
            CancelUsagePersistMonitor();
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    private sealed class RoomSession
    {
        public required string RoomId { get; init; }

        public required string Pin { get; init; }

        public required string AccessToken { get; init; }

        public bool IsRunning { get; set; }

        public string? SourceLanguage { get; set; }

        public string? TargetLanguage { get; set; } = DefaultTargetLanguage;

        public DateTimeOffset? SessionStartedAtUtc { get; set; }

        public DateTimeOffset LastStateChangedAtUtc { get; set; }

        public DateTimeOffset? LastStoppedAtUtc { get; set; }

        public string? LastStopReason { get; set; }

        public DateTimeOffset? LastClientPublishAtUtc { get; set; }

        public string? LastClientSourceText { get; set; }

        public string? LastClientTranslatedText { get; set; }
    }
}
