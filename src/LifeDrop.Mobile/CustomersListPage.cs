using LifeDrop.Mobile.Localization;
using LifeDrop.Mobile.Visits;

namespace LifeDrop.Mobile;

// Finds a customer by name, code or phone (plan step 4.15) and opens the customer's page, where the order, payment, sample and follow-up buttons are.
// Two characters at least; the server limits the answer to the user's area and 50 rows. Online only.
public sealed class CustomersListPage : ContentPage
{
    private readonly SearchBar _search = new();
    private readonly VerticalStackLayout _results = new() { Spacing = 8 };
    private readonly ActivityIndicator _spinner = new() { IsVisible = false };
    private readonly Label _message = new() { IsVisible = false };

    public CustomersListPage()
    {
        FlowDirection = AppLanguage.Direction;
        Title = Texts.Get("app.customers.title");
        _search.Placeholder = Texts.Get("app.customers.search");
        _search.SearchButtonPressed += async (_, _) => await SearchAsync();
        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = 16,
                Spacing = 12,
                Children = { _search, _spinner, _message, _results }
            }
        };
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
        if (text.Length < 2) return;

        _results.Children.Clear();
        _message.IsVisible = false;
        _spinner.IsVisible = _spinner.IsRunning = true;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var (result, items) = await api.SearchCustomersAsync(text, cts.Token);
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
