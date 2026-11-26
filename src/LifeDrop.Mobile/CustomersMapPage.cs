using LifeDrop.Mobile.Localization;
using LifeDrop.Mobile.Visits;

namespace LifeDrop.Mobile;

// The user's customers on a map (owner request 2026-11-26): the ones the user owns in red, the other customers of the area in grey, the phone's position in blue. A tap on a marker
// shows the name and "open", which opens the customer. The map is a web page (Leaflet + OpenStreetMap) like the area drawing, so it needs the internet.
public sealed class CustomersMapPage : ContentPage
{
    private readonly WebView _web = new() { VerticalOptions = LayoutOptions.Fill };
    private readonly ActivityIndicator _spinner = new() { IsVisible = false };
    private readonly Label _message = new() { IsVisible = false, HorizontalTextAlignment = TextAlignment.Center };
    private bool _loaded;

    public CustomersMapPage()
    {
        FlowDirection = AppLanguage.Direction;
        Title = Texts.Get("app.map.title");
        _web.Navigating += OnNavigating;
        var grid = new Grid { RowDefinitions = { new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Star) }, Padding = 0 };
        grid.Add(new VerticalStackLayout { Padding = 8, Children = { _spinner, _message } }, 0, 0);
        grid.Add(_web, 0, 1);
        Content = grid;
    }

    private async void OnNavigating(object? sender, WebNavigatingEventArgs e)
    {
        if (CustomerMapHtml.CustomerIdOf(e.Url) is not { } id) return;
        e.Cancel = true;
        await Navigation.PushAsync(new CustomerPage(id));
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_loaded) return;
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        var api = TrackingHost.CustomersApi;
        if (api is null)
        {
            AppLanguage.ShowRoot(new MainPage());
            return;
        }
        _spinner.IsVisible = _spinner.IsRunning = true;
        _message.IsVisible = false;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var (result, items) = await api.GetMapCustomersAsync(cts.Token);
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
            double? lat = null, lon = null;
            try
            {
                using var gps = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                if ((await new MauiLocationSource().GetFixAsync(gps.Token)).Fix is { } fix) (lat, lon) = (fix.Latitude, fix.Longitude);
            }
            catch (OperationCanceledException)
            {
                // the map then starts on the customers
            }
            _loaded = true;
            _web.Source = new HtmlWebViewSource { Html = CustomerMapHtml.Build(items, lat, lon, Texts.Get("app.map.open"), Texts.Get("app.map.mine"), Texts.Get("app.map.others")) };
            if (items.Count == 0) Show(Texts.Get("app.map.none"));
            else if (!items.Any(c => c.Mine)) Show(Texts.Get("app.map.noneMine"));
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
