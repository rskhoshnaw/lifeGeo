using LifeDrop.Mobile.Localization;
using LifeDrop.Mobile.Visits;

namespace LifeDrop.Mobile;

// The place lists of a customer (plan step 1.23-b), one under the other: city, neighbourhood, street or alley, alley. The
// logic is in PlacePicker (tested); this view only draws it. Under each list that allows it there is a box to type a
// name: matching names of the list are offered (tap one to choose it), and a name that is new can be added as a
// neighbourhood, street or alley - it is part of the reference at once.
public sealed class PlacePickerView : ContentView
{
    private readonly VerticalStackLayout _root = new() { Spacing = 8 };
    private readonly Label _messageLabel = new() { IsVisible = false };
    private readonly Dictionary<int, string> _typed = new();
    private readonly Dictionary<int, VerticalStackLayout> _helpers = new();
    private PlacePicker? _picker;
    private bool _rendering;

    public PlacePickerView() => Content = _root;

    // False for filters: the lists only choose, nothing is typed or added to the place reference.
    public bool AllowAdd { get; set; } = true;

    public Guid? PlaceId => _picker?.PlaceId;
    // The answer of the last failed call, for the page to show; Success when all went well.
    public VisitActionResult Result => _picker?.Result ?? VisitActionResult.Success;

    public async Task LoadAsync(ICustomersApiClient api, IReadOnlyList<PlacePathItem>? path, CancellationToken cancellationToken = default)
    {
        _picker = new PlacePicker(api);
        _typed.Clear();
        await _picker.LoadAsync(path, cancellationToken);
        Render();
        if (_picker.Result != VisitActionResult.Success) ShowMessage(VisitLifecycle.Describe(_picker.Result));
    }

    public void SetEnabled(bool enabled) => _root.IsEnabled = enabled;

    private void ShowMessage(string? text)
    {
        _messageLabel.Text = text;
        _messageLabel.IsVisible = !string.IsNullOrEmpty(text);
    }

    private static string Title(string? parentKind) => Texts.Get(parentKind switch
    {
        null => "app.place.city",
        "city" => "app.place.neighbourhood",
        "neighbourhood" => "app.place.streetOrAlley",
        _ => "app.place.alley"
    });

    private static string Label(PlaceItem place, bool mixed)
    {
        var name = place.DisplayName(Texts.Language);
        if (!mixed) return name;
        return place.Kind switch
        {
            "street" => $"{name} ({Texts.Get("app.place.kindStreet")})",
            "alley" => $"{name} ({Texts.Get("app.place.kindAlley")})",
            _ => name
        };
    }

    private void Render()
    {
        _rendering = true;
        _root.Children.Clear();
        _helpers.Clear();
        var picker = _picker!;
        if (picker.Levels.Count == 1 && picker.Levels[0].Items.Count == 0) _root.Children.Add(new Label { Text = Texts.Get("app.place.noCities") });

        for (var i = 0; i < picker.Levels.Count; i++)
        {
            var level = picker.Levels[i];
            IReadOnlyList<string> addable = AllowAdd ? PlacePicker.AddableKinds(level) : Array.Empty<string>();
            if (level.Items.Count == 0 && addable.Count == 0) continue;
            var index = i;

            _root.Children.Add(new Label { Text = Title(level.ParentKind), FontAttributes = FontAttributes.Bold });
            if (level.Items.Count > 0)
            {
                var mixed = level.ParentKind == "neighbourhood";
                var choices = new List<string> { Texts.Get("app.place.choose") };
                choices.AddRange(level.Items.Select(p => Label(p, mixed)));
                var list = new ChoiceField(Title(level.ParentKind)) { Items = choices, SelectedIndex = level.SelectedId is { } selected ? level.Items.ToList().FindIndex(p => p.Id == selected) + 1 : 0 };
                list.SelectedIndexChanged += async (_, _) =>
                {
                    if (_rendering) return;
                    await _picker!.SelectAsync(index, list.SelectedIndex <= 0 ? null : level.Items[list.SelectedIndex - 1].Id);
                    ShowMessage(null);
                    Render();
                };
                _root.Children.Add(list);
            }

            if (addable.Count > 0)
            {
                var entry = new Entry { Placeholder = Texts.Get("app.place.typeName"), Text = _typed.GetValueOrDefault(index, "") };
                var helpers = new VerticalStackLayout { Spacing = 4 };
                _helpers[index] = helpers;
                entry.TextChanged += (_, e) =>
                {
                    if (_rendering) return;
                    _typed[index] = e.NewTextValue ?? "";
                    RenderHelpers(index);
                };
                _root.Children.Add(entry);
                _root.Children.Add(helpers);
                RenderHelpers(index);
            }
        }
        _root.Children.Add(_messageLabel);
        _rendering = false;
    }

    // Names that match what was typed (tap to choose) and the buttons that add it as something new.
    private void RenderHelpers(int level)
    {
        if (!_helpers.TryGetValue(level, out var helpers) || _picker is null) return;
        helpers.Children.Clear();
        var text = _typed.GetValueOrDefault(level, "").Trim();
        if (text.Length == 0) return;

        var mixed = _picker.Levels[level].ParentKind == "neighbourhood";
        foreach (var match in _picker.Suggest(level, text, Texts.Language))
        {
            var chosen = match;
            var button = new Button { Text = Label(chosen, mixed), HorizontalOptions = LayoutOptions.Start };
            button.Clicked += async (_, _) =>
            {
                await _picker.SelectAsync(level, chosen.Id);
                _typed.Remove(level);
                ShowMessage(null);
                Render();
            };
            helpers.Children.Add(button);
        }

        foreach (var kind in PlacePicker.AddableKinds(_picker.Levels[level]))
        {
            var add = new Button { Text = Texts.Get(kind switch { "neighbourhood" => "app.place.addNeighbourhood", "street" => "app.place.addStreet", _ => "app.place.addAlley" }), HorizontalOptions = LayoutOptions.Start };
            var wanted = kind;
            add.Clicked += async (_, _) =>
            {
                add.IsEnabled = false;
                var typed = _typed.GetValueOrDefault(level, "").Trim();
                var result = await _picker.AddAsync(level, wanted, typed);
                if (result != PlaceAddResult.Failed) _typed.Remove(level);
                Render();
                ShowMessage(result switch
                {
                    PlaceAddResult.Added => Texts.Get("app.place.added", ("name", typed)),
                    PlaceAddResult.UsedExisting => Texts.Get("app.place.usedExisting", ("name", typed)),
                    _ => VisitLifecycle.Describe(_picker.Result)
                });
            };
            helpers.Children.Add(add);
        }
    }
}
