using System.Net;
using System.Text;
using LifeDrop.Mobile.Auth;
using LifeDrop.Mobile.Auth.Models;
using Xunit;

namespace LifeDrop.Mobile.Visits.Tests;

// Follow-ups from the phone (plan step 2.8).
public class FollowUpTests
{
    private static readonly DateOnly Today = new(2026, 10, 5);

    [Fact]
    public void ASubjectAndADueDayNotBeforeTodayAreNeeded()
    {
        var draft = new FollowUpDraft(Guid.NewGuid());
        Assert.False(draft.CanSave(Today));
        draft.Subject = "  Call about the order ";
        Assert.False(draft.CanSave(Today));                 // no due day
        draft.DueDate = new DateOnly(2026, 10, 4);
        Assert.False(draft.CanSave(Today));                 // yesterday
        draft.DueDate = Today;
        Assert.True(draft.CanSave(Today));                  // today is fine
        draft.Subject = new string('x', 201);
        Assert.False(draft.CanSave(Today));
        draft.Subject = "   ";
        Assert.False(draft.CanSave(Today));
        draft.Subject = "ok";
        draft.Created = true;
        Assert.False(draft.CanSave(Today));                 // already made: no second one from the same form
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
    private static FollowUpsApiClient Client(Handler handler) => new(new HttpClient(handler) { BaseAddress = new Uri("https://api.test") }, new Session());

    [Fact]
    public async Task TheRequestCarriesTheSameClientRefAndTheTrimmedSubject()
    {
        var handler = new Handler(_ => Json(HttpStatusCode.Created, """{"id":"00000000-0000-0000-0000-000000000001","replayed":false}"""));
        var draft = new FollowUpDraft(Guid.NewGuid()) { Subject = " Call back ", DueDate = new DateOnly(2099, 1, 31) };
        Assert.Equal(VisitActionResult.Success, await Client(handler).CreateAsync(draft));
        Assert.Equal("/followups", handler.Last!.RequestUri!.AbsolutePath);
        using var body = System.Text.Json.JsonDocument.Parse(handler.Body);
        var root = body.RootElement;
        Assert.Equal(("Call back", "2099-01-31", draft.ClientRef), (root.GetProperty("subject").GetString(), root.GetProperty("dueDate").GetString(), root.GetProperty("clientRef").GetGuid()));
        Assert.False(root.TryGetProperty("assigneeId", out _));   // a visitor makes follow-ups for themselves only

        var untouched = new Handler(_ => Json(HttpStatusCode.Created, "{}"));
        Assert.Equal(VisitActionResult.InvalidData, await Client(untouched).CreateAsync(new FollowUpDraft(Guid.NewGuid())));
        Assert.Equal(VisitActionResult.InvalidData, await Client(untouched).CompleteAsync(Guid.NewGuid(), " "));
        Assert.Equal(VisitActionResult.InvalidData, await Client(untouched).CancelAsync(Guid.NewGuid(), ""));
        Assert.Null(untouched.Last);
    }

    [Fact]
    public async Task OnlyOpenFollowUpsAreListedAndTheStepsGoToTheRightRoutes()
    {
        var a = Guid.NewGuid();
        var json = $$$"""[{"id":"{{{a}}}","customerName":"Shifa","subject":"Call","dueDate":"2026-10-01","status":"pending","isOverdue":true,"createdByEmail":"m@t1"},""" +
                   $$$"""{"id":"{{{Guid.NewGuid()}}}","customerName":"Noor","subject":"Done","dueDate":"2026-10-02","status":"completed","isOverdue":false},""" +
                   $$$"""{"id":"{{{Guid.NewGuid()}}}","customerName":"Noor","subject":"Old","dueDate":"2026-10-03","status":"cancelled","isOverdue":false}]""";
        var list = Client(new Handler(_ => Json(HttpStatusCode.OK, json)));
        var (result, items) = await list.GetOpenAsync();
        Assert.Equal(VisitActionResult.Success, result);
        Assert.Equal(new FollowUpSummary(a, "Shifa", "Call", new DateOnly(2026, 10, 1), "pending", true, "m@t1"), Assert.Single(items));
        Assert.Equal(VisitActionResult.ServerError, (await Client(new Handler(_ => Json(HttpStatusCode.OK, """[{"status":"pending","dueDate":"2026-10-01"}]"""))).GetOpenAsync()).Result);

        var step = new Handler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        var client = Client(step);
        Assert.Equal(VisitActionResult.Success, await client.StartAsync(a));
        Assert.Equal($"/followups/{a}/start", step.Last!.RequestUri!.AbsolutePath);
        Assert.Equal(VisitActionResult.Success, await client.CompleteAsync(a, " got it "));
        Assert.Contains("\"resultNote\":\"got it\"", step.Body);
        Assert.Equal(VisitActionResult.Success, await client.CancelAsync(a, "closed"));
        Assert.Equal($"/followups/{a}/cancel", step.Last.RequestUri!.AbsolutePath);
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
        Assert.Equal(expected, await client.CompleteAsync(Guid.NewGuid(), "x"));
        Assert.Equal(expected, (await client.GetOpenAsync()).Result);
    }
}
