using LifeDrop.Mobile.Localization;
using LifeDrop.Mobile.Offline;
using LifeDrop.Mobile.Visits;

namespace LifeDrop.Mobile;

// What is kept on the phone for the internet (plan step 2.13): waiting items, items the server refused ("needs attention", with the reason; the user may send them again or drop them),
// and what was accepted in the last two days. Visit steps have their own list on the visit page.
public sealed class PendingWorkPage : ContentPage
{
    private readonly CollectionView _list = new() { SelectionMode = SelectionMode.Single };
    private readonly Label _message = new() { IsVisible = false };
    private readonly ActivityIndicator _spinner = new() { IsVisible = false };

    public PendingWorkPage()
    {
        FlowDirection = AppLanguage.Direction;
        Title = Texts.Get("app.pending.title");
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
        var send = new Button { Text = Texts.Get("app.pending.sendNow") };
        send.Clicked += async (_, _) =>
        {
            _spinner.IsVisible = _spinner.IsRunning = true;
            await TrackingHost.SyncQuietlyAsync();
            _spinner.IsVisible = _spinner.IsRunning = false;
            Load();
        };
        var grid = new Grid { RowDefinitions = { new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Star) }, Padding = 12, RowSpacing = 8 };
        grid.Add(new VerticalStackLayout { Spacing = 8, Children = { send, _spinner, _message } }, 0, 0);
        grid.Add(_list, 0, 1);
        Content = grid;
    }

    private sealed record Row(WorkItem Item, string Title, string Details);

    protected override void OnAppearing()
    {
        base.OnAppearing();
        Load();
    }

    private void Load()
    {
        var items = TrackingHost.WorkQueue?.Items ?? Array.Empty<WorkItem>();
        _list.ItemsSource = items.OrderBy(i => i.State == SyncState.Rejected ? 0 : i.State == SyncState.Accepted ? 2 : 1).ThenByDescending(i => i.CreatedAt)
            .Select(i => new Row(i, i.Title, i.State switch
            {
                SyncState.Rejected => $"{Texts.Get("app.pending.needsAttention")}: {VisitLifecycle.Describe(i.Answer ?? VisitActionResult.InvalidData)}",
                SyncState.Accepted => Texts.Get("app.pending.accepted"),
                _ => Texts.Get("app.pending.waiting")
            } + $" · {i.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm}")).ToList();
        _message.Text = items.Count == 0 ? Texts.Get("app.pending.none") : null;
        _message.IsVisible = items.Count == 0;
    }

    private async Task ActAsync(WorkItem item)
    {
        if (TrackingHost.WorkQueue is not { } queue || item.State == SyncState.Accepted) return;
        var retry = Texts.Get("app.pending.retry");
        var drop = Texts.Get("app.pending.drop");
        var choice = await DisplayActionSheet(item.Title, Texts.Get("app.followup.close"), null, item.State == SyncState.Rejected ? [retry, drop] : [drop]);
        if (choice == retry && queue.Retry(item.Id)) await TrackingHost.SyncQuietlyAsync();
        else if (choice == drop) queue.Drop(item.Id);
        Load();
    }
}
