using LifeDrop.Mobile.Auth.Models;
using Xunit;

namespace LifeDrop.Mobile.Auth.Tests;

// Plan step 5.64: the whole story of a phone from the user's side, against a small stand-in of the server's rules (register, approve, delete, login). The same bug came back twice (5.49, then
// 5.63) because each fix mended one screen and no test walked the user's way through all of them. Every way back in after the manager deletes a phone must work with only the buttons on screen.
public class DeviceLifecycleScenarioTests
{
    private const string Tenant = "lifedrops";
    private const string Email = "user@test.com";

    private sealed class Phone : ISecureCredentialStore
    {
        public RegisteredDevice? Device;
        public StoredSession? Session;
        public Task SaveDeviceAsync(RegisteredDevice device, CancellationToken cancellationToken = default) { Device = device; return Task.CompletedTask; }
        public Task<RegisteredDevice?> LoadDeviceAsync(CancellationToken cancellationToken = default) => Task.FromResult(Device);
        public Task ClearDeviceAsync(CancellationToken cancellationToken = default) { Device = null; return Task.CompletedTask; }
        public Task SaveSessionAsync(StoredSession session, CancellationToken cancellationToken = default) { Session = session; return Task.CompletedTask; }
        public Task<StoredSession?> LoadSessionAsync(CancellationToken cancellationToken = default) => Task.FromResult(Session);
        public Task ClearSessionAsync(CancellationToken cancellationToken = default) { Session = null; return Task.CompletedTask; }
        public Task<bool> ClearSessionIfMatchesAsync(StoredSession expectedSession, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> MarkSessionRefreshingAsync(StoredSession expectedSession, StoredSession pendingSession, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> CommitSessionRefreshAsync(StoredSession pendingSession, StoredSession newSession, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task ClearAllAsync(CancellationToken cancellationToken = default) { Device = null; Session = null; return Task.CompletedTask; }
    }

    // The server's rules for devices, as the API has them: a registration is pending until a manager approves it; a live registration of the same install id conflicts with the reason;
    // a deleted device is unknown; a login of a role that needs a phone is refused unless the phone sends an approved device with its secret.
    private sealed class Server : IAuthApiClient
    {
        private sealed class Row { public Guid Id; public string Secret = ""; public string InstallId = ""; public string Status = "pending"; public bool Deleted; }
        private readonly List<Row> _rows = new();
        public int RegisterCalls { get; private set; }

        public void ApproveLatest() => _rows.Last(r => !r.Deleted).Status = "approved";
        public void DeleteLatest() => _rows.Last(r => !r.Deleted).Deleted = true;
        public int LiveRows => _rows.Count(r => !r.Deleted);

        public Task<AuthResult<RegisteredDevice>> RegisterDeviceAsync(RegisterDeviceRequest request, CancellationToken cancellationToken = default)
        {
            RegisterCalls++;
            if (!string.Equals(request.TenantCode, Tenant, StringComparison.OrdinalIgnoreCase)) return Task.FromResult(AuthResult<RegisteredDevice>.InvalidCredentials());
            var live = _rows.FirstOrDefault(r => !r.Deleted && r.InstallId == request.InstallId);
            if (live is not null) return Task.FromResult(AuthResult<RegisteredDevice>.Conflict("registered", live.Status == "approved" ? "DEVICE_ALREADY_APPROVED" : "DEVICE_PENDING"));
            var row = new Row { Id = Guid.NewGuid(), Secret = Convert.ToBase64String(Guid.NewGuid().ToByteArray()), InstallId = request.InstallId };
            _rows.Add(row);
            return Task.FromResult(AuthResult<RegisteredDevice>.Ok(new RegisteredDevice(row.Id, row.Secret, request.TenantCode, request.Email)));
        }

        public Task<AuthResult<AuthTokens>> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default)
        {
            if (request.DeviceId is null || request.DeviceId == Guid.Empty) return Task.FromResult(AuthResult<AuthTokens>.DeviceNotApproved("DEVICE_NOT_APPROVED"));
            var row = _rows.FirstOrDefault(r => r.Id == request.DeviceId);
            if (row is null || row.Deleted) return Task.FromResult(AuthResult<AuthTokens>.DeviceNotApproved("DEVICE_UNKNOWN"));
            if (row.Status != "approved" || row.Secret != request.DeviceSecret) return Task.FromResult(AuthResult<AuthTokens>.DeviceNotApproved("DEVICE_NOT_APPROVED"));
            var now = DateTime.UtcNow;
            return Task.FromResult(AuthResult<AuthTokens>.Ok(new AuthTokens("access", "refresh", "Bearer", now.AddMinutes(15), now.AddDays(7))));
        }

        public Task<AuthResult<AuthTokens>> RefreshAsync(RefreshRequest request, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<AuthResult<bool>> LogoutAsync(string accessToken, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }

    // What the user can press: "Register" and "Log in".
    private sealed class User(Server server, Phone phone, string installId = "install-1")
    {
        public Task<AuthResult<RegisteredDevice>> Register(string tenant = Tenant) =>
            new DeviceRegistrationCoordinator(server, phone).RegisterDeviceAsync(new RegisterDeviceRequest(tenant, Email, "pass", "iPhone", "ios", installId, "1.0"));
        public Task<AuthResult<StoredSession>> LogIn(string tenant = Tenant) => new LoginCoordinator(server, phone).LoginAsync(tenant, Email, "pass");
    }

    [Fact]
    public async Task FirstTime_RegisterThenTheManagerApprovesThenLogIn()
    {
        var server = new Server(); var phone = new Phone(); var user = new User(server, phone);
        Assert.Equal(AuthStatus.Success, (await user.Register()).Status);
        Assert.Equal(AuthStatus.DeviceNotApproved, (await user.LogIn()).Status);          // not approved yet
        server.ApproveLatest();
        Assert.Equal(AuthStatus.Success, (await user.LogIn()).Status);
    }

    [Fact]
    public async Task ManagerDeletesThePhone_UserPressesRegisterFirst_AndGetsBackInAfterApproval()
    {
        var server = new Server(); var phone = new Phone(); var user = new User(server, phone);
        await user.Register(); server.ApproveLatest(); Assert.Equal(AuthStatus.Success, (await user.LogIn()).Status);
        server.DeleteLatest();                                                              // the phone still remembers the old registration
        Assert.Equal(AuthStatus.Success, (await user.Register()).Status);
        Assert.Equal(1, server.LiveRows);                                                   // a new registration exists on the server, waiting for the manager
        server.ApproveLatest();
        Assert.Equal(AuthStatus.Success, (await user.LogIn()).Status);
    }

    [Fact]
    public async Task ManagerDeletesThePhone_UserLogsInFirst_ThenRegisters_AndGetsBackInAfterApproval()
    {
        var server = new Server(); var phone = new Phone(); var user = new User(server, phone);
        await user.Register(); server.ApproveLatest(); await user.LogIn();
        server.DeleteLatest();
        var first = await user.LogIn();
        Assert.Equal((AuthStatus.DeviceNotApproved, "DEVICE_UNKNOWN"), (first.Status, first.ErrorCode));   // the phone is told, and forgets the old record
        Assert.Null(phone.Device);
        Assert.Equal(AuthStatus.Success, (await user.Register()).Status);
        server.ApproveLatest();
        Assert.Equal(AuthStatus.Success, (await user.LogIn()).Status);
    }

    [Fact]
    public async Task ManagerDeletesThePhone_UserKeepsPressingBoth_NeverGetsStuck()
    {
        var server = new Server(); var phone = new Phone(); var user = new User(server, phone);
        await user.Register(); server.ApproveLatest(); await user.LogIn();
        server.DeleteLatest();
        for (var i = 0; i < 3; i++) { await user.LogIn(); await user.Register(); }       // any order, any number of times
        Assert.Equal(1, server.LiveRows);                                                   // never a second live registration
        server.ApproveLatest();
        Assert.Equal(AuthStatus.Success, (await user.LogIn()).Status);
    }

    [Fact]
    public async Task ARecordOfAnotherCompanyOnThePhone_DoesNotBlockRegistering()
    {
        var server = new Server(); var phone = new Phone { Device = new RegisteredDevice(Guid.NewGuid(), "old", "other-company", Email) };
        var user = new User(server, phone);
        Assert.Equal(AuthStatus.Success, (await user.Register()).Status);                  // the server is asked and registers; the old record is replaced
        Assert.Equal(Tenant, phone.Device?.TenantCode);
    }
}
