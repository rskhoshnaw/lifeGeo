using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LifeDrop.Application.Samples;
using LifeDrop.Domain.Common;
using Npgsql;
using NpgsqlTypes;

namespace LifeDrop.Infrastructure.Samples;

public sealed class PostgresSampleStore(NpgsqlDataSource dataSource) : ISampleStore
{
    public static string FormatNumber(long number) => $"S-{number:D6}";

    private static NpgsqlParameter P(string name, NpgsqlDbType type, object? value) => new(name, type) { Value = value ?? DBNull.Value };

    public async Task<(SampleResult Result, CreatedSampleDto? Sample)> CreateAsync(TenantId tenantId, UserId actorId, NewSample sample, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, actorId, cancellationToken);

        await using (var again = new NpgsqlCommand("SELECT id, number, distributed_by, customer_id FROM sample_distributions WHERE tenant_id = @tenant AND client_ref = @ref", connection, transaction))
        {
            again.Parameters.AddWithValue("tenant", tenantId.Value);
            again.Parameters.AddWithValue("ref", sample.ClientRef);
            await using var reader = await again.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                return reader.GetGuid(2) == actorId.Value && reader.GetGuid(3) == sample.CustomerId
                    ? (SampleResult.Success, new CreatedSampleDto(reader.GetGuid(0), FormatNumber(reader.GetInt64(1)), true))
                    : (SampleResult.Conflict, null);
            }
        }

        string? productStatus;
        await using (var product = new NpgsqlCommand("SELECT status FROM products WHERE tenant_id = @tenant AND id = @id", connection, transaction))
        {
            product.Parameters.AddWithValue("tenant", tenantId.Value);
            product.Parameters.AddWithValue("id", sample.ProductId);
            productStatus = await product.ExecuteScalarAsync(cancellationToken) as string;
        }
        if (productStatus is null) return (SampleResult.NotFound, null);
        if (productStatus != "active") return (SampleResult.InvalidData, null);

        long number;
        await using (var counter = new NpgsqlCommand("""
            INSERT INTO sample_counters (tenant_id, last_number) VALUES (@tenant, 1)
            ON CONFLICT (tenant_id) DO UPDATE SET last_number = sample_counters.last_number + 1
            RETURNING last_number
            """, connection, transaction))
        {
            counter.Parameters.AddWithValue("tenant", tenantId.Value);
            number = Convert.ToInt64(await counter.ExecuteScalarAsync(cancellationToken));
        }
        var id = Guid.NewGuid();
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO sample_distributions (id, tenant_id, number, customer_id, visit_id, product_id, quantity, recipient_name, batch_number, expiry_date, note, distributed_by,
                latitude, longitude, accuracy_m, client_ref)
            VALUES (@id, @tenant, @number, @customer, @visit, @product, @quantity, @recipient, @batch, @expiry, @note, @actor, @lat, @lng, @acc, @ref)
            """, connection, transaction))
        {
            insert.Parameters.AddWithValue("id", id);
            insert.Parameters.AddWithValue("tenant", tenantId.Value);
            insert.Parameters.AddWithValue("number", number);
            insert.Parameters.AddWithValue("customer", sample.CustomerId);
            insert.Parameters.Add(P("visit", NpgsqlDbType.Uuid, sample.VisitId));
            insert.Parameters.AddWithValue("product", sample.ProductId);
            insert.Parameters.AddWithValue("quantity", sample.Quantity);
            insert.Parameters.Add(P("recipient", NpgsqlDbType.Text, sample.RecipientName));
            insert.Parameters.Add(P("batch", NpgsqlDbType.Text, sample.BatchNumber));
            insert.Parameters.Add(P("expiry", NpgsqlDbType.Date, sample.ExpiryDate));
            insert.Parameters.Add(P("note", NpgsqlDbType.Text, sample.Note));
            insert.Parameters.AddWithValue("actor", actorId.Value);
            insert.Parameters.Add(P("lat", NpgsqlDbType.Double, sample.Location?.Latitude));
            insert.Parameters.Add(P("lng", NpgsqlDbType.Double, sample.Location?.Longitude));
            insert.Parameters.Add(P("acc", NpgsqlDbType.Double, sample.Location?.AccuracyMeters));
            insert.Parameters.AddWithValue("ref", sample.ClientRef);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        await AuditAsync(connection, transaction, tenantId, actorId, "sample.recorded", id, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (SampleResult.Success, new CreatedSampleDto(id, FormatNumber(number), false));
    }

    private const string Columns = """
        s.id, s.number, s.customer_id, c.code, c.name, s.visit_id, s.product_id, pr.code, pr.name, pr.unit, s.quantity, s.recipient_name, s.batch_number, s.expiry_date, s.note,
        s.distributed_by, u.email, s.latitude, s.longitude, s.status, s.void_reason, s.created_at
        """;

    private const string Joins = """
        FROM sample_distributions s
        JOIN customers c ON c.tenant_id = s.tenant_id AND c.id = s.customer_id
        JOIN products pr ON pr.tenant_id = s.tenant_id AND pr.id = s.product_id
        LEFT JOIN users u ON u.tenant_id = s.tenant_id AND u.id = s.distributed_by
        """;

    private static string ViewFilter(SampleView view) => view switch
    {
        SampleView.Everything => "TRUE",
        SampleView.Region => "(s.distributed_by = @actor OR c.owner_user_id = @actor OR c.region_id = @region)",
        _ => "s.distributed_by = @actor"
    };

    private static SampleDto Read(NpgsqlDataReader r) => new(r.GetGuid(0), FormatNumber(r.GetInt64(1)), r.GetGuid(2), r.GetString(3), r.GetString(4), r.IsDBNull(5) ? null : r.GetGuid(5),
        r.GetGuid(6), r.GetString(7), r.GetString(8), r.GetString(9), r.GetDecimal(10), r.IsDBNull(11) ? null : r.GetString(11), r.IsDBNull(12) ? null : r.GetString(12),
        r.IsDBNull(13) ? null : DateOnly.FromDateTime(r.GetDateTime(13)), r.IsDBNull(14) ? null : r.GetString(14), r.GetGuid(15), r.IsDBNull(16) ? null : r.GetString(16),
        r.IsDBNull(17) ? null : r.GetDouble(17), r.IsDBNull(18) ? null : r.GetDouble(18), r.GetString(19), r.IsDBNull(20) ? null : r.GetString(20), r.GetFieldValue<DateTimeOffset>(21));

    private static void AddFilters(NpgsqlCommand command, TenantId tenantId, UserId actorId, Guid? regionId)
    {
        command.Parameters.AddWithValue("tenant", tenantId.Value);
        command.Parameters.AddWithValue("actor", actorId.Value);
        command.Parameters.Add(P("region", NpgsqlDbType.Uuid, regionId));
    }

    public async Task<IReadOnlyList<SampleDto>> ListAsync(TenantId tenantId, UserId actorId, SampleView view, Guid? regionId, Guid? customerId, Guid? productId, int limit,
        DateTimeOffset? before, Guid? beforeId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, null, cancellationToken);
        await using var command = new NpgsqlCommand($"""
            SELECT {Columns} {Joins}
            WHERE s.tenant_id = @tenant AND {ViewFilter(view)}
              AND (@customer IS NULL OR s.customer_id = @customer) AND (@product IS NULL OR s.product_id = @product)
              AND (@before IS NULL OR (s.created_at, s.id) < (@before, @beforeId))
            ORDER BY s.created_at DESC, s.id DESC LIMIT @limit
            """, connection, transaction);
        AddFilters(command, tenantId, actorId, regionId);
        command.Parameters.Add(P("customer", NpgsqlDbType.Uuid, customerId));
        command.Parameters.Add(P("product", NpgsqlDbType.Uuid, productId));
        command.Parameters.Add(P("before", NpgsqlDbType.TimestampTz, before));
        command.Parameters.Add(P("beforeId", NpgsqlDbType.Uuid, beforeId));
        command.Parameters.AddWithValue("limit", limit);
        var items = new List<SampleDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) items.Add(Read(reader));
        return items;
    }

    public async Task<SampleDto?> GetAsync(TenantId tenantId, UserId actorId, SampleView view, Guid? regionId, Guid sampleId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, null, cancellationToken);
        await using var command = new NpgsqlCommand($"SELECT {Columns} {Joins} WHERE s.tenant_id = @tenant AND s.id = @id AND {ViewFilter(view)}", connection, transaction);
        AddFilters(command, tenantId, actorId, regionId);
        command.Parameters.AddWithValue("id", sampleId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    public async Task<SampleResult> VoidAsync(TenantId tenantId, UserId actorId, Guid sampleId, string reason, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, actorId, cancellationToken);
        string? status;
        await using (var lockRow = new NpgsqlCommand("SELECT status FROM sample_distributions WHERE tenant_id = @tenant AND id = @id FOR UPDATE", connection, transaction))
        {
            lockRow.Parameters.AddWithValue("tenant", tenantId.Value);
            lockRow.Parameters.AddWithValue("id", sampleId);
            status = await lockRow.ExecuteScalarAsync(cancellationToken) as string;
        }
        if (status is null) return SampleResult.NotFound;
        if (status != "given") return SampleResult.Conflict;
        await using (var update = new NpgsqlCommand("""
            UPDATE sample_distributions SET status = 'voided', void_reason = @reason, voided_by = @actor, voided_at = now() WHERE tenant_id = @tenant AND id = @id
            """, connection, transaction))
        {
            update.Parameters.AddWithValue("tenant", tenantId.Value);
            update.Parameters.AddWithValue("actor", actorId.Value);
            update.Parameters.AddWithValue("id", sampleId);
            update.Parameters.AddWithValue("reason", reason);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
        await AuditAsync(connection, transaction, tenantId, actorId, "sample.voided", sampleId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return SampleResult.Success;
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
