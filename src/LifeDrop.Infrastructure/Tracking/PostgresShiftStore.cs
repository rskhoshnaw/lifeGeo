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
