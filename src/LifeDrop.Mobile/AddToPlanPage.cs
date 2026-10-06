using System.Collections.ObjectModel;
using System.ComponentModel;
using LifeDrop.Mobile.Localization;
using LifeDrop.Mobile.Visits;

namespace LifeDrop.Mobile;

// One customer of the search result; Mark shows whether it is ticked.
public sealed class AddCustomerRow(SelfPlanCandidate candidate, string details) : INotifyPropertyChanged
{
    private bool _selected;
    public SelfPlanCandidate Candidate { get; } = candidate;
    public string Title => $"{Candidate.Code} · {Candidate.Name}";
    public string Details { get; } = details;
    public string Mark => _selected ? "☑" : "☐";
    public bool Selected
    {
        get => _selected;
        set
        {
            _selected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Mark)));
        }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}

// The field user adds customers to their own plan (plan step 1.25): choose where (place lists) and/or type a name or code,
// search, tick, add. Online only. The logic is in SelfPlanner (tested); order and moving to another day are on the plan page.
public sealed class AddToPlanPage : ContentPage
{
    private readonly SelfPlanner _planner;
    private readonly PlacePickerView _places = new();
    private readonly Entry _text = new() { MaxLength = SelfPlanner.MaxTextLength };
    private readonly Button _dayButton = new();
    private readonly Button _search = new();
    private readonly Button _add = new() { IsVisible = false };
    private readonly ActivityIndicator _spinner = new() { IsVisible = false };
    private readonly Label _message = new() { IsVisible = false };
    private readonly ObservableCollection<AddCustomerRow> _rows = [];
    private readonly CollectionView _list;
    private DateOnly _day;
    private bool _placesLoaded;

    public AddToPlanPage(ISelfPlanApiClient api, DateOnly day)
    {
        _planner = new SelfPlanner(api);
        _day = day;
        FlowDirection = AppLanguage.Direction;
        Title = Texts.Get("app.add.title");
        _text.Placeholder = Texts.Get("app.add.search");
        _search.Text = Texts.Get("app.add.searchBtn");
        _search.Clicked += async (_, _) => await SearchAsync();
        _dayButton.Clicked += async (_, _) => await ChooseDayAsync();
        _add.Clicked += async (_, _) => await AddAsync();
        ShowDay();

        _list = new CollectionView
        {
            SelectionMode = SelectionMode.Single,
            ItemsSource = _rows,
            ItemTemplate = new DataTemplate(() =>
            {
                var mark = new Label { FontSize = 22, VerticalOptions = LayoutOptions.Center };
                mark.SetBinding(Label.TextProperty, nameof(AddCustomerRow.Mark));
                var title = new Label { FontAttributes = FontAttributes.Bold };
                title.SetBinding(Label.TextProperty, nameof(AddCustomerRow.Title));
                var details = new Label { FontSize = 12 };
                details.SetBinding(Label.TextProperty, nameof(AddCustomerRow.Details));
                var grid = new Grid { ColumnDefinitions = { new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star) }, ColumnSpacing = 12, Padding = new Thickness(0, 6) };
                grid.Add(mark, 0);
                grid.Add(new VerticalStackLayout { Children = { title, details } }, 1);
                return grid;
            })
        };
        _list.SelectionChanged += OnRowSelected;

