using System.Net;
using System.Text;
using LifeDrop.Mobile.Auth;
using LifeDrop.Mobile.Auth.Models;
using Xunit;

namespace LifeDrop.Mobile.Visits.Tests;

// Recording a payment from the phone (plan step 2.5).
public class PaymentTests
{
    [Fact]
    public void FastPayAndFibNeedTheReferenceAndTheAmountMustBeAPositiveNumber()
    {
        var draft = new PaymentDraft(Guid.NewGuid());
        Assert.False(draft.CanSave);                       // no amount
        draft.AmountText = "٢٥٠٠";                         // Arabic-Indic digits
        Assert.True(draft.CanSave);
        Assert.Equal(2500m, draft.Amount);
        draft.Method = "fib";
        Assert.False(draft.CanSave);                       // FIB needs the transaction number
        draft.Reference = "  ";
        Assert.False(draft.CanSave);
        draft.Reference = "TX-77";
        Assert.True(draft.CanSave);
        draft.Method = "cash";
        foreach (var bad in new[] { "0", "-5", "abc", "1.2345", "1000000000000", " " })
        {
            draft.AmountText = bad;
            Assert.False(draft.CanSave);
        }
        draft.AmountText = "12,5";
        Assert.Equal(12.5m, draft.Amount);
        draft.Currency = "EUR";
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
    private static PaymentsApiClient Client(Handler handler) => new(new HttpClient(handler) { BaseAddress = new Uri("https://api.test") }, new Session());

    [Fact]
    public async Task ThePaymentCarriesTheSameClientRefOnEveryRetryAndNoStatus()
    {
        var id = Guid.NewGuid();
        var handler = new Handler(_ => Json(HttpStatusCode.Created, $$$"""{"id":"{{{id}}}","number":"P-000001","status":"collected","replayed":false}"""));
        var draft = new PaymentDraft(Guid.NewGuid()) { AmountText = "5000", Method = "fastpay", Reference = " FP-1 ", Note = "  " };
        var (result, payment) = await Client(handler).RecordAsync(draft);
        Assert.Equal(VisitActionResult.Success, result);
        Assert.Equal(new RecordedPayment(id, "P-000001", "collected", false), payment);
        Assert.Equal(("/payments", HttpMethod.Post), (handler.Last!.RequestUri!.AbsolutePath, handler.Last.Method));
        using var body = System.Text.Json.JsonDocument.Parse(handler.Body);
        var root = body.RootElement;
        Assert.Equal(("fastpay", "IQD", 5000m, "FP-1"), (root.GetProperty("method").GetString(), root.GetProperty("currency").GetString(), root.GetProperty("amount").GetDecimal(), root.GetProperty("reference").GetString()));
        Assert.Equal(draft.ClientRef, root.GetProperty("clientRef").GetGuid());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, root.GetProperty("note").ValueKind);
        Assert.False(root.TryGetProperty("status", out _));   // the server decides collected or confirmed

        // An invalid draft never reaches the server
        var untouched = new Handler(_ => Json(HttpStatusCode.Created, "{}"));
        Assert.Equal(VisitActionResult.InvalidData, (await Client(untouched).RecordAsync(new PaymentDraft(Guid.NewGuid()))).Result);
        Assert.Null(untouched.Last);
    }

    [Theory]
    [InlineData(HttpStatusCode.Conflict, "CONFLICT", VisitActionResult.Conflict)]
    [InlineData(HttpStatusCode.BadRequest, "INVALID_DATA", VisitActionResult.InvalidData)]
    [InlineData(HttpStatusCode.NotFound, "NOT_FOUND", VisitActionResult.NotFound)]
    [InlineData(HttpStatusCode.Forbidden, "FORBIDDEN", VisitActionResult.Forbidden)]
    public async Task TheClientMapsTheServersAnswers(HttpStatusCode status, string code, VisitActionResult expected)
    {
        var client = Client(new Handler(_ => Json(status, $"{{\"code\":\"{code}\"}}")));
        Assert.Equal(expected, (await client.RecordAsync(new PaymentDraft(Guid.NewGuid()) { AmountText = "1" })).Result);
        Assert.Equal(expected, (await client.GetMineAsync()).Result);
        Assert.Equal(expected, (await client.GetBalanceAsync(Guid.NewGuid())).Result);
    }

    [Fact]
    public async Task TheListAndTheBalanceAreReadAndAnAnswerWithoutAnIdIsRefused()
    {
        var id = Guid.NewGuid();
        var list = Client(new Handler(_ => Json(HttpStatusCode.OK, $$$"""[{"id":"{{{id}}}","number":"P-000001","customerName":"Shifa","method":"cash","currency":"IQD","amount":5000,"status":"confirmed","createdAt":"2026-10-07T08:00:00+00:00"}]""")));
        var (result, items) = await list.GetMineAsync();
        Assert.Equal(VisitActionResult.Success, result);
        Assert.Equal("P-000001", Assert.Single(items).Number);
        Assert.Equal(VisitActionResult.ServerError, (await Client(new Handler(_ => Json(HttpStatusCode.OK, """[{"number":"P-1","status":"confirmed"}]"""))).GetMineAsync()).Result);

        var balance = Client(new Handler(_ => Json(HttpStatusCode.OK, """{"customerId":"00000000-0000-0000-0000-000000000001","items":[{"currency":"IQD","delivered":14000,"confirmed":4000,"collected":5000,"balance":10000}]}""")));
        var (r2, lines) = await balance.GetBalanceAsync(Guid.NewGuid());
        Assert.Equal(VisitActionResult.Success, r2);
        Assert.Equal(new BalanceLine("IQD", 14000m, 4000m, 5000m, 10000m), Assert.Single(lines));
    }
}
