namespace LifeDrop.Mobile.Localization;

// The parts of the product a company switched off (plan step 4.48): the home screen leaves out their buttons. Kept on the phone as one comma-separated text.
public static class CompanyModules
{
    public static string Serialize(IEnumerable<string>? hidden) => string.Join(',', (hidden ?? []).Where(m => !string.IsNullOrWhiteSpace(m)).Select(m => m.Trim().ToLowerInvariant()).Distinct().Order(StringComparer.Ordinal));

    public static IReadOnlySet<string> Parse(string? text) =>
        (text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(m => m.ToLowerInvariant()).ToHashSet();

    // The home buttons and what they need: products need "catalog", payments "payments", follow-ups "followups", the warehouse and deliveries "warehouse".
    public static bool Shows(IReadOnlySet<string> hidden, string module) => !hidden.Contains(module);
}
