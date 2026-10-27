using System.Net;
using System.Text;
using LifeDrop.Application.Marketing;
using LifeDrop.Infrastructure.Marketing;
using Xunit;

namespace LifeDrop.Database.Tests;

// The request to the WhatsApp Business Cloud API and how its answers are read (docs/WHATSAPP-API.md); no network, no database.
public class WhatsAppCloudTests
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public HttpRequestMessage? Last;
        public string Body = "";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Last = request;
            Body = request.Content?.ReadAsStringAsync(cancellationToken).Result ?? "";
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task The_template_message_is_posted_with_the_token_and_the_answers_are_classified()
    {
        Assert.False(new WhatsAppCloud(new HttpClient(), false, "123", "token").IsConfigured);
        Assert.False(new WhatsAppCloud(new HttpClient(), true, "", "token").IsConfigured);
        Assert.False(new WhatsAppCloud(new HttpClient(), true, "123", null).IsConfigured);
        var off = new WhatsAppCloud(new HttpClient(), false, "123", "token");
        Assert.Equal(WhatsAppSendOutcome.Failed, (await off.SendTemplateAsync("9647501112233", "t", "en", "x", CancellationToken.None)).Outcome);

        var handler = new Handler(_ => Json(HttpStatusCode.OK, """{"messaging_product":"whatsapp","contacts":[{"wa_id":"9647501112233"}],"messages":[{"id":"wamid.ABC"}]}"""));
        var cloud = new WhatsAppCloud(new HttpClient(handler), true, "555", "secret-token", "v21.0");
        Assert.True(cloud.IsConfigured);
        var sent = await cloud.SendTemplateAsync("9647501112233", "spring_offer", "ckb", "Dr. \"Ali\"", CancellationToken.None);
        Assert.Equal((WhatsAppSendOutcome.Sent, "wamid.ABC"), (sent.Outcome, sent.MessageId));
        Assert.Equal("https://graph.facebook.com/v21.0/555/messages", handler.Last!.RequestUri!.ToString());
        Assert.Equal(("Bearer", "secret-token"), (handler.Last.Headers.Authorization!.Scheme, handler.Last.Headers.Authorization.Parameter));
        using var doc = System.Text.Json.JsonDocument.Parse(handler.Body);
        var root = doc.RootElement;
        Assert.Equal(("whatsapp", "9647501112233", "template"), (root.GetProperty("messaging_product").GetString(), root.GetProperty("to").GetString(), root.GetProperty("type").GetString()));
        var template = root.GetProperty("template");
        Assert.Equal(("spring_offer", "ckb"), (template.GetProperty("name").GetString(), template.GetProperty("language").GetProperty("code").GetString()));
        var parameter = template.GetProperty("components")[0].GetProperty("parameters")[0];
        Assert.Equal(("text", "Dr. \"Ali\""), (parameter.GetProperty("type").GetString(), parameter.GetProperty("text").GetString()));   // quotes survive; the name is data, not markup

        async Task<WhatsAppSendResult> Answer(HttpStatusCode status, string body) =>
            await new WhatsAppCloud(new HttpClient(new Handler(_ => Json(status, body))), true, "555", "t").SendTemplateAsync("9647501112233", "t", "en", "x", CancellationToken.None);
        var limited = await Answer(HttpStatusCode.TooManyRequests, """{"error":{"message":"Too many","code":130429}}""");
        Assert.Equal((WhatsAppSendOutcome.Retry, "429: 130429 Too many"), (limited.Outcome, limited.Error));
        Assert.Equal(WhatsAppSendOutcome.Retry, (await Answer(HttpStatusCode.BadGateway, "bad gateway")).Outcome);
        var refused = await Answer(HttpStatusCode.BadRequest, """{"error":{"message":"Template name does not exist","code":132001}}""");
        Assert.Equal((WhatsAppSendOutcome.Failed, "400: 132001 Template name does not exist"), (refused.Outcome, refused.Error));
        Assert.Equal(WhatsAppSendOutcome.Failed, (await Answer(HttpStatusCode.Unauthorized, "")).Outcome);

        var network = new WhatsAppCloud(new HttpClient(new Handler(_ => throw new HttpRequestException("down"))), true, "555", "t");
        Assert.Equal(WhatsAppSendOutcome.Retry, (await network.SendTemplateAsync("9647501112233", "t", "en", "x", CancellationToken.None)).Outcome);
    }
}
