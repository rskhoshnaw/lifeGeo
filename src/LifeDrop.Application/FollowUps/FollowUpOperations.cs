using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LifeDrop.Application.Customers;
using LifeDrop.Application.Roles;
using LifeDrop.Application.Visits;
using LifeDrop.Domain.Common;

namespace LifeDrop.Application.FollowUps;

// Follow-up (plan step 2.8): a reminder for a customer with a subject, a due day (Iraq) and one responsible user. pending -> in_progress -> completed (with a
// result note) or cancelled (with a reason). Overdue is derived: the due day has passed and the follow-up is still pending or in progress.
public sealed record FollowUpDto(Guid Id, Guid CustomerId, string CustomerCode, string CustomerName, string Subject, DateOnly DueDate, Guid AssigneeId, string? AssigneeEmail, Guid CreatedBy,
    string? CreatedByEmail, string Status, bool IsOverdue, string? ResultNote, string? CancelReason, DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt);

public sealed record CreateFollowUpRequest(Guid? CustomerId, string? Subject, DateOnly? DueDate, Guid? AssigneeId, Guid? ClientRef);
public sealed record CompleteFollowUpRequest(string? ResultNote);
public sealed record CancelFollowUpRequest(string? Reason);
public sealed record ReassignFollowUpRequest(Guid? AssigneeId);
public sealed record CreatedFollowUpDto(Guid Id, bool Replayed);

public enum FollowUpResult { Success, Forbidden, NotFound, InvalidData, Conflict }
public enum FollowUpView { Everything, Region, Mine }

public sealed record NewFollowUp(Guid CustomerId, string Subject, DateOnly DueDate, Guid AssigneeId, Guid ClientRef);

public interface IFollowUpStore
{
    // Replays an earlier send with the same ClientRef (same creator and customer, else Conflict).
    Task<(FollowUpResult Result, CreatedFollowUpDto? FollowUp)> CreateAsync(TenantId tenantId, UserId actorId, NewFollowUp followUp, CancellationToken cancellationToken);
    Task<IReadOnlyList<FollowUpDto>> ListAsync(TenantId tenantId, UserId actorId, FollowUpView view, Guid? regionId, Guid? customerId, Guid? assigneeId, string? status, bool overdueOnly,
        DateOnly today, int limit, CancellationToken cancellationToken);
    // Without any visibility filter: the service checks who may see or change it.
    Task<FollowUpDto?> GetAsync(TenantId tenantId, Guid followUpId, DateOnly today, CancellationToken cancellationToken);
    // Conflict when the follow-up is not in one of the allowed statuses any more. Audit row in the same transaction.
    Task<FollowUpResult> StartAsync(TenantId tenantId, UserId actorId, Guid followUpId, CancellationToken cancellationToken);
    Task<FollowUpResult> CompleteAsync(TenantId tenantId, UserId actorId, Guid followUpId, string resultNote, CancellationToken cancellationToken);
    Task<FollowUpResult> CancelAsync(TenantId tenantId, UserId actorId, Guid followUpId, string reason, CancellationToken cancellationToken);
    Task<FollowUpResult> ReassignAsync(TenantId tenantId, UserId actorId, Guid followUpId, Guid assigneeId, CancellationToken cancellationToken);
}

