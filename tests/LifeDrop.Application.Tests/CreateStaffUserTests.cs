using LifeDrop.Application.Roles;
using LifeDrop.Application.Users;
using LifeDrop.Domain.Common;
using Xunit;

namespace LifeDrop.Application.Tests;

public class CreateStaffUserTests
{
    private static readonly TenantId Tenant = TenantId.New();
    private static readonly UserId Actor = UserId.New();
    private static readonly Guid Role = Guid.NewGuid();
    private const string GoodPassword = "a-long-password-1";

    private sealed class Permissions(params string[] granted) : IPermissionStore
    {
        public Task<bool> HasPermissionAsync(TenantId tenantId, UserId userId, string permissionKey, CancellationToken cancellationToken) =>
            Task.FromResult(granted.Contains(permissionKey));
    }

    private sealed class Store(CreateUserResult result = CreateUserResult.Created) : IUserCreationStore
    {
        public int Calls;
        public (TenantId Tenant, UserId Actor, string Email, string Password, RoleId Role)? Last;
        public Guid NewId = Guid.NewGuid();

        public Task<(CreateUserResult Result, Guid? UserId)> CreateAsync(TenantId tenantId, UserId actorId, string email, string password, RoleId roleId, CancellationToken cancellationToken)
        {
            Calls++;
            Last = (tenantId, actorId, email, password, roleId);
            return Task.FromResult<(CreateUserResult, Guid?)>((result, result == CreateUserResult.Created ? NewId : null));
        }
    }

    private static Task<(CreateUserResult Result, CreatedUserDto? Created)> Run(Store store, Permissions permissions, CreateUserRequest request, TenantId? tenant = null) =>
        new CreateStaffUser(store, permissions).ExecuteAsync(tenant ?? Tenant, Actor, request, CancellationToken.None);

    [Fact]
    public async Task Manager_with_the_role_permission_creates_the_user_in_their_own_company()
    {
        var store = new Store();
        var (result, created) = await Run(store, new Permissions(PermissionKeys.UserManageRoles), new CreateUserRequest("  Rep@Example.Test ", GoodPassword, Role));
        Assert.Equal(CreateUserResult.Created, result);
        Assert.Equal(store.NewId, created!.Id);
        Assert.Equal((Tenant, Actor, "rep@example.test", GoodPassword, new RoleId(Role)), store.Last);
    }

    [Fact]
    public async Task System_admin_may_create_users_too()
    {
        var store = new Store();
        Assert.Equal(CreateUserResult.Created, (await Run(store, new Permissions(PermissionKeys.SystemAdmin), new CreateUserRequest("a@b.co", GoodPassword, Role))).Result);
    }

    [Fact]
    public async Task Without_the_permission_nothing_reaches_the_store()
    {
        var store = new Store();
        var (result, _) = await Run(store, new Permissions("customers.read"), new CreateUserRequest("a@b.co", GoodPassword, Role));
        Assert.Equal(CreateUserResult.Forbidden, result);
        Assert.Equal(0, store.Calls);
    }

    [Fact]
    public async Task Empty_tenant_is_refused()
    {
        var store = new Store();
        Assert.Equal(CreateUserResult.Forbidden, (await Run(store, new Permissions(PermissionKeys.UserManageRoles), new CreateUserRequest("a@b.co", GoodPassword, Role), TenantId.Empty)).Result);
        Assert.Equal(0, store.Calls);
    }

    [Theory]
    [InlineData(null, GoodPassword)]
    [InlineData("", GoodPassword)]
    [InlineData("no-at-sign", GoodPassword)]
    [InlineData("two@@signs.test", GoodPassword)]
    [InlineData("a b@c.test", GoodPassword)]
    [InlineData("o'brien@c.test", GoodPassword)]
    [InlineData("a@nodot", GoodPassword)]
    [InlineData("@c.test", GoodPassword)]
    [InlineData("a@b.co", null)]
    [InlineData("a@b.co", "short-pw-11")]
    public async Task Bad_input_is_refused_before_the_store(string? email, string? password)
    {
        var store = new Store();
        var (result, _) = await Run(store, new Permissions(PermissionKeys.UserManageRoles), new CreateUserRequest(email, password, Role));
        Assert.Equal(CreateUserResult.InvalidData, result);
        Assert.Equal(0, store.Calls);
    }

    [Fact]
    public async Task A_role_is_required()
    {
        var store = new Store();
        var permissions = new Permissions(PermissionKeys.UserManageRoles);
        Assert.Equal(CreateUserResult.InvalidData, (await Run(store, permissions, new CreateUserRequest("a@b.co", GoodPassword, null))).Result);
        Assert.Equal(CreateUserResult.InvalidData, (await Run(store, permissions, new CreateUserRequest("a@b.co", GoodPassword, Guid.Empty))).Result);
        Assert.Equal(0, store.Calls);
    }

    [Theory]
    [InlineData(CreateUserResult.EmailTaken)]
    [InlineData(CreateUserResult.RoleNotFound)]
    public async Task Store_answers_are_passed_on_without_a_user(CreateUserResult answer)
    {
        var (result, created) = await Run(new Store(answer), new Permissions(PermissionKeys.UserManageRoles), new CreateUserRequest("a@b.co", GoodPassword, Role));
        Assert.Equal(answer, result);
        Assert.Null(created);
    }
}
