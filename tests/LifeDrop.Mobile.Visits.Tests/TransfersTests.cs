using System.Net;
using System.Text;
using LifeDrop.Mobile.Auth;
using LifeDrop.Mobile.Auth.Models;
using Xunit;

namespace LifeDrop.Mobile.Visits.Tests;

// Goods moving between warehouses from the phone (plan step 5.61).
public class TransfersTests
{
    private static readonly Guid A = Guid.NewGuid(), B = Guid.NewGuid(), P1 = Guid.NewGuid(), P2 = Guid.NewGuid();
    private static WarehouseStockItem Stock(Guid warehouse, Guid product, decimal quantity, decimal damaged = 0) =>
        new(warehouse, "W", product, "P", "Product", "box", quantity, true, Damaged: damaged);

    [Fact]
    public void TheDraftTakesSellableAndDamagedApart_NeverMoreThanTheSenderHolds_AndNeedsTwoWarehouses()
    {
        var draft = new TransferDraft(Guid.NewGuid());
        Assert.False(draft.CanSend);
        draft.SetFrom(A, new[] { Stock(A, P1, 10, 2), Stock(A, P2, 0, 0), Stock(B, P2, 5) });
        Assert.Equal(new[] { P1 }, draft.Products);                       // nothing to send of P2 here, and B's stock is not A's
        Assert.True(draft.Set(P1, "sellable", "4"));
        Assert.True(draft.Set(P1, "damaged", "1.5"));
        Assert.False(draft.Set(P1, "sellable", "11"));                    // more than it holds
        Assert.False(draft.Set(P1, "damaged", "3"));
        Assert.False(draft.Set(P1, "sellable", "-1"));
        Assert.False(draft.Set(P1, "sellable", "1.2345"));
        Assert.False(draft.Set(P1, "sellable", "abc"));
        Assert.False(draft.Set(P2, "sellable", "1"));                     // not in this warehouse
        Assert.Equal((4m, 1.5m), (draft.Quantity(P1, "sellable"), draft.Quantity(P1, "damaged")));   // a refused entry changes nothing
        Assert.False(draft.CanSend);                                      // no receiver yet
        draft.ToWarehouseId = A;
        Assert.False(draft.CanSend);                                      // not to itself
        draft.ToWarehouseId = B;
        Assert.True(draft.CanSend);
        Assert.Equal(new[] { ("sellable", 4m), ("damaged", 1.5m) }, draft.Lines().Select(l => (l.Bucket, l.Quantity)).ToArray());
        Assert.True(draft.Set(P1, "sellable", "0"));                      // zero removes the line
        Assert.True(draft.Set(P1, "damaged", ""));
        Assert.False(draft.CanSend);
    }

    [Fact]
    public void ChangingTheSenderClearsTheLines_AndTheReceiverThatBecameTheSenderIsDropped()
    {
        var draft = new TransferDraft(Guid.NewGuid());
        draft.SetFrom(A, new[] { Stock(A, P1, 10) });
        draft.Set(P1, "sellable", "3");
        draft.ToWarehouseId = B;
        draft.SetFrom(B, new[] { Stock(B, P2, 5) });
        Assert.Equal((0, null), (draft.LineCount, draft.ToWarehouseId));
    }

    [Fact]
    public void TheReceiveDraftStartsFullAndRefusesMoreThanWasSent()
    {
        var lines = new[] { new TransferLineItem(1, P1, "P", "Product", "box", "sellable", 5m, null), new TransferLineItem(2, P2, "Q", "Other", "box", "damaged", 2m, null) };
        var draft = new ReceiveDraft(lines);
        Assert.False(draft.AnyShort);
        Assert.False(draft.Set(1, "6"));
        Assert.False(draft.Set(3, "1"));
        Assert.False(draft.Set(1, "-1"));
        Assert.True(draft.Set(2, "1.5"));
        Assert.True(draft.AnyShort);
        Assert.Equal((5m, 1.5m), (draft.Arrived(1), draft.Arrived(2)));
    }

    private sealed class Session : IAuthSession
    {
        public Task<AuthResult<string>> GetAccessTokenAsync(CancellationToken cancellationToken = default) => Task.FromResult(AuthResult<string>.Ok("t"));
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

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static TransfersApiClient Client(Handler handler) => new(new HttpClient(handler) { BaseAddress = new Uri("https://api.test") }, new Session());

    [Fact]
    public async Task TheClientSendsOnlyIdsAndQuantities_ReadsTheListAndTheAnswers()
    {
        var id = Guid.NewGuid();
        var create = new Handler(_ => Json(HttpStatusCode.Created, $$"""{"id":"{{id}}","number":"T-000007","replayed":false}"""));
        var (result, number) = await Client(create).CreateAsync(A, B, Guid.NewGuid(), "note", new[] { new TransferLineInput(P1, 4m, "damaged") });
        Assert.Equal((VisitActionResult.Success, "T-000007"), (result, number));
        Assert.Equal("/transfers", create.Last!.RequestUri!.AbsolutePath);
        Assert.Contains("\"bucket\":\"damaged\"", create.Body);
        Assert.DoesNotContain("price", create.Body, StringComparison.OrdinalIgnoreCase);

        var tooMuch = Client(new Handler(_ => Json(HttpStatusCode.Conflict, """{"code":"TRANSFER_TOO_MUCH"}""")));
        Assert.Equal(VisitActionResult.Conflict, (await tooMuch.CreateAsync(A, B, Guid.NewGuid(), null, new[] { new TransferLineInput(P1, 400m, "sellable") })).Result);

        var list = new Handler(_ => Json(HttpStatusCode.OK, $$"""[{"id":"{{id}}","number":"T-000007","fromWarehouseId":"{{A}}","fromWarehouseCode":"WB","fromWarehouseName":"Basra","toWarehouseId":"{{B}}","toWarehouseCode":"WE","toWarehouseName":"Erbil","status":"sent","note":null,"createdAt":"2026-11-27T08:00:00+00:00","closeNote":null,"items":[{"lineNo":1,"productId":"{{P1}}","productCode":"P","productName":"Cough","unit":"box","bucket":"damaged","quantity":2,"receivedQuantity":null}]}]"""));
        var (_, items) = await Client(list).ListAsync("sent");
        Assert.Contains("status=sent", list.Last!.RequestUri!.Query);
        Assert.Equal(("T-000007", "WB · Basra", "sent", "damaged"), (items.Single().Number, items.Single().FromName, items.Single().Status, items.Single().Lines.Single().Bucket));

        var receive = new Handler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        Assert.Equal(VisitActionResult.Success, await Client(receive).ReceiveAsync(id, new Dictionary<int, decimal> { [1] = 1.5m }, null));
        Assert.Equal($"/transfers/{id}/receive", receive.Last!.RequestUri!.AbsolutePath);
        Assert.Contains("\"receivedQuantity\":1.5", receive.Body);
        var cancel = new Handler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        Assert.Equal(VisitActionResult.Success, await Client(cancel).CancelAsync(id, "wrong city"));
        Assert.Equal($"/transfers/{id}/cancel", cancel.Last!.RequestUri!.AbsolutePath);
        Assert.Equal(VisitActionResult.NotFound, await Client(new Handler(_ => Json(HttpStatusCode.NotFound, """{"code":"NOT_FOUND"}"""))).CancelAsync(id, null));

        var broken = Client(new Handler(_ => Json(HttpStatusCode.OK, """[{"id":"00000000-0000-0000-0000-000000000000","number":"","status":""}]""")));
        Assert.Equal(VisitActionResult.ServerError, (await broken.ListAsync("")).Result);
    }
}
