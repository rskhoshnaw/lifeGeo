using LifeDrop.Mobile.Localization;
using LifeDrop.Mobile.Visits;

namespace LifeDrop.Mobile;

// The doctor fields of a customer (plan steps 4.27-b and the editing of a registered doctor): clinic or medical complex, specialty, cooperates, Dealer and class. Used by the
// new-customer form and by the customer's page. Every field may stay "not set".
public sealed class DoctorFieldsView : VerticalStackLayout
{
    private readonly Entry _clinic = new() { MaxLength = 150 };
    private readonly Picker _specialty = new();
    private readonly YesNoView _cooperates = new();
    private readonly YesNoView _dealer = new();
    private readonly Picker _class = new();

    public DoctorFieldsView()
    {
        Spacing = 12;
        _clinic.Placeholder = Texts.Get("app.customer.clinic");
        _specialty.Title = Texts.Get("app.customer.specialty");
        _specialty.ItemsSource = new[] { Texts.Get("app.customer.notSet") }.Concat(DoctorOptions.Specialties.Select(k => Texts.Get("specialty." + k))).ToList();
        _class.Title = Texts.Get("app.customer.class");
        _class.ItemsSource = new[] { Texts.Get("app.customer.notSet") }.Concat(DoctorOptions.Classes).ToList();
        Show(null);
        Children.Add(new CaptionedField("app.customer.clinic", _clinic));
        Children.Add(new CaptionedField("app.customer.specialty", _specialty));
        Children.Add(new CaptionedField("app.customer.cooperates", _cooperates));
        Children.Add(new CaptionedField("app.customer.dealer", _dealer));
        Children.Add(new CaptionedField("app.customer.class", _class));
    }

    // Fills the fields from what the server has (null = nothing set).
    public void Show(DoctorDetails? details)
    {
        _clinic.Text = details?.ClinicName ?? "";
        _specialty.SelectedIndex = details?.Specialty is { } s ? Math.Max(0, DoctorOptions.Specialties.ToList().IndexOf(s) + 1) : 0;
        _class.SelectedIndex = details?.CustomerClass is { } c ? Math.Max(0, DoctorOptions.Classes.ToList().IndexOf(c) + 1) : 0;
        _cooperates.Value = details?.Cooperates;
        _dealer.Value = details?.Dealer;
    }

    public DoctorDetails Read() => new(_clinic.Text,
        _specialty.SelectedIndex > 0 ? DoctorOptions.Specialties[_specialty.SelectedIndex - 1] : null, _cooperates.Value, _dealer.Value,
        _class.SelectedIndex > 0 ? DoctorOptions.Classes[_class.SelectedIndex - 1] : null);
}
