using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using LifeDrop.Mobile.Auth;
using LifeDrop.Mobile.Auth.Models;

namespace LifeDrop.Mobile.Tracking;

public sealed record ShiftInfo(Guid Id, Guid DeviceId, DateTimeOffset StartedAt, DateTimeOffset? EndedAt);

// One tracking point; ClientPointId is created once on the phone so a retried upload is not stored twice.
public sealed record TrackedPoint(Guid ClientPointId, double Latitude, double Longitude, double AccuracyMeters,
    DateTimeOffset CapturedAt, bool IsMocked);

public sealed record UploadResult(int Accepted, int Duplicates, int Rejected);

public enum ShiftCallStatus { Success, NotFound, DeviceRequired, DeviceMismatch, InvalidData, SessionExpired, NetworkError, ServerError }

public interface IShiftApiClient
{
    Task<(ShiftCallStatus Status, ShiftInfo? Shift)> StartShiftAsync(CancellationToken cancellationToken = default);
    Task<(ShiftCallStatus Status, ShiftInfo? Shift)> GetCurrentShiftAsync(CancellationToken cancellationToken = default);
    Task<ShiftCallStatus> EndShiftAsync(Guid shiftId, CancellationToken cancellationToken = default);
    Task<(ShiftCallStatus Status, UploadResult? Result)> UploadPointsAsync(Guid shiftId, IReadOnlyList<TrackedPoint> points,
        CancellationToken cancellationToken = default);
}

// Client for /shifts (plan step 1.13). Uses the device-bound access token of the session.
public sealed class ShiftApiClient : IShiftApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly HttpClient _httpClient;
    private readonly IAuthSession _session;

    public ShiftApiClient(HttpClient httpClient, IAuthSession session)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _session = session ?? throw new ArgumentNullException(nameof(session));
    }

    public async Task<(ShiftCallStatus Status, ShiftInfo? Shift)> StartShiftAsync(CancellationToken cancellationToken = default)
    {
        var (status, shift) = await SendAsync<ShiftInfo>(HttpMethod.Post, "/shifts", null, cancellationToken);
        return status == ShiftCallStatus.Success && (shift is null || shift.Id == Guid.Empty) ? (ShiftCallStatus.ServerError, null) : (status, shift);
    }

    public async Task<(ShiftCallStatus Status, ShiftInfo? Shift)> GetCurrentShiftAsync(CancellationToken cancellationToken = default)
    {
        var (status, shift) = await SendAsync<ShiftInfo>(HttpMethod.Get, "/shifts/current", null, cancellationToken);
        return status == ShiftCallStatus.Success && (shift is null || shift.Id == Guid.Empty) ? (ShiftCallStatus.ServerError, null) : (status, shift);
    }

    public async Task<ShiftCallStatus> EndShiftAsync(Guid shiftId, CancellationToken cancellationToken = default) =>
        (await SendAsync<object>(HttpMethod.Post, $"/shifts/{shiftId}/end", null, cancellationToken)).Status;

    public async Task<(ShiftCallStatus Status, UploadResult? Result)> UploadPointsAsync(Guid shiftId, IReadOnlyList<TrackedPoint> points,
        CancellationToken cancellationToken = default)
    {
        var body = new
        {
            points = points.Select(p => new
            {
                clientPointId = p.ClientPointId,
                latitude = p.Latitude,
                longitude = p.Longitude,
                accuracyMeters = p.AccuracyMeters,
                capturedAt = p.CapturedAt,
                isMocked = p.IsMocked
            })
        };
        var (status, result) = await SendAsync<UploadResult>(HttpMethod.Post, $"/shifts/{shiftId}/points", body, cancellationToken);
        return status == ShiftCallStatus.Success && result is null ? (ShiftCallStatus.ServerError, null) : (status, result);
    }

    private async Task<(ShiftCallStatus Status, T? Body)> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
        where T : class
    {
        var token = await _session.GetAccessTokenAsync(cancellationToken);
        if (!token.IsSuccess || string.IsNullOrEmpty(token.Data))
            return (token.Status == AuthStatus.NetworkOrServerError ? ShiftCallStatus.NetworkError : ShiftCallStatus.SessionExpired, null);
        try
        {
            using var request = new HttpRequestMessage(method, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Data);
            if (body is not null) request.Content = JsonContent.Create(body);
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                if (typeof(T) == typeof(object) || response.StatusCode == HttpStatusCode.NoContent) return (ShiftCallStatus.Success, null);
                try
                {
                    return (ShiftCallStatus.Success, await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken));
                }
                catch (Exception e) when (e is JsonException or NotSupportedException)
                {
                    return (ShiftCallStatus.ServerError, null);
                }
            }
            var code = await ReadCodeAsync(response, cancellationToken);
            return (response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => ShiftCallStatus.SessionExpired,
                HttpStatusCode.NotFound => ShiftCallStatus.NotFound,
                HttpStatusCode.BadRequest => ShiftCallStatus.InvalidData,
                HttpStatusCode.Forbidden => code == "DEVICE_MISMATCH" ? ShiftCallStatus.DeviceMismatch : ShiftCallStatus.DeviceRequired,
                _ => ShiftCallStatus.ServerError
            }, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e) when (e is OperationCanceledException or HttpRequestException)
        {
            return (ShiftCallStatus.NetworkError, null);
        }
    }

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
