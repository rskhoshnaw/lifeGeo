using Xunit;

namespace LifeDrop.Mobile.Visits.Tests;

public class PlacePickerTests
{
    private static readonly Guid Erbil = Guid.NewGuid(), Duhok = Guid.NewGuid(), Ankawa = Guid.NewGuid(), Bakhtiari = Guid.NewGuid(),
        Main = Guid.NewGuid(), Alley2 = Guid.NewGuid(), Alley1 = Guid.NewGuid();

    private static PlaceItem P(Guid id, Guid? parent, string kind, string name, string? ckb = null, int below = 0) => new(id, parent, kind, name, ckb, null, null, below);

    private static FakeApi Tree() => new(
        P(Erbil, null, "city", "Erbil", "هەولێر", 2), P(Duhok, null, "city", "Duhok"),
        P(Ankawa, Erbil, "neighbourhood", "Ankawa", "عەنکاوە", 2), P(Bakhtiari, Erbil, "neighbourhood", "Bakhtiari"),
        P(Main, Ankawa, "street", "Main Street", null, 1), P(Alley2, Ankawa, "alley", "Alley 2"),
        P(Alley1, Main, "alley", "Alley 1"));

    [Fact]
    public async Task Load_StartsWithTheCitiesAndNothingChosen()
    {
        var picker = new PlacePicker(Tree());
        await picker.LoadAsync(null);

        var level = Assert.Single(picker.Levels);
        Assert.Equal(new[] { "Erbil", "Duhok" }, level.Items.Select(p => p.Name).ToArray());
        Assert.Null(picker.PlaceId);
    }

    [Fact]
    public async Task Load_OpensTheListsAlongTheSavedPathAndSelectsEachStep()
    {
        var picker = new PlacePicker(Tree());
        await picker.LoadAsync([new(Erbil, "city", "Erbil"), new(Ankawa, "neighbourhood", "Ankawa"), new(Main, "street", "Main Street")]);

        Assert.Equal(new Guid?[] { Erbil, Ankawa, Main, null }, picker.Levels.Select(l => l.SelectedId).ToArray());   // a list of alleys below the street is open
        Assert.Equal(Main, picker.PlaceId);
        Assert.Equal(new[] { null, "city", "neighbourhood", "street" }, picker.Levels.Select(l => l.ParentKind).ToArray());
    }

    [Fact]
    public async Task Load_StopsAtAPlaceThatNoLongerExists()
    {
        var picker = new PlacePicker(Tree());
        await picker.LoadAsync([new(Erbil, "city", "Erbil"), new(Guid.NewGuid(), "neighbourhood", "Merged away")]);
        Assert.Equal(Erbil, picker.PlaceId);
        Assert.Equal(2, picker.Levels.Count);
    }

    [Fact]
    public async Task Select_OpensTheListBelowAndDropsTheDeeperListsWhenThePlaceChanges()
    {
        var picker = new PlacePicker(Tree());
        await picker.LoadAsync(null);
        await picker.SelectAsync(0, Erbil);
        await picker.SelectAsync(1, Ankawa);
        Assert.Equal(3, picker.Levels.Count);

        await picker.SelectAsync(1, Bakhtiari);
        Assert.Equal(3, picker.Levels.Count);                              // Bakhtiari's own (empty) list
        Assert.Empty(picker.Levels[2].Items);
        await picker.SelectAsync(0, Duhok);
        Assert.Equal(2, picker.Levels.Count);
        await picker.SelectAsync(0, null);                                 // clearing the city closes everything below
        Assert.Single(picker.Levels);
        Assert.Null(picker.PlaceId);
        await picker.SelectAsync(0, Guid.NewGuid());                        // not in the list: ignored
        Assert.Null(picker.PlaceId);
    }

    [Fact]
    public async Task Select_AnAlleyIsTheEndOfThePath()
    {
        var picker = new PlacePicker(Tree());
        await picker.LoadAsync([new(Erbil, "city", "Erbil"), new(Ankawa, "neighbourhood", "Ankawa")]);
        await picker.SelectAsync(2, Alley2);   // an alley straight under a neighbourhood

        Assert.Equal(3, picker.Levels.Count);
        Assert.Equal(Alley2, picker.PlaceId);
    }

