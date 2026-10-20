using System.Net;
using System.Text;
using LifeDrop.Mobile.Auth;
using LifeDrop.Mobile.Auth.Models;
using Xunit;

namespace LifeDrop.Mobile.Visits.Tests;

public class ChannelsApiClientTests
{
    private static (ChannelsApiClient Client, Handler Handler) Create(Func<HttpRequestMessage, HttpResponseMessage> respond, AuthResult<string>? token = null)
    {
        var handler = new Handler(respond);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.test") };
        return (new ChannelsApiClient(http, new Session(token ?? AuthResult<string>.Ok("access-1"))), handler);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task Status_ReadsTheCompanySettingAndTheUsersLink()
    {
        var (client, handler) = Create(_ => Json(HttpStatusCode.OK, """{"enabled":true,"botUsername":"lifedrop_acme_bot","telegramLinked":false}"""));
        var (result, status) = await client.GetStatusAsync();
        Assert.Equal(VisitActionResult.Success, result);
        Assert.Equal((true, "lifedrop_acme_bot", false), (status!.Enabled, status.BotUsername, status.TelegramLinked));
        Assert.Equal(("/channels", "Bearer access-1"), (handler.Last!.RequestUri!.AbsolutePath, handler.Last.Headers.Authorization!.ToString()));
    }

    [Fact]
    public async Task StartLink_GivesTheBotLinkWithTheOneTimeCode()
    {
        var (client, handler) = Create(_ => Json(HttpStatusCode.OK, """{"code":"acme_k3m9q2x7wb4d","botUsername":"lifedrop_acme_bot","expiresAt":"2026-10-20T12:15:00Z"}"""));
        var (result, link) = await client.StartTelegramLinkAsync();
        Assert.Equal(VisitActionResult.Success, result);
        Assert.Equal("https://t.me/lifedrop_acme_bot?start=acme_k3m9q2x7wb4d", link!.Link);
        Assert.Equal((HttpMethod.Post, "/channels/telegram/link"), (handler.Last!.Method, handler.Last.RequestUri!.AbsolutePath));
    }

    [Fact]
    public async Task StartLink_WithoutABotNameHasNoLinkAndABrokenAnswerIsAServerError()
    {
        var (client, _) = Create(_ => Json(HttpStatusCode.OK, """{"code":"acme_k3m9q2x7wb4d","botUsername":null,"expiresAt":"2026-10-20T12:15:00Z"}"""));
        Assert.Null((await client.StartTelegramLinkAsync()).Link!.Link);

        var (empty, _) = Create(_ => Json(HttpStatusCode.OK, "{}"));
        Assert.Equal(VisitActionResult.ServerError, (await empty.StartTelegramLinkAsync()).Result);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, VisitActionResult.SessionExpired)]
    [InlineData(HttpStatusCode.Forbidden, VisitActionResult.Forbidden)]
    [InlineData(HttpStatusCode.ServiceUnavailable, VisitActionResult.ServerError)]
    public async Task Errors_AreMappedToAnswers(HttpStatusCode status, VisitActionResult expected)
    {
        var (client, _) = Create(_ => new HttpResponseMessage(status));
        Assert.Equal(expected, (await client.GetStatusAsync()).Result);
    }

    [Fact]
    public async Task Unlink_PostsAndAnswersSuccessOnNoContent()
    {
        var (client, handler) = Create(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        Assert.Equal(VisitActionResult.Success, await client.UnlinkTelegramAsync());
        Assert.Equal((HttpMethod.Post, "/channels/telegram/unlink"), (handler.Last!.Method, handler.Last.RequestUri!.AbsolutePath));
    }

    [Fact]
    public async Task NoSession_AsksTheUserToSignInAgain()
    {
        var (client, handler) = Create(_ => new HttpResponseMessage(HttpStatusCode.OK), AuthResult<string>.Unauthorized());
        Assert.Equal(VisitActionResult.SessionExpired, (await client.UnlinkTelegramAsync()));
        Assert.Null(handler.Last);
    }

    private sealed class Session(AuthResult<string> token) : IAuthSession
    {
        public Task<AuthResult<string>> GetAccessTokenAsync(CancellationToken cancellationToken = default) => Task.FromResult(token);
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public HttpRequestMessage? Last { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Last = request;
            return Task.FromResult(respond(request));
        }
    }
}
