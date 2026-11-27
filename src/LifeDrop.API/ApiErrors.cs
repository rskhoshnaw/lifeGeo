using Microsoft.AspNetCore.Http.HttpResults;

namespace LifeDrop.API;

// Shared API error contract (PRD §11, plan step 0.5). Every 4xx/5xx response is RFC 9457
// ProblemDetails with a stable machine-readable "code" and a "traceId". Clients branch on
// "code", never on "title" or "detail".
public static class ApiErrors
{
    public const string CodeKey = "code";
    public const string TraceIdKey = "traceId";

    // Specific codes returned by endpoints.
    public const string DeviceNotApproved = "DEVICE_NOT_APPROVED";
    // The phone sent a device id the server does not know for this user (deleted by a manager, or never registered here): the app forgets it and registers again.
    public const string DeviceUnknown = "DEVICE_UNKNOWN";
    public const string DuplicateCode = "DUPLICATE_CODE";
    public const string InvalidState = "INVALID_STATE";
    public const string InvalidData = "INVALID_DATA";
    public const string ConcurrencyConflict = "CONCURRENCY_CONFLICT";
    public const string UserNotFound = "USER_NOT_FOUND";
    public const string CustomerNotFound = "CUSTOMER_NOT_FOUND";
    public const string FeedbackNotFound = "FEEDBACK_NOT_FOUND";
    public const string TooManyPhotos = "TOO_MANY_PHOTOS";
    public const string RegionNotFound = "REGION_NOT_FOUND";
    public const string CustomerNotApproved = "CUSTOMER_NOT_APPROVED";
    public const string CurrencyMismatch = "CURRENCY_MISMATCH";
    public const string OutOfStock = "OUT_OF_STOCK";
    // A transfer asks for more goods than the sending warehouse holds (plan step 5.60).
    public const string TransferTooMuch = "TRANSFER_TOO_MUCH";
    // The warehouse of an order is not in the sender's cities (plan step 5.59; the phone shows a clear message for an order kept offline).
    public const string WarehouseNotAllowed = "WAREHOUSE_NOT_ALLOWED";
    // A return asks for more of a product than the order delivered (minus what other returns of it already take; plan step 5.54).
    public const string ReturnTooMuch = "RETURN_TOO_MUCH";
    // A refund asks for more money than the customer has coming back (plan step 5.55).
    public const string RefundTooMuch = "REFUND_TOO_MUCH";
    public const string WrongPassword = "WRONG_PASSWORD";
    public const string OrderRequired = "ORDER_REQUIRED";
    public const string DuplicateName = "DUPLICATE_NAME";
    public const string DeviceRequired = "DEVICE_REQUIRED";
    // Registering a phone that is already registered (plan step 5.62): waiting for approval, approved, or blocked; or the account / company is switched off.
    public const string DevicePending = "DEVICE_PENDING";
    public const string DeviceAlreadyApproved = "DEVICE_ALREADY_APPROVED";
    public const string DeviceBlocked = "DEVICE_BLOCKED";
    public const string AccountInactive = "ACCOUNT_INACTIVE";
    public const string CustomerLocationMissing = "CUSTOMER_LOCATION_MISSING";
    public const string ReportRequired = "REPORT_REQUIRED";
    public const string DeviceMismatch = "DEVICE_MISMATCH";
    // Check-in/out position rejected by the server (422); one code per reason.
    public const string OutsideGeofence = "OUTSIDE_GEOFENCE";
    public const string GpsLowAccuracy = "GPS_LOW_ACCURACY";
    public const string GpsStale = "GPS_STALE";
    public const string GpsClockSkew = "GPS_CLOCK_SKEW";
    public const string GpsMocked = "GPS_MOCKED";
    public const string OfflineExpired = "OFFLINE_EXPIRED";

    // Generic codes for responses produced without a specific code
    // (empty-bodied results, authentication challenges, rate limiting, unhandled exceptions).
    public const string BadRequest = "BAD_REQUEST";
    public const string Unauthenticated = "UNAUTHENTICATED";
    public const string Forbidden = "FORBIDDEN";
    public const string NotFound = "NOT_FOUND";
    public const string Conflict = "CONFLICT";
    public const string TooManyRequests = "TOO_MANY_REQUESTS";
    public const string ServiceUnavailable = "SERVICE_UNAVAILABLE";
    public const string Unexpected = "UNEXPECTED_ERROR";

    public static ProblemHttpResult Problem(int statusCode, string code) =>
        TypedResults.Problem(statusCode: statusCode,
            extensions: new Dictionary<string, object?> { [CodeKey] = code });

    public static string GenericCodeFor(int statusCode) => statusCode switch
    {
        StatusCodes.Status400BadRequest => BadRequest,
        StatusCodes.Status401Unauthorized => Unauthenticated,
        StatusCodes.Status403Forbidden => Forbidden,
        StatusCodes.Status404NotFound => NotFound,
        StatusCodes.Status409Conflict => Conflict,
        StatusCodes.Status429TooManyRequests => TooManyRequests,
        StatusCodes.Status503ServiceUnavailable => ServiceUnavailable,
        _ => Unexpected
    };

    // Fills in "code" and "traceId" for every ProblemDetails the framework writes.
    public static void Customize(ProblemDetailsContext context)
    {
        var extensions = context.ProblemDetails.Extensions;
        var status = context.ProblemDetails.Status ?? context.HttpContext.Response.StatusCode;
        if (!extensions.ContainsKey(CodeKey))
            extensions[CodeKey] = GenericCodeFor(status);
        extensions[TraceIdKey] = context.HttpContext.TraceIdentifier;
    }
}
