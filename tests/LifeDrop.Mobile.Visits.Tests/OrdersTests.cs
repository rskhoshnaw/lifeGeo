using System.Net;
using System.Text;
using LifeDrop.Mobile.Auth;
using LifeDrop.Mobile.Auth.Models;
using Xunit;

namespace LifeDrop.Mobile.Visits.Tests;

// Placing an order from the phone (plan step 2.2): the draft, and the client that sends no price.
public class OrdersTests
{
    private static readonly Guid Customer = Guid.NewGuid();
    private static PricedProduct P(string code, string currency = "IQD", decimal list = 1800m, decimal price = 1620m) =>
        new(Guid.NewGuid(), code, "Name " + code, null, "ناوی " + code, null, "box", currency, list, price);
    private static OrderWarehouse W(string code) => new(Guid.NewGuid(), code, "Warehouse " + code);

    private sealed class FakeOrders : IOrdersApiClient
    {
        public List<(Guid Customer, Guid Warehouse, Guid? Visit, Guid Ref, string? Note, IReadOnlyList<OrderLineInput> Lines)> Sent = [];
        public VisitActionResult Result = VisitActionResult.Success;
        public Task<(VisitActionResult, IReadOnlyList<OrderWarehouse>)> GetWarehousesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<(VisitActionResult, IReadOnlyList<OrderWarehouse>)>((VisitActionResult.Success, Array.Empty<OrderWarehouse>()));
        public Task<(VisitActionResult, IReadOnlyList<PricedProduct>)> GetPricesAsync(Guid customerId, DateOnly day, CancellationToken cancellationToken = default) =>
            Task.FromResult<(VisitActionResult, IReadOnlyList<PricedProduct>)>((VisitActionResult.Success, Array.Empty<PricedProduct>()));
        public Task<(VisitActionResult, PlacedOrder?)> PlaceAsync(Guid customerId, Guid warehouseId, Guid? visitId, Guid clientRef, string? note,
            IReadOnlyList<OrderLineInput> lines, CancellationToken cancellationToken = default, bool offline = false)
        {
            Sent.Add((customerId, warehouseId, visitId, clientRef, note, lines));
            return Task.FromResult<(VisitActionResult, PlacedOrder?)>(Result == VisitActionResult.Success
                ? (Result, new PlacedOrder(Guid.NewGuid(), "O-000001", "IQD", lines.Count, false)) : (Result, null));
        }
    }

    private static OrderDraft Draft(out PricedProduct a, out PricedProduct b, out OrderWarehouse w1, params OrderWarehouse[] more)
    {
        a = P("A"); b = P("B", price: 1000m, list: 1000m);
        w1 = W("W1");
        var draft = new OrderDraft(Customer, Guid.NewGuid());
        draft.Load(new[] { a, b }, more.Length == 0 ? new[] { w1 } : new[] { w1 }.Concat(more).ToArray());
        return draft;
    }

    [Theory]
    [InlineData("3", 3)]
    [InlineData(" 2.5 ", 2.5)]
    [InlineData("2,5", 2.5)]
    [InlineData("٣", 3)]                 // Arabic-Indic digit
    [InlineData("۱۲", 12)]               // Persian digits
    [InlineData("٢٫٥", 2.5)]             // Arabic decimal mark
    [InlineData("", 0)]
    [InlineData(null, 0)]
    public void QuantitiesAreReadInAnyDigitsAndOnlyWhenTheyAreQuantities(string? text, double expected) =>
        Assert.Equal((decimal)expected, OrderDraft.ParseQuantity(text));

    [Theory]
    [InlineData("-1")]
    [InlineData("abc")]
    [InlineData("1.2345")]
    [InlineData("100001")]
    [InlineData("1e3")]
    public void BadQuantitiesAreRefused(string text) => Assert.Null(OrderDraft.ParseQuantity(text));

