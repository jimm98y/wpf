// System.Windows.Forms.Integration.WindowsFormsHost, for the cross-platform (WebGPU) stack. The
// mirror of ElementHost next door: that embeds a WPF element tree in a WinForms app, this embeds a
// WinForms control tree in a WPF app. Same namespace, same type name and the same core API as the
// Windows-only original, so existing app code -- `<WindowsFormsHost x:Name="host"/>` plus
// `host.Child = myWinFormsControl` -- compiles and runs unchanged.
//
// How it works, and why it is not a bitmap:
//
//   * The WinForms side is ORDINARY WinForms. The child lives in a real (never-shown) Form, so the
//     Mono control tree does its own layout, painting, themes, focus, timers and double buffering
//     exactly as in a standalone app. None of that is re-implemented here.
//
//   * Only presentation changes. The XplatUIWebGpu driver records each window as a SceneVisual --
//     the SAME type WPF's own compositor emits -- so a hosted control's scene is registered with
//     EmbeddedContent and composited by WpfCompositionSink as another child of the WPF root. One
//     WebGPU render pass, one surface: no intermediate bitmap, no readback, no second swap chain,
//     and no airspace, which is the defect that defined the Windows-only original.
//
// Why it is NOT an HwndHost. The original derives from HwndHost and hands WPF a child HWND. There
// are no HWNDs here -- the driver's handles are managed Hwnd objects, and the whole point of the
// port is not to require Win32 -- so this derives from FrameworkElement and bridges at the scene
// level instead. <see cref="Handle"/> is therefore IntPtr.Zero; see the note on it.
//
// Coordinates: the driver works in 96dpi points, which are also WPF's DIPs, so the two agree on
// sizes; EmbeddedItem wants a DEVICE-pixel rect plus the point->device scale, which is this
// element's transform to the render root times the target's DPI.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using SD = System.Drawing;
using SWF = System.Windows.Forms;
// Both halves of the stack define these names, and inside System.Windows.Forms.Integration the
// WINFORMS ones win by proximity -- so the WPF input types have to be named explicitly or the
// overrides below silently bind to nothing (CS0115).
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;
using WpfMouseEventArgs = System.Windows.Input.MouseEventArgs;
using WpfDragEventArgs = System.Windows.DragEventArgs;
using WpfDragDropEffects = System.Windows.DragDropEffects;
using WpfDragDropKeyStates = System.Windows.DragDropKeyStates;

namespace System.Windows.Forms.Integration
{
    /// <summary>Hosts a Windows Forms <see cref="SWF.Control"/> inside a WPF element tree.</summary>
    public class WindowsFormsHost : FrameworkElement, IDisposable
    {
        // The driver only records scenes in GPU-raster mode; without it every hosted window paints
        // into a backing bitmap that nothing on this path ever reads, and the host renders empty.
        // Both variables are read into `static readonly` fields when their declaring types
        // initialize, so they have to be set before the first WinForms type is touched -- which is
        // what this static constructor guarantees, since reaching any WinForms type through this
        // class necessarily runs it first.
        static WindowsFormsHost()
        {
            if (Environment.GetEnvironmentVariable("WF_WEBGPU") == null)
                Environment.SetEnvironmentVariable("WF_WEBGPU", "1");
            if (Environment.GetEnvironmentVariable("WF_GPU_RASTER") == null)
                Environment.SetEnvironmentVariable("WF_GPU_RASTER", "1");
        }

        // Every live host, and the ONE render tick that serves them all. The message pump
        // (Application.DoEvents) is process-wide and EmbeddedContent.Set replaces the whole hosted
        // set, so a per-instance tick would both pump N times a frame and let each host erase the
        // others' scenes -- two hosts in one window, and only the last one drawn would appear.
        private static readonly List<WindowsFormsHost> s_hosts = new List<WindowsFormsHost>();
        private static readonly object s_lock = new object();
        private static bool s_ticking;

