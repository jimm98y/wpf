// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Widget;
using MS.Internal.Interop;

namespace WpfGalleryAndroid;

/// <summary>
/// The whole Android head: an activity whose content is one FrameLayout that WPF windows are added
/// into, and an <see cref="AndroidHost"/> installed in <see cref="AndroidWindow.Host"/> so WPF can
/// reach the Java side. The gallery itself is a stock net10.0 XAML app and knows none of this --
/// see <see cref="GalleryHost"/>.
/// </summary>
[Activity(
    Label = "WPF Gallery",
    MainLauncher = true,
    // No ActionBar: it is Android chrome the WPF window knows nothing about, so it just eats the top
    // of the screen and leaves the app looking like it has a stray dark title bar.
    Theme = "@android:style/Theme.Material.NoActionBar",
    // Handle these ourselves instead of letting Android destroy and recreate the activity. A WPF
    // Application cannot be torn down and rebuilt per rotation, and it does not need to be: the
    // view's surface callback already drives the resize all the way into the compositor.
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize
                         | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize
                         | ConfigChanges.Density | ConfigChanges.KeyboardHidden)]
public class MainActivity : Activity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        // The engine's diagnostics are OFF by default -- the sink log costs an open/write/close per
        // line, several lines per frame. Turn them on for a run with:
        //     adb shell setprop debug.wpf.sinklog 1
        // and read the result with (eng/run-android.sh --log does both):
        //     adb shell run-as net.dot.wpf.gallery cat files/wpf-sink.log
        if (AndroidSystemProperties.Get("debug.wpf.sinklog") is "1" or "true")
            System.Environment.SetEnvironmentVariable("WPF_WEBGPU_SINK_LOG", System.IO.Path.Combine(FilesDir!.AbsolutePath, "wpf-sink.log"));

        string wgpuLog = AndroidSystemProperties.Get("debug.wpf.wgpulog");
        if (!string.IsNullOrEmpty(wgpuLog))
            System.Environment.SetEnvironmentVariable("WPF_WEBGPU_WGPU_LOG", wgpuLog);

        // WPF resolves its glyph-icon families from these; see AndroidFonts.
        AndroidFonts.ExtractBundled(this);

        var root = new FrameLayout(this);
        SetContentView(root);

        AndroidWindow.Host = new AndroidHost(this, root);

        // Start WPF after the first layout pass, so the window it creates is given the activity's
        // real content size rather than a zero one.
        root.Post(GalleryHost.Run);
    }
}
