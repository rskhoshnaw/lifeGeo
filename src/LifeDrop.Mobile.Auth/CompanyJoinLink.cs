namespace LifeDrop.Mobile.Auth;

/// <summary>
/// The company code inside an invitation link (plan step 4.7). A link carries only the public company code, never a password or a token:
/// <c>lifedrop://join/acme</c> (the app link, also in the QR code), <c>lifedrop://join?company=acme</c> or a panel link <c>https://…/?company=acme</c>.
/// Anything that is not a link, or whose code cannot be a company code, gives null, so pasted text can never put something odd into the sign-in.
/// </summary>
public static class CompanyJoinLink
{
    public const string Scheme = "lifedrop";

    /// <summary>True when the text looks like a link (so the sign-in replaces it by the code), whatever it contains.</summary>
    public static bool LooksLikeLink(string? text) => text is not null && text.Contains("://", StringComparison.Ordinal);

    public static string? TryGetCode(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || !Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri)) return null;

        string? candidate = null;
        if (uri.Scheme.Equals(Scheme, StringComparison.OrdinalIgnoreCase))
        {
            if (!uri.Host.Equals("join", StringComparison.OrdinalIgnoreCase)) return null;
            candidate = Unescape(uri.AbsolutePath.Trim('/')) ?? FromQuery(uri.Query);
        }
        else if (uri.Scheme is "https" or "http")
        {
            candidate = FromQuery(uri.Query);
        }

        candidate = candidate?.Trim().ToLowerInvariant();
        return CompanyDirectory.IsValidCode(candidate) ? candidate : null;
    }

    private static string? FromQuery(string query)
    {
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length == 2 && parts[0].Equals("company", StringComparison.OrdinalIgnoreCase)) return Unescape(parts[1]);
        }
        return null;
    }

    // An empty or broken escape is "no code", not an exception.
    private static string? Unescape(string value)
    {
        if (value.Length == 0) return null;
        try { return Uri.UnescapeDataString(value); }
        catch (UriFormatException) { return null; }
    }
}
