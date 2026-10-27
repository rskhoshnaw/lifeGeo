using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using LifeDrop.Application.Marketing;

namespace LifeDrop.Infrastructure.Marketing;

// The official WhatsApp Business Cloud API (docs/WHATSAPP-API.md): one template message per call. Off unless Enabled, the phone number id and the access token are all set. The token is read
// from the environment of the API (WhatsApp__AccessToken) and is never logged or stored.
public sealed class WhatsAppCloud(HttpClient http, bool enabled, string? phoneNumberId, string? accessToken, string? graphVersion = null) : IWhatsAppCloud
{
    private readonly string _version = string.IsNullOrWhiteSpace(graphVersion) ? "v21.0" : graphVersion.Trim();

    public bool IsConfigured => enabled && !string.IsNullOrWhiteSpace(phoneNumberId) && !string.IsNullOrWhiteSpace(accessToken);

    public async Task<WhatsAppSendResult> SendTemplateAsync(string toDigits, string template, string language, string bodyParameter, CancellationToken cancellationToken)
    {
        if (!IsConfigured) return new WhatsAppSendResult(WhatsAppSendOutcome.Failed, Error: "not_configured");
        var payload = JsonSerializer.Serialize(new
        {
            messaging_product = "whatsapp",
            to = toDigits,
            type = "template",
            template = new
            {
                name = template,
                language = new { code = language },
                components = new object[] { new { type = "body", parameters = new object[] { new { type = "text", text = bodyParameter } } } }
            }
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://graph.facebook.com/{_version}/{Uri.EscapeDataString(phoneNumberId!)}/messages")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        try
        {
            using var response = await http.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (response.IsSuccessStatusCode) return new WhatsAppSendResult(WhatsAppSendOutcome.Sent, ReadMessageId(body));
            var reason = $"{(int)response.StatusCode}: {ReadError(body)}";
            // Rate limits and server errors are temporary; anything else (bad number, template not approved, token wrong) will not change by waiting.
            var temporary = response.StatusCode is HttpStatusCode.TooManyRequests or >= HttpStatusCode.InternalServerError || (int)response.StatusCode == 408;
            return new WhatsAppSendResult(temporary ? WhatsAppSendOutcome.Retry : WhatsAppSendOutcome.Failed, Error: reason);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return new WhatsAppSendResult(WhatsAppSendOutcome.Retry, Error: "network");
        }
    }

    private static string? ReadMessageId(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("messages", out var messages) && messages.ValueKind == JsonValueKind.Array && messages.GetArrayLength() > 0 &&
                   messages[0].TryGetProperty("id", out var id) ? id.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // The message of Meta's error object, cut short; the token is never part of a response.
    private static string ReadError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var error))
            {
                var code = error.TryGetProperty("code", out var c) ? c.ToString() : "";
                var message = error.TryGetProperty("message", out var m) ? m.GetString() : null;
                return $"{code} {message}".Trim();
            }
        }
        catch (JsonException)
        {
        }
        return "error";
    }
}
