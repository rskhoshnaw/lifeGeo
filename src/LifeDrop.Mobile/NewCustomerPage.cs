using LifeDrop.Mobile.Localization;
using LifeDrop.Mobile.Visits;

namespace LifeDrop.Mobile;

// Registers a customer in the field (plan step 1.23-b): name, type, phone, address, where it is, and the GPS position of
// the place the user stands at. The code is made by the app. Online only. If the customer is created but the details or the
// position could not be saved, the customer's page opens to finish it.
// Two forms (plan step 5.3): a doctor keeps the doctor form; every other type has: type, place name, customer name, mobile, day of the week, hours, cooperates,
// Dealer, city, neighbourhood, address, notes, location. The place name is the customer's name in the lists; the customer name is the contact person.
public sealed class NewCustomerPage : ContentPage
{
    private readonly Entry _name = new() { MaxLength = NewCustomerFlow.MaxNameLength };
    private readonly ChoiceField _type = new(Texts.Get("app.customer.type")) { Items = NewCustomerFlow.Types.Select(t => DailyPlan.TypeLabel(t.ToLowerInvariant())).ToList(), SelectedIndex = 0 };
    private IReadOnlyList<string> _types = NewCustomerFlow.Types;
    // Doctor details (plan step 4.27-b): shown only when the type is Doctor.
    private readonly DoctorFieldsView _doctorBox = new() { IsVisible = false };
    private readonly Entry _phone = new() { Keyboard = Keyboard.Telephone, MaxLength = 40 };
    private readonly Editor _address = new() { AutoSize = EditorAutoSizeOption.TextChanges, MaxLength = 300, HeightRequest = 80 };
    private readonly Entry _contactName = new() { MaxLength = 100 };
    // Only the form of the other types: day and hours of the visit, cooperates, Dealer.
    private readonly VisitWindowsView _when = new();
    private readonly YesNoView _cooperates = new();
    private readonly YesNoView _dealer = new();
    private readonly Entry _phone2 = new() { Keyboard = Keyboard.Telephone, MaxLength = 40 };
    private readonly Editor _notes = new() { AutoSize = EditorAutoSizeOption.TextChanges, MaxLength = 500, HeightRequest = 80 };
    private readonly PlacePickerView _places = new();
    private readonly Button _locationButton = new();
    private readonly Label _locationLabel = new() { IsVisible = false };
    private readonly Button _create = new();
    private readonly ActivityIndicator _spinner = new() { IsVisible = false };
    private readonly Label _message = new() { IsVisible = false };
    private readonly VerticalStackLayout _form = new() { Padding = 16, Spacing = 12 };
    private readonly CaptionedField _typeBox;
    private readonly CaptionedField _nameBox;
    private readonly CaptionedField _contactBox;
    private readonly CaptionedField _phoneBox;
    private readonly CaptionedField _phone2Box;
    private readonly CaptionedField _addressBox;
    private readonly CaptionedField _notesBox;
    private readonly CaptionedField _cooperatesBox;
    private readonly CaptionedField _dealerBox;
    private readonly Label _placeTitle = new() { FontAttributes = FontAttributes.Bold };
    private DeviceFix? _fix;
    private bool _placesLoaded;

    public NewCustomerPage()
    {
        FlowDirection = AppLanguage.Direction;
        Title = Texts.Get("app.customer.new");
        _phone.Placeholder = Texts.Get("app.customer.phone");
        _address.Placeholder = Texts.Get("app.customer.address");
        _contactName.Placeholder = Texts.Get("app.customer.contactName");
        _phone2.Placeholder = Texts.Get("app.customer.phone2");
        _notes.Placeholder = Texts.Get("app.customer.notes");
        _locationButton.Text = Texts.Get("app.customer.useLocation");
        _locationButton.Clicked += async (_, _) => await CaptureLocationAsync();
        _type.SelectedIndexChanged += (_, _) => OnTypeChanged();
        _create.Text = Texts.Get("app.customer.create");
        _create.Clicked += async (_, _) => await CreateAsync();
        _typeBox = new CaptionedField("app.customer.type", _type);
        _nameBox = new CaptionedField("app.customer.name", _name);
        _contactBox = new CaptionedField("app.customer.contactName", _contactName);
        _phoneBox = new CaptionedField("app.customer.phone", _phone);
        _phone2Box = new CaptionedField("app.customer.phone2", _phone2);
        _addressBox = new CaptionedField("app.customer.address", _address);
        _notesBox = new CaptionedField("app.customer.notes", _notes);
        _cooperatesBox = new CaptionedField("app.customer.cooperates", _cooperates);
        _dealerBox = new CaptionedField("app.customer.dealer", _dealer);
        Content = new ScrollView { Content = _form };
        Layout();
    }

