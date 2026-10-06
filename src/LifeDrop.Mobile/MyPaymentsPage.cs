using LifeDrop.Mobile.Localization;
using LifeDrop.Mobile.Visits;

namespace LifeDrop.Mobile;

// The payments the user recorded (plan step 2.5) and whether the finance manager has confirmed that the money arrived. Online only.
public sealed class MyPaymentsPage : ContentPage
{
    private readonly CollectionView _list = new() { SelectionMode = SelectionMode.None };
    private readonly ActivityIndicator _spinner = new() { IsVisible = false };
    private readonly Label _message = new() { IsVisible = false };

    public MyPaymentsPage()
    {
        FlowDirection = AppLanguage.Direction;
        Title = Texts.Get("app.payment.list.title");
        _list.ItemTemplate = new DataTemplate(() =>
        {
            var title = new Label { FontAttributes = FontAttributes.Bold };
            title.SetBinding(Label.TextProperty, nameof(Row.Title));
            var details = new Label { FontSize = 12 };
            details.SetBinding(Label.TextProperty, nameof(Row.Details));
            return new Border { Padding = 10, Margin = new Thickness(0, 0, 0, 6), Content = new VerticalStackLayout { Children = { title, details } } };
        });
        var grid = new Grid { RowDefinitions = { new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Star) }, Padding = 12, RowSpacing = 8 };
        grid.Add(new VerticalStackLayout { Spacing = 8, Children = { _spinner, _message } }, 0, 0);
        grid.Add(_list, 0, 1);
        Content = grid;
    }

    private sealed record Row(string Title, string Details);

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        var api = TrackingHost.PaymentsApi;
        if (api is null)
        {
            AppLanguage.ShowRoot(new MainPage());
            return;
        }
        _spinner.IsVisible = _spinner.IsRunning = true;
        VisitActionResult result;
        IReadOnlyList<PaymentSummary> payments;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            (result, payments) = await api.GetMineAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            (result, payments) = (VisitActionResult.NetworkError, Array.Empty<PaymentSummary>());
        }
        _spinner.IsVisible = _spinner.IsRunning = false;
        if (result != VisitActionResult.Success)
        {
            Show(VisitLifecycle.Describe(result));
            if (result == VisitActionResult.SessionExpired) AppLanguage.ShowRoot(new MainPage());
            return;
        }
        _list.ItemsSource = payments.Select(p => new Row($"{p.Number} · {p.CustomerName}",
            $"{p.Amount.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} {p.Currency} · {Texts.Get("app.payment.method." + p.Method)} · {Texts.Get("app.payment.status." + p.Status)} · {p.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm}")).ToList();
        Show(payments.Count == 0 ? Texts.Get("app.payment.none") : null);
    }

    private void Show(string? text)
    {
        _message.Text = text;
        _message.IsVisible = !string.IsNullOrEmpty(text);
    }
}
