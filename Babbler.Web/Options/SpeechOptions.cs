namespace Babbler.Web.Options;

public sealed class SpeechOptions
{
    public const string SectionName = "Speech";

    public string Key { get; init; } = string.Empty;

    public string Region { get; init; } = string.Empty;
}
