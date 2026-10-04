using LifeDrop.Application.Roles;
using LifeDrop.Domain.Common;
using Xunit;

namespace LifeDrop.Application.Tests;

public class AssignRoleTests
{
    [Fact]
    public async Task ActorWithoutTenantNeverCallsPersistence()
    {
        var store = new RecordingStore();
        var operation = new AssignRole(store);

        var result = await operation.ExecuteAsync(
            new RoleAssignmentActor(TenantId.Empty, UserId.New()),
            UserId.New(), RoleId.New(), CancellationToken.None);

        Assert.Equal(AssignRoleResult.Forbidden, result);
        Assert.Equal(0, store.CallCount);
    }

    [Fact]
    public async Task UsesAuthenticatedTenantRatherThanTargetSuppliedTenant()
    {
        var store = new RecordingStore();
        var operation = new AssignRole(store);
        var tenant = TenantId.New();
        var actor = UserId.New();
        var target = UserId.New();
        var role = RoleId.New();

        var result = await operation.ExecuteAsync(
            new RoleAssignmentActor(tenant, actor), target, role, CancellationToken.None);

        Assert.Equal(AssignRoleResult.Assigned, result);
        Assert.Equal((tenant, actor, target, role), store.LastCall);
    }

    private sealed class RecordingStore : IRoleAssignmentStore
    {
        public int CallCount { get; private set; }
        public (TenantId, UserId, UserId, RoleId) LastCall { get; private set; }

        public Task<AssignRoleResult> AssignAsync(TenantId tenantId, UserId actorId,
            UserId userId, RoleId roleId, CancellationToken cancellationToken)
        {
            CallCount++;
            LastCall = (tenantId, actorId, userId, roleId);
            return Task.FromResult(AssignRoleResult.Assigned);
        }
    }
}
