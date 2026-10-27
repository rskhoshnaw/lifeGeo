using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LifeDrop.Application.Roles;
using LifeDrop.Domain.Common;
using LifeDrop.Domain.Visits;

namespace LifeDrop.Application.Visits;

// Plan from places (plan step 1.24): a planner picks a field user, a day and where the customers are (a place of the tree and
// everything below it, and/or a region), optionally how many ("all" when empty) and in which order, sees the list first and then
// creates the visits in one step. A priority a manager gave a customer (plan step 1.26) always comes first: high, normal, low. Order: least_recent (never visited first, then the oldest last visit; default) | name | code.
// RespectWindows (default true): customers whose accepted visit days (plan step 1.23) do not include that weekday are left out.
// VisitType / Priority: for every visit (default: by customer type / normal). CustomerIds: only these customers (as chosen in the
// preview); Count is ignored then. Query (plan step 1.25): only customers whose code or name contains the text (max 60 characters). A field
// user without visits.write plans for themselves only: the same routes, AssignedUserId empty = the caller, customers limited to what the
// caller can read. Area (plan step 1.27): corners of an area drawn on a map (3-100 points, closed by the server, at most 0.5 degrees
// wide and high): only located customers inside it. PlaceId, RegionId, Query, Area and CustomerIds (plan step 5.8: applying a plan template) are filters; at least one is needed.
public sealed record GeneratePlanRequest(Guid? AssignedUserId, DateOnly ScheduleDate, Guid? PlaceId = null, Guid? RegionId = null, int? Count = null,
    string? Order = null, bool? RespectWindows = null, string? VisitType = null, string? Priority = null, IReadOnlyList<Guid>? CustomerIds = null, string? Query = null, IReadOnlyList<GeoPoint>? Area = null);

// A corner of a drawn area (plan step 1.27).
public sealed record GeoPoint(double Latitude, double Longitude);

// SkipReason (not included): outside_hours | over_limit. LastVisitDate: the day of the last completed visit.
public sealed record PlanCandidateDto(Guid CustomerId, string Code, string Name, string Type, string? Place, DateOnly? LastVisitDate, string WindowStatus,
    bool Included, string? SkipReason, string CustomerPriority = "normal");

// Matching: customers that fit the filters and are not yet planned that day (before the limit and the hours rule).
public sealed record PlanPreviewDto(int Matching, int Included, IReadOnlyList<PlanCandidateDto> Candidates);

public sealed record PlanGeneratedDto(int Created, int Skipped, IReadOnlyList<Guid> VisitIds);

public sealed record PlanCandidateRow(Guid CustomerId, string Code, string Name, string Type, string? Place, DateOnly? LastVisitDate, string WindowStatus, string CustomerPriority = "normal");

public sealed record PlanVisitToCreate(Guid CustomerId, string VisitType);

public enum PlanGenerationResult { Success, Forbidden, InvalidData, UserNotFound }

public interface IPlanGenerationStore
{
    // Approved customers inside scope and the filters that have no visit (other than cancelled or failed) on that day, in the
    // wanted order, at most MaxMatching rows.
    Task<IReadOnlyList<PlanCandidateRow>> FindCandidatesAsync(TenantId tenantId, AccessScope scope, Guid? placeId, Guid? regionId, DateOnly scheduleDate,
        string order, string? query, IReadOnlyList<GeoPoint>? area, CancellationToken cancellationToken, IReadOnlyList<Guid>? customerIds = null);
    // Creates the visits for the assignee on that day, after the visits that are already in the plan, in one transaction with one
    // audit row per visit and one for the whole generation. Returns the new ids.
    Task<IReadOnlyList<Guid>> CreateAsync(TenantId tenantId, UserId actorId, UserId assigneeId, DateOnly scheduleDate, string priority,
        IReadOnlyList<PlanVisitToCreate> visits, CancellationToken cancellationToken);
}

public sealed class PlanGenerator(IPlanGenerationStore store, IPermissionStore permissions, TimeProvider clock)
{
    public const int MaxMatching = 2000;
    public const int MaxCount = 200;
    public static readonly string[] Orders = ["least_recent", "name", "code"];

    public async Task<(PlanGenerationResult Result, PlanPreviewDto? Preview)> PreviewAsync(TenantId tenantId, UserId actorId, GeneratePlanRequest request, CancellationToken cancellationToken)
    {
        var prepared = await PrepareAsync(tenantId, actorId, request, cancellationToken);
        return prepared.Error is { } error ? (error, null) : (PlanGenerationResult.Success, prepared.Preview);
    }

    public async Task<(PlanGenerationResult Result, PlanGeneratedDto? Generated)> GenerateAsync(TenantId tenantId, UserId actorId, GeneratePlanRequest request, CancellationToken cancellationToken)
    {
        var prepared = await PrepareAsync(tenantId, actorId, request, cancellationToken);
        if (prepared.Error is { } error) return (error, null);

        var chosen = prepared.Preview!.Candidates.Where(c => c.Included).ToList();
        var skipped = (request.CustomerIds?.Count ?? chosen.Count) - chosen.Count;
        if (chosen.Count == 0) return (PlanGenerationResult.Success, new PlanGeneratedDto(0, Math.Max(0, skipped), Array.Empty<Guid>()));

        var priority = request.Priority ?? "normal";
        var ids = await store.CreateAsync(tenantId, actorId, new UserId(request.AssignedUserId ?? actorId.Value), request.ScheduleDate, priority,
            chosen.Select(c => new PlanVisitToCreate(c.CustomerId, request.VisitType ?? VisitTypeOf(c.Type))).ToList(), cancellationToken);
        return (PlanGenerationResult.Success, new PlanGeneratedDto(ids.Count, Math.Max(0, skipped), ids));
    }

