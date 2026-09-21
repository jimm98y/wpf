namespace IosSpike;

[Register ("SceneDelegate")]
public class SceneDelegate : UIResponder, IUIWindowSceneDelegate {

	[Export ("window")]
	public UIWindow? Window { get; set; }

	[Export ("scene:willConnectToSession:options:")]
	public void WillConnect (UIScene scene, UISceneSession session, UISceneConnectionOptions connectionOptions)
	{
		// Use this method to optionally configure and attach the UIWindow 'Window' to the provided UIWindowScene 'scene'.
		// Since we are not using a storyboard, the 'Window' property needs to be initialized and attached to the scene.
		// This delegate does not imply the connecting scene or session are new (see UIApplicationDelegate 'GetConfiguration' instead).
		if (scene is UIWindowScene windowScene) {
			Window ??= new UIWindow (windowScene);

			// Report the wgpu static-link probe plus the screen metrics we will need for the
			// CAMetalLayer / WPF DPI later (these are wrong when iOS runs the app letterboxed).
			var b = UIScreen.MainScreen.Bounds;
			string probe = WgpuNative.Probe () + "\n\n" + WgpuNative.ProbeSharedBinding ();
			Console.WriteLine ($"IosSpike: {probe.Replace ('\n', ' ')}");
			Console.WriteLine ($"IosSpike: screen {b.Width}x{b.Height} @{UIScreen.MainScreen.NativeScale}x");

			var vc = new UIViewController ();
			string mode = Environment.GetEnvironmentVariable ("WPF_IOS_SPIKE") ?? "metal";

			// M3e: a real WPF Window owns the whole screen in "wpf" mode, so the older Metal demo
			// view and its status label are left out - otherwise they overlay the WPF output and
			// make screenshots ambiguous.
			if (mode == "wpf") {
				Window.RootViewController = vc;
				Window.MakeKeyAndVisible ();
				WpfSpike.Run (vc.View!.Handle);
				return;
			}

			// The Metal-backed view fills the window; a label on top reports what happened.
			var metal = new MetalView (Window!.Frame) { AutoresizingMask = UIViewAutoresizing.All };
			vc.View!.AddSubview (metal);
			metal.Start ();

			if (mode == "pump")
				PumpSpike.Run ();

			// White on a semi-dark plate: the label sits ON the Metal surface, so default black text
			// would be invisible whenever the scene renders dark.
			var label = new UILabel (new CoreGraphics.CGRect (0, b.Height * 0.55, b.Width, b.Height * 0.4)) {
				BackgroundColor = UIColor.FromRGBA (0f, 0f, 0f, 0.55f),
				TextColor = UIColor.White,
				TextAlignment = UITextAlignment.Center,
				Lines = 0,
				Font = UIFont.SystemFontOfSize (13),
				Text = $"{probe}\n\nscreen {b.Width}x{b.Height} @{UIScreen.MainScreen.NativeScale}x\n\n{metal.Status}",
				AutoresizingMask = UIViewAutoresizing.All,
			};
			vc.View.AddSubview (label);
			Console.WriteLine ($"IosSpike: metal {metal.Status.Replace ('\n', ' ')}");

			Window.RootViewController = vc;
			Window.MakeKeyAndVisible ();
		}
	}

	[Export ("sceneDidDisconnect:")]
	public void DidDisconnect (UIScene scene)
	{
		// Called as the scene is being released by the system.
		// This occurs shortly after the scene enters the background, or when its session is discarded.
		// Release any resources associated with this scene that can be re-created the next time the scene connects.
		// The scene may re-connect later, as its session was not neccessarily discarded (see UIApplicationDelegate `DidDiscardSceneSessions` instead).
	}

	[Export ("sceneDidBecomeActive:")]
	public void DidBecomeActive (UIScene scene)
	{
		// Called when the scene has moved from an inactive state to an active state.
		// Use this method to restart any tasks that were paused (or not yet started) when the scene was inactive.
	}

	[Export ("sceneWillResignActive:")]
	public void WillResignActive (UIScene scene)
	{
		// Called when the scene will move from an active state to an inactive state.
		// This may occur due to temporary interruptions (ex. an incoming phone call).
	}

	[Export ("sceneWillEnterForeground:")]
	public void WillEnterForeground (UIScene scene)
	{
		// Called as the scene transitions from the background to the foreground.
		// Use this method to undo the changes made on entering the background.
	}

	[Export ("sceneDidEnterBackground:")]
	public void DidEnterBackground (UIScene scene)
	{
		// Called as the scene transitions from the foreground to the background.
		// Use this method to save data, release shared resources, and store enough scene-specific state information
		// to restore the scene back to its current state.
	}
}
