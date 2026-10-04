namespace LifeDrop.Domain.Tests.Users;

using LifeDrop.Domain.Common;
using LifeDrop.Domain.Tenants;
using LifeDrop.Domain.Users;
using Xunit;

public class RoleTests
{
    private readonly TenantId _tenantId = TenantId.New();
    private readonly TenantId _otherTenantId = TenantId.New();

    [Fact]
    public void Create_WithValidData_ShouldCreateRole()
    {
        var role = Role.Create(_tenantId, "Admin", "Administrator role", false);

        Assert.NotNull(role);
        Assert.Equal(_tenantId, role.TenantId);
        Assert.Equal("Admin", role.Name);
        Assert.Equal("Administrator role", role.Description);
        Assert.False(role.IsSystemRole);
    }

    [Fact]
    public void Create_WithEmptyTenantId_ShouldThrow()
    {
        var act = () => Role.Create(TenantId.Empty, "Admin", "Description", false);

        var ex = Assert.Throws<ArgumentException>(act);
        Assert.Contains("TenantId is required", ex.Message);
    }

    [Fact]
    public void Create_WithEmptyName_ShouldThrow()
    {
        var act = () => Role.Create(_tenantId, "", "Description", false);

        var ex = Assert.Throws<ArgumentException>(act);
        Assert.Contains("Role name is required", ex.Message);
    }

    [Fact]
    public void Create_WithSystemRoleFlag_ShouldSetIsSystemRole()
    {
        var role = Role.Create(_tenantId, "SystemAdmin", "System admin", true);

        Assert.True(role.IsSystemRole);
    }

    [Fact]
    public void Update_WithValidData_ShouldUpdateRole()
    {
        var role = Role.Create(_tenantId, "Admin", "Old description", false);

        role.Update("SuperAdmin", "New description");

        Assert.Equal("SuperAdmin", role.Name);
        Assert.Equal("New description", role.Description);
    }

    [Fact]
    public void Update_WithEmptyName_ShouldThrow()
    {
        var role = Role.Create(_tenantId, "Admin", "Description", false);

        var ex = Assert.Throws<ArgumentException>(() => role.Update("", "New description"));
        Assert.Contains("Role name is required", ex.Message);
    }

    [Fact]
    public void AddPermission_WithValidKey_ShouldAddPermission()
    {
        var role = Role.Create(_tenantId, "Admin", "Description", false);

        role.AddPermission(Permissions.User_View);

        var perms = role.Permissions;
        Assert.Single(perms, p => p.PermissionKey == Permissions.User_View);
    }

    [Fact]
    public void AddPermission_WithEmptyKey_ShouldThrow()
    {
        var role = Role.Create(_tenantId, "Admin", "Description", false);

        var ex = Assert.Throws<ArgumentException>(() => role.AddPermission(""));
        Assert.Contains("Permission key is required", ex.Message);
    }

    [Fact]
    public void AddPermission_DuplicatePermission_ShouldNotAddTwice()
    {
        var role = Role.Create(_tenantId, "Admin", "Description", false);

        role.AddPermission(Permissions.User_View);
        role.AddPermission(Permissions.User_View);

        var perms = role.Permissions;
        Assert.Single(perms, p => p.PermissionKey == Permissions.User_View);
    }

    [Fact]
    public void RemovePermission_ExistingPermission_ShouldRemove()
    {
        var role = Role.Create(_tenantId, "Admin", "Description", false);
        role.AddPermission(Permissions.User_View);

        role.RemovePermission(Permissions.User_View);

        Assert.Empty(role.Permissions);
    }

    [Fact]
    public void RemovePermission_NonExistingPermission_ShouldNotThrow()
    {
        var role = Role.Create(_tenantId, "Admin", "Description", false);

        var ex = Record.Exception(() => role.RemovePermission(Permissions.User_View));

        Assert.Null(ex);
    }

    [Fact]
    public void HasPermission_ExistingPermission_ShouldReturnTrue()
    {
        var role = Role.Create(_tenantId, "Admin", "Description", false);
        role.AddPermission(Permissions.User_View);

        Assert.True(role.HasPermission(Permissions.User_View));
    }

    [Fact]
    public void HasPermission_NonExistingPermission_ShouldReturnFalse()
    {
        var role = Role.Create(_tenantId, "Admin", "Description", false);

        Assert.False(role.HasPermission(Permissions.User_View));
    }
}