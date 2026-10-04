using System.IdentityModel.Tokens.Jwt;
using LifeDrop.Application.Regions;
using LifeDrop.Domain.Common;

namespace LifeDrop.API.Endpoints;

// Region and team management (plan step 0.6a, decision D11).
public static class RegionTeamEndpoints
{
    public static void MapRegionTeamEndpoints(this WebApplication app)
    {
        app.MapPost("/regions", async (CreateRegionRequest request, HttpContext context, RegionTeamService service,
            DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var (result, id) = await service.CreateRegionAsync(tenantId, actorId, request, cancellationToken);
            return result == RegionTeamResult.Success
                ? Results.Created($"/regions/{id}", new CreatedResponse(id))
                : ToError(result);
        }).RequireAuthorization()
            .WithName("CreateRegion").WithTags("Regions").Produces<CreatedResponse>(201)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(409).ProducesProblem(429).ProducesProblem(503);

        app.MapGet("/regions", async (HttpContext context, RegionTeamService service,
            DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var (result, items) = await service.ListRegionsAsync(tenantId, actorId, cancellationToken);
            return result == RegionTeamResult.Success ? Results.Ok(items) : ToError(result);
        }).RequireAuthorization()
            .WithName("ListRegions").WithTags("Regions").Produces<List<RegionDto>>(200)
            .ProducesProblem(401).ProducesProblem(403).ProducesProblem(429).ProducesProblem(503);

        app.MapPost("/teams", async (CreateTeamRequest request, HttpContext context, RegionTeamService service,
            DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var (result, id) = await service.CreateTeamAsync(tenantId, actorId, request, cancellationToken);
            return result == RegionTeamResult.Success
                ? Results.Created($"/teams/{id}", new CreatedResponse(id))
                : ToError(result);
        }).RequireAuthorization()
            .WithName("CreateTeam").WithTags("Teams").Produces<CreatedResponse>(201)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).ProducesProblem(429).ProducesProblem(503);

        app.MapGet("/teams", async (HttpContext context, RegionTeamService service,
            DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var (result, items) = await service.ListTeamsAsync(tenantId, actorId, cancellationToken);
            return result == RegionTeamResult.Success ? Results.Ok(items) : ToError(result);
        }).RequireAuthorization()
            .WithName("ListTeams").WithTags("Teams").Produces<List<TeamDto>>(200)
            .ProducesProblem(401).ProducesProblem(403).ProducesProblem(429).ProducesProblem(503);

        app.MapPut("/teams/{teamId:guid}/region", async (Guid teamId, ChangeTeamRegionRequest request, HttpContext context,
            RegionTeamService service, DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var result = await service.ChangeTeamRegionAsync(tenantId, actorId, teamId, request, cancellationToken);
            return result == RegionTeamResult.Success ? Results.NoContent() : ToError(result);
        }).RequireAuthorization()
            .WithName("ChangeTeamRegion").WithTags("Teams").Produces(204)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(429).ProducesProblem(503);

        app.MapPut("/teams/{teamId:guid}/members/{userId:guid}", async (Guid teamId, Guid userId, HttpContext context,
            RegionTeamService service, DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var result = await service.AssignMemberAsync(tenantId, actorId, teamId, userId, cancellationToken);
            return result == RegionTeamResult.Success ? Results.NoContent() : ToError(result);
        }).RequireAuthorization()
            .WithName("AssignTeamMember").WithTags("Teams").Produces(204)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(429).ProducesProblem(503);

        app.MapDelete("/teams/{teamId:guid}/members/{userId:guid}", async (Guid teamId, Guid userId, HttpContext context,
            RegionTeamService service, DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var result = await service.RemoveMemberAsync(tenantId, actorId, teamId, userId, cancellationToken);
            return result == RegionTeamResult.Success ? Results.NoContent() : ToError(result);
        }).RequireAuthorization()
            .WithName("RemoveTeamMember").WithTags("Teams").Produces(204)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(429).ProducesProblem(503);
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

    private static IResult Unavailable() =>
        ApiErrors.Problem(StatusCodes.Status503ServiceUnavailable, ApiErrors.ServiceUnavailable);

    private static IResult ToError(RegionTeamResult result) => result switch
    {
        RegionTeamResult.Forbidden => Results.Forbid(),
        RegionTeamResult.InvalidData => ApiErrors.Problem(StatusCodes.Status400BadRequest, ApiErrors.InvalidData),
        RegionTeamResult.NotFound => ApiErrors.Problem(StatusCodes.Status404NotFound, ApiErrors.NotFound),
        RegionTeamResult.RegionNotFound => ApiErrors.Problem(StatusCodes.Status404NotFound, ApiErrors.RegionNotFound),
        RegionTeamResult.UserNotFound => ApiErrors.Problem(StatusCodes.Status404NotFound, ApiErrors.UserNotFound),
        RegionTeamResult.Duplicate => ApiErrors.Problem(StatusCodes.Status409Conflict, ApiErrors.DuplicateName),
        _ => ApiErrors.Problem(StatusCodes.Status500InternalServerError, ApiErrors.Unexpected)
    };
}
