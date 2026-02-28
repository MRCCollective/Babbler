namespace Babbler.Web.Models;

public sealed record RoomDiagnostics(
    string RoomId,
    bool IsRunning,
    string? SourceLanguage,
    string? TargetLanguage,
    DateTimeOffset LastStateChangedAtUtc,
    DateTimeOffset? LastStoppedAtUtc,
    string? LastStopReason,
    DateTimeOffset? LastClientPublishAtUtc,
    string? LastClientSourceText,
    string? LastClientTranslatedText,
    int ActiveHubConnections,
    DateTimeOffset? LastClientPayloadReceivedAtUtc,
    long ClientPayloadsReceived,
    long ClientPayloadsForwarded,
    long ClientPayloadsDroppedNotRunning,
    long ClientPayloadsDroppedEmpty,
    long ClientPayloadSendErrors,
    double FreeMinutesUsed,
    double FreeMinutesRemaining,
    DateTimeOffset SnapshotUtc);
