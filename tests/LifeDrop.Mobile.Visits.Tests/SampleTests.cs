using System.Net;
using System.Text;
using LifeDrop.Mobile.Auth;
using LifeDrop.Mobile.Auth.Models;
using Xunit;

namespace LifeDrop.Mobile.Visits.Tests;

// Giving pharma samples from the phone (plan step 2.6).
public class SampleTests
{
    private static readonly DateOnly Today = new(2026, 10, 5);

    [Fact]
    public void OnlyTheProductAndAPositiveQuantityAreRequiredAndAnExpiredDateIsRefused()
    {
        var draft = new SampleDraft(Guid.NewGuid());
        Assert.False(draft.CanSave(Today));
        draft.ProductId = Guid.NewGuid();
        Assert.False(draft.CanSave(Today));                 // no quantity
        draft.QuantityText = "٣";                            // Arabic-Indic 3
        Assert.True(draft.CanSave(Today));
        Assert.Equal(3m, draft.Quantity);
        foreach (var bad in new[] { "0", "-1", "abc", "1.2345", "100001" })
        {
            draft.QuantityText = bad;
            Assert.False(draft.CanSave(Today));
        }
        draft.QuantityText = "2,5";
        Assert.Equal(2.5m, draft.Quantity);
        draft.ExpiryText = "2026-10-04";
        Assert.False(draft.CanSave(Today));                 // expired yesterday
        draft.ExpiryText = "2026-10-05";
        Assert.True(draft.CanSave(Today));                  // today is still fine
        draft.ExpiryText = "٢٠٢٧/٠٣/٣١";
        Assert.True(draft.CanSave(Today));
        Assert.True(SampleDraft.TryParseExpiry(draft.ExpiryText, Today, out var expiry));
        Assert.Equal(new DateOnly(2027, 3, 31), expiry);
        draft.ExpiryText = "31.03.2027";
        Assert.False(draft.CanSave(Today));
        draft.ExpiryText = "  ";
        draft.Batch = new string('x', 51);
        Assert.False(draft.CanSave(Today));
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
    private static SamplesApiClient Client(Handler handler) => new(new HttpClient(handler) { BaseAddress = new Uri("https://api.test") }, new Session());

    [Fact]
    public async Task TheRequestCarriesTheSameClientRefAndTheOptionalFieldsOnlyWhenTyped()
    {
        var id = Guid.NewGuid();
        var handler = new Handler(_ => Json(HttpStatusCode.Created, $$$"""{"id":"{{{id}}}","number":"S-000001","replayed":false}"""));
        var draft = new SampleDraft(Guid.NewGuid(), Guid.NewGuid()) { ProductId = Guid.NewGuid(), QuantityText = "3", Recipient = " Dr. Karwan ", Batch = " ", ExpiryText = "2099-01-31" };
        var (result, sample) = await Client(handler).RecordAsync(draft, new PhoneLocation(36.1, 44.0, 9, false));
        Assert.Equal(VisitActionResult.Success, result);
        Assert.Equal(new RecordedSample(id, "S-000001", false), sample);
        Assert.Equal("/samples", handler.Last!.RequestUri!.AbsolutePath);
        using var body = System.Text.Json.JsonDocument.Parse(handler.Body);
        var root = body.RootElement;
        Assert.Equal(("Dr. Karwan", "2099-01-31", 3m), (root.GetProperty("recipientName").GetString(), root.GetProperty("expiryDate").GetString(), root.GetProperty("quantity").GetDecimal()));
        Assert.Equal(System.Text.Json.JsonValueKind.Null, root.GetProperty("batchNumber").ValueKind);
        Assert.Equal(draft.ClientRef, root.GetProperty("clientRef").GetGuid());
        Assert.Equal(36.1, root.GetProperty("location").GetProperty("latitude").GetDouble());

        var untouched = new Handler(_ => Json(HttpStatusCode.Created, "{}"));
        Assert.Equal(VisitActionResult.InvalidData, (await Client(untouched).RecordAsync(new SampleDraft(Guid.NewGuid()), null)).Result);
        Assert.Null(untouched.Last);
    }

    [Theory]
    [InlineData(HttpStatusCode.UnprocessableEntity, "GPS_MOCKED", VisitActionResult.GpsMocked)]
    [InlineData(HttpStatusCode.Conflict, "CONFLICT", VisitActionResult.Conflict)]
    [InlineData(HttpStatusCode.BadRequest, "INVALID_DATA", VisitActionResult.InvalidData)]
    [InlineData(HttpStatusCode.NotFound, "NOT_FOUND", VisitActionResult.NotFound)]
    [InlineData(HttpStatusCode.Forbidden, "FORBIDDEN", VisitActionResult.Forbidden)]
    public async Task TheClientMapsTheServersAnswers(HttpStatusCode status, string code, VisitActionResult expected)
    {
        var client = Client(new Handler(_ => Json(status, $"{{\"code\":\"{code}\"}}")));
        Assert.Equal(expected, (await client.RecordAsync(new SampleDraft(Guid.NewGuid()) { ProductId = Guid.NewGuid(), QuantityText = "1" }, null)).Result);
        Assert.Equal(expected, (await client.GetProductsAsync()).Result);
    }

    [Fact]
    public async Task TheProductListIsReadAndAnAnswerWithoutAnIdIsRefused()
    {
        var id = Guid.NewGuid();
        var (result, items) = await Client(new Handler(_ => Json(HttpStatusCode.OK, $$$"""[{"id":"{{{id}}}","code":"P1","name":"Paracetamol","unit":"box"}]"""))).GetProductsAsync();
        Assert.Equal(VisitActionResult.Success, result);
        Assert.Equal(new SampleProduct(id, "P1", "Paracetamol", "box"), Assert.Single(items));
        Assert.Equal(VisitActionResult.ServerError, (await Client(new Handler(_ => Json(HttpStatusCode.OK, """[{"code":"P1"}]"""))).GetProductsAsync()).Result);
    }
}
