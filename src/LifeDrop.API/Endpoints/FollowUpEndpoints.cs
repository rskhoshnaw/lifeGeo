using System.IdentityModel.Tokens.Jwt;
using LifeDrop.Application.FollowUps;
using LifeDrop.Domain.Common;

namespace LifeDrop.API.Endpoints;

// Follow-up (plan step 2.8): a reminder for a customer with a due day and one responsible user.
public static class FollowUpEndpoints
{
    public static void MapFollowUpEndpoints(this WebApplication app)
    {
        app.MapPost("/followups", async (CreateFollowUpRequest request, HttpContext context, FollowUpService service, DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var (result, item) = await service.CreateAsync(tenantId, actorId, request, cancellationToken);
            if (result != FollowUpResult.Success) return ToError(result);
            return item!.Replayed ? Results.Ok(item) : Results.Created($"/followups/{item.Id}", item);
        }).RequireAuthorization()
            .WithName("CreateFollowUp").WithTags("FollowUps").Produces<CreatedFollowUpDto>(201).Produces<CreatedFollowUpDto>(200)
            .ProducesProblem(401).ProducesProblem(403).ProducesProblem(400).ProducesProblem(404).ProducesProblem(409).ProducesProblem(429).ProducesProblem(503);

        app.MapGet("/followups", async (Guid? customerId, Guid? assigneeId, string? status, bool? overdue, int? limit, HttpContext context, FollowUpService service, DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var (result, items) = await service.ListAsync(tenantId, actorId, customerId, assigneeId, status, overdue, limit, cancellationToken);
            return result == FollowUpResult.Success ? Results.Ok(items) : ToError(result);
        }).RequireAuthorization()
            .WithName("ListFollowUps").WithTags("FollowUps").Produces<List<FollowUpDto>>(200)
            .ProducesProblem(401).ProducesProblem(403).ProducesProblem(400).ProducesProblem(429).ProducesProblem(503);

        app.MapGet("/followups/{followUpId:guid}", async (Guid followUpId, HttpContext context, FollowUpService service, DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var (result, item) = await service.GetAsync(tenantId, actorId, followUpId, cancellationToken);
            return result == FollowUpResult.Success ? Results.Ok(item) : ToError(result);
        }).RequireAuthorization()
            .WithName("GetFollowUp").WithTags("FollowUps").Produces<FollowUpDto>(200)
            .ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(429).ProducesProblem(503);

        app.MapPost("/followups/{followUpId:guid}/start", async (Guid followUpId, HttpContext context, FollowUpService service, DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var result = await service.StartAsync(tenantId, actorId, followUpId, cancellationToken);
            return result == FollowUpResult.Success ? Results.NoContent() : ToError(result);
        }).RequireAuthorization()
            .WithName("StartFollowUp").WithTags("FollowUps").Produces(204)
            .ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).ProducesProblem(429).ProducesProblem(503);

        app.MapPost("/followups/{followUpId:guid}/complete", async (Guid followUpId, CompleteFollowUpRequest request, HttpContext context, FollowUpService service, DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var result = await service.CompleteAsync(tenantId, actorId, followUpId, request, cancellationToken);
            return result == FollowUpResult.Success ? Results.NoContent() : ToError(result);
        }).RequireAuthorization()
            .WithName("CompleteFollowUp").WithTags("FollowUps").Produces(204)
            .ProducesProblem(401).ProducesProblem(403).ProducesProblem(400).ProducesProblem(404).ProducesProblem(409).ProducesProblem(429).ProducesProblem(503);

        app.MapPost("/followups/{followUpId:guid}/cancel", async (Guid followUpId, CancelFollowUpRequest request, HttpContext context, FollowUpService service, DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var result = await service.CancelAsync(tenantId, actorId, followUpId, request, cancellationToken);
            return result == FollowUpResult.Success ? Results.NoContent() : ToError(result);
        }).RequireAuthorization()
            .WithName("CancelFollowUp").WithTags("FollowUps").Produces(204)
            .ProducesProblem(401).ProducesProblem(403).ProducesProblem(400).ProducesProblem(404).ProducesProblem(409).ProducesProblem(429).ProducesProblem(503);

        app.MapPost("/followups/{followUpId:guid}/reassign", async (Guid followUpId, ReassignFollowUpRequest request, HttpContext context, FollowUpService service, DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var result = await service.ReassignAsync(tenantId, actorId, followUpId, request, cancellationToken);
            return result == FollowUpResult.Success ? Results.NoContent() : ToError(result);
        }).RequireAuthorization()
            .WithName("ReassignFollowUp").WithTags("FollowUps").Produces(204)
            .ProducesProblem(401).ProducesProblem(403).ProducesProblem(400).ProducesProblem(404).ProducesProblem(409).ProducesProblem(429).ProducesProblem(503);
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

    private static IResult ToError(FollowUpResult result) => result switch
    {
        FollowUpResult.InvalidData => ApiErrors.Problem(StatusCodes.Status400BadRequest, ApiErrors.InvalidData),
        FollowUpResult.NotFound => ApiErrors.Problem(StatusCodes.Status404NotFound, ApiErrors.NotFound),
        FollowUpResult.Conflict => ApiErrors.Problem(StatusCodes.Status409Conflict, ApiErrors.Conflict),
        _ => Results.Forbid()
    };
}
