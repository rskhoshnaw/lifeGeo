using LifeDrop.Mobile.Localization;
using LifeDrop.Mobile.Visits;

namespace LifeDrop.Mobile;

// The wholesaler officer's page (plan step 5.6): choose the wholesaler, see the open orders, press "sent" when an order has gone out, and print
// the list or save it as a PDF on the phone. Online only; the server shows only the officer's own wholesalers.
public sealed class WholesalerOrdersPage : ContentPage
{
    private readonly ChoiceField _wholesaler = new(Texts.Get("app.wholesaler.title"));
    private readonly VerticalStackLayout _list = new() { Spacing = 8 };
    private readonly Button _print = new() { IsEnabled = false };
    private readonly ActivityIndicator _spinner = new() { IsVisible = false };
    private readonly Label _message = new() { IsVisible = false };
    private IReadOnlyList<WholesalerItem> _wholesalers = [];
    private IReadOnlyList<WholesalerOrderItem> _orders = [];
    private bool _busy;

    public WholesalerOrdersPage()
    {
        FlowDirection = AppLanguage.Direction;
        Title = Texts.Get("app.wholesaler.title");
        _print.Text = Texts.Get("app.wholesaler.print");
        _print.Clicked += (_, _) => PrintList();
        _wholesaler.SelectedIndexChanged += async (_, _) => await LoadOrdersAsync();
        Content = new ScrollView { Content = new VerticalStackLayout { Padding = 12, Spacing = 8, Children = { _wholesaler, _print, _spinner, _message, _list } } };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await LoadAsync();
    }

    private Guid? ChosenId => _wholesaler.SelectedIndex > 0 && _wholesaler.SelectedIndex <= _wholesalers.Count ? _wholesalers[_wholesaler.SelectedIndex - 1].Id : null;

    private async Task LoadAsync()
    {
        if (TrackingHost.WholesalerApi is not { } api)
        {
            AppLanguage.ShowRoot(new MainPage());
            return;
        }
        var chosen = ChosenId;
        var (result, wholesalers) = await CallAsync(c => api.GetWholesalersAsync(c), Array.Empty<WholesalerItem>());
        if (result != VisitActionResult.Success) return;
        _wholesalers = wholesalers;
        _wholesaler.Items = new[] { Texts.Get("app.wholesaler.all") }.Concat(wholesalers.Select(w => $"{w.Name} ({w.OpenOrders})")).ToList();
        _wholesaler.SelectedIndex = chosen is { } id ? wholesalers.ToList().FindIndex(w => w.Id == id) + 1 : 0;
        await LoadOrdersAsync();
    }

    private async Task LoadOrdersAsync()
    {
        if (TrackingHost.WholesalerApi is not { } api) return;
        var (result, orders) = await CallAsync(c => api.GetOrdersAsync(ChosenId, c), Array.Empty<WholesalerOrderItem>());
        if (result != VisitActionResult.Success) return;
        _orders = orders.Where(o => o.Status == "placed").ToList();
        _print.IsEnabled = _orders.Count > 0;
        _list.Children.Clear();
        foreach (var order in _orders) _list.Children.Add(Card(order));
        Show(_orders.Count == 0 ? Texts.Get("app.wholesaler.none") : null);
    }

    private View Card(WholesalerOrderItem order)
    {
        var lines = string.Join("\n", order.Lines.Select(l => $"{l.ProductName} × {l.Quantity:0.###} {l.Unit}"));
        var details = string.Join("\n", new[] { order.WholesalerName, order.Address, order.Phone, $"{order.Total:0.###} {order.Currency}", lines }.Where(s => !string.IsNullOrWhiteSpace(s)));
        var sent = new Button { Text = Texts.Get("app.wholesaler.dispatch") };
        sent.Clicked += async (_, _) => await DispatchAsync(order);
        return new Border
        {
            Padding = 10,
            Content = new VerticalStackLayout
            {
                Spacing = 4,
                Children = { new Label { Text = $"{order.Number} · {order.CustomerName} · {order.CreatedAt.ToLocalTime():yyyy-MM-dd}", FontAttributes = FontAttributes.Bold }, new Label { Text = details, FontSize = 12 }, sent }
            }
        };
    }

    private async Task DispatchAsync(WholesalerOrderItem order)
    {
        if (TrackingHost.WholesalerApi is not { } api) return;
        var (result, _) = await CallAsync(async c => (await api.DispatchAsync(order.Id, c), 0), 0);
        if (result != VisitActionResult.Success) return;
        await LoadAsync();
        Show(Texts.Get("app.wholesaler.dispatched"));
    }

    private void PrintList()
    {
        var headings = new WholesalerReport.Headings(Texts.Get("app.wholesaler.title"), Texts.Get("app.wholesaler.report.customer"), Texts.Get("app.wholesaler.report.address"),
            Texts.Get("app.wholesaler.report.product"), Texts.Get("app.wholesaler.report.quantity"), Texts.Get("app.wholesaler.report.date"), Texts.Get("app.wholesaler.report.price"),
            Texts.Get("app.wholesaler.report.phone"));
        var html = WholesalerReport.BuildHtml(_orders, headings, AppLanguage.Direction == FlowDirection.RightToLeft);
        if (!HtmlPrinter.Print($"{Texts.Get("app.wholesaler.title")} {DateTime.Now:yyyy-MM-dd}", html)) Show(VisitLifecycle.Describe(VisitActionResult.ServerError));
    }

    private async Task<(VisitActionResult Result, T Data)> CallAsync<T>(Func<CancellationToken, Task<(VisitActionResult, T)>> call, T empty)
    {
        if (_busy) return (VisitActionResult.NetworkError, empty);
        _busy = true;
        _spinner.IsVisible = _spinner.IsRunning = true;
        (VisitActionResult Result, T Data) outcome;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            outcome = await call(cts.Token);
        }
        catch (OperationCanceledException)
        {
            outcome = (VisitActionResult.NetworkError, empty);
        }
        _busy = false;
        _spinner.IsVisible = _spinner.IsRunning = false;
        if (outcome.Result != VisitActionResult.Success)
        {
            Show(VisitLifecycle.Describe(outcome.Result));
            if (outcome.Result == VisitActionResult.SessionExpired) AppLanguage.ShowRoot(new MainPage());
        }
        return outcome;
    }

    private void Show(string? text)
    {
        _message.Text = text;
        _message.IsVisible = !string.IsNullOrEmpty(text);
    }
}
