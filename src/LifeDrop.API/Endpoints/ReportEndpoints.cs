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

        // A user's own KPIs for a month with the month before, the same month last year and 12 months of trend (plan step 5.29). Own numbers need no permission; another user's need reports.read.
        app.MapGet("/reports/kpi-compare", async (DateOnly month, Guid? userId, HttpContext context, KpiCompare compare,
            DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return ApiErrors.Problem(StatusCodes.Status503ServiceUnavailable, ApiErrors.ServiceUnavailable);
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var (result, data) = await compare.GetAsync(tenantId, actorId, userId, month, cancellationToken);
            return result switch
            {
                ReportResult.Success => Results.Ok(data),
                ReportResult.InvalidData => ApiErrors.Problem(StatusCodes.Status400BadRequest, ApiErrors.InvalidData),
                ReportResult.NotFound => ApiErrors.Problem(StatusCodes.Status404NotFound, ApiErrors.UserNotFound),
                _ => Results.Forbid()
            };
        }).RequireAuthorization()
            .WithName("GetKpiCompare").WithTags("Reports").Produces<KpiCompareDto>(200)
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
}
