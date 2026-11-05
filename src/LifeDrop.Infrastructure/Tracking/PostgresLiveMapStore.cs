using LifeDrop.Application.Roles;
using LifeDrop.Application.Tracking;
using LifeDrop.Domain.Common;
using Npgsql;
using NpgsqlTypes;

namespace LifeDrop.Infrastructure.Tracking;

// Live map read (plan step 1.19). Read-only; tenant isolation by RLS (app.current_tenant) plus an explicit filter
// on users, which has no RLS.
public sealed class PostgresLiveMapStore(NpgsqlDataSource dataSource) : ILiveMapStore
{
    public async Task<IReadOnlyList<LiveMapRow>> ListOpenShiftsAsync(TenantId tenantId, AccessScope scope, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var setTenant = new NpgsqlCommand("SELECT set_config('app.current_tenant', @tenant, true)", connection, transaction))
        {
            setTenant.Parameters.AddWithValue("tenant", tenantId.Value.ToString());
            await setTenant.ExecuteNonQueryAsync(cancellationToken);
        }

        // Scope 2 = tenant, 1 = region (the actor and members of teams in the region), 0 = own (DataScope values).
        const string sql = """
            SELECT s.user_id, u.email, t.name, t.region_id, s.started_at,
                p.captured_at, ST_Y(p.geom::geometry), ST_X(p.geom::geometry), p.accuracy_m::float8, p.is_mocked,
                lv.name, lv.occurred_at, COALESCE(dv.admin_label, dv.device_label),
                h.seen_at, h.app_version, h.platform, h.os_version, h.device_model, h.location_permission, h.precise_location, h.location_services,
                h.notifications, h.battery_exempt, h.background_refresh, h.battery_percent, h.low_power, u.phone
            FROM shifts s
            LEFT JOIN shift_health h ON h.tenant_id = s.tenant_id AND h.shift_id = s.id
            LEFT JOIN devices dv ON dv.tenant_id = s.tenant_id AND dv.id = s.device_id
            JOIN users u ON u.tenant_id = s.tenant_id AND u.id = s.user_id AND u.is_active
            LEFT JOIN team_members m ON m.user_id = s.user_id
            LEFT JOIN teams t ON t.id = m.team_id
            LEFT JOIN LATERAL (
                SELECT tp.captured_at, tp.geom, tp.accuracy_m, tp.is_mocked FROM tracking_points tp
                WHERE tp.shift_id = s.id ORDER BY tp.captured_at DESC LIMIT 1) p ON true
            LEFT JOIN LATERAL (
                SELECT c.name, h.occurred_at FROM visit_status_history h
                JOIN visits v ON v.tenant_id = h.tenant_id AND v.id = h.visit_id
                JOIN customers c ON c.tenant_id = v.tenant_id AND c.id = v.customer_id
                WHERE v.user_id = s.user_id AND h.to_status = 'InVisit' AND h.occurred_at >= s.started_at
                ORDER BY h.occurred_at DESC LIMIT 1) lv ON true
            WHERE s.tenant_id = @tenantId AND s.ended_at IS NULL
              AND (@scope = 2 OR s.user_id = @actor OR (@scope = 1 AND @region::uuid IS NOT NULL AND t.region_id = @region))
            ORDER BY u.email COLLATE "C" NULLS LAST, s.user_id
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("tenantId", tenantId.Value);
        command.Parameters.AddWithValue("scope", (int)scope.Scope);
        command.Parameters.AddWithValue("actor", scope.ActorId.Value);
        command.Parameters.Add(new NpgsqlParameter("region", NpgsqlDbType.Uuid) { Value = (object?)scope.RegionId ?? DBNull.Value });

        var rows = new List<LiveMapRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new LiveMapRow(
                reader.GetGuid(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetGuid(3),
                reader.GetFieldValue<DateTimeOffset>(4),
                reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5),
                reader.IsDBNull(6) ? null : reader.GetDouble(6),
                reader.IsDBNull(7) ? null : reader.GetDouble(7),
                reader.IsDBNull(8) ? null : reader.GetDouble(8),
                reader.IsDBNull(9) ? null : reader.GetBoolean(9),
                reader.IsDBNull(10) ? null : reader.GetString(10),
                reader.IsDBNull(11) ? null : reader.GetFieldValue<DateTimeOffset>(11),
                reader.IsDBNull(12) ? null : reader.GetString(12),
                reader.IsDBNull(13) ? null : reader.GetFieldValue<DateTimeOffset>(13),
                reader.IsDBNull(13) ? null : new LiveMapHealthDto(
                    reader.IsDBNull(14) ? null : reader.GetString(14),
                    reader.IsDBNull(15) ? null : reader.GetString(15),
                    reader.IsDBNull(16) ? null : reader.GetString(16),
                    reader.IsDBNull(17) ? null : reader.GetString(17),
                    reader.IsDBNull(18) ? null : reader.GetString(18),
                    reader.IsDBNull(19) ? null : reader.GetBoolean(19),
                    reader.IsDBNull(20) ? null : reader.GetBoolean(20),
                    reader.IsDBNull(21) ? null : reader.GetBoolean(21),
                    reader.IsDBNull(22) ? null : reader.GetBoolean(22),
                    reader.IsDBNull(23) ? null : reader.GetString(23),
                    reader.IsDBNull(24) ? null : (int)reader.GetInt16(24),
                    reader.IsDBNull(25) ? null : reader.GetBoolean(25)),
                reader.IsDBNull(26) ? null : reader.GetString(26)));
        }
        return rows;
    }
}
