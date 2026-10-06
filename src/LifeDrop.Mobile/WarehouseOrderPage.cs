using System.Globalization;
using LifeDrop.Mobile.Localization;
using LifeDrop.Mobile.Visits;

namespace LifeDrop.Mobile;

// One order in the warehouse (plan step 2.3): start preparing, say what was prepared (less than ordered needs a reason), hand it to a driver.
// The logic is in PreparationDraft (tested). Every step needs the internet; the server checks it.
public sealed class WarehouseOrderPage(Guid orderId) : ContentPage
{
    private readonly VerticalStackLayout _body = new() { Spacing = 10, Padding = 16 };
    private readonly ActivityIndicator _spinner = new() { IsVisible = false };
    private readonly Label _message = new() { IsVisible = false };
    private PreparationDraft? _draft;
    private IReadOnlyList<DriverItem> _drivers = Array.Empty<DriverItem>();
    private Picker? _driverPicker;
    private Button? _action;
    private bool _busy;

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        FlowDirection = AppLanguage.Direction;
        Title = Texts.Get("app.warehouse.title");
        Content = new ScrollView { Content = _body };
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        var api = TrackingHost.WarehouseApi;
        if (api is null)
        {
            AppLanguage.ShowRoot(new MainPage());
            return;
        }
        SetBusy(true);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var (result, order) = await api.GetOrderAsync(orderId, cts.Token);
            if (result != VisitActionResult.Success || order is null)
            {
                Fail(result);
                return;
            }
            if (order.Status == "ready")
            {
                var (driversResult, drivers) = await api.GetDriversAsync(cts.Token);
                if (driversResult != VisitActionResult.Success)
                {
                    Fail(driversResult);
                    return;
                }
                _drivers = drivers;
            }
            _draft = new PreparationDraft(order);
        }
        catch (OperationCanceledException)
        {
            Fail(VisitActionResult.NetworkError);
            return;
        }
        finally
        {
            SetBusy(false);
        }
        Render();
    }

    private void Render()
    {
        var order = _draft!.Order;
        _body.Children.Clear();
        _body.Children.Add(new Label { Text = $"{order.Number} · {order.CustomerName}", FontAttributes = FontAttributes.Bold, FontSize = 20 });
        _body.Children.Add(new Label { Text = $"{order.WarehouseName} · {Texts.Get("app.warehouse.status." + order.Status)}" });
        if (!string.IsNullOrWhiteSpace(order.Note)) _body.Children.Add(new Label { Text = order.Note });
        if (!string.IsNullOrWhiteSpace(order.DriverEmail)) _body.Children.Add(new Label { Text = order.DriverEmail });

        foreach (var line in order.Lines)
        {
            var box = new VerticalStackLayout { Spacing = 4 };
            box.Children.Add(new Label { Text = $"{line.Code} · {line.Name}", FontAttributes = FontAttributes.Bold });
            box.Children.Add(new Label { Text = Texts.Get("app.warehouse.ordered", ("quantity", Format(line.Quantity)), ("unit", line.Unit)) });
            if (order.Status == "preparing")
            {
                var quantity = new Entry { Keyboard = Keyboard.Numeric, Placeholder = Format(line.Quantity), WidthRequest = 120, HorizontalOptions = LayoutOptions.Start };
                var reason = new Entry { Placeholder = Texts.Get("app.warehouse.reason"), MaxLength = 300, IsVisible = false };
                quantity.TextChanged += (_, e) =>
                {
                    if (!_draft.SetQuantity(line.LineNo, e.NewTextValue))
                    {
                        // Not a quantity, or more than ordered: back to the last good value.
                        var old = _draft.QuantityOf(line.LineNo);
                        quantity.Text = old == line.Quantity ? "" : Format(old);
                    }
                    reason.IsVisible = _draft.IsShort(line.LineNo);
                    UpdateAction();
                };
                reason.TextChanged += (_, e) =>
                {
                    _draft.SetReason(line.LineNo, e.NewTextValue);
                    UpdateAction();
                };
                box.Children.Add(quantity);
                box.Children.Add(reason);
            }
            else if (line.PreparedQuantity is { } prepared)
            {
                box.Children.Add(new Label { Text = $"{Texts.Get("app.warehouse.prepared")}: {Format(prepared)}{(string.IsNullOrWhiteSpace(line.ShortageReason) ? "" : " (" + line.ShortageReason + ")")}" });
            }
            _body.Children.Add(box);
        }

        _driverPicker = null;
        _action = null;
        switch (order.Status)
        {
            case "placed":
                _action = new Button { Text = Texts.Get("app.warehouse.start") };
                _action.Clicked += async (_, _) => await RunAsync(api => api.StartAsync(orderId));
                break;
            case "preparing":
                _action = new Button { Text = Texts.Get("app.warehouse.ready"), IsEnabled = _draft.CanMarkReady };
                _action.Clicked += async (_, _) => await RunAsync(api => api.ReadyAsync(orderId, _draft.Lines));
                break;
            case "ready" when _drivers.Count == 0:
                _body.Children.Add(new Label { Text = Texts.Get("app.warehouse.noDrivers") });
                break;
            case "ready":
                _driverPicker = new Picker { Title = Texts.Get("app.warehouse.chooseDriver"), ItemsSource = _drivers.Select(d => d.Name).ToList() };
                _driverPicker.SelectedIndexChanged += (_, _) => UpdateAction();
                _body.Children.Add(_driverPicker);
                _action = new Button { Text = Texts.Get("app.warehouse.handOver"), IsEnabled = false };
                _action.Clicked += async (_, _) =>
                {
                    if (_driverPicker is { SelectedIndex: >= 0 } picker) await RunAsync(api => api.HandOverAsync(orderId, _drivers[picker.SelectedIndex].Id));
                };
                break;
        }
        if (_action is not null) _body.Children.Add(_action);
        _body.Children.Add(_spinner);
        _body.Children.Add(_message);
        UpdateAction();
    }

    private void UpdateAction()
    {
        if (_action is null || _draft is null) return;
        _action.IsEnabled = !_busy && _draft.Order.Status switch
        {
            "preparing" => _draft.CanMarkReady,
            "ready" => _driverPicker is { SelectedIndex: >= 0 },
            _ => true
        };
        if (_draft.Order.Status == "preparing" && !_draft.CanMarkReady && _draft.Order.Lines.Any(l => _draft.IsShort(l.LineNo))) Show(Texts.Get("app.warehouse.needReason"));
        else if (_message.Text == Texts.Get("app.warehouse.needReason")) Show(null);
    }

    private async Task RunAsync(Func<IWarehouseApiClient, Task<VisitActionResult>> step)
    {
        var api = TrackingHost.WarehouseApi;
        if (api is null || _busy) return;
        SetBusy(true);
        VisitActionResult result;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            result = await step(api);
        }
        catch (OperationCanceledException)
        {
            result = VisitActionResult.NetworkError;
        }
        SetBusy(false);
        if (result == VisitActionResult.Success)
        {
            await LoadAsync();   // the new status and what the server has
            Show(Texts.Get("app.warehouse.done"));
            return;
        }
        Show(VisitLifecycle.Describe(result));
        if (result == VisitActionResult.SessionExpired) AppLanguage.ShowRoot(new MainPage());
        // Another person may have changed the order: show what the server has now.
        if (result is VisitActionResult.Conflict or VisitActionResult.InvalidState) await LoadAsync();
    }

    private void Fail(VisitActionResult result)
    {
        Show(VisitLifecycle.Describe(result));
        if (_message.Parent is null) _body.Children.Add(_message);
        if (result == VisitActionResult.SessionExpired) AppLanguage.ShowRoot(new MainPage());
    }

    private static string Format(decimal value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private void Show(string? text)
    {
        _message.Text = text;
        _message.IsVisible = !string.IsNullOrEmpty(text);
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _spinner.IsVisible = _spinner.IsRunning = busy;
        if (_action is not null)
        {
            if (busy) _action.IsEnabled = false;
            else UpdateAction();
        }
    }
}
