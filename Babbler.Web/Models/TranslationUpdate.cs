namespace Babbler.Web.Models;

public sealed record TranslationUpdate(
    string? SourceText,
    string? TranslatedText,
    string? SourceLanguage,
    string? TargetLanguage,
    bool IsFinal,
    DateTimeOffset TimestampUtc,
    string? SystemMessage);
