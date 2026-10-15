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
    public partial class ElementHost : Control, IEmbeddedScene
    {
        private const int WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_CLIPSIBLINGS = 0x04000000;
        private const int SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;
        private const int WM_LBUTTONDOWN = 0x0201, WM_SETFOCUS = 0x0007, WM_NCHITTEST = 0x0084;

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

        // WPF pumps the thread's messages itself whenever its dispatcher waits (ElementHost.Pump runs
        // the dispatcher every frame), and then input for the hosted tree never passes the
        // application's message filters -- which stock WinForms offers every message it pumps.
        // DotNetBar closes its menus from such a filter: a context menu opened over a hosted WPF
        // canvas could not be closed by clicking elsewhere or by Esc. So input that WPF's loop takes
        // off the queue is offered to the filters first, once per thread.
        [ThreadStatic] private static bool t_filterHooked;
        [ThreadStatic] private static bool t_draining;
        private static readonly bool s_traceFilter = Environment.GetEnvironmentVariable("WF_TRACE_FILTER") == "1";

        private static void HookMessageFilters()
        {
            if (t_filterHooked) return;
            t_filterHooked = true;
            System.Windows.Interop.ComponentDispatcher.ThreadFilterMessage += (ref System.Windows.Interop.MSG msg, ref bool handled) =>
            {
                if (handled) return;
                int m = msg.message;
                bool input = (m >= 0x0100 && m <= 0x0109) || (m >= 0x0200 && m <= 0x020E) || (m >= 0x00A0 && m <= 0x00AD);
                if (!input) return;
                try
                {
                    // One queue, as on Windows: what WinForms posted before this click or key (a
                    // BeginInvoke that opens a menu) runs before it. Here those wait in the driver's
                    // own queue while WPF's loop takes input off the system one, so a second quick
                    // right-click reached the filters before the first menu existed -- nothing to
                    // close -- and DotNetBar then reused that menu for the next one, leaving the first
                    // one's popup window behind, empty, for good.
                    bool press = m == 0x0201 || m == 0x0204 || m == 0x0207 || m == 0x020B || m == 0x00A1 || m == 0x00A4 || m == 0x00A7
                                 || m == 0x0100 || m == 0x0104;
                    if (press && !t_draining)
                    {
                        t_draining = true;
                        try { Application.DoEvents(); }
                        finally { t_draining = false; }
                    }
                    var message = Message.Create(msg.hwnd, m, msg.wParam, msg.lParam);
                    if (Application.FilterMessage(ref message)) handled = true;
                    if (s_traceFilter && m != 0x0200) Console.Error.WriteLine($"elementhost filter: {DateTime.Now:HH:mm:ss.fff} msg=0x{m:x} hwnd=0x{msg.hwnd.ToInt64():x} taken={handled}");
                }
                catch (Exception ex) { Console.Error.WriteLine("ElementHost: message filter failed: " + ex.Message); }
            };
        }

        private readonly System.Threading.Thread _ownerThread = System.Threading.Thread.CurrentThread;

        /// <summary>Whether this is the thread the hosted WPF tree belongs to -- asked without making
        /// the tree (Container is created on first use, and made on the wrong thread it is lost).</summary>
        private bool OnTreeThread => _container != null ? _container.Dispatcher.CheckAccess() : _ownerThread == System.Threading.Thread.CurrentThread;

        public ElementHost()
        {
            HookMessageFilters();
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
                // The hosted window's root is HostContainer, as on Windows, and Child is its content.
                Container.Children.Clear();
                if (value != null) Container.Children.Add(value);
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
            // Every UI thread's frame pumps every embedded scene. Another thread's (a splash screen's)
            // must leave this one alone: invoking onto this tree's dispatcher from there deadlocked
            // the moment this thread was itself waiting on that one.
            if (!OnTreeThread) return;
            try
            {
                EnsureSource();
            }
            catch (Exception ex)
            {
                _failed = true;
                Console.Error.WriteLine("ElementHost: hosting failed: " + ex);
                return;
            }
            try
            {
                // Everything at a HIGHER priority than Background runs first -- which includes Render,
                // where WPF's MediaContext commits the frame the sink then publishes. WPF's DoEvents.
                _dispatcher?.Invoke(() => { }, DispatcherPriority.Background);
            }
            catch (Exception ex)
            {
                // The APPLICATION's code threw: a WPF event handler, run by this pump. WPF has
                // already offered it to Dispatcher.UnhandledException and nobody handled it. It is
                // not a hosting failure. This used to switch the host off for good (GraphWorX's
                // NullReferenceException after its component editor closed), when on Windows the
                // exception goes on to the WinForms message loop that dispatched the WPF window's
                // message: Application.ThreadException, the application's own handler or the
                // exception dialog, and the host carries on.
                ReportAppException(ex);
            }
        }

        /// <summary>An exception from application code run by this host: handled as WinForms' message
        /// loop handles one from a window procedure.</summary>
        private static void ReportAppException(Exception ex)
        {
            if (ex is TargetInvocationException { InnerException: Exception inner }) ex = inner;
            Application.OnThreadException(ex);
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
            if (_child == null) return;                             // nothing to host yet
            // Another UI thread's frame (a splash screen's) pumps every embedded scene; this one is
            // only ever made and moved on the thread its WPF tree belongs to.
            if (!OnTreeThread) return;
            // Not while hidden: .NET's ElementHost gets its window (and so its HwndSource) only once it
            // is shown, as every WinForms control does, and apps count on hidden hosts' WPF trees not
            // having loaded -- GraphWorX's licence model belongs to the first view to load, and a
            // hidden pane's view loading first took it from the main one (no licence notice).
            if (_source == null && !ShownInItsForm()) return;
            // This control's own form's window: not EmbeddedScenes.HostWindow, which is whichever
            // form's host came up last (a tooltip, a drop-down) and would carry this content off.
            if (!EmbeddedScenes.TryGetHostWindow(this, out IntPtr hostWindow, out float hostScale))
            {
                // No window to put it in yet -- the form's own appears only once it is shown. .NET's
                // ElementHost has its HwndSource from handle creation, and apps rely on it: a WPF
                // Focus() made while the form is coming up (GraphWorX focuses its main view as it
                // becomes visible) found no source, did nothing, and the form's activation then gave
                // the keyboard to the first control in tab order instead. So on Windows the source is
                // made at once, in a hidden parking window, and moved into the form's window when
                // there is one.
                if (!CompositeIntoHostFrame || _source != null || FindForm() == null) return;
                hostWindow = ParkingWindow();
                if (hostWindow == IntPtr.Zero) return;
                hostScale = 1f;
                _parked = true;
            }
            else if (_parked && _source != null)
            {
                _parked = false;
                if (!ReparentHostedWindow(hostWindow)) { _failed = true; return; }
                // Shown again in its real parent: WPF tracks visibility from the window's
                // WM_SHOWWINDOW, and in the hidden parking window the tree came up not visible --
                // content that appears only once it is (GraphWorX's licence notice) never did.
                ShowHostedWindow(false);
                ShowHostedWindow(true);
                _placedX = _placedY = int.MinValue;                 // place it again, in its real parent
            }

            // An overlay window is on screen exactly while this control is: Visible (which is false
            // when any parent is hidden) in a shown form. On Windows the hosted window is a child of
            // this control's HWND and is hidden with it; a separate window has to be told.
            bool shown = Visible && FindForm() is Form f && f.Visible;
            if (!CompositeIntoHostFrame && _source != null && shown != _shown)
            {
                _shown = shown;
                ShowHostedWindow(shown);
            }
            if (FindForm() == null) return;                         // not parented yet
            _scale = hostScale;

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
                NativePlatform.GetWindowOrigin(hostWindow, out int hx, out int hy);
                px += hx;
                py += hy;
            }

            if (_source == null)
            {
                _shown = shown || CompositeIntoHostFrame;
                var p = new HwndSourceParameters("ElementHost")
                {
                    ParentWindow = hostWindow,
                    WindowStyle = WS_CHILD | (_shown ? WS_VISIBLE : 0) | WS_CLIPSIBLINGS,
                    PositionX = px,
                    PositionY = py,
                    Width = pw,
                    Height = ph,
                };
                _source = new HwndSource(p);
                // Claim BEFORE the first render: an unclaimed frame would build a swap chain for this
                // window and present the WPF content into it, which is both wasted and visible.
                if (CompositeIntoHostFrame) HostedWpfContent.Claim(_source.Handle);
                _source.RootVisual = RootElement;
                _source.AddHook(SourceHook);
                _dispatcher = _source.Dispatcher;
                _placedX = px; _placedY = py; _placedW = pw; _placedH = ph;
                if (s_trace)
                    Console.WriteLine($"ElementHost: WPF window 0x{_source.Handle:x} in host 0x{hostWindow:x} " +
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
        private bool _parked;
        [ThreadStatic] private static HwndSource t_parking;

        /// <summary>A hidden window of this thread to make hosted windows in before their form has
        /// a window of its own (see EnsureSource). Claimed, so it never gets a surface.</summary>
        private static IntPtr ParkingWindow()
        {
            if (t_parking == null || t_parking.IsDisposed)
            {
                const int WS_POPUP = unchecked((int)0x80000000);
                t_parking = new HwndSource(new HwndSourceParameters("ElementHostParking") { WindowStyle = WS_POPUP, Width = 1, Height = 1 });
                HostedWpfContent.Claim(t_parking.Handle);
            }
            return t_parking.Handle;
        }

        private static MethodInfo s_setParent;

        private bool ReparentHostedWindow(IntPtr parent)
        {
            if (s_setParent == null)
                foreach (Assembly a in new[] { typeof(HwndSource).Assembly, typeof(Dispatcher).Assembly })
                {
                    s_setParent = a.GetType("MS.Win32.UnsafeNativeMethods")?.GetMethod("SetParent", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(HandleRef), typeof(HandleRef) }, null);
                    if (s_setParent != null) break;
                }
            if (s_setParent == null) return false;
            try { s_setParent.Invoke(null, new object[] { new HandleRef(this, _source.Handle), new HandleRef(null, parent) }); return true; }
            catch (Exception ex) { Console.Error.WriteLine("ElementHost: reparent failed: " + ex.Message); return false; }
        }

        /// <summary>Whether this control, and every container up to its form, is meant to be shown --
        /// the form itself may still be coming up. That is when .NET creates a control's window.</summary>
        private bool ShownInItsForm()
        {
            for (Control c = this; c != null && c is not Form; c = c.Parent)
                if (!c.is_visible) return false;
            return true;
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (ShownInItsForm() && CompositeIntoHostFrame && _source == null && !_failed && IsHandleCreated && OnTreeThread)
            {
                try { EnsureSource(); }
                catch (Exception ex) { _failed = true; Console.Error.WriteLine("ElementHost: hosting failed: " + ex); }
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // Only on the thread the WPF tree belongs to (a control can be created on one thread and
            // get its handle on another).
            if (CompositeIntoHostFrame && _source == null && !_failed && OnTreeThread)
            {
                try { EnsureSource(); }
                catch (Exception ex) { _failed = true; Console.Error.WriteLine("ElementHost: hosting failed: " + ex); }
            }
        }

        private static MethodInfo s_setWindowPos;
        private static bool s_setWindowPosResolved;

        private static MethodInfo s_showWindow;
        private static bool s_showWindowResolved;
        private bool _shown;

        // Shows or hides the hosted window through WPF's ShowWindow seam (a forward to the platform
        // window off Windows), like MoveHostedWindow.
        private void ShowHostedWindow(bool show)
        {
            if (!s_showWindowResolved)
            {
                s_showWindowResolved = true;
                foreach (Assembly a in new[] { typeof(HwndSource).Assembly, typeof(Dispatcher).Assembly })
                {
                    s_showWindow = a.GetType("MS.Win32.UnsafeNativeMethods")?.GetMethod("ShowWindow", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(HandleRef), typeof(int) }, null);
                    if (s_showWindow != null) break;
                }
            }
            if (s_showWindow == null) return;
            const int SW_HIDE = 0, SW_SHOWNA = 8;
            try { s_showWindow.Invoke(null, new object[] { new HandleRef(this, _source.Handle), show ? SW_SHOWNA : SW_HIDE }); }
            catch (TargetInvocationException ex) when (ex.InnerException != null) { ReportAppException(ex); }   // a handler of the tree's (IsVisibleChanged ...)
            catch (Exception ex) { Console.Error.WriteLine("ElementHost: show/hide failed: " + ex.Message); }
        }

        private void MoveHostedWindow(int x, int y, int w, int h)
        {
            if (!s_setWindowPosResolved)
            {
                s_setWindowPosResolved = true;
                // The seam is compiled into WindowsBase (UnsafeNativeMethodsCLR.cs); PresentationCore's
                // UnsafeNativeMethods has no SetWindowPos, and looking only there left every move and
                // resize of the hosted window silently undone off Windows.
                foreach (Assembly a in new[] { typeof(HwndSource).Assembly, typeof(Dispatcher).Assembly })
                {
                    s_setWindowPos = a.GetType("MS.Win32.UnsafeNativeMethods")?.GetMethod("SetWindowPos", BindingFlags.Public | BindingFlags.Static, null,
                        new[] { typeof(HandleRef), typeof(HandleRef), typeof(int), typeof(int), typeof(int), typeof(int), typeof(int) }, null);
                    if (s_setWindowPos != null) break;
                }
                if (s_setWindowPos == null) Console.Error.WriteLine("ElementHost: no SetWindowPos seam; the hosted window cannot follow its control");
            }
            if (s_setWindowPos == null) return;
            if (s_trace) Console.WriteLine($"ElementHost: move 0x{_source.Handle:x} to ({x},{y}) {w}x{h}");
            try
            {
                s_setWindowPos.Invoke(null, new object[]
                {
                    new HandleRef(this, _source.Handle), new HandleRef(null, IntPtr.Zero),
                    x, y, w, h, SWP_NOZORDER | SWP_NOACTIVATE,
                });
            }
            catch (TargetInvocationException ex) when (ex.InnerException != null) { ReportAppException(ex); }   // a handler of the tree's (SizeChanged ...)
            catch (Exception ex) { Console.Error.WriteLine("ElementHost: move failed: " + ex.Message); }
        }

        // Give the WPF window the keyboard when it is clicked. A child window does not get focus from
        // DefWindowProc the way a top-level window does -- the hosting app has to grant it, which is
        // what the original ElementHost does too. (The WinForms host takes focus back when a control
        // outside this one is clicked.)
        private IntPtr SourceHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (s_trace && msg is 0x0200 or 0x0201 or 0x0202 or 0x0007 or 0x0008)
                Console.WriteLine($"elementhost trace: msg=0x{msg:x4} w=0x{wParam:x} l=0x{lParam:x} hwnd=0x{hwnd:x} thread={Environment.CurrentManagedThreadId}");
            // Where one of WinForms' popups is drawn over this content (a context menu opened on
            // it), the hosted window is not what the user points at: pass the mouse on to the host
            // window, which hands it to the popup -- as the popup's own window would take it on
            // Windows, being above this one.
            if (msg == WM_NCHITTEST && OperatingSystem.IsWindows())
            {
                int sx = (short)((long)lParam & 0xFFFF), sy = (short)(((long)lParam >> 16) & 0xFFFF);
                if (EmbeddedScenes.PopupCovers(hwnd, sx, sy))
                {
                    handled = true;
                    return new IntPtr(-1);              // HTTRANSPARENT
                }
            }
            if (msg == WM_LBUTTONDOWN && OperatingSystem.IsWindows()) SetFocus(hwnd);
            // The hosted window has the keyboard, so this control is WinForms' focused control, as an
            // ElementHost is in .NET: the form's active chain points here and every container on the
            // way (a DotNetBar dock bar's caption) shows itself inactive. Without it the focus stayed
            // on whatever WinForms control had it before the WPF content was clicked or focused.
            if (msg == WM_SETFOCUS && !Focused && CanFocus) Focus();
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
                || !ClientToScreen(_source.Handle, ref pt) || !EmbeddedScenes.TryGetHostWindow(this, out IntPtr hostWindow, out _) || !ScreenToClient(hostWindow, ref pt))
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
