using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace LifeDrop.Database.Tests;

// The API image (deploy/Dockerfile) copies only some folders. A file that a project includes from outside src/ must be copied too, or the first server build
// fails (it did: i18n/strings.json, error CS1566). No database or Docker needed: every "../" file the API projects include must be in a COPY line.
public class DockerfileTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LifeDrop.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    [Fact]
    public void Dockerfile_copies_the_files_the_api_projects_include_from_outside_src()
    {
        var root = RepoRoot();
        var dockerfile = File.ReadAllText(Path.Combine(root, "deploy", "Dockerfile"));
        foreach (var project in new[] { "LifeDrop.Domain", "LifeDrop.Application", "LifeDrop.Infrastructure", "LifeDrop.API" })
        {
            var csproj = File.ReadAllText(Path.Combine(root, "src", project, project + ".csproj"));
            foreach (Match match in Regex.Matches(csproj, "Include=\"(\\.\\./\\.\\./[^\"]+)\""))
            {
                var topFolder = match.Groups[1].Value.Substring("../../".Length).Split('/')[0];
                Assert.True(Regex.IsMatch(dockerfile, @"(?m)^COPY\s+" + Regex.Escape(topFolder) + @"/?\s"),
                    $"{project} includes {match.Groups[1].Value}, but deploy/Dockerfile has no COPY for '{topFolder}'.");
            }
        }
    }
}
