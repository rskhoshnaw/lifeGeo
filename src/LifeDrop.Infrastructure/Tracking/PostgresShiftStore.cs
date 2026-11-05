using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LifeDrop.Application.Tracking;
using LifeDrop.Domain.Common;
using Npgsql;

namespace LifeDrop.Infrastructure.Tracking;

// Shifts and tracking points (plan step 1.13). Start and end are audited in the same transaction; points are not
// audited (they are the tracking record itself) and their coordinates never reach audit or logs.
public sealed class PostgresShiftStore(NpgsqlDataSource dataSource) : IShiftStore
{
    private const string ShiftColumns = "id, device_id, started_at, ended_at, user_id";

    public async Task<ShiftDto?> GetOpenAsync(TenantId tenantId, UserId userId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginTenantAsync(connection, tenantId, cancellationToken);
        await using var command = new NpgsqlCommand(
            $"SELECT {ShiftColumns} FROM shifts WHERE user_id = @user AND ended_at IS NULL", connection, transaction);
        command.Parameters.AddWithValue("user", userId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadShift(reader) : null;
    }

    public async Task<(ShiftDto Shift, Guid UserId)?> GetAsync(TenantId tenantId, Guid shiftId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginTenantAsync(connection, tenantId, cancellationToken);
        await using var command = new NpgsqlCommand($"SELECT {ShiftColumns} FROM shifts WHERE id = @id", connection, transaction);
        command.Parameters.AddWithValue("id", shiftId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? (ReadShift(reader), reader.GetGuid(4)) : null;
    }

    public async Task<bool> TryStartAsync(TenantId tenantId, UserId userId, Guid deviceId, Guid shiftId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginTenantAsync(connection, tenantId, cancellationToken);
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO shifts (id, tenant_id, user_id, device_id) VALUES (@id, @tenant, @user, @device)
            ON CONFLICT (tenant_id, user_id) WHERE ended_at IS NULL DO NOTHING
            """, connection, transaction))
        {
            insert.Parameters.AddWithValue("id", shiftId);
            insert.Parameters.AddWithValue("tenant", tenantId.Value);
            insert.Parameters.AddWithValue("user", userId.Value);
            insert.Parameters.AddWithValue("device", deviceId);
            if (await insert.ExecuteNonQueryAsync(cancellationToken) == 0) return false;
        }
        await InsertAuditAsync(connection, transaction, tenantId, userId, "shift.started", shiftId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<bool> TryEndAsync(TenantId tenantId, UserId userId, Guid shiftId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginTenantAsync(connection, tenantId, cancellationToken);
        await using (var update = new NpgsqlCommand(
            "UPDATE shifts SET ended_at = now() WHERE id = @id AND user_id = @user AND ended_at IS NULL", connection, transaction))
        {
            update.Parameters.AddWithValue("id", shiftId);
            update.Parameters.AddWithValue("user", userId.Value);
            if (await update.ExecuteNonQueryAsync(cancellationToken) == 0) return false;
        }
        await InsertAuditAsync(connection, transaction, tenantId, userId, "shift.ended", shiftId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<int> AddPointsAsync(TenantId tenantId, UserId userId, Guid deviceId, Guid shiftId, IReadOnlyList<TrackingPointRequest> points,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginTenantAsync(connection, tenantId, cancellationToken);
        var ids = new Guid[points.Count];
        var lats = new double[points.Count];
        var lons = new double[points.Count];
        var accuracies = new double[points.Count];
        var times = new DateTimeOffset[points.Count];
        var mocked = new bool[points.Count];
        for (var i = 0; i < points.Count; i++)
        {
            ids[i] = points[i].ClientPointId;
            lats[i] = points[i].Latitude;
            lons[i] = points[i].Longitude;
            accuracies[i] = Math.Min(points[i].AccuracyMeters, 99999.99);
            times[i] = points[i].CapturedAt;
            mocked[i] = points[i].IsMocked;
        }
        await using var insert = new NpgsqlCommand("""
            INSERT INTO tracking_points (tenant_id, shift_id, user_id, device_id, client_point_id, geom, accuracy_m, captured_at, is_mocked)
            SELECT @tenant, @shift, @user, @device, p.id, ST_SetSRID(ST_MakePoint(p.lon, p.lat), 4326)::geography, p.accuracy, p.captured, p.mocked
            FROM unnest(@ids, @lats, @lons, @accuracies, @times, @mocked) AS p(id, lat, lon, accuracy, captured, mocked)
            ON CONFLICT (shift_id, client_point_id) DO NOTHING
            """, connection, transaction);
        insert.Parameters.AddWithValue("tenant", tenantId.Value);
        insert.Parameters.AddWithValue("shift", shiftId);
        insert.Parameters.AddWithValue("user", userId.Value);
        insert.Parameters.AddWithValue("device", deviceId);
        insert.Parameters.AddWithValue("ids", ids);
        insert.Parameters.AddWithValue("lats", lats);
        insert.Parameters.AddWithValue("lons", lons);
        insert.Parameters.AddWithValue("accuracies", accuracies);
        insert.Parameters.AddWithValue("times", times);
        insert.Parameters.AddWithValue("mocked", mocked);
        var added = await insert.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return added;
    }

    public async Task<bool> TryReopenAsync(TenantId tenantId, UserId userId, Guid shiftId, TimeSpan window, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginTenantAsync(connection, tenantId, cancellationToken);
        try
        {
            await using var command = new NpgsqlCommand("""
                UPDATE shifts SET ended_at = NULL, auto_closed_at = NULL
                WHERE id = @id AND user_id = @user AND ended_at IS NOT NULL AND auto_closed_at IS NOT NULL AND auto_closed_at > now() - @window
                  AND NOT EXISTS (SELECT 1 FROM shifts o WHERE o.user_id = @user AND o.ended_at IS NULL)
                """, connection, transaction);
            command.Parameters.AddWithValue("id", shiftId);
            command.Parameters.AddWithValue("user", userId.Value);
            command.Parameters.AddWithValue("window", window);
            if (await command.ExecuteNonQueryAsync(cancellationToken) == 0) return false;
            await transaction.CommitAsync(cancellationToken);
            return true;
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return false;   // the user started another shift at the same moment
        }
    }

    public async Task RecordHealthAsync(TenantId tenantId, UserId userId, Guid shiftId, ShiftHealthRequest health, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginTenantAsync(connection, tenantId, cancellationToken);
        await using var command = new NpgsqlCommand("""
            INSERT INTO shift_health (shift_id, tenant_id, user_id, seen_at, app_version, platform, os_version, device_model, location_permission,
                precise_location, location_services, notifications, battery_exempt, background_refresh, battery_percent, low_power)
            VALUES (@shift, @tenant, @user, now(), @app, @platform, @os, @model, @permission, @precise, @services, @notifications, @battery, @refresh, @percent, @low)
            ON CONFLICT (shift_id) DO UPDATE SET seen_at = now(), app_version = EXCLUDED.app_version, platform = EXCLUDED.platform, os_version = EXCLUDED.os_version,
                device_model = EXCLUDED.device_model, location_permission = EXCLUDED.location_permission, precise_location = EXCLUDED.precise_location,
                location_services = EXCLUDED.location_services, notifications = EXCLUDED.notifications, battery_exempt = EXCLUDED.battery_exempt,
                background_refresh = EXCLUDED.background_refresh, battery_percent = EXCLUDED.battery_percent, low_power = EXCLUDED.low_power
            """, connection, transaction);
        command.Parameters.AddWithValue("shift", shiftId);
        command.Parameters.AddWithValue("tenant", tenantId.Value);
        command.Parameters.AddWithValue("user", userId.Value);
        command.Parameters.AddWithValue("app", (object?)health.AppVersion ?? DBNull.Value);
        command.Parameters.AddWithValue("platform", (object?)health.Platform ?? DBNull.Value);
        command.Parameters.AddWithValue("os", (object?)health.OsVersion ?? DBNull.Value);
        command.Parameters.AddWithValue("model", (object?)health.DeviceModel ?? DBNull.Value);
        command.Parameters.AddWithValue("permission", health.LocationPermission ?? "unknown");
        command.Parameters.AddWithValue("precise", (object?)health.PreciseLocation ?? DBNull.Value);
        command.Parameters.AddWithValue("services", (object?)health.LocationServices ?? DBNull.Value);
        command.Parameters.AddWithValue("notifications", (object?)health.Notifications ?? DBNull.Value);
        command.Parameters.AddWithValue("battery", (object?)health.BatteryExempt ?? DBNull.Value);
        command.Parameters.AddWithValue("refresh", (object?)health.BackgroundRefresh ?? DBNull.Value);
        command.Parameters.Add(new NpgsqlParameter("percent", NpgsqlTypes.NpgsqlDbType.Smallint) { Value = health.BatteryPercent is { } p ? (object)(short)p : DBNull.Value });
        command.Parameters.AddWithValue("low", (object?)health.LowPower ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<DateTimeOffset>> ListPointTimesAsync(TenantId tenantId, Guid shiftId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginTenantAsync(connection, tenantId, cancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT captured_at FROM tracking_points WHERE shift_id = @id ORDER BY captured_at", connection, transaction);
        command.Parameters.AddWithValue("id", shiftId);
        var times = new List<DateTimeOffset>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) times.Add(reader.GetFieldValue<DateTimeOffset>(0));
        return times;
    }

    private static ShiftDto ReadShift(NpgsqlDataReader reader) => new(reader.GetGuid(0), reader.GetGuid(1),
        reader.GetFieldValue<DateTimeOffset>(2), reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3));

    private static async Task InsertAuditAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, TenantId tenantId, UserId actorId,
        string action, Guid shiftId, CancellationToken cancellationToken)
    {
        await using var audit = new NpgsqlCommand("""
            INSERT INTO audit_logs (id, tenant_id, actor_id, action, entity_id, occurred_at)
            VALUES (gen_random_uuid(), @tenant, @actor, @action, @entity, now())
            """, connection, transaction);
        audit.Parameters.AddWithValue("tenant", tenantId.Value);
        audit.Parameters.AddWithValue("actor", actorId.Value);
        audit.Parameters.AddWithValue("action", action);
        audit.Parameters.AddWithValue("entity", shiftId.ToString());
        await audit.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<NpgsqlTransaction> BeginTenantAsync(NpgsqlConnection connection, TenantId tenantId, CancellationToken cancellationToken)
    {
        var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var setTenant = new NpgsqlCommand("SELECT set_config('app.current_tenant', @tenant, true)", connection, transaction);
        setTenant.Parameters.AddWithValue("tenant", tenantId.Value.ToString());
        await setTenant.ExecuteNonQueryAsync(cancellationToken);
        return transaction;
    }
}
