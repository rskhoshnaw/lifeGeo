using System.Net;
using System.Text;
using LifeDrop.Mobile.Auth;
using LifeDrop.Mobile.Auth.Models;
using LifeDrop.Mobile.Offline;
using Xunit;

namespace LifeDrop.Mobile.Visits.Tests;

// Orders, payments, samples, follow-ups and the visit form kept on the phone without internet (plan step 2.13).
public class WorkQueueTests
{
    private sealed class Memory : IDurableStore
    {
        public Dictionary<string, string> Files = [];
        public string? Read(string key) => Files.GetValueOrDefault(key);
        public void Write(string key, string value) => Files[key] = value;
        public void Delete(string key) => Files.Remove(key);
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    // The executor answers per title; a title can be told to fail once with a transient answer.
    private sealed class Executor : IWorkExecutor
    {
        public List<string> Sent = [];
        public Dictionary<string, VisitActionResult> Answers = [];
        public Task<VisitActionResult> ExecuteAsync(WorkItem item, CancellationToken cancellationToken)
        {
            Sent.Add(item.Title);
            return Task.FromResult(Answers.GetValueOrDefault(item.Title, VisitActionResult.Success));
        }
    }

    private static readonly DateTimeOffset Start = new(2026, 10, 5, 6, 0, 0, TimeSpan.Zero);
    private static OrderPayload Payload() => new(Guid.NewGuid(), Guid.NewGuid(), null, Guid.NewGuid(), null, new[] { new OrderLineInput(Guid.NewGuid(), 2m) });

    [Fact]
    public async Task ItemsAreSentInTheOrderTheyWereMadeAndAreSavedAfterEveryChange()
    {
        var store = new Memory();
        var queue = new WorkQueue(new Clock(Start), store);
        queue.Enqueue(WorkKind.Order, "order", Payload());
        queue.Enqueue(WorkKind.Payment, "payment", new { });
        queue.Enqueue(WorkKind.Sample, "sample", new { });
        Assert.Equal(new WorkCounts(3, 0, 0), queue.Counts);
        Assert.Equal(3, new WorkQueue(new Clock(Start), store).Items.Count);   // the file has them: the app can be closed

        var executor = new Executor();
        Assert.True(await queue.SyncAsync(executor));
        Assert.Equal(new[] { "order", "payment", "sample" }, executor.Sent);
        Assert.Equal(new WorkCounts(0, 0, 3), queue.Counts);
        Assert.True(await queue.SyncAsync(executor));
        Assert.Equal(3, executor.Sent.Count);                                    // nothing is sent twice
    }

    [Theory]
    [InlineData(VisitActionResult.NetworkError)]
    [InlineData(VisitActionResult.ServerError)]
    [InlineData(VisitActionResult.SessionExpired)]
    public async Task ATransientFailureStopsSendingAndKeepsEverythingOnThePhone(VisitActionResult failure)
    {
        var queue = new WorkQueue(new Clock(Start), new Memory());
        queue.Enqueue(WorkKind.Order, "first", Payload());
        queue.Enqueue(WorkKind.Order, "second", Payload());
        var executor = new Executor { Answers = { ["first"] = failure } };
        Assert.False(await queue.SyncAsync(executor));
        Assert.Equal(new[] { "first" }, executor.Sent);                          // the second waited behind it
        Assert.Equal(new WorkCounts(2, 0, 0), queue.Counts);
        Assert.Equal(failure == VisitActionResult.SessionExpired, queue.SessionExpired);
        executor.Answers.Clear();
        Assert.True(await queue.SyncAsync(executor));
        Assert.Equal(new[] { "first", "first", "second" }, executor.Sent);
        Assert.Equal(new WorkCounts(0, 0, 2), queue.Counts);
    }

