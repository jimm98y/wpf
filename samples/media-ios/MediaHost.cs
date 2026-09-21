// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Windows;
using WpfMediaTest;

namespace WpfMediaIos;

/// <summary>
/// The WPF half of the iOS media head: hand UIKit's root view to the fork, put the shared
/// <see cref="MediaTestUi"/> page in a window, and start the dispatcher. Everything media-specific
/// lives in that shared page; this file is only ever about finding the file to play.
/// </summary>
internal static class MediaHost
{
    public static void Run(IntPtr rootView)
    {
        Console.WriteLine("MEDIAIOS: start");

        MS.Internal.Interop.UIKitWindow.RootView = rootView;
        Environment.SetEnvironmentVariable("WPF_USE_WEBGPU_COMPOSITION", "1");

        string source = ResolveSource();
        if (source == null)
        {
            Console.WriteLine("MEDIAIOS: no media file. Bundle one with -p:MediaTestVideo=<path>, " +
                              "or launch with SIMCTL_CHILD_WPF_MEDIA_FILE=<path>.");
            Console.WriteLine("RESULT fail");
            return;
        }

        // Unattended by default so a simctl launch is self-checking; 0 leaves the transport to the
        // on-screen buttons.
        int auto = 14;
        if (int.TryParse(Environment.GetEnvironmentVariable("WPF_MEDIA_AUTO"), out int seconds)) auto = seconds;

        try
        {
            var app = new Application();
            var window = new Window
            {
                Title = "WPF media (iOS)",
                Content = MediaTestUi.Create(source, auto, m => Console.WriteLine(m)),
            };
            window.Show();

            // Returns immediately: Application.Run -> Dispatcher.Run installs the CADisplayLink pump
            // and hands the run loop back to UIKit (see the gallery head's SceneDelegate).
            app.Run();
            Console.WriteLine("MEDIAIOS: Application.Run returned (pump detached, UIKit owns the loop)");
        }
        catch (Exception e)
        {
            Console.WriteLine($"MEDIAIOS: FAILED {e}");
            Console.WriteLine("RESULT fail");
        }
    }

    /// <summary>
    /// An explicit path wins (that is the only channel a simulator launch has -- `simctl launch`
    /// forwards SIMCTL_CHILD_&lt;VAR&gt; into the app's environment); otherwise the clip bundled by the
    /// build, which is what a real device has.
    /// </summary>
    private static string ResolveSource()
    {
        string env = Environment.GetEnvironmentVariable("WPF_MEDIA_FILE");
        if (!string.IsNullOrEmpty(env) && File.Exists(env))
        {
            Console.WriteLine($"MEDIAIOS: source (env) {env}");
            return env;
        }

        string bundled = Path.Combine(AppContext.BaseDirectory, "media", "test.mp4");
        if (File.Exists(bundled))
        {
            Console.WriteLine($"MEDIAIOS: source (bundle) {bundled}");
            return bundled;
        }

        return null;
    }
}
