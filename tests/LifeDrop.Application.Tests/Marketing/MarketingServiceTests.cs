using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LifeDrop.Application.Marketing;
using LifeDrop.Application.Roles;
using LifeDrop.Domain.Common;
using Xunit;

namespace LifeDrop.Application.Tests.Marketing;

public class MarketingServiceTests
{
    private static readonly TenantId Tenant = new(Guid.NewGuid());
    private static readonly UserId User = new(Guid.NewGuid());

    [Fact]
    public async Task EveryCallNeedsItsPermissionAndTemplatesNeedManage()
    {
        var store = new Store();
        var nobody = new MarketingService(store, new Permissions());
        Assert.Equal(MarketingResult.Forbidden, (await nobody.ListTemplatesAsync(Tenant, User, false, CancellationToken.None)).Result);
        Assert.Equal(MarketingResult.Forbidden, (await nobody.CreateCampaignAsync(Tenant, User, new CreateCampaignRequest("A", Guid.NewGuid()), CancellationToken.None)).Result);
        Assert.Equal(MarketingResult.Forbidden, (await nobody.ListCampaignsAsync(Tenant, User, CancellationToken.None)).Result);
        Assert.Equal(MarketingResult.Forbidden, await nobody.HandleAsync(Tenant, User, Guid.NewGuid(), "sent", CancellationToken.None));
        Assert.Empty(store.Calls);

        var rep = new MarketingService(store, new Permissions(PermissionKeys.MarketingUse, PermissionKeys.CustomersRead));
        Assert.Equal(MarketingResult.Success, (await rep.ListTemplatesAsync(Tenant, User, true, CancellationToken.None)).Result);
        Assert.False(store.IncludeInactive);   // only the manager sees the switched-off templates
        Assert.Equal(MarketingResult.Forbidden, (await rep.CreateTemplateAsync(Tenant, User, new SaveTemplateRequest("A", "B"), CancellationToken.None)).Result);
        Assert.Equal(MarketingResult.Forbidden, await rep.UpdateTemplateAsync(Tenant, User, Guid.NewGuid(), new SaveTemplateRequest("A", "B"), CancellationToken.None));

        var manager = new MarketingService(store, new Permissions(PermissionKeys.MarketingManage));
        Assert.Equal(MarketingResult.Success, (await manager.ListTemplatesAsync(Tenant, User, true, CancellationToken.None)).Result);
        Assert.True(store.IncludeInactive);
        Assert.Equal(MarketingResult.Success, (await manager.CreateTemplateAsync(Tenant, User, new SaveTemplateRequest("  Offer ", " Hello {name} "), CancellationToken.None)).Result);
        Assert.Equal(("Offer", "Hello {name}"), (store.Name, store.Body));
    }

    [Fact]
    public async Task TemplatesRefuseEmptyOrTooLongText()
    {
        var service = new MarketingService(new Store(), new Permissions(PermissionKeys.MarketingManage));
        foreach (var bad in new[] { new SaveTemplateRequest(" ", "x"), new SaveTemplateRequest("x", null), new SaveTemplateRequest(new string('n', 101), "x"), new SaveTemplateRequest("x", new string('b', 1001)) })
        {
            Assert.Equal(MarketingResult.InvalidData, (await service.CreateTemplateAsync(Tenant, User, bad, CancellationToken.None)).Result);
            Assert.Equal(MarketingResult.InvalidData, await service.UpdateTemplateAsync(Tenant, User, Guid.NewGuid(), bad, CancellationToken.None));
        }
        Assert.Equal(MarketingResult.InvalidData, await service.UpdateTemplateAsync(Tenant, User, Guid.Empty, new SaveTemplateRequest("x", "y"), CancellationToken.None));
        Assert.Equal(MarketingResult.Success, (await service.CreateTemplateAsync(Tenant, User, new SaveTemplateRequest(new string('n', 100), new string('b', 1000)), CancellationToken.None)).Result);
    }

