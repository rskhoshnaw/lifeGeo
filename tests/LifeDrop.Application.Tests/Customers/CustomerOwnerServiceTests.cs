using LifeDrop.Application.Customers;
using LifeDrop.Application.Roles;
using LifeDrop.Domain.Common;
using Xunit;

namespace LifeDrop.Application.Tests.Customers;

// Giving customers to another user (plan step 5.28): who may do it, which requests are refused, and what reaches the store.
public class CustomerOwnerServiceTests
{
    private static readonly TenantId Tenant = TenantId.New();
    private static readonly UserId Actor = UserId.New();
    private static readonly Guid NewOwner = Guid.NewGuid();
    private static readonly Guid RegionA = Guid.NewGuid();

    private sealed class Permissions(DataScope? approve, Guid? region = null, bool ownerCanRead = true) : IPermissionStore
    {
        public Task<bool> HasPermissionAsync(TenantId tenantId, UserId userId, string permissionKey, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task<AccessScope?> GetAccessScopeAsync(TenantId tenantId, UserId userId, string permissionKey, CancellationToken cancellationToken)
        {
            if (permissionKey == PermissionKeys.CustomersApprove && userId == Actor) return Task.FromResult(approve is { } s ? new AccessScope(s, userId, region) : null);
            if (permissionKey == PermissionKeys.CustomersRead && userId.Value == NewOwner) return Task.FromResult(ownerCanRead ? new AccessScope(DataScope.Own, userId, null, HideDoctors: true) : null);
            return Task.FromResult<AccessScope?>(null);
        }
    }

    private sealed class Store(NewOwnerInfo? owner) : ICustomerOwnerStore
    {
        public (IReadOnlyList<Guid>? Ids, Guid? From, AccessScope? OwnerScope)? Call;
        public Task<NewOwnerInfo?> FindOwnerAsync(TenantId tenantId, Guid userId, CancellationToken cancellationToken) => Task.FromResult(owner);
        public Task<int> SetAsync(TenantId tenantId, UserId actorId, AccessScope scope, AccessScope ownerScope, NewOwnerInfo newOwner, IReadOnlyList<Guid>? customerIds, Guid? fromUserId, CancellationToken cancellationToken)
        {
            Call = (customerIds, fromUserId, ownerScope);
            return Task.FromResult(customerIds?.Count - 1 ?? 7);
        }
    }

    private static CustomerOwnerService Service(Store store, DataScope? approve = DataScope.Tenant, Guid? region = null, bool ownerCanRead = true) => new(store, new Permissions(approve, region, ownerCanRead));

