using LifeDrop.Mobile.Auth.Models;

namespace LifeDrop.Mobile.Auth;

public interface ISecureCredentialStore
{
    Task SaveSessionAsync(StoredSession session, CancellationToken cancellationToken = default);
    Task<StoredSession?> LoadSessionAsync(CancellationToken cancellationToken = default);
    Task ClearSessionAsync(CancellationToken cancellationToken = default);
    Task<bool> ClearSessionIfMatchesAsync(StoredSession expectedSession, CancellationToken cancellationToken = default);

    Task<bool> MarkSessionRefreshingAsync(StoredSession expectedSession, StoredSession pendingSession, CancellationToken cancellationToken = default);
    Task<bool> CommitSessionRefreshAsync(StoredSession pendingSession, StoredSession newSession, CancellationToken cancellationToken = default);

    Task SaveDeviceAsync(RegisteredDevice device, CancellationToken cancellationToken = default);
    Task<RegisteredDevice?> LoadDeviceAsync(CancellationToken cancellationToken = default);
    Task ClearDeviceAsync(CancellationToken cancellationToken = default);

    Task ClearAllAsync(CancellationToken cancellationToken = default);
}
