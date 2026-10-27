using LifeDrop.Application.Roles;
using LifeDrop.Domain.Common;

namespace LifeDrop.Application.Marketing;

// Marketing messages (plan step 5.7, owner request 2026-10-26). The server never sends anything: a campaign is a queue (one message per customer that has a phone, text from a template with
// {name}); the user opens each WhatsApp chat with one tap and presses "sent" himself. See docs/MARKETING.md for why there is no automatic sending.
public sealed record TemplateDto(Guid Id, string Name, string Body, bool IsActive);

public sealed record SaveTemplateRequest(string? Name, string? Body, bool? IsActive = null);

// CustomerTypes empty / null = every type; PlaceId = a city, area or street (its customers and everything below it).
public sealed record CreateCampaignRequest(string? Name, Guid TemplateId, IReadOnlyList<string>? CustomerTypes = null, Guid? PlaceId = null);

public sealed record CampaignCreatedDto(Guid Id, int Queued, int SkippedNoPhone);

public sealed record CampaignDto(Guid Id, string Name, DateTimeOffset CreatedAt, string? CreatedByEmail, int Queued, int Sent, int Skipped);

public sealed record MarketingMessageDto(Guid Id, Guid CustomerId, string CustomerName, string Phone, string Message, string Status);

public enum MarketingResult { Success, Forbidden, NotFound, InvalidData, Conflict }

public interface IMarketingStore
{
    Task<IReadOnlyList<TemplateDto>> ListTemplatesAsync(TenantId tenantId, bool includeInactive, CancellationToken cancellationToken);
    Task<Guid> CreateTemplateAsync(TenantId tenantId, UserId actorId, string name, string body, CancellationToken cancellationToken);
    // False: no such template.
    Task<bool> UpdateTemplateAsync(TenantId tenantId, UserId actorId, Guid id, string name, string body, bool isActive, CancellationToken cancellationToken);
    // Copies the text of an active template, takes the approved, not archived customers inside the customers' scope that match the filters, and writes one queued message for every
    // customer with a usable phone (the same phone only once, the first customer wins). NotFound: no active template (or the place). Audit marketing.campaign_created.
    Task<(MarketingResult Result, CampaignCreatedDto? Campaign)> CreateCampaignAsync(TenantId tenantId, UserId actorId, AccessScope customerScope, string name, Guid templateId,
        IReadOnlyList<string> customerTypes, Guid? placeId, CancellationToken cancellationToken);
    // Newest first; the creator's own campaigns, or all of them for the tenant-wide scope.
    Task<IReadOnlyList<CampaignDto>> ListCampaignsAsync(TenantId tenantId, AccessScope scope, CancellationToken cancellationToken);
    // Null: not a campaign the actor may see. Status empty = every message; queued messages first.
    Task<IReadOnlyList<MarketingMessageDto>?> ListMessagesAsync(TenantId tenantId, AccessScope scope, Guid campaignId, string? status, CancellationToken cancellationToken);
    // queued -> sent | skipped. NotFound: not a message of a campaign the actor may see. Conflict: already handled.
    Task<MarketingResult> HandleAsync(TenantId tenantId, AccessScope scope, Guid messageId, string newStatus, CancellationToken cancellationToken);
}

public sealed class MarketingService(IMarketingStore store, IPermissionStore permissions)
{
    public const int MaxNameLength = 100;
    public const int MaxBodyLength = 1000;
    public const int MaxTypes = 10;

    public static bool ValidText(string? text, int max) => !string.IsNullOrWhiteSpace(text) && text.Trim().Length <= max;

    public async Task<(MarketingResult Result, IReadOnlyList<TemplateDto> Items)> ListTemplatesAsync(TenantId tenantId, UserId actorId, bool includeInactive, CancellationToken cancellationToken)
    {
        if (tenantId.IsEmpty || actorId.IsEmpty) return (MarketingResult.Forbidden, Array.Empty<TemplateDto>());
        var manage = await permissions.HasPermissionAsync(tenantId, actorId, PermissionKeys.MarketingManage, cancellationToken);
        if (!manage && !await permissions.HasPermissionAsync(tenantId, actorId, PermissionKeys.MarketingUse, cancellationToken)) return (MarketingResult.Forbidden, Array.Empty<TemplateDto>());
        return (MarketingResult.Success, await store.ListTemplatesAsync(tenantId, includeInactive && manage, cancellationToken));
    }

