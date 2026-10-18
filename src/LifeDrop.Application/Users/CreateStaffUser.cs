using LifeDrop.Application.Roles;
using LifeDrop.Domain.Common;

namespace LifeDrop.Application.Users;

public sealed record CreateUserRequest(string? Email, string? Password, Guid? RoleId);

public sealed record CreatedUserDto(Guid Id);

public enum CreateUserResult { Created, Forbidden, InvalidData, EmailTaken, RoleNotFound }

public interface IUserCreationStore
{
    // One transaction: the user, its first role and the audit row (user.created), or nothing. The store hashes the password; it is never stored or logged in clear text.
    Task<(CreateUserResult Result, Guid? UserId)> CreateAsync(TenantId tenantId, UserId actorId, string email, string password, RoleId roleId, CancellationToken cancellationToken);
}

// A manager adds an account (for example a sales rep) from the panel (plan step 4.9). Needs the same permission as giving roles (user.manageroles or system.admin);
// the account belongs to the actor's own company and starts with one role of that company. A role that needs an approved device (the field roles) still needs the phone approved.
public sealed class CreateStaffUser(IUserCreationStore store, IPermissionStore permissions)
{
    public const int MinPasswordLength = 12;
    public const int MaxPasswordLength = 200;
    public const int MaxEmailLength = 200;

    public async Task<(CreateUserResult Result, CreatedUserDto? Created)> ExecuteAsync(TenantId tenantId, UserId actorId, CreateUserRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (tenantId.IsEmpty || actorId.IsEmpty) return (CreateUserResult.Forbidden, null);
        if (!await permissions.HasPermissionAsync(tenantId, actorId, PermissionKeys.UserManageRoles, cancellationToken) &&
            !await permissions.HasPermissionAsync(tenantId, actorId, PermissionKeys.SystemAdmin, cancellationToken))
            return (CreateUserResult.Forbidden, null);

        var email = request.Email?.Trim().ToLowerInvariant();
        if (email is null || email.Length > MaxEmailLength || !IsPlainEmail(email) ||
            request.Password is null || request.Password.Length < MinPasswordLength || request.Password.Length > MaxPasswordLength ||
            request.RoleId is null || request.RoleId == Guid.Empty)
            return (CreateUserResult.InvalidData, null);

        var (result, userId) = await store.CreateAsync(tenantId, actorId, email, request.Password, new RoleId(request.RoleId.Value), cancellationToken);
        return result == CreateUserResult.Created && userId is { } id ? (result, new CreatedUserDto(id)) : (result, null);
    }

    public static bool IsPlainEmail(string email)
    {
        if (email.Any(c => char.IsWhiteSpace(c) || c is '\'' or '"' or '<' or '>' or ',' or ';')) return false;
        var at = email.IndexOf('@');
        return at > 0 && at == email.LastIndexOf('@') && at < email.Length - 1 && email[(at + 1)..].Contains('.') && !email.EndsWith('.');
    }
}
