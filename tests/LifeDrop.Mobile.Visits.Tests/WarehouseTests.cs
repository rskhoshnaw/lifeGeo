using System.Net;
using System.Text;
using LifeDrop.Mobile.Auth;
using LifeDrop.Mobile.Auth.Models;
using Xunit;

namespace LifeDrop.Mobile.Visits.Tests;

// Warehouse work from the phone (plan step 2.3).
public class WarehouseTests
{
    private static WarehouseOrderDetail Order(string status = "preparing") => new(Guid.NewGuid(), "O-000001", status, "Shifa", "Erbil", null, null, new[]
    {
        new WarehouseOrderLine(1, "PAR-500", "Paracetamol", "box", 10m, null, null),
        new WarehouseOrderLine(2, "AMX-250", "Amoxicillin", "box", 4m, null, null)
    });

    [Fact]
    public void NothingTypedMeansAsOrderedAndALineThatIsShortNeedsAReason()
    {
        var draft = new PreparationDraft(Order());
        Assert.True(draft.CanMarkReady);
        Assert.Equal(new[] { 10m, 4m }, draft.Lines.Select(l => l.Quantity).ToArray());

        Assert.True(draft.SetQuantity(2, "3"));
        Assert.True(draft.IsShort(2));
        Assert.False(draft.CanMarkReady);                      // short, no reason yet
        draft.SetReason(2, "  only 3 in stock ");
        Assert.True(draft.CanMarkReady);
        Assert.Equal(new[] { new PreparedLineInput(1, 10m, null), new PreparedLineInput(2, 3m, "only 3 in stock") }, draft.Lines.ToArray());

        Assert.True(draft.SetQuantity(2, "4"));                // back to the ordered quantity: the reason is dropped
        Assert.Null(draft.Lines[1].Reason);
        Assert.True(draft.SetQuantity(2, ""));
        Assert.Equal(4m, draft.QuantityOf(2));
    }

    [Fact]
    public void MoreThanOrderedOrNotANumberIsRefusedAndTheOldValueStays()
    {
        var draft = new PreparationDraft(Order());
        Assert.True(draft.SetQuantity(1, "7"));
        Assert.False(draft.SetQuantity(1, "11"));
        Assert.False(draft.SetQuantity(1, "abc"));
        Assert.False(draft.SetQuantity(9, "1"));               // no such line
        Assert.Equal(7m, draft.QuantityOf(1));
        Assert.True(draft.SetQuantity(1, "٠"));                // Arabic-Indic zero: nothing prepared (with a reason it can be ready)
        Assert.Equal(0m, draft.QuantityOf(1));
        Assert.False(draft.CanMarkReady);
    }

    [Theory]
    [InlineData("placed")]
    [InlineData("ready")]
    [InlineData("handed_over")]
    public void OnlyAnOrderThatIsBeingPreparedCanBeMarkedReady(string status) => Assert.False(new PreparationDraft(Order(status)).CanMarkReady);

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
    private static WarehouseApiClient Client(Handler handler) => new(new HttpClient(handler) { BaseAddress = new Uri("https://api.test") }, new Session());

    [Fact]
    public async Task TheClientReadsOrdersAndSendsTheStepsWithoutPrices()
    {
        var id = Guid.NewGuid();
        var list = new Handler(_ => Json(HttpStatusCode.OK, $$"""[{"id":"{{id}}","number":"O-000001","customerName":"Shifa","warehouseName":"Erbil","status":"placed","createdAt":"2026-10-06T08:00:00+00:00"}]"""));
        var (result, orders) = await Client(list).GetOrdersAsync("");
        Assert.Equal(VisitActionResult.Success, result);
        Assert.Equal(("O-000001", "placed"), (orders.Single().Number, orders.Single().Status));
        Assert.Equal("/warehouse/orders", list.Last!.RequestUri!.AbsolutePath);
        Assert.DoesNotContain("status=", list.Last.RequestUri.Query);                     // empty = the open orders
        await Client(list).GetOrdersAsync("ready");
        Assert.Contains("status=ready", list.Last.RequestUri!.Query);

        var detail = new Handler(_ => Json(HttpStatusCode.OK, $$"""{"id":"{{id}}","number":"O-000001","customerName":"Shifa","warehouseName":"Erbil","status":"ready","driverEmail":"driver@t1","createdAt":"2026-10-06T08:00:00+00:00","items":[{"lineNo":1,"productCode":"P1","productName":"Paracetamol","unit":"box","quantity":10,"preparedQuantity":8,"shortageReason":"only 8"}]}"""));
        var (_, order) = await Client(detail).GetOrderAsync(id);
        Assert.Equal(("driver@t1", 8m, "only 8"), (order!.DriverEmail, order.Lines[0].PreparedQuantity, order.Lines[0].ShortageReason));

        var step = new Handler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        Assert.Equal(VisitActionResult.Success, await Client(step).ReadyAsync(id, new[] { new PreparedLineInput(1, 8m, "only 8") }));
        Assert.Equal($"/warehouse/orders/{id}/ready", step.Last!.RequestUri!.AbsolutePath);
        Assert.Contains("\"lines\":[{\"lineNo\":1,\"quantity\":8,\"reason\":\"only 8\"}]", step.Body);
        var driver = Guid.NewGuid();
        Assert.Equal(VisitActionResult.Success, await Client(step).HandOverAsync(id, driver));
        Assert.Contains($"\"driverId\":\"{driver}\"", step.Body);
        Assert.Equal(VisitActionResult.Success, await Client(step).StartAsync(id));
        Assert.EndsWith("/start", step.Last.RequestUri!.AbsolutePath);
    }

    [Theory]
    [InlineData(HttpStatusCode.Conflict, "CONFLICT", VisitActionResult.Conflict)]
    [InlineData(HttpStatusCode.BadRequest, "INVALID_DATA", VisitActionResult.InvalidData)]
    [InlineData(HttpStatusCode.NotFound, "NOT_FOUND", VisitActionResult.NotFound)]
    [InlineData(HttpStatusCode.Forbidden, "FORBIDDEN", VisitActionResult.Forbidden)]
    public async Task TheClientMapsTheServersAnswers(HttpStatusCode status, string code, VisitActionResult expected)
    {
        var client = Client(new Handler(_ => Json(status, $"{{\"code\":\"{code}\"}}")));
        Assert.Equal(expected, await client.StartAsync(Guid.NewGuid()));
        Assert.Equal(expected, (await client.GetOrdersAsync(null)).Result);
        Assert.Equal(expected, (await client.GetDriversAsync()).Result);
    }
}
