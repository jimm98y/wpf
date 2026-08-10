// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Widget;
using MS.Internal.Interop;
using WpfMediaTest;

namespace WpfMediaAndroid;

/// <summary>
/// The whole Android media head: an activity whose content is one FrameLayout that WPF windows are
/// added into, an <see cref="AndroidHost"/> installed in <see cref="AndroidWindow.Host"/> so WPF can
/// reach the Java side, and the shared <see cref="MediaTestUi"/> page inside it. Everything
/// media-specific lives in that shared page; this file is only ever about finding the file to play.
/// </summary>
[Activity(
    Label = "WPF Media",
    MainLauncher = true,
    Theme = "@android:style/Theme.Material.NoActionBar",
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize
                         | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize
                         | ConfigChanges.Density | ConfigChanges.KeyboardHidden)]
public class MainActivity : Activity
{
    protected override void OnCreate(Bundle savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        // Engine diagnostics, same switches the gallery head documents:
        //     adb shell setprop debug.wpf.sinklog 1
        //     adb shell setprop debug.wpf.medialog 1
        if (AndroidSystemProperties.Get("debug.wpf.sinklog") is "1" or "true")
            System.Environment.SetEnvironmentVariable("WPF_WEBGPU_SINK_LOG", System.IO.Path.Combine(FilesDir!.AbsolutePath, "wpf-sink.log"));

        // The backend's own log. It reads an environment variable, which no adb command can set on a
        // zygote-forked process, so the property is translated here.
        if (AndroidSystemProperties.Get("debug.wpf.medialog") is "1" or "true")
            System.Environment.SetEnvironmentVariable("WPF_MEDIA_LOG", "1");

        AndroidFonts.ExtractBundled(this);

        var root = new FrameLayout(this);
        SetContentView(root);

        var host = new AndroidHost(this, root);
        AndroidWindow.Host = host;
        AndroidAccessibility.Host = host;
        AndroidPrint.Host = host;

        System.Environment.SetEnvironmentVariable("WPF_USE_WEBGPU_COMPOSITION", "1");

        string source = ResolveSource();
        if (source == null)
        {
            Console.WriteLine("MEDIAANDROID: no media file. Build with -p:MediaTestVideo=<path>, or " +
                              "push a clip and run: adb shell setprop debug.wpf.mediafile <path>.");
            Console.WriteLine("RESULT fail");
            return;
        }

        // Unattended by default so an `am start` is self-checking; 0 leaves the transport to the
        // on-screen buttons.
        int auto = 14;
        string autoProp = AndroidSystemProperties.Get("debug.wpf.mediaauto");
        if (!string.IsNullOrEmpty(autoProp) && int.TryParse(autoProp, out int seconds)) auto = seconds;

        try
        {
            var app = new System.Windows.Application();
            var window = new System.Windows.Window
            {
                Title = "WPF media (Android)",
                Content = MediaTestUi.Create(source, auto, m => Console.WriteLine(m)),
            };
            window.Show();

            // Returns immediately, as on the other heads: Dispatcher.Run installs the Choreographer
            // pump and hands the loop back to Android.
            app.Run();
            Console.WriteLine("MEDIAANDROID: Application.Run returned (pump detached)");
        }
        catch (Exception e)
        {
            Console.WriteLine($"MEDIAANDROID: FAILED {e}");
            Console.WriteLine("RESULT fail");
        }
    }

    /// <summary>
    /// An explicit path wins (a system property, because an Android app inherits its environment
    /// from zygote and `adb shell VAR=x am start` therefore sets nothing); otherwise the clip packed
    /// into the APK, unpacked to the app's files directory because an asset has no file path.
    /// </summary>
    private string ResolveSource()
    {
        string prop = AndroidSystemProperties.Get("debug.wpf.mediafile");
        if (!string.IsNullOrEmpty(prop) && System.IO.File.Exists(prop))
        {
            Console.WriteLine($"MEDIAANDROID: source (property) {prop}");
            return prop;
        }

        try
        {
            string target = System.IO.Path.Combine(FilesDir!.AbsolutePath, "test.mp4");
            using (var asset = Assets!.Open("media/test.mp4"))
            using (var file = System.IO.File.Create(target))
            {
                asset.CopyTo(file);
            }

            Console.WriteLine($"MEDIAANDROID: source (asset) {target}");
            return target;
        }
        catch (Java.IO.FileNotFoundException)
        {
            return null;   // no clip was packed
        }
        catch (Exception e)
        {
            Console.WriteLine($"MEDIAANDROID: could not unpack the bundled clip: {e.Message}");
            return null;
        }
    }
}
