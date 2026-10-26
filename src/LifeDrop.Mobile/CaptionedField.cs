using LifeDrop.Mobile.Visits;
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

// Opening WhatsApp from a phone number (customer phones, the support line). The link is https://wa.me/..., so the phone hands it to WhatsApp (or its web page).
public static class WhatsAppUi
{
    public static async Task OpenAsync(string? phone)
    {
        if (WhatsAppLink.Url(phone) is { } url) await Launcher.OpenAsync(new Uri(url));
    }

    // A label that looks and works like a link to the phone's WhatsApp chat.
    public static void MakeLink(Label label, string? phone)
    {
        label.TextDecorations = TextDecorations.Underline;
        label.GestureRecognizers.Clear();
        var tap = new TapGestureRecognizer();
        tap.Tapped += async (_, _) => await OpenAsync(phone);
        label.GestureRecognizers.Add(tap);
    }
}

// A phone field with a WhatsApp button under it that shows while the text is a usable number.
public sealed class PhoneWithWhatsApp : VerticalStackLayout
{
    public PhoneWithWhatsApp(Entry entry)
    {
        Spacing = 6;
        var button = new Button { Text = Texts.Get("app.customer.whatsapp"), HorizontalOptions = LayoutOptions.Start, IsVisible = false };
        button.Clicked += async (_, _) => await WhatsAppUi.OpenAsync(entry.Text);
        entry.TextChanged += (_, _) => button.IsVisible = WhatsAppLink.Digits(entry.Text) is not null;
        Children.Add(entry);
        Children.Add(button);
        button.IsVisible = WhatsAppLink.Digits(entry.Text) is not null;
    }
}
