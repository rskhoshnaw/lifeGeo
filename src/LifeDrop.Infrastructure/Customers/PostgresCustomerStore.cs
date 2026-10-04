using System.Collections.Generic;
using LifeDrop.Application.Customers;
using LifeDrop.Application.Roles;
using LifeDrop.Domain.Common;
using Npgsql;

namespace LifeDrop.Infrastructure.Customers;

public sealed class PostgresCustomerStore(NpgsqlDataSource dataSource) : ICustomerStore
{
    public async Task<CustomerDto?> GetByIdAsync(TenantId tenantId, CustomerId id, AccessScope scope, CancellationToken cancellationToken)
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

        await using var command = new NpgsqlCommand(sql + " AND " + ScopeFilter(scope), connection, transaction);
        command.Parameters.AddWithValue("id", id.Value);
        AddScopeParameters(command, scope);

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

    public async Task<IReadOnlyList<CustomerDto>> ListAsync(TenantId tenantId, AccessScope scope, int limit, DateTimeOffset? cursorCreatedAt, Guid? cursorId, CancellationToken cancellationToken)
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

        sql += "\nWHERE " + ScopeFilter(scope);
        if (cursorCreatedAt.HasValue && cursorId.HasValue)
        {
            sql += "\nAND ((created_at > @cursorDate) OR (created_at = @cursorDate AND id > @cursorId))";
        }

        sql += "\nORDER BY created_at ASC, id ASC\nLIMIT @limit";

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("limit", limit);
        AddScopeParameters(command, scope);
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

    // Data scope (plan step 0.6b): tenant = all rows (RLS already limits the tenant); region = customers in the
    // region of the actor's team plus customers the actor owns; own = customers the actor owns.
    private static string ScopeFilter(AccessScope scope) => scope.Scope switch
    {
        DataScope.Tenant => "TRUE",
        DataScope.Region when scope.RegionId is not null => "(owner_user_id = @scopeActor OR region_id = @scopeRegion)",
        _ => "owner_user_id = @scopeActor"
    };

    private static void AddScopeParameters(NpgsqlCommand command, AccessScope scope)
    {
        command.Parameters.AddWithValue("scopeActor", scope.ActorId.Value);
        command.Parameters.AddWithValue("scopeRegion", (object?)scope.RegionId ?? DBNull.Value);
    }
}
