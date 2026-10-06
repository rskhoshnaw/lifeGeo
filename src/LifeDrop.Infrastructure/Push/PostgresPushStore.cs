using LifeDrop.Application.Push;
using LifeDrop.Domain.Common;
using Npgsql;

namespace LifeDrop.Infrastructure.Push;

// Push tokens and the push state of the in-app messages, with the tenant context set for RLS (plan step 2.14).
public sealed class PostgresPushStore(NpgsqlDataSource dataSource) : IPushStore
{
    public Task RegisterTokenAsync(TenantId tenantId, UserId userId, string token, string platform, CancellationToken cancellationToken) =>
        RunAsync(tenantId, cancellationToken, async (connection, transaction) =>
        {
            await using var command = new NpgsqlCommand("""
                INSERT INTO push_tokens (tenant_id, user_id, token, platform) VALUES (@tenant, @user, @token, @platform)
                ON CONFLICT (tenant_id, token) DO UPDATE SET user_id = EXCLUDED.user_id, platform = EXCLUDED.platform, updated_at = now()
                """, connection, transaction);
            command.Parameters.AddWithValue("tenant", tenantId.Value);
            command.Parameters.AddWithValue("user", userId.Value);
            command.Parameters.AddWithValue("token", token);
            command.Parameters.AddWithValue("platform", platform);
            return await command.ExecuteNonQueryAsync(cancellationToken);
        });

    public async Task<bool> RemoveTokenAsync(TenantId tenantId, UserId userId, string token, CancellationToken cancellationToken) =>
        await RunAsync(tenantId, cancellationToken, async (connection, transaction) =>
        {
            await using var command = new NpgsqlCommand("DELETE FROM push_tokens WHERE token = @token AND user_id = @user", connection, transaction);
            command.Parameters.AddWithValue("token", token);
            command.Parameters.AddWithValue("user", userId.Value);
            return await command.ExecuteNonQueryAsync(cancellationToken);
        }) > 0;

    public async Task<IReadOnlyList<PendingPush>> ListPendingAsync(TenantId tenantId, TimeSpan maxAge, int limit, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT n.id, n.user_id, n.kind, n.entity_id, n.push_attempts,
                   COALESCE(array_agg(t.token ORDER BY t.token COLLATE "C") FILTER (WHERE t.token IS NOT NULL), ARRAY[]::text[])
            FROM notifications n
            LEFT JOIN push_tokens t ON t.tenant_id = n.tenant_id AND t.user_id = n.user_id
            WHERE n.pushed_at IS NULL AND n.created_at >= now() - @age
            GROUP BY n.id
            ORDER BY n.created_at, n.id
            LIMIT @limit
            """, connection, transaction);
        command.Parameters.AddWithValue("age", maxAge);
        command.Parameters.AddWithValue("limit", limit);
        var items = new List<PendingPush>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            items.Add(new PendingPush(reader.GetGuid(0), new UserId(reader.GetGuid(1)), reader.GetString(2), reader.GetString(3), reader.GetInt32(4), reader.GetFieldValue<string[]>(5)));
        return items;
    }

    public Task MarkPushedAsync(TenantId tenantId, Guid notificationId, CancellationToken cancellationToken) =>
        Execute(tenantId, "UPDATE notifications SET pushed_at = now() WHERE id = @id AND pushed_at IS NULL", notificationId, cancellationToken);

    public Task CountAttemptAsync(TenantId tenantId, Guid notificationId, CancellationToken cancellationToken) =>
        Execute(tenantId, "UPDATE notifications SET push_attempts = push_attempts + 1 WHERE id = @id AND pushed_at IS NULL", notificationId, cancellationToken);

    public Task DropTokenAsync(TenantId tenantId, string token, CancellationToken cancellationToken) =>
        RunAsync(tenantId, cancellationToken, async (connection, transaction) =>
        {
            await using var command = new NpgsqlCommand("DELETE FROM push_tokens WHERE token = @token", connection, transaction);
            command.Parameters.AddWithValue("token", token);
            return await command.ExecuteNonQueryAsync(cancellationToken);
        });

    public Task ExpireOldAsync(TenantId tenantId, TimeSpan maxAge, CancellationToken cancellationToken) =>
        RunAsync(tenantId, cancellationToken, async (connection, transaction) =>
        {
            await using var command = new NpgsqlCommand("UPDATE notifications SET pushed_at = now() WHERE pushed_at IS NULL AND created_at < now() - @age", connection, transaction);
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

    private static async Task<NpgsqlTransaction> BeginAsync(NpgsqlConnection connection, TenantId tenantId, CancellationToken cancellationToken)
    {
        var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var setTenant = new NpgsqlCommand("SELECT set_config('app.current_tenant', @tenant, true)", connection, transaction);
        setTenant.Parameters.AddWithValue("tenant", tenantId.Value.ToString());
        await setTenant.ExecuteNonQueryAsync(cancellationToken);
        return transaction;
    }
}
