using System.Net;
using System.Text;
using LifeDrop.Mobile.Auth;
using LifeDrop.Mobile.Auth.Models;
using Xunit;

namespace LifeDrop.Mobile.Visits.Tests;

// "My account" on the phone (plan step 4.46): the profile, the password rules and the answers to a password change.
public class AccountTests
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

    private sealed class Session : IAuthSession
    {
        public Task<AuthResult<string>> GetAccessTokenAsync(CancellationToken cancellationToken = default) => Task.FromResult(AuthResult<string>.Ok("t"));
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static AccountApiClient Client(Handler handler) => new(new HttpClient(handler) { BaseAddress = new Uri("https://api.test") }, new Session());

    [Theory]
    [InlineData("old-password-1", "brand-new-password-2", "brand-new-password-2", null)]
    [InlineData("old-password-1", "short", "short", VisitActionResult.InvalidData)]                                    // too short
    [InlineData("same-password-12", "same-password-12", "same-password-12", VisitActionResult.InvalidData)]       // not different
    [InlineData("old-password-1", "brand-new-password-2", "brand-new-password-3", VisitActionResult.InvalidData)] // the two do not match
    [InlineData("", "brand-new-password-2", "brand-new-password-2", VisitActionResult.InvalidData)]
    [InlineData(null, "brand-new-password-2", "brand-new-password-2", VisitActionResult.InvalidData)]
    public void ThePasswordIsCheckedOnThePhoneFirst(string? current, string? next, string? repeat, VisitActionResult? expected) =>
        Assert.Equal(expected, PasswordRules.Check(current, next, repeat));

    [Fact]
    public async Task TheProfileIsReadAndAPasswordChangeIsSentToTheOwnRoute()
    {
        var handler = new Handler(r => r.RequestUri!.AbsolutePath == "/me"
            ? Json(HttpStatusCode.OK, """{"id":"u1","email":"rep@t1","roles":["SalesRep"],"permissions":["customers.read"]}""")
            : new HttpResponseMessage(HttpStatusCode.NoContent));
        var client = Client(handler);
        var (result, account) = await client.GetAccountAsync();
        Assert.Equal(VisitActionResult.Success, result);
        Assert.Equal(("rep@t1", "SalesRep", "customers.read"), (account!.Email, account.Roles.Single(), account.Permissions.Single()));

        Assert.Equal(VisitActionResult.Success, await client.ChangePasswordAsync("old-password-1", "brand-new-password-2"));
        Assert.Equal(("/me/password", HttpMethod.Put), (handler.Last!.RequestUri!.AbsolutePath, handler.Last.Method));
        Assert.Contains("\"currentPassword\":\"old-password-1\"", handler.Body);
        Assert.Contains("\"newPassword\":\"brand-new-password-2\"", handler.Body);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "WRONG_PASSWORD", VisitActionResult.WrongPassword)]
    [InlineData(HttpStatusCode.BadRequest, "INVALID_DATA", VisitActionResult.InvalidData)]
    [InlineData(HttpStatusCode.Unauthorized, "UNAUTHENTICATED", VisitActionResult.SessionExpired)]
    [InlineData(HttpStatusCode.TooManyRequests, "RATE_LIMITED", VisitActionResult.ServerError)]
    public async Task TheAnswersToAPasswordChangeAreMapped(HttpStatusCode status, string code, VisitActionResult expected)
    {
        var client = Client(new Handler(_ => Json(status, $"{{\"code\":\"{code}\"}}")));
        Assert.Equal(expected, await client.ChangePasswordAsync("old-password-1", "brand-new-password-2"));
    }

    [Fact]
    public void AWrongPasswordHasItsOwnMessage() => Assert.Contains("not right", VisitLifecycle.Describe(VisitActionResult.WrongPassword));
}
