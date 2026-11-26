using System.Globalization;
using LifeDrop.Mobile.Localization;
using LifeDrop.Mobile.Visits;

namespace LifeDrop.Mobile;

// The product gallery (plan step 4.43, redone as cards 2026-11-23): two cards per row, photo on top, then name, scientific name, expiry day and price.
// A tap opens ProductDetailPage with everything entered for the product. Search and the three filters (specialty, stock, manufacturer) work on the
// phone. Taking orders is a separate page (OrderEntryPage): this page only shows products. Online only.
public sealed class ProductsPage : ContentPage
{
    private readonly SearchBar _search = new();
    private readonly Picker _specialty = new();
    private readonly Picker _stock = new();
    private readonly Picker _manufacturer = new();
    private readonly Grid _gallery = new() { ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Star) }, ColumnSpacing = 12, RowSpacing = 12 };
    private readonly ActivityIndicator _spinner = new() { IsVisible = false };
    private readonly Label _message = new() { IsVisible = false, HorizontalTextAlignment = TextAlignment.Center };
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
        var filters = new Grid { ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Star) }, ColumnSpacing = 6 };
        filters.Add(_specialty, 0);
        filters.Add(_stock, 1);
        filters.Add(_manufacturer, 2);
        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = new Thickness(12, 8, 12, 24),
                Spacing = 10,
                Children = { _search, filters, _spinner, _message, _gallery }
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
        _gallery.Children.Clear();
        _gallery.RowDefinitions.Clear();
        _message.IsVisible = false;
        if (shown.Count == 0) Show(Texts.Get("app.products.none"));
        var today = DateOnly.FromDateTime(DateTime.Today);
        for (var i = 0; i < shown.Count; i++)
        {
            if (i % 2 == 0) _gallery.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            _gallery.Add(Card(shown[i], today, shown, i), i % 2, i / 2);
        }
    }

    private View Card(ProductItem product, DateOnly today, IReadOnlyList<ProductItem> shown, int index)
    {
        var lines = new VerticalStackLayout { Spacing = 3, Padding = new Thickness(10, 8, 10, 10) };
        lines.Children.Add(new Label { Text = product.DisplayName(Texts.Language), FontAttributes = FontAttributes.Bold, FontSize = 14, MaxLines = 2, LineBreakMode = LineBreakMode.TailTruncation });
        lines.Children.Add(new Label
        {
            Text = string.IsNullOrWhiteSpace(product.ScientificName) ? " " : product.ScientificName,   // a blank line keeps the cards of one row the same height
            FontSize = 12, FontAttributes = FontAttributes.Italic, TextColor = Colors.Gray, MaxLines = 1, LineBreakMode = LineBreakMode.TailTruncation
        });
        if (product.ExpiryDate is { } expiry)
        {
            var status = product.ExpiryStatusOn(today);
            lines.Children.Add(new Label
            {
                Text = status == ExpiryStatus.Expired ? Texts.Get("app.products.expired") + " · " + Day(expiry) : Texts.Get("app.products.expires", ("date", Day(expiry))),
                FontSize = 12,
                TextColor = status switch { ExpiryStatus.Expired => Colors.Red, ExpiryStatus.Soon => Colors.DarkOrange, _ => Colors.Gray }
            });
        }
        else lines.Children.Add(new Label { Text = " ", FontSize = 12 });
        lines.Children.Add(new Label
        {
            Text = product.PricesHidden ? " " : $"{Money(product.BasePrice)} {product.Currency}",
            FontSize = 15, FontAttributes = FontAttributes.Bold, TextColor = Color.FromArgb("#E52B50")
        });
        var card = new VerticalStackLayout { Spacing = 0, Children = { Photo(product, 130), lines } };
        var border = new Border
        {
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 14 },
            Stroke = Color.FromArgb("#E2E8F0"),
            StrokeThickness = 1,
            BackgroundColor = Colors.White,
            Padding = 0,
            Content = card,
            Opacity = product.Available ? 1 : 0.6
        };
        var tap = new TapGestureRecognizer();
        tap.Tapped += async (_, _) => await Navigation.PushAsync(new ProductDetailPage(shown, index));
        border.GestureRecognizers.Add(tap);
        return border;
    }

    // The photo on top of a card or the detail page; a grey tile when the product has none.
    internal static View Photo(ProductItem product, double height)
    {
        if (TrackingHost.ProductsApi?.PhotoUrl(product.PhotoKey) is { } url)
            return new Image { Source = ImageSource.FromUri(new Uri(url)), HeightRequest = height, Aspect = Aspect.AspectFit, BackgroundColor = Color.FromArgb("#F1F5F9") };
        return new Grid
        {
            HeightRequest = height,
            BackgroundColor = Color.FromArgb("#F1F5F9"),
            Children = { new Label { Text = "💊", FontSize = height / 3, HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center, Opacity = 0.5 } }
        };
    }

    internal static string Day(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    internal static string Money(decimal value) => value.ToString("#,##0.###", CultureInfo.InvariantCulture);

    private void Show(string text)
    {
        _message.Text = text;
        _message.IsVisible = true;
    }
}
