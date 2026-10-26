namespace LifeDrop.Mobile;

// "Remember my login" on the phone (plan steps 4.47 and 5.2b, owner request): the company code and the e-mail are kept in the app's preferences, the password in the
// phone's secure storage (Android Keystore, iPhone Keychain). Unticking the box, signing in to another company, or an unreadable store forgets the password.
public static class RememberedLogin
{
    private const string PasswordKey = "LoginPassword";
    private const string ChoiceKey = "RememberLogin";

    // Ticked unless the user once unticked it.
    public static bool Enabled => Preferences.Get(ChoiceKey, true);

    public static async Task<string?> LoadPasswordAsync()
    {
        if (!Enabled) return null;
        try
        {
            return await SecureStorage.Default.GetAsync(PasswordKey);
        }
        catch
        {
            return null;   // the secure storage can be unavailable (new lock screen, restored backup); the user types the password again
        }
    }

    // remember = false forgets the stored password; true stores the one that just worked.
    public static async Task SaveAsync(bool remember, string? password)
    {
        Preferences.Set(ChoiceKey, remember);
        if (remember && !string.IsNullOrEmpty(password))
        {
            try
            {
                await SecureStorage.Default.SetAsync(PasswordKey, password);
                return;
            }
            catch
            {
                // fall through: nothing is kept rather than something in the clear
            }
        }
        Forget();
    }

    public static void Forget()
    {
        try
        {
            SecureStorage.Default.Remove(PasswordKey);
        }
        catch
        {
            // nothing to remove
        }
    }
}
