using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LifeDrop.Application.Roles;
using LifeDrop.Domain.Common;
using LifeDrop.Domain.Visits;

namespace LifeDrop.Application.Visits;

// Plan templates (plan step 5.8, "flexible planning"): plan one week once, save it as a template (weekday + customer for one field user), and apply it to the coming weeks. Applying creates
// the visits through the normal plan generation (PlanGenerator): the accepted hours, the data scope and "no second visit of a customer on the same day" all still apply, past days are left out.
// Who: a manager with visits.write for users inside the scope, or a field user with visits.plan for themselves.
public sealed record SavePlanTemplateRequest(string? Name, DateOnly WeekStart, Guid? AssignedUserId = null);

public sealed record ApplyPlanTemplateRequest(DateOnly StartDate, int Weeks);

public sealed record PlanTemplateDto(Guid Id, Guid UserId, string? UserEmail, string Name, int Customers, int Days, DateTimeOffset CreatedAt);

// Created / Skipped: visits made / customers left out (already planned that day, outside their accepted hours, not approved any more). Days: the days that were planned.
public sealed record PlanTemplateAppliedDto(int Created, int Skipped, int Days);

public sealed record PlanTemplateItem(int Weekday, Guid CustomerId);

public sealed record WeekVisit(DateOnly Date, Guid CustomerId);

public enum PlanTemplateResult { Success, Forbidden, NotFound, InvalidData }

public interface IPlanTemplateStore
{
    // The visits of the user (not cancelled or failed) from the first to the last day, in plan order.
    Task<IReadOnlyList<WeekVisit>> WeekVisitsAsync(TenantId tenantId, Guid userId, DateOnly first, DateOnly last, CancellationToken cancellationToken);
    Task<Guid> CreateAsync(TenantId tenantId, UserId actorId, Guid userId, string name, IReadOnlyList<PlanTemplateItem> items, CancellationToken cancellationToken);
    Task<IReadOnlyList<PlanTemplateDto>> ListAsync(TenantId tenantId, Guid userId, CancellationToken cancellationToken);
    Task<PlanTemplateDto?> GetAsync(TenantId tenantId, Guid id, CancellationToken cancellationToken);
    Task<IReadOnlyList<PlanTemplateItem>> ItemsAsync(TenantId tenantId, Guid id, CancellationToken cancellationToken);
    Task<bool> DeleteAsync(TenantId tenantId, UserId actorId, Guid id, CancellationToken cancellationToken);
}

public sealed class PlanTemplates(IPlanTemplateStore store, PlanGenerator generator, IPermissionStore permissions, TimeProvider clock)
{
    public const int MaxWeeks = 8;
    public const int MaxNameLength = 100;
    public const int MaxTemplatesPerUser = 50;

    // Null when allowed. Forbidden: no permission at all. NotFound: the user is outside the caller's scope.
    private async Task<PlanTemplateResult?> AuthorizeAsync(TenantId tenantId, UserId actorId, UserId user, CancellationToken cancellationToken)
    {
        if (tenantId.IsEmpty || actorId.IsEmpty || user.IsEmpty) return PlanTemplateResult.Forbidden;
        var write = await permissions.GetAccessScopeAsync(tenantId, actorId, PermissionKeys.VisitsWrite, cancellationToken);
        if (write is not null)
        {
            var (exists, region) = await permissions.GetUserRegionAsync(tenantId, user, cancellationToken);
            if (exists && ManageVisit.IsUserInScope(write, user, region)) return null;
        }
        var plan = await permissions.GetAccessScopeAsync(tenantId, actorId, PermissionKeys.VisitsPlan, cancellationToken);
        if (plan is not null && user == actorId) return null;
        return write is null && plan is null ? PlanTemplateResult.Forbidden : PlanTemplateResult.NotFound;
    }

    public async Task<(PlanTemplateResult Result, Guid Id)> SaveAsync(TenantId tenantId, UserId actorId, SavePlanTemplateRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var user = new UserId(request.AssignedUserId ?? actorId.Value);
        if (await AuthorizeAsync(tenantId, actorId, user, cancellationToken) is { } denied) return (denied, Guid.Empty);
        var name = request.Name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > MaxNameLength || request.AssignedUserId == Guid.Empty || request.WeekStart == default) return (PlanTemplateResult.InvalidData, Guid.Empty);

