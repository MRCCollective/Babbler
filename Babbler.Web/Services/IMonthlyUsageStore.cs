namespace Babbler.Web.Services;

public interface IMonthlyUsageStore
{
    Task<TimeSpan> GetUsedAsync(CancellationToken cancellationToken = default);

    Task SaveUsedAsync(TimeSpan used, CancellationToken cancellationToken = default);
}
