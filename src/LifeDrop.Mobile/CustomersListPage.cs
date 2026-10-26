using LifeDrop.Mobile.Localization;
using LifeDrop.Mobile.Visits;

namespace LifeDrop.Mobile;

// Finds a customer by name, code or phone (plan step 4.15), narrowed by type, city and neighbourhood, and "cooperates" (plan step 5.2), and opens the customer's page,
// where the order, payment, sample and follow-up buttons are. Two characters of text at least when no filter is chosen; the server limits the answer to the user's area
// and 50 rows. Online only.
public sealed class CustomersListPage : ContentPage
{
    private readonly SearchBar _search = new();
    private readonly ChoiceField _type = new(Texts.Get("app.customer.type"));
    private readonly PlacePickerView _places = new() { AllowAdd = false };
    private readonly YesNoView _cooperates = new();
    private readonly Button _show = new();
    private readonly VerticalStackLayout _results = new() { Spacing = 8 };
    private readonly ActivityIndicator _spinner = new() { IsVisible = false };
    private readonly Label _message = new() { IsVisible = false };
    private bool _placesLoaded;

    public CustomersListPage()
    {
        FlowDirection = AppLanguage.Direction;
        Title = Texts.Get("app.customers.title");
        _search.Placeholder = Texts.Get("app.customers.search");
        _search.SearchButtonPressed += async (_, _) => await SearchAsync();
        _type.Items = new[] { Texts.Get("app.customers.all") }.Concat(NewCustomerFlow.Types.Select(t => DailyPlan.TypeLabel(t.ToLowerInvariant()))).ToList();
        _show.Text = Texts.Get("app.customers.apply");
        _show.Clicked += async (_, _) => await SearchAsync();
        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = 16,
                Spacing = 12,
                Children =
                {
                    _search,
                    new CaptionedField("app.customer.type", _type),
                    new Label { Text = Texts.Get("app.customers.place"), FontAttributes = FontAttributes.Bold },
                    _places,
                    new CaptionedField("app.customer.cooperates", _cooperates),
                    _show, _spinner, _message, _results
                }
            }
        };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_placesLoaded) return;
        var api = TrackingHost.CustomersApi;
        if (api is null) return;
        _placesLoaded = true;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            await _places.LoadAsync(api, null, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // The place lists stay empty; the text search still works.
        }
    }

    private async Task SearchAsync()
    {
        var api = TrackingHost.CustomersApi;
        if (api is null)
        {
            AppLanguage.ShowRoot(new MainPage());
            return;
        }
        var text = (_search.Text ?? "").Trim();
        var type = _type.SelectedIndex > 0 ? NewCustomerFlow.Types[_type.SelectedIndex - 1] : null;
        var place = _places.PlaceId;
        var cooperates = _cooperates.Value;
        var filtered = type is not null || place is not null || cooperates is not null;
        if (text.Length < 2 && (!filtered || text.Length > 0)) return;

        _results.Children.Clear();
        _message.IsVisible = false;
        _spinner.IsVisible = _spinner.IsRunning = true;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var (result, items) = filtered
                ? await api.FilterCustomersAsync(text.Length == 0 ? null : text, type, place, cooperates, cts.Token)
                : await api.SearchCustomersAsync(text, cts.Token);
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
            if (items.Count == 0) Show(Texts.Get("app.customers.none"));
            foreach (var item in items)
            {
                var button = new Button { Text = $"{item.Code} · {item.Name}" + (string.IsNullOrWhiteSpace(item.Phone) ? "" : $" · {item.Phone}"), HorizontalOptions = LayoutOptions.Fill };
                var captured = item;
                button.Clicked += async (_, _) => await Navigation.PushAsync(new CustomerPage(captured.Id, captured.Name));
                _results.Children.Add(button);
            }
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
