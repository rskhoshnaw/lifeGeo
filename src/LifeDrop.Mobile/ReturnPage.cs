using System.Globalization;
using LifeDrop.Mobile.Localization;
using LifeDrop.Mobile.Visits;

namespace LifeDrop.Mobile;

// Goods back from a customer (plan step 5.54): choose the products, type the quantity, say why and where the goods go (back to the warehouse, or damaged/expired apart), send. The sales manager approves
// it. Prices are the server's, so only products, quantities, reasons and places are sent. Online only. The rules are in ReturnDraft (tested).
public sealed class ReturnPage : ContentPage
{
    private readonly ReturnDraft _draft;
    private readonly string? _customerName;
    private readonly Picker _warehouse = new();
    private readonly SearchBar _search = new();
    private readonly VerticalStackLayout _rows = new() { Spacing = 8 };
    private readonly Editor _note = new() { AutoSize = EditorAutoSizeOption.TextChanges, MaxLength = 500, HeightRequest = 70 };
    private readonly Button _send = new() { IsEnabled = false };
    private readonly ActivityIndicator _spinner = new() { IsVisible = false };
    private readonly Label _message = new() { IsVisible = false };
    private readonly Dictionary<Guid, (View Row, string Text)> _byProduct = [];
    private bool _loaded;