        // Content that is composited the same way but is not a WindowsFormsHost - today, the
        // HwndHost-derived hosts claimed through HwndHostForeignContent. EmbeddedContent.Set
        // REPLACES the whole hosted set, so there can only ever be one publisher; everything that
        // wants to be on screen has to come through this tick.
        private static readonly List<IEmbeddedContentSource> s_extraSources = new List<IEmbeddedContentSource>();

        internal static void AddSource(IEmbeddedContentSource source)
        {
            lock (s_lock)
            {
                if (s_extraSources.Contains(source)) return;
                s_extraSources.Add(source);
                if (!s_ticking)
                {
                    s_ticking = true;
                    CompositionTarget.Rendering += OnRendering;
                }
            }
        }

        internal static void RemoveSource(IEmbeddedContentSource source)
        {
            bool last;
            lock (s_lock)
            {
                s_extraSources.Remove(source);
                last = s_hosts.Count == 0 && s_extraSources.Count == 0;
                if (last && s_ticking)
                {
                    s_ticking = false;
                    CompositionTarget.Rendering -= OnRendering;
                }
            }

            if (last)
            {
                EmbeddedContent.Set(null);
                EmbeddedContent.SetCaret(0, 0, 0, 0, false);
            }
        }
        private static int s_lastPaintVersion = -1;

        private readonly SWF.Form _container;      // the hosted surface; registered with the driver, never shown as an OS window
        private XplatUIWebGpu _driver;
        private SWF.Control _child;
        private int _formOx, _formOy;              // the container's origin in the driver's screen space, refreshed each frame
        private bool _leftDown;
        private bool _disposed;
        private double _lastDevX = double.NaN, _lastDevY, _lastDevW, _lastDevH;

        public WindowsFormsHost()
        {
            // Borderless and never shown: the driver registers the window tree and paints it, but
            // with no per-OS host attached to this form it never reaches a screen of its own. Its
            // pixels only exist as the scene this element composites.
            _container = new SWF.Form
            {
                FormBorderStyle = SWF.FormBorderStyle.None,
                ShowInTaskbar = false,
                StartPosition = SWF.FormStartPosition.Manual,
                MinimumSize = SD.Size.Empty,
            };

            // Never let this one reach the screen. It is a visible top-level Form, so without this
            // the on-screen host would put an empty borderless window up for it -- and, before
            // there could be more than one host, would ALSO leave the dialog the app had actually
            // opened with no window at all.
            SWF.PresentationHost.Suppress(_container);

            Focusable = true;                      // or typed text can never reach the hosted controls
            Loaded += (s, e) => Attach();
            Unloaded += (s, e) => Detach();
        }

        // ---- public surface ---------------------------------------------------------------------

        /// <summary>The hosted Windows Forms control. Assigning replaces the hosted tree.</summary>
        [Browsable(false)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public SWF.Control Child
        {
            get => _child;
            set
            {
                if (ReferenceEquals(_child, value)) return;
                SWF.Control old = _child;
                if (old != null) _container.Controls.Remove(old);
                _child = value;
                if (value != null)
                {
                    // Fill: the original sizes the child to the host's arranged rect, and docking is
                    // how WinForms expresses exactly that.
                    value.Dock = SWF.DockStyle.Fill;
                    _container.Controls.Add(value);
                    value.Invalidate(true);
                }
                InvalidateMeasure();
                OnChildChanged(new ChildChangedEventArgs(old));
            }
        }

        /// <summary>Raised after <see cref="Child"/> is replaced.</summary>
        public event EventHandler<ChildChangedEventArgs> ChildChanged;

        protected virtual void OnChildChanged(ChildChangedEventArgs e) => ChildChanged?.Invoke(this, e);

        /// <summary>
        /// Always <see cref="IntPtr.Zero"/>: this host has no OS window.
        /// </summary>
        /// <remarks>
        /// The Windows-only original inherits this from HwndHost, where it is a real child HWND, and
        /// app code uses it to reach the hosted content with Win32 calls (SetWindowPos to force a
        /// resize, SetFocus, and so on). None of that applies here -- WPF layout sizes this element
        /// and the scene follows it -- and there is no handle that would make those calls meaningful.
        /// Zero rather than a driver handle precisely so that the usual `if (h == IntPtr.Zero)
        /// return;` guard in such code takes the early exit instead of P/Invoking into a user32 that
        /// does not exist on this platform.
        /// </remarks>
        public IntPtr Handle => IntPtr.Zero;

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed) return;
            _disposed = true;
            if (!disposing) return;
            Detach();
            _child = null;
            _container.Dispose();
        }

