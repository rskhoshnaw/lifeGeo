using System.IdentityModel.Tokens.Jwt;
using LifeDrop.Application.Returns;
using LifeDrop.Domain.Common;

namespace LifeDrop.API.Endpoints;

// Customer returns (plan step 5.54): written by the field (returns.create), decided by the sales manager (returns.approve), read inside the data scope of returns.read.
public static class ReturnEndpoints
{
    public static void MapReturnEndpoints(this WebApplication app)
    {
        app.MapPost("/returns", async (CreateReturnRequest request, HttpContext context, ReturnService service, DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var (result, created) = await service.CreateAsync(tenantId, actorId, request, cancellationToken);
            if (result != ReturnResult.Success) return ToError(result);
            return created!.Replayed ? Results.Ok(created) : Results.Created($"/returns/{created.Id}", created);
        }).RequireAuthorization()
            .WithName("CreateReturn").WithTags("Returns").Produces<CreatedReturnDto>(201).Produces<CreatedReturnDto>(200)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).ProducesProblem(429).ProducesProblem(503);

        app.MapGet("/returns", async (Guid? customerId, string? status, bool? mine, int? limit, DateTimeOffset? before, Guid? beforeId, HttpContext context, ReturnService service,
            DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var (result, items) = await service.ListAsync(tenantId, actorId, customerId, status, mine == true, limit, before, beforeId, cancellationToken);
            return result == ReturnResult.Success ? Results.Ok(items) : ToError(result);
        }).RequireAuthorization()
            .WithName("ListReturns").WithTags("Returns").Produces<List<ReturnDto>>(200)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(429).ProducesProblem(503);

        app.MapGet("/returns/damaged", async (DateOnly? from, DateOnly? to, HttpContext context, ReturnService service, DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var (result, items) = await service.DamagedAsync(tenantId, actorId, from, to, cancellationToken);
            return result == ReturnResult.Success ? Results.Ok(items) : ToError(result);
        }).RequireAuthorization()
            .WithName("ListDamagedGoods").WithTags("Returns").Produces<List<DamagedGoodsDto>>(200)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(429).ProducesProblem(503);

        app.MapGet("/returns/{returnId:guid}", async (Guid returnId, HttpContext context, ReturnService service, DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var (result, found) = await service.GetAsync(tenantId, actorId, returnId, cancellationToken);
            return result == ReturnResult.Success ? Results.Ok(found) : ToError(result);
        }).RequireAuthorization()
            .WithName("GetReturn").WithTags("Returns").Produces<ReturnDto>(200)
            .ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(429).ProducesProblem(503);

        app.MapPost("/returns/{returnId:guid}/approve", async (Guid returnId, DecideReturnRequest request, HttpContext context, ReturnService service, DatabaseSecurityState securityState,
            CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var result = await service.ApproveAsync(tenantId, actorId, returnId, request, cancellationToken);
            return result == ReturnResult.Success ? Results.NoContent() : ToError(result);
        }).RequireAuthorization()
            .WithName("ApproveReturn").WithTags("Returns").Produces(204)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).ProducesProblem(429).ProducesProblem(503);

        app.MapPost("/returns/{returnId:guid}/reject", async (Guid returnId, DecideReturnRequest request, HttpContext context, ReturnService service, DatabaseSecurityState securityState,
            CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var result = await service.RejectAsync(tenantId, actorId, returnId, request, cancellationToken);
            return result == ReturnResult.Success ? Results.NoContent() : ToError(result);
        }).RequireAuthorization()
            .WithName("RejectReturn").WithTags("Returns").Produces(204)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).ProducesProblem(429).ProducesProblem(503);

        app.MapPost("/returns/{returnId:guid}/cancel", async (Guid returnId, DecideReturnRequest request, HttpContext context, ReturnService service, DatabaseSecurityState securityState,
            CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var result = await service.CancelAsync(tenantId, actorId, returnId, request, cancellationToken);
            return result == ReturnResult.Success ? Results.NoContent() : ToError(result);
        }).RequireAuthorization()
            .WithName("CancelReturn").WithTags("Returns").Produces(204)
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

    private static IResult ToError(ReturnResult result) => result switch
    {
        ReturnResult.InvalidData => ApiErrors.Problem(StatusCodes.Status400BadRequest, ApiErrors.InvalidData),
        ReturnResult.NotFound => ApiErrors.Problem(StatusCodes.Status404NotFound, ApiErrors.NotFound),
        ReturnResult.Conflict => ApiErrors.Problem(StatusCodes.Status409Conflict, ApiErrors.Conflict),
        ReturnResult.TooMuch => ApiErrors.Problem(StatusCodes.Status409Conflict, ApiErrors.ReturnTooMuch),
        ReturnResult.CurrencyMismatch => ApiErrors.Problem(StatusCodes.Status409Conflict, ApiErrors.CurrencyMismatch),
        _ => Results.Forbid()
    };
}
