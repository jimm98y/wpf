// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Composites content hosted by an HwndHost-derived class.
//
// HwndHost hands its subclass a parent HWND and expects a child HWND back. There are none here, so
// PresentationFramework offers the returned handle to HwndHostForeignContent instead of throwing;
// this is what claims it. The handle is a WinForms control's, minted by XplatUIWebGpu, so the
// content is already a live WinForms tree with its own layout, painting and timers - only its
// PRESENTATION is missing, which is exactly what WindowsFormsHost solves for the case where it owns
// the element. The same collection runs here against the claimed subtree.
//
// This lives in WindowsFormsIntegration because it is the one assembly that sees all three sides:
// WPF (HwndHost), WinForms (the driver) and the WebGPU compositor.
//

using System.Collections.Generic;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using SWF = System.Windows.Forms;

namespace System.Windows.Forms.Integration
{
    internal sealed class ForeignHwndHostContent : IEmbeddedContentSource
    {
        private static readonly object s_lock = new object();
        private static readonly Dictionary<HwndHost, ForeignHwndHostContent> s_claimed
            = new Dictionary<HwndHost, ForeignHwndHostContent>();

        private readonly HwndHost _host;
        private readonly IntPtr _root;
        private readonly XplatUIWebGpu _driver;
        private bool _visible = true;
        private float _lastDevX, _lastDevY, _lastDevW, _lastDevH;

        private ForeignHwndHostContent(HwndHost host, IntPtr root)
        {
            _host = host;
            _root = root;
            _driver = XplatUIWebGpu.GetInstance();
            HookInput();
        }

        /// <summary>
        /// Installs the handlers PresentationFramework calls. Idempotent, and safe before any host
        /// exists - EnableWindowsFormsInterop and the first WindowsFormsHost both reach it.
        /// </summary>
        internal static void Install()
        {
            if (HwndHostForeignContent.Attach != null)
            {
                return;
            }

            HwndHostForeignContent.Attach = OnAttach;
            HwndHostForeignContent.Detach = OnDetach;
            HwndHostForeignContent.SetVisible = OnSetVisible;
            HwndHostForeignContent.SetBounds = OnSetBounds;
        }

        private static bool OnAttach(HwndHost host, IntPtr handle)
        {
            if (Environment.GetEnvironmentVariable("WF_TRACE_WINDOWS") == "1")
                Console.Error.WriteLine($"foreign claim: host={host?.GetType().Name} handle=0x{handle.ToInt64():x} " +
                    $"known={XplatUIWebGpu.GetInstance()?.KnowsWindow(handle)}");

            // Only OUR handles. Anything the driver does not know is a real HWND, or someone else's,
            // and declining lets HwndHost report ChildWindowNotCreated as it always would.
            if (host is null || XplatUIWebGpu.GetInstance()?.KnowsWindow(handle) != true)
            {
                return false;
            }

            // Give the hosted tree its handles. WinForms creates a child's handle lazily -- adding a
            // control to a parent only RE-parents a handle that already exists (Control.ChangeParent),
            // it never makes one -- and Control.CreateControl is what walks a tree creating them. An
            // HwndHost whose BuildWindowCore simply takes its container's Handle creates that one
            // window and no other, so every child stayed handle-less, the driver had no window to
            // paint for any of them, and the host showed just the container's background: a plain
            // Control-coloured rectangle. SharpDevelop hosts all of its WinForms option panels and
            // pads this way.
            SWF.Control hostedRoot = SWF.Control.FromHandle(handle);
            if (hostedRoot != null) hostedRoot.CreateControl();

            // Tell the window hosts that this subtree is composited by WPF, so none of them draws it.
            SWF.PresentationHost.SuppressWindow(handle);

            var claim = new ForeignHwndHostContent(host, handle);
            lock (s_lock)
            {
                if (s_claimed.ContainsKey(host)) return true;
                s_claimed[host] = claim;
            }

            WindowsFormsHost.AddSource(claim);
            return true;
        }

