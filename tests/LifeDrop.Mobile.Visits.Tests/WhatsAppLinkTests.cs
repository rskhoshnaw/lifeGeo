using Xunit;

namespace LifeDrop.Mobile.Visits.Tests;

public class WhatsAppLinkTests
{
    [Theory]
    [InlineData("+9647722656566", "https://wa.me/9647722656566")]
    [InlineData("0772 265 6566", "https://wa.me/9647722656566")]
    [InlineData("07722656566", "https://wa.me/9647722656566")]
    [InlineData("7722656566", "https://wa.me/9647722656566")]
    [InlineData("00964 772 265 6566", "https://wa.me/9647722656566")]
    [InlineData("+964 (772) 265-6566", "https://wa.me/9647722656566")]
    [InlineData("+44 7911 123456", "https://wa.me/447911123456")]
    public void Url_NormalisesIraqiAndInternationalNumbers(string phone, string expected) => Assert.Equal(expected, WhatsAppLink.Url(phone));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("abc")]
    [InlineData("12")]
    [InlineData("0770")]
    public void Url_IsNullForText_ThatIsNotAPhoneNumber(string? phone) => Assert.Null(WhatsAppLink.Url(phone));

    [Fact]
    public void Url_CarriesAnEscapedMessage() => Assert.Equal("https://wa.me/9647722656566?text=Hello%20%26%20welcome", WhatsAppLink.Url("07722656566", "Hello & welcome"));
}

public class CustomerFilterPathTests
{
    private static readonly Guid Place = Guid.Parse("11111111-2222-3333-4444-555555555555");

    [Fact]
    public void FilterPath_SendsOnlyTheFiltersThatAreSet()
    {
        Assert.Equal("/customers?limit=50", CustomersApiClient.FilterPath(null, null, null, null));
        Assert.Equal("/customers?limit=50&q=ali%20h&type=Doctor&placeId=" + Place + "&cooperates=false", CustomersApiClient.FilterPath(" ali h ", "Doctor", Place, false));
        Assert.Equal("/customers?limit=50&cooperates=true", CustomersApiClient.FilterPath("  ", " ", null, true));
    }

    [Fact]
    public void FilterPath_AddsTheFiltersOfStep528AndThePagingCursor()
    {
        var filter = new CustomerListFilter(null, "Doctor", null, null, HasLocation: false, CustomerClass: "A+", AddedFrom: new DateOnly(2026, 11, 1), Dealer: true);
        Assert.Equal("/customers?limit=50&type=Doctor&hasLocation=false&dealer=true&customerClass=A%2B&createdFrom=2026-11-01", CustomersApiClient.FilterPath(filter, null, null));
        var cursor = new DateTimeOffset(2026, 11, 3, 10, 0, 0, TimeSpan.Zero);
        Assert.Equal("/customers?limit=50&hasLocation=true&cursorCreatedAt=2026-11-03T10%3A00%3A00.0000000%2B00%3A00&cursorId=" + Place,
            CustomersApiClient.FilterPath(new CustomerListFilter(HasLocation: true), cursor, Place));
        Assert.Equal("/customers?limit=50", CustomersApiClient.FilterPath(new CustomerListFilter(), cursor, null));   // a cursor needs both parts
        Assert.True(new CustomerListFilter().IsEmpty);
        Assert.False(new CustomerListFilter(HasLocation: false).IsEmpty);
    }
}
