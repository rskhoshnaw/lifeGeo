using System.IdentityModel.Tokens.Jwt;
using LifeDrop.Application.Transfers;
using LifeDrop.Domain.Common;

namespace LifeDrop.API.Endpoints;

// Goods moving between warehouses (plan step 5.60): sent by the worker of the sending warehouse (or a manager), confirmed by the worker of the receiving one (or a manager).
public static class TransferEndpoints
{
    public static void MapTransferEndpoints(this WebApplication app)
    {
        app.MapPost("/transfers", async (CreateTransferRequest request, HttpContext context, TransferService service, DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var (result, created) = await service.CreateAsync(tenantId, actorId, request, cancellationToken);
            if (result != TransferResult.Success) return ToError(result);
            return created!.Replayed ? Results.Ok(created) : Results.Created($"/transfers/{created.Id}", created);
        }).RequireAuthorization()
            .WithName("CreateTransfer").WithTags("Transfers").Produces<CreatedTransferDto>(201).Produces<CreatedTransferDto>(200)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).ProducesProblem(429).ProducesProblem(503);

        app.MapGet("/transfers", async (string? status, Guid? warehouseId, int? limit, HttpContext context, TransferService service, DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var (result, items) = await service.ListAsync(tenantId, actorId, status, warehouseId, limit, cancellationToken);
            return result == TransferResult.Success ? Results.Ok(items) : ToError(result);
        }).RequireAuthorization()
            .WithName("ListTransfers").WithTags("Transfers").Produces<List<TransferDto>>(200)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(429).ProducesProblem(503);

        app.MapGet("/transfers/{transferId:guid}", async (Guid transferId, HttpContext context, TransferService service, DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var (result, item) = await service.GetAsync(tenantId, actorId, transferId, cancellationToken);
            return result == TransferResult.Success ? Results.Ok(item) : ToError(result);
        }).RequireAuthorization()
            .WithName("GetTransfer").WithTags("Transfers").Produces<TransferDto>(200)
            .ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(429).ProducesProblem(503);

        app.MapPost("/transfers/{transferId:guid}/receive", async (Guid transferId, ReceiveTransferRequest request, HttpContext context, TransferService service, DatabaseSecurityState securityState,
            CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var result = await service.ReceiveAsync(tenantId, actorId, transferId, request, cancellationToken);
            return result == TransferResult.Success ? Results.NoContent() : ToError(result);
        }).RequireAuthorization()
            .WithName("ReceiveTransfer").WithTags("Transfers").Produces(204)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).ProducesProblem(429).ProducesProblem(503);

        app.MapPost("/transfers/{transferId:guid}/cancel", async (Guid transferId, CancelTransferRequest request, HttpContext context, TransferService service, DatabaseSecurityState securityState,
            CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var result = await service.CancelAsync(tenantId, actorId, transferId, request, cancellationToken);
            return result == TransferResult.Success ? Results.NoContent() : ToError(result);
        }).RequireAuthorization()
            .WithName("CancelTransfer").WithTags("Transfers").Produces(204)
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

    private static IResult ToError(TransferResult result) => result switch
    {
        TransferResult.InvalidData => ApiErrors.Problem(StatusCodes.Status400BadRequest, ApiErrors.InvalidData),
        TransferResult.NotFound => ApiErrors.Problem(StatusCodes.Status404NotFound, ApiErrors.NotFound),
        TransferResult.Conflict => ApiErrors.Problem(StatusCodes.Status409Conflict, ApiErrors.Conflict),
        TransferResult.TooMuch => ApiErrors.Problem(StatusCodes.Status409Conflict, ApiErrors.TransferTooMuch),
        _ => Results.Forbid()
    };
}
