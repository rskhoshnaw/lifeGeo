using System.Text.Json;
using LifeDrop.Application.Forms;
using LifeDrop.Domain.Common;
using Npgsql;
using NpgsqlTypes;

namespace LifeDrop.Infrastructure.Forms;

// Custom form-field definitions and answers with the tenant context set for RLS (plan step 3.2, part 2). Every change writes its audit row
// in the same transaction.
public sealed class PostgresFormFieldStore(NpgsqlDataSource dataSource) : IFormFieldStore
{
    private const string Columns = "id, key, field_type, applies_to, label_en, label_ckb, label_ar, label_fa, options, is_required, sort_order, is_active";

    public async Task<IReadOnlyList<FormFieldDto>> ListAsync(TenantId tenantId, bool includeInactive, string? visitType, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, cancellationToken);
        await using var command = new NpgsqlCommand($"""
            SELECT {Columns} FROM form_fields
            WHERE (@all OR is_active) AND (@type::text IS NULL OR applies_to IN ('all', @type))
            ORDER BY sort_order, key COLLATE "C"
            """, connection, transaction);
        command.Parameters.AddWithValue("all", includeInactive);
        command.Parameters.AddWithValue("type", (object?)visitType ?? DBNull.Value);
        var items = new List<FormFieldDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) items.Add(Read(reader));
        return items;
    }

    public async Task<FormFieldDto?> GetAsync(TenantId tenantId, Guid id, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, cancellationToken);
        await using var command = new NpgsqlCommand($"SELECT {Columns} FROM form_fields WHERE id = @id", connection, transaction);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    public async Task<int> CountActiveAsync(TenantId tenantId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, cancellationToken);
        await using var command = new NpgsqlCommand("SELECT count(*)::int FROM form_fields WHERE is_active", connection, transaction);
        return (int)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    public async Task<Guid?> CreateAsync(TenantId tenantId, UserId actor, string key, string type, string appliesTo, FormFieldData d, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, cancellationToken);
        try
        {
            Guid id;
            await using (var command = new NpgsqlCommand("""
                INSERT INTO form_fields (tenant_id, key, field_type, applies_to, label_en, label_ckb, label_ar, label_fa, options, is_required, sort_order, is_active, created_by)
                VALUES (@tenant, @key, @type, @applies, @en, @ckb, @ar, @fa, @options, @required, @sort, true, @actor) RETURNING id
                """, connection, transaction))
            {
                command.Parameters.AddWithValue("tenant", tenantId.Value);
                command.Parameters.AddWithValue("key", key);
                command.Parameters.AddWithValue("type", type);
                command.Parameters.AddWithValue("applies", appliesTo);
                command.Parameters.AddWithValue("actor", actor.Value);
                Bind(command, d);
                id = (Guid)(await command.ExecuteScalarAsync(cancellationToken))!;
            }
            await AuditAsync(connection, transaction, tenantId, actor, "form_field.created", id, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return id;
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return null;
        }
    }

    public async Task<bool> UpdateAsync(TenantId tenantId, UserId actor, Guid id, FormFieldData d, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, cancellationToken);
        await using (var command = new NpgsqlCommand("""
            UPDATE form_fields SET label_en = @en, label_ckb = @ckb, label_ar = @ar, label_fa = @fa, options = @options, is_required = @required, sort_order = @sort,
                is_active = @active, updated_at = now() WHERE id = @id
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("id", id);
            Bind(command, d);
            if (await command.ExecuteNonQueryAsync(cancellationToken) == 0) return false;
        }
        await AuditAsync(connection, transaction, tenantId, actor, d.IsActive ? "form_field.updated" : "form_field.deactivated", id, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<bool> SaveAnswersAsync(TenantId tenantId, UserId actor, VisitId visitId, IReadOnlyDictionary<Guid, string> answers, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, cancellationToken);
        var json = JsonSerializer.Serialize(answers.ToDictionary(p => p.Key.ToString(), p => p.Value));
        await using (var command = new NpgsqlCommand("""
            INSERT INTO visit_custom_answers (visit_id, tenant_id, answers, submitted_by)
            SELECT v.id, v.tenant_id, @answers, @actor FROM visits v WHERE v.tenant_id = @tenant AND v.id = @id AND v.user_id = @actor AND v.status = 'InVisit'
            ON CONFLICT (visit_id) DO UPDATE SET answers = EXCLUDED.answers, submitted_by = EXCLUDED.submitted_by, updated_at = now()
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("tenant", tenantId.Value);
            command.Parameters.AddWithValue("id", visitId.Value);
            command.Parameters.AddWithValue("actor", actor.Value);
            command.Parameters.Add(new NpgsqlParameter("answers", NpgsqlDbType.Jsonb) { Value = json });
            if (await command.ExecuteNonQueryAsync(cancellationToken) == 0) return false;
        }
        await AuditAsync(connection, transaction, tenantId, actor, "visit.custom_answers_saved", visitId.Value, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<CustomAnswersDto?> GetAnswersAsync(TenantId tenantId, VisitId visitId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, cancellationToken);
        string json;
        DateTimeOffset updated;
        await using (var command = new NpgsqlCommand("SELECT answers::text, updated_at FROM visit_custom_answers WHERE visit_id = @id", connection, transaction))
        {
            command.Parameters.AddWithValue("id", visitId.Value);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return null;
            json = reader.GetString(0);
            updated = reader.GetFieldValue<DateTimeOffset>(1);
        }
        var values = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [];
        var keys = new Dictionary<Guid, string>();
        await using (var command = new NpgsqlCommand("SELECT id, key FROM form_fields", connection, transaction))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken)) keys[reader.GetGuid(0)] = reader.GetString(1);
        var answers = values.Where(p => Guid.TryParse(p.Key, out var f) && keys.ContainsKey(f))
            .Select(p => new CustomAnswerDto(Guid.Parse(p.Key), keys[Guid.Parse(p.Key)], p.Value)).OrderBy(a => a.Key, StringComparer.Ordinal).ToList();
        return new CustomAnswersDto(visitId.Value, answers, updated);
    }

    private static void Bind(NpgsqlCommand command, FormFieldData d)
    {
        command.Parameters.AddWithValue("en", (object?)d.LabelEn ?? DBNull.Value);
        command.Parameters.AddWithValue("ckb", (object?)d.LabelCkb ?? DBNull.Value);
        command.Parameters.AddWithValue("ar", (object?)d.LabelAr ?? DBNull.Value);
        command.Parameters.AddWithValue("fa", (object?)d.LabelFa ?? DBNull.Value);
        command.Parameters.Add(new NpgsqlParameter("options", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = d.Options.ToArray() });
        command.Parameters.AddWithValue("required", d.IsRequired);
        command.Parameters.AddWithValue("sort", d.SortOrder);
        command.Parameters.AddWithValue("active", d.IsActive);
    }

    private static FormFieldDto Read(NpgsqlDataReader r) => new(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4),
        r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7), r.GetFieldValue<string[]>(8), r.GetBoolean(9), r.GetInt32(10), r.GetBoolean(11));

    private static async Task AuditAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, TenantId tenantId, UserId actor, string action, Guid entity, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("INSERT INTO audit_logs (id, tenant_id, actor_id, action, entity_id, occurred_at) VALUES (gen_random_uuid(), @tenant, @actor, @action, @entity, now())", connection, transaction);
        command.Parameters.AddWithValue("tenant", tenantId.Value);
        command.Parameters.AddWithValue("actor", actor.Value);
        command.Parameters.AddWithValue("action", action);
        command.Parameters.AddWithValue("entity", entity.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<NpgsqlTransaction> BeginAsync(NpgsqlConnection connection, TenantId tenantId, CancellationToken cancellationToken)
    {
        var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var set = new NpgsqlCommand("SELECT set_config('app.current_tenant', @tenant, true)", connection, transaction);
        set.Parameters.AddWithValue("tenant", tenantId.Value.ToString());
        await set.ExecuteNonQueryAsync(cancellationToken);
        return transaction;
    }
}
