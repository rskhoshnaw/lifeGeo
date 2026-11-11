using LifeDrop.Mobile.Localization;
using LifeDrop.Mobile.Visits;

namespace LifeDrop.Mobile;

// The visitor's own KPIs (plan step 5.29): for a month, each number next to the month before and the same month a year earlier with the change (green = better, red = worse), and
// below them the last 12 months. The numbers are the ones of docs/KPI.md for this user only. Months go back and up to this one. Read only. Online only.
public sealed class MyKpiPage : ContentPage
{
    private readonly Button _previous = new() { Text = "◀", WidthRequest = 56 };
    private readonly Button _next = new() { Text = "▶", WidthRequest = 56 };
    private readonly Label _monthLabel = new() { FontAttributes = FontAttributes.Bold, FontSize = 18, VerticalOptions = LayoutOptions.Center, HorizontalOptions = LayoutOptions.Center };
    private readonly VerticalStackLayout _list = new() { Spacing = 8 };
    private readonly ActivityIndicator _spinner = new() { IsVisible = false };
    private readonly Label _message = new() { IsVisible = false };
    private DateOnly _month = TargetProgress.FirstOfMonth(IraqTime.Today(TimeProvider.System));
    private bool _loaded;

    public MyKpiPage()
    {
        FlowDirection = AppLanguage.Direction;
        Title = Texts.Get("app.kpi.title");
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
                Children = { bar, new Label { Text = Texts.Get("app.kpi.note"), FontSize = 12 }, _spinner, _message, _list }
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
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var (result, data) = await api.GetMyKpisAsync(TargetProgress.MonthPath(_month), cts.Token);
            if (result == VisitActionResult.SessionExpired)
            {
                AppLanguage.ShowRoot(new MainPage());
                return;
            }
            if (result != VisitActionResult.Success || data is null)
            {
                Show(Texts.Get("app.kpi.loadFailed"));
                return;
            }
            foreach (var metric in MyKpi.Metrics) _list.Children.Add(Card(metric, data));
            _list.Children.Add(new Label { Text = Texts.Get("app.kpi.trend"), FontAttributes = FontAttributes.Bold, FontSize = 16, Margin = new Thickness(0, 8, 0, 0) });
            foreach (var period in data.Trend ?? [])
                _list.Children.Add(new Label
                {
                    Text = Texts.Get("app.kpi.trendLine",
                        ("month", period.Month), ("completed", period.Counts.VisitsCompleted.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                        ("planned", period.Counts.VisitsPlanned.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                        ("rate", MyKpi.Format(MyKpi.Metrics.First(m => m.Id == "completion"), period.Rates.VisitCompletion)),
                        ("customers", period.Counts.NewCustomers.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                        ("orders", period.Counts.Orders.ToString(System.Globalization.CultureInfo.InvariantCulture))),
                    FontAttributes = period.Month == data.Month ? FontAttributes.Bold : FontAttributes.None,
                });
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

    private static Border Card(KpiMetric metric, MyKpiData data)
    {
        var current = metric.Value(data.Current);
        var stack = new VerticalStackLayout { Spacing = 4 };
        stack.Children.Add(new Label { Text = Texts.Get(metric.TextKey), FontAttributes = FontAttributes.Bold });
        stack.Children.Add(new Label { Text = MyKpi.Format(metric, current), FontSize = 22, FontAttributes = FontAttributes.Bold });
        stack.Children.Add(Line(metric, Texts.Get("app.kpi.previousMonth", ("month", data.Previous.Month)), current, metric.Value(data.Previous)));
        stack.Children.Add(Line(metric, Texts.Get("app.kpi.lastYearMonth", ("month", data.LastYear.Month)), current, metric.Value(data.LastYear)));
        return new Border { Padding = 12, StrokeThickness = 1, Stroke = Colors.Gray, StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 8 }, Content = stack };
    }

    // "2026-10: 8   +25%" with the change in green (better), red (worse) or grey.
    private static View Line(KpiMetric metric, string caption, double? current, double? before)
    {
        var (text, better) = MyKpi.Change(metric, current, before);
        var row = new HorizontalStackLayout { Spacing = 12 };
        row.Children.Add(new Label { Text = $"{caption}: {MyKpi.Format(metric, before)}" });
        row.Children.Add(new Label { Text = text, FontAttributes = FontAttributes.Bold, TextColor = better is null ? Colors.Gray : better == true ? Colors.Green : Colors.Red });
        return row;
    }

    private void Show(string text)
    {
        _message.Text = text;
        _message.IsVisible = true;
    }
}
