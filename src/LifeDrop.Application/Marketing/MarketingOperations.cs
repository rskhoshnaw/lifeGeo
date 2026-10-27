using LifeDrop.Application.Roles;
using LifeDrop.Domain.Common;

namespace LifeDrop.Application.Marketing;

// Marketing messages (plan step 5.7, owner request 2026-10-26). The server never sends anything: a campaign is a queue (one message per customer that has a phone, text from a template with
// {name}); the user opens each WhatsApp chat with one tap and presses "sent" himself. See docs/MARKETING.md for why there is no automatic sending.
// WhatsAppName / WhatsAppLanguage: the template Meta approved for the WhatsApp Business API (its {{1}} gets the customer's name); both or none (docs/WHATSAPP-API.md).
public sealed record TemplateDto(Guid Id, string Name, string Body, bool IsActive, string? WhatsAppName = null, string? WhatsAppLanguage = null);

public sealed record SaveTemplateRequest(string? Name, string? Body, bool? IsActive = null, string? WhatsAppName = null, string? WhatsAppLanguage = null);

// CustomerTypes empty / null = every type; PlaceId = a city, area or street (its customers and everything below it).
// Delivery: "manual" (default: the user opens each chat) or "api" (the server sends the approved WhatsApp template, only to customers who agreed).
public sealed record CreateCampaignRequest(string? Name, Guid TemplateId, IReadOnlyList<string>? CustomerTypes = null, Guid? PlaceId = null, string? Delivery = null);

public sealed record CampaignCreatedDto(Guid Id, int Queued, int SkippedNoPhone, int SkippedNoConsent = 0);

public sealed record CampaignDto(Guid Id, string Name, DateTimeOffset CreatedAt, string? CreatedByEmail, int Queued, int Sent, int Skipped, string Delivery = "manual", int Failed = 0);

public sealed record MarketingMessageDto(Guid Id, Guid CustomerId, string CustomerName, string Phone, string Message, string Status, string? Error = null);

public sealed record MarketingStatusDto(bool WhatsAppApi);

public sealed record WhatsAppConsentDto(bool OptIn, DateTimeOffset? At);

public sealed record SetWhatsAppConsentRequest(bool OptIn);

public enum MarketingResult { Success, Forbidden, NotFound, InvalidData, Conflict, Unavailable }

public interface IMarketingStore
{
    Task<IReadOnlyList<TemplateDto>> ListTemplatesAsync(TenantId tenantId, bool includeInactive, CancellationToken cancellationToken);
    Task<Guid> CreateTemplateAsync(TenantId tenantId, UserId actorId, string name, string body, string? whatsAppName, string? whatsAppLanguage, CancellationToken cancellationToken);
    // False: no such template.
    Task<bool> UpdateTemplateAsync(TenantId tenantId, UserId actorId, Guid id, string name, string body, bool isActive, string? whatsAppName, string? whatsAppLanguage, CancellationToken cancellationToken);
    // Copies the text of an active template, takes the approved, not archived customers inside the customers' scope that match the filters, and writes one queued message for every
    // customer with a usable phone (the same phone only once, the first customer wins). NotFound: no active template (or the place). Audit marketing.campaign_created.
    Task<(MarketingResult Result, CampaignCreatedDto? Campaign)> CreateCampaignAsync(TenantId tenantId, UserId actorId, AccessScope customerScope, string name, Guid templateId,
        IReadOnlyList<string> customerTypes, Guid? placeId, string delivery, CancellationToken cancellationToken);
    // Newest first; the creator's own campaigns, or all of them for the tenant-wide scope.
    Task<IReadOnlyList<CampaignDto>> ListCampaignsAsync(TenantId tenantId, AccessScope scope, CancellationToken cancellationToken);
    // Null: not a campaign the actor may see. Status empty = every message; queued messages first.
    Task<IReadOnlyList<MarketingMessageDto>?> ListMessagesAsync(TenantId tenantId, AccessScope scope, Guid campaignId, string? status, CancellationToken cancellationToken);
    // queued -> sent | skipped. NotFound: not a message of a campaign the actor may see. Conflict: already handled, or the message belongs to an "api" campaign (the server sends those).
    Task<MarketingResult> HandleAsync(TenantId tenantId, AccessScope scope, Guid messageId, string newStatus, CancellationToken cancellationToken);
    // Whether the customer agreed to WhatsApp messages (inside the scope; null = not found), and changing it. Audit customer.whatsapp_consent_changed.
    Task<WhatsAppConsentDto?> GetConsentAsync(TenantId tenantId, AccessScope scope, Guid customerId, CancellationToken cancellationToken);
    Task<bool> SetConsentAsync(TenantId tenantId, UserId actorId, AccessScope scope, Guid customerId, bool optIn, CancellationToken cancellationToken);
}

public sealed class MarketingService(IMarketingStore store, IPermissionStore permissions, IWhatsAppCloud? whatsApp = null)
{
    public const int MaxNameLength = 100;
    public const int MaxBodyLength = 1000;
    public const int MaxTypes = 10;

    public static bool ValidText(string? text, int max) => !string.IsNullOrWhiteSpace(text) && text.Trim().Length <= max;

    private static readonly System.Text.RegularExpressions.Regex TemplateNamePattern = new("^[a-z0-9_]{1,100}$");
    private static readonly System.Text.RegularExpressions.Regex LanguagePattern = new("^[a-z]{2,3}(_[A-Z]{2})?$");

