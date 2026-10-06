using LifeDrop.Mobile.Localization;
using LifeDrop.Mobile.Visits;

namespace LifeDrop.Mobile;

// Draw an area on the map (plan step 1.27): tap to add corners, undo, clear, use. The map is a web page (Leaflet + OpenStreetMap)
// in a WebView, so it needs the internet; the rules for the shape are in AreaDrawing (tested). It starts on the phone's position
// when there is one, otherwise on Erbil.
public sealed class DrawAreaPage : ContentPage
{
    private readonly WebView _web = new() { VerticalOptions = LayoutOptions.Fill };
    private readonly Label _message = new() { IsVisible = false };
    private readonly Action<IReadOnlyList<AreaPoint>> _done;
    private bool _loaded;

    public DrawAreaPage(Action<IReadOnlyList<AreaPoint>> done)
    {
        _done = done;
        FlowDirection = AppLanguage.Direction;
        Title = Texts.Get("app.area.title");
        var undo = new Button { Text = Texts.Get("app.area.undo") };
        undo.Clicked += async (_, _) => await _web.EvaluateJavaScriptAsync("undo()");
        var clear = new Button { Text = Texts.Get("app.area.clear") };
        clear.Clicked += async (_, _) => await _web.EvaluateJavaScriptAsync("clearAll()");
        var use = new Button { Text = Texts.Get("app.area.use") };
        use.Clicked += async (_, _) => await UseAsync();
        var grid = new Grid
        {
            RowDefinitions = { new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Star), new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Auto) },
            Padding = 8,
            RowSpacing = 6
        };
        grid.Add(new Label { Text = Texts.Get("app.area.hint") }, 0, 0);
        grid.Add(_web, 0, 1);
        grid.Add(_message, 0, 2);
        grid.Add(new HorizontalStackLayout { Spacing = 8, Children = { undo, clear, use } }, 0, 3);
        Content = grid;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_loaded) return;
        _loaded = true;
        double lat = 36.19, lon = 44.01;
        var zoom = 11;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            var result = await new MauiLocationSource().GetFixAsync(cts.Token);
            if (result.Fix is { } fix) (lat, lon, zoom) = (fix.Latitude, fix.Longitude, 14);
        }
        catch (OperationCanceledException)
        {
            // The default position is used.
        }
        _web.Source = new HtmlWebViewSource { Html = AreaDrawing.MapHtml(lat, lon, zoom) };
    }

    private async Task UseAsync()
    {
        var points = AreaDrawing.Parse(await _web.EvaluateJavaScriptAsync("getPoints()"));
        if (points is null || points.Count < AreaDrawing.MinPoints)
        {
            Show(Texts.Get("app.area.tooFew"));
            return;
        }
        if (!AreaDrawing.IsValid(points))
        {
            Show(Texts.Get("app.area.invalid"));
            return;
        }
        _done(points);
        await Navigation.PopAsync();
    }

    private void Show(string text)
    {
        _message.Text = text;
        _message.IsVisible = true;
    }
}
