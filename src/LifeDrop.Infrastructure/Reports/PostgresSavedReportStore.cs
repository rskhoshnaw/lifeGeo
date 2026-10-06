using System.Text.Json;
using LifeDrop.Application.Reports;
using LifeDrop.Domain.Common;
using Npgsql;
using NpgsqlTypes;

namespace LifeDrop.Infrastructure.Reports;

// Saved reports and their runs with the tenant context set for RLS (plan step 3.2). The owner filter is explicit in every query.
public sealed class PostgresSavedReportStore(NpgsqlDataSource dataSource) : ISavedReportStore
{
    private const string Columns = "id, name, range_kind, from_date, to_date, region_id, team_id, customer_type, frequency, weekday, day_of_month, run_hour, is_active, next_run_at, last_run_at, created_at";

    public async Task<IReadOnlyList<SavedReportDto>> ListAsync(TenantId tenantId, UserId owner, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, cancellationToken);
        await using var command = new NpgsqlCommand($"SELECT {Columns} FROM saved_reports WHERE owner_user_id = @owner ORDER BY name COLLATE \"C\", id", connection, transaction);
        command.Parameters.AddWithValue("owner", owner.Value);
        var items = new List<SavedReportDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) items.Add(ReadReport(reader));
        return items;
    }

    public async Task<SavedReportDto?> GetAsync(TenantId tenantId, UserId owner, Guid id, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, cancellationToken);
        await using var command = new NpgsqlCommand($"SELECT {Columns} FROM saved_reports WHERE id = @id AND owner_user_id = @owner", connection, transaction);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("owner", owner.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadReport(reader) : null;
    }

    public async Task<int> CountAsync(TenantId tenantId, UserId owner, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, cancellationToken);
        await using var command = new NpgsqlCommand("SELECT count(*)::int FROM saved_reports WHERE owner_user_id = @owner", connection, transaction);
        command.Parameters.AddWithValue("owner", owner.Value);
        return (int)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    public async Task<Guid?> CreateAsync(TenantId tenantId, UserId owner, SavedReportData data, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, cancellationToken);
        try
        {
            await using var command = new NpgsqlCommand("""
                INSERT INTO saved_reports (tenant_id, owner_user_id, name, range_kind, from_date, to_date, region_id, team_id, customer_type, frequency, weekday, day_of_month, run_hour, is_active, next_run_at)
                VALUES (@tenant, @owner, @name, @range, @from, @to, @region, @team, @type, @freq, @weekday, @dom, @hour, @active, @next)
                RETURNING id
                """, connection, transaction);
            command.Parameters.AddWithValue("tenant", tenantId.Value);
            command.Parameters.AddWithValue("owner", owner.Value);
            Bind(command, data);
            var id = (Guid)(await command.ExecuteScalarAsync(cancellationToken))!;
            await transaction.CommitAsync(cancellationToken);
            return id;
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return null;
        }
    }

    public async Task<(bool Found, bool NameTaken)> UpdateAsync(TenantId tenantId, UserId owner, Guid id, SavedReportData data, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, cancellationToken);
        try
        {
            await using var command = new NpgsqlCommand("""
                UPDATE saved_reports SET name = @name, range_kind = @range, from_date = @from, to_date = @to, region_id = @region, team_id = @team, customer_type = @type,
                    frequency = @freq, weekday = @weekday, day_of_month = @dom, run_hour = @hour, is_active = @active, next_run_at = @next, updated_at = now()
                WHERE id = @id AND owner_user_id = @owner
                """, connection, transaction);
            command.Parameters.AddWithValue("id", id);
            command.Parameters.AddWithValue("owner", owner.Value);
            Bind(command, data);
            var found = await command.ExecuteNonQueryAsync(cancellationToken) > 0;
            await transaction.CommitAsync(cancellationToken);
            return (found, false);
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return (true, true);
        }
    }

    public async Task<bool> DeleteAsync(TenantId tenantId, UserId owner, Guid id, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, cancellationToken);
        await using var command = new NpgsqlCommand("DELETE FROM saved_reports WHERE id = @id AND owner_user_id = @owner", connection, transaction);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("owner", owner.Value);
        var found = await command.ExecuteNonQueryAsync(cancellationToken) > 0;
        await transaction.CommitAsync(cancellationToken);
        return found;
    }

    public async Task<IReadOnlyList<ReportRunDto>> ListRunsAsync(TenantId tenantId, UserId owner, Guid reportId, int limit, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT r.id, r.report_id, r.trigger, r.status, r.error_code, r.from_date, r.to_date, r.ran_at
            FROM report_runs r JOIN saved_reports s ON s.tenant_id = r.tenant_id AND s.id = r.report_id
            WHERE r.report_id = @report AND s.owner_user_id = @owner
            ORDER BY r.ran_at DESC, r.id LIMIT @limit
            """, connection, transaction);
        command.Parameters.AddWithValue("report", reportId);
        command.Parameters.AddWithValue("owner", owner.Value);
        command.Parameters.AddWithValue("limit", limit);
        var items = new List<ReportRunDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) items.Add(ReadRun(reader));
        return items;
    }

    public async Task<ReportRunDetailDto?> GetRunAsync(TenantId tenantId, UserId owner, Guid reportId, Guid runId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT r.id, r.report_id, r.trigger, r.status, r.error_code, r.from_date, r.to_date, r.ran_at, r.result::text
            FROM report_runs r JOIN saved_reports s ON s.tenant_id = r.tenant_id AND s.id = r.report_id
            WHERE r.id = @run AND r.report_id = @report AND s.owner_user_id = @owner
            """, connection, transaction);
        command.Parameters.AddWithValue("run", runId);
        command.Parameters.AddWithValue("report", reportId);
        command.Parameters.AddWithValue("owner", owner.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var report = reader.IsDBNull(8) ? null : JsonSerializer.Deserialize<KpiReportDto>(reader.GetString(8), Json);
        return new ReportRunDetailDto(ReadRun(reader), report);
    }

    public async Task<Guid> AddRunAsync(TenantId tenantId, Guid reportId, string trigger, string status, string? errorCode, DateOnly from, DateOnly to, DateTimeOffset ranAt,
        KpiReportDto? result, DateTimeOffset? nextRunAt, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, cancellationToken);
        Guid id;
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO report_runs (tenant_id, report_id, trigger, status, error_code, from_date, to_date, ran_at, result)
            VALUES (@tenant, @report, @trigger, @status, @error, @from, @to, @ran, @result) RETURNING id
            """, connection, transaction))
        {
            insert.Parameters.AddWithValue("tenant", tenantId.Value);
            insert.Parameters.AddWithValue("report", reportId);
            insert.Parameters.AddWithValue("trigger", trigger);
            insert.Parameters.AddWithValue("status", status);
            insert.Parameters.AddWithValue("error", (object?)errorCode ?? DBNull.Value);
            insert.Parameters.AddWithValue("from", from);
            insert.Parameters.AddWithValue("to", to);
            insert.Parameters.AddWithValue("ran", ranAt);
            insert.Parameters.Add(new NpgsqlParameter("result", NpgsqlDbType.Jsonb) { Value = result is null ? DBNull.Value : JsonSerializer.Serialize(result, Json) });
            id = (Guid)(await insert.ExecuteScalarAsync(cancellationToken))!;
        }
        await using (var mark = new NpgsqlCommand("UPDATE saved_reports SET last_run_at = @ran, next_run_at = COALESCE(@next, next_run_at) WHERE id = @report", connection, transaction))
        {
            mark.Parameters.AddWithValue("ran", ranAt);
            mark.Parameters.AddWithValue("next", (object?)nextRunAt ?? DBNull.Value);
            mark.Parameters.AddWithValue("report", reportId);
            await mark.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var prune = new NpgsqlCommand("""
            DELETE FROM report_runs WHERE report_id = @report AND id IN (
                SELECT id FROM report_runs WHERE report_id = @report ORDER BY ran_at DESC, id OFFSET @keep)
            """, connection, transaction))
        {
            prune.Parameters.AddWithValue("report", reportId);
            prune.Parameters.AddWithValue("keep", SavedReportService.MaxRuns);
            await prune.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return id;
    }

    public async Task<IReadOnlyList<DueReport>> ListDueAsync(TenantId tenantId, DateTimeOffset now, int limit, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT id, owner_user_id, range_kind, from_date, to_date, region_id, team_id, customer_type, frequency, weekday, day_of_month, run_hour
            FROM saved_reports WHERE is_active AND frequency IS NOT NULL AND next_run_at <= @now
            ORDER BY next_run_at, id LIMIT @limit
            """, connection, transaction);
        command.Parameters.AddWithValue("now", now);
        command.Parameters.AddWithValue("limit", limit);
        var items = new List<DueReport>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            items.Add(new DueReport(reader.GetGuid(0), new UserId(reader.GetGuid(1)), reader.GetString(2), Date(reader, 3), Date(reader, 4), Guid_(reader, 5), Guid_(reader, 6),
                reader.IsDBNull(7) ? null : reader.GetString(7), reader.GetString(8), reader.IsDBNull(9) ? null : (int)reader.GetInt16(9), reader.IsDBNull(10) ? null : (int)reader.GetInt16(10),
                reader.GetInt16(11)));
        return items;
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static void Bind(NpgsqlCommand command, SavedReportData d)
    {
        command.Parameters.AddWithValue("name", d.Name);
        command.Parameters.AddWithValue("range", d.Range);
        command.Parameters.AddWithValue("from", (object?)d.From ?? DBNull.Value);
        command.Parameters.AddWithValue("to", (object?)d.To ?? DBNull.Value);
        command.Parameters.AddWithValue("region", (object?)d.RegionId ?? DBNull.Value);
        command.Parameters.AddWithValue("team", (object?)d.TeamId ?? DBNull.Value);
        command.Parameters.AddWithValue("type", (object?)d.CustomerType ?? DBNull.Value);
        command.Parameters.AddWithValue("freq", (object?)d.Frequency ?? DBNull.Value);
        command.Parameters.Add(new NpgsqlParameter("weekday", NpgsqlDbType.Smallint) { Value = d.Weekday is null ? DBNull.Value : (short)d.Weekday });
        command.Parameters.Add(new NpgsqlParameter("dom", NpgsqlDbType.Smallint) { Value = d.DayOfMonth is null ? DBNull.Value : (short)d.DayOfMonth });
        command.Parameters.Add(new NpgsqlParameter("hour", NpgsqlDbType.Smallint) { Value = d.Hour is null ? DBNull.Value : (short)d.Hour });
        command.Parameters.AddWithValue("active", d.IsActive);
        command.Parameters.AddWithValue("next", (object?)d.NextRunAt ?? DBNull.Value);
    }

    private static DateOnly? Date(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetFieldValue<DateOnly>(i);
    private static Guid? Guid_(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetGuid(i);

    private static SavedReportDto ReadReport(NpgsqlDataReader r) => new(r.GetGuid(0), r.GetString(1), r.GetString(2), Date(r, 3), Date(r, 4), Guid_(r, 5), Guid_(r, 6),
        r.IsDBNull(7) ? null : r.GetString(7),
        r.IsDBNull(8) ? null : new ScheduleDto(r.GetString(8), r.IsDBNull(9) ? null : (int)r.GetInt16(9), r.IsDBNull(10) ? null : (int)r.GetInt16(10), r.GetInt16(11), r.GetBoolean(12),
            r.IsDBNull(13) ? null : r.GetFieldValue<DateTimeOffset>(13), r.IsDBNull(14) ? null : r.GetFieldValue<DateTimeOffset>(14)),
        r.GetFieldValue<DateTimeOffset>(15));

    private static ReportRunDto ReadRun(NpgsqlDataReader r) => new(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4),
        r.GetFieldValue<DateOnly>(5), r.GetFieldValue<DateOnly>(6), r.GetFieldValue<DateTimeOffset>(7));

    private static async Task<NpgsqlTransaction> BeginAsync(NpgsqlConnection connection, TenantId tenantId, CancellationToken cancellationToken)
    {
        var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var setTenant = new NpgsqlCommand("SELECT set_config('app.current_tenant', @tenant, true)", connection, transaction);
        setTenant.Parameters.AddWithValue("tenant", tenantId.Value.ToString());
        await setTenant.ExecuteNonQueryAsync(cancellationToken);
        return transaction;
    }
}
