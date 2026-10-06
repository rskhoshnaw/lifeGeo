using System.IdentityModel.Tokens.Jwt;
using LifeDrop.Application.Reports;
using LifeDrop.Domain.Common;

namespace LifeDrop.API.Endpoints;

// Reports and KPIs (plan step 2.12): reports.read, only the users inside the reader's area; definitions in docs/KPI.md.
public static class ReportEndpoints
{
    public static void MapReportEndpoints(this WebApplication app)
    {
        app.MapGet("/reports/kpis", async (DateOnly from, DateOnly to, Guid? regionId, Guid? teamId, string? customerType, HttpContext context, KpiReport report,
            DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return ApiErrors.Problem(StatusCodes.Status503ServiceUnavailable, ApiErrors.ServiceUnavailable);
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var (result, kpis) = await report.GetAsync(tenantId, actorId, new KpiFilter(from, to, regionId, teamId, customerType), cancellationToken);
            return result switch
            {
                ReportResult.Success => Results.Ok(kpis),
                ReportResult.InvalidData => ApiErrors.Problem(StatusCodes.Status400BadRequest, ApiErrors.InvalidData),
                _ => Results.Forbid()
            };
        }).RequireAuthorization()
            .WithName("GetKpiReport").WithTags("Reports").Produces<KpiReportDto>(200)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(429).ProducesProblem(503);
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
}
