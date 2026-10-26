using System.IdentityModel.Tokens.Jwt;
using LifeDrop.Application.Orders;
using LifeDrop.Domain.Common;

namespace LifeDrop.API.Endpoints;

// The orders of the wholesalers (mudhakhar) for the wholesaler officer (plan step 5.6): the wholesalers the officer works with, their orders, and "dispatched".
public static class WholesalerEndpoints
{
    public static void MapWholesalerEndpoints(this WebApplication app)
    {
        app.MapGet("/wholesaler/warehouses", async (HttpContext context, WholesalerOrders service, DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var (result, items) = await service.ListWholesalersAsync(tenantId, actorId, cancellationToken);
            return result == OrderResult.Success ? Results.Ok(items) : ToError(result);
        }).RequireAuthorization()
            .WithName("ListWholesalers").WithTags("Wholesaler").Produces<List<WholesalerDto>>(200)
            .ProducesProblem(401).ProducesProblem(403).ProducesProblem(429).ProducesProblem(503);

        app.MapGet("/wholesaler/orders", async (Guid? warehouseId, string? status, int? limit, HttpContext context, WholesalerOrders service, DatabaseSecurityState securityState,
            CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var (result, items) = await service.ListOrdersAsync(tenantId, actorId, warehouseId, status, limit, cancellationToken);
            return result == OrderResult.Success ? Results.Ok(items) : ToError(result);
        }).RequireAuthorization()
            .WithName("ListWholesalerOrders").WithTags("Wholesaler").Produces<List<WholesalerOrderDto>>(200)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(429).ProducesProblem(503);

        app.MapPost("/wholesaler/orders/{orderId:guid}/dispatch", async (Guid orderId, DispatchRequest request, HttpContext context, WholesalerOrders service,
            DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var result = await service.DispatchAsync(tenantId, actorId, orderId, request, cancellationToken);
            return result == OrderResult.Success ? Results.NoContent() : ToError(result);
        }).RequireAuthorization()
            .WithName("DispatchWholesalerOrder").WithTags("Wholesaler").Produces(204)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).ProducesProblem(429).ProducesProblem(503);
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

    private static IResult ToError(OrderResult result) => result switch
    {
        OrderResult.InvalidData => ApiErrors.Problem(StatusCodes.Status400BadRequest, ApiErrors.InvalidData),
        OrderResult.NotFound => ApiErrors.Problem(StatusCodes.Status404NotFound, ApiErrors.NotFound),
        OrderResult.Conflict => ApiErrors.Problem(StatusCodes.Status409Conflict, ApiErrors.Conflict),
        _ => Results.Forbid()
    };
}
