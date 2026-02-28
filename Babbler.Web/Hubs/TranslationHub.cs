using Babbler.Web.Models;
using Babbler.Web.Services;
using Microsoft.AspNetCore.SignalR;
using System.Collections.Concurrent;
using System.Text.Json;

namespace Babbler.Web.Hubs;

public sealed class TranslationHub : Hub
{
    private const string RoomIdItemKey = "roomId";
    private static readonly ConcurrentDictionary<string, int> RoomConnectionCounts =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly TranslationSessionService _session;

    public TranslationHub(TranslationSessionService session)
    {
        _session = session;
    }

    public override async Task OnConnectedAsync()
    {
        var roomId = Context.GetHttpContext()?.Request.Query["roomId"].ToString().Trim().ToUpperInvariant();
        if (!string.IsNullOrWhiteSpace(roomId))
        {
            Context.Items[RoomIdItemKey] = roomId;
            await Groups.AddToGroupAsync(Context.ConnectionId, roomId);
            IncrementRoomConnection(roomId);
        }

        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (Context.Items.TryGetValue(RoomIdItemKey, out var roomIdObject) &&
            roomIdObject is string roomId &&
            !string.IsNullOrWhiteSpace(roomId))
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, roomId);
            DecrementRoomConnection(roomId);
        }

        await base.OnDisconnectedAsync(exception);
    }

    public Task PublishClientTranslation(JsonElement payload)
    {
        if (!Context.Items.TryGetValue(RoomIdItemKey, out var roomIdObject) ||
            roomIdObject is not string roomId ||
            string.IsNullOrWhiteSpace(roomId))
        {
            throw new HubException("Room connection is required.");
        }

        return _session.PublishClientTranslationAsync(
            roomId,
            ParseClientTranslationPayload(payload),
            Context.ConnectionAborted);
    }

    private static ClientTranslationUpdate ParseClientTranslationPayload(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
        {
            throw new HubException("Invalid translation payload.");
        }

        var sourceText = GetStringProperty(payload, "sourceText", "SourceText");
        var sourceLanguage = GetStringProperty(payload, "sourceLanguage", "SourceLanguage");
        var isFinal = GetBooleanProperty(payload, "isFinal", "IsFinal");
        var translations = GetTranslations(payload, "translations", "Translations");

        return new ClientTranslationUpdate(sourceText, sourceLanguage, isFinal, translations);
    }

    private static string? GetStringProperty(JsonElement payload, string camelName, string pascalName)
    {
        if (!TryGetProperty(payload, camelName, pascalName, out var valueElement))
        {
            return null;
        }

        return valueElement.ValueKind == JsonValueKind.String
            ? valueElement.GetString()
            : valueElement.ToString();
    }

    private static bool GetBooleanProperty(JsonElement payload, string camelName, string pascalName)
    {
        if (!TryGetProperty(payload, camelName, pascalName, out var valueElement))
        {
            return false;
        }

        if (valueElement.ValueKind == JsonValueKind.True)
        {
            return true;
        }

        if (valueElement.ValueKind == JsonValueKind.False)
        {
            return false;
        }

        if (valueElement.ValueKind == JsonValueKind.Number && valueElement.TryGetInt32(out var numericValue))
        {
            return numericValue != 0;
        }

        if (valueElement.ValueKind == JsonValueKind.String &&
            bool.TryParse(valueElement.GetString(), out var parsed))
        {
            return parsed;
        }

        return false;
    }

    private static IReadOnlyDictionary<string, string>? GetTranslations(
        JsonElement payload,
        string camelName,
        string pascalName)
    {
        if (!TryGetProperty(payload, camelName, pascalName, out var translationsElement) ||
            translationsElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var translations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in translationsElement.EnumerateObject())
        {
            var key = entry.Name?.Trim();
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            var value = entry.Value.ValueKind == JsonValueKind.String
                ? entry.Value.GetString()
                : entry.Value.ToString();
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            translations[key] = value;
        }

        return translations.Count == 0 ? null : translations;
    }

    private static bool TryGetProperty(
        JsonElement payload,
        string camelName,
        string pascalName,
        out JsonElement value)
    {
        if (payload.TryGetProperty(camelName, out value))
        {
            return true;
        }

        if (payload.TryGetProperty(pascalName, out value))
        {
            return true;
        }

        value = default;
        return false;
    }

    public static int GetRoomConnectionCount(string roomId)
    {
        if (string.IsNullOrWhiteSpace(roomId))
        {
            return 0;
        }

        return RoomConnectionCounts.TryGetValue(roomId, out var count)
            ? Math.Max(0, count)
            : 0;
    }

    private static void IncrementRoomConnection(string roomId)
    {
        RoomConnectionCounts.AddOrUpdate(roomId, 1, static (_, count) => count + 1);
    }

    private static void DecrementRoomConnection(string roomId)
    {
        while (true)
        {
            if (!RoomConnectionCounts.TryGetValue(roomId, out var current))
            {
                return;
            }

            if (current <= 1)
            {
                RoomConnectionCounts.TryRemove(roomId, out _);
                return;
            }

            if (RoomConnectionCounts.TryUpdate(roomId, current - 1, current))
            {
                return;
            }
        }
    }
}
