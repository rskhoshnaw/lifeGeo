using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LifeDrop.Application.Roles;
using LifeDrop.Application.Visits;
using LifeDrop.Domain.Common;
using Npgsql;

namespace LifeDrop.Infrastructure.Visits;

// Candidate search and bulk creation for "plan from places" (plan step 1.24).
public sealed class PostgresPlanGenerationStore(NpgsqlDataSource dataSource) : IPlanGenerationStore
{
    public async Task<IReadOnlyList<PlanCandidateRow>> FindCandidatesAsync(TenantId tenantId, AccessScope scope, Guid? placeId, Guid? regionId,
        DateOnly scheduleDate, string order, string? query, IReadOnlyList<GeoPoint>? area, CancellationToken cancellationToken)
    {
        // The order comes from a whitelist (PlanGenerator.Orders), never from the caller's text.
        var orderBy = order switch
        {
            "name" => "c.name COLLATE \"C\" ASC, c.code COLLATE \"C\" ASC",
            "code" => "c.code COLLATE \"C\" ASC",
            _ => "last_visit ASC NULLS FIRST, c.code COLLATE \"C\" ASC"
        };
        var scopeFilter = scope.Scope switch
        {
            DataScope.Tenant => "TRUE",
            DataScope.Region when scope.RegionId is not null => "(c.owner_user_id = @actor OR c.region_id = @scopeRegion)",
            _ => "c.owner_user_id = @actor"
        };
        var sql = $"""
            WITH RECURSIVE sub AS (
                SELECT p.id FROM places p WHERE p.tenant_id = @tenant AND p.id = @place AND p.merged_into IS NULL
                UNION ALL
                SELECT ch.id FROM places ch JOIN sub ON ch.parent_id = sub.id WHERE ch.tenant_id = @tenant AND ch.merged_into IS NULL
            ),
            chain AS (
                SELECT p.id, p.parent_id, p.name, p.id AS leaf, 0 AS depth FROM places p WHERE p.tenant_id = @tenant
                UNION ALL
                SELECT p.id, p.parent_id, p.name, chain.leaf, chain.depth + 1 FROM places p JOIN chain ON p.id = chain.parent_id WHERE p.tenant_id = @tenant
            )
            SELECT c.id, c.code, c.name, c.type,
                (SELECT string_agg(ch.name, ' › ' ORDER BY ch.depth DESC) FROM chain ch WHERE ch.leaf = c.place_id) AS place,
                (SELECT max(v.schedule_date) FROM visits v WHERE v.tenant_id = c.tenant_id AND v.customer_id = c.id AND v.status = 'Completed') AS last_visit,
                CASE
                    WHEN NOT EXISTS (SELECT 1 FROM customer_visit_windows w WHERE w.tenant_id = c.tenant_id AND w.customer_id = c.id) THEN 'none'
                    WHEN EXISTS (SELECT 1 FROM customer_visit_windows w WHERE w.tenant_id = c.tenant_id AND w.customer_id = c.id
                                 AND w.weekday = EXTRACT(DOW FROM @day::date)::int) THEN 'within'
                    ELSE 'outside' END AS window_status,
                c.priority
            FROM customers c
            WHERE c.tenant_id = @tenant AND c.approval_status = 'approved' AND {scopeFilter}
              AND (@place::uuid IS NULL OR c.place_id IN (SELECT id FROM sub))
              AND (@lons::float8[] IS NULL OR (c.geom IS NOT NULL AND ST_Covers(
                    ST_SetSRID(ST_MakePolygon(ST_MakeLine(ARRAY(SELECT ST_MakePoint(t.lon, t.lat) FROM unnest(@lons::float8[], @lats::float8[]) WITH ORDINALITY AS t(lon, lat, n) ORDER BY t.n))), 4326),
                    c.geom::geometry)))
              AND (@region::uuid IS NULL OR c.region_id = @region)
              AND (@query::text IS NULL OR c.name ILIKE @query ESCAPE '\' OR c.code ILIKE @query ESCAPE '\')
              AND NOT EXISTS (SELECT 1 FROM visits v WHERE v.tenant_id = c.tenant_id AND v.customer_id = c.id AND v.schedule_date = @day
                              AND v.status NOT IN ('Cancelled', 'Failed'))
            ORDER BY CASE c.priority WHEN 'high' THEN 0 WHEN 'normal' THEN 1 ELSE 2 END, {orderBy}
            LIMIT @max
            """;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var set = new NpgsqlCommand("SELECT set_config('app.current_tenant', @tenant, true)", connection, transaction))
        {
            set.Parameters.AddWithValue("tenant", tenantId.Value.ToString());
            await set.ExecuteNonQueryAsync(cancellationToken);
        }
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("tenant", tenantId.Value);
        command.Parameters.AddWithValue("actor", scope.ActorId.Value);
        command.Parameters.Add(new NpgsqlParameter("scopeRegion", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)scope.RegionId ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("place", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)placeId ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("region", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)regionId ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("query", NpgsqlTypes.NpgsqlDbType.Text)
        {
            Value = string.IsNullOrWhiteSpace(query) ? DBNull.Value : "%" + query.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%"
        });
        // The drawn corners, closed (the first point again at the end).
        var ring = area is null ? null : area.Append(area[0]).ToList();
        command.Parameters.Add(new NpgsqlParameter("lons", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Double) { Value = (object?)ring?.Select(p => p.Longitude).ToArray() ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("lats", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Double) { Value = (object?)ring?.Select(p => p.Latitude).ToArray() ?? DBNull.Value });
        command.Parameters.AddWithValue("day", scheduleDate);
        command.Parameters.AddWithValue("max", PlanGenerator.MaxMatching);

        var rows = new List<PlanCandidateRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            rows.Add(new PlanCandidateRow(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetFieldValue<DateOnly>(5), reader.GetString(6), reader.GetString(7)));
        return rows;
    }

