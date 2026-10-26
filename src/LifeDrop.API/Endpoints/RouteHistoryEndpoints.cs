using System.IdentityModel.Tokens.Jwt;
using LifeDrop.Application.Tracking;
using LifeDrop.Domain.Common;

namespace LifeDrop.API.Endpoints;

// Route history, playback and the customer map (plan step 2.11): tracking.read within the reader's area, every read audited.
public static class RouteHistoryEndpoints
{
    public static void MapRouteHistoryEndpoints(this WebApplication app)
    {
        app.MapGet("/tracking/users", async (HttpContext context, RouteHistory history, DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var (result, users) = await history.ListUsersAsync(tenantId, actorId, cancellationToken);
            return result == RouteResult.Success ? Results.Ok(users) : ToError(result);
        }).RequireAuthorization()
            .WithName("ListTrackableUsers").WithTags("Tracking").Produces<List<TrackableUserDto>>(200)
            .ProducesProblem(401).ProducesProblem(403).ProducesProblem(429).ProducesProblem(503);

        app.MapGet("/tracking/days", async (Guid userId, DateOnly month, HttpContext context, RouteHistory history, DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var (result, days) = await history.DaysAsync(tenantId, actorId, userId, month, cancellationToken);
            return result == RouteResult.Success ? Results.Ok(days) : ToError(result);
        }).RequireAuthorization()
            .WithName("ListRouteDays").WithTags("Tracking").Produces<List<RouteDayDto>>(200)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(429).ProducesProblem(503);

        app.MapGet("/tracking/history", async (Guid userId, DateOnly date, HttpContext context, RouteHistory history, DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var (result, route) = await history.GetAsync(tenantId, actorId, userId, date, cancellationToken);
            return result == RouteResult.Success ? Results.Ok(route) : ToError(result);
        }).RequireAuthorization()
            .WithName("GetRouteHistory").WithTags("Tracking").Produces<RouteHistoryDto>(200)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(429).ProducesProblem(503);

        app.MapGet("/customers/map", async (HttpContext context, CustomerMap map, DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var customers = await map.ListAsync(tenantId, actorId, cancellationToken);
            return customers is null ? Results.Forbid() : Results.Ok(customers);
        }).RequireAuthorization()
            .WithName("GetCustomerMap").WithTags("Customers").Produces<List<MapCustomerDto>>(200)
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

    private static IResult ToError(RouteResult result) => result switch
    {
        RouteResult.InvalidData => ApiErrors.Problem(StatusCodes.Status400BadRequest, ApiErrors.InvalidData),
        RouteResult.NotFound => ApiErrors.Problem(StatusCodes.Status404NotFound, ApiErrors.NotFound),
        _ => Results.Forbid()
    };
}
