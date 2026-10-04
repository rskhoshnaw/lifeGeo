using LifeDrop.Domain.Common;

namespace LifeDrop.Application.Roles;

public interface IPermissionStore
{
    Task<bool> HasPermissionAsync(TenantId tenantId, UserId userId, string permissionKey, CancellationToken cancellationToken);
}
