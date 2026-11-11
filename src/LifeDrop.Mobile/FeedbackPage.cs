using LifeDrop.Mobile.Localization;
using LifeDrop.Mobile.Visits;
#if ANDROID || IOS
using Microsoft.Maui.Graphics.Platform;
#endif

namespace LifeDrop.Mobile;

// Feedback and suggestions (plan step 5.30): the user writes a suggestion, a complaint or a note about the work or about a customer (optionally linked to one of their customers),
// adds up to three photos (taken now or chosen), and sends it. Below, "My feedback" lists what they sent with its status and the managers' answers. Online only.
public sealed class FeedbackPage : ContentPage
{
    private readonly ChoiceField _kind = new(Texts.Get("app.feedback.kindLabel"));
    private readonly Editor _text = new() { AutoSize = EditorAutoSizeOption.TextChanges, MaxLength = FeedbackRules.MaxBody, HeightRequest = 120 };
    private readonly SearchBar _customerSearch = new();
    private readonly VerticalStackLayout _customerResults = new() { Spacing = 4 };
    private readonly Label _customerLabel = new() { IsVisible = false, FontAttributes = FontAttributes.Bold };
    private readonly Button _customerClear = new() { IsVisible = false };
    private readonly Button _take = new();
    private readonly Button _choose = new();
    private readonly Label _photosLabel = new() { IsVisible = false };
    private readonly Button _send = new();
    private readonly ActivityIndicator _spinner = new() { IsVisible = false };
    private readonly Label _message = new() { IsVisible = false };
    private readonly VerticalStackLayout _mine = new() { Spacing = 8 };
    private readonly Label _mineTitle = new() { FontAttributes = FontAttributes.Bold, FontSize = 18 };
    private readonly Button _more = new() { IsVisible = false };
    private readonly List<(byte[] Data, string ContentType)> _photos = new();
    private readonly List<MyFeedback> _items = new();
    private Guid? _customerId;
    private bool _loaded;

