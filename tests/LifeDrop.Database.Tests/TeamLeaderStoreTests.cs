using LifeDrop.Application.Regions;
using LifeDrop.Domain.Common;
using LifeDrop.Infrastructure.Regions;
using Npgsql;
using Xunit;

namespace LifeDrop.Database.Tests;

// The team leader (owner request 2026-11-13) against every migration in an isolated schema: only an active member of the team with the role Supervisor can lead it;
// leaving the team (removed or moved), or losing the Supervisor role, ends the leadership; the team list reports the leader's e-mail.
public class TeamLeaderStoreTests
{
    [Fact]
    public async Task Only_a_supervisor_member_leads_and_leadership_ends_with_membership_or_role()
    {
        var url = Environment.GetEnvironmentVariable("TEST_DATABASE_URL")
            ?? throw new InvalidOperationException("TEST_DATABASE_URL must name an isolated lifedrop_test_* database.");
        var builder = new NpgsqlConnectionStringBuilder(url);
        if (builder.Database is null || !builder.Database.StartsWith("lifedrop_test_", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Refusing to test a database outside lifedrop_test_*.");

        var schema = "leader_test_" + Guid.NewGuid().ToString("N");
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

            Guid tenant = Guid.NewGuid(), manager = Guid.NewGuid(), sup = Guid.NewGuid(), rep = Guid.NewGuid(), outsider = Guid.NewGuid();
            Guid region = Guid.NewGuid(), teamA = Guid.NewGuid(), teamB = Guid.NewGuid();
            await using (var seed = new NpgsqlCommand("""
                INSERT INTO tenants(id,name,code) VALUES (@tenant,'A','a');
                INSERT INTO users(id,tenant_id,email) VALUES (@manager,@tenant,'manager@a.test'),(@sup,@tenant,'sup@a.test'),(@rep,@tenant,'rep@a.test'),(@outsider,@tenant,'outsider@a.test');
                """, connection))
            {
                seed.Parameters.AddWithValue("tenant", tenant);
                seed.Parameters.AddWithValue("manager", manager);
                seed.Parameters.AddWithValue("sup", sup);
                seed.Parameters.AddWithValue("rep", rep);
                seed.Parameters.AddWithValue("outsider", outsider);
                await seed.ExecuteNonQueryAsync();
            }
            await InTenant(connection, tenant, $"""
                SELECT seed_default_roles('{tenant}');
                INSERT INTO user_roles(tenant_id,user_id,role_id) SELECT '{tenant}','{sup}',id FROM roles WHERE tenant_id = '{tenant}' AND name = 'Supervisor';
                INSERT INTO user_roles(tenant_id,user_id,role_id) SELECT '{tenant}','{outsider}',id FROM roles WHERE tenant_id = '{tenant}' AND name = 'Supervisor';
                INSERT INTO user_roles(tenant_id,user_id,role_id) SELECT '{tenant}','{rep}',id FROM roles WHERE tenant_id = '{tenant}' AND name = 'SalesRep';
                """);

            var store = new PostgresRegionTeamStore(dataSource);
            var t = new TenantId(tenant);
            var actor = new UserId(manager);
            Assert.Equal(RegionTeamResult.Success, await store.CreateRegionAsync(t, actor, region, "North", CancellationToken.None));
            Assert.Equal(RegionTeamResult.Success, await store.CreateTeamAsync(t, actor, teamA, "Team A", region, CancellationToken.None));
            Assert.Equal(RegionTeamResult.Success, await store.CreateTeamAsync(t, actor, teamB, "Team B", region, CancellationToken.None));
            foreach (var member in new[] { sup, rep }) Assert.Equal(RegionTeamResult.Success, await store.AssignMemberAsync(t, actor, teamA, member, CancellationToken.None));
            async Task<string?> LeaderOf(Guid team) => (await store.ListTeamsAsync(t, CancellationToken.None)).Single(x => x.Id == team).LeaderEmail;

            Assert.Null(await LeaderOf(teamA));
            Assert.Equal(RegionTeamResult.NotEligible, await store.SetLeaderAsync(t, actor, teamA, rep, CancellationToken.None));       // a member, but not a supervisor
            Assert.Equal(RegionTeamResult.NotEligible, await store.SetLeaderAsync(t, actor, teamA, outsider, CancellationToken.None));  // a supervisor, but not a member
            Assert.Equal(RegionTeamResult.NotFound, await store.SetLeaderAsync(t, actor, Guid.NewGuid(), sup, CancellationToken.None));
            Assert.Equal(RegionTeamResult.Success, await store.SetLeaderAsync(t, actor, teamA, sup, CancellationToken.None));
            Assert.Equal("sup@a.test", await LeaderOf(teamA));

            // Moved to another team: no longer the leader of the first.
            Assert.Equal(RegionTeamResult.Success, await store.AssignMemberAsync(t, actor, teamB, sup, CancellationToken.None));
            Assert.Null(await LeaderOf(teamA));
            Assert.Equal(RegionTeamResult.Success, await store.SetLeaderAsync(t, actor, teamB, sup, CancellationToken.None));
            Assert.Equal("sup@a.test", await LeaderOf(teamB));

            // Removed from the team: the leadership ends.
            Assert.Equal(RegionTeamResult.Success, await store.RemoveMemberAsync(t, actor, teamB, sup, CancellationToken.None));
            Assert.Null(await LeaderOf(teamB));

            // Losing the Supervisor role: the stored leader is no longer reported.
            Assert.Equal(RegionTeamResult.Success, await store.AssignMemberAsync(t, actor, teamA, sup, CancellationToken.None));
            Assert.Equal(RegionTeamResult.Success, await store.SetLeaderAsync(t, actor, teamA, sup, CancellationToken.None));
            await InTenant(connection, tenant, $"DELETE FROM user_roles WHERE user_id = '{sup}'");
            Assert.Null(await LeaderOf(teamA));

            // Clearing works, and both changes are audited.
            Assert.Equal(RegionTeamResult.Success, await store.SetLeaderAsync(t, actor, teamA, null, CancellationToken.None));
            Assert.Equal("3", await InTenant(connection, tenant, "SELECT count(*) FROM audit_logs WHERE action = 'team.leader_set'"));
            Assert.Equal("1", await InTenant(connection, tenant, "SELECT count(*) FROM audit_logs WHERE action = 'team.leader_cleared'"));
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP SCHEMA {schema} CASCADE", setup);
            await drop.ExecuteNonQueryAsync();
        }
    }

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
