using LifeDrop.Application.Customers;
using LifeDrop.Application.Roles;
using LifeDrop.Domain.Common;
using Xunit;

namespace LifeDrop.Application.Tests.Customers;

public class CustomerEditServiceTests
{
    private static readonly TenantId Tenant = TenantId.New();
    private static readonly UserId Actor = UserId.New();
    private static readonly Guid Customer = Guid.NewGuid();

    private sealed class Permissions(params string[] granted) : IPermissionStore
    {
        public Task<bool> HasPermissionAsync(TenantId tenantId, UserId userId, string permissionKey, CancellationToken cancellationToken) =>
            Task.FromResult(granted.Contains(permissionKey));
    }

    private sealed class Store : ICustomerStore
    {
        public List<string> Calls = [];
        public CustomerUpdateResult EditResult = CustomerUpdateResult.Success;
        public bool ArchiveResult = true;

        public Task<CustomerDto?> GetByIdAsync(TenantId tenantId, CustomerId id, AccessScope scope, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CreateCustomerResult> CreateAsync(TenantId tenantId, UserId actorId, CustomerId id, CreateCustomerRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<CustomerDto>> ListAsync(TenantId tenantId, AccessScope scope, int limit, DateTimeOffset? cursorCreatedAt, Guid? cursorId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<CustomerUpdateResult> EditAsync(TenantId tenantId, UserId actorId, CustomerId id, AccessScope scope, string type, string code, string name, CancellationToken cancellationToken)
        {
            Calls.Add($"edit:{type}|{code}|{name}");
            return Task.FromResult(EditResult);
        }

        public Task<bool> SetArchivedAsync(TenantId tenantId, UserId actorId, CustomerId id, AccessScope scope, bool archived, CancellationToken cancellationToken)
        {
            Calls.Add("archived:" + archived);
            return Task.FromResult(ArchiveResult);
        }
    }

    private static CustomerEditService Service(Store store, params string[] granted) => new(store, new Permissions(granted));

    [Fact]
    public async Task Edit_trims_and_reaches_the_store_with_customers_write()
    {
        var store = new Store();
        Assert.Equal(CustomerUpdateResult.Success, await Service(store, PermissionKeys.CustomersWrite)
            .EditAsync(Tenant, Actor, Customer, new EditCustomerRequest(" clinic ", " A-9 ", " New name "), CancellationToken.None));
        Assert.Equal(new[] { "edit:clinic|A-9|New name" }, store.Calls.ToArray());
    }

    [Fact]
    public async Task Edit_without_the_permission_or_with_bad_data_never_reaches_the_store()
    {
        var store = new Store();
        Assert.Equal(CustomerUpdateResult.Forbidden, await Service(store, PermissionKeys.CustomersRead)
            .EditAsync(Tenant, Actor, Customer, new EditCustomerRequest("t", "c", "n"), CancellationToken.None));
        var service = Service(store, PermissionKeys.CustomersWrite);
        Assert.Equal(CustomerUpdateResult.InvalidData, await service.EditAsync(Tenant, Actor, Customer, new EditCustomerRequest("t", " ", "n"), CancellationToken.None));
        Assert.Equal(CustomerUpdateResult.InvalidData, await service.EditAsync(Tenant, Actor, Customer, new EditCustomerRequest(null, "c", "n"), CancellationToken.None));
        Assert.Equal(CustomerUpdateResult.InvalidData, await service.EditAsync(Tenant, Actor, Customer, new EditCustomerRequest("t", "c", new string('x', CustomerEditService.MaxNameLength + 1)), CancellationToken.None));
        Assert.Equal(CustomerUpdateResult.InvalidData, await service.EditAsync(Tenant, Actor, Guid.Empty, new EditCustomerRequest("t", "c", "n"), CancellationToken.None));
        Assert.Empty(store.Calls);
    }

    [Fact]
    public async Task A_taken_code_is_reported_as_conflict()
    {
        var store = new Store { EditResult = CustomerUpdateResult.Conflict };
        Assert.Equal(CustomerUpdateResult.Conflict, await Service(store, PermissionKeys.CustomersWrite)
            .EditAsync(Tenant, Actor, Customer, new EditCustomerRequest("t", "c", "n"), CancellationToken.None));
    }

    [Fact]
    public async Task Archive_and_restore_need_customers_approve_not_just_write()
    {
        var store = new Store();
        Assert.Equal(CustomerUpdateResult.Forbidden, await Service(store, PermissionKeys.CustomersWrite).SetArchivedAsync(Tenant, Actor, Customer, true, CancellationToken.None));
        Assert.Empty(store.Calls);
        var manager = Service(store, PermissionKeys.CustomersApprove);
        Assert.Equal(CustomerUpdateResult.Success, await manager.SetArchivedAsync(Tenant, Actor, Customer, true, CancellationToken.None));
        Assert.Equal(CustomerUpdateResult.Success, await manager.SetArchivedAsync(Tenant, Actor, Customer, false, CancellationToken.None));
        Assert.Equal(new[] { "archived:True", "archived:False" }, store.Calls.ToArray());
        store.ArchiveResult = false;
        Assert.Equal(CustomerUpdateResult.NotFound, await manager.SetArchivedAsync(Tenant, Actor, Customer, true, CancellationToken.None));
    }
}
