using System.Reflection;
using System.Windows;

namespace WpfGalleryIos;

internal static class GalleryHost
{
    public static void Run(IntPtr rootView)
    {
        Console.WriteLine("GALLERY: start");

        // WPF windows are added as subviews of the head's root view, and the compositor must take
        // the managed WebGPU path rather than the (absent) native milcore.
        MS.Internal.Interop.UIKitWindow.RootView = rootView;
        Environment.SetEnvironmentVariable("WPF_USE_WEBGPU_COMPOSITION", "1");

        PointResourceLookupAt(Assembly.Load(new AssemblyName("WPFGallery")));

        try
        {
            // The gallery's own entry point: builds its generic host, applies the Fluent theme,
            // resolves MainWindow from DI, shows it and calls Application.Run().
            WPFGallery.App.Main();
            Console.WriteLine("GALLERY: App.Main returned (pump detached, UIKit owns the loop)");

            if (Environment.GetEnvironmentVariable("WPF_IOS_ROTATE_TEST") == "1")
                ScheduleRotateTest(rootView);
        }
        catch (Exception e)
        {
            Console.WriteLine($"GALLERY: FAILED {e}");
        }
    }

    /// <summary>
    /// Swap the root view's width and height a few seconds in, which is what a device rotation does
    /// to it. Neither simctl nor the simulator exposes any rotation API (and AppleScript keystrokes
    /// need Accessibility permission), so this is the only way to exercise the resize path
    /// automatically: autoresizing mask -> WpfMetalView.layoutSubviews -> UIKitWindow.Resized ->
    /// synthetic WM_SIZE -> WPF relayout + swap-chain reconfigure. It does NOT cover UIKit's own
    /// rotation animation - a physical rotate is still the only proof of that segment.
    /// </summary>
    private static void ScheduleRotateTest(IntPtr rootView)
    {
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        timer.Tick += (s, e) =>
        {
            timer.Stop();
            CGRect b = SendRect(rootView, SelReg("bounds"));
            var swapped = new CGRect { x = 0, y = 0, width = b.height, height = b.width };
            Console.WriteLine($"ROTATE: {b.width:F0}x{b.height:F0} -> {swapped.width:F0}x{swapped.height:F0}");
            SendVoidRect(rootView, SelReg("setFrame:"), swapped);
        };
        timer.Start();
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct CGRect { public double x, y, width, height; }

    [System.Runtime.InteropServices.DllImport("/usr/lib/libobjc.dylib", EntryPoint = "sel_registerName")]
    private static extern IntPtr SelReg(string name);
    [System.Runtime.InteropServices.DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern CGRect SendRect(IntPtr r, IntPtr s);
    [System.Runtime.InteropServices.DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern void SendVoidRect(IntPtr r, IntPtr s, CGRect f);

    /// <summary>
    /// Make pack:// and relative resource URIs resolve against the gallery instead of this head.
    ///
    /// BAML asks for things like "assets/win11-dashboard.light.png", which WPF looks up in the ENTRY
    /// assembly's .g.resources. The entry assembly is this host, which has none, so every image in
    /// the gallery's XAML fails with "Cannot locate resource". The public seam for that is
    /// Application.ResourceAssembly, but its setter only accepts a value when
    /// Assembly.GetEntryAssembly() is null - it exists for hosted (XBAP-style) apps that have no
    /// entry assembly at all, and we do have one. So set the two statics it would have written.
    /// Both getters latch to GetEntryAssembly() on first read, hence doing this before touching WPF.
    /// </summary>
    private static void PointResourceLookupAt(Assembly gallery)
    {
        const BindingFlags Static = BindingFlags.NonPublic | BindingFlags.Static;

        typeof(Application).GetField("_resourceAssembly", Static)?.SetValue(null, gallery);

        Type baseUri = typeof(System.Windows.Media.Brush).Assembly            // PresentationCore
            .GetType("System.Windows.Navigation.BaseUriHelper");
        baseUri?.GetField("_resourceAssembly", Static)?.SetValue(null, gallery);

        Console.WriteLine($"GALLERY: resource lookup -> {gallery.GetName().Name}");
    }
}
