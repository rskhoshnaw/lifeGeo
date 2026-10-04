using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using LifeDrop.Mobile.Auth;
using LifeDrop.Mobile.Auth.Models;

namespace LifeDrop.Mobile.Visits;

// One visit of the daily plan as returned by GET /visits.
public sealed record PlannedVisit(
    Guid Id,
    Guid CustomerId,
    string? CustomerName,
    string? CustomerCode,
    string Status,
    DateOnly ScheduleDate,
    string VisitType,
    string Priority,
    TimeOnly? PlannedTime,
    bool IsUnplanned,
    string? ExceptionReason);

public enum PlanLoadStatus { Success, SessionExpired, NetworkError, ServerError }

public sealed record PlanLoadResult(PlanLoadStatus Status, IReadOnlyList<PlannedVisit> Visits)
{
    public static PlanLoadResult Ok(IReadOnlyList<PlannedVisit> visits) => new(PlanLoadStatus.Success, visits);
    public static PlanLoadResult Failed(PlanLoadStatus status) => new(status, Array.Empty<PlannedVisit>());
}

public enum VisitActionResult { Success, InvalidState, Conflict, Forbidden, NotFound, SessionExpired, NetworkError, ServerError }

public interface IVisitsApiClient
{
    // The caller's visits for one day; without a date the server uses today in Asia/Baghdad.
    Task<PlanLoadResult> GetDailyPlanAsync(DateOnly? scheduleDate = null, CancellationToken cancellationToken = default);

    // POST /visits/{id}/en-route and /arrive (plan step 1.11). Only the assignee; the server checks the transition.
    Task<VisitActionResult> MarkEnRouteAsync(Guid visitId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
    Task<VisitActionResult> MarkArrivedAsync(Guid visitId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}

public sealed class VisitsApiClient : IVisitsApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _httpClient;
    private readonly IAuthSession _session;

    public VisitsApiClient(HttpClient httpClient, IAuthSession session)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _session = session ?? throw new ArgumentNullException(nameof(session));
    }

    public async Task<PlanLoadResult> GetDailyPlanAsync(DateOnly? scheduleDate = null, CancellationToken cancellationToken = default)
    {
        var token = await _session.GetAccessTokenAsync(cancellationToken);
        if (!token.IsSuccess || string.IsNullOrEmpty(token.Data))
            return PlanLoadResult.Failed(token.Status == AuthStatus.NetworkOrServerError ? PlanLoadStatus.NetworkError : PlanLoadStatus.SessionExpired);

        var path = scheduleDate is { } date ? $"/visits?scheduleDate={date:yyyy-MM-dd}" : "/visits";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Data);
            using var response = await _httpClient.SendAsync(request, cancellationToken);

            if (response.StatusCode == HttpStatusCode.Unauthorized) return PlanLoadResult.Failed(PlanLoadStatus.SessionExpired);
            if (!response.IsSuccessStatusCode) return PlanLoadResult.Failed(PlanLoadStatus.ServerError);

            List<PlannedVisit>? visits;
            try
            {
                visits = await response.Content.ReadFromJsonAsync<List<PlannedVisit>>(JsonOptions, cancellationToken);
            }
            catch (JsonException)
            {
                return PlanLoadResult.Failed(PlanLoadStatus.ServerError);
            }
            catch (NotSupportedException)
            {
                return PlanLoadResult.Failed(PlanLoadStatus.ServerError); // not JSON
            }

            if (visits is null || visits.Any(v => v is null || v.Id == Guid.Empty || string.IsNullOrWhiteSpace(v.Status)))
                return PlanLoadResult.Failed(PlanLoadStatus.ServerError);
            return PlanLoadResult.Ok(visits);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return PlanLoadResult.Failed(PlanLoadStatus.NetworkError); // HttpClient timeout
        }
        catch (HttpRequestException)
        {
            return PlanLoadResult.Failed(PlanLoadStatus.NetworkError);
        }
    }

    public Task<VisitActionResult> MarkEnRouteAsync(Guid visitId, CancellationToken cancellationToken = default) =>
        PostActionAsync($"/visits/{visitId}/en-route", cancellationToken);

    public Task<VisitActionResult> MarkArrivedAsync(Guid visitId, CancellationToken cancellationToken = default) =>
        PostActionAsync($"/visits/{visitId}/arrive", cancellationToken);

    private async Task<VisitActionResult> PostActionAsync(string path, CancellationToken cancellationToken)
    {
        var token = await _session.GetAccessTokenAsync(cancellationToken);
        if (!token.IsSuccess || string.IsNullOrEmpty(token.Data))
            return token.Status == AuthStatus.NetworkOrServerError ? VisitActionResult.NetworkError : VisitActionResult.SessionExpired;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Data);
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            return response.StatusCode switch
            {
                HttpStatusCode.NoContent or HttpStatusCode.OK => VisitActionResult.Success,
                HttpStatusCode.BadRequest => await ReadCodeAsync(response, cancellationToken) == "INVALID_STATE"
                    ? VisitActionResult.InvalidState : VisitActionResult.ServerError,
                HttpStatusCode.Conflict => VisitActionResult.Conflict,
                HttpStatusCode.Unauthorized => VisitActionResult.SessionExpired,
                HttpStatusCode.Forbidden => VisitActionResult.Forbidden,
                HttpStatusCode.NotFound => VisitActionResult.NotFound,
                _ => VisitActionResult.ServerError
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return VisitActionResult.NetworkError;
        }
        catch (HttpRequestException)
        {
            return VisitActionResult.NetworkError;
        }
    }

    // The stable `code` of a ProblemDetails body, or null.
    private static async Task<string?> ReadCodeAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String
                ? code.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
