namespace LifeDrop.Domain.Tests.Users;

using LifeDrop.Domain.Common;
using LifeDrop.Domain.Tenants;
using LifeDrop.Domain.Users;
using Xunit;

public class UserTests
{
    private readonly TenantId _tenantId = TenantId.New();
    private readonly TenantId _otherTenantId = TenantId.New();

    [Fact]
    public void Create_WithValidData_ShouldCreateUser()
    {
        var user = User.Create(_tenantId, "test@example.com", "John Doe", "+1234567890", "hashed-password");

        Assert.NotNull(user);
        Assert.Equal(_tenantId, user.TenantId);
        Assert.Equal("test@example.com", user.Email);
        Assert.Equal("John Doe", user.FullName);
        Assert.True(user.IsActive);
        Assert.False(user.IsEmailVerified);
    }

    [Fact]
    public void Create_WithEmptyTenantId_ShouldThrow()
    {
        var act = () => User.Create(TenantId.Empty, "test@example.com", "John Doe", "+1234567890", "hashed-password");

        var ex = Assert.Throws<ArgumentException>(act);
        Assert.Contains("TenantId is required", ex.Message);
    }

    [Fact]
    public void Create_WithEmptyEmail_ShouldThrow()
    {
        var act = () => User.Create(_tenantId, "", "John Doe", "+1234567890", "hashed-password");

        var ex = Assert.Throws<ArgumentException>(act);
        Assert.Contains("Email is required", ex.Message);
    }

    [Fact]
    public void Create_WithEmptyPasswordHash_ShouldThrow()
    {
        var act = () => User.Create(_tenantId, "test@example.com", "John Doe", "+1234567890", "");

        var ex = Assert.Throws<ArgumentException>(act);
        Assert.Contains("Password hash is required", ex.Message);
    }

    [Fact]
    public void AddRole_WithValidTenantValidation_ShouldAddRole()
    {
        var user = User.Create(_tenantId, "test@example.com", "John Doe", "+1234567890", "hashed-password");
        var roleId = RoleId.New();

        user.AddRole(roleId, _tenantId);

        Assert.Contains(user.Roles, r => r.RoleId == roleId);
    }

    [Fact]
    public void AddRole_WithEmptyRoleIdAndTenantValidation_ShouldThrow()
    {
        var user = User.Create(_tenantId, "test@example.com", "John Doe", "+1234567890", "hashed-password");

        var ex = Assert.Throws<ArgumentException>(() => user.AddRole(RoleId.Empty, _tenantId));
        Assert.Contains("RoleId is required", ex.Message);
    }

    [Fact]
    public void AddRole_DuplicateRoleWithTenantValidation_ShouldNotAddTwice()
    {
        var user = User.Create(_tenantId, "test@example.com", "John Doe", "+1234567890", "hashed-password");
        var roleId = RoleId.New();

        user.AddRole(roleId, _tenantId);
        user.AddRole(roleId, _tenantId);

        Assert.Single(user.Roles, r => r.RoleId == roleId);
    }

    [Fact]
    public void RemoveRole_ExistingRole_ShouldRemove()
    {
        var user = User.Create(_tenantId, "test@example.com", "John Doe", "+1234567890", "hashed-password");
        var roleId = RoleId.New();
        user.AddRole(roleId, _tenantId);

        user.RemoveRole(roleId);

        Assert.Empty(user.Roles);
    }

    [Fact]
    public void RemoveRole_NonExistingRole_ShouldNotThrow()
    {
        var user = User.Create(_tenantId, "test@example.com", "John Doe", "+1234567890", "hashed-password");
        var roleId = RoleId.New();

        var ex = Record.Exception(() => user.RemoveRole(roleId));

        Assert.Null(ex);
    }

    [Fact]
    public void HasRole_ExistingRole_ShouldReturnTrue()
    {
        var user = User.Create(_tenantId, "test@example.com", "John Doe", "+1234567890", "hashed-password");
        var roleId = RoleId.New();
        user.AddRole(roleId, _tenantId);

        Assert.True(user.HasRole(roleId));
    }

    [Fact]
    public void HasRole_NonExistingRole_ShouldReturnFalse()
    {
        var user = User.Create(_tenantId, "test@example.com", "John Doe", "+1234567890", "hashed-password");
        var roleId = RoleId.New();

        Assert.False(user.HasRole(roleId));
    }

    [Fact]
    public void Activate_ShouldSetIsActiveTrue()
    {
        var user = User.Create(_tenantId, "test@example.com", "John Doe", "+1234567890", "hashed-password");
        user.Deactivate();

        user.Activate();

        Assert.True(user.IsActive);
    }

    [Fact]
    public void Deactivate_ShouldSetIsActiveFalse()
    {
        var user = User.Create(_tenantId, "test@example.com", "John Doe", "+1234567890", "hashed-password");

        user.Deactivate();

        Assert.False(user.IsActive);
    }

    [Fact]
    public void ChangeEmail_ShouldUpdateEmailAndUnverify()
    {
        var user = User.Create(_tenantId, "test@example.com", "John Doe", "+1234567890", "hashed-password");
        user.MarkEmailVerified();

        user.ChangeEmail("new@example.com");

        Assert.Equal("new@example.com", user.Email);
        Assert.False(user.IsEmailVerified);
    }

    [Fact]
    public void ChangePassword_ShouldUpdatePasswordHash()
    {
        var user = User.Create(_tenantId, "test@example.com", "John Doe", "+1234567890", "hashed-password");

        user.ChangePassword("new-hashed-password");

        Assert.Equal("new-hashed-password", user.PasswordHash);
    }

    [Fact]
    public void AddRole_WithSameTenant_ShouldAddRole()
    {
        var user = User.Create(_tenantId, "test@example.com", "John Doe", "+1234567890", "hashed-password");
        var roleId = RoleId.New();

        user.AddRole(roleId, _tenantId);

        Assert.Contains(user.Roles, r => r.RoleId == roleId);
    }

    [Fact]
    public void AddRole_WithDifferentTenant_ShouldThrow()
    {
        var user = User.Create(_tenantId, "test@example.com", "John Doe", "+1234567890", "hashed-password");
        var roleId = RoleId.New();
        var otherTenantId = TenantId.New();

        var ex = Assert.Throws<ArgumentException>(() => user.AddRole(roleId, otherTenantId));
        Assert.Contains("Cannot assign role from a different tenant", ex.Message);
    }
}
