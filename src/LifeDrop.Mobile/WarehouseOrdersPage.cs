using LifeDrop.Mobile.Localization;
using LifeDrop.Mobile.Visits;

namespace LifeDrop.Mobile;

// The orders of the warehouses the user works in (plan step 2.3): the open ones, or one status. Tap one to work on it. Online only; the server
// shows only the user's own warehouses (a user without a warehouse sees an empty list or "not assigned").
public sealed class WarehouseOrdersPage : ContentPage
{
    private static readonly string[] Filters = ["", "placed", "preparing", "ready", "handed_over"];

    private readonly Picker _filter = new();
    private readonly CollectionView _list = new() { SelectionMode = SelectionMode.Single };
    private readonly ActivityIndicator _spinner = new() { IsVisible = false };
    private readonly Label _message = new() { IsVisible = false };

    public WarehouseOrdersPage()
    {
        FlowDirection = AppLanguage.Direction;
        Title = Texts.Get("app.warehouse.title");
        _filter.ItemsSource = Filters.Select(f => f == "" ? Texts.Get("app.warehouse.open") : Texts.Get("app.warehouse.status." + f)).ToList();
        _filter.SelectedIndex = 0;
        _filter.SelectedIndexChanged += async (_, _) => await LoadAsync();
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
            await Navigation.PushAsync(new WarehouseOrderPage(row.Order.Id));
        };
        var grid = new Grid { RowDefinitions = { new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Star) }, Padding = 12, RowSpacing = 8 };
        grid.Add(new VerticalStackLayout { Spacing = 8, Children = { _filter, _spinner, _message } }, 0, 0);
        grid.Add(_list, 0, 1);
        Content = grid;
    }

    private sealed record Row(WarehouseOrderSummary Order, string Title, string Details);

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await LoadAsync();   // also after coming back from an order, so the new status shows
    }

    private async Task LoadAsync()
    {
        var api = TrackingHost.WarehouseApi;
        if (api is null)
        {
            AppLanguage.ShowRoot(new MainPage());
            return;
        }
        _spinner.IsVisible = _spinner.IsRunning = true;
        VisitActionResult result;
        IReadOnlyList<WarehouseOrderSummary> orders;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            (result, orders) = await api.GetOrdersAsync(Filters[Math.Max(0, _filter.SelectedIndex)], cts.Token);
        }
        catch (OperationCanceledException)
        {
            (result, orders) = (VisitActionResult.NetworkError, Array.Empty<WarehouseOrderSummary>());
        }
        _spinner.IsVisible = _spinner.IsRunning = false;
        if (result != VisitActionResult.Success)
        {
            Show(VisitLifecycle.Describe(result));
            if (result == VisitActionResult.SessionExpired) AppLanguage.ShowRoot(new MainPage());
            return;
        }
        _list.ItemsSource = orders.Select(o => new Row(o, $"{o.Number} · {o.CustomerName}", $"{o.WarehouseName} · {Texts.Get("app.warehouse.status." + o.Status)} · {o.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm}")).ToList();
        Show(orders.Count == 0 ? Texts.Get("app.warehouse.none") : null);
    }

    private void Show(string? text)
    {
        _message.Text = text;
        _message.IsVisible = !string.IsNullOrEmpty(text);
    }
}
