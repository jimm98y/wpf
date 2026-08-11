// ElementHost — a Windows Forms control that hosts a WPF element tree, for the cross-platform
// (WebGPU) stack. It is the mirror of the gallery's WindowsFormsHost: there a WinForms control tree
// is embedded in a WPF app, here a WPF element tree is embedded in a WinForms app.
//
// How it works, and why it is not a bitmap:
//
//   * The WPF side is ORDINARY WPF. The element tree is the RootVisual of a real HwndSource whose
//     window is a WS_CHILD of the WinForms host's native window, sized and positioned to this
//     control. So WPF does its own measure/arrange, hit-testing, input, focus, capture, popups,
//     animation and DPI exactly as it does in a standalone app -- none of that is re-implemented
//     here, and mouse/keyboard input needs no forwarding at all: the OS delivers it to the child
//     window, which IS a WPF window.
//
//   * The one thing that changes is presentation. HostedWpfContent.Claim tells WpfCompositionSink
//     not to give this window a swap chain of its own; instead the sink publishes the window's
//     decoded root SceneVisual, and Collect() hands that scene to the WinForms host, which appends
//     it to the driver's own control scenes. The WinForms controls and the WPF tree are therefore
//     composited in ONE WebGPU render pass onto ONE surface -- no intermediate bitmap, no readback,
//     no second swap chain, and no z-order fight between two native windows.
//
// Coordinates: the WinForms driver works in 96dpi points, which are also WPF's DIPs, so the two
// agree on sizes. WPF's scene arrives in DEVICE pixels (its window inherits the host's per-monitor
// DPI), and the presenter scales the whole frame from points to device pixels, so Collect wraps the
// WPF scene in a 1/scale transform to land on device pixels exactly once.

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using Microsoft.Wpf.Interop.WebGpu.Composition.Platform;
using SD = System.Drawing;
using SWF = System.Windows.Forms;
using WPF = System.Windows;

internal sealed class ElementHost : SWF.Control
{
    private const int WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_CLIPSIBLINGS = 0x04000000;
    private const int SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;
    private const int WM_LBUTTONDOWN = 0x0201;

    private readonly WPF.UIElement _child;
    private HwndSource _source;
    private Dispatcher _dispatcher;
    private float _scale = 1f;
    private int _placedX = int.MinValue, _placedY, _placedW, _placedH;
    private bool _failed;
    private bool _loggedPlacement;

    // WF_TRACE_INPUT=1 logs the mouse/focus messages the hosted WPF window sees — the first thing to
    // check when the WPF tree draws but does not react.
    private static readonly bool s_traceInput = Environment.GetEnvironmentVariable("WF_TRACE_INPUT") == "1";

    internal ElementHost(WPF.UIElement child)
    {
        _child = child ?? throw new ArgumentNullException(nameof(child));
        // The WPF tree paints this area; a background here only shows for the frame or two before
        // WPF's first render, so match what the WPF root paints to avoid a flash of grey.
        BackColor = SD.Color.White;
    }

    /// <summary>The hosted WPF element (its properties can be set from WinForms event handlers —
    /// that is the whole point of hosting it).</summary>
    internal WPF.UIElement Child => _child;

    /// <summary>Whether the WPF window exists and has rendered at least once.</summary>
    internal bool IsLive => _source != null && SceneVersion() > 0;

    // ---- host integration -----------------------------------------------------------------

    /// <summary>Register with the WinForms host so this control contributes its WPF scene to every
    /// frame. Call once, before the host's window is created.</summary>
    internal void Register()
    {
        EmbeddedScenes.Collect = Collect;
        EmbeddedScenes.Version = SceneVersion;
    }

    /// <summary>Drive WPF: create the hosted window once the host's own window exists, keep it over
    /// this control, and run the WPF dispatcher's pending work (layout, animation, render). Call once
    /// per iteration of the WinForms host loop, next to Application.DoEvents.</summary>
    internal void Pump()
    {
        if (_failed) return;
        try
        {
            EnsureSource();
            // Everything at a HIGHER priority than Background runs first — which is Render, where
            // WPF's MediaContext commits the frame the sink then publishes. This is WPF's DoEvents.
            _dispatcher?.Invoke(() => { }, DispatcherPriority.Background);
        }
        catch (Exception ex)
        {
            _failed = true;
            Console.Error.WriteLine("ELEMENTHOST-FAILED: " + ex);
        }
    }

