using System.IdentityModel.Tokens.Jwt;
using LifeDrop.Application.Marketing;
using LifeDrop.Domain.Common;

namespace LifeDrop.API.Endpoints;

// Marketing messages (plan step 5.7): templates, campaigns (a queue of customers) and "sent" / "skipped" for each message. Nothing is sent by the server.
public static class MarketingEndpoints
{
    public static void MapMarketingEndpoints(this WebApplication app)
    {
        app.MapGet("/marketing/templates", async (bool? includeInactive, HttpContext context, MarketingService service, DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var (result, items) = await service.ListTemplatesAsync(tenantId, actorId, includeInactive == true, cancellationToken);
            return result == MarketingResult.Success ? Results.Ok(items) : ToError(result);
        }).RequireAuthorization()
            .WithName("ListMarketingTemplates").WithTags("Marketing").Produces<List<TemplateDto>>(200)
            .ProducesProblem(401).ProducesProblem(403).ProducesProblem(429).ProducesProblem(503);

        app.MapPost("/marketing/templates", async (SaveTemplateRequest request, HttpContext context, MarketingService service, DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var (result, id) = await service.CreateTemplateAsync(tenantId, actorId, request, cancellationToken);
            return result == MarketingResult.Success ? Results.Created($"/marketing/templates/{id}", new CreatedResponse(id)) : ToError(result);
        }).RequireAuthorization()
            .WithName("CreateMarketingTemplate").WithTags("Marketing").Produces<CreatedResponse>(201)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(429).ProducesProblem(503);

        app.MapPut("/marketing/templates/{id:guid}", async (Guid id, SaveTemplateRequest request, HttpContext context, MarketingService service, DatabaseSecurityState securityState,
            CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var result = await service.UpdateTemplateAsync(tenantId, actorId, id, request, cancellationToken);
            return result == MarketingResult.Success ? Results.NoContent() : ToError(result);
        }).RequireAuthorization()
            .WithName("UpdateMarketingTemplate").WithTags("Marketing").Produces(204)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(429).ProducesProblem(503);

        app.MapPost("/marketing/campaigns", async (CreateCampaignRequest request, HttpContext context, MarketingService service, DatabaseSecurityState securityState,
            CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var (result, campaign) = await service.CreateCampaignAsync(tenantId, actorId, request, cancellationToken);
            return result == MarketingResult.Success ? Results.Created($"/marketing/campaigns/{campaign!.Id}/messages", campaign) : ToError(result);
        }).RequireAuthorization()
            .WithName("CreateMarketingCampaign").WithTags("Marketing").Produces<CampaignCreatedDto>(201)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(429).ProducesProblem(503);

        app.MapGet("/marketing/campaigns", async (HttpContext context, MarketingService service, DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var (result, items) = await service.ListCampaignsAsync(tenantId, actorId, cancellationToken);
            return result == MarketingResult.Success ? Results.Ok(items) : ToError(result);
        }).RequireAuthorization()
            .WithName("ListMarketingCampaigns").WithTags("Marketing").Produces<List<CampaignDto>>(200)
            .ProducesProblem(401).ProducesProblem(403).ProducesProblem(429).ProducesProblem(503);

        app.MapGet("/marketing/campaigns/{id:guid}/messages", async (Guid id, string? status, HttpContext context, MarketingService service, DatabaseSecurityState securityState,
            CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var (result, items) = await service.ListMessagesAsync(tenantId, actorId, id, status, cancellationToken);
            return result == MarketingResult.Success ? Results.Ok(items) : ToError(result);
        }).RequireAuthorization()
            .WithName("ListMarketingMessages").WithTags("Marketing").Produces<List<MarketingMessageDto>>(200)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(429).ProducesProblem(503);

        app.MapGet("/marketing/status", async (HttpContext context, MarketingService service, DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var (result, status) = await service.StatusAsync(tenantId, actorId, cancellationToken);
            return result == MarketingResult.Success ? Results.Ok(status) : ToError(result);
        }).RequireAuthorization()
            .WithName("GetMarketingStatus").WithTags("Marketing").Produces<MarketingStatusDto>(200)
            .ProducesProblem(401).ProducesProblem(403).ProducesProblem(429).ProducesProblem(503);

        app.MapGet("/customers/{customerId:guid}/whatsapp-consent", async (Guid customerId, HttpContext context, MarketingService service, DatabaseSecurityState securityState,
            CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var (result, consent) = await service.GetConsentAsync(tenantId, actorId, customerId, cancellationToken);
            return result == MarketingResult.Success ? Results.Ok(consent) : ToError(result);
        }).RequireAuthorization()
            .WithName("GetWhatsAppConsent").WithTags("Marketing").Produces<WhatsAppConsentDto>(200)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(429).ProducesProblem(503);

        app.MapPut("/customers/{customerId:guid}/whatsapp-consent", async (Guid customerId, SetWhatsAppConsentRequest request, HttpContext context, MarketingService service,
            DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var result = await service.SetConsentAsync(tenantId, actorId, customerId, request, cancellationToken);
            return result == MarketingResult.Success ? Results.NoContent() : ToError(result);
        }).RequireAuthorization()
            .WithName("SetWhatsAppConsent").WithTags("Marketing").Produces(204)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(429).ProducesProblem(503);

        app.MapPost("/marketing/messages/{id:guid}/sent", async (Guid id, HttpContext context, MarketingService service, DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
            await Handle(id, "sent", context, service, securityState, cancellationToken)).RequireAuthorization()
            .WithName("MarkMarketingMessageSent").WithTags("Marketing").Produces(204)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).ProducesProblem(429).ProducesProblem(503);

        app.MapPost("/marketing/messages/{id:guid}/skip", async (Guid id, HttpContext context, MarketingService service, DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
            await Handle(id, "skipped", context, service, securityState, cancellationToken)).RequireAuthorization()
            .WithName("SkipMarketingMessage").WithTags("Marketing").Produces(204)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).ProducesProblem(429).ProducesProblem(503);
    }

