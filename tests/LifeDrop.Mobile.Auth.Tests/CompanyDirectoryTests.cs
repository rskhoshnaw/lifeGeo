using System.Net;
using LifeDrop.Mobile.Auth.Tests.Stubs;
using Xunit;

namespace LifeDrop.Mobile.Auth.Tests;

public class CompanyDirectoryTests
{
    private static Task<CompanyLookup> Resolve(StubHttpMessageHandler handler, string? bootstrap = "https://boot.example", string? code = "acme", bool allowInsecure = false) =>
        CompanyDirectory.ResolveAsync(new HttpClient(handler), bootstrap, code, allowInsecure);

    [Fact]
    public async Task Company_without_own_address_uses_the_bootstrap_server()
    {
        var handler = StubHttpMessageHandler.FromResponse(HttpStatusCode.OK, """{"tenantCode":"acme","name":"Acme","apiBaseUrl":null,"supportPhone":"+9647510686828"}""");
        var result = await Resolve(handler, code: " ACME ");
        Assert.Equal(CompanyLookupStatus.Found, result.Status);
        Assert.Equal("https://boot.example", result.Config!.ApiBaseUrl);
        Assert.Equal("+9647510686828", result.Config.SupportPhone);
        Assert.Equal("https://boot.example/company-config/acme", handler.LastRequest!.RequestUri!.ToString());
    }

    [Fact]
    public async Task Company_with_own_address_gets_that_address()
    {
        var result = await Resolve(StubHttpMessageHandler.FromResponse(HttpStatusCode.OK, """{"tenantCode":"acme","name":"Acme","apiBaseUrl":"https://api.acme.example"}"""));
        Assert.Equal("https://api.acme.example", result.Config!.ApiBaseUrl);
        Assert.Null(result.Config.SupportPhone);
    }

    [Fact]
    public async Task Answer_about_another_company_is_not_trusted()
    {
        var result = await Resolve(StubHttpMessageHandler.FromResponse(HttpStatusCode.OK, """{"tenantCode":"other","name":"Other","apiBaseUrl":null}"""));
        Assert.Equal(CompanyLookupStatus.Unavailable, result.Status);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, CompanyLookupStatus.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError, CompanyLookupStatus.Unavailable)]
    [InlineData(HttpStatusCode.TooManyRequests, CompanyLookupStatus.Unavailable)]
    public async Task Server_answers_map_to_a_status(HttpStatusCode status, CompanyLookupStatus expected) =>
        Assert.Equal(expected, (await Resolve(StubHttpMessageHandler.FromResponse(status))).Status);

    [Fact]
    public async Task Unavailable_answers_carry_a_short_reason()
    {
        var server = await Resolve(StubHttpMessageHandler.FromResponse(HttpStatusCode.InternalServerError));
        Assert.Equal("HTTP 500", server.Detail);
        var down = await Resolve(new StubHttpMessageHandler(_ => throw new HttpRequestException("offline")));
        Assert.Contains("HttpRequestException: offline", down.Detail);
        var broken = await Resolve(new StubHttpMessageHandler(_ => throw new InvalidOperationException("reflection disabled")));
        Assert.Equal(CompanyLookupStatus.Unavailable, broken.Status);
        Assert.Contains("reflection disabled", broken.Detail);
    }

    [Fact]
    public async Task Insecure_addresses_are_refused_unless_allowed()
    {
        var plain = StubHttpMessageHandler.FromResponse(HttpStatusCode.OK, """{"tenantCode":"acme","name":"Acme","apiBaseUrl":null}""");
        Assert.Equal(CompanyLookupStatus.InsecureAddress, (await Resolve(plain, bootstrap: "http://boot.example")).Status);
        Assert.Equal(CompanyLookupStatus.Found, (await Resolve(plain, bootstrap: "http://boot.example", allowInsecure: true)).Status);

        var downgrade = StubHttpMessageHandler.FromResponse(HttpStatusCode.OK, """{"tenantCode":"acme","name":"Acme","apiBaseUrl":"http://api.acme.example"}""");
        Assert.Equal(CompanyLookupStatus.InsecureAddress, (await Resolve(downgrade)).Status);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("a/../b")]
    [InlineData("x'; --")]
    public async Task Bad_codes_never_reach_the_server(string? code)
    {
        var handler = StubHttpMessageHandler.FromResponse(HttpStatusCode.OK, "{}");
        Assert.Equal(CompanyLookupStatus.InvalidCode, (await Resolve(handler, code: code)).Status);
        Assert.Null(handler.LastRequest);
    }

    [Fact]
    public async Task Missing_bootstrap_address_and_network_failures_are_reported()
    {
        Assert.Equal(CompanyLookupStatus.NotConfigured, (await Resolve(StubHttpMessageHandler.FromResponse(HttpStatusCode.OK), bootstrap: "")).Status);
        var down = new StubHttpMessageHandler(_ => throw new HttpRequestException("offline"));
        Assert.Equal(CompanyLookupStatus.Unavailable, (await Resolve(down)).Status);
    }

    [Theory]
    [InlineData("acme", "lifedrop.example", "https://acme.lifedrop.example")]
    [InlineData(" ACME ", ".lifedrop.example.", "https://acme.lifedrop.example")]
    [InlineData("acme", "", "https://fallback.example")]
    [InlineData("a/../b", "lifedrop.example", "https://fallback.example")]
    [InlineData("my_co", "lifedrop.example", "https://fallback.example")]
    public void Every_company_has_its_own_server_named_by_its_code(string code, string baseDomain, string expected) =>
        Assert.Equal(expected, CompanyDirectory.ServerFor(code, baseDomain, "https://fallback.example"));

    [Theory]
    [InlineData("""{"tenantCode":"acme","name":"Acme","industry":"food"}""", "food")]
    [InlineData("""{"tenantCode":"acme","name":"Acme","industry":"pharma"}""", "pharma")]
    [InlineData("""{"tenantCode":"acme","name":"Acme"}""", "pharma")]
    [InlineData("""{"tenantCode":"acme","name":"Acme","industry":"x"}""", "pharma")]
    public async Task Industry_defaults_to_pharma(string json, string expected) =>
        Assert.Equal(expected, (await Resolve(StubHttpMessageHandler.FromResponse(HttpStatusCode.OK, json))).Config!.Industry);

    [Theory]
    [InlineData(null, "acme", false)]
    [InlineData("acme", "ACME", false)]
    [InlineData("acme", "beta", true)]
    public void Company_change_is_detected_only_when_old_data_belongs_to_another_company(string? previous, string? next, bool expected) =>
        Assert.Equal(expected, CompanyDirectory.IsCompanyChange(previous, next));
}
