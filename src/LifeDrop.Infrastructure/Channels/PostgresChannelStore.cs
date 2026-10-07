using LifeDrop.Application.Channels;
using LifeDrop.Domain.Common;
using Npgsql;
using NpgsqlTypes;

namespace LifeDrop.Infrastructure.Channels;

// Channel links, one-time codes and the channel state of the in-app messages, with the tenant context set for RLS (plan step 3.4).
public sealed class PostgresChannelStore(NpgsqlDataSource dataSource) : IChannelStore
{
    public async Task<bool> HasTelegramAsync(TenantId tenantId, UserId userId, CancellationToken cancellationToken) =>
        await RunAsync(tenantId, cancellationToken, async (connection, transaction) =>
        {
            await using var command = new NpgsqlCommand("SELECT count(*) FROM user_channels WHERE user_id = @user AND channel = 'telegram'", connection, transaction);
            command.Parameters.AddWithValue("user", userId.Value);
            return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
        }) > 0;

    public async Task<string?> GetTenantCodeAsync(TenantId tenantId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("SELECT code FROM tenants WHERE id = @id AND is_active", connection);
        command.Parameters.AddWithValue("id", tenantId.Value);
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    public Task SaveLinkCodeAsync(TenantId tenantId, UserId userId, byte[] codeHash, DateTimeOffset expiresAt, CancellationToken cancellationToken) =>
        RunAsync(tenantId, cancellationToken, async (connection, transaction) =>
        {
            await using var command = new NpgsqlCommand("""
                INSERT INTO channel_link_codes (tenant_id, user_id, channel, code_hash, expires_at) VALUES (@tenant, @user, 'telegram', @hash, @expires)
                ON CONFLICT (tenant_id, user_id, channel) DO UPDATE SET code_hash = EXCLUDED.code_hash, expires_at = EXCLUDED.expires_at
                """, connection, transaction);
            command.Parameters.AddWithValue("tenant", tenantId.Value);
            command.Parameters.AddWithValue("user", userId.Value);
            command.Parameters.AddWithValue("hash", codeHash);
            command.Parameters.Add(new NpgsqlParameter("expires", NpgsqlDbType.TimestampTz) { Value = expiresAt.UtcDateTime });
            return await command.ExecuteNonQueryAsync(cancellationToken);
        });

    public async Task<bool> UnlinkTelegramAsync(TenantId tenantId, UserId userId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, cancellationToken);
        int linked;
        await using (var command = new NpgsqlCommand("DELETE FROM user_channels WHERE user_id = @user AND channel = 'telegram'", connection, transaction))
        {
            command.Parameters.AddWithValue("user", userId.Value);
            linked = await command.ExecuteNonQueryAsync(cancellationToken);
        }
        int waiting;
        await using (var command = new NpgsqlCommand("DELETE FROM channel_link_codes WHERE user_id = @user AND channel = 'telegram'", connection, transaction))
        {
            command.Parameters.AddWithValue("user", userId.Value);
            waiting = await command.ExecuteNonQueryAsync(cancellationToken);
        }
        if (linked > 0) await InsertAuditAsync(connection, transaction, tenantId, userId, "channel.telegram_unlinked", userId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return linked + waiting > 0;
    }

    public async Task<TenantId?> FindTenantAsync(string tenantCode, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("SELECT id FROM tenants WHERE code = @code AND is_active", connection);
        command.Parameters.AddWithValue("code", tenantCode);
        return await command.ExecuteScalarAsync(cancellationToken) is Guid id ? new TenantId(id) : null;
    }

    public async Task<bool> CompleteLinkAsync(TenantId tenantId, byte[] codeHash, string chatId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, cancellationToken);
        Guid? userId;
        await using (var command = new NpgsqlCommand("""
            DELETE FROM channel_link_codes WHERE channel = 'telegram' AND code_hash = @hash AND expires_at > @now RETURNING user_id
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("hash", codeHash);
            command.Parameters.Add(new NpgsqlParameter("now", NpgsqlDbType.TimestampTz) { Value = now.UtcDateTime });
            userId = await command.ExecuteScalarAsync(cancellationToken) as Guid?;
        }
        if (userId is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return false;
        }
        // A chat belongs to one user: linking it again for another user moves it.
        await using (var command = new NpgsqlCommand("DELETE FROM user_channels WHERE channel = 'telegram' AND address = @address AND user_id <> @user", connection, transaction))
        {
            command.Parameters.AddWithValue("address", chatId);
            command.Parameters.AddWithValue("user", userId.Value);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var command = new NpgsqlCommand("""
            INSERT INTO user_channels (tenant_id, user_id, channel, address) VALUES (@tenant, @user, 'telegram', @address)
            ON CONFLICT (tenant_id, user_id, channel) DO UPDATE SET address = EXCLUDED.address, created_at = now()
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("tenant", tenantId.Value);
            command.Parameters.AddWithValue("user", userId.Value);
            command.Parameters.AddWithValue("address", chatId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await InsertAuditAsync(connection, transaction, tenantId, new UserId(userId.Value), "channel.telegram_linked", new UserId(userId.Value), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<PendingChannelMessage>> ListPendingAsync(TenantId tenantId, TimeSpan maxAge, int limit, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT n.id, n.kind, n.entity_id, n.channel_attempts, uc.address, c.name, c.code
            FROM notifications n
            JOIN user_channels uc ON uc.tenant_id = n.tenant_id AND uc.user_id = n.user_id AND uc.channel = 'telegram'
            LEFT JOIN customers c ON c.tenant_id = n.tenant_id AND c.id::text = n.entity_id
            WHERE n.channel_sent_at IS NULL AND n.created_at >= now() - @age
            ORDER BY n.created_at, n.id
            LIMIT @limit
            """, connection, transaction);
        command.Parameters.AddWithValue("age", maxAge);
        command.Parameters.AddWithValue("limit", limit);
        var items = new List<PendingChannelMessage>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            items.Add(new PendingChannelMessage(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3), reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6)));
        return items;
    }

    public Task MarkSentAsync(TenantId tenantId, Guid notificationId, CancellationToken cancellationToken) =>
        Execute(tenantId, "UPDATE notifications SET channel_sent_at = now() WHERE id = @id AND channel_sent_at IS NULL", notificationId, cancellationToken);

    public Task CountAttemptAsync(TenantId tenantId, Guid notificationId, CancellationToken cancellationToken) =>
        Execute(tenantId, "UPDATE notifications SET channel_attempts = channel_attempts + 1 WHERE id = @id AND channel_sent_at IS NULL", notificationId, cancellationToken);

    public Task DropAddressAsync(TenantId tenantId, string chatId, CancellationToken cancellationToken) =>
        RunAsync(tenantId, cancellationToken, async (connection, transaction) =>
        {
            await using var command = new NpgsqlCommand("DELETE FROM user_channels WHERE channel = 'telegram' AND address = @address", connection, transaction);
            command.Parameters.AddWithValue("address", chatId);
            return await command.ExecuteNonQueryAsync(cancellationToken);
        });

    public Task ExpireOldAsync(TenantId tenantId, TimeSpan maxAge, CancellationToken cancellationToken) =>
        RunAsync(tenantId, cancellationToken, async (connection, transaction) =>
        {
            await using var command = new NpgsqlCommand("""
                UPDATE notifications n SET channel_sent_at = now()
                WHERE n.channel_sent_at IS NULL
                  AND (n.created_at < now() - @age
                       OR NOT EXISTS (SELECT 1 FROM user_channels uc WHERE uc.tenant_id = n.tenant_id AND uc.user_id = n.user_id AND uc.channel = 'telegram'))
                """, connection, transaction);
            command.Parameters.AddWithValue("age", maxAge);
            return await command.ExecuteNonQueryAsync(cancellationToken);
        });

    private Task Execute(TenantId tenantId, string sql, Guid id, CancellationToken cancellationToken) =>
        RunAsync(tenantId, cancellationToken, async (connection, transaction) =>
        {
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            command.Parameters.AddWithValue("id", id);
            return await command.ExecuteNonQueryAsync(cancellationToken);
        });

    private async Task<int> RunAsync(TenantId tenantId, CancellationToken cancellationToken, Func<NpgsqlConnection, NpgsqlTransaction, Task<int>> action)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, cancellationToken);
        var changed = await action(connection, transaction);
        await transaction.CommitAsync(cancellationToken);
        return changed;
    }

    private static async Task InsertAuditAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, TenantId tenantId, UserId actorId, string action, UserId entityId,
        CancellationToken cancellationToken)
    {
        await using var audit = new NpgsqlCommand("""
            INSERT INTO audit_logs (id, tenant_id, actor_id, action, entity_id, occurred_at)
            VALUES (gen_random_uuid(), @tenant, @actor, @action, @entity, now())
            """, connection, transaction);
        audit.Parameters.AddWithValue("tenant", tenantId.Value);
        audit.Parameters.AddWithValue("actor", actorId.Value);
        audit.Parameters.AddWithValue("action", action);
        audit.Parameters.AddWithValue("entity", entityId.Value.ToString());
        await audit.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<NpgsqlTransaction> BeginAsync(NpgsqlConnection connection, TenantId tenantId, CancellationToken cancellationToken)
    {
        var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var setTenant = new NpgsqlCommand("SELECT set_config('app.current_tenant', @tenant, true)", connection, transaction);
        setTenant.Parameters.AddWithValue("tenant", tenantId.Value.ToString());
        await setTenant.ExecuteNonQueryAsync(cancellationToken);
        return transaction;
    }
}
