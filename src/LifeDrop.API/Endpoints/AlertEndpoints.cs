using System.IdentityModel.Tokens.Jwt;
using LifeDrop.Application.Alerts;
using LifeDrop.Domain.Common;

namespace LifeDrop.API.Endpoints;

// Alerts in the panel (plan step 2.9): operational and security alerts, separated by permission; new -> seen -> handled with a note.
public static class AlertEndpoints
{
    public static void MapAlertEndpoints(this WebApplication app)
    {
        app.MapGet("/alerts", async (string? category, string? status, string? kind, int? limit, HttpContext context, AlertService service, DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var (result, items) = await service.ListAsync(tenantId, actorId, category, status, kind, limit, cancellationToken);
            return result == AlertResult.Success ? Results.Ok(items) : ToError(result);
        }).RequireAuthorization()
            .WithName("ListAlerts").WithTags("Alerts").Produces<List<AlertDto>>(200)
            .ProducesProblem(401).ProducesProblem(403).ProducesProblem(400).ProducesProblem(429).ProducesProblem(503);

        app.MapGet("/alerts/count", async (HttpContext context, AlertService service, DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var (result, count) = await service.CountAsync(tenantId, actorId, cancellationToken);
            return result == AlertResult.Success ? Results.Ok(count) : ToError(result);
        }).RequireAuthorization()
            .WithName("CountNewAlerts").WithTags("Alerts").Produces<AlertCountDto>(200)
            .ProducesProblem(401).ProducesProblem(403).ProducesProblem(429).ProducesProblem(503);

        app.MapGet("/alerts/{alertId:guid}", async (Guid alertId, HttpContext context, AlertService service, DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var (result, alert) = await service.GetAsync(tenantId, actorId, alertId, cancellationToken);
            return result == AlertResult.Success ? Results.Ok(alert) : ToError(result);
        }).RequireAuthorization()
            .WithName("GetAlert").WithTags("Alerts").Produces<AlertDto>(200)
            .ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(429).ProducesProblem(503);

        app.MapPost("/alerts/{alertId:guid}/seen", async (Guid alertId, HttpContext context, AlertService service, DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var result = await service.MarkSeenAsync(tenantId, actorId, alertId, cancellationToken);
            return result == AlertResult.Success ? Results.NoContent() : ToError(result);
        }).RequireAuthorization()
            .WithName("MarkAlertSeen").WithTags("Alerts").Produces(204)
            .ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).ProducesProblem(429).ProducesProblem(503);

        app.MapPost("/alerts/{alertId:guid}/handle", async (Guid alertId, HandleAlertRequest request, HttpContext context, AlertService service, DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var result = await service.HandleAsync(tenantId, actorId, alertId, request, cancellationToken);
            return result == AlertResult.Success ? Results.NoContent() : ToError(result);
        }).RequireAuthorization()
            .WithName("HandleAlert").WithTags("Alerts").Produces(204)
            .ProducesProblem(401).ProducesProblem(403).ProducesProblem(400).ProducesProblem(404).ProducesProblem(409).ProducesProblem(429).ProducesProblem(503);

        app.MapPost("/alerts/scan", async (HttpContext context, AlertService service, DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var (result, scan) = await service.ScanAsync(tenantId, actorId, cancellationToken);
            return result == AlertResult.Success ? Results.Ok(scan) : ToError(result);
        }).RequireAuthorization()
            .WithName("ScanAlerts").WithTags("Alerts").Produces<ScanResultDto>(200)
            .ProducesProblem(401).ProducesProblem(403).ProducesProblem(429).ProducesProblem(503);
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

    private static IResult ToError(AlertResult result) => result switch
    {
        AlertResult.InvalidData => ApiErrors.Problem(StatusCodes.Status400BadRequest, ApiErrors.InvalidData),
        AlertResult.NotFound => ApiErrors.Problem(StatusCodes.Status404NotFound, ApiErrors.NotFound),
        AlertResult.Conflict => ApiErrors.Problem(StatusCodes.Status409Conflict, ApiErrors.Conflict),
        _ => Results.Forbid()
    };
}
