using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LifeDrop.Mobile.Localization;

// App texts (plan step 1.21, PRD §13) from i18n/strings.json, the file the web panel uses too. Sorani Kurdish is the
// default; Kurdish, Arabic and Persian are right-to-left. The app sets Language at start-up from the saved choice.
public static partial class Texts
{
    public const string DefaultLanguage = "ckb";
    public static readonly IReadOnlyList<(string Code, string Name)> Languages =
        [("ckb", "کوردی"), ("ar", "العربية"), ("fa", "فارسی"), ("en", "English")];

    private static readonly Dictionary<string, Dictionary<string, string>> All = Load();
    private static string _language = DefaultLanguage;

    public static string Language
    {
        get => _language;
        set => _language = Languages.Any(l => l.Code == value) ? value : DefaultLanguage;
    }

    // Industry profile of the company (plan step 4.3). "food" swaps the pharmacy wording for shops and restaurants: a text "key@food" replaces "key".
    public static string Profile { get; set; } = "";

    public static bool IsRightToLeft => Language != "en";

    // Dates such as the plan header; falls back to Arabic (Iraq) where the phone has no Kurdish calendar data.
    public static CultureInfo Culture
    {
        get
        {
            var name = Language switch { "ckb" => "ckb-IQ", "ar" => "ar-IQ", "fa" => "fa-IR", _ => "en-GB" };
            try { return CultureInfo.GetCultureInfo(name); }
            catch (CultureNotFoundException) { return CultureInfo.GetCultureInfo("ar-IQ"); }
        }
    }

    public static IReadOnlyCollection<string> Keys => All.Keys;

    public static bool Has(string key) => All.ContainsKey(key);

    // The text in the current language (English when a translation is missing, the key itself when unknown),
    // with {name} placeholders filled from vars.
    public static string Get(string key, params (string Name, object Value)[] vars) => Get(Language, key, vars);

    public static string Get(string language, string key, params (string Name, object Value)[] vars)
    {
        Dictionary<string, string>? entry;
        if (!string.IsNullOrEmpty(Profile) && All.TryGetValue(key + "@" + Profile, out var special)) entry = special;
        else if (!All.TryGetValue(key, out entry)) return key;
        var text = entry.TryGetValue(language, out var t) && !string.IsNullOrWhiteSpace(t) ? t : entry.GetValueOrDefault("en", key);
        foreach (var (name, value) in vars)
            text = text.Replace("{" + name + "}", Convert.ToString(value, CultureInfo.InvariantCulture));
        return text;
    }

    public static IReadOnlyList<string> Placeholders(string text) =>
        Placeholder().Matches(text).Select(m => m.Groups[1].Value).Order(StringComparer.Ordinal).ToList();

    public static string? Raw(string language, string key) =>
        All.TryGetValue(key, out var entry) ? entry.GetValueOrDefault(language) : null;

    [GeneratedRegex(@"\{(\w+)\}")]
    private static partial Regex Placeholder();

    private static Dictionary<string, Dictionary<string, string>> Load()
    {
        using var stream = typeof(Texts).Assembly.GetManifestResourceStream("LifeDrop.strings.json")
            ?? throw new InvalidOperationException("Embedded i18n/strings.json is missing.");
        return JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(stream)
            ?? throw new InvalidOperationException("i18n/strings.json is empty.");
    }
}