        // Customers once per weekday, in the order of the plan; the weekday is the real day of the week (Sunday = 0, as for the accepted hours).
        var visits = await store.WeekVisitsAsync(tenantId, user.Value, request.WeekStart, request.WeekStart.AddDays(6), cancellationToken);
        var items = visits.Select(v => new PlanTemplateItem((int)v.Date.DayOfWeek, v.CustomerId)).Distinct().ToList();
        if (items.Count == 0 || items.GroupBy(i => i.Weekday).Any(g => g.Count() > PlanGenerator.MaxCount)) return (PlanTemplateResult.InvalidData, Guid.Empty);
        if ((await store.ListAsync(tenantId, user.Value, cancellationToken)).Count >= MaxTemplatesPerUser) return (PlanTemplateResult.InvalidData, Guid.Empty);
        return (PlanTemplateResult.Success, await store.CreateAsync(tenantId, actorId, user.Value, name, items, cancellationToken));
    }

    public async Task<(PlanTemplateResult Result, IReadOnlyList<PlanTemplateDto> Items)> ListAsync(TenantId tenantId, UserId actorId, Guid? assignedUserId, CancellationToken cancellationToken)
    {
        var user = new UserId(assignedUserId ?? actorId.Value);
        if (assignedUserId == Guid.Empty) return (PlanTemplateResult.InvalidData, Array.Empty<PlanTemplateDto>());
        if (await AuthorizeAsync(tenantId, actorId, user, cancellationToken) is { } denied) return (denied, Array.Empty<PlanTemplateDto>());
        return (PlanTemplateResult.Success, await store.ListAsync(tenantId, user.Value, cancellationToken));
    }

    public async Task<PlanTemplateResult> DeleteAsync(TenantId tenantId, UserId actorId, Guid id, CancellationToken cancellationToken)
    {
        if (id == Guid.Empty) return PlanTemplateResult.InvalidData;
        var template = await store.GetAsync(tenantId, id, cancellationToken);
        if (template is null) return await AuthorizeAsync(tenantId, actorId, actorId, cancellationToken) is { } denied && denied == PlanTemplateResult.Forbidden ? denied : PlanTemplateResult.NotFound;
        if (await AuthorizeAsync(tenantId, actorId, new UserId(template.UserId), cancellationToken) is { } no) return no;
        return await store.DeleteAsync(tenantId, actorId, id, cancellationToken) ? PlanTemplateResult.Success : PlanTemplateResult.NotFound;
    }

    public async Task<(PlanTemplateResult Result, PlanTemplateAppliedDto? Applied)> ApplyAsync(TenantId tenantId, UserId actorId, Guid id, ApplyPlanTemplateRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (id == Guid.Empty || request.Weeks is < 1 or > MaxWeeks || request.StartDate == default) return (PlanTemplateResult.InvalidData, null);
        var template = await store.GetAsync(tenantId, id, cancellationToken);
        if (template is null) return (await AuthorizeAsync(tenantId, actorId, actorId, cancellationToken) is { } denied && denied == PlanTemplateResult.Forbidden ? denied : PlanTemplateResult.NotFound, null);
        if (await AuthorizeAsync(tenantId, actorId, new UserId(template.UserId), cancellationToken) is { } no) return (no, null);

        var today = IraqCalendar.Today(clock);
        var byDay = (await store.ItemsAsync(tenantId, id, cancellationToken)).GroupBy(i => i.Weekday).ToDictionary(g => g.Key, g => g.Select(i => i.CustomerId).ToList());
        int created = 0, skipped = 0, days = 0;
        for (var offset = 0; offset < request.Weeks * 7; offset++)
        {
            var date = request.StartDate.AddDays(offset);
            if (date < today || !byDay.TryGetValue((int)date.DayOfWeek, out var customers)) continue;
            var (result, generated) = await generator.GenerateAsync(tenantId, actorId, new GeneratePlanRequest(template.UserId, date, CustomerIds: customers), cancellationToken);
            if (result != PlanGenerationResult.Success) return (result == PlanGenerationResult.Forbidden ? PlanTemplateResult.Forbidden : result == PlanGenerationResult.UserNotFound ? PlanTemplateResult.NotFound : PlanTemplateResult.InvalidData, null);
            created += generated!.Created;
            skipped += generated.Skipped;
            if (generated.Created > 0) days++;
        }
        return (PlanTemplateResult.Success, new PlanTemplateAppliedDto(created, skipped, days));
    }
}
