using System.IdentityModel.Tokens.Jwt;
using LifeDrop.Application.ClientErrors;
using LifeDrop.Domain.Common;

namespace LifeDrop.API.Endpoints;

// Connection errors of the mobile app (plan step 5.10). POST is open (the error happened before sign-in) but rate limited, limited to 20 items, and stores only cleaned text for an existing company;
// GET needs client_errors.read.
public static class ClientErrorEndpoints
{
    public static void MapClientErrorEndpoints(this WebApplication app)
    {
        app.MapPost("/client-errors", async (ReportClientErrorsRequest request, ClientErrorReports service, DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return ApiErrors.Problem(StatusCodes.Status503ServiceUnavailable, ApiErrors.ServiceUnavailable);
            var (result, stored) = await service.ReportAsync(request, cancellationToken);
            return result switch
            {
                ClientErrorResult.Success => Results.Ok(new ClientErrorsStoredResponse(stored)),
                ClientErrorResult.NotFound => ApiErrors.Problem(StatusCodes.Status404NotFound, ApiErrors.NotFound),
                _ => ApiErrors.Problem(StatusCodes.Status400BadRequest, ApiErrors.InvalidData)
            };
        }).AllowAnonymous().RequireRateLimiting("login")
            .WithName("ReportClientErrors").WithTags("Audit").Produces<ClientErrorsStoredResponse>(200)
            .ProducesProblem(400).ProducesProblem(404).ProducesProblem(429).ProducesProblem(503);

        app.MapGet("/client-errors", async (string? stage, DateTimeOffset? from, DateTimeOffset? to, int? limit, DateTimeOffset? cursorReportedAt, Guid? cursorId,
            HttpContext context, ClientErrorReports service, DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return ApiErrors.Problem(StatusCodes.Status503ServiceUnavailable, ApiErrors.ServiceUnavailable);
            var subject = context.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
            var tenant = context.User.FindFirst("tenant_id")?.Value;
            if (!Guid.TryParse(subject, out var actor) || actor == Guid.Empty || !Guid.TryParse(tenant, out var tenantId) || tenantId == Guid.Empty) return Results.Forbid();
            var (result, items) = await service.ListAsync(new TenantId(tenantId), new UserId(actor), new ClientErrorQuery(stage, from, to, limit, cursorReportedAt, cursorId), cancellationToken);
            return result switch
            {
                ClientErrorResult.Success => Results.Ok(items),
                ClientErrorResult.InvalidData => ApiErrors.Problem(StatusCodes.Status400BadRequest, ApiErrors.InvalidData),
                _ => Results.Forbid()
            };
        }).RequireAuthorization()
            .WithName("ListClientErrors").WithTags("Audit").Produces<List<ClientErrorDto>>(200)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(429).ProducesProblem(503);
    }

    public sealed record ClientErrorsStoredResponse(int Stored);
}