    public ReturnPage(Guid customerId, string? customerName)
    {
        _draft = new ReturnDraft(customerId);
        _customerName = customerName;
        FlowDirection = AppLanguage.Direction;
        Title = Texts.Get("app.return.title");
        _warehouse.Title = Texts.Get("app.return.warehouse");
        _warehouse.SelectedIndexChanged += (_, _) =>
        {
            _draft.WarehouseId = _warehouse.SelectedIndex >= 0 && _warehouse.SelectedIndex < _draft.Warehouses.Count ? _draft.Warehouses[_warehouse.SelectedIndex].Id : null;
            Refresh();
        };
        _search.Placeholder = Texts.Get("app.return.search");
        _search.TextChanged += (_, _) => Filter();
        _note.Placeholder = Texts.Get("app.return.note");
        _note.TextChanged += (_, _) => _draft.Note = _note.Text;
        _send.Text = Texts.Get("app.return.send");
        _send.Clicked += async (_, _) => await SendAsync();
        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = 12,
                Spacing = 8,
                Children = { new Label { Text = _customerName ?? "", FontAttributes = FontAttributes.Bold, FontSize = 18 }, _warehouse, _search, _spinner, _message, _rows, _note, _send }
            }
        };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_loaded) return;
        var api = TrackingHost.OrdersApi;
        if (api is null)
        {
            AppLanguage.ShowRoot(new MainPage());
            return;
        }
        _loaded = true;
        _spinner.IsVisible = _spinner.IsRunning = true;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var (priceResult, products) = await api.GetPricesAsync(_draft.CustomerId, IraqTime.Today(TimeProvider.System), cts.Token);
            var (warehouseResult, warehouses) = await api.GetWarehousesAsync(cts.Token);
            var failed = priceResult != VisitActionResult.Success ? priceResult : warehouseResult;
            if (failed != VisitActionResult.Success)
            {
                _loaded = false;   // the next time the page appears it tries again
                Show(VisitLifecycle.Describe(failed));
                if (failed == VisitActionResult.SessionExpired) AppLanguage.ShowRoot(new MainPage());
                return;
            }
            _draft.Load(warehouses);
            _warehouse.ItemsSource = warehouses.Select(w => $"{w.Code} · {w.Name}").ToList();
            if (_draft.WarehouseId is { } id) _warehouse.SelectedIndex = warehouses.ToList().FindIndex(w => w.Id == id);
            foreach (var product in products) AddRow(product);
        }
        catch (OperationCanceledException)
        {
            _loaded = false;
            Show(Texts.Get("app.timeout"));
        }
        finally
        {
            _spinner.IsVisible = _spinner.IsRunning = false;
        }
        Refresh();
    }

    // One row per product, built once: name and quantity; the reason and the place appear when a quantity is typed.
    private void AddRow(PricedProduct product)
    {
        var details = new VerticalStackLayout { Spacing = 4, IsVisible = false };
        var reason = new Picker { Title = Texts.Get("app.return.reason"), ItemsSource = ReturnDraft.Reasons.Select(r => Texts.Get("app.return.reason." + r)).ToList(), SelectedIndex = 0 };
        var destination = new Picker { Title = Texts.Get("app.return.destination"), ItemsSource = ReturnDraft.Destinations.Select(d => Texts.Get("app.return.dest." + d)).ToList(), SelectedIndex = Array.IndexOf(ReturnDraft.Destinations, ReturnDraft.DefaultDestination("damaged")) };
        reason.SelectedIndexChanged += (_, _) =>
        {
            if (reason.SelectedIndex < 0) return;
            _draft.SetReason(product.ProductId, ReturnDraft.Reasons[reason.SelectedIndex]);
            destination.SelectedIndex = Array.IndexOf(ReturnDraft.Destinations, _draft.DestinationOf(product.ProductId));   // the usual place of this reason
            Refresh();
        };
        destination.SelectedIndexChanged += (_, _) =>
        {
            if (destination.SelectedIndex < 0) return;
            _draft.SetDestination(product.ProductId, ReturnDraft.Destinations[destination.SelectedIndex]);
            Refresh();
        };
        details.Children.Add(reason);
        details.Children.Add(destination);
        var quantity = new Entry { Keyboard = Keyboard.Numeric, WidthRequest = 90, Placeholder = Texts.Get("app.return.quantity"), HorizontalTextAlignment = TextAlignment.Center };
        quantity.TextChanged += (_, e) =>
        {
            if (!_draft.SetQuantity(product.ProductId, e.NewTextValue))
            {
                Show(Texts.Get("app.return.invalid"));
                var old = _draft.QuantityOf(product.ProductId);
                quantity.Text = old > 0 ? old.ToString("0.###", CultureInfo.InvariantCulture) : "";
                return;
            }
            details.IsVisible = _draft.QuantityOf(product.ProductId) > 0;
            if (details.IsVisible)
            {
                reason.SelectedIndex = Array.IndexOf(ReturnDraft.Reasons, _draft.ReasonOf(product.ProductId));
                destination.SelectedIndex = Array.IndexOf(ReturnDraft.Destinations, _draft.DestinationOf(product.ProductId));
            }
            Refresh();
        };
        var title = $"{product.Code} · {product.DisplayName(Texts.Language)}";
        var head = new Grid { ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) }, ColumnSpacing = 8 };
        head.Add(new VerticalStackLayout { Children = { new Label { Text = title, FontAttributes = FontAttributes.Bold }, new Label { Text = UnitText.Label(product.Unit), FontSize = 12, TextColor = Colors.Gray } } }, 0);
        head.Add(quantity, 1);
        var row = new VerticalStackLayout { Spacing = 4, Children = { head, details } };
        _byProduct[product.ProductId] = (row, title);
        _rows.Children.Add(row);
    }

    private void Filter()
    {
        var text = (_search.Text ?? "").Trim();
        foreach (var (row, title) in _byProduct.Values) row.IsVisible = text.Length == 0 || title.Contains(text, StringComparison.OrdinalIgnoreCase);
    }

    private void Refresh()
    {
        _send.IsEnabled = _draft.CanSend;
        if (_draft.LineCount == 0) Show(Texts.Get("app.return.noLines"));
        else if (_draft.NeedsWarehouse && _draft.WarehouseId is null) Show(Texts.Get("app.return.needWarehouse"));
        else _message.IsVisible = false;
    }

    private async Task SendAsync()
    {
        var api = TrackingHost.ReturnsApi;
        if (api is null)
        {
            AppLanguage.ShowRoot(new MainPage());
            return;
        }
        if (!_draft.CanSend) return;
        _send.IsEnabled = false;
        _spinner.IsVisible = _spinner.IsRunning = true;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var (result, created) = await api.CreateAsync(_draft.CustomerId, null, _draft.WarehouseId, _draft.ClientRef, _draft.Note, _draft.Lines(), cts.Token);
            if (result == VisitActionResult.SessionExpired)
            {
                AppLanguage.ShowRoot(new MainPage());
                return;
            }
            if (result != VisitActionResult.Success)
            {
                Show(VisitLifecycle.Describe(result));   // the same reference is kept, so pressing again after a lost answer never makes a second return
                return;
            }
            await DisplayAlertAsync(Texts.Get("app.return.title"), Texts.Get("app.return.sent", ("number", created!.Number)), Texts.Get("app.ok"));
            await Navigation.PopAsync();
        }
        catch (OperationCanceledException)
        {
            Show(Texts.Get("app.timeout"));
        }
        finally
        {
            _spinner.IsVisible = _spinner.IsRunning = false;
            _send.IsEnabled = _draft.CanSend;
        }
    }

    private void Show(string? text)
    {
        _message.Text = text;
        _message.IsVisible = !string.IsNullOrEmpty(text);
    }
}
