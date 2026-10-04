# .NET MAUI Shared Authentication — Architecture & Design Document

**Status:** Proposed / Ready for Review  
**Target Projects:** `src/LifeDrop.Mobile.Auth` (plain `net10.0`), `src/LifeDrop.Mobile` (MAUI), `tests/LifeDrop.Mobile.Auth.Tests` (plain `net10.0`)  
**Related Server Endpoints:** `POST /auth/devices/register`, `POST /auth/login`, `POST /auth/refresh`, `POST /auth/logout`

---

## 1. Prerequisites & Environment Status

| Component | Status in Workspace | Required Action |
|---|---|---|
| **.NET SDK** | `10.0.401` Installed | None |
| **MAUI Workload** (`maui-mobile`) | **Not Installed** (`dotnet workload list` is empty) | Install the mobile workload through Visual Studio Installer or `dotnet workload install maui-mobile`; elevation is not universally required |
| **Android SDK / JDK / Build tools** | Not verified | Install or verify separately; the .NET workload alone does not prove Android build readiness |
| **iOS Build Environment** | **Unavailable on Windows** | Mark iOS build as **Unverified** until macOS + Xcode environment is connected |

Prerequisite installation is outside this design step. Recheck `dotnet workload list` and Android build prerequisites before claiming a build. Configure an HTTPS API address reachable from the emulator or device; `localhost:5256` on a phone is not the development PC.

---

## 2. Server Auth Contracts & Mapping

The mobile client interacts exclusively with the existing ASP.NET Core endpoints defined in `src/LifeDrop.API/Program.cs`:

### 2.1. `POST /auth/devices/register`
- **Request Body:**
  ```json
  {
    "tenantCode": "string",
    "email": "string",
    "password": "string",
    "deviceLabel": "string",
    "platform": "android | ios",
    "installId": "string",
    "appVersion": "string"
  }
  ```
- **Responses:**
  - `200 OK`: `{ "deviceId": "guid", "deviceSecret": "string" }`
  - `401 Unauthorized`: Invalid credentials
  - `409 Conflict`: Registration could not be created (including a duplicate installation); the API does not return an existing device secret
  - `400 Bad Request`: Validation failure (e.g. unsupported platform)

### 2.2. `POST /auth/login`
- **Request Body:**
  ```json
  {
    "tenantCode": "string",
    "email": "string",
    "password": "string",
    "deviceId": "guid?",
    "deviceSecret": "string?"
  }
  ```
- **Responses:**
  - `200 OK`:
    ```json
    {
      "accessToken": "jwt_string",
      "refreshToken": "opaque_string",
      "tokenType": "Bearer",
      "expiresAt": "2026-10-01T20:00:00Z",
      "refreshExpiresAt": "2026-10-08T20:00:00Z"
    }
    ```
  - `401 Unauthorized`: Invalid credentials
  - `403 Forbidden` with body `{ "code": "DEVICE_NOT_APPROVED" }`: Device binding required but device pending/unapproved.

### 2.3. `POST /auth/refresh`
- **Request Body:**
  ```json
  {
    "refreshToken": "opaque_string",
    "deviceId": "guid?",
    "deviceSecret": "string?"
  }
  ```
- **Responses:**
  - `200 OK`: New rotated `AccessToken` and `RefreshToken`
  - `401 Unauthorized`: Refresh rejected (for example expired, revoked, replayed, or invalid device credentials); the client cannot infer the exact cause from this status

### 2.4. `POST /auth/logout`
- **Headers:** `Authorization: Bearer <accessToken>`
- **Responses:**
  - `204 No Content`: Entire session family revoked
  - `401 / 403`: Unauthenticated or forbidden

---

## 3. Secure Storage Boundary (`ISecureCredentialStore`)

To prevent secret leakage, tokens and device secrets must not be logged, saved in plain storage, or included in diagnostics. The storage adapter must handle platform read/write failures without exposing values.

```csharp
namespace LifeDrop.Mobile.Auth;

public interface ISecureCredentialStore
{
    Task SaveTokensAsync(AuthTokens tokens, CancellationToken cancellationToken = default);
    Task<AuthTokens?> LoadTokensAsync(CancellationToken cancellationToken = default);
    Task ClearTokensAsync(CancellationToken cancellationToken = default);

    Task SaveDeviceAsync(DeviceRegistration device, CancellationToken cancellationToken = default);
    Task<DeviceRegistration?> LoadDeviceAsync(CancellationToken cancellationToken = default);
    Task ClearDeviceAsync(CancellationToken cancellationToken = default);

    Task ClearAllAsync(CancellationToken cancellationToken = default);
}
```

### Storage plan
- Store the session token pair and expiry fields as one versioned secure-storage value, so a partial multi-key write cannot leave a new access token with an old refresh token.
- Store device credentials with the normalized tenant code and email used for registration as a separate versioned value. The registration response does not include a user ID. Do not reuse credentials for another account; clear or replace them when switching accounts. Ordinary logout may retain them only for the same account.
- Use MAUI `SecureStorage` only in the platform adapter. On Android, handle backup/restore of encrypted preferences; on iOS, account for Keychain values surviving app uninstall and possible Keychain synchronization. Verify the intended device-binding semantics on each platform.
- There is no server `unlink` endpoint yet. Clearing a local device secret does not block its server record; lost or replaced credentials require a documented re-registration and administrator approval flow.

