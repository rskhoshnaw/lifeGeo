using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LifeDrop.Application.Catalog;
using LifeDrop.Application.Orders;
using LifeDrop.Application.Roles;
using LifeDrop.Domain.Common;
using Npgsql;

namespace LifeDrop.Infrastructure.Orders;

public sealed class PostgresOrderStore(NpgsqlDataSource dataSource) : IOrderStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string FormatNumber(long number) => $"O-{number:D6}";

    public async Task<IReadOnlyList<WarehouseDto>> ListWarehousesAsync(TenantId tenantId, bool includeInactive, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, null, cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT id, code, name, status FROM warehouses WHERE tenant_id = @tenant AND (@all OR status = 'active') ORDER BY code COLLATE "C"
            """, connection, transaction);
        command.Parameters.AddWithValue("tenant", tenantId.Value);
        command.Parameters.AddWithValue("all", includeInactive);
        var items = new List<WarehouseDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) items.Add(new WarehouseDto(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        return items;
    }

    public async Task<Guid?> CreateWarehouseAsync(TenantId tenantId, UserId actorId, SaveWarehouseRequest warehouse, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, actorId, cancellationToken);
        var id = Guid.NewGuid();
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO warehouses (id, tenant_id, code, name, status, created_by) VALUES (@id, @tenant, @code, @name, @status, @actor)
            ON CONFLICT (tenant_id, code) DO NOTHING
            """, connection, transaction))
        {
            insert.Parameters.AddWithValue("id", id);
            insert.Parameters.AddWithValue("tenant", tenantId.Value);
            insert.Parameters.AddWithValue("code", warehouse.Code!);
            insert.Parameters.AddWithValue("name", warehouse.Name!);
            insert.Parameters.AddWithValue("status", warehouse.Status ?? "active");
            insert.Parameters.AddWithValue("actor", actorId.Value);
            if (await insert.ExecuteNonQueryAsync(cancellationToken) == 0) return null;
        }
        await AuditAsync(connection, transaction, tenantId, actorId, "warehouse.created", id, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return id;
    }

    public async Task<bool> UpdateWarehouseAsync(TenantId tenantId, UserId actorId, Guid warehouseId, SaveWarehouseRequest warehouse, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, actorId, cancellationToken);
        await using (var update = new NpgsqlCommand("""
            UPDATE warehouses SET name = @name, status = @status, updated_at = now() WHERE tenant_id = @tenant AND id = @id
            """, connection, transaction))
        {
            update.Parameters.AddWithValue("tenant", tenantId.Value);
            update.Parameters.AddWithValue("id", warehouseId);
            update.Parameters.AddWithValue("name", warehouse.Name!);
            update.Parameters.AddWithValue("status", warehouse.Status ?? "active");
            if (await update.ExecuteNonQueryAsync(cancellationToken) == 0) return false;
        }
        await AuditAsync(connection, transaction, tenantId, actorId, "warehouse.updated", warehouseId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<(OrderResult Result, CreatedOrderDto? Order)> CreateAsync(TenantId tenantId, UserId actorId, NewOrder order, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, actorId, cancellationToken);

        // A retry of the same send gives back the order that exists.
        await using (var again = new NpgsqlCommand("""
            SELECT id, number, currency, total, created_by, customer_id FROM orders WHERE tenant_id = @tenant AND client_ref = @ref
            """, connection, transaction))
        {
            again.Parameters.AddWithValue("tenant", tenantId.Value);
            again.Parameters.AddWithValue("ref", order.ClientRef);
            await using var reader = await again.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                return reader.GetGuid(4) == actorId.Value && reader.GetGuid(5) == order.CustomerId
                    ? (OrderResult.Success, new CreatedOrderDto(reader.GetGuid(0), FormatNumber(reader.GetInt64(1)), reader.GetString(2), reader.GetDecimal(3), true))
                    : (OrderResult.Conflict, null);
            }
        }

        string? warehouseStatus;
        await using (var warehouse = new NpgsqlCommand("SELECT status FROM warehouses WHERE tenant_id = @tenant AND id = @id", connection, transaction))
        {
            warehouse.Parameters.AddWithValue("tenant", tenantId.Value);
            warehouse.Parameters.AddWithValue("id", order.WarehouseId);
            warehouseStatus = await warehouse.ExecuteScalarAsync(cancellationToken) as string;
        }
        if (warehouseStatus is null) return (OrderResult.NotFound, null);
        if (warehouseStatus != "active") return (OrderResult.InvalidData, null);

        long number;
        await using (var counter = new NpgsqlCommand("""
            INSERT INTO order_counters (tenant_id, last_number) VALUES (@tenant, 1)
            ON CONFLICT (tenant_id) DO UPDATE SET last_number = order_counters.last_number + 1
            RETURNING last_number
            """, connection, transaction))
        {
            counter.Parameters.AddWithValue("tenant", tenantId.Value);
            number = Convert.ToInt64(await counter.ExecuteScalarAsync(cancellationToken));
        }

        var id = Guid.NewGuid();
        try
        {
            await using (var insert = new NpgsqlCommand("""
                INSERT INTO orders (id, tenant_id, number, customer_id, visit_id, warehouse_id, created_by, currency, total, note, client_ref)
                VALUES (@id, @tenant, @number, @customer, @visit, @warehouse, @actor, @currency, @total, @note, @ref)
                """, connection, transaction))
            {
                insert.Parameters.AddWithValue("id", id);
                insert.Parameters.AddWithValue("tenant", tenantId.Value);
                insert.Parameters.AddWithValue("number", number);
                insert.Parameters.AddWithValue("customer", order.CustomerId);
                insert.Parameters.Add(new NpgsqlParameter("visit", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)order.VisitId ?? DBNull.Value });
                insert.Parameters.AddWithValue("warehouse", order.WarehouseId);
                insert.Parameters.AddWithValue("actor", actorId.Value);
                insert.Parameters.AddWithValue("currency", order.Currency);
                insert.Parameters.AddWithValue("total", order.Total);
                insert.Parameters.Add(new NpgsqlParameter("note", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)order.Note ?? DBNull.Value });
                insert.Parameters.AddWithValue("ref", order.ClientRef);
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }
            var lineNo = 0;
            foreach (var line in order.Lines)
            {
                lineNo++;
                await using var item = new NpgsqlCommand("""
                    INSERT INTO order_items (tenant_id, order_id, line_no, product_id, product_code, product_name, unit, quantity, list_price, unit_price, line_total, discounts)
                    VALUES (@tenant, @order, @line, @product, @code, @name, @unit, @quantity, @list, @price, @total, @discounts::jsonb)
                    """, connection, transaction);
                item.Parameters.AddWithValue("tenant", tenantId.Value);
                item.Parameters.AddWithValue("order", id);
                item.Parameters.AddWithValue("line", lineNo);
                item.Parameters.AddWithValue("product", line.ProductId);
                item.Parameters.AddWithValue("code", line.ProductCode);
                item.Parameters.AddWithValue("name", line.ProductName);
                item.Parameters.AddWithValue("unit", line.Unit);
                item.Parameters.AddWithValue("quantity", line.Quantity);
                item.Parameters.AddWithValue("list", line.ListPrice);
                item.Parameters.AddWithValue("price", line.UnitPrice);
                item.Parameters.AddWithValue("total", line.LineTotal);
                item.Parameters.AddWithValue("discounts", JsonSerializer.Serialize(line.Discounts, Json));
                await item.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            // The same ClientRef arrived twice at the same moment; the sender retries and gets the order that won.
            return (OrderResult.Conflict, null);
        }
        await AddHistoryAsync(connection, transaction, tenantId, id, "placed", actorId, null, cancellationToken);
        await AuditAsync(connection, transaction, tenantId, actorId, "order.created", id, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (OrderResult.Success, new CreatedOrderDto(id, FormatNumber(number), order.Currency, order.Total, false));
    }

    // Tenant: all; region: the customers of the region, the ones the user owns and the orders the user placed; own: the user's own orders and customers.
    private static string ScopeFilter(AccessScope scope) => (scope.Scope switch
    {
        DataScope.Tenant => "TRUE",
        DataScope.Region when scope.RegionId is not null => "(o.created_by = @actor OR c.owner_user_id = @actor OR c.region_id = @scopeRegion)",
        _ => "(o.created_by = @actor OR c.owner_user_id = @actor)"
    }) + scope.DoctorFilter("c.type");

    private static void AddScope(NpgsqlCommand command, AccessScope scope)
    {
        command.Parameters.AddWithValue("actor", scope.ActorId.Value);
        command.Parameters.Add(new NpgsqlParameter("scopeRegion", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)scope.RegionId ?? DBNull.Value });
    }

    private const string OrderColumns = """
        o.id, o.number, o.customer_id, c.code, c.name, o.visit_id, o.warehouse_id, w.name, o.created_by, u.email, o.status, o.currency, o.total,
        o.note, o.cancel_reason, o.created_at, o.driver_id, d.email
        """;

    private const string OrderJoins = """
        FROM orders o
        JOIN customers c ON c.tenant_id = o.tenant_id AND c.id = o.customer_id
        JOIN warehouses w ON w.tenant_id = o.tenant_id AND w.id = o.warehouse_id
        LEFT JOIN users u ON u.tenant_id = o.tenant_id AND u.id = o.created_by
        LEFT JOIN users d ON d.tenant_id = o.tenant_id AND d.id = o.driver_id
        """;

    private static OrderDto ReadOrder(NpgsqlDataReader reader) => new(reader.GetGuid(0), FormatNumber(reader.GetInt64(1)), reader.GetGuid(2), reader.GetString(3), reader.GetString(4),
        reader.IsDBNull(5) ? null : reader.GetGuid(5), reader.GetGuid(6), reader.GetString(7), reader.GetGuid(8), reader.IsDBNull(9) ? null : reader.GetString(9),
        reader.GetString(10), reader.GetString(11), reader.GetDecimal(12), reader.IsDBNull(13) ? null : reader.GetString(13), reader.IsDBNull(14) ? null : reader.GetString(14),
        reader.GetFieldValue<DateTimeOffset>(15), Array.Empty<OrderLineDto>(), reader.IsDBNull(16) ? null : reader.GetGuid(16), reader.IsDBNull(17) ? null : reader.GetString(17));

    public async Task<OrderDto?> GetAsync(TenantId tenantId, Guid orderId, AccessScope scope, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, null, cancellationToken);
        OrderDto? order;
        await using (var command = new NpgsqlCommand($"SELECT {OrderColumns} {OrderJoins} WHERE o.tenant_id = @tenant AND o.id = @id AND {ScopeFilter(scope)}", connection, transaction))
        {
            command.Parameters.AddWithValue("tenant", tenantId.Value);
            command.Parameters.AddWithValue("id", orderId);
            AddScope(command, scope);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            order = await reader.ReadAsync(cancellationToken) ? ReadOrder(reader) : null;
        }
        return order is null ? null : await LoadDetailsAsync(connection, transaction, tenantId, order, cancellationToken);
    }

    // The lines (with what the warehouse prepared) and the steps of one order.
    private static async Task<OrderDto> LoadDetailsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, TenantId tenantId, OrderDto order, CancellationToken cancellationToken)
    {
        var orderId = order.Id;
        var lines = new List<OrderLineDto>();
        await using (var items = new NpgsqlCommand("""
            SELECT line_no, product_id, product_code, product_name, unit, quantity, list_price, unit_price, line_total, discounts::text, prepared_quantity, shortage_reason, delivered_quantity, delivery_reason
            FROM order_items WHERE tenant_id = @tenant AND order_id = @id ORDER BY line_no
            """, connection, transaction))
        {
            items.Parameters.AddWithValue("tenant", tenantId.Value);
            items.Parameters.AddWithValue("id", orderId);
            await using var reader = await items.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                lines.Add(new OrderLineDto(reader.GetInt32(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetDecimal(5),
                    reader.GetDecimal(6), reader.GetDecimal(7), reader.GetDecimal(8),
                    JsonSerializer.Deserialize<List<AppliedDiscountDto>>(reader.GetString(9), Json) ?? new List<AppliedDiscountDto>(),
                    reader.IsDBNull(10) ? null : reader.GetDecimal(10), reader.IsDBNull(11) ? null : reader.GetString(11),
                    reader.IsDBNull(12) ? null : reader.GetDecimal(12), reader.IsDBNull(13) ? null : reader.GetString(13)));
        }
        var history = new List<OrderHistoryDto>();
        await using (var steps = new NpgsqlCommand("""
            SELECT s.status, u.email, s.note, s.occurred_at, s.latitude, s.longitude, s.distance_m FROM order_status_history s
            LEFT JOIN users u ON u.tenant_id = s.tenant_id AND u.id = s.actor_id
            WHERE s.tenant_id = @tenant AND s.order_id = @id ORDER BY s.occurred_at, s.id
            """, connection, transaction))
        {
            steps.Parameters.AddWithValue("tenant", tenantId.Value);
            steps.Parameters.AddWithValue("id", orderId);
            await using var reader = await steps.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                history.Add(new OrderHistoryDto(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetFieldValue<DateTimeOffset>(3),
                    reader.IsDBNull(4) ? null : reader.GetDouble(4), reader.IsDBNull(5) ? null : reader.GetDouble(5), reader.IsDBNull(6) ? null : reader.GetInt32(6)));
        }
        DeliveryInfo? info = null;
        await using (var extra = new NpgsqlCommand("""
            SELECT c.phone, c.address, ST_Y(c.geom::geometry), ST_X(c.geom::geometry), o.receiver_name, o.delivery_note, o.failure_reason
            FROM orders o JOIN customers c ON c.tenant_id = o.tenant_id AND c.id = o.customer_id WHERE o.tenant_id = @tenant AND o.id = @id
            """, connection, transaction))
        {
            extra.Parameters.AddWithValue("tenant", tenantId.Value);
            extra.Parameters.AddWithValue("id", orderId);
            await using var reader = await extra.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
                info = new DeliveryInfo(reader.IsDBNull(0) ? null : reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetDouble(2), reader.IsDBNull(3) ? null : reader.GetDouble(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6));
        }
        return order with { Items = lines, History = history, Delivery = info };
    }

    public async Task<IReadOnlyList<OrderDto>> ListAsync(TenantId tenantId, AccessScope scope, Guid? customerId, string? status, int limit, DateTimeOffset? before, Guid? beforeId,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, null, cancellationToken);
        await using var command = new NpgsqlCommand($"""
            SELECT {OrderColumns} {OrderJoins}
            WHERE o.tenant_id = @tenant AND {ScopeFilter(scope)}
              AND (@customer::uuid IS NULL OR o.customer_id = @customer)
              AND (@status::text IS NULL OR o.status = @status)
              AND (@before::timestamptz IS NULL OR (o.created_at, o.id) < (@before, @beforeId))
            ORDER BY o.created_at DESC, o.id DESC
            LIMIT @limit
            """, connection, transaction);
        command.Parameters.AddWithValue("tenant", tenantId.Value);
        AddScope(command, scope);
        command.Parameters.Add(new NpgsqlParameter("customer", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)customerId ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("status", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)status ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("before", NpgsqlTypes.NpgsqlDbType.TimestampTz) { Value = (object?)before ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("beforeId", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)beforeId ?? DBNull.Value });
        command.Parameters.AddWithValue("limit", limit);
        var items = new List<OrderDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) items.Add(ReadOrder(reader));
        return items;
    }

    public async Task<OrderResult> CancelAsync(TenantId tenantId, UserId actorId, Guid orderId, AccessScope scope, bool isManager, string reason, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, actorId, cancellationToken);
        string status;
        Guid createdBy;
        await using (var read = new NpgsqlCommand($"""
            SELECT o.status, o.created_by FROM orders o JOIN customers c ON c.tenant_id = o.tenant_id AND c.id = o.customer_id
            WHERE o.tenant_id = @tenant AND o.id = @id AND {ScopeFilter(scope)} FOR UPDATE OF o
            """, connection, transaction))
        {
            read.Parameters.AddWithValue("tenant", tenantId.Value);
            read.Parameters.AddWithValue("id", orderId);
            AddScope(read, scope);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return OrderResult.NotFound;
            status = reader.GetString(0);
            createdBy = reader.GetGuid(1);
        }
        if (!isManager && createdBy != actorId.Value) return OrderResult.Forbidden;
        // The creator cancels while the order is only placed; a manager until it has been handed to a driver.
        if (status != "placed" && !(isManager && status is "preparing" or "ready")) return OrderResult.Conflict;
        await using (var update = new NpgsqlCommand("""
            UPDATE orders SET status = 'cancelled', cancel_reason = @reason, cancelled_by = @actor, cancelled_at = now() WHERE tenant_id = @tenant AND id = @id
            """, connection, transaction))
        {
            update.Parameters.AddWithValue("reason", reason);
            update.Parameters.AddWithValue("actor", actorId.Value);
            update.Parameters.AddWithValue("tenant", tenantId.Value);
            update.Parameters.AddWithValue("id", orderId);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
        await AddHistoryAsync(connection, transaction, tenantId, orderId, "cancelled", actorId, reason, cancellationToken);
        await AuditAsync(connection, transaction, tenantId, actorId, "order.cancelled", orderId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return OrderResult.Success;
    }

    // ---- Staff of a warehouse ----

    public async Task<IReadOnlyList<StaffMemberDto>?> ListStaffAsync(TenantId tenantId, Guid warehouseId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, null, cancellationToken);
        if (!await ExistsAsync(connection, transaction, "SELECT 1 FROM warehouses WHERE tenant_id = @tenant AND id = @id", tenantId, warehouseId, cancellationToken)) return null;
        await using var command = new NpgsqlCommand("""
            SELECT s.user_id, u.email FROM warehouse_staff s JOIN users u ON u.tenant_id = s.tenant_id AND u.id = s.user_id
            WHERE s.tenant_id = @tenant AND s.warehouse_id = @id ORDER BY u.email
            """, connection, transaction);
        command.Parameters.AddWithValue("tenant", tenantId.Value);
        command.Parameters.AddWithValue("id", warehouseId);
        var items = new List<StaffMemberDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) items.Add(new StaffMemberDto(reader.GetGuid(0), reader.IsDBNull(1) ? null : reader.GetString(1)));
        return items;
    }

    public async Task<OrderResult> AddStaffAsync(TenantId tenantId, UserId actorId, Guid warehouseId, Guid userId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, actorId, cancellationToken);
        if (!await ExistsAsync(connection, transaction, "SELECT 1 FROM warehouses WHERE tenant_id = @tenant AND id = @id", tenantId, warehouseId, cancellationToken)) return OrderResult.NotFound;
        if (!await ExistsAsync(connection, transaction, "SELECT 1 FROM users WHERE tenant_id = @tenant AND id = @id", tenantId, userId, cancellationToken)) return OrderResult.NotFound;
        // Only a user whose roles give warehouse.work can be put to work in a warehouse.
        if (!await ExistsAsync(connection, transaction, """
            SELECT 1 FROM user_roles ur JOIN role_permissions rp ON rp.tenant_id = ur.tenant_id AND rp.role_id = ur.role_id
            WHERE ur.tenant_id = @tenant AND ur.user_id = @id AND rp.permission_key = 'warehouse.work'
            """, tenantId, userId, cancellationToken)) return OrderResult.InvalidData;
        await using (var insert = new NpgsqlCommand("INSERT INTO warehouse_staff (tenant_id, warehouse_id, user_id) VALUES (@tenant, @warehouse, @user) ON CONFLICT DO NOTHING", connection, transaction))
        {
            insert.Parameters.AddWithValue("tenant", tenantId.Value);
            insert.Parameters.AddWithValue("warehouse", warehouseId);
            insert.Parameters.AddWithValue("user", userId);
            if (await insert.ExecuteNonQueryAsync(cancellationToken) == 0) return OrderResult.Success;   // already there
        }
        await AuditAsync(connection, transaction, tenantId, actorId, "warehouse.staff_added", userId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return OrderResult.Success;
    }

    public async Task<bool> RemoveStaffAsync(TenantId tenantId, UserId actorId, Guid warehouseId, Guid userId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, actorId, cancellationToken);
        await using (var delete = new NpgsqlCommand("DELETE FROM warehouse_staff WHERE tenant_id = @tenant AND warehouse_id = @warehouse AND user_id = @user", connection, transaction))
        {
            delete.Parameters.AddWithValue("tenant", tenantId.Value);
            delete.Parameters.AddWithValue("warehouse", warehouseId);
            delete.Parameters.AddWithValue("user", userId);
            if (await delete.ExecuteNonQueryAsync(cancellationToken) == 0) return false;
        }
        await AuditAsync(connection, transaction, tenantId, actorId, "warehouse.staff_removed", userId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<DriverDto>> ListDriversAsync(TenantId tenantId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, null, cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT DISTINCT u.id, u.email FROM users u
            JOIN user_roles ur ON ur.tenant_id = u.tenant_id AND ur.user_id = u.id
            JOIN roles r ON r.tenant_id = ur.tenant_id AND r.id = ur.role_id
            WHERE u.tenant_id = @tenant AND r.name = 'Driver' ORDER BY u.email
            """, connection, transaction);
        command.Parameters.AddWithValue("tenant", tenantId.Value);
        var items = new List<DriverDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) items.Add(new DriverDto(reader.GetGuid(0), reader.IsDBNull(1) ? null : reader.GetString(1)));
        return items;
    }

    // ---- Warehouse work ----

    private const string WorksHere = "EXISTS (SELECT 1 FROM warehouse_staff s WHERE s.tenant_id = o.tenant_id AND s.warehouse_id = o.warehouse_id AND s.user_id = @actor)";

    public async Task<IReadOnlyList<OrderDto>> ListForWarehouseAsync(TenantId tenantId, UserId actorId, string? status, int limit, DateTimeOffset? before, Guid? beforeId,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, null, cancellationToken);
        await using var command = new NpgsqlCommand($"""
            SELECT {OrderColumns} {OrderJoins}
            WHERE o.tenant_id = @tenant AND {WorksHere}
              AND ((@status::text IS NULL AND o.status IN ('placed', 'preparing', 'ready')) OR o.status = @status)
              AND (@before::timestamptz IS NULL OR (o.created_at, o.id) < (@before, @beforeId))
            ORDER BY o.created_at DESC, o.id DESC
            LIMIT @limit
            """, connection, transaction);
        command.Parameters.AddWithValue("tenant", tenantId.Value);
        command.Parameters.AddWithValue("actor", actorId.Value);
        command.Parameters.Add(new NpgsqlParameter("status", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)status ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("before", NpgsqlTypes.NpgsqlDbType.TimestampTz) { Value = (object?)before ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("beforeId", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)beforeId ?? DBNull.Value });
        command.Parameters.AddWithValue("limit", limit);
        var items = new List<OrderDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) items.Add(ReadOrder(reader));
        return items;
    }

    public async Task<OrderDto?> GetForWarehouseAsync(TenantId tenantId, UserId actorId, Guid orderId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, null, cancellationToken);
        OrderDto? order;
        await using (var command = new NpgsqlCommand($"SELECT {OrderColumns} {OrderJoins} WHERE o.tenant_id = @tenant AND o.id = @id AND {WorksHere}", connection, transaction))
        {
            command.Parameters.AddWithValue("tenant", tenantId.Value);
            command.Parameters.AddWithValue("id", orderId);
            command.Parameters.AddWithValue("actor", actorId.Value);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            order = await reader.ReadAsync(cancellationToken) ? ReadOrder(reader) : null;
        }
        return order is null ? null : await LoadDetailsAsync(connection, transaction, tenantId, order, cancellationToken);
    }

    // Locks the order if the actor works in its warehouse; the status, or null when it is not theirs.
    private static async Task<string?> LockForWarehouseAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, TenantId tenantId, UserId actorId, Guid orderId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"SELECT o.status FROM orders o WHERE o.tenant_id = @tenant AND o.id = @id AND {WorksHere} FOR UPDATE OF o", connection, transaction);
        command.Parameters.AddWithValue("tenant", tenantId.Value);
        command.Parameters.AddWithValue("id", orderId);
        command.Parameters.AddWithValue("actor", actorId.Value);
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    public async Task<OrderResult> StartPreparingAsync(TenantId tenantId, UserId actorId, Guid orderId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, actorId, cancellationToken);
        var status = await LockForWarehouseAsync(connection, transaction, tenantId, actorId, orderId, cancellationToken);
        if (status is null) return OrderResult.NotFound;
        if (status != "placed") return OrderResult.Conflict;
        await SetStatusAsync(connection, transaction, tenantId, orderId, "preparing", cancellationToken);
        await AddHistoryAsync(connection, transaction, tenantId, orderId, "preparing", actorId, null, cancellationToken);
        await AuditAsync(connection, transaction, tenantId, actorId, "order.preparing", orderId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return OrderResult.Success;
    }

    public async Task<OrderResult> MarkReadyAsync(TenantId tenantId, UserId actorId, Guid orderId, IReadOnlyList<PreparedLineRequest> lines, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, actorId, cancellationToken);
        var status = await LockForWarehouseAsync(connection, transaction, tenantId, actorId, orderId, cancellationToken);
        if (status is null) return OrderResult.NotFound;
        if (status != "preparing") return OrderResult.Conflict;

        var ordered = new Dictionary<int, decimal>();
        await using (var read = new NpgsqlCommand("SELECT line_no, quantity FROM order_items WHERE tenant_id = @tenant AND order_id = @id", connection, transaction))
        {
            read.Parameters.AddWithValue("tenant", tenantId.Value);
            read.Parameters.AddWithValue("id", orderId);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) ordered[reader.GetInt32(0)] = reader.GetDecimal(1);
        }
        // Every line exactly once; never more than ordered; less needs a reason.
        if (lines.Count != ordered.Count || lines.Any(l => !ordered.TryGetValue(l.LineNo!.Value, out var max) || l.Quantity > max || (l.Quantity < max && string.IsNullOrEmpty(l.Reason))))
            return OrderResult.InvalidData;

        var shortage = false;
        foreach (var line in lines)
        {
            var less = line.Quantity < ordered[line.LineNo!.Value];
            shortage |= less;
            await using var update = new NpgsqlCommand("""
                UPDATE order_items SET prepared_quantity = @quantity, shortage_reason = @reason WHERE tenant_id = @tenant AND order_id = @id AND line_no = @line
                """, connection, transaction);
            update.Parameters.AddWithValue("quantity", line.Quantity!.Value);
            update.Parameters.Add(new NpgsqlParameter("reason", NpgsqlTypes.NpgsqlDbType.Text) { Value = less ? line.Reason : DBNull.Value });
            update.Parameters.AddWithValue("tenant", tenantId.Value);
            update.Parameters.AddWithValue("id", orderId);
            update.Parameters.AddWithValue("line", line.LineNo!.Value);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
        await SetStatusAsync(connection, transaction, tenantId, orderId, "ready", cancellationToken);
        await AddHistoryAsync(connection, transaction, tenantId, orderId, "ready", actorId, shortage ? "shortage" : null, cancellationToken);
        await AuditAsync(connection, transaction, tenantId, actorId, "order.ready", orderId, cancellationToken);
        if (shortage) await AuditAsync(connection, transaction, tenantId, actorId, "order.shortage", orderId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return OrderResult.Success;
    }

    public async Task<OrderResult> HandOverAsync(TenantId tenantId, UserId actorId, Guid orderId, Guid driverId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, actorId, cancellationToken);
        var status = await LockForWarehouseAsync(connection, transaction, tenantId, actorId, orderId, cancellationToken);
        if (status is null) return OrderResult.NotFound;
        if (status != "ready") return OrderResult.Conflict;
        if (!await ExistsAsync(connection, transaction, """
            SELECT 1 FROM user_roles ur JOIN roles r ON r.tenant_id = ur.tenant_id AND r.id = ur.role_id
            WHERE ur.tenant_id = @tenant AND ur.user_id = @id AND r.name = 'Driver'
            """, tenantId, driverId, cancellationToken)) return OrderResult.InvalidData;
        await using (var update = new NpgsqlCommand("UPDATE orders SET status = 'handed_over', driver_id = @driver WHERE tenant_id = @tenant AND id = @id", connection, transaction))
        {
            update.Parameters.AddWithValue("driver", driverId);
            update.Parameters.AddWithValue("tenant", tenantId.Value);
            update.Parameters.AddWithValue("id", orderId);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
        await AddHistoryAsync(connection, transaction, tenantId, orderId, "handed_over", actorId, null, cancellationToken);
        await AuditAsync(connection, transaction, tenantId, actorId, "order.handed_over", orderId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return OrderResult.Success;
    }

    private static async Task<bool> ExistsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, TenantId tenantId, Guid id, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("tenant", tenantId.Value);
        command.Parameters.AddWithValue("id", id);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static async Task SetStatusAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, TenantId tenantId, Guid orderId, string status, CancellationToken cancellationToken)
    {
        await using var update = new NpgsqlCommand("UPDATE orders SET status = @status WHERE tenant_id = @tenant AND id = @id", connection, transaction);
        update.Parameters.AddWithValue("status", status);
        update.Parameters.AddWithValue("tenant", tenantId.Value);
        update.Parameters.AddWithValue("id", orderId);
        await update.ExecuteNonQueryAsync(cancellationToken);
    }

    // The step of an order; a driver's step also keeps the phone's position and its distance to the customer (when both are known).
    private static async Task AddHistoryAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, TenantId tenantId, Guid orderId, string status, UserId actorId, string? note,
        CancellationToken cancellationToken, DeliveryLocation? location = null)
    {
        await using var insert = new NpgsqlCommand("""
            INSERT INTO order_status_history (tenant_id, order_id, status, actor_id, note, latitude, longitude, accuracy_m, distance_m)
            SELECT @tenant, @order, @status, @actor, @note, @lat, @lon, @accuracy,
                   CASE WHEN @lat::float8 IS NOT NULL AND c.geom IS NOT NULL
                        THEN round(ST_Distance(c.geom, ST_SetSRID(ST_MakePoint(@lon::float8, @lat::float8), 4326)::geography))::int END
            FROM orders o JOIN customers c ON c.tenant_id = o.tenant_id AND c.id = o.customer_id
            WHERE o.tenant_id = @tenant AND o.id = @order
            """, connection, transaction);
        insert.Parameters.AddWithValue("tenant", tenantId.Value);
        insert.Parameters.AddWithValue("order", orderId);
        insert.Parameters.AddWithValue("status", status);
        insert.Parameters.AddWithValue("actor", actorId.Value);
        insert.Parameters.Add(new NpgsqlParameter("note", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)note ?? DBNull.Value });
        insert.Parameters.Add(new NpgsqlParameter("lat", NpgsqlTypes.NpgsqlDbType.Double) { Value = (object?)location?.Latitude ?? DBNull.Value });
        insert.Parameters.Add(new NpgsqlParameter("lon", NpgsqlTypes.NpgsqlDbType.Double) { Value = (object?)location?.Longitude ?? DBNull.Value });
        insert.Parameters.Add(new NpgsqlParameter("accuracy", NpgsqlTypes.NpgsqlDbType.Double) { Value = (object?)location?.AccuracyMeters ?? DBNull.Value });
        await insert.ExecuteNonQueryAsync(cancellationToken);
    }

    // ---- Delivery by the driver ----

    public async Task<IReadOnlyList<OrderDto>> ListForDriverAsync(TenantId tenantId, UserId actorId, string? status, int limit, DateTimeOffset? before, Guid? beforeId,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, null, cancellationToken);
        await using var command = new NpgsqlCommand($"""
            SELECT {OrderColumns} {OrderJoins}
            WHERE o.tenant_id = @tenant AND o.driver_id = @actor
              AND ((@status::text IS NULL AND o.status IN ('handed_over', 'picked_up', 'on_the_way')) OR o.status = @status)
              AND (@before::timestamptz IS NULL OR (o.created_at, o.id) < (@before, @beforeId))
            ORDER BY o.created_at DESC, o.id DESC
            LIMIT @limit
            """, connection, transaction);
        command.Parameters.AddWithValue("tenant", tenantId.Value);
        command.Parameters.AddWithValue("actor", actorId.Value);
        command.Parameters.Add(new NpgsqlParameter("status", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)status ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("before", NpgsqlTypes.NpgsqlDbType.TimestampTz) { Value = (object?)before ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("beforeId", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)beforeId ?? DBNull.Value });
        command.Parameters.AddWithValue("limit", limit);
        var items = new List<OrderDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) items.Add(ReadOrder(reader));
        return items;
    }

    public async Task<OrderDto?> GetForDriverAsync(TenantId tenantId, UserId actorId, Guid orderId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, null, cancellationToken);
        OrderDto? order;
        await using (var command = new NpgsqlCommand($"SELECT {OrderColumns} {OrderJoins} WHERE o.tenant_id = @tenant AND o.id = @id AND o.driver_id = @actor", connection, transaction))
        {
            command.Parameters.AddWithValue("tenant", tenantId.Value);
            command.Parameters.AddWithValue("id", orderId);
            command.Parameters.AddWithValue("actor", actorId.Value);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            order = await reader.ReadAsync(cancellationToken) ? ReadOrder(reader) : null;
        }
        return order is null ? null : await LoadDetailsAsync(connection, transaction, tenantId, order, cancellationToken);
    }

    private static async Task<string?> LockForDriverAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, TenantId tenantId, UserId actorId, Guid orderId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT status FROM orders WHERE tenant_id = @tenant AND id = @id AND driver_id = @actor FOR UPDATE", connection, transaction);
        command.Parameters.AddWithValue("tenant", tenantId.Value);
        command.Parameters.AddWithValue("id", orderId);
        command.Parameters.AddWithValue("actor", actorId.Value);
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    public async Task<OrderResult> DriverStepAsync(TenantId tenantId, UserId actorId, Guid orderId, string step, DeliveryLocation? location, CancellationToken cancellationToken)
    {
        var (from, to, action) = step switch
        {
            "pickup" => ("handed_over", "picked_up", "order.picked_up"),
            "depart" => ("picked_up", "on_the_way", "order.on_the_way"),
            _ => throw new ArgumentOutOfRangeException(nameof(step))
        };
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, actorId, cancellationToken);
        var status = await LockForDriverAsync(connection, transaction, tenantId, actorId, orderId, cancellationToken);
        if (status is null) return OrderResult.NotFound;
        if (status != from) return OrderResult.Conflict;
        await SetStatusAsync(connection, transaction, tenantId, orderId, to, cancellationToken);
        await AddHistoryAsync(connection, transaction, tenantId, orderId, to, actorId, null, cancellationToken, location);
        await AuditAsync(connection, transaction, tenantId, actorId, action, orderId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return OrderResult.Success;
    }

    public async Task<OrderResult> DeliverAsync(TenantId tenantId, UserId actorId, Guid orderId, string receiverName, string? note, IReadOnlyList<DeliveredLineRequest> lines,
        DeliveryLocation? location, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, actorId, cancellationToken);
        var status = await LockForDriverAsync(connection, transaction, tenantId, actorId, orderId, cancellationToken);
        if (status is null) return OrderResult.NotFound;
        if (status != "on_the_way") return OrderResult.Conflict;

        // The baseline is what the warehouse prepared (the ordered quantity when nothing was reported).
        var prepared = new Dictionary<int, decimal>();
        await using (var read = new NpgsqlCommand("SELECT line_no, coalesce(prepared_quantity, quantity) FROM order_items WHERE tenant_id = @tenant AND order_id = @id", connection, transaction))
        {
            read.Parameters.AddWithValue("tenant", tenantId.Value);
            read.Parameters.AddWithValue("id", orderId);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) prepared[reader.GetInt32(0)] = reader.GetDecimal(1);
        }
        if (lines.Count != prepared.Count || lines.Any(l => !prepared.TryGetValue(l.LineNo!.Value, out var max) || l.Quantity > max || (l.Quantity < max && string.IsNullOrEmpty(l.Reason))))
            return OrderResult.InvalidData;

        var shortage = false;
        foreach (var line in lines)
        {
            var less = line.Quantity < prepared[line.LineNo!.Value];
            shortage |= less;
            await using var update = new NpgsqlCommand("""
                UPDATE order_items SET delivered_quantity = @quantity, delivery_reason = @reason WHERE tenant_id = @tenant AND order_id = @id AND line_no = @line
                """, connection, transaction);
            update.Parameters.AddWithValue("quantity", line.Quantity!.Value);
            update.Parameters.Add(new NpgsqlParameter("reason", NpgsqlTypes.NpgsqlDbType.Text) { Value = less ? line.Reason : DBNull.Value });
            update.Parameters.AddWithValue("tenant", tenantId.Value);
            update.Parameters.AddWithValue("id", orderId);
            update.Parameters.AddWithValue("line", line.LineNo!.Value);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var order = new NpgsqlCommand("UPDATE orders SET status = 'delivered', receiver_name = @receiver, delivery_note = @note WHERE tenant_id = @tenant AND id = @id", connection, transaction))
        {
            order.Parameters.AddWithValue("receiver", receiverName);
            order.Parameters.Add(new NpgsqlParameter("note", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)note ?? DBNull.Value });
            order.Parameters.AddWithValue("tenant", tenantId.Value);
            order.Parameters.AddWithValue("id", orderId);
            await order.ExecuteNonQueryAsync(cancellationToken);
        }
        await AddHistoryAsync(connection, transaction, tenantId, orderId, "delivered", actorId, shortage ? "shortage" : null, cancellationToken, location);
        await AuditAsync(connection, transaction, tenantId, actorId, "order.delivered", orderId, cancellationToken);
        if (shortage) await AuditAsync(connection, transaction, tenantId, actorId, "order.delivery_shortage", orderId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return OrderResult.Success;
    }

    public async Task<OrderResult> FailDeliveryAsync(TenantId tenantId, UserId actorId, Guid orderId, string reason, DeliveryLocation? location, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, actorId, cancellationToken);
        var status = await LockForDriverAsync(connection, transaction, tenantId, actorId, orderId, cancellationToken);
        if (status is null) return OrderResult.NotFound;
        if (status is not ("handed_over" or "picked_up" or "on_the_way")) return OrderResult.Conflict;
        await using (var update = new NpgsqlCommand("UPDATE orders SET status = 'delivery_failed', failure_reason = @reason WHERE tenant_id = @tenant AND id = @id", connection, transaction))
        {
            update.Parameters.AddWithValue("reason", reason);
            update.Parameters.AddWithValue("tenant", tenantId.Value);
            update.Parameters.AddWithValue("id", orderId);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
        await AddHistoryAsync(connection, transaction, tenantId, orderId, "delivery_failed", actorId, reason, cancellationToken, location);
        await AuditAsync(connection, transaction, tenantId, actorId, "order.delivery_failed", orderId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return OrderResult.Success;
    }

    public async Task<bool> HasPlacedOrderForVisitAsync(TenantId tenantId, Guid visitId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, null, cancellationToken);
        await using var command = new NpgsqlCommand("SELECT 1 FROM orders WHERE tenant_id = @tenant AND visit_id = @visit AND status = 'placed' LIMIT 1", connection, transaction);
        command.Parameters.AddWithValue("tenant", tenantId.Value);
        command.Parameters.AddWithValue("visit", visitId);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
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
