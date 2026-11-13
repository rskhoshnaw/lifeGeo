using LifeDrop.Mobile.Localization;
using LifeDrop.Mobile.Visits;

namespace LifeDrop.Mobile;

// "My customers" (plan steps 4.15, 5.2 and 5.28): every customer the user may see, oldest first, 50 at a time ("Show more"), narrowed by name, code or phone, type, city and
// neighbourhood, cooperates, with or without a position, class and the day they were added. Each row shows when the customer was added and whether it still has no position.
// A tap opens the customer's page, where the order, payment, sample and follow-up buttons are, and where the position can be set. Online only; the server limits the answer to the user's area.
public sealed class CustomersListPage : ContentPage
{
    private readonly SearchBar _search = new();
    private readonly ChoiceField _type = new(Texts.Get("app.customer.type"));
    private readonly PlacePickerView _places = new() { AllowAdd = false };
    private readonly YesNoView _cooperates = new();
    private readonly YesNoView _hasLocation = new();
    private readonly ChoiceField _class = new(Texts.Get("app.customer.class"));
    private readonly ChoiceField _since = new(Texts.Get("app.customers.addedSince"));
    private readonly ChoiceField _owner = new(Texts.Get("app.customers.owner"));
    private readonly CaptionedField _ownerBox;
    private IReadOnlyList<(Guid Id, string Name)> _owners = Array.Empty<(Guid, string)>();
    private readonly Button _show = new();
    private readonly Button _more = new() { IsVisible = false };
    private readonly Label _count = new() { IsVisible = false, FontAttributes = FontAttributes.Bold };
    private readonly VerticalStackLayout _results = new() { Spacing = 8 };
    private readonly ActivityIndicator _spinner = new() { IsVisible = false };
    private readonly Label _message = new() { IsVisible = false };
    private bool _placesLoaded;
    private CustomerListFilter _filter = new();
    private readonly List<CustomerListItem> _items = new();

    // Days back for "added since": today, 7 days, 30 days.
    private static readonly int[] SinceDays = [0, 7, 30];