    [Fact]
    public async Task ACampaignNeedsTheCustomerScopeAndCleansTheFilters()
    {
        var store = new Store();
        var noCustomers = new MarketingService(store, new Permissions(PermissionKeys.MarketingUse));
        Assert.Equal(MarketingResult.Forbidden, (await noCustomers.CreateCampaignAsync(Tenant, User, new CreateCampaignRequest("A", Guid.NewGuid()), CancellationToken.None)).Result);

        var service = new MarketingService(store, new Permissions(PermissionKeys.MarketingUse, PermissionKeys.CustomersRead));
        Assert.Equal(MarketingResult.Success, (await service.CreateCampaignAsync(Tenant, User, new CreateCampaignRequest(" Spring ", Guid.NewGuid(), [" Pharmacy ", "pharmacy", "Doctor"]), CancellationToken.None)).Result);
        Assert.Equal("Spring", store.Name);
        Assert.Equal(new[] { "pharmacy", "doctor" }, store.Types.ToArray());
        var calls = store.Calls.Count;
        foreach (var bad in new[]
                 {
                     new CreateCampaignRequest(" ", Guid.NewGuid()), new CreateCampaignRequest("A", Guid.Empty), new CreateCampaignRequest("A", Guid.NewGuid(), PlaceId: Guid.Empty),
                     new CreateCampaignRequest("A", Guid.NewGuid(), [""]), new CreateCampaignRequest("A", Guid.NewGuid(), Enumerable.Range(0, 11).Select(i => "t" + i).ToList())
                 })
            Assert.Equal(MarketingResult.InvalidData, (await service.CreateCampaignAsync(Tenant, User, bad, CancellationToken.None)).Result);
        Assert.Equal(calls, store.Calls.Count);
    }

    [Fact]
    public async Task OnlySentAndSkippedAreWorkStepsAndTheStatusFilterIsChecked()
    {
        var store = new Store();
        var service = new MarketingService(store, new Permissions(PermissionKeys.MarketingUse));
        Assert.Equal(MarketingResult.Success, await service.HandleAsync(Tenant, User, Guid.NewGuid(), "sent", CancellationToken.None));
        Assert.Equal(MarketingResult.Success, await service.HandleAsync(Tenant, User, Guid.NewGuid(), "skipped", CancellationToken.None));
        Assert.Equal(MarketingResult.InvalidData, await service.HandleAsync(Tenant, User, Guid.NewGuid(), "queued", CancellationToken.None));
        Assert.Equal(MarketingResult.InvalidData, await service.HandleAsync(Tenant, User, Guid.Empty, "sent", CancellationToken.None));
        Assert.Equal(MarketingResult.Success, (await service.ListMessagesAsync(Tenant, User, Guid.NewGuid(), null, CancellationToken.None)).Result);
        Assert.Equal(MarketingResult.Success, (await service.ListMessagesAsync(Tenant, User, Guid.NewGuid(), "queued", CancellationToken.None)).Result);
        Assert.Equal(MarketingResult.InvalidData, (await service.ListMessagesAsync(Tenant, User, Guid.NewGuid(), "done", CancellationToken.None)).Result);
        Assert.Equal(MarketingResult.InvalidData, (await service.ListMessagesAsync(Tenant, User, Guid.Empty, null, CancellationToken.None)).Result);
        store.MissingCampaign = true;
        Assert.Equal(MarketingResult.NotFound, (await service.ListMessagesAsync(Tenant, User, Guid.NewGuid(), null, CancellationToken.None)).Result);
    }

    private sealed class Cloud(bool configured) : IWhatsAppCloud
    {
        public bool IsConfigured => configured;
        public Task<WhatsAppSendResult> SendTemplateAsync(string toDigits, string template, string language, string bodyParameter, CancellationToken cancellationToken) =>
            Task.FromResult(new WhatsAppSendResult(WhatsAppSendOutcome.Sent));
    }

