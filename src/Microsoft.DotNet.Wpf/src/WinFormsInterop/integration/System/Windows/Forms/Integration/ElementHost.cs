// System.Windows.Forms.Integration.ElementHost, for the cross-platform (WebGPU) stack. The mirror of
// WindowsFormsHost: that embeds a WinForms control tree in a WPF app, this embeds a WPF element tree
// in a WinForms app. Same namespace, same type name and the same core API as the Windows-only
// original, so existing app code -- `new ElementHost { Dock = DockStyle.Fill, Child = myWpfControl }`
// -- compiles and runs unchanged.
//
// How it works, and why it is not a bitmap:
//
//   * The WPF side is ORDINARY WPF. The element tree is the RootVisual of a real HwndSource sized and
//     positioned to this control, so WPF does its own measure/arrange, hit-testing, input, focus,
//     capture, popups, animation and DPI exactly as in a standalone app. None of that is
//     re-implemented here, and on Windows input needs no forwarding at all: the OS delivers it to
//     the hosted window, which IS a WPF window.
//
//   * Only presentation changes. HostedWpfContent.Claim tells WpfCompositionSink not to give this
//     window a swap chain of its own; instead the sink publishes the window's decoded root
//     SceneVisual, and Collect hands it to the WinForms present path. The WinForms controls and the
//     WPF tree are therefore composited in ONE WebGPU render pass onto ONE surface -- no intermediate
//     bitmap, no readback, no second swap chain, no z-order fight between two native windows.
//
// Coordinates: the WinForms driver works in 96dpi points, which are also WPF's DIPs, so the two agree
// on sizes. WPF's scene arrives in DEVICE pixels (its window inherits the host's per-monitor DPI) and
// the presenter scales the whole frame from points to device pixels, so Collect wraps the WPF scene
// in a 1/scale transform to land on device pixels exactly once.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using SD = System.Drawing;
using WPF = System.Windows;
// Both halves of the stack define these names. Inside System.Windows.Forms.Integration the WPF ones
// win by proximity, so the scene-graph types have to be named explicitly.
using WgpuRect = Microsoft.Wpf.Interop.WebGpu.Composition.Rect;
using NativePlatform = Microsoft.Wpf.Interop.WebGpu.Composition.Platform.NativePlatform;

namespace System.Windows.Forms.Integration
{
    /// <summary>Hosts a WPF <see cref="WPF.UIElement"/> inside a Windows Forms control.</summary>
    public class ElementHost : Control, IEmbeddedScene
    {
        private const int WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_CLIPSIBLINGS = 0x04000000;
        private const int SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;
        private const int WM_LBUTTONDOWN = 0x0201;

        private WPF.UIElement _child;
        private HwndSource _source;
        private Dispatcher _dispatcher;
        private float _scale = 1f;
        private int _placedX = int.MinValue, _placedY, _placedW, _placedH;
        private bool _failed;

        // WF_TRACE_INPUT=1 traces the mouse/focus messages the hosted window sees and where its scene
        // is placed -- the first things to check when the WPF tree draws but does not react.
        private static readonly bool s_trace = Environment.GetEnvironmentVariable("WF_TRACE_INPUT") == "1";
        private bool _tracedPlacement;

        public ElementHost()
        {
            // The WPF tree paints this area; the control's own background only shows for the frame or
            // two before WPF's first render. (Not Color.Transparent: a Control refuses that unless it
            // opts into SupportsTransparentBackColor, and the WPF root paints the whole rect anyway.)
            EmbeddedScenes.Register(this);
        }

