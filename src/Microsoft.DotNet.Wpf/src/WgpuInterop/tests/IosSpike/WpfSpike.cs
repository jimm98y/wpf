using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace IosSpike;

/// <summary>
/// M3e: boot a real WPF <see cref="Window"/> on iOS and let the WebGPU compositor render it.
///
/// This is the first end-to-end test of the whole stack on this platform: HwndWrapper's iOS branch
/// creates a UIKitWindow (a Metal-backed UIView), the compositor turns that view into a wgpu
/// surface, the CADisplayLink pump (M3c) drives layout/render, and text exercises the bundled
/// fonts (M3d).
/// </summary>
public static class WpfSpike
{
    private static Window _window;
    private static TextBlock _status;
    private static int _frames;
    private static int _taps;

    public static void Run(IntPtr rootView)
    {
        Console.WriteLine("WPFSPIKE: start");

        // WPF windows are added as subviews of the head's root view; the compositor needs the
        // managed WebGPU path rather than the (absent) native milcore.
        MS.Internal.Interop.UIKitWindow.RootView = rootView;
        Environment.SetEnvironmentVariable("WPF_USE_WEBGPU_COMPOSITION", "1");

        _status = new TextBlock
        {
            Text = "WPF on iOS",
            FontSize = 34,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        bool stress = Environment.GetEnvironmentVariable("WPF_IOS_STRESS") == "1";

        var panel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(_status);

        // Stress mode: enough real WPF content (layout + text + gradients + transforms) that the
        // frame cost stops being display-capped, so AOT and the interpreter can actually be told
        // apart. A rotating container forces a re-render every frame.
        if (stress)
        {
            var wrap = new WrapPanel { Width = 380, HorizontalAlignment = HorizontalAlignment.Center };
            for (int i = 0; i < 240; i++)
            {
                wrap.Children.Add(new Border
                {
                    Width = 42, Height = 42, Margin = new Thickness(2),
                    CornerRadius = new CornerRadius(6),
                    Background = Environment.GetEnvironmentVariable("WPF_IOS_STRESS_SOLID") == "1"
                        ? new SolidColorBrush(Color.FromRgb((byte)(i * 7 % 255), 90, 200))
                        : new LinearGradientBrush(
                            Color.FromRgb((byte)(i * 7 % 255), 90, 200), Colors.Gold, 45),
                    // WPF_IOS_STRESS_TEXT=0 drops the glyphs, keeping identical geometry/brush work.
                    Child = Environment.GetEnvironmentVariable("WPF_IOS_STRESS_TEXT") == "0" ? null
                        : new TextBlock
                        {
                            Text = i.ToString(),
                            Foreground = Brushes.White,
                            HorizontalAlignment = HorizontalAlignment.Center,
                            VerticalAlignment = VerticalAlignment.Center,
                        },
                });
            }
            panel.Children.Add(wrap);

            // WPF_IOS_STRESS_SPIN=0 keeps the tiles AXIS-ALIGNED while still re-rendering every
            // frame (opacity nudge). Isolates "is the cost caused by the rotation defeating the
            // analytic rounded-rect path?" from "is it just the tile count?".
            if (Environment.GetEnvironmentVariable("WPF_IOS_STRESS_SPIN") != "0")
            {
                var spin = new RotateTransform(0);
                // WPF_IOS_STRESS_NOORIGIN=1 drops RenderTransformOrigin: a TransformGroup reports 3
                // children for the 2 I add, so the origin contributes one - this isolates it.
                if (Environment.GetEnvironmentVariable("WPF_IOS_STRESS_NOORIGIN") != "1")
                    wrap.RenderTransformOrigin = new Point(0.5, 0.5);
                // TIME-based, not frame-based: `Angle += k` per Rendering tick makes the rotation
                // speed a direct read-out of frame time, so any jitter looks like the animation
                // speeding up and slowing down. Driving it from the clock keeps the speed uniform
                // regardless of fps, so residual non-uniformity is a REAL compositor stutter.
                // Zoom 1x -> 8x -> 1x on an 8s cycle WHILE rotating. This is the quality test for the
                // local-space coverage cache: masks are rasterized at a bucketed scale, so zooming
                // far past the cached bucket is exactly where the resolution tradeoff shows.
                var zoom = new ScaleTransform(1, 1);
                var group = new TransformGroup();
                group.Children.Add(zoom);
                group.Children.Add(spin);
                wrap.RenderTransform = group;

                var spinClock = System.Diagnostics.Stopwatch.StartNew();
                CompositionTarget.Rendering += (s2, e2) =>
                {
                    double t = spinClock.Elapsed.TotalSeconds;
                    spin.Angle = t * 24.0 % 360.0;                                  // 24 deg/sec
                    double zmax = double.TryParse(Environment.GetEnvironmentVariable("WPF_IOS_STRESS_ZOOM"), out double zz) && zz > 1 ? zz : 8.0;
                    double k = 1.0 + (zmax - 1.0) * (1.0 - Math.Cos(2 * Math.PI * t / 8.0)) / 2.0;
                    zoom.ScaleX = zoom.ScaleY = k;
                };
            }
            else
            {
                CompositionTarget.Rendering += (s2, e2) =>
                    wrap.Opacity = 0.75 + 0.25 * Math.Abs(Math.Sin(Environment.TickCount / 500.0));
            }
        }
        // M4: a real WPF Button, so a tap has to travel UITouch -> RawMouseActions -> InputManager
        // -> hit test -> Click. Nothing about this control knows it is on a phone.
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

        _window = new Window
        {
            Title = "WPF iOS",
            Background = new SolidColorBrush(Color.FromRgb(0x10, 0x18, 0x28)),
            Content = panel,
        };

        Console.WriteLine("WPFSPIKE: window constructed");
        _window.Show();
        Console.WriteLine($"WPFSPIKE: shown, handle=0x{new System.Windows.Interop.WindowInteropHelper(_window).Handle:X}");

        {
            IntPtr v = new System.Windows.Interop.WindowInteropHelper(_window).Handle;
            var fr = objc_msgSend_rect(v, sel_registerName("frame"));
            IntPtr sup = objc_msgSend_ptr(v, sel_registerName("superview"));
            bool hidden = objc_msgSend_bool0(v, sel_registerName("isHidden"));
            IntPtr layer = objc_msgSend_ptr(v, sel_registerName("layer"));
            IntPtr lcls = layer != IntPtr.Zero ? object_getClass(layer) : IntPtr.Zero;
            string lname = lcls != IntPtr.Zero ? System.Runtime.InteropServices.Marshal.PtrToStringAnsi(class_getName(lcls)) : "(none)";
            IntPtr vcls = object_getClass(v);
            string vname = System.Runtime.InteropServices.Marshal.PtrToStringAnsi(class_getName(vcls));
            Console.WriteLine($"VIEWPROBE2: viewClass={vname} layerClass={lname}");
            Console.WriteLine($"VIEWPROBE: frame=({fr.x:F0},{fr.y:F0},{fr.w:F0}x{fr.h:F0}) superview={(sup != IntPtr.Zero ? "yes" : "NO")} hidden={hidden} rootView=0x{MS.Internal.Interop.UIKitWindow.RootView:X}");
        }

        // M4 partial check: UIKit only routes touches to a view that answers the touch selectors,
        // so confirm the synthesized class really carries them (the delivery + hit-test half needs
        // a physical tap).
        IntPtr view = new System.Windows.Interop.WindowInteropHelper(_window).Handle;
        foreach (string sel in new[] { "touchesBegan:withEvent:", "touchesMoved:withEvent:", "touchesEnded:withEvent:", "touchesCancelled:withEvent:" })
            Console.WriteLine($"WPFSPIKE: view responds to {sel} = {RespondsTo(view, sel)}");

        // Drive a synthetic tap at the Button's real centre once layout has run. This exercises
        // everything downstream of UIKit: device-pixel mapping, hit testing and click synthesis.
        // Not in stress mode: the rotating panel moves the Button after this point is computed, so
        // the injected coordinate would miss - a test artifact, not an input failure.
        if (!stress) _window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            Point centre = button.TransformToAncestor(_window)
                                 .Transform(new Point(button.ActualWidth / 2, button.ActualHeight / 2));
            Point dev = PresentationSource.FromVisual(_window).CompositionTarget.TransformToDevice.Transform(centre);
            Console.WriteLine($"WPFSPIKE: injecting tap at device px ({dev.X:F0},{dev.Y:F0})");

            foreach (int kind in new[] { 0, 1, 2 })
                MS.Internal.Interop.UIKitWindow.InjectTouch(view, kind, (int)dev.X, (int)dev.Y);

            Console.WriteLine(_taps > 0
                ? $"WPFSPIKE: INPUT PASS - synthetic tap raised Click ({_taps})"
                : "WPFSPIKE: INPUT FAIL - tap did not reach the Button");
        }));

        // Prove frames keep composing (and that the pump is driving them), then report.
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
            // In stress mode the once-a-second text change is itself under investigation (it forces
            // re-layout + fresh glyph geometry); WPF_IOS_STRESS_NOTEXTUPD=1 removes it so the tick
            // spikes can be attributed.
            if (_taps == 0 && !(stress && Environment.GetEnvironmentVariable("WPF_IOS_STRESS_NOTEXTUPD") == "1"))
                _status.Text = $"WPF on iOS  {seconds}s";
            if (stress && seconds == 6)
            {
                Console.WriteLine($"WPFSPIKE: STRESS fps={_frames / 6.0:F1} over 6s ({_frames} frames, 240 gradient tiles rotating)");
            }
            if (seconds == 4)
            {
                Console.WriteLine(_frames > 30
                    ? $"WPFSPIKE: PASS - {_frames} frames composed in 4s"
                    : $"WPFSPIKE: FAIL - only {_frames} frames composed");
            }
        };
        timer.Start();

        Dispatcher.Run();
        Console.WriteLine("WPFSPIKE: Dispatcher.Run returned (pump is detached on iOS)");
    }

    private static bool RespondsTo(IntPtr obj, string selector)
        => obj != IntPtr.Zero && objc_msgSend_bool(obj, sel_registerName("respondsToSelector:"), sel_registerName(selector));

    [System.Runtime.InteropServices.DllImport("/usr/lib/libobjc.dylib")]
    private static extern IntPtr sel_registerName(string name);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct R { public double x, y, w, h; }
    [System.Runtime.InteropServices.DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern R objc_msgSend_rect(IntPtr r, IntPtr s);
    [System.Runtime.InteropServices.DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend_ptr(IntPtr r, IntPtr s);
    [System.Runtime.InteropServices.DllImport("/usr/lib/libobjc.dylib")]
    private static extern IntPtr object_getClass(IntPtr o);
    [System.Runtime.InteropServices.DllImport("/usr/lib/libobjc.dylib")]
    private static extern IntPtr class_getName(IntPtr c);
    [System.Runtime.InteropServices.DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.I1)]
    private static extern bool objc_msgSend_bool0(IntPtr r, IntPtr s);

    [System.Runtime.InteropServices.DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.I1)]
    private static extern bool objc_msgSend_bool(IntPtr receiver, IntPtr sel, IntPtr arg);
}
