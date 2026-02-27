namespace Babbler.Web.Models;

public sealed record SessionStatus(
    bool IsRunning,
    string? SourceLanguage,
    string? TargetLanguage,
    double FreeMinutesUsed,
    double FreeMinutesLimit,
    double FreeMinutesRemaining,
    bool FreeLimitReached);
