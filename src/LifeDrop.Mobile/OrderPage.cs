using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using LifeDrop.Mobile.Localization;
using LifeDrop.Mobile.Offline;
using LifeDrop.Mobile.Visits;

namespace LifeDrop.Mobile;

// One product of the order list: the quantity box is two-way, so the total follows every key.
public sealed class OrderRow(PricedProduct product, string title, string price) : INotifyPropertyChanged
{
    private string _quantity = "";
    public PricedProduct Product { get; } = product;
    public string Title { get; } = title;
    public string Price { get; } = price;
    public string Quantity
    {
        get => _quantity;
        set
        {
            if (_quantity == value) return;
            _quantity = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Quantity)));
        }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}

// Place an order for a customer, optionally for a visit (plan step 2.2): pick the warehouse, type quantities, place. Prices come from the
// server and are shown only; the order carries no price. Online only. The logic is in OrderDraft (tested).
public sealed class OrderPage : ContentPage
{
    private readonly OrderDraft _draft;
    private readonly string? _customerName;
    private readonly Picker _warehouse = new();
    private readonly Entry _search = new();
    private readonly ObservableCollection<OrderRow> _rows = [];
    private readonly Dictionary<Guid, OrderRow> _byProduct = [];
    private readonly Editor _note = new() { AutoSize = EditorAutoSizeOption.TextChanges, MaxLength = OrderDraft.MaxNoteLength, HeightRequest = 70 };
    private readonly Label _total = new() { FontAttributes = FontAttributes.Bold };
    private readonly Button _place = new() { IsEnabled = false };
    private readonly ActivityIndicator _spinner = new() { IsVisible = false };
    private readonly Label _message = new() { IsVisible = false };
    private bool _loaded;

