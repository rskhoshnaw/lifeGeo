using LifeDrop.Mobile.Localization;
using LifeDrop.Mobile.Offline;
using LifeDrop.Mobile.Visits;

namespace LifeDrop.Mobile;

// The specialised visit form (plan step 2.7): purpose, person met and role, products presented, needs, and the fields of the visit's type
// (doctor, pharmacy or wholesaler). Saved while the visit is in progress. Online only for now. The logic is in VisitDetailsDraft (tested).
public sealed class VisitFormPage : ContentPage
{
    private readonly Guid _visitId;
    private readonly string? _customerName;
    private readonly string _visitType;
    private readonly VisitDetailsDraft _draft;
    private readonly Picker _purpose = new();
    private readonly Picker _currency = new();
    private readonly CollectionView _products = new() { SelectionMode = SelectionMode.Multiple, HeightRequest = 180 };
    private readonly Button _save = new() { IsEnabled = false };
    private readonly ActivityIndicator _spinner = new() { IsVisible = false };
    private readonly Label _message = new() { IsVisible = false };
    private IReadOnlyList<SampleProduct> _list = Array.Empty<SampleProduct>();
    private bool _busy;
    private bool _loaded;

    public VisitFormPage(Guid visitId, string? customerName, string visitType)
    {
        _visitId = visitId;
        _customerName = customerName;
        _visitType = visitType;
        _draft = new VisitDetailsDraft(visitType);
        FlowDirection = AppLanguage.Direction;
        Title = Texts.Get("app.form.title");
        _purpose.Title = Texts.Get("app.form.purpose");
        _purpose.ItemsSource = VisitDetailsDraft.Purposes.Select(p => Texts.Get("app.form.purpose." + p)).ToList();
        _purpose.SelectedIndexChanged += (_, _) => { _draft.Purpose = _purpose.SelectedIndex >= 0 ? VisitDetailsDraft.Purposes[_purpose.SelectedIndex] : null; Refresh(); };
        _currency.ItemsSource = VisitDetailsDraft.Currencies.ToList();
        _currency.SelectedIndex = 0;
        _currency.SelectedIndexChanged += (_, _) => _draft.Currency = VisitDetailsDraft.Currencies[Math.Max(0, _currency.SelectedIndex)];
        _products.ItemTemplate = new DataTemplate(() =>
        {
            var label = new Label { Padding = 6 };
            label.SetBinding(Label.TextProperty, ".");
            return label;
        });
        _products.SelectionChanged += (_, _) =>
        {
            _draft.ProductIds.Clear();
            foreach (var item in _products.SelectedItems.OfType<string>())
            {
                var product = _list.FirstOrDefault(p => $"{p.Code} · {p.Name}" == item);
                if (product is not null) _draft.ProductIds.Add(product.Id);
            }
            Refresh();
        };
        _save.Text = Texts.Get("app.form.save");
        _save.Clicked += async (_, _) => await SaveAsync();

        Entry Field(string key, Action<string?> set, Keyboard? keyboard = null, int max = 100)
        {
            var entry = new Entry { Placeholder = Texts.Get(key), MaxLength = max, Keyboard = keyboard ?? Keyboard.Default };
            entry.TextChanged += (_, e) => { set(e.NewTextValue); Refresh(); };
            return entry;
        }

        var layout = new VerticalStackLayout
        {
            Padding = 16,
            Spacing = 10,
            Children =
            {
                new Label { Text = customerName ?? "", FontAttributes = FontAttributes.Bold, FontSize = 18 },
                _purpose,
                Field("app.form.personMet", v => _draft.PersonMet = v),
                Field("app.form.personRole", v => _draft.PersonRole = v),
                new Label { Text = Texts.Get("app.form.products"), FontAttributes = FontAttributes.Bold },
                _products,
                Field("app.form.needs", v => _draft.Needs = v, max: 1000)
            }
        };
        if (_draft.IsDoctor && Texts.Profile != "food")   // medical details do not apply to a food distributor
        {
            layout.Children.Add(Field("app.form.specialty", v => _draft.Specialty = v));
            layout.Children.Add(Field("app.form.dailyPatients", v => _draft.DailyPatientsText = v, Keyboard.Numeric));
        }
        if (_draft.IsPharmacy)
        {
            layout.Children.Add(_currency);
            layout.Children.Add(Field("app.form.monthlySales", v => _draft.MonthlySalesText = v, Keyboard.Numeric));
            layout.Children.Add(Field("app.form.competitors", v => _draft.Competitors = v, max: 500));
        }
        if (_draft.IsWholesaler)
        {
            layout.Children.Add(_currency);
            layout.Children.Add(Field("app.form.monthlyVolume", v => _draft.MonthlyVolumeText = v, Keyboard.Numeric));
            layout.Children.Add(Field("app.form.paymentTerms", v => _draft.PaymentTerms = v, max: 500));
        }
        layout.Children.Add(_save);
        layout.Children.Add(_spinner);
        layout.Children.Add(_message);
        Content = new ScrollView { Content = layout };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_loaded) return;
        var api = TrackingHost.SamplesApi;   // the same catalogue list as the samples
        if (api is null) return;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var (result, products) = await api.GetProductsAsync(cts.Token);
            if (result != VisitActionResult.Success) return;
            _list = products;
            _products.ItemsSource = products.Select(p => $"{p.Code} · {p.Name}").ToList();
            _loaded = true;
        }
        catch (OperationCanceledException)
        {
            // The form can still be saved without products.
        }
    }

    private void Refresh() => _save.IsEnabled = !_busy && _draft.CanSave;

    private void Show(string? text)
    {
        _message.Text = text;
        _message.IsVisible = !string.IsNullOrEmpty(text);
    }

    private async Task SaveAsync()
    {
        var api = TrackingHost.VisitDetailsApi;
        if (api is null || _busy) return;
        _busy = true;
        _spinner.IsVisible = _spinner.IsRunning = true;
        Refresh();
        Show(null);
        VisitActionResult result;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            result = await api.SaveAsync(_visitId, _draft, cts.Token);
        }
        catch (OperationCanceledException)
        {
            result = VisitActionResult.NetworkError;
        }
        _busy = false;
        _spinner.IsVisible = _spinner.IsRunning = false;
        // No internet: kept on the phone; it is sent after the steps of its visit (plan step 2.13).
        if (result == VisitActionResult.NetworkError && TrackingHost.WorkQueue is { } queue)
        {
            queue.Enqueue(WorkKind.VisitForm, $"{Texts.Get("app.form.title")}: {_customerName}",
                new VisitFormPayload(_visitId, _visitType, _draft.Purpose, _draft.PersonMet, _draft.PersonRole, _draft.Needs, _draft.ProductIds.ToList(), _draft.Specialty, _draft.DailyPatientsText,
                    _draft.MonthlySalesText, _draft.Competitors, _draft.MonthlyVolumeText, _draft.PaymentTerms, _draft.Currency), _visitId);
            Show(Texts.Get("app.offline.saved"));
            Refresh();
            return;
        }
        Show(result == VisitActionResult.Success ? Texts.Get("app.form.saved") : VisitLifecycle.Describe(result));
        if (result == VisitActionResult.SessionExpired) AppLanguage.ShowRoot(new MainPage());
        Refresh();
    }
}