    [Fact]
    public async Task AnApiCampaignNeedsTheApiToBeTurnedOnAndTheDeliveryIsChecked()
    {
        var store = new Store();
        var permissions = new Permissions(PermissionKeys.MarketingUse, PermissionKeys.CustomersRead);
        var off = new MarketingService(store, permissions, new Cloud(false));
        Assert.Equal(MarketingResult.Unavailable, (await off.CreateCampaignAsync(Tenant, User, new CreateCampaignRequest("A", Guid.NewGuid(), Delivery: "api"), CancellationToken.None)).Result);
        Assert.False(off.WhatsAppApiAvailable);
        var none = new MarketingService(store, permissions);   // no cloud registered at all
        Assert.Equal(MarketingResult.Unavailable, (await none.CreateCampaignAsync(Tenant, User, new CreateCampaignRequest("A", Guid.NewGuid(), Delivery: "api"), CancellationToken.None)).Result);
        Assert.DoesNotContain("campaign", store.Calls);

        var on = new MarketingService(store, permissions, new Cloud(true));
        Assert.Equal(MarketingResult.Success, (await on.CreateCampaignAsync(Tenant, User, new CreateCampaignRequest("A", Guid.NewGuid(), Delivery: " API "), CancellationToken.None)).Result);
        Assert.Equal("api", store.Delivery);
        Assert.Equal(MarketingResult.Success, (await on.CreateCampaignAsync(Tenant, User, new CreateCampaignRequest("A", Guid.NewGuid()), CancellationToken.None)).Result);
        Assert.Equal("manual", store.Delivery);
        Assert.Equal(MarketingResult.InvalidData, (await on.CreateCampaignAsync(Tenant, User, new CreateCampaignRequest("A", Guid.NewGuid(), Delivery: "sms"), CancellationToken.None)).Result);
        Assert.True((await on.StatusAsync(Tenant, User, CancellationToken.None)).Status!.WhatsAppApi);
        Assert.False((await off.StatusAsync(Tenant, User, CancellationToken.None)).Status!.WhatsAppApi);
        Assert.Equal(MarketingResult.Forbidden, (await new MarketingService(store, new Permissions(), new Cloud(true)).StatusAsync(Tenant, User, CancellationToken.None)).Result);
    }

    [Fact]
    public async Task TheWhatsAppTemplateIsBothOrNoneAndLooksLikeMetasNames()
    {
        var store = new Store();
        var service = new MarketingService(store, new Permissions(PermissionKeys.MarketingManage));
        Assert.Equal(MarketingResult.Success, (await service.CreateTemplateAsync(Tenant, User, new SaveTemplateRequest("A", "B", WhatsAppName: " spring_offer ", WhatsAppLanguage: "ckb"), CancellationToken.None)).Result);
        Assert.Equal(("spring_offer", "ckb"), (store.WaName, store.WaLanguage));
        Assert.Equal(MarketingResult.Success, (await service.CreateTemplateAsync(Tenant, User, new SaveTemplateRequest("A", "B", WhatsAppName: "offer", WhatsAppLanguage: "en_US"), CancellationToken.None)).Result);
        Assert.Equal(MarketingResult.Success, (await service.CreateTemplateAsync(Tenant, User, new SaveTemplateRequest("A", "B"), CancellationToken.None)).Result);
        Assert.Null(store.WaName);
        foreach (var bad in new (string?, string?)[] { ("offer", null), (null, "en"), ("Spring Offer", "en"), ("offer", "english"), ("offer", "EN"), (new string('a', 101), "en") })
        {
            var request = new SaveTemplateRequest("A", "B", WhatsAppName: bad.Item1, WhatsAppLanguage: bad.Item2);
            Assert.Equal(MarketingResult.InvalidData, (await service.CreateTemplateAsync(Tenant, User, request, CancellationToken.None)).Result);
            Assert.Equal(MarketingResult.InvalidData, await service.UpdateTemplateAsync(Tenant, User, Guid.NewGuid(), request, CancellationToken.None));
        }
    }

    [Fact]
    public async Task ConsentNeedsCustomersWriteAndAKnownCustomer()
    {
        var store = new Store();
        var customer = Guid.NewGuid();
        var nobody = new MarketingService(store, new Permissions(PermissionKeys.MarketingUse));
        Assert.Equal(MarketingResult.Forbidden, await nobody.SetConsentAsync(Tenant, User, customer, new SetWhatsAppConsentRequest(true), CancellationToken.None));
        Assert.Equal(MarketingResult.Forbidden, (await nobody.GetConsentAsync(Tenant, User, customer, CancellationToken.None)).Result);
        var service = new MarketingService(store, new Permissions(PermissionKeys.CustomersWrite));
        Assert.Equal(MarketingResult.Success, await service.SetConsentAsync(Tenant, User, customer, new SetWhatsAppConsentRequest(true), CancellationToken.None));
        Assert.True(store.ConsentSet);
        Assert.Equal(MarketingResult.InvalidData, await service.SetConsentAsync(Tenant, User, Guid.Empty, new SetWhatsAppConsentRequest(true), CancellationToken.None));
        Assert.Equal(MarketingResult.Success, (await service.GetConsentAsync(Tenant, User, customer, CancellationToken.None)).Result);
        store.MissingCampaign = true;   // the fake answers "not found" for everything
        Assert.Equal(MarketingResult.NotFound, await service.SetConsentAsync(Tenant, User, customer, new SetWhatsAppConsentRequest(false), CancellationToken.None));
        Assert.Equal(MarketingResult.NotFound, (await service.GetConsentAsync(Tenant, User, customer, CancellationToken.None)).Result);
    }

