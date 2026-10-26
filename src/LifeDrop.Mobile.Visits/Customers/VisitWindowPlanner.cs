namespace LifeDrop.Mobile.Visits;

public enum VisitWindowPlanResult { Ok, NoDays, BadHours }

// Turns "which days / which hours" from the new-customer form (plan step 5.3) into the visit windows the server stores. "Any day" and "any hour" mean no restriction:
// both together send nothing; a day list with "any hour" is the whole day; "any day" with hours is the same hours on all seven days. Weekday 0 = Sunday .. 6 = Saturday.
public static class VisitWindowPlanner
{
    public static (VisitWindowPlanResult Result, IReadOnlyList<VisitWindowItem> Windows) Build(bool anyDay, IReadOnlyCollection<int> days, bool anyHour, TimeSpan from, TimeSpan to)
    {
        if (anyDay && anyHour) return (VisitWindowPlanResult.Ok, Array.Empty<VisitWindowItem>());
        var chosen = anyDay ? Enumerable.Range(0, 7).ToList() : days.Where(d => d is >= 0 and <= 6).Distinct().OrderBy(d => d).ToList();
        if (chosen.Count == 0) return (VisitWindowPlanResult.NoDays, Array.Empty<VisitWindowItem>());
        if (!anyHour && from >= to) return (VisitWindowPlanResult.BadHours, Array.Empty<VisitWindowItem>());
        var opens = anyHour ? new TimeSpan(0, 0, 0) : from;
        var closes = anyHour ? new TimeSpan(23, 59, 0) : to;
        return (VisitWindowPlanResult.Ok, chosen.Select(d => new VisitWindowItem(d, Format(opens), Format(closes))).ToList());
    }

    private static string Format(TimeSpan time) => time.ToString(@"hh\:mm\:ss", System.Globalization.CultureInfo.InvariantCulture);
}
