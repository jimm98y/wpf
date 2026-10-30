namespace WpfMediaIos;

/// <summary>
/// The UIKit half: make a window, hand its root UIView to the WPF fork, and start the media page.
/// Identical in shape to the gallery head's scene delegate; only the payload differs.
/// </summary>
[Register("SceneDelegate")]
public class SceneDelegate : UIResponder, IUIWindowSceneDelegate
{
    [Export("window")]
    public UIWindow Window { get; set; }

    [Export("scene:willConnectToSession:options:")]
    public void WillConnect(UIScene scene, UISceneSession session, UISceneConnectionOptions connectionOptions)
    {
        if (scene is not UIWindowScene windowScene) return;

        Window ??= new UIWindow(windowScene);
        var vc = new UIViewController();
        Window.RootViewController = vc;
        Window.MakeKeyAndVisible();

        // After the scene has connected: Application.Run waits in the main run loop until the app
        // ends, as on every head, and a scene still connecting when it started never composited.
        IntPtr root = vc.View!.Handle;
        NSRunLoop.Main.BeginInvokeOnMainThread(() => MediaHost.Run(root));
    }
}