    public OrderPage(Guid customerId, string? customerName, Guid? visitId = null)
    {
        _draft = new OrderDraft(customerId, visitId);
        _customerName = customerName;
        FlowDirection = AppLanguage.Direction;
        Title = Texts.Get("app.order.title");
        _warehouse.Title = Texts.Get("app.order.chooseWarehouse");
        _warehouse.SelectedIndexChanged += (_, _) =>
        {
            _draft.WarehouseId = _warehouse.SelectedIndex >= 0 && _warehouse.SelectedIndex < _draft.Warehouses.Count ? _draft.Warehouses[_warehouse.SelectedIndex].Id : null;
            Refresh();
        };
        _search.Placeholder = Texts.Get("app.order.search");
        _search.TextChanged += (_, _) => ShowProducts();
        _note.Placeholder = Texts.Get("app.order.note");
        _note.TextChanged += (_, _) =>
        {
            _draft.Note = _note.Text;
            Refresh();
        };
        _place.Text = Texts.Get("app.order.place");
        _place.Clicked += async (_, _) => await PlaceAsync();

        var list = new CollectionView
        {
            ItemsSource = _rows,
            SelectionMode = SelectionMode.None,
            ItemTemplate = new DataTemplate(() =>
            {
                var title = new Label { FontAttributes = FontAttributes.Bold };
                title.SetBinding(Label.TextProperty, nameof(OrderRow.Title));
                var price = new Label { FontSize = 12 };
                price.SetBinding(Label.TextProperty, nameof(OrderRow.Price));
                var quantity = new Entry { Keyboard = Keyboard.Numeric, WidthRequest = 90, Placeholder = Texts.Get("app.order.quantity"), HorizontalTextAlignment = TextAlignment.Center };
                quantity.SetBinding(Entry.TextProperty, new Binding(nameof(OrderRow.Quantity), BindingMode.TwoWay));
                quantity.TextChanged += OnQuantityChanged;
                var grid = new Grid { ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) }, ColumnSpacing = 8, Padding = new Thickness(0, 4) };
                grid.Add(new VerticalStackLayout { Children = { title, price } }, 0);
                grid.Add(quantity, 1);
                return grid;
            })
        };

        var header = new VerticalStackLayout
        {
            Spacing = 8,
            Children = { new Label { Text = _customerName ?? "", FontAttributes = FontAttributes.Bold, FontSize = 18 }, _warehouse, _search, _spinner, _message, new Label { Text = Texts.Get("app.order.prices"), FontSize = 12 } }
        };
        var footer = new VerticalStackLayout { Spacing = 8, Children = { _note, _total, _place } };
        var grid2 = new Grid
        {
            RowDefinitions = { new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Star), new RowDefinition(GridLength.Auto) },
            Padding = 12,
            RowSpacing = 8
        };
        grid2.Add(header, 0, 0);
        grid2.Add(list, 0, 1);
        grid2.Add(footer, 0, 2);
        Content = grid2;
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
        SetBusy(true);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var (priceResult, products) = await api.GetPricesAsync(_draft.CustomerId, IraqTime.Today(TimeProvider.System), cts.Token);
            var (warehouseResult, warehouses) = await api.GetWarehousesAsync(cts.Token);
            var failed = priceResult != VisitActionResult.Success ? priceResult : warehouseResult;
            if (failed == VisitActionResult.Success)
            {
                // What the phone keeps for working without internet (plan step 2.13): the catalogue without prices and the warehouses.
                TrackingHost.Cache?.SaveProducts(products);
                TrackingHost.Cache?.SaveWarehouses(warehouses);
            }
            else if (failed == VisitActionResult.NetworkError && TrackingHost.Cache is { } cache && cache.Products.Count > 0 && cache.Warehouses.Count > 0)
            {
                // No internet: the order is written from the phone's lists; the prices are set by the server when the order arrives.
                (products, warehouses) = (cache.Products, cache.Warehouses);
                _draft.PricesKnown = false;
                failed = VisitActionResult.Success;
            }
            if (failed != VisitActionResult.Success)
            {
                _loaded = false;   // the next time the page appears it tries again
                Show(VisitLifecycle.Describe(failed));
                if (failed == VisitActionResult.SessionExpired) AppLanguage.ShowRoot(new MainPage());
                return;
            }
            _draft.Load(products, warehouses);
        }
        catch (OperationCanceledException)
        {
            _loaded = false;
            Show(Texts.Get("app.timeout"));
            return;
        }
        finally
        {
            SetBusy(false);
        }
        _warehouse.ItemsSource = _draft.Warehouses.Select(w => $"{w.Code} · {w.Name}").ToList();
        if (_draft.WarehouseId is { } id) _warehouse.SelectedIndex = _draft.Warehouses.ToList().FindIndex(w => w.Id == id);
        ShowProducts();
        Show(!_draft.PricesKnown ? Texts.Get("app.order.offline") : _draft.Warehouses.Count == 0 ? Texts.Get("app.order.noWarehouse") : _draft.Products.Count == 0 ? Texts.Get("app.order.noProducts") : null);
    }

    private void ShowProducts()
    {
        var language = Texts.Language;
        _rows.Clear();
        foreach (var p in _draft.Find(_search.Text, language))
        {
            if (!_byProduct.TryGetValue(p.ProductId, out var row))
            {
                var discount = !_draft.PricesKnown ? "" : p.Price < p.ListPrice ? $" ({Format(p.ListPrice)} → {Format(p.Price)})" : $" {Format(p.Price)}";
                row = new OrderRow(p, $"{p.Code} · {p.DisplayName(language)}", $"{p.Unit}{discount} {p.Currency}");
                if (_draft.QuantityOf(p.ProductId) > 0) row.Quantity = _draft.QuantityOf(p.ProductId).ToString(CultureInfo.InvariantCulture);
                _byProduct[p.ProductId] = row;
            }
            _rows.Add(row);
        }
        Refresh();
    }

    private void OnQuantityChanged(object? sender, TextChangedEventArgs e)
    {
        if ((sender as BindableObject)?.BindingContext is not OrderRow row) return;
        // Text that is not a quantity goes back to what the draft has (the draft keeps the last good value).
        if (!_draft.SetQuantity(row.Product.ProductId, e.NewTextValue))
        {
            var old = _draft.QuantityOf(row.Product.ProductId);
            row.Quantity = old > 0 ? old.ToString(CultureInfo.InvariantCulture) : "";
        }
        Refresh();
    }

    private void Refresh()
    {
        _total.Text = _draft.Lines.Count == 0 ? "" : !_draft.PricesKnown ? Texts.Get("app.order.priceLater") : Texts.Get("app.order.total", ("total", Format(_draft.Total)), ("currency", _draft.Currency));
        _place.IsEnabled = _draft.CanPlace;
        if (_draft.MixedCurrency) Show(VisitLifecycle.Describe(VisitActionResult.CurrencyMismatch));
        else if (_message.Text == VisitLifecycle.Describe(VisitActionResult.CurrencyMismatch)) Show(null);
    }

    private async Task PlaceAsync()
    {
        var api = TrackingHost.OrdersApi;
        if (api is null) return;
        SetBusy(true);
        VisitActionResult result;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            result = await _draft.PlaceAsync(api, cts.Token);
        }
        catch (OperationCanceledException)
        {
            result = VisitActionResult.NetworkError;   // the same reference is sent again on the next try
        }
        SetBusy(false);
        // No internet: the order is kept on the phone and sent later, with the same reference (plan step 2.13).
        if (result == VisitActionResult.NetworkError && TrackingHost.WorkQueue is { } queue)
        {
            queue.Enqueue(WorkKind.Order, $"{Texts.Get("app.order.title")}: {_customerName}", _draft.ToPayload(), null);
            _draft.Queued = true;
            await DisplayAlertAsync(Texts.Get("app.order.title"), Texts.Get("app.offline.saved"), Texts.Get("app.ok"));
            await Navigation.PopAsync();
            return;
        }
        if (result != VisitActionResult.Success)
        {
            Show(VisitLifecycle.Describe(result));
            if (result == VisitActionResult.SessionExpired) AppLanguage.ShowRoot(new MainPage());
            return;
        }
        var order = _draft.Placed!;
        await DisplayAlertAsync(Texts.Get("app.order.title"), Texts.Get("app.order.placed", ("number", order.Number), ("total", Format(order.Total)), ("currency", order.Currency)), Texts.Get("app.ok"));
        await Navigation.PopAsync();
    }

    private static string Format(decimal value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private void Show(string? text)
    {
        _message.Text = text;
        _message.IsVisible = !string.IsNullOrEmpty(text);
    }

    private void SetBusy(bool busy)
    {
        _spinner.IsVisible = _spinner.IsRunning = busy;
        _place.IsEnabled = !busy && _draft.CanPlace;
        _warehouse.IsEnabled = _search.IsEnabled = !busy;
    }
}
