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
        // Places tree (plan step 1.23-b). kind=city lists the cities; parentId lists what is directly below a place.
        app.MapGet("/places", async (Guid? parentId, string? kind, string? q, bool? includeInactive, int? limit, HttpContext context,
            PlaceService service, DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var (result, items) = await service.ListAsync(tenantId, actorId, parentId, kind, q, includeInactive == true, limit, cancellationToken);
            return result == PlaceResult.Success ? Results.Ok(items) : ToError(result);
        }).RequireAuthorization()
            .WithName("ListPlaces").WithTags("Places").Produces<List<PlaceDto>>(200)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(429).ProducesProblem(503);

        app.MapPost("/places", async (CreatePlaceRequest request, HttpContext context, PlaceService service,
            DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var (result, id) = await service.CreateAsync(tenantId, actorId, request, cancellationToken);
            return result == PlaceResult.Success ? Results.Created($"/places/{id}", new CreatedResponse(id)) : ToError(result);
        }).RequireAuthorization()
            .WithName("CreatePlace").WithTags("Places").Produces<CreatedResponse>(201)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).ProducesProblem(429).ProducesProblem(503);

        app.MapPut("/places/{placeId:guid}", async (Guid placeId, UpdatePlaceRequest request, HttpContext context, PlaceService service,
            DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var result = await service.UpdateAsync(tenantId, actorId, placeId, request, cancellationToken);
            return result == PlaceResult.Success ? Results.NoContent() : ToError(result);
        }).RequireAuthorization()
            .WithName("UpdatePlace").WithTags("Places").Produces(204)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).ProducesProblem(429).ProducesProblem(503);

        app.MapPost("/places/{placeId:guid}/merge", async (Guid placeId, MergePlaceRequest request, HttpContext context, PlaceService service,
            DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var result = await service.MergeAsync(tenantId, actorId, placeId, request, cancellationToken);
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

        // The whole tree for other projects: JSON (default) or CSV with the same columns as the import file.
        app.MapGet("/places/export", async (string? format, bool? includeInactive, HttpContext context, PlaceService service,
            DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            if (format is not (null or "json" or "csv")) return ApiErrors.Problem(StatusCodes.Status400BadRequest, ApiErrors.InvalidData);
            var (result, rows) = await service.ExportAsync(tenantId, actorId, includeInactive == true, cancellationToken);
            if (result != PlaceResult.Success) return ToError(result);
            return format == "csv"
                ? Results.Text("\uFEFF" + PlaceExport.ToCsv(rows), "text/csv; charset=utf-8")
                : Results.Ok(rows);
        }).RequireAuthorization()
            .WithName("ExportPlaces").WithTags("Places").Produces<List<PlaceExportRow>>(200).Produces(200, contentType: "text/csv")
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

        // Priority of customers by a manager (plan step 1.26).
        app.MapPut("/customers/priority", async (SetCustomerPriorityRequest request, HttpContext context,
            CustomerPriorityService service, DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var (result, data) = await service.SetAsync(tenantId, actorId, request, cancellationToken);
            return result switch
            {
                CustomerPriorityResult.Success => Results.Ok(data),
                CustomerPriorityResult.InvalidData => ApiErrors.Problem(StatusCodes.Status400BadRequest, ApiErrors.InvalidData),
                _ => Results.Forbid()
            };
        }).RequireAuthorization()
            .WithName("SetCustomerPriority").WithTags("Customers").Produces<CustomerPriorityDto>(200)
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
