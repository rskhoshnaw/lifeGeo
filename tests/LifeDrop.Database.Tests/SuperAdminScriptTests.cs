using System;
using System.IO;
using Xunit;

namespace LifeDrop.Database.Tests;

// deploy/create-super-admin.sh upserts into users. The unique index on users is partial (WHERE email IS NOT NULL, migration 0002), and PostgreSQL only uses a
// partial index for ON CONFLICT when the conflict target repeats its predicate; without it the first deployment failed with "no unique or exclusion constraint
// matching the ON CONFLICT specification". No database needed: the script text is checked against the migration.
public class SuperAdminScriptTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LifeDrop.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    [Fact]
    public void Upsert_repeats_the_predicate_of_the_partial_unique_index()
    {
        var migration = File.ReadAllText(Path.Combine(RepoRoot(), "db", "migrations", "0002_login.sql"));
        Assert.Contains("CREATE UNIQUE INDEX users_tenant_email_unique ON users (tenant_id, lower(email))", migration);
        Assert.Contains("WHERE email IS NOT NULL", migration);

        var script = File.ReadAllText(Path.Combine(RepoRoot(), "deploy", "create-super-admin.sh"));
        Assert.Contains("ON CONFLICT (tenant_id, lower(email)) WHERE email IS NOT NULL DO UPDATE", script);
    }
}