    [Fact]
    public void TheTotalFollowsTheLinesAndZeroRemovesALine()
    {
        var draft = Draft(out var a, out var b, out _);
        Assert.False(draft.CanPlace);
        Assert.True(draft.SetQuantity(a.ProductId, "3"));
        Assert.True(draft.SetQuantity(b.ProductId, "2"));
        Assert.Equal(1620m * 3 + 1000m * 2, draft.Total);
        Assert.Equal("IQD", draft.Currency);
        Assert.True(draft.CanPlace);   // the only warehouse is chosen already
        Assert.True(draft.SetQuantity(a.ProductId, "0"));
        Assert.Equal(new[] { b.ProductId }, draft.Lines.Select(l => l.Product.ProductId).ToArray());
        Assert.False(draft.SetQuantity(b.ProductId, "abc"));
        Assert.Equal(2m, draft.QuantityOf(b.ProductId));                 // the old value stays
        Assert.False(draft.SetQuantity(Guid.NewGuid(), "1"));            // not a product of this customer's list
    }

    [Fact]
    public void WithSeveralWarehousesOneMustBeChosenAndTwoCurrenciesBlockTheOrder()
    {
        var draft = Draft(out var a, out _, out var w1, W("W2"));
        draft.SetQuantity(a.ProductId, "1");
        Assert.False(draft.CanPlace);          // the visitor has not chosen
        draft.WarehouseId = w1.Id;
        Assert.True(draft.CanPlace);
        draft.WarehouseId = Guid.NewGuid();    // not one of the warehouses
        Assert.False(draft.CanPlace);

        var usd = P("U", "USD", 10m, 10m);
        var mixed = new OrderDraft(Customer);
        mixed.Load(new[] { a, usd }, new[] { w1 });
        mixed.SetQuantity(a.ProductId, "1");
        mixed.SetQuantity(usd.ProductId, "1");
        Assert.True(mixed.MixedCurrency);
        Assert.False(mixed.CanPlace);
    }

    [Fact]
    public async Task OnlyProductsAndQuantitiesAreSentAndTheSameReferenceIsUsedUntilTheOrderIsPlaced()
    {
        var draft = Draft(out var a, out _, out var w1);
        draft.Note = "  at noon ";
        draft.SetQuantity(a.ProductId, "3");
        var api = new FakeOrders { Result = VisitActionResult.NetworkError };
        var reference = draft.ClientRef;

        Assert.Equal(VisitActionResult.NetworkError, await draft.PlaceAsync(api));   // the answer was lost
        Assert.Null(draft.Placed);
        Assert.Equal(VisitActionResult.NetworkError, await draft.PlaceAsync(api));
        api.Result = VisitActionResult.Success;
        Assert.Equal(VisitActionResult.Success, await draft.PlaceAsync(api));

        Assert.All(api.Sent, s => Assert.Equal(reference, s.Ref));                   // every retry carries the same reference
        var last = api.Sent[^1];
        Assert.Equal((Customer, w1.Id, "at noon"), (last.Customer, last.Warehouse, last.Note));
        Assert.Equal(new[] { new OrderLineInput(a.ProductId, 3m) }, last.Lines.ToArray());
        Assert.NotNull(draft.Placed);
        Assert.False(draft.CanPlace);                                                // placed: not a second time

        draft.StartNext();
        Assert.NotEqual(reference, draft.ClientRef);
        Assert.Empty(draft.Lines);
        Assert.Equal(VisitActionResult.InvalidState, await draft.PlaceAsync(api));   // nothing to place
    }

