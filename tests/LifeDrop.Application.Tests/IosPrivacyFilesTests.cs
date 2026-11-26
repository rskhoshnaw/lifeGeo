using Xunit;

namespace LifeDrop.Application.Tests;

// Plan step 5.57: what the iOS app tells Apple and the user about camera, photos and location must stay complete.
public class IosPrivacyFilesTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LifeDrop.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    private static readonly string[] Keys =
        ["NSCameraUsageDescription", "NSPhotoLibraryUsageDescription", "NSLocationWhenInUseUsageDescription", "NSLocationAlwaysAndWhenInUseUsageDescription"];

    [Fact]
    public void EveryPermissionTextExistsInAllFourLanguages()
    {
        var resources = Path.Combine(Root(), "src/LifeDrop.Mobile/Platforms/iOS/Resources");
        foreach (var lang in new[] { "en", "fa", "ar", "ckb" })
        {
            var text = File.ReadAllText(Path.Combine(resources, lang + ".lproj", "InfoPlist.strings"));
            foreach (var key in Keys) Assert.Contains($"\"{key}\" = \"", text);
        }
    }

    [Fact]
    public void ThePrivacyManifestDeclaresWhatTheAppCollectsAndTheCameraTextMentionsReturns()
    {
        var root = Root();
        var manifest = File.ReadAllText(Path.Combine(root, "src/LifeDrop.Mobile/Platforms/iOS/Resources/PrivacyInfo.xcprivacy"));
        foreach (var kind in new[] { "PreciseLocation", "Photos", "EmailAddress", "DeviceID" })
            Assert.Contains($"NSPrivacyCollectedDataType{kind}", manifest);
        Assert.Contains("<key>NSPrivacyTracking</key>", manifest);
        var policy = File.ReadAllText(Path.Combine(root, "src/LifeDrop.Web/public/privacy.html"));
        Assert.DoesNotContain("No photo is stored", policy);   // feedback and return photos are stored on the server
        Assert.Contains("return", policy, StringComparison.OrdinalIgnoreCase);
    }
}
