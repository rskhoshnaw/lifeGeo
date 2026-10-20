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
    private const string ProductColumns = "id, code, name, name_ckb, name_ar, name_en, unit, currency, base_price, status, updated_at, specialty, manufacturer, expiry_date, stock_qty, notes, (SELECT ph.photo_key FROM product_photos ph WHERE ph.tenant_id = products.tenant_id AND ph.product_id = products.id)";

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
        while (await reader.ReadAsync(cancellationToken)) items.Add(ReadProduct(reader, 11));
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

    public async Task<bool> SetDetailsAsync(TenantId tenantId, UserId actorId, Guid productId, ProductDetailsRequest details, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, actorId, cancellationToken);
        await using (var update = new NpgsqlCommand("""
            UPDATE products SET specialty = @specialty, manufacturer = @manufacturer, expiry_date = @expiry, stock_qty = @stock, notes = @notes, updated_at = now()
            WHERE tenant_id = @tenant AND id = @id
            """, connection, transaction))
        {
            update.Parameters.AddWithValue("id", productId);
            update.Parameters.AddWithValue("tenant", tenantId.Value);
            update.Parameters.Add(new NpgsqlParameter("specialty", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)details.Specialty ?? DBNull.Value });
            update.Parameters.Add(new NpgsqlParameter("manufacturer", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)details.Manufacturer ?? DBNull.Value });
            update.Parameters.Add(new NpgsqlParameter("expiry", NpgsqlTypes.NpgsqlDbType.Date) { Value = (object?)details.ExpiryDate ?? DBNull.Value });
            update.Parameters.Add(new NpgsqlParameter("stock", NpgsqlTypes.NpgsqlDbType.Numeric) { Value = (object?)details.StockQty ?? DBNull.Value });
            update.Parameters.Add(new NpgsqlParameter("notes", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)details.Notes ?? DBNull.Value });
            if (await update.ExecuteNonQueryAsync(cancellationToken) == 0) return false;
        }
        await AuditAsync(connection, transaction, tenantId, actorId, "product.details_changed", productId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<bool> SetPhotoAsync(TenantId tenantId, UserId actorId, Guid productId, ProductPhoto photo, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, actorId, cancellationToken);
        if (!await ExistsAsync(connection, transaction, "SELECT 1 FROM products WHERE tenant_id = @tenant AND id = @id", tenantId, productId, cancellationToken)) return false;
        await using (var upsert = new NpgsqlCommand("""
            INSERT INTO product_photos (product_id, tenant_id, photo_key, content_type, data, updated_at) VALUES (@id, @tenant, @key, @type, @data, now())
            ON CONFLICT (product_id) DO UPDATE SET photo_key = @key, content_type = @type, data = @data, updated_at = now()
            """, connection, transaction))
        {
            upsert.Parameters.AddWithValue("id", productId);
            upsert.Parameters.AddWithValue("tenant", tenantId.Value);
            upsert.Parameters.AddWithValue("key", Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)).ToLowerInvariant());
            upsert.Parameters.AddWithValue("type", photo.ContentType);
            upsert.Parameters.Add(new NpgsqlParameter("data", NpgsqlTypes.NpgsqlDbType.Bytea) { Value = photo.Data });
            await upsert.ExecuteNonQueryAsync(cancellationToken);
        }
        await TouchAsync(connection, transaction, tenantId, productId, cancellationToken);
        await AuditAsync(connection, transaction, tenantId, actorId, "product.photo_changed", productId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<bool> RemovePhotoAsync(TenantId tenantId, UserId actorId, Guid productId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, actorId, cancellationToken);
        await using (var delete = new NpgsqlCommand("DELETE FROM product_photos WHERE tenant_id = @tenant AND product_id = @id", connection, transaction))
        {
            delete.Parameters.AddWithValue("tenant", tenantId.Value);
            delete.Parameters.AddWithValue("id", productId);
            if (await delete.ExecuteNonQueryAsync(cancellationToken) == 0) return false;
        }
        await TouchAsync(connection, transaction, tenantId, productId, cancellationToken);
        await AuditAsync(connection, transaction, tenantId, actorId, "product.photo_removed", productId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    // Read by the photo key alone (no company is set: product_photos has no row level security).
    public async Task<ProductPhoto?> GetPhotoAsync(string key, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("SELECT content_type, data FROM product_photos WHERE photo_key = @key", connection);
        command.Parameters.AddWithValue("key", key);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? new ProductPhoto(reader.GetString(0), reader.GetFieldValue<byte[]>(1)) : null;
    }

    private static async Task TouchAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, TenantId tenantId, Guid productId, CancellationToken cancellationToken)
    {
        await using var touch = new NpgsqlCommand("UPDATE products SET updated_at = now() WHERE tenant_id = @tenant AND id = @id", connection, transaction);
        touch.Parameters.AddWithValue("tenant", tenantId.Value);
        touch.Parameters.AddWithValue("id", productId);
        await touch.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<CityPriceDto>?> ListCityPricesAsync(TenantId tenantId, Guid productId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, null, cancellationToken);
        if (!await ExistsAsync(connection, transaction, "SELECT 1 FROM products WHERE tenant_id = @tenant AND id = @id", tenantId, productId, cancellationToken)) return null;
        await using var command = new NpgsqlCommand("""
            SELECT r.id, r.product_id, r.city_id, g.name, r.price, r.valid_from, r.valid_to
            FROM city_prices r
            JOIN places g ON g.tenant_id = r.tenant_id AND g.id = r.city_id
            WHERE r.tenant_id = @tenant AND r.product_id = @id
            ORDER BY r.valid_from DESC, r.created_at DESC, r.id
            """, connection, transaction);
        command.Parameters.AddWithValue("tenant", tenantId.Value);
        command.Parameters.AddWithValue("id", productId);
        var items = new List<CityPriceDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            items.Add(new CityPriceDto(reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetString(3), reader.GetDecimal(4),
                reader.GetFieldValue<DateOnly>(5), reader.IsDBNull(6) ? null : reader.GetFieldValue<DateOnly>(6)));
        return items;
    }

    public async Task<(CatalogResult Result, Guid? Id)> CreateCityPriceAsync(TenantId tenantId, UserId actorId, Guid productId, CreateCityPriceRequest request,
        DateOnly validFrom, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, actorId, cancellationToken);

        // The product row is locked so two prices of one product cannot be checked and written at the same time.
        if (!await ExistsAsync(connection, transaction, "SELECT 1 FROM products WHERE tenant_id = @tenant AND id = @id FOR UPDATE", tenantId, productId, cancellationToken))
            return (CatalogResult.NotFound, null);
        await using (var city = new NpgsqlCommand("SELECT kind FROM places WHERE tenant_id = @tenant AND id = @id AND merged_into IS NULL", connection, transaction))
        {
            city.Parameters.AddWithValue("tenant", tenantId.Value);
            city.Parameters.AddWithValue("id", request.CityId!.Value);
            var kind = await city.ExecuteScalarAsync(cancellationToken) as string;
            if (kind is null) return (CatalogResult.NotFound, null);
            if (kind != "city") return (CatalogResult.InvalidData, null);
        }
        await using (var overlap = new NpgsqlCommand("""
            SELECT 1 FROM city_prices
            WHERE tenant_id = @tenant AND product_id = @product AND city_id = @city
              AND valid_from <= coalesce(@to, 'infinity'::date) AND coalesce(valid_to, 'infinity'::date) >= @from
            """, connection, transaction))
        {
            AddCityPriceParameters(overlap, tenantId, productId, request, validFrom);
            if (await overlap.ExecuteScalarAsync(cancellationToken) is not null) return (CatalogResult.Conflict, null);
        }

        var id = Guid.NewGuid();
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO city_prices (id, tenant_id, product_id, city_id, price, valid_from, valid_to, created_by)
            VALUES (@id, @tenant, @product, @city, @price, @from, @to, @actor)
            """, connection, transaction))
        {
            AddCityPriceParameters(insert, tenantId, productId, request, validFrom);
            insert.Parameters.AddWithValue("id", id);
            insert.Parameters.AddWithValue("price", request.Price!.Value);
            insert.Parameters.AddWithValue("actor", actorId.Value);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        await AuditAsync(connection, transaction, tenantId, actorId, "city_price.created", id, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (CatalogResult.Success, id);
    }

    public Task<CatalogResult> EndCityPriceAsync(TenantId tenantId, UserId actorId, Guid cityPriceId, DateOnly validTo, CancellationToken cancellationToken) =>
        EndAsync("city_prices", "city_price.ended", tenantId, actorId, cityPriceId, validTo, cancellationToken);

    public Task<CatalogResult> EndDiscountAsync(TenantId tenantId, UserId actorId, Guid discountId, DateOnly validTo, CancellationToken cancellationToken) =>
        EndAsync("customer_discounts", "discount.ended", tenantId, actorId, discountId, validTo, cancellationToken);

    // The table name comes from the two callers above, never from a request.
    private async Task<CatalogResult> EndAsync(string table, string auditAction, TenantId tenantId, UserId actorId, Guid id, DateOnly validTo, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, actorId, cancellationToken);
        DateOnly from;
        DateOnly? currentEnd;
        await using (var read = new NpgsqlCommand($"SELECT valid_from, valid_to FROM {table} WHERE tenant_id = @tenant AND id = @id FOR UPDATE", connection, transaction))
        {
            read.Parameters.AddWithValue("tenant", tenantId.Value);
            read.Parameters.AddWithValue("id", id);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return CatalogResult.NotFound;
            from = reader.GetFieldValue<DateOnly>(0);
            currentEnd = reader.IsDBNull(1) ? null : reader.GetFieldValue<DateOnly>(1);
        }
        if (validTo < from || (currentEnd is { } end && validTo > end)) return CatalogResult.InvalidData;
        await using (var update = new NpgsqlCommand($"UPDATE {table} SET valid_to = @to WHERE tenant_id = @tenant AND id = @id", connection, transaction))
        {
            update.Parameters.AddWithValue("to", validTo);
            update.Parameters.AddWithValue("tenant", tenantId.Value);
            update.Parameters.AddWithValue("id", id);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
        await AuditAsync(connection, transaction, tenantId, actorId, auditAction, id, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return CatalogResult.Success;
    }

    public async Task<IReadOnlyList<CustomerDiscountDto>> ListDiscountsAsync(TenantId tenantId, Guid customerId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, null, cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT d.id, d.customer_id, d.product_id, p.code, d.kind, d.value, d.valid_from, d.valid_to
            FROM customer_discounts d
            LEFT JOIN products p ON p.tenant_id = d.tenant_id AND p.id = d.product_id
            WHERE d.tenant_id = @tenant AND d.customer_id = @customer
            ORDER BY d.valid_from DESC, d.created_at DESC, d.id
            """, connection, transaction);
        command.Parameters.AddWithValue("tenant", tenantId.Value);
        command.Parameters.AddWithValue("customer", customerId);
        var items = new List<CustomerDiscountDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            items.Add(new CustomerDiscountDto(reader.GetGuid(0), reader.GetGuid(1), reader.IsDBNull(2) ? null : reader.GetGuid(2), reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetString(4), reader.GetDecimal(5), reader.GetFieldValue<DateOnly>(6), reader.IsDBNull(7) ? null : reader.GetFieldValue<DateOnly>(7)));
        return items;
    }

    public async Task<(CatalogResult Result, Guid? Id)> CreateDiscountAsync(TenantId tenantId, UserId actorId, Guid customerId, CreateDiscountRequest request,
        DateOnly validFrom, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, actorId, cancellationToken);
        if (!await ExistsAsync(connection, transaction, "SELECT 1 FROM customers WHERE tenant_id = @tenant AND id = @id", tenantId, customerId, cancellationToken))
            return (CatalogResult.NotFound, null);
        if (request.ProductId is { } product &&
            !await ExistsAsync(connection, transaction, "SELECT 1 FROM products WHERE tenant_id = @tenant AND id = @id", tenantId, product, cancellationToken))
            return (CatalogResult.NotFound, null);

        var id = Guid.NewGuid();
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO customer_discounts (id, tenant_id, customer_id, product_id, kind, value, valid_from, valid_to, created_by)
            VALUES (@id, @tenant, @customer, @product, @kind, @value, @from, @to, @actor)
            """, connection, transaction))
        {
            insert.Parameters.AddWithValue("id", id);
            insert.Parameters.AddWithValue("tenant", tenantId.Value);
            insert.Parameters.AddWithValue("customer", customerId);
            insert.Parameters.Add(new NpgsqlParameter("product", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)request.ProductId ?? DBNull.Value });
            insert.Parameters.AddWithValue("kind", request.Kind!);
            insert.Parameters.AddWithValue("value", request.Value!.Value);
            insert.Parameters.AddWithValue("from", validFrom);
            insert.Parameters.Add(new NpgsqlParameter("to", NpgsqlTypes.NpgsqlDbType.Date) { Value = request.ValidTo is { } to ? to : DBNull.Value });
            insert.Parameters.AddWithValue("actor", actorId.Value);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        await AuditAsync(connection, transaction, tenantId, actorId, "discount.created", id, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (CatalogResult.Success, id);
    }

    public async Task<PricingData> GetPricingAsync(TenantId tenantId, Guid customerId, DateOnly day, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, null, cancellationToken);
        var products = new List<ProductPriceRow>();
        // The customer's city: the place of the customer or the closest place above it that is a city.
        await using (var command = new NpgsqlCommand($"""
            WITH RECURSIVE chain(id, parent_id, kind) AS (
                SELECT p.id, p.parent_id, p.kind FROM places p
                JOIN customers c ON c.tenant_id = p.tenant_id AND c.place_id = p.id
                WHERE c.tenant_id = @tenant AND c.id = @customer
                UNION ALL
                SELECT p.id, p.parent_id, p.kind FROM places p JOIN chain ON p.id = chain.parent_id WHERE p.tenant_id = @tenant
            ),
            city AS (SELECT id FROM chain WHERE kind = 'city' LIMIT 1)
            SELECT p.id, p.code, p.name, p.name_ckb, p.name_ar, p.name_en, p.unit, p.currency, p.base_price, p.status, p.updated_at, cp.price, cp.id, p.specialty, p.manufacturer, p.expiry_date, p.stock_qty, p.notes,
                   (SELECT ph.photo_key FROM product_photos ph WHERE ph.tenant_id = p.tenant_id AND ph.product_id = p.id)
            FROM products p
            LEFT JOIN LATERAL (
                SELECT r.id, r.price FROM city_prices r
                WHERE r.tenant_id = p.tenant_id AND r.product_id = p.id AND r.city_id = (SELECT id FROM city)
                  AND r.valid_from <= @day AND coalesce(r.valid_to, 'infinity'::date) >= @day
                ORDER BY r.valid_from DESC LIMIT 1) cp ON TRUE
            WHERE p.tenant_id = @tenant AND p.status = 'active'
            ORDER BY p.code COLLATE "C"
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("tenant", tenantId.Value);
            command.Parameters.AddWithValue("customer", customerId);
            command.Parameters.AddWithValue("day", day);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                products.Add(new ProductPriceRow(ReadProduct(reader, 13), reader.IsDBNull(11) ? null : reader.GetDecimal(11), reader.IsDBNull(12) ? null : reader.GetGuid(12)));
        }
        var discounts = new List<DiscountRow>();
        await using (var command = new NpgsqlCommand("""
            SELECT id, product_id, kind, value, created_at FROM customer_discounts
            WHERE tenant_id = @tenant AND customer_id = @customer AND valid_from <= @day AND coalesce(valid_to, 'infinity'::date) >= @day
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("tenant", tenantId.Value);
            command.Parameters.AddWithValue("customer", customerId);
            command.Parameters.AddWithValue("day", day);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                discounts.Add(new DiscountRow(reader.GetGuid(0), reader.IsDBNull(1) ? null : reader.GetGuid(1), reader.GetString(2), reader.GetDecimal(3), reader.GetFieldValue<DateTimeOffset>(4)));
        }
        return new PricingData(products, discounts);
    }

    private static async Task<bool> ExistsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, TenantId tenantId, Guid id, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("tenant", tenantId.Value);
        command.Parameters.AddWithValue("id", id);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static ProductDto ReadProduct(NpgsqlDataReader reader, int extra) => new(reader.GetGuid(0), reader.GetString(1), reader.GetString(2),
        reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5),
        reader.GetString(6), reader.GetString(7), reader.GetDecimal(8), reader.GetString(9), reader.GetFieldValue<DateTimeOffset>(10),
        reader.IsDBNull(extra) ? null : reader.GetString(extra), reader.IsDBNull(extra + 1) ? null : reader.GetString(extra + 1),
        reader.IsDBNull(extra + 2) ? null : reader.GetFieldValue<DateOnly>(extra + 2), reader.IsDBNull(extra + 3) ? null : reader.GetDecimal(extra + 3),
        reader.IsDBNull(extra + 4) ? null : reader.GetString(extra + 4), reader.IsDBNull(extra + 5) ? null : reader.GetString(extra + 5));

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

    private static void AddCityPriceParameters(NpgsqlCommand command, TenantId tenantId, Guid productId, CreateCityPriceRequest request, DateOnly validFrom)
    {
        command.Parameters.AddWithValue("tenant", tenantId.Value);
        command.Parameters.AddWithValue("product", productId);
        command.Parameters.AddWithValue("city", request.CityId!.Value);
        command.Parameters.AddWithValue("from", validFrom);
        command.Parameters.Add(new NpgsqlParameter("to", NpgsqlTypes.NpgsqlDbType.Date) { Value = request.ValidTo is { } to ? to : DBNull.Value });
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