    [Fact]
    public void FindSearchesCodeAndEveryName()
    {
        var draft = Draft(out var a, out var b, out _);
        Assert.Equal(2, draft.Find("", "en").Count);
        Assert.Equal(new[] { a.ProductId }, draft.Find("a", "en").Where(p => p.Code == "A").Select(p => p.ProductId).ToArray());
        Assert.Equal(new[] { a.ProductId }, draft.Find("ناوی A", "ckb").Select(p => p.ProductId).ToArray());   // the Arabic-script name
        Assert.Empty(draft.Find("zzz", "en"));
        Assert.Equal("ناوی A", a.DisplayName("ar"));
        Assert.Equal("Name A", a.DisplayName("en"));      // no English name: the main name
        Assert.Single(draft.Find("", "en", max: 1));
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
    private static OrdersApiClient Client(Handler handler) => new(new HttpClient(handler) { BaseAddress = new Uri("https://api.test") }, new Session());

    [Fact]
    public async Task TheClientSendsNoPriceAndReadsTheOrder()
    {
        var handler = new Handler(_ => Json(HttpStatusCode.Created, """{"id":"11111111-1111-1111-1111-111111111111","number":"O-000007","currency":"IQD","total":4860,"replayed":false}"""));
        var (customer, warehouse, visit, reference, product) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        var (result, order) = await Client(handler).PlaceAsync(customer, warehouse, visit, reference, "note", new[] { new OrderLineInput(product, 3m) });

        Assert.Equal(VisitActionResult.Success, result);
        Assert.Equal(("O-000007", "IQD", 4860m, false), (order!.Number, order.Currency, order.Total, order.Replayed));
        Assert.Equal(("/orders", HttpMethod.Post), (handler.Last!.RequestUri!.AbsolutePath, handler.Last.Method));
        Assert.Contains($"\"clientRef\":\"{reference}\"", handler.Body);
        Assert.Contains($"\"items\":[{{\"productId\":\"{product}\",\"quantity\":3}}]", handler.Body);
        Assert.DoesNotContain("price", handler.Body, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "CURRENCY_MISMATCH", VisitActionResult.CurrencyMismatch)]
    [InlineData(HttpStatusCode.BadRequest, "INVALID_DATA", VisitActionResult.InvalidData)]
    [InlineData(HttpStatusCode.Conflict, "CUSTOMER_NOT_APPROVED", VisitActionResult.CustomerNotApproved)]
    [InlineData(HttpStatusCode.Conflict, "CONFLICT", VisitActionResult.Conflict)]
    [InlineData(HttpStatusCode.NotFound, "NOT_FOUND", VisitActionResult.NotFound)]
    [InlineData(HttpStatusCode.Forbidden, "FORBIDDEN", VisitActionResult.Forbidden)]
    [InlineData(HttpStatusCode.Unauthorized, "UNAUTHENTICATED", VisitActionResult.SessionExpired)]
    public async Task TheClientMapsTheServersAnswers(HttpStatusCode status, string code, VisitActionResult expected)
    {
        var client = Client(new Handler(_ => Json(status, $"{{\"code\":\"{code}\"}}")));
        Assert.Equal(expected, (await client.PlaceAsync(Guid.NewGuid(), Guid.NewGuid(), null, Guid.NewGuid(), null, new[] { new OrderLineInput(Guid.NewGuid(), 1m) })).Result);
    }

    [Fact]
    public async Task PricesAndWarehousesAreReadAndBrokenAnswersAreServerErrors()
    {
        var product = Guid.NewGuid();
        var prices = Client(new Handler(_ => Json(HttpStatusCode.OK, $$"""[{"productId":"{{product}}","code":"P1","name":"Paracetamol","nameCkb":"پاراسیتامۆل","unit":"box","currency":"IQD","listPrice":1800,"price":1620,"discounts":[]}]""")));
        var (result, products) = await prices.GetPricesAsync(Guid.NewGuid(), new DateOnly(2026, 10, 6));
        Assert.Equal(VisitActionResult.Success, result);
        Assert.Equal(("P1", 1620m, "پاراسیتامۆل"), (products[0].Code, products[0].Price, products[0].DisplayName("ckb")));

        var warehouses = Client(new Handler(_ => Json(HttpStatusCode.OK, """[{"id":"22222222-2222-2222-2222-222222222222","code":"W1","name":"Erbil"}]""")));
        Assert.Equal("Erbil", (await warehouses.GetWarehousesAsync()).Warehouses.Single().Name);

        var broken = Client(new Handler(_ => Json(HttpStatusCode.OK, """[{"productId":"00000000-0000-0000-0000-000000000000","code":"","currency":""}]""")));
        Assert.Equal(VisitActionResult.ServerError, (await broken.GetPricesAsync(Guid.NewGuid(), new DateOnly(2026, 10, 6))).Result);
    }
}
