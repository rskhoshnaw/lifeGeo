using System.Net;
using System.Text;
using LifeDrop.Mobile.Auth;
using LifeDrop.Mobile.Auth.Models;
using Xunit;

namespace LifeDrop.Mobile.Visits.Tests;

public class VisitsApiClientTests
{
    private const string OneVisit = """
        [{"id":"11111111-1111-1111-1111-111111111111","tenantId":"22222222-2222-2222-2222-222222222222",
          "userId":"33333333-3333-3333-3333-333333333333","customerId":"44444444-4444-4444-4444-444444444444",
          "status":"Planned","exceptionReason":null,"scheduleDate":"2026-10-05","createdAt":"2026-10-04T08:00:00+00:00",
          "visitType":"pharmacy","priority":"high","plannedTime":"09:30:00","isUnplanned":false,"unplannedReason":null,
          "customerName":"Central Pharmacy","customerCode":"C1"}]
        """;

    private static (VisitsApiClient Client, Handler Handler) Create(Func<HttpRequestMessage, HttpResponseMessage> respond,
        AuthResult<string>? token = null)
    {
        var handler = new Handler(respond);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.test") };
        return (new VisitsApiClient(http, new FixedSession(token ?? AuthResult<string>.Ok("access-1"))), handler);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task ReturnsVisitsAndSendsBearerTokenAndDate()
    {
        var (client, handler) = Create(_ => Json(HttpStatusCode.OK, OneVisit));

        var result = await client.GetDailyPlanAsync(new DateOnly(2026, 10, 5));

        Assert.Equal(PlanLoadStatus.Success, result.Status);
        var visit = Assert.Single(result.Visits);
        Assert.Equal(("Central Pharmacy", "C1", "Planned", "pharmacy", "high"), (visit.CustomerName, visit.CustomerCode, visit.Status, visit.VisitType, visit.Priority));
        Assert.Equal(new TimeOnly(9, 30), visit.PlannedTime);
        Assert.Equal("/visits?scheduleDate=2026-10-05", handler.LastRequest!.RequestUri!.PathAndQuery);
        Assert.Equal("Bearer access-1", handler.LastRequest.Headers.Authorization!.ToString());
    }

    [Fact]
    public async Task EmptyDayIsSuccessWithNoVisits()
    {
        var (client, _) = Create(_ => Json(HttpStatusCode.OK, "[]"));
        var result = await client.GetDailyPlanAsync();
        Assert.Equal(PlanLoadStatus.Success, result.Status);
        Assert.Empty(result.Visits);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, PlanLoadStatus.SessionExpired)]
    [InlineData(HttpStatusCode.Forbidden, PlanLoadStatus.ServerError)]
    [InlineData(HttpStatusCode.InternalServerError, PlanLoadStatus.ServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable, PlanLoadStatus.ServerError)]
    public async Task MapsErrorStatus(HttpStatusCode status, PlanLoadStatus expected)
    {
        var (client, _) = Create(_ => Json(status, """{"code":"X"}"""));
        Assert.Equal(expected, (await client.GetDailyPlanAsync()).Status);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("""[{"id":"00000000-0000-0000-0000-000000000000","status":"Planned"}]""")]
    [InlineData("""[{"id":"11111111-1111-1111-1111-111111111111","status":""}]""")]
    public async Task MalformedPayloadIsServerError(string body)
    {
        var (client, _) = Create(_ => Json(HttpStatusCode.OK, body));
        Assert.Equal(PlanLoadStatus.ServerError, (await client.GetDailyPlanAsync()).Status);
    }

    [Fact]
    public async Task NetworkFailureAndTimeoutAreNetworkErrors()
    {
        var (offline, _) = Create(_ => throw new HttpRequestException("offline"));
        Assert.Equal(PlanLoadStatus.NetworkError, (await offline.GetDailyPlanAsync()).Status);

        var (timeout, _) = Create(_ => throw new TaskCanceledException("timeout"));
        Assert.Equal(PlanLoadStatus.NetworkError, (await timeout.GetDailyPlanAsync()).Status);
    }

