using System.Net;
using System.Text;
using LifeDrop.Mobile.Auth;
using LifeDrop.Mobile.Auth.Models;
using Xunit;

namespace LifeDrop.Mobile.Visits.Tests;

// The specialised visit form on the phone (plan step 2.7).
public class VisitDetailsTests
{
    [Fact]
    public void OnlyThePurposeIsRequiredAndNumbersMustBeNumbers()
    {
        var draft = new VisitDetailsDraft("doctor");
        Assert.False(draft.CanSave);
        draft.Purpose = "product_intro";
        Assert.True(draft.CanSave);
        draft.DailyPatientsText = "٤٠";                       // Arabic-Indic
        Assert.Equal(40, draft.DailyPatients);
        Assert.True(draft.CanSave);
        foreach (var bad in new[] { "abc", "-1", "2.5", "10001" })
        {
            draft.DailyPatientsText = bad;
            Assert.False(draft.CanSave);
        }
        draft.DailyPatientsText = "";
        draft.MonthlySalesText = "1,5";
        Assert.Equal(1.5m, draft.MonthlySales);
        draft.MonthlySalesText = "x";
        Assert.False(draft.CanSave);
        draft.MonthlySalesText = "";
        draft.Purpose = "lunch";
        Assert.False(draft.CanSave);
        draft.Purpose = "other";
        draft.Needs = new string('x', 1001);
        Assert.False(draft.CanSave);
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public string Body = "";
        public HttpRequestMessage? Last;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Last = request;
            Body = request.Content?.ReadAsStringAsync(cancellationToken).Result ?? "";
            return Task.FromResult(respond(request));
        }
    }

    private sealed class Session : IAuthSession
    {
        public Task<AuthResult<string>> GetAccessTokenAsync(CancellationToken cancellationToken = default) => Task.FromResult(AuthResult<string>.Ok("t"));
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static VisitDetailsApiClient Client(Handler handler) => new(new HttpClient(handler) { BaseAddress = new Uri("https://api.test") }, new Session());

    [Fact]
    public async Task OnlyTheFieldsOfTheVisitTypeGoToTheServer()
    {
        var product = Guid.NewGuid();
        var visit = Guid.NewGuid();
        // A pharmacy visit: the doctor and wholesaler boxes may hold text, but they are not sent
        var pharmacy = new VisitDetailsDraft("pharmacy") { Purpose = "order_follow_up", PersonMet = " Dr. A ", Specialty = "Cardiology", DailyPatientsText = "40", MonthlySalesText = "1500000", Competitors = "Pharma X",
            MonthlyVolumeText = "99", PaymentTerms = "30 days", Currency = "USD" };
        pharmacy.ProductIds.Add(product);
        var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        Assert.Equal(VisitActionResult.Success, await Client(handler).SaveAsync(visit, pharmacy));
        Assert.Equal($"/visits/{visit}/report/details", handler.Last!.RequestUri!.AbsolutePath);
        Assert.Equal(HttpMethod.Put, handler.Last.Method);
        using var doc = System.Text.Json.JsonDocument.Parse(handler.Body);
        var root = doc.RootElement;
        Assert.Equal(("Dr. A", 1500000m, "Pharma X", "USD"), (root.GetProperty("personMet").GetString(), root.GetProperty("monthlySales").GetDecimal(), root.GetProperty("competitors").GetString(), root.GetProperty("currency").GetString()));
        foreach (var name in new[] { "specialty", "dailyPatients", "monthlyVolume", "paymentTerms" })
            Assert.Equal(System.Text.Json.JsonValueKind.Null, root.GetProperty(name).ValueKind);
        Assert.Equal(product, root.GetProperty("productIds")[0].GetGuid());

        // A visit of type "other": no amount, so no currency either
        var other = new Handler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        await Client(other).SaveAsync(visit, new VisitDetailsDraft("other") { Purpose = "other", MonthlySalesText = "5" });
        using var doc2 = System.Text.Json.JsonDocument.Parse(other.Body);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, doc2.RootElement.GetProperty("currency").ValueKind);

        var untouched = new Handler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        Assert.Equal(VisitActionResult.InvalidData, await Client(untouched).SaveAsync(visit, new VisitDetailsDraft("doctor")));
        Assert.Null(untouched.Last);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "INVALID_STATE", VisitActionResult.InvalidData)]
    [InlineData(HttpStatusCode.NotFound, "NOT_FOUND", VisitActionResult.NotFound)]
    [InlineData(HttpStatusCode.Forbidden, "FORBIDDEN", VisitActionResult.Forbidden)]
    [InlineData(HttpStatusCode.Conflict, "CONCURRENCY_CONFLICT", VisitActionResult.Conflict)]
    public async Task TheClientMapsTheServersAnswers(HttpStatusCode status, string code, VisitActionResult expected)
    {
        var client = Client(new Handler(_ => Json(status, $"{{\"code\":\"{code}\"}}")));
        Assert.Equal(expected, await client.SaveAsync(Guid.NewGuid(), new VisitDetailsDraft("doctor") { Purpose = "other" }));
    }
}
