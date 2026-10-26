using LifeDrop.Mobile.Localization;
using LifeDrop.Mobile.Visits;

namespace LifeDrop.Mobile;

// The visitor's own targets (plan step 5.5): for a month, every product with its target, what was delivered of the visitor's orders and how far that is. Months go back and up to
// this one. Read only; the managers set the targets in the panel. Online only.
public sealed class MyTargetsPage : ContentPage
{
    private readonly Button _previous = new() { Text = "◀", WidthRequest = 56 };
    private readonly Button _next = new() { Text = "▶", WidthRequest = 56 };
    private readonly Label _monthLabel = new() { FontAttributes = FontAttributes.Bold, FontSize = 18, VerticalOptions = LayoutOptions.Center, HorizontalOptions = LayoutOptions.Center };
    private readonly VerticalStackLayout _list = new() { Spacing = 8 };
    private readonly ActivityIndicator _spinner = new() { IsVisible = false };
    private readonly Label _message = new() { IsVisible = false };
    private DateOnly _month = TargetProgress.FirstOfMonth(IraqTime.Today(TimeProvider.System));
    private bool _loaded;

    public MyTargetsPage()
    {
        FlowDirection = AppLanguage.Direction;
        Title = Texts.Get("app.targets.title");
        _previous.Clicked += async (_, _) => await ShiftAsync(-1);
        _next.Clicked += async (_, _) => await ShiftAsync(1);
        var bar = new Grid { ColumnDefinitions = { new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) } };
        bar.Add(_previous);
        bar.Add(_monthLabel, 1);
        bar.Add(_next, 2);
        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = 16,
                Spacing = 12,
                Children = { bar, new Label { Text = Texts.Get("app.targets.note"), FontSize = 12 }, _spinner, _message, _list }
            }
        };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_loaded) return;
        _loaded = true;
        await LoadAsync();
    }

    private async Task ShiftAsync(int by)
    {
        _month = TargetProgress.Shift(_month, by);
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        var today = IraqTime.Today(TimeProvider.System);
        _monthLabel.Text = _month.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);
        _previous.IsEnabled = TargetProgress.CanGoBack(_month, today);
        _next.IsEnabled = TargetProgress.CanGoNext(_month, today);
        var api = TrackingHost.CustomersApi;
        if (api is null)
        {
            AppLanguage.ShowRoot(new MainPage());
            return;
        }
        _list.Children.Clear();
        _message.IsVisible = false;
        _spinner.IsVisible = _spinner.IsRunning = true;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var (result, rows) = await api.GetMyTargetsAsync(TargetProgress.MonthPath(_month), cts.Token);
            if (result == VisitActionResult.SessionExpired)
            {
                AppLanguage.ShowRoot(new MainPage());
                return;
            }
            if (result != VisitActionResult.Success)
            {
                Show(Texts.Get("app.targets.loadFailed"));
                return;
            }
            if (rows.Count == 0) Show(Texts.Get("app.targets.none"));
            foreach (var row in rows.OrderBy(r => r.ProductCode, StringComparer.Ordinal)) _list.Children.Add(Card(row));
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

    private static string Number(decimal value) => value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    private static Border Card(MyTargetRow row)
    {
        var stack = new VerticalStackLayout { Spacing = 4 };
        stack.Children.Add(new Label { Text = $"{row.ProductCode} · {row.ProductName}", FontAttributes = FontAttributes.Bold });
        stack.Children.Add(new Label { Text = row.Target is { } target ? Texts.Get("app.targets.target", ("value", Number(target)), ("unit", row.Unit)) : Texts.Get("app.targets.noTarget") });
        stack.Children.Add(new Label { Text = Texts.Get("app.targets.delivered", ("value", Number(row.Actual)), ("unit", row.Unit)) });
        if (TargetProgress.Percent(row.Target, row.Actual) is { } percent)
        {
            stack.Children.Add(new ProgressBar { Progress = TargetProgress.Fill(row.Target, row.Actual) });
            stack.Children.Add(new Label { Text = $"{percent}%", FontAttributes = FontAttributes.Bold });
        }
        return new Border { Padding = 12, StrokeThickness = 1, Stroke = Colors.Gray, StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 8 }, Content = stack };
    }

    private void Show(string text)
    {
        _message.Text = text;
        _message.IsVisible = true;
    }
}
