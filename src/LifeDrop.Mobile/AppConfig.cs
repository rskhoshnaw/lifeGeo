using System.Reflection;

namespace LifeDrop.Mobile;

// Build-time settings. The bootstrap server only answers "which API belongs to this company code" (plan step 4.2); set it when building:
// dotnet publish -p:LifeDropBootstrapUrl=https://app.example.com
public static class AppConfig
{
    public const string Developer = "Pixelo Design";
    public const string DeveloperUrl = "https://pixelo-design.com/";
    public const string DefaultSupportPhone = "+9647510686828";

    private static string Metadata(string key) => Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(a => a.Key == key)?.Value ?? "";

    // Every company has its own server: code "acme" is https://acme.<BaseDomain>. BootstrapUrl is only the fallback for a single server.
    public static string BaseDomain { get; } = Metadata("BaseDomain");
    public static string BootstrapUrl { get; } = Metadata("BootstrapUrl");

    public static string SupportPhone => Preferences.Get("SupportPhone", DefaultSupportPhone);

    // Set by the Android activity when the app is opened via a lifedrop:// deep link; consumed once by MainPage.
    private static volatile string? _pendingJoinCode;
    public static string? ConsumePendingJoinCode() { var c = _pendingJoinCode; _pendingJoinCode = null; return c; }
    public static void SetPendingJoinCode(string code) => _pendingJoinCode = code;
}
