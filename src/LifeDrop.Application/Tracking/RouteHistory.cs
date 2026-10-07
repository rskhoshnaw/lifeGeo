using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LifeDrop.Application.Customers;
using LifeDrop.Application.Roles;
using LifeDrop.Application.Visits;
using LifeDrop.Domain.Common;

namespace LifeDrop.Application.Tracking;

// Route history and playback (plan step 2.11, decision D6): the points of one user on one day, the stops (at least 5 minutes within 50 metres), the check-ins and
// check-outs and the shifts. Needs tracking.read and a user inside the reader's area; every read is audited. Points older than 90 days are gone (summaries stay).
public sealed record RoutePointDto(double Latitude, double Longitude, double AccuracyMeters, DateTimeOffset CapturedAt, bool IsMocked);
public sealed record RouteStopDto(DateTimeOffset From, DateTimeOffset To, int Seconds, double Latitude, double Longitude, string? CustomerName, int? CustomerDistanceMeters);
public sealed record RouteVisitEventDto(string Kind, bool Accepted, string? RejectionReason, DateTimeOffset CapturedAt, double Latitude, double Longitude, string? CustomerName, int? RadiusMeters, bool IsMocked);
public sealed record RouteShiftDto(Guid ShiftId, DateTimeOffset StartedAt, DateTimeOffset? EndedAt, int PointCount, bool PointsPurged);
public sealed record RouteHistoryDto(Guid UserId, string? Email, DateOnly Date, IReadOnlyList<RouteShiftDto> Shifts, IReadOnlyList<RoutePointDto> Points, IReadOnlyList<RouteStopDto> Stops,
    IReadOnlyList<RouteVisitEventDto> Events, bool Truncated);
public sealed record TrackableUserDto(Guid Id, string? Email, string? TeamName);

public enum RouteResult { Success, Forbidden, NotFound, InvalidData }

public sealed record RouteRaw(string? Email, IReadOnlyList<RouteShiftDto> Shifts, IReadOnlyList<RoutePointDto> Points, IReadOnlyList<RouteVisitEventDto> Events, bool Truncated);
public sealed record NearCustomer(string Name, int DistanceMeters);

public interface IRouteHistoryStore
{
    // Users inside the scope that may have a route (active users).
    Task<IReadOnlyList<TrackableUserDto>> ListUsersAsync(TenantId tenantId, AccessScope scope, CancellationToken cancellationToken);
    // The points, check-in/out events and shifts of one user between from (inclusive) and to (exclusive); at most MaxPoints points (Truncated tells).
    Task<RouteRaw> LoadAsync(TenantId tenantId, UserId userId, DateTimeOffset from, DateTimeOffset to, int maxPoints, CancellationToken cancellationToken);
    Task<NearCustomer?> NearestCustomerAsync(TenantId tenantId, double latitude, double longitude, int maxMeters, CancellationToken cancellationToken);
    Task AuditViewAsync(TenantId tenantId, UserId actorId, string entity, CancellationToken cancellationToken);
    // Writes the summary of every shift that ended before the cutoff and still has points, then deletes those points. Returns the number of points deleted.
    Task<int> PurgeAsync(TenantId tenantId, DateTimeOffset cutoff, CancellationToken cancellationToken);
}

public static class RouteStops
{
    public static readonly TimeSpan MinDuration = TimeSpan.FromMinutes(5);
    public const double RadiusMeters = 50;

    // A stop: following points that all stay within 50 m of the first one for at least 5 minutes. The position is the average of those points.
    public static IReadOnlyList<(DateTimeOffset From, DateTimeOffset To, double Latitude, double Longitude)> Compute(IReadOnlyList<RoutePointDto> points)
    {
        var stops = new List<(DateTimeOffset, DateTimeOffset, double, double)>();
        var start = 0;
        for (var i = 1; i <= points.Count; i++)
        {
            if (i < points.Count && DistanceMeters(points[start], points[i]) <= RadiusMeters) continue;
            var last = i - 1;
            if (points[last].CapturedAt - points[start].CapturedAt >= MinDuration)
            {
                var group = points.Skip(start).Take(i - start).ToList();
                stops.Add((points[start].CapturedAt, points[last].CapturedAt, group.Average(p => p.Latitude), group.Average(p => p.Longitude)));
            }
            start = i;
        }
        return stops;
    }

    public static double DistanceMeters(RoutePointDto a, RoutePointDto b) => DistanceMeters(a.Latitude, a.Longitude, b.Latitude, b.Longitude);

    public static double DistanceMeters(double lat1, double lng1, double lat2, double lng2)
    {
        const double earth = 6_371_000;
        double Rad(double d) => d * Math.PI / 180;
        var dLat = Rad(lat2 - lat1);
        var dLng = Rad(lng2 - lng1);
        var h = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) + Math.Cos(Rad(lat1)) * Math.Cos(Rad(lat2)) * Math.Sin(dLng / 2) * Math.Sin(dLng / 2);
        return 2 * earth * Math.Asin(Math.Min(1, Math.Sqrt(h)));
    }
}

