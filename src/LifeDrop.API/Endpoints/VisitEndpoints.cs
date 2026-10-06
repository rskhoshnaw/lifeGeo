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
                CreateVisitResult.CustomerNotApproved => ApiErrors.Problem(StatusCodes.Status409Conflict, ApiErrors.CustomerNotApproved),
                CreateVisitResult.UserNotFound => ApiErrors.Problem(StatusCodes.Status404NotFound, ApiErrors.UserNotFound),
                _ => Results.Forbid()
            };
        }).RequireAuthorization()
            .WithName("CreateUnplannedVisit").WithTags("Visits").Produces<CreatedResponse>(201)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(429).ProducesProblem(503);

        // Planning board for the panel (plan step 1.18): one day's visits within the caller's visits.write scope.
        app.MapGet("/visit-plan", async (DateOnly? scheduleDate, Guid? assignedUserId, HttpContext context, VisitPlanBoard board,
            DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole)
                return ApiErrors.Problem(StatusCodes.Status503ServiceUnavailable, ApiErrors.ServiceUnavailable);
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var items = await board.ListAsync(tenantId, actorId, scheduleDate, assignedUserId, cancellationToken);
            return items is null ? Results.Forbid() : Results.Ok(items);
        }).RequireAuthorization()
            .WithName("ListVisitPlan").WithTags("Visits").Produces<List<VisitDto>>(200)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(429).ProducesProblem(503);

        // Flexible planning (plan step 1.23): order of a day's visits and moving a visit to another day.
        app.MapPut("/visit-plan/order", async (ReorderPlanRequest request, HttpContext context,
            PlanVisits plan, DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole)
                return ApiErrors.Problem(StatusCodes.Status503ServiceUnavailable, ApiErrors.ServiceUnavailable);
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            return ToResult(await plan.ReorderAsync(tenantId, actorId, request, cancellationToken));
        }).RequireAuthorization()
            .WithName("ReorderVisitPlan").WithTags("Visits").Produces(204)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).ProducesProblem(429).ProducesProblem(503);

        // Plan from places (plan step 1.24): preview, then create in one step.
        app.MapPost("/visit-plan/preview", async (GeneratePlanRequest request, HttpContext context,
            PlanGenerator generator, DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole)
                return ApiErrors.Problem(StatusCodes.Status503ServiceUnavailable, ApiErrors.ServiceUnavailable);
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var (result, preview) = await generator.PreviewAsync(tenantId, actorId, request, cancellationToken);
            return result == PlanGenerationResult.Success ? Results.Ok(preview) : ToResult(result);
        }).RequireAuthorization()
            .WithName("PreviewVisitPlan").WithTags("Visits").Produces<PlanPreviewDto>(200)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(429).ProducesProblem(503);

        app.MapPost("/visit-plan/generate", async (GeneratePlanRequest request, HttpContext context,
            PlanGenerator generator, DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole)
                return ApiErrors.Problem(StatusCodes.Status503ServiceUnavailable, ApiErrors.ServiceUnavailable);
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var (result, generated) = await generator.GenerateAsync(tenantId, actorId, request, cancellationToken);
            return result == PlanGenerationResult.Success ? Results.Ok(generated) : ToResult(result);
        }).RequireAuthorization()
            .WithName("GenerateVisitPlan").WithTags("Visits").Produces<PlanGeneratedDto>(200)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(429).ProducesProblem(503);

        app.MapPost("/visits/{visitId:guid}/postpone", async (Guid visitId, PostponeVisitRequest request, HttpContext context,
            PlanVisits plan, DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole)
                return ApiErrors.Problem(StatusCodes.Status503ServiceUnavailable, ApiErrors.ServiceUnavailable);
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            return ToResult(await plan.PostponeAsync(tenantId, actorId, visitId, request, cancellationToken));
        }).RequireAuthorization()
            .WithName("PostponeVisit").WithTags("Visits").Produces(204)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).ProducesProblem(429).ProducesProblem(503);

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

        // Server-side check-in (plan step 1.7): replaces the earlier status-only /start.
        app.MapPost("/visits/{visitId:guid}/start", async (Guid visitId, CheckInRequest request, HttpContext context,
            VisitCheckpoint checkpoint, DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole)
                return ApiErrors.Problem(StatusCodes.Status503ServiceUnavailable, ApiErrors.ServiceUnavailable);
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var (result, rejection) = await checkpoint.CheckInAsync(tenantId, actorId, ReadDeviceId(context), visitId, request,
                context.Request.Headers["Idempotency-Key"].FirstOrDefault(), cancellationToken);
            // An accepted offline attempt answers 200 with whether a manager must review it (decision D5).
            return result == LocationCheckResult.Success && checkpoint.LastOfflineOutcome is { } offline
                ? Results.Ok(offline) : ToResult(result, rejection);
        }).RequireAuthorization()
            .WithName("StartVisit").WithTags("Visits").Produces(204).Produces<OfflineCheckpointResponse>(200)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409)
            .ProducesProblem(422).ProducesProblem(429).ProducesProblem(503);

        // Server-side check-out (plan step 1.8, decision D2): replaces the earlier status-only /complete.
        app.MapPost("/visits/{visitId:guid}/complete", async (Guid visitId, CheckInRequest request, HttpContext context,
            VisitCheckpoint checkpoint, DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole)
                return ApiErrors.Problem(StatusCodes.Status503ServiceUnavailable, ApiErrors.ServiceUnavailable);
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var (result, rejection) = await checkpoint.CheckOutAsync(tenantId, actorId, ReadDeviceId(context), visitId, request,
                context.Request.Headers["Idempotency-Key"].FirstOrDefault(), cancellationToken);
            // An accepted offline attempt answers 200 with whether a manager must review it (decision D5).
            return result == LocationCheckResult.Success && checkpoint.LastOfflineOutcome is { } offline
                ? Results.Ok(offline) : ToResult(result, rejection);
        }).RequireAuthorization()
            .WithName("CompleteVisit").WithTags("Visits").Produces(204).Produces<OfflineCheckpointResponse>(200)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409)
            .ProducesProblem(422).ProducesProblem(429).ProducesProblem(503);

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

        // Offline check-ins/outs awaiting a manager's review (plan step 1.14, decision D5).
        app.MapGet("/offline-reviews", async (string? state, int? limit, HttpContext context, OfflineReviews reviews,
            DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole)
                return ApiErrors.Problem(StatusCodes.Status503ServiceUnavailable, ApiErrors.ServiceUnavailable);
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var (result, items) = await reviews.ListAsync(tenantId, actorId, state, limit, cancellationToken);
            return result == OfflineReviewResult.Success ? Results.Ok(items) : ToResult(result);
        }).RequireAuthorization()
            .WithName("ListOfflineReviews").WithTags("Visits").Produces<List<OfflineReviewDto>>(200)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(429).ProducesProblem(503);

        app.MapPost("/offline-reviews/{eventId:guid}", async (Guid eventId, DecideOfflineReviewRequest request, HttpContext context,
            OfflineReviews reviews, DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole)
                return ApiErrors.Problem(StatusCodes.Status503ServiceUnavailable, ApiErrors.ServiceUnavailable);
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var result = await reviews.DecideAsync(tenantId, actorId, eventId, request, cancellationToken);
            return result == OfflineReviewResult.Success ? Results.NoContent() : ToResult(result);
        }).RequireAuthorization()
            .WithName("DecideOfflineReview").WithTags("Visits").Produces(204)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).ProducesProblem(429).ProducesProblem(503);

        // Visit status history (plan step 1.9): same readers as the report.
        app.MapGet("/visits/{visitId:guid}/history", async (Guid visitId, HttpContext context,
            VisitReports reports, DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole)
                return ApiErrors.Problem(StatusCodes.Status503ServiceUnavailable, ApiErrors.ServiceUnavailable);
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var (result, history) = await reports.GetHistoryAsync(tenantId, actorId, visitId, cancellationToken);
            return result == ManageVisitResult.Success ? Results.Ok(history) : ToResult(result);
        }).RequireAuthorization()
            .WithName("GetVisitHistory").WithTags("Visits").Produces<List<VisitStatusChangeDto>>(200)
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
        ManageVisitResult.OrderRequired => ApiErrors.Problem(StatusCodes.Status409Conflict, ApiErrors.OrderRequired),
        _ => Results.Forbid()
    };

    private static IResult ToResult(PlanGenerationResult result) => result switch
    {
        PlanGenerationResult.InvalidData => ApiErrors.Problem(StatusCodes.Status400BadRequest, ApiErrors.InvalidData),
        PlanGenerationResult.UserNotFound => ApiErrors.Problem(StatusCodes.Status404NotFound, ApiErrors.UserNotFound),
        _ => Results.Forbid()
    };

    private static IResult ToResult(OfflineReviewResult result) => result switch
    {
        OfflineReviewResult.NotFound => ApiErrors.Problem(StatusCodes.Status404NotFound, ApiErrors.NotFound),
        OfflineReviewResult.InvalidData => ApiErrors.Problem(StatusCodes.Status400BadRequest, ApiErrors.InvalidData),
        OfflineReviewResult.AlreadyDecided => ApiErrors.Problem(StatusCodes.Status409Conflict, ApiErrors.InvalidState),
        _ => ApiErrors.Problem(StatusCodes.Status403Forbidden, ApiErrors.Forbidden)
    };

    private static Guid? ReadDeviceId(HttpContext context) =>
        Guid.TryParse(context.User.FindFirst("device_id")?.Value, out var deviceId) ? deviceId : null;

    internal static IResult ToResult(LocationCheckResult result, LifeDrop.Domain.Visits.LocationRejection? rejection) => result switch
    {
        LocationCheckResult.Success => Results.NoContent(),
        LocationCheckResult.NotFound => ApiErrors.Problem(StatusCodes.Status404NotFound, ApiErrors.NotFound),
        LocationCheckResult.DeviceRequired => ApiErrors.Problem(StatusCodes.Status403Forbidden, ApiErrors.DeviceRequired),
        LocationCheckResult.InvalidState => ApiErrors.Problem(StatusCodes.Status400BadRequest, ApiErrors.InvalidState),
        LocationCheckResult.InvalidData => ApiErrors.Problem(StatusCodes.Status400BadRequest, ApiErrors.InvalidData),
        LocationCheckResult.CustomerLocationMissing => ApiErrors.Problem(StatusCodes.Status409Conflict, ApiErrors.CustomerLocationMissing),
        LocationCheckResult.ReportRequired => ApiErrors.Problem(StatusCodes.Status409Conflict, ApiErrors.ReportRequired),
        LocationCheckResult.Conflict => ApiErrors.Problem(StatusCodes.Status409Conflict, ApiErrors.ConcurrencyConflict),
        LocationCheckResult.Rejected => ApiErrors.Problem(StatusCodes.Status422UnprocessableEntity, rejection switch
        {
            LifeDrop.Domain.Visits.LocationRejection.OutsideGeofence => ApiErrors.OutsideGeofence,
            LifeDrop.Domain.Visits.LocationRejection.LowAccuracy => ApiErrors.GpsLowAccuracy,
            LifeDrop.Domain.Visits.LocationRejection.StaleFix => ApiErrors.GpsStale,
            LifeDrop.Domain.Visits.LocationRejection.ClockSkew => ApiErrors.GpsClockSkew,
            LifeDrop.Domain.Visits.LocationRejection.OfflineExpired => ApiErrors.OfflineExpired,
            _ => ApiErrors.GpsMocked
        }),
        _ => Results.Forbid()
    };
}
