using LifeDrop.Mobile.Localization;
using LifeDrop.Mobile.Visits;

namespace LifeDrop.Mobile;

// Connects the user's private Telegram chat to the company bot (plan step 4.22), so the company's messages also arrive there. The one-time code goes to the
// bot only from this user's own Telegram (the link opens the chat; pressing Start finishes it), so a message can never reach a stranger. Online only.
public sealed class TelegramPage : ContentPage
{
    private readonly Label _status = new();
    private readonly Button _connect = new();
    private readonly Button _disconnect = new() { IsVisible = false };
    private readonly Button _refresh = new();
    private readonly ActivityIndicator _spinner = new() { IsVisible = false };
    private readonly Label _message = new() { IsVisible = false };

    public TelegramPage()
    {
        FlowDirection = AppLanguage.Direction;
        Title = Texts.Get("app.telegram.title");
        _connect.Text = Texts.Get("app.telegram.connect");
        _connect.Clicked += async (_, _) => await ConnectAsync();
        _disconnect.Text = Texts.Get("app.telegram.disconnect");
        _disconnect.Clicked += async (_, _) => await DisconnectAsync();
        _refresh.Text = Texts.Get("app.telegram.refresh");
        _refresh.Clicked += async (_, _) => await LoadAsync();
        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = 16,
                Spacing = 12,
                Children =
                {
                    new Label { Text = Texts.Get("app.telegram.hint") },
                    _status, _connect, _disconnect, _refresh, _spinner, _message
                }
            }
        };
        _connect.IsEnabled = false;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        var api = TrackingHost.ChannelsApi;
        if (api is null)
        {
            AppLanguage.ShowRoot(new MainPage());
            return;
        }
        SetBusy(true);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var (result, status) = await api.GetStatusAsync(cts.Token);
            if (result != VisitActionResult.Success || status is null)
            {
                Show(result == VisitActionResult.SessionExpired ? Texts.Get("app.sessionEnded") : VisitLifecycle.Describe(result), result);
                return;
            }
            _status.Text = !status.Enabled ? Texts.Get("app.telegram.off") : status.TelegramLinked ? Texts.Get("app.telegram.linked") : Texts.Get("app.telegram.notLinked");
            _connect.IsEnabled = status.Enabled && !status.TelegramLinked;
            _connect.IsVisible = !status.TelegramLinked;
            _disconnect.IsVisible = status.TelegramLinked;
        }
        catch (OperationCanceledException)
        {
            Show(Texts.Get("app.timeout"), VisitActionResult.NetworkError);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task ConnectAsync()
    {
        var api = TrackingHost.ChannelsApi;
        if (api is null) return;
        SetBusy(true);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var (result, link) = await api.StartTelegramLinkAsync(cts.Token);
            if (result != VisitActionResult.Success || link?.Link is not { } url)
            {
                Show(result == VisitActionResult.SessionExpired ? Texts.Get("app.sessionEnded") : Texts.Get("app.telegram.unavailable"), result);
                return;
            }
            // Telegram opens (or the browser, which offers Telegram); the user presses Start there and comes back to this page.
            await Launcher.Default.OpenAsync(new Uri(url));
            Show(Texts.Get("app.telegram.opened"), VisitActionResult.Success);
        }
        catch (OperationCanceledException)
        {
            Show(Texts.Get("app.timeout"), VisitActionResult.NetworkError);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task DisconnectAsync()
    {
        var api = TrackingHost.ChannelsApi;
        if (api is null) return;
        if (!await DisplayAlertAsync(Texts.Get("app.telegram.title"), Texts.Get("app.telegram.disconnectQuestion"), Texts.Get("app.telegram.disconnect"), Texts.Get("app.cancel"))) return;
        SetBusy(true);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var result = await api.UnlinkTelegramAsync(cts.Token);
            if (result is VisitActionResult.Success or VisitActionResult.NotFound) await LoadAsync();
            else Show(VisitLifecycle.Describe(result), result);
        }
        catch (OperationCanceledException)
        {
            Show(Texts.Get("app.timeout"), VisitActionResult.NetworkError);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void Show(string text, VisitActionResult result)
    {
        _message.Text = text;
        _message.IsVisible = true;
        if (result == VisitActionResult.SessionExpired) AppLanguage.ShowRoot(new MainPage());
    }

    private void SetBusy(bool busy)
    {
        _spinner.IsVisible = _spinner.IsRunning = busy;
        _refresh.IsEnabled = !busy;
        if (busy)
        {
            _message.IsVisible = false;
            _connect.IsEnabled = false;
        }
    }
}
