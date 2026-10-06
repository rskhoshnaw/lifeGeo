using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LifeDrop.Application.Visits;
using LifeDrop.Domain.Common;
using LifeDrop.Domain.Visits;
using Npgsql;
using NpgsqlTypes;

namespace LifeDrop.Infrastructure.Visits;

public sealed class PostgresVisitDetailsStore(NpgsqlDataSource dataSource) : IVisitDetailsStore
{
    private static NpgsqlParameter P(string name, NpgsqlDbType type, object? value) => new(name, type) { Value = value ?? DBNull.Value };

    private static async Task<NpgsqlTransaction> BeginAsync(NpgsqlConnection connection, TenantId tenantId, UserId? actorId, CancellationToken cancellationToken)
    {
        var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var set = new NpgsqlCommand("SELECT set_config('app.current_tenant', @tenant, true), set_config('app.current_actor', @actor, true)", connection, transaction);
        set.Parameters.AddWithValue("tenant", tenantId.Value.ToString());
        set.Parameters.AddWithValue("actor", actorId?.Value.ToString() ?? "");
        await set.ExecuteNonQueryAsync(cancellationToken);
        return transaction;
    }

    public async Task<ManageVisitResult> SaveAsync(TenantId tenantId, UserId actorId, VisitId visitId, SaveVisitDetailsRequest d, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, actorId, cancellationToken);
        var ids = d.ProductIds?.ToArray() ?? Array.Empty<Guid>();
        if (ids.Length > 0)
        {
            await using var check = new NpgsqlCommand("SELECT count(*) FROM products WHERE tenant_id = @tenant AND id = ANY(@ids) AND status = 'active'", connection, transaction);
            check.Parameters.AddWithValue("tenant", tenantId.Value);
            check.Parameters.Add(P("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid, ids));
            if (Convert.ToInt32(await check.ExecuteScalarAsync(cancellationToken)) != ids.Length) return ManageVisitResult.InvalidData;
        }
        await using (var upsert = new NpgsqlCommand("""
            INSERT INTO visit_report_details (visit_id, tenant_id, purpose, person_met, person_role, needs, product_ids, specialty, daily_patients, monthly_sales, competitors,
                monthly_volume, payment_terms, currency, submitted_by)
            SELECT v.id, v.tenant_id, @purpose, @person, @role, @needs, @ids, @specialty, @patients, @sales, @competitors, @volume, @terms, @currency, @actor
            FROM visits v WHERE v.tenant_id = @tenant AND v.id = @id AND v.user_id = @actor AND v.status = 'InVisit'
            ON CONFLICT (visit_id) DO UPDATE
                SET purpose = EXCLUDED.purpose, person_met = EXCLUDED.person_met, person_role = EXCLUDED.person_role, needs = EXCLUDED.needs, product_ids = EXCLUDED.product_ids,
                    specialty = EXCLUDED.specialty, daily_patients = EXCLUDED.daily_patients, monthly_sales = EXCLUDED.monthly_sales, competitors = EXCLUDED.competitors,
                    monthly_volume = EXCLUDED.monthly_volume, payment_terms = EXCLUDED.payment_terms, currency = EXCLUDED.currency, submitted_by = EXCLUDED.submitted_by, updated_at = now()
            """, connection, transaction))
        {
            upsert.Parameters.AddWithValue("tenant", tenantId.Value);
            upsert.Parameters.AddWithValue("id", visitId.Value);
            upsert.Parameters.AddWithValue("actor", actorId.Value);
            upsert.Parameters.AddWithValue("purpose", d.Purpose!);
            upsert.Parameters.Add(P("person", NpgsqlDbType.Text, d.PersonMet));
            upsert.Parameters.Add(P("role", NpgsqlDbType.Text, d.PersonRole));
            upsert.Parameters.Add(P("needs", NpgsqlDbType.Text, d.Needs));
            upsert.Parameters.Add(P("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid, ids));
            upsert.Parameters.Add(P("specialty", NpgsqlDbType.Text, d.Specialty));
            upsert.Parameters.Add(P("patients", NpgsqlDbType.Integer, d.DailyPatients));
            upsert.Parameters.Add(P("sales", NpgsqlDbType.Numeric, d.MonthlySales));
            upsert.Parameters.Add(P("competitors", NpgsqlDbType.Text, d.Competitors));
            upsert.Parameters.Add(P("volume", NpgsqlDbType.Numeric, d.MonthlyVolume));
            upsert.Parameters.Add(P("terms", NpgsqlDbType.Text, d.PaymentTerms));
            upsert.Parameters.Add(P("currency", NpgsqlDbType.Text, d.Currency));
            if (await upsert.ExecuteNonQueryAsync(cancellationToken) == 0) return ManageVisitResult.Conflict;
        }
        await using (var audit = new NpgsqlCommand("""
            INSERT INTO audit_logs (id, tenant_id, actor_id, action, entity_id, occurred_at) VALUES (gen_random_uuid(), @tenant, @actor, 'visit.report_details_saved', @entity, now())
            """, connection, transaction))
        {
            audit.Parameters.AddWithValue("tenant", tenantId.Value);
            audit.Parameters.AddWithValue("actor", actorId.Value);
            audit.Parameters.AddWithValue("entity", visitId.Value.ToString());
            await audit.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return ManageVisitResult.Success;
    }

    public async Task<VisitType?> GetVisitTypeAsync(TenantId tenantId, VisitId visitId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, null, cancellationToken);
        await using var command = new NpgsqlCommand("SELECT visit_type FROM visits WHERE tenant_id = @tenant AND id = @id", connection, transaction);
        command.Parameters.AddWithValue("tenant", tenantId.Value);
        command.Parameters.AddWithValue("id", visitId.Value);
        return await command.ExecuteScalarAsync(cancellationToken) switch
        {
            "pharmacy" => VisitType.Pharmacy,
            "doctor" => VisitType.Doctor,
            "wholesaler" => VisitType.Wholesaler,
            "other" => VisitType.Other,
            _ => null
        };
    }

    public async Task<VisitDetailsDto?> GetAsync(TenantId tenantId, VisitId visitId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, null, cancellationToken);
        Guid[] ids;
        VisitDetailsDto? dto;
        await using (var command = new NpgsqlCommand("""
            SELECT d.visit_id, v.visit_type, d.purpose, d.person_met, d.person_role, d.needs, d.product_ids, d.specialty, d.daily_patients, d.monthly_sales, d.competitors,
                   d.monthly_volume, d.payment_terms, d.currency, d.submitted_at, d.updated_at
            FROM visit_report_details d JOIN visits v ON v.tenant_id = d.tenant_id AND v.id = d.visit_id
            WHERE d.tenant_id = @tenant AND d.visit_id = @id
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("tenant", tenantId.Value);
            command.Parameters.AddWithValue("id", visitId.Value);
            await using var r = await command.ExecuteReaderAsync(cancellationToken);
            if (!await r.ReadAsync(cancellationToken)) return null;
            ids = r.GetFieldValue<Guid[]>(6);
            dto = new VisitDetailsDto(r.GetGuid(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5),
                Array.Empty<DetailsProductDto>(), r.IsDBNull(7) ? null : r.GetString(7), r.IsDBNull(8) ? null : r.GetInt32(8), r.IsDBNull(9) ? null : r.GetDecimal(9),
                r.IsDBNull(10) ? null : r.GetString(10), r.IsDBNull(11) ? null : r.GetDecimal(11), r.IsDBNull(12) ? null : r.GetString(12), r.IsDBNull(13) ? null : r.GetString(13),
                r.GetFieldValue<DateTimeOffset>(14), r.GetFieldValue<DateTimeOffset>(15));
        }
        var products = new List<DetailsProductDto>();
        if (ids.Length > 0)
        {
            await using var command = new NpgsqlCommand("SELECT id, code, name FROM products WHERE tenant_id = @tenant AND id = ANY(@ids) ORDER BY code COLLATE \"C\"", connection, transaction);
            command.Parameters.AddWithValue("tenant", tenantId.Value);
            command.Parameters.Add(P("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid, ids));
            await using var r = await command.ExecuteReaderAsync(cancellationToken);
            while (await r.ReadAsync(cancellationToken)) products.Add(new DetailsProductDto(r.GetGuid(0), r.GetString(1), r.GetString(2)));
        }
        return dto with { Products = products };
    }
}
