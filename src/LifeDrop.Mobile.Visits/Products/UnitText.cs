using LifeDrop.Mobile.Localization;

namespace LifeDrop.Mobile.Visits;

// A product unit is stored as an English word (box, strip, ...; plan step 5.46) and shown in the user's language. A unit an older product has that is not in the list is shown as it is.
public static class UnitText
{
    public static string Label(string? unit)
    {
        if (string.IsNullOrWhiteSpace(unit)) return "";
        var key = "web.unit." + unit.Trim().ToLowerInvariant();
        return Texts.Has(key) ? Texts.Get(key) : unit;
    }
}
