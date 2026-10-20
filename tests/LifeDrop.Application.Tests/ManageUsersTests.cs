using LifeDrop.Application.Roles;
using LifeDrop.Application.Users;
using LifeDrop.Domain.Common;
using Xunit;

namespace LifeDrop.Application.Tests;

public class ManageUsersTests
{
    private static readonly TenantId Tenant = TenantId.New();
    private static readonly UserId Actor = UserId.New();
    private static readonly UserId Target = UserId.New();

    private sealed class Permissions(params string[] granted) : IPermissionStore
    {
        public Task<bool> HasPermissionAsync(TenantId tenantId, UserId userId, string permissionKey, CancellationToken cancellationToken) =>
            Task.FromResult(granted.Contains(permissionKey));
    }

    private sealed class Store : IUserAccountStore
    {
        public List<string> Calls = [];
        public ManageUserResult Result = ManageUserResult.Done;

        public Task<ManageUserResult> ChangeEmailAsync(TenantId tenantId, UserId actorId, UserId targetId, string email, CancellationToken cancellationToken) => Record("email:" + email);
        public Task<ManageUserResult> SetPasswordAsync(TenantId tenantId, UserId actorId, UserId targetId, string password, CancellationToken cancellationToken) => Record("password");
        public Task<ManageUserResult> SetActiveAsync(TenantId tenantId, UserId actorId, UserId targetId, bool active, CancellationToken cancellationToken) => Record("active:" + active);
        public Task<ManageUserResult> SetCityAsync(TenantId tenantId, UserId actorId, UserId targetId, string? city, CancellationToken cancellationToken) => Record("city:" + city);
        public Task<ManageUserResult> RemoveRoleAsync(TenantId tenantId, UserId actorId, UserId targetId, Guid roleId, CancellationToken cancellationToken) => Record("removeRole");
        private Task<ManageUserResult> Record(string call) { Calls.Add(call); return Task.FromResult(Result); }
    }

    private static ManageUsers Service(Store store, params string[] granted) => new(store, new Permissions(granted));

    [Fact]
    public async Task Role_managers_and_system_admins_reach_the_store_with_a_normalised_email()
    {
        var store = new Store();
        Assert.Equal(ManageUserResult.Done, await Service(store, PermissionKeys.UserManageRoles).ChangeEmailAsync(Tenant, Actor, Target, new ChangeUserEmailRequest("  New@Example.Test "), CancellationToken.None));
        Assert.Equal(ManageUserResult.Done, await Service(store, PermissionKeys.SystemAdmin).SetActiveAsync(Tenant, Actor, Target, false, CancellationToken.None));
        Assert.Equal(new[] { "email:new@example.test", "active:False" }, store.Calls.ToArray());
    }

    [Fact]
    public async Task Without_the_permission_nothing_reaches_the_store()
    {
        var store = new Store();
        var service = Service(store, "customers.read");
        Assert.Equal(ManageUserResult.Forbidden, await service.ChangeEmailAsync(Tenant, Actor, Target, new ChangeUserEmailRequest("a@b.co"), CancellationToken.None));
        Assert.Equal(ManageUserResult.Forbidden, await service.SetPasswordAsync(Tenant, Actor, Target, new SetUserPasswordRequest("a-long-password-1"), CancellationToken.None));
        Assert.Equal(ManageUserResult.Forbidden, await service.SetActiveAsync(Tenant, Actor, Target, true, CancellationToken.None));
        Assert.Empty(store.Calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-an-email")]
    public async Task A_bad_email_is_invalid(string? email)
    {
        var store = new Store();
        Assert.Equal(ManageUserResult.InvalidData, await Service(store, PermissionKeys.UserManageRoles).ChangeEmailAsync(Tenant, Actor, Target, new ChangeUserEmailRequest(email), CancellationToken.None));
        Assert.Empty(store.Calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("short")]
    public async Task A_short_password_is_invalid(string? password)
    {
        var store = new Store();
        Assert.Equal(ManageUserResult.InvalidData, await Service(store, PermissionKeys.UserManageRoles).SetPasswordAsync(Tenant, Actor, Target, new SetUserPasswordRequest(password), CancellationToken.None));
        Assert.Empty(store.Calls);
    }

    [Fact]
    public async Task Nobody_deactivates_their_own_account_but_may_activate_it()
    {
        var store = new Store();
        var service = Service(store, PermissionKeys.UserManageRoles);
        Assert.Equal(ManageUserResult.Conflict, await service.SetActiveAsync(Tenant, Actor, Actor, false, CancellationToken.None));
        Assert.Empty(store.Calls);
        Assert.Equal(ManageUserResult.Done, await service.SetActiveAsync(Tenant, Actor, Actor, true, CancellationToken.None));
    }

    [Fact]
    public async Task The_stores_answer_is_passed_on()
    {
        var store = new Store { Result = ManageUserResult.NotFound };
        Assert.Equal(ManageUserResult.NotFound, await Service(store, PermissionKeys.UserManageRoles).SetPasswordAsync(Tenant, Actor, Target, new SetUserPasswordRequest("a-long-password-1"), CancellationToken.None));
    }

    [Fact]
    public async Task The_city_is_trimmed_and_an_empty_one_clears_it_and_a_long_one_is_invalid()
    {
        var store = new Store();
        var service = Service(store, PermissionKeys.UserManageRoles);
        Assert.Equal(ManageUserResult.Done, await service.SetCityAsync(Tenant, Actor, Target, new SetUserCityRequest("  Erbil "), CancellationToken.None));
        Assert.Equal(ManageUserResult.Done, await service.SetCityAsync(Tenant, Actor, Target, new SetUserCityRequest("  "), CancellationToken.None));
        Assert.Equal(ManageUserResult.InvalidData, await service.SetCityAsync(Tenant, Actor, Target, new SetUserCityRequest(new string('x', ManageUsers.MaxCityLength + 1)), CancellationToken.None));
        Assert.Equal(ManageUserResult.Forbidden, await Service(store, "customers.read").SetCityAsync(Tenant, Actor, Target, new SetUserCityRequest("Erbil"), CancellationToken.None));
        Assert.Equal(new[] { "city:Erbil", "city:" }, store.Calls.ToArray());
    }

    [Fact]
    public async Task Nobody_takes_a_role_from_themselves_and_it_needs_the_manage_permission()
    {
        var store = new Store();
        var service = Service(store, PermissionKeys.UserManageRoles);
        Assert.Equal(ManageUserResult.Conflict, await service.RemoveRoleAsync(Tenant, Actor, Actor, Guid.NewGuid(), CancellationToken.None));
        Assert.Equal(ManageUserResult.InvalidData, await service.RemoveRoleAsync(Tenant, Actor, Target, Guid.Empty, CancellationToken.None));
        Assert.Equal(ManageUserResult.Forbidden, await Service(store, "customers.read").RemoveRoleAsync(Tenant, Actor, Target, Guid.NewGuid(), CancellationToken.None));
        Assert.Empty(store.Calls);
        Assert.Equal(ManageUserResult.Done, await service.RemoveRoleAsync(Tenant, Actor, Target, Guid.NewGuid(), CancellationToken.None));
        Assert.Single(store.Calls);
    }
}
