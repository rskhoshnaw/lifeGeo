using System.Net;
using System.Text;
using LifeDrop.Application.Channels;
using LifeDrop.Infrastructure.Channels;
using Xunit;

namespace LifeDrop.Database.Tests;

// The Telegram Bot API client (plan step 3.4) against a fake HTTP handler: no network, no database.
public class TelegramBotTests
{
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Url, string Body)> Requests { get; } = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.Method, request.RequestUri!.ToString(), request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken)));
            return await respond(request);
        }
    }

    private static Handler Answer(HttpStatusCode status, string body = "{}") =>
        new(_ => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") }));

    private static TelegramBot Bot(Handler handler, bool enabled = true, string? token = "123:ABC", string? username = "@lifedrop_bot") =>
        new(new HttpClient(handler), enabled, token, username, "https://tg.test");

    [Fact]
    public async Task Sends_a_message_to_the_chat_and_keeps_the_token_in_the_address_only()
    {
        var handler = Answer(HttpStatusCode.OK, """{"ok":true}""");
        Assert.Equal(ChannelOutcome.Sent, await Bot(handler).SendAsync("777", "hello \"x\"", CancellationToken.None));
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://tg.test/bot123:ABC/sendMessage", request.Url);
        using var body = System.Text.Json.JsonDocument.Parse(request.Body);
        Assert.Equal("777", body.RootElement.GetProperty("chat_id").GetString());
        Assert.Equal("hello \"x\"", body.RootElement.GetProperty("text").GetString());
        Assert.DoesNotContain("123:ABC", request.Body);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, """{"ok":false,"description":"Forbidden: bot was blocked by the user"}""", ChannelOutcome.InvalidAddress)]
    [InlineData(HttpStatusCode.BadRequest, """{"ok":false,"description":"Bad Request: chat not found"}""", ChannelOutcome.InvalidAddress)]
    [InlineData(HttpStatusCode.BadRequest, """{"ok":false,"description":"Bad Request: message is too long"}""", ChannelOutcome.Failed)]
    [InlineData(HttpStatusCode.TooManyRequests, """{"ok":false,"description":"Too Many Requests: retry after 5"}""", ChannelOutcome.Failed)]
    [InlineData(HttpStatusCode.InternalServerError, "", ChannelOutcome.Failed)]
    public async Task Tells_a_gone_chat_from_a_temporary_failure(HttpStatusCode status, string body, ChannelOutcome expected) =>
        Assert.Equal(expected, await Bot(Answer(status, body)).SendAsync("777", "hi", CancellationToken.None));

    [Fact]
    public async Task A_network_error_is_a_temporary_failure()
    {
        var handler = new Handler(_ => throw new HttpRequestException("no route"));
        Assert.Equal(ChannelOutcome.Failed, await Bot(handler).SendAsync("777", "hi", CancellationToken.None));
        Assert.Null(await Bot(handler).GetUpdatesAsync(0, CancellationToken.None));
    }

    [Fact]
    public async Task Does_nothing_while_off_or_without_a_token()
    {
        foreach (var bot in new[] { Bot(Answer(HttpStatusCode.OK), enabled: false), Bot(Answer(HttpStatusCode.OK), token: null), Bot(Answer(HttpStatusCode.OK), token: "  ") })
        {
            Assert.False(bot.IsConfigured);
            Assert.Equal(ChannelOutcome.Failed, await bot.SendAsync("777", "hi", CancellationToken.None));
            Assert.Null(await bot.GetUpdatesAsync(0, CancellationToken.None));
        }
        var handler = Answer(HttpStatusCode.OK);
        await Bot(handler, enabled: false).SendAsync("777", "hi", CancellationToken.None);
        Assert.Empty(handler.Requests);   // not even a request
        Assert.Equal("lifedrop_bot", Bot(handler).BotUsername);   // the @ is dropped
        Assert.Null(Bot(handler, username: " ").BotUsername);
    }

    [Fact]
    public async Task Reads_updates_after_the_offset_and_ignores_what_is_not_a_chat_message()
    {
        const string json = """
            {"ok":true,"result":[
              {"update_id":10,"message":{"chat":{"id":777,"type":"private"},"text":"/start t1_abc"}},
              {"update_id":11,"message":{"chat":{"id":-100123,"type":"supergroup"},"text":"/start t1_abc"}},
              {"update_id":12,"edited_message":{"chat":{"id":5,"type":"private"},"text":"x"}},
              {"update_id":13,"message":{"chat":{"id":778,"type":"private"}}}
            ]}
            """;
        var handler = Answer(HttpStatusCode.OK, json);
        var updates = await Bot(handler).GetUpdatesAsync(10, CancellationToken.None);

        Assert.Contains("/getUpdates?", handler.Requests[0].Url);
        Assert.Contains("offset=10", handler.Requests[0].Url);
        Assert.Equal(4, updates!.Count);
        Assert.Equal((10L, "777", true, "/start t1_abc"), (updates[0].UpdateId, updates[0].ChatId, updates[0].IsPrivate, updates[0].Text));
        Assert.Equal((11L, "-100123", false), (updates[1].UpdateId, updates[1].ChatId, updates[1].IsPrivate));   // a group is never "private"
        Assert.Equal((12L, "", false, (string?)null), (updates[2].UpdateId, updates[2].ChatId, updates[2].IsPrivate, updates[2].Text));   // still advances the offset
        Assert.Equal((13L, "778", true, (string?)null), (updates[3].UpdateId, updates[3].ChatId, updates[3].IsPrivate, updates[3].Text));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{"ok":false,"description":"Unauthorized"}""")]
    [InlineData("""{"ok":true}""")]
    [InlineData("""{"ok":true,"result":{}}""")]
    public void A_broken_answer_is_not_read(string json) => Assert.Null(TelegramBot.Parse(json));

    [Fact]
    public async Task A_failing_updates_request_is_null_not_empty()
    {
        Assert.Null(await Bot(Answer(HttpStatusCode.Unauthorized)).GetUpdatesAsync(0, CancellationToken.None));
        Assert.Empty((await Bot(Answer(HttpStatusCode.OK, """{"ok":true,"result":[]}""")).GetUpdatesAsync(0, CancellationToken.None))!);
    }
}
