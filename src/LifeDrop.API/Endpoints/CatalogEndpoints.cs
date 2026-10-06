using System.IdentityModel.Tokens.Jwt;
using LifeDrop.Application.Catalog;
using LifeDrop.Domain.Common;

namespace LifeDrop.API.Endpoints;

// Products and prices (plan step 2.1): catalog.read to look, catalog.manage to change. Prices a customer gets are computed here, never sent by a phone.
public static class CatalogEndpoints
{
    public static void MapCatalogEndpoints(this WebApplication app)
    {
        app.MapGet("/products", async (bool? includeInactive, string? q, HttpContext context, CatalogService service,
            DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var (result, items) = await service.ListAsync(tenantId, actorId, includeInactive == true, q, cancellationToken);
            return result == CatalogResult.Success ? Results.Ok(items) : ToError(result);
        }).RequireAuthorization()
            .WithName("ListProducts").WithTags("Catalog").Produces<List<ProductDto>>(200)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(429).ProducesProblem(503);

        app.MapPost("/products", async (SaveProductRequest request, HttpContext context, CatalogService service,
            DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var (result, id) = await service.CreateAsync(tenantId, actorId, request, cancellationToken);
            return result == CatalogResult.Success ? Results.Created($"/products/{id}", new CreatedResponse(id!.Value)) : ToError(result);
        }).RequireAuthorization()
            .WithName("CreateProduct").WithTags("Catalog").Produces<CreatedResponse>(201)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(409).ProducesProblem(429).ProducesProblem(503);

        app.MapPut("/products/{productId:guid}", async (Guid productId, SaveProductRequest request, HttpContext context, CatalogService service,
            DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var result = await service.UpdateAsync(tenantId, actorId, productId, request, cancellationToken);
            return result == CatalogResult.Success ? Results.NoContent() : ToError(result);
        }).RequireAuthorization()
            .WithName("UpdateProduct").WithTags("Catalog").Produces(204)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(429).ProducesProblem(503);

        app.MapGet("/products/{productId:guid}/city-prices", async (Guid productId, HttpContext context, CatalogService service,
            DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var (result, items) = await service.ListCityPricesAsync(tenantId, actorId, productId, cancellationToken);
            return result == CatalogResult.Success ? Results.Ok(items) : ToError(result);
        }).RequireAuthorization()
            .WithName("ListCityPrices").WithTags("Catalog").Produces<List<CityPriceDto>>(200)
            .ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(429).ProducesProblem(503);

        app.MapPost("/products/{productId:guid}/city-prices", async (Guid productId, CreateCityPriceRequest request, HttpContext context, CatalogService service,
            DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var (result, id) = await service.CreateCityPriceAsync(tenantId, actorId, productId, request, cancellationToken);
            return result == CatalogResult.Success ? Results.Created($"/products/{productId}/city-prices", new CreatedResponse(id!.Value)) : ToError(result);
        }).RequireAuthorization()
            .WithName("CreateCityPrice").WithTags("Catalog").Produces<CreatedResponse>(201)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).ProducesProblem(429).ProducesProblem(503);

        app.MapPost("/city-prices/{cityPriceId:guid}/end", async (Guid cityPriceId, EndRuleRequest request, HttpContext context, CatalogService service,
            DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var result = await service.EndCityPriceAsync(tenantId, actorId, cityPriceId, request, cancellationToken);
            return result == CatalogResult.Success ? Results.NoContent() : ToError(result);
        }).RequireAuthorization()
            .WithName("EndCityPrice").WithTags("Catalog").Produces(204)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(429).ProducesProblem(503);

        app.MapGet("/customers/{customerId:guid}/discounts", async (Guid customerId, HttpContext context, CatalogService service,
            DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var (result, items) = await service.ListDiscountsAsync(tenantId, actorId, customerId, cancellationToken);
            return result == CatalogResult.Success ? Results.Ok(items) : ToError(result);
        }).RequireAuthorization()
            .WithName("ListCustomerDiscounts").WithTags("Catalog").Produces<List<CustomerDiscountDto>>(200)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(429).ProducesProblem(503);

        app.MapPost("/customers/{customerId:guid}/discounts", async (Guid customerId, CreateDiscountRequest request, HttpContext context, CatalogService service,
            DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var (result, id) = await service.CreateDiscountAsync(tenantId, actorId, customerId, request, cancellationToken);
            return result == CatalogResult.Success ? Results.Created($"/customers/{customerId}/discounts", new CreatedResponse(id!.Value)) : ToError(result);
        }).RequireAuthorization()
            .WithName("CreateCustomerDiscount").WithTags("Catalog").Produces<CreatedResponse>(201)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(429).ProducesProblem(503);

        app.MapPost("/discounts/{discountId:guid}/end", async (Guid discountId, EndRuleRequest request, HttpContext context, CatalogService service,
            DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var result = await service.EndDiscountAsync(tenantId, actorId, discountId, request, cancellationToken);
            return result == CatalogResult.Success ? Results.NoContent() : ToError(result);
        }).RequireAuthorization()
            .WithName("EndCustomerDiscount").WithTags("Catalog").Produces(204)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(429).ProducesProblem(503);

        app.MapGet("/customers/{customerId:guid}/prices", async (Guid customerId, DateOnly? date, HttpContext context, CatalogService service,
            DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var (result, items) = await service.PricesForCustomerAsync(tenantId, actorId, customerId, date, cancellationToken);
            return result == CatalogResult.Success ? Results.Ok(items) : ToError(result);
        }).RequireAuthorization()
            .WithName("GetCustomerPrices").WithTags("Catalog").Produces<List<EffectivePriceDto>>(200)
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

    private static IResult ToError(CatalogResult result) => result switch
    {
        CatalogResult.InvalidData => ApiErrors.Problem(StatusCodes.Status400BadRequest, ApiErrors.InvalidData),
        CatalogResult.NotFound => ApiErrors.Problem(StatusCodes.Status404NotFound, ApiErrors.NotFound),
        CatalogResult.Conflict => ApiErrors.Problem(StatusCodes.Status409Conflict, ApiErrors.Conflict),
        _ => Results.Forbid()
    };
}
