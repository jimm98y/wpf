// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

// The Android SDK's implicit usings pull in Android.Widget, which has its own Button/TextBlock-alikes.
// Everything below means the WPF one.
using Button = System.Windows.Controls.Button;

namespace AndroidSpike;

/// <summary>
/// Boot a real WPF <see cref="Window"/> on Android and let the WebGPU compositor render it — the
/// end-to-end test of the whole stack on this platform, and the Android twin of IosSpike.WpfSpike:
/// HwndWrapper's Android branch creates an AndroidWindow, the head gives it a SurfaceView, the
/// compositor turns that view's ANativeWindow into a wgpu surface, the Choreographer pump drives
/// layout/render, and the text exercises /system/fonts through SystemFontCatalog.
///
/// Everything it reports goes to logcat (Console.WriteLine lands in the "DOTNET" tag), so
///     adb logcat -s DOTNET:V
/// is the whole test harness.
/// </summary>
public static class WpfSpike
{
    private static Window? _window;
    private static TextBlock? _status;
    private static int _frames;
    private static int _taps;

    public static void Run()
    {
        Console.WriteLine("WPFSPIKE: start");

        try
        {
            RunCore();
        }
        catch (Exception e)
        {
            // An exception escaping into the Java frame that called us kills the process without a
            // managed stack, so report it here.
            Console.WriteLine($"WPFSPIKE: FAILED {e}");
        }
    }

    private static void RunCore()
    {
        ReportCompositionBackend();

        _status = new TextBlock
        {
            Text = "WPF on Android",
            FontSize = 34,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        var panel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(_status);

        // A real WPF Button, so a tap has to travel MotionEvent -> AndroidWindow.NotifyTouch ->
        // RawMouseActions -> InputManager -> hit test -> Click. Nothing about this control knows it
        // is on a phone.
        var button = new Button
        {
            Content = "Tap me",
            Width = 220,
            Height = 90,
            FontSize = 26,
            Margin = new Thickness(0, 24, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        button.Click += (s, e) =>
        {
            _taps++;
            _status.Text = $"tapped {_taps}x";
            Console.WriteLine($"WPFSPIKE: BUTTON CLICK #{_taps}");
        };
        panel.Children.Add(button);

        // Geometry + gradients + transforms, so a green screen is not mistaken for a working
        // renderer: this only looks right if paths, brushes and the transform stack all work.
        var tiles = new WrapPanel { Width = 320, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 24, 0, 0) };
        for (int i = 0; i < 24; i++)
        {
            tiles.Children.Add(new Border
            {
                Width = 42,
                Height = 42,
                Margin = new Thickness(4),
                CornerRadius = new CornerRadius(8),
                Background = new LinearGradientBrush(Color.FromRgb((byte)(i * 10 % 255), 90, 200), Colors.Gold, 45),
            });
        }
        panel.Children.Add(tiles);

        _window = new Window
        {
            Title = "WPF Android",
            Background = new SolidColorBrush(Color.FromRgb(0x10, 0x18, 0x28)),
            Content = panel,
        };

        Console.WriteLine("WPFSPIKE: window constructed");
        _window.Show();

        IntPtr handle = new System.Windows.Interop.WindowInteropHelper(_window).Handle;
        Console.WriteLine($"WPFSPIKE: shown, handle=0x{handle:X}");

        // Drive a synthetic tap at the Button's real centre once layout has run. This exercises
        // everything downstream of the MotionEvent: device-pixel mapping, hit testing and click
        // synthesis. It does NOT cover Android's own delivery into onTouchEvent — a physical tap
        // (or `adb shell input tap`) is still the only proof of that segment.
        _window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            Point centre = button.TransformToAncestor(_window)
                                 .Transform(new Point(button.ActualWidth / 2, button.ActualHeight / 2));
            Point dev = PresentationSource.FromVisual(_window).CompositionTarget.TransformToDevice.Transform(centre);
            Console.WriteLine($"WPFSPIKE: injecting tap at device px ({dev.X:F0},{dev.Y:F0})");

            foreach (int kind in new[] { 0, 1, 2 })
                MS.Internal.Interop.AndroidWindow.NotifyTouch(handle, kind, (int)dev.X, (int)dev.Y);

            Console.WriteLine(_taps > 0
                ? $"WPFSPIKE: INPUT PASS - synthetic tap raised Click ({_taps})"
                : "WPFSPIKE: INPUT FAIL - tap did not reach the Button");
        }));

        CompositionTarget.Rendering += (s, e) =>
        {
            if (++_frames is 1 or 60 or 180)
                Console.WriteLine($"WPFSPIKE: composed frame {_frames}");
        };

        var timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromSeconds(1) };
        int seconds = 0;
        timer.Tick += (s, e) =>
        {
            seconds++;
            if (_taps == 0) _status.Text = $"WPF on Android  {seconds}s";
            if (seconds == 4)
            {
                Console.WriteLine(_frames > 30
                    ? $"WPFSPIKE: PASS - {_frames} frames composed in 4s"
                    : $"WPFSPIKE: FAIL - only {_frames} frames composed");
            }
        };
        timer.Start();

        Dispatcher.Run();
        Console.WriteLine("WPFSPIKE: Dispatcher.Run returned (pump is detached on Android)");
    }

    /// <summary>
    /// Say out loud which compositor is driving. The WebGPU engine is discovered by NAME
    /// (DUCE.ManagedComposition.EnsureAutoRegistered) rather than referenced, and if that discovery
    /// fails WPF silently falls back to native milcore — which does not exist here, so the failure
    /// surfaces much later as an unrelated-looking crash somewhere inside the render pass.
    /// </summary>
    private static void ReportCompositionBackend()
    {
        try
        {
            var asm = System.Reflection.Assembly.Load(new System.Reflection.AssemblyName("Microsoft.Wpf.Interop.WebGpu"));
            Console.WriteLine($"WPFSPIKE: engine assembly loaded: {asm.FullName}");
        }
        catch (Exception e)
        {
            Console.WriteLine($"WPFSPIKE: engine assembly FAILED to load: {e.GetType().Name}: {e.Message}");
        }

        try
        {
            Type? duce = typeof(System.Windows.Media.Brush).Assembly.GetType("System.Windows.Media.Composition.DUCE");
            Type? managed = duce?.GetNestedType("ManagedComposition", System.Reflection.BindingFlags.NonPublic);
            managed?.GetMethod("EnsureAutoRegistered", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)?.Invoke(null, null);
            object? enabled = managed?.GetProperty("IsEnabled", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)?.GetValue(null);
            Console.WriteLine($"WPFSPIKE: managed (WebGPU) composition enabled = {enabled}");
        }
        catch (Exception e)
        {
            Console.WriteLine($"WPFSPIKE: could not probe the composition backend: {e}");
        }
    }
}