    [Fact]
    public async Task Suggest_MatchesAnyNameIgnoringCaseAndArabicLookAlikesAndPutsPrefixesFirst()
    {
        var picker = new PlacePicker(Tree());
        await picker.LoadAsync([new(Erbil, "city", "Erbil")]);

        Assert.Equal(new[] { "Ankawa" }, picker.Suggest(1, "  ANK", "en").Select(p => p.Name).ToArray());
        Assert.Equal(new[] { "Ankawa" }, picker.Suggest(1, "عەنکا", "ckb").Select(p => p.Name).ToArray());   // the Kurdish name
        Assert.Equal(new[] { "Ankawa" }, picker.Suggest(1, "عەنكا", "ckb").Select(p => p.Name).ToArray());   // typed with an Arabic kaf
        Assert.Equal(new[] { "Bakhtiari", "Ankawa" }, picker.Suggest(1, "a", "en").OrderBy(p => p.Name == "Bakhtiari" ? 0 : 1).Select(p => p.Name).ToArray());
        Assert.Empty(picker.Suggest(1, "   ", "en"));
        Assert.Empty(picker.Suggest(7, "ank", "en"));
        Assert.Single(picker.Suggest(1, "a", "en", max: 1));
    }

    [Theory]
    [InlineData(null, new string[0])]
    [InlineData("city", new[] { "neighbourhood" })]
    [InlineData("neighbourhood", new[] { "street", "alley" })]
    [InlineData("street", new[] { "alley" })]
    [InlineData("alley", new string[0])]
    public void AddableKinds_FollowTheTree(string? parentKind, string[] expected) =>
        Assert.Equal(expected, PlacePicker.AddableKinds(new PlacePickerLevel { ParentKind = parentKind }).ToArray());

    [Fact]
    public async Task Add_CreatesTheNewNameBelowTheChosenPlaceAndSelectsIt()
    {
        var api = Tree();
        var picker = new PlacePicker(api);
        await picker.LoadAsync([new(Erbil, "city", "Erbil")]);

        Assert.Equal(PlaceAddResult.Added, await picker.AddAsync(1, "neighbourhood", "  Nishtiman "));

        Assert.Equal(new[] { (Erbil, "neighbourhood", "Nishtiman") }, api.Created.Select(c => (c.Parent, c.Kind, c.Name)).ToArray());
        Assert.Equal("Nishtiman", picker.Levels[1].Items.Single(p => p.Id == picker.PlaceId).Name);
        Assert.Equal(3, picker.Levels.Count);   // the list below the new neighbourhood is open (empty)
    }

    [Fact]
    public async Task Add_AStreetOrAnAlleyBelowANeighbourhood()
    {
        var api = Tree();
        var picker = new PlacePicker(api);
        await picker.LoadAsync([new(Erbil, "city", "Erbil"), new(Ankawa, "neighbourhood", "Ankawa")]);

        Assert.Equal(PlaceAddResult.Added, await picker.AddAsync(2, "street", "Hospital Street"));
        Assert.Equal(PlaceAddResult.Added, await picker.AddAsync(3, "alley", "Alley A"));
        Assert.Equal(new[] { "street", "alley" }, api.Created.Select(c => c.Kind).ToArray());
        Assert.Equal(("Alley A", "alley"), (picker.Levels[3].Items.Single(p => p.Id == picker.PlaceId).Name, picker.Levels[3].Items.Single(p => p.Id == picker.PlaceId).Kind));
    }

    [Fact]
    public async Task Add_AnExistingNameIsChosenInsteadOfAddedAgain()
    {
        var api = Tree();
        var picker = new PlacePicker(api);
        await picker.LoadAsync([new(Erbil, "city", "Erbil")]);

        Assert.Equal(PlaceAddResult.UsedExisting, await picker.AddAsync(1, "neighbourhood", " ankawa "));
        Assert.Empty(api.Created);
        Assert.Equal(Ankawa, picker.PlaceId);
    }

    [Fact]
    public async Task Add_ANameThatAnotherEmployeeAddedAMomentAgoIsChosen()
    {
        var api = Tree();
        api.OnCreate = (parent, kind, name) =>
        {
            api.Add(P(Guid.NewGuid(), parent, kind, name));   // appears on the server, but the answer is "already exists"
            return (VisitActionResult.Conflict, null);
        };
        var picker = new PlacePicker(api);
        await picker.LoadAsync([new(Erbil, "city", "Erbil")]);

        Assert.Equal(PlaceAddResult.UsedExisting, await picker.AddAsync(1, "neighbourhood", "Nishtiman"));
        Assert.Equal("Nishtiman", picker.Levels[1].Items.Single(p => p.Id == picker.PlaceId).Name);
    }

