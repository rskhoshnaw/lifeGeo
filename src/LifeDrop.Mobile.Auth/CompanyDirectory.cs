using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LifeDrop.Mobile.Auth;

/// <summary>What the app needs to know about a company before sign-in (plan step 4.2).</summary>
public sealed record CompanyConfig(string TenantCode, string Name, string ApiBaseUrl, string? SupportPhone);

public enum CompanyLookupStatus { Found, NotFound, InvalidCode, NotConfigured, InsecureAddress, Unavailable }

public sealed record CompanyLookup(CompanyLookupStatus Status, CompanyConfig? Config = null);

/// <summary>
/// Turns a company code into the company's API address by asking the bootstrap server, so no company address is built into the app.
/// A company without its own address uses the bootstrap server itself. An http address is refused unless the caller allows it (debug builds).
/// </summary>
public static partial class CompanyDirectory
{
    [GeneratedRegex("^[a-z0-9][a-z0-9_-]{1,39}$")]
    private static partial Regex CodePattern();

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public static bool IsValidCode(string? code) => code is not null && CodePattern().IsMatch(code.Trim().ToLowerInvariant());

    public static async Task<CompanyLookup> ResolveAsync(HttpClient http, string? bootstrapUrl, string? code, bool allowInsecure, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(http);
        if (!IsValidCode(code)) return new CompanyLookup(CompanyLookupStatus.InvalidCode);
        if (!Uri.TryCreate(bootstrapUrl, UriKind.Absolute, out var bootstrap)) return new CompanyLookup(CompanyLookupStatus.NotConfigured);
        if (!IsAllowed(bootstrap, allowInsecure)) return new CompanyLookup(CompanyLookupStatus.InsecureAddress);

        try
        {
            var normalized = code!.Trim().ToLowerInvariant();
            var address = new Uri(bootstrap, "/company-config/" + Uri.EscapeDataString(normalized));
            using var response = await http.GetAsync(address, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound) return new CompanyLookup(CompanyLookupStatus.NotFound);
            if (!response.IsSuccessStatusCode) return new CompanyLookup(CompanyLookupStatus.Unavailable);

            var body = await response.Content.ReadFromJsonAsync<Body>(Json, cancellationToken);
            // The answer must be about the company that was asked for; anything else is not trusted.
            if (body is null || string.IsNullOrWhiteSpace(body.TenantCode) || string.IsNullOrWhiteSpace(body.Name) ||
                !string.Equals(body.TenantCode, normalized, StringComparison.OrdinalIgnoreCase))
                return new CompanyLookup(CompanyLookupStatus.Unavailable);

            var api = bootstrap;
            if (!string.IsNullOrWhiteSpace(body.ApiBaseUrl))
            {
                if (!Uri.TryCreate(body.ApiBaseUrl, UriKind.Absolute, out api!)) return new CompanyLookup(CompanyLookupStatus.Unavailable);
                if (!IsAllowed(api, allowInsecure)) return new CompanyLookup(CompanyLookupStatus.InsecureAddress);
            }
            return new CompanyLookup(CompanyLookupStatus.Found,
                new CompanyConfig(body.TenantCode, body.Name, api.GetLeftPart(UriPartial.Authority), string.IsNullOrWhiteSpace(body.SupportPhone) ? null : body.SupportPhone));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or OperationCanceledException)
        {
            return new CompanyLookup(CompanyLookupStatus.Unavailable);
        }
    }

    /// <summary>True when the phone holds data of another company, so the saved data of the old company must be wiped before the new one signs in.</summary>
    public static bool IsCompanyChange(string? previousCode, string? newCode) =>
        !string.IsNullOrWhiteSpace(previousCode) && !string.Equals(previousCode.Trim(), newCode?.Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool IsAllowed(Uri uri, bool allowInsecure) => uri.Scheme == Uri.UriSchemeHttps || (allowInsecure && uri.Scheme == Uri.UriSchemeHttp);

    private sealed record Body(string? TenantCode, string? Name, string? ApiBaseUrl, string? SupportPhone);
}
