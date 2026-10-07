using System.Reflection;

namespace LifeDrop.Mobile;

// Build-time settings. The bootstrap server only answers "which API belongs to this company code" (plan step 4.2); set it when building:
// dotnet publish -p:LifeDropBootstrapUrl=https://app.example.com
public static class AppConfig
{
    public const string Developer = "Pixelo Design";
    public const string DeveloperUrl = "https://pixelo-design.com/";
    public const string DefaultSupportPhone = "+9647510686828";

    public static string BootstrapUrl { get; } = Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(a => a.Key == "BootstrapUrl")?.Value ?? "";

    public static string SupportPhone => Preferences.Get("SupportPhone", DefaultSupportPhone);
}