    [Fact]
    public async Task CallerCancellationIsRethrown()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var (client, _) = Create(_ => throw new TaskCanceledException());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetDailyPlanAsync(null, cts.Token));
    }

    [Fact]
    public async Task NoUsableSessionSendsNoRequest()
    {
        var (expired, handler) = Create(_ => Json(HttpStatusCode.OK, "[]"), AuthResult<string>.Unauthorized("expired"));
        Assert.Equal(PlanLoadStatus.SessionExpired, (await expired.GetDailyPlanAsync()).Status);
        Assert.Null(handler.LastRequest);

        var (offline, _) = Create(_ => Json(HttpStatusCode.OK, "[]"), AuthResult<string>.NetworkOrServerError("refresh failed"));
        Assert.Equal(PlanLoadStatus.NetworkError, (await offline.GetDailyPlanAsync()).Status);
    }

    [Theory]
    [InlineData(HttpStatusCode.NoContent, "", VisitActionResult.Success)]
    [InlineData(HttpStatusCode.BadRequest, """{"code":"INVALID_STATE"}""", VisitActionResult.InvalidState)]
    [InlineData(HttpStatusCode.BadRequest, """{"code":"BAD_REQUEST"}""", VisitActionResult.ServerError)]
    [InlineData(HttpStatusCode.BadRequest, "not json", VisitActionResult.ServerError)]
    [InlineData(HttpStatusCode.Conflict, """{"code":"CONCURRENCY_CONFLICT"}""", VisitActionResult.Conflict)]
    [InlineData(HttpStatusCode.Unauthorized, "", VisitActionResult.SessionExpired)]
    [InlineData(HttpStatusCode.Forbidden, "", VisitActionResult.Forbidden)]
    [InlineData(HttpStatusCode.NotFound, "", VisitActionResult.NotFound)]
    [InlineData(HttpStatusCode.ServiceUnavailable, "", VisitActionResult.ServerError)]
    public async Task ActionMapsResponse(HttpStatusCode status, string body, VisitActionResult expected)
    {
        var (client, _) = Create(_ => Json(status, body));
        Assert.Equal(expected, await client.MarkEnRouteAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task ActionsPostToTheirRoutesWithBearerToken()
    {
        var id = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var (client, handler) = Create(_ => new HttpResponseMessage(HttpStatusCode.NoContent));

        await client.MarkEnRouteAsync(id);
        Assert.Equal((HttpMethod.Post, $"/visits/{id}/en-route"), (handler.LastRequest!.Method, handler.LastRequest.RequestUri!.AbsolutePath));
        Assert.Equal("Bearer access-1", handler.LastRequest.Headers.Authorization!.ToString());

        await client.MarkArrivedAsync(id);
        Assert.Equal($"/visits/{id}/arrive", handler.LastRequest!.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task ActionNetworkFailureAndMissingSession()
    {
        var (offline, _) = Create(_ => throw new HttpRequestException("offline"));
        Assert.Equal(VisitActionResult.NetworkError, await offline.MarkArrivedAsync(Guid.NewGuid()));

        var (expired, handler) = Create(_ => new HttpResponseMessage(HttpStatusCode.NoContent), AuthResult<string>.Unauthorized("expired"));
        Assert.Equal(VisitActionResult.SessionExpired, await expired.MarkArrivedAsync(Guid.NewGuid()));
        Assert.Null(handler.LastRequest);
    }

    private sealed class FixedSession(AuthResult<string> token) : IAuthSession
    {
        public Task<AuthResult<string>> GetAccessTokenAsync(CancellationToken cancellationToken = default) => Task.FromResult(token);
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastRequest = request;
            return Task.FromResult(respond(request));
        }
    }
}
