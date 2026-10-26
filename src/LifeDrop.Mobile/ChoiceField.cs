using LifeDrop.Mobile.Localization;

namespace LifeDrop.Mobile;

// A row of the choice list (public so the list can bind to it).
public sealed class ChoiceRow(int index, string text)
{
    public int Index { get; } = index;
    public string Text { get; } = text;
}

// The page that lists the choices with a search box. It answers with the index of the chosen row, or null when the user cancels or leaves.
public sealed class ChoicePage : ContentPage
{
    private readonly TaskCompletionSource<int?> _done = new();
    private readonly IReadOnlyList<string> _items;
    private readonly int _selected;
    private readonly CollectionView _list = new() { SelectionMode = SelectionMode.Single };

    public Task<int?> Result => _done.Task;

    public ChoicePage(string title, IReadOnlyList<string> items, int selected)
    {
        FlowDirection = AppLanguage.Direction;
        Title = title;
        _items = items;
        _selected = selected;
        var search = new SearchBar { Placeholder = Texts.Get("app.choice.search") };
        search.TextChanged += (_, e) => Show(e.NewTextValue);
        ToolbarItems.Add(new ToolbarItem { Text = Texts.Get("app.cancel"), Command = new Command(() => _done.TrySetResult(null)) });
        _list.ItemTemplate = new DataTemplate(() =>
        {
            var label = new Label { Padding = new Thickness(16, 12), FontSize = 16 };
            label.SetBinding(Label.TextProperty, nameof(ChoiceRow.Text));
            return label;
        });
        _list.SelectionChanged += (_, e) =>
        {
            if (e.CurrentSelection.FirstOrDefault() is ChoiceRow row) _done.TrySetResult(row.Index);
        };
        Show("");
        Content = new Grid { RowDefinitions = { new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Star) }, Children = { search, _list } };
        Grid.SetRow(_list, 1);
    }

    private void Show(string? filter)
    {
        var text = (filter ?? "").Trim();
        _list.ItemsSource = _items
            .Select((item, index) => new ChoiceRow(index, index == _selected ? "✓ " + item : item))
            .Where(row => text.Length == 0 || row.Text.Contains(text, StringComparison.CurrentCultureIgnoreCase))
            .ToList();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _done.TrySetResult(null);   // left without choosing (back button, swipe)
    }
}

// A choice from a list as a tappable box, the same on Android and iPhone: tapping opens a page with a search box and the list.
// It replaces the native Picker where the page changes while a choice is made (the iPhone wheel closes when its view is rebuilt).
// SelectedIndexChanged fires only when the user picks another row, never when the code sets the index.
public sealed class ChoiceField : ContentView
{
    private readonly Label _text = new() { VerticalOptions = LayoutOptions.Center, LineBreakMode = LineBreakMode.TailTruncation };
    private readonly string _title;
    private IReadOnlyList<string> _items = [];
    private int _selected;
    private bool _opening;

    public event EventHandler? SelectedIndexChanged;

    public ChoiceField(string title)
    {
        _title = title;
        var grid = new Grid { ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) }, Padding = new Thickness(12, 10) };
        grid.Add(_text);
        var arrow = new Label { Text = "▾", VerticalOptions = LayoutOptions.Center };
        grid.Add(arrow, 1);
        var tap = new TapGestureRecognizer();
        tap.Tapped += async (_, _) => await OpenAsync();
        grid.GestureRecognizers.Add(tap);
        Content = new Border { StrokeThickness = 1, Stroke = Colors.Gray, StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 8 }, Content = grid };
        SemanticProperties.SetDescription(this, title);
        Refresh();
    }

    public IReadOnlyList<string> Items
    {
        get => _items;
        set
        {
            _items = value;
            if (_selected >= _items.Count) _selected = 0;
            Refresh();
        }
    }

    public int SelectedIndex
    {
        get => _selected;
        set
        {
            _selected = Math.Max(0, Math.Min(value, Math.Max(0, _items.Count - 1)));
            Refresh();
        }
    }

    private void Refresh() => _text.Text = _selected < _items.Count ? _items[_selected] : _title;

    private Page? HostPage()
    {
        Element? element = this;
        while (element is not null and not Page) element = element.Parent;
        return element as Page;
    }

    private async Task OpenAsync()
    {
        if (_opening || !IsEnabled || _items.Count == 0 || HostPage() is not { } page) return;
        _opening = true;
        try
        {
            var choice = new ChoicePage(_title, _items, _selected);
            var modal = new NavigationPage(choice);
            await page.Navigation.PushModalAsync(modal);
            var result = await choice.Result;
            if (page.Navigation.ModalStack.Contains(modal)) await page.Navigation.PopModalAsync();
            if (result is { } index && index != _selected)
            {
                SelectedIndex = index;
                SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        finally
        {
            _opening = false;
        }
    }
}