    [Fact]
    public async Task Ticked_customers_go_to_the_new_owner_and_the_skipped_count_is_reported()
    {
        var store = new Store(new NewOwnerInfo(NewOwner, null));
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), ids0 };
        var (result, data) = await Service(store).SetAsync(Tenant, Actor, new SetCustomerOwnerRequest(NewOwner, ids), CancellationToken.None);
        Assert.Equal(CustomerOwnerResult.Success, result);
        Assert.Equal(new CustomerOwnerDto(3, 1), data);
        Assert.Equal(4, store.Call!.Value.Ids!.Count);
        Assert.True(store.Call.Value.OwnerScope!.HideDoctors);   // the new owner's own rules reach the store
    }

    private static readonly Guid ids0 = Guid.NewGuid();

    [Fact]
    public async Task All_customers_of_a_user_can_move_in_one_call()
    {
        var store = new Store(new NewOwnerInfo(NewOwner, null));
        var from = Guid.NewGuid();
        var (result, data) = await Service(store).SetAsync(Tenant, Actor, new SetCustomerOwnerRequest(NewOwner, FromUserId: from), CancellationToken.None);
        Assert.Equal(CustomerOwnerResult.Success, result);
        Assert.Equal(7, data!.Updated);
        Assert.Equal(from, store.Call!.Value.From);
    }

    [Fact]
    public async Task Refuses_bad_requests_without_touching_the_store()
    {
        var store = new Store(new NewOwnerInfo(NewOwner, null));
        var service = Service(store);
        var id = Guid.NewGuid();
        foreach (var request in new[]
        {
            new SetCustomerOwnerRequest(null, [id]),                                                              // no new owner
            new SetCustomerOwnerRequest(Guid.Empty, [id]),
            new SetCustomerOwnerRequest(NewOwner),                                                                // neither ids nor a user
            new SetCustomerOwnerRequest(NewOwner, [id], Guid.NewGuid()),                                          // both
            new SetCustomerOwnerRequest(NewOwner, [Guid.Empty]),
            new SetCustomerOwnerRequest(NewOwner, Enumerable.Range(0, 201).Select(_ => Guid.NewGuid()).ToList()), // too many
            new SetCustomerOwnerRequest(NewOwner, FromUserId: NewOwner),                                          // from the same user
        })
        {
            var (result, _) = await service.SetAsync(Tenant, Actor, request, CancellationToken.None);
            Assert.Equal(CustomerOwnerResult.InvalidData, result);
        }
        Assert.Null(store.Call);
    }

    [Fact]
    public async Task Needs_the_approve_permission_an_active_new_owner_who_can_read_customers_and_the_own_region_for_a_supervisor()
    {
        var id = Guid.NewGuid();
        var request = new SetCustomerOwnerRequest(NewOwner, [id]);
        Assert.Equal(CustomerOwnerResult.Forbidden, (await Service(new Store(new NewOwnerInfo(NewOwner, null)), approve: null).SetAsync(Tenant, Actor, request, CancellationToken.None)).Result);
        Assert.Equal(CustomerOwnerResult.NotFound, (await Service(new Store(null)).SetAsync(Tenant, Actor, request, CancellationToken.None)).Result);
        Assert.Equal(CustomerOwnerResult.InvalidData, (await Service(new Store(new NewOwnerInfo(NewOwner, null)), ownerCanRead: false).SetAsync(Tenant, Actor, request, CancellationToken.None)).Result);

        // A supervisor: only a user of the own region. A user of another region, or without a team, is refused.
        Assert.Equal(CustomerOwnerResult.Success, (await Service(new Store(new NewOwnerInfo(NewOwner, RegionA)), DataScope.Region, RegionA).SetAsync(Tenant, Actor, request, CancellationToken.None)).Result);
        Assert.Equal(CustomerOwnerResult.Forbidden, (await Service(new Store(new NewOwnerInfo(NewOwner, Guid.NewGuid())), DataScope.Region, RegionA).SetAsync(Tenant, Actor, request, CancellationToken.None)).Result);
        Assert.Equal(CustomerOwnerResult.Forbidden, (await Service(new Store(new NewOwnerInfo(NewOwner, null)), DataScope.Region, RegionA).SetAsync(Tenant, Actor, request, CancellationToken.None)).Result);
    }

    [Fact]
    public void The_list_filter_refuses_a_bad_range_an_empty_id_and_an_unknown_specialty_or_class()
    {
        Assert.True(new CustomerFilter(null, null, null, Guid.NewGuid(), Guid.NewGuid(), new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 1), "orthopedics", "a+", true, false).IsValid());
        Assert.False(new CustomerFilter(null, null, null, CreatedFrom: new DateOnly(2026, 11, 2), CreatedTo: new DateOnly(2026, 11, 1)).IsValid());
        Assert.False(new CustomerFilter(null, null, null, OwnerId: Guid.Empty).IsValid());
        Assert.False(new CustomerFilter(null, null, null, Specialty: "magic").IsValid());
        Assert.False(new CustomerFilter(null, null, null, CustomerClass: "Z").IsValid());
        Assert.True(new CustomerFilter(null, null, null).IsEmpty);
        Assert.False(new CustomerFilter(null, null, null, HasLocation: false).IsEmpty);
    }
}
