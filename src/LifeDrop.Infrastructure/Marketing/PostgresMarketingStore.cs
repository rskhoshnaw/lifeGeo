using LifeDrop.Application.Marketing;
using LifeDrop.Application.Roles;
using LifeDrop.Domain.Common;
using Npgsql;

namespace LifeDrop.Infrastructure.Marketing;

public sealed class PostgresMarketingStore(NpgsqlDataSource dataSource) : IMarketingStore
{
    private static async Task<NpgsqlTransaction> BeginAsync(NpgsqlConnection connection, TenantId tenantId, CancellationToken cancellationToken)
    {
        var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var set = new NpgsqlCommand("SELECT set_config('app.current_tenant', @tenant, true)", connection, transaction);
        set.Parameters.AddWithValue("tenant", tenantId.Value.ToString());
        await set.ExecuteNonQueryAsync(cancellationToken);
        return transaction;
    }

    private static async Task AuditAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, TenantId tenantId, UserId actorId, string action, Guid entityId, CancellationToken cancellationToken)
    {
        await using var audit = new NpgsqlCommand("INSERT INTO audit_logs (id, tenant_id, actor_id, action, entity_id, occurred_at) VALUES (gen_random_uuid(), @tenant, @actor, @action, @entity, now())",
            connection, transaction);
        audit.Parameters.AddWithValue("tenant", tenantId.Value);
        audit.Parameters.AddWithValue("actor", actorId.Value);
        audit.Parameters.AddWithValue("action", action);
        audit.Parameters.AddWithValue("entity", entityId);
        await audit.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<TemplateDto>> ListTemplatesAsync(TenantId tenantId, bool includeInactive, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, cancellationToken);
        await using var command = new NpgsqlCommand($"SELECT id, name, body, is_active FROM marketing_templates WHERE tenant_id = @tenant{(includeInactive ? "" : " AND is_active")} ORDER BY name COLLATE \"C\", id",
            connection, transaction);
        command.Parameters.AddWithValue("tenant", tenantId.Value);
        var items = new List<TemplateDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) items.Add(new TemplateDto(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetBoolean(3)));
        return items;
    }

    public async Task<Guid> CreateTemplateAsync(TenantId tenantId, UserId actorId, string name, string body, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, cancellationToken);
        Guid id;
        await using (var insert = new NpgsqlCommand("INSERT INTO marketing_templates (tenant_id, name, body, created_by) VALUES (@tenant, @name, @body, @actor) RETURNING id", connection, transaction))
        {
            insert.Parameters.AddWithValue("tenant", tenantId.Value);
            insert.Parameters.AddWithValue("name", name);
            insert.Parameters.AddWithValue("body", body);
            insert.Parameters.AddWithValue("actor", actorId.Value);
            id = (Guid)(await insert.ExecuteScalarAsync(cancellationToken))!;
        }
        await AuditAsync(connection, transaction, tenantId, actorId, "marketing.template_saved", id, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return id;
    }

    public async Task<bool> UpdateTemplateAsync(TenantId tenantId, UserId actorId, Guid id, string name, string body, bool isActive, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, cancellationToken);
        await using (var update = new NpgsqlCommand("UPDATE marketing_templates SET name = @name, body = @body, is_active = @active, updated_at = now() WHERE tenant_id = @tenant AND id = @id", connection, transaction))
        {
            update.Parameters.AddWithValue("tenant", tenantId.Value);
            update.Parameters.AddWithValue("id", id);
            update.Parameters.AddWithValue("name", name);
            update.Parameters.AddWithValue("body", body);
            update.Parameters.AddWithValue("active", isActive);
            if (await update.ExecuteNonQueryAsync(cancellationToken) == 0) return false;
        }
        await AuditAsync(connection, transaction, tenantId, actorId, "marketing.template_saved", id, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    // Data scope of customers.read (plan step 0.6b / 4.35), on the alias c.
    private static string CustomerScope(AccessScope scope) => (scope.Scope switch
    {
        DataScope.Tenant => "TRUE",
        DataScope.Region when scope.RegionId is not null => "(c.owner_user_id = @scopeActor OR c.region_id = @scopeRegion)",
        _ => "c.owner_user_id = @scopeActor"
    }) + scope.DoctorFilter("c.type");

    public async Task<(MarketingResult Result, CampaignCreatedDto? Campaign)> CreateCampaignAsync(TenantId tenantId, UserId actorId, AccessScope customerScope, string name, Guid templateId,
        IReadOnlyList<string> customerTypes, Guid? placeId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, cancellationToken);
        string? body;
        await using (var template = new NpgsqlCommand("SELECT body FROM marketing_templates WHERE tenant_id = @tenant AND id = @id AND is_active", connection, transaction))
        {
            template.Parameters.AddWithValue("tenant", tenantId.Value);
            template.Parameters.AddWithValue("id", templateId);
            body = await template.ExecuteScalarAsync(cancellationToken) as string;
        }
        if (body is null) return (MarketingResult.NotFound, null);
        if (placeId is { } place)
        {
            await using var exists = new NpgsqlCommand("SELECT 1 FROM places WHERE tenant_id = @tenant AND id = @id", connection, transaction);
            exists.Parameters.AddWithValue("tenant", tenantId.Value);
            exists.Parameters.AddWithValue("id", place);
            if (await exists.ExecuteScalarAsync(cancellationToken) is null) return (MarketingResult.NotFound, null);
        }

        Guid campaignId;
        await using (var insert = new NpgsqlCommand("INSERT INTO marketing_campaigns (tenant_id, name, body, created_by) VALUES (@tenant, @name, @body, @actor) RETURNING id", connection, transaction))
        {
            insert.Parameters.AddWithValue("tenant", tenantId.Value);
            insert.Parameters.AddWithValue("name", name);
            insert.Parameters.AddWithValue("body", body);
            insert.Parameters.AddWithValue("actor", actorId.Value);
            campaignId = (Guid)(await insert.ExecuteScalarAsync(cancellationToken))!;
        }

        // Approved, not archived customers inside the scope; the digits of the phone decide whether it is usable (8 to 15 digits) and whether it was taken already (last 9 digits).
        await using var queue = new NpgsqlCommand($"""
            WITH eligible AS (
                SELECT c.id, c.name, c.phone, coalesce(regexp_replace(c.phone, '[^0-9]', '', 'g'), '') AS digits
                FROM customers c
                WHERE c.tenant_id = @tenant AND c.approval_status = 'approved' AND c.archived_at IS NULL AND {CustomerScope(customerScope)}
                  {(customerTypes.Count > 0 ? "AND lower(c.type) = ANY(@types)" : "")}
                  {(placeId is not null ? "AND c.place_id IN (WITH RECURSIVE below AS (SELECT id FROM places WHERE id = @place UNION ALL SELECT p.id FROM places p JOIN below b ON p.parent_id = b.id) SELECT id FROM below)" : "")}
            ), usable AS (
                SELECT DISTINCT ON (right(digits, 9)) id, name, phone FROM eligible WHERE length(digits) BETWEEN 8 AND 15 ORDER BY right(digits, 9), id
            ), written AS (
                INSERT INTO marketing_messages (tenant_id, campaign_id, customer_id, phone, message)
                SELECT @tenant, @campaign, u.id, u.phone, replace(@body, @placeholder, u.name) FROM usable u
                RETURNING 1
            )
            SELECT (SELECT count(*) FROM written)::int, (SELECT count(*) FROM eligible WHERE length(digits) NOT BETWEEN 8 AND 15)::int
            """, connection, transaction);
        queue.Parameters.AddWithValue("tenant", tenantId.Value);
        queue.Parameters.AddWithValue("campaign", campaignId);
        queue.Parameters.AddWithValue("body", body);
        queue.Parameters.AddWithValue("placeholder", "{name}");
        queue.Parameters.AddWithValue("scopeActor", customerScope.ActorId.Value);
        queue.Parameters.AddWithValue("scopeRegion", (object?)customerScope.RegionId ?? DBNull.Value);
        if (customerTypes.Count > 0) queue.Parameters.AddWithValue("types", customerTypes.ToArray());
        if (placeId is { } p) queue.Parameters.AddWithValue("place", p);
        int queued, skipped;
        await using (var reader = await queue.ExecuteReaderAsync(cancellationToken))
        {
            await reader.ReadAsync(cancellationToken);
            (queued, skipped) = (reader.GetInt32(0), reader.GetInt32(1));
        }
        await AuditAsync(connection, transaction, tenantId, actorId, "marketing.campaign_created", campaignId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (MarketingResult.Success, new CampaignCreatedDto(campaignId, queued, skipped));
    }

    private static string CampaignScope(AccessScope scope) => scope.Scope == DataScope.Tenant ? "TRUE" : "mc.created_by = @scopeActor";

    public async Task<IReadOnlyList<CampaignDto>> ListCampaignsAsync(TenantId tenantId, AccessScope scope, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, cancellationToken);
        await using var command = new NpgsqlCommand($"""
            SELECT mc.id, mc.name, mc.created_at, u.email,
                   count(*) FILTER (WHERE m.status = 'queued')::int, count(*) FILTER (WHERE m.status = 'sent')::int, count(*) FILTER (WHERE m.status = 'skipped')::int
            FROM marketing_campaigns mc
            LEFT JOIN users u ON u.tenant_id = mc.tenant_id AND u.id = mc.created_by
            LEFT JOIN marketing_messages m ON m.tenant_id = mc.tenant_id AND m.campaign_id = mc.id
            WHERE mc.tenant_id = @tenant AND {CampaignScope(scope)}
            GROUP BY mc.id, mc.name, mc.created_at, u.email
            ORDER BY mc.created_at DESC, mc.id
            LIMIT 100
            """, connection, transaction);
        command.Parameters.AddWithValue("tenant", tenantId.Value);
        command.Parameters.AddWithValue("scopeActor", scope.ActorId.Value);
        var items = new List<CampaignDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            items.Add(new CampaignDto(reader.GetGuid(0), reader.GetString(1), reader.GetFieldValue<DateTimeOffset>(2), reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetInt32(4), reader.GetInt32(5), reader.GetInt32(6)));
        return items;
    }

    public async Task<IReadOnlyList<MarketingMessageDto>?> ListMessagesAsync(TenantId tenantId, AccessScope scope, Guid campaignId, string? status, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, cancellationToken);
        await using (var visible = new NpgsqlCommand($"SELECT 1 FROM marketing_campaigns mc WHERE mc.tenant_id = @tenant AND mc.id = @campaign AND {CampaignScope(scope)}", connection, transaction))
        {
            visible.Parameters.AddWithValue("tenant", tenantId.Value);
            visible.Parameters.AddWithValue("campaign", campaignId);
            visible.Parameters.AddWithValue("scopeActor", scope.ActorId.Value);
            if (await visible.ExecuteScalarAsync(cancellationToken) is null) return null;
        }
        await using var command = new NpgsqlCommand($"""
            SELECT m.id, m.customer_id, c.name, m.phone, m.message, m.status
            FROM marketing_messages m
            JOIN customers c ON c.tenant_id = m.tenant_id AND c.id = m.customer_id
            WHERE m.tenant_id = @tenant AND m.campaign_id = @campaign{(string.IsNullOrEmpty(status) ? "" : " AND m.status = @status")}
            ORDER BY (m.status = 'queued') DESC, c.name COLLATE "C", m.id
            """, connection, transaction);
        command.Parameters.AddWithValue("tenant", tenantId.Value);
        command.Parameters.AddWithValue("campaign", campaignId);
        if (!string.IsNullOrEmpty(status)) command.Parameters.AddWithValue("status", status);
        var items = new List<MarketingMessageDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) items.Add(new MarketingMessageDto(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5)));
        return items;
    }

    public async Task<MarketingResult> HandleAsync(TenantId tenantId, AccessScope scope, Guid messageId, string newStatus, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, cancellationToken);
        string? current;
        await using (var find = new NpgsqlCommand($"""
            SELECT m.status FROM marketing_messages m JOIN marketing_campaigns mc ON mc.tenant_id = m.tenant_id AND mc.id = m.campaign_id
            WHERE m.tenant_id = @tenant AND m.id = @id AND {CampaignScope(scope)} FOR UPDATE OF m
            """, connection, transaction))
        {
            find.Parameters.AddWithValue("tenant", tenantId.Value);
            find.Parameters.AddWithValue("id", messageId);
            find.Parameters.AddWithValue("scopeActor", scope.ActorId.Value);
            current = await find.ExecuteScalarAsync(cancellationToken) as string;
        }
        if (current is null) return MarketingResult.NotFound;
        if (current != "queued") return MarketingResult.Conflict;
        await using var update = new NpgsqlCommand("UPDATE marketing_messages SET status = @status, handled_at = now(), handled_by = @actor WHERE tenant_id = @tenant AND id = @id", connection, transaction);
        update.Parameters.AddWithValue("tenant", tenantId.Value);
        update.Parameters.AddWithValue("id", messageId);
        update.Parameters.AddWithValue("status", newStatus);
        update.Parameters.AddWithValue("actor", scope.ActorId.Value);
        await update.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return MarketingResult.Success;
    }
}
