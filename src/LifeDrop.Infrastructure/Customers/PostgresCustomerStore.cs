using System.Collections.Generic;
using LifeDrop.Application.Customers;
using LifeDrop.Application.Roles;
using LifeDrop.Domain.Common;
using Npgsql;

namespace LifeDrop.Infrastructure.Customers;

public sealed class PostgresCustomerStore(NpgsqlDataSource dataSource) : ICustomerStore
{
    public async Task<CustomerDto?> GetByIdAsync(TenantId tenantId, CustomerId id, AccessScope scope, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var setTenantCommand = new NpgsqlCommand("SELECT set_config('app.current_tenant', @tenant, true)", connection, transaction))
        {
            setTenantCommand.Parameters.AddWithValue("tenant", tenantId.Value.ToString());
            await setTenantCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        var sql = "SELECT " + SelectColumns + " FROM customers WHERE id = @id";

        await using var command = new NpgsqlCommand(sql + " AND " + ScopeFilter(scope), connection, transaction);
        command.Parameters.AddWithValue("id", id.Value);
        AddScopeParameters(command, scope);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;

        return ReadCustomer(reader);
    }

    public Task<CreateCustomerResult> CreateAsync(TenantId tenantId, UserId actorId, CustomerId id, CreateCustomerRequest request, CancellationToken cancellationToken) =>
        CreateAsync(tenantId, actorId, id, request, null, "approved", cancellationToken);

