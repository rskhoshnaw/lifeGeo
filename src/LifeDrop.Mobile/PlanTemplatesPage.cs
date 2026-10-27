using LifeDrop.Mobile.Localization;
using LifeDrop.Mobile.Visits;

namespace LifeDrop.Mobile;

// Plan templates (plan step 5.8): plan one week, save it as a template, and apply it to the coming weeks, so the regular rounds do not have to be planned again. The server plans through the
// normal plan generation: customers already planned on a day or outside their accepted hours are left out. Online only.
public sealed class PlanTemplatesPage : ContentPage
{
    private readonly Entry _name = new() { MaxLength = 100 };
    private readonly ChoiceField _week = new(Texts.Get("app.planTemplates.week"));
    private readonly ChoiceField _weeks = new(Texts.Get("app.planTemplates.weeks"));
    private readonly Button _save = new();
    private readonly VerticalStackLayout _list = new() { Spacing = 8 };
    private readonly ActivityIndicator _spinner = new() { IsVisible = false };
    private readonly Label _message = new() { IsVisible = false };
    private bool _busy;

    public PlanTemplatesPage()
    {
        FlowDirection = AppLanguage.Direction;
        Title = Texts.Get("app.planTemplates.title");
        _name.Placeholder = Texts.Get("app.planTemplates.name");
        _save.Text = Texts.Get("app.planTemplates.save");
        _week.Items = new[] { Texts.Get("app.planTemplates.thisWeek"), Texts.Get("app.planTemplates.lastWeek"), Texts.Get("app.planTemplates.nextWeek") };
        _weeks.Items = Enumerable.Range(1, 8).Select(n => n.ToString()).ToList();
        _weeks.SelectedIndex = 3;   // four weeks
        _save.Clicked += async (_, _) => await SaveAsync();
        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = 12,
                Spacing = 8,
                Children = { new Label { Text = Texts.Get("app.planTemplates.hint") }, _name, _week, _save, new Label { Text = Texts.Get("app.planTemplates.weeks") }, _weeks, _spinner, _message, _list }
            }
        };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await LoadAsync();
    }

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Now);

    private async Task LoadAsync()
    {
        if (TrackingHost.PlanTemplatesApi is not { } api)
        {
            AppLanguage.ShowRoot(new MainPage());
            return;
        }
        IReadOnlyList<PlanTemplateItem> templates = [];
        var status = await RunAsync(async c =>
        {
            var (result, items) = await api.GetAsync(c);
            if (result == VisitActionResult.Success) templates = items;
            return result;
        });
        if (status != VisitActionResult.Success) return;
        _list.Children.Clear();
        foreach (var template in templates)
        {
            var id = template.Id;
            var apply = new Button { Text = Texts.Get("app.planTemplates.apply") };
            apply.Clicked += async (_, _) => await ApplyAsync(id);
            var delete = new Button { Text = Texts.Get("app.planTemplates.delete") };
            delete.Clicked += async (_, _) => await DeleteAsync(id);
            _list.Children.Add(new Border
            {
                Padding = 10,
                Content = new VerticalStackLayout
                {
                    Spacing = 4,
                    Children =
                    {
                        new Label { Text = template.Name, FontAttributes = FontAttributes.Bold },
                        new Label { Text = Texts.Get("app.planTemplates.counts", ("customers", template.Customers.ToString()), ("days", template.Days.ToString())), FontSize = 12 },
                        apply,
                        delete
                    }
                }
            });
        }
        Show(templates.Count == 0 ? Texts.Get("app.planTemplates.none") : null);
    }

    private async Task SaveAsync()
    {
        if (TrackingHost.PlanTemplatesApi is not { } api) return;
        if (string.IsNullOrWhiteSpace(_name.Text))
        {
            Show(Texts.Get("app.planTemplates.needName"));
            return;
        }
        var offset = _week.SelectedIndex switch { 1 => -1, 2 => 1, _ => 0 };
        var status = await RunAsync(c => api.SaveAsync(_name.Text.Trim(), PlanTemplateWeeks.WeekStart(Today, offset), c));
        if (status != VisitActionResult.Success) return;
        _name.Text = "";
        await LoadAsync();
        Show(Texts.Get("app.planTemplates.saved"));
    }

    private async Task ApplyAsync(Guid id)
    {
        if (TrackingHost.PlanTemplatesApi is not { } api) return;
        PlanTemplateApplied? applied = null;
        var weeks = Math.Max(1, _weeks.SelectedIndex + 1);
        var status = await RunAsync(async c =>
        {
            var (result, data) = await api.ApplyAsync(id, Today, weeks, c);
            applied = data;
            return result;
        });
        if (status == VisitActionResult.Success && applied is not null)
            Show(Texts.Get("app.planTemplates.applied", ("created", applied.Created.ToString()), ("skipped", applied.Skipped.ToString()), ("days", applied.Days.ToString())));
    }

    private async Task DeleteAsync(Guid id)
    {
        if (TrackingHost.PlanTemplatesApi is not { } api) return;
        var status = await RunAsync(c => api.DeleteAsync(id, c));
        if (status == VisitActionResult.Success) await LoadAsync();
    }

    private async Task<VisitActionResult> RunAsync(Func<CancellationToken, Task<VisitActionResult>> call)
    {
        if (_busy) return VisitActionResult.NetworkError;
        _busy = true;
        _spinner.IsVisible = _spinner.IsRunning = true;
        VisitActionResult result;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            result = await call(cts.Token);
        }
        catch (OperationCanceledException)
        {
            result = VisitActionResult.NetworkError;
        }
        _busy = false;
        _spinner.IsVisible = _spinner.IsRunning = false;
        if (result != VisitActionResult.Success)
        {
            Show(VisitLifecycle.Describe(result));
            if (result == VisitActionResult.SessionExpired) AppLanguage.ShowRoot(new MainPage());
        }
        return result;
    }

    private void Show(string? text)
    {
        _message.Text = text;
        _message.IsVisible = !string.IsNullOrEmpty(text);
    }
}