        Content = new Grid
        {
            RowDefinitions = { new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Star), new RowDefinition(GridLength.Auto) },
            Padding = 16,
            RowSpacing = 8,
            Children =
            {
                new ScrollView
                {
                    MaximumHeightRequest = 320,
                    Content = new VerticalStackLayout
                    {
                        Spacing = 8,
                        Children = { new Label { Text = Texts.Get("app.add.place"), FontAttributes = FontAttributes.Bold }, _places, _text, _dayButton, _search, _spinner, _message }
                    }
                },
                _list,
                _add
            }
        };
        var grid = (Grid)Content;
        Grid.SetRow(grid.Children[0] as BindableObject ?? throw new InvalidOperationException(), 0);
        Grid.SetRow(_list, 1);
        Grid.SetRow(_add, 2);
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
            await _places.LoadAsync(api, null, cts.Token);
        }
        catch (OperationCanceledException)
        {
            Show(Texts.Get("app.timeout"));
        }
    }

    private void ShowDay() => _dayButton.Text = Texts.Get("app.add.day", ("day", _day.ToString("dddd d MMMM", Texts.Culture)));

    private async Task ChooseDayAsync()
    {
        var today = IraqTime.Today(TimeProvider.System);
        var days = new[] { today }.Concat(DailyPlan.PostponeChoices(today)).ToList();
        var labels = days.Select(d => d.ToString("dddd d MMMM", Texts.Culture)).ToArray();
        var picked = await DisplayActionSheetAsync(Texts.Get("app.add.title"), Texts.Get("app.cancel"), null, labels);
        var index = picked is null ? -1 : Array.IndexOf(labels, picked);
        if (index < 0) return;
        _day = days[index];
        ShowDay();
        _rows.Clear();
        _add.IsVisible = false;
    }

    private async Task SearchAsync()
    {
        if (!SelfPlanner.CanSearch(_places.PlaceId, _text.Text))
        {
            Show(Texts.Get("app.add.needSearch"));
            return;
        }
        SetBusy(true);
        VisitActionResult result;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            result = await _planner.SearchAsync(_day, _places.PlaceId, _text.Text, cts.Token);
        }
        catch (OperationCanceledException)
        {
            result = VisitActionResult.NetworkError;
        }
        SetBusy(false);
        if (result != VisitActionResult.Success)
        {
            Show(VisitLifecycle.Describe(result));
            if (result == VisitActionResult.SessionExpired) AppLanguage.ShowRoot(new MainPage());
            return;
        }
        Fill();
        Show(_rows.Count == 0 ? Texts.Get("app.add.none") : null);
    }

    private void Fill()
    {
        _rows.Clear();
        foreach (var c in _planner.Candidates) _rows.Add(new AddCustomerRow(c, Describe(c)));
        UpdateAddButton();
    }

    private static string Describe(SelfPlanCandidate c)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(c.Place)) parts.Add(c.Place);
        parts.Add(c.LastVisitDate is { } last ? Texts.Get("app.add.lastVisit", ("date", last.ToString("d MMM yyyy", Texts.Culture))) : Texts.Get("app.add.neverVisited"));
        if (c.CustomerPriority != "normal") parts.Add(DailyPlan.PriorityNote(c.CustomerPriority).TrimStart(' ', '·', ' '));
        if (c.WindowStatus == "outside") parts.Add("⚠ " + Texts.Get("app.add.closed"));
        return string.Join(" · ", parts);
    }

    private void OnRowSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (e.CurrentSelection.FirstOrDefault() is not AddCustomerRow row) return;
        _list.SelectedItem = null;
        row.Selected = _planner.Toggle(row.Candidate.CustomerId);
        UpdateAddButton();
    }

    private void UpdateAddButton()
    {
        _add.IsVisible = _planner.Selected.Count > 0;
        _add.Text = Texts.Get("app.add.addBtn", ("count", _planner.Selected.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)));
    }

    private async Task AddAsync()
    {
        SetBusy(true);
        VisitActionResult result;
        var created = 0;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            (result, created) = await _planner.AddAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            result = VisitActionResult.NetworkError;
        }
        SetBusy(false);
        if (result != VisitActionResult.Success)
        {
            Show(VisitLifecycle.Describe(result));
            if (result == VisitActionResult.SessionExpired) AppLanguage.ShowRoot(new MainPage());
            return;
        }
        Fill();
        Show(Texts.Get("app.add.added", ("count", created.ToString(System.Globalization.CultureInfo.InvariantCulture))));
    }

    private void Show(string? text)
    {
        _message.Text = text;
        _message.IsVisible = !string.IsNullOrEmpty(text);
    }

    private void SetBusy(bool busy)
    {
        _spinner.IsVisible = _spinner.IsRunning = busy;
        _search.IsEnabled = _add.IsEnabled = _dayButton.IsEnabled = !busy;
        if (busy) _message.IsVisible = false;
    }
}
