using System.Globalization;
using LifeDrop.Mobile.Localization;
using LifeDrop.Mobile.Visits;

namespace LifeDrop.Mobile;

// One delivery (plan step 2.4): where to go, then pick up, leave, deliver (receiver, delivered quantities, a reason for less) or fail (reason). Each
// step sends the phone's position as evidence; if the phone has none the step still goes (the server only records it). The logic is in DeliveryDraft.
public sealed class DeliveryPage(Guid orderId) : ContentPage
{
    private readonly VerticalStackLayout _body = new() { Spacing = 10, Padding = 16 };
    private readonly ActivityIndicator _spinner = new() { IsVisible = false };
    private readonly Label _message = new() { IsVisible = false };
    private DeliveryDraft? _draft;
    private Button? _deliver;
    private bool _busy;

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        FlowDirection = AppLanguage.Direction;
        Title = Texts.Get("app.delivery.title");
        Content = new ScrollView { Content = _body };
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        var api = TrackingHost.DeliveryApi;
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
            _draft = new DeliveryDraft(order);
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
        _deliver = null;
        _body.Children.Add(new Label { Text = $"{order.Number} · {order.CustomerName}", FontAttributes = FontAttributes.Bold, FontSize = 20 });
        _body.Children.Add(new Label { Text = Texts.Get("app.delivery.status." + order.Status), FontAttributes = FontAttributes.Bold });
        if (!string.IsNullOrWhiteSpace(order.Address)) _body.Children.Add(new Label { Text = order.Address });
        var contact = new HorizontalStackLayout { Spacing = 8 };
        if (!string.IsNullOrWhiteSpace(order.Phone))
        {
            var call = new Button { Text = Texts.Get("app.delivery.call") + " " + order.Phone };
            call.Clicked += async (_, _) => { try { await Launcher.Default.OpenAsync("tel:" + order.Phone); } catch (Exception) { } };
            contact.Children.Add(call);
        }
        if (order.Latitude is { } lat && order.Longitude is { } lon)
        {
            var navigate = new Button { Text = Texts.Get("app.delivery.navigate") };
            navigate.Clicked += async (_, _) =>
            {
                var point = lat.ToString(CultureInfo.InvariantCulture) + "," + lon.ToString(CultureInfo.InvariantCulture);
                try { await Launcher.Default.OpenAsync("geo:" + point + "?q=" + point); } catch (Exception) { }
            };
            contact.Children.Add(navigate);
        }
        if (contact.Children.Count > 0) _body.Children.Add(contact);
        if (!string.IsNullOrWhiteSpace(order.Note)) _body.Children.Add(new Label { Text = order.Note });

        var delivering = order.Status == "on_the_way";
        foreach (var line in order.Lines)
        {
            var box = new VerticalStackLayout { Spacing = 4 };
            box.Children.Add(new Label { Text = $"{line.Code} · {line.Name}", FontAttributes = FontAttributes.Bold });
            box.Children.Add(new Label { Text = Texts.Get("app.delivery.prepared", ("quantity", Format(line.Prepared)), ("unit", line.Unit)) });
            if (delivering)
            {
                var quantity = new Entry { Keyboard = Keyboard.Numeric, Placeholder = Format(line.Prepared), WidthRequest = 120, HorizontalOptions = LayoutOptions.Start };
                var reason = new Entry { Placeholder = Texts.Get("app.delivery.reason"), MaxLength = 300, IsVisible = false };
                quantity.TextChanged += (_, e) =>
                {
                    if (!_draft.SetQuantity(line.LineNo, e.NewTextValue))
                    {
                        var old = _draft.QuantityOf(line.LineNo);
                        quantity.Text = old == line.Prepared ? "" : Format(old);
                    }
                    reason.IsVisible = _draft.IsShort(line.LineNo);
                    UpdateDeliver();
                };
                reason.TextChanged += (_, e) =>
                {
                    _draft.SetReason(line.LineNo, e.NewTextValue);
                    UpdateDeliver();
                };
                box.Children.Add(quantity);
                box.Children.Add(reason);
            }
            else if (line.Delivered is { } delivered)
            {
                box.Children.Add(new Label { Text = $"{Texts.Get("app.delivery.delivered")}: {Format(delivered)}{(string.IsNullOrWhiteSpace(line.Reason) ? "" : " (" + line.Reason + ")")}" });
            }
            _body.Children.Add(box);
        }

