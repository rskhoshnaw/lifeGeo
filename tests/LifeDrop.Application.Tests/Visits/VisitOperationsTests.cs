using System;
using System.Threading;
using System.Threading.Tasks;
using LifeDrop.Application.Visits;
using LifeDrop.Domain.Common;
using LifeDrop.Domain.Visits;
using Xunit;

namespace LifeDrop.Application.Tests.Visits;

public class VisitOperationsTests
{
    private sealed class FakeVisitStore : IVisitStore
    {
        public bool CreateCalled;
        
        public Task<(CreateVisitResult Result, Visit? Visit)> CreateAsync(TenantId tenantId, UserId actorId, Visit visit, CancellationToken cancellationToken)
        {
            CreateCalled = true;
            return Task.FromResult((CreateVisitResult.Created, (Visit?)visit));
        }

        public Task<System.Collections.Generic.List<VisitDto>> ListAssignedVisitsAsync(TenantId tenantId, UserId assignedUserId, DateOnly scheduleDate, CancellationToken cancellationToken)
        {
            return Task.FromResult(new System.Collections.Generic.List<VisitDto>());
        }

        public Visit? ReturnForGet;
        public bool UpdateCalled;
        public bool ReturnForUpdate = true;

        public Task<Visit?> GetAsync(TenantId tenantId, VisitId visitId, CancellationToken cancellationToken)
        {
            return Task.FromResult(ReturnForGet);
        }

        public Task<bool> TryUpdateStatusAsync(TenantId tenantId, VisitId visitId, UserId expectedUserId, VisitStatus expectedStatus, VisitStatus newStatus, string? exceptionReason, CancellationToken cancellationToken)
        {
            UpdateCalled = true;
            return Task.FromResult(ReturnForUpdate);
        }
    }

    [Fact]
    public async Task CreateVisit_ValidRequest_CallsStore()
    {
        var store = new FakeVisitStore();
        var sut = new CreateVisit(store);
        
        var result = await sut.ExecuteAsync(TenantId.New(), UserId.New(), new CreateVisitRequest(Guid.NewGuid(), Guid.NewGuid(), new DateOnly(2026, 10, 3)), CancellationToken.None);
        
        Assert.Equal(CreateVisitResult.Created, result.Result);
        Assert.True(store.CreateCalled);
    }

    [Fact]
    public async Task CreateVisit_EmptyTenantOrActor_ReturnsForbidden()
    {
        var store = new FakeVisitStore();
        var sut = new CreateVisit(store);
        
        var result = await sut.ExecuteAsync(TenantId.Empty, UserId.Empty, new CreateVisitRequest(Guid.NewGuid(), Guid.NewGuid(), new DateOnly(2026, 10, 3)), CancellationToken.None);
        
        Assert.Equal(CreateVisitResult.Forbidden, result.Result);
        Assert.False(store.CreateCalled);
    }
    
    [Fact]
    public async Task CreateVisit_EmptyUserOrCustomer_ReturnsInvalidData()
    {
        var store = new FakeVisitStore();
        var sut = new CreateVisit(store);
        
        var result = await sut.ExecuteAsync(TenantId.New(), UserId.New(), new CreateVisitRequest(Guid.Empty, Guid.Empty, new DateOnly(2026, 10, 3)), CancellationToken.None);
        
        Assert.Equal(CreateVisitResult.InvalidData, result.Result);
        Assert.False(store.CreateCalled);
    }

    [Fact]
    public async Task MarkEnRoute_Valid_Success()
    {
        var store = new FakeVisitStore();
        var sut = new MarkVisitEnRoute(store);
        var tenant = TenantId.New();
        var user = UserId.New();
        var visit = Visit.Create(tenant, user, CustomerId.New(), new DateOnly(2026, 10, 3));
        store.ReturnForGet = visit;

        var result = await sut.ExecuteAsync(tenant, user, visit.Id.Value, CancellationToken.None);

        Assert.Equal(MarkVisitResult.Success, result);
        Assert.True(store.UpdateCalled);
        Assert.Equal(VisitStatus.EnRoute, visit.Status);
    }

    [Fact]
    public async Task MarkEnRoute_WrongUser_Forbidden()
    {
        var store = new FakeVisitStore();
        var sut = new MarkVisitEnRoute(store);
        var tenant = TenantId.New();
        var user = UserId.New();
        var visit = Visit.Create(tenant, UserId.New(), CustomerId.New(), new DateOnly(2026, 10, 3));
        store.ReturnForGet = visit;

        var result = await sut.ExecuteAsync(tenant, user, visit.Id.Value, CancellationToken.None);

        Assert.Equal(MarkVisitResult.Forbidden, result);
        Assert.False(store.UpdateCalled);
    }

    [Fact]
    public async Task MarkEnRoute_InvalidState_ReturnsInvalidState()
    {
        var store = new FakeVisitStore();
        var sut = new MarkVisitEnRoute(store);
        var tenant = TenantId.New();
        var user = UserId.New();
        var visit = Visit.Create(tenant, user, CustomerId.New(), new DateOnly(2026, 10, 3));
        visit.MarkEnRoute(); // now EnRoute
        store.ReturnForGet = visit;

        var result = await sut.ExecuteAsync(tenant, user, visit.Id.Value, CancellationToken.None);

        Assert.Equal(MarkVisitResult.InvalidState, result);
        Assert.False(store.UpdateCalled);
    }
    
