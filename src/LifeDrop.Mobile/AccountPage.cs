using LifeDrop.Mobile.Localization;
using LifeDrop.Mobile.Visits;

namespace LifeDrop.Mobile;

// "My account" (plan step 4.46): who is signed in, the company, the app version and changing one's own password. The current password is needed; the user's other
// sessions end when the new one is set. Online only.
public sealed class AccountPage : ContentPage
{
    private readonly Label _who = new() { FontAttributes = FontAttributes.Bold };
    private readonly Label _roles = new();
    private readonly Label _company = new();
    private readonly Label _version = new() { FontSize = 12, TextColor = Colors.Gray };
    private readonly Entry _current = new() { IsPassword = true, MaxLength = PasswordRules.MaxLength };
    private readonly Entry _new = new() { IsPassword = true, MaxLength = PasswordRules.MaxLength };
    private readonly Entry _repeat = new() { IsPassword = true, MaxLength = PasswordRules.MaxLength };
    private readonly Button _change = new();
    private readonly ActivityIndicator _spinner = new() { IsVisible = false };
    private readonly Label _message = new() { IsVisible = false };
    private bool _loaded;

    public AccountPage()
    {
        FlowDirection = AppLanguage.Direction;
        Title = Texts.Get("app.account.title");
        _company.Text = Texts.Get("app.account.company", ("code", Preferences.Get("CompanyCode", "")));
        _version.Text = Texts.Get("app.account.version", ("version", AppInfo.VersionString));
        _current.Placeholder = Texts.Get("app.account.current");
        _new.Placeholder = Texts.Get("app.account.new", ("min", PasswordRules.MinLength));
        _repeat.Placeholder = Texts.Get("app.account.repeat");
        _change.Text = Texts.Get("app.account.change");
        _change.Clicked += async (_, _) => await ChangeAsync();
        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = 16,
                Spacing = 12,
                Children =
                {
                    _who, _roles, _company, _version,
                    new Label { Text = Texts.Get("app.account.passwordTitle"), FontAttributes = FontAttributes.Bold },
                    _current, _new, _repeat, _change, _spinner, _message
                }
            }
        };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_loaded) return;
        var api = TrackingHost.AccountApi;
        if (api is null)
        {
            AppLanguage.ShowRoot(new MainPage());
            return;
        }
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var (result, account) = await api.GetAccountAsync(cts.Token);
            if (result == VisitActionResult.SessionExpired)
            {
                AppLanguage.ShowRoot(new MainPage());
                return;
            }
            if (result != VisitActionResult.Success || account is null)
            {
                Show(VisitLifecycle.Describe(result));
                return;
            }
            _loaded = true;
            _who.Text = account.Email ?? "";
            _roles.Text = string.Join(", ", account.Roles);
        }
        catch (OperationCanceledException)
        {
            Show(Texts.Get("app.timeout"));
        }
    }

    private async Task ChangeAsync()
    {
        var api = TrackingHost.AccountApi;
        if (api is null) return;
        if (PasswordRules.Check(_current.Text, _new.Text, _repeat.Text) is { } problem)
        {
            Show(Texts.Get(_new.Text != _repeat.Text ? "app.account.mismatch" : "app.account.tooShort", ("min", PasswordRules.MinLength)));
            return;
        }
        _spinner.IsVisible = _spinner.IsRunning = true;
        _change.IsEnabled = false;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var result = await api.ChangePasswordAsync(_current.Text!, _new.Text!, cts.Token);
            if (result == VisitActionResult.SessionExpired)
            {
                AppLanguage.ShowRoot(new MainPage());
                return;
            }
            if (result != VisitActionResult.Success)
            {
                Show(VisitLifecycle.Describe(result));
                return;
            }
            _current.Text = _new.Text = _repeat.Text = "";
            Show(Texts.Get("app.account.changed"));
        }
        catch (OperationCanceledException)
        {
            Show(Texts.Get("app.timeout"));
        }
        finally
        {
            _spinner.IsVisible = _spinner.IsRunning = false;
            _change.IsEnabled = true;
        }
    }

    private void Show(string text)
    {
        _message.Text = text;
        _message.IsVisible = true;
    }
}