        // ---- layout ------------------------------------------------------------------------------

        protected override Size MeasureOverride(Size availableSize)
        {
            if (_child == null) return new Size(0, 0);

            // The child's own preferred size, as the original does -- but never the infinity WPF
            // hands out inside a StackPanel/ScrollViewer, which a WinForms control cannot express.
            SD.Size pref;
            try { pref = _child.GetPreferredSize(SD.Size.Empty); }
            catch { pref = _child.Size; }

            double w = double.IsInfinity(availableSize.Width) ? pref.Width : availableSize.Width;
            double h = double.IsInfinity(availableSize.Height) ? pref.Height : availableSize.Height;
            return new Size(Math.Max(0, w), Math.Max(0, h));
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            // Push WPF's arranged size into the hosted window. DIPs and the driver's points are both
            // 96dpi, so this is a straight round -- the DPI scale enters only at composition time.
            int w = Math.Max(1, (int)Math.Round(finalSize.Width));
            int h = Math.Max(1, (int)Math.Round(finalSize.Height));
            if (_container.ClientSize.Width != w || _container.ClientSize.Height != h)
            {
                _container.ClientSize = new SD.Size(w, h);
                _container.PerformLayout();
                _container.Invalidate(true);
            }
            return finalSize;
        }

        // The scene is injected by the compositor, not drawn here -- but WPF routes mouse input only
        // to elements with hit-test geometry, so without this transparent fill every click would pass
        // straight through and never reach the hosted controls.
        protected override void OnRender(DrawingContext dc)
            => dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));

        // ---- input: WPF over this element -> the WinForms driver --------------------------------
        // A WPF point (DIPs, relative to this element = the container's client origin) becomes driver
        // screen coordinates by adding the container's origin.

        private (int X, int Y) ToDriver(Point p)
            => (_formOx + (int)Math.Round(p.X), _formOy + (int)Math.Round(p.Y));

        protected override void OnMouseMove(WpfMouseEventArgs e)
        {
            if (_driver == null) return;
            var (x, y) = ToDriver(e.GetPosition(this));
            _driver.InjectMouseMoveIn(_container.Handle, x, y, _leftDown);
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            if (_driver == null) return;
            Focus();
            CaptureMouse();
            var (x, y) = ToDriver(e.GetPosition(this));
            _leftDown = true;
            _driver.InjectMouseMoveIn(_container.Handle, x, y, false);
            _driver.InjectMouseDownIn(_container.Handle, x, y);
            e.Handled = true;
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            if (_driver == null) return;
            var (x, y) = ToDriver(e.GetPosition(this));
            _leftDown = false;
            _driver.InjectMouseUpIn(_container.Handle, x, y);
            ReleaseMouseCapture();
            e.Handled = true;
        }

        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            if (_driver == null) return;
            var (x, y) = ToDriver(e.GetPosition(this));
            _driver.InjectWheelIn(_container.Handle, x, y, e.Delta > 0 ? 120 : -120);
            e.Handled = true;
        }

        // ---- drag and drop ---------------------------------------------------------------
        //
        // The host is a WPF drop target standing exactly where the hosted controls are drawn, so a
        // drag over it is a drag over them. Each event is handed to the driver, which finds the
        // control under the point and raises the WinForms event on it -- the same routing an
        // injected click takes. The effect the control chooses comes back and becomes the WPF
        // answer, which is what shows the user the right cursor.

        protected override void OnDragEnter(WpfDragEventArgs e)
        {
            if (_driver == null) return;
            var (x, y) = ToDriver(e.GetPosition(this));
            e.Effects = ToWpfEffects(_driver.InjectDragEnter(x, y,
                DragDropData.ToWinForms(e.Data), ToWinFormsEffects(e.AllowedEffects), KeyState(e)));
            e.Handled = true;
        }

        protected override void OnDragOver(WpfDragEventArgs e)
        {
            if (_driver == null) return;
            var (x, y) = ToDriver(e.GetPosition(this));
            e.Effects = ToWpfEffects(_driver.InjectDragOver(x, y,
                DragDropData.ToWinForms(e.Data), ToWinFormsEffects(e.AllowedEffects), KeyState(e)));
            e.Handled = true;
        }

        protected override void OnDragLeave(WpfDragEventArgs e)
        {
            _driver?.InjectDragLeave();
            e.Handled = true;
        }

        protected override void OnDrop(WpfDragEventArgs e)
        {
            if (_driver == null) return;
            var (x, y) = ToDriver(e.GetPosition(this));
            e.Effects = ToWpfEffects(_driver.InjectDragDrop(x, y,
                DragDropData.ToWinForms(e.Data), ToWinFormsEffects(e.AllowedEffects), KeyState(e)));
            e.Handled = true;
        }

        /// <summary>
        /// A hosted control called DoDragDrop. The drag has to be started by WPF, from a WPF element,
        /// so the host starts it on the control's behalf and hands back what the drop decided.
        /// </summary>
        private SWF.DragDropEffects OnHostedControlStartedDrag(object data, SWF.DragDropEffects allowed)
        {
            System.Windows.IDataObject payload = DragDropData.ToWpf(data);
            if (payload == null) return SWF.DragDropEffects.None;

            WpfDragDropEffects performed = System.Windows.DragDrop.DoDragDrop(
                this, payload, ToWpfEffects(allowed));
            return ToWinFormsEffects(performed);
        }

        /// <summary>
        /// The system clipboard, for the WinForms driver. Text only, which is what can honestly
        /// cross a process boundary -- see the note in XplatUIWebGpu.Clipboard.cs.
        /// </summary>
        private sealed class WpfClipboardBridge : SWF.IClipboardBridge
        {
            public bool TryGetText(out string text)
            {
                text = null;
                try
                {
                    if (!System.Windows.Clipboard.ContainsText()) return false;
                    text = System.Windows.Clipboard.GetText();
                    return text != null;
                }
                catch
                {
                    // A clipboard is shared with the rest of the desktop and can fail for reasons
                    // that are nothing to do with this application (another process holding it, no
                    // clipboard at all on a headless box). Paste declining to produce anything is a
                    // far better answer than an exception out of a Ctrl+V.
                    return false;
                }
            }

            public void SetText(string text)
            {
                try { System.Windows.Clipboard.SetText(text); } catch { }
            }

            public bool ContainsText()
            {
                try { return System.Windows.Clipboard.ContainsText(); } catch { return false; }
            }

            public void Clear()
            {
                try { System.Windows.Clipboard.Clear(); } catch { }
            }
        }

        // The two DragDropEffects enums carry the same values (they are both the Win32 DROPEFFECT
        // bits), but they are different types, so the cast has to be written down somewhere.
        private static SWF.DragDropEffects ToWinFormsEffects(WpfDragDropEffects e) => (SWF.DragDropEffects)(int)e;

        private static WpfDragDropEffects ToWpfEffects(SWF.DragDropEffects e) => (WpfDragDropEffects)(int)e;

        /// <summary>
        /// The modifier and button state WinForms drag handlers read, in the Win32 MK_* bits their
        /// KeyState is defined in terms of.
        /// </summary>
        private static int KeyState(WpfDragEventArgs e)
        {
            int state = 0;
            if ((e.KeyStates & WpfDragDropKeyStates.LeftMouseButton) != 0) state |= 0x0001;
            if ((e.KeyStates & WpfDragDropKeyStates.RightMouseButton) != 0) state |= 0x0002;
            if ((e.KeyStates & WpfDragDropKeyStates.ShiftKey) != 0) state |= 0x0004;
            if ((e.KeyStates & WpfDragDropKeyStates.ControlKey) != 0) state |= 0x0008;
            if ((e.KeyStates & WpfDragDropKeyStates.MiddleMouseButton) != 0) state |= 0x0010;
            if ((e.KeyStates & WpfDragDropKeyStates.AltKey) != 0) state |= 0x0020;
            return state;
        }

        protected override void OnTextInput(TextCompositionEventArgs e)
        {
            if (_driver == null) return;
            foreach (char ch in e.Text) _driver.InjectChar(ch);
            e.Handled = true;
        }

        protected override void OnKeyDown(WpfKeyEventArgs e)
        {
            if (_driver == null) return;
            int vk = VirtualKey(e.Key);
            if (vk == 0) return;
            _driver.InjectKeyDown(vk);
            e.Handled = true;
        }

        // WPF Key -> Win32 virtual-key, for the non-text keys WinForms editors act on. (Character
        // input arrives through OnTextInput instead, already composed by the OS keyboard layout.)
        private static int VirtualKey(Key k) => k switch
        {
            Key.Back => 0x08, Key.Tab => 0x09, Key.Enter => 0x0D, Key.Escape => 0x1B, Key.Delete => 0x2E,
            Key.Left => 0x25, Key.Up => 0x26, Key.Right => 0x27, Key.Down => 0x28,
            Key.Home => 0x24, Key.End => 0x23, Key.PageUp => 0x21, Key.PageDown => 0x22,
            _ => 0,
        };

        // ---- the shared frame tick ---------------------------------------------------------------

        /// <summary>
        /// Prepares the process for hosting Windows Forms content in a WPF application.
        /// </summary>
        /// <remarks>
        /// The original enables WPF's message loop to pump the hosted controls: it hosts a child
        /// HWND, so WinForms messages have to be filtered into WPF's dispatcher for a hosted control
        /// to work at all. Applications call it once at startup, before any host exists.
        ///
        /// That bridging is inherent here — the WinForms side is driven by XplatUIWebGpu and
        /// composited into the same WebGPU pass as WPF, so there is no second loop to join. What
        /// remains worth doing eagerly is the process-wide setup a host would otherwise perform
        /// lazily when the first one is created: bring the driver up, and lend it the clipboard
        /// bridge it needs to reach the system clipboard (its assembly cannot see WPF's Clipboard).
        ///
        /// Idempotent, and safe to call before any WindowsFormsHost or WPF window exists — which is
        /// where applications do call it.
        /// </remarks>
        public static void EnableWindowsFormsInterop()
        {
            XplatUIWebGpu.GetInstance();
            XplatUIWebGpu.ClipboardBridge ??= new WpfClipboardBridge();
            StartTopLevelPump();

            // An application that hosts WinForms through its OWN HwndHost subclass never
            // constructs a WindowsFormsHost, so this is the only place the claim handlers would
            // otherwise be installed from.
            ForeignHwndHostContent.Install();
        }


        // A top-level WinForms window in a WPF app has nothing driving it. PresentationHost.Tick is
        // reached from XplatUIWebGpu.GetMessage -- i.e. from a WinForms message loop -- and a WPF
        // app runs the WPF dispatcher instead. Form.ShowDialog happened to work, because a modal
        // dialog runs a WinForms loop of its own; Form.Show did not, and a modeless dialog reported
        // Visible == true while never appearing on screen. Drive the same tick from the dispatcher.
        //
        // The tick is cheap when there is nothing to do: it walks Application.OpenForms, finds no
        // form that wants a window, and returns. It is only started from EnableWindowsFormsInterop,
        // so a pure WinForms app (which drives Tick from its own loop) never gets a second driver.
        private static DispatcherTimer s_topLevelPump;

        private static void StartTopLevelPump()
        {
            if (s_topLevelPump != null) return;
            s_topLevelPump = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(16),   // ~60Hz, the rate the hosts present at
            };
            s_topLevelPump.Tick += (s, e) => SWF.PresentationHost.TickExternal();
            s_topLevelPump.Start();
        }

        private void Attach()
        {
            if (_disposed) return;
            lock (s_lock)
            {
                if (s_hosts.Contains(this)) return;
                s_hosts.Add(this);
                if (!s_ticking)
                {
                    s_ticking = true;
                    CompositionTarget.Rendering += OnRendering;
                }
            }

            _driver = XplatUIWebGpu.GetInstance();
            ForeignHwndHostContent.Install();

            // Drag and drop crosses here in both directions. AllowDrop on the HOST is what makes WPF
            // route drags to this element at all, and a hosted control asking to be a drop target is
            // the only reason to want it; DoDragDrop inside the hosted control comes back out
            // through StartDragRequested, because the drag has to be started by WPF against a WPF
            // element -- the driver has no window of its own to start one from.
            AllowDrop = true;
            _driver.StartDragRequested = OnHostedControlStartedDrag;

            // Copy and paste in a hosted control reach the SYSTEM clipboard the same way: the
            // driver's assembly cannot see WPF's Clipboard, so the host lends it one.
            XplatUIWebGpu.ClipboardBridge ??= new WpfClipboardBridge();

            _container.CreateControl();
            _container.Show();          // registers the window tree with the driver and paints it
            foreach (SWF.Control c in Flatten(_container)) c.Invalidate(true);
            SWF.Application.DoEvents();
        }

        private void Detach()
        {
            bool last;
            lock (s_lock)
            {
                s_hosts.Remove(this);
                last = s_hosts.Count == 0 && s_extraSources.Count == 0;
                if (last && s_ticking)
                {
                    s_ticking = false;
                    CompositionTarget.Rendering -= OnRendering;
                }
            }
            // Nothing of ours is on screen any more; leaving the last scenes registered would freeze
            // a ghost of the control tree over the window.
            if (last)
            {
                EmbeddedContent.Set(null);
                EmbeddedContent.SetCaret(0, 0, 0, 0, false);
            }
        }

        private static void OnRendering(object sender, EventArgs e)
        {
            WindowsFormsHost[] hosts;
            IEmbeddedContentSource[] extras;
            lock (s_lock)
            {
                hosts = s_hosts.ToArray();
                extras = s_extraSources.ToArray();
            }
            if (hosts.Length == 0 && extras.Length == 0) return;

            // One pump for the whole process: this is what advances WinForms layout, paints,
            // timers and the caret blink.
            SWF.Application.DoEvents();

            var items = new List<EmbeddedItem>();
            WindowsFormsHost caretHost = null;
            bool moved = false;
            foreach (WindowsFormsHost h in hosts)
            {
                if (h.Collect(items)) moved = true;
                // The driver has ONE caret, belonging to whatever has focus; mapping it through the
                // wrong host's origin would place it in the wrong window. Keyboard focus is what
                // identifies the right one -- with a single host, it is that host by elimination.
                if (h.IsKeyboardFocusWithin || (hosts.Length == 1 && caretHost == null)) caretHost = h;
            }
            foreach (IEmbeddedContentSource src in extras)
            {
                if (src.Collect(items)) moved = true;
            }
            EmbeddedContent.Set(items);
            caretHost?.PublishCaret();

            // Present-on-change: a WPF frame is only worth forcing when the hosted pixels actually
            // changed (a control repainted) or a host moved under them (a scroll or a splitter drag).
            // Unconditional invalidation here would pin the whole app at full frame rate forever.
            // The driver is process-wide; take it from whichever source exists.
            XplatUIWebGpu driver = hosts.Length > 0 ? hosts[0]._driver : XplatUIWebGpu.GetInstance();
            int version = driver?.GetPaintVersion() ?? 0;
            if (version != s_lastPaintVersion || moved)
            {
                s_lastPaintVersion = version;
                foreach (WindowsFormsHost h in hosts) h.InvalidateVisual();
                foreach (IEmbeddedContentSource src in extras) src.Invalidate();
            }
        }

        /// <summary>Add this host's window scenes, placed at its device rect. Returns true when that
        /// rect changed since the previous frame.</summary>
        private bool Collect(List<EmbeddedItem> into)
        {
            if (_driver == null || !IsVisible) return false;

            PresentationSource src = PresentationSource.FromVisual(this);
            if (src?.CompositionTarget == null || src.RootVisual == null) return false;

            double dpi = src.CompositionTarget.TransformToDevice.M11;
            Point origin;
            try { origin = TransformToAncestor(src.RootVisual).Transform(new Point(0, 0)); }
            catch { return false; }                 // transient, during layout or teardown

            float hostDevX = (float)(origin.X * dpi), hostDevY = (float)(origin.Y * dpi);

            long[] wins = _driver.GetPresentWindows(_container.Handle);
            if (wins == null || wins.Length < 3) return false;
            int ox = (int)wins[1], oy = (int)wins[2];
            _formOx = ox; _formOy = oy;             // input mapping uses the same origin

            for (int i = 0; i + 2 < wins.Length; i += 3)
            {
                IntPtr h = (IntPtr)wins[i];
                object scene = _driver.GetWindowScene(h);
                if (scene == null) continue;
                long packed = _driver.GetWindowSizePacked(h);
                int w = (int)(packed >> 32), ht = (int)(packed & 0xFFFFFFFF);
                var item = new EmbeddedItem
                {
                    Scene = scene,
                    DeviceX = hostDevX + ((int)wins[i + 1] - ox) * (float)dpi,
                    DeviceY = hostDevY + ((int)wins[i + 2] - oy) * (float)dpi,
                    DeviceW = w * (float)dpi,
                    DeviceH = ht * (float)dpi,
                    Scale = (float)dpi,
                    // Tag the window: the registry is process-wide, and without this every WPF
                    // window composited every other one's hosted content.
                    Window = (src as HwndSource)?.Handle ?? IntPtr.Zero,
                };
                // Confine it to this host, the way WPF clips its own content by every ancestor:
                // a hosted control reaching past the host's edge would otherwise be drawn in full,
                // over whatever sits beside it. See ForeignHwndHostContent.ClipToHost.
                float hostW = (float)(RenderSize.Width * dpi), hostH = (float)(RenderSize.Height * dpi);
                float left = Math.Max(item.DeviceX, hostDevX), top = Math.Max(item.DeviceY, hostDevY);
                float right = Math.Min(item.DeviceX + item.DeviceW, hostDevX + hostW);
                float bottom = Math.Min(item.DeviceY + item.DeviceH, hostDevY + hostH);
                if (right <= left || bottom <= top) continue;
                item.ClipX = left - item.DeviceX;
                item.ClipY = top - item.DeviceY;
                item.DeviceW = right - left;
                item.DeviceH = bottom - top;
                into.Add(item);
            }

            bool moved = _lastDevX != hostDevX || _lastDevY != hostDevY
                      || _lastDevW != RenderSize.Width || _lastDevH != RenderSize.Height;
            _lastDevX = hostDevX; _lastDevY = hostDevY;
            _lastDevW = RenderSize.Width; _lastDevH = RenderSize.Height;
            return moved;
        }

        /// <summary>Place the driver's text caret in device pixels, on top of the hosted scenes. The
        /// recorded scenes do not contain it: WinForms drives it out of band through
        /// CreateCaret/SetCaretPos, and its blink is advanced by the pump above.</summary>
        private void PublishCaret()
        {
            if (_driver == null) { EmbeddedContent.SetCaret(0, 0, 0, 0, false); return; }

            PresentationSource src = PresentationSource.FromVisual(this);
            if (src?.CompositionTarget == null || src.RootVisual == null) return;
            double dpi = src.CompositionTarget.TransformToDevice.M11;
            Point origin;
            try { origin = TransformToAncestor(src.RootVisual).Transform(new Point(0, 0)); }
            catch { return; }

            if (_driver.GetCaret(out int cx, out int cy, out int cw, out int ch))
            {
                EmbeddedContent.SetCaret(
                    (float)(origin.X * dpi) + (cx - _formOx) * (float)dpi,
                    (float)(origin.Y * dpi) + (cy - _formOy) * (float)dpi,
                    Math.Max(1, cw) * (float)dpi, ch * (float)dpi, true);
            }
            else EmbeddedContent.SetCaret(0, 0, 0, 0, false);
        }

        private static IEnumerable<SWF.Control> Flatten(SWF.Control c)
        {
            yield return c;
            foreach (SWF.Control child in c.Controls)
                foreach (SWF.Control g in Flatten(child)) yield return g;
        }
    }
}
