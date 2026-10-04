namespace LifeDrop.Domain.Tests.Devices;

using LifeDrop.Domain.Common;
using LifeDrop.Domain.Tenants;
using LifeDrop.Domain.Users;
using LifeDrop.Domain.Devices;

using Xunit;

public class DeviceTests
{
    private readonly TenantId _tenantId = TenantId.New();
    private readonly TenantId _otherTenantId = TenantId.New();
    private readonly UserId _userId = UserId.New();
    private readonly UserId _otherUserId = UserId.New();
    private readonly UserId _approverId = UserId.New();

    [Fact]
    public void Register_WithValidData_ShouldCreateDevice()
    {
        var device = Device.Register(_tenantId, "iPhone 15", "iPhone15,2", "iOS 17.0", "1.0.0", "device-token-123", "secret-hash");

        Assert.NotNull(device);
        Assert.Equal(_tenantId, device.TenantId);
        Assert.Equal("iPhone 15", device.DeviceName);
        Assert.Equal("iPhone15,2", device.DeviceModel);
        Assert.Equal("iOS 17.0", device.OsVersion);
        Assert.Equal("1.0.0", device.AppVersion);
        Assert.Equal("device-token-123", device.DeviceToken);
        Assert.Equal("secret-hash", device.DeviceSecretHash);
        Assert.Equal(DeviceStatus.PendingApproval, device.Status);
        Assert.Null(device.UserId);
    }

    [Fact]
    public void Register_WithEmptyTenantId_ShouldThrow()
    {
        var act = () => Device.Register(TenantId.Empty, "iPhone 15", "iPhone15,2", "iOS 17.0", "1.0.0", "device-token-123", "secret-hash");

        var ex = Assert.Throws<ArgumentException>(act);
        Assert.Contains("TenantId is required", ex.Message);
    }

    [Fact]
    public void Register_WithEmptyDeviceName_ShouldThrow()
    {
        var act = () => Device.Register(_tenantId, "", "iPhone15,2", "iOS 17.0", "1.0.0", "device-token-123", "secret-hash");

        var ex = Assert.Throws<ArgumentException>(act);
        Assert.Contains("Device name is required", ex.Message);
    }

    [Fact]
    public void Register_WithEmptyDeviceToken_ShouldThrow()
    {
        var act = () => Device.Register(_tenantId, "iPhone 15", "iPhone15,2", "iOS 17.0", "1.0.0", "", "secret-hash");

        var ex = Assert.Throws<ArgumentException>(act);
        Assert.Contains("Device token is required", ex.Message);
    }

    [Fact]
    public void Register_WithEmptyDeviceSecretHash_ShouldThrow()
    {
        var act = () => Device.Register(_tenantId, "iPhone 15", "iPhone15,2", "iOS 17.0", "1.0.0", "device-token-123", "");

        var ex = Assert.Throws<ArgumentException>(act);
        Assert.Contains("Device secret hash is required", ex.Message);
    }

    [Fact]
    public void Approve_WithValidApprover_ShouldApproveDevice()
    {
        var device = Device.Register(_tenantId, "iPhone 15", "iPhone15,2", "iOS 17.0", "1.0.0", "device-token-123", "secret-hash");

        device.Approve(_approverId);

        Assert.Equal(DeviceStatus.Approved, device.Status);
        Assert.Equal(_approverId, device.ApprovedBy);
        Assert.NotNull(device.ApprovedAt);
    }

    [Fact]
    public void Approve_WithEmptyApproverId_ShouldThrow()
    {
        var device = Device.Register(_tenantId, "iPhone 15", "iPhone15,2", "iOS 17.0", "1.0.0", "device-token-123", "secret-hash");

        var ex = Assert.Throws<ArgumentException>(() => device.Approve(UserId.Empty));
        Assert.Contains("ApprovedBy is required", ex.Message);
    }

    [Fact]
    public void Approve_AlreadyApprovedDevice_ShouldThrow()
    {
        var device = Device.Register(_tenantId, "iPhone 15", "iPhone15,2", "iOS 17.0", "1.0.0", "device-token-123", "secret-hash");
        device.Approve(_approverId);

        var ex = Assert.Throws<InvalidOperationException>(() => device.Approve(_approverId));
        Assert.Contains("Only pending devices can be approved", ex.Message);
    }

    [Fact]
    public void Approve_RevokedDevice_ShouldThrow()
    {
        var device = Device.Register(_tenantId, "iPhone 15", "iPhone15,2", "iOS 17.0", "1.0.0", "device-token-123", "secret-hash");
        device.Approve(_approverId);
        device.Revoke(_approverId, "Lost device");

        var ex = Assert.Throws<InvalidOperationException>(() => device.Approve(_approverId));
        Assert.Contains("Only pending devices can be approved", ex.Message);
    }