        private static void OnDetach(HwndHost host, IntPtr handle)
        {
            ForeignHwndHostContent claim = null;
            lock (s_lock)
            {
                if (s_claimed.TryGetValue(host, out claim)) s_claimed.Remove(host);
            }

            SWF.PresentationHost.UnsuppressWindow(handle);
            if (claim != null)
            {
                claim.UnhookInput();
                WindowsFormsHost.RemoveSource(claim);
            }
        }

        private static void OnSetVisible(HwndHost host, IntPtr handle, bool visible)
        {
            ForeignHwndHostContent claim;
            lock (s_lock) s_claimed.TryGetValue(host, out claim);
            if (claim != null) claim._visible = visible;
        }

        private static void OnSetBounds(HwndHost host, IntPtr handle, int x, int y, int w, int h)
        {
            // Deliberately ignored. HwndHost computes this rect for SetWindowPos, in the root's
            // device space; the scene positions below are derived from the element's own transform
            // every frame, which stays correct through transforms and DPI changes that a cached
            // rect would not survive. The handler exists so HwndHost has somewhere to send it.
        }

        // WF_TRACE_WINDOWS=1: what this host actually publishes, once.
        private static readonly bool s_trace = Environment.GetEnvironmentVariable("WF_TRACE_WINDOWS") == "1";
        private int _traced = -1;

        private int _ox, _oy;               // the hosted root's origin in the driver's screen space
        private bool _leftDown;

        // ---- input: WPF over the host -> the WinForms driver ------------------------------------
        //
        // A claimed foreign child has no window of its own, so nothing delivers OS input to it: the
        // content was displayed but completely dead -- no click, no tab, no scroll anywhere in a
        // hosted pad. Route WPF's input to the driver the way WindowsFormsHost does for its own
        // hosted controls. (HwndHost.OnRender gives WPF the hit-test geometry to route to.)
        private void HookInput()
        {
            // ...and it has to be able to hold keyboard focus, or typed keys never reach it.
            _host.Focusable = true;

            // A menu is dismissed by clicking ANYWHERE else, including outside this host, where WPF
            // routes the click to some other element and the menu never hears about it. Watch the
            // whole window: on this stack there is no OS-level mouse hook to do it for us, so a
            // context menu stayed on screen for good and every further right-click added another.
            _host.Loaded += OnHostLoaded;
            if (_host.IsLoaded) HookWindow();

            _host.MouseMove += OnHostMouseMove;
            _host.MouseLeftButtonDown += OnHostMouseDown;
            _host.MouseLeftButtonUp += OnHostMouseUp;
            _host.MouseWheel += OnHostMouseWheel;
            _host.MouseRightButtonDown += OnHostRightDown;
            _host.MouseRightButtonUp += OnHostRightUp;
            _host.TextInput += OnHostTextInput;
            _host.KeyDown += OnHostKeyDown;
        }

        private void OnHostLoaded(object sender, RoutedEventArgs e) => HookWindow();

        private System.Windows.Window _window;

        private void HookWindow()
        {
            System.Windows.Window w = System.Windows.Window.GetWindow(_host);
            if (w == null || ReferenceEquals(w, _window)) return;
            if (_window != null) _window.PreviewMouseDown -= OnWindowMouseDown;
            _window = w;
            _window.PreviewMouseDown += OnWindowMouseDown;
        }

        private void OnWindowMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (_driver == null) return;

            // Only the host that owns the open popups closes them, and only for a click that is not
            // inside one -- a click on a menu item must reach the item, not dismiss the menu first.
            if (!ReferenceEquals(s_lastInput, this)) return;

            Point p = e.GetPosition(_host);
            var (x, y) = ToDriver(p);
            HashSet<long> owned = OwnedWindows();
            long[] all = _driver.GetPresentWindows(_root);
            if (all == null) return;

            bool insidePopup = false;
            var popups = new List<IntPtr>();
            for (int i = 0; i + 2 < all.Length; i += 3)
            {
                if (owned.Contains(all[i])) continue;
                popups.Add((IntPtr)all[i]);
                long packed = _driver.GetWindowSizePacked((IntPtr)all[i]);
                int w = (int)(packed >> 32), h = (int)(packed & 0xFFFFFFFF);
                int px = (int)all[i + 1], py = (int)all[i + 2];
                if (x >= px && y >= py && x < px + w && y < py + h) insidePopup = true;
            }
            if (insidePopup || popups.Count == 0) return;

