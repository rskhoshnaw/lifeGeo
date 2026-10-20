using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LifeDrop.Mobile.Auth;

/// <summary>What the app needs to know about a company before sign-in (plan step 4.2).</summary>
public sealed record CompanyConfig(string TenantCode, string Name, string ApiBaseUrl, string? SupportPhone, string Industry = "pharma",
    // Plan step 4.48: parts of the product this company switched off, and its own wording (text key -> language -> text).
    IReadOnlyList<string>? HiddenModules = null, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? Texts = null);

public enum CompanyLookupStatus { Found, NotFound, InvalidCode, NotConfigured, InsecureAddress, Unavailable }

/// <summary>Detail is a short technical reason for an Unavailable answer (exception type and message, or the HTTP status); the sign-in screen shows it under the message so a failure can be told apart without a debugger.</summary>
public sealed record CompanyLookup(CompanyLookupStatus Status, CompanyConfig? Config = null, string? Detail = null);

/// <summary>
/// Turns a company code into the company's API address by asking the bootstrap server, so no company address is built into the app.
/// A company without its own address uses the bootstrap server itself. An http address is refused unless the caller allows it (debug builds).
/// </summary>
public static partial class CompanyDirectory
{
    [GeneratedRegex("^[a-z0-9][a-z0-9_-]{1,39}$")]
    private static partial Regex CodePattern();

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// The server that answers for a company. Every company has its own server, so the code decides the address: code "acme" is the
    /// subdomain acme.&lt;base domain&gt; of the build's base domain. Without a base domain the fallback (single-server installs, development) is used.
    /// </summary>
    public static string? ServerFor(string? code, string? baseDomain, string? fallbackUrl)
    {
        var c = (code ?? "").Trim().ToLowerInvariant();
        var domain = (baseDomain ?? "").Trim().Trim('.').ToLowerInvariant();
        if (domain.Length > 0 && IsValidCode(c) && !c.Contains('_')) return "https://" + c + "." + domain;
        return fallbackUrl;
    }

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
            if (!response.IsSuccessStatusCode) return new CompanyLookup(CompanyLookupStatus.Unavailable, null, "HTTP " + (int)response.StatusCode);

            var body = await response.Content.ReadFromJsonAsync<Body>(Json, cancellationToken);
            // The answer must be about the company that was asked for; anything else is not trusted.
            if (body is null || string.IsNullOrWhiteSpace(body.TenantCode) || string.IsNullOrWhiteSpace(body.Name) ||
                !string.Equals(body.TenantCode, normalized, StringComparison.OrdinalIgnoreCase))
                return new CompanyLookup(CompanyLookupStatus.Unavailable, null, "unexpected answer");

            var api = bootstrap;
            if (!string.IsNullOrWhiteSpace(body.ApiBaseUrl))
            {
                if (!Uri.TryCreate(body.ApiBaseUrl, UriKind.Absolute, out api!)) return new CompanyLookup(CompanyLookupStatus.Unavailable);
                if (!IsAllowed(api, allowInsecure)) return new CompanyLookup(CompanyLookupStatus.InsecureAddress);
            }
            return new CompanyLookup(CompanyLookupStatus.Found,
                new CompanyConfig(body.TenantCode, body.Name, api.GetLeftPart(UriPartial.Authority), string.IsNullOrWhiteSpace(body.SupportPhone) ? null : body.SupportPhone,
                    string.Equals(body.Industry, "food", StringComparison.OrdinalIgnoreCase) ? "food" : "pharma",
                    (body.HiddenModules ?? []).Where(m => !string.IsNullOrWhiteSpace(m)).Select(m => m.Trim().ToLowerInvariant()).Distinct().ToList(),
                    (body.Texts ?? []).Where(kv => kv.Value is not null).ToDictionary(kv => kv.Key, kv => (IReadOnlyDictionary<string, string>)kv.Value)));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or InvalidOperationException or NotSupportedException or OperationCanceledException)
        {
            return new CompanyLookup(CompanyLookupStatus.Unavailable, null, Describe(e));
        }
    }

    /// <summary>True when the phone holds data of another company, so the saved data of the old company must be wiped before the new one signs in.</summary>
    public static bool IsCompanyChange(string? previousCode, string? newCode) =>
        !string.IsNullOrWhiteSpace(previousCode) && !string.Equals(previousCode.Trim(), newCode?.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string Describe(Exception e)
    {
        var inner = e.InnerException is null ? "" : " <- " + e.InnerException.GetType().Name + ": " + e.InnerException.Message;
        var text = e.GetType().Name + ": " + e.Message + inner;
        return text.Length <= 220 ? text : text[..220];
    }

    private static bool IsAllowed(Uri uri, bool allowInsecure) => uri.Scheme == Uri.UriSchemeHttps || (allowInsecure && uri.Scheme == Uri.UriSchemeHttp);

    private sealed record Body(string? TenantCode, string? Name, string? ApiBaseUrl, string? SupportPhone, string? Industry, string[]? HiddenModules = null, Dictionary<string, Dictionary<string, string>>? Texts = null);
}
