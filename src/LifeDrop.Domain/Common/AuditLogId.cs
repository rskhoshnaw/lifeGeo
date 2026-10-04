namespace LifeDrop.Domain.Common;

public readonly record struct AuditLogId(Guid Value)
{
    public static AuditLogId New() => new(Guid.NewGuid());
    public static AuditLogId Empty => new(Guid.Empty);
    public bool IsEmpty => Value == Guid.Empty;
    public override string ToString() => Value.ToString();
}
