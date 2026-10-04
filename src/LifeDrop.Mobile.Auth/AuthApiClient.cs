using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using LifeDrop.Mobile.Auth.Models;

namespace LifeDrop.Mobile.Auth;

public sealed class AuthApiClient : IAuthApiClient
{
    private readonly HttpClient _httpClient;
    private const string GenericNetworkErrorMessage = "A network error occurred while communicating with the server.";
    private const string MalformedPayloadErrorMessage = "Malformed or incomplete response payload from server.";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public AuthApiClient(HttpClient httpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public AuthApiClient(HttpClient httpClient, Uri baseAddress)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _httpClient.BaseAddress = baseAddress ?? throw new ArgumentNullException(nameof(baseAddress));
    }

    public async Task<AuthResult<RegisteredDevice>> RegisterDeviceAsync(
        RegisterDeviceRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            using var response = await _httpClient.PostAsJsonAsync("/auth/devices/register", request, JsonOptions, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                RegisteredDevice? device = null;
                try
                {
                    device = await response.Content.ReadFromJsonAsync<RegisteredDevice>(JsonOptions, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw; // Let outer block handle it (cancellation vs timeout)
                }
                catch (JsonException)
                {
                    return AuthResult<RegisteredDevice>.NetworkOrServerError(MalformedPayloadErrorMessage, (int)response.StatusCode);
                }

                if (!IsValidDevice(device))
                {
                    return AuthResult<RegisteredDevice>.NetworkOrServerError(MalformedPayloadErrorMessage, (int)response.StatusCode);
                }

                return AuthResult<RegisteredDevice>.Ok(device!, (int)response.StatusCode);
            }

            return response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => AuthResult<RegisteredDevice>.InvalidCredentials(401),
                HttpStatusCode.Conflict => AuthResult<RegisteredDevice>.Conflict("Device already registered or conflict occurred."),
                HttpStatusCode.BadRequest => AuthResult<RegisteredDevice>.BadRequest("Invalid device registration request."),
                _ => AuthResult<RegisteredDevice>.NetworkOrServerError($"Server responded with status code {(int)response.StatusCode}", (int)response.StatusCode)
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return AuthResult<RegisteredDevice>.NetworkOrServerError(GenericNetworkErrorMessage);
        }
        catch (HttpRequestException ex)
        {
            return AuthResult<RegisteredDevice>.NetworkOrServerError(GenericNetworkErrorMessage, (int?)ex.StatusCode);
        }
    }

