namespace LifeDrop.Domain.Users;

using LifeDrop.Domain.Common;
using LifeDrop.Domain.Tenants;

public class User : AggregateRoot<UserId>
{
    public TenantId TenantId { get; private set; }
    public string Email { get; private set; }
    public string FullName { get; private set; }
    public string PhoneNumber { get; private set; }
    public string PasswordHash { get; private set; }
    public bool IsActive { get; private set; }
    public bool IsEmailVerified { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime? UpdatedAt { get; private set; }
    public DateTime? LastLoginAt { get; private set; }

    private readonly List<UserRole> _roles = new();
    public IReadOnlyList<UserRole> Roles => _roles.AsReadOnly();

    private User(UserId id, TenantId tenantId, string email, string fullName, string phoneNumber, string passwordHash) : base(id)
    {
        TenantId = tenantId;
        Email = email;
        FullName = fullName;
        PhoneNumber = phoneNumber;
        PasswordHash = passwordHash;
        IsActive = true;
        IsEmailVerified = false;
        CreatedAt = DateTime.UtcNow;
    }

    public static User Create(TenantId tenantId, string email, string fullName, string phoneNumber, string passwordHash)
    {
        if (tenantId.IsEmpty)
            throw new ArgumentException("TenantId is required", nameof(tenantId));
        if (string.IsNullOrWhiteSpace(email))
            throw new ArgumentException("Email is required", nameof(email));
        if (string.IsNullOrWhiteSpace(fullName))
            throw new ArgumentException("Full name is required", nameof(fullName));
        if (string.IsNullOrWhiteSpace(passwordHash))
            throw new ArgumentException("Password hash is required", nameof(passwordHash));

        return new User(UserId.New(), tenantId, email.Trim().ToLowerInvariant(), fullName.Trim(), phoneNumber?.Trim() ?? string.Empty, passwordHash);
    }

    public void UpdateProfile(string fullName, string phoneNumber)
    {
        if (string.IsNullOrWhiteSpace(fullName))
            throw new ArgumentException("Full name is required", nameof(fullName));
        FullName = fullName.Trim();
        PhoneNumber = phoneNumber?.Trim() ?? string.Empty;
        UpdatedAt = DateTime.UtcNow;
    }

    public void ChangeEmail(string email)
    {
        if (string.IsNullOrWhiteSpace(email))
            throw new ArgumentException("Email is required", nameof(email));
        Email = email.Trim().ToLowerInvariant();
        IsEmailVerified = false;
        UpdatedAt = DateTime.UtcNow;
    }

    public void ChangePassword(string newPasswordHash)
    {
        if (string.IsNullOrWhiteSpace(newPasswordHash))
            throw new ArgumentException("Password hash is required", nameof(newPasswordHash));
        PasswordHash = newPasswordHash;
        UpdatedAt = DateTime.UtcNow;
    }

    public void MarkEmailVerified()
    {
        IsEmailVerified = true;
        UpdatedAt = DateTime.UtcNow;
    }

    public void RecordLogin()
    {
        LastLoginAt = DateTime.UtcNow;
    }

    /// <summary>
/// Adds a role with tenant validation. The role must belong to the same tenant as the user.
/// </summary>
/// <param name="roleId">The role to assign</param>
/// <param name="roleTenantId">The tenant ID of the role (for cross-tenant validation)</param>
/// <exception cref="ArgumentException">Thrown when role belongs to a different tenant</exception>
public void AddRole(RoleId roleId, TenantId roleTenantId)
    {
        if (roleId.IsEmpty)
            throw new ArgumentException("RoleId is required", nameof(roleId));
        if (roleTenantId != TenantId)
            throw new ArgumentException("Cannot assign role from a different tenant", nameof(roleTenantId));
        if (_roles.Any(r => r.RoleId == roleId))
            return;
        _roles.Add(new UserRole(Id, roleId));
        UpdatedAt = DateTime.UtcNow;
    }

    public void RemoveRole(RoleId roleId)
    {
        var role = _roles.FirstOrDefault(r => r.RoleId == roleId);
        if (role is not null)
        {
            _roles.Remove(role);
            UpdatedAt = DateTime.UtcNow;
        }
    }

    public void Activate()
    {
        IsActive = true;
        UpdatedAt = DateTime.UtcNow;
    }

    public void Deactivate()
    {
        IsActive = false;
        UpdatedAt = DateTime.UtcNow;
    }

    public bool HasRole(RoleId roleId) => _roles.Any(r => r.RoleId == roleId);
}
