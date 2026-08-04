using Foundation;
using NcfMobileApp.iOS.Services;
using NcfMobileApp.iOS.ViewControllers;
using ObjCRuntime;
using UIKit;

namespace NcfMobileApp.iOS;

[Register("AppDelegate")]
public sealed class AppDelegate : UIApplicationDelegate
{
    private readonly MobileAppServices _services = new();

    public override UIWindow? Window { get; set; }

    public override bool FinishedLaunching(UIApplication application, NSDictionary? launchOptions)
    {
        Window = new UIWindow(UIScreen.MainScreen.Bounds);
        var navigationController = new UINavigationController(
            new ConnectionViewController(_services));
        navigationController.NavigationBar.PrefersLargeTitles = true;
        Window.RootViewController = navigationController;
        Window.MakeKeyAndVisible();
        return true;
    }

    public override void WillTerminate(UIApplication application)
    {
        _services.Dispose();
        base.WillTerminate(application);
    }
}
