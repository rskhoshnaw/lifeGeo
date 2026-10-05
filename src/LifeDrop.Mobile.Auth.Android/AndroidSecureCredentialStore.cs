#if ANDROID
using Android.Content;
using Android.Security.Keystore;
using Java.Security;
using Javax.Crypto;
using Javax.Crypto.Spec;
#endif

using LifeDrop.Mobile.Auth.Models;
using System.Text;
using System.Text.Json;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace LifeDrop.Mobile.Auth.Android;

public class AndroidSecureCredentialStore : ISecureCredentialStore
{
    private static readonly SemaphoreSlim _sessionLock = new(1, 1);
    private const string PrefsName = "LifeDrop.Auth.Prefs";
    private const string SessionKey = "encrypted_session";
    private const string KeyAlias = "LifeDrop.Session.Key";

    private readonly Func<string, string?> _getString;
    private readonly Func<string, string, bool> _putString;
    private readonly Func<string, bool> _removeString;

#if ANDROID
    public AndroidSecureCredentialStore(Context context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var prefs = context.GetSharedPreferences(PrefsName, FileCreationMode.Private) 
            ?? throw new InvalidOperationException("Failed to get SharedPreferences");

        _getString = key => prefs.GetString(key, null);
        _putString = (key, val) => prefs.Edit()!.PutString(key, val)!.Commit();
        _removeString = key => prefs.Edit()!.Remove(key)!.Commit();
    }
#endif

    // Constructor for testing / platform-neutral seam
    protected AndroidSecureCredentialStore(Func<string, string?> getString, Func<string, string, bool> putString, Func<string, bool> removeString)
    {
        _getString = getString ?? throw new ArgumentNullException(nameof(getString));
        _putString = putString ?? throw new ArgumentNullException(nameof(putString));
        _removeString = removeString ?? throw new ArgumentNullException(nameof(removeString));
    }

    protected virtual string Encrypt(string plaintext)
    {
#if ANDROID
        var ks = KeyStore.GetInstance("AndroidKeyStore");
        ks!.Load(null);
        
        if (!ks.ContainsAlias(KeyAlias))
        {
            var keyGenerator = KeyGenerator.GetInstance(KeyProperties.KeyAlgorithmAes, "AndroidKeyStore");
            var spec = new KeyGenParameterSpec.Builder(KeyAlias, KeyStorePurpose.Encrypt | KeyStorePurpose.Decrypt)
                .SetBlockModes(KeyProperties.BlockModeGcm)
                .SetEncryptionPaddings(KeyProperties.EncryptionPaddingNone)
                .Build();
            keyGenerator!.Init(spec);
            keyGenerator.GenerateKey();
        }

        var key = ks.GetKey(KeyAlias, null);
        var cipher = Cipher.GetInstance("AES/GCM/NoPadding");
        cipher!.Init(CipherMode.EncryptMode, key);
        var iv = cipher.GetIV();
        var ciphertext = cipher.DoFinal(Encoding.UTF8.GetBytes(plaintext));
        
        return $"{Convert.ToBase64String(iv!)}:{Convert.ToBase64String(ciphertext!)}";
#else
        throw new PlatformNotSupportedException("Android KeyStore encryption is only available on Android.");
#endif
    }

    protected virtual string Decrypt(string encryptedData)
    {
#if ANDROID
        var parts = encryptedData.Split(':');
        if (parts.Length != 2) throw new InvalidOperationException("Invalid encrypted data format.");
        
        var iv = Convert.FromBase64String(parts[0]);
        var ciphertext = Convert.FromBase64String(parts[1]);

        var ks = KeyStore.GetInstance("AndroidKeyStore");
        ks!.Load(null);
        var key = ks.GetKey(KeyAlias, null);

        var cipher = Cipher.GetInstance("AES/GCM/NoPadding");
        cipher!.Init(CipherMode.DecryptMode, key, new GCMParameterSpec(128, iv));
        var plaintext = cipher.DoFinal(ciphertext);
        
        return Encoding.UTF8.GetString(plaintext!);
#else
        throw new PlatformNotSupportedException("Android KeyStore decryption is only available on Android.");
#endif
    }

    private record StoredSessionDto(
        AuthTokens Tokens, string TenantCode, string Email, bool IsDeviceBound, 
        Guid? BoundDeviceId, SessionStatus Status, Guid SessionId, Guid AttemptId);

    private StoredSession? ReadSessionUnlocked()
    {
        var encrypted = _getString(SessionKey);
        if (string.IsNullOrEmpty(encrypted)) return null;

        try
        {
            var json = Decrypt(encrypted);
            var dto = JsonSerializer.Deserialize<StoredSessionDto>(json);
            if (dto == null) return null;
            return new StoredSession(dto.Tokens, dto.TenantCode, dto.Email, dto.IsDeviceBound, dto.BoundDeviceId, dto.Status, dto.SessionId, dto.AttemptId);
        }
        catch
        {
            return null; // Fail safe on corruption
        }
    }

    private bool WriteSessionUnlocked(StoredSession session)
    {
        var dto = new StoredSessionDto(session.Tokens, session.TenantCode, session.Email, session.IsDeviceBound, session.BoundDeviceId, session.Status, session.SessionId, session.AttemptId);
        var json = JsonSerializer.Serialize(dto);
        var encrypted = Encrypt(json);
        return _putString(SessionKey, encrypted);
    }