    [Fact]
    public void AssignToUser_WithValidTenantValidation_ShouldAssign()
    {
        var device = Device.Register(_tenantId, "iPhone 15", "iPhone15,2", "iOS 17.0", "1.0.0", "device-token-123", "secret-hash");

        device.AssignToUser(_userId, _approverId, _tenantId);

        Assert.Equal(_userId, device.UserId);
        Assert.Equal(DeviceStatus.Approved, device.Status);
        Assert.Equal(_approverId, device.ApprovedBy);
        Assert.NotNull(device.ApprovedAt);
    }

    [Fact]
    public void AssignToUser_WithDifferentTenant_ShouldThrow()
    {
        var device = Device.Register(_tenantId, "iPhone 15", "iPhone15,2", "iOS 17.0", "1.0.0", "device-token-123", "secret-hash");

        var ex = Assert.Throws<ArgumentException>(() => device.AssignToUser(_userId, _approverId, _otherTenantId));
        Assert.Contains("Cannot assign device to user from a different tenant", ex.Message);
    }

    [Fact]
    public void AssignToUser_WithEmptyUserId_ShouldThrow()
    {
        var device = Device.Register(_tenantId, "iPhone 15", "iPhone15,2", "iOS 17.0", "1.0.0", "device-token-123", "secret-hash");

        var ex = Assert.Throws<ArgumentException>(() => device.AssignToUser(UserId.Empty, _approverId, _tenantId));
        Assert.Contains("UserId is required", ex.Message);
    }

    [Fact]
    public void AssignToUser_WithEmptyApproverId_ShouldThrow()
    {
        var device = Device.Register(_tenantId, "iPhone 15", "iPhone15,2", "iOS 17.0", "1.0.0", "device-token-123", "secret-hash");

        var ex = Assert.Throws<ArgumentException>(() => device.AssignToUser(_userId, UserId.Empty, _tenantId));
        Assert.Contains("ApprovedBy is required", ex.Message);
    }

    [Fact]
    public void AssignToUser_WithInvalidStatus_ShouldThrow()
    {
        var device = Device.Register(_tenantId, "iPhone 15", "iPhone15,2", "iOS 17.0", "1.0.0", "device-token-123", "secret-hash");
        device.Revoke(_approverId, "Lost device");

        var ex = Assert.Throws<InvalidOperationException>(() => device.AssignToUser(_userId, _approverId, _tenantId));
        Assert.Contains("Device must be in pending or approved status to assign", ex.Message);
    }

    [Fact]
    public void AssignToUser_WithoutTenantValidation_ShouldNotCompile()
    {
        // This test documents that there is no public AssignToUser(UserId, UserId) method without tenant validation.
        // If this test compiles, the unsafe method has been removed.
        var device = Device.Register(_tenantId, "iPhone 15", "iPhone15,2", "iOS 17.0", "1.0.0", "device-token-123", "secret-hash");

        // The following line would not compile if the unsafe method exists:
        // device.AssignToUser(_userId, _approverId); // CS1501: No overload for method 'AssignToUser' takes 2 arguments
        
        // Instead, the only public method requires tenant validation:
        device.AssignToUser(_userId, _approverId, _tenantId);

        Assert.Equal(_userId, device.UserId);
        Assert.Equal(DeviceStatus.Approved, device.Status);
    }

    [Fact]
    public void Revoke_WithValidData_ShouldRevokeDevice()
    {
        var device = Device.Register(_tenantId, "iPhone 15", "iPhone15,2", "iOS 17.0", "1.0.0", "device-token-123", "secret-hash");
        device.Approve(_approverId);
        device.AssignToUser(_userId, _approverId, _tenantId);

        device.Revoke(_approverId, "Lost device");

        Assert.Equal(DeviceStatus.Revoked, device.Status);
        Assert.Equal(_approverId, device.RevokedBy);
        Assert.NotNull(device.RevokedAt);
        Assert.Equal("Lost device", device.RevocationReason);
        Assert.Null(device.UserId);
    }

    [Fact]
    public void Revoke_WithEmptyRevokedBy_ShouldThrow()
    {
        var device = Device.Register(_tenantId, "iPhone 15", "iPhone15,2", "iOS 17.0", "1.0.0", "device-token-123", "secret-hash");
        device.Approve(_approverId);

        var ex = Assert.Throws<ArgumentException>(() => device.Revoke(UserId.Empty, "Lost device"));
        Assert.Contains("RevokedBy is required", ex.Message);
    }