    // Whether the hosted window's scene is COMPOSITED into the WinForms frame (claimed from the sink,
    // one render pass, one surface) or the window PRESENTS ITSELF as a borderless overlay pinned over
    // this control.
    //
    // The difference is what a "child window" means per platform. On Windows the hosted HwndSource is
    // a real WS_CHILD HWND: it never paints, and the host's swap-chain present covers its area, so the
    // composited scene is what shows and the OS still routes input to it. Off Windows there are no
    // child windows -- HwndWrapper maps WS_CHILD to a borderless platform window (NSWindow / wl_surface)
    // owned by the host, which is how WPF popups already work there. Such a window is composited by the
    // OS, not by us, so leaving it unpainted would show an empty rectangle; it presents its own surface
    // instead. Either way the WPF tree is a real HwndSource with real layout, hit-testing and input --
    // Cocoa and Wayland deliver mouse/keyboard into HwndMouseInputProvider exactly as for any WPF window.
    private static bool CompositeIntoHostFrame => OperatingSystem.IsWindows();

    private void EnsureSource()
    {
        if (EmbeddedScenes.HostWindow == IntPtr.Zero) return;    // host window not created yet
        _scale = EmbeddedScenes.HostScale > 0 ? EmbeddedScenes.HostScale : 1f;

        // Where this control is, in the hosted window's coordinate space, in device pixels. A real
        // child window is positioned in its PARENT's client space; a borderless overlay is positioned
        // in SCREEN space. The driver's form-client origin coincides with the host window's client
        // origin (the host presents the form there), so both derive from the same two points.
        SD.Point mine = PointToScreen(SD.Point.Empty);          // driver screen space (points)
        SD.Point form = FindForm().PointToScreen(SD.Point.Empty);
        int pw = (int)Math.Round(Width * _scale);
        int ph = (int)Math.Round(Height * _scale);
        int px = (int)Math.Round((mine.X - form.X) * _scale);
        int py = (int)Math.Round((mine.Y - form.Y) * _scale);
        if (!CompositeIntoHostFrame)
        {
            NativePlatform.GetWindowOrigin(EmbeddedScenes.HostWindow, out int hx, out int hy);
            px += hx;
            py += hy;
        }

        if (_source == null)
        {
            var p = new HwndSourceParameters("WpfElementHost")
            {
                ParentWindow = EmbeddedScenes.HostWindow,
                WindowStyle = WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS,
                PositionX = px,
                PositionY = py,
                Width = pw,
                Height = ph,
            };
            _source = new HwndSource(p);
            // Claim BEFORE the first render: an unclaimed frame would build a swap chain for this
            // window and present the WPF content into it, which is both wasted and visible.
            if (CompositeIntoHostFrame) HostedWpfContent.Claim(_source.Handle);
            _source.RootVisual = _child;
            _source.AddHook(SourceHook);
            _dispatcher = _source.Dispatcher;
            _placedX = px; _placedY = py; _placedW = pw; _placedH = ph;
            Console.WriteLine($"ElementHost: WPF window 0x{_source.Handle:x} in host 0x{EmbeddedScenes.HostWindow:x} " +
                              $"at ({px},{py}) {pw}x{ph} scale={_scale} " +
                              (CompositeIntoHostFrame ? "composited into the host frame" : "presenting its own overlay surface"));
            return;
        }

        // Follow the control when the layout (or the whole window) moves or resizes it. The WPF tree
        // re-measures itself from the window size, exactly as a resized top-level WPF window does.
        if (px != _placedX || py != _placedY || pw != _placedW || ph != _placedH)
        {
            _placedX = px; _placedY = py; _placedW = pw; _placedH = ph;
            MoveHostedWindow(px, py, pw, ph);
        }
    }

    // Move/resize the hosted window through WPF's OWN windowing seam rather than user32, so this works
    // on every head: MS.Win32.UnsafeNativeMethods.SetWindowPos is a real SetWindowPos on Windows and a
    // forward to the platform window (Cocoa/Wayland/browser) everywhere else. It is internal to
    // PresentationCore, hence the reflection; a head that somehow lacks it just leaves the window put.
    private static MethodInfo s_setWindowPos;
    private static bool s_setWindowPosResolved;

    private void MoveHostedWindow(int x, int y, int w, int h)
    {
        if (!s_setWindowPosResolved)
        {
            s_setWindowPosResolved = true;
            Type t = typeof(HwndSource).Assembly.GetType("MS.Win32.UnsafeNativeMethods");
            s_setWindowPos = t?.GetMethod("SetWindowPos", BindingFlags.Public | BindingFlags.Static);
        }
        if (s_setWindowPos == null) return;
        try
        {
            s_setWindowPos.Invoke(null, new object[]
            {
                new HandleRef(this, _source.Handle), new HandleRef(null, IntPtr.Zero),
                x, y, w, h, SWP_NOZORDER | SWP_NOACTIVATE,
            });
        }
        catch (Exception ex) { Console.Error.WriteLine("ElementHost: move failed: " + ex.Message); }
    }