    private sealed class Store : IMarketingStore
    {
        public List<string> Calls { get; } = [];
        public bool IncludeInactive, MissingCampaign;
        public string? Name, Body, WaName, WaLanguage, Delivery;
        public IReadOnlyList<string> Types = [];

        public Task<IReadOnlyList<TemplateDto>> ListTemplatesAsync(TenantId tenantId, bool includeInactive, CancellationToken cancellationToken)
        {
            Calls.Add("templates");
            IncludeInactive = includeInactive;
            return Task.FromResult<IReadOnlyList<TemplateDto>>([]);
        }

        public Task<Guid> CreateTemplateAsync(TenantId tenantId, UserId actorId, string name, string body, string? whatsAppName, string? whatsAppLanguage, CancellationToken cancellationToken)
        {
            Calls.Add("create-template");
            (Name, Body, WaName, WaLanguage) = (name, body, whatsAppName, whatsAppLanguage);
            return Task.FromResult(Guid.NewGuid());
        }

        public Task<bool> UpdateTemplateAsync(TenantId tenantId, UserId actorId, Guid id, string name, string body, bool isActive, string? whatsAppName, string? whatsAppLanguage, CancellationToken cancellationToken)
        {
            Calls.Add("update-template");
            return Task.FromResult(true);
        }

        public Task<(MarketingResult Result, CampaignCreatedDto? Campaign)> CreateCampaignAsync(TenantId tenantId, UserId actorId, AccessScope customerScope, string name, Guid templateId,
            IReadOnlyList<string> customerTypes, Guid? placeId, string delivery, CancellationToken cancellationToken)
        {
            Calls.Add("campaign");
            (Name, Types, Delivery) = (name, customerTypes, delivery);
            return Task.FromResult<(MarketingResult, CampaignCreatedDto?)>((MarketingResult.Success, new CampaignCreatedDto(Guid.NewGuid(), 0, 0)));
        }

        public Task<IReadOnlyList<CampaignDto>> ListCampaignsAsync(TenantId tenantId, AccessScope scope, CancellationToken cancellationToken)
        {
            Calls.Add("campaigns");
            return Task.FromResult<IReadOnlyList<CampaignDto>>([]);
        }

        public Task<IReadOnlyList<MarketingMessageDto>?> ListMessagesAsync(TenantId tenantId, AccessScope scope, Guid campaignId, string? status, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MarketingMessageDto>?>(MissingCampaign ? null : []);

        public Task<MarketingResult> HandleAsync(TenantId tenantId, AccessScope scope, Guid messageId, string newStatus, CancellationToken cancellationToken) =>
            Task.FromResult(MarketingResult.Success);

        public bool? ConsentSet;
        public Task<WhatsAppConsentDto?> GetConsentAsync(TenantId tenantId, AccessScope scope, Guid customerId, CancellationToken cancellationToken) =>
            Task.FromResult<WhatsAppConsentDto?>(MissingCampaign ? null : new WhatsAppConsentDto(true, null));

        public Task<bool> SetConsentAsync(TenantId tenantId, UserId actorId, AccessScope scope, Guid customerId, bool optIn, CancellationToken cancellationToken)
        {
            ConsentSet = optIn;
            return Task.FromResult(!MissingCampaign);
        }
    }

    private sealed class Permissions(params string[] allowed) : IPermissionStore
    {
        public Task<bool> HasPermissionAsync(TenantId tenantId, UserId userId, string permissionKey, CancellationToken cancellationToken) =>
            Task.FromResult(allowed.Contains(permissionKey));
    }
}
