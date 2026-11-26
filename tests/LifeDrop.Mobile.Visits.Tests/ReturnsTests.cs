using System.Net;
using System.Text;
using System.Text.Json;
using LifeDrop.Mobile.Auth;
using LifeDrop.Mobile.Auth.Models;
using Xunit;

namespace LifeDrop.Mobile.Visits.Tests;

// Customer returns from the phone (plan step 5.54): the draft's rules (reason sets the usual place, a line that goes back needs a warehouse) and what the client sends and reads.
public class ReturnsTests
{
    private static readonly Guid P1 = Guid.NewGuid(), P2 = Guid.NewGuid();

    [Fact]
    public void DamagedAndExpiredGoodsGoApartAndEveryOtherReasonGoesBackToTheWarehouse()
    {
        Assert.Equal("damaged", ReturnDraft.DefaultDestination("damaged"));
        Assert.Equal("damaged", ReturnDraft.DefaultDestination("expired"));
        Assert.Equal("warehouse", ReturnDraft.DefaultDestination("wrong_item"));
        Assert.Equal("warehouse", ReturnDraft.DefaultDestination("customer_request"));
        Assert.Equal("warehouse", ReturnDraft.DefaultDestination("other"));
    }

    [Fact]
    public void ALineNeedsAQuantityAndTheWarehouseOnlyMattersForGoodsThatGoBack()
    {
        var draft = new ReturnDraft(Guid.NewGuid());
        draft.Load([new OrderWarehouse(Guid.NewGuid(), "W1", "Main"), new OrderWarehouse(Guid.NewGuid(), "W2", "Basra")]);
        Assert.False(draft.CanSend);                                  // nothing yet
        Assert.True(draft.SetQuantity(P1, "2"));
        Assert.Equal(("damaged", "damaged"), (draft.ReasonOf(P1), draft.DestinationOf(P1)));   // a new line starts as damaged
        Assert.True(draft.CanSend);                                   // goods that go apart need no warehouse
        Assert.True(draft.SetReason(P1, "wrong_item"));               // the reason sets the usual place
        Assert.Equal("warehouse", draft.DestinationOf(P1));
        Assert.True(draft.NeedsWarehouse);
        Assert.False(draft.CanSend);                                  // two warehouses and none chosen
        draft.WarehouseId = draft.Warehouses[1].Id;
        Assert.True(draft.CanSend);
        Assert.True(draft.SetDestination(P1, "damaged"));             // the user may change the place of one line
        Assert.False(draft.NeedsWarehouse);
    }

    [Fact]
    public void OneWarehouseIsChosenAlone_TextThatIsNotANumberIsRefused_ZeroOrEmptyRemovesTheLine()
    {
        var only = new OrderWarehouse(Guid.NewGuid(), "W1", "Main");
        var draft = new ReturnDraft(Guid.NewGuid());
        draft.Load([only]);
        Assert.Equal(only.Id, draft.WarehouseId);
        Assert.False(draft.SetQuantity(P1, "abc"));
        Assert.False(draft.SetQuantity(P1, "-3"));
        Assert.Equal(0, draft.LineCount);
        Assert.True(draft.SetQuantity(P1, "١٫٥"));                    // digits of any script, comma as the point
        Assert.Equal(1.5m, draft.QuantityOf(P1));
        Assert.True(draft.SetQuantity(P2, "4"));
        Assert.False(draft.SetReason(Guid.NewGuid(), "damaged"));      // only a line that exists changes
        Assert.False(draft.SetReason(P1, "gift"));
        Assert.True(draft.SetQuantity(P1, ""));
        Assert.True(draft.SetQuantity(P2, "0"));
        Assert.Equal(0, draft.LineCount);
    }

    [Fact]
    public void TheLinesGoToTheServerWithQuantityReasonAndPlaceAndNoPrice()
    {
        var draft = new ReturnDraft(Guid.NewGuid());
        draft.SetQuantity(P1, "2");
        draft.SetQuantity(P2, "1");
        draft.SetReason(P2, "customer_request");
        var lines = draft.Lines();
        Assert.Equal(new[] { ("damaged", "damaged", 2m), ("customer_request", "warehouse", 1m) }, lines.Select(l => (l.Reason, l.Destination, l.Quantity)).ToArray());
        Assert.NotEqual(Guid.Empty, draft.ClientRef);
        Assert.Equal(draft.ClientRef, draft.ClientRef);   // the same for every retry of this return
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public HttpRequestMessage? Last { get; private set; }
        public string? Body { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Last = request;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return respond(request);
        }
    }

