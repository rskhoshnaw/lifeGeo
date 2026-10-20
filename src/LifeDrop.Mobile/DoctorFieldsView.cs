using LifeDrop.Mobile.Localization;
using LifeDrop.Mobile.Visits;

namespace LifeDrop.Mobile;

// The doctor fields of a customer (plan steps 4.27-b and the editing of a registered doctor): clinic or medical complex, specialty, cooperates, Dealer and class. Used by the
// new-customer form and by the customer's page. Every field may stay "not set".
public sealed class DoctorFieldsView : VerticalStackLayout
{
    private readonly Entry _clinic = new() { MaxLength = 150 };
    private readonly Picker _specialty = new();
    private readonly Picker _cooperates = new();
    private readonly Picker _dealer = new();
    private readonly Picker _class = new();

    public DoctorFieldsView()
    {
        Spacing = 12;
        _clinic.Placeholder = Texts.Get("app.customer.clinic");
        _specialty.Title = Texts.Get("app.customer.specialty");
        _specialty.ItemsSource = new[] { Texts.Get("app.customer.notSet") }.Concat(DoctorOptions.Specialties.Select(k => Texts.Get("specialty." + k))).ToList();
        _class.Title = Texts.Get("app.customer.class");
        _class.ItemsSource = new[] { Texts.Get("app.customer.notSet") }.Concat(DoctorOptions.Classes).ToList();
        foreach (var (picker, title) in new[] { (_cooperates, Texts.Get("app.customer.cooperates")), (_dealer, Texts.Get("app.customer.dealer")) })
        {
            picker.Title = title;
            picker.ItemsSource = new List<string> { Texts.Get("app.customer.notSet"), Texts.Get("app.customer.yes"), Texts.Get("app.customer.no") };
        }
        Show(null);
        Children.Add(_clinic);
        Children.Add(_specialty);
        Children.Add(_cooperates);
        Children.Add(_dealer);
        Children.Add(_class);
    }

    // Fills the fields from what the server has (null = nothing set).
    public void Show(DoctorDetails? details)
    {
        _clinic.Text = details?.ClinicName ?? "";
        _specialty.SelectedIndex = details?.Specialty is { } s ? Math.Max(0, DoctorOptions.Specialties.ToList().IndexOf(s) + 1) : 0;
        _class.SelectedIndex = details?.CustomerClass is { } c ? Math.Max(0, DoctorOptions.Classes.ToList().IndexOf(c) + 1) : 0;
        _cooperates.SelectedIndex = Index(details?.Cooperates);
        _dealer.SelectedIndex = Index(details?.Dealer);
    }

    public DoctorDetails Read() => new(_clinic.Text,
        _specialty.SelectedIndex > 0 ? DoctorOptions.Specialties[_specialty.SelectedIndex - 1] : null, Value(_cooperates), Value(_dealer),
        _class.SelectedIndex > 0 ? DoctorOptions.Classes[_class.SelectedIndex - 1] : null);

    private static int Index(bool? value) => value switch { true => 1, false => 2, _ => 0 };

    private static bool? Value(Picker picker) => picker.SelectedIndex switch { 1 => true, 2 => false, _ => null };
}
