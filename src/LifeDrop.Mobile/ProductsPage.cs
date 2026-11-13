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
    private readonly Button _customerButton = new();
    private CustomerListItem? _customer;
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
        _customerButton.Text = Texts.Get("app.products.pickCustomer");
        _customerButton.Clicked += async (_, _) => await Navigation.PushAsync(new CustomerPickPage(customer =>
        {
            _customer = customer;
            _customerButton.Text = Texts.Get("app.products.orderFor", ("customer", customer.Name));
        }));
        _specialty.SelectedIndexChanged += (_, _) => Render();
        _manufacturer.SelectedIndexChanged += (_, _) => Render();
        _stock.SelectedIndexChanged += (_, _) => Render();
        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = 16,
                Spacing = 10,
                Children = { _customerButton, _search, _specialty, _stock, _manufacturer, _spinner, _message, _results }
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
            // Photo, name, scientific name, expiry day, price (owner request 2026-11-13).
            var lines = new VerticalStackLayout { Spacing = 2, HorizontalOptions = LayoutOptions.Fill };
            lines.Children.Add(new Label { Text = product.DisplayName(language), FontAttributes = FontAttributes.Bold });
            if (!string.IsNullOrWhiteSpace(product.ScientificName)) lines.Children.Add(new Label { Text = product.ScientificName, FontSize = 13, FontAttributes = FontAttributes.Italic });
            if (product.ExpiryDate is { } expiry) lines.Children.Add(new Label { Text = Texts.Get("app.products.expires", ("date", expiry.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))), FontSize = 13 });
            var price = product.PricesHidden ? null : $"{product.BasePrice.ToString("0.###", CultureInfo.InvariantCulture)} {product.Currency} / {product.Unit}";
            if (price is not null) lines.Children.Add(new Label { Text = price, FontSize = 13, FontAttributes = FontAttributes.Bold });
            if (product.StockQty is not null && !product.InStock) lines.Children.Add(new Label { Text = Texts.Get("app.products.outOfStock"), FontSize = 12, TextColor = Colors.Gray });
            if (!product.Available) lines.Children.Add(new Label { Text = Texts.Get("app.products.unavailable"), FontSize = 13, FontAttributes = FontAttributes.Bold, TextColor = Colors.OrangeRed });
            var order = new Button { Text = Texts.Get("app.products.order"), FontSize = 13, HorizontalOptions = LayoutOptions.Start, IsEnabled = product.Available };
            var captured = product;
            order.Clicked += async (_, _) => await OrderAsync(captured);
            lines.Children.Add(order);
            var row = new HorizontalStackLayout { Spacing = 10 };
            if (api?.PhotoUrl(product.PhotoKey) is { } url)
                row.Children.Add(new Image { Source = ImageSource.FromUri(new Uri(url)), WidthRequest = 72, HeightRequest = 72, Aspect = Aspect.AspectFill });
            row.Children.Add(lines);
            _results.Children.Add(row);
        }
    }

    // The order page for the chosen customer with this product already in the list; without a customer the user is asked to choose one first.
    private async Task OrderAsync(ProductItem product)
    {
        if (_customer is not { } customer)
        {
            await DisplayAlertAsync(Texts.Get("app.products.title"), Texts.Get("app.products.needCustomer"), Texts.Get("app.ok"));
            return;
        }
        await Navigation.PushAsync(new OrderPage(customer.Id, customer.Name, null, product.Id));
    }

    private void Show(string text)
    {
        _message.Text = text;
        _message.IsVisible = true;
    }
}
