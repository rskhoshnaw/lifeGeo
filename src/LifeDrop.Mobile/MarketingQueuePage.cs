using LifeDrop.Mobile.Localization;
using LifeDrop.Mobile.Visits;

namespace LifeDrop.Mobile;

// The queue of one campaign (plan step 5.7): one customer at a time with the ready text. "Open WhatsApp" opens the chat (the user sends the message there himself), then "Sent" marks it;
// the next chat opens only after a pause (MarketingPacing), because many messages in a row from one personal number can get it banned. Nothing is sent by the app or the server.
public sealed class MarketingQueuePage(Guid campaignId) : ContentPage
{
    private readonly Label _count = new() { FontAttributes = FontAttributes.Bold };
    private readonly Label _customer = new() { FontSize = 18, FontAttributes = FontAttributes.Bold };
    private readonly Label _phone = new();
    private readonly Label _text = new();
    private readonly Label _wait = new() { IsVisible = false };
    private readonly Button _open = new();
    private readonly Button _sent = new() { IsEnabled = false };
    private readonly Button _skip = new();
    private readonly ActivityIndicator _spinner = new() { IsVisible = false };
    private readonly Label _message = new() { IsVisible = false };
    private readonly VerticalStackLayout _card = new() { Spacing = 8, IsVisible = false };
    private readonly List<MarketingMessageItem> _queue = [];
    private DateTimeOffset? _lastSent;
    private bool _opened, _busy;
    private IDispatcherTimer? _timer;

    protected override void OnAppearing()
    {
        base.OnAppearing();
        FlowDirection = AppLanguage.Direction;
        Title = Texts.Get("app.marketing.queue");
        _open.Text = Texts.Get("app.marketing.open");
        _sent.Text = Texts.Get("app.marketing.sent");
        _skip.Text = Texts.Get("app.marketing.skip");
        if (Content is null)
        {
            _open.Clicked += async (_, _) => await OpenChatAsync();
            _sent.Clicked += async (_, _) => await HandleAsync(sent: true);
            _skip.Clicked += async (_, _) => await HandleAsync(sent: false);
            _card.Children.Add(_customer);
            _card.Children.Add(_phone);
            _card.Children.Add(_text);
            _card.Children.Add(_wait);
            _card.Children.Add(_open);
            _card.Children.Add(_sent);
            _card.Children.Add(_skip);
            Content = new ScrollView { Content = new VerticalStackLayout { Padding = 12, Spacing = 10, Children = { _count, new Label { Text = Texts.Get("app.marketing.pause") }, _spinner, _message, _card } } };
            _timer = Dispatcher.CreateTimer();
            _timer.Interval = TimeSpan.FromSeconds(1);
            _timer.Tick += (_, _) => RefreshButtons();
        }
        _timer?.Start();
        _ = LoadAsync();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _timer?.Stop();
    }

    private async Task LoadAsync()
    {
        if (TrackingHost.MarketingApi is not { } api)
        {
            AppLanguage.ShowRoot(new MainPage());
            return;
        }
        var result = await RunAsync(async c =>
        {
            var (status, messages) = await api.GetQueueAsync(campaignId, c);
            if (status == VisitActionResult.Success)
            {
                _queue.Clear();
                _queue.AddRange(messages);
            }
            return status;
        });
        if (result == VisitActionResult.Success) Render();
    }

    private void Render()
    {
        _count.Text = Texts.Get("app.marketing.left", ("count", _queue.Count.ToString()));
        _card.IsVisible = _queue.Count > 0;
        Show(_queue.Count == 0 ? Texts.Get("app.marketing.done") : null);
        if (_queue.Count > 0)
        {
            _customer.Text = _queue[0].CustomerName;
            _phone.Text = _queue[0].Phone;
            _text.Text = _queue[0].Message;
        }
        _opened = false;
        RefreshButtons();
    }

    // The chat opens only when the pause after the last sent message is over; "Sent" only after the chat was opened.
    private void RefreshButtons()
    {
        var remaining = MarketingPacing.Remaining(_lastSent, DateTimeOffset.UtcNow);
        _wait.IsVisible = remaining > TimeSpan.Zero;
        _wait.Text = Texts.Get("app.marketing.wait", ("seconds", ((int)Math.Ceiling(remaining.TotalSeconds)).ToString()));
        _open.IsEnabled = !_busy && _queue.Count > 0 && remaining == TimeSpan.Zero;
        _sent.IsEnabled = !_busy && _opened;
        _skip.IsEnabled = !_busy && _queue.Count > 0;
    }

    private async Task OpenChatAsync()
    {
        if (_queue.Count == 0 || WhatsAppLink.Url(_queue[0].Phone, _queue[0].Message) is not { } url)
        {
            Show(Texts.Get("app.marketing.noPhone"));
            return;
        }
        await Launcher.OpenAsync(new Uri(url));
        _opened = true;
        RefreshButtons();
    }

    private async Task HandleAsync(bool sent)
    {
        if (TrackingHost.MarketingApi is not { } api || _queue.Count == 0) return;
        var first = _queue[0];
        var result = await RunAsync(c => sent ? api.SentAsync(first.Id, c) : api.SkipAsync(first.Id, c));
        if (result != VisitActionResult.Success) return;
        if (sent) _lastSent = DateTimeOffset.UtcNow;
        _queue.RemoveAt(0);
        Render();
    }

    private async Task<VisitActionResult> RunAsync(Func<CancellationToken, Task<VisitActionResult>> call)
    {
        if (_busy) return VisitActionResult.NetworkError;
        _busy = true;
        _spinner.IsVisible = _spinner.IsRunning = true;
        RefreshButtons();
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
        RefreshButtons();
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
