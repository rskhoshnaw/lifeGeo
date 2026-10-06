using LifeDrop.Mobile.Localization;
using LifeDrop.Mobile.Offline;
using LifeDrop.Mobile.Visits;

namespace LifeDrop.Mobile;

// My open follow-ups (plan step 2.8), the earliest due day first; overdue ones are marked. Tap one to start it, complete it with a result note, or cancel it
// with a reason. The server decides who may do what. Online only.
public sealed class MyFollowUpsPage : ContentPage
{
    private readonly CollectionView _list = new() { SelectionMode = SelectionMode.Single };
    private readonly ActivityIndicator _spinner = new() { IsVisible = false };
    private readonly Label _message = new() { IsVisible = false };
    private readonly HashSet<Guid> _kept = [];   // steps kept on the phone: their follow-ups are not shown again until the server has them

    public MyFollowUpsPage()
    {
        FlowDirection = AppLanguage.Direction;
        Title = Texts.Get("app.followup.title");
        _list.ItemTemplate = new DataTemplate(() =>
        {
            var title = new Label { FontAttributes = FontAttributes.Bold };
            title.SetBinding(Label.TextProperty, nameof(Row.Title));
            var details = new Label { FontSize = 12 };
            details.SetBinding(Label.TextProperty, nameof(Row.Details));
            return new Border { Padding = 10, Margin = new Thickness(0, 0, 0, 6), Content = new VerticalStackLayout { Children = { title, details } } };
        });
        _list.SelectionChanged += async (_, e) =>
        {
            if (e.CurrentSelection.FirstOrDefault() is not Row row) return;
            _list.SelectedItem = null;
            await ActAsync(row.Item);
        };
        var grid = new Grid { RowDefinitions = { new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Star) }, Padding = 12, RowSpacing = 8 };
        grid.Add(new VerticalStackLayout { Spacing = 8, Children = { _spinner, _message } }, 0, 0);
        grid.Add(_list, 0, 1);
        Content = grid;
    }

    private sealed record Row(FollowUpSummary Item, string Title, string Details);

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        var api = TrackingHost.FollowUpsApi;
        if (api is null)
        {
            AppLanguage.ShowRoot(new MainPage());
            return;
        }
        _spinner.IsVisible = _spinner.IsRunning = true;
        VisitActionResult result;
        IReadOnlyList<FollowUpSummary> items;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            (result, items) = await api.GetOpenAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            (result, items) = (VisitActionResult.NetworkError, Array.Empty<FollowUpSummary>());
        }
        _spinner.IsVisible = _spinner.IsRunning = false;
        if (result != VisitActionResult.Success)
        {
            Show(VisitLifecycle.Describe(result));
            if (result == VisitActionResult.SessionExpired) AppLanguage.ShowRoot(new MainPage());
            return;
        }
        _list.ItemsSource = items.Where(f => !_kept.Contains(f.Id)).Select(f => new Row(f, $"{(f.IsOverdue ? "⚠ " : "")}{f.Subject}",
            $"{f.CustomerName} · {f.DueDate:yyyy-MM-dd} · {Texts.Get("app.followup.status." + f.Status)}{(f.IsOverdue ? " · " + Texts.Get("app.followup.overdue") : "")}")).ToList();
        Show(items.Count == 0 ? Texts.Get("app.followup.none") : null);
    }

    private async Task ActAsync(FollowUpSummary item)
    {
        var api = TrackingHost.FollowUpsApi;
        if (api is null) return;
        var start = Texts.Get("app.followup.start");
        var complete = Texts.Get("app.followup.complete");
        var cancel = Texts.Get("app.followup.cancel");
        var choice = await DisplayActionSheet(item.Subject, Texts.Get("app.followup.close"), null, item.Status == "pending" ? [start, complete, cancel] : [complete, cancel]);
        VisitActionResult result;
        string? stepText = null;
        if (choice == start) result = await api.StartAsync(item.Id);
        else if (choice == complete)
        {
            var note = await DisplayPromptAsync(item.Subject, Texts.Get("app.followup.resultNote"), maxLength: 500);
            if (string.IsNullOrWhiteSpace(note)) return;
            stepText = note;
            result = await api.CompleteAsync(item.Id, note);
        }
        else if (choice == cancel)
        {
            var reason = await DisplayPromptAsync(item.Subject, Texts.Get("app.followup.reason"), maxLength: 500);
            if (string.IsNullOrWhiteSpace(reason)) return;
            stepText = reason;
            result = await api.CancelAsync(item.Id, reason);
        }
        else return;
        // No internet: the step is kept on the phone and sent later (plan step 2.13); a step sent twice is not a conflict.
        if (result == VisitActionResult.NetworkError && TrackingHost.WorkQueue is { } queue)
        {
            queue.Enqueue(choice == start ? WorkKind.FollowUpStart : choice == complete ? WorkKind.FollowUpComplete : WorkKind.FollowUpCancel, item.Subject, new FollowUpStepPayload(item.Id, stepText));
            _kept.Add(item.Id);
            Show(Texts.Get("app.offline.saved"));
            _list.ItemsSource = ((IEnumerable<object>?)_list.ItemsSource)?.Where(r => (r as Row)?.Item.Id != item.Id).ToList();
            return;
        }
        if (result != VisitActionResult.Success) Show(VisitLifecycle.Describe(result));
        await LoadAsync();
    }

    private void Show(string? text)
    {
        _message.Text = text;
        _message.IsVisible = !string.IsNullOrEmpty(text);
    }
}
