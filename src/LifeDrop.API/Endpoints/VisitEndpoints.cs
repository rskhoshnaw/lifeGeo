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
    }
}
