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
            ORDER BY created_at ASC;
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
}
