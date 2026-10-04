using System.IdentityModel.Tokens.Jwt;
using LifeDrop.Application.Visits;
using LifeDrop.Domain.Common;

namespace LifeDrop.API.Endpoints;

public static class VisitEndpoints
{
    public static void MapVisitEndpoints(this WebApplication app)
    {
        // Unplanned visit for the caller, today (plan step 1.2, PRD §5.1).
        app.MapPost("/visits/unplanned", async (CreateUnplannedVisitRequest request, HttpContext context,
            CreateUnplannedVisit createUnplanned, DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole)
                return ApiErrors.Problem(StatusCodes.Status503ServiceUnavailable, ApiErrors.ServiceUnavailable);
            var subject = context.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
            var tenant = context.User.FindFirst("tenant_id")?.Value;
            if (!Guid.TryParse(subject, out var actorId) || actorId == Guid.Empty ||
                !Guid.TryParse(tenant, out var tenantId) || tenantId == Guid.Empty)
                return Results.Forbid();

            var (result, visitId) = await createUnplanned.ExecuteAsync(new TenantId(tenantId), new UserId(actorId), request, cancellationToken);
            return result switch
            {
                CreateVisitResult.Created => Results.Created($"/visits/{visitId}", new CreatedResponse(visitId!.Value)),
                CreateVisitResult.InvalidData => ApiErrors.Problem(StatusCodes.Status400BadRequest, ApiErrors.InvalidData),
                CreateVisitResult.CustomerNotFound => ApiErrors.Problem(StatusCodes.Status404NotFound, ApiErrors.CustomerNotFound),
                CreateVisitResult.UserNotFound => ApiErrors.Problem(StatusCodes.Status404NotFound, ApiErrors.UserNotFound),
                _ => Results.Forbid()
            };
        }).RequireAuthorization()
            .WithName("CreateUnplannedVisit").WithTags("Visits").Produces<CreatedResponse>(201)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(429).ProducesProblem(503);

        // Manager actions (plan step 1.4).
        app.MapPut("/visits/{visitId:guid}/assignee", async (Guid visitId, ReassignVisitRequest request, HttpContext context,
            ManageVisit manage, DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole)
                return ApiErrors.Problem(StatusCodes.Status503ServiceUnavailable, ApiErrors.ServiceUnavailable);
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            return ToResult(await manage.ReassignAsync(tenantId, actorId, visitId, request, cancellationToken));
        }).RequireAuthorization()
            .WithName("ReassignVisit").WithTags("Visits").Produces(204)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).ProducesProblem(429).ProducesProblem(503);

        app.MapPost("/visits/{visitId:guid}/cancel", async (Guid visitId, CancelVisitRequest request, HttpContext context,
            ManageVisit manage, DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole)
                return ApiErrors.Problem(StatusCodes.Status503ServiceUnavailable, ApiErrors.ServiceUnavailable);
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            return ToResult(await manage.CancelAsync(tenantId, actorId, visitId, request, cancellationToken));
        }).RequireAuthorization()
            .WithName("CancelVisit").WithTags("Visits").Produces(204)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).ProducesProblem(429).ProducesProblem(503);

        // Visit report (plan step 1.6).
        app.MapPut("/visits/{visitId:guid}/report", async (Guid visitId, SaveVisitReportRequest request, HttpContext context,
            VisitReports reports, DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole)
                return ApiErrors.Problem(StatusCodes.Status503ServiceUnavailable, ApiErrors.ServiceUnavailable);
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            return ToResult(await reports.SaveAsync(tenantId, actorId, visitId, request, cancellationToken));
        }).RequireAuthorization()
            .WithName("SaveVisitReport").WithTags("Visits").Produces(204)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).ProducesProblem(429).ProducesProblem(503);

        app.MapGet("/visits/{visitId:guid}/report", async (Guid visitId, HttpContext context,
            VisitReports reports, DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole)
                return ApiErrors.Problem(StatusCodes.Status503ServiceUnavailable, ApiErrors.ServiceUnavailable);
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var (result, report) = await reports.GetAsync(tenantId, actorId, visitId, cancellationToken);
            return result == ManageVisitResult.Success ? Results.Ok(report) : ToResult(result);
        }).RequireAuthorization()
            .WithName("GetVisitReport").WithTags("Visits").Produces<VisitReportDto>(200)
            .ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(429).ProducesProblem(503);
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

    private static IResult ToResult(ManageVisitResult result) => result switch
    {
        ManageVisitResult.Success => Results.NoContent(),
        ManageVisitResult.NotFound => ApiErrors.Problem(StatusCodes.Status404NotFound, ApiErrors.NotFound),
        ManageVisitResult.UserNotFound => ApiErrors.Problem(StatusCodes.Status404NotFound, ApiErrors.UserNotFound),
        ManageVisitResult.InvalidState => ApiErrors.Problem(StatusCodes.Status400BadRequest, ApiErrors.InvalidState),
        ManageVisitResult.InvalidData => ApiErrors.Problem(StatusCodes.Status400BadRequest, ApiErrors.InvalidData),
        ManageVisitResult.Conflict => ApiErrors.Problem(StatusCodes.Status409Conflict, ApiErrors.ConcurrencyConflict),
        _ => Results.Forbid()
    };
}
