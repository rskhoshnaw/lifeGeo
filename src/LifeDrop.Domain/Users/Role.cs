namespace LifeDrop.Domain.Users;

using LifeDrop.Domain.Common;
using LifeDrop.Domain.Tenants;

public class Role : AggregateRoot<RoleId>
{
    public TenantId TenantId { get; private set; }
    public string Name { get; private set; }
    public string Description { get; private set; }
    public bool IsSystemRole { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime? UpdatedAt { get; private set; }

    private readonly List<RolePermission> _permissions = new();
    public IReadOnlyList<RolePermission> Permissions => _permissions.AsReadOnly();

    private Role(RoleId id, TenantId tenantId, string name, string description, bool isSystemRole) : base(id)
    {
        TenantId = tenantId;
        Name = name;
        Description = description;
        IsSystemRole = isSystemRole;
        CreatedAt = DateTime.UtcNow;
    }

    public static Role Create(TenantId tenantId, string name, string description, bool isSystemRole = false)
    {
        if (tenantId.IsEmpty)
            throw new ArgumentException("TenantId is required", nameof(tenantId));
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Role name is required", nameof(name));

        return new Role(RoleId.New(), tenantId, name.Trim(), description?.Trim() ?? string.Empty, isSystemRole);
    }

    public void Update(string name, string description)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Role name is required", nameof(name));
        Name = name.Trim();
        Description = description?.Trim() ?? string.Empty;
        UpdatedAt = DateTime.UtcNow;
    }

    public void AddPermission(string permissionKey)
    {
        if (string.IsNullOrWhiteSpace(permissionKey))
            throw new ArgumentException("Permission key is required", nameof(permissionKey));
        if (_permissions.Any(p => p.PermissionKey == permissionKey))
            return;
        _permissions.Add(new RolePermission(Id, permissionKey));
        UpdatedAt = DateTime.UtcNow;
    }

    public void RemovePermission(string permissionKey)
    {
        var perm = _permissions.FirstOrDefault(p => p.PermissionKey == permissionKey);
        if (perm is not null)
        {
            _permissions.Remove(perm);
            UpdatedAt = DateTime.UtcNow;
        }
    }

    public bool HasPermission(string permissionKey) => _permissions.Any(p => p.PermissionKey == permissionKey);
}
