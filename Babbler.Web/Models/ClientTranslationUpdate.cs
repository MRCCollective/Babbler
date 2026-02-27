namespace Babbler.Web.Models;

public sealed record ClientTranslationUpdate(
    string? SourceText,
    string? SourceLanguage,
    bool IsFinal,
    IReadOnlyDictionary<string, string>? Translations);
