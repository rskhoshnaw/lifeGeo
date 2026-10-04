namespace LifeDrop.Domain.Common;

public abstract class Entity<TId> where TId : notnull
{
    public TId Id { get; protected init; }

    protected Entity(TId id)
    {
        Id = id;
    }

    public override bool Equals(object? obj)
    {
        if (obj is not Entity<TId> other)
            return false;

        if (ReferenceEquals(this, other))
            return true;

        if (Id.Equals(default(TId)) || other.Id.Equals(default(TId)))
            return false;

        return Id.Equals(other.Id);
    }

    public override int GetHashCode() => Id.GetHashCode();

    public static bool operator ==(Entity<TId> a, Entity<TId> b)
    {
        if (a is null && b is null)
            return true;
        if (a is null || b is null)
            return false;
        return a.Equals(b);
    }

    public static bool operator !=(Entity<TId> a, Entity<TId> b) => !(a == b);
}
