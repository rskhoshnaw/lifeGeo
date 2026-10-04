namespace LifeDrop.Domain.Audit;

using LifeDrop.Domain.Common;
using LifeDrop.Domain.Tenants;
using LifeDrop.Domain.Users;
using System.Text.Json;

public class AuditLog : AggregateRoot<AuditLogId>
{
    public TenantId TenantId { get; private set; }
    public UserId? UserId { get; private set; }
    public string Action { get; private set; }
    public string EntityType { get; private set; }
    public string EntityId { get; private set; }
    public string? OldValuesJson { get; private set; }
    public string? NewValuesJson { get; private set; }
    public string? IpAddress { get; private set; }
    public string? UserAgent { get; private set; }
    public string? CorrelationId { get; private set; }
    public DateTime OccurredAt { get; private set; }

    private AuditLog(AuditLogId id, TenantId tenantId, UserId? userId, string action, string entityType, string entityId, AuditPayload? oldValues, AuditPayload? newValues, string? ipAddress, string? userAgent, string? correlationId) : base(id)
    {
        TenantId = tenantId;
        UserId = userId;
        Action = action;
        EntityType = entityType;
        EntityId = entityId;
        OldValuesJson = oldValues?.ToJson();
        NewValuesJson = newValues?.ToJson();
        IpAddress = ipAddress;
        UserAgent = userAgent;
        CorrelationId = correlationId;
        OccurredAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Creates an audit log with explicit audit payloads. Use this method to prevent sensitive data leakage.
    /// </summary>
    public static AuditLog Create(TenantId tenantId, UserId? userId, string action, string entityType, string entityId, AuditPayload? oldValues = null, AuditPayload? newValues = null, string? ipAddress = null, string? userAgent = null, string? correlationId = null)
    {
        if (tenantId.IsEmpty)
            throw new ArgumentException("TenantId is required", nameof(tenantId));
        if (string.IsNullOrWhiteSpace(action))
            throw new ArgumentException("Action is required", nameof(action));
        if (string.IsNullOrWhiteSpace(entityType))
            throw new ArgumentException("EntityType is required", nameof(entityType));
        if (string.IsNullOrWhiteSpace(entityId))
            throw new ArgumentException("EntityId is required", nameof(entityId));

        return new AuditLog(AuditLogId.New(), tenantId, userId, action.Trim(), entityType.Trim(), entityId.Trim(), oldValues, newValues, ipAddress, userAgent, correlationId);
    }

    /// <summary>
    /// Creates an audit log for a simple action without old/new values.
    /// </summary>
    public static AuditLog CreateAction(TenantId tenantId, UserId? userId, string action, string entityType, string entityId, string? ipAddress = null, string? userAgent = null, string? correlationId = null)
    {
        return Create(tenantId, userId, action, entityType, entityId, null, null, ipAddress, userAgent, correlationId);
    }

    public AuditPayload? GetOldValues()
    {
        if (string.IsNullOrEmpty(OldValuesJson))
            return null;
        return JsonSerializer.Deserialize<AuditPayload>(OldValuesJson);
    }

    public AuditPayload? GetNewValues()
    {
        if (string.IsNullOrEmpty(NewValuesJson))
            return null;
        return JsonSerializer.Deserialize<AuditPayload>(NewValuesJson);
    }
}