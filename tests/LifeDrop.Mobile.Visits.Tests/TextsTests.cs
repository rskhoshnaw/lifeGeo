using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using LifeDrop.Mobile.Localization;
using Xunit;

namespace LifeDrop.Mobile.Visits.Tests;

// The other tests check English messages; the app's default language is Sorani Kurdish.
internal static class EnglishForTests
{
    [ModuleInitializer]
    internal static void Init() => Texts.Language = "en";
}

// Changes the global language, so it never runs in parallel with the tests that read English messages.
[CollectionDefinition(nameof(TextsTests), DisableParallelization = true)]
public sealed class TextsCollection;

// App texts (plan step 1.21): one shared file with the web panel, four languages, Sorani Kurdish by default.
[Collection(nameof(TextsTests))]
public class TextsTests
{
    [Fact]
    public void EveryKeyHasEveryLanguageWithTheSamePlaceholders()
    {
        foreach (var key in Texts.Keys)
        {
            var english = Texts.Raw("en", key)!;
            foreach (var (code, _) in Texts.Languages)
            {
                var text = Texts.Raw(code, key);
                Assert.False(string.IsNullOrWhiteSpace(text), $"{key}.{code} is missing");
                Assert.Equal(Texts.Placeholders(english), Texts.Placeholders(text!));
            }
        }
    }

    [Fact]
    public void FoodProfileReplacesPharmacyWordingAndOnlyThat()
    {
        try
        {
            Texts.Profile = "";
            Assert.Equal("Pharmacy", Texts.Get("en", "app.type.pharmacy"));
            Texts.Profile = "food";
            Assert.Equal("Shop / supermarket", Texts.Get("en", "app.type.pharmacy"));
            Assert.Equal("Restaurant / hotel", Texts.Get("en", "app.type.doctor"));
            Assert.Equal("Wholesaler", Texts.Get("en", "app.type.wholesaler"));
            Assert.DoesNotContain("octor", Texts.Get("en", "app.sample.recipient"));
        }
        finally { Texts.Profile = ""; }
    }

    [Fact]
    public void EveryKeyUsedByTheAppExists()
    {
        var root = RepoRoot();
        var used = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => Path.GetFileName(Path.GetDirectoryName(f)!) is not ("obj" or "bin") && f.Contains("LifeDrop.Mobile"))
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), "\"(app\\.[A-Za-z_.]+)\"").Select(m => m.Groups[1].Value))
            .Where(k => !k.EndsWith('.'))
            .Distinct()
            .ToList();
        Assert.NotEmpty(used);
        Assert.Empty(used.Where(k => !Texts.Has(k)));
    }

    [Fact]
    public void FillsPlaceholdersAndFallsBack()
    {
        Assert.Equal("دۆخ: Live", Texts.Get("ckb", "app.visit.status", ("status", "Live")));
        Assert.Equal("Status: {status}", Texts.Get("en", "app.visit.status"));
        Assert.Equal("app.unknown", Texts.Get("en", "app.unknown"));
    }

    [Fact]
    public void SoraniIsTheDefaultAndRightToLeft()
    {
        var saved = Texts.Language;
        try
        {
            Texts.Language = "xx";
            Assert.Equal(("ckb", true), (Texts.Language, Texts.IsRightToLeft));
            Assert.Equal("پلانی ئەمڕۆ", Texts.Get("app.plan.title"));
            Assert.NotNull(Texts.Culture);
            Texts.Language = "en";
            Assert.False(Texts.IsRightToLeft);
        }
        finally
        {
            Texts.Language = saved;
        }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "i18n", "strings.json"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
