namespace LifeDrop.Domain.Auth;

using LifeDrop.Domain.Common;
using LifeDrop.Domain.Tenants;
using LifeDrop.Domain.Users;

public class RefreshToken : AggregateRoot<RefreshTokenId>
{
    public TenantId TenantId { get; private set; }
    public UserId UserId { get; private set; }
    public string TokenHash { get; private set; }
    public string DeviceToken { get; private set; }
    public DateTime ExpiresAt { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime? RevokedAt { get; private set; }
    public string? RevokedByIp { get; private set; }
    public string? ReplacedByTokenHash { get; private set; }

    private RefreshToken(RefreshTokenId id, TenantId tenantId, UserId userId, string tokenHash, string deviceToken, DateTime expiresAt) : base(id)
    {
        TenantId = tenantId;
        UserId = userId;
        TokenHash = tokenHash;
        DeviceToken = deviceToken;
        ExpiresAt = expiresAt;
        CreatedAt = DateTime.UtcNow;
    }

    public static RefreshToken Create(TenantId tenantId, UserId userId, string tokenHash, string deviceToken, DateTime expiresAt)
    {
        if (tenantId.IsEmpty)
            throw new ArgumentException("TenantId is required", nameof(tenantId));
        if (userId.IsEmpty)
            throw new ArgumentException("UserId is required", nameof(userId));
        if (string.IsNullOrWhiteSpace(tokenHash))
            throw new ArgumentException("Token hash is required", nameof(tokenHash));
        if (string.IsNullOrWhiteSpace(deviceToken))
            throw new ArgumentException("Device token is required", nameof(deviceToken));
        if (expiresAt <= DateTime.UtcNow)
            throw new ArgumentException("Expiration must be in the future", nameof(expiresAt));

        return new RefreshToken(RefreshTokenId.New(), tenantId, userId, tokenHash, deviceToken, expiresAt);
    }

    public bool IsExpired => DateTime.UtcNow >= ExpiresAt;
    public bool IsRevoked => RevokedAt.HasValue;
    public bool IsActive => !IsExpired && !IsRevoked;

    public void Revoke(string? revokedByIp = null, string? replacedByTokenHash = null)
    {
        RevokedAt = DateTime.UtcNow;
        RevokedByIp = revokedByIp;
        ReplacedByTokenHash = replacedByTokenHash;
    }
}