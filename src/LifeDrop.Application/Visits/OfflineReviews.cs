using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LifeDrop.Application.Roles;
using LifeDrop.Domain.Common;

namespace LifeDrop.Application.Visits;

// One offline check-in/out waiting for (or after) a manager's decision. No coordinates: the distance to the
// customer and the reasons are enough to decide, and the full path is on the tracking map.
public sealed record OfflineReviewDto(Guid EventId, Guid VisitId, Guid UserId, string Kind, DateTimeOffset CapturedAt,
    DateTimeOffset ReceivedAt, double? DistanceMeters, int? RadiusMeters, double AccuracyMeters, IReadOnlyList<string> Reasons,
    string State, Guid? ReviewedBy, DateTimeOffset? ReviewedAt, string? Note);

public sealed record DecideOfflineReviewRequest(string? Decision, string? Note);

public interface IOfflineReviewStore
{
    Task<IReadOnlyList<OfflineReviewDto>> ListAsync(TenantId tenantId, string state, int limit, CancellationToken cancellationToken);
    Task<OfflineReviewDto?> GetAsync(TenantId tenantId, Guid eventId, CancellationToken cancellationToken);
    // Sets the decision only while the event is still pending; audited in the same transaction.
    Task<bool> TryDecideAsync(TenantId tenantId, UserId reviewerId, Guid eventId, string decision, string? note, CancellationToken cancellationToken);
}

public enum OfflineReviewResult { Success, Forbidden, NotFound, InvalidData, AlreadyDecided }

// Manager review of offline check-ins/outs that the automatic checks could not confirm (plan step 1.14, decision
// D5). Reviewers need visits.write with tenant or region scope; they see and decide only for users in that scope
// and never their own events. The visit's status is not changed by the decision; the decision is recorded and
// audited for reports and follow-up.
public sealed class OfflineReviews(IOfflineReviewStore store, IPermissionStore permissions)
{
    public const int MaxNoteLength = 500;
    private static readonly string[] States = ["pending", "approved", "rejected"];

    public async Task<(OfflineReviewResult Result, IReadOnlyList<OfflineReviewDto>? Items)> ListAsync(TenantId tenantId, UserId actorId,
        string? state, int? limit, CancellationToken cancellationToken)
    {
        var scope = await ReviewerScopeAsync(tenantId, actorId, cancellationToken);
        if (scope is null) return (OfflineReviewResult.Forbidden, null);
        state ??= "pending";
        if (!States.Contains(state) || limit is < 1 or > 200) return (OfflineReviewResult.InvalidData, null);

        var all = await store.ListAsync(tenantId, state, limit ?? 50, cancellationToken);
        var visible = new List<OfflineReviewDto>();
        foreach (var item in all)
            if (await InScopeAsync(tenantId, scope, item.UserId, cancellationToken)) visible.Add(item);
        return (OfflineReviewResult.Success, visible);
    }

    public async Task<OfflineReviewResult> DecideAsync(TenantId tenantId, UserId actorId, Guid eventId, DecideOfflineReviewRequest request,
        CancellationToken cancellationToken)
    {
        var scope = await ReviewerScopeAsync(tenantId, actorId, cancellationToken);
        if (scope is null) return OfflineReviewResult.Forbidden;
        var decision = request.Decision switch { "approve" => "approved", "reject" => "rejected", _ => null };
        var note = request.Note?.Trim();
        if (decision is null || note is { Length: > MaxNoteLength } || (decision == "rejected" && string.IsNullOrEmpty(note)))
            return OfflineReviewResult.InvalidData;

        var item = eventId == Guid.Empty ? null : await store.GetAsync(tenantId, eventId, cancellationToken);
        if (item is null || item.UserId == actorId.Value || !await InScopeAsync(tenantId, scope, item.UserId, cancellationToken))
            return OfflineReviewResult.NotFound;
        if (item.State != "pending") return OfflineReviewResult.AlreadyDecided;

        return await store.TryDecideAsync(tenantId, actorId, eventId, decision, string.IsNullOrEmpty(note) ? null : note, cancellationToken)
            ? OfflineReviewResult.Success : OfflineReviewResult.AlreadyDecided;
    }

    private async Task<AccessScope?> ReviewerScopeAsync(TenantId tenantId, UserId actorId, CancellationToken cancellationToken)
    {
        if (tenantId.IsEmpty || actorId.IsEmpty) return null;
        var scope = await permissions.GetAccessScopeAsync(tenantId, actorId, PermissionKeys.VisitsWrite, cancellationToken);
        return scope is { Scope: DataScope.Tenant or DataScope.Region } ? scope : null;
    }

    private async Task<bool> InScopeAsync(TenantId tenantId, AccessScope scope, Guid userId, CancellationToken cancellationToken)
    {
        if (scope.Scope == DataScope.Tenant) return true;
        var (_, region) = await permissions.GetUserRegionAsync(tenantId, new UserId(userId), cancellationToken);
        return scope.RegionId is not null && region == scope.RegionId;
    }
}