    public FeedbackPage()
    {
        FlowDirection = AppLanguage.Direction;
        Title = Texts.Get("app.feedback.title");
        _kind.Items = FeedbackRules.Kinds.Select(k => Texts.Get(FeedbackRules.KindKey(k))).ToList();
        _text.Placeholder = Texts.Get("app.feedback.text");
        _customerSearch.Placeholder = Texts.Get("app.feedback.customerSearch");
        _customerSearch.SearchButtonPressed += async (_, _) => await SearchCustomerAsync();
        _customerClear.Text = Texts.Get("app.feedback.customerClear");
        _customerClear.Clicked += (_, _) => SetCustomer(null, null);
        _take.Text = Texts.Get("app.feedback.takePhoto");
        _take.Clicked += async (_, _) => await AddPhotoAsync(capture: true);
        _choose.Text = Texts.Get("app.feedback.choosePhoto");
        _choose.Clicked += async (_, _) => await AddPhotoAsync(capture: false);
        _send.Text = Texts.Get("app.feedback.send");
        _send.Clicked += async (_, _) => await SendAsync();
        _mineTitle.Text = Texts.Get("app.feedback.mine");
        _more.Text = Texts.Get("app.customers.more");
        _more.Clicked += async (_, _) => await LoadMineAsync(more: true);
        var photoButtons = new HorizontalStackLayout { Spacing = 12, Children = { _take, _choose } };
        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = 16,
                Spacing = 12,
                Children =
                {
                    new CaptionedField("app.feedback.kindLabel", _kind), _text, _customerSearch, _customerResults, _customerLabel, _customerClear,
                    photoButtons, _photosLabel, _send, _spinner, _message, _mineTitle, _mine, _more
                }
            }
        };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_loaded) return;
        _loaded = true;
        await LoadMineAsync(more: false);
    }

    private void SetCustomer(Guid? id, string? label)
    {
        _customerId = id;
        _customerLabel.Text = label is null ? "" : Texts.Get("app.feedback.customerLinked", ("customer", label));
        _customerLabel.IsVisible = _customerClear.IsVisible = id is not null;
        _customerResults.Children.Clear();
    }

    // Two characters at least; the server limits the answer to the customers of the user's area.
    private async Task SearchCustomerAsync()
    {
        var api = TrackingHost.CustomersApi;
        var text = (_customerSearch.Text ?? "").Trim();
        if (api is null || text.Length < 2) return;
        _customerResults.Children.Clear();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var (result, items) = await api.SearchCustomersAsync(text, cts.Token);
            if (result == VisitActionResult.SessionExpired)
            {
                AppLanguage.ShowRoot(new MainPage());
                return;
            }
            if (result != VisitActionResult.Success) return;
            foreach (var item in items.Take(8))
            {
                var label = $"{item.Code} · {item.Name}";
                var button = new Button { Text = label, HorizontalOptions = LayoutOptions.Fill };
                var captured = item;
                button.Clicked += (_, _) => SetCustomer(captured.Id, label);
                _customerResults.Children.Add(button);
            }
        }
        catch (OperationCanceledException)
        {
            Show(Texts.Get("app.timeout"));
        }
    }

    private async Task AddPhotoAsync(bool capture)
    {
        if (_photos.Count >= FeedbackRules.MaxPhotos) return;
        try
        {
            FileResult? file = capture
                ? (MediaPicker.Default.IsCaptureSupported ? await MediaPicker.Default.CapturePhotoAsync() : null)
                : await MediaPicker.Default.PickPhotoAsync();
            if (file is null) return;
            var (data, contentType, errorKey) = await FeedbackPhotos.PrepareAsync(file);
            if (data is null || contentType is null)
            {
                Show(Texts.Get(errorKey ?? "app.feedback.photoFailed"));
                return;
            }
            _photos.Add((data, contentType));
            _photosLabel.Text = Texts.Get("app.feedback.photosAdded", ("count", _photos.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)), ("max", FeedbackRules.MaxPhotos.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            _photosLabel.IsVisible = true;
            _message.IsVisible = false;
        }
        catch (Exception)
        {
            Show(Texts.Get("app.feedback.photoFailed"));   // no permission, no camera or the picker was closed in a way that failed
        }
    }

    private async Task SendAsync()
    {
        var kind = FeedbackRules.Kinds[Math.Max(0, Math.Min(_kind.SelectedIndex, FeedbackRules.Kinds.Count - 1))];
        var body = (_text.Text ?? "").Trim();
        if (!FeedbackRules.IsValid(kind, body))
        {
            Show(Texts.Get("app.feedback.invalid"));
            return;
        }
        var api = TrackingHost.CustomersApi;
        if (api is null)
        {
            AppLanguage.ShowRoot(new MainPage());
            return;
        }
        SetBusy(true);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            var (result, id) = await api.CreateFeedbackAsync(kind, body, _customerId, cts.Token);
            if (result == VisitActionResult.SessionExpired)
            {
                AppLanguage.ShowRoot(new MainPage());
                return;
            }
            if (result != VisitActionResult.Success || id is not { } feedbackId)
            {
                Show(Texts.Get("app.feedback.failed"));
                return;
            }
            var failed = 0;
            foreach (var (data, contentType) in _photos)
                if (await api.AddFeedbackPhotoAsync(feedbackId, data, contentType, cts.Token) != VisitActionResult.Success) failed++;
            _text.Text = "";
            _photos.Clear();
            _photosLabel.IsVisible = false;
            _customerSearch.Text = "";
            SetCustomer(null, null);
            Show(failed == 0 ? Texts.Get("app.feedback.sent") : Texts.Get("app.feedback.sentPhotosFailed", ("failed", failed.ToString(System.Globalization.CultureInfo.InvariantCulture))));
            await LoadMineAsync(more: false);
        }
        catch (OperationCanceledException)
        {
            Show(Texts.Get("app.timeout"));
        }
        finally
        {
            SetBusy(false);
        }
    }

    // more = false: the newest 50; true: the next page.
    private async Task LoadMineAsync(bool more)
    {
        var api = TrackingHost.CustomersApi;
        if (api is null) return;
        if (!more)
        {
            _items.Clear();
            _mine.Children.Clear();
        }
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var (result, page) = await api.GetMyFeedbackAsync(more && _items.Count > 0 ? _items[^1].CreatedAt : null, cts.Token);
            if (result == VisitActionResult.SessionExpired)
            {
                AppLanguage.ShowRoot(new MainPage());
                return;
            }
            if (result != VisitActionResult.Success) return;
            foreach (var item in page)
            {
                _items.Add(item);
                _mine.Children.Add(Card(item));
            }
            if (_items.Count == 0) _mine.Children.Add(new Label { Text = Texts.Get("app.feedback.noneYet") });
            _more.IsVisible = page.Count == 50;
        }
        catch (OperationCanceledException)
        {
            // The list stays as it is; the form still works.
        }
    }

    private static Border Card(MyFeedback f)
    {
        // Iraq is UTC+3 all year.
        var day = f.CreatedAt.ToOffset(TimeSpan.FromHours(3)).ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        var stack = new VerticalStackLayout { Spacing = 4 };
        stack.Children.Add(new Label { Text = $"{day} · {Texts.Get(FeedbackRules.KindKey(f.Kind))} · {Texts.Get(FeedbackRules.StatusKey(f.Status))}", FontAttributes = FontAttributes.Bold });
        stack.Children.Add(new Label { Text = f.Body });
        if (f.CustomerCode is not null) stack.Children.Add(new Label { Text = Texts.Get("app.feedback.customerLinked", ("customer", $"{f.CustomerCode} · {f.CustomerName}")), FontSize = 12 });
        if (f.PhotoIds is { Count: > 0 } photos) stack.Children.Add(new Label { Text = Texts.Get("app.feedback.photoCount", ("count", photos.Count.ToString(System.Globalization.CultureInfo.InvariantCulture))), FontSize = 12 });
        foreach (var reply in f.Replies ?? [])
            stack.Children.Add(new Label { Text = Texts.Get("app.feedback.answer", ("who", reply.UserEmail ?? ""), ("text", reply.Body)), TextColor = Colors.SeaGreen });
        return new Border { Padding = 12, StrokeThickness = 1, Stroke = Colors.Gray, StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 8 }, Content = stack };
    }

    private void SetBusy(bool busy)
    {
        _spinner.IsVisible = _spinner.IsRunning = busy;
        _send.IsEnabled = _take.IsEnabled = _choose.IsEnabled = !busy;
        if (busy) _message.IsVisible = false;
    }

    private void Show(string text)
    {
        _message.Text = text;
        _message.IsVisible = true;
    }
}

