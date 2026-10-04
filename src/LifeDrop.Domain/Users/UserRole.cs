namespace LifeDrop.Domain.Users;

using LifeDrop.Domain.Common;

public class UserRole : Entity<(UserId UserId, RoleId RoleId)>
{
    public UserId UserId => Id.UserId;
    public RoleId RoleId => Id.RoleId;

    public UserRole(UserId userId, RoleId roleId) : base((userId, roleId))
    {
    }
}
