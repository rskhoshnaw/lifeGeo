using System.Globalization;
using LifeDrop.Mobile.Localization;
using LifeDrop.Mobile.Visits;

namespace LifeDrop.Mobile;

// Goods moving between warehouses (plan step 5.61): the transfers of the user's warehouses with their goods; the receiving worker confirms what arrived (line by line), the sender may cancel what has not
// arrived, and "Send goods" opens the form for a new transfer. Online only; the server decides who may do what (a worker only works with his own warehouses).
public sealed class TransfersPage : ContentPage
{
    private static readonly string[] Statuses = ["", "sent", "received", "cancelled"];
    private readonly Picker _status = new();
    private readonly VerticalStackLayout _rows = new() { Spacing = 8 };
    private readonly ActivityIndicator _spinner = new() { IsVisible = false };
    private readonly Label _message = new() { IsVisible = false };
    private bool _loaded;

    public TransfersPage()
    {
        FlowDirection = AppLanguage.Direction;
        Title = Texts.Get("app.transfers.title");
        _status.ItemsSource = Statuses.Select(s => Texts.Get(s.Length == 0 ? "app.transfers.all" : "app.transfers.status." + s)).ToList();
        _status.SelectedIndex = 1;   // what is on the road is what the worker looks for first
        _status.SelectedIndexChanged += async (_, _) => { if (_loaded) await LoadAsync(); };
        var send = new Button { Text = Texts.Get("app.transfers.new"), FontSize = 14 };
        send.Clicked += async (_, _) => await Navigation.PushAsync(new NewTransferPage());
        Content = new ScrollView { Content = new VerticalStackLayout { Padding = 12, Spacing = 8, Children = { send, _status, _spinner, _message, _rows } } };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        _loaded = true;   // coming back from a form shows what changed
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        var api = TrackingHost.TransfersApi;
        if (api is null)
        {
            AppLanguage.ShowRoot(new MainPage());
            return;
        }
        _spinner.IsVisible = _spinner.IsRunning = true;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var (result, items) = await api.ListAsync(Statuses[Math.Max(0, _status.SelectedIndex)], cts.Token);
            if (result == VisitActionResult.SessionExpired) { AppLanguage.ShowRoot(new MainPage()); return; }
            if (result != VisitActionResult.Success) { Show(VisitLifecycle.Describe(result)); return; }
            Show(items.Count == 0 ? Texts.Get("app.transfers.none") : null);
            Render(items);
        }
        catch (OperationCanceledException)
        {
            Show(Texts.Get("app.timeout"));
        }
        finally
        {
            _spinner.IsVisible = _spinner.IsRunning = false;
        }
    }

    private void Render(IReadOnlyList<TransferItem> items)
    {
        _rows.Children.Clear();
        foreach (var t in items)
        {
            var card = new VerticalStackLayout { Spacing = 3, Padding = 10 };
            card.Children.Add(new Label { Text = $"{t.Number} · {Texts.Get("app.transfers.status." + t.Status)}", FontAttributes = FontAttributes.Bold });
            card.Children.Add(new Label { Text = $"{t.FromName} → {t.ToName}", FontSize = 13 });
            foreach (var line in t.Lines)
            {
                var text = $"{line.ProductCode} · {line.ProductName} — {Number(line.Quantity)} {UnitText.Label(line.Unit)}" + (line.Bucket == "damaged" ? $" ({Texts.Get("app.transfers.damaged")})" : "");
                if (line.ReceivedQuantity is { } got && got != line.Quantity) text += " · " + Texts.Get("app.transfers.arrived", ("quantity", Number(got)));
                card.Children.Add(new Label { Text = text, FontSize = 12 });
            }
            if (!string.IsNullOrEmpty(t.Note)) card.Children.Add(new Label { Text = t.Note, FontSize = 12, TextColor = Colors.Gray });
            if (!string.IsNullOrEmpty(t.CloseNote)) card.Children.Add(new Label { Text = t.CloseNote, FontSize = 12, TextColor = Colors.Gray });
            if (t.Status == "sent")
            {
                var receive = new Button { Text = Texts.Get("app.transfers.receive"), FontSize = 13 };
                receive.Clicked += async (_, _) => await Navigation.PushAsync(new ReceiveTransferPage(t));
                var cancel = new Button { Text = Texts.Get("app.transfers.cancel"), FontSize = 13, TextColor = Colors.Red };
                cancel.Clicked += async (_, _) => await CancelAsync(t);
                card.Children.Add(new HorizontalStackLayout { Spacing = 8, Children = { receive, cancel } });
            }
            _rows.Children.Add(new Border
            {
                StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 12 },
                Stroke = Color.FromArgb("#E2E8F0"),
                StrokeThickness = 1,
                BackgroundColor = Colors.White,
                Content = card
            });
        }
    }

    private async Task CancelAsync(TransferItem t)
    {
        var api = TrackingHost.TransfersApi;
        if (api is null) return;
        var note = await DisplayPromptAsync(Texts.Get("app.transfers.cancel"), Texts.Get("app.transfers.cancelNote"), Texts.Get("app.ok"), Texts.Get("app.cancel"));
        if (note is null) return;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var result = await api.CancelAsync(t.Id, string.IsNullOrWhiteSpace(note) ? null : note.Trim(), cts.Token);
        if (result == VisitActionResult.SessionExpired) { AppLanguage.ShowRoot(new MainPage()); return; }
        if (result != VisitActionResult.Success) { Show(VisitLifecycle.Describe(result)); return; }
        await LoadAsync();
    }

    internal static string Number(decimal value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private void Show(string? text)
    {
        _message.Text = text;
        _message.IsVisible = !string.IsNullOrEmpty(text);
    }
}