    // Give the WPF window the keyboard when it is clicked. A child window does not get focus from
    // DefWindowProc the way a top-level window does — the hosting app has to grant it, which is what
    // the real ElementHost does too. (The WinForms host hands focus back when a control is clicked.)
    private IntPtr SourceHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (s_traceInput && msg is 0x0200 or 0x0201 or 0x0202 or 0x0007 or 0x0008 or 0x0021)
            Console.WriteLine($"elementhost trace: msg=0x{msg:x4} w=0x{wParam:x} l=0x{lParam:x}");
        if (msg == WM_LBUTTONDOWN && OperatingSystem.IsWindows()) SetFocus(hwnd);
        return IntPtr.Zero;
    }

    /// <summary>The WPF scene for this frame, positioned like any of the driver's own window scenes:
    /// in form-point space, with (ox, oy) the form's origin in the driver's screen space.</summary>
    private List<(object Scene, int X, int Y)> Collect(int ox, int oy)
    {
        if (_source == null || !CompositeIntoHostFrame) return null;   // the window presents itself
        object scene = HostedWpfContent.GetScene(_source.Handle, out int w, out int h, out _);
        if (scene is not SceneVisual sv) return null;      // WPF has not rendered a frame yet

        // WPF's scene is in ITS device pixels; the presenter scales the whole frame by the same
        // factor, so undo it here to land 1:1. The clip is evaluated in this node's LOCAL (pre-
        // transform) space, so it is expressed in those same device pixels — see EmbeddedContent,
        // which gets this the other way round for the same reason.
        var wrap = new SceneVisual
        {
            Transform = Matrix3x2.CreateScale(1f / _scale),
            Clip = new Rect(0, 0, w, h),
        };
        wrap.Children.Add(sv);

        SD.Point s = PointToScreen(SD.Point.Empty);
        if (s_traceInput && !_loggedPlacement)
        {
            _loggedPlacement = true;
            Console.WriteLine($"elementhost placement: scene=({s.X - ox},{s.Y - oy}) window=({_placedX / _scale},{_placedY / _scale}) " +
                              $"ptToScreen=({s.X},{s.Y}) formOrigin=({ox},{oy})");
        }
        return new List<(object, int, int)> { (wrap, s.X - ox, s.Y - oy) };
    }

    // ---- self-test input ------------------------------------------------------------------

    /// <summary>
    /// Where the centre of a hosted WPF element is DRAWN, and where it is CLICKABLE, both in the host
    /// window's client pixels. They must agree: the scene is composited at this control's position in
    /// the host's frame, while input arrives at the hosted window's own client rect, and nothing ties
    /// the two together automatically. They came apart when the host's surface did not map 1:1 onto
    /// its client area -- the compositor rescaled the frame, so what you saw drifted from what you
    /// hit, worse the further from the origin you looked. Asserting this needs no mouse, which also
    /// makes it safe to run on a machine somebody is using.
    /// </summary>
    internal bool TryGetAlignment(WPF.FrameworkElement target, out SD.Point drawn, out SD.Point clickable)
    {
        drawn = clickable = SD.Point.Empty;
        if (_source?.RootVisual is not System.Windows.Media.Visual root || target == null) return false;
        WPF.Point c = target.TransformToAncestor(root)
                            .Transform(new WPF.Point(target.ActualWidth / 2, target.ActualHeight / 2));

        // Drawn: element DIPs -> this control's points -> host client pixels, the path Collect uses.
        SD.Point origin = PointToScreen(SD.Point.Empty);
        drawn = new SD.Point((int)Math.Round((origin.X + c.X) * _scale), (int)Math.Round((origin.Y + c.Y) * _scale));

        // Clickable: the same element point in the hosted window's client pixels, mapped to screen by
        // the OS and back into the host's client space.
        var pt = new SD.Point((int)Math.Round(c.X * _scale), (int)Math.Round(c.Y * _scale));
        if (!OsInput.TryMapClientToClient(_source.Handle, EmbeddedScenes.HostWindow, ref pt)) return false;
        clickable = pt;
        return true;
    }

    /// <summary>Put the real cursor over the centre of a hosted WPF element, aiming at where it is
    /// DRAWN (see <see cref="TryGetAlignment"/>). Manual/opt-in: it moves the physical pointer.</summary>
    internal bool MoveCursorTo(WPF.FrameworkElement target)
    {
        if (!TryGetAlignment(target, out SD.Point drawn, out _)) return false;
        return OsInput.MoveCursorToClientPoint(EmbeddedScenes.HostWindow, drawn.X, drawn.Y);
    }

    private int SceneVersion()
    {
        if (_source == null) return 0;
        HostedWpfContent.GetScene(_source.Handle, out _, out _, out int version);
        return version;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && _source != null)
        {
            HostedWpfContent.Release(_source.Handle);
            EmbeddedScenes.Collect = null;
            EmbeddedScenes.Version = null;
            _source.Dispose();
            _source = null;
        }
        base.Dispose(disposing);
    }

    [DllImport("user32")] private static extern IntPtr SetFocus(IntPtr hWnd);
}