    private sealed class Session : IAuthSession
    {
        public Task<AuthResult<string>> GetAccessTokenAsync(CancellationToken cancellationToken = default) => Task.FromResult(AuthResult<string>.Ok("t"));
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static ReturnsApiClient Client(Handler handler) => new(new HttpClient(handler) { BaseAddress = new Uri("https://api.test") }, new Session());

    [Fact]
    public async Task TheClientSendsOnlyProductsQuantitiesReasonsAndPlacesAndMapsTheAnswers()
    {
        var handler = new Handler(_ => Json(HttpStatusCode.Created, """{"id":"11111111-1111-1111-1111-111111111111","number":"R-000007","currency":"IQD","total":2500,"replayed":false}"""));
        var customer = Guid.NewGuid();
        var (result, created) = await Client(handler).CreateAsync(customer, null, Guid.NewGuid(), Guid.NewGuid(), "note", [new ReturnLineInput(P1, 2m, "damaged", "damaged")]);
        Assert.Equal(VisitActionResult.Success, result);
        Assert.Equal(("R-000007", 2500m), (created!.Number, created.Total));
        Assert.Equal("/returns", handler.Last!.RequestUri!.AbsolutePath);
        using var body = JsonDocument.Parse(handler.Body!);
        var item = body.RootElement.GetProperty("items")[0];
        Assert.Equal(("damaged", "damaged", 2m), (item.GetProperty("reason").GetString(), item.GetProperty("destination").GetString(), item.GetProperty("quantity").GetDecimal()));
        Assert.False(item.TryGetProperty("price", out _) || item.TryGetProperty("unitPrice", out _));   // the server knows the prices

        Assert.Equal(VisitActionResult.ReturnTooMuch, (await Client(new Handler(_ => Json(HttpStatusCode.Conflict, """{"code":"RETURN_TOO_MUCH"}"""))).CreateAsync(customer, Guid.NewGuid(), null, Guid.NewGuid(), null, [new ReturnLineInput(P1, 9m, "other", "warehouse")])).Result);
        Assert.Equal(VisitActionResult.InvalidData, (await Client(new Handler(_ => Json(HttpStatusCode.BadRequest, """{"code":"INVALID_DATA"}"""))).CreateAsync(customer, null, null, Guid.NewGuid(), null, [])).Result);
        Assert.Contains("more than was delivered", VisitLifecycle.Describe(VisitActionResult.ReturnTooMuch), StringComparison.OrdinalIgnoreCase);   // the tests run in English
    }

    [Fact]
    public async Task MyReturnsAreReadWithTheirGoodsAndTheDecision_ACancelIsPosted()
    {
        var handler = new Handler(r => r.Method == HttpMethod.Post ? new HttpResponseMessage(HttpStatusCode.NoContent) : Json(HttpStatusCode.OK, $$"""
            [{"id":"11111111-1111-1111-1111-111111111111","number":"R-000001","customerName":"Shifa","status":"approved","currency":"IQD","total":2500,"pricesHidden":false,"decisionNote":"ok","createdAt":"2026-11-27T08:00:00+00:00",
              "items":[{"productId":"{{P1}}","productName":"Paracetamol","unit":"box","quantity":2,"lineTotal":2000,"reason":"damaged","destination":"damaged","stockApplied":null},
                       {"productId":"{{P2}}","productName":"Vitamin C","unit":"box","quantity":1,"lineTotal":500,"reason":"customer_request","destination":"warehouse","stockApplied":false}]}]
            """));
        var client = Client(handler);
        var (result, items) = await client.ListMineAsync(null, null);
        Assert.Equal(VisitActionResult.Success, result);
        var mine = items.Single();
        Assert.Equal(("R-000001", "approved", "ok", 2), (mine.Number, mine.Status, mine.DecisionNote, mine.Lines.Count));
        Assert.Equal(new bool?[] { null, false }, mine.Lines.Select(l => l.StockApplied).ToArray());
        Assert.Contains("mine=true", handler.Last!.RequestUri!.Query);
        Assert.Equal(VisitActionResult.Success, await client.CancelAsync(mine.Id, "mistake"));
        Assert.Equal($"/returns/{mine.Id}/cancel", handler.Last!.RequestUri!.AbsolutePath);
        Assert.Equal(VisitActionResult.ServerError, (await Client(new Handler(_ => Json(HttpStatusCode.OK, """[{"id":"00000000-0000-0000-0000-000000000000"}]"""))).ListMineAsync(null, null)).Result);
    }

