using System.Security.Cryptography;
using System.Text;
using LifeDrop.Domain.Common;

namespace LifeDrop.Application.Channels;

// Optional message channels (plan step 3.4, owner decision D17): free, secure and OFF until the owner turns them on and gives a bot token. Only Telegram, only for
// staff: the messages of the in-app list (`notifications`) also go to the private Telegram chat of the user who linked it. The link is proved with a one-time code
// the user sends to the company bot, so an address is never typed in by somebody else and a message can never reach a stranger.
public enum ChannelOutcome { Sent, InvalidAddress, Failed }

public sealed record TelegramUpdate(long UpdateId, string ChatId, bool IsPrivate, string? Text);

public interface ITelegramBot
{
    // False when the channels are off or no bot token is set: nothing is sent, nothing is read, no message is consumed.
    bool IsConfigured { get; }
    string? BotUsername { get; }
    Task<ChannelOutcome> SendAsync(string chatId, string text, CancellationToken cancellationToken);
    // The updates after `offset`; null when Telegram could not be reached.
    Task<IReadOnlyList<TelegramUpdate>?> GetUpdatesAsync(long offset, CancellationToken cancellationToken);
}

// Text for a channel message in the company language (i18n/strings.json, keys msg.channel.*).
public interface IChannelTexts
{
    string Get(string key, params (string Name, object Value)[] vars);
}

public sealed record PendingChannelMessage(Guid NotificationId, string Kind, string EntityId, int Attempts, string Address, string? CustomerName, string? CustomerCode);

public interface IChannelStore
{
    Task<bool> HasTelegramAsync(TenantId tenantId, UserId userId, CancellationToken cancellationToken);
    Task<string?> GetTenantCodeAsync(TenantId tenantId, CancellationToken cancellationToken);
    // One live code per user: a new one replaces the old.
    Task SaveLinkCodeAsync(TenantId tenantId, UserId userId, byte[] codeHash, DateTimeOffset expiresAt, CancellationToken cancellationToken);
    // True when the user had a link or a waiting code.
    Task<bool> UnlinkTelegramAsync(TenantId tenantId, UserId userId, CancellationToken cancellationToken);
    // The active company with this code, or null. No tenant context is needed (the poller does not know the company yet).
    Task<TenantId?> FindTenantAsync(string tenantCode, CancellationToken cancellationToken);
    // Uses a live code once: links the chat to the user who asked for it. False when no live code matches.
    Task<bool> CompleteLinkAsync(TenantId tenantId, byte[] codeHash, string chatId, DateTimeOffset now, CancellationToken cancellationToken);
    // Unsent messages from the last `maxAge` of users who have a Telegram link, oldest first.
    Task<IReadOnlyList<PendingChannelMessage>> ListPendingAsync(TenantId tenantId, TimeSpan maxAge, int limit, CancellationToken cancellationToken);
    Task MarkSentAsync(TenantId tenantId, Guid notificationId, CancellationToken cancellationToken);
    Task CountAttemptAsync(TenantId tenantId, Guid notificationId, CancellationToken cancellationToken);
    // A chat Telegram says is gone or blocked (any user).
    Task DropAddressAsync(TenantId tenantId, string chatId, CancellationToken cancellationToken);
    // Messages older than maxAge, and messages of users without a link, are marked without sending: a link only brings messages from after it.
    Task ExpireOldAsync(TenantId tenantId, TimeSpan maxAge, CancellationToken cancellationToken);
}

public sealed record ChannelStatus(bool Enabled, string? BotUsername, bool TelegramLinked);
public sealed record TelegramLink(string Code, string? BotUsername, DateTimeOffset ExpiresAt);

public enum ChannelResult { Success, Forbidden, NotConfigured, NotFound }

public sealed class ChannelLinkService(IChannelStore store, ITelegramBot bot, IChannelTexts texts, TimeProvider clock)
{
    public static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(15);
    public const int RandomLength = 12;
    public const int MaxCodeLength = 64;   // what Telegram allows after /start
    private const string Alphabet = "abcdefghjkmnpqrstuvwxyz23456789";   // no look-alike characters

    public async Task<(ChannelResult Result, ChannelStatus? Status)> StatusAsync(TenantId tenantId, UserId userId, CancellationToken cancellationToken)
    {
        if (tenantId.IsEmpty || userId.IsEmpty) return (ChannelResult.Forbidden, null);
        var linked = await store.HasTelegramAsync(tenantId, userId, cancellationToken);
        return (ChannelResult.Success, new ChannelStatus(bot.IsConfigured, bot.IsConfigured ? bot.BotUsername : null, linked));
    }