---

## 4. Token Refresh & Session Management (`IAuthSession`)

- **Refresh Timing:** Lazy evaluation with a **30-second buffer** before access token expiration.
- **Concurrency Control:** Protected by `SemaphoreSlim(1, 1)` to prevent duplicate concurrent refresh requests.
- **Family Revocation Handling:** If `/auth/refresh` returns `401 Unauthorized`, clear local tokens and transition to `Unauthenticated`. Do not treat a timeout or transient network failure as a `401`.
- **Rotation Safety:** Persist the new token pair before releasing the refresh lock or allowing another authenticated request. On a crash between server rotation and local save, the user may need to sign in again; do not retry the consumed refresh token repeatedly.
- **Logout:** Call the server with a valid access token and clear local tokens even if the server is unreachable, while clearly reporting that server revocation is unconfirmed. Retain device credentials only for the same tenant/user; account switching performs a full local reset.
- **Registration:** The returned device secret is shown only once by the server. Save it securely before telling the user registration succeeded. A pending device must wait for administrator approval before login.
- **Lost device secret:** A repeat registration with the same installation ID may return `409` and cannot recover the old secret. Use a new installation ID for an explicit recovery registration, require administrator approval, and arrange for the old device to be blocked.

---

## 5. File Checklist (Implementation Plan)

### Project Scaffolding:
- [ ] `src/LifeDrop.Mobile.Auth/LifeDrop.Mobile.Auth.csproj` (plain `net10.0` library; HTTP contracts, session logic, and storage interface)
- [ ] `src/LifeDrop.Mobile/LifeDrop.Mobile.csproj` (Targeting `net10.0-android`, `net10.0-ios`)
- [ ] `src/LifeDrop.Mobile/MauiProgram.cs`
- [ ] `src/LifeDrop.Mobile/App.xaml` + `App.xaml.cs`
- [ ] `src/LifeDrop.Mobile/AppShell.xaml` + `AppShell.xaml.cs`
- [ ] Update `LifeDrop.slnx` with the shared library, MAUI host, and test project when created

### Models & Contracts:
- [ ] `src/LifeDrop.Mobile.Auth/Models/AuthTokens.cs`
- [ ] `src/LifeDrop.Mobile.Auth/Models/DeviceRegistration.cs`
- [ ] `src/LifeDrop.Mobile.Auth/Models/LoginRequest.cs`
- [ ] `src/LifeDrop.Mobile.Auth/Models/RegisterDeviceRequest.cs`

### Services:
- [ ] `src/LifeDrop.Mobile.Auth/Services/ISecureCredentialStore.cs`
- [ ] `src/LifeDrop.Mobile/Services/SecureCredentialStore.cs` (MAUI `SecureStorage` wrapper)
- [ ] `src/LifeDrop.Mobile.Auth/Services/IAuthApiClient.cs`
- [ ] `src/LifeDrop.Mobile.Auth/Services/AuthApiClient.cs` (`HttpClient` client)
- [ ] `src/LifeDrop.Mobile.Auth/Services/IAuthSession.cs`
- [ ] `src/LifeDrop.Mobile.Auth/Services/AuthSession.cs`

### UI Scaffolding:
- [ ] `src/LifeDrop.Mobile/Pages/LoginPage.xaml` + `LoginPage.xaml.cs`

### Unit & Logic Tests:
- [ ] `tests/LifeDrop.Mobile.Auth.Tests/LifeDrop.Mobile.Auth.Tests.csproj` (plain `net10.0`; references the shared library, not the MAUI host)
- [ ] `tests/LifeDrop.Mobile.Auth.Tests/Services/AuthApiClientTests.cs`
- [ ] `tests/LifeDrop.Mobile.Auth.Tests/Services/AuthSessionTests.cs`
- [ ] `tests/LifeDrop.Mobile.Auth.Tests/Stubs/InMemoryCredentialStore.cs`
- [ ] `tests/LifeDrop.Mobile.Auth.Tests/Stubs/StubHttpMessageHandler.cs`
- [ ] Verify the actual MAUI `SecureStorage` adapter on each platform when a build environment is available; plain `net10.0` tests cannot exercise platform storage

---

## 6. Verification Boundaries

- **Logic Tests:** Planned via `dotnet test tests/LifeDrop.Mobile.Auth.Tests/` on Windows (`net10.0`); not yet run.
- **Android Compilation:** Planned via `dotnet build src/LifeDrop.Mobile/ -f net10.0-android` after toolchain setup; not yet run.
- **Android Runtime / SecureStorage:** Unverified until emulator or device testing.
- **iOS Compilation & Runtime:** Unverified until a Mac/Xcode build host and simulator or device are available.
- **GPS / Geofencing / Location Tracking:** Explicitly excluded from this slice.

## 7. Tooling references

- [.NET MAUI installation and Android dependencies](https://learn.microsoft.com/en-us/dotnet/maui/get-started/installation?view=net-maui-10.0)
- [.NET workload install behavior](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-workload-install)
- [.NET MAUI SecureStorage behavior](https://learn.microsoft.com/en-us/dotnet/maui/platform-integration/storage/secure-storage?view=net-maui-10.0)
- [Pair to Mac for iOS builds](https://learn.microsoft.com/en-us/dotnet/maui/ios/pair-to-mac?view=net-maui-10.0)
