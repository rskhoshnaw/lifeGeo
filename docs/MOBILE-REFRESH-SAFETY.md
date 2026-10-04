# Mobile Refresh-Token Safety Design

## 1. Context and The Replay Risk
Currently, `AuthSession` initiates a refresh request to the server while leaving the existing `StoredSession` active on disk. If the server successfully processes the rotation but the client fails to receive or save the new tokens (due to network timeout, crash, or storage write failure), the client and server states diverge. 

On the next access or app restart, the client will attempt to use the old refresh token. The server will reject this (since the token was already consumed and rotated). Without a durable fail-closed mechanism, the client is risking replay attacks and desynchronization.

## 2. Persistent State Machine Design
We propose a strict binary session state machine. It can prevent retry of a possibly consumed token only when the storage adapter meets the durability and atomicity contract below.

### State Transition Table

| Current State | Trigger Action | Next Local State | Outcome / Action |
| :--- | :--- | :--- | :--- |
| **Active** | Token expires / Refresh needed | **RefreshPending** | Durably save `RefreshPending` state to disk *before* initiating network call. |
| **RefreshPending** | Server returns 200 OK | **Active** | Durably save new tokens with `Active` state to disk. Return new access token. |
| **RefreshPending** | Server returns 401 | **Cleared** | Refresh was rejected. Clear only the auth session; preserve device registration and offline work. Require login again. |
| **RefreshPending** | Server returns 403 or another unexpected status | **RefreshPending** | Treat the outcome conservatively as unknown; do not retry the old token. |
| **RefreshPending** | Network Timeout / HTTP 50x / Crash | **RefreshPending** | Outcome is unknown. Leave as `RefreshPending`. Return `LocalStateError`. |
| **RefreshPending** | Initial Load (App Launch) | **RefreshPending** | Return a local reauthentication-required state without sending Refresh. Clear the obsolete auth session only after a deliberate recovery/login flow. |

## 3. Storage Interface & Model Changes
Adding a `Status` property alone is insufficient: the current `SaveSessionAsync` signature does not promise an atomic, durable pre-flight write or an atomic commit. The platform adapter must provide those guarantees, and the plain `net10.0` tests cannot prove them across an app crash.

```csharp
public enum SessionStatus 
{
    Active,
    RefreshPending 
}

// Illustrative persisted states; RefreshPending must never be treated as Active.
public SessionStatus Status { get; init; } = SessionStatus.Active;
```

Required storage operations (names are illustrative):
1. `TryBeginRefreshAsync(expectedSessionVersion)`: atomically compare the active session and durably mark it pending **before** network dispatch. If this operation fails or its outcome is uncertain, do not send Refresh.
2. `CommitRefreshAsync(attemptId, newSession)`: atomically replace the pending attempt with the new active token pair. Return success only after persistence completes.
3. `LoadSessionStateAsync()`: return either active or pending state after restart. A pending state must never expose the old refresh token as usable to `AuthSession`.

The attempt/version prevents separate `AuthSession` instances from refreshing the same token concurrently; a process-local `SemaphoreSlim` alone does not. Define how the MAUI secure-storage adapter implements and verifies these operations before claiming an across-restart guarantee.

## 4. Crash-Recovery & Unknown Outcomes
- **App Crash / Process Death:** If the app dies during `_apiClient.RefreshAsync`, a correctly durable store returns `RefreshPending` after restart. `AuthSession` must not retry Refresh and must request reauthentication when online; it need not erase the pending record immediately.
- **Save Failure After Success:** A failed or uncertain commit must not make the old token active again. The next call must reload state and fail closed if the commit cannot be proven. Do not assume an exception proves which value remained on disk.
- **Network Timeout:** If the HTTP client times out, the client cannot know if the server rotated the token. The session remains in `RefreshPending`, locking out any retry attempts.
- **Pre-flight Write Failure:** Never dispatch the HTTP request unless durable pending state is confirmed.

## 5. User-Visible Re-Login Outcome
Because `RefreshPending` causes the session to fail closed, the UI must request reauthentication when connectivity returns. It may allow already cached, authorized offline work to continue under its separate offline policy; this design does not delete the offline queue, visit drafts, or device registration.

## 6. Explicit Offline Usability Tradeoff
**The Tradeoff:** Strict replay avoidance can force reauthentication after a brief connectivity loss in the refresh window.

If a user hits a dead zone exactly when their token hits the 30-second expiration buffer, the client will durably mark the session as `RefreshPending` and attempt a network call. If the connection drops (e.g., `HttpRequestException` or Timeout), the refresh fails. 

Because we cannot prove the request did not reach the server, the client must not retry the old refresh token. A user who briefly loses signal may need to sign in again once online. Offline data remains separate and must be preserved.

## 7. Focused Test Plan
We will add the following tests to `LifeDrop.Mobile.Auth.Tests`:

1. **`GetAccessTokenAsync_MarksInFlight_BeforeCallingApi`**:
   - Provide a custom `FakeCredentialStore`.
   - Verify that the durable begin operation completes before the `IAuthApiClient` delegate is invoked; a failed or uncertain begin sends nothing.
2. **`GetAccessTokenAsync_NetworkTimeout_LeavesSessionInFlight`**:
   - Simulate a network timeout.
   - Verify the final state in the store is `RefreshPending` (not cleared, but un-retriable).
3. **`GetAccessTokenAsync_RecoversFromInFlight_ByFailingClosed`**:
   - Initialize the store with a `RefreshPending` session.
   - Verify `GetAccessTokenAsync` returns a distinct local reauthentication-required result without invoking the API or reactivating the old token.
4. **`GetAccessTokenAsync_SaveFailureAfterSuccess_PropagatesAndFailsClosed`**:
   - Simulate a successful API response, but configure the atomic commit operation to fail or report an uncertain outcome.
   - Verify the old token is never retried, including after re-instantiating `AuthSession`; do not assume a thrown write means a particular persisted state.
5. Test concurrent `AuthSession` instances against one store, not only concurrent calls on one instance.

## 8. Verification boundary
An in-memory fake can verify orchestration order and state transitions. An across-restart durability claim requires a real platform storage adapter and crash/restart tests on Android and iOS. Until then this remains a proposed protocol, not an implemented guarantee.
