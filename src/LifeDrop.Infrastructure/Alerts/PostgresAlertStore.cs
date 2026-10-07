using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LifeDrop.Application.Alerts;
using LifeDrop.Application.Tracking;
using LifeDrop.Domain.Common;
using Npgsql;
using NpgsqlTypes;

namespace LifeDrop.Infrastructure.Alerts;

public sealed class PostgresAlertStore(NpgsqlDataSource dataSource) : IAlertStore
{
    private static NpgsqlParameter P(string name, NpgsqlDbType type, object? value) => new(name, type) { Value = value ?? DBNull.Value };

    private static async Task<NpgsqlTransaction> BeginAsync(NpgsqlConnection connection, TenantId tenantId, UserId? actorId, CancellationToken cancellationToken)
    {
        var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var set = new NpgsqlCommand("SELECT set_config('app.current_tenant', @tenant, true), set_config('app.current_actor', @actor, true)", connection, transaction);
        set.Parameters.AddWithValue("tenant", tenantId.Value.ToString());
        set.Parameters.AddWithValue("actor", actorId?.Value.ToString() ?? "");
        await set.ExecuteNonQueryAsync(cancellationToken);
        return transaction;
    }

    // (category, region): a category is visible tenant-wide (region null) or only for the alerts about users of one region.
    private static string Visibility(IReadOnlyList<AlertAudience> audiences, NpgsqlCommand command)
    {
        var parts = new List<string>();
        for (var i = 0; i < audiences.Count; i++)
        {
            command.Parameters.AddWithValue($"cat{i}", audiences[i].Category);
            if (audiences[i].RegionId is { } region)
            {
                command.Parameters.AddWithValue($"reg{i}", region);
                parts.Add($"(a.category = @cat{i} AND a.region_id = @reg{i})");
            }
            else parts.Add($"(a.category = @cat{i})");
        }
        return parts.Count == 0 ? "FALSE" : "(" + string.Join(" OR ", parts) + ")";
    }

    private const string Columns = """
        a.id, a.kind, a.category, a.severity, a.subject_user_id, u.email, a.customer_id, c.code, c.name, a.entity_id, a.detail, a.status, a.occurred_at, a.created_at, a.seen_at,
        s.email, a.handled_at, h.email, a.handled_note, a.evidence::text, a.handled_outcome
        """;

    private const string Joins = """
        FROM alerts a
        LEFT JOIN users u ON u.tenant_id = a.tenant_id AND u.id = a.subject_user_id
        LEFT JOIN customers c ON c.tenant_id = a.tenant_id AND c.id = a.customer_id
        LEFT JOIN users s ON s.tenant_id = a.tenant_id AND s.id = a.seen_by
        LEFT JOIN users h ON h.tenant_id = a.tenant_id AND h.id = a.handled_by
        """;

    private static AlertDto Read(NpgsqlDataReader r) => new(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetGuid(4), r.IsDBNull(5) ? null : r.GetString(5),
        r.IsDBNull(6) ? null : r.GetGuid(6), r.IsDBNull(7) ? null : r.GetString(7), r.IsDBNull(8) ? null : r.GetString(8), r.GetString(9), r.IsDBNull(10) ? null : r.GetString(10), r.GetString(11),
        r.GetFieldValue<DateTimeOffset>(12), r.GetFieldValue<DateTimeOffset>(13), r.IsDBNull(14) ? null : r.GetFieldValue<DateTimeOffset>(14), r.IsDBNull(15) ? null : r.GetString(15),
        r.IsDBNull(16) ? null : r.GetFieldValue<DateTimeOffset>(16), r.IsDBNull(17) ? null : r.GetString(17), r.IsDBNull(18) ? null : r.GetString(18),
        r.IsDBNull(19) ? null : r.GetString(19), r.IsDBNull(20) ? null : r.GetString(20));

