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
    double FreeMinutesUsed,
    double FreeMinutesRemaining,
    DateTimeOffset SnapshotUtc);
