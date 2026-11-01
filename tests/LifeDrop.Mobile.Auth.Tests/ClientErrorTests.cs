using System.Net;
using System.Net.Sockets;
using LifeDrop.Mobile.Auth.Tests.Stubs;
using Xunit;

namespace LifeDrop.Mobile.Auth.Tests;

// Connection errors kept on the phone and sent later (plan step 5.10).
public class ClientErrorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lifedrop-client-errors-" + Guid.NewGuid().ToString("N"));
    private string File1 => Path.Combine(_dir, "client-errors.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private static PendingClientError Error(string message = "boom", string? email = "panar@example.com") =>
        new(DateTimeOffset.UtcNow, "login", "dns", "HttpRequestException", message, "Lifedrops", email, "device-1", "1.0.0", "android");

    [Fact]
    public void Dns_failure_is_classified_as_dns()
    {
        var failure = NetworkFailure.From(new HttpRequestException("Connection failed", new SocketException((int)SocketError.HostNotFound)));
        Assert.Equal("dns", failure.Kind);
        Assert.Equal("HttpRequestException", failure.Type);
    }

    [Theory]
    [InlineData("No such host is known", "dns")]
    [InlineData("Unable to resolve host \"lifedrop.example.com\": No address associated with hostname", "dns")]
    [InlineData("NSURLErrorDomain Code=-1003 \"A server with the specified hostname could not be found.\"", "dns")]
    [InlineData("The SSL connection could not be established", "tls")]
    [InlineData("Something odd", "other")]
    public void Platform_wording_is_classified(string message, string kind) =>
        Assert.Equal(kind, NetworkFailure.From(new HttpRequestException(message)).Kind);

    [Fact]
    public void Timeout_and_long_messages_are_handled()
    {
        Assert.Equal("timeout", NetworkFailure.From(new TaskCanceledException("x")).Kind);
        Assert.Equal("timeout", NetworkFailure.Timeout().Kind);
        Assert.True(NetworkFailure.From(new HttpRequestException(new string('a', 1000))).Message.Length <= 300);
        Assert.Equal("http", NetworkFailure.Http(502).Kind);
    }

    [Fact]
    public void Outbox_keeps_errors_between_runs_and_drops_the_oldest_over_capacity()
    {
        var outbox = new ClientErrorOutbox(File1, capacity: 3);
        for (var i = 1; i <= 5; i++) outbox.Add(Error("e" + i));
        var again = new ClientErrorOutbox(File1, capacity: 3);   // another run of the app
        Assert.Equal(new[] { "e3", "e4", "e5" }, again.Pending().Select(e => e.Message));
        again.RemoveFirst(2);
        Assert.Equal(new[] { "e5" }, again.Pending().Select(e => e.Message));
        again.RemoveFirst(10);
        Assert.Empty(again.Pending());
        Assert.False(File.Exists(File1));
    }

    [Fact]
    public void Damaged_outbox_file_is_treated_as_empty()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(File1, "{not json");
        var outbox = new ClientErrorOutbox(File1);
        Assert.Empty(outbox.Pending());
        outbox.Add(Error());
        Assert.Single(outbox.Pending());
    }

    [Fact]
    public async Task Flush_sends_company_code_and_errors_without_any_password_and_empties_the_outbox()
    {
        var outbox = new ClientErrorOutbox(File1);
        outbox.Add(Error("first"));
        outbox.Add(Error("second"));
        var handler = StubHttpMessageHandler.FromResponse(HttpStatusCode.OK, """{"stored":2}""");
        var sent = await ClientErrorSender.FlushAsync(new HttpClient(handler) { BaseAddress = new Uri("https://lifedrops.example.com") }, outbox, "lifedrops");

        Assert.Equal(2, sent);
        Assert.Empty(outbox.Pending());
        Assert.Equal("https://lifedrops.example.com/client-errors", handler.LastRequest!.RequestUri!.ToString());
        Assert.Contains("\"companyCode\":\"lifedrops\"", handler.LastRequestBody);
        Assert.Contains("\"enteredEmail\":\"panar@example.com\"", handler.LastRequestBody);
        Assert.Contains("\"enteredCompanyCode\":\"Lifedrops\"", handler.LastRequestBody);
        Assert.DoesNotContain("password", handler.LastRequestBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", handler.LastRequestBody, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, 0)]    // the server will never take them: dropped
    [InlineData(HttpStatusCode.NotFound, 0)]
    [InlineData(HttpStatusCode.TooManyRequests, 1)]   // kept for the next time
    [InlineData(HttpStatusCode.ServiceUnavailable, 1)]
    public async Task Flush_keeps_errors_only_when_the_server_may_take_them_later(HttpStatusCode status, int left)
    {
        var outbox = new ClientErrorOutbox(File1);
        outbox.Add(Error());
        await ClientErrorSender.FlushAsync(new HttpClient(StubHttpMessageHandler.FromResponse(status)) { BaseAddress = new Uri("https://x.example") }, outbox, "x1");
        Assert.Equal(left, outbox.Pending().Count);
    }

    [Fact]
    public async Task Flush_without_connection_keeps_errors_and_does_not_throw()
    {
        var outbox = new ClientErrorOutbox(File1);
        outbox.Add(Error());
        var handler = new StubHttpMessageHandler(_ => throw new HttpRequestException("offline"));
        var sent = await ClientErrorSender.FlushAsync(new HttpClient(handler) { BaseAddress = new Uri("https://x.example") }, outbox, "x1");
        Assert.Equal(0, sent);
        Assert.Single(outbox.Pending());
    }

    [Fact]
    public async Task Failed_lookup_and_login_carry_the_failure()
    {
        var handler = new StubHttpMessageHandler(_ => throw new HttpRequestException("No such host is known"));
        var lookup = await CompanyDirectory.ResolveAsync(new HttpClient(handler), "https://lifedrops.example.com", "lifedrops", false);
        Assert.Equal(CompanyLookupStatus.Unavailable, lookup.Status);
        Assert.Equal("dns", lookup.Failure!.Kind);
    }
}
