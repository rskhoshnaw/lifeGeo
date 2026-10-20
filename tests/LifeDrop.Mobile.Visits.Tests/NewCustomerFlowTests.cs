using Xunit;

namespace LifeDrop.Mobile.Visits.Tests;

public class NewCustomerFlowTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 6, 0, 0, TimeSpan.Zero);   // 5 Oct 2026 in Baghdad
    private static readonly Guid Place = Guid.NewGuid();

    private static DeviceFix Fix(double accuracy = 8) => new(36.19, 44.01, accuracy, Now, false);

    [Fact]
    public void MedicalAssistant_IsOfferedAndHasItsOwnLabel()
    {
        Assert.Contains("MedicalAssistant", NewCustomerFlow.Types);
        Assert.Equal("Medical assistant", DailyPlan.TypeLabel("medicalassistant"));
        Assert.Equal("Other", DailyPlan.TypeLabel("something"));
    }

    [Fact]
    public void Generate_MakesAReadableCodeFromTheDayAndFourSafeCharacters()
    {
        var next = new Queue<int>([0, 1, 2, 30]);
        Assert.Equal("N261005-ABC9", CustomerCodes.Generate(new DateOnly(2026, 10, 5), _ => next.Dequeue()));
        // The alphabet has no 0, 1, I, L or O
        var all = string.Concat(Enumerable.Range(0, 31).Select(i => CustomerCodes.Generate(new DateOnly(2026, 1, 2), n => i % n)[^1]));
        Assert.DoesNotContain(all, c => "01ILO".Contains(c));
        Assert.Equal(31, all.Distinct().Count());
    }

    [Fact]
    public async Task Create_RegistersTheCustomerThenSavesTheProfileAndThePosition()
    {
        var api = new FakeApi();
        var flow = new NewCustomerFlow(api, new FixedClock(Now), _ => 0);

        var outcome = await flow.CreateAsync(new NewCustomerRequest("  Shifa Pharmacy ", "Pharmacy", " 0750 ", "  Near the bridge ", Place, Fix()));

        Assert.True(outcome.Complete);
        Assert.Equal(("N261005-AAAA", api.CustomerId), (outcome.Code, outcome.CustomerId));
        Assert.Equal(new[] { "create", "profile", "location" }, api.Calls.ToArray());
        Assert.Equal(("Pharmacy", "N261005-AAAA", "Shifa Pharmacy"), api.Created!.Value);
        Assert.Equal(("0750", "Near the bridge", (Guid?)Place), api.Profile!.Value);
        Assert.Equal(8, api.Location!.AccuracyMeters);
    }

    [Fact]
    public async Task Create_SavesTheContactDetailsOnTheirOwnRouteAndOnlyWhenGiven()
    {
        var api = new FakeApi();
        var flow = new NewCustomerFlow(api, new FixedClock(Now), _ => 0);
        var outcome = await flow.CreateAsync(new NewCustomerRequest("Shifa", "Pharmacy", null, null, null, null, "  Dr. Ali ", " ", " Pays on Sunday "));
        Assert.True(outcome.Complete);
        Assert.Equal(new[] { "create", "contact" }, api.Calls.ToArray());
        Assert.Equal(("Dr. Ali", (string?)null, "Pays on Sunday"), api.Contact!.Value);

        var none = new FakeApi();
        await new NewCustomerFlow(none, new FixedClock(Now), _ => 0).CreateAsync(new NewCustomerRequest("Shifa", "Pharmacy", null, null, null, null, " ", null, ""));
        Assert.Equal(new[] { "create" }, none.Calls.ToArray());
    }

    [Fact]
    public async Task Create_ACustomerWhoseContactDetailsFailedIsCreatedButNotComplete()
    {
        var api = new FakeApi { ContactResult = VisitActionResult.NetworkError };
        var outcome = await new NewCustomerFlow(api, new FixedClock(Now), _ => 0).CreateAsync(new NewCustomerRequest("Shifa", "Pharmacy", null, null, null, null, "Dr. Ali"));
        Assert.False(outcome.Complete);
        Assert.Equal(api.CustomerId, outcome.CustomerId);
        Assert.Equal(VisitActionResult.NetworkError, outcome.Contact);
    }

    [Fact]
    public async Task Create_WithOnlyANameSendsNothingElse()
    {
        var api = new FakeApi();
        var outcome = await new NewCustomerFlow(api, new FixedClock(Now), _ => 0).CreateAsync(new NewCustomerRequest("Dr. Karwan", "Doctor", "  ", null, null, null));
        Assert.True(outcome.Complete);
        Assert.Equal(new[] { "create" }, api.Calls.ToArray());
        Assert.Null(outcome.Profile);
        Assert.Null(outcome.Location);
    }

    [Fact]
    public async Task Create_TriesANewCodeWhenTheServerSaysItIsTaken()
    {
        var api = new FakeApi { TakenCodes = 2 };
        var calls = 0;
        var flow = new NewCustomerFlow(api, new FixedClock(Now), n => ++calls % n);

        var outcome = await flow.CreateAsync(new NewCustomerRequest("Shifa", "Pharmacy", null, null, null, null));

        Assert.True(outcome.Complete);
        Assert.Equal(3, api.CreateAttempts.Count);
        Assert.Equal(3, api.CreateAttempts.Distinct().Count());   // each attempt used a different code
        Assert.Equal(api.CreateAttempts[2], outcome.Code);
    }

    [Fact]
    public async Task Create_GivesUpAfterFiveTakenCodes()
    {
        var api = new FakeApi { TakenCodes = 99 };
        var outcome = await new NewCustomerFlow(api, new FixedClock(Now)).CreateAsync(new NewCustomerRequest("Shifa", "Pharmacy", "1", null, null, Fix()));

        Assert.Equal((VisitActionResult.Conflict, (Guid?)null), (outcome.Create, outcome.CustomerId));
        Assert.Equal(NewCustomerFlow.MaxAttempts, api.CreateAttempts.Count);
        Assert.Equal(new[] { "create" }, api.Calls.Distinct().ToArray());   // no profile or position without a customer
    }

    [Theory]
    [InlineData("", "Pharmacy")]
    [InlineData("   ", "Pharmacy")]
    [InlineData("Shifa", "Shop")]
    public async Task Create_RefusesAMissingNameOrAnUnknownTypeWithoutCallingTheServer(string name, string type)
    {
        var api = new FakeApi();
        var outcome = await new NewCustomerFlow(api, new FixedClock(Now)).CreateAsync(new NewCustomerRequest(name, type, null, null, null, null));
        Assert.Equal(VisitActionResult.InvalidData, outcome.Create);
        Assert.Empty(api.Calls);
    }

    [Fact]
    public async Task Create_RefusesAnInaccuratePositionBeforeCreatingAnything()
    {
        var api = new FakeApi();
        var outcome = await new NewCustomerFlow(api, new FixedClock(Now)).CreateAsync(new NewCustomerRequest("Shifa", "Pharmacy", null, null, null, Fix(accuracy: 250)));
        Assert.Equal(VisitActionResult.GpsLowAccuracy, outcome.Create);
        Assert.Empty(api.Calls);
        var limit = await new NewCustomerFlow(api, new FixedClock(Now)).CreateAsync(new NewCustomerRequest("Shifa", "Pharmacy", null, null, null, Fix(accuracy: 100)));
        Assert.True(limit.Complete);
    }

    [Fact]
    public async Task Create_AFailedProfileOrPositionKeepsTheCreatedCustomerAndSaysWhatFailed()
    {
        var api = new FakeApi { ProfileResult = VisitActionResult.NetworkError, LocationResult = VisitActionResult.Forbidden };
        var outcome = await new NewCustomerFlow(api, new FixedClock(Now)).CreateAsync(new NewCustomerRequest("Shifa", "Pharmacy", "1", null, Place, Fix()));

        Assert.False(outcome.Complete);
        Assert.Equal(VisitActionResult.Success, outcome.Create);
        Assert.NotNull(outcome.CustomerId);
        Assert.Equal((VisitActionResult?)VisitActionResult.NetworkError, outcome.Profile);
        Assert.Equal((VisitActionResult?)VisitActionResult.Forbidden, outcome.Location);
    }

    [Fact]
    public async Task Create_AFailedCreationSavesNothingMore()
    {
        var api = new FakeApi { CreateResult = VisitActionResult.Forbidden };
        var outcome = await new NewCustomerFlow(api, new FixedClock(Now)).CreateAsync(new NewCustomerRequest("Shifa", "Pharmacy", "1", null, Place, Fix()));
        Assert.Equal((VisitActionResult.Forbidden, (Guid?)null), (outcome.Create, outcome.CustomerId));
        Assert.Equal(new[] { "create" }, api.Calls.ToArray());
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FakeApi : ICustomersApiClient
    {
        public List<string> Calls { get; } = new();
        public List<string> CreateAttempts { get; } = new();
        public int TakenCodes { get; set; }
        public VisitActionResult CreateResult { get; set; } = VisitActionResult.Success;
        public VisitActionResult ProfileResult { get; set; } = VisitActionResult.Success;
        public VisitActionResult LocationResult { get; set; } = VisitActionResult.Success;
        public Guid CustomerId { get; } = Guid.NewGuid();
        public (string Type, string Code, string Name)? Created { get; private set; }
        public (string? Phone, string? Address, Guid? PlaceId)? Profile { get; private set; }
        public DeviceFix? Location { get; private set; }

        public Task<(VisitActionResult Result, Guid? Id)> CreateCustomerAsync(string type, string code, string name, CancellationToken cancellationToken = default)
        {
            Calls.Add("create");
            CreateAttempts.Add(code);
            if (CreateAttempts.Count <= TakenCodes) return Task.FromResult<(VisitActionResult, Guid?)>((VisitActionResult.Conflict, null));
            Created = (type, code, name);
            return Task.FromResult<(VisitActionResult, Guid?)>(CreateResult == VisitActionResult.Success ? (CreateResult, CustomerId) : (CreateResult, null));
        }

        public Task<VisitActionResult> SetProfileAsync(Guid customerId, string? phone, string? address, Guid? placeId, CancellationToken cancellationToken = default)
        {
            Calls.Add("profile");
            Profile = (phone, address, placeId);
            return Task.FromResult(ProfileResult);
        }

        public VisitActionResult ContactResult { get; set; } = VisitActionResult.Success;
        public (string? ContactName, string? Phone2, string? Notes)? Contact { get; private set; }

        public Task<VisitActionResult> SetContactAsync(Guid customerId, string? contactName, string? phone2, string? notes, CancellationToken cancellationToken = default)
        {
            Calls.Add("contact");
            Contact = (contactName, phone2, notes);
            return Task.FromResult(ContactResult);
        }

        public Task<VisitActionResult> SetLocationAsync(Guid customerId, DeviceFix fix, CancellationToken cancellationToken = default)
        {
            Calls.Add("location");
            Location = fix;
            return Task.FromResult(LocationResult);
        }

        public Task<(VisitActionResult Result, CustomerProfile? Customer)> GetCustomerAsync(Guid customerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<(VisitActionResult Result, IReadOnlyList<PlaceItem> Places)> GetPlacesAsync(Guid? parentId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<(VisitActionResult Result, Guid? Id)> CreatePlaceAsync(Guid parentId, string kind, string name, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
