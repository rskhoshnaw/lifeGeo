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
    string? ExceptionReason,
    // Flexible planning (plan step 1.23): manual position, times moved to another day, and whether the visit's
    // day/time fits the customer's accepted hours ("none", "within" or "outside").
    int? SortOrder = null,
    int PostponeCount = 0,
    string? PostponeReason = null,
    string WindowStatus = "none",
    // The priority a manager gave the customer (plan step 1.26): high | normal | low.
    string CustomerPriority = "normal");

public enum PlanLoadStatus { Success, SessionExpired, NetworkError, ServerError }

public sealed record PlanLoadResult(PlanLoadStatus Status, IReadOnlyList<PlannedVisit> Visits)
{
    public static PlanLoadResult Ok(IReadOnlyList<PlannedVisit> visits) => new(PlanLoadStatus.Success, visits);
    public static PlanLoadResult Failed(PlanLoadStatus status) => new(status, Array.Empty<PlannedVisit>());
}

public enum VisitActionResult
{
    Success, InvalidState, Conflict, Forbidden, NotFound, SessionExpired, NetworkError, ServerError,
    // Check-in / check-out and report (plan step 1.12), from the server's ProblemDetails code.
    InvalidData, DeviceRequired, CustomerLocationMissing, ReportRequired,
    OutsideGeofence, GpsLowAccuracy, GpsStale, GpsClockSkew, GpsMocked, OfflineExpired,
    // Orders (plan step 2.2): "order placed" needs a real order; all lines of an order share one currency; the customer is not approved yet.
    OrderRequired, CurrencyMismatch, CustomerNotApproved,
    // Plan step 4.41: a product of the order is out of stock (or short of what was asked).
    OutOfStock
}

// A position from the device, sent as captured (plan step 1.12; the server judges it, PRD §6).
public sealed record DeviceFix(double Latitude, double Longitude, double AccuracyMeters, DateTimeOffset CapturedAt, bool IsMocked);

// Report outcome contract version 1: products_presented | order_intent | follow_up_needed | no_interest | other.
public sealed record VisitReportDraft(string Outcome, string Note);

public enum ReportLoadStatus { Found, None, SessionExpired, NetworkError, ServerError }

public interface IVisitsApiClient
{
    // The caller's visits for one day; without a date the server uses today in Asia/Baghdad.
    Task<PlanLoadResult> GetDailyPlanAsync(DateOnly? scheduleDate = null, CancellationToken cancellationToken = default);

