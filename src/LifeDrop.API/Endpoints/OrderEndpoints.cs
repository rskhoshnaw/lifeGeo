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

        // Staff of a warehouse (plan step 2.3): who works where, set by the sales manager.
        app.MapGet("/warehouses/{warehouseId:guid}/staff", async (Guid warehouseId, HttpContext context, OrderService service, DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var (result, items) = await service.ListStaffAsync(tenantId, actorId, warehouseId, cancellationToken);
            return result == OrderResult.Success ? Results.Ok(items) : ToError(result);
        }).RequireAuthorization()
            .WithName("ListWarehouseStaff").WithTags("Warehouse").Produces<List<StaffMemberDto>>(200)
            .ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(429).ProducesProblem(503);

        app.MapPost("/warehouses/{warehouseId:guid}/staff", async (Guid warehouseId, AddStaffRequest request, HttpContext context, OrderService service,
            DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var result = await service.AddStaffAsync(tenantId, actorId, warehouseId, request, cancellationToken);
            return result == OrderResult.Success ? Results.NoContent() : ToError(result);
        }).RequireAuthorization()
            .WithName("AddWarehouseStaff").WithTags("Warehouse").Produces(204)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(429).ProducesProblem(503);

        app.MapDelete("/warehouses/{warehouseId:guid}/staff/{userId:guid}", async (Guid warehouseId, Guid userId, HttpContext context, OrderService service,
            DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var result = await service.RemoveStaffAsync(tenantId, actorId, warehouseId, userId, cancellationToken);
            return result == OrderResult.Success ? Results.NoContent() : ToError(result);
        }).RequireAuthorization()
            .WithName("RemoveWarehouseStaff").WithTags("Warehouse").Produces(204)
            .ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(429).ProducesProblem(503);

        // Warehouse work (plan step 2.3): the orders of the warehouses the user works in, nothing else.
        app.MapGet("/warehouse/orders", async (string? status, int? limit, DateTimeOffset? before, Guid? beforeId, HttpContext context, OrderService service,
            DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var (result, items) = await service.ListForWarehouseAsync(tenantId, actorId, status, limit, before, beforeId, cancellationToken);
            return result == OrderResult.Success ? Results.Ok(items) : ToError(result);
        }).RequireAuthorization()
            .WithName("ListWarehouseOrders").WithTags("Warehouse").Produces<List<OrderDto>>(200)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(429).ProducesProblem(503);

        app.MapGet("/warehouse/orders/{orderId:guid}", async (Guid orderId, HttpContext context, OrderService service, DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var (result, order) = await service.GetForWarehouseAsync(tenantId, actorId, orderId, cancellationToken);
            return result == OrderResult.Success ? Results.Ok(order) : ToError(result);
        }).RequireAuthorization()
            .WithName("GetWarehouseOrder").WithTags("Warehouse").Produces<OrderDto>(200)
            .ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(429).ProducesProblem(503);

        app.MapPost("/warehouse/orders/{orderId:guid}/start", async (Guid orderId, HttpContext context, OrderService service, DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var result = await service.StartPreparingAsync(tenantId, actorId, orderId, cancellationToken);
            return result == OrderResult.Success ? Results.NoContent() : ToError(result);
        }).RequireAuthorization()
            .WithName("StartPreparingOrder").WithTags("Warehouse").Produces(204)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).ProducesProblem(429).ProducesProblem(503);

        app.MapPost("/warehouse/orders/{orderId:guid}/ready", async (Guid orderId, MarkReadyRequest request, HttpContext context, OrderService service,
            DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var result = await service.MarkReadyAsync(tenantId, actorId, orderId, request, cancellationToken);
            return result == OrderResult.Success ? Results.NoContent() : ToError(result);
        }).RequireAuthorization()
            .WithName("MarkOrderReady").WithTags("Warehouse").Produces(204)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).ProducesProblem(429).ProducesProblem(503);

        app.MapPost("/warehouse/orders/{orderId:guid}/handover", async (Guid orderId, HandOverRequest request, HttpContext context, OrderService service,
            DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var result = await service.HandOverAsync(tenantId, actorId, orderId, request, cancellationToken);
            return result == OrderResult.Success ? Results.NoContent() : ToError(result);
        }).RequireAuthorization()
            .WithName("HandOverOrder").WithTags("Warehouse").Produces(204)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).ProducesProblem(429).ProducesProblem(503);

        app.MapGet("/warehouse/drivers", async (HttpContext context, OrderService service, DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var (result, items) = await service.ListDriversAsync(tenantId, actorId, cancellationToken);
            return result == OrderResult.Success ? Results.Ok(items) : ToError(result);
        }).RequireAuthorization()
            .WithName("ListDrivers").WithTags("Warehouse").Produces<List<DriverDto>>(200)
            .ProducesProblem(401).ProducesProblem(403).ProducesProblem(429).ProducesProblem(503);

        // Delivery by the driver (plan step 2.4): only the orders assigned to the driver.
        app.MapGet("/deliveries", async (string? status, int? limit, DateTimeOffset? before, Guid? beforeId, HttpContext context, OrderService service,
            DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var (result, items) = await service.ListForDriverAsync(tenantId, actorId, status, limit, before, beforeId, cancellationToken);
            return result == OrderResult.Success ? Results.Ok(items) : ToError(result);
        }).RequireAuthorization()
            .WithName("ListDeliveries").WithTags("Delivery").Produces<List<OrderDto>>(200)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(429).ProducesProblem(503);

        app.MapGet("/deliveries/{orderId:guid}", async (Guid orderId, HttpContext context, OrderService service, DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var (result, order) = await service.GetForDriverAsync(tenantId, actorId, orderId, cancellationToken);
            return result == OrderResult.Success ? Results.Ok(order) : ToError(result);
        }).RequireAuthorization()
            .WithName("GetDelivery").WithTags("Delivery").Produces<OrderDto>(200)
            .ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(429).ProducesProblem(503);

        foreach (var step in new[] { "pickup", "depart" })
        {
            app.MapPost($"/deliveries/{{orderId:guid}}/{step}", async (Guid orderId, DeliveryStepRequest? request, HttpContext context, OrderService service,
                DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
            {
                if (securityState.IsInsecureDbRole) return Unavailable();
                if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
                var result = await service.DriverStepAsync(tenantId, actorId, orderId, step, request ?? new DeliveryStepRequest(), cancellationToken);
                return result == OrderResult.Success ? Results.NoContent() : ToError(result);
            }).RequireAuthorization()
                .WithName(step == "pickup" ? "PickUpDelivery" : "DepartDelivery").WithTags("Delivery").Produces(204)
                .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).ProducesProblem(422).ProducesProblem(429).ProducesProblem(503);
        }

        app.MapPost("/deliveries/{orderId:guid}/deliver", async (Guid orderId, DeliverRequest request, HttpContext context, OrderService service,
            DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var result = await service.DeliverAsync(tenantId, actorId, orderId, request, cancellationToken);
            return result == OrderResult.Success ? Results.NoContent() : ToError(result);
        }).RequireAuthorization()
            .WithName("DeliverOrder").WithTags("Delivery").Produces(204)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).ProducesProblem(422).ProducesProblem(429).ProducesProblem(503);

        app.MapPost("/deliveries/{orderId:guid}/fail", async (Guid orderId, FailDeliveryRequest request, HttpContext context, OrderService service,
            DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var result = await service.FailDeliveryAsync(tenantId, actorId, orderId, request, cancellationToken);
            return result == OrderResult.Success ? Results.NoContent() : ToError(result);
        }).RequireAuthorization()
            .WithName("FailDelivery").WithTags("Delivery").Produces(204)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).ProducesProblem(422).ProducesProblem(429).ProducesProblem(503);

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
        OrderResult.GpsMocked => ApiErrors.Problem(StatusCodes.Status422UnprocessableEntity, ApiErrors.GpsMocked),
        _ => Results.Forbid()
    };
}
