using System.Net;
using System.Text;
using LifeDrop.Mobile.Auth;
using LifeDrop.Mobile.Auth.Models;
using Xunit;

namespace LifeDrop.Mobile.Visits.Tests;

// Adding customers to the user's own plan (plan step 1.25).
public class SelfPlannerTests
{
    private static readonly DateOnly Day = new(2026, 10, 6);

    private sealed class FakeSelfPlan(params SelfPlanCandidate[] found) : ISelfPlanApiClient
    {
        public int Searches;
        public SelfPlanQuery? LastQuery;
        public IReadOnlyList<Guid>? Added;
        public VisitActionResult AddResult = VisitActionResult.Success;

        public Task<(VisitActionResult, IReadOnlyList<SelfPlanCandidate>)> SearchAsync(SelfPlanQuery query, CancellationToken cancellationToken = default)
        {
            Searches++;
            LastQuery = query;
            return Task.FromResult<(VisitActionResult, IReadOnlyList<SelfPlanCandidate>)>((VisitActionResult.Success, found));
        }

        public Task<(VisitActionResult, int)> AddAsync(SelfPlanQuery query, IReadOnlyList<Guid> customerIds, CancellationToken cancellationToken = default)
        {
            Added = customerIds;
            return Task.FromResult((AddResult, AddResult == VisitActionResult.Success ? customerIds.Count : 0));
        }
    }

    private static SelfPlanCandidate C(string code) => new(Guid.NewGuid(), code, "Name " + code, null, null, "none");

    [Theory]
    [InlineData(null, null, false)]
    [InlineData(null, "a", false)]
    [InlineData(null, "  a ", false)]
    [InlineData(null, "ab", true)]
    public void ASearchNeedsAPlaceOrTwoLetters(object? place, string? text, bool expected) =>
        Assert.Equal(expected, SelfPlanner.CanSearch(place is null ? null : Guid.NewGuid(), text));

    [Fact]
    public async Task APlaceAloneIsEnoughAndTooShortTextNeverReachesTheServer()
    {
        var api = new FakeSelfPlan(C("A"));
        var planner = new SelfPlanner(api);

        Assert.Equal(VisitActionResult.InvalidData, await planner.SearchAsync(Day, null, "a"));
        Assert.Equal(0, api.Searches);
        Assert.Empty(planner.Candidates);

        var place = Guid.NewGuid();
        Assert.Equal(VisitActionResult.Success, await planner.SearchAsync(Day, place, "  "));
        Assert.Equal(new SelfPlanQuery(Day, place, null), api.LastQuery);
        Assert.Single(planner.Candidates);
    }

    [Fact]
    public async Task OnlyTickedCustomersAreAddedInListOrderAndLeaveTheList()
    {
        var (a, b, c) = (C("A"), C("B"), C("C"));
        var api = new FakeSelfPlan(a, b, c);
        var planner = new SelfPlanner(api);
        await planner.SearchAsync(Day, null, "name");

        Assert.True(planner.Toggle(c.CustomerId));
        Assert.True(planner.Toggle(a.CustomerId));
        Assert.False(planner.Toggle(Guid.NewGuid()));   // not in the list
        Assert.Equal((VisitActionResult.Success, 2), await planner.AddAsync());

        Assert.Equal(new[] { a.CustomerId, c.CustomerId }, api.Added!.ToArray());
        Assert.Equal(new[] { b.CustomerId }, planner.Candidates.Select(x => x.CustomerId).ToArray());
        Assert.Empty(planner.Selected);
        // Nothing ticked: nothing is sent
        api.Added = null;
        Assert.Equal((VisitActionResult.InvalidState, 0), await planner.AddAsync());
        Assert.Null(api.Added);
    }

    [Fact]
    public async Task TickingTwiceUnticksAndAFailedAddKeepsTheTicks()
    {
        var a = C("A");
        var api = new FakeSelfPlan(a) { AddResult = VisitActionResult.NetworkError };
        var planner = new SelfPlanner(api);
        await planner.SearchAsync(Day, null, "name");

        Assert.True(planner.Toggle(a.CustomerId));
        Assert.False(planner.Toggle(a.CustomerId));
        Assert.True(planner.Toggle(a.CustomerId));
        Assert.Equal((VisitActionResult.NetworkError, 0), await planner.AddAsync());
        Assert.True(planner.IsSelected(a.CustomerId));
        Assert.Single(planner.Candidates);
    }

