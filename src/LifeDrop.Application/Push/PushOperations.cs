using LifeDrop.Domain.Common;

namespace LifeDrop.Application.Push;

// Push to the phone (plan step 2.14). The in-app message (`notifications`) is the source; one data-only push per message
// carries kind + entity id and the app writes the localized text. Only Firebase (free) is built; SMS is paid.
public sealed record PushMessage(Guid NotificationId, string Kind, string EntityId);

public enum PushOutcome { Sent, InvalidToken, Failed }

public interface IPushSender
{
    // False when no provider key is configured: nothing is sent and no message is consumed.
    bool IsConfigured { get; }
    Task<PushOutcome> SendAsync(string token, PushMessage message, CancellationToken cancellationToken);
}

public sealed record PendingPush(Guid NotificationId, UserId UserId, string Kind, string EntityId, int Attempts, IReadOnlyList<string> Tokens);

public interface IPushStore
{
    // The token moves to this user when another user had it.
    Task RegisterTokenAsync(TenantId tenantId, UserId userId, string token, string platform, CancellationToken cancellationToken);
    Task<bool> RemoveTokenAsync(TenantId tenantId, UserId userId, string token, CancellationToken cancellationToken);
    // Unpushed messages from the last `maxAge`, oldest first, with the tokens of their user.
    Task<IReadOnlyList<PendingPush>> ListPendingAsync(TenantId tenantId, TimeSpan maxAge, int limit, CancellationToken cancellationToken);
    Task MarkPushedAsync(TenantId tenantId, Guid notificationId, CancellationToken cancellationToken);
    Task CountAttemptAsync(TenantId tenantId, Guid notificationId, CancellationToken cancellationToken);
    // A token the provider says is dead (any user).
    Task DropTokenAsync(TenantId tenantId, string token, CancellationToken cancellationToken);
    // Messages older than maxAge are marked without sending.
    Task ExpireOldAsync(TenantId tenantId, TimeSpan maxAge, CancellationToken cancellationToken);
}

public enum PushResult { Success, Forbidden, InvalidData, NotFound }

public sealed class PushTokenService(IPushStore store)
{
    public const int MinTokenLength = 20;
    public const int MaxTokenLength = 4096;

    public async Task<PushResult> RegisterAsync(TenantId tenantId, UserId userId, string? token, string? platform, CancellationToken cancellationToken)
    {
        if (tenantId.IsEmpty || userId.IsEmpty) return PushResult.Forbidden;
        token = token?.Trim();
        platform = platform?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(token) || token.Length is < MinTokenLength or > MaxTokenLength || platform is not ("android" or "ios")) return PushResult.InvalidData;
        await store.RegisterTokenAsync(tenantId, userId, token, platform, cancellationToken);
        return PushResult.Success;
    }

    // Only the owner removes a token (sign-out on the phone).
    public async Task<PushResult> RemoveAsync(TenantId tenantId, UserId userId, string? token, CancellationToken cancellationToken)
    {
        if (tenantId.IsEmpty || userId.IsEmpty) return PushResult.Forbidden;
        if (string.IsNullOrWhiteSpace(token)) return PushResult.InvalidData;
        return await store.RemoveTokenAsync(tenantId, userId, token.Trim(), cancellationToken) ? PushResult.Success : PushResult.NotFound;
    }
}

// Sends the waiting messages of one company. A message is done when one token took it, when its user has no token, or after
// MaxAttempts temporary failures; dead tokens are dropped. Messages older than MaxAge are not sent any more (a stale
// "customer changed" push would only confuse).
public sealed class PushDispatcher(IPushStore store, IPushSender sender)
{
    public const int MaxAttempts = 5;
    public const int BatchSize = 100;
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);

    public async Task<int> DispatchTenantAsync(TenantId tenantId, CancellationToken cancellationToken)
    {
        if (!sender.IsConfigured || tenantId.IsEmpty) return 0;
        await store.ExpireOldAsync(tenantId, MaxAge, cancellationToken);
        var sentCount = 0;
        foreach (var pending in await store.ListPendingAsync(tenantId, MaxAge, BatchSize, cancellationToken))
        {
            var message = new PushMessage(pending.NotificationId, pending.Kind, pending.EntityId);
            var sent = false;
            var failed = false;
            foreach (var token in pending.Tokens)
            {
                switch (await sender.SendAsync(token, message, cancellationToken))
                {
                    case PushOutcome.Sent: sent = true; break;
                    case PushOutcome.InvalidToken: await store.DropTokenAsync(tenantId, token, cancellationToken); break;
                    default: failed = true; break;
                }
            }
            if (sent) sentCount++;
            if (sent || !failed || pending.Attempts + 1 >= MaxAttempts) await store.MarkPushedAsync(tenantId, pending.NotificationId, cancellationToken);
            else await store.CountAttemptAsync(tenantId, pending.NotificationId, cancellationToken);
        }
        return sentCount;
    }
}
