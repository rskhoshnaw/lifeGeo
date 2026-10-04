using System.IdentityModel.Tokens.Jwt;
using LifeDrop.Application.Tracking;
using LifeDrop.Domain.Common;

namespace LifeDrop.API.Endpoints;

// Shifts and basic tracking (plan step 1.13, PRD §6.2, decision D1). Every route acts on the caller's own shift and
// needs an access token bound to an approved device.
public static class ShiftEndpoints
{
    public static void MapShiftEndpoints(this WebApplication app)
    {
        app.MapPost("/shifts", async (HttpContext context, ShiftTracking tracking, DatabaseSecurityState securityState,
            CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole)
                return ApiErrors.Problem(StatusCodes.Status503ServiceUnavailable, ApiErrors.ServiceUnavailable);
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var (result, shift) = await tracking.StartAsync(tenantId, actorId, ReadDeviceId(context), cancellationToken);
            return result switch
            {
                ShiftResult.Created => Results.Created($"/shifts/{shift!.Id}", shift),
                ShiftResult.Success => Results.Ok(shift),
                _ => ToResult(result)
            };
        }).RequireAuthorization()
            .WithName("StartShift").WithTags("Shifts").Produces<ShiftDto>(200).Produces<ShiftDto>(201)
            .ProducesProblem(401).ProducesProblem(403).ProducesProblem(409).ProducesProblem(429).ProducesProblem(503);

        app.MapGet("/shifts/current", async (HttpContext context, ShiftTracking tracking, DatabaseSecurityState securityState,
            CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole)
                return ApiErrors.Problem(StatusCodes.Status503ServiceUnavailable, ApiErrors.ServiceUnavailable);
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var shift = await tracking.GetCurrentAsync(tenantId, actorId, cancellationToken);
            return shift is null ? ApiErrors.Problem(StatusCodes.Status404NotFound, ApiErrors.NotFound) : Results.Ok(shift);
        }).RequireAuthorization()
            .WithName("GetCurrentShift").WithTags("Shifts").Produces<ShiftDto>(200)
            .ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(429).ProducesProblem(503);

        app.MapPost("/shifts/{shiftId:guid}/end", async (Guid shiftId, HttpContext context, ShiftTracking tracking,
            DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole)
                return ApiErrors.Problem(StatusCodes.Status503ServiceUnavailable, ApiErrors.ServiceUnavailable);
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var result = await tracking.EndAsync(tenantId, actorId, shiftId, cancellationToken);
            return result == ShiftResult.Success ? Results.NoContent() : ToResult(result);
        }).RequireAuthorization()
            .WithName("EndShift").WithTags("Shifts").Produces(204)
            .ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).ProducesProblem(429).ProducesProblem(503);

        // Batch upload of 1-200 points; retried points are recognised by clientPointId.
        app.MapPost("/shifts/{shiftId:guid}/points", async (Guid shiftId, UploadPointsRequest request, HttpContext context,
            ShiftTracking tracking, DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole)
                return ApiErrors.Problem(StatusCodes.Status503ServiceUnavailable, ApiErrors.ServiceUnavailable);
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var (result, response) = await tracking.UploadAsync(tenantId, actorId, ReadDeviceId(context), shiftId, request, cancellationToken);
            return result == ShiftResult.Success ? Results.Ok(response) : ToResult(result);
        }).RequireAuthorization()
            .WithName("UploadTrackingPoints").WithTags("Shifts").Produces<UploadPointsResponse>(200)
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(429).ProducesProblem(503);

        // Coverage of a shift, used to judge the real-device tracking test (>= 90 % of points, gap <= 5 minutes).
        app.MapGet("/shifts/{shiftId:guid}/summary", async (Guid shiftId, HttpContext context, ShiftTracking tracking,
            DatabaseSecurityState securityState, CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole)
                return ApiErrors.Problem(StatusCodes.Status503ServiceUnavailable, ApiErrors.ServiceUnavailable);
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var (result, summary) = await tracking.SummarizeAsync(tenantId, actorId, shiftId, cancellationToken);
            return result == ShiftResult.Success ? Results.Ok(summary) : ToResult(result);
        }).RequireAuthorization()
            .WithName("GetShiftSummary").WithTags("Shifts").Produces<ShiftSummaryDto>(200)
            .ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(429).ProducesProblem(503);

        // Basic live map (plan step 1.19, decision D3): tracking.read gives positions, tracking.summary status only.
        app.MapGet("/live-map", async (HttpContext context, LiveMap liveMap, DatabaseSecurityState securityState,
            CancellationToken cancellationToken) =>
        {
            if (securityState.IsInsecureDbRole)
                return ApiErrors.Problem(StatusCodes.Status503ServiceUnavailable, ApiErrors.ServiceUnavailable);
            if (!TryReadActor(context, out var tenantId, out var actorId)) return Results.Forbid();
            var entries = await liveMap.ListAsync(tenantId, actorId, cancellationToken);
            return entries is null ? Results.Forbid() : Results.Ok(entries);
        }).RequireAuthorization()
            .WithName("GetLiveMap").WithTags("Shifts").Produces<List<LiveMapEntryDto>>(200)
            .ProducesProblem(401).ProducesProblem(403).ProducesProblem(429).ProducesProblem(503);
    }

    private static IResult ToResult(ShiftResult result) => result switch
    {
        ShiftResult.NotFound => ApiErrors.Problem(StatusCodes.Status404NotFound, ApiErrors.NotFound),
        ShiftResult.DeviceRequired => ApiErrors.Problem(StatusCodes.Status403Forbidden, ApiErrors.DeviceRequired),
        ShiftResult.DeviceMismatch => ApiErrors.Problem(StatusCodes.Status403Forbidden, ApiErrors.DeviceMismatch),
        ShiftResult.InvalidData => ApiErrors.Problem(StatusCodes.Status400BadRequest, ApiErrors.InvalidData),
        ShiftResult.Conflict => ApiErrors.Problem(StatusCodes.Status409Conflict, ApiErrors.ConcurrencyConflict),
        _ => Results.Forbid()
    };

    private static Guid? ReadDeviceId(HttpContext context) =>
        Guid.TryParse(context.User.FindFirst("device_id")?.Value, out var deviceId) ? deviceId : null;

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
