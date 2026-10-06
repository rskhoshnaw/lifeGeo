using LifeDrop.Mobile.Localization;
using LifeDrop.Mobile.Visits;

namespace LifeDrop.Mobile;

// Record pharma samples given to a customer (plan step 2.6): a product from the catalogue, the quantity, and optionally the recipient, batch number and
// expiry date. The phone's position goes along as evidence when the phone has one. Online only. The logic is in SampleDraft (tested).
public sealed class SamplePage : ContentPage
{
    private readonly SampleDraft _draft;
    private readonly Picker _product = new();
    private readonly Entry _quantity = new() { Keyboard = Keyboard.Numeric };
    private readonly Entry _recipient = new() { MaxLength = 100 };
    private readonly Entry _batch = new() { MaxLength = 50 };
    private readonly Entry _expiry = new() { MaxLength = 10 };
    private readonly Editor _note = new() { AutoSize = EditorAutoSizeOption.TextChanges, MaxLength = 500, HeightRequest = 70 };
    private readonly Button _save = new() { IsEnabled = false };
    private readonly ActivityIndicator _spinner = new() { IsVisible = false };
    private readonly Label _message = new() { IsVisible = false };
    private IReadOnlyList<SampleProduct> _products = Array.Empty<SampleProduct>();
    private bool _busy;
    private bool _loaded;

    public SamplePage(Guid customerId, string? customerName, Guid? visitId = null)
    {
        _draft = new SampleDraft(customerId, visitId);
        FlowDirection = AppLanguage.Direction;
        Title = Texts.Get("app.sample.title");
        _product.Title = Texts.Get("app.sample.chooseProduct");
        _product.SelectedIndexChanged += (_, _) =>
        {
            _draft.ProductId = _product.SelectedIndex >= 0 && _product.SelectedIndex < _products.Count ? _products[_product.SelectedIndex].Id : null;
            Refresh();
        };
        _quantity.Placeholder = Texts.Get("app.sample.quantity");
        _quantity.TextChanged += (_, e) => { _draft.QuantityText = e.NewTextValue; Refresh(); };
        _recipient.Placeholder = Texts.Get("app.sample.recipient");
        _recipient.TextChanged += (_, e) => { _draft.Recipient = e.NewTextValue; Refresh(); };
        _batch.Placeholder = Texts.Get("app.sample.batch");
        _batch.TextChanged += (_, e) => { _draft.Batch = e.NewTextValue; Refresh(); };
        _expiry.Placeholder = Texts.Get("app.sample.expiry");
        _expiry.TextChanged += (_, e) => { _draft.ExpiryText = e.NewTextValue; Refresh(); };
        _note.Placeholder = Texts.Get("app.sample.note");
        _note.TextChanged += (_, e) => { _draft.Note = e.NewTextValue; Refresh(); };
        _save.Text = Texts.Get("app.sample.save");
        _save.Clicked += async (_, _) => await SaveAsync();
        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = 16,
                Spacing = 10,
                Children = { new Label { Text = customerName ?? "", FontAttributes = FontAttributes.Bold, FontSize = 18 }, _product, _quantity, _recipient, _batch, _expiry, _note, _save, _spinner, _message }
            }
        };
    }

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3));   // Iraq time

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_loaded) return;
        var api = TrackingHost.SamplesApi;
        if (api is null)
        {
            AppLanguage.ShowRoot(new MainPage());
            return;
        }
        _spinner.IsVisible = _spinner.IsRunning = true;
        VisitActionResult result;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            (result, _products) = await api.GetProductsAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            result = VisitActionResult.NetworkError;
        }
        _spinner.IsVisible = _spinner.IsRunning = false;
        if (result != VisitActionResult.Success)
        {
            Show(VisitLifecycle.Describe(result));
            if (result == VisitActionResult.SessionExpired) AppLanguage.ShowRoot(new MainPage());
            return;
        }
        _loaded = true;
        _product.ItemsSource = _products.Select(p => $"{p.Code} · {p.Name}").ToList();
    }

    private void Refresh() => _save.IsEnabled = !_busy && _draft.CanSave(Today);

    private void Show(string? text)
    {
        _message.Text = text;
        _message.IsVisible = !string.IsNullOrEmpty(text);
    }

    private async Task SaveAsync()
    {
        var api = TrackingHost.SamplesApi;
        if (api is null || _busy) return;
        _busy = true;
        _spinner.IsVisible = _spinner.IsRunning = true;
        Refresh();
        Show(null);
        VisitActionResult result;
        RecordedSample? sample;
        try
        {
            // The position is evidence: a fresh fix when the phone has one within a few seconds; otherwise the record goes without it.
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
            (result, sample) = await api.RecordAsync(_draft, location, cts.Token);
        }
        catch (OperationCanceledException)
        {
            (result, sample) = (VisitActionResult.NetworkError, null);
        }
        _busy = false;
        _spinner.IsVisible = _spinner.IsRunning = false;
        if (result == VisitActionResult.Success && sample is not null)
        {
            _draft.Recorded = sample;
            Show(Texts.Get("app.sample.saved", ("number", sample.Number)));
            Refresh();
            return;
        }
        Show(result == VisitActionResult.GpsMocked ? Texts.Get("app.result.GpsMockedDelivery") : VisitLifecycle.Describe(result));
        if (result == VisitActionResult.SessionExpired) AppLanguage.ShowRoot(new MainPage());
        Refresh();
    }
}