    // The code is "<company code>_<random>": the company code tells the poller which company to look in, the random part is the secret.
    public async Task<(ChannelResult Result, TelegramLink? Link)> StartTelegramLinkAsync(TenantId tenantId, UserId userId, CancellationToken cancellationToken)
    {
        if (tenantId.IsEmpty || userId.IsEmpty) return (ChannelResult.Forbidden, null);
        if (!bot.IsConfigured) return (ChannelResult.NotConfigured, null);
        var tenantCode = await store.GetTenantCodeAsync(tenantId, cancellationToken);
        if (string.IsNullOrEmpty(tenantCode)) return (ChannelResult.NotFound, null);
        var code = tenantCode.ToLowerInvariant() + "_" + RandomNumberGenerator.GetString(Alphabet, RandomLength);
        var expiresAt = clock.GetUtcNow() + CodeLifetime;
        await store.SaveLinkCodeAsync(tenantId, userId, Hash(code), expiresAt, cancellationToken);
        return (ChannelResult.Success, new TelegramLink(code, bot.BotUsername, expiresAt));
    }

    public async Task<ChannelResult> UnlinkTelegramAsync(TenantId tenantId, UserId userId, CancellationToken cancellationToken)
    {
        if (tenantId.IsEmpty || userId.IsEmpty) return ChannelResult.Forbidden;
        return await store.UnlinkTelegramAsync(tenantId, userId, cancellationToken) ? ChannelResult.Success : ChannelResult.NotFound;
    }

    // One message the company bot received. Only "/start <code>" in a PRIVATE chat counts (a group must never be linked); everything else is ignored.
    public async Task HandleUpdateAsync(TelegramUpdate update, CancellationToken cancellationToken)
    {
        if (!update.IsPrivate || string.IsNullOrEmpty(update.ChatId)) return;
        var text = update.Text?.Trim();
        if (text is null || !text.StartsWith("/start ", StringComparison.Ordinal)) return;
        var code = text["/start ".Length..].Trim().ToLowerInvariant();
        var linked = false;
        var cut = code.LastIndexOf('_');
        if (cut > 0 && code.Length <= MaxCodeLength && await store.FindTenantAsync(code[..cut], cancellationToken) is { } tenantId)
            linked = await store.CompleteLinkAsync(tenantId, Hash(code), update.ChatId, clock.GetUtcNow(), cancellationToken);
        await bot.SendAsync(update.ChatId, texts.Get(linked ? "msg.channel.linked" : "msg.channel.linkInvalid"), cancellationToken);
    }

    public static byte[] Hash(string code) => SHA256.HashData(Encoding.UTF8.GetBytes(code));
}

// Sends the waiting messages of one company to Telegram. A message is done when the chat took it, when the chat is gone (the link is dropped), or after
// MaxAttempts temporary failures. A kind without a text is skipped. Messages older than MaxAge are not sent any more.
public sealed class ChannelDispatcher(IChannelStore store, ITelegramBot bot, IChannelTexts texts)
{
    public const int MaxAttempts = 5;
    public const int BatchSize = 100;
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);

    // The in-app message kinds and their channel texts.
    public static readonly IReadOnlyDictionary<string, string> TextKeys = new Dictionary<string, string>
    {
        ["customer.profile_changed"] = "msg.channel.customerProfileChanged",
        ["customer.location_changed"] = "msg.channel.customerLocationChanged",
    };

    public async Task<int> DispatchTenantAsync(TenantId tenantId, CancellationToken cancellationToken)
    {
        if (!bot.IsConfigured || tenantId.IsEmpty) return 0;
        await store.ExpireOldAsync(tenantId, MaxAge, cancellationToken);
        var sentCount = 0;
        foreach (var message in await store.ListPendingAsync(tenantId, MaxAge, BatchSize, cancellationToken))
        {
            if (!TextKeys.TryGetValue(message.Kind, out var key))
            {
                await store.MarkSentAsync(tenantId, message.NotificationId, cancellationToken);
                continue;
            }
            var text = texts.Get(key, ("name", message.CustomerName ?? "-"), ("code", message.CustomerCode ?? "-"));
            switch (await bot.SendAsync(message.Address, text, cancellationToken))
            {
                case ChannelOutcome.Sent:
                    sentCount++;
                    await store.MarkSentAsync(tenantId, message.NotificationId, cancellationToken);
                    break;
                case ChannelOutcome.InvalidAddress:
                    await store.DropAddressAsync(tenantId, message.Address, cancellationToken);
                    await store.MarkSentAsync(tenantId, message.NotificationId, cancellationToken);
                    break;
                default:
                    if (message.Attempts + 1 >= MaxAttempts) await store.MarkSentAsync(tenantId, message.NotificationId, cancellationToken);
                    else await store.CountAttemptAsync(tenantId, message.NotificationId, cancellationToken);
                    break;
            }
        }
        return sentCount;
    }
}