    [Fact]
    public async Task ARefusalNeedsAttentionButDoesNotStopTheOthersAndCanBeSentAgainOrDropped()
    {
        var queue = new WorkQueue(new Clock(Start), new Memory());
        var bad = queue.Enqueue(WorkKind.Order, "bad order", Payload());
        queue.Enqueue(WorkKind.Payment, "payment", new { });
        var executor = new Executor { Answers = { ["bad order"] = VisitActionResult.CustomerNotApproved } };
        Assert.True(await queue.SyncAsync(executor));
        Assert.Equal(new WorkCounts(0, 1, 1), queue.Counts);                     // the payment was sent although the order was refused
        var refused = queue.Items.Single(i => i.Id == bad.Id);
        Assert.Equal((SyncState.Rejected, VisitActionResult.CustomerNotApproved), (refused.State, refused.Answer));

        // A refused item is not sent again by itself; the user sends it again once the reason is gone
        Assert.True(await queue.SyncAsync(executor));
        Assert.Equal(2, executor.Sent.Count);
        executor.Answers.Clear();
        Assert.True(queue.Retry(bad.Id));
        Assert.False(queue.Retry(bad.Id));                                       // only a refused item can be retried
        Assert.True(await queue.SyncAsync(executor));
        Assert.Equal(new WorkCounts(0, 0, 2), queue.Counts);

        var other = queue.Enqueue(WorkKind.Sample, "sample", new { });
        Assert.True(queue.Drop(other.Id));
        Assert.False(queue.Drop(other.Id));
        Assert.True(await queue.SyncAsync(executor));
        Assert.DoesNotContain("sample", executor.Sent);                          // a dropped item is never sent
    }

    [Fact]
    public async Task AVisitFormWaitsWhileEarlierStepsOfItsVisitWaitInTheVisitQueue()
    {
        var visit = Guid.NewGuid();
        var queue = new WorkQueue(new Clock(Start), new Memory());
        queue.Enqueue(WorkKind.VisitForm, "form", new { }, visit);
        queue.Enqueue(WorkKind.Payment, "payment", new { });
        var executor = new Executor();
        var pending = true;
        Assert.True(await queue.SyncAsync(executor, id => id == visit && pending));
        Assert.Equal(new[] { "payment" }, executor.Sent);                        // the payment did not wait for the visit
        pending = false;
        Assert.True(await queue.SyncAsync(executor, id => id == visit && pending));
        Assert.Equal(new[] { "payment", "form" }, executor.Sent);
    }

    [Fact]
    public async Task AnItemBeingSentWhenTheAppClosedIsSentAgainAndOldAcceptedItemsGoAfterTwoDays()
    {
        var store = new Memory();
        var clock = new Clock(Start);
        var queue = new WorkQueue(clock, store);
        queue.Enqueue(WorkKind.Order, "order", Payload());
        // The app closed while the order was being sent (state "sent" on the file)
        store.Files["work-queue"] = store.Files["work-queue"].Replace("\"state\":0", "\"state\":1");
        var reopened = new WorkQueue(clock, store);
        Assert.Equal(new WorkCounts(1, 0, 0), reopened.Counts);
        var executor = new Executor();
        await reopened.SyncAsync(executor);
        Assert.Equal(new WorkCounts(0, 0, 1), reopened.Counts);
        clock.Now = Start.AddHours(49);
        await reopened.SyncAsync(executor);
        Assert.Empty(reopened.Items);
    }

    [Fact]
    public void ADamagedFileIsIgnoredAndTheCacheKeepsProductsWithoutPrices()
    {
        var store = new Memory { Files = { ["work-queue"] = "{not json" } };
        Assert.Empty(new WorkQueue(new Clock(Start), store).Items);

        var cache = new ReferenceCache(store);
        Assert.Empty(cache.Products);
        cache.SaveProducts(new[] { new PricedProduct(Guid.NewGuid(), "P1", "Paracetamol", null, null, "Paracetamol", "box", "IQD", 1800m, 1620m) });
        cache.SaveWarehouses(new[] { new OrderWarehouse(Guid.NewGuid(), "W1", "Erbil") });
        var again = new ReferenceCache(store);
        Assert.Equal((0m, 0m, "P1"), (again.Products[0].Price, again.Products[0].ListPrice, again.Products[0].Code));   // a price on the phone would be out of date: it is not kept
        Assert.Equal("W1", again.Warehouses[0].Code);
        store.Files["cache-products"] = "oops";
        Assert.Empty(new ReferenceCache(store).Products);
    }

    // ---- the executor sends through the real clients