    public async Task SaveSessionAsync(StoredSession session, CancellationToken cancellationToken = default)
    {
        await _sessionLock.WaitAsync(cancellationToken);
        try
        {
            if (!WriteSessionUnlocked(session))
                throw new InvalidOperationException("Failed to commit session to secure storage.");
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    public async Task<StoredSession?> LoadSessionAsync(CancellationToken cancellationToken = default)
    {
        await _sessionLock.WaitAsync(cancellationToken);
        try
        {
            return ReadSessionUnlocked();
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    public async Task ClearSessionAsync(CancellationToken cancellationToken = default)
    {
        await _sessionLock.WaitAsync(cancellationToken);
        try
        {
            if (!_removeString(SessionKey))
                throw new InvalidOperationException("Failed to clear session from secure storage.");
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    public async Task<bool> ClearSessionIfMatchesAsync(StoredSession expectedSession, CancellationToken cancellationToken = default)
    {
        await _sessionLock.WaitAsync(cancellationToken);
        try
        {
            var current = ReadSessionUnlocked();
            if (current == null || 
                current.SessionId != expectedSession.SessionId || 
                current.AttemptId != expectedSession.AttemptId ||
                current.TenantCode != expectedSession.TenantCode ||
                current.Email != expectedSession.Email ||
                current.IsDeviceBound != expectedSession.IsDeviceBound ||
                current.BoundDeviceId != expectedSession.BoundDeviceId)
            {
                return false;
            }

            return _removeString(SessionKey);
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    public async Task<bool> MarkSessionRefreshingAsync(StoredSession expectedSession, StoredSession pendingSession, CancellationToken cancellationToken = default)
    {
        if (expectedSession.SessionId == Guid.Empty || pendingSession.SessionId == Guid.Empty) return false;

        await _sessionLock.WaitAsync(cancellationToken);
        try
        {
            var current = ReadSessionUnlocked();
            if (current == null || 
                current.SessionId != expectedSession.SessionId || 
                current.AttemptId != expectedSession.AttemptId || 
                current.Status != SessionStatus.Active ||
                current.TenantCode != expectedSession.TenantCode ||
                current.Email != expectedSession.Email ||
                current.IsDeviceBound != expectedSession.IsDeviceBound ||
                current.BoundDeviceId != expectedSession.BoundDeviceId)
            {
                return false;
            }

            if (pendingSession.Status != SessionStatus.RefreshPending ||
                pendingSession.SessionId != current.SessionId ||
                pendingSession.TenantCode != current.TenantCode ||
                pendingSession.Email != current.Email ||
                pendingSession.IsDeviceBound != current.IsDeviceBound ||
                pendingSession.BoundDeviceId != current.BoundDeviceId ||
                pendingSession.Tokens.AccessToken != current.Tokens.AccessToken ||
                pendingSession.Tokens.RefreshToken != current.Tokens.RefreshToken ||
                pendingSession.AttemptId == Guid.Empty ||
                pendingSession.AttemptId == current.AttemptId)
            {
                return false;
            }

            return WriteSessionUnlocked(pendingSession);
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    public async Task<bool> CommitSessionRefreshAsync(StoredSession pendingSession, StoredSession newSession, CancellationToken cancellationToken = default)
    {
        if (pendingSession.SessionId == Guid.Empty || newSession.SessionId == Guid.Empty) return false;

        await _sessionLock.WaitAsync(cancellationToken);
        try
        {
            var current = ReadSessionUnlocked();
            if (current == null || current.SessionId != pendingSession.SessionId || current.AttemptId != pendingSession.AttemptId || current.Status != SessionStatus.RefreshPending)
            {
                return false;
            }

            if (!newSession.IsValid() ||
                newSession.SessionId != current.SessionId ||
                newSession.TenantCode != current.TenantCode ||
                newSession.Email != current.Email ||
                newSession.IsDeviceBound != current.IsDeviceBound ||
                newSession.BoundDeviceId != current.BoundDeviceId ||
                newSession.AttemptId != Guid.Empty)
            {
                return false;
            }

            return WriteSessionUnlocked(newSession);
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    public Task SaveDeviceAsync(RegisteredDevice device, CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(device);
        string encrypted;
        try
        {
            encrypted = Encrypt(json);
        }
        catch (Exception ex)
        {
#if ANDROID
            // Shown by: adb logcat -s LifeDrop (the coordinator turns any failure into a generic message).
            global::Android.Util.Log.Error("LifeDrop", "Device registration encrypt failed: " + ex);
#endif
            throw;
        }
        if (!_putString("DeviceRegistration", encrypted))
            throw new InvalidOperationException("Failed to commit device registration to secure storage.");
        return Task.CompletedTask;
    }

    public Task<RegisteredDevice?> LoadDeviceAsync(CancellationToken cancellationToken = default)
    {
        var encrypted = _getString("DeviceRegistration");
        if (string.IsNullOrEmpty(encrypted)) return Task.FromResult<RegisteredDevice?>(null);
        try
        {
            var json = Decrypt(encrypted);
            return Task.FromResult(JsonSerializer.Deserialize<RegisteredDevice>(json));
        }
        catch
        {
            return Task.FromResult<RegisteredDevice?>(null);
        }
    }

    public Task ClearDeviceAsync(CancellationToken cancellationToken = default)
    {
        if (!_removeString("DeviceRegistration"))
            throw new InvalidOperationException("Failed to clear device registration from secure storage.");
        return Task.CompletedTask;
    }

    public async Task ClearAllAsync(CancellationToken cancellationToken = default)
    {
        await ClearSessionAsync(cancellationToken);
        await ClearDeviceAsync(cancellationToken);
    }
}