    [Fact]
    public void ChoosingAnotherOrderDropsTheLinesOfTheOldList()
    {
        var draft = new ReturnDraft(Guid.NewGuid());
        var order = Guid.NewGuid();
        draft.SetQuantity(P1, "2");
        draft.ChooseOrder(order);
        Assert.Equal((order, 0), (draft.OrderId, draft.LineCount));   // the products of another list are gone
        draft.SetQuantity(P2, "1");
        draft.ChooseOrder(order);                                      // the same order again keeps the lines
        Assert.Equal(1, draft.LineCount);
        draft.ChooseOrder(null);
        Assert.Equal((null, 0), (draft.OrderId, draft.LineCount));
    }

    [Fact]
    public async Task TheDeliveredOrdersAndTheirLinesAreReadAndTheOrderGoesWithTheReturn()
    {
        var order = Guid.NewGuid();
        var list = $$"""[{"id":"{{order}}","number":"O-000001","currency":"IQD","total":5000,"pricesHidden":false,"createdAt":"2026-11-10T08:00:00+00:00"}]""";
        var detail = $$"""
            {"id":"{{order}}","number":"O-000001","currency":"IQD","total":5000,"pricesHidden":false,"createdAt":"2026-11-10T08:00:00+00:00",
             "items":[{"productId":"{{P1}}","productCode":"P1","productName":"Paracetamol","unit":"box","quantity":5,"deliveredQuantity":4},
                      {"productId":"{{P2}}","productCode":"P2","productName":"Vitamin C","unit":"box","quantity":2,"deliveredQuantity":0}]}
            """;
        var created = """{"id":"11111111-1111-1111-1111-111111111111","number":"R-000002","currency":"IQD","total":1000,"replayed":false}""";
        var handler = new Handler(r => r.RequestUri!.AbsolutePath.EndsWith("/orders") ? Json(HttpStatusCode.OK, list)
            : r.RequestUri.AbsolutePath.StartsWith("/orders/") ? Json(HttpStatusCode.OK, detail) : Json(HttpStatusCode.Created, created));
        var client = Client(handler);
        var customer = Guid.NewGuid();
        var (result, orders) = await client.ListDeliveredOrdersAsync(customer);
        Assert.Equal(VisitActionResult.Success, result);
        Assert.Equal("O-000001", orders.Single().Number);
        Assert.Contains($"customerId={customer}", handler.Last!.RequestUri!.Query);
        Assert.Contains("status=delivered", handler.Last.RequestUri.Query);
        var (_, lines) = await client.GetOrderLinesAsync(order);
        Assert.Equal(new[] { ("P1", 4m) }, lines.Select(l => (l.Code, l.Delivered)).ToArray());   // a line nothing of which was delivered cannot come back
        await client.CreateAsync(customer, order, null, Guid.NewGuid(), null, [new ReturnLineInput(P1, 1m, "damaged", "damaged")]);
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal(order, body.RootElement.GetProperty("orderId").GetGuid());
    }

    [Fact]
    public async Task ThePictureGoesAsItsOwnBytesWithItsType()
    {
        var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        var id = Guid.NewGuid();
        var result = await Client(handler).AddPhotoAsync(id, [1, 2, 3, 4], "image/jpeg");
        Assert.Equal(VisitActionResult.Success, result);
        Assert.Equal($"/returns/{id}/photos", handler.Last!.RequestUri!.AbsolutePath);
        Assert.Equal("image/jpeg", handler.Last.Content!.Headers.ContentType!.MediaType);
        Assert.Equal("\u0001\u0002\u0003\u0004", handler.Body);
        Assert.Equal(VisitActionResult.Conflict, await Client(new Handler(_ => Json(HttpStatusCode.Conflict, """{"code":"TOO_MANY_PHOTOS"}"""))).AddPhotoAsync(id, [1], "image/png"));
    }
}
