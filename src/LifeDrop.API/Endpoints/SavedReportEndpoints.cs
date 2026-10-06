using System.IdentityModel.Tokens.Jwt;
using LifeDrop.Application.Reports;
using LifeDrop.Domain.Common;

namespace LifeDrop.API.Endpoints;

// Saved and scheduled reports (plan step 3.2, part 1): reports.read; everybody works only with their own reports.
public static class SavedReportEndpoints
{
    public static void MapSavedReportEndpoints(this WebApplication app)
    {
        app.MapGet("/saved-reports", async (HttpContext context, SavedReportService service, DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var (result, items) = await service.ListAsync(tenantId, actorId, cancellationToken);
            return result == SavedReportResult.Success ? Results.Ok(items) : ToError(result);
        }).RequireAuthorization()
            .WithName("ListSavedReports").WithTags("Saved reports").Produces<List<SavedReportDto>>(200)
            .ProducesProblem(401).ProducesProblem(403).ProducesProblem(429).ProducesProblem(503);

        app.MapPost("/saved-reports", async (SaveReportRequest request, HttpContext context, SavedReportService service, DatabaseSecurityState securityState,
            CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var (result, id) = await service.CreateAsync(tenantId, actorId, request, cancellationToken);
            return result == SavedReportResult.Success ? Results.Created($"/saved-reports/{id}", new CreatedResponse(id!.Value)) : ToError(result);
        }).RequireAuthorization()
            .WithName("CreateSavedReport").WithTags("Saved reports").Produces<CreatedResponse>(201)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(409).ProducesProblem(429).ProducesProblem(503);

        app.MapPut("/saved-reports/{reportId:guid}", async (Guid reportId, SaveReportRequest request, HttpContext context, SavedReportService service,
            DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var result = await service.UpdateAsync(tenantId, actorId, reportId, request, cancellationToken);
            return result == SavedReportResult.Success ? Results.NoContent() : ToError(result);
        }).RequireAuthorization()
            .WithName("UpdateSavedReport").WithTags("Saved reports").Produces(204)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).ProducesProblem(429).ProducesProblem(503);

        app.MapDelete("/saved-reports/{reportId:guid}", async (Guid reportId, HttpContext context, SavedReportService service, DatabaseSecurityState securityState,
            CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var result = await service.DeleteAsync(tenantId, actorId, reportId, cancellationToken);
            return result == SavedReportResult.Success ? Results.NoContent() : ToError(result);
        }).RequireAuthorization()
            .WithName("DeleteSavedReport").WithTags("Saved reports").Produces(204)
            .ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(429).ProducesProblem(503);

        app.MapPost("/saved-reports/{reportId:guid}/run", async (Guid reportId, HttpContext context, SavedReportService service, DatabaseSecurityState securityState,
            CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var (result, run) = await service.RunNowAsync(tenantId, actorId, reportId, cancellationToken);
            return result == SavedReportResult.Success ? Results.Ok(run) : ToError(result);
        }).RequireAuthorization()
            .WithName("RunSavedReport").WithTags("Saved reports").Produces<ReportRunDetailDto>(200)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(429).ProducesProblem(503);

        app.MapGet("/saved-reports/{reportId:guid}/runs", async (Guid reportId, int? limit, HttpContext context, SavedReportService service, DatabaseSecurityState securityState,
            CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var (result, items) = await service.ListRunsAsync(tenantId, actorId, reportId, limit, cancellationToken);
            return result == SavedReportResult.Success ? Results.Ok(items) : ToError(result);
        }).RequireAuthorization()
            .WithName("ListSavedReportRuns").WithTags("Saved reports").Produces<List<ReportRunDto>>(200)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(429).ProducesProblem(503);

        app.MapGet("/saved-reports/{reportId:guid}/runs/{runId:guid}", async (Guid reportId, Guid runId, HttpContext context, SavedReportService service,
            DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole) return Unavailable();
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var (result, run) = await service.GetRunAsync(tenantId, actorId, reportId, runId, cancellationToken);
            return result == SavedReportResult.Success ? Results.Ok(run) : ToError(result);
        }).RequireAuthorization()
            .WithName("GetSavedReportRun").WithTags("Saved reports").Produces<ReportRunDetailDto>(200)
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

    private static IResult Unavailable() => ApiErrors.Problem(StatusCodes.Status503ServiceUnavailable, ApiErrors.ServiceUnavailable);

    private static IResult ToError(SavedReportResult result) => result switch
    {
        SavedReportResult.InvalidData => ApiErrors.Problem(StatusCodes.Status400BadRequest, ApiErrors.InvalidData),
        SavedReportResult.NotFound => ApiErrors.Problem(StatusCodes.Status404NotFound, ApiErrors.NotFound),
        SavedReportResult.Conflict => ApiErrors.Problem(StatusCodes.Status409Conflict, ApiErrors.Conflict),
        _ => Results.Forbid()
    };
}
