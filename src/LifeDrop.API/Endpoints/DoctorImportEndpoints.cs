using System.IdentityModel.Tokens.Jwt;
using LifeDrop.Application.Customers;
using LifeDrop.Domain.Common;

namespace LifeDrop.API.Endpoints;

// Import of a doctor list (plan step 5.9, docs/doctors-import.md).
public static class DoctorImportEndpoints
{
    public static void MapDoctorImportEndpoints(this WebApplication app)
    {
        app.MapPost("/customers/import-doctors", async (ImportDoctorsRequest request, HttpContext context, DoctorImport service, DatabaseSecurityState securityState,
            CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return ApiErrors.Problem(StatusCodes.Status503ServiceUnavailable, ApiErrors.ServiceUnavailable);
            var subject = context.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
            var tenant = context.User.FindFirst("tenant_id")?.Value;
            if (!Guid.TryParse(subject, out var actor) || actor == Guid.Empty || !Guid.TryParse(tenant, out var tenantGuid) || tenantGuid == Guid.Empty) return Results.Forbid();
            var (outcome, result) = await service.ImportAsync(new TenantId(tenantGuid), new UserId(actor), request, cancellationToken);
            return outcome switch
            {
                DoctorImportOutcome.Success => Results.Ok(result),
                DoctorImportOutcome.InvalidData => ApiErrors.Problem(StatusCodes.Status400BadRequest, ApiErrors.InvalidData),
                _ => Results.Forbid()
            };
        }).RequireAuthorization()
            .WithName("ImportDoctors").WithTags("Customers").Produces<ImportDoctorsResult>(200)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(429).ProducesProblem(503);
    }
}
