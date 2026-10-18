using LifeDrop.Application.Customers;
using LifeDrop.Application.FollowUps;
using LifeDrop.Application.Roles;
using LifeDrop.Domain.Common;
using Xunit;

namespace LifeDrop.Application.Tests;

public class FollowUpEditTests
{
    private static readonly TenantId Tenant = TenantId.New();
    private static readonly UserId Creator = UserId.New();
    private static readonly UserId Other = UserId.New();
    private static readonly Guid Id = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 9, 0, 0, TimeSpan.Zero);

    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Permissions(params string[] granted) : IPermissionStore
    {
        public Task<bool> HasPermissionAsync(TenantId tenantId, UserId userId, string permissionKey, CancellationToken cancellationToken) =>
            Task.FromResult(granted.Contains(permissionKey));
    }

    private sealed class Store(DateOnly due, string status = "pending") : IFollowUpStore
    {
        public List<string> Calls = [];
        public FollowUpResult EditResult = FollowUpResult.Success;

        public Task<FollowUpDto?> GetAsync(TenantId tenantId, Guid followUpId, DateOnly today, CancellationToken cancellationToken) =>
            Task.FromResult<FollowUpDto?>(new FollowUpDto(Id, Guid.NewGuid(), "P-1", "Shifa", "Call", due, Creator.Value, null, Creator.Value, null, status, false, null, null, Now, null));

        public Task<FollowUpResult> EditAsync(TenantId tenantId, UserId actorId, Guid followUpId, string subject, DateOnly dueDate, CancellationToken cancellationToken)
        {
            Calls.Add($"{subject}|{dueDate:yyyy-MM-dd}");
            return Task.FromResult(EditResult);
        }

        public Task<(FollowUpResult Result, CreatedFollowUpDto? FollowUp)> CreateAsync(TenantId tenantId, UserId actorId, NewFollowUp followUp, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<FollowUpDto>> ListAsync(TenantId tenantId, UserId actorId, FollowUpView view, Guid? regionId, Guid? customerId, Guid? assigneeId, string? status, bool overdueOnly, DateOnly today, int limit, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<FollowUpResult> StartAsync(TenantId tenantId, UserId actorId, Guid followUpId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<FollowUpResult> CompleteAsync(TenantId tenantId, UserId actorId, Guid followUpId, string resultNote, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<FollowUpResult> CancelAsync(TenantId tenantId, UserId actorId, Guid followUpId, string reason, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<FollowUpResult> ReassignAsync(TenantId tenantId, UserId actorId, Guid followUpId, Guid assigneeId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private static FollowUpService Service(Store store, params string[] granted) => new(store, new Permissions(granted), null!, new Clock());

    private static Task<FollowUpResult> Edit(FollowUpService service, UserId actor, string? subject, DateOnly? due) =>
        service.EditAsync(Tenant, actor, Id, new EditFollowUpRequest(subject, due), CancellationToken.None);

    [Fact]
    public async Task The_creator_changes_subject_and_a_future_day()
    {
        var store = new Store(new DateOnly(2026, 10, 20));
        Assert.Equal(FollowUpResult.Success, await Edit(Service(store, PermissionKeys.FollowUpsCreate), Creator, "  New subject ", new DateOnly(2026, 10, 25)));
        Assert.Equal(new[] { "New subject|2026-10-25" }, store.Calls.ToArray());
    }

    [Fact]
    public async Task An_overdue_follow_up_may_keep_its_past_day_but_not_move_to_another_past_day()
    {
        var store = new Store(new DateOnly(2026, 10, 1));
        var service = Service(store, PermissionKeys.FollowUpsCreate);
        Assert.Equal(FollowUpResult.Success, await Edit(service, Creator, "Renamed", new DateOnly(2026, 10, 1)));
        Assert.Equal(FollowUpResult.InvalidData, await Edit(service, Creator, "Renamed", new DateOnly(2026, 10, 2)));
        Assert.Single(store.Calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public async Task An_empty_subject_is_invalid(string? subject)
    {
        var store = new Store(new DateOnly(2026, 10, 20));
        Assert.Equal(FollowUpResult.InvalidData, await Edit(Service(store, PermissionKeys.FollowUpsCreate), Creator, subject, new DateOnly(2026, 10, 25)));
        Assert.Empty(store.Calls);
    }

    [Fact]
    public async Task Somebody_who_neither_created_nor_manages_it_is_refused()
    {
        var store = new Store(new DateOnly(2026, 10, 20));
        // Visible through followups.read, but not the creator and no manage permission.
        Assert.Equal(FollowUpResult.Forbidden, await Edit(Service(store, PermissionKeys.FollowUpsRead), Other, "x", new DateOnly(2026, 10, 25)));
        Assert.Empty(store.Calls);
    }

    [Fact]
    public async Task A_manager_may_edit_and_the_stores_conflict_for_a_closed_one_is_passed_on()
    {
        var store = new Store(new DateOnly(2026, 10, 20)) { EditResult = FollowUpResult.Conflict };
        Assert.Equal(FollowUpResult.Conflict, await Edit(Service(store, PermissionKeys.FollowUpsManage), Other, "x", new DateOnly(2026, 10, 25)));
    }
}
