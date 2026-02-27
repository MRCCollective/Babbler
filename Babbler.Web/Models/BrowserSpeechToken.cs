namespace Babbler.Web.Models;

public sealed record BrowserSpeechToken(
    string Token,
    string Region,
    DateTimeOffset ExpiresAtUtc);
