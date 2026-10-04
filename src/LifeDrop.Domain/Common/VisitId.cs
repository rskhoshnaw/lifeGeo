namespace LifeDrop.Domain.Common;

public readonly record struct VisitId(Guid Value)
{
    public static VisitId New() => new(Guid.NewGuid());
    public static VisitId Empty => new(Guid.Empty);
    public bool IsEmpty => Value == Guid.Empty;
    public override string ToString() => Value.ToString();
}
