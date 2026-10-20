using LifeDrop.Mobile.Localization;
using Xunit;

namespace LifeDrop.Mobile.Visits.Tests;

// What a company sets for itself on the server (plan step 4.48): its own wording and the parts it switched off.
public class CompanyProfileTests
{
    [Fact]
    public void TheCompanysOwnTextWinsInItsLanguageAndOtherLanguagesKeepTheBuiltInText()
    {
        var builtInFa = Texts.Get("fa", "app.home.customers");
        var builtInEn = Texts.Get("en", "app.home.customers");
        try
        {
            Texts.SetOverrides(new Dictionary<string, IReadOnlyDictionary<string, string>> { ["app.home.customers"] = new Dictionary<string, string> { ["fa"] = "فروشگاه‌های من" } });
            Assert.Equal("فروشگاه‌های من", Texts.Get("fa", "app.home.customers"));
            Assert.Equal(builtInEn, Texts.Get("en", "app.home.customers"));   // no English text from the company: the built-in one
            Texts.SetOverrides(new Dictionary<string, IReadOnlyDictionary<string, string>> { ["app.order.total"] = new Dictionary<string, string> { ["en"] = "Sum {total} {currency}" } });
            Assert.Equal("Sum 5 IQD", Texts.Get("en", "app.order.total", ("total", 5), ("currency", "IQD")));   // placeholders are filled
            Assert.Equal(builtInFa, Texts.Get("fa", "app.home.customers"));
            Texts.SetOverrides(new Dictionary<string, IReadOnlyDictionary<string, string>> { ["app.home.customers"] = new Dictionary<string, string> { ["en"] = "  " } });
            Assert.Equal(builtInEn, Texts.Get("en", "app.home.customers"));   // an empty text is ignored
        }
        finally
        {
            Texts.SetOverrides(null);
        }
        Assert.Equal(builtInFa, Texts.Get("fa", "app.home.customers"));
    }

    [Fact]
    public void TheOverridesSurviveBeingKeptOnThePhoneAndBrokenSavedTextIsIgnored()
    {
        var overrides = new Dictionary<string, IReadOnlyDictionary<string, string>> { ["app.home.plan"] = new Dictionary<string, string> { ["ckb"] = "پلانی من", ["en"] = "My route" } };
        var back = Texts.ParseOverrides(Texts.OverridesJson(overrides));
        Assert.Equal("My route", back["app.home.plan"]["en"]);
        Assert.Empty(Texts.ParseOverrides("{not json"));
        Assert.Empty(Texts.ParseOverrides(null));
        Assert.Empty(Texts.ParseOverrides(""));
    }

    [Fact]
    public void HiddenModulesAreKeptAsOneTextAndTheHomeScreenAsksIfAModuleShows()
    {
        Assert.Equal("payments,samples", CompanyModules.Serialize(new[] { "Samples", "payments", " samples ", "" }));
        var hidden = CompanyModules.Parse("payments, Samples,,");
        Assert.False(CompanyModules.Shows(hidden, "payments"));
        Assert.False(CompanyModules.Shows(hidden, "samples"));
        Assert.True(CompanyModules.Shows(hidden, "orders"));
        Assert.True(CompanyModules.Shows(CompanyModules.Parse(null), "payments"));   // nothing hidden when nothing was kept
    }
}