    public const int MaxAreaPoints = 100;
    public const double MaxAreaDegrees = 0.5;

    // Null is fine (no area). Otherwise 3-100 real coordinates whose box is not larger than MaxAreaDegrees, so a drawing cannot
    // select a whole country, and with a real surface (not a line or a single spot).
    public static bool IsValidArea(IReadOnlyList<GeoPoint>? area)
    {
        if (area is null) return true;
        if (area.Count < 3 || area.Count > MaxAreaPoints) return false;
        if (area.Any(p => double.IsNaN(p.Latitude) || double.IsNaN(p.Longitude) || p.Latitude is < -90 or > 90 || p.Longitude is < -180 or > 180)) return false;
        if (area.Max(p => p.Latitude) - area.Min(p => p.Latitude) > MaxAreaDegrees || area.Max(p => p.Longitude) - area.Min(p => p.Longitude) > MaxAreaDegrees) return false;
        double twice = 0;
        for (var i = 0; i < area.Count; i++)
        {
            var (a, b) = (area[i], area[(i + 1) % area.Count]);
            twice += a.Longitude * b.Latitude - b.Longitude * a.Latitude;
        }
        return Math.Abs(twice) > 1e-12;
    }

    // The visit type that fits a customer type (Pharmacy, Doctor, Wholesaler; anything else is "other").
    public static string VisitTypeOf(string customerType) => customerType.ToLowerInvariant() switch
    {
        "pharmacy" or "medicalassistant" => "pharmacy",
        "doctor" => "doctor",
        "wholesaler" => "wholesaler",
        _ => "other"
    };

    private async Task<(PlanGenerationResult? Error, PlanPreviewDto? Preview)> PrepareAsync(TenantId tenantId, UserId actorId, GeneratePlanRequest request, CancellationToken cancellationToken)
    {
        if (tenantId.IsEmpty || actorId.IsEmpty) return (PlanGenerationResult.Forbidden, null);
        var order = request.Order ?? "least_recent";
        if (request.AssignedUserId == Guid.Empty || request.ScheduleDate == default || request.ScheduleDate < IraqCalendar.Today(clock) ||
            (request.PlaceId is null && request.RegionId is null && string.IsNullOrWhiteSpace(request.Query) && request.Area is null && request.CustomerIds is null) || !IsValidArea(request.Area) || request.Query?.Trim().Length > 60 || request.PlaceId == Guid.Empty || request.RegionId == Guid.Empty ||
            request.Count is < 1 or > MaxCount || !Orders.Contains(order) ||
            !VisitPlanningContract.TryParseType(request.VisitType, out _) || !VisitPlanningContract.TryParsePriority(request.Priority, out _) ||
            (request.CustomerIds is { } ids && (ids.Count == 0 || ids.Count > MaxCount || ids.Any(i => i == Guid.Empty))))
            return (PlanGenerationResult.InvalidData, null);

        var assignee = new UserId(request.AssignedUserId ?? actorId.Value);
        var scope = await permissions.GetAccessScopeAsync(tenantId, actorId, PermissionKeys.VisitsWrite, cancellationToken);
        if (scope is not null)
        {
            var (exists, assigneeRegion) = await permissions.GetUserRegionAsync(tenantId, assignee, cancellationToken);
            if (!exists) return (PlanGenerationResult.UserNotFound, null);
            if (!ManageVisit.IsUserInScope(scope, assignee, assigneeRegion)) return (PlanGenerationResult.Forbidden, null);
        }
        else
        {
            // A field user plans for themselves from the customers they may read.
            if (assignee != actorId || await permissions.GetAccessScopeAsync(tenantId, actorId, PermissionKeys.VisitsPlan, cancellationToken) is null)
                return (PlanGenerationResult.Forbidden, null);
            scope = await permissions.GetAccessScopeAsync(tenantId, actorId, PermissionKeys.CustomersRead, cancellationToken);
            if (scope is null) return (PlanGenerationResult.Forbidden, null);
        }

        var rows = await store.FindCandidatesAsync(tenantId, scope, request.PlaceId, request.RegionId, request.ScheduleDate, order, request.Query?.Trim(), request.Area, cancellationToken, request.CustomerIds);
        var respect = request.RespectWindows ?? true;
        var wanted = request.CustomerIds?.ToHashSet();
        var remaining = request.Count ?? int.MaxValue;
        var candidates = new List<PlanCandidateDto>(rows.Count);
        foreach (var row in rows)
        {
            string? reason = null;
            if (wanted is not null && !wanted.Contains(row.CustomerId)) continue;   // not chosen in the preview
            if (respect && row.WindowStatus == "outside") reason = "outside_hours";
            else if (wanted is null && remaining <= 0) reason = "over_limit";
            if (reason is null && wanted is null) remaining--;
            candidates.Add(new PlanCandidateDto(row.CustomerId, row.Code, row.Name, row.Type, row.Place, row.LastVisitDate, row.WindowStatus, reason is null, reason, row.CustomerPriority));
        }
        return (null, new PlanPreviewDto(rows.Count, candidates.Count(c => c.Included), candidates));
    }
}
