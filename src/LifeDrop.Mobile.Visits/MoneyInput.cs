using System.Text;
using System.Text.RegularExpressions;

namespace LifeDrop.Mobile.Visits;

public enum MoneyProblem { Empty, Invalid, Decimals }

// Typing an amount of money (owner request 2026-11-27). In Iraq the dot and the comma also separate thousands, so "10.440" and "10,440" are
// ten thousand four hundred and forty dinars, not 10.44. IQD has no decimals: only whole numbers, and a separator followed by exactly three
// digits is a thousands mark. USD has two decimals. Digits of any script (Persian, Arabic-Indic) and spaces are accepted.
// The panel has the same rules (moneyInput.ts, same test cases) and the server refuses what they refuse (MoneyRules).
public static partial class MoneyInput
{
    [GeneratedRegex(@"^\d{1,3}([.,]\d{3})+$")]
    private static partial Regex Groups();
    [GeneratedRegex(@"^\d[\d.,]*\d$")]
    private static partial Regex Shape();
    [GeneratedRegex(@"^\d+[.,]\d+$")]
    private static partial Regex WithFraction();

    private static string Normalise(string text)
    {
        var result = new StringBuilder();
        foreach (var c in text.Trim())
        {
            switch (c)
            {
                case >= '٠' and <= '٩': result.Append((char)('0' + (c - '٠'))); break;
                case >= '۰' and <= '۹': result.Append((char)('0' + (c - '۰'))); break;
                case '٫': result.Append('.'); break;
                case '٬' or '،': result.Append(','); break;
                case '\'' or '’': break;
                default:
                    if (!char.IsWhiteSpace(c) && c != ' ' && c != ' ') result.Append(c);
                    break;
            }
        }
        return result.ToString();
    }

    private static decimal Whole(string digitsWithMarks) => decimal.Parse(digitsWithMarks.Replace(".", "").Replace(",", ""), System.Globalization.CultureInfo.InvariantCulture);

    public static (decimal? Value, MoneyProblem? Problem) Parse(string? text, string? currency)
    {
        var s = Normalise(text ?? "");
        if (s.Length == 0) return (null, MoneyProblem.Empty);
        if (s.All(char.IsAsciiDigit)) return (Whole(s), null);
        if (!Shape().IsMatch(s) || s.Contains("..") || s.Contains(",,") || s.Contains(".,") || s.Contains(",.")) return (null, MoneyProblem.Invalid);
        if (currency != "USD")
        {
            if (Groups().IsMatch(s)) return (Whole(s), null);
            return (null, WithFraction().IsMatch(s) ? MoneyProblem.Decimals : MoneyProblem.Invalid);
        }
        // USD: the last separator is the decimal mark when one or two digits follow it (or when both kinds are used); the others are thousands marks.
        var last = Math.Max(s.LastIndexOf('.'), s.LastIndexOf(','));
        var whole = s[..last];
        var fraction = s[(last + 1)..];
        var both = s.Contains('.') && s.Contains(',');
        if (fraction.Length == 3 && !both && Groups().IsMatch(s)) return (Whole(s), null);
        if (fraction.Length > 2) return (null, MoneyProblem.Decimals);
        var wholeDigits = whole.Replace(".", "").Replace(",", "");
        if (!(whole == wholeDigits || Groups().IsMatch(whole))) return (null, MoneyProblem.Invalid);
        return (decimal.Parse($"{wholeDigits}.{fraction}", System.Globalization.CultureInfo.InvariantCulture), null);
    }
}
