using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using LifeDrop.Mobile.Auth;
using LifeDrop.Mobile.Auth.Models;

namespace LifeDrop.Mobile.Visits;

// One HTTP call with the bearer token and the answer mapped to VisitActionResult from the status and the problem code (orders and warehouse work).
internal sealed class OrdersHttp(HttpClient httpClient, IAuthSession session)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<(VisitActionResult Result, T? Data)> CallAsync<T>(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        var token = await session.GetAccessTokenAsync(cancellationToken);
        if (!token.IsSuccess || string.IsNullOrEmpty(token.Data))
            return (token.Status == AuthStatus.NetworkOrServerError ? VisitActionResult.NetworkError : VisitActionResult.SessionExpired, default);

        try
        {
            using var request = new HttpRequestMessage(method, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Data);
            if (body is not null) request.Content = JsonContent.Create(body);
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

            var code = await ReadCodeAsync(response, cancellationToken);
            return (response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => VisitActionResult.SessionExpired,
                HttpStatusCode.Forbidden => VisitActionResult.Forbidden,
                HttpStatusCode.NotFound => VisitActionResult.NotFound,
                HttpStatusCode.BadRequest => code switch { "CURRENCY_MISMATCH" => VisitActionResult.CurrencyMismatch, "WRONG_PASSWORD" => VisitActionResult.WrongPassword, _ => VisitActionResult.InvalidData },
                HttpStatusCode.UnprocessableEntity => code == "GPS_MOCKED" ? VisitActionResult.GpsMocked : VisitActionResult.ServerError,
                HttpStatusCode.Conflict => code switch { "CUSTOMER_NOT_APPROVED" => VisitActionResult.CustomerNotApproved, "OUT_OF_STOCK" => VisitActionResult.OutOfStock, _ => VisitActionResult.Conflict },
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

    private static async Task<string?> ReadCodeAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            return document.RootElement.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String ? code.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
