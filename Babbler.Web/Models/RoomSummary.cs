namespace Babbler.Web.Models;

public sealed record RoomSummary(
    string RoomId,
    bool IsRunning,
    string? SourceLanguage,
    string? TargetLanguage,
    DateTimeOffset LastStateChangedAtUtc,
    DateTimeOffset? LastStoppedAtUtc);
