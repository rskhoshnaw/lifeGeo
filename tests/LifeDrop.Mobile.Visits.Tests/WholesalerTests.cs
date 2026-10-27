using System.Net;
using System.Text;
using LifeDrop.Mobile.Auth;
using LifeDrop.Mobile.Auth.Models;
using Xunit;

namespace LifeDrop.Mobile.Visits.Tests;

// The wholesaler officer's orders and the printable list (plan step 5.6).
public class WholesalerTests
{
    private static readonly WholesalerReport.Headings H = new("Orders", "Customer", "Address", "Product", "Quantity", "Date", "Price", "Phone");

    private static WholesalerOrderItem Order(string customer, string? phone = "0750") => new(Guid.NewGuid(), "O-000001", "placed", new DateTimeOffset(2026, 10, 26, 9, 0, 0, TimeSpan.Zero),
        "Al-Noor", customer, phone, "Ankawa", "IQD", 15000m, [new WholesalerOrderLineItem("Paracetamol", "box", 10m), new WholesalerOrderLineItem("Amoxicillin", "box", 2.5m)]);

    [Fact]
    public void TheReportHasTheSevenColumnsAndEscapesEveryText()
    {
        var html = WholesalerReport.BuildHtml([Order("Shifa <b>&</b>")], H, rightToLeft: true);
        Assert.Contains("dir=\"rtl\"", html);
        Assert.Contains("<th>Customer</th><th>Address</th><th>Product</th><th>Quantity</th><th>Date</th><th>Price</th><th>Phone</th>", html);
        Assert.Contains("Shifa &lt;b&gt;&amp;&lt;/b&gt;", html);
        Assert.DoesNotContain("<b>", html);
        Assert.Contains("Paracetamol<br>Amoxicillin", html);
        Assert.Contains("10 box<br>2.5 box", html);
        Assert.Contains("2026-10-26", html);
        Assert.Contains("15000 IQD", html);
    }

    [Fact]
    public void AMissingPhoneIsAnEmptyCellNotTheWordNull() => Assert.DoesNotContain("null", WholesalerReport.BuildHtml([Order("Shifa", null)], H, false), StringComparison.OrdinalIgnoreCase);

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
    private static WholesalerApiClient Client(Handler handler) => new(new HttpClient(handler) { BaseAddress = new Uri("https://api.test") }, new Session());

    [Fact]
    public async Task TheClientReadsTheWholesalersAndOrdersAndSendsDispatched()
    {
        var wid = Guid.NewGuid();
        var oid = Guid.NewGuid();
        var list = new Handler(_ => Json(HttpStatusCode.OK, $$$"""[{"id":"{{{wid}}}","code":"W1","name":"Al-Noor","openOrders":3}]"""));
        var (result, wholesalers) = await Client(list).GetWholesalersAsync();
        Assert.Equal(VisitActionResult.Success, result);
        Assert.Equal(("Al-Noor", 3), (wholesalers[0].Name, wholesalers[0].OpenOrders));
        Assert.Equal("/wholesaler/warehouses", list.Last!.RequestUri!.AbsolutePath);

        var orders = new Handler(_ => Json(HttpStatusCode.OK, $$$"""[{"id":"{{{oid}}}","number":"O-000001","status":"placed","createdAt":"2026-10-26T09:00:00+00:00","wholesalerName":"Al-Noor","customerName":"Shifa","phone":"0750","address":"Ankawa","currency":"IQD","total":15000,"lines":[{"productName":"Paracetamol","unit":"box","quantity":10}]}]"""));
        var (ordersResult, items) = await Client(orders).GetOrdersAsync(wid);
        Assert.Equal(VisitActionResult.Success, ordersResult);
        Assert.Equal(("Shifa", 10m), (items[0].CustomerName, items[0].Lines[0].Quantity));
        Assert.Contains($"warehouseId={wid}", orders.Last!.RequestUri!.Query);

        var bad = new Handler(_ => Json(HttpStatusCode.OK, """[{"id":"00000000-0000-0000-0000-000000000000","number":"x","status":"placed","lines":[]}]"""));
        Assert.Equal(VisitActionResult.ServerError, (await Client(bad).GetOrdersAsync(null)).Result);

        var step = new Handler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        Assert.Equal(VisitActionResult.Success, await Client(step).DispatchAsync(oid));
        Assert.Equal($"/wholesaler/orders/{oid}/dispatch", step.Last!.RequestUri!.AbsolutePath);
        Assert.Equal(HttpMethod.Post, step.Last.Method);
    }
}
