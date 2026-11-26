using LifeDrop.Mobile.Localization;

namespace LifeDrop.Mobile;

// Choose one day with a date picker (any day between `min` and `max`); used to see another day of the plan and to move a visit to any day.
public sealed class DayPickerPage : ContentPage
{
    public DayPickerPage(string title, DateOnly min, DateOnly max, DateOnly start, Func<DateOnly, Task> picked)
    {
        FlowDirection = AppLanguage.Direction;
        Title = title;
        var picker = new DatePicker
        {
            MinimumDate = min.ToDateTime(TimeOnly.MinValue),
            MaximumDate = max.ToDateTime(TimeOnly.MinValue),
            Date = (start < min ? min : start > max ? max : start).ToDateTime(TimeOnly.MinValue),
            Format = "dddd d MMMM yyyy"
        };
        var ok = new Button { Text = Texts.Get("app.ok") };
        ok.Clicked += async (_, _) =>
        {
            var day = DateOnly.FromDateTime(picker.Date ?? DateTime.Today);
            await Navigation.PopAsync();
            await picked(day);
        };
        Content = new VerticalStackLayout { Padding = 16, Spacing = 16, Children = { new Label { Text = title, FontAttributes = FontAttributes.Bold, FontSize = 18 }, picker, ok } };
    }
}
