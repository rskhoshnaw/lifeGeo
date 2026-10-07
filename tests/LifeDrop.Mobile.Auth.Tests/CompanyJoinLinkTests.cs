using Xunit;

namespace LifeDrop.Mobile.Auth.Tests;

public class CompanyJoinLinkTests
{
    [Theory]
    [InlineData("lifedrop://join/acme", "acme")]
    [InlineData("  LifeDrop://JOIN/Acme  ", "acme")]
    [InlineData("lifedrop://join/acme/", "acme")]
    [InlineData("lifedrop://join?company=acme", "acme")]
    [InlineData("lifedrop://join/?company=ACME-2", "acme-2")]
    [InlineData("https://panel.example.com/?company=acme", "acme")]
    [InlineData("https://panel.example.com/login?x=1&company=acme&y=2", "acme")]
    [InlineData("http://localhost:5173/?company=dev", "dev")]
    public void Reads_the_company_code_from_every_kind_of_link(string link, string expected) =>
        Assert.Equal(expected, CompanyJoinLink.TryGetCode(link));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("acme")]                                      // a plain code is not a link; the sign-in keeps what the user typed
    [InlineData("lifedrop://join")]                           // no code
    [InlineData("lifedrop://join/")]
    [InlineData("lifedrop://other/acme")]                     // another host of our scheme
    [InlineData("lifedrop://join/a")]                         // too short to be a code
    [InlineData("lifedrop://join/..%2Fadmin")]                // not a code
    [InlineData("lifedrop://join/acme%20corp")]
    [InlineData("lifedrop://join/%E0%A4%A")]                  // broken escape
    [InlineData("https://panel.example.com/")]                // a link without a company
    [InlineData("https://panel.example.com/?company=")]
    [InlineData("https://panel.example.com/acme")]            // the path of a web link is not a company
    [InlineData("ftp://x.example/?company=acme")]             // not our scheme and not http(s)
    [InlineData("javascript://x/?company=acme")]
    public void Gives_nothing_for_what_cannot_be_a_company_link(string? text) => Assert.Null(CompanyJoinLink.TryGetCode(text));

    [Fact]
    public void Knows_a_link_from_a_typed_code()
    {
        Assert.True(CompanyJoinLink.LooksLikeLink("lifedrop://join/acme"));
        Assert.True(CompanyJoinLink.LooksLikeLink("https://x/?company=a"));
        Assert.False(CompanyJoinLink.LooksLikeLink("acme"));
        Assert.False(CompanyJoinLink.LooksLikeLink(null));
    }

    [Fact]
    public void A_link_made_for_a_code_gives_the_same_code_back()
    {
        foreach (var code in new[] { "acme", "a-b_c", "x1", "company-with-a-long-code-0123456789-ab" })
            Assert.Equal(code, CompanyJoinLink.TryGetCode("lifedrop://join/" + Uri.EscapeDataString(code)));
    }
}
