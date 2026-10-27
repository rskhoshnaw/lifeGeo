using System.Net;
using System.Text;
using LifeDrop.Mobile.Auth;
using LifeDrop.Mobile.Auth.Models;
using Xunit;

namespace LifeDrop.Mobile.Visits.Tests;

// Marketing messages from the phone (plan step 5.7).
public class MarketingTests
{
    [Fact]
    public void TheNextChatWaitsTwoMinutesAfterTheLastSentMessage()
    {
        var now = new DateTimeOffset(2026, 10, 26, 9, 0, 0, TimeSpan.Zero);
        Assert.Equal(TimeSpan.Zero, MarketingPacing.Remaining(null, now));
        Assert.Equal(TimeSpan.FromSeconds(90), MarketingPacing.Remaining(now.AddSeconds(-30), now));
        Assert.Equal(TimeSpan.FromMinutes(2), MarketingPacing.Remaining(now, now));
        Assert.Equal(TimeSpan.Zero, MarketingPacing.Remaining(now.AddMinutes(-2), now));
        Assert.Equal(TimeSpan.Zero, MarketingPacing.Remaining(now.AddHours(-1), now));
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
    private static MarketingApiClient Client(Handler handler) => new(new HttpClient(handler) { BaseAddress = new Uri("https://api.test") }, new Session());

    [Fact]
    public async Task TheClientCreatesACampaignReadsTheQueueAndMarksMessages()
    {
        var template = Guid.NewGuid();
        var campaign = Guid.NewGuid();
        var message = Guid.NewGuid();

        var create = new Handler(_ => Json(HttpStatusCode.Created, $$$"""{"id":"{{{campaign}}}","queued":3,"skippedNoPhone":1}"""));
        var (result, created) = await Client(create).CreateCampaignAsync("Spring", template, "Pharmacy");
        Assert.Equal(VisitActionResult.Success, result);
        Assert.Equal((campaign, 3, 1), (created!.Id, created.Queued, created.SkippedNoPhone));
        Assert.Contains("\"customerTypes\":[\"Pharmacy\"]", create.Body);
        Assert.Contains($"\"templateId\":\"{template}\"", create.Body);
        await Client(create).CreateCampaignAsync("All", template, "");
        Assert.Contains("\"customerTypes\":[]", create.Body);

        var queue = new Handler(_ => Json(HttpStatusCode.OK, $$$"""[{"id":"{{{message}}}","customerName":"Shifa","phone":"0750","message":"Hi Shifa","status":"queued"}]"""));
        var (queueResult, messages) = await Client(queue).GetQueueAsync(campaign);
        Assert.Equal(VisitActionResult.Success, queueResult);
        Assert.Equal(("Shifa", "0750", "Hi Shifa"), (messages[0].CustomerName, messages[0].Phone, messages[0].Message));
        Assert.Equal("status=queued", queue.Last!.RequestUri!.Query.TrimStart('?'));

        var bad = new Handler(_ => Json(HttpStatusCode.OK, """[{"id":"00000000-0000-0000-0000-000000000000"}]"""));
        Assert.Equal(VisitActionResult.ServerError, (await Client(bad).GetQueueAsync(campaign)).Result);

        var step = new Handler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        Assert.Equal(VisitActionResult.Success, await Client(step).SentAsync(message));
        Assert.Equal($"/marketing/messages/{message}/sent", step.Last!.RequestUri!.AbsolutePath);
        Assert.Equal(VisitActionResult.Success, await Client(step).SkipAsync(message));
        Assert.Equal($"/marketing/messages/{message}/skip", step.Last!.RequestUri!.AbsolutePath);

        var list = new Handler(_ => Json(HttpStatusCode.OK, $$$"""[{"id":"{{{campaign}}}","name":"Spring","queued":2,"sent":1,"skipped":0,"delivery":"api","failed":0}]"""));
        var (_, campaigns) = await Client(list).GetCampaignsAsync();
        Assert.Equal(("Spring", 2, 1, "api"), (campaigns[0].Name, campaigns[0].Queued, campaigns[0].Sent, campaigns[0].Delivery));
        var templates = new Handler(_ => Json(HttpStatusCode.OK, $$$"""[{"id":"{{{template}}}","name":"Offer","body":"Hi {name}","isActive":true}]"""));
        Assert.Equal("Offer", (await Client(templates).GetTemplatesAsync()).Templates[0].Name);
    }
}
