// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using System.Windows;

// The Android SDK's implicit usings pull in Android.App, which has its own Application. Everything
// below means WPF's.
using Application = System.Windows.Application;

namespace WpfGalleryAndroid;

/// <summary>
/// Boots the WPF Gallery (dotnet/wpf-samples) inside this activity. The Android twin of
/// samples/wpf-gallery-ios/GalleryHost, and it does the same three things for the same reasons:
/// point resource lookup at the gallery assembly, run its entry point, and then adjust the two
/// desktop assumptions that do not hold on a phone.
/// </summary>
internal static class GalleryHost
{
    public static void Run()
    {
        Console.WriteLine("GALLERY: start");

        try
        {
            PointResourceLookupAt(Assembly.Load(new AssemblyName("WPFGallery")));

            // Once the gallery is up: App.Main does not return until it ends (its dispatcher blocks on
            // this thread, as on every head), so what this head adjusts is queued for when it first idles.
            System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(AdjustForTheScreen));

            // The gallery's own entry point: builds its generic host, applies the Fluent theme,
            // resolves MainWindow from DI, shows it and calls Application.Run().
            WPFGallery.App.Main();
            Console.WriteLine("GALLERY: App.Main returned");
        }
        catch (Exception e)
        {
            // Reported here, where the managed stack is still at hand.
            Console.WriteLine($"GALLERY: FAILED {e}");
        }
    }

    private static void AdjustForTheScreen()
    {
        // The gallery is a DESKTOP app: MainWindow.xaml sets MinWidth=780 / MinHeight=470, which no
        // phone screen satisfies. An Android window IS the activity's content area (AndroidWindow.Create
        // snaps it), so the minimums cannot apply here.
        Window? main = Application.Current?.MainWindow;
        if (main == null) return;
        main.MinWidth = 0;
        main.MinHeight = 0;
        Console.WriteLine($"GALLERY: relaxed window minimums, window={main.ActualWidth:F0}x{main.ActualHeight:F0}");
        HideCaptionButtons(main);
    }

    /// <summary>
    /// Drop the gallery's minimize / maximize / close buttons. They are the SAMPLE's own title-bar
    /// buttons (MainWindow.xaml), not WPF chrome, so nothing in the fork can know to suppress them --
    /// but there is no window to minimize, restore or close on Android, where the app IS the screen
    /// and the system handles app lifetime. Hidden here rather than in the sample so WPF-Samples
    /// stays unmodified, the same way this head redirects resource lookup and relaxes the minimums.
    /// </summary>
    private static void HideCaptionButtons(Window main)
    {
        foreach (string name in new[] { "MinimizeButton", "MaximizeButton", "CloseButton" })
        {
            if (main.FindName(name) is UIElement button)
                button.Visibility = Visibility.Collapsed;
            else
                Console.WriteLine($"GALLERY: caption button '{name}' not found");
        }
    }

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

        Type? baseUri = typeof(System.Windows.Media.Brush).Assembly            // PresentationCore
            .GetType("System.Windows.Navigation.BaseUriHelper");
        baseUri?.GetField("_resourceAssembly", Static)?.SetValue(null, gallery);

        Console.WriteLine($"GALLERY: resource lookup -> {gallery.GetName().Name}");
    }
}
