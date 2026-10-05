using System.IdentityModel.Tokens.Jwt;
using LifeDrop.Application.Customers;
using LifeDrop.Application.Places;
using LifeDrop.Domain.Common;

namespace LifeDrop.API.Endpoints;

// Reference list of cities, neighbourhoods and streets and the customer profile (plan step 1.23-b).
public static class PlaceEndpoints
{
    public static void MapPlaceEndpoints(this WebApplication app)
    {
        app.MapGet("/cities", async (bool? includeInactive, HttpContext context, PlaceService service,
            DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var (result, items) = await service.ListCitiesAsync(tenantId, actorId, includeInactive == true, cancellationToken);
            return result == PlaceResult.Success ? Results.Ok(items) : ToError(result);
        }).RequireAuthorization()
            .WithName("ListCities").WithTags("Places").Produces<List<CityDto>>(200)
            .ProducesProblem(401).ProducesProblem(403).ProducesProblem(429).ProducesProblem(503);

        app.MapPost("/cities", async (CreateCityRequest request, HttpContext context, PlaceService service,
            DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var (result, id) = await service.CreateCityAsync(tenantId, actorId, request, cancellationToken);
            return result == PlaceResult.Success ? Results.Created($"/cities/{id}", new CreatedResponse(id)) : ToError(result);
        }).RequireAuthorization()
            .WithName("CreateCity").WithTags("Places").Produces<CreatedResponse>(201)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(409).ProducesProblem(429).ProducesProblem(503);

        app.MapGet("/places", async (Guid? cityId, string? kind, string? q, bool? includeInactive, int? limit, HttpContext context,
            PlaceService service, DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var (result, items) = await service.ListPlacesAsync(tenantId, actorId, cityId, kind, q, includeInactive == true, limit, cancellationToken);
            return result == PlaceResult.Success ? Results.Ok(items) : ToError(result);
        }).RequireAuthorization()
            .WithName("ListPlaces").WithTags("Places").Produces<List<PlaceDto>>(200)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(429).ProducesProblem(503);

        app.MapPost("/places", async (CreatePlaceRequest request, HttpContext context, PlaceService service,
            DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var (result, id) = await service.CreatePlaceAsync(tenantId, actorId, request, cancellationToken);
            return result == PlaceResult.Success ? Results.Created($"/places/{id}", new CreatedResponse(id)) : ToError(result);
        }).RequireAuthorization()
            .WithName("CreatePlace").WithTags("Places").Produces<CreatedResponse>(201)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).ProducesProblem(429).ProducesProblem(503);

        app.MapPut("/places/{placeId:guid}", async (Guid placeId, UpdatePlaceRequest request, HttpContext context, PlaceService service,
            DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var result = await service.UpdatePlaceAsync(tenantId, actorId, placeId, request, cancellationToken);
            return result == PlaceResult.Success ? Results.NoContent() : ToError(result);
        }).RequireAuthorization()
            .WithName("UpdatePlace").WithTags("Places").Produces(204)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).ProducesProblem(429).ProducesProblem(503);

        app.MapPost("/places/{placeId:guid}/merge", async (Guid placeId, MergePlaceRequest request, HttpContext context, PlaceService service,
            DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var result = await service.MergePlaceAsync(tenantId, actorId, placeId, request, cancellationToken);
            return result == PlaceResult.Success ? Results.NoContent() : ToError(result);
        }).RequireAuthorization()
            .WithName("MergePlace").WithTags("Places").Produces(204)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).ProducesProblem(429).ProducesProblem(503);

        app.MapPost("/places/import", async (ImportPlacesRequest request, HttpContext context, PlaceService service,
            DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var (result, summary) = await service.ImportAsync(tenantId, actorId, request, cancellationToken);
            return result == PlaceResult.Success ? Results.Ok(summary) : ToError(result);
        }).RequireAuthorization()
            .WithName("ImportPlaces").WithTags("Places").Produces<ImportPlacesResult>(200)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(429).ProducesProblem(503);

        // Phone, address and place of a customer (plan step 1.23-b).
        app.MapPut("/customers/{customerId:guid}/profile", async (Guid customerId, CustomerProfileRequest request, HttpContext context,
            CustomerProfileService service, DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var (result, _) = await service.SetAsync(tenantId, actorId, customerId, request, cancellationToken);
            return result switch
            {
                CustomerUpdateResult.Success => Results.NoContent(),
                CustomerUpdateResult.NotFound => ApiErrors.Problem(StatusCodes.Status404NotFound, ApiErrors.NotFound),
                CustomerUpdateResult.InvalidData => ApiErrors.Problem(StatusCodes.Status400BadRequest, ApiErrors.InvalidData),
                _ => Results.Forbid()
            };
        }).RequireAuthorization()
            .WithName("SetCustomerProfile").WithTags("Customers").Produces(204)
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

    private static IResult ToError(PlaceResult result) => result switch
    {
        PlaceResult.Forbidden => Results.Forbid(),
        PlaceResult.InvalidData => ApiErrors.Problem(StatusCodes.Status400BadRequest, ApiErrors.InvalidData),
        PlaceResult.NotFound => ApiErrors.Problem(StatusCodes.Status404NotFound, ApiErrors.NotFound),
        PlaceResult.Duplicate => ApiErrors.Problem(StatusCodes.Status409Conflict, ApiErrors.DuplicateName),
        PlaceResult.Conflict => ApiErrors.Problem(StatusCodes.Status409Conflict, ApiErrors.ConcurrencyConflict),
        _ => ApiErrors.Problem(StatusCodes.Status500InternalServerError, ApiErrors.Unexpected)
    };
}