    private static async Task<IResult> Handle(Guid id, string status, HttpContext context, MarketingService service, DatabaseSecurityState securityState, CancellationToken cancellationToken)
    {
        if (securityState.IsInsecureDbRole) return Unavailable();
        if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
        var result = await service.HandleAsync(tenantId, actorId, id, status, cancellationToken);
        return result == MarketingResult.Success ? Results.NoContent() : ToError(result);
    }

    private static bool TryReadActor(HttpContext context, out TenantId tenantId, out UserId actorId)
    {
        var subject = context.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        var tenant = context.User.FindFirst("tenant_id")?.Value;
        var valid = Guid.TryParse(subject, out var actor) && actor != Guid.Empty &&
                    Guid.TryParse(tenant, out var tenantGuid) && tenantGuid != Guid.Empty;
        tenantId = valid ? new TenantId(Guid.Parse(tenant!)) : default;
        actorId = valid ? new UserId(actor) : default;
        return valid;
    }

    private static IResult Unavailable() => ApiErrors.Problem(StatusCodes.Status503ServiceUnavailable, ApiErrors.ServiceUnavailable);

    private static IResult ToError(MarketingResult result) => result switch
    {
        MarketingResult.InvalidData => ApiErrors.Problem(StatusCodes.Status400BadRequest, ApiErrors.InvalidData),
        MarketingResult.NotFound => ApiErrors.Problem(StatusCodes.Status404NotFound, ApiErrors.NotFound),
        MarketingResult.Conflict => ApiErrors.Problem(StatusCodes.Status409Conflict, ApiErrors.Conflict),
        MarketingResult.Unavailable => ApiErrors.Problem(StatusCodes.Status503ServiceUnavailable, ApiErrors.ServiceUnavailable),
        _ => Results.Forbid()
    };
}
