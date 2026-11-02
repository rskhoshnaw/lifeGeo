using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;

namespace LifeDrop.Mobile.Auth;

// Connection errors of the app (plan step 5.10). A DNS or connection failure never reaches the server, so the app writes it to a small file on the phone and sends it when a later request works.
// What is kept: when, at which step, the kind of error, the exception type, a short message, and what was typed (company code, e-mail). A password or token is never part of it.

/// <summary>Kind: dns | timeout | tls | http | other. Type: exception class name. Message: short technical text (cut to 300 characters; the server removes URLs and token-like strings too).</summary>
public sealed record NetworkFailure(string Kind, string Type, string Message)
{
    private const int MaxMessage = 300;

    public static NetworkFailure From(Exception exception)
    {
        var all = Chain(exception).ToList();
        var kind = "other";
        foreach (var e in all)
        {
            kind = KindOf(e);
            if (kind != "other") break;
        }
        var message = string.Join(" <- ", all.Select(e => e.GetType().Name + ": " + e.Message));
        return new NetworkFailure(kind, exception.GetType().Name, message.Length <= MaxMessage ? message : message[..MaxMessage]);
    }

    public static NetworkFailure Timeout(Exception? exception = null) => new("timeout", exception?.GetType().Name ?? "Timeout", "The request took too long.");

    public static NetworkFailure Http(int statusCode) => new("http", "HttpStatus", "HTTP " + statusCode);

    private static IEnumerable<Exception> Chain(Exception exception)
    {
        for (var e = exception; e is not null; e = e.InnerException) yield return e;
    }

    private static string KindOf(Exception e)
    {
        if (e is HttpRequestException { HttpRequestError: HttpRequestError.NameResolutionError }) return "dns";
        if (e is SocketException { SocketErrorCode: SocketError.HostNotFound or SocketError.TryAgain or SocketError.NoData }) return "dns";
        if (e is HttpRequestException { HttpRequestError: HttpRequestError.SecureConnectionError } or System.Security.Authentication.AuthenticationException) return "tls";
        if (e is TimeoutException or TaskCanceledException or OperationCanceledException) return "timeout";
        // Android and iOS word the same failures differently.
        var text = e.Message;
        if (text.Contains("No such host", StringComparison.OrdinalIgnoreCase) || text.Contains("Name or service not known", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("nodename nor servname", StringComparison.OrdinalIgnoreCase) || text.Contains("Unable to resolve host", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("hostname could not be found", StringComparison.OrdinalIgnoreCase) || text.Contains("-1003", StringComparison.Ordinal)) return "dns";
        if (text.Contains("SSL", StringComparison.OrdinalIgnoreCase) || text.Contains("certificate", StringComparison.OrdinalIgnoreCase) || text.Contains("TLS", StringComparison.Ordinal)) return "tls";
        return "other";
    }
}

/// <summary>One error waiting to be sent. There is deliberately no password or token field.</summary>
public sealed record PendingClientError(DateTimeOffset OccurredAt, string Stage, string ErrorKind, string ErrorType, string Message,
    string? EnteredCompanyCode, string? EnteredEmail, string? DeviceId, string? AppVersion, string? Platform);

/// <summary>The errors waiting on the phone, in one JSON file. At most <c>capacity</c> are kept (the oldest go first); a damaged file is treated as empty.</summary>
public sealed class ClientErrorOutbox(string filePath, int capacity = 50)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly object _gate = new();

    public IReadOnlyList<PendingClientError> Pending() { lock (_gate) return Load(); }

    public void Add(PendingClientError error)
    {
        lock (_gate)
        {
            var list = Load();
            list.Add(error);
            if (list.Count > capacity) list.RemoveRange(0, list.Count - capacity);
            Save(list);
        }
    }

    /// <summary>Removes the first <paramref name="count"/> errors (the ones that were sent); errors added meanwhile stay.</summary>
    public void RemoveFirst(int count)
    {
        lock (_gate)
        {
            var list = Load();
            list.RemoveRange(0, Math.Min(count, list.Count));
            Save(list);
        }
    }

    private List<PendingClientError> Load()
    {
        try
        {
            return File.Exists(filePath) ? JsonSerializer.Deserialize<List<PendingClientError>>(File.ReadAllText(filePath), Json) ?? [] : [];
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private void Save(List<PendingClientError> list)
    {
        try
        {
            if (list.Count == 0) { if (File.Exists(filePath)) File.Delete(filePath); return; }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(filePath))!);
            var temporary = filePath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(list, Json));
            File.Move(temporary, filePath, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Reporting must never break the app.
        }
    }
}

public static class ClientErrorSender
{
    public const int BatchSize = 20;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Sends the waiting errors to the company server (<paramref name="http"/> has its address). Returns how many left the outbox. 200 = stored; 400/404 = the server will never take them (dropped);
    /// anything else (no connection, 429, 5xx) keeps them for the next time.</summary>
    public static async Task<int> FlushAsync(HttpClient http, ClientErrorOutbox outbox, string companyCode, CancellationToken cancellationToken = default)
    {
        var sent = 0;
        try
        {
            while (true)
            {
                var batch = outbox.Pending().Take(BatchSize).ToList();
                if (batch.Count == 0) return sent;
                using var response = await http.PostAsJsonAsync("/client-errors", new { companyCode, items = batch }, Json, cancellationToken);
                if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.BadRequest or HttpStatusCode.NotFound)) return sent;
                outbox.RemoveFirst(batch.Count);
                sent += batch.Count;
            }
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or JsonException)
        {
            return sent;
        }
    }
}
