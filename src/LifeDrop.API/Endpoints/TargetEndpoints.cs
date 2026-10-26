using System.IdentityModel.Tokens.Jwt;
using LifeDrop.Application.Targets;
using LifeDrop.Domain.Common;

namespace LifeDrop.API.Endpoints;

// Sales targets per visitor, product and month and the report against what was delivered (plan step 5.5).
public static class TargetEndpoints
{
    public static void MapTargetEndpoints(this WebApplication app)
    {
        app.MapGet("/targets", async (DateOnly month, HttpContext context, SalesTargets targets, DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var (result, rows) = await targets.ListAsync(tenantId, actorId, month, cancellationToken);
            return result == TargetResult.Success ? Results.Ok(rows) : ToError(result);
        }).RequireAuthorization()
            .WithName("ListTargets").WithTags("Targets").Produces<List<TargetRowDto>>(200)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(429).ProducesProblem(503);

        app.MapPut("/targets", async (SetTargetRequest request, HttpContext context, SalesTargets targets, DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var result = await targets.SetAsync(tenantId, actorId, request, cancellationToken);
            return result == TargetResult.Success ? Results.NoContent() : ToError(result);
        }).RequireAuthorization()
            .WithName("SetTarget").WithTags("Targets").Produces(204)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(429).ProducesProblem(503);

        // The caller's own targets and deliveries (the visitor's page in the app); needs no permission.
        app.MapGet("/targets/mine", async (DateOnly month, HttpContext context, SalesTargets targets, DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var (result, rows) = await targets.MineAsync(tenantId, actorId, month, cancellationToken);
            return result == TargetResult.Success ? Results.Ok(rows) : ToError(result);
        }).RequireAuthorization()
            .WithName("ListMyTargets").WithTags("Targets").Produces<List<TargetRowDto>>(200)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(429).ProducesProblem(503);
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

    private static IResult ToError(TargetResult result) => result switch
    {
        TargetResult.InvalidData => ApiErrors.Problem(StatusCodes.Status400BadRequest, ApiErrors.InvalidData),
        TargetResult.NotFound => ApiErrors.Problem(StatusCodes.Status404NotFound, ApiErrors.NotFound),
        _ => Results.Forbid()
    };
}
