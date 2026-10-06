using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LifeDrop.Application.Payments;
using LifeDrop.Domain.Common;
using Npgsql;
using NpgsqlTypes;

namespace LifeDrop.Infrastructure.Payments;

public sealed class PostgresPaymentStore(NpgsqlDataSource dataSource) : IPaymentStore
{
    public static string FormatNumber(long number) => $"P-{number:D6}";

    public async Task<(PaymentResult Result, CreatedPaymentDto? Payment)> CreateAsync(TenantId tenantId, UserId actorId, NewPayment payment, string status, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, actorId, cancellationToken);

        // A retry of the same send gives back the payment that exists.
        await using (var again = new NpgsqlCommand("SELECT id, number, status, recorded_by, customer_id FROM payments WHERE tenant_id = @tenant AND client_ref = @ref", connection, transaction))
        {
            again.Parameters.AddWithValue("tenant", tenantId.Value);
            again.Parameters.AddWithValue("ref", payment.ClientRef);
            await using var reader = await again.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                return reader.GetGuid(3) == actorId.Value && reader.GetGuid(4) == payment.CustomerId
                    ? (PaymentResult.Success, new CreatedPaymentDto(reader.GetGuid(0), FormatNumber(reader.GetInt64(1)), reader.GetString(2), true))
                    : (PaymentResult.Conflict, null);
            }
        }

        var check = await CheckOrderAsync(connection, transaction, tenantId, payment, cancellationToken);
        if (check != PaymentResult.Success) return (check, null);

        var created = await InsertAsync(connection, transaction, tenantId, actorId, payment, status, null, cancellationToken);
        await AuditAsync(connection, transaction, tenantId, actorId, status == "confirmed" ? "payment.recorded_confirmed" : "payment.recorded", created.Id, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (PaymentResult.Success, created);
    }

    private static async Task<PaymentResult> CheckOrderAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, TenantId tenantId, NewPayment payment, CancellationToken cancellationToken)
    {
        if (payment.OrderId is null) return PaymentResult.Success;
        await using var command = new NpgsqlCommand("SELECT customer_id, status, currency FROM orders WHERE tenant_id = @tenant AND id = @id", connection, transaction);
        command.Parameters.AddWithValue("tenant", tenantId.Value);
        command.Parameters.AddWithValue("id", payment.OrderId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return PaymentResult.NotFound;
        return reader.GetGuid(0) != payment.CustomerId || reader.GetString(1) == "cancelled" || reader.GetString(2) != payment.Currency ? PaymentResult.InvalidData : PaymentResult.Success;
    }

    private static async Task<CreatedPaymentDto> InsertAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, TenantId tenantId, UserId actorId, NewPayment payment, string status,
        Guid? replaces, CancellationToken cancellationToken)
    {
        long number;
        await using (var counter = new NpgsqlCommand("""
            INSERT INTO payment_counters (tenant_id, last_number) VALUES (@tenant, 1)
            ON CONFLICT (tenant_id) DO UPDATE SET last_number = payment_counters.last_number + 1
            RETURNING last_number
            """, connection, transaction))
        {
            counter.Parameters.AddWithValue("tenant", tenantId.Value);
            number = Convert.ToInt64(await counter.ExecuteScalarAsync(cancellationToken));
        }
        var id = Guid.NewGuid();
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO payments (id, tenant_id, number, customer_id, order_id, method, currency, amount, reference, note, status, received_by, recorded_by,
                confirmed_by, confirmed_at, replaces_payment_id, client_ref)
            VALUES (@id, @tenant, @number, @customer, @order, @method, @currency, @amount, @reference, @note, @status, @actor, @actor,
                CASE WHEN @status = 'confirmed' THEN @actor END, CASE WHEN @status = 'confirmed' THEN now() END, @replaces, @ref)
            """, connection, transaction))
        {
            insert.Parameters.AddWithValue("id", id);
            insert.Parameters.AddWithValue("tenant", tenantId.Value);
            insert.Parameters.AddWithValue("number", number);
            insert.Parameters.AddWithValue("customer", payment.CustomerId);
            insert.Parameters.Add(new NpgsqlParameter("order", NpgsqlDbType.Uuid) { Value = (object?)payment.OrderId ?? DBNull.Value });
            insert.Parameters.AddWithValue("method", payment.Method);
            insert.Parameters.AddWithValue("currency", payment.Currency);
            insert.Parameters.AddWithValue("amount", payment.Amount);
            insert.Parameters.Add(new NpgsqlParameter("reference", NpgsqlDbType.Text) { Value = (object?)payment.Reference ?? DBNull.Value });
            insert.Parameters.Add(new NpgsqlParameter("note", NpgsqlDbType.Text) { Value = (object?)payment.Note ?? DBNull.Value });
            insert.Parameters.Add(new NpgsqlParameter("status", NpgsqlDbType.Text) { Value = status });
            insert.Parameters.AddWithValue("actor", actorId.Value);
            insert.Parameters.Add(new NpgsqlParameter("replaces", NpgsqlDbType.Uuid) { Value = (object?)replaces ?? DBNull.Value });
            insert.Parameters.AddWithValue("ref", payment.ClientRef);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        await HistoryAsync(connection, transaction, tenantId, actorId, id, status, replaces is null ? null : "correction", cancellationToken);
        return new CreatedPaymentDto(id, FormatNumber(number), status, false);
    }

    private static async Task HistoryAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, TenantId tenantId, UserId actorId, Guid paymentId, string status, string? note,
        CancellationToken cancellationToken)
    {
        await using var history = new NpgsqlCommand("""
            INSERT INTO payment_history (tenant_id, payment_id, status, actor_id, note) VALUES (@tenant, @payment, @status, @actor, @note)
            """, connection, transaction);
        history.Parameters.AddWithValue("tenant", tenantId.Value);
        history.Parameters.AddWithValue("payment", paymentId);
        history.Parameters.AddWithValue("status", status);
        history.Parameters.AddWithValue("actor", actorId.Value);
        history.Parameters.Add(new NpgsqlParameter("note", NpgsqlDbType.Text) { Value = (object?)note ?? DBNull.Value });
        await history.ExecuteNonQueryAsync(cancellationToken);
    }

    private const string Columns = """
        p.id, p.number, p.customer_id, c.code, c.name, p.order_id, o.number, p.method, p.currency, p.amount, p.reference, p.note, p.status, p.received_by, ru.email,
        p.recorded_by, cu.email, p.confirmed_at, p.closed_reason, p.replaces_payment_id, nx.id, p.created_at
        """;

    private const string Joins = """
        FROM payments p
        JOIN customers c ON c.tenant_id = p.tenant_id AND c.id = p.customer_id
        LEFT JOIN orders o ON o.tenant_id = p.tenant_id AND o.id = p.order_id
        LEFT JOIN users ru ON ru.tenant_id = p.tenant_id AND ru.id = p.received_by
        LEFT JOIN users cu ON cu.tenant_id = p.tenant_id AND cu.id = p.confirmed_by
        LEFT JOIN payments nx ON nx.tenant_id = p.tenant_id AND nx.replaces_payment_id = p.id
        """;

    private static string ViewFilter(PaymentView view) => view switch
    {
        PaymentView.Everything => "TRUE",
        PaymentView.Region => "(p.recorded_by = @actor OR c.owner_user_id = @actor OR c.region_id = @region)",
        _ => "p.recorded_by = @actor"
    };

    private static PaymentDto Read(NpgsqlDataReader r) => new(r.GetGuid(0), FormatNumber(r.GetInt64(1)), r.GetGuid(2), r.GetString(3), r.GetString(4), r.IsDBNull(5) ? null : r.GetGuid(5),
        r.IsDBNull(6) ? null : "O-" + r.GetInt64(6).ToString("D6"), r.GetString(7), r.GetString(8), r.GetDecimal(9), r.IsDBNull(10) ? null : r.GetString(10), r.IsDBNull(11) ? null : r.GetString(11),
        r.GetString(12), r.GetGuid(13), r.IsDBNull(14) ? null : r.GetString(14), r.GetGuid(15), r.IsDBNull(16) ? null : r.GetString(16),
        r.IsDBNull(17) ? null : r.GetFieldValue<DateTimeOffset>(17), r.IsDBNull(18) ? null : r.GetString(18), r.IsDBNull(19) ? null : r.GetGuid(19), r.IsDBNull(20) ? null : r.GetGuid(20),
        r.GetFieldValue<DateTimeOffset>(21));

    public async Task<IReadOnlyList<PaymentDto>> ListAsync(TenantId tenantId, UserId actorId, PaymentView view, Guid? regionId, Guid? customerId, string? status, int limit,
        DateTimeOffset? before, Guid? beforeId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, null, cancellationToken);
        await using var command = new NpgsqlCommand($"""
            SELECT {Columns} {Joins}
            WHERE p.tenant_id = @tenant AND {ViewFilter(view)}
              AND (@customer IS NULL OR p.customer_id = @customer) AND (@status IS NULL OR p.status = @status)
              AND (@before IS NULL OR (p.created_at, p.id) < (@before, @beforeId))
            ORDER BY p.created_at DESC, p.id DESC LIMIT @limit
            """, connection, transaction);
        AddFilters(command, tenantId, actorId, regionId);
        command.Parameters.Add(new NpgsqlParameter("customer", NpgsqlDbType.Uuid) { Value = (object?)customerId ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("status", NpgsqlDbType.Text) { Value = (object?)status ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("before", NpgsqlDbType.TimestampTz) { Value = (object?)before ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("beforeId", NpgsqlDbType.Uuid) { Value = (object?)beforeId ?? DBNull.Value });
        command.Parameters.AddWithValue("limit", limit);
        var items = new List<PaymentDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) items.Add(Read(reader));
        return items;
    }

    private static void AddFilters(NpgsqlCommand command, TenantId tenantId, UserId actorId, Guid? regionId)
    {
        command.Parameters.AddWithValue("tenant", tenantId.Value);
        command.Parameters.AddWithValue("actor", actorId.Value);
        command.Parameters.Add(new NpgsqlParameter("region", NpgsqlDbType.Uuid) { Value = (object?)regionId ?? DBNull.Value });
    }

    public async Task<PaymentDto?> GetAsync(TenantId tenantId, UserId actorId, PaymentView view, Guid? regionId, Guid paymentId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, null, cancellationToken);
        PaymentDto? payment;
        await using (var command = new NpgsqlCommand($"SELECT {Columns} {Joins} WHERE p.tenant_id = @tenant AND p.id = @id AND {ViewFilter(view)}", connection, transaction))
        {
            AddFilters(command, tenantId, actorId, regionId);
            command.Parameters.AddWithValue("id", paymentId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            payment = await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
        }
        if (payment is null) return null;
        var history = new List<PaymentHistoryDto>();
        await using (var command = new NpgsqlCommand("""
            SELECT h.status, u.email, h.note, h.occurred_at FROM payment_history h LEFT JOIN users u ON u.tenant_id = h.tenant_id AND u.id = h.actor_id
            WHERE h.tenant_id = @tenant AND h.payment_id = @id ORDER BY h.occurred_at, h.id
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("tenant", tenantId.Value);
            command.Parameters.AddWithValue("id", paymentId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                history.Add(new PaymentHistoryDto(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetFieldValue<DateTimeOffset>(3)));
        }
        return payment with { History = history };
    }

    public async Task<PaymentResult> ConfirmAsync(TenantId tenantId, UserId actorId, Guid paymentId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, actorId, cancellationToken);
        var status = await LockStatusAsync(connection, transaction, tenantId, paymentId, cancellationToken);
        if (status is null) return PaymentResult.NotFound;
        if (status != "collected") return PaymentResult.Conflict;
        await using (var update = new NpgsqlCommand("UPDATE payments SET status = 'confirmed', confirmed_by = @actor, confirmed_at = now() WHERE tenant_id = @tenant AND id = @id", connection, transaction))
        {
            update.Parameters.AddWithValue("tenant", tenantId.Value);
            update.Parameters.AddWithValue("actor", actorId.Value);
            update.Parameters.AddWithValue("id", paymentId);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
        await HistoryAsync(connection, transaction, tenantId, actorId, paymentId, "confirmed", null, cancellationToken);
        await AuditAsync(connection, transaction, tenantId, actorId, "payment.confirmed", paymentId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return PaymentResult.Success;
    }

    public async Task<PaymentResult> VoidAsync(TenantId tenantId, UserId actorId, Guid paymentId, string reason, bool isManager, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, actorId, cancellationToken);
        var status = await LockStatusAsync(connection, transaction, tenantId, paymentId, cancellationToken);
        if (status is null) return PaymentResult.NotFound;
        if (status is not ("collected" or "confirmed")) return PaymentResult.Conflict;
        if (!isManager)
        {
            Guid recorder;
            await using (var who = new NpgsqlCommand("SELECT recorded_by FROM payments WHERE tenant_id = @tenant AND id = @id", connection, transaction))
            {
                who.Parameters.AddWithValue("tenant", tenantId.Value);
                who.Parameters.AddWithValue("id", paymentId);
                recorder = (Guid)(await who.ExecuteScalarAsync(cancellationToken))!;
            }
            // A visitor may take back only their own payment that the finance manager has not confirmed yet.
            if (recorder != actorId.Value) return PaymentResult.NotFound;
            if (status != "collected") return PaymentResult.Forbidden;
        }
        await CloseAsync(connection, transaction, tenantId, actorId, paymentId, "voided", reason, cancellationToken);
        await AuditAsync(connection, transaction, tenantId, actorId, "payment.voided", paymentId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return PaymentResult.Success;
    }

    public async Task<(PaymentResult Result, CreatedPaymentDto? Payment)> CorrectAsync(TenantId tenantId, UserId actorId, Guid paymentId, NewPayment replacement, string reason,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, actorId, cancellationToken);
        Guid customer;
        string? status;
        await using (var lockRow = new NpgsqlCommand("SELECT status, customer_id FROM payments WHERE tenant_id = @tenant AND id = @id FOR UPDATE", connection, transaction))
        {
            lockRow.Parameters.AddWithValue("tenant", tenantId.Value);
            lockRow.Parameters.AddWithValue("id", paymentId);
            await using var reader = await lockRow.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return (PaymentResult.NotFound, null);
            status = reader.GetString(0);
            customer = reader.GetGuid(1);
        }
        if (status is not ("collected" or "confirmed")) return (PaymentResult.Conflict, null);
        var payment = replacement with { CustomerId = customer };
        var check = await CheckOrderAsync(connection, transaction, tenantId, payment, cancellationToken);
        if (check != PaymentResult.Success) return (check, null);
        await CloseAsync(connection, transaction, tenantId, actorId, paymentId, "superseded", reason, cancellationToken);
        var created = await InsertAsync(connection, transaction, tenantId, actorId, payment, "confirmed", paymentId, cancellationToken);
        await AuditAsync(connection, transaction, tenantId, actorId, "payment.corrected", paymentId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (PaymentResult.Success, created);
    }

    public async Task<IReadOnlyList<BalanceItemDto>> BalanceAsync(TenantId tenantId, Guid customerId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, null, cancellationToken);
        await using var command = new NpgsqlCommand("""
            WITH delivered AS (
                SELECT o.currency, sum(i.delivered_quantity * i.unit_price) AS value
                FROM orders o JOIN order_items i ON i.tenant_id = o.tenant_id AND i.order_id = o.id
                WHERE o.tenant_id = @tenant AND o.customer_id = @customer AND o.status = 'delivered'
                GROUP BY o.currency),
            paid AS (
                SELECT currency,
                       sum(amount) FILTER (WHERE status = 'confirmed') AS confirmed,
                       sum(amount) FILTER (WHERE status = 'collected') AS collected
                FROM payments WHERE tenant_id = @tenant AND customer_id = @customer GROUP BY currency)
            SELECT coalesce(d.currency, p.currency), coalesce(d.value, 0), coalesce(p.confirmed, 0), coalesce(p.collected, 0)
            FROM delivered d FULL OUTER JOIN paid p ON p.currency = d.currency
            ORDER BY 1
            """, connection, transaction);
        command.Parameters.AddWithValue("tenant", tenantId.Value);
        command.Parameters.AddWithValue("customer", customerId);
        var items = new List<BalanceItemDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var delivered = reader.GetDecimal(1);
            var confirmed = reader.GetDecimal(2);
            items.Add(new BalanceItemDto(reader.GetString(0), delivered, confirmed, reader.GetDecimal(3), delivered - confirmed));
        }
        return items;
    }

    private static async Task<string?> LockStatusAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, TenantId tenantId, Guid paymentId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT status FROM payments WHERE tenant_id = @tenant AND id = @id FOR UPDATE", connection, transaction);
        command.Parameters.AddWithValue("tenant", tenantId.Value);
        command.Parameters.AddWithValue("id", paymentId);
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    private static async Task CloseAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, TenantId tenantId, UserId actorId, Guid paymentId, string status, string reason,
        CancellationToken cancellationToken)
    {
        await using (var update = new NpgsqlCommand("""
            UPDATE payments SET status = @status, closed_reason = @reason, closed_by = @actor, closed_at = now() WHERE tenant_id = @tenant AND id = @id
            """, connection, transaction))
        {
            update.Parameters.AddWithValue("tenant", tenantId.Value);
            update.Parameters.AddWithValue("actor", actorId.Value);
            update.Parameters.AddWithValue("id", paymentId);
            update.Parameters.AddWithValue("status", status);
            update.Parameters.AddWithValue("reason", reason);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
        await HistoryAsync(connection, transaction, tenantId, actorId, paymentId, status, reason, cancellationToken);
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
