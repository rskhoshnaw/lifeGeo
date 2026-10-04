using System.IdentityModel.Tokens.Jwt;
using LifeDrop.Application.Audit;
using LifeDrop.Domain.Common;

namespace LifeDrop.API.Endpoints;

// Read-only audit search (plan step 0.8). There is deliberately no update or delete route.
public static class AuditEndpoints
{
    public static void MapAuditEndpoints(this WebApplication app)
    {
        app.MapGet("/audit-logs", async (string? action, Guid? actorId, string? entityId, DateTimeOffset? from, DateTimeOffset? to,
            int? limit, DateTimeOffset? cursorOccurredAt, Guid? cursorId, HttpContext context, SearchAuditLogs search,
            DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole)
                return ApiErrors.Problem(StatusCodes.Status503ServiceUnavailable, ApiErrors.ServiceUnavailable);
            var subject = context.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
            var tenant = context.User.FindFirst("tenant_id")?.Value;
            if (!Guid.TryParse(subject, out var actor) || actor == Guid.Empty ||
                !Guid.TryParse(tenant, out var tenantId) || tenantId == Guid.Empty)
                return Results.Forbid();

            var query = new AuditLogQuery(action, actorId, entityId, from, to, limit, cursorOccurredAt, cursorId);
            var (result, items) = await search.ExecuteAsync(new TenantId(tenantId), new UserId(actor), query, cancellationToken);
            return result switch
            {
                AuditSearchResult.Success => Results.Ok(items),
                AuditSearchResult.InvalidData => ApiErrors.Problem(StatusCodes.Status400BadRequest, ApiErrors.InvalidData),
                _ => Results.Forbid()
            };
        }).RequireAuthorization()
            .WithName("SearchAuditLogs").WithTags("Audit").Produces<List<AuditLogEntryDto>>(200)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(429).ProducesProblem(503);
    }
}
