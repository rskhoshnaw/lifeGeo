using LifeDrop.Mobile.Localization;
using LifeDrop.Mobile.Visits;

namespace LifeDrop.Mobile;

// What happened with one customer (plan step 5.4): every visit with its result (status, report outcome and note, the reason of a failed one, check-in and check-out time)
// and the orders with their products. The customer must be inside the user's area; the orders show only to users who may read orders. Online only.
public sealed class CustomerHistoryPage : ContentPage
{
    private readonly Guid _customerId;
    private readonly VerticalStackLayout _body = new() { Padding = 16, Spacing = 8 };
    private readonly ActivityIndicator _spinner = new() { IsRunning = true };
    private bool _loaded;

    public CustomerHistoryPage(Guid customerId, string? name)
    {
        _customerId = customerId;
        FlowDirection = AppLanguage.Direction;
        Title = string.IsNullOrWhiteSpace(name) ? Texts.Get("app.history.title") : $"{Texts.Get("app.history.title")} · {name}";
        _body.Children.Add(_spinner);
        Content = new ScrollView { Content = _body };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_loaded) return;
        var api = TrackingHost.CustomersApi;
        if (api is null)
        {
            AppLanguage.ShowRoot(new MainPage());
            return;
        }
        _loaded = true;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var (result, history) = await api.GetCustomerHistoryAsync(_customerId, cts.Token);
            _body.Children.Clear();
            if (result == VisitActionResult.SessionExpired)
            {
                AppLanguage.ShowRoot(new MainPage());
                return;
            }
            if (result != VisitActionResult.Success || history is null)
            {
                _loaded = false;
                _body.Children.Add(new Label { Text = Texts.Get("app.history.loadFailed") });
                return;
            }
            Render(history);
        }
        catch (OperationCanceledException)
        {
            _loaded = false;
            _body.Children.Clear();
            _body.Children.Add(new Label { Text = Texts.Get("app.timeout") });
        }
    }

    private static string Time(DateTimeOffset time) => time.ToOffset(TimeSpan.FromHours(3)).ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);

    private void Render(CustomerHistoryData history)
    {
        _body.Children.Add(new Label { Text = Texts.Get("app.history.visits"), FontAttributes = FontAttributes.Bold, FontSize = 18 });
        if (history.Visits.Count == 0) _body.Children.Add(new Label { Text = Texts.Get("app.history.none") });
        foreach (var visit in history.Visits)
        {
            var lines = new List<string> { $"{visit.Date} · {Texts.Get("app.status." + visit.Status)}" + (string.IsNullOrWhiteSpace(visit.UserEmail) ? "" : $" · {visit.UserEmail}") };
            if (visit.CheckInAt is { } checkIn) lines.Add(Time(checkIn) + (visit.CheckOutAt is { } checkOut ? $" – {Time(checkOut)}" : ""));
            if (!string.IsNullOrWhiteSpace(visit.Outcome)) lines.Add(Texts.Get("app.outcome." + visit.Outcome));
            if (!string.IsNullOrWhiteSpace(visit.Note)) lines.Add(visit.Note!);
            if (!string.IsNullOrWhiteSpace(visit.Reason)) lines.Add(visit.Reason!);
            _body.Children.Add(Card(lines));
        }

        _body.Children.Add(new Label { Text = Texts.Get("app.history.orders"), FontAttributes = FontAttributes.Bold, FontSize = 18, Margin = new Thickness(0, 12, 0, 0) });
        if (history.Orders is null) _body.Children.Add(new Label { Text = Texts.Get("app.history.ordersHidden") });
        else if (history.Orders.Count == 0) _body.Children.Add(new Label { Text = Texts.Get("app.history.none") });
        else
        {
            foreach (var order in history.Orders)
            {
                var amount = order.PricesHidden ? "" : $" · {order.Total.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} {order.Currency}";
                var lines = new List<string> { $"{order.Number} · {Texts.Get("app.history.order." + order.Status)}{amount}", order.CreatedAt.ToOffset(TimeSpan.FromHours(3)).ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture) };
                lines.AddRange(order.Lines.Select(l => $"{l.ProductName} × {l.Quantity:0.###}" + (l.DeliveredQuantity is { } delivered && delivered != l.Quantity ? $" ({delivered:0.###})" : "")));
                _body.Children.Add(Card(lines));
            }
        }
    }

    private static Border Card(IEnumerable<string> lines)
    {
        var stack = new VerticalStackLayout { Spacing = 2 };
        var first = true;
        foreach (var line in lines)
        {
            stack.Children.Add(new Label { Text = line, FontAttributes = first ? FontAttributes.Bold : FontAttributes.None });
            first = false;
        }
        return new Border { Padding = 12, StrokeThickness = 1, Stroke = Colors.Gray, StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 8 }, Content = stack };
    }
}
