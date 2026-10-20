using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using LifeDrop.Mobile.Auth;
using LifeDrop.Mobile.Auth.Models;

namespace LifeDrop.Mobile.Visits;

// Where the company's messages also arrive (plan step 3.4 and 4.22). Enabled: the company turned Telegram on; Linked: this user's private chat is linked.
public sealed record ChannelStatusInfo(bool Enabled, string? BotUsername, bool TelegramLinked);

// A one-time code (15 minutes) for the company bot. The user opens Link in Telegram and presses Start; only then does the server link that private chat.
public sealed record TelegramLinkInfo(string Code, string? BotUsername, DateTimeOffset ExpiresAt)
{
    public string? Link => string.IsNullOrWhiteSpace(BotUsername) ? null : $"https://t.me/{BotUsername}?start={Uri.EscapeDataString(Code)}";
}

public interface IChannelsApiClient
{
    Task<(VisitActionResult Result, ChannelStatusInfo? Status)> GetStatusAsync(CancellationToken cancellationToken = default);
    Task<(VisitActionResult Result, TelegramLinkInfo? Link)> StartTelegramLinkAsync(CancellationToken cancellationToken = default);
    Task<VisitActionResult> UnlinkTelegramAsync(CancellationToken cancellationToken = default);
}

// Online only. 503 from the server means Telegram is not turned on for the company; it is shown as ServerError and the page uses the status call first.
public sealed class ChannelsApiClient(HttpClient httpClient, IAuthSession session) : IChannelsApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<(VisitActionResult Result, ChannelStatusInfo? Status)> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var (result, dto) = await CallAsync<StatusDto>(HttpMethod.Get, "/channels", cancellationToken);
        return result != VisitActionResult.Success ? (result, null) : dto is null ? (VisitActionResult.ServerError, null) : (result, new ChannelStatusInfo(dto.Enabled, dto.BotUsername, dto.TelegramLinked));
    }

    public async Task<(VisitActionResult Result, TelegramLinkInfo? Link)> StartTelegramLinkAsync(CancellationToken cancellationToken = default)
    {
        var (result, dto) = await CallAsync<LinkDto>(HttpMethod.Post, "/channels/telegram/link", cancellationToken);
        if (result != VisitActionResult.Success) return (result, null);
        return dto is null || string.IsNullOrWhiteSpace(dto.Code) ? (VisitActionResult.ServerError, null) : (result, new TelegramLinkInfo(dto.Code, dto.BotUsername, dto.ExpiresAt));
    }

    public async Task<VisitActionResult> UnlinkTelegramAsync(CancellationToken cancellationToken = default) =>
        (await CallAsync<object>(HttpMethod.Post, "/channels/telegram/unlink", cancellationToken)).Result;

    private async Task<(VisitActionResult Result, T? Data)> CallAsync<T>(HttpMethod method, string path, CancellationToken cancellationToken)
    {
        var token = await session.GetAccessTokenAsync(cancellationToken);
        if (!token.IsSuccess || string.IsNullOrEmpty(token.Data))
            return (token.Status == AuthStatus.NetworkOrServerError ? VisitActionResult.NetworkError : VisitActionResult.SessionExpired, default);

        try
        {
            using var request = new HttpRequestMessage(method, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Data);
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                if (typeof(T) == typeof(object) || response.StatusCode == HttpStatusCode.NoContent) return (VisitActionResult.Success, default);
                try
                {
                    return (VisitActionResult.Success, await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken));
                }
                catch (Exception e) when (e is JsonException or NotSupportedException)
                {
                    return (VisitActionResult.ServerError, default);
                }
            }

            return (response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => VisitActionResult.SessionExpired,
                HttpStatusCode.Forbidden => VisitActionResult.Forbidden,
                HttpStatusCode.NotFound => VisitActionResult.NotFound,
                _ => VisitActionResult.ServerError
            }, default);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e) when (e is OperationCanceledException or HttpRequestException)
        {
            return (VisitActionResult.NetworkError, default);
        }
    }

    private sealed record StatusDto(bool Enabled, string? BotUsername, bool TelegramLinked);
    private sealed record LinkDto(string? Code, string? BotUsername, DateTimeOffset ExpiresAt);
}