    [Fact]
    public async Task MarkArrived_Valid_Success()
    {
        var store = new FakeVisitStore();
        var sut = new MarkVisitArrived(store);
        var tenant = TenantId.New();
        var user = UserId.New();
        var visit = Visit.Create(tenant, user, CustomerId.New(), new DateOnly(2026, 10, 3));
        visit.MarkEnRoute(); // must be EnRoute first
        store.ReturnForGet = visit;

        var result = await sut.ExecuteAsync(tenant, user, visit.Id.Value, CancellationToken.None);

        Assert.Equal(MarkVisitResult.Success, result);
        Assert.True(store.UpdateCalled);
        Assert.Equal(VisitStatus.Arrived, visit.Status);
    }

    [Fact]
    public async Task MarkArrived_WrongUser_Forbidden()
    {
        var store = new FakeVisitStore();
        var sut = new MarkVisitArrived(store);
        var tenant = TenantId.New();
        var user = UserId.New();
        var visit = Visit.Create(tenant, UserId.New(), CustomerId.New(), new DateOnly(2026, 10, 3));
        store.ReturnForGet = visit;

        var result = await sut.ExecuteAsync(tenant, user, visit.Id.Value, CancellationToken.None);

        Assert.Equal(MarkVisitResult.Forbidden, result);
        Assert.False(store.UpdateCalled);
    }

    [Fact]
    public async Task MarkArrived_InvalidState_ReturnsInvalidState()
    {
        var store = new FakeVisitStore();
        var sut = new MarkVisitArrived(store);
        var tenant = TenantId.New();
        var user = UserId.New();
        var visit = Visit.Create(tenant, user, CustomerId.New(), new DateOnly(2026, 10, 3));
        // Still planned
        store.ReturnForGet = visit;

        var result = await sut.ExecuteAsync(tenant, user, visit.Id.Value, CancellationToken.None);

        Assert.Equal(MarkVisitResult.InvalidState, result);
        Assert.False(store.UpdateCalled);
    } 

    [Fact]
    public async Task MarkInVisit_Valid_Success()
    {
        var store = new FakeVisitStore();
        var sut = new MarkVisitInVisit(store);
        var tenant = TenantId.New();
        var user = UserId.New();
        var visit = Visit.Create(tenant, user, CustomerId.New(), new DateOnly(2026, 10, 3));
        visit.MarkEnRoute();
        visit.MarkArrived();
        store.ReturnForGet = visit;

        var result = await sut.ExecuteAsync(tenant, user, visit.Id.Value, CancellationToken.None);

        Assert.Equal(MarkVisitResult.Success, result);
        Assert.True(store.UpdateCalled);
        Assert.Equal(VisitStatus.InVisit, visit.Status);
    }

    [Fact]
    public async Task MarkInVisit_WrongUser_Forbidden()
    {
        var store = new FakeVisitStore();
        var sut = new MarkVisitInVisit(store);
        var tenant = TenantId.New();
        var user = UserId.New();
        var visit = Visit.Create(tenant, UserId.New(), CustomerId.New(), new DateOnly(2026, 10, 3));
        store.ReturnForGet = visit;

        var result = await sut.ExecuteAsync(tenant, user, visit.Id.Value, CancellationToken.None);

        Assert.Equal(MarkVisitResult.Forbidden, result);
        Assert.False(store.UpdateCalled);
    }

    [Fact]
    public async Task MarkInVisit_InvalidState_ReturnsInvalidState()
    {
        var store = new FakeVisitStore();
        var sut = new MarkVisitInVisit(store);
        var tenant = TenantId.New();
        var user = UserId.New();
        var visit = Visit.Create(tenant, user, CustomerId.New(), new DateOnly(2026, 10, 3));
        store.ReturnForGet = visit;

        var result = await sut.ExecuteAsync(tenant, user, visit.Id.Value, CancellationToken.None);

        Assert.Equal(MarkVisitResult.InvalidState, result);
        Assert.False(store.UpdateCalled);
    }

    [Fact]
    public async Task MarkCompleted_Valid_Success()
    {
        var store = new FakeVisitStore();
        var sut = new MarkVisitCompleted(store);
        var tenant = TenantId.New();
        var user = UserId.New();
        var visit = Visit.Create(tenant, user, CustomerId.New(), new DateOnly(2026, 10, 3));
        visit.MarkEnRoute();
        visit.MarkArrived();
        visit.StartVisit();
        store.ReturnForGet = visit;

        var result = await sut.ExecuteAsync(tenant, user, visit.Id.Value, CancellationToken.None);

        Assert.Equal(MarkVisitResult.Success, result);
        Assert.True(store.UpdateCalled);
        Assert.Equal(VisitStatus.Completed, visit.Status);
    }

