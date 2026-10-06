using System.IdentityModel.Tokens.Jwt;
using LifeDrop.Application.Orders;
using LifeDrop.Domain.Common;

namespace LifeDrop.API.Endpoints;

// Orders and warehouses (plan step 2.2). The caller never sends a price: the server works out every line (general or city price, then the
// customer's discounts). An order is "placed" only after this answer is 201 (or 200 for a retry of the same ClientRef).
public static class OrderEndpoints
{
    public static void MapOrderEndpoints(this WebApplication app)
    {
        app.MapGet("/warehouses", async (bool? includeInactive, HttpContext context, OrderService service, DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var (result, items) = await service.ListWarehousesAsync(tenantId, actorId, includeInactive == true, cancellationToken);
            return result == OrderResult.Success ? Results.Ok(items) : ToError(result);
        }).RequireAuthorization()
            .WithName("ListWarehouses").WithTags("Orders").Produces<List<WarehouseDto>>(200)
            .ProducesProblem(401).ProducesProblem(403).ProducesProblem(429).ProducesProblem(503);

        app.MapPost("/warehouses", async (SaveWarehouseRequest request, HttpContext context, OrderService service, DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var (result, id) = await service.CreateWarehouseAsync(tenantId, actorId, request, cancellationToken);
            return result == OrderResult.Success ? Results.Created($"/warehouses/{id}", new CreatedResponse(id!.Value)) : ToError(result);
        }).RequireAuthorization()
            .WithName("CreateWarehouse").WithTags("Orders").Produces<CreatedResponse>(201)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(409).ProducesProblem(429).ProducesProblem(503);

        app.MapPut("/warehouses/{warehouseId:guid}", async (Guid warehouseId, SaveWarehouseRequest request, HttpContext context, OrderService service,
            DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var result = await service.UpdateWarehouseAsync(tenantId, actorId, warehouseId, request, cancellationToken);
            return result == OrderResult.Success ? Results.NoContent() : ToError(result);
        }).RequireAuthorization()
            .WithName("UpdateWarehouse").WithTags("Orders").Produces(204)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(429).ProducesProblem(503);

        app.MapPost("/orders", async (CreateOrderRequest request, HttpContext context, OrderService service, DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var (result, order) = await service.CreateAsync(tenantId, actorId, request, cancellationToken);
            if (result != OrderResult.Success) return ToError(result);
            return order!.Replayed ? Results.Ok(order) : Results.Created($"/orders/{order.Id}", order);
        }).RequireAuthorization()
            .WithName("CreateOrder").WithTags("Orders").Produces<CreatedOrderDto>(201).Produces<CreatedOrderDto>(200)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).ProducesProblem(429).ProducesProblem(503);

        app.MapGet("/orders", async (Guid? customerId, string? status, int? limit, DateTimeOffset? before, Guid? beforeId, HttpContext context, OrderService service,
            DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var (result, items) = await service.ListAsync(tenantId, actorId, customerId, status, limit, before, beforeId, cancellationToken);
            return result == OrderResult.Success ? Results.Ok(items) : ToError(result);
        }).RequireAuthorization()
            .WithName("ListOrders").WithTags("Orders").Produces<List<OrderDto>>(200)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(429).ProducesProblem(503);

        app.MapGet("/orders/{orderId:guid}", async (Guid orderId, HttpContext context, OrderService service, DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var (result, order) = await service.GetAsync(tenantId, actorId, orderId, cancellationToken);
            return result == OrderResult.Success ? Results.Ok(order) : ToError(result);
        }).RequireAuthorization()
            .WithName("GetOrder").WithTags("Orders").Produces<OrderDto>(200)
            .ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(429).ProducesProblem(503);

        app.MapPost("/orders/{orderId:guid}/cancel", async (Guid orderId, CancelOrderRequest request, HttpContext context, OrderService service,
            DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var result = await service.CancelAsync(tenantId, actorId, orderId, request, cancellationToken);
            return result == OrderResult.Success ? Results.NoContent() : ToError(result);
        }).RequireAuthorization()
            .WithName("CancelOrder").WithTags("Orders").Produces(204)
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
        OrderResult.CurrencyMismatch => ApiErrors.Problem(StatusCodes.Status400BadRequest, ApiErrors.CurrencyMismatch),
        OrderResult.NotFound => ApiErrors.Problem(StatusCodes.Status404NotFound, ApiErrors.NotFound),
        OrderResult.CustomerNotApproved => ApiErrors.Problem(StatusCodes.Status409Conflict, ApiErrors.CustomerNotApproved),
        OrderResult.Conflict => ApiErrors.Problem(StatusCodes.Status409Conflict, ApiErrors.Conflict),
        _ => Results.Forbid()
    };
}
