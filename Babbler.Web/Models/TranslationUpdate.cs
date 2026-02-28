namespace Babbler.Web.Models;

public sealed record TranslationUpdate(
    string? SourceText,
    string? TranslatedText,
    string? SourceLanguage,
    string? TargetLanguage,
    IReadOnlyDictionary<string, string>? Translations,
    bool IsFinal,
    DateTimeOffset TimestampUtc,
    string? SystemMessage);
