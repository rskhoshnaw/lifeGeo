using LifeDrop.Mobile.Localization;
using LifeDrop.Mobile.Visits;

namespace LifeDrop.Mobile;

// "Which day of the week" and "which hours" of the new-customer form (plan step 5.3). Each is "always" or a choice: any number of days, or from - to.
// The week starts on Saturday, as in Iraq; the values are the server's (0 = Sunday .. 6 = Saturday).
public sealed class VisitWindowsView : VerticalStackLayout
{
    private static int _groups;
    private static readonly int[] Days = [6, 0, 1, 2, 3, 4, 5];

    private readonly RadioButton _anyDay = new() { Content = Texts.Get("app.customer.always"), IsChecked = true };
    private readonly RadioButton _someDays = new() { Content = Texts.Get("app.customer.selectedDays") };
    private readonly RadioButton _anyHour = new() { Content = Texts.Get("app.customer.always"), IsChecked = true };
    private readonly RadioButton _someHours = new() { Content = Texts.Get("app.customer.hoursRange") };
    private readonly FlexLayout _dayBoxes = new() { Wrap = Microsoft.Maui.Layouts.FlexWrap.Wrap, IsVisible = false };
    private readonly Dictionary<int, CheckBox> _checks = new();
    private readonly TimePicker _from = new() { Time = new TimeSpan(9, 0, 0) };
    private readonly TimePicker _to = new() { Time = new TimeSpan(17, 0, 0) };
    private readonly HorizontalStackLayout _hourBoxes = new() { Spacing = 8, IsVisible = false };

    public VisitWindowsView()
    {
        Spacing = 12;
        var id = Interlocked.Increment(ref _groups);
        _anyDay.GroupName = _someDays.GroupName = "days" + id;
        _anyHour.GroupName = _someHours.GroupName = "hours" + id;
        foreach (var day in Days)
        {
            var check = new CheckBox();
            _checks[day] = check;
            _dayBoxes.Children.Add(new HorizontalStackLayout { Spacing = 2, Margin = new Thickness(0, 0, 12, 0), Children = { check, new Label { Text = Texts.Get("app.weekday." + day), VerticalOptions = LayoutOptions.Center } } });
        }
        _someDays.CheckedChanged += (_, _) => _dayBoxes.IsVisible = _someDays.IsChecked;
        _someHours.CheckedChanged += (_, _) => _hourBoxes.IsVisible = _someHours.IsChecked;
        _hourBoxes.Children.Add(new Label { Text = Texts.Get("app.customer.hoursFrom"), VerticalOptions = LayoutOptions.Center });
        _hourBoxes.Children.Add(_from);
        _hourBoxes.Children.Add(new Label { Text = Texts.Get("app.customer.hoursTo"), VerticalOptions = LayoutOptions.Center });
        _hourBoxes.Children.Add(_to);

        Children.Add(new CaptionedField("app.customer.visitDays", new VerticalStackLayout { Spacing = 4, Children = { new HorizontalStackLayout { Spacing = 24, Children = { _anyDay, _someDays } }, _dayBoxes } }));
        Children.Add(new CaptionedField("app.customer.visitHours", new VerticalStackLayout { Spacing = 4, Children = { new HorizontalStackLayout { Spacing = 24, Children = { _anyHour, _someHours } }, _hourBoxes } }));
    }

    public (VisitWindowPlanResult Result, IReadOnlyList<VisitWindowItem> Windows) Read() =>
        VisitWindowPlanner.Build(_anyDay.IsChecked, _checks.Where(c => c.Value.IsChecked).Select(c => c.Key).ToList(), _anyHour.IsChecked, (TimeSpan)_from.Time!, (TimeSpan)_to.Time!);
}
