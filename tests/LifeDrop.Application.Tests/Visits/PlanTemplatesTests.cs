using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LifeDrop.Application.Roles;
using LifeDrop.Application.Visits;
using LifeDrop.Domain.Common;
using Xunit;

namespace LifeDrop.Application.Tests.Visits;

// Plan templates (plan step 5.8): save a week, apply it to the coming weeks through the normal plan generation.
public class PlanTemplatesTests
{
    private static readonly TenantId Tenant = new(Guid.NewGuid());
    private static readonly UserId Rep = new(Guid.NewGuid());
    private static readonly UserId Other = new(Guid.NewGuid());
    private static readonly Guid CustomerA = Guid.NewGuid(), CustomerB = Guid.NewGuid();
    // 2026-10-26 is a Monday (Iraq time); the clock stands at noon that day.
    private static readonly DateOnly Monday = new(2026, 10, 26);

    private static PlanTemplates Service(Store store, Generation generation, params string[] allowed) =>
        new(store, new PlanGenerator(generation, new Permissions(allowed), new Clock()), new Permissions(allowed), new Clock());

    [Fact]
    public async Task ASelfPlannerUsesTheirOwnTemplatesAndAManagerThoseOfUsersInScope()
    {
        var store = new Store();
        var rep = Service(store, new Generation(), PermissionKeys.VisitsPlan, PermissionKeys.CustomersRead);
        Assert.Equal(PlanTemplateResult.Success, (await rep.ListAsync(Tenant, Rep, null, CancellationToken.None)).Result);
        Assert.Equal(PlanTemplateResult.NotFound, (await rep.ListAsync(Tenant, Rep, Other.Value, CancellationToken.None)).Result);   // somebody else's
        Assert.Equal(PlanTemplateResult.NotFound, (await rep.SaveAsync(Tenant, Rep, new SavePlanTemplateRequest("W", Monday, Other.Value), CancellationToken.None)).Result);

        var manager = Service(store, new Generation(), PermissionKeys.VisitsWrite);
        Assert.Equal(PlanTemplateResult.Success, (await manager.ListAsync(Tenant, Rep, Other.Value, CancellationToken.None)).Result);

        var nobody = Service(store, new Generation());
        Assert.Equal(PlanTemplateResult.Forbidden, (await nobody.ListAsync(Tenant, Rep, null, CancellationToken.None)).Result);
        Assert.Equal(PlanTemplateResult.Forbidden, (await nobody.SaveAsync(Tenant, Rep, new SavePlanTemplateRequest("W", Monday), CancellationToken.None)).Result);
        Assert.Equal(PlanTemplateResult.Forbidden, await nobody.DeleteAsync(Tenant, Rep, Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task SavingTakesTheWeekdaysOfTheWeekOnceAndRefusesAnEmptyWeekOrABadName()
    {
        var store = new Store();
        store.Week.AddRange([new WeekVisit(Monday, CustomerA), new WeekVisit(Monday, CustomerB), new WeekVisit(Monday.AddDays(2), CustomerA), new WeekVisit(Monday.AddDays(2), CustomerA)]);
        var service = Service(store, new Generation(), PermissionKeys.VisitsPlan);
        var (result, id) = await service.SaveAsync(Tenant, Rep, new SavePlanTemplateRequest("  Erbil week ", Monday), CancellationToken.None);
        Assert.Equal(PlanTemplateResult.Success, result);
        Assert.NotEqual(Guid.Empty, id);
        Assert.Equal("Erbil week", store.Name);
        Assert.Equal(new[] { (1, CustomerA), (1, CustomerB), (3, CustomerA) }, store.Items.Select(i => (i.Weekday, i.CustomerId)).ToArray());   // Monday = 1, Wednesday = 3; the repeat is dropped
        Assert.Equal((Monday, Monday.AddDays(6)), (store.From, store.To));

        foreach (var bad in new[] { new SavePlanTemplateRequest(" ", Monday), new SavePlanTemplateRequest(new string('n', 101), Monday), new SavePlanTemplateRequest("x", default) })
            Assert.Equal(PlanTemplateResult.InvalidData, (await service.SaveAsync(Tenant, Rep, bad, CancellationToken.None)).Result);
        store.Week.Clear();
        Assert.Equal(PlanTemplateResult.InvalidData, (await service.SaveAsync(Tenant, Rep, new SavePlanTemplateRequest("x", Monday), CancellationToken.None)).Result);
    }

    [Fact]
    public async Task ApplyingPlansOnlyTheMatchingFutureDaysAndAddsUpTheResult()
    {
        var store = new Store { Template = new PlanTemplateDto(Guid.NewGuid(), Rep.Value, "rep@t", "W", 3, 2, DateTimeOffset.UtcNow) };
        store.Items.AddRange([new PlanTemplateItem(1, CustomerA), new PlanTemplateItem(1, CustomerB), new PlanTemplateItem(3, CustomerA)]);
        var generation = new Generation();
        var service = Service(store, generation, PermissionKeys.VisitsPlan, PermissionKeys.CustomersRead);

        // From the Monday of the week before, two weeks: last Monday and Wednesday are past; this week's Monday and Wednesday are planned.
        var (result, applied) = await service.ApplyAsync(Tenant, Rep, store.Template.Id, new ApplyPlanTemplateRequest(Monday.AddDays(-7), 2), CancellationToken.None);
        Assert.Equal(PlanTemplateResult.Success, result);
        Assert.Equal(new[] { Monday, Monday.AddDays(2) }, generation.Days.ToArray());
        Assert.Equal((3, 0, 2), (applied!.Created, applied.Skipped, applied.Days));
        Assert.Equal(new[] { 2, 1 }, generation.Created.ToArray());

        Assert.Equal(PlanTemplateResult.InvalidData, (await service.ApplyAsync(Tenant, Rep, store.Template.Id, new ApplyPlanTemplateRequest(Monday, 0), CancellationToken.None)).Result);
        Assert.Equal(PlanTemplateResult.InvalidData, (await service.ApplyAsync(Tenant, Rep, store.Template.Id, new ApplyPlanTemplateRequest(Monday, PlanTemplates.MaxWeeks + 1), CancellationToken.None)).Result);
        Assert.Equal(PlanTemplateResult.NotFound, (await service.ApplyAsync(Tenant, Rep, Guid.NewGuid(), new ApplyPlanTemplateRequest(Monday, 1), CancellationToken.None)).Result);
        store.Template = store.Template with { UserId = Other.Value };
        Assert.Equal(PlanTemplateResult.NotFound, (await service.ApplyAsync(Tenant, Rep, store.Template.Id, new ApplyPlanTemplateRequest(Monday, 1), CancellationToken.None)).Result);   // another user's template
    }

    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 10, 26, 9, 0, 0, TimeSpan.Zero);
    }

    private sealed class Store : IPlanTemplateStore
    {
        public List<WeekVisit> Week { get; } = [];
        public List<PlanTemplateItem> Items { get; } = [];
        public PlanTemplateDto? Template;
        public string? Name;
        public DateOnly From, To;

        public Task<IReadOnlyList<WeekVisit>> WeekVisitsAsync(TenantId tenantId, Guid userId, DateOnly first, DateOnly last, CancellationToken cancellationToken)
        {
            (From, To) = (first, last);
            return Task.FromResult<IReadOnlyList<WeekVisit>>(Week.ToList());
        }

        public Task<Guid> CreateAsync(TenantId tenantId, UserId actorId, Guid userId, string name, IReadOnlyList<PlanTemplateItem> items, CancellationToken cancellationToken)
        {
            Name = name;
            Items.Clear();
            Items.AddRange(items);
            return Task.FromResult(Guid.NewGuid());
        }

        public Task<IReadOnlyList<PlanTemplateDto>> ListAsync(TenantId tenantId, Guid userId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<PlanTemplateDto>>([]);

        public Task<PlanTemplateDto?> GetAsync(TenantId tenantId, Guid id, CancellationToken cancellationToken) => Task.FromResult(Template is { } t && t.Id == id ? t : null);

        public Task<IReadOnlyList<PlanTemplateItem>> ItemsAsync(TenantId tenantId, Guid id, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<PlanTemplateItem>>(Items.ToList());

        public Task<bool> DeleteAsync(TenantId tenantId, UserId actorId, Guid id, CancellationToken cancellationToken) => Task.FromResult(true);
    }

    // The plan generation store: every asked customer is a candidate; the days and the number of visits made are recorded.
    private sealed class Generation : IPlanGenerationStore
    {
        public List<DateOnly> Days { get; } = [];
        public List<int> Created { get; } = [];

        public Task<IReadOnlyList<PlanCandidateRow>> FindCandidatesAsync(TenantId tenantId, AccessScope scope, Guid? placeId, Guid? regionId, DateOnly scheduleDate, string order, string? query,
            IReadOnlyList<GeoPoint>? area, CancellationToken cancellationToken, IReadOnlyList<Guid>? customerIds = null) =>
            Task.FromResult<IReadOnlyList<PlanCandidateRow>>((customerIds ?? []).Select(id => new PlanCandidateRow(id, "C", "Name", "Pharmacy", null, null, "none")).ToList());

        public Task<IReadOnlyList<Guid>> CreateAsync(TenantId tenantId, UserId actorId, UserId assigneeId, DateOnly scheduleDate, string priority, IReadOnlyList<PlanVisitToCreate> visits,
            CancellationToken cancellationToken)
        {
            Days.Add(scheduleDate);
            Created.Add(visits.Count);
            return Task.FromResult<IReadOnlyList<Guid>>(visits.Select(_ => Guid.NewGuid()).ToList());
        }
    }

    private sealed class Permissions(string[] allowed) : IPermissionStore
    {
        public Task<bool> HasPermissionAsync(TenantId tenantId, UserId userId, string permissionKey, CancellationToken cancellationToken) => Task.FromResult(allowed.Contains(permissionKey));
    }
}
