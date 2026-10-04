namespace LifeDrop.Domain.Users;

using LifeDrop.Domain.Common;

public class RolePermission : Entity<(RoleId RoleId, string PermissionKey)>
{
    public RoleId RoleId => Id.RoleId;
    public string PermissionKey => Id.PermissionKey;

    public RolePermission(RoleId roleId, string permissionKey) : base((roleId, permissionKey))
    {
    }
}
