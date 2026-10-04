namespace LifeDrop.Domain.Audit;

using LifeDrop.Domain.Common;
using LifeDrop.Domain.Tenants;
using LifeDrop.Domain.Users;
using LifeDrop.Domain.Devices;
using LifeDrop.Domain.Auth;
using System.Text.Json;

public record AuditPayload
{
    public string? Id { get; init; }
    public string? Name { get; init; }
    public string? Code { get; init; }
    public string? Email { get; init; }
    public string? FullName { get; init; }
    public string? DeviceName { get; init; }
    public string? DeviceModel { get; init; }
    public string? Status { get; init; }
    public string? RoleName { get; init; }
    public string? PermissionKey { get; init; }
    public bool? IsActive { get; init; }
    public bool? IsEmailVerified { get; init; }
    public bool? IsSystemRole { get; init; }
    public DateTime? CreatedAt { get; init; }
    public DateTime? UpdatedAt { get; init; }
    public DateTime? ApprovedAt { get; init; }
    public DateTime? RevokedAt { get; init; }
    public string? RevocationReason { get; init; }
    public string? Action { get; init; }

    public static AuditPayload FromTenant(Tenant tenant) => new()
    {
        Id = tenant.Id.ToString(),
        Name = tenant.Name,
        Code = tenant.Code,
        IsActive = tenant.IsActive,
        CreatedAt = tenant.CreatedAt,
        UpdatedAt = tenant.UpdatedAt
    };

    public static AuditPayload FromUser(User user) => new()
    {
        Id = user.Id.ToString(),
        Email = user.Email,
        FullName = user.FullName,
        IsActive = user.IsActive,
        IsEmailVerified = user.IsEmailVerified,
        CreatedAt = user.CreatedAt,
        UpdatedAt = user.UpdatedAt
    };

    public static AuditPayload FromRole(Role role) => new()
    {
        Id = role.Id.ToString(),
        Name = role.Name,
        RoleName = role.Name,
        IsSystemRole = role.IsSystemRole,
        CreatedAt = role.CreatedAt,
        UpdatedAt = role.UpdatedAt
    };

    public static AuditPayload FromDevice(Device device) => new()
    {
        Id = device.Id.ToString(),
        DeviceName = device.DeviceName,
        DeviceModel = device.DeviceModel,
        Status = device.Status.ToString(),
        IsActive = device.IsActive,
        CreatedAt = device.RegisteredAt,
        ApprovedAt = device.ApprovedAt,
        RevokedAt = device.RevokedAt,
        RevocationReason = device.RevocationReason
    };

    public static AuditPayload FromAction(string action) => new() { Action = action };

    public string ToJson() => JsonSerializer.Serialize(this);
}