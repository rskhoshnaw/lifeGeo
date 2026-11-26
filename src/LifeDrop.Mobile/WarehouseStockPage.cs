using System.Globalization;
using LifeDrop.Mobile.Localization;
using LifeDrop.Mobile.Visits;

namespace LifeDrop.Mobile;

// Count the stock of the warehouses the user works in (owner request 2026-11-13): choose the warehouse, find a product, type the quantity, save. Online only;
// the server lists and accepts only the user's own warehouses.
public sealed class WarehouseStockPage : ContentPage
{
    private readonly Picker _warehouse = new();
    private readonly SearchBar _search = new();
    private readonly VerticalStackLayout _rows = new() { Spacing = 6 };
    private readonly ActivityIndicator _spinner = new() { IsVisible = false };
    private readonly Label _message = new() { IsVisible = false };
    private IReadOnlyList<WarehouseStockItem> _all = Array.Empty<WarehouseStockItem>();
    private List<Guid> _warehouseIds = new();
    private bool _filling;
    private bool _cards = true;   // photos in two columns (default) or the plain list
    private readonly Button _viewButton = new() { FontSize = 13, HorizontalOptions = LayoutOptions.Start };

    public WarehouseStockPage()
    {
        FlowDirection = AppLanguage.Direction;
        Title = Texts.Get("app.stock.title");
        _warehouse.Title = Texts.Get("app.order.chooseWarehouse");
        _warehouse.SelectedIndexChanged += (_, _) => Render();
        _search.Placeholder = Texts.Get("app.products.search");
        _search.TextChanged += (_, _) => Render();
        _viewButton.Clicked += (_, _) =>
        {
            _cards = !_cards;
            Render();
        };
        Content = new ScrollView { Content = new VerticalStackLayout { Padding = 12, Spacing = 8, Children = { _warehouse, _search, _viewButton, _spinner, _message, _rows } } };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_all.Count == 0) await LoadAsync();
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
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var (result, items) = await api.GetStockAsync(cts.Token);
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
            _all = items;
            _filling = true;
            var warehouses = items.GroupBy(i => i.WarehouseId).Select(g => (Id: g.Key, Name: g.First().WarehouseName)).ToList();
            _warehouseIds = warehouses.Select(w => w.Id).ToList();
            _warehouse.ItemsSource = warehouses.Select(w => w.Name).ToList();
            _filling = false;
            if (warehouses.Count > 0) _warehouse.SelectedIndex = 0;
            Show(warehouses.Count == 0 ? Texts.Get("app.stock.none") : null);
            Render();
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
        if (_filling) return;
        _rows.Children.Clear();
        _viewButton.Text = Texts.Get(_cards ? "web.catalog.table" : "web.catalog.gallery");
        if (_warehouse.SelectedIndex < 0 || _warehouse.SelectedIndex >= _warehouseIds.Count) return;
        var id = _warehouseIds[_warehouse.SelectedIndex];
        var text = (_search.Text ?? "").Trim();
        var shown = _all.Where(i => i.WarehouseId == id && (text.Length == 0 || i.ProductCode.Contains(text, StringComparison.OrdinalIgnoreCase) || i.ProductName.Contains(text, StringComparison.OrdinalIgnoreCase))).ToList();
        if (_cards) RenderCards(shown);
        else RenderList(shown);
    }

    // The plain list: code and name, the unit under it, the quantity box and the save button.
    private void RenderList(IReadOnlyList<WarehouseStockItem> shown)
    {
        foreach (var item in shown)
        {
            var (quantity, save) = Controls(item);
            var title = new Label { Text = $"{item.ProductCode} · {item.ProductName}", FontAttributes = FontAttributes.Bold };
            var status = new Label { Text = item.Counted ? UnitText.Label(item.Unit) : Texts.Get("app.stock.notCounted"), FontSize = 12 };
            var grid = new Grid { ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Auto) }, ColumnSpacing = 8 };
            grid.Add(new VerticalStackLayout { Children = { title, status } }, 0);
            grid.Add(quantity, 1);
            grid.Add(save, 2);
            _rows.Children.Add(grid);
        }
    }

    // Cards, two to a row: the photo whole on top, the name, what is free (not taken by open orders), the quantity box and the save button.
    private void RenderCards(IReadOnlyList<WarehouseStockItem> shown)
    {
        var grid = new Grid { ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Star) }, ColumnSpacing = 10, RowSpacing = 10 };
        var today = DateOnly.FromDateTime(DateTime.Today);
        for (var i = 0; i < shown.Count; i++)
        {
            var item = shown[i];
            if (i % 2 == 0) grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var (quantity, save) = Controls(item);
            var product = new ProductItem(item.ProductId, item.ProductCode, item.ProductName, null, null, null, item.Unit, "", 0m, null, null, item.ExpiryDate, item.Quantity, item.Quantity > 0, null, item.PhotoKey, true);
            var info = new VerticalStackLayout { Spacing = 3, Padding = new Thickness(8, 6, 8, 8) };
            info.Children.Add(new Label { Text = item.ProductName, FontAttributes = FontAttributes.Bold, FontSize = 14, MaxLines = 2, LineBreakMode = LineBreakMode.TailTruncation });
            info.Children.Add(new Label { Text = $"{item.ProductCode} · {(item.Counted ? UnitText.Label(item.Unit) : Texts.Get("app.stock.notCounted"))}", FontSize = 12, TextColor = Colors.Gray });
            if (item.ExpiryDate is { } expiry && product.ExpiryStatusOn(today) is ExpiryStatus.Expired or ExpiryStatus.Soon)
                info.Children.Add(new Label { Text = ProductsPage.Day(expiry), FontSize = 12, TextColor = product.ExpiryStatusOn(today) == ExpiryStatus.Expired ? Colors.Red : Colors.DarkOrange });
            info.Children.Add(new Grid { ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) }, ColumnSpacing = 6, Children = { quantity, SetColumn(save) } });
            var card = new Border
            {
                StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 14 },
                Stroke = Color.FromArgb("#E2E8F0"),
                StrokeThickness = 1,
                BackgroundColor = Colors.White,
                Padding = 0,
                Content = new VerticalStackLayout { Spacing = 0, Children = { ProductsPage.Photo(product, 110), info } }
            };
            grid.Add(card, i % 2, i / 2);
        }
        _rows.Children.Add(grid);
    }

    private static View SetColumn(View view)
    {
        Grid.SetColumn(view, 1);
        return view;
    }

    private (Entry Quantity, Button Save) Controls(WarehouseStockItem item)
    {
        var quantity = new Entry { Keyboard = Keyboard.Numeric, WidthRequest = 90, Text = item.Quantity.ToString("0.###", CultureInfo.InvariantCulture), HorizontalTextAlignment = TextAlignment.Center };
        var save = new Button { Text = Texts.Get("app.stock.save"), FontSize = 13 };
        save.Clicked += async (_, _) => await SaveAsync(item, quantity, save);
        return (quantity, save);
    }

    private async Task SaveAsync(WarehouseStockItem item, Entry box, Button button)
    {
        var api = TrackingHost.WarehouseApi;
        if (api is null) return;
        // Digits of any script and a comma or dot are accepted as in the order form; anything else is refused.
        if (OrderDraft.ParseQuantity(box.Text) is not { } quantity)
        {
            Show(Texts.Get("app.stock.invalid"));
            return;
        }
        button.IsEnabled = false;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var result = await api.SetStockAsync(item.WarehouseId, item.ProductId, quantity, cts.Token);
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
            _all = _all.Select(i => i.WarehouseId == item.WarehouseId && i.ProductId == item.ProductId ? i with { Quantity = quantity, Counted = true } : i).ToList();
            Show(Texts.Get("app.stock.saved", ("name", item.ProductName)));
        }
        catch (OperationCanceledException)
        {
            Show(Texts.Get("app.timeout"));
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private void Show(string? text)
    {
        _message.Text = text;
        _message.IsVisible = !string.IsNullOrEmpty(text);
    }
}
