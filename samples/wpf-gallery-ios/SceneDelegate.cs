namespace WpfGalleryIos;

/// <summary>
/// The UIKit half: make a window, hand its root UIView to the WPF fork, and start the gallery.
/// Nothing here knows anything about the gallery beyond its entry point.
/// </summary>
[Register("SceneDelegate")]
public class SceneDelegate : UIResponder, IUIWindowSceneDelegate
{
    [Export("window")]
    public UIWindow? Window { get; set; }

    [Export("scene:willConnectToSession:options:")]
    public void WillConnect(UIScene scene, UISceneSession session, UISceneConnectionOptions connectionOptions)
    {
        if (scene is not UIWindowScene windowScene) return;

        Window ??= new UIWindow(windowScene);
        var vc = new UIViewController();
        Window.RootViewController = vc;
        Window.MakeKeyAndVisible();

        GalleryHost.Run(vc.View!.Handle);
        // Returns immediately: Application.Run -> Dispatcher.Run installs the CADisplayLink pump and
        // hands the run loop back to UIKit. Blocking here would leave the scene half-connected and
        // its window never composited (the app renders correctly and the screen stays blank).
    }
}