        /// <summary>The hosted WPF element. Assigning replaces the hosted tree.</summary>
        [Browsable(false)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public WPF.UIElement Child
        {
            get => _child;
            set
            {
                if (ReferenceEquals(_child, value)) return;
                WPF.UIElement old = _child;
                _child = value;
                if (_source != null) _source.RootVisual = value;
                OnChildChanged(new ChildChangedEventArgs(old));
            }
        }

        /// <summary>Raised after <see cref="Child"/> is replaced.</summary>
        public event EventHandler<ChildChangedEventArgs> ChildChanged;

        protected virtual void OnChildChanged(ChildChangedEventArgs e) => ChildChanged?.Invoke(this, e);

        protected override SD.Size DefaultSize => new SD.Size(100, 100);

        /// <summary>Whether the hosted window exists and WPF has rendered at least one frame.</summary>
        [Browsable(false)]
        public bool IsChildLive => _source != null && Version > 0;

        // ---- IEmbeddedScene: driven once per frame by the WinForms present loop ------------------

        void IEmbeddedScene.Pump()
        {
            if (_failed || IsDisposed) return;
            try
            {
                EnsureSource();
                // Everything at a HIGHER priority than Background runs first -- which includes Render,
                // where WPF's MediaContext commits the frame the sink then publishes. WPF's DoEvents.
                _dispatcher?.Invoke(() => { }, DispatcherPriority.Background);
            }
            catch (Exception ex)
            {
                _failed = true;
                Console.Error.WriteLine("ElementHost: hosting failed: " + ex);
            }
        }

        int IEmbeddedScene.Version => Version;

        private int Version
        {
            get
            {
                if (_source == null) return 0;
                HostedWpfContent.GetScene(_source.Handle, out _, out _, out int version);
                return version;
            }
        }

        void IEmbeddedScene.Collect(int ox, int oy, List<(object Scene, int X, int Y)> into)
        {
            if (_source == null || !CompositeIntoHostFrame || !Visible) return;
            object scene = HostedWpfContent.GetScene(_source.Handle, out int w, out int h, out _);
            if (scene is not SceneVisual sv) return;              // WPF has not rendered a frame yet

            // WPF's scene is in ITS device pixels; the presenter scales the whole frame by the same
            // factor, so undo it here to land 1:1. The clip is evaluated in this node's LOCAL
            // (pre-transform) space, so it is expressed in those same device pixels -- see
            // EmbeddedContent, which gets this the other way round for the same reason.
            var wrap = new SceneVisual
            {
                Transform = Matrix3x2.CreateScale(1f / _scale),
                Clip = new WgpuRect(0, 0, w, h),
            };
            wrap.Children.Add(sv);

            SD.Point s = PointToScreen(SD.Point.Empty);
            if (s_trace && !_tracedPlacement)
            {
                _tracedPlacement = true;
                Console.WriteLine($"elementhost placement: scene=({s.X - ox},{s.Y - oy}) window=({_placedX / _scale},{_placedY / _scale})");
            }
            into.Add((wrap, s.X - ox, s.Y - oy));
        }

        // ---- the hosted window -------------------------------------------------------------------

        // Whether the hosted window's scene is COMPOSITED into the WinForms frame (claimed from the
        // sink, one render pass, one surface) or the window PRESENTS ITSELF as a borderless overlay
        // pinned over this control.
        //
        // The difference is what a "child window" means per platform. On Windows the hosted HwndSource
        // is a real WS_CHILD HWND: it never paints, and the host's swap-chain present covers its area,
        // so the composited scene is what shows while the OS still routes input to it. Off Windows
        // there are no child windows -- HwndWrapper maps WS_CHILD to a borderless platform window
        // (NSWindow / wl_surface) owned by the host, which is how WPF popups already work there. Such
        // a window is composited by the OS, not by us, so leaving it unpainted would show an empty
        // rectangle; it presents its own surface instead. Either way the WPF tree is a real
        // HwndSource with real layout, hit-testing and input -- Cocoa and Wayland deliver
        // mouse/keyboard into HwndMouseInputProvider exactly as for any WPF window.
        private static bool CompositeIntoHostFrame => OperatingSystem.IsWindows();

        private void EnsureSource()
        {
            if (EmbeddedScenes.HostWindow == IntPtr.Zero) return;   // host window not created yet
            if (_child == null) return;                             // nothing to host yet
            if (FindForm() == null) return;                         // not parented yet
            _scale = EmbeddedScenes.HostScale > 0 ? EmbeddedScenes.HostScale : 1f;

            // Where this control is, in the hosted window's coordinate space, in device pixels. A real
            // child window is positioned in its PARENT's client space; a borderless overlay is
            // positioned in SCREEN space. The driver's form-client origin coincides with the host
            // window's client origin (the host presents the form there), so both derive from the same
            // two points.
            SD.Point mine = PointToScreen(SD.Point.Empty);
            SD.Point form = FindForm().PointToScreen(SD.Point.Empty);
            int pw = Math.Max(1, (int)Math.Round(Width * _scale));
            int ph = Math.Max(1, (int)Math.Round(Height * _scale));
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
                var p = new HwndSourceParameters("ElementHost")
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
                if (s_trace)
                    Console.WriteLine($"ElementHost: WPF window 0x{_source.Handle:x} in host 0x{EmbeddedScenes.HostWindow:x} " +
                                      $"at ({px},{py}) {pw}x{ph} scale={_scale} " +
                                      (CompositeIntoHostFrame ? "composited into the host frame" : "presenting its own overlay surface"));
                return;
            }

            // Follow the control when the layout (or the whole window) moves or resizes it. The WPF
            // tree re-measures itself from the window size, as a resized top-level WPF window does.
            if (px != _placedX || py != _placedY || pw != _placedW || ph != _placedH)
            {
                _placedX = px; _placedY = py; _placedW = pw; _placedH = ph;
                MoveHostedWindow(px, py, pw, ph);
            }
        }