public sealed class FollowUpService(IFollowUpStore store, IPermissionStore permissions, ICustomerStore customers, TimeProvider clock)
{
    public async Task<(FollowUpResult Result, CreatedFollowUpDto? FollowUp)> CreateAsync(TenantId tenantId, UserId actorId, CreateFollowUpRequest request, CancellationToken cancellationToken)
    {
        var scope = await ScopeAsync(tenantId, actorId, PermissionKeys.FollowUpsCreate, cancellationToken);
        if (scope is null) return (FollowUpResult.Forbidden, null);
        var subject = request.Subject?.Trim();
        if (request.CustomerId is null || request.CustomerId == Guid.Empty || string.IsNullOrEmpty(subject) || subject.Length > 200 || request.DueDate is null ||
            request.DueDate < IraqCalendar.Today(clock) || request.AssigneeId == Guid.Empty || request.ClientRef is null || request.ClientRef == Guid.Empty)
            return (FollowUpResult.InvalidData, null);
        if (await customers.GetByIdAsync(tenantId, new CustomerId(request.CustomerId.Value), scope, cancellationToken) is null) return (FollowUpResult.NotFound, null);

        // A follow-up is for the creator, unless a manager gives it to somebody in the manager's area.
        var assignee = request.AssigneeId ?? actorId.Value;
        if (assignee != actorId.Value)
        {
            var manage = await ScopeAsync(tenantId, actorId, PermissionKeys.FollowUpsManage, cancellationToken);
            if (manage is null) return (FollowUpResult.Forbidden, null);
            var (exists, region) = await permissions.GetUserRegionAsync(tenantId, new UserId(assignee), cancellationToken);
            if (!exists || !ManageVisit.IsUserInScope(manage, new UserId(assignee), region)) return (FollowUpResult.NotFound, null);
        }
        return await store.CreateAsync(tenantId, actorId, new NewFollowUp(request.CustomerId.Value, subject, request.DueDate.Value, assignee, request.ClientRef.Value), cancellationToken);
    }

    public async Task<(FollowUpResult Result, IReadOnlyList<FollowUpDto> Items)> ListAsync(TenantId tenantId, UserId actorId, Guid? customerId, Guid? assigneeId, string? status, bool? overdue,
        int? limit, CancellationToken cancellationToken)
    {
        var (view, region) = await ViewAsync(tenantId, actorId, cancellationToken);
        if (view is null) return (FollowUpResult.Forbidden, Array.Empty<FollowUpDto>());
        var take = limit ?? 100;
        if (take is < 1 or > 200 || customerId == Guid.Empty || assigneeId == Guid.Empty || status is not (null or "pending" or "in_progress" or "completed" or "cancelled"))
            return (FollowUpResult.InvalidData, Array.Empty<FollowUpDto>());
        return (FollowUpResult.Success, await store.ListAsync(tenantId, actorId, view.Value, region, customerId, assigneeId, status, overdue == true, IraqCalendar.Today(clock), take, cancellationToken));
    }

    public async Task<(FollowUpResult Result, FollowUpDto? FollowUp)> GetAsync(TenantId tenantId, UserId actorId, Guid followUpId, CancellationToken cancellationToken)
    {
        var (item, _, _) = await LoadAsync(tenantId, actorId, followUpId, cancellationToken);
        return item is null ? (FollowUpResult.NotFound, null) : (FollowUpResult.Success, item.Value.Item);
    }

    public async Task<FollowUpResult> StartAsync(TenantId tenantId, UserId actorId, Guid followUpId, CancellationToken cancellationToken)
    {
        var (item, _, _) = await LoadAsync(tenantId, actorId, followUpId, cancellationToken);
        if (item is null) return FollowUpResult.NotFound;
        return item.Value.Item.AssigneeId != actorId.Value ? FollowUpResult.Forbidden : await store.StartAsync(tenantId, actorId, followUpId, cancellationToken);
    }

    public async Task<FollowUpResult> CompleteAsync(TenantId tenantId, UserId actorId, Guid followUpId, CompleteFollowUpRequest request, CancellationToken cancellationToken)
    {
        var (item, _, _) = await LoadAsync(tenantId, actorId, followUpId, cancellationToken);
        if (item is null) return FollowUpResult.NotFound;
        if (item.Value.Item.AssigneeId != actorId.Value) return FollowUpResult.Forbidden;
        var note = request.ResultNote?.Trim();
        return string.IsNullOrEmpty(note) || note.Length > 500 ? FollowUpResult.InvalidData : await store.CompleteAsync(tenantId, actorId, followUpId, note, cancellationToken);
    }

