using LifeDrop.Domain.Common;

namespace LifeDrop.Application.Notifications;

// Messages for one user (plan step 1.23-c). Kind: customer.profile_changed | customer.location_changed; EntityId is the id of the
// customer; Actor* is who made the change. A user reads and marks only their own messages.
public sealed record NotificationDto(Guid Id, string Kind, string EntityId, Guid ActorId, string? ActorEmail, string? CustomerCode, string? CustomerName,
    DateTimeOffset CreatedAt, DateTimeOffset? ReadAt);

public interface INotificationStore
{
    // Newest first; limit 1-200.
    Task<IReadOnlyList<NotificationDto>> ListAsync(TenantId tenantId, UserId userId, bool unreadOnly, int limit, CancellationToken cancellationToken);
    Task<int> CountUnreadAsync(TenantId tenantId, UserId userId, CancellationToken cancellationToken);
    // False when the message is not the user's.
    Task<bool> MarkReadAsync(TenantId tenantId, UserId userId, Guid id, CancellationToken cancellationToken);
    Task<int> MarkAllReadAsync(TenantId tenantId, UserId userId, CancellationToken cancellationToken);
}

public enum NotificationResult { Success, Forbidden, InvalidData, NotFound }

public sealed class NotificationService(INotificationStore store)
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 200;

    public async Task<(NotificationResult Result, IReadOnlyList<NotificationDto> Items)> ListAsync(TenantId tenantId, UserId userId, bool unreadOnly, int? limit,
        CancellationToken cancellationToken)
    {
        if (tenantId.IsEmpty || userId.IsEmpty) return (NotificationResult.Forbidden, Array.Empty<NotificationDto>());
        if (limit is < 1 or > MaxLimit) return (NotificationResult.InvalidData, Array.Empty<NotificationDto>());
        return (NotificationResult.Success, await store.ListAsync(tenantId, userId, unreadOnly, limit ?? DefaultLimit, cancellationToken));
    }

    public async Task<(NotificationResult Result, int Count)> CountUnreadAsync(TenantId tenantId, UserId userId, CancellationToken cancellationToken) =>
        tenantId.IsEmpty || userId.IsEmpty ? (NotificationResult.Forbidden, 0) : (NotificationResult.Success, await store.CountUnreadAsync(tenantId, userId, cancellationToken));

    public async Task<NotificationResult> MarkReadAsync(TenantId tenantId, UserId userId, Guid id, CancellationToken cancellationToken)
    {
        if (tenantId.IsEmpty || userId.IsEmpty) return NotificationResult.Forbidden;
        if (id == Guid.Empty) return NotificationResult.NotFound;
        return await store.MarkReadAsync(tenantId, userId, id, cancellationToken) ? NotificationResult.Success : NotificationResult.NotFound;
    }

    public async Task<(NotificationResult Result, int Marked)> MarkAllReadAsync(TenantId tenantId, UserId userId, CancellationToken cancellationToken) =>
        tenantId.IsEmpty || userId.IsEmpty ? (NotificationResult.Forbidden, 0) : (NotificationResult.Success, await store.MarkAllReadAsync(tenantId, userId, cancellationToken));
}
