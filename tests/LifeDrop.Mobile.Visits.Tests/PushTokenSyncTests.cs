using LifeDrop.Mobile.Localization;
using Xunit;

namespace LifeDrop.Mobile.Visits.Tests;

// The push token on the phone (plan step 3.8): registered after sign-in, again when it changes, removed at sign-out; the message text is written on the phone.
public class PushTokenSyncTests
{
    private sealed class Memory : IDurableStore
    {
        private readonly Dictionary<string, string> _data = [];
        public string? Read(string key) => _data.GetValueOrDefault(key);
        public void Write(string key, string value) => _data[key] = value;
        public void Delete(string key) => _data.Remove(key);
    }

    private sealed class Api(VisitActionResult result = VisitActionResult.Success) : IPushApiClient
    {
        public List<string> Calls { get; } = [];
        public VisitActionResult Result { get; set; } = result;
        public Task<VisitActionResult> RegisterAsync(string token, string platform, CancellationToken cancellationToken = default) { Calls.Add($"register {platform} {token}"); return Task.FromResult(Result); }
        public Task<VisitActionResult> RemoveAsync(string token, CancellationToken cancellationToken = default) { Calls.Add($"remove {token}"); return Task.FromResult(Result); }
    }

    private const string TokenA = "token-aaaaaaaaaaaaaaaaaaaa-1";
    private const string TokenB = "token-bbbbbbbbbbbbbbbbbbbb-2";

    [Fact]
    public async Task ARegisteredTokenIsNotSentAgainUntilItChanges()
    {
        var api = new Api();
        var sync = new PushTokenSync(api, new Memory());
        Assert.False(await sync.EnsureRegisteredAsync());              // no token yet: nothing to do
        Assert.Empty(api.Calls);
        sync.TokenReceived(TokenA);
        Assert.True(await sync.EnsureRegisteredAsync());
        Assert.True(await sync.EnsureRegisteredAsync());
        Assert.Equal([$"register android {TokenA}"], api.Calls);
        sync.TokenReceived(TokenA);                                    // the same token again changes nothing
        Assert.True(await sync.EnsureRegisteredAsync());
        Assert.Single(api.Calls);
        sync.TokenReceived(TokenB);                                    // refreshed by the system
        Assert.True(await sync.EnsureRegisteredAsync());
        Assert.Equal([$"register android {TokenA}", $"register android {TokenB}"], api.Calls);
    }

    [Fact]
    public async Task ALostAnswerMeansRegisterAgainNeverAssumeItWorked()
    {
        var api = new Api(VisitActionResult.NetworkError);
        var sync = new PushTokenSync(api, new Memory());
        sync.TokenReceived(TokenA);
        Assert.False(await sync.EnsureRegisteredAsync());
        api.Result = VisitActionResult.Success;
        Assert.True(await sync.EnsureRegisteredAsync());
        Assert.Equal(2, api.Calls.Count);
    }

    [Fact]
    public async Task SignOutRemovesTheTokenFromTheServerAndTheNextUserRegistersItAgain()
    {
        var store = new Memory();
        var api = new Api();
        var sync = new PushTokenSync(api, store);
        sync.TokenReceived(TokenA);
        await sync.EnsureRegisteredAsync();
        await sync.RemoveAsync();
        Assert.Equal($"remove {TokenA}", api.Calls[^1]);
        Assert.Null(store.Read(PushTokenSync.RegisteredKey));
        Assert.Equal(TokenA, sync.Token);                              // the phone keeps its token for the next sign-in
        Assert.True(await sync.EnsureRegisteredAsync());
        Assert.Equal(3, api.Calls.Count);

        // Without internet the removal fails: the marker stays, nothing breaks
        api.Result = VisitActionResult.NetworkError;
        await sync.RemoveAsync();
        Assert.Equal(TokenA, store.Read(PushTokenSync.RegisteredKey));
    }

    [Fact]
    public void ATokenThatIsTooShortIsIgnoredAndTheSystemCallbackOnlyNeedsTheStore()
    {
        var store = new Memory();
        PushTokenSync.RememberToken(store, "short");
        Assert.Null(store.Read(PushTokenSync.TokenKey));
        PushTokenSync.RememberToken(store, "  " + TokenA + "  ");
        Assert.Equal(TokenA, store.Read(PushTokenSync.TokenKey));
    }

    [Theory]
    [InlineData("customer.profile_changed", "app.push.customerProfileChanged")]
    [InlineData("customer.location_changed", "app.push.customerLocationChanged")]
    [InlineData("something.else", null)]
    [InlineData(null, null)]
    public void MessageKindsMapToTextKeysThatExistInEveryLanguage(string? kind, string? key)
    {
        Assert.Equal(key, PushMessages.TextKey(kind));
        if (key is not null)
            foreach (var (code, _) in Texts.Languages) Assert.False(string.IsNullOrWhiteSpace(Texts.Raw(code, key)), $"{key}.{code}");
    }
}