    // The creator or a manager whose area covers the responsible user.
    public async Task<FollowUpResult> CancelAsync(TenantId tenantId, UserId actorId, Guid followUpId, CancelFollowUpRequest request, CancellationToken cancellationToken)
    {
        var (item, manages, _) = await LoadAsync(tenantId, actorId, followUpId, cancellationToken);
        if (item is null) return FollowUpResult.NotFound;
        if (!manages && item.Value.Item.CreatedBy != actorId.Value) return FollowUpResult.Forbidden;
        var reason = request.Reason?.Trim();
        return string.IsNullOrEmpty(reason) || reason.Length > 500 ? FollowUpResult.InvalidData : await store.CancelAsync(tenantId, actorId, followUpId, reason, cancellationToken);
    }

    public async Task<FollowUpResult> ReassignAsync(TenantId tenantId, UserId actorId, Guid followUpId, ReassignFollowUpRequest request, CancellationToken cancellationToken)
    {
        var (item, manages, manage) = await LoadAsync(tenantId, actorId, followUpId, cancellationToken);
        if (item is null) return FollowUpResult.NotFound;
        if (!manages || manage is null) return FollowUpResult.Forbidden;
        if (request.AssigneeId is null || request.AssigneeId == Guid.Empty) return FollowUpResult.InvalidData;
        var (exists, region) = await permissions.GetUserRegionAsync(tenantId, new UserId(request.AssigneeId.Value), cancellationToken);
        if (!exists || !ManageVisit.IsUserInScope(manage, new UserId(request.AssigneeId.Value), region)) return FollowUpResult.NotFound;
        return await store.ReassignAsync(tenantId, actorId, followUpId, request.AssigneeId.Value, cancellationToken);
    }

    // The follow-up and what the caller may do: Item is null (404) unless the caller is the responsible user, the creator, or reads/manages the responsible user's area.
    private async Task<((FollowUpDto Item, bool Visible)? Item, bool Manages, AccessScope? Manage)> LoadAsync(TenantId tenantId, UserId actorId, Guid followUpId, CancellationToken cancellationToken)
    {
        if (tenantId.IsEmpty || actorId.IsEmpty || followUpId == Guid.Empty) return (null, false, null);
        var item = await store.GetAsync(tenantId, followUpId, IraqCalendar.Today(clock), cancellationToken);
        if (item is null) return (null, false, null);
        var assignee = new UserId(item.AssigneeId);
        var manage = await ScopeAsync(tenantId, actorId, PermissionKeys.FollowUpsManage, cancellationToken);
        var read = await ScopeAsync(tenantId, actorId, PermissionKeys.FollowUpsRead, cancellationToken);
        Guid? assigneeRegion = null;
        if (manage is { Scope: not DataScope.Tenant } || read is { Scope: not DataScope.Tenant }) assigneeRegion = (await permissions.GetUserRegionAsync(tenantId, assignee, cancellationToken)).RegionId;
        var manages = manage is not null && ManageVisit.IsUserInScope(manage, assignee, assigneeRegion);
        var reads = read is not null && ManageVisit.IsUserInScope(read, assignee, assigneeRegion);
        var visible = item.AssigneeId == actorId.Value || item.CreatedBy == actorId.Value || manages || reads;
        return visible ? ((item, true), manages, manage) : (null, false, null);
    }

    private async Task<(FollowUpView? View, Guid? Region)> ViewAsync(TenantId tenantId, UserId actorId, CancellationToken cancellationToken)
    {
        var read = await ScopeAsync(tenantId, actorId, PermissionKeys.FollowUpsRead, cancellationToken);
        if (read is not null) return read.Scope == DataScope.Tenant ? (FollowUpView.Everything, null) : read.RegionId is { } r ? (FollowUpView.Region, r) : (FollowUpView.Mine, null);
        // Anybody who may create follow-ups sees their own; so does a responsible user who cannot create (assigned by a manager).
        return await ScopeAsync(tenantId, actorId, PermissionKeys.FollowUpsCreate, cancellationToken) is not null ? (FollowUpView.Mine, null) : (null, null);
    }

    private Task<AccessScope?> ScopeAsync(TenantId tenantId, UserId actorId, string permission, CancellationToken cancellationToken) =>
        tenantId.IsEmpty || actorId.IsEmpty ? Task.FromResult<AccessScope?>(null) : permissions.GetAccessScopeAsync(tenantId, actorId, permission, cancellationToken);
}
