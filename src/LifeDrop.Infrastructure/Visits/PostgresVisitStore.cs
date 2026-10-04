using System.Threading;
using System.Threading.Tasks;
using LifeDrop.Application.Visits;
using LifeDrop.Domain.Common;
using LifeDrop.Domain.Visits;
using Npgsql;

namespace LifeDrop.Infrastructure.Visits;

public sealed class PostgresVisitStore(NpgsqlDataSource dataSource) : IVisitStore
{
    public async Task<(CreateVisitResult Result, Visit? Visit)> CreateAsync(TenantId tenantId, UserId actorId, Visit visit, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var setTenantCommand = new NpgsqlCommand("SELECT set_config('app.current_tenant', @tenant, true)", connection, transaction))
        {
            setTenantCommand.Parameters.AddWithValue("tenant", tenantId.Value.ToString());
            await setTenantCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        const string sql = """
            WITH u AS (
                SELECT id FROM users WHERE id = @userId AND tenant_id = @tenantId
            ),
            c AS (
                SELECT id FROM customers WHERE id = @customerId AND tenant_id = @tenantId
            )
            INSERT INTO visits (id, tenant_id, user_id, customer_id, status, schedule_date, created_at,
                visit_type, priority, planned_time, is_unplanned, unplanned_reason)
            SELECT @id, @tenantId, u.id, c.id, @status, @scheduleDate, now(),
                @visitType, @priority, @plannedTime, @isUnplanned, @unplannedReason
            FROM u, c
            RETURNING id;
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("id", visit.Id.Value);
        command.Parameters.AddWithValue("tenantId", tenantId.Value);
        command.Parameters.AddWithValue("userId", visit.UserId.Value);
        command.Parameters.AddWithValue("customerId", visit.CustomerId.Value);
        command.Parameters.AddWithValue("status", visit.Status.ToString());
        command.Parameters.AddWithValue("scheduleDate", visit.ScheduleDate);
        command.Parameters.AddWithValue("visitType", visit.Type.ToContract());
        command.Parameters.AddWithValue("priority", visit.Priority.ToContract());
        command.Parameters.AddWithValue("plannedTime", (object?)visit.PlannedTime ?? DBNull.Value);
        command.Parameters.AddWithValue("isUnplanned", visit.IsUnplanned);
        command.Parameters.AddWithValue("unplannedReason", (object?)visit.UnplannedReason ?? DBNull.Value);

        var insertedId = await command.ExecuteScalarAsync(cancellationToken);
        
        if (insertedId == null)
        {
            // Figure out what was missing
            await using var checkCmd = new NpgsqlCommand("SELECT (SELECT count(*) FROM users WHERE id = @userId AND tenant_id = @tenantId), (SELECT count(*) FROM customers WHERE id = @customerId AND tenant_id = @tenantId)", connection, transaction);
            checkCmd.Parameters.AddWithValue("tenantId", tenantId.Value);
            checkCmd.Parameters.AddWithValue("userId", visit.UserId.Value);
            checkCmd.Parameters.AddWithValue("customerId", visit.CustomerId.Value);
            
            await using var reader = await checkCmd.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                var userExists = reader.GetInt64(0) > 0;
                var customerExists = reader.GetInt64(1) > 0;
                if (!userExists) return (CreateVisitResult.UserNotFound, null);
                if (!customerExists) return (CreateVisitResult.CustomerNotFound, null);
            }
            return (CreateVisitResult.InvalidData, null);
        }

        await transaction.CommitAsync(cancellationToken);
        return (CreateVisitResult.Created, visit);
    }

    public async Task<System.Collections.Generic.List<VisitDto>> ListAssignedVisitsAsync(TenantId tenantId, UserId assignedUserId, System.DateOnly scheduleDate, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var setTenantCommand = new NpgsqlCommand("SELECT set_config('app.current_tenant', @tenant, true)", connection, transaction))
        {
            setTenantCommand.Parameters.AddWithValue("tenant", tenantId.Value.ToString());
            await setTenantCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        const string sql = @"
            SELECT id, user_id, customer_id, status, exception_reason, schedule_date, created_at,
                visit_type, priority, planned_time, is_unplanned, unplanned_reason 
            FROM visits 
            WHERE user_id = @userId AND schedule_date = @scheduleDate
            ORDER BY planned_time ASC NULLS LAST,
                     CASE priority WHEN 'high' THEN 0 WHEN 'normal' THEN 1 ELSE 2 END,
                     created_at ASC, id ASC;
        ";
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("userId", assignedUserId.Value);
        command.Parameters.AddWithValue("scheduleDate", scheduleDate);

        var results = new System.Collections.Generic.List<VisitDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new VisitDto(
                Id: reader.GetGuid(0),
                TenantId: tenantId.Value,
                UserId: reader.GetGuid(1),
                CustomerId: reader.GetGuid(2),
                Status: reader.GetString(3),
                ExceptionReason: reader.IsDBNull(4) ? null : reader.GetString(4),
                ScheduleDate: System.DateOnly.FromDateTime(reader.GetDateTime(5)),
                CreatedAt: reader.GetFieldValue<System.DateTimeOffset>(6),
                VisitType: reader.GetString(7),
                Priority: reader.GetString(8),
                PlannedTime: reader.IsDBNull(9) ? null : reader.GetFieldValue<System.TimeOnly>(9),
                IsUnplanned: reader.GetBoolean(10),
                UnplannedReason: reader.IsDBNull(11) ? null : reader.GetString(11)
            ));
        }

        return results;
    }

    public async Task<Visit?> GetAsync(TenantId tenantId, VisitId visitId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var setTenantCommand = new NpgsqlCommand("SELECT set_config('app.current_tenant', @tenant, true)", connection, transaction))
        {
            setTenantCommand.Parameters.AddWithValue("tenant", tenantId.Value.ToString());
            await setTenantCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        const string sql = @"
            SELECT user_id, customer_id, schedule_date, status, exception_reason
            FROM visits 
            WHERE id = @id;
        ";
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("id", visitId.Value);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            if (!System.Enum.TryParse<VisitStatus>(reader.GetString(3), out var status))
                return null;
                
            return Visit.Load(
                visitId,
                tenantId,
                new UserId(reader.GetGuid(0)),
                new CustomerId(reader.GetGuid(1)),
                System.DateOnly.FromDateTime(reader.GetDateTime(2)),
                status,
                reader.IsDBNull(4) ? null : reader.GetString(4)
            );
        }

        return null;
    }

    public async Task<bool> TryUpdateStatusAsync(TenantId tenantId, VisitId visitId, UserId expectedUserId, VisitStatus expectedStatus, VisitStatus newStatus, string? exceptionReason, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var setTenantCommand = new NpgsqlCommand("SELECT set_config('app.current_tenant', @tenant, true)", connection, transaction))
        {
            setTenantCommand.Parameters.AddWithValue("tenant", tenantId.Value.ToString());
            await setTenantCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        const string sql = @"
            UPDATE visits 
            SET status = @newStatus, exception_reason = @exceptionReason
            WHERE id = @id AND user_id = @userId AND status = @expectedStatus;
        ";
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("newStatus", newStatus.ToString());
        command.Parameters.AddWithValue("exceptionReason", (object?)exceptionReason ?? System.DBNull.Value);
        command.Parameters.AddWithValue("id", visitId.Value);
        command.Parameters.AddWithValue("userId", expectedUserId.Value);
        command.Parameters.AddWithValue("expectedStatus", expectedStatus.ToString());

        var rowsAffected = await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        
        return rowsAffected > 0;
    }

    public Task<bool> TryReassignAsync(TenantId tenantId, UserId actorId, VisitId visitId, UserId expectedUserId, UserId newUserId, CancellationToken cancellationToken) =>
        UpdateWithAuditAsync(tenantId, actorId, visitId, "visit.reassigned", """
            UPDATE visits SET user_id = @newUser
            WHERE id = @id AND user_id = @expectedUser AND status = 'Planned'
              AND EXISTS (SELECT 1 FROM users WHERE tenant_id = @tenant AND id = @newUser AND is_active)
            """, command =>
        {
            command.Parameters.AddWithValue("newUser", newUserId.Value);
            command.Parameters.AddWithValue("expectedUser", expectedUserId.Value);
        }, cancellationToken);

    public Task<bool> TryCancelAsync(TenantId tenantId, UserId actorId, VisitId visitId, UserId expectedUserId, VisitStatus expectedStatus, string reason, CancellationToken cancellationToken) =>
        UpdateWithAuditAsync(tenantId, actorId, visitId, "visit.cancelled", """
            UPDATE visits SET status = 'Cancelled', exception_reason = @reason
            WHERE id = @id AND user_id = @expectedUser AND status = @expectedStatus
            """, command =>
        {
            command.Parameters.AddWithValue("reason", reason);
            command.Parameters.AddWithValue("expectedUser", expectedUserId.Value);
            command.Parameters.AddWithValue("expectedStatus", expectedStatus.ToString());
        }, cancellationToken);

    // Upsert limited to a visit that is InVisit and assigned to the actor; the audit row is written in the same
    // transaction only when a row was written.
    public Task<bool> TrySaveReportAsync(TenantId tenantId, UserId actorId, VisitId visitId, VisitReport report, CancellationToken cancellationToken) =>
        UpdateWithAuditAsync(tenantId, actorId, visitId, "visit.report_saved", """
            INSERT INTO visit_reports (visit_id, tenant_id, outcome, note, contract_version, submitted_by)
            SELECT v.id, v.tenant_id, @outcome, @note, @version, @actor
            FROM visits v WHERE v.id = @id AND v.user_id = @actor AND v.status = 'InVisit'
            ON CONFLICT (visit_id) DO UPDATE
                SET outcome = EXCLUDED.outcome, note = EXCLUDED.note, contract_version = EXCLUDED.contract_version,
                    submitted_by = EXCLUDED.submitted_by, updated_at = now()
                WHERE EXISTS (SELECT 1 FROM visits v WHERE v.id = @id AND v.user_id = @actor AND v.status = 'InVisit')
            """, command =>
        {
            command.Parameters.AddWithValue("outcome", report.OutcomeContract);
            command.Parameters.AddWithValue("note", report.Note);
            command.Parameters.AddWithValue("version", (short)VisitReport.ContractVersion);
            command.Parameters.AddWithValue("actor", actorId.Value);
        }, cancellationToken);

    public async Task<VisitReportDto?> GetReportAsync(TenantId tenantId, VisitId visitId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var setTenant = new NpgsqlCommand("SELECT set_config('app.current_tenant', @tenant, true)", connection, transaction))
        {
            setTenant.Parameters.AddWithValue("tenant", tenantId.Value.ToString());
            await setTenant.ExecuteNonQueryAsync(cancellationToken);
        }
        await using var command = new NpgsqlCommand(
            "SELECT visit_id, outcome, note, contract_version, submitted_by, submitted_at, updated_at FROM visit_reports WHERE visit_id = @id",
            connection, transaction);
        command.Parameters.AddWithValue("id", visitId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new VisitReportDto(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetInt16(3), reader.GetGuid(4),
            reader.GetFieldValue<DateTimeOffset>(5), reader.GetFieldValue<DateTimeOffset>(6));
    }

    // Runs one conditional UPDATE and, only if it changed a row, its audit entry, in one transaction.
    private async Task<bool> UpdateWithAuditAsync(TenantId tenantId, UserId actorId, VisitId visitId, string auditAction, string updateSql,
        Action<NpgsqlCommand> addParameters, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var setTenant = new NpgsqlCommand("SELECT set_config('app.current_tenant', @tenant, true)", connection, transaction))
        {
            setTenant.Parameters.AddWithValue("tenant", tenantId.Value.ToString());
            await setTenant.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var update = new NpgsqlCommand(updateSql, connection, transaction);
        update.Parameters.AddWithValue("id", visitId.Value);
        update.Parameters.AddWithValue("tenant", tenantId.Value);
        addParameters(update);
        if (await update.ExecuteNonQueryAsync(cancellationToken) == 0) return false;

        await using var audit = new NpgsqlCommand("""
            INSERT INTO audit_logs (id, tenant_id, actor_id, action, entity_id, occurred_at)
            VALUES (gen_random_uuid(), @tenant, @actor, @action, @entity, now())
            """, connection, transaction);
        audit.Parameters.AddWithValue("tenant", tenantId.Value);
        audit.Parameters.AddWithValue("actor", actorId.Value);
        audit.Parameters.AddWithValue("action", auditAction);
        audit.Parameters.AddWithValue("entity", visitId.Value.ToString());
        await audit.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }
}