    // The fields in the order of the chosen type: the doctor form as before, the other types as listed at the top of this class.
    private void Layout()
    {
        var doctor = NewCustomerFlow.IsDoctor(SelectedType);
        _doctorBox.IsVisible = doctor;
        _nameBox.Caption.Text = Texts.Get(doctor ? "app.customer.doctorName" : "app.customer.placeName");
        _name.Placeholder = _nameBox.Caption.Text;
        _contactBox.Caption.Text = Texts.Get(doctor ? "app.customer.contactName" : "app.customer.name");
        _contactName.Placeholder = _contactBox.Caption.Text;
        _placeTitle.Text = Texts.Get(doctor ? "app.customer.place" : "app.customers.place");
        _form.Children.Clear();
        _form.Children.Add(_typeBox);
        _form.Children.Add(_nameBox);
        if (doctor)
        {
            foreach (var view in new View[] { _doctorBox, _phoneBox, _addressBox, _contactBox, _phone2Box, _notesBox, _placeTitle, _places }) _form.Children.Add(view);
        }
        else
        {
            foreach (var view in new View[] { _contactBox, _phoneBox, _when, _cooperatesBox, _dealerBox, _placeTitle, _places, _addressBox, _notesBox }) _form.Children.Add(view);
        }
        foreach (var view in new View[] { _locationButton, _locationLabel, _create, _spinner, _message }) _form.Children.Add(view);
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_placesLoaded) return;
        var api = TrackingHost.CustomersApi;
        if (api is null)
        {
            AppLanguage.ShowRoot(new MainPage());
            return;
        }
        _placesLoaded = true;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            // Only the types this user may register (a sales rep has no Doctor; a scientific rep only Doctor). If the permissions cannot be read, all types are offered and the server decides.
            var (permissionsResult, permissions) = await api.GetMyPermissionsAsync(cts.Token);
            if (permissionsResult == VisitActionResult.Success)
            {
                _types = NewCustomerFlow.TypesFor(permissions);
                _type.Items = _types.Select(t => DailyPlan.TypeLabel(t.ToLowerInvariant())).ToList();
                _type.SelectedIndex = 0;
                OnTypeChanged();
            }
            await _places.LoadAsync(api, null, cts.Token);
        }
        catch (OperationCanceledException)
        {
            Show(Texts.Get("app.timeout"));
        }
    }

    private string SelectedType => _types[Math.Max(0, Math.Min(_type.SelectedIndex, _types.Count - 1))];

    private void OnTypeChanged() => Layout();

    private DoctorDetails? DoctorFromForm() => NewCustomerFlow.IsDoctor(SelectedType) ? _doctorBox.Read() : null;

    private async Task CaptureLocationAsync()
    {
        SetBusy(true);
        try
        {
            var result = await new MauiLocationSource().GetFixAsync();
            _fix = result.Fix;
            _locationLabel.Text = result.Fix is { } fix
                ? Texts.Get("app.customer.locationSet", ("meters", Math.Round(fix.AccuracyMeters).ToString(System.Globalization.CultureInfo.InvariantCulture)))
                : Texts.Get("app.customer.locationFailed");
            _locationLabel.IsVisible = true;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task CreateAsync()
    {
        var api = TrackingHost.CustomersApi;
        if (api is null) return;
        var isDoctor = NewCustomerFlow.IsDoctor(SelectedType);
        IReadOnlyList<VisitWindowItem>? windows = null;
        if (!isDoctor)
        {
            var (plan, items) = _when.Read();
            if (plan != VisitWindowPlanResult.Ok)
            {
                Show(Texts.Get(plan == VisitWindowPlanResult.NoDays ? "app.customer.daysNone" : "app.customer.hoursInvalid"));
                return;
            }
            windows = items;
        }
        var request = isDoctor
            ? new NewCustomerRequest(_name.Text ?? "", SelectedType, _phone.Text, _address.Text, _places.PlaceId, _fix, _contactName.Text, _phone2.Text, _notes.Text, DoctorFromForm())
            : new NewCustomerRequest(_name.Text ?? "", SelectedType, _phone.Text, _address.Text, _places.PlaceId, _fix, _contactName.Text, null, _notes.Text,
                new DoctorDetails(null, null, _cooperates.Value, _dealer.Value, null), windows);
        if (!NewCustomerFlow.IsValid(request))
        {
            Show(Texts.Get("app.customer.nameRequired"));
            return;
        }

        SetBusy(true);
        NewCustomerOutcome outcome;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            outcome = await new NewCustomerFlow(api, TimeProvider.System).CreateAsync(request, cts.Token);
        }
        catch (OperationCanceledException)
        {
            SetBusy(false);
            Show(Texts.Get("app.timeout"));
            return;
        }
        SetBusy(false);

        if (outcome.Create != VisitActionResult.Success || outcome.CustomerId is not { } customerId)
        {
            Show(outcome.Create switch
            {
                VisitActionResult.Conflict => Texts.Get("app.customer.codeFailed"),
                VisitActionResult.SessionExpired => Texts.Get("app.sessionEnded"),
                _ => VisitLifecycle.Describe(outcome.Create)
            });
            if (outcome.Create == VisitActionResult.SessionExpired) AppLanguage.ShowRoot(new MainPage());
            return;
        }

        if (outcome.Complete)
        {
            await DisplayAlertAsync(Texts.Get("app.customer.new"), Texts.Get("app.customer.created", ("code", outcome.Code ?? "")), Texts.Get("app.ok"));
            await Navigation.PopAsync();
            return;
        }

        // Created, but something was not saved: open the customer to finish.
        var notes = new List<string>();
        if (outcome.Profile is not null and not VisitActionResult.Success) notes.Add(Texts.Get("app.customer.partialProfile", ("code", outcome.Code ?? "")));
        if (outcome.Location is not null and not VisitActionResult.Success) notes.Add(Texts.Get("app.customer.partialLocation", ("code", outcome.Code ?? "")));
        if (outcome.Contact is not null and not VisitActionResult.Success) notes.Add(Texts.Get("app.customer.partialContact", ("code", outcome.Code ?? "")));
        if (outcome.Windows is not null and not VisitActionResult.Success) notes.Add(Texts.Get("app.customer.partialWindows", ("code", outcome.Code ?? "")));
        await DisplayAlertAsync(Texts.Get("app.customer.new"), string.Join("\n\n", notes), Texts.Get("app.ok"));
        Navigation.InsertPageBefore(new CustomerPage(customerId, _name.Text?.Trim()), this);
        await Navigation.PopAsync();
    }

    private void Show(string text)
    {
        _message.Text = text;
        _message.IsVisible = true;
    }

    private void SetBusy(bool busy)
    {
        _spinner.IsVisible = _spinner.IsRunning = busy;
        _create.IsEnabled = _locationButton.IsEnabled = !busy;
        _name.IsEnabled = _phone.IsEnabled = _address.IsEnabled = _type.IsEnabled = _contactName.IsEnabled = _notes.IsEnabled = _when.IsEnabled = _cooperates.IsEnabled = _dealer.IsEnabled = !busy;
        _places.SetEnabled(!busy);
        if (busy) _message.IsVisible = false;
    }
}
