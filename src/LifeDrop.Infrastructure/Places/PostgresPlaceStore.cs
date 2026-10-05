using LifeDrop.Application.Places;
using LifeDrop.Domain.Common;
using Npgsql;
using NpgsqlTypes;

namespace LifeDrop.Infrastructure.Places;

// Every write runs in one transaction with the tenant context set for RLS and appends its audit row in the same
// transaction (plan step 1.23-b).
public sealed class PostgresPlaceStore(NpgsqlDataSource dataSource) : IPlaceStore
{
    public async Task<IReadOnlyList<CityDto>> ListCitiesAsync(TenantId tenantId, bool includeInactive, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginTenantTransactionAsync(connection, tenantId, cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT id, name, name_ckb, name_ar, name_en, status FROM cities
            WHERE @all OR status = 'active' ORDER BY name COLLATE "C", id
            """, connection, transaction);
        command.Parameters.AddWithValue("all", includeInactive);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var items = new List<CityDto>();
        while (await reader.ReadAsync(cancellationToken))
            items.Add(new CityDto(reader.GetGuid(0), reader.GetString(1), Text(reader, 2), Text(reader, 3), Text(reader, 4), reader.GetString(5)));
        return items;
    }

    public async Task<IReadOnlyList<PlaceDto>> ListPlacesAsync(TenantId tenantId, Guid? cityId, string? kind, string? search, bool includeInactive,
        int limit, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginTenantTransactionAsync(connection, tenantId, cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT id, city_id, kind, name, name_ckb, name_ar, name_en, status, merged_into FROM places
            WHERE (@city::uuid IS NULL OR city_id = @city)
              AND (@kind::text IS NULL OR kind = @kind)
              AND (@all OR (status IN ('active', 'suggested') AND merged_into IS NULL))
              AND (@pattern::text IS NULL OR name ILIKE @pattern ESCAPE '\' OR name_ckb ILIKE @pattern ESCAPE '\'
                   OR name_ar ILIKE @pattern ESCAPE '\' OR name_en ILIKE @pattern ESCAPE '\')
            ORDER BY name COLLATE "C", id
            LIMIT @limit
            """, connection, transaction);
        command.Parameters.Add(new NpgsqlParameter("city", NpgsqlDbType.Uuid) { Value = (object?)cityId ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("kind", NpgsqlDbType.Text) { Value = (object?)kind ?? DBNull.Value });
        command.Parameters.AddWithValue("all", includeInactive);
        command.Parameters.Add(new NpgsqlParameter("pattern", NpgsqlDbType.Text) { Value = search is null ? DBNull.Value : "%" + EscapeLike(search) + "%" });
        command.Parameters.AddWithValue("limit", limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var items = new List<PlaceDto>();
        while (await reader.ReadAsync(cancellationToken))
            items.Add(new PlaceDto(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3),
                Text(reader, 4), Text(reader, 5), Text(reader, 6), reader.GetString(7), reader.IsDBNull(8) ? null : reader.GetGuid(8)));
        return items;
    }

    public async Task<PlaceResult> CreateCityAsync(TenantId tenantId, UserId actorId, Guid id, CreateCityRequest request, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginTenantTransactionAsync(connection, tenantId, cancellationToken);
        await using var insert = new NpgsqlCommand("""
            INSERT INTO cities (id, tenant_id, name, name_ckb, name_ar, name_en) VALUES (@id, @tenant, @name, @ckb, @ar, @en)
            ON CONFLICT (tenant_id, (lower(btrim(name)))) DO NOTHING
            """, connection, transaction);
        insert.Parameters.AddWithValue("id", id);
        insert.Parameters.AddWithValue("tenant", tenantId.Value);
        AddNames(insert, request.Name, request.NameCkb, request.NameAr, request.NameEn);
        if (await insert.ExecuteNonQueryAsync(cancellationToken) == 0) return PlaceResult.Duplicate;

        await AuditAsync(connection, transaction, tenantId, actorId, "city.created", id.ToString(), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return PlaceResult.Success;
    }

    public async Task<PlaceResult> CreatePlaceAsync(TenantId tenantId, UserId actorId, Guid id, CreatePlaceRequest request, string status, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginTenantTransactionAsync(connection, tenantId, cancellationToken);

        await using (var city = new NpgsqlCommand("SELECT 1 FROM cities WHERE id = @id AND status = 'active'", connection, transaction))
        {
            city.Parameters.AddWithValue("id", request.CityId);
            if (await city.ExecuteScalarAsync(cancellationToken) is null) return PlaceResult.NotFound;
        }

        await using var insert = new NpgsqlCommand("""
            INSERT INTO places (id, tenant_id, city_id, kind, name, name_ckb, name_ar, name_en, status, suggested_by)
            VALUES (@id, @tenant, @city, @kind, @name, @ckb, @ar, @en, @status, @suggestedBy)
            ON CONFLICT (tenant_id, city_id, kind, (lower(btrim(name)))) WHERE merged_into IS NULL DO NOTHING
            """, connection, transaction);
        insert.Parameters.AddWithValue("id", id);
        insert.Parameters.AddWithValue("tenant", tenantId.Value);
        insert.Parameters.AddWithValue("city", request.CityId);
        insert.Parameters.AddWithValue("kind", request.Kind);
        AddNames(insert, request.Name, request.NameCkb, request.NameAr, request.NameEn);
        insert.Parameters.AddWithValue("status", status);
        insert.Parameters.Add(new NpgsqlParameter("suggestedBy", NpgsqlDbType.Uuid) { Value = status == "suggested" ? actorId.Value : DBNull.Value });
        if (await insert.ExecuteNonQueryAsync(cancellationToken) == 0) return PlaceResult.Duplicate;

        await AuditAsync(connection, transaction, tenantId, actorId, status == "suggested" ? "place.suggested" : "place.created", id.ToString(), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return PlaceResult.Success;
    }

    public async Task<PlaceResult> UpdatePlaceAsync(TenantId tenantId, UserId actorId, Guid id, UpdatePlaceRequest request, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginTenantTransactionAsync(connection, tenantId, cancellationToken);
        await using var update = new NpgsqlCommand("""
            UPDATE places SET name = @name, name_ckb = @ckb, name_ar = @ar, name_en = @en, status = @status
            WHERE id = @id AND merged_into IS NULL
            """, connection, transaction);
        update.Parameters.AddWithValue("id", id);
        AddNames(update, request.Name, request.NameCkb, request.NameAr, request.NameEn);
        update.Parameters.AddWithValue("status", request.Status);
        try
        {
            if (await update.ExecuteNonQueryAsync(cancellationToken) == 0) return PlaceResult.NotFound;
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return PlaceResult.Duplicate;
        }

        await AuditAsync(connection, transaction, tenantId, actorId, "place.updated", id.ToString(), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return PlaceResult.Success;
    }

    public async Task<PlaceResult> MergePlaceAsync(TenantId tenantId, UserId actorId, Guid id, Guid intoPlaceId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginTenantTransactionAsync(connection, tenantId, cancellationToken);

        var source = await ReadForUpdateAsync(connection, transaction, id, cancellationToken);
        if (source is null) return PlaceResult.NotFound;
        if (source.Value.Merged) return PlaceResult.Conflict;
        var target = await ReadForUpdateAsync(connection, transaction, intoPlaceId, cancellationToken);
        if (target is null) return PlaceResult.NotFound;
        if (target.Value.Merged || target.Value.Status == "inactive") return PlaceResult.Conflict;
        if (target.Value.CityId != source.Value.CityId || target.Value.Kind != source.Value.Kind) return PlaceResult.InvalidData;

        foreach (var column in new[] { "street_id", "neighbourhood_id" })
        {
            await using var move = new NpgsqlCommand($"UPDATE customers SET {column} = @into WHERE {column} = @id", connection, transaction);
            move.Parameters.AddWithValue("into", intoPlaceId);
            move.Parameters.AddWithValue("id", id);
            await move.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var retire = new NpgsqlCommand("UPDATE places SET merged_into = @into, status = 'inactive' WHERE id = @id", connection, transaction))
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

        var cityIds = new Dictionary<string, Guid>();
        int citiesCreated = 0, placesCreated = 0, skipped = 0;
        foreach (var row in rows)
        {
            var cityKey = row.City.ToLowerInvariant();
            if (!cityIds.TryGetValue(cityKey, out var cityId))
            {
                await using var insertCity = new NpgsqlCommand("""
                    INSERT INTO cities (tenant_id, name) VALUES (@tenant, @name)
                    ON CONFLICT (tenant_id, (lower(btrim(name)))) DO NOTHING
                    """, connection, transaction);
                insertCity.Parameters.AddWithValue("tenant", tenantId.Value);
                insertCity.Parameters.AddWithValue("name", row.City);
                citiesCreated += await insertCity.ExecuteNonQueryAsync(cancellationToken);

                await using var findCity = new NpgsqlCommand("SELECT id FROM cities WHERE lower(btrim(name)) = lower(btrim(@name))", connection, transaction);
                findCity.Parameters.AddWithValue("name", row.City);
                cityId = (Guid)(await findCity.ExecuteScalarAsync(cancellationToken))!;
                cityIds[cityKey] = cityId;
            }

            await using var insert = new NpgsqlCommand("""
                INSERT INTO places (tenant_id, city_id, kind, name, name_ckb, name_ar, name_en, osm_ref, status)
                VALUES (@tenant, @city, @kind, @name, @ckb, @ar, @en, @osm, @status)
                ON CONFLICT (tenant_id, city_id, kind, (lower(btrim(name)))) WHERE merged_into IS NULL DO NOTHING
                """, connection, transaction);
            insert.Parameters.AddWithValue("tenant", tenantId.Value);
            insert.Parameters.AddWithValue("city", cityId);
            insert.Parameters.AddWithValue("kind", row.Kind);
            AddNames(insert, row.Name, row.NameCkb, row.NameAr, row.NameEn);
            insert.Parameters.Add(new NpgsqlParameter("osm", NpgsqlDbType.Text) { Value = (object?)row.Osm ?? DBNull.Value });
            insert.Parameters.AddWithValue("status", row.Active ? "active" : "inactive");
            if (await insert.ExecuteNonQueryAsync(cancellationToken) == 0) skipped++;
            else placesCreated++;
        }

        await AuditAsync(connection, transaction, tenantId, actorId, "places.imported", Guid.NewGuid().ToString(), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new ImportPlacesResult(citiesCreated, placesCreated, skipped);
    }

    private static async Task<(Guid CityId, string Kind, string Status, bool Merged)?> ReadForUpdateAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, Guid id, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT city_id, kind, status, merged_into IS NOT NULL FROM places WHERE id = @id FOR UPDATE", connection, transaction);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? (reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetBoolean(3))
            : null;
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
