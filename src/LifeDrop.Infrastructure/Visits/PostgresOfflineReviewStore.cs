using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LifeDrop.Application.Visits;
using LifeDrop.Domain.Common;
using Npgsql;

namespace LifeDrop.Infrastructure.Visits;

// Offline check-ins/outs awaiting or after a manager's decision (plan step 1.14). Coordinates are not read.
public sealed class PostgresOfflineReviewStore(NpgsqlDataSource dataSource) : IOfflineReviewStore
{
    private const string Columns = """
        id, visit_id, user_id, kind, captured_at, received_at, distance_m, radius_m, accuracy_m, review_reasons,
        review_state, reviewed_by, reviewed_at, review_note
        """;

    public async Task<IReadOnlyList<OfflineReviewDto>> ListAsync(TenantId tenantId, string state, int limit, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginTenantAsync(connection, tenantId, cancellationToken);
        await using var command = new NpgsqlCommand(
            $"SELECT {Columns} FROM visit_location_events WHERE review_state = @state ORDER BY received_at, id LIMIT @limit",
            connection, transaction);
        command.Parameters.AddWithValue("state", state);
        command.Parameters.AddWithValue("limit", limit);
        var items = new List<OfflineReviewDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) items.Add(Read(reader));
        return items;
    }

    public async Task<OfflineReviewDto?> GetAsync(TenantId tenantId, Guid eventId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginTenantAsync(connection, tenantId, cancellationToken);
        await using var command = new NpgsqlCommand(
            $"SELECT {Columns} FROM visit_location_events WHERE id = @id AND review_state IS NOT NULL", connection, transaction);
        command.Parameters.AddWithValue("id", eventId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    public async Task<bool> TryDecideAsync(TenantId tenantId, UserId reviewerId, Guid eventId, string decision, string? note,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginTenantAsync(connection, tenantId, cancellationToken);
        Guid visitId;
        await using (var update = new NpgsqlCommand("""
            UPDATE visit_location_events
            SET review_state = @decision, reviewed_by = @reviewer, reviewed_at = now(), review_note = @note
            WHERE id = @id AND review_state = 'pending'
            RETURNING visit_id
            """, connection, transaction))
        {
            update.Parameters.AddWithValue("decision", decision);
            update.Parameters.AddWithValue("reviewer", reviewerId.Value);
            update.Parameters.AddWithValue("note", (object?)note ?? DBNull.Value);
            update.Parameters.AddWithValue("id", eventId);
            if (await update.ExecuteScalarAsync(cancellationToken) is not Guid found) return false;
            visitId = found;
        }
        await using (var audit = new NpgsqlCommand("""
            INSERT INTO audit_logs (id, tenant_id, actor_id, action, entity_id, occurred_at)
            VALUES (gen_random_uuid(), @tenant, @actor, @action, @entity, now())
            """, connection, transaction))
        {
            audit.Parameters.AddWithValue("tenant", tenantId.Value);
            audit.Parameters.AddWithValue("actor", reviewerId.Value);
            audit.Parameters.AddWithValue("action", decision == "approved" ? "visit.offline_review_approved" : "visit.offline_review_rejected");
            audit.Parameters.AddWithValue("entity", visitId.ToString());
            await audit.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private static OfflineReviewDto Read(NpgsqlDataReader r) => new(
        r.GetGuid(0), r.GetGuid(1), r.GetGuid(2), r.GetString(3), r.GetFieldValue<DateTimeOffset>(4), r.GetFieldValue<DateTimeOffset>(5),
        r.IsDBNull(6) ? null : (double)r.GetDecimal(6), r.IsDBNull(7) ? null : r.GetInt32(7), (double)r.GetDecimal(8),
        r.GetFieldValue<string[]>(9), r.GetString(10), r.IsDBNull(11) ? null : r.GetGuid(11),
        r.IsDBNull(12) ? null : r.GetFieldValue<DateTimeOffset>(12), r.IsDBNull(13) ? null : r.GetString(13));

    private static async Task<NpgsqlTransaction> BeginTenantAsync(NpgsqlConnection connection, TenantId tenantId, CancellationToken cancellationToken)
    {
        var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var setTenant = new NpgsqlCommand("SELECT set_config('app.current_tenant', @tenant, true)", connection, transaction);
        setTenant.Parameters.AddWithValue("tenant", tenantId.Value.ToString());
        await setTenant.ExecuteNonQueryAsync(cancellationToken);
        return transaction;
    }
}
