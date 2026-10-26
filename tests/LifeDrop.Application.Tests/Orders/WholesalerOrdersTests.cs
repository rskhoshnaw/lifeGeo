using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LifeDrop.Application.Orders;
using LifeDrop.Application.Roles;
using LifeDrop.Domain.Common;
using Xunit;

namespace LifeDrop.Application.Tests.Orders;

public class WholesalerOrdersTests
{
    private static readonly TenantId Tenant = new(Guid.NewGuid());
    private static readonly UserId Officer = new(Guid.NewGuid());

    [Fact]
    public async Task OnlyTheHolderOfWholesalerWorkSeesAndDispatches()
    {
        var store = new Store();
        var denied = new WholesalerOrders(store, new Permissions(false));
        Assert.Equal(OrderResult.Forbidden, (await denied.ListWholesalersAsync(Tenant, Officer, CancellationToken.None)).Result);
        Assert.Equal(OrderResult.Forbidden, (await denied.ListOrdersAsync(Tenant, Officer, null, null, null, CancellationToken.None)).Result);
        Assert.Equal(OrderResult.Forbidden, await denied.DispatchAsync(Tenant, Officer, Guid.NewGuid(), new DispatchRequest(null), CancellationToken.None));
        Assert.Empty(store.Calls);
    }

    [Fact]
    public async Task TheListAsksForTheWaitingOrdersByDefaultAndRefusesWrongInput()
    {
        var store = new Store();
        var service = new WholesalerOrders(store, new Permissions(true));
        Assert.Equal(OrderResult.Success, (await service.ListOrdersAsync(Tenant, Officer, null, null, null, CancellationToken.None)).Result);
        Assert.Equal("placed", store.Status);
        Assert.Equal(100, store.Limit);
        foreach (var bad in new (string? Status, int? Limit, Guid? Warehouse)[] { ("delivered", null, null), ("preparing", null, null), (null, 0, null), (null, WholesalerOrders.MaxLimit + 1, null), (null, null, Guid.Empty) })
            Assert.Equal(OrderResult.InvalidData, (await service.ListOrdersAsync(Tenant, Officer, bad.Warehouse, bad.Status, bad.Limit, CancellationToken.None)).Result);
        Assert.Equal(OrderResult.Success, (await service.ListOrdersAsync(Tenant, Officer, null, "dispatched", 5, CancellationToken.None)).Result);
        Assert.Equal(("dispatched", 5), (store.Status, store.Limit));
    }

    [Fact]
    public async Task DispatchTrimsTheNoteAndRefusesALongOneOrAnEmptyOrder()
    {
        var store = new Store();
        var service = new WholesalerOrders(store, new Permissions(true));
        var order = Guid.NewGuid();
        Assert.Equal(OrderResult.Success, await service.DispatchAsync(Tenant, Officer, order, new DispatchRequest("  told Ali  "), CancellationToken.None));
        Assert.Equal("told Ali", store.Note);
        Assert.Equal(OrderResult.Success, await service.DispatchAsync(Tenant, Officer, order, new DispatchRequest(" "), CancellationToken.None));
        Assert.Null(store.Note);
        Assert.Equal(OrderResult.InvalidData, await service.DispatchAsync(Tenant, Officer, order, new DispatchRequest(new string('x', WholesalerOrders.MaxNoteLength + 1)), CancellationToken.None));
        Assert.Equal(OrderResult.InvalidData, await service.DispatchAsync(Tenant, Officer, Guid.Empty, new DispatchRequest(null), CancellationToken.None));
        Assert.Equal(2, store.Dispatches);
    }

    private sealed class Store : IWholesalerStore
    {
        public List<string> Calls { get; } = [];
        public string? Status, Note;
        public int Limit, Dispatches;

        public Task<IReadOnlyList<WholesalerDto>> ListWholesalersAsync(TenantId tenantId, UserId actorId, CancellationToken cancellationToken)
        {
            Calls.Add("wholesalers");
            return Task.FromResult<IReadOnlyList<WholesalerDto>>([]);
        }

        public Task<IReadOnlyList<WholesalerOrderDto>> ListOrdersAsync(TenantId tenantId, UserId actorId, Guid? warehouseId, string status, int limit, CancellationToken cancellationToken)
        {
            Calls.Add("orders");
            (Status, Limit) = (status, limit);
            return Task.FromResult<IReadOnlyList<WholesalerOrderDto>>([]);
        }

        public Task<OrderResult> DispatchAsync(TenantId tenantId, UserId actorId, Guid orderId, string? note, CancellationToken cancellationToken)
        {
            Calls.Add("dispatch");
            Note = note;
            Dispatches++;
            return Task.FromResult(OrderResult.Success);
        }
    }

    private sealed class Permissions(bool allowed) : IPermissionStore
    {
        public Task<bool> HasPermissionAsync(TenantId tenantId, UserId userId, string permissionKey, CancellationToken cancellationToken) =>
            Task.FromResult(allowed && permissionKey == PermissionKeys.WholesalerWork);
    }
}