public sealed class RouteHistory(IRouteHistoryStore store, IPermissionStore permissions, TimeProvider clock)
{
    // 90 days by default (decision D6); a company may keep less through Tracking:RetentionDays (7-365), applied once at start (docs/DEPLOY.md).
    public static TimeSpan Retention { get; private set; } = TimeSpan.FromDays(90);
    public static void UseRetentionDays(int days) => Retention = TimeSpan.FromDays(Math.Clamp(days, 7, 365));
    public const int MaxPoints = 20_000;
    public const int StopCustomerMeters = 150;

    public async Task<(RouteResult Result, IReadOnlyList<TrackableUserDto> Users)> ListUsersAsync(TenantId tenantId, UserId actorId, CancellationToken cancellationToken)
    {
        var scope = await ScopeAsync(tenantId, actorId, cancellationToken);
        return scope is null ? (RouteResult.Forbidden, Array.Empty<TrackableUserDto>()) : (RouteResult.Success, await store.ListUsersAsync(tenantId, scope, cancellationToken));
    }

    public async Task<(RouteResult Result, RouteHistoryDto? History)> GetAsync(TenantId tenantId, UserId actorId, Guid userId, DateOnly date, CancellationToken cancellationToken)
    {
        var scope = await ScopeAsync(tenantId, actorId, cancellationToken);
        if (scope is null) return (RouteResult.Forbidden, null);
        var today = IraqCalendar.Today(clock);
        // Only days the points can still exist for.
        if (userId == Guid.Empty || date > today || date < today.AddDays(-(int)Retention.TotalDays)) return (RouteResult.InvalidData, null);
        var (exists, region) = await permissions.GetUserRegionAsync(tenantId, new UserId(userId), cancellationToken);
        if (!exists || !ManageVisit.IsUserInScope(scope, new UserId(userId), region)) return (RouteResult.NotFound, null);

        var from = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(3));   // Iraq day
        var raw = await store.LoadAsync(tenantId, new UserId(userId), from, from.AddDays(1), MaxPoints, cancellationToken);
        var stops = new List<RouteStopDto>();
        foreach (var stop in RouteStops.Compute(raw.Points))
        {
            var near = await store.NearestCustomerAsync(tenantId, stop.Latitude, stop.Longitude, StopCustomerMeters, cancellationToken);
            stops.Add(new RouteStopDto(stop.From, stop.To, (int)(stop.To - stop.From).TotalSeconds, stop.Latitude, stop.Longitude, near?.Name, near?.DistanceMeters));
        }
        await store.AuditViewAsync(tenantId, actorId, $"{userId}:{date:yyyy-MM-dd}", cancellationToken);
        return (RouteResult.Success, new RouteHistoryDto(userId, raw.Email, date, raw.Shifts, raw.Points, stops, raw.Events, raw.Truncated));
    }

    private async Task<AccessScope?> ScopeAsync(TenantId tenantId, UserId actorId, CancellationToken cancellationToken) =>
        tenantId.IsEmpty || actorId.IsEmpty ? null : await permissions.GetAccessScopeAsync(tenantId, actorId, PermissionKeys.TrackingRead, cancellationToken);
}

// Removes the exact points of shifts that ended more than 90 days ago, after their summary is written.
public sealed class TrackingRetention(IRouteHistoryStore store, TimeProvider clock)
{
    public Task<int> PurgeAsync(TenantId tenantId, CancellationToken cancellationToken) => store.PurgeAsync(tenantId, clock.GetUtcNow() - RouteHistory.Retention, cancellationToken);
}

// The located customers of the caller's area for the map: the panel clusters them and draws the check-in circle (decision D6/1.5).
public sealed record MapCustomerDto(Guid Id, string Code, string Name, string Type, double Latitude, double Longitude, int GeofenceRadiusMeters);

public sealed class CustomerMap(ICustomerStore customers, IPermissionStore permissions)
{
    public const int Max = 5000;

    public async Task<IReadOnlyList<MapCustomerDto>?> ListAsync(TenantId tenantId, UserId actorId, CancellationToken cancellationToken)
    {
        if (tenantId.IsEmpty || actorId.IsEmpty) return null;
        var scope = await permissions.GetAccessScopeAsync(tenantId, actorId, PermissionKeys.CustomersRead, cancellationToken);
        if (scope is null) return null;
        var result = new List<MapCustomerDto>();
        DateTimeOffset? cursorAt = null;
        Guid? cursorId = null;
        while (result.Count < Max)
        {
            var page = await customers.ListAsync(tenantId, scope, 500, cursorAt, cursorId, cancellationToken);
            result.AddRange(page.Where(c => c.Latitude is not null && c.Longitude is not null)
                .Select(c => new MapCustomerDto(c.Id, c.Code, c.Name, c.Type, c.Latitude!.Value, c.Longitude!.Value, c.GeofenceRadiusMeters)));
            if (page.Count < 500) break;
            cursorAt = page[^1].CreatedAt;
            cursorId = page[^1].Id;
        }
        return result.Take(Max).ToList();
    }
}
