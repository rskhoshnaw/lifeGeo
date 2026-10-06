using LifeDrop.Application.Platform;
using LifeDrop.Domain.Common;
using LifeDrop.Infrastructure.Auth;
using Npgsql;

namespace LifeDrop.Infrastructure.Platform;

// Companies and the platform audit (plan step 3.1). tenants, users and roles have no RLS; audit_logs has none either and is filtered by the
// explicit tenant id. Every write has its platform_audit_logs row in the same transaction.
public sealed class PostgresPlatformStore(NpgsqlDataSource dataSource) : IPlatformStore
{
    public async Task<IReadOnlyList<TenantSummaryDto>> ListTenantsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT t.id, t.code, t.name, t.is_active, count(u.id)::int, (count(u.id) FILTER (WHERE u.is_active))::int
            FROM tenants t LEFT JOIN users u ON u.tenant_id = t.id
            GROUP BY t.id ORDER BY t.code COLLATE "C"
            """, connection);
        var items = new List<TenantSummaryDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            items.Add(new TenantSummaryDto(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetBoolean(3), reader.GetInt32(4), reader.GetInt32(5)));
        return items;
    }

    public async Task<CreatedTenantDto?> CreateTenantAsync(UserId actor, string code, string name, string managerEmail, string managerPassword, CancellationToken cancellationToken)
    {
        var tenantId = Guid.NewGuid();
        var managerId = Guid.NewGuid();
        var hash = PasswordHash.Create(managerPassword);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await using (var insert = new NpgsqlCommand("INSERT INTO tenants (id, name, code, is_active) VALUES (@id, @name, @code, true)", connection, transaction))
            {
                insert.Parameters.AddWithValue("id", tenantId);
                insert.Parameters.AddWithValue("name", name);
                insert.Parameters.AddWithValue("code", code);
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }
            await using (var seed = new NpgsqlCommand("SELECT seed_default_roles(@id)", connection, transaction))
            {
                seed.Parameters.AddWithValue("id", tenantId);
                await seed.ExecuteNonQueryAsync(cancellationToken);
            }
            await using (var manager = new NpgsqlCommand("""
                INSERT INTO users (id, tenant_id, email, password_hash) VALUES (@user, @id, @email, @hash);
                INSERT INTO user_roles (tenant_id, user_id, role_id) SELECT @id, @user, r.id FROM roles r WHERE r.tenant_id = @id AND r.name = 'CompanyManager'
                """, connection, transaction))
            {
                manager.Parameters.AddWithValue("user", managerId);
                manager.Parameters.AddWithValue("id", tenantId);
                manager.Parameters.AddWithValue("email", managerEmail);
                manager.Parameters.AddWithValue("hash", hash);
                await manager.ExecuteNonQueryAsync(cancellationToken);
            }
            await WriteAuditAsync(connection, transaction, actor, "platform.tenant_created", tenantId, null, $"code={code}", cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new CreatedTenantDto(tenantId, code, managerId);
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return null;
        }
    }

    public async Task<bool> UpdateTenantAsync(UserId actor, Guid tenantId, string? name, bool? isActive, string reason, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var update = new NpgsqlCommand("UPDATE tenants SET name = COALESCE(@name, name), is_active = COALESCE(@active, is_active) WHERE id = @id", connection, transaction))
        {
            update.Parameters.AddWithValue("name", (object?)name ?? DBNull.Value);
            update.Parameters.AddWithValue("active", (object?)isActive ?? DBNull.Value);
            update.Parameters.AddWithValue("id", tenantId);
            if (await update.ExecuteNonQueryAsync(cancellationToken) == 0) return false;
        }
        var action = isActive switch { true => "platform.tenant_activated", false => "platform.tenant_deactivated", _ => "platform.tenant_renamed" };
        await WriteAuditAsync(connection, transaction, actor, action, tenantId, reason, name is null ? null : $"name={name}", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<TenantAuditEntryDto>?> ReadTenantAuditAsync(UserId actor, Guid tenantId, string reason, string? action, int limit, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var exists = new NpgsqlCommand("SELECT 1 FROM tenants WHERE id = @id", connection, transaction))
        {
            exists.Parameters.AddWithValue("id", tenantId);
            if (await exists.ExecuteScalarAsync(cancellationToken) is null) return null;
        }
        await WriteAuditAsync(connection, transaction, actor, "platform.tenant_audit_read", tenantId, reason, action is null ? null : $"action={action}", cancellationToken);
        var items = new List<TenantAuditEntryDto>();
        await using (var read = new NpgsqlCommand("""
            SELECT a.id, a.actor_id, u.email, a.action, a.entity_id, a.occurred_at
            FROM audit_logs a LEFT JOIN users u ON u.tenant_id = a.tenant_id AND u.id = a.actor_id
            WHERE a.tenant_id = @id AND (@action::text IS NULL OR a.action = @action)
            ORDER BY a.occurred_at DESC, a.id LIMIT @limit
            """, connection, transaction))
        {
            read.Parameters.AddWithValue("id", tenantId);
            read.Parameters.AddWithValue("action", (object?)action ?? DBNull.Value);
            read.Parameters.AddWithValue("limit", limit);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                items.Add(new TenantAuditEntryDto(reader.GetGuid(0), reader.GetGuid(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetString(3), reader.GetString(4),
                    reader.GetFieldValue<DateTimeOffset>(5)));
        }
        await transaction.CommitAsync(cancellationToken);
        return items;
    }

    public async Task<IReadOnlyList<PlatformAuditEntryDto>> ListPlatformAuditAsync(Guid? tenantId, int limit, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT a.id, a.actor_id, u.email, a.action, a.target_tenant_id, t.code, a.reason, a.detail, a.occurred_at
            FROM platform_audit_logs a
            LEFT JOIN users u ON u.tenant_id = a.actor_tenant_id AND u.id = a.actor_id
            LEFT JOIN tenants t ON t.id = a.target_tenant_id
            WHERE (@tenant::uuid IS NULL OR a.target_tenant_id = @tenant)
            ORDER BY a.occurred_at DESC, a.id LIMIT @limit
            """, connection);
        command.Parameters.AddWithValue("tenant", (object?)tenantId ?? DBNull.Value);
        command.Parameters.AddWithValue("limit", limit);
        var items = new List<PlatformAuditEntryDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            items.Add(new PlatformAuditEntryDto(reader.GetGuid(0), reader.GetGuid(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetGuid(4), reader.IsDBNull(5) ? null : reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7), reader.GetFieldValue<DateTimeOffset>(8)));
        return items;
    }

    private static async Task WriteAuditAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, UserId actor, string action, Guid tenantId, string? reason, string? detail,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO platform_audit_logs (actor_tenant_id, actor_id, action, target_tenant_id, reason, detail)
            VALUES (@platform, @actor, @action, @target, @reason, @detail)
            """, connection, transaction);
        command.Parameters.AddWithValue("platform", PlatformTenant.Id.Value);
        command.Parameters.AddWithValue("actor", actor.Value);
        command.Parameters.AddWithValue("action", action);
        command.Parameters.AddWithValue("target", tenantId);
        command.Parameters.AddWithValue("reason", (object?)reason ?? DBNull.Value);
        command.Parameters.AddWithValue("detail", (object?)detail ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
