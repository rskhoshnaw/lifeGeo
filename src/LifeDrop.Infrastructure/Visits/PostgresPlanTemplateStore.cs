using LifeDrop.Application.Visits;
using LifeDrop.Domain.Common;
using Npgsql;

namespace LifeDrop.Infrastructure.Visits;

// Plan templates (plan step 5.8): a week of visits saved as weekday + customer for one field user.
public sealed class PostgresPlanTemplateStore(NpgsqlDataSource dataSource) : IPlanTemplateStore
{
    private static async Task<NpgsqlTransaction> BeginAsync(NpgsqlConnection connection, TenantId tenantId, CancellationToken cancellationToken)
    {
        var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var set = new NpgsqlCommand("SELECT set_config('app.current_tenant', @tenant, true)", connection, transaction);
        set.Parameters.AddWithValue("tenant", tenantId.Value.ToString());
        await set.ExecuteNonQueryAsync(cancellationToken);
        return transaction;
    }

    public async Task<IReadOnlyList<WeekVisit>> WeekVisitsAsync(TenantId tenantId, Guid userId, DateOnly first, DateOnly last, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT schedule_date, customer_id FROM visits
            WHERE tenant_id = @tenant AND user_id = @user AND schedule_date BETWEEN @first AND @last AND status NOT IN ('Cancelled', 'Failed')
            ORDER BY schedule_date, sort_order NULLS LAST, planned_time NULLS LAST, created_at, id
            """, connection, transaction);
        command.Parameters.AddWithValue("tenant", tenantId.Value);
        command.Parameters.AddWithValue("user", userId);
        command.Parameters.AddWithValue("first", first);
        command.Parameters.AddWithValue("last", last);
        var items = new List<WeekVisit>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) items.Add(new WeekVisit(reader.GetFieldValue<DateOnly>(0), reader.GetGuid(1)));
        return items;
    }

    public async Task<Guid> CreateAsync(TenantId tenantId, UserId actorId, Guid userId, string name, IReadOnlyList<PlanTemplateItem> items, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, cancellationToken);
        Guid id;
        await using (var insert = new NpgsqlCommand("INSERT INTO plan_templates (tenant_id, user_id, name, created_by) VALUES (@tenant, @user, @name, @actor) RETURNING id", connection, transaction))
        {
            insert.Parameters.AddWithValue("tenant", tenantId.Value);
            insert.Parameters.AddWithValue("user", userId);
            insert.Parameters.AddWithValue("name", name);
            insert.Parameters.AddWithValue("actor", actorId.Value);
            id = (Guid)(await insert.ExecuteScalarAsync(cancellationToken))!;
        }
        for (var i = 0; i < items.Count; i++)
        {
            await using var item = new NpgsqlCommand("INSERT INTO plan_template_items (tenant_id, template_id, weekday, customer_id, position) VALUES (@tenant, @template, @weekday, @customer, @position)", connection, transaction);
            item.Parameters.AddWithValue("tenant", tenantId.Value);
            item.Parameters.AddWithValue("template", id);
            item.Parameters.AddWithValue("weekday", items[i].Weekday);
            item.Parameters.AddWithValue("customer", items[i].CustomerId);
            item.Parameters.AddWithValue("position", i);
            await item.ExecuteNonQueryAsync(cancellationToken);
        }
        await AuditAsync(connection, transaction, tenantId, actorId, "plan_template.created", id, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return id;
    }

    private const string Select = """
        SELECT t.id, t.user_id, u.email, t.name, (SELECT count(*)::int FROM plan_template_items i WHERE i.tenant_id = t.tenant_id AND i.template_id = t.id),
               (SELECT count(DISTINCT i.weekday)::int FROM plan_template_items i WHERE i.tenant_id = t.tenant_id AND i.template_id = t.id), t.created_at
        FROM plan_templates t LEFT JOIN users u ON u.tenant_id = t.tenant_id AND u.id = t.user_id
        """;

    private static PlanTemplateDto Read(NpgsqlDataReader reader) =>
        new(reader.GetGuid(0), reader.GetGuid(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetString(3), reader.GetInt32(4), reader.GetInt32(5), reader.GetFieldValue<DateTimeOffset>(6));

    public async Task<IReadOnlyList<PlanTemplateDto>> ListAsync(TenantId tenantId, Guid userId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, cancellationToken);
        await using var command = new NpgsqlCommand(Select + " WHERE t.tenant_id = @tenant AND t.user_id = @user ORDER BY t.created_at DESC, t.id", connection, transaction);
        command.Parameters.AddWithValue("tenant", tenantId.Value);
        command.Parameters.AddWithValue("user", userId);
        var items = new List<PlanTemplateDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) items.Add(Read(reader));
        return items;
    }

    public async Task<PlanTemplateDto?> GetAsync(TenantId tenantId, Guid id, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, cancellationToken);
        await using var command = new NpgsqlCommand(Select + " WHERE t.tenant_id = @tenant AND t.id = @id", connection, transaction);
        command.Parameters.AddWithValue("tenant", tenantId.Value);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    public async Task<IReadOnlyList<PlanTemplateItem>> ItemsAsync(TenantId tenantId, Guid id, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, cancellationToken);
        await using var command = new NpgsqlCommand("SELECT weekday, customer_id FROM plan_template_items WHERE tenant_id = @tenant AND template_id = @id ORDER BY weekday, position", connection, transaction);
        command.Parameters.AddWithValue("tenant", tenantId.Value);
        command.Parameters.AddWithValue("id", id);
        var items = new List<PlanTemplateItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) items.Add(new PlanTemplateItem(reader.GetInt32(0), reader.GetGuid(1)));
        return items;
    }

    public async Task<bool> DeleteAsync(TenantId tenantId, UserId actorId, Guid id, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await BeginAsync(connection, tenantId, cancellationToken);
        await using (var delete = new NpgsqlCommand("DELETE FROM plan_templates WHERE tenant_id = @tenant AND id = @id", connection, transaction))
        {
            delete.Parameters.AddWithValue("tenant", tenantId.Value);
            delete.Parameters.AddWithValue("id", id);
            if (await delete.ExecuteNonQueryAsync(cancellationToken) == 0) return false;
        }
        await AuditAsync(connection, transaction, tenantId, actorId, "plan_template.deleted", id, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
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
}
