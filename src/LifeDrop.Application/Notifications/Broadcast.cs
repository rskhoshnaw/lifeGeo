using LifeDrop.Application.Roles;
using LifeDrop.Domain.Common;

namespace LifeDrop.Application.Notifications;

// Who gets a manager's message (plan step 4.16). UserIds: exactly those people. Otherwise the filters Role, RegionId and TeamId are combined (all must
// match); Everyone = true (no other filter) is every active person of the company. Only active users are reached, and a sender with region scope only
// reaches people of their own region.
public sealed record SendMessageRequest(string? Message, Guid[]? UserIds, string? Role, Guid? RegionId, Guid? TeamId, bool? Everyone);

public sealed record SendMessageResultDto(int Recipients);

public enum SendMessageOutcome { Success, Forbidden, InvalidData }

public sealed record MessageAudience(IReadOnlyList<Guid>? UserIds, string? Role, Guid? RegionId, Guid? TeamId, Guid? OnlyRegionId);

public interface IBroadcastStore
{
    // One transaction: a notification of kind manager.message (text in entity_id) for every active person of the audience, and one audit row
    // (notification.broadcast, with the number of people). Returns the number of people.
    Task<int> SendAsync(TenantId tenantId, UserId actorId, MessageAudience audience, string message, CancellationToken cancellationToken);
}

// The in-app message is delivered by the push and Telegram layers that already read the notifications table (plan steps 2.14 and 3.4).
public sealed class BroadcastService(IBroadcastStore store, IPermissionStore permissions)
{
    public const int MaxMessageLength = 500;
    public const int MaxUsers = 500;
    public const string Kind = "manager.message";

    public async Task<(SendMessageOutcome Result, SendMessageResultDto? Sent)> SendAsync(TenantId tenantId, UserId actorId, SendMessageRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var scope = tenantId.IsEmpty || actorId.IsEmpty ? null : await permissions.GetAccessScopeAsync(tenantId, actorId, PermissionKeys.NotificationsSend, cancellationToken);
        if (scope is null || scope.Scope == DataScope.Own || (scope.Scope == DataScope.Region && scope.RegionId is null)) return (SendMessageOutcome.Forbidden, null);

        var message = request.Message?.Trim();
        var role = string.IsNullOrWhiteSpace(request.Role) ? null : request.Role.Trim();
        var users = request.UserIds?.Distinct().ToArray();
        var hasFilter = role is not null || request.RegionId is not null || request.TeamId is not null;
        if (string.IsNullOrEmpty(message) || message.Length > MaxMessageLength || request.RegionId == Guid.Empty || request.TeamId == Guid.Empty ||
            (users is not null && (users.Length is 0 or > MaxUsers || users.Contains(Guid.Empty) || hasFilter || request.Everyone == true)) ||
            (users is null && !hasFilter && request.Everyone != true) ||
            (hasFilter && request.Everyone == true))
            return (SendMessageOutcome.InvalidData, null);

        var audience = new MessageAudience(users, role, request.RegionId, request.TeamId, scope.Scope == DataScope.Region ? scope.RegionId : null);
        return (SendMessageOutcome.Success, new SendMessageResultDto(await store.SendAsync(tenantId, actorId, audience, message, cancellationToken)));
    }
}
