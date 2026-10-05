using System.Runtime.CompilerServices;
using LifeDrop.Mobile.Localization;

namespace LifeDrop.Database.Tests;

// Tests that drive the mobile client check its English messages; the app's default is Sorani Kurdish (step 1.21).
internal static class EnglishTexts
{
    [ModuleInitializer]
    internal static void Init() => Texts.Language = "en";
}
