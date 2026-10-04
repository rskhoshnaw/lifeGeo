using System.Collections.Generic;
using LifeDrop.Application.Customers;
using LifeDrop.Domain.Common;
using Npgsql;

namespace LifeDrop.Infrastructure.Customers;

public sealed class PostgresCustomerStore(NpgsqlDataSource dataSource) : ICustomerStore
{
    public async Task<CustomerDto?> GetByIdAsync(TenantId tenantId, CustomerId id, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var setTenantCommand = new NpgsqlCommand("SELECT set_config('app.current_tenant', @tenant, true)", connection, transaction))
        {
            setTenantCommand.Parameters.AddWithValue("tenant", tenantId.Value.ToString());
            await setTenantCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        const string sql = """
            SELECT id, tenant_id, type, code, name, status, created_at
            FROM customers
            WHERE id = @id
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("id", id.Value);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;

        return new CustomerDto(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetFieldValue<DateTimeOffset>(6)
        );
    }

    public async Task<CreateCustomerResult> CreateAsync(TenantId tenantId, UserId actorId, CustomerId id, CreateCustomerRequest request, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var setTenantCommand = new NpgsqlCommand("SELECT set_config('app.current_tenant', @tenant, true)", connection, transaction))
        {
            setTenantCommand.Parameters.AddWithValue("tenant", tenantId.Value.ToString());
            await setTenantCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        const string sql = """
            WITH inserted AS (
                INSERT INTO customers (id, tenant_id, type, code, name, status, owner_user_id)
                VALUES (@id, @tenant, @type, @code, @name, 'no_location', @actor)
                ON CONFLICT (tenant_id, code) DO NOTHING
                RETURNING id, tenant_id
            ), audited AS (
                INSERT INTO audit_logs (id, tenant_id, actor_id, action, entity_id, occurred_at)
                SELECT gen_random_uuid(), tenant_id, @actor, 'customer.created', id::text, now()
                FROM inserted
                RETURNING id
            )
            SELECT count(*) FROM inserted;
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("id", id.Value);
        command.Parameters.AddWithValue("tenant", tenantId.Value);
        command.Parameters.AddWithValue("actor", actorId.Value);
        command.Parameters.AddWithValue("type", request.Type);
        command.Parameters.AddWithValue("code", request.Code);
        command.Parameters.AddWithValue("name", request.Name);

        var inserted = (long)(await command.ExecuteScalarAsync(cancellationToken) ?? 0L);
        if (inserted == 0) return CreateCustomerResult.DuplicateCode;
        
        await transaction.CommitAsync(cancellationToken);
        return CreateCustomerResult.Created;
    }

    public async Task<IReadOnlyList<CustomerDto>> ListAsync(TenantId tenantId, int limit, DateTimeOffset? cursorCreatedAt, Guid? cursorId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var setTenantCommand = new NpgsqlCommand("SELECT set_config('app.current_tenant', @tenant, true)", connection, transaction))
        {
            setTenantCommand.Parameters.AddWithValue("tenant", tenantId.Value.ToString());
            await setTenantCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        var sql = """
            SELECT id, tenant_id, type, code, name, status, created_at
            FROM customers
            """;

        if (cursorCreatedAt.HasValue && cursorId.HasValue)
        {
            sql += "\nWHERE (created_at > @cursorDate) OR (created_at = @cursorDate AND id > @cursorId)";
        }

        sql += "\nORDER BY created_at ASC, id ASC\nLIMIT @limit";

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("limit", limit);
        if (cursorCreatedAt.HasValue && cursorId.HasValue)
        {
            command.Parameters.AddWithValue("cursorDate", cursorCreatedAt.Value);
            command.Parameters.AddWithValue("cursorId", cursorId.Value);
        }

        var results = new List<CustomerDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new CustomerDto(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetFieldValue<DateTimeOffset>(6)
            ));
        }

        return results;
    }
}
