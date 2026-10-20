using LifeDrop.Application.Catalog;
using LifeDrop.Domain.Common;
using Npgsql;

namespace LifeDrop.Infrastructure.Catalog;

// places and regions are row level secured by company; every statement runs in a transaction that names the company.
public sealed class PostgresPriceVisibilityStore(NpgsqlDataSource dataSource) : IPriceVisibilityStore
{
    public async Task<bool> RegionHidesPricesAsync(TenantId tenantId, Guid regionId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, cancellationToken);
        await using var command = new NpgsqlCommand("SELECT hide_prices FROM regions WHERE id = @id", connection, transaction);
        command.Parameters.AddWithValue("id", regionId);
        return await command.ExecuteScalarAsync(cancellationToken) is true;
    }

    public async Task<IReadOnlySet<Guid>> CustomersInHiddenCitiesAsync(TenantId tenantId, IReadOnlyCollection<Guid> customerIds, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, cancellationToken);
        await using var command = new NpgsqlCommand("""
            WITH RECURSIVE chain(customer_id, id, parent_id, kind, hide) AS (
                SELECT c.id, p.id, p.parent_id, p.kind, p.hide_prices FROM customers c JOIN places p ON p.id = c.place_id WHERE c.id = ANY(@ids)
                UNION ALL
                SELECT chain.customer_id, p.id, p.parent_id, p.kind, p.hide_prices FROM places p JOIN chain ON p.id = chain.parent_id
            )
            SELECT DISTINCT customer_id FROM chain WHERE kind = 'city' AND hide
            """, connection, transaction);
        command.Parameters.AddWithValue("ids", customerIds.ToArray());
        var hidden = new HashSet<Guid>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) hidden.Add(reader.GetGuid(0));
        return hidden;
    }

    public Task<bool> SetCityHidesPricesAsync(TenantId tenantId, UserId actorId, Guid placeId, bool hide, CancellationToken cancellationToken) =>
        SetAsync(tenantId, actorId, "UPDATE places SET hide_prices = @hide WHERE id = @id AND kind = 'city'", "place.prices_visibility_changed", placeId, hide, cancellationToken);

    public Task<bool> SetRegionHidesPricesAsync(TenantId tenantId, UserId actorId, Guid regionId, bool hide, CancellationToken cancellationToken) =>
        SetAsync(tenantId, actorId, "UPDATE regions SET hide_prices = @hide WHERE id = @id", "region.prices_visibility_changed", regionId, hide, cancellationToken);

    private async Task<bool> SetAsync(TenantId tenantId, UserId actorId, string sql, string action, Guid id, bool hide, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, cancellationToken);
        await using (var update = new NpgsqlCommand(sql, connection, transaction))
        {
            update.Parameters.AddWithValue("id", id);
            update.Parameters.AddWithValue("hide", hide);
            if (await update.ExecuteNonQueryAsync(cancellationToken) == 0) return false;
        }
        await using (var audit = new NpgsqlCommand("INSERT INTO audit_logs (id, tenant_id, actor_id, action, entity_id, occurred_at) VALUES (gen_random_uuid(), @tenant, @actor, @action, @id::text, now())", connection, transaction))
        {
            audit.Parameters.AddWithValue("tenant", tenantId.Value);
            audit.Parameters.AddWithValue("actor", actorId.Value);
            audit.Parameters.AddWithValue("action", action);
            audit.Parameters.AddWithValue("id", id);
            await audit.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private static async Task<NpgsqlTransaction> BeginAsync(NpgsqlConnection connection, TenantId tenantId, CancellationToken cancellationToken)
    {
        var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var set = new NpgsqlCommand("SELECT set_config('app.current_tenant', @tenant, true)", connection, transaction);
        set.Parameters.AddWithValue("tenant", tenantId.Value.ToString());
        await set.ExecuteNonQueryAsync(cancellationToken);
        return transaction;
    }
}
