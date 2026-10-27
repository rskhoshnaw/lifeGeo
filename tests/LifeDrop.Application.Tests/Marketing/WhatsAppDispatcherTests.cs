using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LifeDrop.Application.Marketing;
using LifeDrop.Domain.Common;
using Xunit;

namespace LifeDrop.Application.Tests.Marketing;

// The WhatsApp Business API sender (docs/WHATSAPP-API.md): what is sent, what is retried and when it gives up.
public class WhatsAppDispatcherTests
{
    private static readonly TenantId Tenant = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 9, 0, 0, TimeSpan.Zero);

    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Theory]
    [InlineData("0750 111 2233", "9647501112233")]
    [InlineData("+964 750 111 2233", "9647501112233")]
    [InlineData("00964750 111 2233", "9647501112233")]
    [InlineData("750 111 2233", "9647501112233")]
    [InlineData("+44 20 7946 0958", "442079460958")]
    [InlineData("12", null)]
    [InlineData("  ", null)]
    [InlineData(null, null)]
    public void PhonesBecomeInternationalDigits(string? phone, string? expected) => Assert.Equal(expected, WhatsAppPhone.Digits(phone));

    [Fact]
    public async Task NothingIsReadOrSentWhileTheApiIsNotConfigured()
    {
        var store = new Store();
        var cloud = new Cloud { Configured = false };
        Assert.Equal(0, await new WhatsAppDispatcher(store, cloud, new Clock()).DispatchTenantAsync(Tenant, CancellationToken.None));
        Assert.Equal(0, store.Claims);
        Assert.Empty(cloud.Sent);
    }

    [Fact]
    public async Task ASentMessageIsMarkedAndGetsTheCustomerNameAsTheFirstVariable()
    {
        var id = Guid.NewGuid();
        var store = new Store { Due = { new WhatsAppDue(id, "0750 111 2233", "Shifa", "spring_offer", "ckb", 1) } };
        var cloud = new Cloud { Next = new WhatsAppSendResult(WhatsAppSendOutcome.Sent, "wamid.1") };
        Assert.Equal(1, await new WhatsAppDispatcher(store, cloud, new Clock()).DispatchTenantAsync(Tenant, CancellationToken.None));
        Assert.Equal(("9647501112233", "spring_offer", "ckb", "Shifa"), cloud.Sent[0]);
        Assert.Equal(new[] { $"sent:{id}:wamid.1" }, store.Marks.ToArray());
    }

    [Fact]
    public async Task ABadPhoneFailsAtOnceWithoutCallingMeta()
    {
        var id = Guid.NewGuid();
        var store = new Store { Due = { new WhatsAppDue(id, "12", "Shifa", "t", "en", 1) } };
        var cloud = new Cloud();
        await new WhatsAppDispatcher(store, cloud, new Clock()).DispatchTenantAsync(Tenant, CancellationToken.None);
        Assert.Empty(cloud.Sent);
        Assert.Equal(new[] { $"failed:{id}:invalid_phone" }, store.Marks.ToArray());
    }

    [Fact]
    public async Task ATemporaryFailureIsTriedAgainLaterButOnlyThreeTimes()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var third = Guid.NewGuid();
        var store = new Store
        {
            Due =
            {
                new WhatsAppDue(first, "0750 111 2233", "A", "t", "en", 1),
                new WhatsAppDue(second, "0750 111 2233", "B", "t", "en", 2),
                new WhatsAppDue(third, "0750 111 2233", "C", "t", "en", WhatsAppDispatcher.MaxAttempts),
            }
        };
        var cloud = new Cloud { Next = new WhatsAppSendResult(WhatsAppSendOutcome.Retry, Error: "429: rate limit") };
        await new WhatsAppDispatcher(store, cloud, new Clock()).DispatchTenantAsync(Tenant, CancellationToken.None);
        Assert.Equal(new[]
        {
            $"retry:{first}:{Now.AddMinutes(1):O}:429: rate limit",
            $"retry:{second}:{Now.AddMinutes(10):O}:429: rate limit",
            $"failed:{third}:429: rate limit",
        }, store.Marks.ToArray());
    }

    [Fact]
    public async Task AnErrorFromMetaIsFinalAndAThrownErrorCountsAsTemporary()
    {
        var refused = Guid.NewGuid();
        var thrown = Guid.NewGuid();
        var store = new Store { Due = { new WhatsAppDue(refused, "0750 111 2233", "A", "t", "en", 1) } };
        await new WhatsAppDispatcher(store, new Cloud { Next = new WhatsAppSendResult(WhatsAppSendOutcome.Failed, Error: "400: template not approved") }, new Clock()).DispatchTenantAsync(Tenant, CancellationToken.None);
        Assert.Equal(new[] { $"failed:{refused}:400: template not approved" }, store.Marks.ToArray());

        store = new Store { Due = { new WhatsAppDue(thrown, "0750 111 2233", "A", "t", "en", 1) } };
        await new WhatsAppDispatcher(store, new Cloud { Throw = true }, new Clock()).DispatchTenantAsync(Tenant, CancellationToken.None);
        Assert.Equal(new[] { $"retry:{thrown}:{Now.AddMinutes(1):O}:exception" }, store.Marks.ToArray());
    }

    private sealed class Cloud : IWhatsAppCloud
    {
        public bool Configured { get; init; } = true;
        public bool IsConfigured => Configured;
        public WhatsAppSendResult Next { get; init; } = new(WhatsAppSendOutcome.Sent, "id");
        public bool Throw { get; init; }
        public List<(string To, string Template, string Language, string Parameter)> Sent { get; } = [];

        public Task<WhatsAppSendResult> SendTemplateAsync(string toDigits, string template, string language, string bodyParameter, CancellationToken cancellationToken)
        {
            if (Throw) throw new InvalidOperationException("boom");
            Sent.Add((toDigits, template, language, bodyParameter));
            return Task.FromResult(Next);
        }
    }

    private sealed class Store : IWhatsAppStore
    {
        public List<WhatsAppDue> Due { get; } = [];
        public List<string> Marks { get; } = [];
        public int Claims;

        public Task<IReadOnlyList<WhatsAppDue>> ClaimDueAsync(TenantId tenantId, int limit, CancellationToken cancellationToken)
        {
            Claims++;
            return Task.FromResult<IReadOnlyList<WhatsAppDue>>(Due.ToArray());
        }

        public Task MarkSentAsync(TenantId tenantId, Guid messageId, string? providerMessageId, CancellationToken cancellationToken)
        {
            Marks.Add($"sent:{messageId}:{providerMessageId}");
            return Task.CompletedTask;
        }

        public Task MarkRetryAsync(TenantId tenantId, Guid messageId, DateTimeOffset nextAttempt, string error, CancellationToken cancellationToken)
        {
            Marks.Add($"retry:{messageId}:{nextAttempt:O}:{error}");
            return Task.CompletedTask;
        }

        public Task MarkFailedAsync(TenantId tenantId, Guid messageId, string error, CancellationToken cancellationToken)
        {
            Marks.Add($"failed:{messageId}:{error}");
            return Task.CompletedTask;
        }
    }
}
