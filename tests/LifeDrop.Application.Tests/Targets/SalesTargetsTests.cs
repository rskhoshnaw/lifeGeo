using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LifeDrop.Application.Roles;
using LifeDrop.Application.Targets;
using LifeDrop.Domain.Common;
using Xunit;

namespace LifeDrop.Application.Tests.Targets;

public class SalesTargetsTests
{
    private static readonly Guid Tenant = Guid.NewGuid(), Actor = Guid.NewGuid(), Rep = Guid.NewGuid(), ErbilRep = Guid.NewGuid(), Product = Guid.NewGuid();
    private static readonly Guid Erbil = Guid.NewGuid(), Basra = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 8, 0, 0, TimeSpan.Zero);

    private static SalesTargets Service(Store store, DataScope? manage, DataScope? read = null) =>
        new(store, new Permissions { Manage = manage, Read = read }, new FixedTime());

    [Fact]
    public async Task OnlyTheHolderOfTheManagePermissionSetsATarget()
    {
        var store = new Store();
        Assert.Equal(TargetResult.Forbidden, await Service(store, null).SetAsync(new TenantId(Tenant), new UserId(Actor), new SetTargetRequest(Rep, Product, new DateOnly(2026, 10, 1), 10), CancellationToken.None));
        Assert.Empty(store.Sets);
        Assert.Equal(TargetResult.Success, await Service(store, DataScope.Tenant).SetAsync(new TenantId(Tenant), new UserId(Actor), new SetTargetRequest(Rep, Product, new DateOnly(2026, 10, 17), 10), CancellationToken.None));
        Assert.Equal((new DateOnly(2026, 10, 1), 10m), (store.Sets[0].Month, store.Sets[0].Quantity));   // any date of the month names the month
    }

    [Theory]
    [InlineData(2024, 9, false)]    // more than two years back
    [InlineData(2024, 10, true)]
    [InlineData(2027, 10, true)]
    [InlineData(2027, 11, false)]   // more than a year ahead
    public async Task OnlyMonthsThatMakeSenseAreAccepted(int year, int month, bool allowed)
    {
        var result = await Service(new Store(), DataScope.Tenant).SetAsync(new TenantId(Tenant), new UserId(Actor), new SetTargetRequest(Rep, Product, new DateOnly(year, month, 5), 10), CancellationToken.None);
        Assert.Equal(allowed ? TargetResult.Success : TargetResult.InvalidData, result);
    }

    [Fact]
    public async Task ZeroClearsAndANegativeOrHugeQuantityIsRefused()
    {
        var store = new Store();
        var service = Service(store, DataScope.Tenant);
        var month = new DateOnly(2026, 10, 1);
        Assert.Equal(TargetResult.Success, await service.SetAsync(new TenantId(Tenant), new UserId(Actor), new SetTargetRequest(Rep, Product, month, 0), CancellationToken.None));
        Assert.Equal(TargetResult.Success, await service.SetAsync(new TenantId(Tenant), new UserId(Actor), new SetTargetRequest(Rep, Product, month, null), CancellationToken.None));
        Assert.Equal(new decimal?[] { null, null }, store.Sets.ConvertAll(s => s.Quantity));
        Assert.Equal(TargetResult.InvalidData, await service.SetAsync(new TenantId(Tenant), new UserId(Actor), new SetTargetRequest(Rep, Product, month, -1), CancellationToken.None));
        Assert.Equal(TargetResult.InvalidData, await service.SetAsync(new TenantId(Tenant), new UserId(Actor), new SetTargetRequest(Rep, Product, month, SalesTargets.MaxQuantity + 1), CancellationToken.None));
        Assert.Equal(TargetResult.InvalidData, await service.SetAsync(new TenantId(Tenant), new UserId(Actor), new SetTargetRequest(Guid.Empty, Product, month, 5), CancellationToken.None));
        Assert.Equal(2, store.Sets.Count);
    }

    [Fact]
    public async Task ASupervisorSetsTargetsOnlyForPeopleOfTheirRegion()
    {
        var store = new Store();
        var service = Service(store, DataScope.Region);   // the actor's region is Erbil
        var month = new DateOnly(2026, 10, 1);
        Assert.Equal(TargetResult.Success, await service.SetAsync(new TenantId(Tenant), new UserId(Actor), new SetTargetRequest(ErbilRep, Product, month, 10), CancellationToken.None));
        Assert.Equal(TargetResult.NotFound, await service.SetAsync(new TenantId(Tenant), new UserId(Actor), new SetTargetRequest(Rep, Product, month, 10), CancellationToken.None));   // a Basra user
        Assert.Single(store.Sets);
    }

    [Fact]
    public async Task TheReportNeedsTheReadPermissionAndAsksForTheIraqMonth()
    {
        var store = new Store();
        Assert.Equal(TargetResult.Forbidden, (await Service(store, DataScope.Tenant).ListAsync(new TenantId(Tenant), new UserId(Actor), new DateOnly(2026, 10, 9), CancellationToken.None)).Result);
        var (result, _) = await Service(store, null, DataScope.Region).ListAsync(new TenantId(Tenant), new UserId(Actor), new DateOnly(2026, 10, 9), CancellationToken.None);
        Assert.Equal(TargetResult.Success, result);
        Assert.Equal(DataScope.Region, store.ListScope!.Scope);
        Assert.Equal(new DateTimeOffset(2026, 9, 30, 21, 0, 0, TimeSpan.Zero), store.From!.Value.ToUniversalTime());    // 1 October 00:00 in Iraq
        Assert.Equal(new DateTimeOffset(2026, 10, 31, 21, 0, 0, TimeSpan.Zero), store.To!.Value.ToUniversalTime());
        Assert.Equal(new DateOnly(2026, 10, 1), store.ListMonth);
        Assert.Equal(TargetResult.InvalidData, (await Service(store, null, DataScope.Tenant).ListAsync(new TenantId(Tenant), new UserId(Actor), new DateOnly(2030, 1, 1), CancellationToken.None)).Result);
    }

    private sealed class Store : ITargetStore
    {
        public List<(Guid User, Guid Product, DateOnly Month, decimal? Quantity)> Sets { get; } = [];
        public AccessScope? ListScope;
        public DateOnly? ListMonth;
        public DateTimeOffset? From, To;

        public Task<TargetResult> SetAsync(TenantId tenantId, UserId actorId, Guid userId, Guid productId, DateOnly month, decimal? quantity, CancellationToken cancellationToken)
        {
            Sets.Add((userId, productId, month, quantity));
            return Task.FromResult(TargetResult.Success);
        }

        public Task<IReadOnlyList<TargetRowDto>> ListAsync(TenantId tenantId, AccessScope scope, DateOnly month, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
        {
            (ListScope, ListMonth, From, To) = (scope, month, from, to);
            return Task.FromResult<IReadOnlyList<TargetRowDto>>([]);
        }
    }

    private sealed class Permissions : IPermissionStore
    {
        public DataScope? Manage, Read;
        public Task<bool> HasPermissionAsync(TenantId tenantId, UserId userId, string permissionKey, CancellationToken cancellationToken) => Task.FromResult(Find(permissionKey) is not null);
        public Task<AccessScope?> GetAccessScopeAsync(TenantId tenantId, UserId userId, string permissionKey, CancellationToken cancellationToken) =>
            Task.FromResult(Find(permissionKey) is { } scope ? new AccessScope(scope, userId, Erbil) : null);
        public Task<(bool Exists, Guid? RegionId)> GetUserRegionAsync(TenantId tenantId, UserId userId, CancellationToken cancellationToken) =>
            Task.FromResult<(bool, Guid?)>((true, userId.Value == ErbilRep ? Erbil : Basra));
        private DataScope? Find(string key) => key == PermissionKeys.TargetsManage ? Manage : key == PermissionKeys.TargetsRead ? Read : null;
    }

    private sealed class FixedTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
