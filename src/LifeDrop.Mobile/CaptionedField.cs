using LifeDrop.Mobile.Localization;

namespace LifeDrop.Mobile;

// A form field with a caption that stays visible above it (a placeholder disappears once the field is filled).
// The caption's box follows the field's IsVisible, so a page can still hide a field as before.
public sealed class CaptionedField : VerticalStackLayout
{
    public Label Caption { get; }

    public CaptionedField(string textKey, View field)
    {
        Spacing = 4;
        Caption = new Label { Text = Texts.Get(textKey), FontAttributes = FontAttributes.Bold, FontSize = 14 };
        Children.Add(Caption);
        Children.Add(field);
        IsVisible = field.IsVisible;
        field.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IsVisible)) IsVisible = field.IsVisible;
        };
    }
}

// A yes / no choice as two radio buttons; neither selected means "not set".
public sealed class YesNoView : HorizontalStackLayout
{
    private static int _groups;
    private readonly RadioButton _yes = new() { Content = Texts.Get("app.customer.yes") };
    private readonly RadioButton _no = new() { Content = Texts.Get("app.customer.no") };

    public YesNoView()
    {
        Spacing = 24;
        var group = "yesno" + Interlocked.Increment(ref _groups);
        _yes.GroupName = _no.GroupName = group;
        Children.Add(_yes);
        Children.Add(_no);
    }

    public bool? Value
    {
        get => _yes.IsChecked ? true : _no.IsChecked ? false : null;
        set
        {
            _yes.IsChecked = value == true;
            _no.IsChecked = value == false;
        }
    }
}
