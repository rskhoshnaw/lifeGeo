using LifeDrop.Mobile.Auth;

namespace LifeDrop.Mobile.Visits;

// The phone's push token (plan step 2.14 and 3.8): kept on the phone, registered with the server after sign-in and whenever it changes, removed at sign-out.
public interface IPushApiClient
{
    Task<VisitActionResult> RegisterAsync(string token, string platform, CancellationToken cancellationToken = default);
    Task<VisitActionResult> RemoveAsync(string token, CancellationToken cancellationToken = default);
}

public sealed class PushApiClient : IPushApiClient
{
    private readonly OrdersHttp _http;

    public PushApiClient(HttpClient httpClient, IAuthSession session) =>
        _http = new OrdersHttp(httpClient ?? throw new ArgumentNullException(nameof(httpClient)), session ?? throw new ArgumentNullException(nameof(session)));

    public async Task<VisitActionResult> RegisterAsync(string token, string platform, CancellationToken cancellationToken = default) =>
        (await _http.CallAsync<object>(HttpMethod.Put, "/push-token", new { token, platform }, cancellationToken)).Result;

    public async Task<VisitActionResult> RemoveAsync(string token, CancellationToken cancellationToken = default) =>
        (await _http.CallAsync<object>(HttpMethod.Post, "/push-token/remove", new { token }, cancellationToken)).Result;
}

// The token the system gave this install and the one the server already knows. The second is only written after the server said yes, so a lost answer
// means "register again", never "assume it worked". A token the next user of a shared phone registers moves to them on the server.
public sealed class PushTokenSync(IPushApiClient api, IDurableStore store, string platform = "android")
{
    public const string TokenKey = "push-token";
    public const string RegisteredKey = "push-token-registered";

    // Also used by the system callback when the app is not running: it only needs the store.
    public static void RememberToken(IDurableStore store, string token)
    {
        token = token.Trim();
        if (token.Length is < 20 or > 4096) return;
        if (store.Read(TokenKey) == token) return;
        store.Write(TokenKey, token);
        store.Delete(RegisteredKey);
    }

    public string? Token => store.Read(TokenKey);

    public void TokenReceived(string token) => RememberToken(store, token);

    // True when the server knows the current token.
    public async Task<bool> EnsureRegisteredAsync(CancellationToken cancellationToken = default)
    {
        var token = Token;
        if (string.IsNullOrEmpty(token)) return false;
        if (store.Read(RegisteredKey) == token) return true;
        var result = await api.RegisterAsync(token, platform, cancellationToken);
        if (result != VisitActionResult.Success) return false;
        store.Write(RegisteredKey, token);
        return true;
    }

    // At sign-out: the server stops pushing to this phone for this user. Best effort: without internet the token stays and moves to the next user who signs in here.
    public async Task RemoveAsync(CancellationToken cancellationToken = default)
    {
        var token = Token;
        if (string.IsNullOrEmpty(token)) return;
        var result = await api.RemoveAsync(token, cancellationToken);
        if (result is VisitActionResult.Success or VisitActionResult.NotFound) store.Delete(RegisteredKey);
    }
}

// What a push message says. The server sends only the kind and the ids; the text is written here, in the language of the app.
public static class PushMessages
{
    public static string? TextKey(string? kind) => kind switch
    {
        "customer.profile_changed" => "app.push.customerProfileChanged",
        "customer.location_changed" => "app.push.customerLocationChanged",
        _ => null
    };
}
