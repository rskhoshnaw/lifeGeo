using LifeDrop.Application.Roles;
using LifeDrop.Application.Visits;
using LifeDrop.Domain.Common;
using LifeDrop.Infrastructure.Visits;
using Npgsql;
using Xunit;

namespace LifeDrop.Database.Tests;

// Plan templates of plan step 5.8 against every migration in an isolated schema: a week of visits is saved (cancelled ones left out), applied through the real plan generation to two coming
// weeks (a day that already has the visit is skipped), and deleted.
public class PlanTemplateStoreTests
{
    [Fact]
    public async Task A_saved_week_is_applied_to_the_coming_weeks_without_double_planning()
    {
        var url = Environment.GetEnvironmentVariable("TEST_DATABASE_URL")
            ?? throw new InvalidOperationException("TEST_DATABASE_URL must name an isolated lifedrop_test_* database.");
        var builder = new NpgsqlConnectionStringBuilder(url);
        if (builder.Database is null || !builder.Database.StartsWith("lifedrop_test_", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Refusing to test a database outside lifedrop_test_*.");

        var schema = "plantpl_test_" + Guid.NewGuid().ToString("N");
        await using var setup = new NpgsqlConnection(url);
        await setup.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE SCHEMA {schema}", setup))
            await create.ExecuteNonQueryAsync();

        try
        {
            builder.SearchPath = $"{schema},public";
            await using var dataSource = NpgsqlDataSource.Create(builder.ConnectionString);
            await using var connection = await dataSource.OpenConnectionAsync();
            var all = string.Concat(Directory.GetFiles(AppContext.BaseDirectory, "00??_*.sql")
                .Select(Path.GetFileName).OrderBy(f => f, StringComparer.Ordinal)
                .Select(f => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, f!))));
            await using (var migrate = new NpgsqlCommand($"SET search_path TO {schema}, public; {all}", connection))
                await migrate.ExecuteNonQueryAsync();

            Guid tenant = Guid.NewGuid(), rep = Guid.NewGuid(), other = Guid.NewGuid();
            Guid cA = Guid.NewGuid(), cB = Guid.NewGuid(), cC = Guid.NewGuid();
            // The clock stands on Monday 2026-10-26 (Iraq). The saved week is 2026-10-19 .. 2026-10-25 (Monday: A and B; Wednesday: A; a cancelled visit of C is left out).
            await using (var seed = new NpgsqlCommand("""
                INSERT INTO tenants(id,name,code) VALUES (@tenant,'A','a');
                INSERT INTO users(id,tenant_id,email) VALUES (@rep,@tenant,'rep@a.test'),(@other,@tenant,'other@a.test');
                """, connection))
            {
                seed.Parameters.AddWithValue("tenant", tenant);
                seed.Parameters.AddWithValue("rep", rep);
                seed.Parameters.AddWithValue("other", other);
                await seed.ExecuteNonQueryAsync();
            }
            await InTenant(connection, tenant, rep, $"""
                INSERT INTO customers(id,tenant_id,type,code,name,owner_user_id) VALUES
                    ('{cA}','{tenant}','Pharmacy','P-1','Shifa','{rep}'),('{cB}','{tenant}','Pharmacy','P-2','Noor','{rep}'),('{cC}','{tenant}','Pharmacy','P-3','Zain','{rep}');
                INSERT INTO visits(id,tenant_id,user_id,customer_id,status,schedule_date,created_at,visit_type,priority,is_unplanned,sort_order) VALUES
                    (gen_random_uuid(),'{tenant}','{rep}','{cA}','Planned','2026-10-19',now(),'pharmacy','normal',false,1),
                    (gen_random_uuid(),'{tenant}','{rep}','{cB}','Planned','2026-10-19',now(),'pharmacy','normal',false,2),
                    (gen_random_uuid(),'{tenant}','{rep}','{cA}','Planned','2026-10-21',now(),'pharmacy','normal',false,1),
                    (gen_random_uuid(),'{tenant}','{rep}','{cC}','Cancelled','2026-10-21',now(),'pharmacy','normal',false,2),
                    (gen_random_uuid(),'{tenant}','{rep}','{cA}','Planned','2026-11-04',now(),'pharmacy','normal',false,1);
                """);

            var t = new TenantId(tenant);
            var me = new UserId(rep);
            var clock = new Clock();
            var generation = new PostgresPlanGenerationStore(dataSource);
            var permissions = new Permissions();
            var service = new PlanTemplates(new PostgresPlanTemplateStore(dataSource), new PlanGenerator(generation, permissions, clock), permissions, clock);

            var (saved, id) = await service.SaveAsync(t, me, new SavePlanTemplateRequest("Erbil week", new DateOnly(2026, 10, 19)), CancellationToken.None);
            Assert.Equal(PlanTemplateResult.Success, saved);
            var list = Assert.Single((await service.ListAsync(t, me, null, CancellationToken.None)).Items);
            Assert.Equal(("Erbil week", "rep@a.test", 3, 2), (list.Name, list.UserEmail, list.Customers, list.Days));   // A and B on Monday, A on Wednesday
            Assert.Empty((await service.ListAsync(t, new UserId(other), null, CancellationToken.None)).Items);

            // Two weeks from 2026-10-26: Monday 26th (A, B), Wednesday 28th (A), Monday 2 Nov (A, B), Wednesday 4 Nov (A is already planned there).
            var (applied, result) = await service.ApplyAsync(t, me, id, new ApplyPlanTemplateRequest(new DateOnly(2026, 10, 26), 2), CancellationToken.None);
            Assert.Equal(PlanTemplateResult.Success, applied);
            Assert.Equal((5, 1, 3), (result!.Created, result.Skipped, result.Days));   // A, B / A / A, B created; A on 4 Nov skipped; 4 Nov had nothing new
            Assert.Equal("8", await InTenant(connection, tenant, rep, $"SELECT count(*) FROM visits WHERE user_id = '{rep}' AND status = 'Planned'"));   // 3 before (19th B, 21st A, 4 Nov A) + 5 new
            Assert.Equal("1", await InTenant(connection, tenant, rep, $"SELECT count(*) FROM visits WHERE user_id = '{rep}' AND schedule_date = '2026-11-04'"));

            // Again: everything is planned already, nothing new.
            var (_, again) = await service.ApplyAsync(t, me, id, new ApplyPlanTemplateRequest(new DateOnly(2026, 10, 26), 2), CancellationToken.None);
            Assert.Equal((0, 6), (again!.Created, again.Skipped));

            Assert.Equal(PlanTemplateResult.NotFound, await service.DeleteAsync(t, new UserId(other), id, CancellationToken.None));   // not theirs (they cannot plan for the rep)
            Assert.Equal(PlanTemplateResult.Success, await service.DeleteAsync(t, me, id, CancellationToken.None));
            Assert.Empty((await service.ListAsync(t, me, null, CancellationToken.None)).Items);
            Assert.Equal("0", await InTenant(connection, tenant, rep, "SELECT count(*) FROM plan_template_items"));
            Assert.Equal("1", await InTenant(connection, tenant, rep, "SELECT count(*) FROM audit_logs WHERE action = 'plan_template.deleted'"));
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE", setup);
            await drop.ExecuteNonQueryAsync();
        }
    }

    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 10, 26, 9, 0, 0, TimeSpan.Zero);
    }

    // A field user: may plan for themselves and read customers.
    private sealed class Permissions : IPermissionStore
    {
        public Task<bool> HasPermissionAsync(TenantId tenantId, UserId userId, string permissionKey, CancellationToken cancellationToken) =>
            Task.FromResult(permissionKey is PermissionKeys.VisitsPlan or PermissionKeys.CustomersRead);
    }

    // The tables have row level security: every statement runs in a transaction that names the company, as the API does.
    private static async Task<string> InTenant(NpgsqlConnection connection, Guid tenant, Guid actor, string sql)
    {
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var set = new NpgsqlCommand("SELECT set_config('app.current_tenant', @tenant, true), set_config('app.current_actor', @actor, true)", connection, transaction))
        {
            set.Parameters.AddWithValue("tenant", tenant.ToString());
            set.Parameters.AddWithValue("actor", actor.ToString());
            await set.ExecuteNonQueryAsync();
        }
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        var value = Convert.ToString(await command.ExecuteScalarAsync()) ?? "";
        await transaction.CommitAsync();
        return value;
    }
}