    private sealed class Handler : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path, string Body)> Calls = [];
        public HttpStatusCode Status = HttpStatusCode.OK;
        public string Reply = "{}";
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls.Add((request.Method, request.RequestUri!.AbsolutePath, request.Content?.ReadAsStringAsync(cancellationToken).Result ?? ""));
            return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent(Reply, Encoding.UTF8, "application/json") });
        }
    }

    private sealed class Session : IAuthSession
    {
        public Task<AuthResult<string>> GetAccessTokenAsync(CancellationToken cancellationToken = default) => Task.FromResult(AuthResult<string>.Ok("t"));
    }

    private static (WorkExecutor Executor, Handler Handler) Executor_()
    {
        var handler = new Handler();
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.test") };
        var session = new Session();
        return (new WorkExecutor(new OrdersApiClient(http, session), new PaymentsApiClient(http, session), new SamplesApiClient(http, session), new FollowUpsApiClient(http, session),
            new VisitDetailsApiClient(http, session)), handler);
    }

    [Fact]
    public async Task TheExecutorSendsKeptWorkWithItsOwnReferenceAndTheOfflineMark()
    {
        var (executor, handler) = Executor_();
        var customer = Guid.NewGuid();
        var order = new OrderPayload(customer, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "note", new[] { new OrderLineInput(Guid.NewGuid(), 2m) });
        handler.Reply = $$$"""{"id":"{{{Guid.NewGuid()}}}","number":"O-000001","currency":"IQD","total":2000,"replayed":false,"visitDetached":true}""";
        var queue = new WorkQueue(new Clock(Start));
        Assert.Equal(VisitActionResult.Success, await executor.ExecuteAsync(queue.Enqueue(WorkKind.Order, "o", order), default));
        Assert.Equal("/orders", handler.Calls[^1].Path);
        Assert.Contains($"\"clientRef\":\"{order.ClientRef}\"", handler.Calls[^1].Body);
        Assert.Contains("\"detachVisitIfInvalid\":true", handler.Calls[^1].Body);
        Assert.DoesNotContain("price", handler.Calls[^1].Body, StringComparison.OrdinalIgnoreCase);   // an order never carries a price

        handler.Reply = $$$"""{"id":"{{{Guid.NewGuid()}}}","number":"P-000001","status":"collected","replayed":false,"detached":true}""";
        var payment = new PaymentPayload(customer, Guid.NewGuid(), Guid.NewGuid(), "cash", "IQD", "٥٠٠٠", null, null);
        Assert.Equal(VisitActionResult.Success, await executor.ExecuteAsync(queue.Enqueue(WorkKind.Payment, "p", payment), default));
        Assert.Contains("\"detachOrderIfInvalid\":true", handler.Calls[^1].Body);
        Assert.Contains("\"amount\":5000", handler.Calls[^1].Body);
        Assert.Contains($"\"clientRef\":\"{payment.ClientRef}\"", handler.Calls[^1].Body);

        handler.Reply = $$$"""{"id":"{{{Guid.NewGuid()}}}","number":"S-000001","replayed":false}""";
        var sample = new SamplePayload(customer, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "2", "Dr. A", null, null, null, new PhoneLocation(36.1, 44.0, 9, false));
        Assert.Equal(VisitActionResult.Success, await executor.ExecuteAsync(queue.Enqueue(WorkKind.Sample, "s", sample), default));
        Assert.Contains("\"detachVisitIfInvalid\":true", handler.Calls[^1].Body);
        Assert.Contains("\"latitude\":36.1", handler.Calls[^1].Body);

        handler.Reply = """{"id":"00000000-0000-0000-0000-000000000001","replayed":false}""";
        var followUp = new FollowUpPayload(customer, Guid.NewGuid(), "Call back", new DateOnly(2099, 1, 31));
        Assert.Equal(VisitActionResult.Success, await executor.ExecuteAsync(queue.Enqueue(WorkKind.FollowUpCreate, "f", followUp), default));
        Assert.Contains("\"dueDate\":\"2099-01-31\"", handler.Calls[^1].Body);
        Assert.Contains($"\"clientRef\":\"{followUp.ClientRef}\"", handler.Calls[^1].Body);

        var id = Guid.NewGuid();
        handler.Status = HttpStatusCode.NoContent;
        Assert.Equal(VisitActionResult.Success, await executor.ExecuteAsync(queue.Enqueue(WorkKind.FollowUpStart, "st", new FollowUpStepPayload(id, null)), default));
        Assert.Equal($"/followups/{id}/start", handler.Calls[^1].Path);
        Assert.Equal(VisitActionResult.Success, await executor.ExecuteAsync(queue.Enqueue(WorkKind.FollowUpComplete, "co", new FollowUpStepPayload(id, "got it")), default));
        Assert.Contains("\"resultNote\":\"got it\"", handler.Calls[^1].Body);
        Assert.Equal(VisitActionResult.Success, await executor.ExecuteAsync(queue.Enqueue(WorkKind.FollowUpCancel, "ca", new FollowUpStepPayload(id, "closed")), default));
        Assert.Contains("\"reason\":\"closed\"", handler.Calls[^1].Body);

        var visit = Guid.NewGuid();
        var form = new VisitFormPayload(visit, "doctor", "product_intro", "Dr. A", null, null, new[] { Guid.NewGuid() }, "Cardiology", "40", null, null, null, null, "IQD");
        Assert.Equal(VisitActionResult.Success, await executor.ExecuteAsync(queue.Enqueue(WorkKind.VisitForm, "vf", form, visit), default));
        Assert.Equal($"/visits/{visit}/report/details", handler.Calls[^1].Path);
        Assert.Contains("\"dailyPatients\":40", handler.Calls[^1].Body);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "INVALID_DATA", VisitActionResult.InvalidData)]
    [InlineData(HttpStatusCode.Conflict, "CUSTOMER_NOT_APPROVED", VisitActionResult.CustomerNotApproved)]
    [InlineData(HttpStatusCode.Conflict, "CONFLICT", VisitActionResult.Conflict)]
    [InlineData(HttpStatusCode.NotFound, "NOT_FOUND", VisitActionResult.NotFound)]
    public async Task TheServersRefusalComesBackAsTheAnswerOfTheItem(HttpStatusCode status, string code, VisitActionResult expected)
    {
        var (executor, handler) = Executor_();
        handler.Status = status;
        handler.Reply = $"{{\"code\":\"{code}\"}}";
        var order = new OrderPayload(Guid.NewGuid(), Guid.NewGuid(), null, Guid.NewGuid(), null, new[] { new OrderLineInput(Guid.NewGuid(), 1m) });
        Assert.Equal(expected, await executor.ExecuteAsync(new WorkQueue(new Clock(Start)).Enqueue(WorkKind.Order, "o", order), default));
    }

    [Fact]
    public async Task ADamagedItemIsAnInvalidAnswerNotACrash()
    {
        var (executor, _) = Executor_();
        var item = new WorkItem(Guid.NewGuid(), WorkKind.Order, "broken", null, "{not json", Start);
        Assert.Equal(VisitActionResult.InvalidData, await executor.ExecuteAsync(item, default));
    }

    [Fact]
    public void AFormKeptOnThePhoneOffersNoSecondSaveAndKeepsItsReference()
    {
        var today = new DateOnly(2026, 10, 5);
        var payment = new PaymentDraft(Guid.NewGuid()) { AmountText = "5" };
        Assert.True(payment.CanSave);
        payment.Queued = true;
        Assert.False(payment.CanSave);
        Assert.Equal(payment.ClientRef, new PaymentDraft(payment.CustomerId, null, payment.ClientRef).ClientRef);   // the reference is rebuilt from the item, so a retry is the same payment

        var sample = new SampleDraft(Guid.NewGuid()) { ProductId = Guid.NewGuid(), QuantityText = "1" };
        Assert.True(sample.CanSave(today));
        sample.Queued = true;
        Assert.False(sample.CanSave(today));

        var followUp = new FollowUpDraft(Guid.NewGuid()) { Subject = "x", DueDate = today };
        Assert.True(followUp.CanSave(today));
        followUp.Queued = true;
        Assert.False(followUp.CanSave(today));
    }

    [Fact]
    public void AnOrderWrittenWithoutInternetHasNoPricesAndIsKeptWithItsLines()
    {
        var customer = Guid.NewGuid();
        var product = new PricedProduct(Guid.NewGuid(), "P1", "Paracetamol", null, null, null, "box", "IQD", 0m, 0m);
        var warehouse = new OrderWarehouse(Guid.NewGuid(), "W1", "Erbil");
        var draft = new OrderDraft(customer, Guid.NewGuid()) { PricesKnown = false };
        draft.Load(new[] { product }, new[] { warehouse });
        Assert.True(draft.SetQuantity(product.ProductId, "3"));
        Assert.True(draft.CanPlace);
        var payload = draft.ToPayload();
        Assert.Equal((customer, warehouse.Id, draft.ClientRef), (payload.CustomerId, payload.WarehouseId, payload.ClientRef));
        Assert.Equal(new OrderLineInput(product.ProductId, 3m), Assert.Single(payload.Lines));
        draft.Queued = true;
        Assert.False(draft.CanPlace);
        draft.StartNext();
        Assert.False(draft.Queued);
    }
}
