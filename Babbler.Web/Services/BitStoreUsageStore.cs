using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Babbler.Web.Options;
using Microsoft.Extensions.Options;

namespace Babbler.Web.Services;

public sealed class BitStoreUsageStore : IMonthlyUsageStore
{
    private const int MaxBase36FourChars = 1679615;
    private const string Base36Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<BitStoreUsageStore> _logger;
    private readonly BitStoreOptions _options;
    private readonly SemaphoreSlim _cacheGate = new(1, 1);
    private int? _cachedRecordId;
    private bool _recordCacheLoaded;

    public BitStoreUsageStore(
        IHttpClientFactory httpClientFactory,
        ILogger<BitStoreUsageStore> logger,
        IOptions<BitStoreOptions> options)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _options = options.Value;
    }

    public async Task<TimeSpan> GetUsedAsync(CancellationToken cancellationToken = default)
    {
        if (!CanRead())
        {
            return TimeSpan.Zero;
        }

        try
        {
            var latestRecord = await GetLatestRecordAsync(cancellationToken);
            await _cacheGate.WaitAsync(cancellationToken);
            try
            {
                _cachedRecordId = latestRecord.RecordId;
                _recordCacheLoaded = true;
            }
            finally
            {
                _cacheGate.Release();
            }

            if (!TryDecodeUsedSeconds(latestRecord.EncodedValue, out var usedSeconds))
            {
                _logger.LogWarning(
                    "BitStore usage payload had invalid format: {Payload}",
                    latestRecord.EncodedValue);
                return TimeSpan.Zero;
            }

            return TimeSpan.FromSeconds(usedSeconds);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load usage from BitStore.");
            return TimeSpan.Zero;
        }
    }

    public async Task SaveUsedAsync(TimeSpan used, CancellationToken cancellationToken = default)
    {
        if (!CanWrite())
        {
            return;
        }

        try
        {
            var seconds = Math.Clamp((int)Math.Round(used.TotalSeconds), 0, MaxBase36FourChars);
            var encoded = EncodeUsedSeconds(seconds);
            await _cacheGate.WaitAsync(cancellationToken);
            try
            {
                if (!_recordCacheLoaded)
                {
                    var latestRecord = await GetLatestRecordAsync(cancellationToken);
                    _cachedRecordId = latestRecord.RecordId;
                    _recordCacheLoaded = true;
                }

                if (_cachedRecordId is { } recordId)
                {
                    if (await TryUpdateRecordAsync(recordId, encoded, cancellationToken))
                    {
                        return;
                    }
                }

                _cachedRecordId = await CreateRecordAsync(encoded, cancellationToken);
            }
            finally
            {
                _cacheGate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save usage to BitStore.");
        }
    }

    private async Task<LatestRecordData> GetLatestRecordAsync(CancellationToken cancellationToken)
    {
        var client = CreateClient();
        var url = BuildUrl("latest");
        using var response = await client.GetAsync(url, cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return new LatestRecordData(null, string.Empty);
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "BitStore usage read failed. Status: {StatusCode}",
                response.StatusCode);
            return new LatestRecordData(null, string.Empty);
        }

        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        TryExtractLatestRecord(payload, out var recordData);
        return recordData;
    }

    private async Task<bool> TryUpdateRecordAsync(int recordId, string encodedValue, CancellationToken cancellationToken)
    {
        var client = CreateClient();
        var url = BuildUrl($"records/{recordId}");
        using var request = new HttpRequestMessage(HttpMethod.Put, url);
        request.Headers.Add("X-BitStore-Key", _options.WriteKey.Trim());
        request.Content = JsonContent.Create(new { value = encodedValue });

        using var response = await client.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return true;
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            _logger.LogInformation(
                "BitStore usage record {RecordId} no longer exists. Recreating.",
                recordId);
            return false;
        }

        var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
        _logger.LogWarning(
            "BitStore usage update failed. Status: {StatusCode}. Body: {Body}",
            response.StatusCode,
            errorBody);
        return false;
    }

    private async Task<int?> CreateRecordAsync(string encodedValue, CancellationToken cancellationToken)
    {
        var client = CreateClient();
        var url = BuildUrl("records");
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Add("X-BitStore-Key", _options.WriteKey.Trim());
        request.Content = JsonContent.Create(new { value = encodedValue });

        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogWarning(
                "BitStore usage create failed. Status: {StatusCode}. Body: {Body}",
                response.StatusCode,
                errorBody);
            return null;
        }

        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        if (TryExtractRecordId(payload, out var recordId))
        {
            return recordId;
        }

        _logger.LogWarning("BitStore usage create response did not include record id.");
        return null;
    }

    private bool CanRead() =>
        _options.Enabled &&
        !string.IsNullOrWhiteSpace(_options.BaseUrl) &&
        !string.IsNullOrWhiteSpace(_options.BucketSlug);

    private bool CanWrite() =>
        CanRead() &&
        !string.IsNullOrWhiteSpace(_options.WriteKey);

    private HttpClient CreateClient()
    {
        var client = _httpClientFactory.CreateClient("bitstore");
        client.Timeout = TimeSpan.FromSeconds(8);
        return client;
    }

    private string BuildUrl(string suffix)
    {
        var baseUrl = (_options.BaseUrl ?? string.Empty).Trim().TrimEnd('/');
        var slug = Uri.EscapeDataString((_options.BucketSlug ?? string.Empty).Trim());
        return $"{baseUrl}/api/buckets/{slug}/{suffix}";
    }

    private static string EncodeUsedSeconds(int usedSeconds)
    {
        var monthCode = DateTimeOffset.UtcNow.ToString("yyMM", CultureInfo.InvariantCulture);
        var secondsCode = ToBase36(usedSeconds).PadLeft(4, '0');
        return $"{monthCode}{secondsCode}";
    }

    private static bool TryDecodeUsedSeconds(string encoded, out int usedSeconds)
    {
        usedSeconds = 0;
        if (string.IsNullOrWhiteSpace(encoded))
        {
            return false;
        }

        var trimmed = encoded.Trim();
        if (trimmed.Length != 8)
        {
            return false;
        }

        var monthCode = trimmed[..4];
        var currentMonthCode = DateTimeOffset.UtcNow.ToString("yyMM", CultureInfo.InvariantCulture);
        if (!string.Equals(monthCode, currentMonthCode, StringComparison.Ordinal))
        {
            usedSeconds = 0;
            return true;
        }

        return TryParseBase36(trimmed[4..], out usedSeconds) && usedSeconds >= 0;
    }

    private static bool TryExtractEncodedValue(string payload, out string encoded)
    {
        encoded = string.Empty;
        if (string.IsNullOrWhiteSpace(payload))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (TryGetValueFromElement(root, out encoded))
            {
                return true;
            }

            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            foreach (var propertyName in new[] { "record", "latestRecord", "data" })
            {
                if (!root.TryGetProperty(propertyName, out var child))
                {
                    continue;
                }

                if (TryGetValueFromElement(child, out encoded))
                {
                    return true;
                }
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryExtractLatestRecord(string payload, out LatestRecordData latestRecord)
    {
        latestRecord = new LatestRecordData(null, string.Empty);
        if (string.IsNullOrWhiteSpace(payload))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;

            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("record", out var recordElement))
            {
                if (TryExtractRecordId(recordElement, out var recordId) &&
                    TryGetValueFromElement(recordElement, out var recordValue))
                {
                    latestRecord = new LatestRecordData(recordId, recordValue);
                    return true;
                }

                latestRecord = new LatestRecordData(null, string.Empty);
                return true;
            }

            if (TryExtractRecordId(root, out var rootId) &&
                TryGetValueFromElement(root, out var rootValue))
            {
                latestRecord = new LatestRecordData(rootId, rootValue);
                return true;
            }
        }
        catch
        {
            // ignored
        }

        return false;
    }

    private static bool TryExtractRecordId(string payload, out int recordId)
    {
        recordId = 0;
        if (string.IsNullOrWhiteSpace(payload))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(payload);
            return TryExtractRecordId(document.RootElement, out recordId);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryExtractRecordId(JsonElement element, out int recordId)
    {
        recordId = 0;
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (element.TryGetProperty("id", out var idElement) &&
            idElement.ValueKind == JsonValueKind.Number &&
            idElement.TryGetInt32(out recordId))
        {
            return true;
        }

        if (element.TryGetProperty("record", out var recordElement))
        {
            return TryExtractRecordId(recordElement, out recordId);
        }

        return false;
    }

    private static bool TryGetValueFromElement(JsonElement element, out string value)
    {
        value = string.Empty;

        if (element.ValueKind == JsonValueKind.String)
        {
            value = element.GetString() ?? string.Empty;
            return !string.IsNullOrWhiteSpace(value);
        }

        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty("value", out var valueElement) ||
            valueElement.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = valueElement.GetString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(value);
    }

    private static string ToBase36(int value)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Value must be non-negative.");
        }

        if (value == 0)
        {
            return "0";
        }

        var chars = new char[8];
        var position = chars.Length;
        var remaining = value;

        while (remaining > 0)
        {
            var index = remaining % 36;
            chars[--position] = Base36Alphabet[index];
            remaining /= 36;
        }

        return new string(chars, position, chars.Length - position);
    }

    private static bool TryParseBase36(string input, out int value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var span = input.AsSpan().Trim();
        foreach (var character in span)
        {
            int digit;
            if (character is >= '0' and <= '9')
            {
                digit = character - '0';
            }
            else if (character is >= 'A' and <= 'Z')
            {
                digit = character - 'A' + 10;
            }
            else if (character is >= 'a' and <= 'z')
            {
                digit = character - 'a' + 10;
            }
            else
            {
                return false;
            }

            checked
            {
                value = (value * 36) + digit;
            }
        }

        return true;
    }

    private readonly record struct LatestRecordData(int? RecordId, string EncodedValue);
}
