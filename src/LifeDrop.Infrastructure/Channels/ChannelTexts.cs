using System.Globalization;
using System.Text.Json;
using LifeDrop.Application.Channels;

namespace LifeDrop.Infrastructure.Channels;

// Channel texts (plan step 3.4) from i18n/strings.json in ONE language for the whole company (Channels:Language, default Sorani Kurdish), because a user's own
// language is not stored on the server. A missing translation falls back to English, an unknown key to the key itself.
public sealed class ChannelTexts : IChannelTexts
{
    public const string DefaultLanguage = "ckb";
    private static readonly Lazy<Dictionary<string, Dictionary<string, string>>> All = new(Load);
    private readonly string language;

    public ChannelTexts(string? language) => this.language = string.IsNullOrWhiteSpace(language) ? DefaultLanguage : language.Trim().ToLowerInvariant();

    public string Get(string key, params (string Name, object Value)[] vars)
    {
        if (!All.Value.TryGetValue(key, out var entry)) return key;
        var text = entry.TryGetValue(language, out var t) && !string.IsNullOrWhiteSpace(t) ? t : entry.GetValueOrDefault("en", key);
        foreach (var (name, value) in vars)
            text = text.Replace("{" + name + "}", Convert.ToString(value, CultureInfo.InvariantCulture));
        return text;
    }

    private static Dictionary<string, Dictionary<string, string>> Load()
    {
        using var stream = typeof(ChannelTexts).Assembly.GetManifestResourceStream("LifeDrop.strings.json")
            ?? throw new InvalidOperationException("Embedded i18n/strings.json is missing.");
        return JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(stream)
            ?? throw new InvalidOperationException("i18n/strings.json is empty.");
    }
}
