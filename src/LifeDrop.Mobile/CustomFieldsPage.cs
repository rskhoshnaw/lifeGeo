using LifeDrop.Mobile.Localization;
using LifeDrop.Mobile.Visits;

namespace LifeDrop.Mobile;

// The extra fields of the visit report (plan step 3.2, part 2): one input per field the company defined for this visit type (text, number, choice, yes/no, date).
// Saved while the visit is in progress; online only. The rules are in CustomAnswersDraft (tested).
public sealed class CustomFieldsPage : ContentPage
{
    private readonly Guid _visitId;
    private readonly string? _customerName;
    private readonly string _visitType;
    private readonly VerticalStackLayout _fieldsLayout = new() { Spacing = 10 };
    private readonly Button _save = new() { IsEnabled = false, IsVisible = false };
    private readonly ActivityIndicator _spinner = new() { IsVisible = false };
    private readonly Label _message = new() { IsVisible = false };
    private CustomAnswersDraft? _draft;
    private bool _busy;
    private bool _loaded;

    public CustomFieldsPage(Guid visitId, string? customerName, string visitType)
    {
        _visitId = visitId;
        _customerName = customerName;
        _visitType = visitType;
        FlowDirection = AppLanguage.Direction;
        Title = Texts.Get("app.custom.title");
        _save.Text = Texts.Get("app.custom.save");
        _save.Clicked += async (_, _) => await SaveAsync();
        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = 16,
                Spacing = 10,
                Children = { new Label { Text = customerName ?? "", FontAttributes = FontAttributes.Bold, FontSize = 18 }, _fieldsLayout, _save, _spinner, _message }
            }
        };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_loaded) return;
        var api = TrackingHost.CustomFieldsApi;
        if (api is null) return;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var (result, fields) = await api.GetFieldsAsync(_visitType, cts.Token);
            if (result != VisitActionResult.Success)
            {
                Show(VisitLifecycle.Describe(result));
                if (result == VisitActionResult.SessionExpired) AppLanguage.ShowRoot(new MainPage());
                return;
            }
            var (answerResult, saved) = await api.GetAnswersAsync(_visitId, cts.Token);
            var draft = new CustomAnswersDraft(fields);
            if (answerResult == VisitActionResult.Success)
                foreach (var (id, value) in saved) draft.Set(id, value);
            _draft = draft;
            Build(fields, draft);
            _loaded = true;
        }
        catch (OperationCanceledException)
        {
            Show(VisitLifecycle.Describe(VisitActionResult.NetworkError));
        }
    }

    private void Build(IReadOnlyList<CustomField> fields, CustomAnswersDraft draft)
    {
        _fieldsLayout.Children.Clear();
        if (fields.Count == 0)
        {
            Show(Texts.Get("app.custom.none"));
            return;
        }
        var yes = Texts.Get("app.custom.yes");
        var no = Texts.Get("app.custom.no");
        var choose = Texts.Get("app.custom.choose");
        foreach (var field in fields)
        {
            var title = field.LabelFor(Texts.Language) + (field.IsRequired ? $" ({Texts.Get("app.custom.required")})" : "");
            _fieldsLayout.Children.Add(new Label { Text = title, FontAttributes = FontAttributes.Bold });
            var current = draft.Get(field.Id);
            switch (field.Type)
            {
                case "choice":
                {
                    var picker = new Picker { Title = choose, ItemsSource = field.Options.ToList() };
                    if (current is not null) picker.SelectedIndex = field.Options.ToList().IndexOf(current);
                    picker.SelectedIndexChanged += (_, _) => { draft.Set(field.Id, picker.SelectedIndex >= 0 ? field.Options[picker.SelectedIndex] : null); Refresh(); };
                    _fieldsLayout.Children.Add(picker);
                    break;
                }
                case "yes_no":
                {
                    var picker = new Picker { Title = choose, ItemsSource = new List<string> { yes, no } };
                    picker.SelectedIndex = current switch { "true" => 0, "false" => 1, _ => -1 };
                    picker.SelectedIndexChanged += (_, _) => { draft.Set(field.Id, picker.SelectedIndex switch { 0 => "true", 1 => "false", _ => null }); Refresh(); };
                    _fieldsLayout.Children.Add(picker);
                    break;
                }
                default:
                {
                    var entry = new Entry
                    {
                        Text = current ?? "",
                        MaxLength = field.Type == "text" ? CustomAnswersDraft.MaxText : 40,
                        Keyboard = field.Type == "number" ? Keyboard.Numeric : Keyboard.Default,
                        Placeholder = field.Type == "date" ? Texts.Get("app.custom.date") : ""
                    };
                    entry.TextChanged += (_, e) => { draft.Set(field.Id, e.NewTextValue); Refresh(); };
                    _fieldsLayout.Children.Add(entry);
                    break;
                }
            }
        }
        _save.IsVisible = true;
        Refresh();
    }

    private void Refresh() => _save.IsEnabled = !_busy && _draft is { CanSave: true };

    private void Show(string? text)
    {
        _message.Text = text;
        _message.IsVisible = !string.IsNullOrEmpty(text);
    }

    private async Task SaveAsync()
    {
        var api = TrackingHost.CustomFieldsApi;
        if (api is null || _draft is null || _busy) return;
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
        Show(result == VisitActionResult.Success ? Texts.Get("app.custom.saved") : VisitLifecycle.Describe(result));
        if (result == VisitActionResult.SessionExpired) AppLanguage.ShowRoot(new MainPage());
        Refresh();
    }
}
