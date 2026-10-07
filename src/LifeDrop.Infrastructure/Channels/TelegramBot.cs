using System.Net;
using System.Text;
using System.Text.Json;
using LifeDrop.Application.Channels;

namespace LifeDrop.Infrastructure.Channels;

// The Telegram Bot API (free, plan step 3.4). Off unless `enabled` and a token is given; the token is never written to a log or an error. A blocked bot or a
// chat that is gone is "invalid address" (the link is dropped); a timeout, a flood limit or a server error is a temporary failure.
public sealed class TelegramBot(HttpClient http, bool enabled, string? token, string? botUsername, string baseUrl = "https://api.telegram.org") : ITelegramBot
{
    public bool IsConfigured => enabled && !string.IsNullOrWhiteSpace(token);

    public string? BotUsername => string.IsNullOrWhiteSpace(botUsername) ? null : botUsername.Trim().TrimStart('@');

    public async Task<ChannelOutcome> SendAsync(string chatId, string text, CancellationToken cancellationToken)
    {
        if (!IsConfigured || string.IsNullOrWhiteSpace(chatId)) return ChannelOutcome.Failed;
        try
        {
            var body = JsonSerializer.Serialize(new { chat_id = chatId, text, disable_web_page_preview = true });
            using var request = new HttpRequestMessage(HttpMethod.Post, Url("sendMessage")) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            using var response = await http.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode) return ChannelOutcome.Sent;
            if (response.StatusCode == HttpStatusCode.Forbidden) return ChannelOutcome.InvalidAddress;   // bot blocked or removed from the chat
            if (response.StatusCode == HttpStatusCode.BadRequest)
            {
                var answer = await response.Content.ReadAsStringAsync(cancellationToken);
                return answer.Contains("chat not found", StringComparison.OrdinalIgnoreCase) ? ChannelOutcome.InvalidAddress : ChannelOutcome.Failed;
            }
            return ChannelOutcome.Failed;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return ChannelOutcome.Failed;
        }
    }

    public async Task<IReadOnlyList<TelegramUpdate>?> GetUpdatesAsync(long offset, CancellationToken cancellationToken)
    {
        if (!IsConfigured) return null;
        try
        {
            using var response = await http.GetAsync(Url("getUpdates") + "?timeout=0&allowed_updates=%5B%22message%22%5D&offset=" + offset.ToString(System.Globalization.CultureInfo.InvariantCulture), cancellationToken);
            if (!response.IsSuccessStatusCode) return null;
            return Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    // The updates of a getUpdates answer; anything that is not a text message with a chat is skipped, a broken answer is null.
    public static IReadOnlyList<TelegramUpdate>? Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True || !root.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Array) return null;
            var updates = new List<TelegramUpdate>();
            foreach (var item in result.EnumerateArray())
            {
                if (!item.TryGetProperty("update_id", out var id) || id.ValueKind != JsonValueKind.Number) continue;
                var updateId = id.GetInt64();
                if (!item.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object ||
                    !message.TryGetProperty("chat", out var chat) || chat.ValueKind != JsonValueKind.Object ||
                    !chat.TryGetProperty("id", out var chatId) || chatId.ValueKind != JsonValueKind.Number)
                {
                    updates.Add(new TelegramUpdate(updateId, "", false, null));   // still advances the offset
                    continue;
                }
                var isPrivate = chat.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == "private";
                var text = message.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
                updates.Add(new TelegramUpdate(updateId, chatId.GetInt64().ToString(System.Globalization.CultureInfo.InvariantCulture), isPrivate, text));
            }
            return updates;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private string Url(string method) => baseUrl.TrimEnd('/') + "/bot" + token + "/" + method;
}
