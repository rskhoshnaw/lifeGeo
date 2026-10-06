using System.Globalization;
using LifeDrop.Mobile.Auth;

namespace LifeDrop.Mobile.Visits;

public sealed record FollowUpSummary(Guid Id, string CustomerName, string Subject, DateOnly DueDate, string Status, bool IsOverdue, string? CreatedByEmail);

// Follow-ups from the phone (plan step 2.8): make one for a customer, see mine, start, complete with a result note, or cancel with a reason. Online only.
public interface IFollowUpsApiClient
{
    // The open ones (pending, in progress) of the user, the earliest due day first.
    Task<(VisitActionResult Result, IReadOnlyList<FollowUpSummary> Items)> GetOpenAsync(CancellationToken cancellationToken = default);
    // The draft's ClientRef stays the same for every retry, so a lost answer never makes two follow-ups.
    Task<VisitActionResult> CreateAsync(FollowUpDraft draft, CancellationToken cancellationToken = default);
    Task<VisitActionResult> StartAsync(Guid id, CancellationToken cancellationToken = default);
    Task<VisitActionResult> CompleteAsync(Guid id, string resultNote, CancellationToken cancellationToken = default);
    Task<VisitActionResult> CancelAsync(Guid id, string reason, CancellationToken cancellationToken = default);
}

// The follow-up a field user is typing: a subject and a due day (not before today). The customer comes from the page.
public sealed class FollowUpDraft(Guid customerId, Guid? clientRef = null)
{
    public const int MaxSubjectLength = 200;

    public Guid CustomerId { get; } = customerId;
    public Guid ClientRef { get; } = clientRef ?? Guid.NewGuid();
    // Kept on the phone to be sent when the internet returns (plan step 2.13).
    public bool Queued { get; set; }
    public string? Subject { get; set; }
    public DateOnly? DueDate { get; set; }
    public bool Created { get; set; }

    public bool CanSave(DateOnly today) => !Created && !Queued && !string.IsNullOrWhiteSpace(Subject) && Subject.Trim().Length <= MaxSubjectLength && DueDate is { } due && due >= today;
}

public sealed class FollowUpsApiClient : IFollowUpsApiClient
{
    private readonly OrdersHttp _http;

    public FollowUpsApiClient(HttpClient httpClient, IAuthSession session) =>
        _http = new OrdersHttp(httpClient ?? throw new ArgumentNullException(nameof(httpClient)), session ?? throw new ArgumentNullException(nameof(session)));

    public async Task<(VisitActionResult Result, IReadOnlyList<FollowUpSummary> Items)> GetOpenAsync(CancellationToken cancellationToken = default)
    {
        var (result, items) = await _http.CallAsync<List<FollowUpDto>>(HttpMethod.Get, "/followups?limit=200", null, cancellationToken);
        if (result != VisitActionResult.Success) return (result, Array.Empty<FollowUpSummary>());
        if (items is null || items.Any(f => f is null || f.Id == Guid.Empty || string.IsNullOrWhiteSpace(f.Status) || !DateOnly.TryParseExact(f.DueDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)))
            return (VisitActionResult.ServerError, Array.Empty<FollowUpSummary>());
        return (result, items.Where(f => f.Status is "pending" or "in_progress")
            .Select(f => new FollowUpSummary(f.Id, f.CustomerName ?? "", f.Subject ?? "", DateOnly.ParseExact(f.DueDate!, "yyyy-MM-dd", CultureInfo.InvariantCulture), f.Status!, f.IsOverdue, f.CreatedByEmail)).ToList());
    }

    public async Task<VisitActionResult> CreateAsync(FollowUpDraft draft, CancellationToken cancellationToken = default)
    {
        if (!draft.CanSave(DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3)))) return VisitActionResult.InvalidData;
        return (await _http.CallAsync<object>(HttpMethod.Post, "/followups", new
        {
            customerId = draft.CustomerId,
            subject = draft.Subject!.Trim(),
            dueDate = draft.DueDate!.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            clientRef = draft.ClientRef
        }, cancellationToken)).Result;
    }

    public async Task<VisitActionResult> StartAsync(Guid id, CancellationToken cancellationToken = default) =>
        (await _http.CallAsync<object>(HttpMethod.Post, $"/followups/{id}/start", new { }, cancellationToken)).Result;

    public async Task<VisitActionResult> CompleteAsync(Guid id, string resultNote, CancellationToken cancellationToken = default) =>
        string.IsNullOrWhiteSpace(resultNote) ? VisitActionResult.InvalidData : (await _http.CallAsync<object>(HttpMethod.Post, $"/followups/{id}/complete", new { resultNote = resultNote.Trim() }, cancellationToken)).Result;

    public async Task<VisitActionResult> CancelAsync(Guid id, string reason, CancellationToken cancellationToken = default) =>
        string.IsNullOrWhiteSpace(reason) ? VisitActionResult.InvalidData : (await _http.CallAsync<object>(HttpMethod.Post, $"/followups/{id}/cancel", new { reason = reason.Trim() }, cancellationToken)).Result;

    private sealed record FollowUpDto(Guid Id, string? CustomerName, string? Subject, string? DueDate, string? Status, bool IsOverdue, string? CreatedByEmail);
}