    public async Task<CreateCustomerResult> CreateAsync(TenantId tenantId, UserId actorId, CustomerId id, CreateCustomerRequest request,
        Guid? regionId, string approvalStatus, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var setTenantCommand = new NpgsqlCommand("SELECT set_config('app.current_tenant', @tenant, true)", connection, transaction))
        {
            setTenantCommand.Parameters.AddWithValue("tenant", tenantId.Value.ToString());
            await setTenantCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        const string sql = """
            WITH inserted AS (
                INSERT INTO customers (id, tenant_id, type, code, name, status, owner_user_id, region_id, approval_status)
                VALUES (@id, @tenant, @type, @code, @name, 'no_location', @actor, @region, @approval)
                ON CONFLICT (tenant_id, code) DO NOTHING
                RETURNING id, tenant_id
            ), audited AS (
                INSERT INTO audit_logs (id, tenant_id, actor_id, action, entity_id, occurred_at)
                SELECT gen_random_uuid(), tenant_id, @actor,
                       CASE WHEN @approval = 'pending' THEN 'customer.created_pending' ELSE 'customer.created' END, id::text, now()
                FROM inserted
                RETURNING id
            )
            SELECT count(*) FROM inserted;
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("id", id.Value);
        command.Parameters.AddWithValue("tenant", tenantId.Value);
        command.Parameters.AddWithValue("actor", actorId.Value);
        command.Parameters.AddWithValue("type", request.Type);
        command.Parameters.AddWithValue("code", request.Code);
        command.Parameters.AddWithValue("name", request.Name);
        command.Parameters.AddWithValue("region", (object?)regionId ?? DBNull.Value);
        command.Parameters.AddWithValue("approval", approvalStatus);

        if (regionId is not null)
        {
            await using var regionCheck = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM regions WHERE id = @region)", connection, transaction);
            regionCheck.Parameters.AddWithValue("region", regionId.Value);
            if (!(bool)(await regionCheck.ExecuteScalarAsync(cancellationToken) ?? false)) return CreateCustomerResult.RegionNotFound;
        }

        var inserted = (long)(await command.ExecuteScalarAsync(cancellationToken) ?? 0L);
        if (inserted == 0) return CreateCustomerResult.DuplicateCode;
        
        await transaction.CommitAsync(cancellationToken);
        return CreateCustomerResult.Created;
    }

    public Task<IReadOnlyList<CustomerDto>> ListAsync(TenantId tenantId, AccessScope scope, int limit, DateTimeOffset? cursorCreatedAt, Guid? cursorId, CancellationToken cancellationToken) =>
        ListCoreAsync(tenantId, scope, limit, cursorCreatedAt, cursorId, archived: false, cancellationToken);

    public Task<IReadOnlyList<CustomerDto>> ListArchivedAsync(TenantId tenantId, AccessScope scope, int limit, DateTimeOffset? cursorCreatedAt, Guid? cursorId, CancellationToken cancellationToken) =>
        ListCoreAsync(tenantId, scope, limit, cursorCreatedAt, cursorId, archived: true, cancellationToken);

    public async Task<CustomerUpdateResult> EditAsync(TenantId tenantId, UserId actorId, CustomerId id, AccessScope scope, string type, string code, string name, CancellationToken cancellationToken)
    {
        try
        {
            return await UpdateWithAuditAsync(tenantId, actorId, id, scope, "customer.edited", "type = @type, code = @code, name = @name", command =>
            {
                command.Parameters.AddWithValue("type", type);
                command.Parameters.AddWithValue("code", code);
                command.Parameters.AddWithValue("name", name);
            }, cancellationToken) ? CustomerUpdateResult.Success : CustomerUpdateResult.NotFound;
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return CustomerUpdateResult.Conflict;
        }
    }

    public async Task<bool> SetContactAsync(TenantId tenantId, UserId actorId, CustomerId id, AccessScope scope, string? contactName, string? phone2, string? notes, CancellationToken cancellationToken) =>
        await UpdateWithAuditAsync(tenantId, actorId, id, scope, "customer.contact_changed", "contact_name = @contactName, phone2 = @phone2, notes = @notes", command =>
        {
            command.Parameters.AddWithValue("contactName", (object?)contactName ?? DBNull.Value);
            command.Parameters.AddWithValue("phone2", (object?)phone2 ?? DBNull.Value);
            command.Parameters.AddWithValue("notes", (object?)notes ?? DBNull.Value);
        }, cancellationToken);

    public Task<IReadOnlyList<CustomerDto>> SearchAsync(TenantId tenantId, AccessScope scope, int limit, DateTimeOffset? cursorCreatedAt, Guid? cursorId, string search, CancellationToken cancellationToken) =>
        ListCoreAsync(tenantId, scope, limit, cursorCreatedAt, cursorId, archived: false, cancellationToken, search);

    public async Task<bool> SetArchivedAsync(TenantId tenantId, UserId actorId, CustomerId id, AccessScope scope, bool archived, CancellationToken cancellationToken) =>
        await UpdateWithAuditAsync(tenantId, actorId, id, scope, archived ? "customer.archived" : "customer.restored",
            archived ? "archived_at = COALESCE(archived_at, now())" : "archived_at = NULL", _ => { }, cancellationToken);

    private async Task<IReadOnlyList<CustomerDto>> ListCoreAsync(TenantId tenantId, AccessScope scope, int limit, DateTimeOffset? cursorCreatedAt, Guid? cursorId, bool archived, CancellationToken cancellationToken, string? search = null)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var setTenantCommand = new NpgsqlCommand("SELECT set_config('app.current_tenant', @tenant, true)", connection, transaction))
        {
            setTenantCommand.Parameters.AddWithValue("tenant", tenantId.Value.ToString());
            await setTenantCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        var sql = "SELECT " + SelectColumns + " FROM customers";

        sql += "\nWHERE " + ScopeFilter(scope) + (archived ? " AND archived_at IS NOT NULL" : " AND archived_at IS NULL");
        if (search is not null)
            sql += "\nAND (name ILIKE @search ESCAPE '\\' OR code ILIKE @search ESCAPE '\\' OR phone ILIKE @search ESCAPE '\\' OR phone2 ILIKE @search ESCAPE '\\')";
        if (cursorCreatedAt.HasValue && cursorId.HasValue)
        {
            sql += "\nAND ((created_at > @cursorDate) OR (created_at = @cursorDate AND id > @cursorId))";
        }

        sql += "\nORDER BY created_at ASC, id ASC\nLIMIT @limit";

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("limit", limit);
        if (search is not null) command.Parameters.AddWithValue("search", "%" + search.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%");
        AddScopeParameters(command, scope);
        if (cursorCreatedAt.HasValue && cursorId.HasValue)
        {
            command.Parameters.AddWithValue("cursorDate", cursorCreatedAt.Value);
            command.Parameters.AddWithValue("cursorId", cursorId.Value);
        }

        var results = new List<CustomerDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(ReadCustomer(reader));
        }

        return results;
    }

    // Data scope (plan step 0.6b): tenant = all rows (RLS already limits the tenant); region = customers in the
    // region of the actor's team plus customers the actor owns; own = customers the actor owns.
    private static string ScopeFilter(AccessScope scope) => scope.Scope switch
    {
        DataScope.Tenant => "TRUE",
        DataScope.Region when scope.RegionId is not null => "(owner_user_id = @scopeActor OR region_id = @scopeRegion)",
        _ => "owner_user_id = @scopeActor"
    };

    private static void AddScopeParameters(NpgsqlCommand command, AccessScope scope)
    {
        command.Parameters.AddWithValue("scopeActor", scope.ActorId.Value);
        command.Parameters.AddWithValue("scopeRegion", (object?)scope.RegionId ?? DBNull.Value);
    }

    private const string SelectColumns = """
        id, tenant_id, type, code, name, status, created_at,
        ST_Y(geom::geometry), ST_X(geom::geometry), location_source, location_updated_at, geofence_radius_m,
        region_id, approval_status, phone, address, place_id,
        (SELECT jsonb_agg(jsonb_build_object('id', a.id, 'kind', a.kind, 'name', a.name) ORDER BY a.depth DESC)
         FROM (WITH RECURSIVE chain(id, parent_id, kind, name, depth) AS (
                   SELECT p.id, p.parent_id, p.kind, p.name, 0 FROM places p WHERE p.id = customers.place_id
                   UNION ALL
                   SELECT p.id, p.parent_id, p.kind, p.name, c.depth + 1 FROM places p JOIN chain c ON p.id = c.parent_id)
               SELECT * FROM chain) a),
        priority, contact_name, phone2, notes
        """;

    private static CustomerDto ReadCustomer(NpgsqlDataReader reader) => new(
        reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
        reader.GetString(5), reader.GetFieldValue<DateTimeOffset>(6),
        reader.IsDBNull(7) ? null : reader.GetDouble(7),
        reader.IsDBNull(8) ? null : reader.GetDouble(8),
        reader.IsDBNull(9) ? null : reader.GetString(9),
        reader.IsDBNull(10) ? null : reader.GetFieldValue<DateTimeOffset>(10),
        reader.IsDBNull(11) ? CustomerDto.DefaultGeofenceRadiusMeters : reader.GetInt32(11),
        reader.IsDBNull(12) ? null : reader.GetGuid(12), reader.GetString(13),
        reader.IsDBNull(14) ? null : reader.GetString(14), reader.IsDBNull(15) ? null : reader.GetString(15),
        reader.IsDBNull(16) ? null : reader.GetGuid(16), reader.IsDBNull(17) ? null : ReadPath(reader.GetString(17)), reader.GetString(18),
        reader.IsDBNull(19) ? null : reader.GetString(19), reader.IsDBNull(20) ? null : reader.GetString(20), reader.IsDBNull(21) ? null : reader.GetString(21));

    private static readonly System.Text.Json.JsonSerializerOptions PathJson = new(System.Text.Json.JsonSerializerDefaults.Web);

    private static IReadOnlyList<PlacePathItem>? ReadPath(string json) =>
        System.Text.Json.JsonSerializer.Deserialize<List<PlacePathItem>>(json, PathJson);

    public async Task<bool> SetLocationAsync(TenantId tenantId, UserId actorId, CustomerId id, AccessScope scope,
        double latitude, double longitude, string source, double? accuracyMeters, CancellationToken cancellationToken) =>
        await UpdateWithAuditAsync(tenantId, actorId, id, scope, "customer.location_changed", """
            geom = ST_SetSRID(ST_MakePoint(@lon, @lat), 4326)::geography, location_source = @source,
            location_accuracy_m = @accuracy, location_updated_at = now(), location_updated_by = @scopeActor, status = 'located'
            """, command =>
        {
            command.Parameters.AddWithValue("lat", latitude);
            command.Parameters.AddWithValue("lon", longitude);
            command.Parameters.AddWithValue("source", source);
            command.Parameters.AddWithValue("accuracy", (object?)accuracyMeters ?? DBNull.Value);
        }, cancellationToken);

    public async Task<bool> SetGeofenceRadiusAsync(TenantId tenantId, UserId actorId, CustomerId id, AccessScope scope,
        int? radiusMeters, CancellationToken cancellationToken) =>
        await UpdateWithAuditAsync(tenantId, actorId, id, scope, "customer.geofence_changed", "geofence_radius_m = @radius",
            command => command.Parameters.AddWithValue("radius", (object?)radiusMeters ?? DBNull.Value), cancellationToken);

    public async Task<IReadOnlyList<VisitWindowDto>?> GetVisitWindowsAsync(TenantId tenantId, CustomerId id, AccessScope scope, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await SetTenantAsync(connection, transaction, tenantId, cancellationToken);

        await using (var exists = new NpgsqlCommand($"SELECT 1 FROM customers WHERE id = @id AND {ScopeFilter(scope)}", connection, transaction))
        {
            exists.Parameters.AddWithValue("id", id.Value);
            AddScopeParameters(exists, scope);
            if (await exists.ExecuteScalarAsync(cancellationToken) is null) return null;
        }

        await using var command = new NpgsqlCommand("""
            SELECT weekday, opens_at, closes_at FROM customer_visit_windows
            WHERE customer_id = @id ORDER BY weekday, opens_at
            """, connection, transaction);
        command.Parameters.AddWithValue("id", id.Value);
        var windows = new List<VisitWindowDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            windows.Add(new VisitWindowDto(reader.GetInt16(0), reader.GetFieldValue<TimeOnly>(1), reader.GetFieldValue<TimeOnly>(2)));
        return windows;
    }

    public async Task<bool> ReplaceVisitWindowsAsync(TenantId tenantId, UserId actorId, CustomerId id, AccessScope scope,
        IReadOnlyList<VisitWindowDto> windows, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await SetTenantAsync(connection, transaction, tenantId, cancellationToken);

        // Lock the customer row (inside the actor's scope) so concurrent replacements run one after the other.
        await using (var lockRow = new NpgsqlCommand($"SELECT 1 FROM customers WHERE id = @id AND {ScopeFilter(scope)} FOR UPDATE", connection, transaction))
        {
            lockRow.Parameters.AddWithValue("id", id.Value);
            AddScopeParameters(lockRow, scope);
            if (await lockRow.ExecuteScalarAsync(cancellationToken) is null) return false;
        }

        await using (var delete = new NpgsqlCommand("DELETE FROM customer_visit_windows WHERE customer_id = @id", connection, transaction))
        {
            delete.Parameters.AddWithValue("id", id.Value);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }
        foreach (var window in windows)
        {
            await using var insert = new NpgsqlCommand("""
                INSERT INTO customer_visit_windows (tenant_id, customer_id, weekday, opens_at, closes_at)
                VALUES (@tenant, @id, @weekday, @opens, @closes)
                """, connection, transaction);
            insert.Parameters.AddWithValue("tenant", tenantId.Value);
            insert.Parameters.AddWithValue("id", id.Value);
            insert.Parameters.AddWithValue("weekday", (short)window.Weekday);
            insert.Parameters.AddWithValue("opens", window.OpensAt);
            insert.Parameters.AddWithValue("closes", window.ClosesAt);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var audit = new NpgsqlCommand("""
            INSERT INTO audit_logs (id, tenant_id, actor_id, action, entity_id, occurred_at)
            VALUES (gen_random_uuid(), @tenant, @actor, 'customer.visit_windows_changed', @entity, now())
            """, connection, transaction);
        audit.Parameters.AddWithValue("tenant", tenantId.Value);
        audit.Parameters.AddWithValue("actor", actorId.Value);
        audit.Parameters.AddWithValue("entity", id.Value.ToString());
        await audit.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<CustomerProfileResult> SetProfileAsync(TenantId tenantId, UserId actorId, CustomerId id, AccessScope scope,
        CustomerProfileRequest profile, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await SetTenantAsync(connection, transaction, tenantId, cancellationToken);

        await using (var lockRow = new NpgsqlCommand($"SELECT 1 FROM customers WHERE id = @id AND {ScopeFilter(scope)} FOR UPDATE", connection, transaction))
        {
            lockRow.Parameters.AddWithValue("id", id.Value);
            AddScopeParameters(lockRow, scope);
            if (await lockRow.ExecuteScalarAsync(cancellationToken) is null) return CustomerProfileResult.NotFound;
        }

        // The place must be active and not merged away.
        if (profile.PlaceId is { } placeId && !await ExistsAsync(connection, transaction,
                "SELECT 1 FROM places WHERE id = @id AND status = 'active' AND merged_into IS NULL", placeId, cancellationToken))
            return CustomerProfileResult.InvalidData;

        await using (var update = new NpgsqlCommand("""
            UPDATE customers SET phone = @phone, address = @address, place_id = @place
            WHERE id = @id
            """, connection, transaction))
        {
            update.Parameters.AddWithValue("id", id.Value);
            update.Parameters.Add(new NpgsqlParameter("phone", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)profile.Phone ?? DBNull.Value });
            update.Parameters.Add(new NpgsqlParameter("address", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)profile.Address ?? DBNull.Value });
            update.Parameters.Add(new NpgsqlParameter("place", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)profile.PlaceId ?? DBNull.Value });
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var audit = new NpgsqlCommand("""
            INSERT INTO audit_logs (id, tenant_id, actor_id, action, entity_id, occurred_at)
            VALUES (gen_random_uuid(), @tenant, @actor, 'customer.profile_changed', @entity, now())
            """, connection, transaction);
        audit.Parameters.AddWithValue("tenant", tenantId.Value);
        audit.Parameters.AddWithValue("actor", actorId.Value);
        audit.Parameters.AddWithValue("entity", id.Value.ToString());
        await audit.ExecuteNonQueryAsync(cancellationToken);
        await NotifyAsync(connection, transaction, tenantId, actorId, id, "customer.profile_changed", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return CustomerProfileResult.Success;
    }

    private static async Task<bool> ExistsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, Guid id, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("id", id);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static async Task SetTenantAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, TenantId tenantId, CancellationToken cancellationToken)
    {
        await using var setTenant = new NpgsqlCommand("SELECT set_config('app.current_tenant', @tenant, true)", connection, transaction);
        setTenant.Parameters.AddWithValue("tenant", tenantId.Value.ToString());
        await setTenant.ExecuteNonQueryAsync(cancellationToken);
    }

    // One UPDATE limited to the actor's scope and, only if a row changed, its audit entry, in one transaction.
    private async Task<bool> UpdateWithAuditAsync(TenantId tenantId, UserId actorId, CustomerId id, AccessScope scope, string auditAction,
        string setClause, Action<NpgsqlCommand> addParameters, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var setTenant = new NpgsqlCommand("SELECT set_config('app.current_tenant', @tenant, true)", connection, transaction))
        {
            setTenant.Parameters.AddWithValue("tenant", tenantId.Value.ToString());
            await setTenant.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var update = new NpgsqlCommand($"UPDATE customers SET {setClause} WHERE id = @id AND {ScopeFilter(scope)}", connection, transaction);
        update.Parameters.AddWithValue("id", id.Value);
        AddScopeParameters(update, scope);
        addParameters(update);
        if (await update.ExecuteNonQueryAsync(cancellationToken) == 0) return false;

        await using var audit = new NpgsqlCommand("""
            INSERT INTO audit_logs (id, tenant_id, actor_id, action, entity_id, occurred_at)
            VALUES (gen_random_uuid(), @tenant, @actor, @action, @entity, now())
            """, connection, transaction);
        audit.Parameters.AddWithValue("tenant", tenantId.Value);
        audit.Parameters.AddWithValue("actor", actorId.Value);
        audit.Parameters.AddWithValue("action", auditAction);
        audit.Parameters.AddWithValue("entity", id.Value.ToString());
        await audit.ExecuteNonQueryAsync(cancellationToken);
        if (auditAction == "customer.location_changed")
            await NotifyAsync(connection, transaction, tenantId, actorId, id, auditAction, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    // Tells the people responsible that somebody other than the customer's owner changed it (plan step 1.23-c): the supervisors of the
    // customer's region (a role with customers.approve and region scope, in a team of that region); when the region has none, the sales
    // managers (customers.approve with tenant scope). The person who made the change is never told.
    private static async Task NotifyAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, TenantId tenantId, UserId actorId, CustomerId id,
        string kind, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            WITH c AS (SELECT id, region_id, owner_user_id FROM customers WHERE id = @id),
            supervisors AS (
                SELECT DISTINCT ur.user_id
                FROM c
                JOIN teams t ON t.region_id = c.region_id
                JOIN team_members tm ON tm.team_id = t.id
                JOIN user_roles ur ON ur.tenant_id = tm.tenant_id AND ur.user_id = tm.user_id
                JOIN roles r ON r.tenant_id = ur.tenant_id AND r.id = ur.role_id AND r.data_scope = 'region'
                JOIN role_permissions rp ON rp.tenant_id = r.tenant_id AND rp.role_id = r.id AND rp.permission_key = 'customers.approve'
                JOIN users u ON u.tenant_id = ur.tenant_id AND u.id = ur.user_id AND u.is_active
                WHERE ur.tenant_id = @tenant
            ),
            managers AS (
                SELECT DISTINCT ur.user_id
                FROM user_roles ur
                JOIN roles r ON r.tenant_id = ur.tenant_id AND r.id = ur.role_id AND r.data_scope = 'tenant'
                JOIN role_permissions rp ON rp.tenant_id = r.tenant_id AND rp.role_id = r.id AND rp.permission_key = 'customers.approve'
                JOIN users u ON u.tenant_id = ur.tenant_id AND u.id = ur.user_id AND u.is_active
                WHERE ur.tenant_id = @tenant
            ),
            recipients AS (
                SELECT user_id FROM supervisors
                UNION ALL
                SELECT user_id FROM managers WHERE NOT EXISTS (SELECT 1 FROM supervisors)
            )
            INSERT INTO notifications (tenant_id, user_id, kind, entity_id, actor_id)
            SELECT @tenant, r.user_id, @kind, @entity, @actor
            FROM recipients r
            WHERE r.user_id <> @actor AND (SELECT owner_user_id FROM c) IS DISTINCT FROM @actor
            """, connection, transaction);
        command.Parameters.AddWithValue("id", id.Value);
        command.Parameters.AddWithValue("tenant", tenantId.Value);
        command.Parameters.AddWithValue("kind", kind);
        command.Parameters.AddWithValue("entity", id.Value.ToString());
        command.Parameters.AddWithValue("actor", actorId.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
