namespace LifeDrop.Domain.Tenants;

using LifeDrop.Domain.Common;

public class Tenant : AggregateRoot<TenantId>
{
    public string Name { get; private set; }
    public string Code { get; private set; }
    public bool IsActive { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime? UpdatedAt { get; private set; }

    private Tenant(TenantId id, string name, string code) : base(id)
    {
        Name = name;
        Code = code;
        IsActive = true;
        CreatedAt = DateTime.UtcNow;
    }

    public static Tenant Create(string name, string code)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Tenant name cannot be empty", nameof(name));
        if (string.IsNullOrWhiteSpace(code))
            throw new ArgumentException("Tenant code cannot be empty", nameof(code));

        return new Tenant(TenantId.New(), name.Trim(), code.Trim().ToUpperInvariant());
    }

    public void UpdateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Tenant name cannot be empty", nameof(name));
        Name = name.Trim();
        UpdatedAt = DateTime.UtcNow;
    }

    public void UpdateCode(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
            throw new ArgumentException("Tenant code cannot be empty", nameof(code));
        Code = code.Trim().ToUpperInvariant();
        UpdatedAt = DateTime.UtcNow;
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
}