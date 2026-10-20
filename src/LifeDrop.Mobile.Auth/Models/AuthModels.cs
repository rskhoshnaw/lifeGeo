using System.Text.Json.Serialization;

namespace LifeDrop.Mobile.Auth.Models;

public sealed record RegisterDeviceRequest(
    string TenantCode,
    string Email,
    string Password,
    string DeviceLabel,
    string Platform,
    string InstallId,
    string AppVersion,
    // Plan step 4.39: the phone model and system version, shown to the manager in the panel.
    string? Model = null,
    string? OsVersion = null)
{
    public override string ToString() =>
        $"RegisterDeviceRequest(TenantCode={TenantCode}, Email={Email}, DeviceLabel={DeviceLabel}, Platform={Platform}, InstallId={InstallId}, AppVersion={AppVersion}, Password=[REDACTED])";
}

public sealed record RegisteredDevice(
    Guid DeviceId,
    string DeviceSecret,
    string TenantCode = "",
    string Email = "")
{
    public override string ToString() =>
        $"RegisteredDevice(TenantCode={TenantCode}, Email={Email}, DeviceId={DeviceId}, DeviceSecret=[REDACTED])";
}

public sealed record LoginRequest(
    string TenantCode,
    string Email,
    string Password,
    Guid? DeviceId = null,
    string? DeviceSecret = null)
{
    public override string ToString() =>
        $"LoginRequest(TenantCode={TenantCode}, Email={Email}, DeviceId={DeviceId}, Password=[REDACTED], DeviceSecret={(DeviceSecret is null ? "null" : "[REDACTED]")})";
}

public sealed record RefreshRequest(
    string RefreshToken,
    Guid? DeviceId = null,
    string? DeviceSecret = null)
{
    public override string ToString() =>
        $"RefreshRequest(DeviceId={DeviceId}, RefreshToken=[REDACTED], DeviceSecret={(DeviceSecret is null ? "null" : "[REDACTED]")})";
}

public sealed record AuthTokens(
    string AccessToken,
    string RefreshToken,
    string TokenType,
    DateTime ExpiresAt,
    DateTime RefreshExpiresAt)
{
    public override string ToString() =>
        $"AuthTokens(TokenType={TokenType}, ExpiresAt={ExpiresAt:O}, RefreshExpiresAt={RefreshExpiresAt:O}, AccessToken=[REDACTED], RefreshToken=[REDACTED])";
}

public enum SessionStatus
{
    Active,
    RefreshPending
}

public sealed record StoredSession
{
    public AuthTokens Tokens { get; init; }
    public string TenantCode { get; init; }
    public string Email { get; init; }
    public bool IsDeviceBound { get; init; }
    public Guid? BoundDeviceId { get; init; }
    public SessionStatus Status { get; init; } = SessionStatus.Active;
    public Guid SessionId { get; init; }
    public Guid AttemptId { get; init; }

    public StoredSession(AuthTokens tokens, string tenantCode, string email, bool isDeviceBound, Guid? boundDeviceId, SessionStatus status = SessionStatus.Active, Guid? sessionId = null, Guid? attemptId = null)
    {
        if (string.IsNullOrWhiteSpace(tenantCode)) throw new ArgumentException("TenantCode is required.");
        if (string.IsNullOrWhiteSpace(email)) throw new ArgumentException("Email is required.");
        if (isDeviceBound && (!boundDeviceId.HasValue || boundDeviceId.Value == Guid.Empty)) throw new ArgumentException("BoundDeviceId is required and cannot be empty when IsDeviceBound is true.");
        if (!isDeviceBound && boundDeviceId.HasValue) throw new ArgumentException("BoundDeviceId must be null when IsDeviceBound is false.");
        
        Tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        TenantCode = tenantCode;
        Email = email;
        IsDeviceBound = isDeviceBound;
        BoundDeviceId = boundDeviceId;
        Status = status;
        SessionId = sessionId ?? Guid.NewGuid();
        AttemptId = attemptId ?? Guid.Empty;
    }

    public bool IsValid()
    {
        if (Status != SessionStatus.Active)
            return false;

        if (Tokens == null || string.IsNullOrWhiteSpace(Tokens.AccessToken) || string.IsNullOrWhiteSpace(Tokens.RefreshToken))
            return false;
            
        if (!string.Equals(Tokens.TokenType, "Bearer", StringComparison.OrdinalIgnoreCase))
            return false;

        if (Tokens.ExpiresAt == default || Tokens.RefreshExpiresAt == default)
            return false;

        if (Tokens.ExpiresAt > Tokens.RefreshExpiresAt)
            return false;
        
        if (string.IsNullOrWhiteSpace(TenantCode) || string.IsNullOrWhiteSpace(Email))
            return false;

        if (IsDeviceBound)
        {
            if (!BoundDeviceId.HasValue || BoundDeviceId.Value == Guid.Empty)
                return false;
        }
        else
        {
            if (BoundDeviceId.HasValue)
                return false;
        }

        return true;
    }

    public override string ToString() =>
        $"StoredSession(TenantCode={TenantCode}, Email={Email}, IsDeviceBound={IsDeviceBound}, BoundDeviceId={BoundDeviceId}, Tokens={Tokens})";
}

public sealed record AuthErrorResponse(
    [property: JsonPropertyName("code")]
    [property: JsonInclude]
    string? Code = null);

public enum AuthStatus
{
    Success,
    InvalidCredentials,
    DeviceNotApproved,
    Conflict,
    Unauthorized,
    BadRequest,
    Forbidden,
    NetworkOrServerError,
    LocalStateError
}

public sealed record AuthResult<T>(
    AuthStatus Status,
    T? Data = default,
    string? ErrorCode = null,
    string? ErrorMessage = null,
    int? HttpStatusCode = null)
{
    public bool IsSuccess => Status == AuthStatus.Success;

    public static AuthResult<T> Ok(T data, int statusCode = 200) =>
        new(AuthStatus.Success, Data: data, HttpStatusCode: statusCode);

    public static AuthResult<T> DeviceNotApproved(string? errorCode = "DEVICE_NOT_APPROVED") =>
        new(AuthStatus.DeviceNotApproved, ErrorCode: errorCode, ErrorMessage: "Device is pending approval or not approved.", HttpStatusCode: 403);

    public static AuthResult<T> InvalidCredentials(int statusCode = 401) =>
        new(AuthStatus.InvalidCredentials, ErrorMessage: "Invalid credentials.", HttpStatusCode: statusCode);

    public static AuthResult<T> Conflict(string? message = "Device registration conflict.") =>
        new(AuthStatus.Conflict, ErrorMessage: message, HttpStatusCode: 409);

    public static AuthResult<T> Unauthorized(string? message = "Unauthorized.") =>
        new(AuthStatus.Unauthorized, ErrorMessage: message, HttpStatusCode: 401);

    public static AuthResult<T> BadRequest(string? message = "Bad request.") =>
        new(AuthStatus.BadRequest, ErrorMessage: message, HttpStatusCode: 400);

    public static AuthResult<T> Forbidden(string? message = "Forbidden.") =>
        new(AuthStatus.Forbidden, ErrorMessage: message, HttpStatusCode: 403);

    public static AuthResult<T> NetworkOrServerError(string message, int? statusCode = null) =>
        new(AuthStatus.NetworkOrServerError, ErrorMessage: message, HttpStatusCode: statusCode);

    public static AuthResult<T> LocalStateError(string message) =>
        new(AuthStatus.LocalStateError, ErrorMessage: message);
}
