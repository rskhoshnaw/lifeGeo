using LifeDrop.Application.Customers;
using LifeDrop.Domain.Common;
using Npgsql;
using NpgsqlTypes;

namespace LifeDrop.Infrastructure.Customers;

// Doctor list import (plan step 5.9): places are found or added, the doctor gets the next code D-0001..., all in one transaction that is rolled back for a dry run.
public sealed class PostgresDoctorImportStore(NpgsqlDataSource dataSource) : IDoctorImportStore
{
    public async Task<IReadOnlyDictionary<string, (Guid UserId, Guid? RegionId)>> FindUsersAsync(TenantId tenantId, IReadOnlyCollection<string> emails, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var set = new NpgsqlCommand("SELECT set_config('app.current_tenant', @tenant, true)", connection, transaction))
        {
            set.Parameters.AddWithValue("tenant", tenantId.Value.ToString());
            await set.ExecuteNonQueryAsync(cancellationToken);
        }
        await using var command = new NpgsqlCommand("""
            SELECT lower(u.email), u.id, (SELECT t.region_id FROM team_members m JOIN teams t ON t.id = m.team_id WHERE m.user_id = u.id LIMIT 1)
            FROM users u WHERE u.tenant_id = @tenant AND u.is_active AND lower(u.email) = ANY(@emails)
            """, connection, transaction);
        command.Parameters.AddWithValue("tenant", tenantId.Value);
        command.Parameters.AddWithValue("emails", emails.ToArray());
        var found = new Dictionary<string, (Guid, Guid?)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) found[reader.GetString(0)] = (reader.GetGuid(1), reader.IsDBNull(2) ? null : reader.GetGuid(2));
        return found;
    }

    public async Task<(int Created, int Duplicates)> ImportAsync(TenantId tenantId, UserId actorId, IReadOnlyList<DoctorToImport> rows, bool dryRun, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var set = new NpgsqlCommand("SELECT set_config('app.current_tenant', @tenant, true)", connection, transaction))
        {
            set.Parameters.AddWithValue("tenant", tenantId.Value.ToString());
            await set.ExecuteNonQueryAsync(cancellationToken);
        }
        // Two imports at once must not hand out the same codes.
        await using (var lockCommand = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtext('doctor-import:' || @tenant))", connection, transaction))
        {
            lockCommand.Parameters.AddWithValue("tenant", tenantId.Value.ToString());
            await lockCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        int next;
        await using (var max = new NpgsqlCommand("SELECT coalesce(max(substring(code from 3)::int), 0) FROM customers WHERE tenant_id = @tenant AND code ~ '^D-[0-9]{1,9}$'", connection, transaction))
        {
            max.Parameters.AddWithValue("tenant", tenantId.Value);
            next = Convert.ToInt32(await max.ExecuteScalarAsync(cancellationToken));
        }

        var known = new Dictionary<string, Guid>();
        async Task<Guid> EnsurePlaceAsync(Guid? parentId, string kind, string name)
        {
            var key = $"{parentId}|{kind}|{name.ToLowerInvariant()}";
            if (known.TryGetValue(key, out var cached)) return cached;
            await using (var similar = new NpgsqlCommand("""
                SELECT id FROM places WHERE parent_key = COALESCE(@parent, '00000000-0000-0000-0000-000000000000'::uuid)
                  AND kind = @kind AND merged_into IS NULL AND place_name_norm(name) = place_name_norm(@name) ORDER BY created_at, id LIMIT 1
                """, connection, transaction))
            {
                similar.Parameters.Add(new NpgsqlParameter("parent", NpgsqlDbType.Uuid) { Value = (object?)parentId ?? DBNull.Value });
                similar.Parameters.AddWithValue("kind", kind);
                similar.Parameters.AddWithValue("name", name);
                if (await similar.ExecuteScalarAsync(cancellationToken) is Guid same) return known[key] = same;
            }
            await using var insert = new NpgsqlCommand("""
                INSERT INTO places (tenant_id, parent_id, kind, name, created_by) VALUES (@tenant, @parent, @kind, @name, @actor) RETURNING id
                """, connection, transaction);
            insert.Parameters.AddWithValue("tenant", tenantId.Value);
            insert.Parameters.Add(new NpgsqlParameter("parent", NpgsqlDbType.Uuid) { Value = (object?)parentId ?? DBNull.Value });
            insert.Parameters.AddWithValue("kind", kind);
            insert.Parameters.AddWithValue("name", name);
            insert.Parameters.AddWithValue("actor", actorId.Value);
            return known[key] = (Guid)(await insert.ExecuteScalarAsync(cancellationToken))!;
        }

        int created = 0, duplicates = 0;
        foreach (var item in rows)
        {
            var row = item.Data;
            await using (var duplicate = new NpgsqlCommand("""
                SELECT 1 FROM customers WHERE tenant_id = @tenant AND lower(type) = 'doctor' AND lower(btrim(name)) = lower(@name)
                  AND coalesce(regexp_replace(phone, '[^0-9]', '', 'g'), '') = coalesce(regexp_replace(@phone, '[^0-9]', '', 'g'), '') LIMIT 1
                """, connection, transaction))
            {
                duplicate.Parameters.AddWithValue("tenant", tenantId.Value);
                duplicate.Parameters.AddWithValue("name", row.Name!);
                duplicate.Parameters.Add(new NpgsqlParameter("phone", NpgsqlDbType.Text) { Value = (object?)row.Phone ?? DBNull.Value });
                if (await duplicate.ExecuteScalarAsync(cancellationToken) is not null)
                {
                    duplicates++;
                    continue;
                }
            }

            Guid? place = null;
            if (row.City is not null)
            {
                place = await EnsurePlaceAsync(null, "city", row.City);
                if (row.Neighbourhood is not null)
                {
                    place = await EnsurePlaceAsync(place, "neighbourhood", row.Neighbourhood);
                    if (row.Street is not null) place = await EnsurePlaceAsync(place, "street", row.Street);
                }
            }

            var located = row.Latitude is not null && row.Longitude is not null;
            var id = Guid.NewGuid();
            await using (var insert = new NpgsqlCommand($"""
                INSERT INTO customers (id, tenant_id, type, code, name, status, owner_user_id, region_id, approval_status, phone, phone2, address, place_id, notes,
                                       clinic_name, specialty, cooperates, is_dealer, customer_class{(located ? ", geom, location_source, location_updated_at, location_updated_by" : "")})
                VALUES (@id, @tenant, 'Doctor', @code, @name, @status, @owner, @region, 'approved', @phone, @phone2, @address, @place, @notes,
                        @clinic, @specialty, @cooperates, @dealer, @class{(located ? ", ST_SetSRID(ST_MakePoint(@lon, @lat), 4326)::geography, 'manual', now(), @actor" : "")})
                """, connection, transaction))
            {
                next++;
                insert.Parameters.AddWithValue("id", id);
                insert.Parameters.AddWithValue("tenant", tenantId.Value);
                insert.Parameters.AddWithValue("code", $"D-{next:D4}");
                insert.Parameters.AddWithValue("name", row.Name!);
                insert.Parameters.AddWithValue("status", located ? "located" : "no_location");
                insert.Parameters.AddWithValue("owner", item.OwnerId ?? actorId.Value);
                insert.Parameters.Add(new NpgsqlParameter("region", NpgsqlDbType.Uuid) { Value = (object?)item.RegionId ?? DBNull.Value });
                insert.Parameters.Add(new NpgsqlParameter("phone", NpgsqlDbType.Text) { Value = (object?)row.Phone ?? DBNull.Value });
                insert.Parameters.Add(new NpgsqlParameter("phone2", NpgsqlDbType.Text) { Value = (object?)row.Phone2 ?? DBNull.Value });
                insert.Parameters.Add(new NpgsqlParameter("address", NpgsqlDbType.Text) { Value = (object?)row.Address ?? DBNull.Value });
                insert.Parameters.Add(new NpgsqlParameter("place", NpgsqlDbType.Uuid) { Value = (object?)place ?? DBNull.Value });
                insert.Parameters.Add(new NpgsqlParameter("notes", NpgsqlDbType.Text) { Value = (object?)row.Notes ?? DBNull.Value });
                insert.Parameters.Add(new NpgsqlParameter("clinic", NpgsqlDbType.Text) { Value = (object?)row.ClinicName ?? DBNull.Value });
                insert.Parameters.Add(new NpgsqlParameter("specialty", NpgsqlDbType.Text) { Value = (object?)row.Specialty ?? DBNull.Value });
                insert.Parameters.Add(new NpgsqlParameter("cooperates", NpgsqlDbType.Boolean) { Value = (object?)row.Cooperates ?? DBNull.Value });
                insert.Parameters.Add(new NpgsqlParameter("dealer", NpgsqlDbType.Boolean) { Value = (object?)row.Dealer ?? DBNull.Value });
                insert.Parameters.Add(new NpgsqlParameter("class", NpgsqlDbType.Text) { Value = (object?)row.CustomerClass ?? DBNull.Value });
                if (located)
                {
                    insert.Parameters.AddWithValue("lat", row.Latitude!.Value);
                    insert.Parameters.AddWithValue("lon", row.Longitude!.Value);
                    insert.Parameters.AddWithValue("actor", actorId.Value);
                }
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }
            created++;
            await using var audit = new NpgsqlCommand("INSERT INTO audit_logs (id, tenant_id, actor_id, action, entity_id, occurred_at) VALUES (gen_random_uuid(), @tenant, @actor, 'customer.created', @entity, now())",
                connection, transaction);
            audit.Parameters.AddWithValue("tenant", tenantId.Value);
            audit.Parameters.AddWithValue("actor", actorId.Value);
            audit.Parameters.AddWithValue("entity", id.ToString());
            await audit.ExecuteNonQueryAsync(cancellationToken);
        }

        if (dryRun || created == 0) await transaction.RollbackAsync(cancellationToken);
        else
        {
            await using (var summary = new NpgsqlCommand("INSERT INTO audit_logs (id, tenant_id, actor_id, action, entity_id, occurred_at) VALUES (gen_random_uuid(), @tenant, @actor, 'customers.doctors_imported', @entity, now())", connection, transaction))
            {
                summary.Parameters.AddWithValue("tenant", tenantId.Value);
                summary.Parameters.AddWithValue("actor", actorId.Value);
                summary.Parameters.AddWithValue("entity", created.ToString());
                await summary.ExecuteNonQueryAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
        }
        return (created, duplicates);
    }
}
