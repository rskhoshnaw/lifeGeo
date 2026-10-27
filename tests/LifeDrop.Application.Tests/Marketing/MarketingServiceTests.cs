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

    private sealed class Store : IMarketingStore
    {
        public List<string> Calls { get; } = [];
        public bool IncludeInactive, MissingCampaign;
        public string? Name, Body;
        public IReadOnlyList<string> Types = [];

        public Task<IReadOnlyList<TemplateDto>> ListTemplatesAsync(TenantId tenantId, bool includeInactive, CancellationToken cancellationToken)
        {
            Calls.Add("templates");
            IncludeInactive = includeInactive;
            return Task.FromResult<IReadOnlyList<TemplateDto>>([]);
        }

        public Task<Guid> CreateTemplateAsync(TenantId tenantId, UserId actorId, string name, string body, CancellationToken cancellationToken)
        {
            Calls.Add("create-template");
            (Name, Body) = (name, body);
            return Task.FromResult(Guid.NewGuid());
        }

        public Task<bool> UpdateTemplateAsync(TenantId tenantId, UserId actorId, Guid id, string name, string body, bool isActive, CancellationToken cancellationToken)
        {
            Calls.Add("update-template");
            return Task.FromResult(true);
        }

        public Task<(MarketingResult Result, CampaignCreatedDto? Campaign)> CreateCampaignAsync(TenantId tenantId, UserId actorId, AccessScope customerScope, string name, Guid templateId,
            IReadOnlyList<string> customerTypes, Guid? placeId, CancellationToken cancellationToken)
        {
            Calls.Add("campaign");
            (Name, Types) = (name, customerTypes);
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
    }

    private sealed class Permissions(params string[] allowed) : IPermissionStore
    {
        public Task<bool> HasPermissionAsync(TenantId tenantId, UserId userId, string permissionKey, CancellationToken cancellationToken) =>
            Task.FromResult(allowed.Contains(permissionKey));
    }
}