    public async Task<AuthResult<AuthTokens>> LoginAsync(
        LoginRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            using var response = await _httpClient.PostAsJsonAsync("/auth/login", request, JsonOptions, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                AuthTokens? tokens = null;
                try
                {
                    tokens = await response.Content.ReadFromJsonAsync<AuthTokens>(JsonOptions, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw; // Let outer block handle it
                }
                catch (JsonException)
                {
                    return AuthResult<AuthTokens>.NetworkOrServerError(MalformedPayloadErrorMessage, (int)response.StatusCode);
                }

                if (!IsValidTokens(tokens))
                {
                    return AuthResult<AuthTokens>.NetworkOrServerError(MalformedPayloadErrorMessage, (int)response.StatusCode);
                }

                return AuthResult<AuthTokens>.Ok(tokens!, (int)response.StatusCode);
            }

            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                var error = await TryReadErrorAsync(response, cancellationToken);
                if (string.Equals(error?.Code, "DEVICE_NOT_APPROVED", StringComparison.OrdinalIgnoreCase))
                {
                    return AuthResult<AuthTokens>.DeviceNotApproved(error?.Code ?? "DEVICE_NOT_APPROVED");
                }
                return AuthResult<AuthTokens>.Forbidden("Access forbidden.");
            }

            return response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => AuthResult<AuthTokens>.InvalidCredentials(401),
                HttpStatusCode.BadRequest => AuthResult<AuthTokens>.BadRequest("Invalid login request."),
                _ => AuthResult<AuthTokens>.NetworkOrServerError($"Server responded with status code {(int)response.StatusCode}", (int)response.StatusCode)
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return AuthResult<AuthTokens>.NetworkOrServerError(GenericNetworkErrorMessage);
        }
        catch (HttpRequestException ex)
        {
            return AuthResult<AuthTokens>.NetworkOrServerError(GenericNetworkErrorMessage, (int?)ex.StatusCode);
        }
    }

    public async Task<AuthResult<AuthTokens>> RefreshAsync(
        RefreshRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            using var response = await _httpClient.PostAsJsonAsync("/auth/refresh", request, JsonOptions, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                AuthTokens? tokens = null;
                try
                {
                    tokens = await response.Content.ReadFromJsonAsync<AuthTokens>(JsonOptions, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw; // Let outer block handle it
                }
                catch (JsonException)
                {
                    return AuthResult<AuthTokens>.NetworkOrServerError(MalformedPayloadErrorMessage, (int)response.StatusCode);
                }

                if (!IsValidTokens(tokens))
                {
                    return AuthResult<AuthTokens>.NetworkOrServerError(MalformedPayloadErrorMessage, (int)response.StatusCode);
                }

                return AuthResult<AuthTokens>.Ok(tokens!, (int)response.StatusCode);
            }

            return response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => AuthResult<AuthTokens>.Unauthorized("Session refresh token expired or revoked."),
                HttpStatusCode.BadRequest => AuthResult<AuthTokens>.BadRequest("Invalid refresh request."),
                _ => AuthResult<AuthTokens>.NetworkOrServerError($"Server responded with status code {(int)response.StatusCode}", (int)response.StatusCode)
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return AuthResult<AuthTokens>.NetworkOrServerError(GenericNetworkErrorMessage);
        }
        catch (HttpRequestException ex)
        {
            return AuthResult<AuthTokens>.NetworkOrServerError(GenericNetworkErrorMessage, (int?)ex.StatusCode);
        }
    }

    public async Task<AuthResult<bool>> LogoutAsync(
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new ArgumentException("Access token is required for logout.", nameof(accessToken));

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/auth/logout");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            using var response = await _httpClient.SendAsync(request, cancellationToken);

            if (response.StatusCode == HttpStatusCode.NoContent)
            {
                return AuthResult<bool>.Ok(true, (int)response.StatusCode);
            }

            return response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => AuthResult<bool>.Unauthorized("Access token is invalid or expired."),
                HttpStatusCode.Forbidden => AuthResult<bool>.Forbidden("Forbidden logout request."),
                _ => AuthResult<bool>.NetworkOrServerError($"Server responded with status code {(int)response.StatusCode}", (int)response.StatusCode)
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return AuthResult<bool>.NetworkOrServerError(GenericNetworkErrorMessage);
        }
        catch (HttpRequestException ex)
        {
            return AuthResult<bool>.NetworkOrServerError(GenericNetworkErrorMessage, (int?)ex.StatusCode);
        }
    }

    private static bool IsValidDevice(RegisteredDevice? device) =>
        device is not null &&
        device.DeviceId != Guid.Empty &&
        !string.IsNullOrWhiteSpace(device.DeviceSecret);

    private static bool IsValidTokens(AuthTokens? tokens) =>
        tokens is not null &&
        !string.IsNullOrWhiteSpace(tokens.AccessToken) &&
        !string.IsNullOrWhiteSpace(tokens.RefreshToken) &&
        string.Equals(tokens.TokenType, "Bearer", StringComparison.OrdinalIgnoreCase) &&
        tokens.ExpiresAt != default &&
        tokens.RefreshExpiresAt != default &&
        tokens.RefreshExpiresAt > tokens.ExpiresAt;

    private static async Task<AuthErrorResponse?> TryReadErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<AuthErrorResponse>(JsonOptions, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw; // Let outer block handle it
        }
        catch
        {
            return null;
        }
    }
}
