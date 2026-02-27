namespace Babbler.Web.Options;

public sealed class SessionLimitsOptions
{
    public const string SectionName = "SessionLimits";

    public double FreeMinutesLimit { get; init; } = 15;
}
