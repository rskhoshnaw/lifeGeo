using LifeDrop.Application.Marketing;
using LifeDrop.Application.Roles;
using LifeDrop.Domain.Common;
using LifeDrop.Infrastructure.Marketing;
using Npgsql;
using Xunit;

namespace LifeDrop.Database.Tests;

// The marketing store of plan step 5.7 against every migration in an isolated schema: a campaign queues one message per approved customer inside the scope that has a usable phone (the same
// phone only once, no phone skipped), fills {name}, a rep sees only their own campaigns and customers, "sent" works once.
public class MarketingStoreTests
{
    [Fact]
    public async Task A_campaign_queues_the_customers_of_the_scope_and_each_message_is_handled_once()
    {
        var url = Environment.GetEnvironmentVariable("TEST_DATABASE_URL")
            ?? throw new InvalidOperationException("TEST_DATABASE_URL must name an isolated lifedrop_test_* database.");
        var builder = new NpgsqlConnectionStringBuilder(url);
        if (builder.Database is null || !builder.Database.StartsWith("lifedrop_test_", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Refusing to test a database outside lifedrop_test_*.");

        var schema = "marketing_test_" + Guid.NewGuid().ToString("N");
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

            Guid tenant = Guid.NewGuid(), rep = Guid.NewGuid(), other = Guid.NewGuid(), manager = Guid.NewGuid();
            Guid cShifa = Guid.NewGuid(), cDup = Guid.NewGuid(), cNoPhone = Guid.NewGuid(), cShort = Guid.NewGuid(), cPending = Guid.NewGuid(), cOther = Guid.NewGuid(), cDoctor = Guid.NewGuid();
            await using (var seed = new NpgsqlCommand("""
                INSERT INTO tenants(id,name,code) VALUES (@tenant,'A','a');
                INSERT INTO users(id,tenant_id,email) VALUES (@rep,@tenant,'rep@a.test'),(@other,@tenant,'other@a.test'),(@manager,@tenant,'manager@a.test');
                """, connection))
            {
                seed.Parameters.AddWithValue("tenant", tenant);
                seed.Parameters.AddWithValue("rep", rep);
                seed.Parameters.AddWithValue("other", other);
                seed.Parameters.AddWithValue("manager", manager);
                await seed.ExecuteNonQueryAsync();
            }
            await InTenant(connection, tenant, $"""
                INSERT INTO customers(id,tenant_id,type,code,name,phone,owner_user_id) VALUES
                    ('{cShifa}','{tenant}','Pharmacy','P-1','Shifa','0750 111 2233','{rep}'),
                    ('{cDup}','{tenant}','Pharmacy','P-2','Shifa Branch','+964 750 111 2233','{rep}'),
                    ('{cNoPhone}','{tenant}','Pharmacy','P-3','NoPhone',NULL,'{rep}'),
                    ('{cShort}','{tenant}','Pharmacy','P-4','Short','12','{rep}'),
                    ('{cDoctor}','{tenant}','Doctor','D-1','Dr Ali','0770 555 6677','{rep}'),
                    ('{cOther}','{tenant}','Pharmacy','P-5','Others','0780 999 8888','{other}');
                INSERT INTO customers(id,tenant_id,type,code,name,phone,owner_user_id,approval_status) VALUES ('{cPending}','{tenant}','Pharmacy','P-6','Pending','0790 111 0000','{rep}','pending');
                """);

            var store = new PostgresMarketingStore(dataSource);
            var t = new TenantId(tenant);
            var own = new AccessScope(DataScope.Own, new UserId(rep), null);
            var wide = AccessScope.WholeTenant(new UserId(manager));

            var template = await store.CreateTemplateAsync(t, new UserId(manager), "Offer", "Hello {name}, new prices", CancellationToken.None);
            Assert.Single(await store.ListTemplatesAsync(t, false, CancellationToken.None));
            Assert.True(await store.UpdateTemplateAsync(t, new UserId(manager), template, "Offer 2", "Hi {name}", true, CancellationToken.None));
            Assert.False(await store.UpdateTemplateAsync(t, new UserId(manager), Guid.NewGuid(), "x", "y", true, CancellationToken.None));

            var (missing, _) = await store.CreateCampaignAsync(t, new UserId(rep), own, "A", Guid.NewGuid(), [], null, CancellationToken.None);
            Assert.Equal(MarketingResult.NotFound, missing);

            var (result, campaign) = await store.CreateCampaignAsync(t, new UserId(rep), own, "Spring", template, ["pharmacy"], null, CancellationToken.None);
            Assert.Equal(MarketingResult.Success, result);
            Assert.Equal((1, 2), (campaign!.Queued, campaign.SkippedNoPhone));   // Shifa only (Shifa Branch has the same phone); no phone and "12" are skipped; not the doctor, pending or others'
            var repScope = new AccessScope(DataScope.Own, new UserId(rep), null);
            var messages = (await store.ListMessagesAsync(t, repScope, campaign.Id, null, CancellationToken.None))!;
            var first = Assert.Single(messages);
            Assert.Equal(("Hi Shifa", "queued", "0750 111 2233"), (first.Message, first.Status, first.Phone));

            var (_, withDoctors) = await store.CreateCampaignAsync(t, new UserId(rep), own, "All", template, [], null, CancellationToken.None);
            Assert.Equal(2, withDoctors!.Queued);   // the doctor joins when no type is chosen

            Assert.Equal(new[] { "All", "Spring" }, (await store.ListCampaignsAsync(t, repScope, CancellationToken.None)).Select(c => c.Name).Order(StringComparer.Ordinal).ToArray());
            Assert.Empty(await store.ListCampaignsAsync(t, new AccessScope(DataScope.Own, new UserId(other), null), CancellationToken.None));
            Assert.Equal(2, (await store.ListCampaignsAsync(t, wide, CancellationToken.None)).Count);   // the tenant-wide scope sees everybody's
            Assert.Null(await store.ListMessagesAsync(t, new AccessScope(DataScope.Own, new UserId(other), null), campaign.Id, null, CancellationToken.None));

            Assert.Equal(MarketingResult.NotFound, await store.HandleAsync(t, new AccessScope(DataScope.Own, new UserId(other), null), first.Id, "sent", CancellationToken.None));
            Assert.Equal(MarketingResult.Success, await store.HandleAsync(t, repScope, first.Id, "sent", CancellationToken.None));
            Assert.Equal(MarketingResult.Conflict, await store.HandleAsync(t, repScope, first.Id, "skipped", CancellationToken.None));   // only once
            var summary = Assert.Single((await store.ListCampaignsAsync(t, repScope, CancellationToken.None)).Where(c => c.Id == campaign.Id));
            Assert.Equal((0, 1, 0), (summary.Queued, summary.Sent, summary.Skipped));
            Assert.Equal("2", await InTenant(connection, tenant, "SELECT count(*) FROM audit_logs WHERE action = 'marketing.campaign_created'"));
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE", setup);
            await drop.ExecuteNonQueryAsync();
        }
    }

    // The tables have row level security: every statement runs in a transaction that names the company, as the API does.
    private static async Task<string> InTenant(NpgsqlConnection connection, Guid tenant, string sql)
    {
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var set = new NpgsqlCommand("SELECT set_config('app.current_tenant', @tenant, true)", connection, transaction))
        {
            set.Parameters.AddWithValue("tenant", tenant.ToString());
            await set.ExecuteNonQueryAsync();
        }
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        var value = Convert.ToString(await command.ExecuteScalarAsync()) ?? "";
        await transaction.CommitAsync();
        return value;
    }
}
