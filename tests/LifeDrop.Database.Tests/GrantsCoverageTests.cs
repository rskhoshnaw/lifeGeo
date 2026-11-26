using System.Text.RegularExpressions;
using Xunit;

namespace LifeDrop.Database.Tests;

// The API runs as a restricted database role (db/grants/lifedrop_app_grants.sql). The other tests run as the owner of their schema and so never notice a missing grant, which only fails on
// the real server. This test reads the SQL in the program (Infrastructure and API) and checks that every table it inserts into, updates or deletes from has that privilege in the grants file.
public class GrantsCoverageTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "LifeDrop.Infrastructure"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    }

    private static Dictionary<string, HashSet<string>> Grants(string root)
    {
        var sql = File.ReadAllText(Path.Combine(root, "db", "grants", "lifedrop_app_grants.sql"));
        sql = Regex.Replace(sql, @"--[^\n]*", "");
        var grants = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in Regex.Matches(sql, @"GRANT\s+([A-Za-z,\s]+?)\s+ON\s+([\s\S]+?)\s+TO\s+lifedrop_app\s*;", RegexOptions.IgnoreCase))
        {
            var privileges = m.Groups[1].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(p => p.ToUpperInvariant()).ToList();
            foreach (var table in m.Groups[2].Value.Split([',', ' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries))
            {
                if (!grants.TryGetValue(table, out var set)) grants[table] = set = [];
                foreach (var p in privileges) set.Add(p);
            }
        }
        return grants;
    }

    [Fact]
    public void EveryTableTheProgramWritesHasTheGrantInTheGrantsFile()
    {
        var root = RepoRoot();
        var grants = Grants(root);
        var missing = new SortedSet<string>();
        var tables = Directory.GetFiles(Path.Combine(root, "db", "migrations"), "*.sql")
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), @"CREATE\s+TABLE\s+(?:IF\s+NOT\s+EXISTS\s+)?([a-z_][a-z0-9_]*)", RegexOptions.IgnoreCase).Select(m => m.Groups[1].Value.ToLowerInvariant()))
            .ToHashSet();
        var files = new[] { "src/LifeDrop.Infrastructure", "src/LifeDrop.API" }
            .SelectMany(d => Directory.GetFiles(Path.Combine(root, d), "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            void Check(string pattern, string privilege)
            {
                foreach (Match m in Regex.Matches(text, pattern, RegexOptions.IgnoreCase))
                {
                    var table = m.Groups[1].Value.ToLowerInvariant();
                    if (table is "set" or "select" or "only" or "table" || table.Length < 3) continue;
                    if (!grants.TryGetValue(table, out var have) || !have.Contains(privilege)) missing.Add($"{privilege} on {table} ({Path.GetFileName(file)})");
                }
            }
            Check(@"\bINSERT\s+INTO\s+([a-z_][a-z0-9_]*)", "INSERT");
            Check(@"\bUPDATE\s+([a-z_][a-z0-9_]*)\s+(?:AS\s+)?(?:[a-z]\w*\s+)?SET\b", "UPDATE");
            Check(@"\bDELETE\s+FROM\s+([a-z_][a-z0-9_]*)", "DELETE");
            // Reading: a name after FROM or JOIN that is a table of the database.
            foreach (Match m in Regex.Matches(text, @"\b(?:FROM|JOIN)\s+([a-z_][a-z0-9_]*)\b", RegexOptions.IgnoreCase))
            {
                var table = m.Groups[1].Value.ToLowerInvariant();
                if (tables.Contains(table) && (!grants.TryGetValue(table, out var have) || !have.Contains("SELECT"))) missing.Add($"SELECT on {table} ({Path.GetFileName(file)})");
            }
        }
        Assert.True(missing.Count == 0, "Missing grants for the API role (db/grants/lifedrop_app_grants.sql):\n" + string.Join("\n", missing));
    }
}