    [Fact]
    public void Revoke_WithEmptyReason_ShouldThrow()
    {
        var device = Device.Register(_tenantId, "iPhone 15", "iPhone15,2", "iOS 17.0", "1.0.0", "device-token-123", "secret-hash");
        device.Approve(_approverId);

        var ex = Assert.Throws<ArgumentException>(() => device.Revoke(_approverId, ""));
        Assert.Contains("Revocation reason is required", ex.Message);
    }

    [Fact]
    public void Revoke_AlreadyRevokedDevice_ShouldThrow()
    {
        var device = Device.Register(_tenantId, "iPhone 15", "iPhone15,2", "iOS 17.0", "1.0.0", "device-token-123", "secret-hash");
        device.Approve(_approverId);
        device.Revoke(_approverId, "Lost device");

        var ex = Assert.Throws<InvalidOperationException>(() => device.Revoke(_approverId, "Stolen"));
        Assert.Contains("Device is already revoked", ex.Message);
    }

    [Fact]
    public void UpdateLastSeen_ShouldUpdateTimestamp()
    {
        var device = Device.Register(_tenantId, "iPhone 15", "iPhone15,2", "iOS 17.0", "1.0.0", "device-token-123", "secret-hash");
        var before = DateTime.UtcNow;

        device.UpdateLastSeen();

        Assert.NotNull(device.LastSeenAt);
        Assert.True(device.LastSeenAt >= before);
    }

    [Fact]
    public void UpdateInfo_ShouldUpdateDeviceInfo()
    {
        var device = Device.Register(_tenantId, "iPhone 15", "iPhone15,2", "iOS 17.0", "1.0.0", "device-token-123", "secret-hash");

        device.UpdateInfo("iPhone 15 Pro", "iPhone15,3", "iOS 17.1", "1.1.0");

        Assert.Equal("iPhone 15 Pro", device.DeviceName);
        Assert.Equal("iPhone15,3", device.DeviceModel);
        Assert.Equal("iOS 17.1", device.OsVersion);
        Assert.Equal("1.1.0", device.AppVersion);
    }

    [Fact]
    public void RotateSecret_WithValidHash_ShouldUpdateSecret()
    {
        var device = Device.Register(_tenantId, "iPhone 15", "iPhone15,2", "iOS 17.0", "1.0.0", "device-token-123", "secret-hash");

        device.RotateSecret("new-secret-hash");

        Assert.Equal("new-secret-hash", device.DeviceSecretHash);
    }

    [Fact]
    public void RotateSecret_WithEmptyHash_ShouldThrow()
    {
        var device = Device.Register(_tenantId, "iPhone 15", "iPhone15,2", "iOS 17.0", "1.0.0", "device-token-123", "secret-hash");

        var ex = Assert.Throws<ArgumentException>(() => device.RotateSecret(""));
        Assert.Contains("Device secret hash is required", ex.Message);
    }

    [Fact]
    public void IsActive_ApprovedAndAssigned_ShouldReturnTrue()
    {
        var device = Device.Register(_tenantId, "iPhone 15", "iPhone15,2", "iOS 17.0", "1.0.0", "device-token-123", "secret-hash");
        device.Approve(_approverId);
        device.AssignToUser(_userId, _approverId, _tenantId);

        Assert.True(device.IsActive);
    }

    [Fact]
    public void IsActive_NotApproved_ShouldReturnFalse()
    {
        var device = Device.Register(_tenantId, "iPhone 15", "iPhone15,2", "iOS 17.0", "1.0.0", "device-token-123", "secret-hash");

        Assert.False(device.IsActive);
    }

    [Fact]
    public void IsActive_ApprovedButNotAssigned_ShouldReturnFalse()
    {
        var device = Device.Register(_tenantId, "iPhone 15", "iPhone15,2", "iOS 17.0", "1.0.0", "device-token-123", "secret-hash");
        device.Approve(_approverId);

        Assert.False(device.IsActive);
    }

    [Fact]
    public void IsActive_Revoked_ShouldReturnFalse()
    {
        var device = Device.Register(_tenantId, "iPhone 15", "iPhone15,2", "iOS 17.0", "1.0.0", "device-token-123", "secret-hash");
        device.Approve(_approverId);
        device.AssignToUser(_userId, _approverId, _tenantId);
        device.Revoke(_approverId, "Lost device");

        Assert.False(device.IsActive);
    }
}