    [Fact]
    public async Task AtMostTwoHundredCanBeTickedAtOnce()
    {
        var many = Enumerable.Range(0, SelfPlanner.MaxPerAdd + 1).Select(i => C("C" + i)).ToArray();
        var planner = new SelfPlanner(new FakeSelfPlan(many));
        await planner.SearchAsync(Day, null, "name");
        foreach (var c in many.Take(SelfPlanner.MaxPerAdd)) Assert.True(planner.Toggle(c.CustomerId));
        Assert.False(planner.Toggle(many[^1].CustomerId));
        Assert.Equal(SelfPlanner.MaxPerAdd, planner.Selected.Count);
    }

    [Fact]
    public async Task ANewSearchClearsTheTicks()
    {
        var a = C("A");
        var planner = new SelfPlanner(new FakeSelfPlan(a));
        await planner.SearchAsync(Day, null, "name");
        planner.Toggle(a.CustomerId);
        await planner.SearchAsync(Day, null, "name");
        Assert.Empty(planner.Selected);
    }

    // The real client: no assignee is sent (the server plans for the caller), hours are not applied, ids are the ticked ones.
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

    [Fact]
    public async Task TheClientSendsTheQueryWithoutAnAssigneeAndReadsTheList()
    {
        var customer = Guid.NewGuid();
        var handler = new Handler(_ => Json(HttpStatusCode.OK, $$"""{"matching":1,"included":1,"candidates":[{"customerId":"{{customer}}","code":"C1","name":"Shifa","type":"Pharmacy","place":"Erbil › N1","lastVisitDate":"2026-09-01","windowStatus":"outside","included":true,"skipReason":null}]}"""));
        var client = new VisitsApiClient(new HttpClient(handler) { BaseAddress = new Uri("https://api.test") }, new Session());
        var place = Guid.NewGuid();

        var (result, found) = await client.SearchAsync(new SelfPlanQuery(Day, place, "shi"));

        Assert.Equal(VisitActionResult.Success, result);
        Assert.Equal(new SelfPlanCandidate(customer, "C1", "Shifa", "Erbil › N1", new DateOnly(2026, 9, 1), "outside"), Assert.Single(found));
        Assert.Equal("/visit-plan/preview", handler.Last!.RequestUri!.AbsolutePath);
        Assert.Contains("\"scheduleDate\":\"2026-10-06\"", handler.Body);
        Assert.Contains($"\"placeId\":\"{place}\"", handler.Body);
        Assert.Contains("\"query\":\"shi\"", handler.Body);
        Assert.Contains("\"respectWindows\":false", handler.Body);
        Assert.DoesNotContain("assignedUserId", handler.Body, StringComparison.OrdinalIgnoreCase);

        handler = new Handler(_ => Json(HttpStatusCode.OK, """{"created":1,"skipped":0,"visitIds":[]}"""));
        client = new VisitsApiClient(new HttpClient(handler) { BaseAddress = new Uri("https://api.test") }, new Session());
        Assert.Equal((VisitActionResult.Success, 1), await client.AddAsync(new SelfPlanQuery(Day, place, null), new[] { customer }));
        Assert.Equal("/visit-plan/generate", handler.Last!.RequestUri!.AbsolutePath);
        Assert.Contains($"\"customerIds\":[\"{customer}\"]", handler.Body);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, VisitActionResult.Forbidden)]
    [InlineData(HttpStatusCode.BadRequest, VisitActionResult.InvalidData)]
    [InlineData(HttpStatusCode.Unauthorized, VisitActionResult.SessionExpired)]
    public async Task TheClientMapsRefusals(HttpStatusCode status, VisitActionResult expected)
    {
        var handler = new Handler(_ => Json(status, "{\"code\":\"INVALID_DATA\"}"));
        var client = new VisitsApiClient(new HttpClient(handler) { BaseAddress = new Uri("https://api.test") }, new Session());
        Assert.Equal(expected, (await client.SearchAsync(new SelfPlanQuery(Day, null, "ab"))).Result);
        Assert.Equal((expected, 0), await client.AddAsync(new SelfPlanQuery(Day, null, "ab"), new[] { Guid.NewGuid() }));
    }
}
