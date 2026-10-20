using LifeDrop.Mobile.Auth;

namespace LifeDrop.Mobile.Visits;

// Who the signed-in user is (GET /me).
public sealed record MyAccount(string? Email, IReadOnlyList<string> Roles, IReadOnlyList<string> Permissions);

// "My account" (plan step 4.46): the profile and changing one's own password. Online only.
public interface IAccountApiClient
{
    Task<(VisitActionResult Result, MyAccount? Account)> GetAccountAsync(CancellationToken cancellationToken = default);
    // WrongPassword: the current password is not right (nothing changes). On success the user's other sessions end; this one stays.
    Task<VisitActionResult> ChangePasswordAsync(string currentPassword, string newPassword, CancellationToken cancellationToken = default);
}

public static class PasswordRules
{
    public const int MinLength = 12;
    public const int MaxLength = 200;

    // Checked on the phone before anything is sent; the server checks again.
    public static VisitActionResult? Check(string? current, string? next, string? repeat)
    {
        if (string.IsNullOrEmpty(current) || string.IsNullOrEmpty(next)) return VisitActionResult.InvalidData;
        if (next.Length < MinLength || next.Length > MaxLength || next == current) return VisitActionResult.InvalidData;
        return next == repeat ? null : VisitActionResult.InvalidData;
    }
}

public sealed class AccountApiClient : IAccountApiClient
{
    private readonly OrdersHttp _http;

    public AccountApiClient(HttpClient httpClient, IAuthSession session)
    {
        _http = new OrdersHttp(httpClient ?? throw new ArgumentNullException(nameof(httpClient)), session ?? throw new ArgumentNullException(nameof(session)));
    }

    public async Task<(VisitActionResult Result, MyAccount? Account)> GetAccountAsync(CancellationToken cancellationToken = default)
    {
        var (result, me) = await _http.CallAsync<MeDto>(HttpMethod.Get, "/me", null, cancellationToken);
        if (result != VisitActionResult.Success) return (result, null);
        return me is null ? (VisitActionResult.ServerError, null) : (result, new MyAccount(me.Email, me.Roles ?? [], me.Permissions ?? []));
    }

    public async Task<VisitActionResult> ChangePasswordAsync(string currentPassword, string newPassword, CancellationToken cancellationToken = default) =>
        (await _http.CallAsync<object>(HttpMethod.Put, "/me/password", new { currentPassword, newPassword }, cancellationToken)).Result;

    private sealed record MeDto(string? Email, string[]? Roles, string[]? Permissions);
}