        switch (order.Status)
        {
            case "handed_over":
                AddStep(Texts.Get("app.delivery.pickUp"), (api, location) => api.PickUpAsync(orderId, location));
                AddFail();
                break;
            case "picked_up":
                AddStep(Texts.Get("app.delivery.depart"), (api, location) => api.DepartAsync(orderId, location));
                AddFail();
                break;
            case "on_the_way":
                var receiver = new Entry { Placeholder = Texts.Get("app.delivery.receiver"), MaxLength = 100 };
                receiver.TextChanged += (_, e) => { _draft!.ReceiverName = e.NewTextValue; UpdateDeliver(); };
                var note = new Entry { Placeholder = Texts.Get("app.delivery.note"), MaxLength = 500 };
                note.TextChanged += (_, e) => _draft!.Note = e.NewTextValue;
                _body.Children.Add(receiver);
                _body.Children.Add(note);
                _deliver = new Button { Text = Texts.Get("app.delivery.deliver"), IsEnabled = false };
                _deliver.Clicked += async (_, _) => await RunAsync((api, location) =>
                    api.DeliverAsync(orderId, _draft!.ReceiverName!.Trim(), string.IsNullOrWhiteSpace(_draft.Note) ? null : _draft.Note.Trim(), _draft.Lines, location));
                _body.Children.Add(_deliver);
                AddFail();
                break;
            case "delivered" when !string.IsNullOrWhiteSpace(order.ReceiverName):
                _body.Children.Add(new Label { Text = order.ReceiverName });
                break;
            case "delivery_failed" when !string.IsNullOrWhiteSpace(order.FailureReason):
                _body.Children.Add(new Label { Text = order.FailureReason });
                break;
        }
        _body.Children.Add(_spinner);
        _body.Children.Add(_message);
        UpdateDeliver();
    }

    private void AddStep(string text, Func<IDeliveryApiClient, PhoneLocation?, Task<VisitActionResult>> step)
    {
        var button = new Button { Text = text };
        button.Clicked += async (_, _) => await RunAsync(step);
        _body.Children.Add(button);
    }

    private void AddFail()
    {
        var button = new Button { Text = Texts.Get("app.delivery.fail"), TextColor = Colors.Red };
        button.Clicked += async (_, _) =>
        {
            var reason = await DisplayPromptAsync(Texts.Get("app.delivery.fail"), Texts.Get("app.delivery.failPrompt"), Texts.Get("app.ok"), Texts.Get("app.cancel"), maxLength: 300);
            if (string.IsNullOrWhiteSpace(reason)) return;
            await RunAsync((api, location) => api.FailAsync(orderId, reason.Trim(), location));
        };
        _body.Children.Add(button);
    }

    private void UpdateDeliver()
    {
        if (_deliver is null || _draft is null) return;
        _deliver.IsEnabled = !_busy && _draft.CanDeliver;
        if (!_draft.CanDeliver && _draft.Order.Status == "on_the_way" && _draft.Order.Lines.Any(l => _draft.IsShort(l.LineNo))) Show(Texts.Get("app.delivery.needDetails"));
        else if (_message.Text == Texts.Get("app.delivery.needDetails")) Show(null);
    }

    private async Task RunAsync(Func<IDeliveryApiClient, PhoneLocation?, Task<VisitActionResult>> step)
    {
        var api = TrackingHost.DeliveryApi;
        if (api is null || _busy) return;
        SetBusy(true);
        VisitActionResult result;
        try
        {
            // The position is evidence: a fresh fix when the phone has one within a few seconds; otherwise the step goes without it.
            PhoneLocation? location = null;
            try
            {
                using var fixCts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                location = PhoneLocation.From((await new MauiLocationSource().GetFixAsync(fixCts.Token)).Fix);
            }
            catch (OperationCanceledException)
            {
            }
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            result = await step(api, location);
        }
        catch (OperationCanceledException)
        {
            result = VisitActionResult.NetworkError;
        }
        SetBusy(false);
        if (result == VisitActionResult.Success)
        {
            await LoadAsync();
            Show(Texts.Get("app.delivery.done"));
            return;
        }
        Show(result == VisitActionResult.GpsMocked ? Texts.Get("app.result.GpsMockedDelivery") : VisitLifecycle.Describe(result));
        if (result == VisitActionResult.SessionExpired) AppLanguage.ShowRoot(new MainPage());
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
        if (_deliver is not null)
        {
            if (busy) _deliver.IsEnabled = false;
            else UpdateDeliver();
        }
    }
}
