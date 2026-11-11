using LifeDrop.Application.Feedback;
using LifeDrop.Application.Roles;
using LifeDrop.Domain.Common;
using Npgsql;
using NpgsqlTypes;

namespace LifeDrop.Infrastructure.Feedback;

// Feedback from the field (plan step 5.30). Every statement names the company (row level security) and limits what it reads or changes to the caller's access: their own
// feedback, plus the data scope of feedback.manage (the whole company, or the feedback written from the own region).
public sealed class PostgresFeedbackStore(NpgsqlDataSource dataSource) : IFeedbackStore
{
    private static async Task<NpgsqlTransaction> BeginAsync(NpgsqlConnection connection, TenantId tenantId, CancellationToken cancellationToken)
    {
        var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var set = new NpgsqlCommand("SELECT set_config('app.current_tenant', @tenant, true)", connection, transaction);
        set.Parameters.AddWithValue("tenant", tenantId.Value.ToString());
        await set.ExecuteNonQueryAsync(cancellationToken);
        return transaction;
    }

    // The feedback a manager's scope reaches (not the manager's own: that is added where it applies).
    private static string ManageFilter(AccessScope? manage) => manage switch
    {
        { Scope: DataScope.Tenant } => "TRUE",
        { Scope: DataScope.Region, RegionId: not null } => "f.region_id = @scopeRegion",
        _ => "FALSE"
    };

    private static void AddAccess(NpgsqlCommand command, TenantId tenantId, UserId actor, AccessScope? manage)
    {
        command.Parameters.AddWithValue("tenant", tenantId.Value);
        command.Parameters.AddWithValue("actor", actor.Value);
        command.Parameters.Add(new NpgsqlParameter("scopeRegion", NpgsqlDbType.Uuid) { Value = (object?)manage?.RegionId ?? DBNull.Value });
    }

    public async Task<Guid?> CreateAsync(TenantId tenantId, UserId author, string kind, string body, Guid? customerId, AccessScope? customerScope, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, cancellationToken);

        if (customerId is not null && customerScope is not null)
        {
            var scope = (customerScope.Scope switch
            {
                DataScope.Tenant => "TRUE",
                DataScope.Region when customerScope.RegionId is not null => "(c.owner_user_id = @actor OR c.region_id = @scopeRegion)",
                _ => "c.owner_user_id = @actor"
            }) + customerScope.DoctorFilter("c.type");
            await using var check = new NpgsqlCommand($"SELECT 1 FROM customers c WHERE c.tenant_id = @tenant AND c.id = @customer AND c.archived_at IS NULL AND {scope}", connection, transaction);
            AddAccess(check, tenantId, author, customerScope);
            check.Parameters.AddWithValue("customer", customerId.Value);
            if (await check.ExecuteScalarAsync(cancellationToken) is null) return null;
        }

        Guid? region;
        await using (var regionCommand = new NpgsqlCommand("""
            SELECT t.region_id FROM team_members tm JOIN teams t ON t.tenant_id = tm.tenant_id AND t.id = tm.team_id
            WHERE tm.tenant_id = @tenant AND tm.user_id = @actor LIMIT 1
            """, connection, transaction))
        {
            regionCommand.Parameters.AddWithValue("tenant", tenantId.Value);
            regionCommand.Parameters.AddWithValue("actor", author.Value);
            region = await regionCommand.ExecuteScalarAsync(cancellationToken) as Guid?;
        }