    public CustomersListPage()
    {
        FlowDirection = AppLanguage.Direction;
        _ownerBox = new CaptionedField("app.customers.owner", _owner) { IsVisible = false };
        Title = Texts.Get("app.customers.title");
        _search.Placeholder = Texts.Get("app.customers.search");
        _search.SearchButtonPressed += async (_, _) => await LoadAsync(more: false);
        _type.Items = new[] { Texts.Get("app.customers.all") }.Concat(NewCustomerFlow.Types.Select(t => DailyPlan.TypeLabel(t.ToLowerInvariant()))).ToList();
        _class.Items = new[] { Texts.Get("app.customers.all") }.Concat(DoctorOptions.Classes).ToList();
        _since.Items = new[] { Texts.Get("app.customers.all"), Texts.Get("app.customers.sinceToday"), Texts.Get("app.customers.sinceWeek"), Texts.Get("app.customers.sinceMonth") };
        _show.Text = Texts.Get("app.customers.apply");
        _show.Clicked += async (_, _) => await LoadAsync(more: false);
        _more.Text = Texts.Get("app.customers.more");
        _more.Clicked += async (_, _) => await LoadAsync(more: true);
        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = 16,
                Spacing = 12,
                Children =
                {
                    _search,
                    new CaptionedField("app.customer.type", _type),
                    new Label { Text = Texts.Get("app.customers.place"), FontAttributes = FontAttributes.Bold },
                    _places,
                    new CaptionedField("app.customer.cooperates", _cooperates),
                    new CaptionedField("app.customers.hasLocation", _hasLocation),
                    new CaptionedField("app.customer.class", _class),
                    new CaptionedField("app.customers.addedSince", _since),
                    _ownerBox,
                    _show, _spinner, _message, _count, _results, _more
                }
            }
        };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_placesLoaded) return;
        var api = TrackingHost.CustomersApi;
        if (api is null) return;
        _placesLoaded = true;
        // The list opens with the first customers of the user's area (all of the company for a manager); no need to press "show" first.
        _ = LoadAsync(more: false);
        try
        {
            using var ownersCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            _owners = await api.GetOwnersAsync(ownersCts.Token);
            if (_owners.Count > 0)
            {
                _owner.Items = new[] { Texts.Get("app.customers.all") }.Concat(_owners.Select(o => o.Name)).ToList();
                _ownerBox.IsVisible = true;
            }
        }
        catch (OperationCanceledException)
        {
            // No owner filter then.
        }
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            await _places.LoadAsync(api, null, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // The place lists stay empty; the text search still works.
        }
    }

    // more = false: a new list with the filters as they are now; true: the next page of the same list.
    private async Task LoadAsync(bool more)
    {
        var api = TrackingHost.CustomersApi;
        if (api is null)
        {
            AppLanguage.ShowRoot(new MainPage());
            return;
        }
        if (!more)
        {
            var text = (_search.Text ?? "").Trim();
            if (text.Length == 1) return;   // the server wants two characters at least
            _filter = new CustomerListFilter(
                text.Length == 0 ? null : text,
                _type.SelectedIndex > 0 ? NewCustomerFlow.Types[_type.SelectedIndex - 1] : null,
                _places.PlaceId,
                _cooperates.Value,
                _hasLocation.Value,
                _class.SelectedIndex > 0 ? DoctorOptions.Classes[_class.SelectedIndex - 1] : null,
                _since.SelectedIndex > 0 ? IraqTime.Today(TimeProvider.System).AddDays(-SinceDays[_since.SelectedIndex - 1]) : null,
                OwnerId: _owner.SelectedIndex > 0 && _owner.SelectedIndex - 1 < _owners.Count ? _owners[_owner.SelectedIndex - 1].Id : null);
            _items.Clear();
            _results.Children.Clear();
        }

        _message.IsVisible = false;
        _more.IsVisible = false;
        _spinner.IsVisible = _spinner.IsRunning = true;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var (result, page) = await api.ListCustomersAsync(_filter, more && _items.Count > 0 ? _items[^1] : null, cts.Token);
            if (result == VisitActionResult.SessionExpired)
            {
                AppLanguage.ShowRoot(new MainPage());
                return;
            }
            if (result != VisitActionResult.Success)
            {
                Show(VisitLifecycle.Describe(result));
                return;
            }
            foreach (var item in page)
            {
                _items.Add(item);
                var button = new Button { Text = RowText(item), HorizontalOptions = LayoutOptions.Fill };
                var captured = item;
                button.Clicked += async (_, _) => await Navigation.PushAsync(new CustomerPage(captured.Id, captured.Name));
                _results.Children.Add(button);
            }
            if (_items.Count == 0) Show(Texts.Get("app.customers.none"));
            _count.IsVisible = _items.Count > 0;
            _count.Text = Texts.Get("app.customers.count", ("count", _items.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            _more.IsVisible = page.Count == 50;   // a full page: there may be more
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

    // "D-0001 · Dr. Ali · 0750…" and, below, the day it was added (Iraq is UTC+3 all year) and a note when it has no position yet.
    internal static string RowText(CustomerListItem item)
    {
        var line = $"{item.Code} · {item.Name}" + (string.IsNullOrWhiteSpace(item.Phone) ? "" : $" · {item.Phone}");
        var added = item.CreatedAt is { } at ? at.ToOffset(TimeSpan.FromHours(3)).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) : "";
        var detail = string.Join(" · ", new[] { added, item.OwnerEmail ?? "", item.HasLocation ? "" : Texts.Get("app.customers.noLocationMark") }.Where(x => x.Length > 0));
        return detail.Length == 0 ? line : line + "\n" + detail;
    }

    private void Show(string text)
    {
        _message.Text = text;
        _message.IsVisible = true;
    }
}
