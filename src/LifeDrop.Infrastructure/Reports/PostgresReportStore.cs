using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LifeDrop.Application.Reports;
using LifeDrop.Application.Roles;
using LifeDrop.Application.Alerts;
using LifeDrop.Domain.Common;
using Npgsql;
using NpgsqlTypes;

namespace LifeDrop.Infrastructure.Reports;

public sealed class PostgresReportStore(NpgsqlDataSource dataSource) : IReportStore
{
    private static NpgsqlParameter P(string name, NpgsqlDbType type, object? value) => new(name, type) { Value = value ?? DBNull.Value };

    private static async Task<NpgsqlTransaction> BeginAsync(NpgsqlConnection connection, TenantId tenantId, CancellationToken cancellationToken)
    {
        var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var set = new NpgsqlCommand("SELECT set_config('app.current_tenant', @tenant, true)", connection, transaction);
        set.Parameters.AddWithValue("tenant", tenantId.Value.ToString());
        await set.ExecuteNonQueryAsync(cancellationToken);
        return transaction;
    }

    private static void AddFilter(NpgsqlCommand command, TenantId tenantId, KpiFilter filter, Guid[] users)
    {
        command.Parameters.AddWithValue("tenant", tenantId.Value);
        command.Parameters.Add(P("from", NpgsqlDbType.Date, filter.From));
        command.Parameters.Add(P("to", NpgsqlDbType.Date, filter.To));
        command.Parameters.Add(P("ctype", NpgsqlDbType.Text, filter.CustomerType));
        command.Parameters.Add(P("users", NpgsqlDbType.Array | NpgsqlDbType.Uuid, users));
    }

