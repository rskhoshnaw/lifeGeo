using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LifeDrop.Application.Customers;
using LifeDrop.Domain.Common;
using Npgsql;

namespace LifeDrop.Infrastructure.Customers;

// Pending customers and their approval (plan step 1.15, decision D11).
public sealed class PostgresCustomerApprovalStore(NpgsqlDataSource dataSource) : ICustomerApprovalStore
{
    public async Task<IReadOnlyList<CustomerDto>> ListPendingAsync(TenantId tenantId, Guid? regionId, int limit, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginTenantAsync(connection, tenantId, cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT id, tenant_id, type, code, name, status, created_at, region_id, approval_status FROM customers
            WHERE approval_status = 'pending' AND (@region::uuid IS NULL OR region_id = @region)
            ORDER BY created_at, id LIMIT @limit
            """, connection, transaction);
        command.Parameters.AddWithValue("region", NpgsqlTypes.NpgsqlDbType.Uuid, (object?)regionId ?? DBNull.Value);
        command.Parameters.AddWithValue("limit", limit);
        var items = new List<CustomerDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            items.Add(new CustomerDto(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                reader.GetString(5), reader.GetFieldValue<DateTimeOffset>(6),
                RegionId: reader.IsDBNull(7) ? null : reader.GetGuid(7), ApprovalStatus: reader.GetString(8)));
        return items;
    }

    public async Task<(Guid? RegionId, string Status)?> GetApprovalAsync(TenantId tenantId, CustomerId id, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginTenantAsync(connection, tenantId, cancellationToken);
        await using var command = new NpgsqlCommand("SELECT region_id, approval_status FROM customers WHERE id = @id", connection, transaction);
        command.Parameters.AddWithValue("id", id.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return (reader.IsDBNull(0) ? null : reader.GetGuid(0), reader.GetString(1));
    }

    public async Task<bool> TryDecideAsync(TenantId tenantId, UserId actorId, CustomerId id, string decision, string? note, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginTenantAsync(connection, tenantId, cancellationToken);
        await using (var update = new NpgsqlCommand("""
            UPDATE customers SET approval_status = @decision, approved_by = @actor, approved_at = now(), approval_note = @note
            WHERE id = @id AND approval_status = 'pending'
            """, connection, transaction))
        {
            update.Parameters.AddWithValue("decision", decision);
            update.Parameters.AddWithValue("actor", actorId.Value);
            update.Parameters.AddWithValue("note", (object?)note ?? DBNull.Value);
            update.Parameters.AddWithValue("id", id.Value);
            if (await update.ExecuteNonQueryAsync(cancellationToken) == 0) return false;
        }
        await using (var audit = new NpgsqlCommand("""
            INSERT INTO audit_logs (id, tenant_id, actor_id, action, entity_id, occurred_at)
            VALUES (gen_random_uuid(), @tenant, @actor, @action, @entity, now())
            """, connection, transaction))
        {
            audit.Parameters.AddWithValue("tenant", tenantId.Value);
            audit.Parameters.AddWithValue("actor", actorId.Value);
            audit.Parameters.AddWithValue("action", decision == "approved" ? "customer.approved" : "customer.rejected");
            audit.Parameters.AddWithValue("entity", id.Value.ToString());
            await audit.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return true;
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
