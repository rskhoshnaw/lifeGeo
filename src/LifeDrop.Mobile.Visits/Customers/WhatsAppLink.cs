namespace LifeDrop.Mobile.Visits;

// The wa.me link that opens a WhatsApp chat with a phone number (customer phones, the support line). Iraqi numbers are typed as 07xx xxx xxxx;
// WhatsApp wants the country code and digits only, so 0770... becomes 964770.... The same rule is in the panel (components/whatsapp.ts).
public static class WhatsAppLink
{
    public const string CountryCode = "964";

    // The digits WhatsApp needs, or null when the text is not a usable phone number.
    public static string? Digits(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone)) return null;
        var digits = new string(phone.Where(char.IsAsciiDigit).ToArray());
        var international = phone.TrimStart().StartsWith('+') || digits.StartsWith("00", StringComparison.Ordinal);
        if (digits.StartsWith("00", StringComparison.Ordinal)) digits = digits[2..];
        else if (!international && digits.StartsWith('0')) digits = CountryCode + digits[1..];
        else if (!international && digits.Length == 10 && digits.StartsWith('7')) digits = CountryCode + digits;
        return digits.Length is >= 8 and <= 15 ? digits : null;
    }

    public static string? Url(string? phone, string? text = null)
    {
        var digits = Digits(phone);
        if (digits is null) return null;
        return string.IsNullOrWhiteSpace(text) ? $"https://wa.me/{digits}" : $"https://wa.me/{digits}?text={Uri.EscapeDataString(text)}";
    }
}
