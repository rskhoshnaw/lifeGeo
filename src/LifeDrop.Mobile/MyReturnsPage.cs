using System.Globalization;
using LifeDrop.Mobile.Localization;
using LifeDrop.Mobile.Visits;

namespace LifeDrop.Mobile;

// The returns the user wrote (plan step 5.54), newest first, with the decision of the sales manager; a return that still waits can be cancelled. "New return" asks for the customer first.
public sealed class MyReturnsPage : ContentPage
{
    private readonly VerticalStackLayout _list = new() { Spacing = 10 };
    private readonly Button _more = new() { IsVisible = false };
    private readonly ActivityIndicator _spinner = new() { IsVisible = false };
    private readonly Label _message = new() { IsVisible = false };
    private readonly List<MyReturn> _items = [];

    public MyReturnsPage()
    {
        FlowDirection = AppLanguage.Direction;
        Title = Texts.Get("app.return.mineTitle");
        var create = new Button { Text = Texts.Get("app.return.new") };
        create.Clicked += async (_, _) => await Navigation.PushAsync(new CustomerPickPage(async customer => await Navigation.PushAsync(new ReturnPage(customer.Id, customer.Name))));
        _more.Text = Texts.Get("app.return.more");
        _more.Clicked += async (_, _) => await LoadAsync(more: true);
        Content = new ScrollView { Content = new VerticalStackLayout { Padding = 12, Spacing = 10, Children = { create, _spinner, _message, _list, _more } } };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await LoadAsync(more: false);   // also when coming back from a new return
    }

    private async Task LoadAsync(bool more)
    {
        var api = TrackingHost.ReturnsApi;
        if (api is null)
        {
            AppLanguage.ShowRoot(new MainPage());
            return;
        }
        _spinner.IsVisible = _spinner.IsRunning = true;
        _message.IsVisible = false;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var last = more && _items.Count > 0 ? _items[^1] : null;
            var (result, page) = await api.ListMineAsync(last?.CreatedAt, last?.Id, cts.Token);
            if (result == VisitActionResult.SessionExpired)
            {
                AppLanguage.ShowRoot(new MainPage());
                return;
            }
            if (result != VisitActionResult.Success)
            {
                Show(VisitLifecycle.Describe(result));
                return;
            }
            if (!more) _items.Clear();
            _items.AddRange(page);
            Render();
            _more.IsVisible = page.Count == 50;
        }
        catch (OperationCanceledException)
        {
            Show(Texts.Get("app.timeout"));
        }
        finally
        {
            _spinner.IsVisible = _spinner.IsRunning = false;
        }
    }

    private void Render()
    {
        _list.Children.Clear();
        if (_items.Count == 0) Show(Texts.Get("app.return.none"));
        foreach (var item in _items)
        {
            var captured = item;
            var statusColor = item.Status switch { "approved" => Colors.Green, "rejected" => Colors.Red, "cancelled" => Colors.Gray, _ => Colors.DarkOrange };
            var stack = new VerticalStackLayout { Spacing = 3, Padding = 12 };
            stack.Children.Add(new Label { Text = $"{item.Number} · {item.CustomerName}", FontAttributes = FontAttributes.Bold });
            stack.Children.Add(new Label { Text = Texts.Get("app.return.status." + item.Status), TextColor = statusColor, FontAttributes = FontAttributes.Bold, FontSize = 13 });
            if (!item.PricesHidden) stack.Children.Add(new Label { Text = $"{item.Total.ToString("#,##0.###", CultureInfo.InvariantCulture)} {item.Currency}", FontSize = 13 });
            stack.Children.Add(new Label { Text = item.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), FontSize = 12, TextColor = Colors.Gray });
            foreach (var line in item.Lines)
            {
                var text = $"{line.ProductName} × {line.Quantity.ToString("0.###", CultureInfo.InvariantCulture)} {UnitText.Label(line.Unit)} · {Texts.Get("app.return.reason." + line.Reason)} → {Texts.Get("app.return.dest." + line.Destination)}";
                if (line.Decision == "rejected") text += " · " + Texts.Get("app.return.lineRejected");
                if (item.Status == "approved" && line.Decision != "rejected" && line.Destination == "warehouse" && line.StockApplied == false) text += " · " + Texts.Get("app.return.notOnShelf");
                stack.Children.Add(new Label { Text = text, FontSize = 12 });
            }
            if (!string.IsNullOrWhiteSpace(item.DecisionNote)) stack.Children.Add(new Label { Text = Texts.Get("app.return.decision", ("text", item.DecisionNote)), FontSize = 12, FontAttributes = FontAttributes.Italic });
            if (item.Status == "requested")
            {
                var cancel = new Button { Text = Texts.Get("app.return.cancel"), FontSize = 13, HorizontalOptions = LayoutOptions.Center };
                cancel.Clicked += async (_, _) => await CancelAsync(captured);
                stack.Children.Add(cancel);
            }
            _list.Children.Add(new Border { StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 12 }, Stroke = Color.FromArgb("#E2E8F0"), StrokeThickness = 1, Content = stack });
        }
    }

    private async Task CancelAsync(MyReturn item)
    {
        var reason = (await DisplayPromptAsync(Texts.Get("app.return.cancel"), Texts.Get("app.return.cancelReason"), Texts.Get("app.ok"), Texts.Get("app.cancel"), maxLength: 500))?.Trim();
        if (string.IsNullOrEmpty(reason) || TrackingHost.ReturnsApi is not { } api) return;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var result = await api.CancelAsync(item.Id, reason, cts.Token);
        if (result == VisitActionResult.SessionExpired)
        {
            AppLanguage.ShowRoot(new MainPage());
            return;
        }
        Show(result == VisitActionResult.Success ? Texts.Get("app.return.cancelled") : VisitLifecycle.Describe(result));
        await LoadAsync(more: false);
    }

    private void Show(string text)
    {
        _message.Text = text;
        _message.IsVisible = true;
    }
}