// A picture for the server: JPEG, PNG or WEBP of at most 3 MB. On the phone it is made smaller first (1600 pixels on the long side, JPEG), because a camera photo is much bigger.
internal static class FeedbackPhotos
{
    public static async Task<(byte[]? Data, string? ContentType, string? ErrorKey)> PrepareAsync(FileResult file)
    {
        var known = FeedbackRules.PhotoContentType(file.FileName);
        var type = known ?? "image/jpeg";   // a camera photo can come without a usable name
        byte[] bytes;
        await using (var stream = await file.OpenReadAsync())
        {
            using var memory = new MemoryStream();
            await stream.CopyToAsync(memory);
            bytes = memory.ToArray();
        }
        var reencoded = false;
#if ANDROID || IOS
        try
        {
            var image = PlatformImage.FromStream(new MemoryStream(bytes));
            if (image is not null)
            {
                bytes = image.Downsize(1600f, true).AsBytes(ImageFormat.Jpeg, 0.8f);
                type = "image/jpeg";
                reencoded = true;
            }
        }
        catch (Exception)
        {
            // The picture stays as it is; it is checked below.
        }
#endif
        if (!reencoded && known is null && !string.IsNullOrEmpty(Path.GetExtension(file.FileName))) return (null, null, "app.feedback.photoUnsupported");   // for example HEIC
        if (bytes.Length > FeedbackRules.MaxPhotoBytes) return (null, null, "app.feedback.photoTooLarge");
        return (bytes, type, null);
    }
}
