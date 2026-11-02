using LifeDrop.Application.ClientErrors;
using LifeDrop.Domain.Common;
using Npgsql;

namespace LifeDrop.Infrastructure.ClientErrors;

// Connection-error reports of the mobile app (plan step 5.10). Row-level security keeps every company to its own rows.
public sealed class PostgresClientErrorStore(NpgsqlDataSource dataSource) : IClientErrorStore
{
    private static async Task<NpgsqlTransaction> BeginAsync(NpgsqlConnection connection, TenantId tenantId, CancellationToken cancellationToken)
    {
        var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var set = new NpgsqlCommand("SELECT set_config('app.current_tenant', @tenant, true)", connection, transaction);
        set.Parameters.AddWithValue("tenant", tenantId.Value.ToString());
        await set.ExecuteNonQueryAsync(cancellationToken);
        return transaction;
    }

    public async Task<TenantId?> FindTenantAsync(string companyCode, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("SELECT id FROM tenants WHERE lower(code) = @code AND is_active");
        command.Parameters.AddWithValue("code", companyCode);
        return await command.ExecuteScalarAsync(cancellationToken) is Guid id ? new TenantId(id) : null;
    }

    public async Task<int> AddAsync(TenantId tenantId, IReadOnlyList<ClientErrorRecord> records, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, cancellationToken);
        foreach (var r in records)
        {
            await using var insert = new NpgsqlCommand("""
                INSERT INTO client_error_reports (tenant_id, occurred_at, stage, error_kind, error_type, message, entered_company_code, entered_email, device_id, app_version, platform)
                VALUES (@tenant, @occurred, @stage, @kind, @type, @message, @code, @email, @device, @version, @platform) RETURNING id
                """, connection, transaction);
            insert.Parameters.AddWithValue("tenant", tenantId.Value);
            insert.Parameters.AddWithValue("occurred", r.OccurredAt);
            insert.Parameters.AddWithValue("stage", r.Stage);
            insert.Parameters.AddWithValue("kind", r.ErrorKind);
            insert.Parameters.AddWithValue("type", r.ErrorType);
            insert.Parameters.AddWithValue("message", r.Message);
            insert.Parameters.AddWithValue("code", (object?)r.EnteredCompanyCode ?? DBNull.Value);
            insert.Parameters.AddWithValue("email", (object?)r.EnteredEmail ?? DBNull.Value);
            insert.Parameters.AddWithValue("device", (object?)r.DeviceId ?? DBNull.Value);
            insert.Parameters.AddWithValue("version", (object?)r.AppVersion ?? DBNull.Value);
            insert.Parameters.AddWithValue("platform", (object?)r.Platform ?? DBNull.Value);
            var id = (Guid)(await insert.ExecuteScalarAsync(cancellationToken))!;

            // Audit needs a real user as actor: the user whose e-mail was typed, when the company has one.
            await using var audit = new NpgsqlCommand("""
                INSERT INTO audit_logs (id, tenant_id, actor_id, action, entity_id, occurred_at)
                SELECT gen_random_uuid(), @tenant, u.id, 'client_error.reported', @entity, now()
                FROM users u WHERE u.tenant_id = @tenant AND lower(u.email) = lower(@email)
                """, connection, transaction);
            audit.Parameters.AddWithValue("tenant", tenantId.Value);
            audit.Parameters.AddWithValue("entity", id.ToString());
            audit.Parameters.AddWithValue("email", r.EnteredEmail ?? "");
            await audit.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return records.Count;
    }

    public async Task<IReadOnlyList<ClientErrorDto>> ListAsync(TenantId tenantId, ClientErrorQuery query, int limit, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT id, reported_at, occurred_at, stage, error_kind, error_type, message, entered_company_code, entered_email, device_id, app_version, platform
            FROM client_error_reports
            WHERE tenant_id = @tenant
              AND (@stage::text IS NULL OR stage = @stage)
              AND (@from::timestamptz IS NULL OR reported_at >= @from)
              AND (@to::timestamptz IS NULL OR reported_at < @to)
              AND (@cursorAt::timestamptz IS NULL OR (reported_at, id) < (@cursorAt, @cursorId))
            ORDER BY reported_at DESC, id DESC
            LIMIT @limit
            """, connection, transaction);
        command.Parameters.AddWithValue("tenant", tenantId.Value);
        command.Parameters.Add(new NpgsqlParameter("stage", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)query.Stage ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("from", NpgsqlTypes.NpgsqlDbType.TimestampTz) { Value = (object?)query.From ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("to", NpgsqlTypes.NpgsqlDbType.TimestampTz) { Value = (object?)query.To ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("cursorAt", NpgsqlTypes.NpgsqlDbType.TimestampTz) { Value = (object?)query.CursorReportedAt ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("cursorId", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)query.CursorId ?? DBNull.Value });
        command.Parameters.AddWithValue("limit", limit);
        var items = new List<ClientErrorDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            items.Add(new ClientErrorDto(reader.GetGuid(0), reader.GetFieldValue<DateTimeOffset>(1), reader.GetFieldValue<DateTimeOffset>(2), reader.GetString(3), reader.GetString(4), reader.GetString(5),
                reader.GetString(6), Str(reader, 7), Str(reader, 8), Str(reader, 9), Str(reader, 10), Str(reader, 11)));
        return items;
    }

    private static string? Str(NpgsqlDataReader reader, int i) => reader.IsDBNull(i) ? null : reader.GetString(i);

    public async Task<int> PurgeAsync(TenantId tenantId, DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, cancellationToken);
        await using var command = new NpgsqlCommand("DELETE FROM client_error_reports WHERE tenant_id = @tenant AND reported_at < @cutoff", connection, transaction);
        command.Parameters.AddWithValue("tenant", tenantId.Value);
        command.Parameters.AddWithValue("cutoff", cutoff);
        var deleted = await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return deleted;
    }
}
