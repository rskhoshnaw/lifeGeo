using LifeDrop.Mobile.Localization;
using LifeDrop.Mobile.Visits;

namespace LifeDrop.Mobile;

// A new follow-up for a customer (plan step 2.8): a subject and a due day. It is for the user who makes it. Online only. The logic is in FollowUpDraft (tested).
public sealed class FollowUpPage : ContentPage
{
    private readonly FollowUpDraft _draft;
    private readonly Entry _subject = new() { MaxLength = FollowUpDraft.MaxSubjectLength };
    private readonly DatePicker _due = new();
    private readonly Button _save = new() { IsEnabled = false };
    private readonly ActivityIndicator _spinner = new() { IsVisible = false };
    private readonly Label _message = new() { IsVisible = false };
    private bool _busy;

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3));   // Iraq time

    public FollowUpPage(Guid customerId, string? customerName)
    {
        _draft = new FollowUpDraft(customerId) { DueDate = Today.AddDays(7) };
        FlowDirection = AppLanguage.Direction;
        Title = Texts.Get("app.followup.new");
        _subject.Placeholder = Texts.Get("app.followup.subject");
        _subject.TextChanged += (_, e) => { _draft.Subject = e.NewTextValue; Refresh(); };
        _due.MinimumDate = Today.ToDateTime(TimeOnly.MinValue);
        _due.Date = _draft.DueDate!.Value.ToDateTime(TimeOnly.MinValue);
        _due.DateSelected += (_, e) => { _draft.DueDate = e.NewDate is { } day ? DateOnly.FromDateTime(day) : null; Refresh(); };
        _save.Text = Texts.Get("app.followup.save");
        _save.Clicked += async (_, _) => await SaveAsync();
        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = 16,
                Spacing = 10,
                Children =
                {
                    new Label { Text = customerName ?? "", FontAttributes = FontAttributes.Bold, FontSize = 18 },
                    _subject,
                    new Label { Text = Texts.Get("app.followup.due") },
                    _due, _save, _spinner, _message
                }
            }
        };
    }

    private void Refresh() => _save.IsEnabled = !_busy && _draft.CanSave(Today);

    private void Show(string? text)
    {
        _message.Text = text;
        _message.IsVisible = !string.IsNullOrEmpty(text);
    }

    private async Task SaveAsync()
    {
        var api = TrackingHost.FollowUpsApi;
        if (api is null || _busy) return;
        _busy = true;
        _spinner.IsVisible = _spinner.IsRunning = true;
        Refresh();
        Show(null);
        VisitActionResult result;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            result = await api.CreateAsync(_draft, cts.Token);
        }
        catch (OperationCanceledException)
        {
            result = VisitActionResult.NetworkError;
        }
        _busy = false;
        _spinner.IsVisible = _spinner.IsRunning = false;
        if (result == VisitActionResult.Success) _draft.Created = true;
        Show(result == VisitActionResult.Success ? Texts.Get("app.followup.saved") : VisitLifecycle.Describe(result));
        if (result == VisitActionResult.SessionExpired) AppLanguage.ShowRoot(new MainPage());
        Refresh();
    }
}
