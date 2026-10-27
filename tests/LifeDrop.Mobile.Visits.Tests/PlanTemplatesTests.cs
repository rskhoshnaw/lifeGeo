using System.Net;
using System.Text;
using LifeDrop.Mobile.Auth;
using LifeDrop.Mobile.Auth.Models;
using Xunit;

namespace LifeDrop.Mobile.Visits.Tests;

// Plan templates from the phone (plan step 5.8).
public class PlanTemplatesTests
{
    [Theory]
    [InlineData("2026-10-26", 0, "2026-10-24")]    // Monday: the week began on Saturday the 24th
    [InlineData("2026-10-24", 0, "2026-10-24")]    // Saturday itself
    [InlineData("2026-10-30", 0, "2026-10-24")]    // Friday, the last day of the week
    [InlineData("2026-10-26", -1, "2026-10-17")]
    [InlineData("2026-10-26", 1, "2026-10-31")]
    public void TheWeekStartsOnSaturday(string today, int offset, string expected) =>
        Assert.Equal(DateOnly.Parse(expected), PlanTemplateWeeks.WeekStart(DateOnly.Parse(today), offset));

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
    private static PlanTemplatesApiClient Client(Handler handler) => new(new HttpClient(handler) { BaseAddress = new Uri("https://api.test") }, new Session());

    [Fact]
    public async Task TheClientListsSavesAppliesAndDeletes()
    {
        var id = Guid.NewGuid();
        var list = new Handler(_ => Json(HttpStatusCode.OK, $$$"""[{"id":"{{{id}}}","userId":"{{{Guid.NewGuid()}}}","userEmail":"rep@t","name":"Erbil week","customers":5,"days":3,"createdAt":"2026-10-20T08:00:00+00:00"}]"""));
        var (result, templates) = await Client(list).GetAsync();
        Assert.Equal(VisitActionResult.Success, result);
        Assert.Equal(("Erbil week", 5, 3), (templates[0].Name, templates[0].Customers, templates[0].Days));
        Assert.Equal("/visit-plan/templates", list.Last!.RequestUri!.AbsolutePath);

        var step = new Handler(_ => new HttpResponseMessage(HttpStatusCode.Created));
        Assert.Equal(VisitActionResult.Success, await Client(step).SaveAsync("Week", new DateOnly(2026, 10, 24)));
        Assert.Contains("\"weekStart\":\"2026-10-24\"", step.Body);
        Assert.Contains("\"name\":\"Week\"", step.Body);

        var apply = new Handler(_ => Json(HttpStatusCode.OK, """{"created":12,"skipped":2,"days":6}"""));
        var (applyResult, applied) = await Client(apply).ApplyAsync(id, new DateOnly(2026, 10, 26), 4);
        Assert.Equal(VisitActionResult.Success, applyResult);
        Assert.Equal((12, 2, 6), (applied!.Created, applied.Skipped, applied.Days));
        Assert.Equal($"/visit-plan/templates/{id}/apply", apply.Last!.RequestUri!.AbsolutePath);
        Assert.Contains("\"startDate\":\"2026-10-26\"", apply.Body);
        Assert.Contains("\"weeks\":4", apply.Body);

        var delete = new Handler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        Assert.Equal(VisitActionResult.Success, await Client(delete).DeleteAsync(id));
        Assert.Equal(HttpMethod.Delete, delete.Last!.Method);
    }
}