    // POST /visits/{id}/en-route and /arrive (plan step 1.11). Only the assignee; the server checks the transition.
    Task<VisitActionResult> MarkEnRouteAsync(Guid visitId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
    Task<VisitActionResult> MarkArrivedAsync(Guid visitId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    // POST /visits/{id}/start and /complete with the fix and an Idempotency-Key (plan step 1.12). Retrying with the
    // same key after a lost reply returns Success without a second record.
    Task<VisitActionResult> CheckInAsync(Guid visitId, DeviceFix fix, string idempotencyKey, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
    Task<VisitActionResult> CheckOutAsync(Guid visitId, DeviceFix fix, string idempotencyKey, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    // Offline check-in / check-out sent later (decision D5): the server may accept it and ask a manager to review it.
    Task<(VisitActionResult Result, bool ReviewRequired)> SendOfflineCheckpointAsync(Guid visitId, bool checkOut, DeviceFix fix,
        long? elapsedSinceCaptureMs, string idempotencyKey, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    // PUT /visit-plan/order (the whole day in the wanted order) and POST /visits/{id}/postpone (another day, optional
    // reason) - plan step 1.23. Online only; the server checks that the visits are the caller's.
    Task<VisitActionResult> ReorderPlanAsync(DateOnly scheduleDate, IReadOnlyList<Guid> visitIds, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
    Task<VisitActionResult> PostponeAsync(Guid visitId, DateOnly newDate, string? reason, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    // PUT / GET /visits/{id}/report.
    Task<VisitActionResult> SaveReportAsync(Guid visitId, VisitReportDraft report, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
    Task<(ReportLoadStatus Status, VisitReportDraft? Report)> GetReportAsync(Guid visitId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}

public sealed class VisitsApiClient : IVisitsApiClient, ISelfPlanApiClient
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
        SendActionAsync(HttpMethod.Post, $"/visits/{visitId}/en-route", null, null, cancellationToken);

    public Task<VisitActionResult> MarkArrivedAsync(Guid visitId, CancellationToken cancellationToken = default) =>
        SendActionAsync(HttpMethod.Post, $"/visits/{visitId}/arrive", null, null, cancellationToken);

    public Task<VisitActionResult> ReorderPlanAsync(DateOnly scheduleDate, IReadOnlyList<Guid> visitIds, CancellationToken cancellationToken = default) =>
        SendActionAsync(HttpMethod.Put, "/visit-plan/order", new { scheduleDate = scheduleDate.ToString("yyyy-MM-dd"), visitIds }, null, cancellationToken);

    public async Task<(VisitActionResult Result, IReadOnlyList<SelfPlanCandidate> Candidates)> SearchAsync(SelfPlanQuery query, CancellationToken cancellationToken = default)
    {
        SelfPlanPreviewDto? dto = null;
        var result = await SendActionAsync(HttpMethod.Post, "/visit-plan/preview", SelfPlanBody(query, null), null, cancellationToken, async response =>
        {
            try { dto = await response.Content.ReadFromJsonAsync<SelfPlanPreviewDto>(JsonOptions, cancellationToken); }
            catch (Exception e) when (e is JsonException or NotSupportedException) { }
        });
        if (result != VisitActionResult.Success) return (result, Array.Empty<SelfPlanCandidate>());
        if (dto?.Candidates is null || dto.Candidates.Any(c => c is null || c.CustomerId == Guid.Empty || string.IsNullOrWhiteSpace(c.Code)))
            return (VisitActionResult.ServerError, Array.Empty<SelfPlanCandidate>());
        // The user knows the customer: "closed that weekday" only warns; a customer already over a limit does not exist here (no count is sent).
        return (result, dto.Candidates.Select(c => new SelfPlanCandidate(c.CustomerId, c.Code!, c.Name ?? "", c.Place, c.LastVisitDate, c.WindowStatus ?? "none", c.CustomerPriority ?? "normal")).ToList());
    }

    public async Task<(VisitActionResult Result, int Created)> AddAsync(SelfPlanQuery query, IReadOnlyList<Guid> customerIds, CancellationToken cancellationToken = default)
    {
        SelfPlanGeneratedDto? dto = null;
        var result = await SendActionAsync(HttpMethod.Post, "/visit-plan/generate", SelfPlanBody(query, customerIds), null, cancellationToken, async response =>
        {
            try { dto = await response.Content.ReadFromJsonAsync<SelfPlanGeneratedDto>(JsonOptions, cancellationToken); }
            catch (Exception e) when (e is JsonException or NotSupportedException) { }
        });
        if (result != VisitActionResult.Success) return (result, 0);
        return dto is null ? (VisitActionResult.ServerError, 0) : (result, dto.Created);
    }

    // No assignee: the server plans for the caller. Hours are not applied; the order is the longest time since the last visit.
    private static object SelfPlanBody(SelfPlanQuery query, IReadOnlyList<Guid>? customerIds) => new
    {
        scheduleDate = query.Day.ToString("yyyy-MM-dd"),
        placeId = query.PlaceId,
        query = query.Text,
        area = query.Area?.Select(p => new { latitude = p.Latitude, longitude = p.Longitude }).ToList(),
        order = "least_recent",
        respectWindows = false,
        customerIds
    };

    private sealed record SelfPlanCandidateDto(Guid CustomerId, string? Code, string? Name, string? Place, DateOnly? LastVisitDate, string? WindowStatus, string? CustomerPriority);
    private sealed record SelfPlanPreviewDto(List<SelfPlanCandidateDto>? Candidates);
    private sealed record SelfPlanGeneratedDto(int Created);

    public Task<VisitActionResult> PostponeAsync(Guid visitId, DateOnly newDate, string? reason, CancellationToken cancellationToken = default) =>
        SendActionAsync(HttpMethod.Post, $"/visits/{visitId}/postpone", new { scheduleDate = newDate.ToString("yyyy-MM-dd"), reason }, null, cancellationToken);

    public Task<VisitActionResult> CheckInAsync(Guid visitId, DeviceFix fix, string idempotencyKey, CancellationToken cancellationToken = default) =>
        SendActionAsync(HttpMethod.Post, $"/visits/{visitId}/start", FixBody(fix), idempotencyKey, cancellationToken);

    public Task<VisitActionResult> CheckOutAsync(Guid visitId, DeviceFix fix, string idempotencyKey, CancellationToken cancellationToken = default) =>
        SendActionAsync(HttpMethod.Post, $"/visits/{visitId}/complete", FixBody(fix), idempotencyKey, cancellationToken);

    public async Task<(VisitActionResult Result, bool ReviewRequired)> SendOfflineCheckpointAsync(Guid visitId, bool checkOut, DeviceFix fix,
        long? elapsedSinceCaptureMs, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        var body = new
        {
            latitude = fix.Latitude,
            longitude = fix.Longitude,
            accuracyMeters = fix.AccuracyMeters,
            capturedAt = fix.CapturedAt,
            isMocked = fix.IsMocked,
            isOffline = true,
            elapsedSinceCaptureMs
        };
        bool reviewRequired = false;
        var result = await SendActionAsync(HttpMethod.Post, $"/visits/{visitId}/{(checkOut ? "complete" : "start")}", body, idempotencyKey,
            cancellationToken, async response =>
            {
                // 200 carries {reviewRequired, reviewReasons}; 204 is a replay of an already accepted attempt.
                if (response.StatusCode != HttpStatusCode.OK) return;
                try
                {
                    using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
                    reviewRequired = document.RootElement.TryGetProperty("reviewRequired", out var flag) && flag.ValueKind == JsonValueKind.True;
                }
                catch (JsonException)
                {
                }
            });
        return (result, reviewRequired);
    }

    public Task<VisitActionResult> SaveReportAsync(Guid visitId, VisitReportDraft report, CancellationToken cancellationToken = default) =>
        SendActionAsync(HttpMethod.Put, $"/visits/{visitId}/report",
            new { outcome = report.Outcome, note = report.Note, contractVersion = 1 }, null, cancellationToken);

    public async Task<(ReportLoadStatus Status, VisitReportDraft? Report)> GetReportAsync(Guid visitId, CancellationToken cancellationToken = default)
    {
        var token = await _session.GetAccessTokenAsync(cancellationToken);
        if (!token.IsSuccess || string.IsNullOrEmpty(token.Data))
            return (token.Status == AuthStatus.NetworkOrServerError ? ReportLoadStatus.NetworkError : ReportLoadStatus.SessionExpired, null);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"/visits/{visitId}/report");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Data);
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound) return (ReportLoadStatus.None, null);
            if (response.StatusCode == HttpStatusCode.Unauthorized) return (ReportLoadStatus.SessionExpired, null);
            if (!response.IsSuccessStatusCode) return (ReportLoadStatus.ServerError, null);
            try
            {
                var report = await response.Content.ReadFromJsonAsync<VisitReportDraft>(JsonOptions, cancellationToken);
                return report is null || string.IsNullOrEmpty(report.Outcome) || report.Note is null
                    ? (ReportLoadStatus.ServerError, null) : (ReportLoadStatus.Found, report);
            }
            catch (Exception e) when (e is JsonException or NotSupportedException)
            {
                return (ReportLoadStatus.ServerError, null);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e) when (e is OperationCanceledException or HttpRequestException)
        {
            return (ReportLoadStatus.NetworkError, null);
        }
    }

    private static object FixBody(DeviceFix fix) => new
    {
        latitude = fix.Latitude,
        longitude = fix.Longitude,
        accuracyMeters = fix.AccuracyMeters,
        capturedAt = fix.CapturedAt,
        isMocked = fix.IsMocked
    };

    private async Task<VisitActionResult> SendActionAsync(HttpMethod method, string path, object? body, string? idempotencyKey,
        CancellationToken cancellationToken, Func<HttpResponseMessage, Task>? onSuccess = null)
    {
        var token = await _session.GetAccessTokenAsync(cancellationToken);
        if (!token.IsSuccess || string.IsNullOrEmpty(token.Data))
            return token.Status == AuthStatus.NetworkOrServerError ? VisitActionResult.NetworkError : VisitActionResult.SessionExpired;

        try
        {
            using var request = new HttpRequestMessage(method, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Data);
            if (idempotencyKey is not null) request.Headers.Add("Idempotency-Key", idempotencyKey);
            if (body is not null) request.Content = JsonContent.Create(body);
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                if (onSuccess is not null) await onSuccess(response);
                return VisitActionResult.Success;
            }

            var code = await ReadCodeAsync(response, cancellationToken);
            return response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => VisitActionResult.SessionExpired,
                HttpStatusCode.NotFound => VisitActionResult.NotFound,
                HttpStatusCode.BadRequest => code switch
                {
                    "INVALID_STATE" => VisitActionResult.InvalidState,
                    "INVALID_DATA" => VisitActionResult.InvalidData,
                    _ => VisitActionResult.ServerError
                },
                HttpStatusCode.Forbidden => code == "DEVICE_REQUIRED" ? VisitActionResult.DeviceRequired : VisitActionResult.Forbidden,
                HttpStatusCode.Conflict => code switch
                {
                    "CUSTOMER_LOCATION_MISSING" => VisitActionResult.CustomerLocationMissing,
                    "REPORT_REQUIRED" => VisitActionResult.ReportRequired,
                    "ORDER_REQUIRED" => VisitActionResult.OrderRequired,
                    _ => VisitActionResult.Conflict
                },
                HttpStatusCode.UnprocessableEntity => code switch
                {
                    "OUTSIDE_GEOFENCE" => VisitActionResult.OutsideGeofence,
                    "GPS_LOW_ACCURACY" => VisitActionResult.GpsLowAccuracy,
                    "GPS_STALE" => VisitActionResult.GpsStale,
                    "GPS_CLOCK_SKEW" => VisitActionResult.GpsClockSkew,
                    "GPS_MOCKED" => VisitActionResult.GpsMocked,
                    "OFFLINE_EXPIRED" => VisitActionResult.OfflineExpired,
                    _ => VisitActionResult.ServerError
                },
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
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(text)) return null;
            using var document = JsonDocument.Parse(text);
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