    [Fact]
    public async Task Add_RefusesWhatTheTreeDoesNotAllowAndReportsServerFailures()
    {
        var api = Tree();
        var picker = new PlacePicker(api);
        await picker.LoadAsync([new(Erbil, "city", "Erbil")]);

        Assert.Equal(PlaceAddResult.Failed, await picker.AddAsync(0, "city", "Sulaymaniyah"));            // the app does not add cities
        Assert.Equal(PlaceAddResult.Failed, await picker.AddAsync(1, "street", "Skips a level"));
        Assert.Equal(PlaceAddResult.Failed, await picker.AddAsync(1, "neighbourhood", "   "));
        Assert.Equal(PlaceAddResult.Failed, await picker.AddAsync(9, "neighbourhood", "x"));
        Assert.Equal(VisitActionResult.InvalidData, picker.Result);
        Assert.Empty(api.Created);

        api.OnCreate = (_, _, _) => (VisitActionResult.NetworkError, null);
        Assert.Equal(PlaceAddResult.Failed, await picker.AddAsync(1, "neighbourhood", "Nishtiman"));
        Assert.Equal(VisitActionResult.NetworkError, picker.Result);
        Assert.Equal(Erbil, picker.PlaceId);   // the choice made before is unchanged
    }

    [Fact]
    public async Task Load_ReportsAFailureAndKeepsWhatWasLoaded()
    {
        var api = Tree();
        api.Fail = VisitActionResult.NetworkError;
        var picker = new PlacePicker(api);
        await picker.LoadAsync(null);
        Assert.Equal(VisitActionResult.NetworkError, picker.Result);
        Assert.Empty(picker.Levels);
    }

    [Theory]
    [InlineData("  Gulan   Street ", "gulan street")]
    [InlineData("ڕزگاري", "ڕزگاری")]
    [InlineData("كەركوك", "کەرکوک")]
    [InlineData("شـەقام", "شەقام")]
    [InlineData("A‌B", "ab")]
    public void Normalize_UnifiesCaseSpacingAndLookAlikes(string text, string expected) => Assert.Equal(expected, PlacePicker.Normalize(text));

    private sealed class FakeApi : ICustomersApiClient
    {
        private readonly List<PlaceItem> _places;
        public FakeApi(params PlaceItem[] places) => _places = places.ToList();
        public List<(Guid Parent, string Kind, string Name)> Created { get; } = new();
        public Func<Guid, string, string, (VisitActionResult, Guid?)>? OnCreate { get; set; }
        public VisitActionResult Fail { get; set; } = VisitActionResult.Success;

        public void Add(PlaceItem place) => _places.Add(place);

        public Task<(VisitActionResult Result, IReadOnlyList<PlaceItem> Places)> GetPlacesAsync(Guid? parentId, CancellationToken cancellationToken = default) =>
            Task.FromResult<(VisitActionResult, IReadOnlyList<PlaceItem>)>(Fail != VisitActionResult.Success
                ? (Fail, Array.Empty<PlaceItem>())
                : (VisitActionResult.Success, _places.Where(p => p.ParentId == parentId).Select(p => p with { ChildCount = _places.Count(c => c.ParentId == p.Id) }).ToList()));

        public Task<(VisitActionResult Result, Guid? Id)> CreatePlaceAsync(Guid parentId, string kind, string name, CancellationToken cancellationToken = default)
        {
            if (OnCreate is { } custom) return Task.FromResult(custom(parentId, kind, name));
            Created.Add((parentId, kind, name));
            var id = Guid.NewGuid();
            _places.Add(P(id, parentId, kind, name));
            return Task.FromResult<(VisitActionResult, Guid?)>((VisitActionResult.Success, id));
        }

        public Task<(VisitActionResult Result, CustomerProfile? Customer)> GetCustomerAsync(Guid customerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<VisitActionResult> SetProfileAsync(Guid customerId, string? phone, string? address, Guid? placeId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<(VisitActionResult Result, Guid? Id)> CreateCustomerAsync(string type, string code, string name, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<VisitActionResult> SetLocationAsync(Guid customerId, DeviceFix fix, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
