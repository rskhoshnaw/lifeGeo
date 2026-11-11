using LifeDrop.Application.Feedback;
using LifeDrop.Application.Roles;
using LifeDrop.Domain.Common;
using LifeDrop.Infrastructure.Feedback;
using Npgsql;
using Xunit;

namespace LifeDrop.Database.Tests;

// Feedback from the field (plan step 5.30) against every migration in an isolated schema: who sees which feedback, the customer link, photos, answers, status and the
// notifications to the managers and to the author.
public class FeedbackStoreTests
{
    [Fact]
    public async Task Feedback_is_visible_by_scope_with_photos_answers_status_and_notifications()
    {
        var url = Environment.GetEnvironmentVariable("TEST_DATABASE_URL")
            ?? throw new InvalidOperationException("TEST_DATABASE_URL must name an isolated lifedrop_test_* database.");
        var builder = new NpgsqlConnectionStringBuilder(url);
        if (builder.Database is null || !builder.Database.StartsWith("lifedrop_test_", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Refusing to test a database outside lifedrop_test_*.");

        var schema = "feedback_test_" + Guid.NewGuid().ToString("N");
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

            Guid tenant = Guid.NewGuid(), other = Guid.NewGuid();
            Guid boss = Guid.NewGuid(), sup = Guid.NewGuid(), supB = Guid.NewGuid(), ann = Guid.NewGuid(), bob = Guid.NewGuid(), carl = Guid.NewGuid(), foreign = Guid.NewGuid();
            Guid regionA = Guid.NewGuid(), regionB = Guid.NewGuid(), teamA = Guid.NewGuid(), teamB = Guid.NewGuid(), c1 = Guid.NewGuid(), c2 = Guid.NewGuid();
            await using (var seed = new NpgsqlCommand("""
                INSERT INTO tenants(id,name,code) VALUES (@tenant,'A','a'),(@other,'B','b');
                INSERT INTO users(id,tenant_id,email,is_active) VALUES (@boss,@tenant,'boss@x.com',true),(@sup,@tenant,'sup@x.com',true),(@supB,@tenant,'supb@x.com',true),
                    (@ann,@tenant,'ann@x.com',true),(@bob,@tenant,'bob@x.com',true),(@carl,@tenant,'carl@x.com',true),(@foreign,@other,'foreign@x.com',true);
                """, connection))
            {
                foreach (var (name, value) in new[] { ("tenant", tenant), ("other", other), ("boss", boss), ("sup", sup), ("supB", supB), ("ann", ann), ("bob", bob), ("carl", carl), ("foreign", foreign) })
                    seed.Parameters.AddWithValue(name, value);
                await seed.ExecuteNonQueryAsync();
            }
            await InTenant(connection, tenant, $"""
                SELECT seed_default_roles('{tenant}');
                INSERT INTO user_roles(tenant_id,user_id,role_id) SELECT '{tenant}','{boss}',id FROM roles WHERE tenant_id = '{tenant}' AND name = 'CompanyManager';
                INSERT INTO user_roles(tenant_id,user_id,role_id) SELECT '{tenant}',u,id FROM roles, (VALUES ('{sup}'::uuid),('{supB}'::uuid)) AS v(u) WHERE tenant_id = '{tenant}' AND name = 'Supervisor';
                INSERT INTO user_roles(tenant_id,user_id,role_id) SELECT '{tenant}',u,id FROM roles, (VALUES ('{ann}'::uuid),('{bob}'::uuid),('{carl}'::uuid)) AS v(u) WHERE tenant_id = '{tenant}' AND name = 'SalesRep';
                INSERT INTO regions(id,tenant_id,name) VALUES ('{regionA}','{tenant}','A'),('{regionB}','{tenant}','B');
                INSERT INTO teams(id,tenant_id,name,region_id) VALUES ('{teamA}','{tenant}','Team A','{regionA}'),('{teamB}','{tenant}','Team B','{regionB}');
                INSERT INTO team_members(team_id,tenant_id,user_id) VALUES ('{teamA}','{tenant}','{sup}'),('{teamA}','{tenant}','{ann}'),('{teamB}','{tenant}','{supB}'),('{teamB}','{tenant}','{bob}');
                INSERT INTO customers(id,tenant_id,type,code,name,owner_user_id) VALUES ('{c1}','{tenant}','Pharmacy','F-1','Shifa','{ann}'),('{c2}','{tenant}','Pharmacy','F-2','Other','{bob}');
                """);

            var store = new PostgresFeedbackStore(dataSource);
            var t = new TenantId(tenant);
            var mine = new AccessScope(DataScope.Own, new UserId(ann), regionA);
            FeedbackAccess Own(Guid user) => new(new UserId(user), null);
            var bossAccess = new FeedbackAccess(new UserId(boss), AccessScope.WholeTenant(new UserId(boss)));
            var supAccess = new FeedbackAccess(new UserId(sup), new AccessScope(DataScope.Region, new UserId(sup), regionA));
            async Task<string> Ids(FeedbackAccess access, bool onlyMine = false, string? status = null, string? kind = null, Guid? user = null, Guid? customer = null, DateTimeOffset? before = null) =>
                string.Join(",", (await store.ListAsync(t, access, new FeedbackFilter(onlyMine, status, kind, user, customer, before, 50), CancellationToken.None)).Select(f => f.Body).OrderBy(b => b, StringComparer.Ordinal));

            // Create: a customer is linked only when it is in the author's scope; the region of the author's team is kept.
            var f1 = (await store.CreateAsync(t, new UserId(ann), "suggestion", "A1 about Shifa", c1, mine, CancellationToken.None))!.Value;
            Assert.Null(await store.CreateAsync(t, new UserId(ann), "note", "bad link", c2, mine, CancellationToken.None));         // Bob's customer is not Ann's to see
            var f2 = (await store.CreateAsync(t, new UserId(bob), "complaint", "B1", null, null, CancellationToken.None))!.Value;
            var f3 = (await store.CreateAsync(t, new UserId(carl), "note", "C1 no team", null, null, CancellationToken.None))!.Value;   // Carl has no team: no region
            Assert.Equal(regionA.ToString(), await InTenant(connection, tenant, $"SELECT region_id::text FROM feedback WHERE id = '{f1}'"));
            Assert.Equal("", await InTenant(connection, tenant, $"SELECT coalesce(region_id::text, '') FROM feedback WHERE id = '{f3}'"));

            // Who sees what: the author their own, the whole-company manager all, the supervisor the own region (and own), nobody another company's.
            Assert.Equal("A1 about Shifa", await Ids(Own(ann)));
            Assert.Equal("A1 about Shifa,B1,C1 no team", await Ids(bossAccess));
            Assert.Equal("A1 about Shifa", await Ids(supAccess));
            Assert.Empty(await store.ListAsync(new TenantId(other), new FeedbackAccess(new UserId(foreign), AccessScope.WholeTenant(new UserId(foreign))), new FeedbackFilter(false, null, null, null, null, null, 50), CancellationToken.None));   // another company sees none of it
            Assert.Equal("", await Ids(bossAccess, onlyMine: true));                                                                 // "mine" is only their own
            Assert.Equal("B1", await Ids(bossAccess, kind: "complaint"));
            Assert.Equal("A1 about Shifa", await Ids(bossAccess, user: ann));
            Assert.Equal("A1 about Shifa", await Ids(bossAccess, customer: c1));
            Assert.Equal("", await Ids(bossAccess, status: "done"));
            Assert.Equal("", await Ids(bossAccess, before: DateTimeOffset.UtcNow.AddDays(-1)));
            var one = (await store.GetAsync(t, bossAccess, f1, CancellationToken.None))!;
            Assert.Equal(("suggestion", "ann@x.com", "F-1", "Shifa", "new"), (one.Kind, one.UserEmail, one.CustomerCode, one.CustomerName, one.Status));
            Assert.Null(await store.GetAsync(t, Own(bob), f1, CancellationToken.None));                                              // another user's feedback is not found
            Assert.Null(await store.GetAsync(t, supAccess, f2, CancellationToken.None));                                             // outside the supervisor's region

            // New feedback tells the managers whose scope reaches the author: the whole-company manager and the supervisor of Ann's region, not the author, not region B's supervisor.
            Assert.Equal("boss@x.com,sup@x.com", await InTenant(connection, tenant, $"SELECT string_agg(u.email, ',' ORDER BY u.email) FROM notifications n JOIN users u ON u.id = n.user_id WHERE n.kind = 'feedback.new' AND n.entity_id = '{f1}'"));
            Assert.Equal("boss@x.com,supb@x.com", await InTenant(connection, tenant, $"SELECT string_agg(u.email, ',' ORDER BY u.email) FROM notifications n JOIN users u ON u.id = n.user_id WHERE n.kind = 'feedback.new' AND n.entity_id = '{f2}'"));
            Assert.Equal("boss@x.com", await InTenant(connection, tenant, $"SELECT string_agg(u.email, ',') FROM notifications n JOIN users u ON u.id = n.user_id WHERE n.kind = 'feedback.new' AND n.entity_id = '{f3}'"));   // no region: only the whole-company manager

            // Photos: only the author adds, three at most; the author and the managers in scope read them.
            var jpeg = new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 };
            Assert.Equal(FeedbackResult.NotFound, await store.AddPhotoAsync(t, new UserId(boss), f1, "image/jpeg", jpeg, 3, CancellationToken.None));
            for (var i = 0; i < 3; i++) Assert.Equal(FeedbackResult.Success, await store.AddPhotoAsync(t, new UserId(ann), f1, "image/jpeg", jpeg, 3, CancellationToken.None));
            Assert.Equal(FeedbackResult.TooManyPhotos, await store.AddPhotoAsync(t, new UserId(ann), f1, "image/png", jpeg, 3, CancellationToken.None));
            var photoIds = (await store.GetAsync(t, Own(ann), f1, CancellationToken.None))!.PhotoIds;
            Assert.Equal(3, photoIds.Count);
            Assert.Equal(("image/jpeg", 4), (await store.GetPhotoAsync(t, Own(ann), f1, photoIds[0], CancellationToken.None) is { } p ? (p.ContentType, p.Data.Length) : ("", 0)));
            Assert.NotNull(await store.GetPhotoAsync(t, supAccess, f1, photoIds[0], CancellationToken.None));
            Assert.Null(await store.GetPhotoAsync(t, Own(bob), f1, photoIds[0], CancellationToken.None));
            Assert.Null(await store.GetPhotoAsync(t, supAccess, f2, photoIds[0], CancellationToken.None));

            // Answers: a manager in scope answers; the first answer moves it to in_review and tells the author; outside the scope it is not found.
            Assert.True(await store.AddReplyAsync(t, new UserId(sup), supAccess.Manage!, f1, "Thanks, we will look.", CancellationToken.None));
            Assert.False(await store.AddReplyAsync(t, new UserId(sup), supAccess.Manage!, f2, "not my region", CancellationToken.None));
            var answered = (await store.GetAsync(t, Own(ann), f1, CancellationToken.None))!;
            Assert.Equal(("in_review", 1, "Thanks, we will look.", "sup@x.com"), (answered.Status, answered.Replies.Count, answered.Replies[0].Body, answered.Replies[0].UserEmail));
            Assert.Equal("ann@x.com", await InTenant(connection, tenant, $"SELECT u.email FROM notifications n JOIN users u ON u.id = n.user_id WHERE n.kind = 'feedback.reply' AND n.entity_id = '{f1}'"));
            Assert.Equal("1", await InTenant(connection, tenant, $"SELECT count(*) FROM audit_logs WHERE action = 'feedback.replied' AND entity_id = '{f1}' AND actor_id = '{sup}'"));

            // Status: set by a manager in scope; setting the same status again is fine and audits nothing new; outside the scope it is not found.
            Assert.True(await store.SetStatusAsync(t, new UserId(boss), bossAccess.Manage!, f1, "done", CancellationToken.None));
            Assert.True(await store.SetStatusAsync(t, new UserId(boss), bossAccess.Manage!, f1, "done", CancellationToken.None));
            Assert.False(await store.SetStatusAsync(t, new UserId(sup), supAccess.Manage!, f2, "done", CancellationToken.None));
            Assert.Equal("1", await InTenant(connection, tenant, $"SELECT count(*) FROM audit_logs WHERE action = 'feedback.status_changed' AND entity_id = '{f1}'"));
            Assert.Equal("A1 about Shifa", await Ids(bossAccess, status: "done"));
            Assert.Equal("done", (await store.GetAsync(t, Own(ann), f1, CancellationToken.None))!.Status);
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE", setup);
            await drop.ExecuteNonQueryAsync();
        }
    }

    // The tables have row level security in the real schema; here only the company is named, as the API does for every statement.
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
