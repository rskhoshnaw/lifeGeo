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
        public bool ContactResult = true;

        public Task<CustomerDto?> GetByIdAsync(TenantId tenantId, CustomerId id, AccessScope scope, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CreateCustomerResult> CreateAsync(TenantId tenantId, UserId actorId, CustomerId id, CreateCustomerRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<CustomerDto>> ListAsync(TenantId tenantId, AccessScope scope, int limit, DateTimeOffset? cursorCreatedAt, Guid? cursorId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<CustomerUpdateResult> EditAsync(TenantId tenantId, UserId actorId, CustomerId id, AccessScope scope, string type, string code, string name, CancellationToken cancellationToken)
        {
            Calls.Add($"edit:{type}|{code}|{name}");
            return Task.FromResult(EditResult);
        }

        public bool DoctorResult = true;
        public Task<bool> SetDoctorAsync(TenantId tenantId, UserId actorId, CustomerId id, AccessScope scope, CustomerDoctorRequest details, CancellationToken cancellationToken)
        {
            Calls.Add($"doctor:{details.ClinicName}|{details.Specialty}|{details.Cooperates}|{details.Dealer}|{details.CustomerClass}");
            return Task.FromResult(DoctorResult);
        }

        public Task<bool> SetContactAsync(TenantId tenantId, UserId actorId, CustomerId id, AccessScope scope, string? contactName, string? phone2, string? notes, CancellationToken cancellationToken)
        {
            Calls.Add($"contact:{contactName}|{phone2}|{notes}");
            return Task.FromResult(ContactResult);
        }

        public Task<IReadOnlyList<CustomerDto>> SearchAsync(TenantId tenantId, AccessScope scope, int limit, DateTimeOffset? cursorCreatedAt, Guid? cursorId, string search, CancellationToken cancellationToken)
        {
            Calls.Add("search:" + search);
            return Task.FromResult<IReadOnlyList<CustomerDto>>([]);
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

    [Fact]
    public async Task Contact_details_are_trimmed_and_empty_ones_clear_the_field()
    {
        var store = new Store();
        var service = Service(store, PermissionKeys.CustomersWrite);
        Assert.Equal(CustomerUpdateResult.Success, await service.SetContactAsync(Tenant, Actor, Customer, new CustomerContactRequest(" Dr. Ali ", "  ", "Pays on Sunday"), CancellationToken.None));
        Assert.Equal(new[] { "contact:Dr. Ali||Pays on Sunday" }, store.Calls.ToArray());
    }

    [Fact]
    public async Task Contact_details_need_customers_write_and_sensible_lengths()
    {
        var store = new Store();
        Assert.Equal(CustomerUpdateResult.Forbidden, await Service(store, PermissionKeys.CustomersRead).SetContactAsync(Tenant, Actor, Customer, new CustomerContactRequest("a", null, null), CancellationToken.None));
        var service = Service(store, PermissionKeys.CustomersWrite);
        Assert.Equal(CustomerUpdateResult.InvalidData, await service.SetContactAsync(Tenant, Actor, Customer, new CustomerContactRequest(new string('x', CustomerEditService.MaxContactNameLength + 1), null, null), CancellationToken.None));
        Assert.Equal(CustomerUpdateResult.InvalidData, await service.SetContactAsync(Tenant, Actor, Customer, new CustomerContactRequest(null, new string('1', CustomerEditService.MaxPhone2Length + 1), null), CancellationToken.None));
        Assert.Equal(CustomerUpdateResult.InvalidData, await service.SetContactAsync(Tenant, Actor, Customer, new CustomerContactRequest(null, null, new string('n', CustomerEditService.MaxNotesLength + 1)), CancellationToken.None));
        Assert.Empty(store.Calls);
        store.ContactResult = false;
        Assert.Equal(CustomerUpdateResult.NotFound, await service.SetContactAsync(Tenant, Actor, Customer, new CustomerContactRequest("a", null, null), CancellationToken.None));
    }

    [Theory]
    [InlineData("Pharmacy", "pharmacy")]
    [InlineData("MedicalAssistant", "pharmacy")]
    [InlineData("Doctor", "doctor")]
    [InlineData("Wholesaler", "wholesaler")]
    [InlineData("Clinic", "other")]
    public void A_medical_assistant_is_visited_with_the_pharmacy_form(string customerType, string visitType) =>
        Assert.Equal(visitType, LifeDrop.Application.Visits.PlanGenerator.VisitTypeOf(customerType));

    [Fact]
    public async Task Doctor_details_are_trimmed_and_checked_against_the_fixed_lists()
    {
        var store = new Store();
        var service = Service(store, PermissionKeys.CustomersWrite);
        Assert.Equal(CustomerUpdateResult.Success, await service.SetDoctorAsync(Tenant, Actor, Customer, new CustomerDoctorRequest(" Azadi Complex ", "orthopedics", true, false, "A+"), CancellationToken.None));
        Assert.Equal(CustomerUpdateResult.Success, await service.SetDoctorAsync(Tenant, Actor, Customer, new CustomerDoctorRequest(" ", null, null, null, null), CancellationToken.None));
        Assert.Equal(new[] { "doctor:Azadi Complex|orthopedics|True|False|A+", "doctor:||||" }, store.Calls.ToArray());
        Assert.Equal(CustomerUpdateResult.InvalidData, await service.SetDoctorAsync(Tenant, Actor, Customer, new CustomerDoctorRequest(null, "dentist", null, null, null), CancellationToken.None));
        Assert.Equal(CustomerUpdateResult.InvalidData, await service.SetDoctorAsync(Tenant, Actor, Customer, new CustomerDoctorRequest(null, null, null, null, "D"), CancellationToken.None));
        Assert.Equal(CustomerUpdateResult.InvalidData, await service.SetDoctorAsync(Tenant, Actor, Customer, new CustomerDoctorRequest(new string('c', CustomerEditService.MaxClinicNameLength + 1), null, null, null, null), CancellationToken.None));
        Assert.Equal(CustomerUpdateResult.Forbidden, await Service(store, PermissionKeys.CustomersRead).SetDoctorAsync(Tenant, Actor, Customer, new CustomerDoctorRequest(null, null, null, null, null), CancellationToken.None));
        Assert.Equal(2, store.Calls.Count);
        store.DoctorResult = false;
        Assert.Equal(CustomerUpdateResult.NotFound, await service.SetDoctorAsync(Tenant, Actor, Customer, new CustomerDoctorRequest(null, null, null, null, null), CancellationToken.None));
    }

    [Theory]
    [InlineData("a")]
    [InlineData(" a ")]
    public async Task A_search_needs_two_characters(string text)
    {
        var store = new Store();
        var (valid, _) = await new ListCustomers(store).SearchAsync(Tenant, AccessScope.WholeTenant(Actor), null, null, null, text, CancellationToken.None);
        Assert.False(valid);
        Assert.Empty(store.Calls);
    }

    [Fact]
    public async Task A_search_reaches_the_store_with_the_trimmed_text()
    {
        var store = new Store();
        var (valid, _) = await new ListCustomers(store).SearchAsync(Tenant, AccessScope.WholeTenant(Actor), null, null, null, "  shifa ", CancellationToken.None);
        Assert.True(valid);
        Assert.Equal(new[] { "search:shifa" }, store.Calls.ToArray());
    }
}