    public async Task<IReadOnlyList<KpiUser>> ListUsersAsync(TenantId tenantId, AccessScope scope, KpiFilter filter, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, cancellationToken);
        // Scope 2 = tenant, 1 = region (the actor and members of teams in the region), 0 = own (DataScope values).
        await using var command = new NpgsqlCommand("""
            SELECT u.id, u.email, t.name
            FROM users u
            LEFT JOIN team_members m ON m.user_id = u.id
            LEFT JOIN teams t ON t.id = m.team_id
            WHERE u.tenant_id = @tenant AND u.is_active
              AND (@scope = 2 OR u.id = @actor OR (@scope = 1 AND @scopeRegion::uuid IS NOT NULL AND t.region_id = @scopeRegion))
              AND (@team::uuid IS NULL OR t.id = @team) AND (@region::uuid IS NULL OR t.region_id = @region)
            ORDER BY u.email COLLATE "C" NULLS LAST, u.id
            """, connection, transaction);
        command.Parameters.AddWithValue("tenant", tenantId.Value);
        command.Parameters.AddWithValue("scope", (int)scope.Scope);
        command.Parameters.AddWithValue("actor", scope.ActorId.Value);
        command.Parameters.Add(P("scopeRegion", NpgsqlDbType.Uuid, scope.RegionId));
        command.Parameters.Add(P("team", NpgsqlDbType.Uuid, filter.TeamId));
        command.Parameters.Add(P("region", NpgsqlDbType.Uuid, filter.RegionId));
        var users = new List<KpiUser>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) users.Add(new KpiUser(reader.GetGuid(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2)));
        return users;
    }

    private sealed class Acc
    {
        public int Planned, Completed, Failed, DelayBase, Delayed, NewCustomers, ApprovedNew, Orders, Delivered, DeliveryFailed, FollowBase, Overdue;
        public List<CurrencyTotal> Totals = [];
    }

    public async Task<IReadOnlyDictionary<Guid, KpiCounts>> CountsAsync(TenantId tenantId, IReadOnlyList<Guid> userIds, KpiFilter filter, DateOnly today, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, cancellationToken);
        var users = userIds.ToArray();
        var acc = new Dictionary<Guid, Acc>();
        Acc Of(Guid id) => acc.TryGetValue(id, out var a) ? a : acc[id] = new Acc();

        async Task QueryAsync(string sql, Action<NpgsqlCommand> extra, Action<NpgsqlDataReader> read)
        {
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            AddFilter(command, tenantId, filter, users);
            extra(command);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) read(reader);
        }

        // Visits scheduled in the range (a cancelled visit is not planned work).
        await QueryAsync("""
            SELECT v.user_id, count(*) FILTER (WHERE v.status <> 'Cancelled')::int, count(*) FILTER (WHERE v.status = 'Completed')::int, count(*) FILTER (WHERE v.status = 'Failed')::int
            FROM visits v JOIN customers c ON c.tenant_id = v.tenant_id AND c.id = v.customer_id
            WHERE v.tenant_id = @tenant AND v.user_id = ANY(@users) AND v.schedule_date BETWEEN @from AND @to AND (@ctype::text IS NULL OR c.type = @ctype)
            GROUP BY v.user_id
            """, _ => { }, r => { var a = Of(r.GetGuid(0)); a.Planned = r.GetInt32(1); a.Completed = r.GetInt32(2); a.Failed = r.GetInt32(3); });

        // Delay: visits with a planned time that were started, or whose start window (planned time + 30 minutes) is over; late = checked in after the window, or never.
        await QueryAsync("""
            SELECT x.user_id, count(*)::int, count(*) FILTER (WHERE x.late)::int FROM (
                SELECT v.user_id,
                       (v.schedule_date + v.planned_time + @delay) AS due,
                       s.checkin AT TIME ZONE 'Asia/Baghdad' AS checkin_local,
                       CASE WHEN s.checkin IS NOT NULL THEN (s.checkin AT TIME ZONE 'Asia/Baghdad') > (v.schedule_date + v.planned_time + @delay) ELSE true END AS late
                FROM visits v
                JOIN customers c ON c.tenant_id = v.tenant_id AND c.id = v.customer_id
                LEFT JOIN LATERAL (SELECT min(h.occurred_at) AS checkin FROM visit_status_history h WHERE h.visit_id = v.id AND h.to_status = 'InVisit') s ON true
                WHERE v.tenant_id = @tenant AND v.user_id = ANY(@users) AND v.schedule_date BETWEEN @from AND @to AND v.status <> 'Cancelled' AND v.planned_time IS NOT NULL
                  AND (@ctype::text IS NULL OR c.type = @ctype)) x
            WHERE x.checkin_local IS NOT NULL OR x.due < (@now AT TIME ZONE 'Asia/Baghdad')
            GROUP BY x.user_id
            """, c =>
        {
            c.Parameters.Add(P("delay", NpgsqlDbType.Interval, AlertRules.VisitDelay));
            c.Parameters.Add(P("now", NpgsqlDbType.TimestampTz, now.ToUniversalTime()));
        }, r => { var a = Of(r.GetGuid(0)); a.DelayBase = r.GetInt32(1); a.Delayed = r.GetInt32(2); });

        // New customers registered in the range, by the user who owns them.
        await QueryAsync("""
            SELECT c.owner_user_id, count(*)::int, count(*) FILTER (WHERE c.approval_status = 'approved')::int
            FROM customers c
            WHERE c.tenant_id = @tenant AND c.owner_user_id = ANY(@users) AND (c.created_at AT TIME ZONE 'Asia/Baghdad')::date BETWEEN @from AND @to AND (@ctype::text IS NULL OR c.type = @ctype)
            GROUP BY c.owner_user_id
            """, _ => { }, r => { var a = Of(r.GetGuid(0)); a.NewCustomers = r.GetInt32(1); a.ApprovedNew = r.GetInt32(2); });

        // Orders placed in the range by the user (cancelled ones are not counted); money per currency.
        await QueryAsync("""
            SELECT o.created_by, count(*) FILTER (WHERE o.status <> 'cancelled')::int, count(*) FILTER (WHERE o.status = 'delivered')::int, count(*) FILTER (WHERE o.status = 'delivery_failed')::int
            FROM orders o JOIN customers c ON c.tenant_id = o.tenant_id AND c.id = o.customer_id
            WHERE o.tenant_id = @tenant AND o.created_by = ANY(@users) AND (o.created_at AT TIME ZONE 'Asia/Baghdad')::date BETWEEN @from AND @to AND (@ctype::text IS NULL OR c.type = @ctype)
            GROUP BY o.created_by
            """, _ => { }, r => { var a = Of(r.GetGuid(0)); a.Orders = r.GetInt32(1); a.Delivered = r.GetInt32(2); a.DeliveryFailed = r.GetInt32(3); });
        await QueryAsync("""
            SELECT o.created_by, o.currency, sum(o.total)
            FROM orders o JOIN customers c ON c.tenant_id = o.tenant_id AND c.id = o.customer_id
            WHERE o.tenant_id = @tenant AND o.created_by = ANY(@users) AND o.status <> 'cancelled' AND (o.created_at AT TIME ZONE 'Asia/Baghdad')::date BETWEEN @from AND @to AND (@ctype::text IS NULL OR c.type = @ctype)
            GROUP BY o.created_by, o.currency
            """, _ => { }, r => Of(r.GetGuid(0)).Totals.Add(new CurrencyTotal(r.GetString(1), r.GetDecimal(2))));

        // Follow-ups due in the range (cancelled ones are not counted); overdue = still open and the due day is before today.
        await QueryAsync("""
            SELECT f.assignee_id, count(*) FILTER (WHERE f.status <> 'cancelled')::int, count(*) FILTER (WHERE f.status IN ('pending', 'in_progress') AND f.due_date < @today)::int
            FROM follow_ups f JOIN customers c ON c.tenant_id = f.tenant_id AND c.id = f.customer_id
            WHERE f.tenant_id = @tenant AND f.assignee_id = ANY(@users) AND f.due_date BETWEEN @from AND @to AND (@ctype::text IS NULL OR c.type = @ctype)
            GROUP BY f.assignee_id
            """, c => c.Parameters.Add(P("today", NpgsqlDbType.Date, today)), r => { var a = Of(r.GetGuid(0)); a.FollowBase = r.GetInt32(1); a.Overdue = r.GetInt32(2); });

        return acc.Where(p => p.Value.Planned + p.Value.DelayBase + p.Value.NewCustomers + p.Value.Orders + p.Value.FollowBase + p.Value.Totals.Count > 0)
            .ToDictionary(p => p.Key, p => new KpiCounts(p.Value.Planned, p.Value.Completed, p.Value.Failed, p.Value.DelayBase, p.Value.Delayed, p.Value.NewCustomers, p.Value.ApprovedNew, p.Value.Orders,
                p.Value.Delivered, p.Value.DeliveryFailed, p.Value.Totals.OrderBy(t => t.Currency, StringComparer.Ordinal).ToList(), p.Value.FollowBase, p.Value.Overdue));
    }

    public async Task<IReadOnlyList<AlertKpi>> AlertsAsync(TenantId tenantId, IReadOnlyList<Guid> userIds, IReadOnlyList<string> categories, KpiFilter filter, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT a.kind, a.category, count(*)::int, count(*) FILTER (WHERE a.status = 'handled')::int,
                   count(*) FILTER (WHERE a.handled_outcome = 'valid')::int, count(*) FILTER (WHERE a.handled_outcome = 'suspicious')::int, count(*) FILTER (WHERE a.handled_outcome = 'fraud')::int
            FROM alerts a
            WHERE a.tenant_id = @tenant AND a.subject_user_id = ANY(@users) AND a.category = ANY(@categories) AND (a.occurred_at AT TIME ZONE 'Asia/Baghdad')::date BETWEEN @from AND @to
            GROUP BY a.kind, a.category ORDER BY a.category, a.kind
            """, connection, transaction);
        AddFilter(command, tenantId, filter, userIds.ToArray());
        command.Parameters.Add(P("categories", NpgsqlDbType.Array | NpgsqlDbType.Text, categories.ToArray()));
        var items = new List<AlertKpi>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) items.Add(new AlertKpi(reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetInt32(3), reader.GetInt32(4), reader.GetInt32(5), reader.GetInt32(6)));
        return items;
    }
}