    public async Task<IReadOnlyList<AlertDto>> ListAsync(TenantId tenantId, IReadOnlyList<AlertAudience> audiences, string? category, string? status, string? kind, int limit, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, null, cancellationToken);
        await using var command = new NpgsqlCommand("", connection, transaction);
        var visibility = Visibility(audiences, command);
        command.CommandText = $"""
            SELECT {Columns} {Joins}
            WHERE a.tenant_id = @tenant AND {visibility}
              AND (@category IS NULL OR a.category = @category) AND (@status IS NULL OR a.status = @status) AND (@kind IS NULL OR a.kind = @kind)
            ORDER BY (a.status = 'handled'), a.occurred_at DESC, a.id LIMIT @limit
            """;
        command.Parameters.AddWithValue("tenant", tenantId.Value);
        command.Parameters.Add(P("category", NpgsqlDbType.Text, category));
        command.Parameters.Add(P("status", NpgsqlDbType.Text, status));
        command.Parameters.Add(P("kind", NpgsqlDbType.Text, kind));
        command.Parameters.AddWithValue("limit", limit);
        var items = new List<AlertDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) items.Add(Read(reader));
        return items;
    }

    public async Task<int> CountNewAsync(TenantId tenantId, IReadOnlyList<AlertAudience> audiences, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, null, cancellationToken);
        await using var command = new NpgsqlCommand("", connection, transaction);
        command.CommandText = $"SELECT count(*) FROM alerts a WHERE a.tenant_id = @tenant AND a.status = 'new' AND {Visibility(audiences, command)}";
        command.Parameters.AddWithValue("tenant", tenantId.Value);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    public async Task<AlertDto?> GetAsync(TenantId tenantId, Guid alertId, IReadOnlyList<AlertAudience> audiences, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, null, cancellationToken);
        await using var command = new NpgsqlCommand("", connection, transaction);
        command.CommandText = $"SELECT {Columns} {Joins} WHERE a.tenant_id = @tenant AND a.id = @id AND {Visibility(audiences, command)}";
        command.Parameters.AddWithValue("tenant", tenantId.Value);
        command.Parameters.AddWithValue("id", alertId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    public Task<AlertResult> MarkSeenAsync(TenantId tenantId, UserId actorId, Guid alertId, DateTimeOffset now, CancellationToken cancellationToken) =>
        ChangeAsync(tenantId, actorId, alertId, "alert.seen", "status = 'seen', seen_at = @now, seen_by = @actor", "status = 'new'", now, _ => { }, cancellationToken);

    public Task<AlertResult> HandleAsync(TenantId tenantId, UserId actorId, Guid alertId, string note, string? outcome, DateTimeOffset now, CancellationToken cancellationToken) =>
        ChangeAsync(tenantId, actorId, alertId, "alert.handled", "status = 'handled', handled_at = @now, handled_by = @actor, handled_note = @note, handled_outcome = @outcome", "status IN ('new', 'seen')", now,
            c =>
            {
                c.Parameters.AddWithValue("note", note);
                c.Parameters.Add(P("outcome", NpgsqlDbType.Text, outcome));
            }, cancellationToken);

    private async Task<AlertResult> ChangeAsync(TenantId tenantId, UserId actorId, Guid alertId, string action, string set, string whenStatus, DateTimeOffset now, Action<NpgsqlCommand> bind,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, actorId, cancellationToken);
        await using (var update = new NpgsqlCommand($"UPDATE alerts SET {set} WHERE tenant_id = @tenant AND id = @id AND {whenStatus}", connection, transaction))
        {
            update.Parameters.AddWithValue("tenant", tenantId.Value);
            update.Parameters.AddWithValue("id", alertId);
            update.Parameters.AddWithValue("actor", actorId.Value);
            update.Parameters.Add(P("now", NpgsqlDbType.TimestampTz, now));
            bind(update);
            if (await update.ExecuteNonQueryAsync(cancellationToken) == 0) return AlertResult.Conflict;
        }
        await using (var audit = new NpgsqlCommand("""
            INSERT INTO audit_logs (id, tenant_id, actor_id, action, entity_id, occurred_at) VALUES (gen_random_uuid(), @tenant, @actor, @action, @entity, now())
            """, connection, transaction))
        {
            audit.Parameters.AddWithValue("tenant", tenantId.Value);
            audit.Parameters.AddWithValue("actor", actorId.Value);
            audit.Parameters.AddWithValue("action", action);
            audit.Parameters.AddWithValue("entity", alertId.ToString());
            await audit.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return AlertResult.Success;
    }

    // The region of the user an alert is about: the region of the user's team.
    private const string RegionOf = "(SELECT t.region_id FROM team_members m JOIN teams t ON t.id = m.team_id WHERE m.user_id = {0} LIMIT 1)";

    public async Task<int> ScanAsync(TenantId tenantId, DateTimeOffset now, DateOnly today, TimeOnly localTime, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, null, cancellationToken);
        var created = 0;

        async Task RunAsync(string sql)
        {
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            command.Parameters.AddWithValue("tenant", tenantId.Value);
            command.Parameters.Add(P("now", NpgsqlDbType.TimestampTz, now));
            command.Parameters.Add(P("today", NpgsqlDbType.Date, today));
            command.Parameters.Add(P("localTime", NpgsqlDbType.Time, localTime));
            command.Parameters.Add(P("delay", NpgsqlDbType.Interval, AlertRules.VisitDelay));
            command.Parameters.Add(P("stay", NpgsqlDbType.Interval, AlertRules.LongStay));
            command.Parameters.Add(P("gap", NpgsqlDbType.Interval, AlertRules.TrackingGap));
            command.Parameters.Add(P("window", NpgsqlDbType.Interval, AlertRules.ScanWindow));
            command.Parameters.Add(P("clockAhead", NpgsqlDbType.Interval, AlertRules.ClockAhead));
            command.Parameters.AddWithValue("maxSpeed", AlertRules.MaxSpeedKmh);
            command.Parameters.AddWithValue("minJump", AlertRules.MinJumpMeters);
            command.Parameters.AddWithValue("areaRadius", AlertRules.AreaRadiusMeters);
            command.Parameters.AddWithValue("poorMeters", AlertRules.PoorAccuracyMeters);
            command.Parameters.AddWithValue("poorCount", AlertRules.PoorAccuracyCount);
            command.Parameters.Add(P("minStay", NpgsqlDbType.Interval, AlertRules.MinStay));
            command.Parameters.Add(P("coverageMinShift", NpgsqlDbType.Interval, AlertRules.CoverageMinShift));
            command.Parameters.AddWithValue("coveragePercent", AlertRules.LowCoveragePercent);
            command.Parameters.AddWithValue("expectedSeconds", ShiftTracking.ExpectedInterval.TotalSeconds);
            created += await command.ExecuteNonQueryAsync(cancellationToken);
        }

        // An open follow-up whose due day has passed.
        await RunAsync($"""
            INSERT INTO alerts (tenant_id, kind, category, severity, subject_user_id, region_id, customer_id, entity_id, dedupe_key, occurred_at)
            SELECT f.tenant_id, 'followup_overdue', 'operational', 'warning', f.assignee_id, {string.Format(RegionOf, "f.assignee_id")}, f.customer_id, f.id::text, 'followup_overdue:' || f.id, @now
            FROM follow_ups f WHERE f.tenant_id = @tenant AND f.status IN ('pending', 'in_progress') AND f.due_date < @today
            ON CONFLICT (tenant_id, dedupe_key) DO NOTHING
            """);
        // A planned visit of today with a planned time that did not start in time.
        await RunAsync($"""
            INSERT INTO alerts (tenant_id, kind, category, severity, subject_user_id, region_id, customer_id, entity_id, dedupe_key, occurred_at)
            SELECT v.tenant_id, 'visit_delayed', 'operational', 'warning', v.user_id, {string.Format(RegionOf, "v.user_id")}, v.customer_id, v.id::text, 'visit_delayed:' || v.id, @now
            FROM visits v WHERE v.tenant_id = @tenant AND v.status = 'Planned' AND v.schedule_date = @today AND v.planned_time IS NOT NULL AND v.planned_time + @delay < @localTime
            ON CONFLICT (tenant_id, dedupe_key) DO NOTHING
            """);
        // A visit that is in progress for too long; the detail says whether the report is saved.
        await RunAsync($"""
            INSERT INTO alerts (tenant_id, kind, category, severity, subject_user_id, region_id, customer_id, entity_id, detail, dedupe_key, occurred_at)
            SELECT v.tenant_id, 'visit_long_stay', 'operational', 'warning', v.user_id, {string.Format(RegionOf, "v.user_id")}, v.customer_id, v.id::text,
                   CASE WHEN EXISTS (SELECT 1 FROM visit_reports r WHERE r.visit_id = v.id) THEN 'report_saved' ELSE 'report_missing' END, 'visit_long_stay:' || v.id, @now
            FROM visits v
            JOIN LATERAL (SELECT min(h.occurred_at) AS since FROM visit_status_history h WHERE h.visit_id = v.id AND h.to_status = 'InVisit') s ON s.since IS NOT NULL
            WHERE v.tenant_id = @tenant AND v.status = 'InVisit' AND s.since + @stay < @now
            ON CONFLICT (tenant_id, dedupe_key) DO NOTHING
            """);
        // An open shift without a point for a while.
        await RunAsync($"""
            INSERT INTO alerts (tenant_id, kind, category, severity, subject_user_id, region_id, entity_id, dedupe_key, occurred_at)
            SELECT sh.tenant_id, 'tracking_gap', 'operational', 'warning', sh.user_id, {string.Format(RegionOf, "sh.user_id")}, sh.id::text, 'tracking_gap:' || sh.id, @now
            FROM shifts sh
            WHERE sh.tenant_id = @tenant AND sh.ended_at IS NULL
              AND coalesce((SELECT max(p.captured_at) FROM tracking_points p WHERE p.shift_id = sh.id), sh.started_at) + @gap < @now
            ON CONFLICT (tenant_id, dedupe_key) DO NOTHING
            """);
        // Security: a phone that waits for approval.
        await RunAsync($"""
            INSERT INTO alerts (tenant_id, kind, category, severity, subject_user_id, region_id, entity_id, detail, dedupe_key, occurred_at)
            SELECT d.tenant_id, 'new_device', 'security', 'info', d.user_id, {string.Format(RegionOf, "d.user_id")}, d.id::text, left(d.device_label, 200), 'new_device:' || d.id, d.registered_at
            FROM devices d WHERE d.tenant_id = @tenant AND d.status = 'pending'
            ON CONFLICT (tenant_id, dedupe_key) DO NOTHING
            """);
        // Security: a check-in/out position the phone says is fake, and a shift with fake tracking points.
        await RunAsync($"""
            INSERT INTO alerts (tenant_id, kind, category, severity, subject_user_id, region_id, customer_id, entity_id, detail, dedupe_key, occurred_at)
            SELECT e.tenant_id, 'gps_mocked', 'security', 'warning', e.user_id, {string.Format(RegionOf, "e.user_id")}, v.customer_id, e.visit_id::text, e.kind, 'gps_mocked:event:' || e.id, e.captured_at
            FROM visit_location_events e JOIN visits v ON v.tenant_id = e.tenant_id AND v.id = e.visit_id
            WHERE e.tenant_id = @tenant AND (e.is_mocked OR e.rejection_reason = 'mock_location')
            ON CONFLICT (tenant_id, dedupe_key) DO NOTHING
            """);
        await RunAsync($"""
            INSERT INTO alerts (tenant_id, kind, category, severity, subject_user_id, region_id, entity_id, detail, dedupe_key, occurred_at)
            SELECT p.tenant_id, 'gps_mocked', 'security', 'warning', p.user_id, {string.Format(RegionOf, "p.user_id")}, p.shift_id::text, 'tracking', 'gps_mocked:shift:' || p.shift_id, min(p.captured_at)
            FROM tracking_points p WHERE p.tenant_id = @tenant AND p.is_mocked
            GROUP BY p.tenant_id, p.user_id, p.shift_id
            ON CONFLICT (tenant_id, dedupe_key) DO NOTHING
            """);
        // Anti-fraud (plan step 2.10): signals from the tracking points of shifts that started in the last 48 hours. Evidence is kept in the alert.
        // The first point that breaks the rule in a shift is the evidence; one alert per shift and signal.
        await RunAsync($"""
            INSERT INTO alerts (tenant_id, kind, category, severity, subject_user_id, region_id, entity_id, detail, evidence, dedupe_key, occurred_at)
            SELECT x.tenant_id, 'impossible_speed', 'security', 'warning', x.user_id, {string.Format(RegionOf, "x.user_id")}, x.shift_id::text, round(x.speed)::text || ' km/h',
                   jsonb_build_object('fromPointId', x.prev_id, 'toPointId', x.id, 'fromAt', x.prev_at, 'toAt', x.captured_at, 'distanceM', round(x.dist), 'seconds', round(x.dt), 'speedKmh', round(x.speed),
                                      'fromLat', ST_Y(x.prev_geom::geometry), 'fromLng', ST_X(x.prev_geom::geometry), 'toLat', ST_Y(x.geom::geometry), 'toLng', ST_X(x.geom::geometry)),
                   'impossible_speed:' || x.shift_id, x.captured_at
            FROM (
                SELECT DISTINCT ON (q.shift_id) q.*, ST_Distance(q.geom, q.prev_geom) AS dist, extract(epoch FROM q.captured_at - q.prev_at) AS dt,
                       ST_Distance(q.geom, q.prev_geom) / extract(epoch FROM q.captured_at - q.prev_at) * 3.6 AS speed
                FROM (
                    SELECT p.tenant_id, p.shift_id, p.user_id, p.id, p.geom, p.captured_at,
                           lag(p.id) OVER w AS prev_id, lag(p.geom) OVER w AS prev_geom, lag(p.captured_at) OVER w AS prev_at
                    FROM tracking_points p JOIN shifts sh ON sh.tenant_id = p.tenant_id AND sh.id = p.shift_id
                    WHERE p.tenant_id = @tenant AND sh.started_at > @now - @window
                    WINDOW w AS (PARTITION BY p.shift_id ORDER BY p.captured_at, p.id)
                ) q
                WHERE q.prev_at IS NOT NULL AND q.captured_at > q.prev_at
                  AND ST_Distance(q.geom, q.prev_geom) > @minJump
                  AND ST_Distance(q.geom, q.prev_geom) / extract(epoch FROM q.captured_at - q.prev_at) * 3.6 > @maxSpeed
                ORDER BY q.shift_id, q.captured_at
            ) x
            ON CONFLICT (tenant_id, dedupe_key) DO NOTHING
            """);
        await RunAsync($"""
            INSERT INTO alerts (tenant_id, kind, category, severity, subject_user_id, region_id, entity_id, detail, evidence, dedupe_key, occurred_at)
            SELECT x.tenant_id, 'clock_tamper', 'security', 'warning', x.user_id, {string.Format(RegionOf, "x.user_id")}, x.shift_id::text, round(extract(epoch FROM x.captured_at - x.received_at) / 60)::text || ' min ahead',
                   jsonb_build_object('pointId', x.id, 'capturedAt', x.captured_at, 'receivedAt', x.received_at, 'aheadSeconds', round(extract(epoch FROM x.captured_at - x.received_at))),
                   'clock_tamper:shift:' || x.shift_id, x.captured_at
            FROM (
                SELECT DISTINCT ON (p.shift_id) p.tenant_id, p.shift_id, p.user_id, p.id, p.captured_at, p.received_at
                FROM tracking_points p JOIN shifts sh ON sh.tenant_id = p.tenant_id AND sh.id = p.shift_id
                WHERE p.tenant_id = @tenant AND sh.started_at > @now - @window AND p.captured_at > p.received_at + @clockAhead
                ORDER BY p.shift_id, p.captured_at
            ) x
            ON CONFLICT (tenant_id, dedupe_key) DO NOTHING
            """);
        await RunAsync($"""
            INSERT INTO alerts (tenant_id, kind, category, severity, subject_user_id, region_id, customer_id, entity_id, detail, evidence, dedupe_key, occurred_at)
            SELECT e.tenant_id, 'clock_tamper', 'security', 'warning', e.user_id, {string.Format(RegionOf, "e.user_id")}, v.customer_id, e.visit_id::text, e.kind,
                   jsonb_build_object('eventId', e.id, 'kind', e.kind, 'capturedAt', e.captured_at, 'receivedAt', e.received_at),
                   'clock_tamper:event:' || e.id, e.captured_at
            FROM visit_location_events e JOIN visits v ON v.tenant_id = e.tenant_id AND v.id = e.visit_id
            WHERE e.tenant_id = @tenant AND e.rejection_reason = 'clock_skew' AND e.received_at > @now - @window
            ON CONFLICT (tenant_id, dedupe_key) DO NOTHING
            """);
        await RunAsync($"""
            INSERT INTO alerts (tenant_id, kind, category, severity, subject_user_id, region_id, entity_id, detail, evidence, dedupe_key, occurred_at)
            SELECT x.tenant_id, 'outside_area', 'security', 'warning', x.user_id, x.region_id, x.shift_id::text, round(x.nearest / 1000)::text || ' km',
                   jsonb_build_object('pointId', x.id, 'capturedAt', x.captured_at, 'lat', ST_Y(x.geom::geometry), 'lng', ST_X(x.geom::geometry), 'regionId', x.region_id,
                                      'nearestCustomerM', round(x.nearest), 'radiusM', @areaRadius),
                   'outside_area:' || x.shift_id, x.captured_at
            FROM (
                SELECT DISTINCT ON (p.shift_id) p.tenant_id, p.shift_id, p.user_id, p.id, p.geom, p.captured_at, r.region_id,
                       (SELECT min(ST_Distance(c.geom, p.geom)) FROM customers c WHERE c.tenant_id = p.tenant_id AND c.region_id = r.region_id AND c.geom IS NOT NULL) AS nearest
                FROM tracking_points p
                JOIN shifts sh ON sh.tenant_id = p.tenant_id AND sh.id = p.shift_id
                CROSS JOIN LATERAL (SELECT t.region_id FROM team_members m JOIN teams t ON t.id = m.team_id WHERE m.user_id = p.user_id AND t.region_id IS NOT NULL LIMIT 1) r(region_id)
                WHERE p.tenant_id = @tenant AND sh.started_at > @now - @window
                  AND EXISTS (SELECT 1 FROM customers c WHERE c.tenant_id = p.tenant_id AND c.region_id = r.region_id AND c.geom IS NOT NULL)
                  AND NOT EXISTS (SELECT 1 FROM customers c WHERE c.tenant_id = p.tenant_id AND c.region_id = r.region_id AND c.geom IS NOT NULL AND ST_DWithin(c.geom, p.geom, @areaRadius))
                ORDER BY p.shift_id, p.captured_at
            ) x
            ON CONFLICT (tenant_id, dedupe_key) DO NOTHING
            """);
        await RunAsync($"""
            INSERT INTO alerts (tenant_id, kind, category, severity, subject_user_id, region_id, entity_id, detail, evidence, dedupe_key, occurred_at)
            SELECT x.tenant_id, 'poor_accuracy', 'security', 'info', x.user_id, {string.Format(RegionOf, "x.user_id")}, x.shift_id::text, x.n::text || ' points',
                   jsonb_build_object('points', x.n, 'worseThanM', @poorMeters, 'worstM', x.worst, 'firstAt', x.first_at),
                   'poor_accuracy:' || x.shift_id, x.first_at
            FROM (
                SELECT p.tenant_id, p.shift_id, p.user_id, count(*) AS n, max(p.accuracy_m) AS worst, min(p.captured_at) AS first_at
                FROM tracking_points p JOIN shifts sh ON sh.tenant_id = p.tenant_id AND sh.id = p.shift_id
                WHERE p.tenant_id = @tenant AND sh.started_at > @now - @window AND p.accuracy_m > @poorMeters
                GROUP BY p.tenant_id, p.shift_id, p.user_id HAVING count(*) >= @poorCount
            ) x
            ON CONFLICT (tenant_id, dedupe_key) DO NOTHING
            """);
        await RunAsync($"""
            INSERT INTO alerts (tenant_id, kind, category, severity, subject_user_id, region_id, entity_id, detail, evidence, dedupe_key, occurred_at)
            SELECT x.tenant_id, 'device_mismatch', 'security', 'warning', x.user_id, {string.Format(RegionOf, "x.user_id")}, x.shift_id::text, x.n::text || ' points',
                   jsonb_build_object('shiftDeviceId', x.shift_device, 'otherDeviceIds', x.others, 'points', x.n, 'firstAt', x.first_at),
                   'device_mismatch:' || x.shift_id, x.first_at
            FROM (
                SELECT p.tenant_id, p.shift_id, p.user_id, sh.device_id AS shift_device, count(*) AS n, array_agg(DISTINCT p.device_id) AS others, min(p.captured_at) AS first_at
                FROM tracking_points p JOIN shifts sh ON sh.tenant_id = p.tenant_id AND sh.id = p.shift_id
                WHERE p.tenant_id = @tenant AND sh.started_at > @now - @window AND p.device_id <> sh.device_id
                GROUP BY p.tenant_id, p.shift_id, p.user_id, sh.device_id
            ) x
            ON CONFLICT (tenant_id, dedupe_key) DO NOTHING
            """);
        // Plan step 4.8: signals that need no dense tracking. Travel time between two visits (accepted check-in/out events of one user), a stay that is too short,
        // and a finished shift with too few tracking points.
        await RunAsync($"""
            INSERT INTO alerts (tenant_id, kind, category, severity, subject_user_id, region_id, customer_id, entity_id, detail, evidence, dedupe_key, occurred_at)
            SELECT x.tenant_id, 'impossible_travel', 'security', 'warning', x.user_id, {string.Format(RegionOf, "x.user_id")}, x.customer_id, x.visit_id::text, round(x.speed)::text || ' km/h',
                   jsonb_build_object('fromVisitId', x.prev_visit_id, 'toVisitId', x.visit_id, 'fromAt', x.prev_at, 'toAt', x.captured_at, 'distanceM', round(x.dist), 'seconds', round(x.dt),
                                      'speedKmh', round(x.speed)),
                   'impossible_travel:' || x.visit_id, x.captured_at
            FROM (
                SELECT q.*, v.customer_id, ST_Distance(q.geom, q.prev_geom) AS dist, extract(epoch FROM q.captured_at - q.prev_at) AS dt,
                       ST_Distance(q.geom, q.prev_geom) / extract(epoch FROM q.captured_at - q.prev_at) * 3.6 AS speed
                FROM (
                    SELECT e.tenant_id, e.user_id, e.visit_id, e.kind, e.geom, e.captured_at,
                           lag(e.visit_id) OVER w AS prev_visit_id, lag(e.geom) OVER w AS prev_geom, lag(e.captured_at) OVER w AS prev_at
                    FROM visit_location_events e
                    WHERE e.tenant_id = @tenant AND e.accepted AND e.captured_at > @now - @window - @window
                    WINDOW w AS (PARTITION BY e.user_id ORDER BY e.captured_at, e.id)
                ) q
                JOIN visits v ON v.tenant_id = q.tenant_id AND v.id = q.visit_id
                WHERE q.kind = 'check_in' AND q.prev_visit_id IS NOT NULL AND q.prev_visit_id <> q.visit_id AND q.captured_at > q.prev_at AND q.captured_at > @now - @window
                  AND ST_Distance(q.geom, q.prev_geom) > @minJump
                  AND ST_Distance(q.geom, q.prev_geom) / extract(epoch FROM q.captured_at - q.prev_at) * 3.6 > @maxSpeed
            ) x
            ON CONFLICT (tenant_id, dedupe_key) DO NOTHING
            """);
        await RunAsync($"""
            INSERT INTO alerts (tenant_id, kind, category, severity, subject_user_id, region_id, customer_id, entity_id, detail, evidence, dedupe_key, occurred_at)
            SELECT i.tenant_id, 'short_stay', 'security', 'info', i.user_id, {string.Format(RegionOf, "i.user_id")}, v.customer_id, i.visit_id::text, round(extract(epoch FROM o.captured_at - i.captured_at))::text || ' s',
                   jsonb_build_object('checkInAt', i.captured_at, 'checkOutAt', o.captured_at, 'seconds', round(extract(epoch FROM o.captured_at - i.captured_at)), 'minSeconds', extract(epoch FROM @minStay)),
                   'short_stay:' || i.visit_id, o.captured_at
            FROM visit_location_events i
            JOIN visit_location_events o ON o.tenant_id = i.tenant_id AND o.visit_id = i.visit_id AND o.kind = 'check_out' AND o.accepted
            JOIN visits v ON v.tenant_id = i.tenant_id AND v.id = i.visit_id
            WHERE i.tenant_id = @tenant AND i.kind = 'check_in' AND i.accepted AND o.captured_at > @now - @window
              AND o.captured_at >= i.captured_at AND o.captured_at - i.captured_at < @minStay
            ON CONFLICT (tenant_id, dedupe_key) DO NOTHING
            """);
        await RunAsync($"""
            INSERT INTO alerts (tenant_id, kind, category, severity, subject_user_id, region_id, entity_id, detail, evidence, dedupe_key, occurred_at)
            SELECT x.tenant_id, 'low_coverage', 'operational', 'warning', x.user_id, {string.Format(RegionOf, "x.user_id")}, x.id::text, x.coverage::text || ' %',
                   jsonb_build_object('points', x.n, 'expectedPoints', x.expected, 'coveragePercent', x.coverage, 'startedAt', x.started_at, 'endedAt', x.ended_at),
                   'low_coverage:' || x.id, x.ended_at
            FROM (
                SELECT sh.tenant_id, sh.id, sh.user_id, sh.started_at, sh.ended_at, p.n, e.expected, round((100.0 * p.n / e.expected)::numeric, 1) AS coverage
                FROM shifts sh
                CROSS JOIN LATERAL (SELECT count(*) AS n FROM tracking_points tp WHERE tp.tenant_id = sh.tenant_id AND tp.shift_id = sh.id) p
                CROSS JOIN LATERAL (SELECT greatest(1, floor(extract(epoch FROM sh.ended_at - sh.started_at) / @expectedSeconds)) AS expected) e
                WHERE sh.tenant_id = @tenant AND sh.ended_at IS NOT NULL AND sh.started_at > @now - @window AND sh.ended_at - sh.started_at >= @coverageMinShift
            ) x
            WHERE x.coverage < @coveragePercent
            ON CONFLICT (tenant_id, dedupe_key) DO NOTHING
            """);
        await transaction.CommitAsync(cancellationToken);
        return created;
    }

    public async Task<IReadOnlyList<TenantId>> ListActiveTenantsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("SELECT id FROM tenants WHERE is_active", connection);
        var ids = new List<TenantId>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) ids.Add(new TenantId(reader.GetGuid(0)));
        return ids;
    }
}
