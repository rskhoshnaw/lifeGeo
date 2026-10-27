using LifeDrop.Application.Marketing;
using LifeDrop.Domain.Common;
using Npgsql;

namespace LifeDrop.Infrastructure.Marketing;

// The queue of the WhatsApp Business API sender: messages of "api" campaigns (docs/WHATSAPP-API.md).
public sealed class PostgresWhatsAppStore(NpgsqlDataSource dataSource) : IWhatsAppStore
{
    private static async Task<NpgsqlTransaction> BeginAsync(NpgsqlConnection connection, TenantId tenantId, CancellationToken cancellationToken)
    {
        var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var set = new NpgsqlCommand("SELECT set_config('app.current_tenant', @tenant, true)", connection, transaction);
        set.Parameters.AddWithValue("tenant", tenantId.Value.ToString());
        await set.ExecuteNonQueryAsync(cancellationToken);
        return transaction;
    }

    public async Task<IReadOnlyList<WhatsAppDue>> ClaimDueAsync(TenantId tenantId, int limit, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, cancellationToken);
        // Only customers who still agree are written to; a withdrawn consent stops the message.
        await using var command = new NpgsqlCommand("""
            WITH picked AS (
                SELECT m.id FROM marketing_messages m
                JOIN marketing_campaigns mc ON mc.tenant_id = m.tenant_id AND mc.id = m.campaign_id AND mc.delivery = 'api'
                JOIN customers c ON c.tenant_id = m.tenant_id AND c.id = m.customer_id AND c.whatsapp_opt_in
                WHERE m.tenant_id = @tenant AND m.status = 'queued' AND m.next_attempt_at <= now()
                ORDER BY m.next_attempt_at, m.id
                LIMIT @limit
                FOR UPDATE OF m SKIP LOCKED
            ), claimed AS (
                UPDATE marketing_messages m SET attempts = m.attempts + 1, next_attempt_at = now() + interval '5 minutes'
                FROM picked WHERE m.tenant_id = @tenant AND m.id = picked.id
                RETURNING m.id, m.campaign_id, m.customer_id, m.phone, m.attempts
            )
            SELECT cl.id, cl.phone, c.name, mc.wa_template, mc.wa_language, cl.attempts
            FROM claimed cl
            JOIN marketing_campaigns mc ON mc.tenant_id = @tenant AND mc.id = cl.campaign_id
            JOIN customers c ON c.tenant_id = @tenant AND c.id = cl.customer_id
            ORDER BY cl.id
            """, connection, transaction);
        command.Parameters.AddWithValue("tenant", tenantId.Value);
        command.Parameters.AddWithValue("limit", limit);
        var items = new List<WhatsAppDue>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                items.Add(new WhatsAppDue(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetInt32(5)));
        }
        await transaction.CommitAsync(cancellationToken);
        return items;
    }

    private async Task UpdateAsync(TenantId tenantId, Guid messageId, string sql, Action<NpgsqlCommand> parameters, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("tenant", tenantId.Value);
        command.Parameters.AddWithValue("id", messageId);
        parameters(command);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public Task MarkSentAsync(TenantId tenantId, Guid messageId, string? providerMessageId, CancellationToken cancellationToken) =>
        UpdateAsync(tenantId, messageId, "UPDATE marketing_messages SET status = 'sent', handled_at = now(), provider_message_id = @provider, last_error = NULL WHERE tenant_id = @tenant AND id = @id AND status = 'queued'",
            c => c.Parameters.Add(new NpgsqlParameter("provider", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)providerMessageId ?? DBNull.Value }), cancellationToken);

    public Task MarkRetryAsync(TenantId tenantId, Guid messageId, DateTimeOffset nextAttempt, string error, CancellationToken cancellationToken) =>
        UpdateAsync(tenantId, messageId, "UPDATE marketing_messages SET next_attempt_at = @next, last_error = @error WHERE tenant_id = @tenant AND id = @id AND status = 'queued'",
            c =>
            {
                c.Parameters.AddWithValue("next", nextAttempt);
                c.Parameters.AddWithValue("error", error.Length > 300 ? error[..300] : error);
            }, cancellationToken);

    public Task MarkFailedAsync(TenantId tenantId, Guid messageId, string error, CancellationToken cancellationToken) =>
        UpdateAsync(tenantId, messageId, "UPDATE marketing_messages SET status = 'failed', handled_at = now(), last_error = @error WHERE tenant_id = @tenant AND id = @id AND status = 'queued'",
            c => c.Parameters.AddWithValue("error", error.Length > 300 ? error[..300] : error), cancellationToken);
}