    public async Task<IReadOnlyList<Guid>> CreateAsync(TenantId tenantId, UserId actorId, UserId assigneeId, DateOnly scheduleDate, string priority,
        IReadOnlyList<PlanVisitToCreate> visits, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var set = new NpgsqlCommand("SELECT set_config('app.current_tenant', @tenant, true), set_config('app.current_actor', @actor, true)", connection, transaction))
        {
            set.Parameters.AddWithValue("tenant", tenantId.Value.ToString());
            set.Parameters.AddWithValue("actor", actorId.Value.ToString());
            await set.ExecuteNonQueryAsync(cancellationToken);
        }

        // Give the visits that are already in the plan a fixed order (their current display order), then add the new ones after them.
        await using (var renumber = new NpgsqlCommand("""
            UPDATE visits v SET sort_order = o.pos
            FROM (SELECT id, row_number() OVER (ORDER BY sort_order ASC NULLS LAST, planned_time ASC NULLS LAST,
                         CASE priority WHEN 'high' THEN 0 WHEN 'normal' THEN 1 ELSE 2 END, created_at ASC, id ASC) AS pos
                  FROM visits WHERE tenant_id = @tenant AND user_id = @assignee AND schedule_date = @day) o
            WHERE v.id = o.id AND v.tenant_id = @tenant
            """, connection, transaction))
        {
            renumber.Parameters.AddWithValue("tenant", tenantId.Value);
            renumber.Parameters.AddWithValue("assignee", assigneeId.Value);
            renumber.Parameters.AddWithValue("day", scheduleDate);
            await renumber.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var next = new NpgsqlCommand("SELECT coalesce(max(sort_order), 0) FROM visits WHERE tenant_id = @tenant AND user_id = @assignee AND schedule_date = @day", connection, transaction);
        next.Parameters.AddWithValue("tenant", tenantId.Value);
        next.Parameters.AddWithValue("assignee", assigneeId.Value);
        next.Parameters.AddWithValue("day", scheduleDate);
        var position = Convert.ToInt32(await next.ExecuteScalarAsync(cancellationToken));

        var ids = new List<Guid>(visits.Count);
        foreach (var visit in visits)
        {
            position++;
            var id = Guid.NewGuid();
            await using var insert = new NpgsqlCommand("""
                INSERT INTO visits (id, tenant_id, user_id, customer_id, status, schedule_date, created_at, visit_type, priority, is_unplanned, sort_order)
                SELECT @id, @tenant, @assignee, c.id, 'Planned', @day, now(), @type, @priority, false, @position
                FROM customers c WHERE c.tenant_id = @tenant AND c.id = @customer AND c.approval_status = 'approved'
                """, connection, transaction);
            insert.Parameters.AddWithValue("id", id);
            insert.Parameters.AddWithValue("tenant", tenantId.Value);
            insert.Parameters.AddWithValue("assignee", assigneeId.Value);
            insert.Parameters.AddWithValue("customer", visit.CustomerId);
            insert.Parameters.AddWithValue("day", scheduleDate);
            insert.Parameters.AddWithValue("type", visit.VisitType);
            insert.Parameters.AddWithValue("priority", priority);
            insert.Parameters.AddWithValue("position", position);
            if (await insert.ExecuteNonQueryAsync(cancellationToken) == 0) { position--; continue; }
            ids.Add(id);
            await Audit(connection, transaction, tenantId, actorId, "visit.created", id.ToString(), cancellationToken);
        }
        await Audit(connection, transaction, tenantId, actorId, "visit.plan_generated", assigneeId.Value.ToString(), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ids;
    }

    private static async Task Audit(NpgsqlConnection connection, NpgsqlTransaction transaction, TenantId tenantId, UserId actorId, string action, string entity,
        CancellationToken cancellationToken)
    {
        await using var audit = new NpgsqlCommand("""
            INSERT INTO audit_logs (id, tenant_id, actor_id, action, entity_id, occurred_at)
            VALUES (gen_random_uuid(), @tenant, @actor, @action, @entity, now())
            """, connection, transaction);
        audit.Parameters.AddWithValue("tenant", tenantId.Value);
        audit.Parameters.AddWithValue("actor", actorId.Value);
        audit.Parameters.AddWithValue("action", action);
        audit.Parameters.AddWithValue("entity", entity);
        await audit.ExecuteNonQueryAsync(cancellationToken);
    }
}