// Confirm what arrived: every line starts with the full quantity; lower a line that came short, then confirm.
public sealed class ReceiveTransferPage : ContentPage
{
    private readonly TransferItem _transfer;
    private readonly ReceiveDraft _draft;
    private readonly Dictionary<int, Entry> _boxes = new();
    private readonly Editor _note = new() { MaxLength = 500, HeightRequest = 70 };
    private readonly Button _confirm = new() { FontSize = 14 };
    private readonly Label _message = new() { IsVisible = false };

    public ReceiveTransferPage(TransferItem transfer)
    {
        _transfer = transfer;
        _draft = new ReceiveDraft(transfer.Lines);
        FlowDirection = AppLanguage.Direction;
        Title = Texts.Get("app.transfers.receiveTitle", ("number", transfer.Number));
        var rows = new VerticalStackLayout { Spacing = 8, Padding = 12 };
        rows.Children.Add(new Label { Text = $"{transfer.FromName} → {transfer.ToName}", FontAttributes = FontAttributes.Bold });
        foreach (var line in transfer.Lines)
        {
            var box = new Entry { Keyboard = Keyboard.Numeric, WidthRequest = 90, Text = TransfersPage.Number(line.Quantity), HorizontalTextAlignment = TextAlignment.Center };
            box.TextChanged += (_, _) => Check();
            _boxes[line.LineNo] = box;
            var title = new Label { Text = $"{line.ProductCode} · {line.ProductName}" + (line.Bucket == "damaged" ? $" ({Texts.Get("app.transfers.damaged")})" : ""), FontAttributes = FontAttributes.Bold };
            var sent = new Label { Text = Texts.Get("app.transfers.sentQty", ("quantity", TransfersPage.Number(line.Quantity)), ("unit", UnitText.Label(line.Unit))), FontSize = 12 };
            var grid = new Grid { ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) }, ColumnSpacing = 8 };
            grid.Add(new VerticalStackLayout { Children = { title, sent } }, 0);
            grid.Add(box, 1);
            rows.Children.Add(grid);
        }
        _note.Placeholder = Texts.Get("app.transfers.note");
        _confirm.Text = Texts.Get("app.transfers.confirm");
        _confirm.Clicked += async (_, _) => await ConfirmAsync();
        rows.Children.Add(_note);
        rows.Children.Add(_message);
        rows.Children.Add(_confirm);
        Content = new ScrollView { Content = rows };
    }

    private bool Check()
    {
        var ok = true;
        foreach (var (lineNo, box) in _boxes) ok &= _draft.Set(lineNo, box.Text);
        _message.Text = ok ? null : Texts.Get("app.transfers.invalidArrived");
        _message.IsVisible = !ok;
        _confirm.IsEnabled = ok;
        return ok;
    }

    private async Task ConfirmAsync()
    {
        var api = TrackingHost.TransfersApi;
        if (api is null || !Check()) return;
        _confirm.IsEnabled = false;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var result = await api.ReceiveAsync(_transfer.Id, _draft.Lines(), string.IsNullOrWhiteSpace(_note.Text) ? null : _note.Text.Trim(), cts.Token);
            if (result == VisitActionResult.SessionExpired) { AppLanguage.ShowRoot(new MainPage()); return; }
            if (result != VisitActionResult.Success) { _message.Text = VisitLifecycle.Describe(result); _message.IsVisible = true; return; }
            await DisplayAlertAsync(Texts.Get("app.transfers.title"), Texts.Get("app.transfers.received", ("number", _transfer.Number)), Texts.Get("app.ok"));
            await Navigation.PopAsync();
        }
        catch (OperationCanceledException)
        {
            _message.Text = Texts.Get("app.timeout");
            _message.IsVisible = true;
        }
        finally
        {
            _confirm.IsEnabled = true;
        }
    }
}

