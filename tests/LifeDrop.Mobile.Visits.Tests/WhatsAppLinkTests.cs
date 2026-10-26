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
