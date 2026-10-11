// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Minimal WPF app for the WebAssembly media test: a single MediaElement auto-playing a served video,
// exercising the BrowserMediaBackend (HTML5 <video>) + the compositor's MilDrawVideo path in the browser.
//

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

internal static class Program
{
    internal static int Main(string[] args)
    {
        if (Environment.GetEnvironmentVariable("WPF_USE_WEBGPU_COMPOSITION") == null)
            Environment.SetEnvironmentVariable("WPF_USE_WEBGPU_COMPOSITION", "1");

        var app = new Application();

        var media = new MediaElement
        {
            // Relative -> browser-media.js resolves it against the page base (the served wwwroot).
            Source = new Uri("test.mov", UriKind.Relative),
            LoadedBehavior = MediaState.Play,
            Stretch = Stretch.Uniform,
        };
        media.MediaOpened += (s, e) => Console.WriteLine($"MediaWasm: MediaOpened {media.NaturalVideoWidth}x{media.NaturalVideoHeight}");
        media.MediaFailed += (s, e) => Console.WriteLine($"MediaWasm: MediaFailed {e.ErrorException?.Message}");
        media.MediaEnded += (s, e) => Console.WriteLine("MediaWasm: MediaEnded");

        var root = new Border
        {
            Background = Brushes.DarkSlateGray,
            BorderBrush = Brushes.LimeGreen,
            BorderThickness = new Thickness(3),
            Padding = new Thickness(10),
            Child = media,
        };

        var window = new Window { Title = "Media WASM", Width = 640, Height = 400, Content = root };
        window.Show();
        Console.WriteLine("MediaWasm: window shown");

        app.Run();
        return 0;
    }
}
