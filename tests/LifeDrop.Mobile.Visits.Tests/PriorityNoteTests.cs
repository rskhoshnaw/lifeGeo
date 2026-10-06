using System.Net;
using System.Text;
using LifeDrop.Mobile.Auth;
using LifeDrop.Mobile.Auth.Models;
using Xunit;

namespace LifeDrop.Mobile.Visits.Tests;

// The priority a manager gave a customer is shown in the plan and the add-customers list (plan step 1.26).
public class PriorityNoteTests
{
    private static PlannedVisit Visit(string priority) => new(Guid.NewGuid(), Guid.NewGuid(), "Shifa", "C1", "Planned", new DateOnly(2026, 10, 6), "pharmacy", "normal",
        null, false, null, CustomerPriority: priority);

    [Fact]
    public void OnlyHighAndLowAreMentionedAndNormalAddsNothing()
    {
        Assert.Contains("high", DailyPlan.PlanNotes(Visit("high")));
        Assert.Contains("★", DailyPlan.PlanNotes(Visit("high")));
        Assert.Contains("low", DailyPlan.PlanNotes(Visit("low")));
        Assert.DoesNotContain("★", DailyPlan.PlanNotes(Visit("low")));
        Assert.Equal("", DailyPlan.PlanNotes(Visit("normal")));
    }

    private sealed class Handler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }

    private sealed class Session : IAuthSession
    {
        public Task<AuthResult<string>> GetAccessTokenAsync(CancellationToken cancellationToken = default) => Task.FromResult(AuthResult<string>.Ok("t"));
    }

    private static VisitsApiClient Client(string body) => new(new HttpClient(new Handler(body)) { BaseAddress = new Uri("https://api.test") }, new Session());

    [Fact]
    public async Task ThePlanAndTheSearchReadThePriorityAndOlderServersMeanNormal()
    {
        const string visit = """{"id":"11111111-1111-1111-1111-111111111111","customerId":"44444444-4444-4444-4444-444444444444","status":"Planned","scheduleDate":"2026-10-06","visitType":"pharmacy","priority":"normal","isUnplanned":false,"customerCode":"C1"%EXTRA%}""";
        var withPriority = await Client("[" + visit.Replace("%EXTRA%", ",\"customerPriority\":\"high\"") + "]").GetDailyPlanAsync();
        Assert.Equal("high", Assert.Single(withPriority.Visits).CustomerPriority);
        var without = await Client("[" + visit.Replace("%EXTRA%", "") + "]").GetDailyPlanAsync();
        Assert.Equal("normal", Assert.Single(without.Visits).CustomerPriority);

        const string found = """{"candidates":[{"customerId":"44444444-4444-4444-4444-444444444444","code":"C1","name":"Shifa","customerPriority":"high"},{"customerId":"55555555-5555-5555-5555-555555555555","code":"C2","name":"Noor"}]}""";
        var (_, candidates) = await Client(found).SearchAsync(new SelfPlanQuery(new DateOnly(2026, 10, 6), null, "sh"));
        Assert.Equal(new[] { "high", "normal" }, candidates.Select(c => c.CustomerPriority).ToArray());
    }
}
