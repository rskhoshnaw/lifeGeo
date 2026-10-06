using System.IdentityModel.Tokens.Jwt;
using LifeDrop.Application.Payments;
using LifeDrop.Domain.Common;

namespace LifeDrop.API.Endpoints;

// Payments (plan step 2.5): recorded against a customer (optionally one of its orders). A visitor's payment is "collected" until the finance manager confirms it.
public static class PaymentEndpoints
{
    public static void MapPaymentEndpoints(this WebApplication app)
    {
        app.MapPost("/payments", async (RecordPaymentRequest request, HttpContext context, PaymentService service, DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var (result, payment) = await service.RecordAsync(tenantId, actorId, request, cancellationToken);
            if (result != PaymentResult.Success) return ToError(result);
            return payment!.Replayed ? Results.Ok(payment) : Results.Created($"/payments/{payment.Id}", payment);
        }).RequireAuthorization()
            .WithName("RecordPayment").WithTags("Payments").Produces<CreatedPaymentDto>(201).Produces<CreatedPaymentDto>(200)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).ProducesProblem(429).ProducesProblem(503);

        app.MapGet("/payments", async (Guid? customerId, string? status, int? limit, DateTimeOffset? before, Guid? beforeId, HttpContext context, PaymentService service,
            DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var (result, items) = await service.ListAsync(tenantId, actorId, customerId, status, limit, before, beforeId, cancellationToken);
            return result == PaymentResult.Success ? Results.Ok(items) : ToError(result);
        }).RequireAuthorization()
            .WithName("ListPayments").WithTags("Payments").Produces<List<PaymentDto>>(200)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(429).ProducesProblem(503);

        app.MapGet("/payments/mine", async (string? status, int? limit, DateTimeOffset? before, Guid? beforeId, HttpContext context, PaymentService service,
            DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var (result, items) = await service.ListMineAsync(tenantId, actorId, status, limit, before, beforeId, cancellationToken);
            return result == PaymentResult.Success ? Results.Ok(items) : ToError(result);
        }).RequireAuthorization()
            .WithName("ListMyPayments").WithTags("Payments").Produces<List<PaymentDto>>(200)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(429).ProducesProblem(503);

        app.MapGet("/payments/{paymentId:guid}", async (Guid paymentId, HttpContext context, PaymentService service, DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var (result, payment) = await service.GetAsync(tenantId, actorId, paymentId, cancellationToken);
            return result == PaymentResult.Success ? Results.Ok(payment) : ToError(result);
        }).RequireAuthorization()
            .WithName("GetPayment").WithTags("Payments").Produces<PaymentDto>(200)
            .ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(429).ProducesProblem(503);

        app.MapPost("/payments/{paymentId:guid}/confirm", async (Guid paymentId, HttpContext context, PaymentService service, DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var result = await service.ConfirmAsync(tenantId, actorId, paymentId, cancellationToken);
            return result == PaymentResult.Success ? Results.NoContent() : ToError(result);
        }).RequireAuthorization()
            .WithName("ConfirmPayment").WithTags("Payments").Produces(204)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).ProducesProblem(429).ProducesProblem(503);

        app.MapPost("/payments/{paymentId:guid}/void", async (Guid paymentId, ClosePaymentRequest request, HttpContext context, PaymentService service,
            DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var result = await service.VoidAsync(tenantId, actorId, paymentId, request, cancellationToken);
            return result == PaymentResult.Success ? Results.NoContent() : ToError(result);
        }).RequireAuthorization()
            .WithName("VoidPayment").WithTags("Payments").Produces(204)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).ProducesProblem(429).ProducesProblem(503);

        app.MapPost("/payments/{paymentId:guid}/correct", async (Guid paymentId, CorrectPaymentRequest request, HttpContext context, PaymentService service,
            DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var (result, payment) = await service.CorrectAsync(tenantId, actorId, paymentId, request, cancellationToken);
            return result == PaymentResult.Success ? Results.Created($"/payments/{payment!.Id}", payment) : ToError(result);
        }).RequireAuthorization()
            .WithName("CorrectPayment").WithTags("Payments").Produces<CreatedPaymentDto>(201)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).ProducesProblem(429).ProducesProblem(503);

        app.MapGet("/customers/{customerId:guid}/balance", async (Guid customerId, HttpContext context, PaymentService service, DatabaseSecurityState securityState,
            CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var (result, balance) = await service.BalanceAsync(tenantId, actorId, customerId, cancellationToken);
            return result == PaymentResult.Success ? Results.Ok(balance) : ToError(result);
        }).RequireAuthorization()
            .WithName("GetCustomerBalance").WithTags("Payments").Produces<CustomerBalanceDto>(200)
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

    private static IResult ToError(PaymentResult result) => result switch
    {
        PaymentResult.InvalidData => ApiErrors.Problem(StatusCodes.Status400BadRequest, ApiErrors.InvalidData),
        PaymentResult.NotFound => ApiErrors.Problem(StatusCodes.Status404NotFound, ApiErrors.NotFound),
        PaymentResult.Conflict => ApiErrors.Problem(StatusCodes.Status409Conflict, ApiErrors.Conflict),
        _ => Results.Forbid()
    };
}
