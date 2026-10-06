using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LifeDrop.Application.Push;

namespace LifeDrop.Infrastructure.Push;

// Firebase Cloud Messaging (HTTP v1, free). Authenticates with a service-account key (OAuth2 JWT bearer) and sends a data-only
// message; the app writes the localized text. Without a key it is "not configured" and nothing is consumed (docs/PUSH.md).
public sealed class FcmPushSender : IPushSender
{
    private const string Scope = "https://www.googleapis.com/auth/firebase.messaging";
    private readonly HttpClient http;
    private readonly string? projectId;
    private readonly string? clientEmail;
    private readonly string tokenUri;
    private readonly RSA? key;
    private readonly SemaphoreSlim gate = new(1, 1);
    private string? accessToken;
    private DateTimeOffset accessTokenExpires;

    public FcmPushSender(HttpClient http, string? serviceAccountJson)
    {
        this.http = http;
        tokenUri = "https://oauth2.googleapis.com/token";
        if (string.IsNullOrWhiteSpace(serviceAccountJson)) return;
        try
        {
            using var document = JsonDocument.Parse(serviceAccountJson);
            var root = document.RootElement;
            projectId = root.GetProperty("project_id").GetString();
            clientEmail = root.GetProperty("client_email").GetString();
            if (root.TryGetProperty("token_uri", out var uri) && uri.GetString() is { Length: > 0 } custom) tokenUri = custom;
            key = RSA.Create();
            key.ImportFromPem(root.GetProperty("private_key").GetString());
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or CryptographicException or ArgumentException)
        {
            key?.Dispose();
            key = null;
            projectId = null;
        }
    }

    public bool IsConfigured => key is not null && !string.IsNullOrEmpty(projectId) && !string.IsNullOrEmpty(clientEmail);

    public async Task<PushOutcome> SendAsync(string token, PushMessage message, CancellationToken cancellationToken)
    {
        if (!IsConfigured) return PushOutcome.Failed;
        try
        {
            var bearer = await GetAccessTokenAsync(cancellationToken);
            if (bearer is null) return PushOutcome.Failed;
            using var request = new HttpRequestMessage(HttpMethod.Post, $"https://fcm.googleapis.com/v1/projects/{projectId}/messages:send");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            request.Content = new StringContent(BuildBody(token, message), Encoding.UTF8, "application/json");
            using var response = await http.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode) return PushOutcome.Sent;
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            return IsDeadToken(response.StatusCode, body) ? PushOutcome.InvalidToken : PushOutcome.Failed;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return PushOutcome.Failed;
        }
    }

    // Data values must be strings in FCM.
    public static string BuildBody(string token, PushMessage message) => JsonSerializer.Serialize(new
    {
        message = new
        {
            token,
            data = new Dictionary<string, string> { ["kind"] = message.Kind, ["entityId"] = message.EntityId, ["notificationId"] = message.NotificationId.ToString() },
            android = new { priority = "HIGH" }
        }
    });

    // 404 UNREGISTERED and 400 INVALID_ARGUMENT mean the token is dead; anything else (quota, 5xx) is temporary.
    public static bool IsDeadToken(HttpStatusCode status, string body) =>
        (status == HttpStatusCode.NotFound || status == HttpStatusCode.BadRequest) &&
        (body.Contains("UNREGISTERED", StringComparison.Ordinal) || body.Contains("INVALID_ARGUMENT", StringComparison.Ordinal));

    private async Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        if (accessToken is not null && accessTokenExpires > DateTimeOffset.UtcNow.AddMinutes(1)) return accessToken;
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (accessToken is not null && accessTokenExpires > DateTimeOffset.UtcNow.AddMinutes(1)) return accessToken;
            var now = DateTimeOffset.UtcNow;
            var assertion = BuildAssertion(now);
            using var response = await http.PostAsync(tokenUri, new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer",
                ["assertion"] = assertion
            }), cancellationToken);
            if (!response.IsSuccessStatusCode) return null;
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            accessToken = document.RootElement.GetProperty("access_token").GetString();
            accessTokenExpires = now.AddSeconds(document.RootElement.TryGetProperty("expires_in", out var seconds) ? seconds.GetInt32() : 3000);
            return accessToken;
        }
        finally { gate.Release(); }
    }

    private string BuildAssertion(DateTimeOffset now)
    {
        var header = Base64Url(Encoding.UTF8.GetBytes("""{"alg":"RS256","typ":"JWT"}"""));
        var claims = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new
        {
            iss = clientEmail, scope = Scope, aud = tokenUri, iat = now.ToUnixTimeSeconds(), exp = now.AddMinutes(55).ToUnixTimeSeconds()
        }));
        var signature = key!.SignData(Encoding.ASCII.GetBytes($"{header}.{claims}"), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return $"{header}.{claims}.{Base64Url(signature)}";
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
