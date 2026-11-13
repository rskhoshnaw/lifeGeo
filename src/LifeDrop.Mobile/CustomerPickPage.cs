using LifeDrop.Mobile.Localization;
using LifeDrop.Mobile.Visits;

namespace LifeDrop.Mobile;

// Choose one customer by name, code or phone (owner request 2026-11-13: order from the product list). The list opens with the
// first customers of the user's area; typing two characters or more searches. The chosen customer goes back through the callback.
public sealed class CustomerPickPage : ContentPage
{
    private readonly Action<CustomerListItem> _picked;
    private readonly SearchBar _search = new();
    private readonly VerticalStackLayout _results = new() { Spacing = 6 };
    private readonly Button _more = new() { IsVisible = false };
    private readonly ActivityIndicator _spinner = new() { IsVisible = false };
    private readonly Label _message = new() { IsVisible = false };
    private readonly List<CustomerListItem> _items = new();
    private CustomerListFilter _filter = new();
    private bool _loaded;

    public CustomerPickPage(Action<CustomerListItem> picked)
    {
        _picked = picked;
        FlowDirection = AppLanguage.Direction;
        Title = Texts.Get("app.products.pickCustomer");
        _search.Placeholder = Texts.Get("app.customers.search");
        _search.SearchButtonPressed += async (_, _) => await LoadAsync(more: false);
        _more.Text = Texts.Get("app.customers.more");
        _more.Clicked += async (_, _) => await LoadAsync(more: true);
        Content = new ScrollView { Content = new VerticalStackLayout { Padding = 16, Spacing = 10, Children = { _search, _spinner, _message, _results, _more } } };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_loaded) return;
        _loaded = true;
        await LoadAsync(more: false);
    }

    private async Task LoadAsync(bool more)
    {
        var api = TrackingHost.CustomersApi;
        if (api is null)
        {
            AppLanguage.ShowRoot(new MainPage());
            return;
        }
        if (!more)
        {
            var text = (_search.Text ?? "").Trim();
            if (text.Length == 1) return;   // the server wants two characters at least
            _filter = new CustomerListFilter(text.Length == 0 ? null : text);
            _items.Clear();
            _results.Children.Clear();
        }
        _message.IsVisible = false;
        _more.IsVisible = false;
        _spinner.IsVisible = _spinner.IsRunning = true;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var (result, page) = await api.ListCustomersAsync(_filter, more && _items.Count > 0 ? _items[^1] : null, cts.Token);
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
            foreach (var item in page)
            {
                _items.Add(item);
                var captured = item;
                var button = new Button { Text = $"{item.Code} · {item.Name}" + (string.IsNullOrWhiteSpace(item.Phone) ? "" : $" · {item.Phone}"), HorizontalOptions = LayoutOptions.Fill };
                button.Clicked += async (_, _) =>
                {
                    _picked(captured);
                    await Navigation.PopAsync();
                };
                _results.Children.Add(button);
            }
            if (_items.Count == 0) Show(Texts.Get("app.customers.none"));
            _more.IsVisible = page.Count == 50;   // a full page: there may be more
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

    private void Show(string text)
    {
        _message.Text = text;
        _message.IsVisible = true;
    }
}