        var id = Guid.NewGuid();
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO feedback (id, tenant_id, user_id, kind, body, customer_id, region_id) VALUES (@id, @tenant, @actor, @kind, @body, @customer, @region)
            """, connection, transaction))
        {
            insert.Parameters.AddWithValue("id", id);
            insert.Parameters.AddWithValue("tenant", tenantId.Value);
            insert.Parameters.AddWithValue("actor", author.Value);
            insert.Parameters.AddWithValue("kind", kind);
            insert.Parameters.AddWithValue("body", body);
            insert.Parameters.Add(new NpgsqlParameter("customer", NpgsqlDbType.Uuid) { Value = (object?)customerId ?? DBNull.Value });
            insert.Parameters.Add(new NpgsqlParameter("region", NpgsqlDbType.Uuid) { Value = (object?)region ?? DBNull.Value });
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        // The managers whose scope reaches the author: the whole-company ones, and the supervisors of the author's region.
        await using (var notify = new NpgsqlCommand("""
            INSERT INTO notifications (tenant_id, user_id, kind, entity_id, actor_id)
            SELECT DISTINCT @tenant, ur.user_id, 'feedback.new', @entity, @actor
            FROM user_roles ur
            JOIN roles r ON r.tenant_id = ur.tenant_id AND r.id = ur.role_id
            JOIN role_permissions rp ON rp.tenant_id = r.tenant_id AND rp.role_id = r.id AND rp.permission_key = 'feedback.manage'
            JOIN users u ON u.tenant_id = ur.tenant_id AND u.id = ur.user_id AND u.is_active
            WHERE ur.tenant_id = @tenant AND ur.user_id <> @actor
              AND (r.data_scope = 'tenant'
                   OR (r.data_scope = 'region' AND @region::uuid IS NOT NULL AND EXISTS (
                        SELECT 1 FROM team_members tm JOIN teams t ON t.tenant_id = tm.tenant_id AND t.id = tm.team_id
                        WHERE tm.tenant_id = ur.tenant_id AND tm.user_id = ur.user_id AND t.region_id = @region)))
            """, connection, transaction))
        {
            notify.Parameters.AddWithValue("tenant", tenantId.Value);
            notify.Parameters.AddWithValue("actor", author.Value);
            notify.Parameters.AddWithValue("entity", id.ToString());
            notify.Parameters.Add(new NpgsqlParameter("region", NpgsqlDbType.Uuid) { Value = (object?)region ?? DBNull.Value });
            await notify.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return id;
    }

    private const string Columns = """
        f.id, f.kind, f.body, f.status, f.created_at, f.updated_at, f.user_id, u.email, f.customer_id, c.code, c.name
        """;

    private const string Joins = """
        FROM feedback f
        JOIN users u ON u.tenant_id = f.tenant_id AND u.id = f.user_id
        LEFT JOIN customers c ON c.tenant_id = f.tenant_id AND c.id = f.customer_id
        """;

    public async Task<IReadOnlyList<FeedbackDto>> ListAsync(TenantId tenantId, FeedbackAccess access, FeedbackFilter filter, CancellationToken cancellationToken)
    {
        var reach = filter.MineOnly ? "f.user_id = @actor" : $"(f.user_id = @actor OR {ManageFilter(access.Manage)})";
        var sql = $"SELECT {Columns} {Joins} WHERE f.tenant_id = @tenant AND {reach}";
        if (filter.Status is not null) sql += " AND f.status = @status";
        if (filter.Kind is not null) sql += " AND f.kind = @kind";
        if (filter.UserId is not null) sql += " AND f.user_id = @user";
        if (filter.CustomerId is not null) sql += " AND f.customer_id = @customer";
        if (filter.Before is not null) sql += " AND f.created_at < @before";
        sql += " ORDER BY f.created_at DESC, f.id LIMIT @limit";

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        AddAccess(command, tenantId, access.ActorId, access.Manage);
        command.Parameters.AddWithValue("limit", filter.Limit);
        if (filter.Status is not null) command.Parameters.AddWithValue("status", filter.Status);
        if (filter.Kind is not null) command.Parameters.AddWithValue("kind", filter.Kind);
        if (filter.UserId is not null) command.Parameters.AddWithValue("user", filter.UserId.Value);
        if (filter.CustomerId is not null) command.Parameters.AddWithValue("customer", filter.CustomerId.Value);
        if (filter.Before is not null) command.Parameters.AddWithValue("before", filter.Before.Value);
        var rows = new List<FeedbackRow>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken)) rows.Add(Read(reader));
        return await WithDetailsAsync(connection, transaction, tenantId, rows, cancellationToken);
    }

    public async Task<FeedbackDto?> GetAsync(TenantId tenantId, FeedbackAccess access, Guid id, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, cancellationToken);
        await using var command = new NpgsqlCommand($"SELECT {Columns} {Joins} WHERE f.tenant_id = @tenant AND f.id = @id AND (f.user_id = @actor OR {ManageFilter(access.Manage)})", connection, transaction);
        AddAccess(command, tenantId, access.ActorId, access.Manage);
        command.Parameters.AddWithValue("id", id);
        var rows = new List<FeedbackRow>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken)) rows.Add(Read(reader));
        return (await WithDetailsAsync(connection, transaction, tenantId, rows, cancellationToken)).FirstOrDefault();
    }

    private sealed record FeedbackRow(Guid Id, string Kind, string Body, string Status, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, Guid UserId, string? UserEmail, Guid? CustomerId, string? CustomerCode, string? CustomerName);

    private static FeedbackRow Read(NpgsqlDataReader r) => new(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetFieldValue<DateTimeOffset>(4), r.GetFieldValue<DateTimeOffset>(5), r.GetGuid(6),
        r.IsDBNull(7) ? null : r.GetString(7), r.IsDBNull(8) ? null : r.GetGuid(8), r.IsDBNull(9) ? null : r.GetString(9), r.IsDBNull(10) ? null : r.GetString(10));

    // The replies (oldest first) and the photo ids of the feedback found.
    private static async Task<IReadOnlyList<FeedbackDto>> WithDetailsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, TenantId tenantId, List<FeedbackRow> rows, CancellationToken cancellationToken)
    {
        if (rows.Count == 0) return [];
        var ids = rows.Select(r => r.Id).ToArray();
        var replies = new Dictionary<Guid, List<FeedbackReplyDto>>();
        await using (var command = new NpgsqlCommand("""
            SELECT p.feedback_id, p.id, p.user_id, u.email, p.body, p.created_at
            FROM feedback_replies p JOIN users u ON u.tenant_id = p.tenant_id AND u.id = p.user_id
            WHERE p.tenant_id = @tenant AND p.feedback_id = ANY(@ids) ORDER BY p.created_at, p.id
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("tenant", tenantId.Value);
            command.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = ids });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var feedbackId = reader.GetGuid(0);
                if (!replies.TryGetValue(feedbackId, out var list)) replies[feedbackId] = list = [];
                list.Add(new FeedbackReplyDto(reader.GetGuid(1), reader.GetGuid(2), reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetString(4), reader.GetFieldValue<DateTimeOffset>(5)));
            }
        }
        var photos = new Dictionary<Guid, List<Guid>>();
        await using (var command = new NpgsqlCommand("SELECT feedback_id, id FROM feedback_photos WHERE tenant_id = @tenant AND feedback_id = ANY(@ids) ORDER BY created_at, id", connection, transaction))
        {
            command.Parameters.AddWithValue("tenant", tenantId.Value);
            command.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = ids });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var feedbackId = reader.GetGuid(0);
                if (!photos.TryGetValue(feedbackId, out var list)) photos[feedbackId] = list = [];
                list.Add(reader.GetGuid(1));
            }
        }
        return rows.Select(r => new FeedbackDto(r.Id, r.Kind, r.Body, r.Status, r.CreatedAt, r.UpdatedAt, r.UserId, r.UserEmail, r.CustomerId, r.CustomerCode, r.CustomerName,
            photos.GetValueOrDefault(r.Id) ?? [], replies.GetValueOrDefault(r.Id) ?? [])).ToList();
    }

    public async Task<FeedbackResult> AddPhotoAsync(TenantId tenantId, UserId author, Guid id, string contentType, byte[] data, int maxPhotos, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, cancellationToken);
        // The row is locked, so two uploads at once cannot both pass the count.
        await using (var own = new NpgsqlCommand("SELECT 1 FROM feedback WHERE tenant_id = @tenant AND id = @id AND user_id = @actor FOR UPDATE", connection, transaction))
        {
            own.Parameters.AddWithValue("tenant", tenantId.Value);
            own.Parameters.AddWithValue("id", id);
            own.Parameters.AddWithValue("actor", author.Value);
            if (await own.ExecuteScalarAsync(cancellationToken) is null) return FeedbackResult.NotFound;
        }
        await using (var count = new NpgsqlCommand("SELECT count(*) FROM feedback_photos WHERE tenant_id = @tenant AND feedback_id = @id", connection, transaction))
        {
            count.Parameters.AddWithValue("tenant", tenantId.Value);
            count.Parameters.AddWithValue("id", id);
            if (Convert.ToInt32(await count.ExecuteScalarAsync(cancellationToken)) >= maxPhotos) return FeedbackResult.TooManyPhotos;
        }
        await using (var insert = new NpgsqlCommand("INSERT INTO feedback_photos (tenant_id, feedback_id, content_type, data) VALUES (@tenant, @id, @type, @data)", connection, transaction))
        {
            insert.Parameters.AddWithValue("tenant", tenantId.Value);
            insert.Parameters.AddWithValue("id", id);
            insert.Parameters.AddWithValue("type", contentType);
            insert.Parameters.Add(new NpgsqlParameter("data", NpgsqlDbType.Bytea) { Value = data });
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return FeedbackResult.Success;
    }

    public async Task<FeedbackPhoto?> GetPhotoAsync(TenantId tenantId, FeedbackAccess access, Guid id, Guid photoId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, cancellationToken);
        await using var command = new NpgsqlCommand($"""
            SELECT p.content_type, p.data FROM feedback_photos p JOIN feedback f ON f.tenant_id = p.tenant_id AND f.id = p.feedback_id
            WHERE p.tenant_id = @tenant AND p.id = @photo AND p.feedback_id = @id AND (f.user_id = @actor OR {ManageFilter(access.Manage)})
            """, connection, transaction);
        AddAccess(command, tenantId, access.ActorId, access.Manage);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("photo", photoId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? new FeedbackPhoto(reader.GetString(0), reader.GetFieldValue<byte[]>(1)) : null;
    }

    public async Task<bool> AddReplyAsync(TenantId tenantId, UserId actor, AccessScope manage, Guid id, string body, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, cancellationToken);
        Guid author;
        await using (var find = new NpgsqlCommand($"SELECT f.user_id FROM feedback f WHERE f.tenant_id = @tenant AND f.id = @id AND {ManageFilter(manage)} FOR UPDATE", connection, transaction))
        {
            AddAccess(find, tenantId, actor, manage);
            find.Parameters.AddWithValue("id", id);
            if (await find.ExecuteScalarAsync(cancellationToken) is not Guid found) return false;
            author = found;
        }
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO feedback_replies (tenant_id, feedback_id, user_id, body) VALUES (@tenant, @id, @actor, @body);
            UPDATE feedback SET status = CASE WHEN status = 'new' THEN 'in_review' ELSE status END, updated_at = now() WHERE tenant_id = @tenant AND id = @id;
            INSERT INTO audit_logs (id, tenant_id, actor_id, action, entity_id, occurred_at) VALUES (gen_random_uuid(), @tenant, @actor, 'feedback.replied', @id::text, now());
            """, connection, transaction))
        {
            insert.Parameters.AddWithValue("tenant", tenantId.Value);
            insert.Parameters.AddWithValue("id", id);
            insert.Parameters.AddWithValue("actor", actor.Value);
            insert.Parameters.AddWithValue("body", body);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        if (author != actor.Value)
        {
            await using var notify = new NpgsqlCommand("INSERT INTO notifications (tenant_id, user_id, kind, entity_id, actor_id) VALUES (@tenant, @author, 'feedback.reply', @entity, @actor)", connection, transaction);
            notify.Parameters.AddWithValue("tenant", tenantId.Value);
            notify.Parameters.AddWithValue("author", author);
            notify.Parameters.AddWithValue("entity", id.ToString());
            notify.Parameters.AddWithValue("actor", actor.Value);
            await notify.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<bool> SetStatusAsync(TenantId tenantId, UserId actor, AccessScope manage, Guid id, string status, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, cancellationToken);
        await using var command = new NpgsqlCommand($"""
            WITH changed AS (
                UPDATE feedback f SET status = @status, updated_at = now()
                WHERE f.tenant_id = @tenant AND f.id = @id AND f.status <> @status AND {ManageFilter(manage)}
                RETURNING f.id
            ),
            logged AS (
                INSERT INTO audit_logs (id, tenant_id, actor_id, action, entity_id, occurred_at)
                SELECT gen_random_uuid(), @tenant, @actor, 'feedback.status_changed', changed.id::text, now() FROM changed
                RETURNING 1
            )
            SELECT (SELECT count(*) FROM changed) > 0 OR EXISTS (SELECT 1 FROM feedback f WHERE f.tenant_id = @tenant AND f.id = @id AND f.status = @status AND {ManageFilter(manage)})
            """, connection, transaction);
        AddAccess(command, tenantId, actor, manage);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("status", status);
        var found = (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
        await transaction.CommitAsync(cancellationToken);
        return found;
    }
}
