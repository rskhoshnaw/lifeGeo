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
        await using var transaction = await BeginTenantAsync(connection, tenantId, cancellationToken, actorId);

        const string sql = """
            WITH u AS (
                SELECT id FROM users WHERE id = @userId AND tenant_id = @tenantId
            ),
            c AS (
                SELECT id FROM customers WHERE id = @customerId AND tenant_id = @tenantId AND archived_at IS NULL
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

        await InsertAuditAsync(connection, transaction, tenantId, actorId, visit.IsUnplanned ? "visit.created_unplanned" : "visit.created", visit.Id, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (CreateVisitResult.Created, visit);
    }

    // Whether the visit's day (and planned time, when set) fits the customer's accepted visit windows (plan step 1.23):
    // 'none' = the customer has no windows, 'within', or 'outside'. weekday 0 = Sunday, like EXTRACT(DOW).
    private const string WindowStatusSql = @"CASE
                WHEN NOT EXISTS (SELECT 1 FROM customer_visit_windows w WHERE w.tenant_id = v.tenant_id AND w.customer_id = v.customer_id) THEN 'none'
                WHEN EXISTS (SELECT 1 FROM customer_visit_windows w WHERE w.tenant_id = v.tenant_id AND w.customer_id = v.customer_id
                             AND w.weekday = EXTRACT(DOW FROM v.schedule_date)::int
                             AND (v.planned_time IS NULL OR (v.planned_time >= w.opens_at AND v.planned_time < w.closes_at))) THEN 'within'
                ELSE 'outside' END";

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
            SELECT v.id, v.user_id, v.customer_id, v.status, v.exception_reason, v.schedule_date, v.created_at,
                v.visit_type, v.priority, v.planned_time, v.is_unplanned, v.unplanned_reason, c.name, c.code,
                v.sort_order, v.postpone_count, v.postpone_reason, " + WindowStatusSql + @", c.priority
            FROM visits v
            LEFT JOIN customers c ON c.tenant_id = v.tenant_id AND c.id = v.customer_id
            WHERE v.user_id = @userId AND v.schedule_date = @scheduleDate
            ORDER BY v.sort_order ASC NULLS LAST, v.planned_time ASC NULLS LAST,
                     CASE v.priority WHEN 'high' THEN 0 WHEN 'normal' THEN 1 ELSE 2 END,
                     v.created_at ASC, v.id ASC;
        ";
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("userId", assignedUserId.Value);
        command.Parameters.AddWithValue("scheduleDate", scheduleDate);

        return await ReadVisitsAsync(command, tenantId, withAssigneeEmail: false, cancellationToken);
    }

    public async Task<System.Collections.Generic.List<VisitDto>> ListPlannedVisitsAsync(TenantId tenantId, LifeDrop.Application.Roles.AccessScope scope,
        System.DateOnly scheduleDate, UserId? assignedUserId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var setTenantCommand = new NpgsqlCommand("SELECT set_config('app.current_tenant', @tenant, true)", connection, transaction))
        {
            setTenantCommand.Parameters.AddWithValue("tenant", tenantId.Value.ToString());
            await setTenantCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        // Scope 2 = tenant, 1 = region (the actor and members of teams in the region), 0 = own (DataScope values).
        const string sql = @"
            SELECT v.id, v.user_id, v.customer_id, v.status, v.exception_reason, v.schedule_date, v.created_at,
                v.visit_type, v.priority, v.planned_time, v.is_unplanned, v.unplanned_reason, c.name, c.code,
                v.sort_order, v.postpone_count, v.postpone_reason, " + WindowStatusSql + @", u.email, c.priority
            FROM visits v
            LEFT JOIN customers c ON c.tenant_id = v.tenant_id AND c.id = v.customer_id
            LEFT JOIN users u ON u.tenant_id = v.tenant_id AND u.id = v.user_id
            WHERE v.tenant_id = @tenantId AND v.schedule_date = @scheduleDate
              AND (@assignee::uuid IS NULL OR v.user_id = @assignee)
              AND (@scope = 2 OR v.user_id = @actor
                   OR (@scope = 1 AND @region::uuid IS NOT NULL AND EXISTS (
                       SELECT 1 FROM team_members m JOIN teams t ON t.id = m.team_id
                       WHERE m.user_id = v.user_id AND t.region_id = @region)))
            ORDER BY v.sort_order ASC NULLS LAST, v.planned_time ASC NULLS LAST,
                     CASE v.priority WHEN 'high' THEN 0 WHEN 'normal' THEN 1 ELSE 2 END,
                     v.created_at ASC, v.id ASC;
        ";
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("tenantId", tenantId.Value);
        command.Parameters.AddWithValue("scheduleDate", scheduleDate);
        command.Parameters.Add(new NpgsqlParameter("assignee", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)assignedUserId?.Value ?? System.DBNull.Value });
        command.Parameters.AddWithValue("scope", (int)scope.Scope);
        command.Parameters.AddWithValue("actor", scope.ActorId.Value);
        command.Parameters.Add(new NpgsqlParameter("region", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)scope.RegionId ?? System.DBNull.Value });
        return await ReadVisitsAsync(command, tenantId, withAssigneeEmail: true, cancellationToken);
    }

    private static async Task<System.Collections.Generic.List<VisitDto>> ReadVisitsAsync(NpgsqlCommand command, TenantId tenantId,
        bool withAssigneeEmail, CancellationToken cancellationToken)
    {
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
                UnplannedReason: reader.IsDBNull(11) ? null : reader.GetString(11),
                CustomerName: reader.IsDBNull(12) ? null : reader.GetString(12),
                CustomerCode: reader.IsDBNull(13) ? null : reader.GetString(13),
                AssigneeEmail: withAssigneeEmail && !reader.IsDBNull(18) ? reader.GetString(18) : null,
                SortOrder: reader.IsDBNull(14) ? null : reader.GetInt32(14),
                PostponeCount: reader.GetInt32(15),
                PostponeReason: reader.IsDBNull(16) ? null : reader.GetString(16),
                WindowStatus: reader.GetString(17),
                CustomerPriority: reader.IsDBNull(withAssigneeEmail ? 19 : 18) ? "normal" : reader.GetString(withAssigneeEmail ? 19 : 18)
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
        await using var transaction = await BeginTenantAsync(connection, tenantId, cancellationToken, expectedUserId);

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

        if (await command.ExecuteNonQueryAsync(cancellationToken) == 0) return false;

        // The status history row is written by a trigger; the audit row names the transition.
        await InsertAuditAsync(connection, transaction, tenantId, expectedUserId, newStatus switch
        {
            VisitStatus.EnRoute => "visit.en_route",
            VisitStatus.Arrived => "visit.arrived",
            VisitStatus.Failed => "visit.failed",
            _ => "visit.status_changed"
        }, visitId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
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

    // Plan step 1.23. The positions are written only when every listed visit belongs to the assignee on that day.
    public async Task<bool> TryReorderAsync(TenantId tenantId, UserId actorId, UserId assigneeId, System.DateOnly scheduleDate,
        System.Collections.Generic.IReadOnlyList<System.Guid> visitIds, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginTenantAsync(connection, tenantId, cancellationToken, actorId);

        await using var update = new NpgsqlCommand("""
            UPDATE visits v SET sort_order = o.pos
            FROM unnest(@ids::uuid[], @positions::int[]) AS o(id, pos)
            WHERE v.id = o.id AND v.tenant_id = @tenant AND v.user_id = @assignee AND v.schedule_date = @day
            """, connection, transaction);
        update.Parameters.AddWithValue("ids", System.Linq.Enumerable.ToArray(visitIds));
        update.Parameters.AddWithValue("positions", System.Linq.Enumerable.Range(1, visitIds.Count).ToArray());
        update.Parameters.AddWithValue("tenant", tenantId.Value);
        update.Parameters.AddWithValue("assignee", assigneeId.Value);
        update.Parameters.AddWithValue("day", scheduleDate);
        if (await update.ExecuteNonQueryAsync(cancellationToken) != visitIds.Count)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        await InsertAuditAsync(connection, transaction, tenantId, actorId, "visit.reordered", assigneeId.Value.ToString(), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public Task<bool> TryPostponeAsync(TenantId tenantId, UserId actorId, VisitId visitId, UserId expectedUserId, VisitStatus expectedStatus,
        System.DateOnly newDate, string? reason, CancellationToken cancellationToken) =>
        UpdateWithAuditAsync(tenantId, actorId, visitId, "visit.postponed", """
            UPDATE visits SET schedule_date = @newDate, status = 'Planned', sort_order = NULL,
                postpone_count = postpone_count + 1, postpone_reason = @reason
            WHERE id = @id AND user_id = @expectedUser AND status = @expectedStatus
            """, command =>
        {
            command.Parameters.AddWithValue("newDate", newDate);
            command.Parameters.Add(new NpgsqlParameter("reason", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)reason ?? System.DBNull.Value });
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

    public async Task<System.Collections.Generic.List<VisitStatusChangeDto>> ListStatusHistoryAsync(TenantId tenantId, VisitId visitId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginTenantAsync(connection, tenantId, cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT actor_id, from_status, to_status, reason, occurred_at FROM visit_status_history
            WHERE visit_id = @id ORDER BY occurred_at, id
            """, connection, transaction);
        command.Parameters.AddWithValue("id", visitId.Value);
        var history = new System.Collections.Generic.List<VisitStatusChangeDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            history.Add(new VisitStatusChangeDto(reader.IsDBNull(0) ? null : reader.GetGuid(0), reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetFieldValue<DateTimeOffset>(4)));
        return history;
    }

    public async Task<(double DistanceMeters, int RadiusMeters)?> MeasureFromCustomerAsync(TenantId tenantId, CustomerId customerId, GpsFix fix, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginTenantAsync(connection, tenantId, cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT ST_Distance(geom, ST_SetSRID(ST_MakePoint(@lon, @lat), 4326)::geography), coalesce(geofence_radius_m, 20)
            FROM customers WHERE id = @id AND geom IS NOT NULL
            """, connection, transaction);
        command.Parameters.AddWithValue("id", customerId.Value);
        command.Parameters.AddWithValue("lat", fix.Latitude);
        command.Parameters.AddWithValue("lon", fix.Longitude);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return (reader.GetDouble(0), reader.GetInt32(1));
    }

    public async Task<bool> HasAcceptedEventAsync(TenantId tenantId, VisitId visitId, string kind, string idempotencyKey, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginTenantAsync(connection, tenantId, cancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM visit_location_events WHERE visit_id = @id AND kind = @kind AND accepted AND idempotency_key = @key)",
            connection, transaction);
        command.Parameters.AddWithValue("id", visitId.Value);
        command.Parameters.AddWithValue("kind", kind);
        command.Parameters.AddWithValue("key", idempotencyKey);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
    }

    public async Task<bool> TryAcceptLocationEventAsync(TenantId tenantId, UserId actorId, VisitId visitId, VisitStatus expectedStatus, VisitStatus newStatus,
        LocationEvent locationEvent, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginTenantAsync(connection, tenantId, cancellationToken, actorId);
        await using (var update = new NpgsqlCommand(
            """
            UPDATE visits SET status = @new
            WHERE id = @id AND user_id = @actor AND status = @expected
              AND (@new <> 'Completed' OR EXISTS (SELECT 1 FROM visit_reports r WHERE r.visit_id = visits.id))
            """, connection, transaction))
        {
            update.Parameters.AddWithValue("new", newStatus.ToString());
            update.Parameters.AddWithValue("id", visitId.Value);
            update.Parameters.AddWithValue("actor", actorId.Value);
            update.Parameters.AddWithValue("expected", expectedStatus.ToString());
            if (await update.ExecuteNonQueryAsync(cancellationToken) == 0) return false;
        }

        await InsertLocationEventAsync(connection, transaction, tenantId, actorId, visitId, locationEvent, cancellationToken);
        await InsertAuditAsync(connection, transaction, tenantId, actorId, locationEvent.Kind == "check_in" ? "visit.checked_in" : "visit.checked_out", visitId, cancellationToken);
        if (NeedsReview(locationEvent))
            await InsertAuditAsync(connection, transaction, tenantId, actorId, "visit.offline_review_required", visitId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task RecordRejectedLocationEventAsync(TenantId tenantId, UserId actorId, VisitId visitId, LocationEvent locationEvent, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginTenantAsync(connection, tenantId, cancellationToken);
        await InsertLocationEventAsync(connection, transaction, tenantId, actorId, visitId, locationEvent, cancellationToken);
        await InsertAuditAsync(connection, transaction, tenantId, actorId, $"visit.{locationEvent.Kind}_rejected", visitId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static bool NeedsReview(LocationEvent e) => e.Offline && e.Rejection is null && e.ReviewReasons is { Count: > 0 };

    public async Task<bool> HasTrackingPointNearCustomerAsync(TenantId tenantId, UserId userId, CustomerId customerId, DateTimeOffset from,
        DateTimeOffset to, double maxDistanceMeters, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginTenantAsync(connection, tenantId, cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT EXISTS (
                SELECT 1 FROM tracking_points p JOIN customers c ON c.id = @customer AND c.geom IS NOT NULL
                WHERE p.user_id = @user AND p.captured_at BETWEEN @from AND @to AND NOT p.is_mocked
                  AND ST_DWithin(p.geom, c.geom, @distance))
            """, connection, transaction);
        command.Parameters.AddWithValue("customer", customerId.Value);
        command.Parameters.AddWithValue("user", userId.Value);
        command.Parameters.AddWithValue("from", from);
        command.Parameters.AddWithValue("to", to);
        command.Parameters.AddWithValue("distance", maxDistanceMeters);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
    }

    private static async Task InsertLocationEventAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, TenantId tenantId, UserId actorId,
        VisitId visitId, LocationEvent e, CancellationToken cancellationToken)
    {
        await using var insert = new NpgsqlCommand("""
            INSERT INTO visit_location_events (id, tenant_id, visit_id, user_id, device_id, kind, accepted, rejection_reason,
                geom, accuracy_m, distance_m, radius_m, captured_at, is_mocked, idempotency_key,
                offline, elapsed_ms, review_state, review_reasons)
            VALUES (gen_random_uuid(), @tenant, @visit, @user, @device, @kind, @accepted, @reason,
                ST_SetSRID(ST_MakePoint(@lon, @lat), 4326)::geography, @accuracy, @distance, @radius, @captured, @mocked, @key,
                @offline, @elapsed, @reviewState, @reviewReasons)
            """, connection, transaction);
        insert.Parameters.AddWithValue("tenant", tenantId.Value);
        insert.Parameters.AddWithValue("visit", visitId.Value);
        insert.Parameters.AddWithValue("user", actorId.Value);
        insert.Parameters.AddWithValue("device", e.DeviceId);
        insert.Parameters.AddWithValue("kind", e.Kind);
        insert.Parameters.AddWithValue("accepted", e.Rejection is null);
        insert.Parameters.AddWithValue("reason", (object?)e.Rejection?.ToContract() ?? DBNull.Value);
        insert.Parameters.AddWithValue("lat", e.Fix.Latitude);
        insert.Parameters.AddWithValue("lon", e.Fix.Longitude);
        insert.Parameters.AddWithValue("accuracy", Math.Min(e.Fix.AccuracyMeters, 99999.99));
        insert.Parameters.AddWithValue("distance", (object?)(e.DistanceMeters is { } d ? Math.Min(d, 9999999.99) : null) ?? DBNull.Value);
        insert.Parameters.AddWithValue("radius", (object?)e.RadiusMeters ?? DBNull.Value);
        insert.Parameters.AddWithValue("captured", e.Fix.CapturedAt);
        insert.Parameters.AddWithValue("mocked", e.Fix.IsMocked);
        insert.Parameters.AddWithValue("key", (object?)e.IdempotencyKey ?? DBNull.Value);
        insert.Parameters.AddWithValue("offline", e.Offline);
        insert.Parameters.AddWithValue("elapsed", (object?)e.ElapsedMs ?? DBNull.Value);
        insert.Parameters.AddWithValue("reviewState", NeedsReview(e) ? "pending" : DBNull.Value);
        insert.Parameters.AddWithValue("reviewReasons", (e.ReviewReasons ?? []).ToArray());
        await insert.ExecuteNonQueryAsync(cancellationToken);
    }

    private static Task InsertAuditAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, TenantId tenantId, UserId actorId,
        string action, VisitId visitId, CancellationToken cancellationToken) =>
        InsertAuditAsync(connection, transaction, tenantId, actorId, action, visitId.Value.ToString(), cancellationToken);

    private static async Task InsertAuditAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, TenantId tenantId, UserId actorId,
        string action, string entityId, CancellationToken cancellationToken)
    {
        await using var audit = new NpgsqlCommand("""
            INSERT INTO audit_logs (id, tenant_id, actor_id, action, entity_id, occurred_at)
            VALUES (gen_random_uuid(), @tenant, @actor, @action, @entity, now())
            """, connection, transaction);
        audit.Parameters.AddWithValue("tenant", tenantId.Value);
        audit.Parameters.AddWithValue("actor", actorId.Value);
        audit.Parameters.AddWithValue("action", action);
        audit.Parameters.AddWithValue("entity", entityId);
        await audit.ExecuteNonQueryAsync(cancellationToken);
    }

    // Sets the tenant for RLS and, for writes, the actor that the visit status history trigger records (step 1.9).
    private static async Task<NpgsqlTransaction> BeginTenantAsync(NpgsqlConnection connection, TenantId tenantId, CancellationToken cancellationToken,
        UserId? actorId = null)
    {
        var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var setTenant = new NpgsqlCommand(
            "SELECT set_config('app.current_tenant', @tenant, true), set_config('app.current_actor', @actor, true)", connection, transaction);
        setTenant.Parameters.AddWithValue("tenant", tenantId.Value.ToString());
        setTenant.Parameters.AddWithValue("actor", actorId?.Value.ToString() ?? "");
        await setTenant.ExecuteNonQueryAsync(cancellationToken);
        return transaction;
    }

    // Runs one conditional UPDATE and, only if it changed a row, its audit entry, in one transaction.
    private async Task<bool> UpdateWithAuditAsync(TenantId tenantId, UserId actorId, VisitId visitId, string auditAction, string updateSql,
        Action<NpgsqlCommand> addParameters, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginTenantAsync(connection, tenantId, cancellationToken, actorId);

        await using var update = new NpgsqlCommand(updateSql, connection, transaction);
        update.Parameters.AddWithValue("id", visitId.Value);
        update.Parameters.AddWithValue("tenant", tenantId.Value);
        addParameters(update);
        if (await update.ExecuteNonQueryAsync(cancellationToken) == 0) return false;

        await InsertAuditAsync(connection, transaction, tenantId, actorId, auditAction, visitId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }
}
