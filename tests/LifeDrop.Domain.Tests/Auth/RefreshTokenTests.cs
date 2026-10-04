namespace LifeDrop.Domain.Tests.Auth;

using LifeDrop.Domain.Common;
using LifeDrop.Domain.Tenants;
using LifeDrop.Domain.Users;
using LifeDrop.Domain.Auth;
using Xunit;

public class RefreshTokenTests
{
    private readonly TenantId _tenantId = TenantId.New();
    private readonly TenantId _otherTenantId = TenantId.New();
    private readonly UserId _userId = UserId.New();
    private readonly UserId _otherUserId = UserId.New();

    [Fact]
    public void Create_WithValidData_ShouldCreateRefreshToken()
    {
        var expiresAt = DateTime.UtcNow.AddDays(7);
        var token = RefreshToken.Create(_tenantId, _userId, "token-hash", "device-token", expiresAt);

        Assert.NotNull(token);
        Assert.Equal(_tenantId, token.TenantId);
        Assert.Equal(_userId, token.UserId);
        Assert.Equal("token-hash", token.TokenHash);
        Assert.Equal("device-token", token.DeviceToken);
        Assert.Equal(expiresAt, token.ExpiresAt);
        Assert.True(token.IsActive);
        Assert.False(token.IsRevoked);
        Assert.False(token.IsExpired);
    }

    [Fact]
    public void Create_WithEmptyTenantId_ShouldThrow()
    {
        var expiresAt = DateTime.UtcNow.AddDays(7);
        var act = () => RefreshToken.Create(TenantId.Empty, _userId, "token-hash", "device-token", expiresAt);

        var ex = Assert.Throws<ArgumentException>(act);
        Assert.Contains("TenantId is required", ex.Message);
    }

    [Fact]
    public void Create_WithEmptyUserId_ShouldThrow()
    {
        var expiresAt = DateTime.UtcNow.AddDays(7);
        var act = () => RefreshToken.Create(_tenantId, UserId.Empty, "token-hash", "device-token", expiresAt);

        var ex = Assert.Throws<ArgumentException>(act);
        Assert.Contains("UserId is required", ex.Message);
    }

    [Fact]
    public void Create_WithEmptyTokenHash_ShouldThrow()
    {
        var expiresAt = DateTime.UtcNow.AddDays(7);
        var act = () => RefreshToken.Create(_tenantId, _userId, "", "device-token", expiresAt);

        var ex = Assert.Throws<ArgumentException>(act);
        Assert.Contains("Token hash is required", ex.Message);
    }

    [Fact]
    public void Create_WithEmptyDeviceToken_ShouldThrow()
    {
        var expiresAt = DateTime.UtcNow.AddDays(7);
        var act = () => RefreshToken.Create(_tenantId, _userId, "token-hash", "", expiresAt);

        var ex = Assert.Throws<ArgumentException>(act);
        Assert.Contains("Device token is required", ex.Message);
    }

    [Fact]
    public void Create_WithPastExpiration_ShouldThrow()
    {
        var expiresAt = DateTime.UtcNow.AddMinutes(-1);
        var act = () => RefreshToken.Create(_tenantId, _userId, "token-hash", "device-token", expiresAt);

        var ex = Assert.Throws<ArgumentException>(act);
        Assert.Contains("Expiration must be in the future", ex.Message);
    }

    [Fact]
    public void Create_WithCurrentExpiration_ShouldThrow()
    {
        var expiresAt = DateTime.UtcNow;
        var act = () => RefreshToken.Create(_tenantId, _userId, "token-hash", "device-token", expiresAt);

        var ex = Assert.Throws<ArgumentException>(act);
        Assert.Contains("Expiration must be in the future", ex.Message);
    }

    [Fact]
    public void Revoke_ShouldMarkAsRevoked()
    {
        var expiresAt = DateTime.UtcNow.AddDays(7);
        var token = RefreshToken.Create(_tenantId, _userId, "token-hash", "device-token", expiresAt);

        token.Revoke("192.168.1.1", "new-token-hash");

        Assert.True(token.IsRevoked);
        Assert.False(token.IsActive);
        Assert.Equal("192.168.1.1", token.RevokedByIp);
        Assert.Equal("new-token-hash", token.ReplacedByTokenHash);
        Assert.NotNull(token.RevokedAt);
    }

    [Fact]
    public void Revoke_WithoutParameters_ShouldMarkAsRevoked()
    {
        var expiresAt = DateTime.UtcNow.AddDays(7);
        var token = RefreshToken.Create(_tenantId, _userId, "token-hash", "device-token", expiresAt);

        token.Revoke();

        Assert.True(token.IsRevoked);
        Assert.False(token.IsActive);
        Assert.Null(token.RevokedByIp);
        Assert.Null(token.ReplacedByTokenHash);
        Assert.NotNull(token.RevokedAt);
    }

    [Fact]
    public void IsExpired_WhenFutureExpiration_ShouldReturnFalse()
    {
        var expiresAt = DateTime.UtcNow.AddDays(7);
        var token = RefreshToken.Create(_tenantId, _userId, "token-hash", "device-token", expiresAt);

        Assert.False(token.IsExpired);
    }

    [Fact]
    public void IsActive_WhenNotExpiredAndNotRevoked_ShouldReturnTrue()
    {
        var expiresAt = DateTime.UtcNow.AddDays(7);
        var token = RefreshToken.Create(_tenantId, _userId, "token-hash", "device-token", expiresAt);

        Assert.True(token.IsActive);
    }

    [Fact]
    public void IsActive_WhenRevoked_ShouldReturnFalse()
    {
        var expiresAt = DateTime.UtcNow.AddDays(7);
        var token = RefreshToken.Create(_tenantId, _userId, "token-hash", "device-token", expiresAt);
        token.Revoke();

        Assert.False(token.IsActive);
    }

    [Fact]
    public void IsActive_AfterRevoke_ShouldReturnFalse()
    {
        var expiresAt = DateTime.UtcNow.AddDays(7);
        var token = RefreshToken.Create(_tenantId, _userId, "token-hash", "device-token", expiresAt);
        
        token.Revoke();
        
        Assert.True(token.IsRevoked);
        Assert.False(token.IsActive);
    }
}