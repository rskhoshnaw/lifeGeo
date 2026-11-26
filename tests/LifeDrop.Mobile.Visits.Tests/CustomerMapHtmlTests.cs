using Xunit;

namespace LifeDrop.Mobile.Visits.Tests;

// The customer map of the app (step 5.52): the page carries every customer as JSON, marks the owned ones, and the open link is read back safely.
public class CustomerMapHtmlTests
{
    private static MapCustomerItem C(string name, bool mine, double lat = 36.2, double lon = 44.0) => new(Guid.NewGuid(), "C-" + name.Length, name, "Pharmacy", lat, lon, mine);

    [Fact]
    public void ThePageHoldsEveryCustomerAsDataAndMarksTheOwnedOnes()
    {
        var html = CustomerMapHtml.Build([C("Shifa", true), C("Other", false)], 36.19, 44.01, "Open", "Mine", "Others");
        Assert.Contains("\"name\":\"Shifa\"", html);
        Assert.Contains("\"mine\":true", html);
        Assert.Contains("\"mine\":false", html);
        Assert.Contains("lifedrop://customer/", html);
        Assert.DoesNotContain("__DATA__", html);
        Assert.Contains("var me = [36.19,44.01];", html);
    }

    [Fact]
    public void ANameWithMarkupCannotBreakOutOfThePage()
    {
        var html = CustomerMapHtml.Build([C("</script><script>alert(1)</script>", true)], null, null, "Open", "Mine", "Others");
        Assert.DoesNotContain("</script><script>alert(1)", html);
        Assert.Contains("var me = null;", html);
    }

    [Fact]
    public void WithoutAPhonePositionTheMapStartsOnTheOwnCustomers()
    {
        var html = CustomerMapHtml.Build([C("A", true, 36.0, 44.0), C("B", true, 36.5, 44.5), C("Far", false, 30.0, 40.0)], null, null, "Open", "Mine", "Others");
        Assert.Contains("setView([36.25, 44.25], 12)", html);
    }

    [Fact]
    public void TheOpenAddressGivesTheCustomerIdAndNothingElse()
    {
        var id = Guid.NewGuid();
        Assert.Equal(id, CustomerMapHtml.CustomerIdOf("lifedrop://customer/" + id));
        Assert.Equal(id, CustomerMapHtml.CustomerIdOf("LIFEDROP://customer/" + id + "/"));
        Assert.Null(CustomerMapHtml.CustomerIdOf("https://tile.openstreetmap.org/1/2/3.png"));
        Assert.Null(CustomerMapHtml.CustomerIdOf("lifedrop://customer/not-a-guid"));
        Assert.Null(CustomerMapHtml.CustomerIdOf(null));
    }
}
