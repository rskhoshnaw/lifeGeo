using LifeDrop.Mobile.Auth.Android;

namespace LifeDrop.Mobile;

// Keychain-backed session store for iOS (plan step 3.8). The session logic is shared with Android; only the storage differs: MAUI SecureStorage is the iOS Keychain,
// so the value needs no extra encryption of ours.
public sealed class IosSecureCredentialStore : AndroidSecureCredentialStore
{
    public IosSecureCredentialStore() : base(Get, Put, Remove) { }

    private static string? Get(string key)
    {
        try { return SecureStorage.Default.GetAsync(key).GetAwaiter().GetResult(); }
        catch { return null; }
    }

    private static bool Put(string key, string value)
    {
        try { SecureStorage.Default.SetAsync(key, value).GetAwaiter().GetResult(); return true; }
        catch { return false; }
    }

    private static bool Remove(string key)
    {
        try { SecureStorage.Default.Remove(key); return true; }
        catch { return false; }
    }

    protected override string Encrypt(string plaintext) => plaintext;

    protected override string Decrypt(string encryptedData) => encryptedData;
}
