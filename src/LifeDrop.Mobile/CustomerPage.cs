using LifeDrop.Mobile.Localization;
using LifeDrop.Mobile.Visits;

namespace LifeDrop.Mobile;

// Details of a customer and the profile the field user completes at the first visit (plan step 1.23-b): phone, address
// note and where the customer is (city, neighbourhood, street, alley). Saving needs the internet; a customer that the
// user did not add cannot be changed (the server answers "not found").
public sealed class CustomerPage : ContentPage
{
    private readonly Guid _customerId;
    private readonly Label _titleLabel = new() { FontAttributes = FontAttributes.Bold, FontSize = 20 };
    private readonly Label _typeLabel = new();
    private readonly Entry _phone = new() { Keyboard = Keyboard.Telephone, MaxLength = 40 };
    private readonly Editor _address = new() { AutoSize = EditorAutoSizeOption.TextChanges, MaxLength = 300, HeightRequest = 80 };
    private readonly Entry _contactName = new() { MaxLength = 100 };
    private readonly Entry _phone2 = new() { Keyboard = Keyboard.Telephone, MaxLength = 40 };
    private readonly Editor _notes = new() { AutoSize = EditorAutoSizeOption.TextChanges, MaxLength = 500, HeightRequest = 80 };
    private readonly PlacePickerView _places = new();
    private readonly Button _save = new();
    private readonly Button _order = new();
    private readonly Button _payment = new();
    private readonly Button _sample = new();
    private readonly Button _followUp = new();
    private readonly ActivityIndicator _spinner = new() { IsVisible = false };
    private readonly Label _message = new() { IsVisible = false };
    private bool _loaded;

    public CustomerPage(Guid customerId, string? name = null)
    {
        _customerId = customerId;
        FlowDirection = AppLanguage.Direction;
        Title = Texts.Get("app.customer.details");
        _titleLabel.Text = name ?? "";
        _phone.Placeholder = Texts.Get("app.customer.phone");
        _address.Placeholder = Texts.Get("app.customer.address");
        _contactName.Placeholder = Texts.Get("app.customer.contactName");
        _phone2.Placeholder = Texts.Get("app.customer.phone2");
        _notes.Placeholder = Texts.Get("app.customer.notes");
        _save.Text = Texts.Get("app.customer.save");
        _save.Clicked += async (_, _) => await SaveAsync();
        _save.IsEnabled = false;
        _order.Text = Texts.Get("app.order.button");
        _order.Clicked += async (_, _) => await Navigation.PushAsync(new OrderPage(_customerId, _titleLabel.Text));
        _payment.Text = Texts.Get("app.payment.button");
        _payment.Clicked += async (_, _) => await Navigation.PushAsync(new PaymentPage(_customerId, _titleLabel.Text));
        _sample.Text = Texts.Get("app.sample.button");
        _sample.Clicked += async (_, _) => await Navigation.PushAsync(new SamplePage(_customerId, _titleLabel.Text));
        _followUp.Text = Texts.Get("app.followup.new");
        _followUp.Clicked += async (_, _) => await Navigation.PushAsync(new FollowUpPage(_customerId, _titleLabel.Text));
        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = 16,
                Spacing = 12,
                Children =
                {
                    _titleLabel, _typeLabel, _phone, _address, _contactName, _phone2, _notes,
                    new Label { Text = Texts.Get("app.customer.place"), FontAttributes = FontAttributes.Bold },
                    _places, _save, _order, _payment, _sample, _followUp, _spinner, _message
                }
            }
        };
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

        SetBusy(true);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var (result, customer) = await api.GetCustomerAsync(_customerId, cts.Token);
            if (result != VisitActionResult.Success || customer is null)
            {
                Show(result == VisitActionResult.NotFound ? Texts.Get("app.customer.notYours") : Texts.Get("app.customer.loadFailed"), result);
                return;
            }
            _loaded = true;
            _titleLabel.Text = $"{customer.Name} ({customer.Code})";
            _typeLabel.Text = DailyPlan.TypeLabel(customer.Type.ToLowerInvariant());
            _phone.Text = customer.Phone;
            _address.Text = customer.Address;
            _contactName.Text = customer.ContactName;
            _phone2.Text = customer.Phone2;
            _notes.Text = customer.Notes;
            await _places.LoadAsync(api, customer.PlacePath, cts.Token);
            _save.IsEnabled = true;
        }
        catch (OperationCanceledException)
        {
            Show(Texts.Get("app.timeout"), VisitActionResult.NetworkError);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task SaveAsync()
    {
        var api = TrackingHost.CustomersApi;
        if (api is null) return;
        SetBusy(true);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var result = await api.SetProfileAsync(_customerId, _phone.Text, _address.Text, _places.PlaceId, cts.Token);
            if (result == VisitActionResult.Success)
                result = await api.SetContactAsync(_customerId, _contactName.Text, _phone2.Text, _notes.Text, cts.Token);
            Show(result == VisitActionResult.Success ? Texts.Get("app.customer.saved")
                : result == VisitActionResult.NotFound ? Texts.Get("app.customer.notYours") : VisitLifecycle.Describe(result), result);
        }
        catch (OperationCanceledException)
        {
            Show(Texts.Get("app.timeout"), VisitActionResult.NetworkError);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void Show(string text, VisitActionResult result)
    {
        _message.Text = text;
        _message.IsVisible = true;
        if (result == VisitActionResult.SessionExpired) AppLanguage.ShowRoot(new MainPage());
    }

    private void SetBusy(bool busy)
    {
        _spinner.IsVisible = _spinner.IsRunning = busy;
        _save.IsEnabled = _loaded && !busy;
        _phone.IsEnabled = _address.IsEnabled = _contactName.IsEnabled = _phone2.IsEnabled = _notes.IsEnabled = !busy;
        _places.SetEnabled(!busy);
        if (busy) _message.IsVisible = false;
    }
}
