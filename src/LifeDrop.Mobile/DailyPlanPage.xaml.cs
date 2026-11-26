using LifeDrop.Mobile.Localization;
using LifeDrop.Mobile.Visits;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace LifeDrop.Mobile;

// One row of the plan, already formatted for display.
public sealed record VisitRow(PlannedVisit Visit, string Customer, string Time, string Details, string Status,
    bool CanMoveUp, bool CanMoveDown, bool CanPostpone, string MoveUpText, string MoveDownText, string PostponeText);

// Daily plan (plan step 1.10, PRD §5.1): today's visits (Iraq date) from GET /visits. Loading, empty and
// error states come from DailyPlan; a failed refresh keeps the last list and says it may be out of date.
public partial class DailyPlanPage : ContentPage
{
    private DailyPlan? _plan;
    private IVisitsApiClient? _api;

    public DailyPlanPage()
    {
        InitializeComponent();
        FlowDirection = AppLanguage.Direction;
        Title = Texts.Get("app.plan.title");
        RetryBtn.Text = Texts.Get("app.tryAgain");
        AddBtn.Text = Texts.Get("app.plan.add");
        TodayBtn.Text = Texts.Get("app.plan.backToToday");
        PickDayBtn.Text = Texts.Get("app.plan.pickDay");
        SelectBtn.Text = Texts.Get("app.plan.select");
        MoveSelectedBtn.Text = Texts.Get("app.plan.moveSelected", ("count", "0"));
    }

    // "Move several": rows are ticked, then one day is chosen for all of them (a day off or a holiday).
    private bool _selecting;

    private void OnSelectClicked(object? sender, EventArgs e)
    {
        _selecting = !_selecting;
        VisitList.SelectedItems?.Clear();
        VisitList.SelectionMode = _selecting ? SelectionMode.Multiple : SelectionMode.Single;
        SelectBtn.Text = Texts.Get(_selecting ? "app.cancel" : "app.plan.select");
        UpdateSelectionBar();
    }

    private IReadOnlyList<VisitRow> SelectedRows() => VisitList.SelectedItems?.OfType<VisitRow>().Where(r => r.CanPostpone).ToList() ?? new List<VisitRow>();

    private void UpdateSelectionBar()
    {
        var count = _selecting ? SelectedRows().Count : 0;
        MoveSelectedBtn.IsVisible = _selecting && count > 0;
        MoveSelectedBtn.Text = Texts.Get("app.plan.moveSelected", ("count", count.ToString()));
    }

    private async void OnMoveSelectedClicked(object? sender, EventArgs e)
    {
        var rows = SelectedRows();
        if (_plan is null || rows.Count == 0) return;
        var today = IraqTime.Today(TimeProvider.System);
        var start = _plan.Date.AddDays(1) < today ? today : _plan.Date.AddDays(1);
        await Navigation.PushAsync(new DayPickerPage(Texts.Get("app.plan.moveSelected", ("count", rows.Count.ToString())), today, today.AddDays(120), start, async day => await MoveManyAsync(rows, day)));
    }

