using LifeDrop.Application.Forms;
using Xunit;

namespace LifeDrop.Application.Tests;

// What the server accepts as an answer to a custom form field (plan step 3.2, part 2).
public class FormFieldAnswerTests
{
    private static FormFieldDto Field(string type, params string[] options) =>
        new(System.Guid.NewGuid(), "f", type, "all", "Label", null, null, null, options, false, 0, true);

    [Theory]
    [InlineData("4", "4")]
    [InlineData("4.50", "4.5")]
    [InlineData("-3", "-3")]
    [InlineData("0.125", "0.125")]
    [InlineData("1.2345", null)]
    [InlineData("abc", null)]
    [InlineData("1e3", null)]
    [InlineData("1,5", null)]
    [InlineData("1000000000000", null)]
    public void NumbersAreInvariantWithAtMostThreeDecimals(string value, string? expected) =>
        Assert.Equal(expected, FormFieldService.Normalise(Field("number"), value));

    [Theory]
    [InlineData("true", "true")]
    [InlineData("false", "false")]
    [InlineData("yes", null)]
    [InlineData("True", null)]
    public void YesNoIsTrueOrFalse(string value, string? expected) => Assert.Equal(expected, FormFieldService.Normalise(Field("yes_no"), value));

    [Theory]
    [InlineData("2026-10-05", "2026-10-05")]
    [InlineData("2026-13-01", null)]
    [InlineData("05/10/2026", null)]
    [InlineData("2026-02-30", null)]
    public void DatesAreIsoDays(string value, string? expected) => Assert.Equal(expected, FormFieldService.Normalise(Field("date"), value));

    [Fact]
    public void ChoicesMustBeOneOfTheOptionsAndTextHasALimit()
    {
        var choice = Field("choice", "good", "bad");
        Assert.Equal("good", FormFieldService.Normalise(choice, "good"));
        Assert.Null(FormFieldService.Normalise(choice, "Good"));
        Assert.Null(FormFieldService.Normalise(choice, "great"));
        Assert.Equal(new string('x', 1000), FormFieldService.Normalise(Field("text"), new string('x', 1000)));
        Assert.Null(FormFieldService.Normalise(Field("text"), new string('x', 1001)));
    }
}
