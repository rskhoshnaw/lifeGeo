using LifeDrop.Mobile.Localization;
using LifeDrop.Mobile.Offline;
using LifeDrop.Mobile.Visits;

namespace LifeDrop.Mobile;

// Record the money a customer paid (plan step 2.5): cash, FastPay, FIB or other, the amount, the reference FastPay and FIB need. The payment
// stays "collected" until the finance manager confirms it. Online only. The logic is in PaymentDraft (tested).
public sealed class PaymentPage : ContentPage
{
    private readonly PaymentDraft _draft;
    private readonly Picker _method = new();
    private readonly Picker _currency = new();
    private readonly Entry _amount = new() { Keyboard = Keyboard.Numeric };
    private readonly Entry _reference = new() { MaxLength = 100 };
    private readonly Editor _note = new() { AutoSize = EditorAutoSizeOption.TextChanges, MaxLength = 500, HeightRequest = 70 };
    private readonly Button _save = new() { IsEnabled = false };
    private readonly Label _balance = new() { IsVisible = false };
    private readonly ActivityIndicator _spinner = new() { IsVisible = false };
    private readonly Label _message = new() { IsVisible = false };
    private readonly Guid _customerId;
    private readonly string? _customerName;
    private bool _busy;

    public PaymentPage(Guid customerId, string? customerName)
    {
        _customerId = customerId;
        _customerName = customerName;
        _draft = new PaymentDraft(customerId);
        FlowDirection = AppLanguage.Direction;
        Title = Texts.Get("app.payment.title");
        _method.ItemsSource = PaymentDraft.Methods.Select(m => Texts.Get("app.payment.method." + m)).ToList();
        _method.SelectedIndex = 0;
        _method.SelectedIndexChanged += (_, _) => { _draft.Method = PaymentDraft.Methods[Math.Max(0, _method.SelectedIndex)]; Refresh(); };
        _currency.ItemsSource = PaymentDraft.Currencies.ToList();
        _currency.SelectedIndex = 0;
        _currency.SelectedIndexChanged += (_, _) => { _draft.Currency = PaymentDraft.Currencies[Math.Max(0, _currency.SelectedIndex)]; Refresh(); };
        _amount.Placeholder = Texts.Get("app.payment.amount");
        _amount.TextChanged += (_, e) => { _draft.AmountText = e.NewTextValue; Refresh(); };
        _reference.Placeholder = Texts.Get("app.payment.reference");
        _reference.TextChanged += (_, e) => { _draft.Reference = e.NewTextValue; Refresh(); };
        _note.Placeholder = Texts.Get("app.payment.note");
        _note.TextChanged += (_, e) => { _draft.Note = e.NewTextValue; Refresh(); };
        _save.Text = Texts.Get("app.payment.save");
        _save.Clicked += async (_, _) => await SaveAsync();
        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = 16,
                Spacing = 10,
                Children = { new Label { Text = customerName ?? "", FontAttributes = FontAttributes.Bold, FontSize = 18 }, _balance, _method, _currency, _amount, _reference, _note, _save, _spinner, _message }
            }
        };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        var api = TrackingHost.PaymentsApi;
        if (api is null) return;
        // What the customer owes now, when the user may see it (otherwise nothing is shown).
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var (result, lines) = await api.GetBalanceAsync(_customerId, cts.Token);
            if (result != VisitActionResult.Success || lines.Count == 0) return;
            _balance.Text = string.Join("  ·  ", lines.Select(l => Texts.Get("app.payment.owes", ("amount", l.Balance.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)), ("currency", l.Currency))));
            _balance.IsVisible = true;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void Refresh()
    {
        _reference.IsVisible = _draft.NeedsReference || !string.IsNullOrEmpty(_reference.Text);
        _save.IsEnabled = !_busy && _draft.CanSave;
    }

    private void Show(string? text)
    {
        _message.Text = text;
        _message.IsVisible = !string.IsNullOrEmpty(text);
    }

    private async Task SaveAsync()
    {
        var api = TrackingHost.PaymentsApi;
        if (api is null)
        {
            AppLanguage.ShowRoot(new MainPage());
            return;
        }
        _busy = true;
        _spinner.IsVisible = _spinner.IsRunning = true;
        Refresh();
        Show(null);
        VisitActionResult result;
        RecordedPayment? payment;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            (result, payment) = await api.RecordAsync(_draft, cts.Token);
        }
        catch (OperationCanceledException)
        {
            (result, payment) = (VisitActionResult.NetworkError, null);
        }
        _busy = false;
        _spinner.IsVisible = _spinner.IsRunning = false;
        if (result == VisitActionResult.Success && payment is not null)
        {
            _draft.Recorded = payment;
            Show(Texts.Get(payment.Status == "confirmed" ? "app.payment.savedConfirmed" : "app.payment.saved", ("number", payment.Number)));
            Refresh();
            return;
        }
        // No internet: the payment is kept on the phone and sent later (plan step 2.13); the money was taken, so it must not be lost.
        if (result == VisitActionResult.NetworkError && TrackingHost.WorkQueue is { } queue)
        {
            queue.Enqueue(WorkKind.Payment, $"{Texts.Get("app.payment.title")}: {_customerName}",
                new PaymentPayload(_customerId, _draft.OrderId, _draft.ClientRef, _draft.Method, _draft.Currency, _draft.AmountText, _draft.Reference, _draft.Note));
            _draft.Queued = true;
            Show(Texts.Get("app.offline.saved"));
            Refresh();
            return;
        }
        Show(VisitLifecycle.Describe(result));
        if (result == VisitActionResult.SessionExpired) AppLanguage.ShowRoot(new MainPage());
        Refresh();
    }
}
