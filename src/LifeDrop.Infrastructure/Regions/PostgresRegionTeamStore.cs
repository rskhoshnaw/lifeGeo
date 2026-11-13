using LifeDrop.Application.Regions;
using LifeDrop.Domain.Common;
using Npgsql;

namespace LifeDrop.Infrastructure.Regions;

// Every write runs in one transaction with the tenant context set for RLS and appends its audit row
// in the same transaction, so a change is never stored without its audit entry.
public sealed class PostgresRegionTeamStore(NpgsqlDataSource dataSource) : IRegionTeamStore
{
    public async Task<RegionTeamResult> CreateRegionAsync(TenantId tenantId, UserId actorId, Guid regionId, string name, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginTenantTransactionAsync(connection, tenantId, cancellationToken);

        await using var insert = new NpgsqlCommand("""
            INSERT INTO regions (id, tenant_id, name) VALUES (@id, @tenant, @name)
            ON CONFLICT (tenant_id, name) DO NOTHING
            """, connection, transaction);
        insert.Parameters.AddWithValue("id", regionId);
        insert.Parameters.AddWithValue("tenant", tenantId.Value);
        insert.Parameters.AddWithValue("name", name);
        if (await insert.ExecuteNonQueryAsync(cancellationToken) == 0) return RegionTeamResult.Duplicate;

        await AuditAsync(connection, transaction, tenantId, actorId, "region.created", regionId.ToString(), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return RegionTeamResult.Success;
    }

    public async Task<IReadOnlyList<RegionDto>> ListRegionsAsync(TenantId tenantId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginTenantTransactionAsync(connection, tenantId, cancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT id, name, created_at, hide_prices FROM regions ORDER BY name, id", connection, transaction);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var items = new List<RegionDto>();
        while (await reader.ReadAsync(cancellationToken))
            items.Add(new RegionDto(reader.GetGuid(0), reader.GetString(1), reader.GetFieldValue<DateTimeOffset>(2), reader.GetBoolean(3)));
        return items;
    }

    public async Task<RegionTeamResult> CreateTeamAsync(TenantId tenantId, UserId actorId, Guid teamId, string name, Guid regionId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginTenantTransactionAsync(connection, tenantId, cancellationToken);
        if (!await ExistsAsync(connection, transaction, "regions", regionId, cancellationToken)) return RegionTeamResult.RegionNotFound;

        await using var insert = new NpgsqlCommand("""
            INSERT INTO teams (id, tenant_id, region_id, name) VALUES (@id, @tenant, @region, @name)
            ON CONFLICT (tenant_id, name) DO NOTHING
            """, connection, transaction);
        insert.Parameters.AddWithValue("id", teamId);
        insert.Parameters.AddWithValue("tenant", tenantId.Value);
        insert.Parameters.AddWithValue("region", regionId);
        insert.Parameters.AddWithValue("name", name);
        if (await insert.ExecuteNonQueryAsync(cancellationToken) == 0) return RegionTeamResult.Duplicate;

        await AuditAsync(connection, transaction, tenantId, actorId, "team.created", teamId.ToString(), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return RegionTeamResult.Success;
    }

    public async Task<IReadOnlyList<TeamDto>> ListTeamsAsync(TenantId tenantId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginTenantTransactionAsync(connection, tenantId, cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            SELECT t.id, t.name, t.region_id, t.created_at, l.id, l.email
            FROM teams t
            LEFT JOIN users l ON l.tenant_id = t.tenant_id AND l.id = t.leader_user_id AND l.is_active
                AND EXISTS (SELECT 1 FROM team_members m WHERE m.tenant_id = t.tenant_id AND m.team_id = t.id AND m.user_id = l.id)
                AND EXISTS (SELECT 1 FROM user_roles ur JOIN roles r ON r.tenant_id = ur.tenant_id AND r.id = ur.role_id WHERE ur.tenant_id = t.tenant_id AND ur.user_id = l.id AND r.name = 'Supervisor')
            ORDER BY t.name, t.id
            """, connection, transaction);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var items = new List<TeamDto>();
        while (await reader.ReadAsync(cancellationToken))
            items.Add(new TeamDto(reader.GetGuid(0), reader.GetString(1), reader.GetGuid(2), reader.GetFieldValue<DateTimeOffset>(3),
                reader.IsDBNull(4) ? null : reader.GetGuid(4), reader.IsDBNull(5) ? null : reader.GetString(5)));
        return items;
    }

    public async Task<RegionTeamResult> ChangeTeamRegionAsync(TenantId tenantId, UserId actorId, Guid teamId, Guid regionId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginTenantTransactionAsync(connection, tenantId, cancellationToken);
        if (!await ExistsAsync(connection, transaction, "teams", teamId, cancellationToken)) return RegionTeamResult.NotFound;
        if (!await ExistsAsync(connection, transaction, "regions", regionId, cancellationToken)) return RegionTeamResult.RegionNotFound;

        await using var update = new NpgsqlCommand(
            "UPDATE teams SET region_id = @region WHERE id = @id", connection, transaction);
        update.Parameters.AddWithValue("region", regionId);
        update.Parameters.AddWithValue("id", teamId);
        await update.ExecuteNonQueryAsync(cancellationToken);

        await AuditAsync(connection, transaction, tenantId, actorId, "team.region_changed", teamId.ToString(), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return RegionTeamResult.Success;
    }

    // A user belongs to at most one team: assigning moves the user from any previous team.
    public async Task<RegionTeamResult> AssignMemberAsync(TenantId tenantId, UserId actorId, Guid teamId, Guid userId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginTenantTransactionAsync(connection, tenantId, cancellationToken);
        if (!await ExistsAsync(connection, transaction, "teams", teamId, cancellationToken)) return RegionTeamResult.NotFound;

        // users has no RLS, so the tenant filter is explicit.
        await using (var userCheck = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM users WHERE tenant_id = @tenant AND id = @user AND is_active)", connection, transaction))
        {
            userCheck.Parameters.AddWithValue("tenant", tenantId.Value);
            userCheck.Parameters.AddWithValue("user", userId);
            if (!(bool)(await userCheck.ExecuteScalarAsync(cancellationToken) ?? false)) return RegionTeamResult.UserNotFound;
        }

        await using var upsert = new NpgsqlCommand("""
            INSERT INTO team_members (tenant_id, user_id, team_id) VALUES (@tenant, @user, @team)
            ON CONFLICT (tenant_id, user_id) DO UPDATE SET team_id = EXCLUDED.team_id
            """, connection, transaction);
        upsert.Parameters.AddWithValue("tenant", tenantId.Value);
        upsert.Parameters.AddWithValue("user", userId);
        upsert.Parameters.AddWithValue("team", teamId);
        await upsert.ExecuteNonQueryAsync(cancellationToken);

        await using (var endLead = new NpgsqlCommand("UPDATE teams SET leader_user_id = NULL WHERE tenant_id = @tenant AND leader_user_id = @user AND id <> @team", connection, transaction))
        {
            endLead.Parameters.AddWithValue("tenant", tenantId.Value);
            endLead.Parameters.AddWithValue("user", userId);
            endLead.Parameters.AddWithValue("team", teamId);
            await endLead.ExecuteNonQueryAsync(cancellationToken);
        }
        await AuditAsync(connection, transaction, tenantId, actorId, "team.member_assigned", $"{teamId}:{userId}", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return RegionTeamResult.Success;
    }

    public async Task<RegionTeamResult> SetLeaderAsync(TenantId tenantId, UserId actorId, Guid teamId, Guid? userId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginTenantTransactionAsync(connection, tenantId, cancellationToken);
        if (!await ExistsAsync(connection, transaction, "teams", teamId, cancellationToken)) return RegionTeamResult.NotFound;
        if (userId is { } leader)
        {
            await using var check = new NpgsqlCommand("""
                SELECT EXISTS (
                    SELECT 1 FROM users u
                    JOIN team_members m ON m.tenant_id = u.tenant_id AND m.user_id = u.id AND m.team_id = @team
                    JOIN user_roles ur ON ur.tenant_id = u.tenant_id AND ur.user_id = u.id
                    JOIN roles r ON r.tenant_id = ur.tenant_id AND r.id = ur.role_id AND r.name = 'Supervisor'
                    WHERE u.tenant_id = @tenant AND u.id = @user AND u.is_active)
                """, connection, transaction);
            check.Parameters.AddWithValue("tenant", tenantId.Value);
            check.Parameters.AddWithValue("team", teamId);
            check.Parameters.AddWithValue("user", leader);
            if (!(bool)(await check.ExecuteScalarAsync(cancellationToken) ?? false)) return RegionTeamResult.NotEligible;
        }
        await using (var update = new NpgsqlCommand("UPDATE teams SET leader_user_id = @user WHERE tenant_id = @tenant AND id = @team", connection, transaction))
        {
            update.Parameters.Add(new NpgsqlParameter("user", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)userId ?? DBNull.Value });
            update.Parameters.AddWithValue("tenant", tenantId.Value);
            update.Parameters.AddWithValue("team", teamId);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
        await AuditAsync(connection, transaction, tenantId, actorId, userId is null ? "team.leader_cleared" : "team.leader_set", $"{teamId}:{userId}", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return RegionTeamResult.Success;
    }

    public async Task<RegionTeamResult> RemoveMemberAsync(TenantId tenantId, UserId actorId, Guid teamId, Guid userId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginTenantTransactionAsync(connection, tenantId, cancellationToken);

        await using var delete = new NpgsqlCommand(
            "DELETE FROM team_members WHERE team_id = @team AND user_id = @user", connection, transaction);
        delete.Parameters.AddWithValue("team", teamId);
        delete.Parameters.AddWithValue("user", userId);
        if (await delete.ExecuteNonQueryAsync(cancellationToken) == 0) return RegionTeamResult.NotFound;

        await using (var endLead = new NpgsqlCommand("UPDATE teams SET leader_user_id = NULL WHERE tenant_id = @tenant AND id = @team AND leader_user_id = @user", connection, transaction))
        {
            endLead.Parameters.AddWithValue("tenant", tenantId.Value);
            endLead.Parameters.AddWithValue("team", teamId);
            endLead.Parameters.AddWithValue("user", userId);
            await endLead.ExecuteNonQueryAsync(cancellationToken);
        }
        await AuditAsync(connection, transaction, tenantId, actorId, "team.member_removed", $"{teamId}:{userId}", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return RegionTeamResult.Success;
    }

    private static async Task<NpgsqlTransaction> BeginTenantTransactionAsync(NpgsqlConnection connection, TenantId tenantId, CancellationToken cancellationToken)
    {
        var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var setTenant = new NpgsqlCommand("SELECT set_config('app.current_tenant', @tenant, true)", connection, transaction);
        setTenant.Parameters.AddWithValue("tenant", tenantId.Value.ToString());
        await setTenant.ExecuteNonQueryAsync(cancellationToken);
        return transaction;
    }

    // Table names are compile-time constants from this class, never user input.
    private static async Task<bool> ExistsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string table, Guid id, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"SELECT EXISTS (SELECT 1 FROM {table} WHERE id = @id)", connection, transaction);
        command.Parameters.AddWithValue("id", id);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
    }

    private static async Task AuditAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, TenantId tenantId, UserId actorId, string action, string entityId, CancellationToken cancellationToken)
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
}