    public async Task<(MarketingResult Result, Guid Id)> CreateTemplateAsync(TenantId tenantId, UserId actorId, SaveTemplateRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (tenantId.IsEmpty || actorId.IsEmpty || !await permissions.HasPermissionAsync(tenantId, actorId, PermissionKeys.MarketingManage, cancellationToken)) return (MarketingResult.Forbidden, Guid.Empty);
        if (!ValidText(request.Name, MaxNameLength) || !ValidText(request.Body, MaxBodyLength)) return (MarketingResult.InvalidData, Guid.Empty);
        return (MarketingResult.Success, await store.CreateTemplateAsync(tenantId, actorId, request.Name!.Trim(), request.Body!.Trim(), cancellationToken));
    }

    public async Task<MarketingResult> UpdateTemplateAsync(TenantId tenantId, UserId actorId, Guid id, SaveTemplateRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (tenantId.IsEmpty || actorId.IsEmpty || !await permissions.HasPermissionAsync(tenantId, actorId, PermissionKeys.MarketingManage, cancellationToken)) return MarketingResult.Forbidden;
        if (id == Guid.Empty || !ValidText(request.Name, MaxNameLength) || !ValidText(request.Body, MaxBodyLength)) return MarketingResult.InvalidData;
        return await store.UpdateTemplateAsync(tenantId, actorId, id, request.Name!.Trim(), request.Body!.Trim(), request.IsActive ?? true, cancellationToken) ? MarketingResult.Success : MarketingResult.NotFound;
    }

    public async Task<(MarketingResult Result, CampaignCreatedDto? Campaign)> CreateCampaignAsync(TenantId tenantId, UserId actorId, CreateCampaignRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (tenantId.IsEmpty || actorId.IsEmpty || !await permissions.HasPermissionAsync(tenantId, actorId, PermissionKeys.MarketingUse, cancellationToken)) return (MarketingResult.Forbidden, null);
        // The customers the actor may see (the data scope and the doctor rule of customers.read).
        var scope = await permissions.GetAccessScopeAsync(tenantId, actorId, PermissionKeys.CustomersRead, cancellationToken);
        if (scope is null) return (MarketingResult.Forbidden, null);
        var types = (request.CustomerTypes ?? []).Select(t => t?.Trim().ToLowerInvariant() ?? "").Distinct().ToList();
        if (!ValidText(request.Name, MaxNameLength) || request.TemplateId == Guid.Empty || request.PlaceId == Guid.Empty || types.Count > MaxTypes || types.Any(t => t.Length is 0 or > 30))
            return (MarketingResult.InvalidData, null);
        return await store.CreateCampaignAsync(tenantId, actorId, scope, request.Name!.Trim(), request.TemplateId, types, request.PlaceId, cancellationToken);
    }

    private async Task<AccessScope?> UseScopeAsync(TenantId tenantId, UserId actorId, CancellationToken cancellationToken) =>
        tenantId.IsEmpty || actorId.IsEmpty ? null : await permissions.GetAccessScopeAsync(tenantId, actorId, PermissionKeys.MarketingUse, cancellationToken);

    public async Task<(MarketingResult Result, IReadOnlyList<CampaignDto> Items)> ListCampaignsAsync(TenantId tenantId, UserId actorId, CancellationToken cancellationToken)
    {
        var scope = await UseScopeAsync(tenantId, actorId, cancellationToken);
        return scope is null ? (MarketingResult.Forbidden, Array.Empty<CampaignDto>()) : (MarketingResult.Success, await store.ListCampaignsAsync(tenantId, scope, cancellationToken));
    }

    public async Task<(MarketingResult Result, IReadOnlyList<MarketingMessageDto> Items)> ListMessagesAsync(TenantId tenantId, UserId actorId, Guid campaignId, string? status,
        CancellationToken cancellationToken)
    {
        var scope = await UseScopeAsync(tenantId, actorId, cancellationToken);
        if (scope is null) return (MarketingResult.Forbidden, Array.Empty<MarketingMessageDto>());
        if (campaignId == Guid.Empty || (!string.IsNullOrEmpty(status) && status is not ("queued" or "sent" or "skipped"))) return (MarketingResult.InvalidData, Array.Empty<MarketingMessageDto>());
        var items = await store.ListMessagesAsync(tenantId, scope, campaignId, status, cancellationToken);
        return items is null ? (MarketingResult.NotFound, Array.Empty<MarketingMessageDto>()) : (MarketingResult.Success, items);
    }

    // newStatus: "sent" or "skipped".
    public async Task<MarketingResult> HandleAsync(TenantId tenantId, UserId actorId, Guid messageId, string newStatus, CancellationToken cancellationToken)
    {
        var scope = await UseScopeAsync(tenantId, actorId, cancellationToken);
        if (scope is null) return MarketingResult.Forbidden;
        if (messageId == Guid.Empty || newStatus is not ("sent" or "skipped")) return MarketingResult.InvalidData;
        return await store.HandleAsync(tenantId, scope, messageId, newStatus, cancellationToken);
    }
}
