using LifeDrop.Application.Channels;
using LifeDrop.Domain.Common;
using Xunit;

namespace LifeDrop.Application.Tests.Channels;

public class ChannelTests
{
    private static readonly TenantId Tenant = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private static readonly UserId User = new(Guid.Parse("22222222-2222-2222-2222-222222222222"));
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FakeTexts : IChannelTexts
    {
        public string Get(string key, params (string Name, object Value)[] vars) => vars.Length == 0 ? key : key + ":" + string.Join(",", vars.Select(v => v.Name + "=" + v.Value));
    }

    private sealed class FakeBot : ITelegramBot
    {
        public bool IsConfigured { get; set; } = true;
        public string? BotUsername => "lifedrop_bot";
        public ChannelOutcome Outcome { get; set; } = ChannelOutcome.Sent;
        public List<(string Chat, string Text)> Sent { get; } = [];
        public Task<ChannelOutcome> SendAsync(string chatId, string text, CancellationToken cancellationToken)
        {
            Sent.Add((chatId, text));
            return Task.FromResult(Outcome);
        }
        public Task<IReadOnlyList<TelegramUpdate>?> GetUpdatesAsync(long offset, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<TelegramUpdate>?>(null);
    }

    private sealed class FakeStore : IChannelStore
    {
        public string? TenantCode { get; set; } = "acme";
        public string? KnownTenantCode { get; set; } = "acme";
        public bool LiveCode { get; set; } = true;
        public List<PendingChannelMessage> Pending { get; } = [];
        public (byte[] Hash, DateTimeOffset Expires)? Saved { get; private set; }
        public (string Chat, byte[] Hash)? Completed { get; private set; }
        public List<Guid> MarkedSent { get; } = [];
        public List<Guid> Counted { get; } = [];
        public List<string> Dropped { get; } = [];
        public int Expired { get; private set; }
        public bool Linked { get; set; }

        public Task<bool> HasTelegramAsync(TenantId tenantId, UserId userId, CancellationToken cancellationToken) => Task.FromResult(Linked);
        public Task<string?> GetTenantCodeAsync(TenantId tenantId, CancellationToken cancellationToken) => Task.FromResult(TenantCode);
        public Task SaveLinkCodeAsync(TenantId tenantId, UserId userId, byte[] codeHash, DateTimeOffset expiresAt, CancellationToken cancellationToken)
        {
            Saved = (codeHash, expiresAt);
            return Task.CompletedTask;
        }
        public Task<bool> UnlinkTelegramAsync(TenantId tenantId, UserId userId, CancellationToken cancellationToken) => Task.FromResult(Linked);
        public Task<TenantId?> FindTenantAsync(string tenantCode, CancellationToken cancellationToken) =>
            Task.FromResult<TenantId?>(tenantCode == KnownTenantCode ? Tenant : null);
        public Task<bool> CompleteLinkAsync(TenantId tenantId, byte[] codeHash, string chatId, DateTimeOffset now, CancellationToken cancellationToken)
        {
            if (!LiveCode) return Task.FromResult(false);
            Completed = (chatId, codeHash);
            return Task.FromResult(true);
        }
        public Task<IReadOnlyList<PendingChannelMessage>> ListPendingAsync(TenantId tenantId, TimeSpan maxAge, int limit, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PendingChannelMessage>>(Pending.ToList());
        public Task MarkSentAsync(TenantId tenantId, Guid notificationId, CancellationToken cancellationToken)
        {
            MarkedSent.Add(notificationId);
            return Task.CompletedTask;
        }
        public Task CountAttemptAsync(TenantId tenantId, Guid notificationId, CancellationToken cancellationToken)
        {
            Counted.Add(notificationId);
            return Task.CompletedTask;
        }
        public Task DropAddressAsync(TenantId tenantId, string chatId, CancellationToken cancellationToken)
        {
            Dropped.Add(chatId);
            return Task.CompletedTask;
        }
        public Task ExpireOldAsync(TenantId tenantId, TimeSpan maxAge, CancellationToken cancellationToken)
        {
            Expired++;
            return Task.CompletedTask;
        }
    }

    private static ChannelLinkService Links(FakeStore store, FakeBot bot) => new(store, bot, new FakeTexts(), new FixedTime(Now));
    private static ChannelDispatcher Dispatcher(FakeStore store, FakeBot bot) => new(store, bot, new FakeTexts());
    private static PendingChannelMessage Message(Guid id, string kind = "customer.profile_changed", int attempts = 0, string address = "777") =>
        new(id, kind, "c1", attempts, address, "Shifa", "P-1");

