namespace LifeDrop.Domain.Tests.Audit;

using LifeDrop.Domain.Common;
using LifeDrop.Domain.Tenants;
using LifeDrop.Domain.Users;
using LifeDrop.Domain.Devices;
using LifeDrop.Domain.Audit;

using Xunit;

public class AuditLogTests
{
    private readonly TenantId _tenantId = TenantId.New();
    private readonly UserId _userId = UserId.New();

    [Fact]
    public void Create_WithAuditPayload_ShouldCreateAuditLog()
    {
        var oldUser = User.Create(_tenantId, "old@example.com", "Old Name", "", "hash");
        var newUser = User.Create(_tenantId, "new@example.com", "New Name", "", "hash");
        var oldValues = AuditPayload.FromUser(oldUser);
        var newValues = AuditPayload.FromUser(newUser);

        var auditLog = AuditLog.Create(_tenantId, _userId, AuditActions.UserUpdated, "User", _userId.ToString(), oldValues, newValues);

        Assert.NotNull(auditLog);
        Assert.Equal(_tenantId, auditLog.TenantId);
        Assert.Equal(_userId, auditLog.UserId);
        Assert.Equal(AuditActions.UserUpdated, auditLog.Action);
        Assert.Equal("User", auditLog.EntityType);
        Assert.Equal(_userId.ToString(), auditLog.EntityId);
        Assert.False(string.IsNullOrEmpty(auditLog.OldValuesJson));
        Assert.False(string.IsNullOrEmpty(auditLog.NewValuesJson));
    }

    [Fact]
    public void Create_WithActionOnly_ShouldCreateAuditLog()
    {
        var auditLog = AuditLog.CreateAction(_tenantId, _userId, AuditActions.Login, "Auth", "session-123");

        Assert.NotNull(auditLog);
        Assert.Equal(AuditActions.Login, auditLog.Action);
        Assert.Null(auditLog.OldValuesJson);
        Assert.Null(auditLog.NewValuesJson);
    }

    [Fact]
    public void Create_WithEmptyTenantId_ShouldThrow()
    {
        var act = () => AuditLog.Create(TenantId.Empty, _userId, AuditActions.Login, "Auth", "session-123");

        var ex = Assert.Throws<ArgumentException>(act);
        Assert.Contains("TenantId is required", ex.Message);
    }

    [Fact]
    public void Create_WithEmptyAction_ShouldThrow()
    {
        var act = () => AuditLog.Create(_tenantId, _userId, "", "Auth", "session-123");

        var ex = Assert.Throws<ArgumentException>(act);
        Assert.Contains("Action is required", ex.Message);
    }

    [Fact]
    public void Create_WithEmptyEntityType_ShouldThrow()
    {
        var act = () => AuditLog.Create(_tenantId, _userId, AuditActions.Login, "", "session-123");

        var ex = Assert.Throws<ArgumentException>(act);
        Assert.Contains("EntityType is required", ex.Message);
    }

    [Fact]
    public void Create_WithEmptyEntityId_ShouldThrow()
    {
        var act = () => AuditLog.Create(_tenantId, _userId, AuditActions.Login, "Auth", "");

        var ex = Assert.Throws<ArgumentException>(act);
        Assert.Contains("EntityId is required", ex.Message);
    }

    [Fact]
    public void GetOldValues_ShouldDeserializePayload()
    {
        var oldUser = User.Create(_tenantId, "old@example.com", "Old Name", "", "hash");
        var newUser = User.Create(_tenantId, "new@example.com", "New Name", "", "hash");
        var oldValues = AuditPayload.FromUser(oldUser);
        var newValues = AuditPayload.FromUser(newUser);

        var auditLog = AuditLog.Create(_tenantId, _userId, AuditActions.UserUpdated, "User", _userId.ToString(), oldValues, newValues);

        var retrievedOld = auditLog.GetOldValues();
        var retrievedNew = auditLog.GetNewValues();

        Assert.NotNull(retrievedOld);
        Assert.Equal("old@example.com", retrievedOld!.Email);
        Assert.Equal("Old Name", retrievedOld.FullName);

        Assert.NotNull(retrievedNew);
        Assert.Equal("new@example.com", retrievedNew!.Email);
        Assert.Equal("New Name", retrievedNew.FullName);
    }

    [Fact]
    public void GetOldValues_WhenNull_ShouldReturnNull()
    {
        var auditLog = AuditLog.CreateAction(_tenantId, _userId, AuditActions.Login, "Auth", "session-123");

        var retrievedOld = auditLog.GetOldValues();
        var retrievedNew = auditLog.GetNewValues();

        Assert.Null(retrievedOld);
        Assert.Null(retrievedNew);
    }

    [Fact]
    public void AuditPayload_FromTenant_ShouldContainSafeDataOnly()
    {
        var tenant = Tenant.Create("Test Tenant", "TEST");
        
        var payload = AuditPayload.FromTenant(tenant);

        Assert.Equal(tenant.Id.ToString(), payload.Id);
        Assert.Equal("Test Tenant", payload.Name);
        Assert.Equal("TEST", payload.Code);
        Assert.True(payload.IsActive);
        Assert.Equal(tenant.CreatedAt, payload.CreatedAt);
        // Sensitive fields should not be present (no password, tokens, etc.)
    }

    [Fact]
    public void AuditPayload_FromUser_ShouldContainSafeDataOnly()
    {
        var user = User.Create(_tenantId, "test@example.com", "John Doe", "+1234567890", "hashed-password");
        
        var payload = AuditPayload.FromUser(user);

        Assert.Equal(user.Id.ToString(), payload.Id);
        Assert.Equal("test@example.com", payload.Email);
        Assert.Equal("John Doe", payload.FullName);
        Assert.True(payload.IsActive);
        Assert.False(payload.IsEmailVerified);
        // PasswordHash should NOT be in the payload
    }

    [Fact]
    public void AuditPayload_FromDevice_ShouldContainSafeDataOnly()
    {
        var device = Device.Register(_tenantId, "iPhone 15", "iPhone15,2", "iOS 17.0", "1.0.0", "device-token-123", "secret-hash");
        
        var payload = AuditPayload.FromDevice(device);

        Assert.Equal(device.Id.ToString(), payload.Id);
        Assert.Equal("iPhone 15", payload.DeviceName);
        Assert.Equal("iPhone15,2", payload.DeviceModel);
        Assert.Equal("PendingApproval", payload.Status);
        // DeviceSecretHash and DeviceToken should NOT be in the payload
    }
}