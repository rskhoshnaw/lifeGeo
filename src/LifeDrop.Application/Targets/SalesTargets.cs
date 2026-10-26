using LifeDrop.Application.Roles;
using LifeDrop.Application.Visits;
using LifeDrop.Domain.Common;

namespace LifeDrop.Application.Targets;

// Sales targets (plan step 5.5, owner request 2026-10-26): how many units (boxes) of a product a visitor should sell in a month, against what was really delivered.
// Month is any date of the month. Quantity null or 0 clears the target.
public sealed record SetTargetRequest(Guid UserId, Guid ProductId, DateOnly Month, decimal? Quantity);

// One visitor and one product of a month: the target (null when none was set) and the quantity delivered of the orders the visitor placed (the Iraq month of the delivery).
public sealed record TargetRowDto(Guid UserId, string? UserEmail, Guid ProductId, string ProductCode, string ProductName, string Unit, decimal? Target, decimal Actual);

public enum TargetResult { Success, Forbidden, NotFound, InvalidData }

public interface ITargetStore
{
    // Quantity null clears (nothing to clear is still a success). NotFound: the visitor or the product is not in the company. Audit target.set / target.cleared.
    Task<TargetResult> SetAsync(TenantId tenantId, UserId actorId, Guid userId, Guid productId, DateOnly month, decimal? quantity, CancellationToken cancellationToken);
    // Rows for the users inside the scope (the actor, the people of the actor's region, or everybody): every product that has a target or a delivered quantity in the month,
    // from (inclusive) to (exclusive) being the Iraq month.
    Task<IReadOnlyList<TargetRowDto>> ListAsync(TenantId tenantId, AccessScope scope, DateOnly month, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken);
}

public sealed class SalesTargets(ITargetStore store, IPermissionStore permissions, TimeProvider clock)
{
    public const decimal MaxQuantity = 1_000_000m;

    // The months that make sense: up to two years back and one year ahead.
    private bool MonthAllowed(DateOnly month)
    {
        var first = new DateOnly(month.Year, month.Month, 1);
        var current = IraqCalendar.Today(clock);
        var currentFirst = new DateOnly(current.Year, current.Month, 1);
        return first >= currentFirst.AddYears(-2) && first <= currentFirst.AddYears(1);
    }

    public async Task<TargetResult> SetAsync(TenantId tenantId, UserId actorId, SetTargetRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var scope = tenantId.IsEmpty || actorId.IsEmpty ? null : await permissions.GetAccessScopeAsync(tenantId, actorId, PermissionKeys.TargetsManage, cancellationToken);
        if (scope is null) return TargetResult.Forbidden;
        var quantity = request.Quantity is 0 ? null : request.Quantity;
        if (request.UserId == Guid.Empty || request.ProductId == Guid.Empty || !MonthAllowed(request.Month) || quantity is < 0 or > MaxQuantity)
            return TargetResult.InvalidData;
        var (exists, region) = await permissions.GetUserRegionAsync(tenantId, new UserId(request.UserId), cancellationToken);
        if (!exists || !ManageVisit.IsUserInScope(scope, new UserId(request.UserId), region)) return TargetResult.NotFound;
        return await store.SetAsync(tenantId, actorId, request.UserId, request.ProductId, new DateOnly(request.Month.Year, request.Month.Month, 1), quantity, cancellationToken);
    }

    // The user's own targets and deliveries of a month (plan step 5.5, the visitor's own page in the app): no permission is needed to see one's own figures.
    public async Task<(TargetResult Result, IReadOnlyList<TargetRowDto> Rows)> MineAsync(TenantId tenantId, UserId actorId, DateOnly month, CancellationToken cancellationToken)
    {
        if (tenantId.IsEmpty || actorId.IsEmpty) return (TargetResult.Forbidden, Array.Empty<TargetRowDto>());
        if (!MonthAllowed(month)) return (TargetResult.InvalidData, Array.Empty<TargetRowDto>());
        var first = new DateOnly(month.Year, month.Month, 1);
        var from = new DateTimeOffset(first.ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(3));
        return (TargetResult.Success, await store.ListAsync(tenantId, new AccessScope(DataScope.Own, actorId, null), first, from, from.AddMonths(1), cancellationToken));
    }

    public async Task<(TargetResult Result, IReadOnlyList<TargetRowDto> Rows)> ListAsync(TenantId tenantId, UserId actorId, DateOnly month, CancellationToken cancellationToken)
    {
        var scope = tenantId.IsEmpty || actorId.IsEmpty ? null : await permissions.GetAccessScopeAsync(tenantId, actorId, PermissionKeys.TargetsRead, cancellationToken);
        if (scope is null) return (TargetResult.Forbidden, Array.Empty<TargetRowDto>());
        if (!MonthAllowed(month)) return (TargetResult.InvalidData, Array.Empty<TargetRowDto>());
        var first = new DateOnly(month.Year, month.Month, 1);
        var from = new DateTimeOffset(first.ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(3));   // the Iraq month
        return (TargetResult.Success, await store.ListAsync(tenantId, scope, first, from, from.AddMonths(1), cancellationToken));
    }
}
