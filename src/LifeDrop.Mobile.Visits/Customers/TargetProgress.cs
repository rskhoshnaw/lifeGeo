namespace LifeDrop.Mobile.Visits;

// The numbers of the visitor's own targets page (plan step 5.5): how far a product is, and which months can be shown.
public static class TargetProgress
{
    // Whole percent of the target that was delivered (not capped, 130 means more than the target); null when there is no target.
    public static int? Percent(decimal? target, decimal actual) =>
        target is > 0 ? (int)Math.Round(actual / target.Value * 100m, MidpointRounding.AwayFromZero) : null;

    // The part of a progress bar to fill, 0 to 1.
    public static double Fill(decimal? target, decimal actual) => target is > 0 ? (double)Math.Min(1m, Math.Max(0m, actual / target.Value)) : 0;

    public static DateOnly FirstOfMonth(DateOnly date) => new(date.Year, date.Month, 1);

    public static DateOnly Shift(DateOnly month, int by) => FirstOfMonth(month).AddMonths(by);

    // The server shows the months from two years back to the current one (and a year ahead); the page goes back and up to today's month.
    public static bool CanGoNext(DateOnly month, DateOnly today) => FirstOfMonth(month) < FirstOfMonth(today);
    public static bool CanGoBack(DateOnly month, DateOnly today) => FirstOfMonth(month) > FirstOfMonth(today).AddYears(-2);

    public static string MonthPath(DateOnly month) => FirstOfMonth(month).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
}
