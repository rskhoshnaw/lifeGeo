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
// preview); Count is ignored then.
public sealed record GeneratePlanRequest(Guid? AssignedUserId, DateOnly ScheduleDate, Guid? PlaceId = null, Guid? RegionId = null, int? Count = null,
    string? Order = null, bool? RespectWindows = null, string? VisitType = null, string? Priority = null, IReadOnlyList<Guid>? CustomerIds = null, string? Query = null);

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
        string order, string? query, CancellationToken cancellationToken);
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

    // The visit type that fits a customer type (Pharmacy, Doctor, Wholesaler; anything else is "other").
    public static string VisitTypeOf(string customerType) => customerType.ToLowerInvariant() switch
    {
        "pharmacy" => "pharmacy",
        "doctor" => "doctor",
        "wholesaler" => "wholesaler",
        _ => "other"
    };

    private async Task<(PlanGenerationResult? Error, PlanPreviewDto? Preview)> PrepareAsync(TenantId tenantId, UserId actorId, GeneratePlanRequest request, CancellationToken cancellationToken)
    {
        if (tenantId.IsEmpty || actorId.IsEmpty) return (PlanGenerationResult.Forbidden, null);
        var order = request.Order ?? "least_recent";
        if (request.AssignedUserId == Guid.Empty || request.ScheduleDate == default || request.ScheduleDate < IraqCalendar.Today(clock) ||
            (request.PlaceId is null && request.RegionId is null && string.IsNullOrWhiteSpace(request.Query)) || request.Query?.Trim().Length > 60 || request.PlaceId == Guid.Empty || request.RegionId == Guid.Empty ||
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

        var rows = await store.FindCandidatesAsync(tenantId, scope, request.PlaceId, request.RegionId, request.ScheduleDate, order, request.Query?.Trim(), cancellationToken);
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
