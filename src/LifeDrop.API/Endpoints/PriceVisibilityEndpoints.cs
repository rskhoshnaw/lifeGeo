using System.IdentityModel.Tokens.Jwt;
using LifeDrop.Application.Catalog;
using LifeDrop.Domain.Common;

namespace LifeDrop.API.Endpoints;

// "Hide prices" ticks of a city (places.manage) and of a region (regions.manage), plan step 4.42.
public static class PriceVisibilityEndpoints
{
    public static void MapPriceVisibilityEndpoints(this WebApplication app)
    {
        app.MapPut("/places/{placeId:guid}/hide-prices", async (Guid placeId, SetHidePricesRequest request, HttpContext context, PriceVisibility service,
            DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            return ToResult(await service.SetCityAsync(tenantId, actorId, placeId, request, cancellationToken));
        }).RequireAuthorization()
            .WithName("SetCityHidePrices").WithTags("Places").Produces(204)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(429).ProducesProblem(503);

        app.MapPut("/regions/{regionId:guid}/hide-prices", async (Guid regionId, SetHidePricesRequest request, HttpContext context, PriceVisibility service,
            DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            return ToResult(await service.SetRegionAsync(tenantId, actorId, regionId, request, cancellationToken));
        }).RequireAuthorization()
            .WithName("SetRegionHidePrices").WithTags("Regions").Produces(204)
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

    private static IResult Unavailable() => ApiErrors.Problem(StatusCodes.Status503ServiceUnavailable, ApiErrors.ServiceUnavailable);

    private static IResult ToResult(PriceVisibilityResult result) => result switch
    {
        PriceVisibilityResult.Success => Results.NoContent(),
        PriceVisibilityResult.InvalidData => ApiErrors.Problem(StatusCodes.Status400BadRequest, ApiErrors.InvalidData),
        PriceVisibilityResult.NotFound => ApiErrors.Problem(StatusCodes.Status404NotFound, ApiErrors.NotFound),
        _ => Results.Forbid()
    };
}
