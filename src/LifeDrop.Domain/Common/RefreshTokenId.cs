namespace LifeDrop.Domain.Common;

public readonly record struct RefreshTokenId(Guid Value)
{
    public static RefreshTokenId New() => new(Guid.NewGuid());
    public static RefreshTokenId Empty => new(Guid.Empty);
    public bool IsEmpty => Value == Guid.Empty;
    public override string ToString() => Value.ToString();
}