    [Fact]
    public async Task A_link_code_names_the_company_is_random_and_lives_15_minutes()
    {
        var store = new FakeStore();
        var (result, link) = await Links(store, new FakeBot()).StartTelegramLinkAsync(Tenant, User, CancellationToken.None);

        Assert.Equal(ChannelResult.Success, result);
        Assert.StartsWith("acme_", link!.Code);
        Assert.Equal("acme_".Length + ChannelLinkService.RandomLength, link.Code.Length);
        Assert.True(link.Code.Length <= ChannelLinkService.MaxCodeLength);
        Assert.Equal(Now + TimeSpan.FromMinutes(15), link.ExpiresAt);
        Assert.Equal("lifedrop_bot", link.BotUsername);
        Assert.Equal(ChannelLinkService.Hash(link.Code), store.Saved!.Value.Hash);   // only the hash is kept
        Assert.Equal(link.ExpiresAt, store.Saved.Value.Expires);

        var (_, other) = await Links(store, new FakeBot()).StartTelegramLinkAsync(Tenant, User, CancellationToken.None);
        Assert.NotEqual(link.Code, other!.Code);
    }

    [Fact]
    public async Task Nothing_is_made_or_stored_while_the_channels_are_off_or_for_nobody()
    {
        var store = new FakeStore();
        Assert.Equal(ChannelResult.NotConfigured, (await Links(store, new FakeBot { IsConfigured = false }).StartTelegramLinkAsync(Tenant, User, CancellationToken.None)).Result);
        Assert.Equal(ChannelResult.Forbidden, (await Links(store, new FakeBot()).StartTelegramLinkAsync(TenantId.Empty, User, CancellationToken.None)).Result);
        Assert.Equal(ChannelResult.Forbidden, (await Links(store, new FakeBot()).StartTelegramLinkAsync(Tenant, UserId.Empty, CancellationToken.None)).Result);
        Assert.Null(store.Saved);

        var status = (await Links(store, new FakeBot { IsConfigured = false }).StatusAsync(Tenant, User, CancellationToken.None)).Status!;
        Assert.Equal((false, null), (status.Enabled, status.BotUsername));   // the bot name is not shown while it is off
    }

    [Fact]
    public async Task Only_start_with_a_code_in_a_private_chat_links_and_the_chat_is_told_the_result()
    {
        var store = new FakeStore();
        var bot = new FakeBot();
        var links = Links(store, bot);

        await links.HandleUpdateAsync(new TelegramUpdate(1, "-100", false, "/start acme_abcdefghjkmn"), CancellationToken.None);   // a group must never be linked
        await links.HandleUpdateAsync(new TelegramUpdate(2, "777", true, "hello"), CancellationToken.None);
        await links.HandleUpdateAsync(new TelegramUpdate(3, "777", true, "/start"), CancellationToken.None);
        await links.HandleUpdateAsync(new TelegramUpdate(4, "", true, "/start acme_abcdefghjkmn"), CancellationToken.None);
        await links.HandleUpdateAsync(new TelegramUpdate(5, "777", true, null), CancellationToken.None);
        Assert.Empty(bot.Sent);
        Assert.Null(store.Completed);

        await links.HandleUpdateAsync(new TelegramUpdate(6, "777", true, "  /start   ACME_ABCDEFGHJKMN  "), CancellationToken.None);   // case does not matter
        Assert.Equal("777", store.Completed!.Value.Chat);
        Assert.Equal(ChannelLinkService.Hash("acme_abcdefghjkmn"), store.Completed.Value.Hash);
        Assert.Equal(new[] { ("777", "msg.channel.linked") }, bot.Sent);
    }

