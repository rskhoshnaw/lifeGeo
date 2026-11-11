using LifeDrop.Application.Reports;
using LifeDrop.Application.Roles;
using LifeDrop.Domain.Common;
using Xunit;

namespace LifeDrop.Application.Tests;

// A user's KPIs for a month against the month before, the same month last year and a 12-month trend (plan step 5.29).
public class KpiCompareTests
{
    private static readonly TenantId Tenant = TenantId.New();
    private static readonly UserId Actor = UserId.New();
    private static readonly Guid Other = Guid.NewGuid();
    private static readonly Guid Region = Guid.NewGuid();
    // 2026-11-11 12:00 in Iraq.
    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static readonly FixedClock Clock = new(new DateTimeOffset(2026, 11, 11, 9, 0, 0, TimeSpan.Zero));

    private static KpiCounts Counts(int planned, int completed) => new(planned, completed, 0, 0, 0, 2, 2, 3, 3, 0, [], 0, 0);

    private sealed class Permissions(DataScope? reports) : IPermissionStore
    {
        public Task<bool> HasPermissionAsync(TenantId tenantId, UserId userId, string permissionKey, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task<AccessScope?> GetAccessScopeAsync(TenantId tenantId, UserId userId, string permissionKey, CancellationToken cancellationToken) =>
            Task.FromResult(permissionKey == PermissionKeys.ReportsRead && reports is { } s ? new AccessScope(s, userId, Region) : null);
    }

    // Users the reader may see; the counts a month has (by its first day).
    private sealed class Store(IReadOnlyList<KpiUser> users, Dictionary<DateOnly, KpiCounts> byMonth) : IReportStore
    {
        public List<(DateOnly From, DateOnly To)> Asked = [];
        public AccessScope? Scope;
        public Task<IReadOnlyList<KpiUser>> ListUsersAsync(TenantId tenantId, AccessScope scope, KpiFilter filter, CancellationToken cancellationToken)
        {
            Scope = scope;
            return Task.FromResult<IReadOnlyList<KpiUser>>(users.Where(u => u.UserId == scope.ActorId.Value || scope.Scope != DataScope.Own).ToList());
        }
        public Task<IReadOnlyDictionary<Guid, KpiCounts>> CountsAsync(TenantId tenantId, IReadOnlyList<Guid> userIds, KpiFilter filter, DateOnly today, DateTimeOffset now, CancellationToken cancellationToken)
        {
            Asked.Add((filter.From, filter.To));
            IReadOnlyDictionary<Guid, KpiCounts> answer = byMonth.TryGetValue(filter.From, out var c) ? new Dictionary<Guid, KpiCounts> { [userIds[0]] = c } : new Dictionary<Guid, KpiCounts>();
            return Task.FromResult(answer);
        }
        public Task<IReadOnlyList<AlertKpi>> AlertsAsync(TenantId tenantId, IReadOnlyList<Guid> userIds, IReadOnlyList<string> categories, KpiFilter filter, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AlertKpi>>([]);
    }

    private static IReadOnlyList<KpiUser> Users() => [new KpiUser(Actor.Value, "me@x.com", "Team A"), new KpiUser(Other, "other@x.com", null)];

    private static KpiCompare Service(Store store, DataScope? reports = null) => new(store, new Permissions(reports), Clock);

    [Fact]
    public async Task Everybody_reads_the_own_month_the_month_before_last_year_and_twelve_months_of_trend()
    {
        var store = new Store(Users(), new()
        {
            [new DateOnly(2026, 11, 1)] = Counts(10, 8),
            [new DateOnly(2026, 10, 1)] = Counts(20, 10),
            [new DateOnly(2025, 11, 1)] = Counts(4, 1),
        });
        var (result, data) = await Service(store).GetAsync(Tenant, Actor, null, new DateOnly(2026, 11, 20), CancellationToken.None);   // any day of the month

        Assert.Equal(ReportResult.Success, result);
        Assert.Equal((Actor.Value, "me@x.com", "2026-11"), (data!.UserId, data.Email, data.Month));
        Assert.Equal(80.0, data.Current.Rates.VisitCompletion);
        Assert.Equal(("2026-10", 50.0), (data.Previous.Month, data.Previous.Rates.VisitCompletion));
        Assert.Equal(("2025-11", 25.0), (data.LastYear.Month, data.LastYear.Rates.VisitCompletion));
        Assert.Equal(12, data.Trend.Count);
        Assert.Equal(("2025-12", "2026-11"), (data.Trend[0].Month, data.Trend[11].Month));            // oldest first, ends with the month
        Assert.Equal(13, store.Asked.Count);                                                           // 12 months of trend + the month a year earlier, each asked once
        Assert.Equal((new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 30)), store.Asked.Single(a => a.From == new DateOnly(2026, 11, 1)));
        Assert.Equal(DataScope.Own, store.Scope!.Scope);                                              // own numbers need no permission and use the own scope
    }

    [Fact]
    public async Task A_month_without_data_has_zero_counts_and_no_rates()
    {
        var (result, data) = await Service(new Store(Users(), new())).GetAsync(Tenant, Actor, Actor.Value, new DateOnly(2026, 11, 1), CancellationToken.None);
        Assert.Equal(ReportResult.Success, result);
        Assert.Equal((0, 0), (data!.Current.Counts.VisitsPlanned, data.Current.Counts.Orders));
        Assert.Equal(new double?[] { null, null, null, null, null }, [data.Current.Rates.VisitCompletion, data.Current.Rates.VisitFailure, data.Current.Rates.Delay, data.Current.Rates.OrderDelivery, data.Current.Rates.FollowUpOverdue]);
    }

    [Fact]
    public async Task Months_in_the_future_or_more_than_three_years_back_are_refused()
    {
        var service = Service(new Store(Users(), new()));
        Assert.Equal(ReportResult.InvalidData, (await service.GetAsync(Tenant, Actor, null, new DateOnly(2026, 12, 1), CancellationToken.None)).Result);
        Assert.Equal(ReportResult.InvalidData, (await service.GetAsync(Tenant, Actor, null, new DateOnly(2023, 10, 1), CancellationToken.None)).Result);
        Assert.Equal(ReportResult.Success, (await service.GetAsync(Tenant, Actor, null, new DateOnly(2023, 11, 1), CancellationToken.None)).Result);
        Assert.Equal(ReportResult.InvalidData, (await service.GetAsync(Tenant, Actor, Guid.Empty, new DateOnly(2026, 11, 1), CancellationToken.None)).Result);
    }

    [Fact]
    public async Task Another_user_needs_reports_read_and_the_user_inside_the_readers_area()
    {
        var month = new DateOnly(2026, 11, 1);
        var store = new Store(Users(), new() { [month] = Counts(5, 5) });
        Assert.Equal(ReportResult.Forbidden, (await Service(store).GetAsync(Tenant, Actor, Other, month, CancellationToken.None)).Result);   // no reports.read
        var (result, data) = await Service(store, DataScope.Tenant).GetAsync(Tenant, Actor, Other, month, CancellationToken.None);
        Assert.Equal((ReportResult.Success, Other, "other@x.com"), (result, data!.UserId, data.Email));
        Assert.Equal(DataScope.Tenant, store.Scope!.Scope);

        // Outside the area (the store does not list the user): not found, not "forbidden", so nobody can probe for users.
        var narrow = new Store([new KpiUser(Actor.Value, "me@x.com", null)], new());
        Assert.Equal(ReportResult.NotFound, (await Service(narrow, DataScope.Region).GetAsync(Tenant, Actor, Other, month, CancellationToken.None)).Result);
    }
}
