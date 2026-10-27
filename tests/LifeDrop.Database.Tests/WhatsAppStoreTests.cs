using LifeDrop.Application.Marketing;
using LifeDrop.Application.Roles;
using LifeDrop.Domain.Common;
using LifeDrop.Infrastructure.Marketing;
using Npgsql;
using Xunit;

namespace LifeDrop.Database.Tests;

// The "api" campaigns of the WhatsApp Business API against every migration in an isolated schema: only customers who agreed are queued, a template without a WhatsApp name cannot be sent by the
// API, the sender claims what is due once, a withdrawn consent stops a message, and retry / failed / sent are written; the user cannot mark an api message by hand.
public class WhatsAppStoreTests
{
    [Fact]
    public async Task Api_campaigns_queue_only_customers_who_agreed_and_the_sender_claims_and_marks_them()
    {
        var url = Environment.GetEnvironmentVariable("TEST_DATABASE_URL")
            ?? throw new InvalidOperationException("TEST_DATABASE_URL must name an isolated lifedrop_test_* database.");
        var builder = new NpgsqlConnectionStringBuilder(url);
        if (builder.Database is null || !builder.Database.StartsWith("lifedrop_test_", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Refusing to test a database outside lifedrop_test_*.");

        var schema = "whatsapp_test_" + Guid.NewGuid().ToString("N");
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

            Guid tenant = Guid.NewGuid(), rep = Guid.NewGuid(), manager = Guid.NewGuid();
            Guid cYes = Guid.NewGuid(), cNo = Guid.NewGuid(), cBadPhone = Guid.NewGuid();
            await using (var seed = new NpgsqlCommand("""
                INSERT INTO tenants(id,name,code) VALUES (@tenant,'A','a');
                INSERT INTO users(id,tenant_id,email) VALUES (@rep,@tenant,'rep@a.test'),(@manager,@tenant,'manager@a.test');
                """, connection))
            {
                seed.Parameters.AddWithValue("tenant", tenant);
                seed.Parameters.AddWithValue("rep", rep);
                seed.Parameters.AddWithValue("manager", manager);
                await seed.ExecuteNonQueryAsync();
            }
            await InTenant(connection, tenant, $"""
                INSERT INTO customers(id,tenant_id,type,code,name,phone,owner_user_id) VALUES
                    ('{cYes}','{tenant}','Pharmacy','P-1','Shifa','0750 111 2233','{rep}'),
                    ('{cNo}','{tenant}','Pharmacy','P-2','Noor','0770 555 6677','{rep}'),
                    ('{cBadPhone}','{tenant}','Pharmacy','P-3','Zain','12','{rep}');
                """);

            var marketing = new PostgresMarketingStore(dataSource);
            var sender = new PostgresWhatsAppStore(dataSource);
            var t = new TenantId(tenant);
            var own = new AccessScope(DataScope.Own, new UserId(rep), null);

            // Consent: given for one customer, with its audit row; read back inside the scope only.
            Assert.True(await marketing.SetConsentAsync(t, new UserId(rep), own, cYes, true, CancellationToken.None));
            Assert.True(await marketing.SetConsentAsync(t, new UserId(rep), own, cBadPhone, true, CancellationToken.None));
            Assert.False(await marketing.SetConsentAsync(t, new UserId(manager), new AccessScope(DataScope.Own, new UserId(manager), null), cYes, true, CancellationToken.None));   // not their customer
            var consent = await marketing.GetConsentAsync(t, own, cYes, CancellationToken.None);
            Assert.True(consent!.OptIn);
            Assert.NotNull(consent.At);
            Assert.False((await marketing.GetConsentAsync(t, own, cNo, CancellationToken.None))!.OptIn);
            Assert.Null(await marketing.GetConsentAsync(t, new AccessScope(DataScope.Own, new UserId(manager), null), cYes, CancellationToken.None));
            Assert.Equal("2", await InTenant(connection, tenant, "SELECT count(*) FROM audit_logs WHERE action = 'customer.whatsapp_consent_given'"));

            // A plain template cannot be sent by the API; one with the approved WhatsApp name can.
            var plain = await marketing.CreateTemplateAsync(t, new UserId(manager), "Plain", "Hi {name}", null, null, CancellationToken.None);
            var approved = await marketing.CreateTemplateAsync(t, new UserId(manager), "Spring", "Hi {name}, new prices", "spring_offer", "ckb", CancellationToken.None);
            Assert.Equal("spring_offer|ckb", (await marketing.ListTemplatesAsync(t, false, CancellationToken.None)).Where(x => x.Id == approved).Select(x => x.WhatsAppName + "|" + x.WhatsAppLanguage).Single());
            var (noName, _) = await marketing.CreateCampaignAsync(t, new UserId(rep), own, "X", plain, [], null, "api", CancellationToken.None);
            Assert.Equal(MarketingResult.InvalidData, noName);

            var (result, campaign) = await marketing.CreateCampaignAsync(t, new UserId(rep), own, "Spring by API", approved, [], null, "api", CancellationToken.None);
            Assert.Equal(MarketingResult.Success, result);
            Assert.Equal((1, 1, 1), (campaign!.Queued, campaign.SkippedNoPhone, campaign.SkippedNoConsent));   // Shifa only; Zain agreed but the phone is unusable; Noor did not agree
            var queue = (await marketing.ListMessagesAsync(t, own, campaign.Id, "queued", CancellationToken.None))!;
            var message = Assert.Single(queue);
            Assert.Equal("Hi Shifa", message.Message[..8]);

            // A manual campaign is never touched by the sender; the user cannot mark an api message by hand.
            var (_, manual) = await marketing.CreateCampaignAsync(t, new UserId(rep), own, "By hand", plain, [], null, "manual", CancellationToken.None);
            Assert.Equal(2, manual!.Queued);   // Shifa and Noor (consent is not needed for the free way)
            Assert.Equal(MarketingResult.Conflict, await marketing.HandleAsync(t, own, message.Id, "sent", CancellationToken.None));

            // The sender claims the due message once; for the next 5 minutes it is hidden.
            var due = Assert.Single(await sender.ClaimDueAsync(t, 20, CancellationToken.None));
            Assert.Equal((message.Id, "Shifa", "spring_offer", "ckb", 1), (due.MessageId, due.CustomerName, due.Template, due.Language, due.Attempts));
            Assert.Empty(await sender.ClaimDueAsync(t, 20, CancellationToken.None));

            // A temporary failure: tried again when due; a withdrawn consent stops the message.
            await sender.MarkRetryAsync(t, message.Id, DateTimeOffset.UtcNow.AddMinutes(-1), "429: rate limit", CancellationToken.None);
            Assert.True(await marketing.SetConsentAsync(t, new UserId(rep), own, cYes, false, CancellationToken.None));
            Assert.Empty(await sender.ClaimDueAsync(t, 20, CancellationToken.None));
            Assert.True(await marketing.SetConsentAsync(t, new UserId(rep), own, cYes, true, CancellationToken.None));
            Assert.Equal(2, Assert.Single(await sender.ClaimDueAsync(t, 20, CancellationToken.None)).Attempts);

            // Given up: failed with the reason, counted on the campaign, shown in the list.
            await sender.MarkFailedAsync(t, message.Id, "400: 132001 Template name does not exist", CancellationToken.None);
            var summary = Assert.Single((await marketing.ListCampaignsAsync(t, own, CancellationToken.None)).Where(c => c.Id == campaign.Id));
            Assert.Equal(("api", 0, 0, 1), (summary.Delivery, summary.Queued, summary.Sent, summary.Failed));
            var failed = Assert.Single((await marketing.ListMessagesAsync(t, own, campaign.Id, null, CancellationToken.None))!);
            Assert.Equal(("failed", "400: 132001 Template name does not exist"), (failed.Status, failed.Error));
            Assert.Empty(await sender.ClaimDueAsync(t, 20, CancellationToken.None));

            // Sent: the id Meta gave is kept.
            var (_, second) = await marketing.CreateCampaignAsync(t, new UserId(rep), own, "Again", approved, [], null, "api", CancellationToken.None);
            var again = Assert.Single(await sender.ClaimDueAsync(t, 20, CancellationToken.None));
            await sender.MarkSentAsync(t, again.MessageId, "wamid.1", CancellationToken.None);
            Assert.Equal("sent|wamid.1", await InTenant(connection, tenant, $"SELECT status || '|' || provider_message_id FROM marketing_messages WHERE id = '{again.MessageId}'"));
            Assert.Equal(1, Assert.Single((await marketing.ListCampaignsAsync(t, own, CancellationToken.None)).Where(c => c.Id == second!.Id)).Sent);
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
