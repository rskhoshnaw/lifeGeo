using Xunit;
using LifeDrop.Mobile.Visits;

namespace LifeDrop.Mobile.Visits.Tests;

// The same cases as the panel (src/LifeDrop.Web/src/pages/moneyInput.test.ts): 10.440 and 10,440 are ten thousand four hundred and forty dinars.
public sealed class MoneyInputTests
{
    [Theory]
    [InlineData("10440", 10440)]
    [InlineData("10.440", 10440)]
    [InlineData("10,440", 10440)]
    [InlineData("۱۰٬۴۴۰", 10440)]
    [InlineData("١٠٬٤٤٠", 10440)]
    [InlineData("10 440", 10440)]
    [InlineData("10 440", 10440)]
    [InlineData("1.234.567", 1234567)]
    [InlineData("1,234,567", 1234567)]
    [InlineData("0", 0)]
    [InlineData("0.500", 500)]
    public void DinarsTakeThousandsMarks(string text, int expected) => Assert.Equal((decimal)expected, MoneyInput.Parse(text, "IQD").Value);

    [Theory]
    [InlineData("10.44")]
    [InlineData("10,5")]
    [InlineData("1.5")]
    [InlineData("10.4400")]
    [InlineData("12.34.5")]
    public void DinarsRefuseAFraction(string text)
    {
        var (value, problem) = MoneyInput.Parse(text, "IQD");
        Assert.Null(value);
        Assert.NotNull(problem);
    }

    [Fact]
    public void ProblemsAreNamed()
    {
        Assert.Equal(MoneyProblem.Decimals, MoneyInput.Parse("10.44", "IQD").Problem);
        Assert.Equal(MoneyProblem.Empty, MoneyInput.Parse("", "IQD").Problem);
        Assert.Equal(MoneyProblem.Invalid, MoneyInput.Parse("abc", "IQD").Problem);
        Assert.Equal(MoneyProblem.Invalid, MoneyInput.Parse("10..5", "IQD").Problem);
        Assert.Equal(MoneyProblem.Invalid, MoneyInput.Parse("-5", "IQD").Problem);
    }

    [Theory]
    [InlineData("12", "12")]
    [InlineData("12.5", "12.5")]
    [InlineData("12,5", "12.5")]
    [InlineData("12.50", "12.5")]
    [InlineData("1,234.56", "1234.56")]
    [InlineData("1.234,56", "1234.56")]
    [InlineData("۱۲٫۵", "12.5")]
    [InlineData("1,500", "1500")]
    [InlineData("12.345", "12345")]
    public void DollarsTakeTwoDecimals(string text, string expected) => Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), MoneyInput.Parse(text, "USD").Value);

    [Theory]
    [InlineData("12.3456")]
    [InlineData("1,234.567")]
    public void DollarsRefuseMoreThanTwoDecimals(string text) => Assert.Equal(MoneyProblem.Decimals, MoneyInput.Parse(text, "USD").Problem);
}