            foreach (IntPtr popup in popups)
            {
                if (SWF.Control.FromHandle(popup) is SWF.ToolStripDropDown drop) drop.Close();
            }
        }

        private void UnhookInput()
        {
            _host.Loaded -= OnHostLoaded;
            if (_window != null) { _window.PreviewMouseDown -= OnWindowMouseDown; _window = null; }

            _host.MouseMove -= OnHostMouseMove;
            _host.MouseLeftButtonDown -= OnHostMouseDown;
            _host.MouseLeftButtonUp -= OnHostMouseUp;
            _host.MouseWheel -= OnHostMouseWheel;
            _host.MouseRightButtonDown -= OnHostRightDown;
            _host.MouseRightButtonUp -= OnHostRightUp;
            _host.TextInput -= OnHostTextInput;
            _host.KeyDown -= OnHostKeyDown;
        }

        // Keyboard, for the same reason as the mouse: a composited child receives no OS input, so
        // arrow keys, typing and Delete did nothing anywhere in a hosted control.
        private void OnHostTextInput(object sender, System.Windows.Input.TextCompositionEventArgs e)
        {
            if (_driver == null) return;
            foreach (char ch in e.Text) _driver.InjectChar(ch);
            e.Handled = true;
        }

        private void OnHostKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (_driver == null) return;
            int vk = VirtualKey(e.Key);
            if (vk == 0) return;
            _driver.InjectKeyDown(vk);
            e.Handled = true;
        }

        // WPF Key -> Win32 virtual-key, for the non-text keys a control acts on. Character input
        // arrives through TextInput instead, already composed by the keyboard layout.
        private static int VirtualKey(System.Windows.Input.Key k)
        {
            switch (k)
            {
                case System.Windows.Input.Key.Back: return 0x08;
                case System.Windows.Input.Key.Tab: return 0x09;
                case System.Windows.Input.Key.Enter: return 0x0D;
                case System.Windows.Input.Key.Escape: return 0x1B;
                case System.Windows.Input.Key.Space: return 0x20;
                case System.Windows.Input.Key.PageUp: return 0x21;
                case System.Windows.Input.Key.PageDown: return 0x22;
                case System.Windows.Input.Key.End: return 0x23;
                case System.Windows.Input.Key.Home: return 0x24;
                case System.Windows.Input.Key.Left: return 0x25;
                case System.Windows.Input.Key.Up: return 0x26;
                case System.Windows.Input.Key.Right: return 0x27;
                case System.Windows.Input.Key.Down: return 0x28;
                case System.Windows.Input.Key.Delete: return 0x2E;
                case System.Windows.Input.Key.F2: return 0x71;
                default: return 0;
            }
        }

        // Give the clicked control WinForms focus. Nothing else does it for composited content, and
        // controls draw themselves differently without it: a TreeView paints its selected node with
        // the plain control colour and its own fore colour rather than the highlight pair, which
        // came out as white text on a white row -- the selection simply vanished.
        private void FocusAt(int x, int y)
        {
            IntPtr hit = TargetAt(x, y);
            if (hit != IntPtr.Zero) _driver.SetFocus(hit);
        }

        // The claim that last received input. A menu or drop-down is a top-level window of the
        // driver, not part of any host's subtree, so no host would publish it and it never appeared
        // -- a right-click opened a context menu that could not be seen. Give those windows to
        // whichever host the user last interacted with, which is the one that opened them.
        private static ForeignHwndHostContent s_lastInput;


        /// <summary>The windows that belong to some claimed host -- everything else on screen is a
        /// menu or drop-down one of them opened.</summary>
        /// <summary>Whether a top-level driver window is a Form, which PresentationHost puts on
        /// screen as a real window of its own. Only menus and drop-downs belong to a host.</summary>
        private static bool IsOwnWindow(IntPtr handle) => SWF.Control.FromHandle(handle) is SWF.Form;

        private HashSet<long> OwnedWindows()
        {
            var owned = new HashSet<long>();
            lock (s_lock)
            {
                foreach (ForeignHwndHostContent claim in s_claimed.Values)
                {
                    long[] mine = _driver.GetSubtreeWindows(claim._root);
                    if (mine == null) continue;
                    for (int i = 0; i + 2 < mine.Length; i += 3) owned.Add(mine[i]);
                }
            }
            return owned;
        }

        /// <summary>
        /// The window a point belongs to: a popup if one is under it, else this host's own content.
        /// </summary>
        /// <remarks>
        /// Popups are drawn on top and must be clickable, or a menu can neither be used nor
        /// dismissed -- it just stayed on screen while every further right-click opened another one
        /// behind it. They are top-level windows, so the driver's global hit-test finds them; the
        /// only thing that has to be excluded is other hosts' content, which overlaps this host's
        /// because every hosted container sits at the driver's origin.
        /// </remarks>
        private IntPtr TargetAt(int x, int y)
        {
            // Look for a popup explicitly rather than trusting the driver's global hit-test. Its
            // answer is ordered by paint order, and a popup has no parent to take a z-order from,
            // so it sorts to the BOTTOM and a hosted container -- every one of which sits at the
            // driver's origin and therefore covers the point -- wins instead. Newest popup first:
            // that is the menu on top.
            long[] all = _driver.GetPresentWindows(_root);
            if (all != null)
            {
                HashSet<long> owned = OwnedWindows();
                for (int i = all.Length - 3; i >= 0; i -= 3)
                {
                    if (owned.Contains(all[i])) continue;
                    if (IsOwnWindow((IntPtr)all[i])) continue;
                    long packed = _driver.GetWindowSizePacked((IntPtr)all[i]);
                    int w = (int)(packed >> 32), h = (int)(packed & 0xFFFFFFFF);
                    int px = (int)all[i + 1], py = (int)all[i + 2];
                    if (x >= px && y >= py && x < px + w && y < py + h) return (IntPtr)all[i];
                }
            }

            return _driver.WindowAtPointIn(_root, x, y);
        }

        private (int X, int Y) ToDriver(Point p) => (_ox + (int)Math.Round(p.X), _oy + (int)Math.Round(p.Y));

        private void OnHostMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (_driver == null) return;
            var (x, y) = ToDriver(e.GetPosition(_host));
            _driver.InjectMouseMoveAt(TargetAt(x, y), x, y, _leftDown);
        }

        private void OnHostMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (_driver == null) return;
            _host.Focus();
            _host.CaptureMouse();
            s_lastInput = this;
            var (x, y) = ToDriver(e.GetPosition(_host));
            FocusAt(x, y);
            if (Environment.GetEnvironmentVariable("WF_TRACE_INPUT") == "1")
            {
                IntPtr hit = _driver.WindowAtPointIn(_root, x, y);
                SWF.Control c = SWF.Control.FromHandle(hit);
                string state = "";
                if (c is SWF.TreeView tv)
                    state = $" focused={tv.Focused} selected='{tv.SelectedNode?.Text}' hideSelection={tv.HideSelection}";
                Console.Error.WriteLine($"foreign click: wpf={e.GetPosition(_host)} origin=({_ox},{_oy}) " +
                    $"driver=({x},{y}) root=0x{_root.ToInt64():x} hit=0x{hit.ToInt64():x} " +
                    $"control={(c == null ? "<none>" : c.GetType().Name)}{state}");
            }
            _leftDown = true;
            IntPtr target = TargetAt(x, y);
            _driver.InjectMouseMoveAt(target, x, y, false);
            _driver.InjectMouseDownAt(target, x, y);
            if (Environment.GetEnvironmentVariable("WF_TRACE_INPUT") == "1")
            {
                SWF.Control after = SWF.Control.FromHandle(target);
                string st = after is SWF.TreeView tv2
                    ? $" focused={tv2.Focused} selected='{tv2.SelectedNode?.Text}'" : "";
                Console.Error.WriteLine($"  after down: target=0x{target.ToInt64():x} " +
                    $"{(after == null ? "<none>" : after.GetType().Name)}{st}");
            }
            e.Handled = true;
        }

        private void OnHostMouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (_driver == null) return;
            var (x, y) = ToDriver(e.GetPosition(_host));
            _leftDown = false;
            _driver.InjectMouseUpAt(TargetAt(x, y), x, y);
            _host.ReleaseMouseCapture();
            e.Handled = true;
        }

        // A context menu needs the right button, which nothing forwarded: right-clicking anywhere in
        // a hosted control did nothing at all.
        private void OnHostRightDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (_driver == null) return;
            _host.Focus();
            s_lastInput = this;
            var (x, y) = ToDriver(e.GetPosition(_host));
            FocusAt(x, y);
            IntPtr rtarget = TargetAt(x, y);
            _driver.InjectMouseMoveAt(rtarget, x, y, false);
            _driver.InjectRightDownAt(rtarget, x, y);
            e.Handled = true;
        }

        private void OnHostRightUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (_driver == null) return;
            var (x, y) = ToDriver(e.GetPosition(_host));
            _driver.InjectRightUpAt(TargetAt(x, y), x, y);
            e.Handled = true;
        }

        private void OnHostMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
        {
            if (_driver == null) return;
            var (x, y) = ToDriver(e.GetPosition(_host));
            _driver.InjectWheelIn(_root, x, y, e.Delta > 0 ? 120 : -120);
            e.Handled = true;
        }

        private string _lastExit;

        private bool TraceExit(string why)
        {
            if (s_trace && _lastExit != why) { _lastExit = why; Console.Error.WriteLine($"foreign host skip: {why}"); }
            return false;
        }


        /// <summary>
        /// Confine one window's item to the host's rectangle.
        /// </summary>
        /// <remarks>
        /// Each window was clipped to its OWN bounds, and nothing clipped to the host's, so a
        /// hosted control reaching past the host's edge was drawn in full and spilled across
        /// whatever sat beside it -- the XPath Query pad's combo box ran out of the pad and over
        /// the pane next to it. WPF clips its own content by every ancestor; hosted content has to
        /// be clipped to its host here, because the compositor draws it on top of the WPF scene
        /// rather than inside that visual tree.
        /// </remarks>
        private static bool ClipToHost(EmbeddedItem item, float hostX, float hostY, float hostW, float hostH)
        {
            float left = Math.Max(item.DeviceX, hostX);
            float top = Math.Max(item.DeviceY, hostY);
            float right = Math.Min(item.DeviceX + item.DeviceW, hostX + hostW);
            float bottom = Math.Min(item.DeviceY + item.DeviceH, hostY + hostH);
            if (right <= left || bottom <= top) return false;      // entirely outside: drop it

            item.ClipX = left - item.DeviceX;
            item.ClipY = top - item.DeviceY;
            item.DeviceW = right - left;
            item.DeviceH = bottom - top;
            return true;
        }


        private void CollectPopups(List<EmbeddedItem> into, float hostDevX, float hostDevY,
            int ox, int oy, double dpi, PresentationSource src)
        {
            long[] all = _driver.GetPresentWindows(_root);
            if (all is null) return;

            // Anything inside a claimed host is somebody's own content, not a popup.
            var owned = new HashSet<long>();
            lock (s_lock)
            {
                foreach (ForeignHwndHostContent claim in s_claimed.Values)
                {
                    long[] mine = _driver.GetSubtreeWindows(claim._root);
                    if (mine == null) continue;
                    for (int i = 0; i + 2 < mine.Length; i += 3) owned.Add(mine[i]);
                }
            }

            for (int i = 0; i + 2 < all.Length; i += 3)
            {
                if (owned.Contains(all[i])) continue;
                var h = (IntPtr)all[i];
                if (IsOwnWindow(h)) continue;          // a Form; PresentationHost gives it a real window
                object scene = _driver.GetWindowScene(h);
                if (scene is null) continue;

                long packed = _driver.GetWindowSizePacked(h);
                int w = (int)(packed >> 32), ht = (int)(packed & 0xFFFFFFFF);
                if (w <= 0 || ht <= 0) continue;

                into.Add(new EmbeddedItem
                {
                    Scene = scene,
                    DeviceX = hostDevX + ((int)all[i + 1] - ox) * (float)dpi,
                    DeviceY = hostDevY + ((int)all[i + 2] - oy) * (float)dpi,
                    DeviceW = w * (float)dpi,
                    DeviceH = ht * (float)dpi,
                    Scale = (float)dpi,
                    Window = (src as HwndSource)?.Handle ?? IntPtr.Zero,
                });
            }
        }

        public bool Collect(List<EmbeddedItem> into)
        {
            if (_driver is null || !_visible || !_host.IsVisible)
                return TraceExit($"driver={_driver != null} visible={_visible} hostVisible={_host?.IsVisible}");

            PresentationSource src = PresentationSource.FromVisual(_host);
            if (src?.CompositionTarget is null || src.RootVisual is null)
                return TraceExit("no presentation source");

            double dpi = src.CompositionTarget.TransformToDevice.M11;
            Point origin;
            try { origin = _host.TransformToAncestor(src.RootVisual).Transform(new Point(0, 0)); }
            catch { return false; }                 // transient, during layout or teardown

            float hostDevX = (float)(origin.X * dpi), hostDevY = (float)(origin.Y * dpi);

            // Only this host's subtree: with several hosts, GetPresentWindows would hand each of
            // them every other host's windows as well.
            long[] wins = _driver.GetSubtreeWindows(_root);
            if (wins is null || wins.Length < 3)
                return TraceExit($"empty subtree for root 0x{_root.ToInt64():x} " +
                    $"({(SWF.Control.FromHandle(_root)?.GetType().Name ?? "<none>")})");

            int ox = (int)wins[1], oy = (int)wins[2];
            _ox = ox; _oy = oy;                 // input maps through the same origin; see OnHostMouseDown
            if (s_trace && _traced != wins.Length)
            {
                _traced = wins.Length;
                Console.Error.WriteLine($"foreign host root=0x{_root.ToInt64():x} windows={wins.Length / 3}");
                SWF.Control rootControl = SWF.Control.FromHandle(_root);
                if (rootControl != null)
                {
                    Console.Error.WriteLine($"   winforms children of {rootControl.GetType().Name}: {rootControl.Controls.Count}");
                    foreach (SWF.Control kid in rootControl.Controls)
                        Console.Error.WriteLine($"     {kid.GetType().Name} '{kid.Name}' {kid.Bounds} " +
                            $"visible={kid.Visible} handleCreated={kid.IsHandleCreated} " +
                            $"handle=0x{(kid.IsHandleCreated ? kid.Handle.ToInt64() : 0):x} children={kid.Controls.Count}");
                }
                for (int j = 0; j + 2 < wins.Length; j += 3)
                {
                    var wh = (IntPtr)wins[j];
                    SWF.Control wc = SWF.Control.FromHandle(wh);
                    Console.Error.WriteLine($"   0x{wins[j]:x} at ({wins[j + 1]},{wins[j + 2]}) " +
                        $"{(wc == null ? "<none>" : wc.GetType().Name + " '" + wc.Name + "' " + wc.Bounds + " visible=" + wc.Visible)} " +
                        $"scene={(_driver.GetWindowScene(wh) == null ? "null" : "ok")}");
                }
            }
            for (int i = 0; i + 2 < wins.Length; i += 3)
            {
                IntPtr h = (IntPtr)wins[i];
                object scene = _driver.GetWindowScene(h);
                if (scene is null) continue;

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
                if (ClipToHost(item, hostDevX, hostDevY,
                        (float)(_host.RenderSize.Width * dpi), (float)(_host.RenderSize.Height * dpi)))
                    into.Add(item);
            }

            // Menus and drop-downs: top-level windows of the driver that belong to no host's subtree.
            // Publish them from whichever host the user last touched, unclipped -- a menu is meant to
            // escape the control that opened it -- and after the host's own content so they sit on
            // top.
            if (ReferenceEquals(s_lastInput, this)) CollectPopups(into, hostDevX, hostDevY, ox, oy, dpi, src);

            bool moved = _lastDevX != hostDevX || _lastDevY != hostDevY
                      || _lastDevW != _host.RenderSize.Width || _lastDevH != _host.RenderSize.Height;
            _lastDevX = hostDevX; _lastDevY = hostDevY;
            _lastDevW = (float)_host.RenderSize.Width; _lastDevH = (float)_host.RenderSize.Height;
            return moved;
        }

        public void Invalidate() => _host.InvalidateVisual();
    }
}
