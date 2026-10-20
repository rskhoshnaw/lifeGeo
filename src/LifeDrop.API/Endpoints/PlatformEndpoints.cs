using System.IdentityModel.Tokens.Jwt;
using LifeDrop.Application.Platform;
using LifeDrop.Domain.Common;

namespace LifeDrop.API.Endpoints;

// Super Admin of the platform company (plan step 3.1): companies, and the explicit audited read of another company's audit log.
// The rules (platform company + permission, reasons, audit) are in PlatformService.
public static class PlatformEndpoints
{
    public static void MapPlatformEndpoints(this WebApplication app)
    {
        app.MapGet("/platform/tenants", async (HttpContext context, PlatformService service, DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var (result, items) = await service.ListTenantsAsync(tenantId, actorId, cancellationToken);
            return result == PlatformResult.Success ? Results.Ok(items) : ToError(result);
        }).RequireAuthorization()
            .WithName("ListPlatformTenants").WithTags("Platform").Produces<List<TenantSummaryDto>>(200)
            .ProducesProblem(401).ProducesProblem(403).ProducesProblem(429).ProducesProblem(503);

        app.MapPost("/platform/tenants", async (CreateTenantRequest request, HttpContext context, PlatformService service, DatabaseSecurityState securityState,
            CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var (result, created) = await service.CreateTenantAsync(tenantId, actorId, request, cancellationToken);
            return result == PlatformResult.Success ? Results.Created($"/platform/tenants/{created!.Id}", created) : ToError(result);
        }).RequireAuthorization()
            .WithName("CreatePlatformTenant").WithTags("Platform").Produces<CreatedTenantDto>(201)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(409).ProducesProblem(429).ProducesProblem(503);

        app.MapPut("/platform/tenants/{tenantId:guid}", async (Guid tenantId, UpdateTenantRequest request, HttpContext context, PlatformService service,
            DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var callerTenant, out var actorId)) return Results.Forbid();
            var result = await service.UpdateTenantAsync(callerTenant, actorId, tenantId, request, cancellationToken);
            return result == PlatformResult.Success ? Results.NoContent() : ToError(result);
        }).RequireAuthorization()
            .WithName("UpdatePlatformTenant").WithTags("Platform").Produces(204)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).ProducesProblem(429).ProducesProblem(503);

        app.MapPut("/platform/tenants/{tenantId:guid}/profile", async (Guid tenantId, UpdateTenantProfileRequest request, HttpContext context, PlatformService service,
            DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var callerTenant, out var actorId)) return Results.Forbid();
            var result = await service.UpdateTenantProfileAsync(callerTenant, actorId, tenantId, request, cancellationToken);
            return result == PlatformResult.Success ? Results.NoContent() : ToError(result);
        }).RequireAuthorization()
            .WithName("UpdatePlatformTenantProfile").WithTags("Platform").Produces(204)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(429).ProducesProblem(503);

        app.MapGet("/platform/tenants/{tenantId:guid}/audit-logs", async (Guid tenantId, string? reason, string? action, int? limit, HttpContext context, PlatformService service,
            DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var callerTenant, out var actorId)) return Results.Forbid();
            var (result, items) = await service.ReadTenantAuditAsync(callerTenant, actorId, tenantId, reason, action, limit, cancellationToken);
            return result == PlatformResult.Success ? Results.Ok(items) : ToError(result);
        }).RequireAuthorization()
            .WithName("ReadPlatformTenantAuditLogs").WithTags("Platform").Produces<List<TenantAuditEntryDto>>(200)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(429).ProducesProblem(503);

        app.MapGet("/platform/audit-logs", async (Guid? tenantId, int? limit, HttpContext context, PlatformService service, DatabaseSecurityState securityState,
            CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var callerTenant, out var actorId)) return Results.Forbid();
            var (result, items) = await service.ListPlatformAuditAsync(callerTenant, actorId, tenantId, limit, cancellationToken);
            return result == PlatformResult.Success ? Results.Ok(items) : ToError(result);
        }).RequireAuthorization()
            .WithName("ListPlatformAuditLogs").WithTags("Platform").Produces<List<PlatformAuditEntryDto>>(200)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(429).ProducesProblem(503);
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

    private static IResult ToError(PlatformResult result) => result switch
    {
        PlatformResult.InvalidData => ApiErrors.Problem(StatusCodes.Status400BadRequest, ApiErrors.InvalidData),
        PlatformResult.NotFound => ApiErrors.Problem(StatusCodes.Status404NotFound, ApiErrors.NotFound),
        PlatformResult.Conflict => ApiErrors.Problem(StatusCodes.Status409Conflict, ApiErrors.Conflict),
        _ => Results.Forbid()
    };
}