// Send goods to another warehouse: the sender (one of the user's warehouses), the receiver, then the quantity of every product (sellable and damaged apart), never more than the sender holds.
public sealed class NewTransferPage : ContentPage
{
    private readonly TransferDraft _draft = new(Guid.NewGuid());
    private readonly Picker _from = new();
    private readonly Picker _to = new();
    private readonly SearchBar _search = new();
    private readonly VerticalStackLayout _rows = new() { Spacing = 6 };
    private readonly Editor _note = new() { MaxLength = 500, HeightRequest = 70 };
    private readonly Button _send = new() { FontSize = 14, IsEnabled = false };
    private readonly ActivityIndicator _spinner = new() { IsVisible = false };
    private readonly Label _message = new() { IsVisible = false };
    private IReadOnlyList<WarehouseStockItem> _stock = Array.Empty<WarehouseStockItem>();
    private List<(Guid Id, string Name)> _mine = new();
    private List<TransferWarehouse> _destinations = new();
    private List<TransferWarehouse> _targets = new();
    private bool _filling;

    public NewTransferPage()
    {
        FlowDirection = AppLanguage.Direction;
        Title = Texts.Get("app.transfers.new");
        _from.Title = Texts.Get("app.transfers.from");
        _to.Title = Texts.Get("app.transfers.to");
        _search.Placeholder = Texts.Get("app.products.search");
        _note.Placeholder = Texts.Get("app.transfers.note");
        _send.Text = Texts.Get("app.transfers.send");
        _from.SelectedIndexChanged += (_, _) => OnFromChanged();
        _to.SelectedIndexChanged += (_, _) =>
        {
            _draft.ToWarehouseId = !_filling && _to.SelectedIndex >= 0 && _to.SelectedIndex < _targets.Count ? _targets[_to.SelectedIndex].Id : null;
            Refresh();
        };
        _search.TextChanged += (_, _) => Render();
        _send.Clicked += async (_, _) => await SendAsync();
        Content = new ScrollView { Content = new VerticalStackLayout { Padding = 12, Spacing = 8, Children = { _from, _to, _search, _spinner, _message, _rows, _note, _send } } };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_stock.Count == 0) await LoadAsync();
    }

    private async Task LoadAsync()
    {
        var warehouseApi = TrackingHost.WarehouseApi;
        var api = TrackingHost.TransfersApi;
        if (warehouseApi is null || api is null) { AppLanguage.ShowRoot(new MainPage()); return; }
        _spinner.IsVisible = _spinner.IsRunning = true;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var (stockResult, stock) = await warehouseApi.GetStockAsync(cts.Token);
            var (destResult, destinations) = await api.ListWarehousesAsync(cts.Token);
            var failed = stockResult != VisitActionResult.Success ? stockResult : destResult;
            if (failed == VisitActionResult.SessionExpired) { AppLanguage.ShowRoot(new MainPage()); return; }
            if (failed != VisitActionResult.Success) { Show(VisitLifecycle.Describe(failed)); return; }
            _stock = stock;
            _destinations = destinations.ToList();
            _mine = stock.GroupBy(i => i.WarehouseId).Select(g => (g.Key, g.First().WarehouseName)).ToList();
            _from.ItemsSource = _mine.Select(w => w.Name).ToList();
            if (_mine.Count == 0) Show(Texts.Get("app.stock.none"));
            else if (_mine.Count == 1) _from.SelectedIndex = 0;
        }
        catch (OperationCanceledException)
        {
            Show(Texts.Get("app.timeout"));
        }
        finally
        {
            _spinner.IsVisible = _spinner.IsRunning = false;
        }
    }

    private void OnFromChanged()
    {
        if (_from.SelectedIndex < 0 || _from.SelectedIndex >= _mine.Count) return;
        var from = _mine[_from.SelectedIndex].Id;
        _draft.SetFrom(from, _stock);
        _filling = true;
        _targets = _destinations.Where(w => w.Id != from).ToList();
        _to.ItemsSource = _targets.Select(w => $"{w.Code} · {w.Name}" + (string.IsNullOrEmpty(w.CityName) ? "" : $" ({w.CityName})")).ToList();
        _filling = false;
        Render();
        Refresh();
    }

    private void Render()
    {
        _rows.Children.Clear();
        var text = (_search.Text ?? "").Trim();
        foreach (var product in _draft.Products)
        {
            var item = _stock.First(s => s.WarehouseId == _draft.FromWarehouseId && s.ProductId == product);
            if (text.Length > 0 && !item.ProductCode.Contains(text, StringComparison.OrdinalIgnoreCase) && !item.ProductName.Contains(text, StringComparison.OrdinalIgnoreCase)) continue;
            var row = new VerticalStackLayout { Spacing = 2 };
            row.Children.Add(new Label { Text = $"{item.ProductCode} · {item.ProductName}", FontAttributes = FontAttributes.Bold });
            foreach (var bucket in new[] { "sellable", "damaged" })
            {
                var available = _draft.Available(product, bucket);
                if (available <= 0) continue;
                var box = new Entry { Keyboard = Keyboard.Numeric, WidthRequest = 90, Placeholder = "0", HorizontalTextAlignment = TextAlignment.Center };
                var current = _draft.Quantity(product, bucket);
                if (current > 0) box.Text = TransfersPage.Number(current);
                box.TextChanged += (_, _) =>
                {
                    var ok = _draft.Set(product, bucket, box.Text);
                    box.TextColor = ok ? Colors.Black : Colors.Red;
                    Refresh();
                };
                var label = Texts.Get(bucket == "damaged" ? "app.transfers.damagedOf" : "app.transfers.sellableOf", ("quantity", TransfersPage.Number(available)), ("unit", UnitText.Label(item.Unit)));
                var grid = new Grid { ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) }, ColumnSpacing = 8 };
                grid.Add(new Label { Text = label, FontSize = 12, VerticalOptions = LayoutOptions.Center }, 0);
                grid.Add(box, 1);
                row.Children.Add(grid);
            }
            _rows.Children.Add(row);
        }
    }

    private void Refresh() => _send.IsEnabled = _draft.CanSend;

    private async Task SendAsync()
    {
        var api = TrackingHost.TransfersApi;
        if (api is null || !_draft.CanSend) return;
        _send.IsEnabled = false;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            // The same reference for every try of this form: a lost answer never sends the goods twice.
            var (result, number) = await api.CreateAsync(_draft.FromWarehouseId!.Value, _draft.ToWarehouseId!.Value, _draft.ClientRef, string.IsNullOrWhiteSpace(_note.Text) ? null : _note.Text.Trim(), _draft.Lines(), cts.Token);
            if (result == VisitActionResult.SessionExpired) { AppLanguage.ShowRoot(new MainPage()); return; }
            if (result != VisitActionResult.Success) { Show(result == VisitActionResult.Conflict ? Texts.Get("app.transfers.tooMuch") : VisitLifecycle.Describe(result)); return; }
            await DisplayAlertAsync(Texts.Get("app.transfers.title"), Texts.Get("app.transfers.sent", ("number", number ?? "")), Texts.Get("app.ok"));
            await Navigation.PopAsync();
        }
        catch (OperationCanceledException)
        {
            Show(Texts.Get("app.timeout"));
        }
        finally
        {
            Refresh();
        }
    }

    private void Show(string? text)
    {
        _message.Text = text;
        _message.IsVisible = !string.IsNullOrEmpty(text);
    }
}
