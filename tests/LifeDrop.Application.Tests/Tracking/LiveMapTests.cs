using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LifeDrop.Application.Roles;
using LifeDrop.Application.Tracking;
using LifeDrop.Domain.Common;
using Xunit;

namespace LifeDrop.Application.Tests.Tracking;

// Live map rules (plan step 1.19, decision D3): coordinates only with tracking.read and only for users its scope
// covers; tracking.summary never gets coordinates; a point older than 5 minutes is never "live".
public class LiveMapTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 8, 0, 0, TimeSpan.Zero);
    private static readonly Guid Tenant = Guid.NewGuid(), Actor = Guid.NewGuid(), Erbil = Guid.NewGuid(), Basra = Guid.NewGuid();

    private static LiveMapRow Row(Guid user, Guid? region, DateTimeOffset? lastPoint) =>
        new(user, "u@t1", "Team", region, Now.AddHours(-2), lastPoint, 36.19, 44.01, 12.5, false, "Shifa", Now.AddMinutes(-20));

    [Fact]
    public void PointWithinFiveMinutesIsLive_OlderIsNot()
    {
        Assert.True(LiveMap.ToEntry(Row(Guid.NewGuid(), null, Now.AddMinutes(-5)), true, Now).IsLive);
        var stale = LiveMap.ToEntry(Row(Guid.NewGuid(), null, Now.AddMinutes(-5).AddSeconds(-1)), true, Now);
        Assert.False(stale.IsLive);
        Assert.Equal(36.19, stale.Latitude); // still shown, but as last known, not live
        Assert.False(LiveMap.ToEntry(Row(Guid.NewGuid(), null, null), true, Now).IsLive);
    }

    [Fact]
    public void SummaryNeverCarriesCoordinates()
    {
        var entry = LiveMap.ToEntry(Row(Guid.NewGuid(), null, Now.AddMinutes(-1)), false, Now);
        Assert.Equal((false, true, null, null, null, null), (entry.Precise, entry.IsLive, entry.Latitude, entry.Longitude, entry.AccuracyMeters, entry.IsMocked));
        Assert.Equal("Shifa", entry.LastCustomerName);
    }

    [Fact]
    public async Task WithoutTrackingPermissionsTheMapIsRefused()
    {
        var map = new LiveMap(new Store(), new Permissions(), new FixedTime());
        Assert.Null(await map.ListAsync(new TenantId(Tenant), new UserId(Actor), CancellationToken.None));
    }

    [Fact]
    public async Task SummaryOnlyRoleSeesStatusWithoutCoordinates()
    {
        var store = new Store();
        var map = new LiveMap(store, new Permissions { Summary = DataScope.Tenant }, new FixedTime());
        var entries = await map.ListAsync(new TenantId(Tenant), new UserId(Actor), CancellationToken.None);
        Assert.Equal(DataScope.Tenant, store.Scope!.Scope);
        Assert.All(entries!, e => Assert.Null(e.Latitude));
    }

    [Fact]
    public async Task RegionReadPlusTenantSummaryGivesCoordinatesOnlyInsideTheRegion()
    {
        var store = new Store();
        var map = new LiveMap(store, new Permissions { Read = DataScope.Region, Summary = DataScope.Tenant }, new FixedTime());
        var entries = await map.ListAsync(new TenantId(Tenant), new UserId(Actor), CancellationToken.None);
        Assert.Equal(DataScope.Tenant, store.Scope!.Scope); // rows come from the wider scope
        Assert.Equal(new[] { true, false, true }, entries!.Select(e => e.Precise).ToArray());
        Assert.Equal(new double?[] { 36.19, null, 36.19 }, entries.Select(e => e.Latitude).ToArray());
    }

    private sealed class Store : ILiveMapStore
    {
        public AccessScope? Scope;
        public Task<IReadOnlyList<LiveMapRow>> ListOpenShiftsAsync(TenantId tenantId, AccessScope scope, CancellationToken cancellationToken)
        {
            Scope = scope;
            return Task.FromResult<IReadOnlyList<LiveMapRow>>([Row(Guid.NewGuid(), Erbil, Now), Row(Guid.NewGuid(), Basra, Now), Row(Actor, null, Now)]);
        }
    }

    private sealed class Permissions : IPermissionStore
    {
        public DataScope? Read, Summary;
        public Task<bool> HasPermissionAsync(TenantId tenantId, UserId userId, string permissionKey, CancellationToken cancellationToken) =>
            Task.FromResult(Find(permissionKey) is not null);
        public Task<AccessScope?> GetAccessScopeAsync(TenantId tenantId, UserId userId, string permissionKey, CancellationToken cancellationToken) =>
            Task.FromResult(Find(permissionKey) is { } scope ? new AccessScope(scope, userId, Erbil) : null);
        private DataScope? Find(string key) => key == PermissionKeys.TrackingRead ? Read : key == PermissionKeys.TrackingSummary ? Summary : null;
    }

    private sealed class FixedTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
