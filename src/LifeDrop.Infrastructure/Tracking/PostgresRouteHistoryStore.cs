using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LifeDrop.Application.Roles;
using LifeDrop.Application.Tracking;
using LifeDrop.Domain.Common;
using Npgsql;
using NpgsqlTypes;

namespace LifeDrop.Infrastructure.Tracking;

public sealed class PostgresRouteHistoryStore(NpgsqlDataSource dataSource) : IRouteHistoryStore
{
    private static async Task<NpgsqlTransaction> BeginAsync(NpgsqlConnection connection, TenantId tenantId, UserId? actorId, CancellationToken cancellationToken)
    {
        var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var set = new NpgsqlCommand("SELECT set_config('app.current_tenant', @tenant, true), set_config('app.current_actor', @actor, true)", connection, transaction);
        set.Parameters.AddWithValue("tenant", tenantId.Value.ToString());
        set.Parameters.AddWithValue("actor", actorId?.Value.ToString() ?? "");
        await set.ExecuteNonQueryAsync(cancellationToken);
        return transaction;
    }

    public async Task<IReadOnlyList<TrackableUserDto>> ListUsersAsync(TenantId tenantId, AccessScope scope, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, null, cancellationToken);
        // Scope 2 = tenant, 1 = region (the actor and members of teams in the region), 0 = own (DataScope values). users has no RLS, so the tenant filter is explicit.
        await using var command = new NpgsqlCommand("""
            SELECT u.id, u.email, t.name
            FROM users u
            LEFT JOIN team_members m ON m.user_id = u.id
            LEFT JOIN teams t ON t.id = m.team_id
            WHERE u.tenant_id = @tenant AND u.is_active
              AND (@scope = 2 OR u.id = @actor OR (@scope = 1 AND @region::uuid IS NOT NULL AND t.region_id = @region))
              AND EXISTS (SELECT 1 FROM shifts s WHERE s.tenant_id = u.tenant_id AND s.user_id = u.id)
            ORDER BY u.email COLLATE "C" NULLS LAST, u.id
            """, connection, transaction);
        command.Parameters.AddWithValue("tenant", tenantId.Value);
        command.Parameters.AddWithValue("scope", (int)scope.Scope);
        command.Parameters.AddWithValue("actor", scope.ActorId.Value);
        command.Parameters.Add(new NpgsqlParameter("region", NpgsqlDbType.Uuid) { Value = (object?)scope.RegionId ?? DBNull.Value });
        var users = new List<TrackableUserDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) users.Add(new TrackableUserDto(reader.GetGuid(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2)));
        return users;
    }

    public async Task<IReadOnlyList<RouteDayDto>> ListDaysAsync(TenantId tenantId, UserId userId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, null, cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT d.day, d.shifts, COALESCE(p.points, 0)
            FROM (SELECT (s.started_at AT TIME ZONE 'Asia/Baghdad')::date AS day, count(*)::int AS shifts
                  FROM shifts s
                  WHERE s.tenant_id = @tenant AND s.user_id = @user AND s.started_at >= @from AND s.started_at < @to
                  GROUP BY 1) d
            LEFT JOIN (SELECT (t.captured_at AT TIME ZONE 'Asia/Baghdad')::date AS day, count(*)::int AS points
                       FROM tracking_points t
                       WHERE t.tenant_id = @tenant AND t.user_id = @user AND t.captured_at >= @from AND t.captured_at < @to
                       GROUP BY 1) p ON p.day = d.day
            ORDER BY d.day
            """, connection, transaction);
        command.Parameters.AddWithValue("tenant", tenantId.Value);
        command.Parameters.AddWithValue("user", userId.Value);
        command.Parameters.Add(new NpgsqlParameter("from", NpgsqlDbType.TimestampTz) { Value = from.ToUniversalTime() });   // Npgsql accepts only offset 0
        command.Parameters.Add(new NpgsqlParameter("to", NpgsqlDbType.TimestampTz) { Value = to.ToUniversalTime() });
        var days = new List<RouteDayDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) days.Add(new RouteDayDto(DateOnly.FromDateTime(reader.GetDateTime(0)), reader.GetInt32(1), reader.GetInt32(2)));
        return days;
    }

    public async Task<RouteRaw> LoadAsync(TenantId tenantId, UserId userId, DateTimeOffset from, DateTimeOffset to, int maxPoints, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, null, cancellationToken);
        string? email;
        await using (var user = new NpgsqlCommand("SELECT email FROM users WHERE tenant_id = @tenant AND id = @id", connection, transaction))
        {
            user.Parameters.AddWithValue("tenant", tenantId.Value);
            user.Parameters.AddWithValue("id", userId.Value);
            email = await user.ExecuteScalarAsync(cancellationToken) as string;
        }

        var shifts = new List<RouteShiftDto>();
        await using (var command = new NpgsqlCommand("""
            SELECT s.id, s.started_at, s.ended_at, coalesce(sm.point_count, (SELECT count(*) FROM tracking_points p WHERE p.shift_id = s.id))::int, sm.shift_id IS NOT NULL
            FROM shifts s LEFT JOIN shift_summaries sm ON sm.tenant_id = s.tenant_id AND sm.shift_id = s.id
            WHERE s.tenant_id = @tenant AND s.user_id = @user AND s.started_at < @to AND coalesce(s.ended_at, 'infinity'::timestamptz) >= @from
            ORDER BY s.started_at
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("tenant", tenantId.Value);
            command.Parameters.AddWithValue("user", userId.Value);
            command.Parameters.Add(new NpgsqlParameter("from", NpgsqlDbType.TimestampTz) { Value = from.ToUniversalTime() });
            command.Parameters.Add(new NpgsqlParameter("to", NpgsqlDbType.TimestampTz) { Value = to.ToUniversalTime() });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                shifts.Add(new RouteShiftDto(reader.GetGuid(0), reader.GetFieldValue<DateTimeOffset>(1), reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTimeOffset>(2), reader.GetInt32(3), reader.GetBoolean(4)));
        }

        var points = new List<RoutePointDto>();
        await using (var command = new NpgsqlCommand("""
            SELECT ST_Y(p.geom::geometry), ST_X(p.geom::geometry), p.accuracy_m::float8, p.captured_at, p.is_mocked
            FROM tracking_points p WHERE p.tenant_id = @tenant AND p.user_id = @user AND p.captured_at >= @from AND p.captured_at < @to
            ORDER BY p.captured_at, p.id LIMIT @limit
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("tenant", tenantId.Value);
            command.Parameters.AddWithValue("user", userId.Value);
            command.Parameters.Add(new NpgsqlParameter("from", NpgsqlDbType.TimestampTz) { Value = from.ToUniversalTime() });
            command.Parameters.Add(new NpgsqlParameter("to", NpgsqlDbType.TimestampTz) { Value = to.ToUniversalTime() });
            command.Parameters.AddWithValue("limit", maxPoints + 1);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                points.Add(new RoutePointDto(reader.GetDouble(0), reader.GetDouble(1), reader.GetDouble(2), reader.GetFieldValue<DateTimeOffset>(3), reader.GetBoolean(4)));
        }
        var truncated = points.Count > maxPoints;
        if (truncated) points.RemoveAt(points.Count - 1);

        var events = new List<RouteVisitEventDto>();
        await using (var command = new NpgsqlCommand("""
            SELECT e.kind, e.accepted, e.rejection_reason, e.captured_at, ST_Y(e.geom::geometry), ST_X(e.geom::geometry), c.name, e.radius_m, e.is_mocked
            FROM visit_location_events e
            JOIN visits v ON v.tenant_id = e.tenant_id AND v.id = e.visit_id
            JOIN customers c ON c.tenant_id = v.tenant_id AND c.id = v.customer_id
            WHERE e.tenant_id = @tenant AND e.user_id = @user AND e.captured_at >= @from AND e.captured_at < @to
            ORDER BY e.captured_at, e.id
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("tenant", tenantId.Value);
            command.Parameters.AddWithValue("user", userId.Value);
            command.Parameters.Add(new NpgsqlParameter("from", NpgsqlDbType.TimestampTz) { Value = from.ToUniversalTime() });
            command.Parameters.Add(new NpgsqlParameter("to", NpgsqlDbType.TimestampTz) { Value = to.ToUniversalTime() });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                events.Add(new RouteVisitEventDto(reader.GetString(0), reader.GetBoolean(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetFieldValue<DateTimeOffset>(3), reader.GetDouble(4),
                    reader.GetDouble(5), reader.IsDBNull(6) ? null : reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetInt32(7), reader.GetBoolean(8)));
        }
        return new RouteRaw(email, shifts, points, events, truncated);
    }

    public async Task<NearCustomer?> NearestCustomerAsync(TenantId tenantId, double latitude, double longitude, int maxMeters, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, null, cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT c.name, ST_Distance(c.geom, p.geom)::float8
            FROM customers c, (SELECT ST_SetSRID(ST_MakePoint(@lng, @lat), 4326)::geography AS geom) p
            WHERE c.tenant_id = @tenant AND c.geom IS NOT NULL AND ST_DWithin(c.geom, p.geom, @max)
            ORDER BY ST_Distance(c.geom, p.geom), c.id LIMIT 1
            """, connection, transaction);
        command.Parameters.AddWithValue("tenant", tenantId.Value);
        command.Parameters.AddWithValue("lat", latitude);
        command.Parameters.AddWithValue("lng", longitude);
        command.Parameters.AddWithValue("max", (double)maxMeters);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? new NearCustomer(reader.GetString(0), (int)Math.Round(reader.GetDouble(1))) : null;
    }

    public async Task AuditViewAsync(TenantId tenantId, UserId actorId, string entity, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, actorId, cancellationToken);
        await using (var audit = new NpgsqlCommand("""
            INSERT INTO audit_logs (id, tenant_id, actor_id, action, entity_id, occurred_at) VALUES (gen_random_uuid(), @tenant, @actor, 'tracking.history_viewed', @entity, now())
            """, connection, transaction))
        {
            audit.Parameters.AddWithValue("tenant", tenantId.Value);
            audit.Parameters.AddWithValue("actor", actorId.Value);
            audit.Parameters.AddWithValue("entity", entity);
            await audit.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<int> PurgeAsync(TenantId tenantId, DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, null, cancellationToken);
        await using (var summary = new NpgsqlCommand("""
            WITH old AS (SELECT sh.id FROM shifts sh WHERE sh.tenant_id = @tenant AND sh.ended_at < @cutoff AND EXISTS (SELECT 1 FROM tracking_points p WHERE p.shift_id = sh.id)),
            gaps AS (
                SELECT p.shift_id, extract(epoch FROM p.captured_at - lag(p.captured_at) OVER (PARTITION BY p.shift_id ORDER BY p.captured_at)) AS g
                FROM tracking_points p WHERE p.tenant_id = @tenant AND p.shift_id IN (SELECT id FROM old)),
            agg AS (
                SELECT p.shift_id, count(*)::int AS n, min(p.captured_at) AS first_at, max(p.captured_at) AS last_at, (count(*) FILTER (WHERE p.is_mocked))::int AS mocked,
                       ST_Length(ST_MakeLine(p.geom::geometry ORDER BY p.captured_at)::geography) AS dist
                FROM tracking_points p WHERE p.tenant_id = @tenant AND p.shift_id IN (SELECT id FROM old) GROUP BY p.shift_id)
            INSERT INTO shift_summaries (shift_id, tenant_id, point_count, first_point_at, last_point_at, max_gap_seconds, distance_m, mocked_points)
            SELECT a.shift_id, @tenant, a.n, a.first_at, a.last_at, coalesce((SELECT round(max(g.g))::int FROM gaps g WHERE g.shift_id = a.shift_id), 0), round(coalesce(a.dist, 0)::numeric, 1), a.mocked
            FROM agg a
            ON CONFLICT (shift_id) DO NOTHING
            """, connection, transaction))
        {
            summary.Parameters.AddWithValue("tenant", tenantId.Value);
            summary.Parameters.Add(new NpgsqlParameter("cutoff", NpgsqlDbType.TimestampTz) { Value = cutoff.ToUniversalTime() });
            await summary.ExecuteNonQueryAsync(cancellationToken);
        }
        int deleted;
        await using (var delete = new NpgsqlCommand("""
            DELETE FROM tracking_points WHERE tenant_id = @tenant AND shift_id IN (SELECT id FROM shifts WHERE tenant_id = @tenant AND ended_at < @cutoff)
            """, connection, transaction))
        {
            delete.Parameters.AddWithValue("tenant", tenantId.Value);
            delete.Parameters.Add(new NpgsqlParameter("cutoff", NpgsqlDbType.TimestampTz) { Value = cutoff.ToUniversalTime() });
            deleted = await delete.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return deleted;
    }
}
