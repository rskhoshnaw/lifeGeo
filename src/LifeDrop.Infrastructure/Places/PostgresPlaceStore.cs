using LifeDrop.Application.Places;
using LifeDrop.Domain.Common;
using Npgsql;
using NpgsqlTypes;

namespace LifeDrop.Infrastructure.Places;

// Every write runs in one transaction with the tenant context set for RLS and appends its audit row in the same
// transaction (plan step 1.23-b). The tree rules (city -> neighbourhood -> street -> alley) are also enforced by a
// database trigger; a violation (SQLSTATE 23514) is reported as InvalidData.
public sealed class PostgresPlaceStore(NpgsqlDataSource dataSource) : IPlaceStore
{
    private const string SelectColumns = """
        p.id, p.parent_id, p.kind, p.name, p.name_ckb, p.name_ar, p.name_en, p.status, p.merged_into,
        (SELECT count(*)::int FROM places c WHERE c.parent_id = p.id AND c.status = 'active' AND c.merged_into IS NULL)
        """;

    public async Task<IReadOnlyList<PlaceDto>> ListAsync(TenantId tenantId, Guid? parentId, string? kind, string? search, bool includeInactive, int limit,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginTenantTransactionAsync(connection, tenantId, cancellationToken);
        await using var command = new NpgsqlCommand($"""
            SELECT {SelectColumns} FROM places p
            WHERE (@parent::uuid IS NULL OR p.parent_id = @parent)
              AND (@kind::text IS NULL OR p.kind = @kind)
              AND (@all OR (p.status = 'active' AND p.merged_into IS NULL))
              AND (@pattern::text IS NULL OR p.name ILIKE @pattern ESCAPE '\' OR p.name_ckb ILIKE @pattern ESCAPE '\'
                   OR p.name_ar ILIKE @pattern ESCAPE '\' OR p.name_en ILIKE @pattern ESCAPE '\')
            ORDER BY p.name COLLATE "C", p.id
            LIMIT @limit
            """, connection, transaction);
        command.Parameters.Add(new NpgsqlParameter("parent", NpgsqlDbType.Uuid) { Value = (object?)parentId ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("kind", NpgsqlDbType.Text) { Value = (object?)kind ?? DBNull.Value });
        command.Parameters.AddWithValue("all", includeInactive);
        command.Parameters.Add(new NpgsqlParameter("pattern", NpgsqlDbType.Text) { Value = search is null ? DBNull.Value : "%" + EscapeLike(search) + "%" });
        command.Parameters.AddWithValue("limit", limit);
        return await ReadPlacesAsync(command, cancellationToken);
    }

    public async Task<IReadOnlyList<PlaceDto>> ListAllAsync(TenantId tenantId, bool includeInactive, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginTenantTransactionAsync(connection, tenantId, cancellationToken);
        await using var command = new NpgsqlCommand($"""
            SELECT {SelectColumns} FROM places p WHERE @all OR (p.status = 'active' AND p.merged_into IS NULL)
            """, connection, transaction);
        command.Parameters.AddWithValue("all", includeInactive);
        return await ReadPlacesAsync(command, cancellationToken);
    }

    public async Task<PlaceResult> CreateAsync(TenantId tenantId, UserId actorId, Guid id, CreatePlaceRequest request, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginTenantTransactionAsync(connection, tenantId, cancellationToken);

        if (request.ParentId is { } parentId)
        {
            await using var parent = new NpgsqlCommand("SELECT status, merged_into IS NOT NULL FROM places WHERE id = @id", connection, transaction);
            parent.Parameters.AddWithValue("id", parentId);
            await using var reader = await parent.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return PlaceResult.NotFound;
            if (reader.GetString(0) != "active" || reader.GetBoolean(1)) return PlaceResult.InvalidData;
        }

        await using var insert = new NpgsqlCommand("""
            INSERT INTO places (id, tenant_id, parent_id, kind, name, name_ckb, name_ar, name_en, created_by)
            VALUES (@id, @tenant, @parent, @kind, @name, @ckb, @ar, @en, @actor)
            ON CONFLICT (tenant_id, parent_key, kind, name_key) WHERE merged_into IS NULL DO NOTHING
            """, connection, transaction);
        insert.Parameters.AddWithValue("id", id);
        insert.Parameters.AddWithValue("tenant", tenantId.Value);
        insert.Parameters.Add(new NpgsqlParameter("parent", NpgsqlDbType.Uuid) { Value = (object?)request.ParentId ?? DBNull.Value });
        insert.Parameters.AddWithValue("kind", request.Kind);
        AddNames(insert, request.Name, request.NameCkb, request.NameAr, request.NameEn);
        insert.Parameters.AddWithValue("actor", actorId.Value);
        try
        {
            if (await insert.ExecuteNonQueryAsync(cancellationToken) == 0) return PlaceResult.Duplicate;
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.CheckViolation)
        {
            return PlaceResult.InvalidData;
        }

        await AuditAsync(connection, transaction, tenantId, actorId, "place.created", id.ToString(), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return PlaceResult.Success;
    }

    public async Task<PlaceResult> UpdateAsync(TenantId tenantId, UserId actorId, Guid id, UpdatePlaceRequest request, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginTenantTransactionAsync(connection, tenantId, cancellationToken);

        if (request.ParentId is { } newParent)
        {
            await using var parent = new NpgsqlCommand("SELECT status, merged_into IS NOT NULL FROM places WHERE id = @id", connection, transaction);
            parent.Parameters.AddWithValue("id", newParent);
            await using var reader = await parent.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return PlaceResult.NotFound;
            if (reader.GetString(0) != "active" || reader.GetBoolean(1)) return PlaceResult.InvalidData;
        }

        await using var update = new NpgsqlCommand("""
            UPDATE places SET name = @name, name_ckb = @ckb, name_ar = @ar, name_en = @en, status = @status,
                parent_id = COALESCE(@parent, parent_id), updated_at = now()
            WHERE id = @id AND merged_into IS NULL
            """, connection, transaction);
        update.Parameters.AddWithValue("id", id);
        AddNames(update, request.Name, request.NameCkb, request.NameAr, request.NameEn);
        update.Parameters.AddWithValue("status", request.Status);
        update.Parameters.Add(new NpgsqlParameter("parent", NpgsqlDbType.Uuid) { Value = (object?)request.ParentId ?? DBNull.Value });
        try
        {
            if (await update.ExecuteNonQueryAsync(cancellationToken) == 0) return PlaceResult.NotFound;
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return PlaceResult.Duplicate;
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.CheckViolation)
        {
            return PlaceResult.InvalidData;
        }

        await AuditAsync(connection, transaction, tenantId, actorId, "place.updated", id.ToString(), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return PlaceResult.Success;
    }

    public async Task<PlaceResult> MergeAsync(TenantId tenantId, UserId actorId, Guid id, Guid intoPlaceId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginTenantTransactionAsync(connection, tenantId, cancellationToken);

        var source = await ReadForUpdateAsync(connection, transaction, id, cancellationToken);
        if (source is null) return PlaceResult.NotFound;
        if (source.Value.Merged) return PlaceResult.Conflict;
        var target = await ReadForUpdateAsync(connection, transaction, intoPlaceId, cancellationToken);
        if (target is null) return PlaceResult.NotFound;
        if (target.Value.Merged || target.Value.Status == "inactive") return PlaceResult.Conflict;
        if (target.Value.Kind != source.Value.Kind) return PlaceResult.InvalidData;

        try
        {
            // Places below and customers move to the kept place; a name that already exists below it is a conflict.
            await using (var children = new NpgsqlCommand("UPDATE places SET parent_id = @into, updated_at = now() WHERE parent_id = @id AND merged_into IS NULL", connection, transaction))
            {
                children.Parameters.AddWithValue("into", intoPlaceId);
                children.Parameters.AddWithValue("id", id);
                await children.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return PlaceResult.Conflict;
        }
        await using (var customers = new NpgsqlCommand("UPDATE customers SET place_id = @into WHERE place_id = @id", connection, transaction))
        {
            customers.Parameters.AddWithValue("into", intoPlaceId);
            customers.Parameters.AddWithValue("id", id);
            await customers.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var retire = new NpgsqlCommand("UPDATE places SET merged_into = @into, status = 'inactive', updated_at = now() WHERE id = @id", connection, transaction))
        {
            retire.Parameters.AddWithValue("into", intoPlaceId);
            retire.Parameters.AddWithValue("id", id);
            await retire.ExecuteNonQueryAsync(cancellationToken);
        }

        await AuditAsync(connection, transaction, tenantId, actorId, "place.merged", id.ToString(), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return PlaceResult.Success;
    }

    public async Task<ImportPlacesResult> ImportAsync(TenantId tenantId, UserId actorId, IReadOnlyList<ImportPlaceRow> rows, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginTenantTransactionAsync(connection, tenantId, cancellationToken);

        // Parents are created first (cities, then neighbourhoods, streets, alleys); every level is found by name below its parent.
        var rank = new Dictionary<string, int> { ["city"] = 0, ["neighbourhood"] = 1, ["street"] = 2, ["alley"] = 3 };
        var known = new Dictionary<string, Guid>();
        int created = 0, skipped = 0;

        async Task<(Guid Id, bool Created)> EnsureAsync(Guid? parentId, string kind, string name, ImportPlaceRow? names)
        {
            var key = $"{parentId}|{kind}|{name.ToLowerInvariant()}";
            if (known.TryGetValue(key, out var cached)) return (cached, false);

            await using var insert = new NpgsqlCommand("""
                INSERT INTO places (tenant_id, parent_id, kind, name, name_ckb, name_ar, name_en, status, created_by)
                VALUES (@tenant, @parent, @kind, @name, @ckb, @ar, @en, @status, @actor)
                ON CONFLICT (tenant_id, parent_key, kind, name_key) WHERE merged_into IS NULL DO NOTHING
                RETURNING id
                """, connection, transaction);
            insert.Parameters.AddWithValue("tenant", tenantId.Value);
            insert.Parameters.Add(new NpgsqlParameter("parent", NpgsqlDbType.Uuid) { Value = (object?)parentId ?? DBNull.Value });
            insert.Parameters.AddWithValue("kind", kind);
            AddNames(insert, name, names?.NameCkb, names?.NameAr, names?.NameEn);
            insert.Parameters.AddWithValue("status", names is { Active: false } ? "inactive" : "active");
            insert.Parameters.AddWithValue("actor", actorId.Value);
            var inserted = await insert.ExecuteScalarAsync(cancellationToken);
            if (inserted is Guid newId)
            {
                known[key] = newId;
                return (newId, true);
            }

            await using var find = new NpgsqlCommand("""
                SELECT id FROM places WHERE parent_key = COALESCE(@parent, '00000000-0000-0000-0000-000000000000'::uuid)
                  AND kind = @kind AND name_key = lower(btrim(@name)) AND merged_into IS NULL
                """, connection, transaction);
            find.Parameters.Add(new NpgsqlParameter("parent", NpgsqlDbType.Uuid) { Value = (object?)parentId ?? DBNull.Value });
            find.Parameters.AddWithValue("kind", kind);
            find.Parameters.AddWithValue("name", name);
            var existing = (Guid)(await find.ExecuteScalarAsync(cancellationToken))!;
            known[key] = existing;
            return (existing, false);
        }

        foreach (var row in rows.OrderBy(r => rank[r.Kind]))
        {
            Guid? parent = null;
            if (row.City is not null)
            {
                var city = await EnsureAsync(null, "city", row.City, null);
                if (city.Created) created++;
                parent = city.Id;
            }
            if (row.Neighbourhood is not null)
            {
                var neighbourhood = await EnsureAsync(parent, "neighbourhood", row.Neighbourhood, null);
                if (neighbourhood.Created) created++;
                parent = neighbourhood.Id;
            }
            if (row.Street is not null)
            {
                var street = await EnsureAsync(parent, "street", row.Street, null);
                if (street.Created) created++;
                parent = street.Id;
            }
            var place = await EnsureAsync(parent, row.Kind, row.Name, row);
            if (place.Created) created++;
            else skipped++;
        }

        await AuditAsync(connection, transaction, tenantId, actorId, "places.imported", Guid.NewGuid().ToString(), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new ImportPlacesResult(created, skipped);
    }

    private static async Task<(string Kind, string Status, bool Merged)?> ReadForUpdateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid id, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT kind, status, merged_into IS NOT NULL FROM places WHERE id = @id FOR UPDATE", connection, transaction);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? (reader.GetString(0), reader.GetString(1), reader.GetBoolean(2)) : null;
    }

    private static async Task<IReadOnlyList<PlaceDto>> ReadPlacesAsync(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var items = new List<PlaceDto>();
        while (await reader.ReadAsync(cancellationToken))
            items.Add(new PlaceDto(reader.GetGuid(0), reader.IsDBNull(1) ? null : reader.GetGuid(1), reader.GetString(2), reader.GetString(3),
                Text(reader, 4), Text(reader, 5), Text(reader, 6), reader.GetString(7), reader.IsDBNull(8) ? null : reader.GetGuid(8), reader.GetInt32(9)));
        return items;
    }

    // name, ckb, ar, en parameters shared by the insert and update statements.
    private static void AddNames(NpgsqlCommand command, string name, string? ckb, string? ar, string? en)
    {
        command.Parameters.AddWithValue("name", name);
        command.Parameters.Add(new NpgsqlParameter("ckb", NpgsqlDbType.Text) { Value = (object?)ckb ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("ar", NpgsqlDbType.Text) { Value = (object?)ar ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("en", NpgsqlDbType.Text) { Value = (object?)en ?? DBNull.Value });
    }

    private static string? Text(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static string EscapeLike(string value) => value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private static async Task<NpgsqlTransaction> BeginTenantTransactionAsync(NpgsqlConnection connection, TenantId tenantId, CancellationToken cancellationToken)
    {
        var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var setTenant = new NpgsqlCommand("SELECT set_config('app.current_tenant', @tenant, true)", connection, transaction);
        setTenant.Parameters.AddWithValue("tenant", tenantId.Value.ToString());
        await setTenant.ExecuteNonQueryAsync(cancellationToken);
        return transaction;
    }

    private static async Task AuditAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, TenantId tenantId, UserId actorId,
        string action, string entityId, CancellationToken cancellationToken)
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
