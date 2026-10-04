using LifeDrop.Application.Audit;
using LifeDrop.Application.Roles;
using LifeDrop.Domain.Common;
using Npgsql;

namespace LifeDrop.Infrastructure.Audit;

public sealed class PostgresAuditLogStore(NpgsqlDataSource dataSource) : IAuditLogStore
{
    public async Task<IReadOnlyList<AuditLogEntryDto>> SearchAsync(TenantId tenantId, AccessScope scope, AuditLogQuery query, int limit, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var setTenant = new NpgsqlCommand("SELECT set_config('app.current_tenant', @tenant, true)", connection, transaction))
        {
            setTenant.Parameters.AddWithValue("tenant", tenantId.Value.ToString());
            await setTenant.ExecuteNonQueryAsync(cancellationToken);
        }

        // audit_logs has no RLS, so the tenant filter is explicit. Region scope: entries whose actor is in a
        // team of the caller's region (team tables are tenant-filtered by RLS). Own scope: the caller's own entries.
        var sql = """
            SELECT a.id, a.actor_id, a.action, a.entity_id, a.occurred_at
            FROM audit_logs a
            WHERE a.tenant_id = @tenant
            """;
        sql += scope.Scope switch
        {
            DataScope.Tenant => "",
            DataScope.Region when scope.RegionId is not null => """

                AND (a.actor_id = @scopeActor OR a.actor_id IN (
                    SELECT m.user_id FROM team_members m JOIN teams t ON t.id = m.team_id WHERE t.region_id = @scopeRegion))
                """,
            _ => "\nAND a.actor_id = @scopeActor"
        };
        if (query.Action is not null) sql += "\nAND a.action = @action";
        if (query.ActorId is not null) sql += "\nAND a.actor_id = @actorFilter";
        if (query.EntityId is not null) sql += "\nAND a.entity_id = @entity";
        if (query.From is not null) sql += "\nAND a.occurred_at >= @from";
        if (query.To is not null) sql += "\nAND a.occurred_at <= @to";
        if (query.CursorOccurredAt is not null)
            sql += "\nAND (a.occurred_at < @cursorAt OR (a.occurred_at = @cursorAt AND a.id < @cursorId))";
        sql += "\nORDER BY a.occurred_at DESC, a.id DESC\nLIMIT @limit";

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("tenant", tenantId.Value);
        command.Parameters.AddWithValue("scopeActor", scope.ActorId.Value);
        command.Parameters.AddWithValue("scopeRegion", (object?)scope.RegionId ?? DBNull.Value);
        command.Parameters.AddWithValue("limit", limit);
        if (query.Action is not null) command.Parameters.AddWithValue("action", query.Action);
        if (query.ActorId is not null) command.Parameters.AddWithValue("actorFilter", query.ActorId.Value);
        if (query.EntityId is not null) command.Parameters.AddWithValue("entity", query.EntityId);
        if (query.From is not null) command.Parameters.AddWithValue("from", query.From.Value);
        if (query.To is not null) command.Parameters.AddWithValue("to", query.To.Value);
        if (query.CursorOccurredAt is not null)
        {
            command.Parameters.AddWithValue("cursorAt", query.CursorOccurredAt.Value);
            command.Parameters.AddWithValue("cursorId", query.CursorId!.Value);
        }

        var items = new List<AuditLogEntryDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            items.Add(new AuditLogEntryDto(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3),
                reader.GetFieldValue<DateTimeOffset>(4)));
        return items;
    }
}
