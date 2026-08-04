using Foundation;

namespace NcfMobileApp.iOS.Services;

internal static class MobileSettings
{
    private const string SiteUrlKey = "NcfMobile.SiteUrl";
    private const string UserNameKey = "NcfMobile.UserName";
    private const string AutoSpeakKey = "NcfMobile.AutoSpeak";

    public static string SiteUrl
    {
        get => NSUserDefaults.StandardUserDefaults.StringForKey(SiteUrlKey) ?? "https://";
        set => NSUserDefaults.StandardUserDefaults.SetString(value, SiteUrlKey);
    }

    public static string UserName
    {
        get => NSUserDefaults.StandardUserDefaults.StringForKey(UserNameKey) ?? string.Empty;
        set => NSUserDefaults.StandardUserDefaults.SetString(value, UserNameKey);
    }

    public static bool AutoSpeak
    {
        get => NSUserDefaults.StandardUserDefaults.ObjectForKey(AutoSpeakKey) == null ||
               NSUserDefaults.StandardUserDefaults.BoolForKey(AutoSpeakKey);
        set => NSUserDefaults.StandardUserDefaults.SetBool(value, AutoSpeakKey);
    }

    public static void Synchronize()
    {
        NSUserDefaults.StandardUserDefaults.Synchronize();
    }
}
