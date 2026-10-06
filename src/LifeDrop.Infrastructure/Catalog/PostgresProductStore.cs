using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LifeDrop.Application.Catalog;
using LifeDrop.Domain.Common;
using Npgsql;

namespace LifeDrop.Infrastructure.Catalog;

public sealed class PostgresProductStore(NpgsqlDataSource dataSource) : IProductStore
{
    private const string ProductColumns = "id, code, name, name_ckb, name_ar, name_en, unit, currency, base_price, status, updated_at";

    public async Task<IReadOnlyList<ProductDto>> ListAsync(TenantId tenantId, bool includeInactive, string? query, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, null, cancellationToken);
        await using var command = new NpgsqlCommand($"""
            SELECT {ProductColumns} FROM products
            WHERE (@all OR status = 'active')
              AND (@q::text IS NULL OR code ILIKE @q ESCAPE '\' OR name ILIKE @q ESCAPE '\' OR name_ckb ILIKE @q ESCAPE '\' OR name_ar ILIKE @q ESCAPE '\' OR name_en ILIKE @q ESCAPE '\')
            ORDER BY code COLLATE "C"
            LIMIT 1000
            """, connection, transaction);
        command.Parameters.AddWithValue("all", includeInactive);
        command.Parameters.Add(new NpgsqlParameter("q", NpgsqlTypes.NpgsqlDbType.Text) { Value = query is null ? DBNull.Value : "%" + query.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%" });
        var items = new List<ProductDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) items.Add(ReadProduct(reader));
        return items;
    }