        // Move/resize the hosted window through WPF's OWN windowing seam rather than user32, so this
        // works on every head: MS.Win32.UnsafeNativeMethods.SetWindowPos is a real SetWindowPos on
        // Windows and a forward to the platform window (Cocoa/Wayland/browser) everywhere else. It is
        // internal to PresentationCore, hence the reflection; a head that somehow lacks it just leaves
        // the window put rather than failing the app.
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
        // DefWindowProc the way a top-level window does -- the hosting app has to grant it, which is
        // what the original ElementHost does too. (The WinForms host takes focus back when a control
        // outside this one is clicked.)
        private IntPtr SourceHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (s_trace && msg is 0x0200 or 0x0201 or 0x0202 or 0x0007 or 0x0008)
                Console.WriteLine($"elementhost trace: msg=0x{msg:x4} w=0x{wParam:x} l=0x{lParam:x}");
            if (msg == WM_LBUTTONDOWN && OperatingSystem.IsWindows()) SetFocus(hwnd);
            return IntPtr.Zero;
        }

        /// <summary>
        /// Where the centre of a hosted WPF element is DRAWN, and where it is CLICKABLE, both in the
        /// host window's client pixels. They must agree: the scene is composited at this control's
        /// position in the host's frame, while input arrives at the hosted window's own client rect,
        /// and nothing ties the two together automatically. Exposed because it is the cheapest way for
        /// a test to assert that -- no mouse required.
        /// </summary>
        public bool TryGetAlignment(WPF.FrameworkElement target, out SD.Point drawn, out SD.Point clickable)
        {
            drawn = clickable = SD.Point.Empty;
            if (_source?.RootVisual is not WPF.Media.Visual root || target == null) return false;
            WPF.Point c = target.TransformToAncestor(root)
                                .Transform(new WPF.Point(target.ActualWidth / 2, target.ActualHeight / 2));

            SD.Point origin = PointToScreen(SD.Point.Empty);
            drawn = new SD.Point((int)Math.Round((origin.X + c.X) * _scale), (int)Math.Round((origin.Y + c.Y) * _scale));

            var pt = new POINT { x = (int)Math.Round(c.X * _scale), y = (int)Math.Round(c.Y * _scale) };
            if (!OperatingSystem.IsWindows()
                || !ClientToScreen(_source.Handle, ref pt) || !ScreenToClient(EmbeddedScenes.HostWindow, ref pt))
                return false;
            clickable = new SD.Point(pt.x, pt.y);
            return true;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                EmbeddedScenes.Unregister(this);
                if (_source != null)
                {
                    HostedWpfContent.Release(_source.Handle);
                    _source.Dispose();
                    _source = null;
                }
            }
            base.Dispose(disposing);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int x, y; }

        [DllImport("user32")] private static extern IntPtr SetFocus(IntPtr hWnd);
        [DllImport("user32")] private static extern bool ClientToScreen(IntPtr hWnd, ref POINT pt);
        [DllImport("user32")] private static extern bool ScreenToClient(IntPtr hWnd, ref POINT pt);
    }

    /// <summary>Carries the previously hosted element when <see cref="ElementHost.Child"/> changes.</summary>
    public class ChildChangedEventArgs : EventArgs
    {
        public ChildChangedEventArgs(object previousChild) => PreviousChild = previousChild;

        public object PreviousChild { get; }
    }
}
