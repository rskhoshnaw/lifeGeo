using System.Globalization;

namespace LifeDrop.Mobile.Visits;

public sealed record PlanTemplateItem(Guid Id, string Name, int Customers, int Days);
public sealed record PlanTemplateApplied(int Created, int Skipped, int Days);

// Plan templates from the phone (plan step 5.8): save a week of the user's own visits as a template and apply it to the coming weeks. Online only; the server plans through the normal plan
// generation (accepted hours, "already planned that day", scope).
public interface IPlanTemplatesApiClient
{
    Task<(VisitActionResult Result, IReadOnlyList<PlanTemplateItem> Templates)> GetAsync(CancellationToken cancellationToken = default);
    Task<VisitActionResult> SaveAsync(string name, DateOnly weekStart, CancellationToken cancellationToken = default);
    Task<(VisitActionResult Result, PlanTemplateApplied? Applied)> ApplyAsync(Guid id, DateOnly startDate, int weeks, CancellationToken cancellationToken = default);
    Task<VisitActionResult> DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}

// The Iraqi week starts on Saturday: the first day of the week that holds a date, moved by whole weeks (-1 last week, 0 this week, 1 next week).
public static class PlanTemplateWeeks
{
    public static DateOnly WeekStart(DateOnly today, int weekOffset)
    {
        var back = ((int)today.DayOfWeek - (int)DayOfWeek.Saturday + 7) % 7;
        return today.AddDays(-back + 7 * weekOffset);
    }
}

public sealed class PlanTemplatesApiClient : IPlanTemplatesApiClient
{
    private readonly OrdersHttp _http;

    public PlanTemplatesApiClient(HttpClient httpClient, LifeDrop.Mobile.Auth.IAuthSession session) =>
        _http = new OrdersHttp(httpClient ?? throw new ArgumentNullException(nameof(httpClient)), session ?? throw new ArgumentNullException(nameof(session)));

    private static string Day(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public async Task<(VisitActionResult Result, IReadOnlyList<PlanTemplateItem> Templates)> GetAsync(CancellationToken cancellationToken = default)
    {
        var (result, items) = await _http.CallAsync<List<TemplateDto>>(HttpMethod.Get, "/visit-plan/templates", null, cancellationToken);
        if (result != VisitActionResult.Success) return (result, Array.Empty<PlanTemplateItem>());
        if (items is null || items.Any(t => t is null || t.Id == Guid.Empty)) return (VisitActionResult.ServerError, Array.Empty<PlanTemplateItem>());
        return (result, items.Select(t => new PlanTemplateItem(t.Id, t.Name ?? "", t.Customers, t.Days)).ToList());
    }

    public async Task<VisitActionResult> SaveAsync(string name, DateOnly weekStart, CancellationToken cancellationToken = default) =>
        (await _http.CallAsync<object>(HttpMethod.Post, "/visit-plan/templates", new { name, weekStart = Day(weekStart) }, cancellationToken)).Result;

    public async Task<(VisitActionResult Result, PlanTemplateApplied? Applied)> ApplyAsync(Guid id, DateOnly startDate, int weeks, CancellationToken cancellationToken = default)
    {
        var (result, applied) = await _http.CallAsync<AppliedDto>(HttpMethod.Post, $"/visit-plan/templates/{id}/apply", new { startDate = Day(startDate), weeks }, cancellationToken);
        if (result != VisitActionResult.Success) return (result, null);
        return applied is null ? (VisitActionResult.ServerError, null) : (result, new PlanTemplateApplied(applied.Created, applied.Skipped, applied.Days));
    }

    public async Task<VisitActionResult> DeleteAsync(Guid id, CancellationToken cancellationToken = default) =>
        (await _http.CallAsync<object>(HttpMethod.Delete, $"/visit-plan/templates/{id}", null, cancellationToken)).Result;

    private sealed record TemplateDto(Guid Id, string? Name, int Customers, int Days);
    private sealed record AppliedDto(int Created, int Skipped, int Days);
}
