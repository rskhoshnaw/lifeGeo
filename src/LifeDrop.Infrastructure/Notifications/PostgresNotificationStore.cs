using LifeDrop.Application.Notifications;
using LifeDrop.Domain.Common;
using Npgsql;

namespace LifeDrop.Infrastructure.Notifications;

// Reads and marks the messages of one user with the tenant context set for RLS (plan step 1.23-c). The messages are
// created by the changes themselves (the customer store), in the same transaction.
public sealed class PostgresNotificationStore(NpgsqlDataSource dataSource) : INotificationStore
{
    public async Task<IReadOnlyList<NotificationDto>> ListAsync(TenantId tenantId, UserId userId, bool unreadOnly, int limit, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT n.id, n.kind, n.entity_id, n.actor_id, a.email, c.code, c.name, n.created_at, n.read_at
            FROM notifications n
            LEFT JOIN users a ON a.tenant_id = n.tenant_id AND a.id = n.actor_id
            LEFT JOIN customers c ON c.tenant_id = n.tenant_id AND c.id::text = n.entity_id AND n.kind LIKE 'customer.%'
            WHERE n.user_id = @user AND (NOT @unread OR n.read_at IS NULL)
            ORDER BY n.created_at DESC, n.id
            LIMIT @limit
            """, connection, transaction);
        command.Parameters.AddWithValue("user", userId.Value);
        command.Parameters.AddWithValue("unread", unreadOnly);
        command.Parameters.AddWithValue("limit", limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var items = new List<NotificationDto>();
        while (await reader.ReadAsync(cancellationToken))
            items.Add(new NotificationDto(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetGuid(3),
                reader.IsDBNull(4) ? null : reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.GetFieldValue<DateTimeOffset>(7), reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTimeOffset>(8)));
        return items;
    }

    public async Task<int> CountUnreadAsync(TenantId tenantId, UserId userId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, cancellationToken);
        await using var command = new NpgsqlCommand("SELECT count(*)::int FROM notifications WHERE user_id = @user AND read_at IS NULL", connection, transaction);
        command.Parameters.AddWithValue("user", userId.Value);
        return (int)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    public async Task<bool> MarkReadAsync(TenantId tenantId, UserId userId, Guid id, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, cancellationToken);
        await using var command = new NpgsqlCommand("UPDATE notifications SET read_at = COALESCE(read_at, now()) WHERE id = @id AND user_id = @user", connection, transaction);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("user", userId.Value);
        var changed = await command.ExecuteNonQueryAsync(cancellationToken) > 0;
        await transaction.CommitAsync(cancellationToken);
        return changed;
    }

    public async Task<int> MarkAllReadAsync(TenantId tenantId, UserId userId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, cancellationToken);
        await using var command = new NpgsqlCommand("UPDATE notifications SET read_at = now() WHERE user_id = @user AND read_at IS NULL", connection, transaction);
        command.Parameters.AddWithValue("user", userId.Value);
        var marked = await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return marked;
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