    private async Task MoveManyAsync(IReadOnlyList<VisitRow> rows, DateOnly day)
    {
        if (_plan is null) return;
        Spinner.IsVisible = Spinner.IsRunning = true;
        (int Moved, int Failed, VisitActionResult? Stopped) outcome;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            outcome = await _plan.PostponeManyAsync(rows.Select(r => r.Visit.Id).ToList(), day, null, cts.Token);
        }
        catch (OperationCanceledException)
        {
            outcome = (0, rows.Count, VisitActionResult.NetworkError);
        }
        finally
        {
            Spinner.IsVisible = Spinner.IsRunning = false;
        }
        _selecting = false;
        VisitList.SelectedItems?.Clear();
        VisitList.SelectionMode = SelectionMode.Single;
        SelectBtn.Text = Texts.Get("app.plan.select");
        UpdateSelectionBar();
        Render();
        MessageLabel.Text = outcome.Stopped == VisitActionResult.SessionExpired ? Texts.Get("app.sessionEnded")
            : outcome.Failed == 0 && outcome.Stopped is null ? Texts.Get("app.plan.movedMany", ("count", outcome.Moved.ToString()), ("date", day.ToString("dddd d MMMM", Texts.Culture)))
            : Texts.Get("app.plan.movedSome", ("moved", outcome.Moved.ToString()), ("failed", outcome.Failed.ToString()));
        MessageLabel.IsVisible = true;
        if (outcome.Stopped == VisitActionResult.SessionExpired) ShowSessionEnded();
    }

    private DateOnly ShownDay => _plan?.Selected ?? IraqTime.Today(TimeProvider.System);

    private async Task ShowDayAsync(DateOnly day)
    {
        if (_plan is null) return;
        var today = IraqTime.Today(TimeProvider.System);
        if (day < today.AddDays(-30)) day = today.AddDays(-30);   // the past is only for looking back a month
        _plan.SelectDay(day);
        await LoadAsync();
    }

    private async void OnPrevDay(object? sender, EventArgs e) => await ShowDayAsync(ShownDay.AddDays(-1));

    private async void OnNextDay(object? sender, EventArgs e) => await ShowDayAsync(ShownDay.AddDays(1));

    private async void OnToday(object? sender, EventArgs e) => await ShowDayAsync(IraqTime.Today(TimeProvider.System));

    private async void OnPickDay(object? sender, EventArgs e)
    {
        var today = IraqTime.Today(TimeProvider.System);
        await Navigation.PushAsync(new DayPickerPage(Texts.Get("app.plan.pickDay"), today.AddDays(-30), today.AddDays(120), ShownDay, async day => await ShowDayAsync(day)));
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        // Also after returning from a visit, so its new status shows.
        await LoadAsync();
    }

    private async void OnRefreshing(object? sender, EventArgs e)
    {
        await LoadAsync();
        Refresher.IsRefreshing = false;
    }

    private async void OnRetryClicked(object? sender, EventArgs e) => await LoadAsync();

    // Adding customers to the user's own plan (plan step 1.25), for the day on screen.
    private async void OnAddClicked(object? sender, EventArgs e)
    {
        TrackingHost.GetOrCreate();
        if (TrackingHost.VisitsApi is not ISelfPlanApiClient api)
        {
            ShowSessionEnded();
            return;
        }
        await Navigation.PushAsync(new AddToPlanPage(api, _plan?.Date is { } day && day != default ? day : IraqTime.Today(TimeProvider.System)));
    }

    // Plan changes (plan step 1.23): the server must accept them, so they need a connection.
    private async void OnMoveUp(object? sender, EventArgs e) => await ChangeAsync(sender, (plan, row) => plan.MoveAsync(row.Visit.Id, -1));

    private async void OnMoveDown(object? sender, EventArgs e) => await ChangeAsync(sender, (plan, row) => plan.MoveAsync(row.Visit.Id, 1));

    private async void OnPostpone(object? sender, EventArgs e)
    {
        if (_plan is null || (sender as BindableObject)?.BindingContext is not VisitRow row) return;
        var choices = DailyPlan.PostponeChoices(_plan.Date);
        var labels = choices.Select(d => d.ToString("dddd d MMMM", Texts.Culture)).ToArray();
        var other = Texts.Get("app.plan.otherDay");
        var picked = await DisplayActionSheet(Texts.Get("app.plan.postponeTitle"), Texts.Get("app.cancel"), null, labels.Append(other).ToArray());
        if (picked is null) return;
        if (picked == other)
        {
            // Any day from tomorrow on (up to four months): a page with a date to choose.
            var visitRow = row;
            var today = IraqTime.Today(TimeProvider.System);
            await Navigation.PushAsync(new DayPickerPage(Texts.Get("app.plan.postponeTitle"), today, today.AddDays(120), _plan.Date.AddDays(1) < today ? today : _plan.Date.AddDays(1),
                async day => await ChangeAsync(visitRow, (plan, visit) => plan.PostponeAsync(visit.Visit.Id, day, null))));
            return;
        }
        var index = Array.IndexOf(labels, picked);
        if (index < 0) return;
        await ChangeAsync(sender, (plan, visit) => plan.PostponeAsync(visit.Visit.Id, choices[index], null));
    }

    private Task ChangeAsync(object? sender, Func<DailyPlan, VisitRow, Task<VisitActionResult>> change) =>
        (sender as BindableObject)?.BindingContext is VisitRow row ? ChangeAsync(row, change) : Task.CompletedTask;

    private async Task ChangeAsync(VisitRow row, Func<DailyPlan, VisitRow, Task<VisitActionResult>> change)
    {
        if (_plan is null) return;
        var plan = _plan;
        Spinner.IsVisible = Spinner.IsRunning = true;
        VisitActionResult result;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            result = await change(plan, row);
        }
        catch (OperationCanceledException)
        {
            result = VisitActionResult.NetworkError;
        }
        finally
        {
            Spinner.IsVisible = Spinner.IsRunning = false;
        }

        Render();
        var message = DailyPlan.ChangeMessage(result);
        if (message is null) return;
        MessageLabel.Text = message;
        MessageLabel.IsVisible = true;
        if (result == VisitActionResult.SessionExpired) ShowSessionEnded();
    }

    private async void OnVisitSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (_selecting)
        {
            UpdateSelectionBar();
            return;
        }
        if (e.CurrentSelection.FirstOrDefault() is not VisitRow row || _api is null) return;
        VisitList.SelectedItem = null;
        await Navigation.PushAsync(new VisitDetailsPage(new VisitLifecycle(_api, row.Visit, new MauiLocationSource(), outbox: TrackingHost.Outbox)));
    }

    private async Task LoadAsync()
    {
        if (_plan is null)
        {
            TrackingHost.GetOrCreate();
            _api = TrackingHost.VisitsApi;
            _plan = _api is null ? null : new DailyPlan(_api, TimeProvider.System, TrackingHost.Store);
        }
        // Steps saved offline go first, so the plan shows what the server now has.
        await TrackingHost.SyncQuietlyAsync();
        if (_plan is null)
        {
            ShowSessionEnded();
            return;
        }

        RetryBtn.IsEnabled = false;
        Spinner.IsVisible = Spinner.IsRunning = !Refresher.IsRefreshing;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await _plan.LoadAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            // State is unchanged; the user can retry.
        }
        finally
        {
            Spinner.IsVisible = Spinner.IsRunning = false;
            RetryBtn.IsEnabled = true;
        }
        Render();
    }

    private void Render()
    {
        var plan = _plan!;
        if (plan.Date != default)
        {
            var text = plan.Date.ToString("dddd d MMMM yyyy", Texts.Culture);
            DateLabel.Text = plan.Selected is null ? Texts.Get("app.plan.today", ("date", text)) : text;
            TodayBtn.IsVisible = plan.Selected is not null;
        }
        VisitList.ItemsSource = plan.Visits.Select((v, i) => ToRow(v, i, plan.Visits.Count)).ToList();
        MessageLabel.Text = plan.Message;
        MessageLabel.IsVisible = plan.Message is not null;
        RetryBtn.IsVisible = plan.State == DailyPlanState.Error && plan.Error != PlanLoadStatus.SessionExpired;

        if (plan.Error == PlanLoadStatus.SessionExpired) ShowSessionEnded();
    }

    private void ShowSessionEnded()
    {
        MessageLabel.Text = Texts.Get("app.sessionEnded");
        MessageLabel.IsVisible = true;
        RetryBtn.IsVisible = false;
        AppLanguage.ShowRoot(new MainPage());
    }

    private static VisitRow ToRow(PlannedVisit v, int index, int count)
    {
        var customer = string.IsNullOrWhiteSpace(v.CustomerName) ? Texts.Get("app.customer") : v.CustomerName!;
        if (!string.IsNullOrWhiteSpace(v.CustomerCode)) customer += $" ({v.CustomerCode})";
        var details = DailyPlan.TypeLabel(v.VisitType);
        if (v.Priority == "high") details += " · " + Texts.Get("app.plan.highPriority");
        if (v.IsUnplanned) details += " · " + Texts.Get("app.plan.unplanned");
        details += DailyPlan.PlanNotes(v);
        // Steps saved on the phone move the status forward until the server has them.
        var outbox = TrackingHost.Outbox;
        var waiting = outbox?.HasPending(v.Id) == true;
        var status = waiting ? VisitLifecycle.LocalStatus(outbox!, v) : v.Status;
        return new VisitRow(v, customer, v.PlannedTime?.ToString("HH:mm") ?? Texts.Get("app.plan.anyTime"), details,
            DailyPlan.StatusLabel(status) + (waiting ? " · " + Texts.Get("app.plan.waiting") : ""),
            index > 0, index < count - 1, DailyPlan.CanPostpone(v, waiting),
            Texts.Get("app.plan.moveUp") + " " + customer, Texts.Get("app.plan.moveDown") + " " + customer, Texts.Get("app.plan.postpone"));
    }
}
