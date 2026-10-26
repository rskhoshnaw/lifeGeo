using LifeDrop.Mobile.Visits;

namespace LifeDrop.Mobile;

// The permissions of the signed-in user, kept on the phone so the home buttons are right at once (plan step 5.3); refreshed from the server every time the home page opens.
public static class MyPermissions
{
    private const string Key = "MyPermissions";

    public static bool Has(string permission) => ButtonAccess.Allows(Preferences.Get(Key, ""), permission);

    public static void Save(IEnumerable<string> permissions) => Preferences.Set(Key, ButtonAccess.Store(permissions));

    public static void Forget() => Preferences.Remove(Key);
}
