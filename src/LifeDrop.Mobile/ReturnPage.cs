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
    private readonly Picker _order = new();
    private IReadOnlyList<ReturnableOrder> _orders = [];
    private IReadOnlyList<PricedProduct> _catalogue = [];
    private readonly List<(byte[] Data, string ContentType)> _photos = [];
    private readonly Label _photosLabel = new() { IsVisible = false, FontSize = 12 };
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
        _order.Title = Texts.Get("app.return.order");
        _order.SelectedIndexChanged += async (_, _) => await OnOrderChosenAsync();
        var take = new Button { Text = Texts.Get("app.return.photoTake"), FontSize = 13 };
        take.Clicked += async (_, _) => await AddPhotoAsync(capture: true);
        var pick = new Button { Text = Texts.Get("app.return.photoPick"), FontSize = 13 };
        pick.Clicked += async (_, _) => await AddPhotoAsync(capture: false);
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
                Children = { new Label { Text = _customerName ?? "", FontAttributes = FontAttributes.Bold, FontSize = 18 }, _order, _warehouse, _search, _spinner, _message, _rows, new HorizontalStackLayout { Spacing = 8, HorizontalOptions = LayoutOptions.Center, Children = { take, pick } }, _photosLabel, _note, _send }
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
            _catalogue = products;
            // The delivered orders of the customer: goods may come back against one (its prices, no more than was delivered); a failure here only leaves "no order".
            if (TrackingHost.ReturnsApi is { } returnsApi)
            {
                var (orderResult, orders) = await returnsApi.ListDeliveredOrdersAsync(_draft.CustomerId, cts.Token);
                if (orderResult == VisitActionResult.Success) _orders = orders;
            }
            _order.ItemsSource = new[] { Texts.Get("app.return.noOrder") }.Concat(_orders.Select(o => $"{o.Number} · {o.CreatedAt.ToLocalTime():yyyy-MM-dd}")).ToList();
            _order.SelectedIndex = 0;
            ShowCatalogue();
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
    private void AddRow(Guid productId, string code, string name, string unit, string? hint)
    {
        var details = new VerticalStackLayout { Spacing = 4, IsVisible = false };
        var reason = new Picker { Title = Texts.Get("app.return.reason"), ItemsSource = ReturnDraft.Reasons.Select(r => Texts.Get("app.return.reason." + r)).ToList(), SelectedIndex = 0 };
        var destination = new Picker { Title = Texts.Get("app.return.destination"), ItemsSource = ReturnDraft.Destinations.Select(d => Texts.Get("app.return.dest." + d)).ToList(), SelectedIndex = Array.IndexOf(ReturnDraft.Destinations, ReturnDraft.DefaultDestination("damaged")) };
        reason.SelectedIndexChanged += (_, _) =>
        {
            if (reason.SelectedIndex < 0) return;
            _draft.SetReason(productId, ReturnDraft.Reasons[reason.SelectedIndex]);
            destination.SelectedIndex = Array.IndexOf(ReturnDraft.Destinations, _draft.DestinationOf(productId));   // the usual place of this reason
            Refresh();
        };
        destination.SelectedIndexChanged += (_, _) =>
        {
            if (destination.SelectedIndex < 0) return;
            _draft.SetDestination(productId, ReturnDraft.Destinations[destination.SelectedIndex]);
            Refresh();
        };
        details.Children.Add(reason);
        details.Children.Add(destination);
        var quantity = new Entry { Keyboard = Keyboard.Numeric, WidthRequest = 90, Placeholder = Texts.Get("app.return.quantity"), HorizontalTextAlignment = TextAlignment.Center };
        quantity.TextChanged += (_, e) =>
        {
            if (!_draft.SetQuantity(productId, e.NewTextValue))
            {
                Show(Texts.Get("app.return.invalid"));
                var old = _draft.QuantityOf(productId);
                quantity.Text = old > 0 ? old.ToString("0.###", CultureInfo.InvariantCulture) : "";
                return;
            }
            details.IsVisible = _draft.QuantityOf(productId) > 0;
            if (details.IsVisible)
            {
                reason.SelectedIndex = Array.IndexOf(ReturnDraft.Reasons, _draft.ReasonOf(productId));
                destination.SelectedIndex = Array.IndexOf(ReturnDraft.Destinations, _draft.DestinationOf(productId));
            }
            Refresh();
        };
        var title = $"{code} · {name}";
        var head = new Grid { ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) }, ColumnSpacing = 8 };
        head.Add(new VerticalStackLayout { Children = { new Label { Text = title, FontAttributes = FontAttributes.Bold }, new Label { Text = hint is null ? UnitText.Label(unit) : $"{UnitText.Label(unit)} · {hint}", FontSize = 12, TextColor = Colors.Gray } } }, 0);
        head.Add(quantity, 1);
        var row = new VerticalStackLayout { Spacing = 4, Children = { head, details } };
        _byProduct[productId] = (row, title);
        _rows.Children.Add(row);
    }

    // Without an order: every product of the catalogue (today's price of the customer is used by the server).
    private void ShowCatalogue()
    {
        _rows.Children.Clear();
        _byProduct.Clear();
        foreach (var product in _catalogue) AddRow(product.ProductId, product.Code, product.DisplayName(Texts.Language), product.Unit, null);
        Filter();
    }

    // With an order: the lines of that order with what was delivered; the lines written before are dropped, they belonged to another list.
    private async Task OnOrderChosenAsync()
    {
        if (!_loaded) return;
        var index = _order.SelectedIndex - 1;
        var chosen = index >= 0 && index < _orders.Count ? _orders[index] : null;
        if (chosen?.Id == _draft.OrderId) return;
        _draft.ChooseOrder(chosen?.Id);
        Refresh();
        if (chosen is null)
        {
            ShowCatalogue();
            return;
        }
        var api = TrackingHost.ReturnsApi;
        if (api is null) return;
        _spinner.IsVisible = _spinner.IsRunning = true;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var (result, lines) = await api.GetOrderLinesAsync(chosen.Id, cts.Token);
            if (result != VisitActionResult.Success)
            {
                Show(result == VisitActionResult.ServerError ? Texts.Get("app.return.orderFailed") : VisitLifecycle.Describe(result));
                _order.SelectedIndex = 0;
                return;
            }
            _rows.Children.Clear();
            _byProduct.Clear();
            foreach (var line in lines)
                AddRow(line.ProductId, line.Code, line.Name, line.Unit, Texts.Get("app.return.delivered", ("quantity", line.Delivered.ToString("0.###", CultureInfo.InvariantCulture))));
            Filter();
        }
        catch (OperationCanceledException)
        {
            Show(Texts.Get("app.timeout"));
            _order.SelectedIndex = 0;
        }
        finally
        {
            _spinner.IsVisible = _spinner.IsRunning = false;
        }
    }

    private async Task AddPhotoAsync(bool capture)
    {
        if (_photos.Count >= ReturnDraft.MaxPhotos) return;
        try
        {
            FileResult? file = capture ? (MediaPicker.Default.IsCaptureSupported ? await MediaPicker.Default.CapturePhotoAsync() : null) : await MediaPicker.Default.PickPhotoAsync();
            if (file is null) return;
            var (data, contentType, errorKey) = await FeedbackPhotos.PrepareAsync(file);   // made smaller first (1600 px JPEG), the same as for feedback
            if (data is null || contentType is null)
            {
                Show(Texts.Get(errorKey ?? "app.feedback.photoFailed"));
                return;
            }
            _photos.Add((data, contentType));
            _photosLabel.Text = Texts.Get("app.return.photosAdded", ("count", _photos.Count.ToString(CultureInfo.InvariantCulture)), ("max", ReturnDraft.MaxPhotos.ToString(CultureInfo.InvariantCulture)));
            _photosLabel.IsVisible = true;
        }
        catch (Exception)
        {
            Show(Texts.Get("app.feedback.photoFailed"));
        }
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
            var (result, created) = await api.CreateAsync(_draft.CustomerId, _draft.OrderId, _draft.WarehouseId, _draft.ClientRef, _draft.Note, _draft.Lines(), cts.Token);
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
            // The pictures follow the return (it must exist first); a picture that does not go is reported, the return itself is already sent.
            var failed = 0;
            foreach (var (data, contentType) in _photos)
                if (await api.AddPhotoAsync(created!.Id, data, contentType, cts.Token) != VisitActionResult.Success) failed++;
            await DisplayAlertAsync(Texts.Get("app.return.title"), Texts.Get("app.return.sent", ("number", created!.Number)) + (failed > 0 ? "\n" + Texts.Get("app.return.photoFailed", ("count", failed.ToString(CultureInfo.InvariantCulture))) : ""), Texts.Get("app.ok"));
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
