using System.IdentityModel.Tokens.Jwt;
using LifeDrop.Application.Visits;
using LifeDrop.Domain.Common;

namespace LifeDrop.API.Endpoints;

// Plan templates (plan step 5.8): save a week of visits as a pattern and apply it to the coming weeks.
public static class PlanTemplateEndpoints
{
    public static void MapPlanTemplateEndpoints(this WebApplication app)
    {
        app.MapPost("/visit-plan/templates", async (SavePlanTemplateRequest request, HttpContext context, PlanTemplates service, DatabaseSecurityState securityState,
            CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var (result, id) = await service.SaveAsync(tenantId, actorId, request, cancellationToken);
            return result == PlanTemplateResult.Success ? Results.Created($"/visit-plan/templates/{id}", new CreatedResponse(id)) : ToError(result);
        }).RequireAuthorization()
            .WithName("SavePlanTemplate").WithTags("Visits").Produces<CreatedResponse>(201)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(429).ProducesProblem(503);

        app.MapGet("/visit-plan/templates", async (Guid? assignedUserId, HttpContext context, PlanTemplates service, DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var (result, items) = await service.ListAsync(tenantId, actorId, assignedUserId, cancellationToken);
            return result == PlanTemplateResult.Success ? Results.Ok(items) : ToError(result);
        }).RequireAuthorization()
            .WithName("ListPlanTemplates").WithTags("Visits").Produces<List<PlanTemplateDto>>(200)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(429).ProducesProblem(503);

        app.MapDelete("/visit-plan/templates/{id:guid}", async (Guid id, HttpContext context, PlanTemplates service, DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var result = await service.DeleteAsync(tenantId, actorId, id, cancellationToken);
            return result == PlanTemplateResult.Success ? Results.NoContent() : ToError(result);
        }).RequireAuthorization()
            .WithName("DeletePlanTemplate").WithTags("Visits").Produces(204)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(429).ProducesProblem(503);

        app.MapPost("/visit-plan/templates/{id:guid}/apply", async (Guid id, ApplyPlanTemplateRequest request, HttpContext context, PlanTemplates service,
            DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var (result, applied) = await service.ApplyAsync(tenantId, actorId, id, request, cancellationToken);
            return result == PlanTemplateResult.Success ? Results.Ok(applied) : ToError(result);
        }).RequireAuthorization()
            .WithName("ApplyPlanTemplate").WithTags("Visits").Produces<PlanTemplateAppliedDto>(200)
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

    private static IResult ToError(PlanTemplateResult result) => result switch
    {
        PlanTemplateResult.InvalidData => ApiErrors.Problem(StatusCodes.Status400BadRequest, ApiErrors.InvalidData),
        PlanTemplateResult.NotFound => ApiErrors.Problem(StatusCodes.Status404NotFound, ApiErrors.NotFound),
        _ => Results.Forbid()
    };
}
