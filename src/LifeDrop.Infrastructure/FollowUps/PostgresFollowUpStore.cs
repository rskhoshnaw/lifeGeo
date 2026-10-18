using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LifeDrop.Application.FollowUps;
using LifeDrop.Domain.Common;
using Npgsql;
using NpgsqlTypes;

namespace LifeDrop.Infrastructure.FollowUps;

public sealed class PostgresFollowUpStore(NpgsqlDataSource dataSource) : IFollowUpStore
{
    private static NpgsqlParameter P(string name, NpgsqlDbType type, object? value) => new(name, type) { Value = value ?? DBNull.Value };

    public async Task<(FollowUpResult Result, CreatedFollowUpDto? FollowUp)> CreateAsync(TenantId tenantId, UserId actorId, NewFollowUp f, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, actorId, cancellationToken);
        await using (var again = new NpgsqlCommand("SELECT id, created_by, customer_id FROM follow_ups WHERE tenant_id = @tenant AND client_ref = @ref", connection, transaction))
        {
            again.Parameters.AddWithValue("tenant", tenantId.Value);
            again.Parameters.AddWithValue("ref", f.ClientRef);
            await using var reader = await again.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
                return reader.GetGuid(1) == actorId.Value && reader.GetGuid(2) == f.CustomerId ? (FollowUpResult.Success, new CreatedFollowUpDto(reader.GetGuid(0), true)) : (FollowUpResult.Conflict, null);
        }
        var id = Guid.NewGuid();
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO follow_ups (id, tenant_id, customer_id, subject, due_date, assignee_id, created_by, client_ref)
            VALUES (@id, @tenant, @customer, @subject, @due, @assignee, @actor, @ref)
            """, connection, transaction))
        {
            insert.Parameters.AddWithValue("id", id);
            insert.Parameters.AddWithValue("tenant", tenantId.Value);
            insert.Parameters.AddWithValue("customer", f.CustomerId);
            insert.Parameters.AddWithValue("subject", f.Subject);
            insert.Parameters.Add(P("due", NpgsqlDbType.Date, f.DueDate));
            insert.Parameters.AddWithValue("assignee", f.AssigneeId);
            insert.Parameters.AddWithValue("actor", actorId.Value);
            insert.Parameters.AddWithValue("ref", f.ClientRef);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        await AuditAsync(connection, transaction, tenantId, actorId, "followup.created", id, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (FollowUpResult.Success, new CreatedFollowUpDto(id, false));
    }

    private const string Columns = """
        f.id, f.customer_id, c.code, c.name, f.subject, f.due_date, f.assignee_id, a.email, f.created_by, cb.email, f.status,
        (f.status IN ('pending', 'in_progress') AND f.due_date < @today), f.result_note, f.cancel_reason, f.created_at, f.completed_at
        """;

    private const string Joins = """
        FROM follow_ups f
        JOIN customers c ON c.tenant_id = f.tenant_id AND c.id = f.customer_id
        LEFT JOIN users a ON a.tenant_id = f.tenant_id AND a.id = f.assignee_id
        LEFT JOIN users cb ON cb.tenant_id = f.tenant_id AND cb.id = f.created_by
        """;

    private static FollowUpDto Read(NpgsqlDataReader r) => new(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetString(3), r.GetString(4), DateOnly.FromDateTime(r.GetDateTime(5)), r.GetGuid(6),
        r.IsDBNull(7) ? null : r.GetString(7), r.GetGuid(8), r.IsDBNull(9) ? null : r.GetString(9), r.GetString(10), r.GetBoolean(11), r.IsDBNull(12) ? null : r.GetString(12),
        r.IsDBNull(13) ? null : r.GetString(13), r.GetFieldValue<DateTimeOffset>(14), r.IsDBNull(15) ? null : r.GetFieldValue<DateTimeOffset>(15));

    public async Task<IReadOnlyList<FollowUpDto>> ListAsync(TenantId tenantId, UserId actorId, FollowUpView view, Guid? regionId, Guid? customerId, Guid? assigneeId, string? status, bool overdueOnly,
        DateOnly today, int limit, CancellationToken cancellationToken)
    {
        var visibility = view switch
        {
            FollowUpView.Everything => "TRUE",
            FollowUpView.Region => "(f.assignee_id = @actor OR f.created_by = @actor OR EXISTS (SELECT 1 FROM team_members m JOIN teams t ON t.id = m.team_id WHERE m.user_id = f.assignee_id AND t.region_id = @region))",
            _ => "(f.assignee_id = @actor OR f.created_by = @actor)"
        };
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, null, cancellationToken);
        await using var command = new NpgsqlCommand($"""
            SELECT {Columns} {Joins}
            WHERE f.tenant_id = @tenant AND {visibility}
              AND (@customer IS NULL OR f.customer_id = @customer) AND (@assignee IS NULL OR f.assignee_id = @assignee) AND (@status IS NULL OR f.status = @status)
              AND (NOT @overdue OR (f.status IN ('pending', 'in_progress') AND f.due_date < @today))
            ORDER BY f.due_date, f.created_at, f.id LIMIT @limit
            """, connection, transaction);
        command.Parameters.AddWithValue("tenant", tenantId.Value);
        command.Parameters.AddWithValue("actor", actorId.Value);
        command.Parameters.Add(P("region", NpgsqlDbType.Uuid, regionId));
        command.Parameters.Add(P("customer", NpgsqlDbType.Uuid, customerId));
        command.Parameters.Add(P("assignee", NpgsqlDbType.Uuid, assigneeId));
        command.Parameters.Add(P("status", NpgsqlDbType.Text, status));
        command.Parameters.AddWithValue("overdue", overdueOnly);
        command.Parameters.Add(P("today", NpgsqlDbType.Date, today));
        command.Parameters.AddWithValue("limit", limit);
        var items = new List<FollowUpDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) items.Add(Read(reader));
        return items;
    }

    public async Task<FollowUpDto?> GetAsync(TenantId tenantId, Guid followUpId, DateOnly today, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, null, cancellationToken);
        await using var command = new NpgsqlCommand($"SELECT {Columns} {Joins} WHERE f.tenant_id = @tenant AND f.id = @id", connection, transaction);
        command.Parameters.AddWithValue("tenant", tenantId.Value);
        command.Parameters.AddWithValue("id", followUpId);
        command.Parameters.Add(P("today", NpgsqlDbType.Date, today));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    // A step the phone sends again after a lost answer is not a conflict (plan step 2.13): starting a follow-up that is in progress, or completing / cancelling one that already
    // has the same note or reason, answers Success without writing again. Any other status or another text is a conflict.
    public Task<FollowUpResult> StartAsync(TenantId tenantId, UserId actorId, Guid id, CancellationToken cancellationToken) =>
        ChangeAsync(tenantId, actorId, id, "followup.started", "status = 'in_progress', started_at = now()", "status = 'pending'", _ => { },
            "status = 'in_progress'", null, cancellationToken);

    public Task<FollowUpResult> CompleteAsync(TenantId tenantId, UserId actorId, Guid id, string resultNote, CancellationToken cancellationToken) =>
        ChangeAsync(tenantId, actorId, id, "followup.completed", "status = 'completed', result_note = @note, completed_at = now()", "status IN ('pending', 'in_progress')",
            c => c.Parameters.AddWithValue("note", resultNote), "status = 'completed' AND result_note = @note", c => c.Parameters.AddWithValue("note", resultNote), cancellationToken);

    public Task<FollowUpResult> CancelAsync(TenantId tenantId, UserId actorId, Guid id, string reason, CancellationToken cancellationToken) =>
        ChangeAsync(tenantId, actorId, id, "followup.cancelled", "status = 'cancelled', cancel_reason = @reason, cancelled_at = now()", "status IN ('pending', 'in_progress')",
            c => c.Parameters.AddWithValue("reason", reason), "status = 'cancelled' AND cancel_reason = @reason", c => c.Parameters.AddWithValue("reason", reason), cancellationToken);

    public Task<FollowUpResult> ReassignAsync(TenantId tenantId, UserId actorId, Guid id, Guid assigneeId, CancellationToken cancellationToken) =>
        ChangeAsync(tenantId, actorId, id, "followup.reassigned", "assignee_id = @assignee", "status IN ('pending', 'in_progress')", c => c.Parameters.AddWithValue("assignee", assigneeId),
            null, null, cancellationToken);

    public Task<FollowUpResult> EditAsync(TenantId tenantId, UserId actorId, Guid id, string subject, DateOnly dueDate, CancellationToken cancellationToken) =>
        ChangeAsync(tenantId, actorId, id, "followup.edited", "subject = @subject, due_date = @due", "status IN ('pending', 'in_progress')", c =>
        {
            c.Parameters.AddWithValue("subject", subject);
            c.Parameters.Add(P("due", NpgsqlDbType.Date, dueDate));
        }, null, null, cancellationToken);

    private async Task<FollowUpResult> ChangeAsync(TenantId tenantId, UserId actorId, Guid id, string action, string set, string whenStatus, Action<NpgsqlCommand> bind,
        string? alreadyDone, Action<NpgsqlCommand>? bindDone, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, actorId, cancellationToken);
        await using (var update = new NpgsqlCommand($"UPDATE follow_ups SET {set}, updated_at = now() WHERE tenant_id = @tenant AND id = @id AND {whenStatus}", connection, transaction))
        {
            update.Parameters.AddWithValue("tenant", tenantId.Value);
            update.Parameters.AddWithValue("id", id);
            bind(update);
            if (await update.ExecuteNonQueryAsync(cancellationToken) == 0)
            {
                await using var exists = new NpgsqlCommand($"SELECT {(alreadyDone is null ? "false" : alreadyDone)} FROM follow_ups WHERE tenant_id = @tenant AND id = @id", connection, transaction);
                exists.Parameters.AddWithValue("tenant", tenantId.Value);
                exists.Parameters.AddWithValue("id", id);
                bindDone?.Invoke(exists);
                var done = await exists.ExecuteScalarAsync(cancellationToken);
                return done is null ? FollowUpResult.NotFound : done is true ? FollowUpResult.Success : FollowUpResult.Conflict;
            }
        }
        await AuditAsync(connection, transaction, tenantId, actorId, action, id, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return FollowUpResult.Success;
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

    private static async Task AuditAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, TenantId tenantId, UserId actorId, string action, Guid entity, CancellationToken cancellationToken)
    {
        await using var audit = new NpgsqlCommand("""
            INSERT INTO audit_logs (id, tenant_id, actor_id, action, entity_id, occurred_at) VALUES (gen_random_uuid(), @tenant, @actor, @action, @entity, now())
            """, connection, transaction);
        audit.Parameters.AddWithValue("tenant", tenantId.Value);
        audit.Parameters.AddWithValue("actor", actorId.Value);
        audit.Parameters.AddWithValue("action", action);
        audit.Parameters.AddWithValue("entity", entity.ToString());
        await audit.ExecuteNonQueryAsync(cancellationToken);
    }
}
