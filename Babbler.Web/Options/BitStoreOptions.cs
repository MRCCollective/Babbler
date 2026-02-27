namespace Babbler.Web.Options;

public sealed class BitStoreOptions
{
    public const string SectionName = "BitStore";

    public bool Enabled { get; init; }

    public string BaseUrl { get; init; } = "https://bitstorehome.azurewebsites.net";

    public string BucketSlug { get; init; } = string.Empty;

    public string WriteKey { get; init; } = string.Empty;
}
