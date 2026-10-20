using System.Globalization;
using LifeDrop.Mobile.Localization;
using LifeDrop.Mobile.Visits;

namespace LifeDrop.Mobile;

// The product catalogue (plan step 4.43): photo, name, specialty, manufacturer, expiry, stock and (unless the server hid it) the price. The list is loaded once from the server;
// search and the three filters (specialty, in or out of stock, manufacturer) work on the phone. Online only.
public sealed class ProductsPage : ContentPage
{
    private readonly SearchBar _search = new();
    private readonly Picker _specialty = new();
    private readonly Picker _stock = new();
    private readonly Picker _manufacturer = new();
    private readonly VerticalStackLayout _results = new() { Spacing = 10 };
    private readonly ActivityIndicator _spinner = new() { IsVisible = false };
    private readonly Label _message = new() { IsVisible = false };
    private IReadOnlyList<ProductItem> _all = Array.Empty<ProductItem>();
    private IReadOnlyList<string> _specialtyKeys = Array.Empty<string>();
    private IReadOnlyList<string> _manufacturers = Array.Empty<string>();
    private bool _loaded;
    private bool _filling;

    public ProductsPage()
    {
        FlowDirection = AppLanguage.Direction;
        Title = Texts.Get("app.products.title");
        _search.Placeholder = Texts.Get("app.products.search");
        _search.TextChanged += (_, _) => Render();
        _specialty.Title = Texts.Get("app.products.specialty");
        _manufacturer.Title = Texts.Get("app.products.manufacturer");
        _stock.Title = Texts.Get("app.products.stock");
        _stock.ItemsSource = new List<string> { Texts.Get("app.products.stockAll"), Texts.Get("app.products.inStock"), Texts.Get("app.products.outOfStock") };
        _stock.SelectedIndex = 0;
        _specialty.SelectedIndexChanged += (_, _) => Render();
        _manufacturer.SelectedIndexChanged += (_, _) => Render();
        _stock.SelectedIndexChanged += (_, _) => Render();
        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = 16,
                Spacing = 10,
                Children = { _search, _specialty, _stock, _manufacturer, _spinner, _message, _results }
            }
        };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_loaded) return;
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        var api = TrackingHost.ProductsApi;
        if (api is null)
        {
            AppLanguage.ShowRoot(new MainPage());
            return;
        }
        _message.IsVisible = false;
        _spinner.IsVisible = _spinner.IsRunning = true;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var (result, items) = await api.GetProductsAsync(cts.Token);
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
            _loaded = true;
            FillPickers();
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

    private void FillPickers()
    {
        _filling = true;
        _specialtyKeys = ProductFilter.Specialties(_all);
        _specialty.ItemsSource = new[] { Texts.Get("app.products.specialtyAll") }.Concat(_specialtyKeys.Select(k => Texts.Get("specialty." + k))).ToList();
        _specialty.SelectedIndex = 0;
        _manufacturers = ProductFilter.Manufacturers(_all);
        _manufacturer.ItemsSource = new[] { Texts.Get("app.products.manufacturerAll") }.Concat(_manufacturers).ToList();
        _manufacturer.SelectedIndex = 0;
        _filling = false;
    }

    private void Render()
    {
        if (_filling || !_loaded) return;
        var specialty = _specialty.SelectedIndex > 0 && _specialty.SelectedIndex - 1 < _specialtyKeys.Count ? _specialtyKeys[_specialty.SelectedIndex - 1] : null;
        var manufacturer = _manufacturer.SelectedIndex > 0 && _manufacturer.SelectedIndex - 1 < _manufacturers.Count ? _manufacturers[_manufacturer.SelectedIndex - 1] : null;
        var stock = _stock.SelectedIndex switch { 1 => StockFilter.InStock, 2 => StockFilter.OutOfStock, _ => StockFilter.All };
        var shown = ProductFilter.Apply(_all, _search.Text, specialty, stock, manufacturer);
        _results.Children.Clear();
        _message.IsVisible = false;
        if (shown.Count == 0) Show(Texts.Get("app.products.none"));
        var language = Texts.Language;
        var api = TrackingHost.ProductsApi;
        foreach (var product in shown)
        {
            var lines = new VerticalStackLayout { Spacing = 2, HorizontalOptions = LayoutOptions.Fill };
            lines.Children.Add(new Label { Text = $"{product.Code} · {product.DisplayName(language)}", FontAttributes = FontAttributes.Bold });
            var details = new List<string>();
            if (!string.IsNullOrWhiteSpace(product.Specialty)) details.Add(Texts.Get("specialty." + product.Specialty));
            if (!string.IsNullOrWhiteSpace(product.Manufacturer)) details.Add(product.Manufacturer!);
            if (product.ExpiryDate is { } expiry) details.Add(Texts.Get("app.products.expires", ("date", expiry.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))));
            if (details.Count > 0) lines.Children.Add(new Label { Text = string.Join(" · ", details), FontSize = 13 });
            var stockText = product.StockQty is null ? null : product.InStock ? Texts.Get("app.products.inStockQty", ("qty", product.StockQty.Value.ToString("0.###", CultureInfo.InvariantCulture))) : Texts.Get("app.products.outOfStock");
            var price = product.PricesHidden ? null : $"{product.BasePrice.ToString("0.###", CultureInfo.InvariantCulture)} {product.Currency} / {product.Unit}";
            var summary = string.Join(" · ", new[] { stockText, price }.Where(x => !string.IsNullOrEmpty(x)));
            if (summary.Length > 0) lines.Children.Add(new Label { Text = summary, FontSize = 13 });
            if (!string.IsNullOrWhiteSpace(product.Notes)) lines.Children.Add(new Label { Text = product.Notes, FontSize = 12, TextColor = Colors.Gray });
            var row = new HorizontalStackLayout { Spacing = 10 };
            if (api?.PhotoUrl(product.PhotoKey) is { } url)
                row.Children.Add(new Image { Source = ImageSource.FromUri(new Uri(url)), WidthRequest = 56, HeightRequest = 56, Aspect = Aspect.AspectFill });
            row.Children.Add(lines);
            _results.Children.Add(row);
        }
    }

    private void Show(string text)
    {
        _message.Text = text;
        _message.IsVisible = true;
    }
}