    [Fact]
    public async Task MarkCompleted_WrongUser_Forbidden()
    {
        var store = new FakeVisitStore();
        var sut = new MarkVisitCompleted(store);
        var tenant = TenantId.New();
        var user = UserId.New();
        var visit = Visit.Create(tenant, UserId.New(), CustomerId.New(), new DateOnly(2026, 10, 3));
        store.ReturnForGet = visit;

        var result = await sut.ExecuteAsync(tenant, user, visit.Id.Value, CancellationToken.None);

        Assert.Equal(MarkVisitResult.Forbidden, result);
        Assert.False(store.UpdateCalled);
    }

    [Fact]
    public async Task MarkCompleted_InvalidState_ReturnsInvalidState()
    {
        var store = new FakeVisitStore();
        var sut = new MarkVisitCompleted(store);
        var tenant = TenantId.New();
        var user = UserId.New();
        var visit = Visit.Create(tenant, user, CustomerId.New(), new DateOnly(2026, 10, 3));
        store.ReturnForGet = visit;

        var result = await sut.ExecuteAsync(tenant, user, visit.Id.Value, CancellationToken.None);

        Assert.Equal(MarkVisitResult.InvalidState, result);
        Assert.False(store.UpdateCalled);
    }

    private sealed class FakePermissionStore : LifeDrop.Application.Roles.IPermissionStore
    {
        public bool ReturnHasPermission = true;
        public Task<bool> HasPermissionAsync(TenantId tenantId, UserId userId, string permissionCode, CancellationToken cancellationToken)
        {
            return Task.FromResult(ReturnHasPermission);
        }
    }

    [Fact]
    public async Task MarkFailed_Valid_Success()
    {
        var store = new FakeVisitStore();
        var permissions = new FakePermissionStore();
        var sut = new MarkVisitFailed(store, permissions);
        var tenant = TenantId.New();
        var user = UserId.New();
        var visit = Visit.Create(tenant, user, CustomerId.New(), new DateOnly(2026, 10, 3));
        store.ReturnForGet = visit;

        var result = await sut.ExecuteAsync(tenant, user, visit.Id.Value, "Car broke down", CancellationToken.None);

        Assert.Equal(MarkVisitResult.Success, result);
        Assert.True(store.UpdateCalled);
        Assert.Equal(VisitStatus.Failed, visit.Status);
    }

    [Fact]
    public async Task MarkFailed_MissingPermission_Forbidden()
    {
        var store = new FakeVisitStore();
        var permissions = new FakePermissionStore { ReturnHasPermission = false };
        var sut = new MarkVisitFailed(store, permissions);
        var tenant = TenantId.New();
        var user = UserId.New();
        var visit = Visit.Create(tenant, user, CustomerId.New(), new DateOnly(2026, 10, 3));
        store.ReturnForGet = visit;

        var result = await sut.ExecuteAsync(tenant, user, visit.Id.Value, "Reason", CancellationToken.None);

        Assert.Equal(MarkVisitResult.Forbidden, result);
        Assert.False(store.UpdateCalled);
    }

    [Fact]
    public async Task MarkFailed_WrongUser_Forbidden()
    {
        var store = new FakeVisitStore();
        var permissions = new FakePermissionStore();
        var sut = new MarkVisitFailed(store, permissions);
        var tenant = TenantId.New();
        var user = UserId.New();
        var visit = Visit.Create(tenant, UserId.New(), CustomerId.New(), new DateOnly(2026, 10, 3));
        store.ReturnForGet = visit;

        var result = await sut.ExecuteAsync(tenant, user, visit.Id.Value, "Reason", CancellationToken.None);

        Assert.Equal(MarkVisitResult.Forbidden, result);
        Assert.False(store.UpdateCalled);
    }

    [Fact]
    public async Task MarkFailed_BlankReason_InvalidData()
    {
        var store = new FakeVisitStore();
        var permissions = new FakePermissionStore();
        var sut = new MarkVisitFailed(store, permissions);
        var tenant = TenantId.New();
        var user = UserId.New();
        var visit = Visit.Create(tenant, user, CustomerId.New(), new DateOnly(2026, 10, 3));
        store.ReturnForGet = visit;

        var result = await sut.ExecuteAsync(tenant, user, visit.Id.Value, "   ", CancellationToken.None);

        Assert.Equal(MarkVisitResult.InvalidData, result);
        Assert.False(store.UpdateCalled);
    }

    [Fact]
    public async Task MarkFailed_TerminalState_InvalidState()
    {
        var store = new FakeVisitStore();
        var permissions = new FakePermissionStore();
        var sut = new MarkVisitFailed(store, permissions);
        var tenant = TenantId.New();
        var user = UserId.New();
        var visit = Visit.Create(tenant, user, CustomerId.New(), new DateOnly(2026, 10, 3));
        visit.MarkEnRoute();
        visit.MarkArrived();
        visit.StartVisit();
        visit.Complete(); // Now in Completed terminal state
        store.ReturnForGet = visit;

        var result = await sut.ExecuteAsync(tenant, user, visit.Id.Value, "Reason", CancellationToken.None);

        Assert.Equal(MarkVisitResult.InvalidState, result);
        Assert.False(store.UpdateCalled);
    }
}