    [Theory]
    [InlineData("/start nounderscore")]
    [InlineData("/start unknown_abcdefghjkmn")]                                  // no such company
    [InlineData("/start _abcdefghjkmn")]                                         // empty company
    [InlineData("/start acme_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]   // longer than Telegram allows
    public async Task A_wrong_code_gets_the_invalid_answer_and_links_nothing(string text)
    {
        var store = new FakeStore();
        var bot = new FakeBot();
        await Links(store, bot).HandleUpdateAsync(new TelegramUpdate(1, "777", true, text), CancellationToken.None);
        Assert.Null(store.Completed);
        Assert.Equal(new[] { ("777", "msg.channel.linkInvalid") }, bot.Sent);
    }

    [Fact]
    public async Task A_used_or_expired_code_gets_the_invalid_answer()
    {
        var store = new FakeStore { LiveCode = false };
        var bot = new FakeBot();
        await Links(store, bot).HandleUpdateAsync(new TelegramUpdate(1, "777", true, "/start acme_abcdefghjkmn"), CancellationToken.None);
        Assert.Null(store.Completed);
        Assert.Equal(new[] { ("777", "msg.channel.linkInvalid") }, bot.Sent);
    }

    [Fact]
    public async Task A_managers_message_is_sent_as_it_was_written()
    {
        var store = new FakeStore();
        var id = Guid.NewGuid();
        store.Pending.Add(new PendingChannelMessage(id, "manager.message", "Meeting at 9", 0, "777", null, null));
        var bot = new FakeBot();

        Assert.Equal(1, await Dispatcher(store, bot).DispatchTenantAsync(Tenant, CancellationToken.None));
        Assert.Equal(new[] { ("777", "msg.channel.managerMessage:message=Meeting at 9") }, bot.Sent);
        Assert.Equal(new[] { id }, store.MarkedSent);
    }

    [Fact]
    public async Task A_message_is_sent_with_the_customer_and_marked_done()
    {
        var store = new FakeStore();
        var id = Guid.NewGuid();
        store.Pending.Add(Message(id));
        var bot = new FakeBot();

        Assert.Equal(1, await Dispatcher(store, bot).DispatchTenantAsync(Tenant, CancellationToken.None));
        Assert.Equal(new[] { ("777", "msg.channel.customerProfileChanged:name=Shifa,code=P-1") }, bot.Sent);
        Assert.Equal(new[] { id }, store.MarkedSent);
        Assert.Equal(1, store.Expired);   // old messages and messages of users without a link are cleared first
    }

    [Fact]
    public async Task Nothing_is_sent_or_consumed_while_the_channels_are_off()
    {
        var store = new FakeStore();
        store.Pending.Add(Message(Guid.NewGuid()));
        var bot = new FakeBot { IsConfigured = false };
        Assert.Equal(0, await Dispatcher(store, bot).DispatchTenantAsync(Tenant, CancellationToken.None));
        Assert.Empty(bot.Sent);
        Assert.Empty(store.MarkedSent);
        Assert.Equal(0, store.Expired);
        Assert.Equal(0, await Dispatcher(store, new FakeBot()).DispatchTenantAsync(TenantId.Empty, CancellationToken.None));
    }

    [Fact]
    public async Task A_chat_that_is_gone_loses_its_link_and_the_message_is_done()
    {
        var store = new FakeStore();
        var id = Guid.NewGuid();
        store.Pending.Add(Message(id));
        var bot = new FakeBot { Outcome = ChannelOutcome.InvalidAddress };

        Assert.Equal(0, await Dispatcher(store, bot).DispatchTenantAsync(Tenant, CancellationToken.None));
        Assert.Equal(new[] { "777" }, store.Dropped);
        Assert.Equal(new[] { id }, store.MarkedSent);
    }

    [Fact]
    public async Task A_temporary_failure_is_retried_and_given_up_after_five_tries()
    {
        var store = new FakeStore();
        var first = Guid.NewGuid();
        var last = Guid.NewGuid();
        store.Pending.Add(Message(first, attempts: 0));
        store.Pending.Add(Message(last, attempts: ChannelDispatcher.MaxAttempts - 1));
        var bot = new FakeBot { Outcome = ChannelOutcome.Failed };

        Assert.Equal(0, await Dispatcher(store, bot).DispatchTenantAsync(Tenant, CancellationToken.None));
        Assert.Equal(new[] { first }, store.Counted);      // tried again next time
        Assert.Equal(new[] { last }, store.MarkedSent);    // the fifth failure ends it
        Assert.Empty(store.Dropped);               // a failure never drops the link
    }

    [Fact]
    public async Task A_kind_without_a_text_is_skipped_not_sent()
    {
        var store = new FakeStore();
        var id = Guid.NewGuid();
        store.Pending.Add(Message(id, kind: "something.new"));
        var bot = new FakeBot();
        Assert.Equal(0, await Dispatcher(store, bot).DispatchTenantAsync(Tenant, CancellationToken.None));
        Assert.Empty(bot.Sent);
        Assert.Equal(new[] { id }, store.MarkedSent);
    }

    [Fact]
    public void Every_in_app_message_kind_has_a_channel_text()
    {
        Assert.Equal(new[] { "customer.location_changed", "customer.profile_changed" }, ChannelDispatcher.TextKeys.Keys.Order(StringComparer.Ordinal).ToArray());
    }
}
