using System.Net;
using System.Text;
using LifeDrop.Mobile.Auth;
using LifeDrop.Mobile.Auth.Models;
using Xunit;

namespace LifeDrop.Mobile.Visits.Tests;

// The product catalogue page (plan step 4.43): the client, the photo address, the filters, and prices the server hid.
public class ProductsTests
{
    private static ProductItem P(string code, string? specialty = null, string? maker = null, bool inStock = true, string? ckb = null, bool hidden = false) =>
        new(Guid.NewGuid(), code, "Name " + code, ckb, null, null, "box", "IQD", hidden ? 0m : 1500m, specialty, maker, null, inStock ? 5m : 0m, inStock, null, null, hidden);

    [Fact]
    public void TheFiltersWorkTogetherAndTextMatchesCodeOrAnyName()
    {
        var items = new[] { P("A1", "pediatrics", "Pioneer", ckb: "پاراسیتامۆل"), P("A2", "pediatrics", "Samara", inStock: false), P("B1", "ent", "pioneer"), P("B2") };
        Assert.Equal(4, ProductFilter.Apply(items, null, null, StockFilter.All, null).Count);
        Assert.Equal(new[] { "A1", "A2" }, ProductFilter.Apply(items, "a", "pediatrics", StockFilter.All, null).Select(p => p.Code).ToArray());
        Assert.Equal(new[] { "A2" }, ProductFilter.Apply(items, null, "pediatrics", StockFilter.OutOfStock, null).Select(p => p.Code).ToArray());
        Assert.Equal(new[] { "A1", "B1" }, ProductFilter.Apply(items, null, null, StockFilter.All, "PIONEER").Select(p => p.Code).ToArray());   // a manufacturer matches whatever its capitals
        Assert.Equal(new[] { "A1" }, ProductFilter.Apply(items, "پاراسی", null, StockFilter.All, null).Select(p => p.Code).ToArray());
        Assert.Equal(new[] { "A1", "A2", "B2" }.Length, ProductFilter.Apply(items, null, null, StockFilter.All, null, max: 3).Count);
        Assert.Equal(new[] { "ent", "pediatrics" }, ProductFilter.Specialties(items).ToArray());
        Assert.Equal(new[] { "Pioneer", "Samara" }, ProductFilter.Manufacturers(items).ToArray());   // "pioneer" is the same maker
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public HttpRequestMessage? Last;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Last = request;
            return Task.FromResult(respond(request));
        }
    }

    private sealed class Session : IAuthSession
    {
        public Task<AuthResult<string>> GetAccessTokenAsync(CancellationToken cancellationToken = default) => Task.FromResult(AuthResult<string>.Ok("t"));
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static ProductsApiClient Client(Handler handler) => new(new HttpClient(handler) { BaseAddress = new Uri("https://api.test") }, new Session());

    [Fact]
    public async Task TheClientReadsTheCatalogueAndBuildsThePhotoAddress()
    {
        var handler = new Handler(_ => Json(HttpStatusCode.OK, """
            [{"id":"11111111-1111-1111-1111-111111111111","code":"P1","name":"Paracetamol","nameCkb":"پاراسیتامۆل","unit":"box","currency":"IQD","basePrice":1500,"status":"active",
              "specialty":"pediatrics","manufacturer":"Pioneer","expiryDate":"2027-03-31","stockQty":0,"inStock":false,"notes":"Keep cool","photoKey":"0123456789abcdef0123456789abcdef","pricesHidden":false}]
            """));
        var client = Client(handler);
        var (result, products) = await client.GetProductsAsync();
        Assert.Equal(VisitActionResult.Success, result);
        var p = products.Single();
        Assert.Equal(("P1", "pediatrics", "Pioneer", new DateOnly(2027, 3, 31), false, 1500m, "پاراسیتامۆل"), (p.Code, p.Specialty, p.Manufacturer, p.ExpiryDate, p.InStock, p.BasePrice, p.DisplayName("ckb")));
        Assert.Equal("/products", handler.Last!.RequestUri!.AbsolutePath);
        Assert.Equal("https://api.test/product-photos/0123456789abcdef0123456789abcdef", client.PhotoUrl(p.PhotoKey));
        Assert.Null(client.PhotoUrl(null));
    }

    [Fact]
    public async Task HiddenPricesArriveAsAFlagAndABrokenAnswerIsAServerError()
    {
        var hidden = Client(new Handler(_ => Json(HttpStatusCode.OK, """[{"id":"11111111-1111-1111-1111-111111111111","code":"P1","name":"X","unit":"box","currency":"IQD","basePrice":0,"inStock":true,"pricesHidden":true}]""")));
        Assert.True((await hidden.GetProductsAsync()).Products.Single().PricesHidden);
        var broken = Client(new Handler(_ => Json(HttpStatusCode.OK, """[{"id":"00000000-0000-0000-0000-000000000000","code":"","name":"","currency":""}]""")));
        Assert.Equal(VisitActionResult.ServerError, (await broken.GetProductsAsync()).Result);
        Assert.Equal(VisitActionResult.Forbidden, (await Client(new Handler(_ => Json(HttpStatusCode.Forbidden, """{"code":"FORBIDDEN"}"""))).GetProductsAsync()).Result);
    }

    [Fact]
    public async Task TheOrderPriceFlagReachesTheDraftAndTheOutOfStockAnswerIsMapped()
    {
        var product = Guid.NewGuid();
        var prices = Client2(new Handler(_ => Json(HttpStatusCode.OK, $$"""[{"productId":"{{product}}","code":"P1","name":"Paracetamol","unit":"box","currency":"IQD","listPrice":0,"price":0,"pricesHidden":true}]""")));
        var (_, items) = await prices.GetPricesAsync(Guid.NewGuid(), new DateOnly(2026, 10, 6));
        var draft = new OrderDraft(Guid.NewGuid(), Guid.NewGuid());
        draft.Load(items, new[] { new OrderWarehouse(Guid.NewGuid(), "W1", "Main") });
        Assert.True(draft.PricesHidden);

        var placed = Client2(new Handler(_ => Json(HttpStatusCode.Created, """{"id":"11111111-1111-1111-1111-111111111111","number":"O-000009","currency":"IQD","total":0,"replayed":false,"pricesHidden":true}""")));
        Assert.True((await placed.PlaceAsync(Guid.NewGuid(), Guid.NewGuid(), null, Guid.NewGuid(), null, new[] { new OrderLineInput(product, 1m) })).Order!.PricesHidden);

        var stock = Client2(new Handler(_ => Json(HttpStatusCode.Conflict, """{"code":"OUT_OF_STOCK"}""")));
        Assert.Equal(VisitActionResult.OutOfStock, (await stock.PlaceAsync(Guid.NewGuid(), Guid.NewGuid(), null, Guid.NewGuid(), null, new[] { new OrderLineInput(product, 1m) })).Result);
        Assert.Contains("out of stock", VisitLifecycle.Describe(VisitActionResult.OutOfStock));   // the tests run in English
    }

    private static OrdersApiClient Client2(Handler handler) => new(new HttpClient(handler) { BaseAddress = new Uri("https://api.test") }, new Session());
}