    public async Task<Guid?> CreateAsync(TenantId tenantId, UserId actorId, SaveProductRequest product, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, actorId, cancellationToken);
        var id = Guid.NewGuid();
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO products (id, tenant_id, code, name, name_ckb, name_ar, name_en, unit, currency, base_price, status, created_by)
            VALUES (@id, @tenant, @code, @name, @ckb, @ar, @en, @unit, @currency, @price, @status, @actor)
            ON CONFLICT (tenant_id, code) DO NOTHING
            """, connection, transaction))
        {
            insert.Parameters.AddWithValue("id", id);
            insert.Parameters.AddWithValue("tenant", tenantId.Value);
            insert.Parameters.AddWithValue("actor", actorId.Value);
            insert.Parameters.AddWithValue("code", product.Code!);
            AddFields(insert, product);
            if (await insert.ExecuteNonQueryAsync(cancellationToken) == 0) return null;
        }
        await AuditAsync(connection, transaction, tenantId, actorId, "product.created", id, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return id;
    }

    public async Task<bool> UpdateAsync(TenantId tenantId, UserId actorId, Guid productId, SaveProductRequest product, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, actorId, cancellationToken);
        await using (var update = new NpgsqlCommand("""
            UPDATE products SET name = @name, name_ckb = @ckb, name_ar = @ar, name_en = @en, unit = @unit, currency = @currency,
                base_price = @price, status = @status, updated_at = now()
            WHERE tenant_id = @tenant AND id = @id
            """, connection, transaction))
        {
            update.Parameters.AddWithValue("id", productId);
            update.Parameters.AddWithValue("tenant", tenantId.Value);
            AddFields(update, product);
            if (await update.ExecuteNonQueryAsync(cancellationToken) == 0) return false;
        }
        await AuditAsync(connection, transaction, tenantId, actorId, "product.updated", productId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<PriceRuleDto>?> ListRulesAsync(TenantId tenantId, Guid productId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, null, cancellationToken);
        await using (var exists = new NpgsqlCommand("SELECT 1 FROM products WHERE tenant_id = @tenant AND id = @id", connection, transaction))
        {
            exists.Parameters.AddWithValue("tenant", tenantId.Value);
            exists.Parameters.AddWithValue("id", productId);
            if (await exists.ExecuteScalarAsync(cancellationToken) is null) return null;
        }
        await using var command = new NpgsqlCommand("""
            SELECT r.id, r.product_id, r.customer_id, r.region_id,
                   CASE WHEN r.customer_id IS NOT NULL THEN c.code || ' · ' || c.name ELSE g.name END,
                   r.price, r.valid_from, r.valid_to
            FROM price_rules r
            LEFT JOIN customers c ON c.tenant_id = r.tenant_id AND c.id = r.customer_id
            LEFT JOIN regions g ON g.tenant_id = r.tenant_id AND g.id = r.region_id
            WHERE r.tenant_id = @tenant AND r.product_id = @id
            ORDER BY r.valid_from DESC, r.created_at DESC, r.id
            """, connection, transaction);
        command.Parameters.AddWithValue("tenant", tenantId.Value);
        command.Parameters.AddWithValue("id", productId);
        var items = new List<PriceRuleDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            items.Add(new PriceRuleDto(reader.GetGuid(0), reader.GetGuid(1), reader.IsDBNull(2) ? "region" : "customer",
                reader.IsDBNull(2) ? null : reader.GetGuid(2), reader.IsDBNull(3) ? null : reader.GetGuid(3), reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetDecimal(5), reader.GetFieldValue<DateOnly>(6), reader.IsDBNull(7) ? null : reader.GetFieldValue<DateOnly>(7)));
        return items;
    }

    public async Task<(CatalogResult Result, Guid? Id)> CreateRuleAsync(TenantId tenantId, UserId actorId, Guid productId, CreatePriceRuleRequest rule, DateOnly validFrom,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, actorId, cancellationToken);

        // The product row is locked so two rules of one product cannot be checked and written at the same time.
        await using (var lockRow = new NpgsqlCommand("SELECT 1 FROM products WHERE tenant_id = @tenant AND id = @id FOR UPDATE", connection, transaction))
        {
            lockRow.Parameters.AddWithValue("tenant", tenantId.Value);
            lockRow.Parameters.AddWithValue("id", productId);
            if (await lockRow.ExecuteScalarAsync(cancellationToken) is null) return (CatalogResult.NotFound, null);
        }
        await using (var target = new NpgsqlCommand(rule.CustomerId is not null
            ? "SELECT 1 FROM customers WHERE tenant_id = @tenant AND id = @target"
            : "SELECT 1 FROM regions WHERE tenant_id = @tenant AND id = @target", connection, transaction))
        {
            target.Parameters.AddWithValue("tenant", tenantId.Value);
            target.Parameters.AddWithValue("target", (rule.CustomerId ?? rule.RegionId)!.Value);
            if (await target.ExecuteScalarAsync(cancellationToken) is null) return (CatalogResult.NotFound, null);
        }
        await using (var overlap = new NpgsqlCommand("""
            SELECT 1 FROM price_rules
            WHERE tenant_id = @tenant AND product_id = @product
              AND customer_id IS NOT DISTINCT FROM @customer AND region_id IS NOT DISTINCT FROM @region
              AND valid_from <= coalesce(@to, 'infinity'::date) AND coalesce(valid_to, 'infinity'::date) >= @from
            """, connection, transaction))
        {
            AddRuleParameters(overlap, tenantId, productId, rule, validFrom);
            if (await overlap.ExecuteScalarAsync(cancellationToken) is not null) return (CatalogResult.Conflict, null);
        }

        var id = Guid.NewGuid();
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO price_rules (id, tenant_id, product_id, customer_id, region_id, price, valid_from, valid_to, created_by)
            VALUES (@id, @tenant, @product, @customer, @region, @price, @from, @to, @actor)
            """, connection, transaction))
        {
            AddRuleParameters(insert, tenantId, productId, rule, validFrom);
            insert.Parameters.AddWithValue("id", id);
            insert.Parameters.AddWithValue("price", rule.Price!.Value);
            insert.Parameters.AddWithValue("actor", actorId.Value);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        await AuditAsync(connection, transaction, tenantId, actorId, "price_rule.created", id, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (CatalogResult.Success, id);
    }

    public async Task<CatalogResult> EndRuleAsync(TenantId tenantId, UserId actorId, Guid ruleId, DateOnly validTo, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, actorId, cancellationToken);
        DateOnly from;
        DateOnly? currentEnd;
        await using (var read = new NpgsqlCommand("SELECT valid_from, valid_to FROM price_rules WHERE tenant_id = @tenant AND id = @id FOR UPDATE", connection, transaction))
        {
            read.Parameters.AddWithValue("tenant", tenantId.Value);
            read.Parameters.AddWithValue("id", ruleId);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return CatalogResult.NotFound;
            from = reader.GetFieldValue<DateOnly>(0);
            currentEnd = reader.IsDBNull(1) ? null : reader.GetFieldValue<DateOnly>(1);
        }
        if (validTo < from || (currentEnd is { } end && validTo > end)) return CatalogResult.InvalidData;
        await using (var update = new NpgsqlCommand("UPDATE price_rules SET valid_to = @to WHERE tenant_id = @tenant AND id = @id", connection, transaction))
        {
            update.Parameters.AddWithValue("to", validTo);
            update.Parameters.AddWithValue("tenant", tenantId.Value);
            update.Parameters.AddWithValue("id", ruleId);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
        await AuditAsync(connection, transaction, tenantId, actorId, "price_rule.ended", ruleId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return CatalogResult.Success;
    }

    public async Task<IReadOnlyList<EffectivePriceDto>> EffectivePricesAsync(TenantId tenantId, Guid customerId, Guid? customerRegionId, DateOnly day,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, null, cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT p.id, p.code, p.name, p.name_ckb, p.name_ar, p.name_en, p.unit, p.currency,
                   coalesce(cr.price, rr.price, p.base_price),
                   CASE WHEN cr.id IS NOT NULL THEN 'customer' WHEN rr.id IS NOT NULL THEN 'region' ELSE 'base' END,
                   coalesce(cr.id, rr.id)
            FROM products p
            LEFT JOIN LATERAL (
                SELECT r.id, r.price FROM price_rules r
                WHERE r.tenant_id = p.tenant_id AND r.product_id = p.id AND r.customer_id = @customer
                  AND r.valid_from <= @day AND coalesce(r.valid_to, 'infinity'::date) >= @day
                ORDER BY r.valid_from DESC LIMIT 1) cr ON TRUE
            LEFT JOIN LATERAL (
                SELECT r.id, r.price FROM price_rules r
                WHERE r.tenant_id = p.tenant_id AND r.product_id = p.id AND @region::uuid IS NOT NULL AND r.region_id = @region
                  AND r.valid_from <= @day AND coalesce(r.valid_to, 'infinity'::date) >= @day
                ORDER BY r.valid_from DESC LIMIT 1) rr ON TRUE
            WHERE p.tenant_id = @tenant AND p.status = 'active'
            ORDER BY p.code COLLATE "C"
            """, connection, transaction);
        command.Parameters.AddWithValue("tenant", tenantId.Value);
        command.Parameters.AddWithValue("customer", customerId);
        command.Parameters.Add(new NpgsqlParameter("region", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)customerRegionId ?? DBNull.Value });
        command.Parameters.AddWithValue("day", day);
        var items = new List<EffectivePriceDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            items.Add(new EffectivePriceDto(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5), reader.GetString(6), reader.GetString(7),
                reader.GetDecimal(8), reader.GetString(9), reader.IsDBNull(10) ? null : reader.GetGuid(10)));
        return items;
    }

    private static ProductDto ReadProduct(NpgsqlDataReader reader) => new(reader.GetGuid(0), reader.GetString(1), reader.GetString(2),
        reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5),
        reader.GetString(6), reader.GetString(7), reader.GetDecimal(8), reader.GetString(9), reader.GetFieldValue<DateTimeOffset>(10));

    private static void AddFields(NpgsqlCommand command, SaveProductRequest product)
    {
        command.Parameters.AddWithValue("name", product.Name!);
        command.Parameters.Add(new NpgsqlParameter("ckb", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)product.NameCkb ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("ar", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)product.NameAr ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("en", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)product.NameEn ?? DBNull.Value });
        command.Parameters.AddWithValue("unit", product.Unit!);
        command.Parameters.AddWithValue("currency", product.Currency!);
        command.Parameters.AddWithValue("price", product.BasePrice!.Value);
        command.Parameters.AddWithValue("status", product.Status ?? "active");
    }

    private static void AddRuleParameters(NpgsqlCommand command, TenantId tenantId, Guid productId, CreatePriceRuleRequest rule, DateOnly validFrom)
    {
        command.Parameters.AddWithValue("tenant", tenantId.Value);
        command.Parameters.AddWithValue("product", productId);
        command.Parameters.Add(new NpgsqlParameter("customer", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)rule.CustomerId ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("region", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)rule.RegionId ?? DBNull.Value });
        command.Parameters.AddWithValue("from", validFrom);
        command.Parameters.Add(new NpgsqlParameter("to", NpgsqlTypes.NpgsqlDbType.Date) { Value = rule.ValidTo is { } to ? to : DBNull.Value });
    }

    private static async Task<NpgsqlTransaction> BeginAsync(NpgsqlConnection connection, TenantId tenantId, UserId? actorId, CancellationToken cancellationToken)
    {
        var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var set = new NpgsqlCommand("SELECT set_config('app.current_tenant', @tenant, true), set_config('app.current_actor', @actor, true)", connection, transaction);
        set.Parameters.AddWithValue("tenant", tenantId.Value.ToString());
        set.Parameters.AddWithValue("actor", actorId?.Value.ToString() ?? "");
        await set.ExecuteNonQueryAsync(cancellationToken);
        return transaction;
    }

    private static async Task AuditAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, TenantId tenantId, UserId actorId, string action, Guid entity,
        CancellationToken cancellationToken)
    {
        await using var audit = new NpgsqlCommand("""
            INSERT INTO audit_logs (id, tenant_id, actor_id, action, entity_id, occurred_at)
            VALUES (gen_random_uuid(), @tenant, @actor, @action, @entity, now())
            """, connection, transaction);
        audit.Parameters.AddWithValue("tenant", tenantId.Value);
        audit.Parameters.AddWithValue("actor", actorId.Value);
        audit.Parameters.AddWithValue("action", action);
        audit.Parameters.AddWithValue("entity", entity.ToString());
        await audit.ExecuteNonQueryAsync(cancellationToken);
    }
}