    // Both empty (no WhatsApp template) or both valid (the name and language Meta shows for the approved template).
    private static bool TryReadWhatsApp(SaveTemplateRequest request, out string? name, out string? language)
    {
        name = string.IsNullOrWhiteSpace(request.WhatsAppName) ? null : request.WhatsAppName.Trim();
        language = string.IsNullOrWhiteSpace(request.WhatsAppLanguage) ? null : request.WhatsAppLanguage.Trim();
        return (name is null && language is null) || (name is not null && language is not null && TemplateNamePattern.IsMatch(name) && LanguagePattern.IsMatch(language));
    }

    public bool WhatsAppApiAvailable => whatsApp?.IsConfigured == true;

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
        if (!ValidText(request.Name, MaxNameLength) || !ValidText(request.Body, MaxBodyLength) || !TryReadWhatsApp(request, out var waName, out var waLanguage)) return (MarketingResult.InvalidData, Guid.Empty);
        return (MarketingResult.Success, await store.CreateTemplateAsync(tenantId, actorId, request.Name!.Trim(), request.Body!.Trim(), waName, waLanguage, cancellationToken));
    }

    public async Task<MarketingResult> UpdateTemplateAsync(TenantId tenantId, UserId actorId, Guid id, SaveTemplateRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (tenantId.IsEmpty || actorId.IsEmpty || !await permissions.HasPermissionAsync(tenantId, actorId, PermissionKeys.MarketingManage, cancellationToken)) return MarketingResult.Forbidden;
        if (id == Guid.Empty || !ValidText(request.Name, MaxNameLength) || !ValidText(request.Body, MaxBodyLength) || !TryReadWhatsApp(request, out var waName, out var waLanguage)) return MarketingResult.InvalidData;
        return await store.UpdateTemplateAsync(tenantId, actorId, id, request.Name!.Trim(), request.Body!.Trim(), request.IsActive ?? true, waName, waLanguage, cancellationToken) ? MarketingResult.Success : MarketingResult.NotFound;
    }

    public async Task<(MarketingResult Result, CampaignCreatedDto? Campaign)> CreateCampaignAsync(TenantId tenantId, UserId actorId, CreateCampaignRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (tenantId.IsEmpty || actorId.IsEmpty || !await permissions.HasPermissionAsync(tenantId, actorId, PermissionKeys.MarketingUse, cancellationToken)) return (MarketingResult.Forbidden, null);
        // The customers the actor may see (the data scope and the doctor rule of customers.read).
        var scope = await permissions.GetAccessScopeAsync(tenantId, actorId, PermissionKeys.CustomersRead, cancellationToken);
        if (scope is null) return (MarketingResult.Forbidden, null);
        var types = (request.CustomerTypes ?? []).Select(t => t?.Trim().ToLowerInvariant() ?? "").Distinct().ToList();
        var delivery = string.IsNullOrWhiteSpace(request.Delivery) ? "manual" : request.Delivery.Trim().ToLowerInvariant();
        if (!ValidText(request.Name, MaxNameLength) || request.TemplateId == Guid.Empty || request.PlaceId == Guid.Empty || types.Count > MaxTypes || types.Any(t => t.Length is 0 or > 30) || delivery is not ("manual" or "api"))
            return (MarketingResult.InvalidData, null);
        if (delivery == "api" && !WhatsAppApiAvailable) return (MarketingResult.Unavailable, null);   // not turned on for this company
        return await store.CreateCampaignAsync(tenantId, actorId, scope, request.Name!.Trim(), request.TemplateId, types, request.PlaceId, delivery, cancellationToken);
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

    // Whether the WhatsApp Business API is turned on (the campaign form offers "send by the API" only then). Needs marketing.use.
    public async Task<(MarketingResult Result, MarketingStatusDto? Status)> StatusAsync(TenantId tenantId, UserId actorId, CancellationToken cancellationToken) =>
        await UseScopeAsync(tenantId, actorId, cancellationToken) is null ? (MarketingResult.Forbidden, null) : (MarketingResult.Success, new MarketingStatusDto(WhatsAppApiAvailable));

    private async Task<AccessScope?> CustomerWriteScopeAsync(TenantId tenantId, UserId actorId, CancellationToken cancellationToken) =>
        tenantId.IsEmpty || actorId.IsEmpty ? null : await permissions.GetAccessScopeAsync(tenantId, actorId, PermissionKeys.CustomersWrite, cancellationToken);

    public async Task<(MarketingResult Result, WhatsAppConsentDto? Consent)> GetConsentAsync(TenantId tenantId, UserId actorId, Guid customerId, CancellationToken cancellationToken)
    {
        var scope = await CustomerWriteScopeAsync(tenantId, actorId, cancellationToken);
        if (scope is null) return (MarketingResult.Forbidden, null);
        if (customerId == Guid.Empty) return (MarketingResult.InvalidData, null);
        var consent = await store.GetConsentAsync(tenantId, scope, customerId, cancellationToken);
        return consent is null ? (MarketingResult.NotFound, null) : (MarketingResult.Success, consent);
    }

    // Who may say that a customer agreed: whoever may edit the customer (customers.write inside the data scope; a role that cannot see doctors cannot touch them).
    public async Task<MarketingResult> SetConsentAsync(TenantId tenantId, UserId actorId, Guid customerId, SetWhatsAppConsentRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var scope = await CustomerWriteScopeAsync(tenantId, actorId, cancellationToken);
        if (scope is null) return MarketingResult.Forbidden;
        if (customerId == Guid.Empty) return MarketingResult.InvalidData;
        return await store.SetConsentAsync(tenantId, actorId, scope, customerId, request.OptIn, cancellationToken) ? MarketingResult.Success : MarketingResult.NotFound;
    }
}
