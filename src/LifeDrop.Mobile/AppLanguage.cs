using LifeDrop.Mobile.Localization;

namespace LifeDrop.Mobile;

// Language and reading direction of the app (plan step 1.21). The choice is kept on the phone; Sorani Kurdish is
// the default. Changing it rebuilds the current screen so every text and the direction follow at once.
public static class AppLanguage
{
    private const string PreferenceKey = "lang";

    public static void Load() => Texts.Language = Preferences.Get(PreferenceKey, Texts.DefaultLanguage);

    public static FlowDirection Direction => Texts.IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

    public static string CurrentName => Texts.Languages.First(l => l.Code == Texts.Language).Name;

    public static NavigationPage Root(Page page) => new(page) { FlowDirection = Direction };

    public static void ShowRoot(Page page) => Application.Current!.Windows[0].Page = Root(page);

    public static async Task ChooseAsync(Page from, Func<Page> rebuild)
    {
        var picked = await from.DisplayActionSheetAsync(Texts.Get("app.language"), Texts.Get("app.cancel"), null,
            Texts.Languages.Select(l => l.Name).ToArray());
        var code = Texts.Languages.FirstOrDefault(l => l.Name == picked).Code;
        if (code is null || code == Texts.Language) return;
        Texts.Language = code;
        Preferences.Set(PreferenceKey, code);
        ShowRoot(rebuild());
    }
}
