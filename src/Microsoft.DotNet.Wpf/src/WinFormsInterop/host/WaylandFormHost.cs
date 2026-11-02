// The Linux head's window for a top-level form: a Wayland toplevel of its own, made by the same window
// layer as WPF's windows on that head (MS.Internal.Interop.Wayland.WaylandWindow), as CocoaHost gives a
// form an NSWindow of its own on macOS. The present path is the one every host shares (the driver's
// scenes through WgpuPresenter); the surface is the toplevel's wl_surface, and the input is the seat's.
//
// THE FRAME. The driver gives a form Windows 11's non-client frame (XplatUIWebGpu.TopLevelFrame), and the
// toplevel's content is the form's CLIENT area: GNOME's headerbar is the frame on screen, as macOS's
// title bar is (CocoaHost). A form that takes the non-client area over (WM_NCCALCSIZE: DotNetBar's ribbon
// form) or has no border is a chromeless toplevel -- libdecor's headerbar hidden, the form's own picture
// the whole window -- moved and sized through the compositor from what WM_NCHITTEST says is under a press
// (WaylandWindow.NonClientPress).
//
// SCREEN COORDINATES. Wayland tells a client nowhere where its toplevel is and lets it put one nowhere;
// WaylandWindow keeps a virtual screen in device pixels in which every window has a remembered origin.
// The forms' screen is that space at the driver's scale: a form's client origin there is the toplevel's
// client origin, so a menu the driver opens at a point of the screen lands at that point of the window.
//
// POPUPS of the driver -- menus, drop-downs, their shadows -- go to a popup layer: an xdg_popup of the
// form's toplevel over the rectangle they cover, see-through around them, so one that reaches past the
// form's edge is seen past it, as on Windows (CocoaHost's popup layer). The pointer over the layer is the
// form's, at the point of the screen it is over. A form of a thread that does not read the compositor
// connection has its popups drawn into its own surface, cut at its edge.
//
// A hosted WPF tree (ElementHost) is composited into the form's frame, and its window -- which the
// compositor never shows, so never sends anything -- gets the pointer and keys that land over it from
// here, as the OS gives a child window its input on Windows.

extern alias WindowsBase;

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.Versioning;
using WaylandWindow = WindowsBase::MS.Internal.Interop.Wayland.WaylandWindow;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using Microsoft.Wpf.Interop.WebGpu.Composition.Platform;

namespace System.Windows.Forms
{
	[SupportedOSPlatform ("linux")]
	internal sealed class WaylandFormHost : IWinFormsHost
	{
		private readonly Form _form;
		private readonly XplatUIWebGpu _driver = XplatUIWebGpu.GetInstance ();
		private readonly System.Diagnostics.Stopwatch _blink = System.Diagnostics.Stopwatch.StartNew ();
		private WaylandWindow _window;
		private WgpuPresenter _wgpu;
		// device pixels per driver unit; and driver units per logical pixel -- 1 for an application laid
		// out at 96 dpi, the screen's scale for a per-monitor one, which lays out in device pixels
		private double _scale = 1, _k = 1;
		private int _lastVer = -1, _surfaceW, _surfaceH;
		private bool _lastCaretOn, _lastPresentOk, _closed, _chromeless;
		private int _lastCursor = int.MinValue;
		private Size _sizedTo;                 // the client size the toplevel was last given (driver units)
		// The left button as the seat has it, not as each window saw it (BrowserFormHost._leftDown).
		private static bool s_leftDown;
		// a hosted WPF window that took the press: it has the pointer until the release, as a child
		// window that has the capture keeps it on Windows
		private static IntPtr s_pointerOwner;
		private static IntPtr s_keyboardOwner;  // a hosted WPF window that took the keys with a press

		private static readonly List<WaylandFormHost> s_hosts = new List<WaylandFormHost> ();
		private static readonly bool s_trace = Environment.GetEnvironmentVariable ("WF_TRACE_WAYLAND") == "1";

		private static bool PerMonitor => ScaleHelper.IsDeviceScaled && ScaleHelper.HostSourceDpi == 0;

		// ---- the screen ----------------------------------------------------------------------

		/// <summary>Called by the driver as it starts: WinForms' screen is the primary output.</summary>
		internal static void InstallScreens ()
		{
			XplatUIWebGpu.ScreensProvider ??= OutputScreens;
		}

		private static Screen[] OutputScreens ()
		{
			WaylandWindow.GetPrimaryScreenPixels (out int left, out int top, out int right, out int bottom, out _, out _, out _, out _);
			double perDevice = PerMonitor || ScaleHelper.HostSourceDpi <= 0 ? 1 : 96.0 / ScaleHelper.HostSourceDpi * WaylandWindow.PrimaryScale ();
			var bounds = new Rectangle ((int)Math.Round (left / perDevice), (int)Math.Round (top / perDevice),
				Math.Max (1, (int)Math.Round ((right - left) / perDevice)), Math.Max (1, (int)Math.Round ((bottom - top) / perDevice)));
			return new[] { new Screen (true, "DISPLAY1", bounds, bounds) };
		}

		/// <summary>The screen's DPI as Windows would give it: 96 per logical pixel.</summary>
		internal static int ScreenDpi ()
		{
			try {
				double scale = WaylandWindow.PrimaryScale ();
				return scale > 0 ? (int)Math.Round (96 * scale) : 0;
			} catch (Exception) {
				return 0;   // no compositor to ask
			}
		}

		static WaylandFormHost ()
		{
			XplatUIWebGpu.WindowStateRequested ??= ApplyWindowState;
			// DefWindowProc's move and size loops (SC_MOVE, SC_SIZE, a press on a caption or a sizing edge
			// left to the default): the compositor's interactive move and resize, from the press in hand
			XplatUIWebGpu.BeginWindowMove ??= h => HostOf (h)?.BeginCompositorMove ();
			XplatUIWebGpu.BeginWindowSize ??= (h, edge) => HostOf (h)?.BeginCompositorSize (edge);
			// WindowFromPoint over a form finds the hosted WPF window under the point, its child
			WaylandWindow.ChildWindowAt ??= (top, x, y) => HostOfWindow (top)?.HostedWindowAt (x, y) ?? IntPtr.Zero;
		}

		private static WaylandFormHost HostOf (IntPtr formHandle)
		{
			lock (s_hosts)
				foreach (WaylandFormHost h in s_hosts)
					if (!h._closed && h._ready && h._form.IsHandleCreated && h._form.Handle == formHandle)
						return h;
			return null;
		}

		private static WaylandFormHost HostOfWindow (IntPtr window)
		{
			lock (s_hosts)
				foreach (WaylandFormHost h in s_hosts)
					if (!h._closed && h._ready && h._window != null && h._window.Handle == window && !h._form.InvokeRequired)
						return h;
			return null;
		}

		private void BeginCompositorMove () => OnPump (() => { if (!_closed) _window.BeginMoveDrag (); });

		/// <param name="edge">WMSZ_LEFT (1) .. WMSZ_BOTTOMRIGHT (8): the hit-test code less 9.</param>
		private void BeginCompositorSize (int edge)
		{
			Size min = _form.MinimumSize, max = _form.MaximumSize;
			int minW = (int)Math.Round (min.Width * _scale), minH = (int)Math.Round (min.Height * _scale);
			int maxW = (int)Math.Round (max.Width * _scale), maxH = (int)Math.Round (max.Height * _scale);
			OnPump (() => { if (!_closed) _window.BeginResizeDrag (edge + 9, minW, minH, maxW, maxH); });
		}

		/// <summary>Form.WindowState was set (its own minimize and maximize buttons, SC_RESTORE): the
		/// toplevel is minimized, maximized or restored by the compositor, as Windows does it to the window.
		/// Nothing listened here, so those buttons changed only the form's idea of itself -- a restore put
		/// the form back to its restore bounds inside a still maximized window, framed in white.</summary>
		private static void ApplyWindowState (IntPtr formHandle, FormWindowState state)
		{
			if (s_trace) Console.WriteLine ($"[wayland form] window 0x{formHandle:x}: state {state} requested");
			WaylandFormHost host = null;
			lock (s_hosts)
				foreach (WaylandFormHost h in s_hosts)
					if (!h._closed && h._form.IsHandleCreated && h._form.Handle == formHandle) { host = h; break; }
			if (host == null || !host._ready)
				return;   // the window, when it is made, takes the state the form has then (MakeWindow)
			int sw = state switch { FormWindowState.Minimized => 6, FormWindowState.Maximized => 3, _ => 9 };
			if (s_trace) Console.WriteLine ($"[wayland form] '{host._form.Text}' state {state} requested");
			OnPump (() => { if (!host._closed) host._window.SetWindowState (sw); });
		}

		private FormWindowState _reportedState = FormWindowState.Normal;

		/// <summary>The window's state as the compositor has it, for the driver (Form.WindowState): a
		/// caption double-click maximizes there, and a window back from the dock is no longer minimized.</summary>
		private void ReportWindowState ()
		{
			if (_window == null || !_ready || _form.IsDisposed || !_form.IsHandleCreated)
				return;
			FormWindowState state = _window.GetWindowState () == 3 ? FormWindowState.Maximized : FormWindowState.Normal;
			if (state == _reportedState && _driver.GetWindowState (_form.Handle) == state)
				return;
			_reportedState = state;
			_driver.HostWindowState (_form.Handle, state);
		}

		internal WaylandFormHost (Form form)
		{
			_form = form;
			PresentationHost.Attach (this, form);
		}

		private Padding Frame => _form.IsHandleCreated ? _driver.FrameOf (_form.Handle) : Padding.Empty;

		/// <summary>
		/// What lies between the form's bounds and the window's content: the frame Windows gives the form
		/// (the headerbar is its frame here), none for a chromeless window -- except maximized. A maximized
		/// window that draws its own frame is, on Windows, the work area grown by the sizing frame
		/// (SM_CXFRAME + SM_CXPADDEDBORDER) on every side, which lies off the screen; the form pads its
		/// content by it (DotNetBar's ribbon form does). The compositor makes the window the screen exactly,
		/// so the form is that much bigger than it and the window shows what is inside -- the padding was a
		/// white border all round (CocoaHost.Maximize's overhang).
		/// </summary>
		private const int MaximizedOverhang = 4 + 4;   // SM_CXFRAME + SM_CXPADDEDBORDER at 96 dpi

		private Padding Inset
		{
			get {
				if (!_chromeless)
					return Frame;
				if (_window == null || !_ready || _popup || _form.FormBorderStyle == FormBorderStyle.None || _window.GetWindowState () != 3)
					return Padding.Empty;
				int o = (int)Math.Round (MaximizedOverhang * _k);   // in the form's units: 96-dpi ones scaled as it lays out
				return new Padding (o);
			}
		}

		/// <summary>The form's client area on the screen (driver units): what the toplevel's content shows.</summary>
		private Rectangle ClientScreen
		{
			get {
				Padding f = Inset;
				Rectangle b = _form.Bounds;
				return new Rectangle (b.X + f.Left, b.Y + f.Top, Math.Max (1, b.Width - f.Horizontal), Math.Max (1, b.Height - f.Vertical));
			}
		}

		// ---- the compositor's thread ------------------------------------------------------------
		//
		// One thread reads and dispatches the compositor connection: the thread that started the driver,
		// the app's main thread (XplatUIWebGpu's constructor claims it). A form of another thread -- a
		// splash screen of its own -- has its window made and changed there: the work is queued, and the
		// main thread picks it up at its next call into the driver or its next idle wait (DrainPumpWork),
		// as iOS's main thread does (UIKitFormHost.OnMain). Its picture is presented from its own thread;
		// the window's events come on the main thread and are handed to it.

		private static readonly System.Collections.Concurrent.ConcurrentQueue<Action> s_pumpWork = new ();
		private volatile bool _ready;   // the window exists (made on the compositor's thread)

		private static void OnPump (Action action)
		{
			if (WaylandWindow.IsUiThread) { action (); return; }
			s_pumpWork.Enqueue (action);
		}

		/// <summary>The calling thread (the driver's, the app's main thread) reads the compositor connection.</summary>
		internal static void ClaimThread () => WaylandWindow.ClaimUiThread ();

		/// <summary>On the compositor's thread, runs the window work other threads have queued for it.</summary>
		internal static void DrainPumpWork ()
		{
			if (s_pumpWork.IsEmpty || !WaylandWindow.IsUiThread) return;
			while (s_pumpWork.TryDequeue (out Action work)) {
				try { work (); }
				catch (Exception e) { Console.Error.WriteLine ($"[WaylandFormHost] window work failed: {e}"); }
			}
		}

		public void Show ()
		{
			if (s_trace) Console.WriteLine ($"[wayland form] show '{_form.Text}' {_form.Bounds} border={_form.FormBorderStyle} thread={Environment.CurrentManagedThreadId} pump={WaylandWindow.IsUiThread}");
			_window = new WaylandWindow ();
			// no border, or a frame the form draws itself: a toplevel without libdecor's
			_chromeless = _form.FormBorderStyle == FormBorderStyle.None || (Frame == Padding.Empty && _form.FormBorderStyle != FormBorderStyle.None);
			double backing;
			try {
				backing = Math.Max (0.5, WaylandWindow.PrimaryScale ());
			} catch (Exception) {
				backing = 1;
			}
			_k = PerMonitor ? _driver.WindowDpi (_form.Handle) / 96.0 : ScaleHelper.HostSourceDpi / 96.0;
			if (_k <= 0) _k = 1;
			_scale = backing / _k;
			Rectangle client = ClientScreen;
			string title = _form.Text;
			int ox = (int)Math.Round (_form.Left * _scale), oy = (int)Math.Round (_form.Top * _scale);
			int ow = Math.Max (1, (int)Math.Round (_form.Width * _scale)), oh = Math.Max (1, (int)Math.Round (_form.Height * _scale));
			// A borderless form over the app's window -- a docking splitter's drag bar, a floating hint -- is
			// a popup of that window: placed where the form says (a toplevel cannot be placed on Wayland,
			// and GNOME put the bar wherever it liked) and never activated, so it does not take the keyboard
			// from the window it is drawn over. The first window (a splash screen) has nothing to belong to.
			WaylandFormHost over = _form.FormBorderStyle == FormBorderStyle.None ? OwnerHost () : null;
			IntPtr owner = over?._window?.Handle ?? IntPtr.Zero;
			if (owner != IntPtr.Zero)
				_popup = true;
			// The window a dialog belongs to: its Owner, or for a modal dialog with none the active form's,
			// as ShowDialog takes the active window for its owner on Windows
			IntPtr parent = IntPtr.Zero;
			if (owner == IntPtr.Zero) {
				WaylandFormHost of = HostOfForm (_form.Owner) ?? (_form.Modal ? OwnerHost () : null);
				parent = of?._window?.Handle ?? IntPtr.Zero;
			}
			// a window the app put at the centre of the screen opens there (a toplevel cannot be placed;
			// the compositor centres it -- WaylandWindow.CenterWhenMapped)
			if (owner == IntPtr.Zero && parent == IntPtr.Zero && CenteredOnScreen ())
				_window.CenterWhenMapped = true;
			OnPump (() => MakeWindow (title, ox, oy, ow, oh, client.Size, owner, parent));
			_form.VisibleChanged += OnVisibleChanged;
			_form.HandleDestroyed += OnHandleDestroyed;
			_form.LocationChanged += OnFormMoved;
			_form.SizeChanged += OnFormSized;
			_form.TextChanged += OnFormText;
			WaylandWindow.MouseInput += OnMouse;
			WaylandWindow.KeyInput += OnKey;
			lock (s_hosts) s_hosts.Add (this);
		}

		/// <summary>The form is where StartPosition.CenterScreen puts it: the middle of the screen's
		/// working area (or of its bounds), give or take a pixel of rounding.</summary>
		private bool CenteredOnScreen ()
		{
			if (_form.StartPosition == FormStartPosition.CenterScreen)
				return true;
			Screen screen = Screen.FromRectangle (_form.Bounds);
			Point c = new Point (_form.Left + _form.Width / 2, _form.Top + _form.Height / 2);
			foreach (Rectangle area in new[] { screen.WorkingArea, screen.Bounds }) {
				Point m = new Point (area.Left + area.Width / 2, area.Top + area.Height / 2);
				if (Math.Abs (c.X - m.X) <= 1 && Math.Abs (c.Y - m.Y) <= 1)
					return true;
			}
			return false;
		}

		/// <summary>The toplevel, on the compositor's thread; then the form takes its place, on its own.</summary>
		private void MakeWindow (string title, int x, int y, int width, int height, Size client, IntPtr owner, IntPtr parent)
		{
			if (_closed)
				return;
			try {
				// the outer rectangle in device pixels, as WaylandWindow takes it; the content is then set to
				// the client exactly
				if (owner != IntPtr.Zero)
					_window.Create (title, x, y, width, height, borderless: true, owner);
				else
					_window.Create (title, x, y, width, height, borderless: false, parent);
			} catch (Exception e) {
				Console.Error.WriteLine ($"WaylandFormHost: '{title}' has no window: {e.Message}");
				_closed = true;
				return;
			}
			if (_chromeless && !_popup)
				_window.SetChromeless (true);
			_sizedTo = client;
			_window.SetContentSize (Math.Max (1, (int)Math.Round (client.Width / _k)), Math.Max (1, (int)Math.Round (client.Height / _k)));
			// the state the form was given before it had a window (an app that starts maximized)
			FormWindowState initial = _driver.GetWindowState (_form.Handle);
			if (initial == FormWindowState.Maximized)
				_window.SetWindowState (3);
			// No NonClientHitTest for the window layer: a press on the frame goes to the form as Windows
			// sends it (WM_NCLBUTTONDOWN, OnNonClient), which handles its own caption buttons there and leaves
			// the rest to DefWindowProc -- whose move and size come back here (BeginWindowMove/Size).
			_window.Resized += OnResized;
			_window.Closed += OnCloseRequested;
			_window.ActiveChanged += OnActiveChanged;
			EmbeddedScenes.PublishHostWindow (_window.Handle, (float)_scale, _form);
			bool active = _window.IsActive;
			_ready = true;
			Action placed = () => {
				SyncFromWindow ();
				if (active)
					OnActiveChanged (true);
				Present ();
			};
			if (_form.InvokeRequired) {
				if (_form.IsHandleCreated) _form.BeginInvoke (placed);
			} else {
				placed ();
			}
		}

		/// <summary>The toplevel's content to the form's client size (driver units), in logical pixels.</summary>
		private void SizeToClient (Size client)
		{
			if (_window == null || !_ready || client == _sizedTo)
				return;
			_sizedTo = client;
			int w = Math.Max (1, (int)Math.Round (client.Width / _k)), h = Math.Max (1, (int)Math.Round (client.Height / _k));
			OnPump (() => { if (!_closed) _window.SetContentSize (w, h); });
		}

		/// <summary>The form takes the place its window is in: Wayland's virtual screen, at the driver's scale.</summary>
		private void SyncFromWindow ()
		{
			if (_window == null || !_ready || _form.IsDisposed || !_form.IsHandleCreated)
				return;
			_window.GetClientScreenOriginPixels (out int px, out int py);
			Padding f = Inset;
			int x = (int)Math.Round (px / _scale) - f.Left, y = (int)Math.Round (py / _scale) - f.Top;
			if (_form.Left != x || _form.Top != y)
				XplatUI.SetWindowPos (_form.Handle, x, y, _form.Width, _form.Height);
		}

		/// <summary>The app moved the form: the toplevel's remembered origin follows (the compositor places it).</summary>
		private void OnFormMoved (object sender, EventArgs e)
		{
			if (_closed || _window == null || !_ready || _form.IsDisposed || !_form.IsHandleCreated)
				return;
			Rectangle client = ClientScreen;
			int tx = (int)Math.Round (client.X * _scale), ty = (int)Math.Round (client.Y * _scale);
			OnPump (() => {
				if (_closed) return;
				_window.GetClientScreenOriginPixels (out int cx, out int cy);
				if (tx == cx && ty == cy) return;
				((WindowsBase::MS.Internal.Interop.IPlatformWindow)_window).GetWindowScreenOriginPixels (out int wx, out int wy);
				_window.SetFrameOrigin (wx + tx - cx, wy + ty - cy);
			});
			_lastVer = -1;
		}

		private void OnFormSized (object sender, EventArgs e)
		{
			if (_closed || _window == null || _form.IsDisposed)
				return;
			SizeToClient (ClientScreen.Size);
			_lastVer = -1;
		}

		private void OnFormText (object sender, EventArgs e)
		{
			string title = _form.Text;
			if (_window != null && _ready && !_closed)
				OnPump (() => { if (!_closed) WaylandWindow.SetTitle (_window.Handle, title); });
		}

		/// <summary>The compositor resized the window (a drag on its border, maximize): the form follows.</summary>
		private void OnResized (int logicalW, int logicalH)
		{
			if (_closed || _form.IsDisposed || !_form.IsHandleCreated)
				return;
			if (_form.InvokeRequired) {
				_form.BeginInvoke ((Action)(() => OnResized (logicalW, logicalH)));
				return;
			}
			// The compositor sends a size for each step of a drag and shows the window at that size once a
			// buffer of it is committed. So each step commits the last frame at the new size at once -- the
			// edge follows the pointer, as a native window's does -- and the form is laid out and drawn at
			// the latest size as soon as the one before is done (Present): sizes that arrive meanwhile are
			// folded into one. Held back until the drag paused (or every half second), the content moved in
			// jumps while the edge moved smoothly.
			_pendingW = logicalW;
			_pendingH = logicalH;
			if (_resizing)
				return;
			if (_wgpu != null && _ready) {
				var client = new Size ((int)Math.Round (logicalW * _k), (int)Math.Round (logicalH * _k));
				if (client != _form.ClientSize && client.Width > 0 && client.Height > 0)
					_wgpu.PresentLastAtSize (client.Width, client.Height);
			}
		}

		/// <summary>The size the compositor gave last, laid out and drawn, from the idle frame.</summary>
		private void ApplyPendingSize ()
		{
			if (_pendingW == 0 || _resizing)
				return;
			_resizing = true;
			try {
				while (_pendingW != 0) {
					int pw = _pendingW, ph = _pendingH;
					_pendingW = _pendingH = 0;
					ResizeTo (pw, ph);
				}
			} finally {
				_resizing = false;
			}
		}

		private bool _resizing;
		private int _pendingW, _pendingH;

		private void ResizeTo (int logicalW, int logicalH)
		{
			ReportWindowState ();
			var client = new Size ((int)Math.Round (logicalW * _k), (int)Math.Round (logicalH * _k));
			_sizedTo = client;
			Padding f = Inset;
			int w = client.Width + f.Horizontal, h = client.Height + f.Vertical;
			long t0 = System.Diagnostics.Stopwatch.GetTimestamp ();
			bool changed = w != _form.Width || h != _form.Height;
			if (changed)
				XplatUI.SetWindowPos (_form.Handle, _form.Left, _form.Top, w, h);
			SyncFromWindow ();
			_lastVer = -1;
			if (!changed)
				return;
			// A frame at the new size now, as Windows' sizing loop paints between moves: the compositor
			// shows the window at the size it asked for only once a buffer of that size is committed, and
			// waiting for the next idle tick -- after every other message -- made the edge trail the
			// pointer. The messages the form posted to itself first (DotNetBar lays its ribbon out from
			// one), then the embedded trees' frames, then the present.
			long t1 = System.Diagnostics.Stopwatch.GetTimestamp ();
			if (!s_inResize) {
				s_inResize = true;
				try { Application.DoEvents (); } finally { s_inResize = false; }
			}
			long t2 = System.Diagnostics.Stopwatch.GetTimestamp ();
			EmbeddedScenes.Pump ();
			long t3 = System.Diagnostics.Stopwatch.GetTimestamp ();
			Present ();
			if (s_traceResize) {
				long t4 = System.Diagnostics.Stopwatch.GetTimestamp ();
				static double Ms (long a, long b) => (b - a) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
				Console.WriteLine ($"[resize] {logicalW}x{logicalH} layout={Ms (t0, t1):F1}ms events={Ms (t1, t2):F1}ms embedded={Ms (t2, t3):F1}ms present={Ms (t3, t4):F1}ms [configure {_wgpu?.LastConfigureMs:F1} ms; {_wgpu?.LastTiming}]");
			}
		}

		[ThreadStatic] private static bool s_inResize;
		private static readonly bool s_traceResize = Environment.GetEnvironmentVariable ("WF_TRACE_RESIZE") == "1";

		/// <summary>The headerbar's close button, or the compositor's close: SC_CLOSE, as Windows' does.</summary>
		private void OnCloseRequested (IntPtr handle)
		{
			const int SC_CLOSE = 0xF060;
			if (_form.InvokeRequired) {
				if (!_closed && _form.IsHandleCreated) _form.BeginInvoke ((Action)(() => OnCloseRequested (handle)));
				return;
			}
			if (!_closed && _form.IsHandleCreated && !_form.IsDisposed)
				XplatUI.SendMessage (_form.Handle, Msg.WM_SYSCOMMAND, (IntPtr)SC_CLOSE, IntPtr.Zero);
		}

		/// <summary>The compositor's active window is Windows' active window: WM_ACTIVATE, Form.Activated.</summary>
		private void OnActiveChanged (bool active)
		{
			if (_closed || !_form.IsHandleCreated || _form.IsDisposed)
				return;
			if (_form.InvokeRequired) {
				_form.BeginInvoke ((Action)(() => OnActiveChanged (active)));
				return;
			}
			if (s_trace) Console.WriteLine ($"[wayland form] '{_form.Text}' active={active}");
			if (active) {
				s_activeHost = this;
				ReportWindowState ();   // back from the dock: not minimized any more
			}
			_driver.HostActivate ((long)_form.Handle, active);
			if (!active)
				s_keyboardOwner = IntPtr.Zero;
			Repaint ();
		}

		/// <summary>WM_NCHITTEST at a point of the virtual screen (device pixels), for a press on a
		/// chromeless toplevel: the form's answer, as Windows asks it.</summary>
		private int NonClientHitTest (int x, int y)
		{
			// a form of another thread is asked nothing from here: its press is its client's
			if (_closed || !_form.IsHandleCreated || _form.IsDisposed || _form.InvokeRequired)
				return 1;
			int sx = (int)Math.Floor (x / _scale), sy = (int)Math.Floor (y / _scale);
			IntPtr lParam = (IntPtr)(((sy & 0xFFFF) << 16) | (sx & 0xFFFF));
			return (int)(long)XplatUI.SendMessage (_form.Handle, Msg.WM_NCHITTEST, IntPtr.Zero, lParam);
		}

		private void OnVisibleChanged (object sender, EventArgs e)
		{
			if (_closed || _window == null || !_ready)
				return;
			bool visible = _form.Visible;
			OnPump (() => { if (!_closed) _window.SetVisible (visible); });
			_lastVer = -1;
			if (_form.Visible)
				Present ();
		}

		private void OnHandleDestroyed (object sender, EventArgs e)
		{
			Close ();
			PresentationHost.Detach (this);
		}

		// ---- present -------------------------------------------------------------------------

		private bool EnsurePresenter ()
		{
			if (!_window.IsMapped) {
				// no role (a popup whose owner went): nothing may be presented into it, EGL aborts
				_wgpu?.Dispose ();
				_wgpu = null;
				return false;
			}
			if (_wgpu != null)
				return true;
			var ctx = ProcessGpu.Acquire ();
			if (s_trace) Console.WriteLine ($"[wayland form] '{_form.Text}' presenter on thread {Environment.CurrentManagedThreadId} for surface 0x{_window.Handle:x} chromeless={_chromeless}");
			IntPtr surface = NativePlatform.CreateWindowSurface (ctx.Instance, _window.Handle);
			if (surface == IntPtr.Zero)
				return false;
			Rectangle client = ClientScreen;
			_surfaceW = client.Width;
			_surfaceH = client.Height;
			_wgpu = new WgpuPresenter (ctx, surface, _surfaceW, _surfaceH, _scale, srgb: true, threadDevice: true);
			if (IsLayered ()) {
				// a layered window (a splash with soft corners) is see-through where its picture is, as
				// CocoaHost's: cleared to nothing, and the surface keeps the alpha the scene leaves in it
				_wgpu.SetTransparent (true);
			} else {
				Color bc = _form.BackColor;
				_wgpu.ClearColor = new RgbaColor (SrgbToLinear (bc.R / 255f), SrgbToLinear (bc.G / 255f), SrgbToLinear (bc.B / 255f), 1f);
			}
			_lastVer = -1;
			if (s_trace) Console.WriteLine ($"[wayland form] '{_form.Text}' layered={IsLayered ()}");
			return true;
		}

		public void Present ()
		{
			if (_closed || _window == null || !_ready || !_form.Visible)
				return;
			if (_pendingW != 0 && !_resizing) {
				ApplyPendingSize ();   // presents itself
				return;
			}
			ApplyCursor ();
			if (!EnsurePresenter ())
				return;
			int ver = _driver.GetPaintVersion () + EmbeddedScenes.CurrentVersion ();
			bool caretOn = CaretOn ();
			if (ver == _lastVer && caretOn == _lastCaretOn && _lastPresentOk)
				return;
			// A swap waits for the frame callback of the frame before, which comes only from a surface the
			// compositor shows: a drop-down moved before the compositor had placed it never was, and the
			// next frame for it waited for good inside the property grid's drop-down loop. Not now; again
			// once it is on screen (WaylandWindow.CanPresent, as WPF's windows are asked).
			if (!_window.ReadyToPresent ()) {
				_lastPresentOk = false;
				return;
			}
			Rectangle client = ClientScreen;
			var popups = s_popupLayer && WaylandWindow.IsUiThread ? new List<(object, int, int)> () : null;
			var scenes = GetScenes (client.X, client.Y, popups, out Rectangle popupBounds);
			Rectangle? caret = caretOn && _driver.GetCaret (_form.Handle, out int cx, out int cy, out int cw, out int ch)
				? new Rectangle (cx - client.X, cy - client.Y, Math.Max (1, cw), ch) : null;
			_surfaceW = client.Width;
			_surfaceH = client.Height;
			_lastPresentOk = _wgpu.PresentScenes (scenes, caret, _surfaceW, _surfaceH);
			if (s_trace && scenes.Count > 0 && ver != _lastVer)
				Console.WriteLine ($"[wayland form] '{_form.Text}' form={_form.Bounds} client={client} scale={_scale} ok={_lastPresentOk} scenes={scenes.Count}");
			if (popups != null)
				PresentPopups (popups, popupBounds);
			_lastVer = ver;
			_lastCaretOn = caretOn;
		}

		// ---- the popup layer -------------------------------------------------------------------------

		private static readonly bool s_popupLayer = Environment.GetEnvironmentVariable ("WF_WAYLAND_POPUP_LAYER") != "0";
		private WaylandWindow _layer;
		private WgpuPresenter _layerWgpu;
		private Rectangle _layerRect;           // what the layer shows, in client space (driver units)
		private int _layerX, _layerY;           // where it was put on the virtual screen (device pixels)
		private bool _layerDismissed, _layerCreating;

		/// <summary>In a popup window (WS_POPUP) -- a menu, a drop-down, a shadow -- and not the form's own
		/// (CocoaHost.IsPopup).</summary>
		private bool IsPopup (IntPtr handle)
		{
			// up to the window that is not a child: a popup's parent is its owner, the form itself
			Hwnd h = Hwnd.ObjectFromHandle (handle);
			while (h?.parent != null && (h.initial_style & WindowStyles.WS_CHILD) != 0) h = h.parent;
			if (h == null || (_form.IsHandleCreated && h.Handle == _form.Handle)) return false;
			return (h.initial_style & WindowStyles.WS_POPUP) != 0 && h.width > 1 && h.height > 1;
		}

		/// <summary>The popups on the layer, which is put over the rectangle they cover; with none, the
		/// layer goes.</summary>
		private void PresentPopups (List<(object, int, int)> popups, Rectangle bounds)
		{
			// Making the layer waits for the compositor's configure and dispatches what comes meanwhile,
			// which can present again: that frame waits for this one, or there were two layers, the
			// second over the first and taking its pointer
			if (_layerCreating)
				return;
			if (popups.Count == 0 || bounds.Width <= 0 || bounds.Height <= 0 || _closed) {
				CloseLayer ();
				return;
			}
			if (_layer != null && (_layerDismissed || !_layer.IsMapped))
				CloseLayer ();
			_window.GetClientScreenOriginPixels (out int cx, out int cy);
			int vx = cx + (int)Math.Round (bounds.X * _scale), vy = cy + (int)Math.Round (bounds.Y * _scale);
			int pw = Math.Max (1, (int)Math.Round (bounds.Width * _scale)), ph = Math.Max (1, (int)Math.Round (bounds.Height * _scale));
			try {
				if (_layer == null) {
					var layer = new WaylandWindow ();
					_layerCreating = true;
					try {
						layer.Create (string.Empty, vx, vy, pw, ph, borderless: true, _window.Handle);
					} finally {
						_layerCreating = false;
					}
					layer.Closed += _ => _layerDismissed = true;
					_layer = layer;
					_layerDismissed = false;
					if (s_trace) Console.WriteLine ($"[wayland form] '{_form.Text}' popup layer 0x{_layer.Handle:x} {bounds} at ({vx},{vy})");
				} else if (bounds != _layerRect || vx != _layerX || vy != _layerY) {
					if (bounds.Size != _layerRect.Size)
						_layer.SetContentSizePixels (pw, ph);
					_layer.SetFrameOrigin (vx, vy);
				}
				_layerRect = bounds;
				_layerX = vx;
				_layerY = vy;
				if (!_layer.IsMapped || !_layer.ReadyToPresent ()) {
					_lastPresentOk = false;   // again on the next frame (the form's own, above)
					return;
				}
				if (_layerWgpu == null) {
					var ctx = ProcessGpu.Acquire ();
					IntPtr surface = NativePlatform.CreateWindowSurface (ctx.Instance, _layer.Handle);
					if (surface == IntPtr.Zero)
						return;
					_layerWgpu = new WgpuPresenter (ctx, surface, bounds.Width, bounds.Height, _scale, srgb: true, threadDevice: true);
					_layerWgpu.SetTransparent (true);
				}
				var placed = new List<(object, int, int)> (popups.Count);
				foreach (var (scene, x, y) in popups)
					placed.Add ((scene, x - bounds.X, y - bounds.Y));
				if (!_layerWgpu.PresentScenes (placed, null, bounds.Width, bounds.Height))
					_lastPresentOk = false;
			} catch (Exception e) {
				Console.Error.WriteLine ($"[WaylandFormHost] popup layer failed: {e.Message}");
				CloseLayer ();
			}
		}

		private void CloseLayer ()
		{
			if (s_trace && _layer != null) Console.WriteLine ($"[wayland form] '{_form.Text}' popup layer 0x{_layer.Handle:x} closed (dismissed={_layerDismissed}, mapped={_layer.IsMapped})");
			_layerWgpu?.Dispose ();
			_layerWgpu = null;
			WaylandWindow layer = _layer;
			_layer = null;
			_layerRect = Rectangle.Empty;
			if (layer != null)
				OnPump (layer.Destroy);
		}

		/// <summary>The form's windows and the windows over it -- its popups, and the WPF trees it hosts --
		/// in client space (AndroidFormHost.GetScenes, without the surface growing to hold the popups).</summary>
		private List<(object, int, int)> GetScenes (int ox, int oy)
			=> GetScenes (ox, oy, null, out _);

		/// <summary>With <paramref name="popups"/> given, the popup windows go there instead (the popup
		/// layer), and <paramref name="popupBounds"/> is the rectangle they cover, in client space.</summary>
		private List<(object, int, int)> GetScenes (int ox, int oy, List<(object, int, int)> popups, out Rectangle popupBounds)
		{
			popupBounds = Rectangle.Empty;
			long[] wins = _driver.GetPresentWindows (_form.Handle);
			var list = new List<(object, int, int)> (wins.Length / 3);
			var roots = new List<IntPtr> (wins.Length / 3);
			for (int i = 0; i + 2 < wins.Length; i += 3) {
				IntPtr h = (IntPtr)wins [i];
				object scene = _driver.GetWindowScene (h);
				if (scene == null)
					continue;
				var item = (scene, (int)wins [i + 1] - ox, (int)wins [i + 2] - oy);
				if (popups != null && IsPopup (h)) {
					long size = _driver.GetWindowSizePacked (h);
					var r = new Rectangle (item.Item2, item.Item3, (int)(size >> 32), (int)(size & 0xFFFFFFFF));
					popupBounds = popupBounds.IsEmpty ? r : Rectangle.Union (popupBounds, r);
					popups.Add (item);
					continue;
				}
				list.Add (item);
				roots.Add (Control.FromHandle (h)?.TopLevelControl is Control top && top.IsHandleCreated ? top.Handle : h);
			}
			var embedded = EmbeddedScenes.GetWithOwners (ox, oy);
			if (embedded != null) {
				for (int e = embedded.Count - 1; e >= 0; e--) {
					var (scene, x, y, owner) = embedded [e];
					int last = roots.LastIndexOf (owner);
					if (last < 0)
						continue;
					list.Insert (last + 1, (scene, x, y));
					roots.Insert (last + 1, owner);
				}
			}
			return list;
		}

		private bool IsLayered ()
		{
			Hwnd hwnd = _form.IsHandleCreated ? Hwnd.ObjectFromHandle (_form.Handle) : null;
			return hwnd != null && (hwnd.initial_ex_style & WindowExStyles.WS_EX_LAYERED) != 0;
		}

		private bool CaretOn () => _driver.GetCaret (_form.Handle, out _, out _, out _, out _) && (_blink.ElapsedMilliseconds / 530) % 2 == 0;

		private static float SrgbToLinear (float c) => c <= 0.04045f ? c / 12.92f : (float)Math.Pow ((c + 0.055) / 1.055, 2.4);

		/// <summary>The cursor the control under the pointer asks for (the driver's StdCursor), as Windows
		/// shows it on WM_SETCURSOR, by the name WPF's cursors go by on this head.</summary>
		private void ApplyCursor ()
		{
			if (!WaylandWindow.IsUiThread || (Form.ActiveForm != _form && !_pointerOver))
				return;
			// over the sizing border, the sizing cursor, as DefWindowProc's WM_SETCURSOR shows it
			int id = _ncHit switch {
				10 or 11 => 24, 12 or 15 => 22, 13 or 17 => 23, 14 or 16 => 21,
				_ => _driver.GetActiveCursor (),
			};
			if (s_trace && id != _lastCursor) Console.WriteLine ($"[wayland form] cursor {_lastCursor} -> {id} (frame {_ncHit})");
			if (id == _lastCursor)
				return;
			_lastCursor = id;
			WaylandWindow.SetCursor (id switch {
				1 => "AppStarting", 3 => "Cross", 4 => "Hand", 5 => "Help", 6 => "SizeNS", 7 => "IBeam", 8 => "No",
				>= 9 and <= 19 => "ScrollAll", 20 => "SizeAll", 21 => "SizeNESW", 22 => "SizeNS",
				23 => "SizeNWSE", 24 => "SizeWE", 26 => "SizeWE", 27 => "Wait",
				_ => "Arrow",
			});
		}

		private bool _pointerOver;
		private bool _popup;                    // a popup of another form's window (see Show)
		private static WaylandFormHost s_activeHost;

		/// <summary>The window a borderless form shown now belongs to: the active form's, else the newest
		/// framed one of this thread.</summary>
		private static WaylandFormHost HostOfForm (Form form)
		{
			if (form == null) return null;
			lock (s_hosts)
				foreach (WaylandFormHost h in s_hosts)
					if (h._form == form && !h._closed && h._ready && !h._popup)
						return h;
			return null;
		}

		private WaylandFormHost OwnerHost ()
		{
			WaylandFormHost active = s_activeHost;
			if (active != null && active != this && !active._closed && active._ready && !active._popup && !active._form.InvokeRequired)
				return active;
			lock (s_hosts)
				for (int i = s_hosts.Count - 1; i >= 0; i--) {
					WaylandFormHost h = s_hosts [i];
					if (h != this && !h._closed && h._ready && !h._popup && h._form.FormBorderStyle != FormBorderStyle.None && !h._form.InvokeRequired)
						return h;
				}
			return null;
		}

		// ---- input ---------------------------------------------------------------------------

		public bool Pump () => !_closed;   // input arrives through WaylandWindow's events

		private bool Mine (IntPtr surface) => !_closed && _window != null && (surface == _window.Handle || (_layer != null && surface == _layer.Handle))
			&& _form.IsHandleCreated && !_form.IsDisposed;

		/// <summary>A hosted WPF window (an ElementHost's) under a point of the virtual screen, or zero --
		/// only where the form's own windows put that ElementHost: its window is the control's child, clipped
		/// to it and under the controls over it, as on Windows. Taken by its rectangle alone, it took the
		/// presses on the docking splitter beside the canvas, which could hardly be grabbed.</summary>
		private IntPtr HostedWindowAt (int vx, int vy)
		{
			if (!HostedWpfContent.Any)
				return IntPtr.Zero;
			IntPtr wpf = WaylandWindow.HitTest (vx, vy, h => HostedWpfContent.IsClaimed ((ulong)h));
			if (wpf == IntPtr.Zero)
				return IntPtr.Zero;
			Control owner = Control.FromChildHandle (wpf);
			if (owner == null || !owner.IsHandleCreated)
				return wpf;
			IntPtr under = _driver.WindowAtPointIn (_form.Handle, (int)Math.Floor (vx / _scale), (int)Math.Floor (vy / _scale));
			for (Control c = Control.FromHandle (under); c != null; c = c.Parent)
				if (c == owner)
					return wpf;
			return IntPtr.Zero;
		}

		private void OnMouse (WaylandWindow.MouseMessage m)
		{
			if (!Mine (m.Window))
				return;
			if (_form.InvokeRequired) {
				_form.BeginInvoke ((Action)(() => OnMouse (m)));
				return;
			}
			int vx, vy;
			if (_layer != null && m.Window == _layer.Handle) {
				// over the popup layer: the point of the screen it shows (where it was put, whatever the
				// compositor made of that)
				vx = _layerX + m.X;
				vy = _layerY + m.Y;
			} else {
				_window.GetClientScreenOriginPixels (out int cx, out int cy);
				vx = cx + m.X;
				vy = cy + m.Y;
			}
			if (m.Kind == 4) {
				_pointerOver = false;
				if (s_pointerOwner == IntPtr.Zero)
					_driver.InjectMouseLeaveAll ();
				return;
			}
			_pointerOver = true;

			// Over a hosted WPF tree, unless a WinForms control holds the mouse: that window's input, as
			// the OS routes it to a child window on Windows. It keeps the pointer from a press to its release.
			IntPtr wpf = s_pointerOwner != IntPtr.Zero ? s_pointerOwner : _driver.GrabHandle == IntPtr.Zero ? HostedWindowAt (vx, vy) : IntPtr.Zero;
			if (s_trace && m.Kind == 1) {
				IntPtr under = WaylandWindow.HitTest (vx, vy);
				Console.WriteLine ($"[wayland form] press at ({vx},{vy}) under=0x{under:x} claimed={(under != IntPtr.Zero && HostedWpfContent.Any && HostedWpfContent.IsClaimed ((ulong)under))} grab=0x{_driver.GrabHandle:x} -> wpf=0x{wpf:x}");
			}
			if (wpf != IntPtr.Zero && WaylandWindow.FromHandle (wpf) is { } hosted) {
				hosted.GetClientScreenOriginPixels (out int hx, out int hy);
				if (s_trace && m.Kind == 1) {
					hosted.GetPixelSize (out int hw, out int hh);
					Console.WriteLine ($"[wayland form] -> hosted 0x{wpf:x} at ({hx},{hy}) {hw}x{hh}: local ({vx - hx},{vy - hy}); form client {ClientScreen} scale {_scale}");
				}
				if (m.Kind == 1) {
					s_pointerOwner = wpf;
					s_keyboardOwner = wpf;
				} else if (m.Kind == 2 && m.Button == 1) {
					s_pointerOwner = IntPtr.Zero;
				}
				_driver.InjectMouseLeaveAll ();
				_lastCursor = int.MinValue;   // the WPF tree sets the cursor itself: set ours again on the way out
				WaylandWindow.DeliverMouse (wpf, m.Kind, m.Button, vx - hx, vy - hy, m.Wheel);
				Present ();
				return;
			}
			if (m.Kind == 1)
				s_keyboardOwner = IntPtr.Zero;

			int x = (int)Math.Floor (vx / _scale), y = (int)Math.Floor (vy / _scale);
			if (OnNonClient (m, x, y)) {
				Present ();
				return;
			}
			IntPtr outerRoot = XplatUIWebGpu.InputRoot;
			XplatUIWebGpu.InputRoot = _form.Handle;
			try {
				switch (m.Kind) {
				case 0:
					// a move without the button down is Windows' WM_SETCURSOR, which ends a Cursor.Current
					if (!s_leftDown && _driver.GrabHandle == IntPtr.Zero)
						_driver.EndCursorOverride ();
					_driver.InjectMouseMove (x, y, s_leftDown);
					break;
				case 1:
					if (m.Button == 1) { s_leftDown = true; _driver.InjectMouseDown (x, y); }
					else if (m.Button == 2) _driver.InjectRightDown (x, y);
					break;
				case 2:
					if (m.Button == 1) { s_leftDown = false; _driver.InjectMouseUp (x, y); }
					else if (m.Button == 2) _driver.InjectRightUp (x, y);
					break;
				case 3: _driver.InjectWheel (x, y, m.Wheel); break;
				}
			} finally {
				XplatUIWebGpu.InputRoot = outerRoot;
			}
			Present ();
		}

		// ---- the non-client area: what Windows sends a window about its frame ------------------------

		private const int HTCLIENT = 1;
		private int _ncHit;                  // the frame part the pointer was last over, 0 when the client
		private long _ncPressTime;
		private Point _ncPressAt;

		/// <summary>
		/// The pointer over the form's frame -- WM_NCHITTEST says what is there: WM_NCMOUSEMOVE, and the left
		/// button's WM_NCLBUTTONDOWN (WM_NCLBUTTONDBLCLK for a second press soon enough) and WM_NCLBUTTONUP,
		/// with the hit-test code and the point on the screen, as Windows sends them. A form that draws its
		/// own frame (DotNetBar's ribbon form) handles its caption buttons from these and leaves the caption
		/// and the edges to DefWindowProc. True when the event was the frame's.
		/// </summary>
		private bool OnNonClient (WaylandWindow.MouseMessage m, int x, int y)
		{
			// a window holding the mouse (a control that captured it) gets everything as its client's
			if (_driver.GrabHandle != IntPtr.Zero || (m.Button != 0 && m.Button != 1) || m.Kind > 2)
				return false;
			IntPtr lp = (IntPtr)(((y & 0xFFFF) << 16) | (x & 0xFFFF));
			// the deepest window there answers first (a ribbon's caption buttons are its client, though the
			// form calls the strip around them caption), as Windows asks it; only a caption or an edge is
			// the frame's
			// The sizing border is the form's, whatever lies under it: on Windows the frame is outside the
			// children (a ribbon form keeps a 5-pixel border there), and asked deepest-first the children
			// reaching the edge left a band of a pixel or two to take hold of. The rest the deepest window
			// answers first (a ribbon's caption buttons are its client, though the form calls the strip
			// around them caption); only a caption or an edge is the frame's.
			int hit = _form.WindowState == FormWindowState.Normal && _form.FormBorderStyle is FormBorderStyle.Sizable or FormBorderStyle.SizableToolWindow
				? (int)(long)XplatUI.SendMessage (_form.Handle, Msg.WM_NCHITTEST, IntPtr.Zero, lp) : 0;
			if (hit < 10 || hit > 17)
				hit = _driver.HostHitTest (x, y);
			if (hit == 0) hit = HTCLIENT;
			const int WM_NCMOUSEMOVE = 0xA0, WM_NCLBUTTONDOWN = 0xA1, WM_NCLBUTTONUP = 0xA2, WM_NCLBUTTONDBLCLK = 0xA3, WM_NCMOUSELEAVE = 0x2A2;
			if (hit == HTCLIENT || hit <= 0) {
				if (_ncHit != 0) {
					_ncHit = 0;
					XplatUI.SendMessage (_form.Handle, (Msg)WM_NCMOUSELEAVE, IntPtr.Zero, IntPtr.Zero);
				}
				return false;
			}
			_ncHit = hit;
			int message = WM_NCMOUSEMOVE;
			if (m.Kind == 1) {
				long now = Environment.TickCount64;
				bool second = now - _ncPressTime <= SystemInformation.DoubleClickTime
					&& Math.Abs (x - _ncPressAt.X) <= SystemInformation.DoubleClickSize.Width / 2
					&& Math.Abs (y - _ncPressAt.Y) <= SystemInformation.DoubleClickSize.Height / 2;
				_ncPressTime = second ? 0 : now;
				_ncPressAt = new Point (x, y);
				message = second ? WM_NCLBUTTONDBLCLK : WM_NCLBUTTONDOWN;
			} else if (m.Kind == 2) {
				message = WM_NCLBUTTONUP;
			}
			if (s_trace && m.Kind != 0) Console.WriteLine ($"[wayland form] '{_form.Text}' 0x{message:x} hit {hit} at ({x},{y})");
			XplatUI.SendMessage (_form.Handle, (Msg)message, (IntPtr)hit, lp);
			return true;
		}

		/// <summary>A key for the window that has the keyboard (BrowserFormHost's): WM_KEYDOWN/UP and the
		/// typed character (WM_CHAR) -- or the hosted WPF tree's, when a press gave it the keys.</summary>
		private void OnKey (WaylandWindow.KeyMessage k)
		{
			if (!Mine (k.Window))
				return;
			if (_form.InvokeRequired) {
				_form.BeginInvoke ((Action)(() => OnKey (k)));
				return;
			}
			if (s_keyboardOwner != IntPtr.Zero && WaylandWindow.FromHandle (s_keyboardOwner) != null) {
				WaylandWindow.DeliverKey (s_keyboardOwner, k);
				Present ();
				return;
			}
			Keys modifiers = (k.Ctrl ? Keys.Control : 0) | (k.Shift ? Keys.Shift : 0) | (k.Alt ? Keys.Alt : 0);
			_driver.SetModifierKeys (modifiers);
			int vk = k.VirtualKey;
			if (vk == 0)
				return;
			if (!k.IsDown) {
				_driver.InjectKeyUp (vk);
				return;
			}
			bool consumed = _driver.InjectKeyDown (vk);
			// WM_CHAR, as TranslateMessage makes it: the typed character, and the control characters
			// Windows translates keys into (Backspace, Tab, Enter, Escape)
			if (!consumed) {
				string chars = k.Characters;
				if (chars is { Length: 1 } && chars [0] >= ' ' && chars [0] != '\x7f' && !k.Ctrl)
					_driver.InjectChar (chars [0]);
				else if (vk is 0x08 or 0x09 or 0x0D or 0x1B)
					_driver.InjectChar ((char)vk);
			}
			Present ();
		}

		private void Repaint ()
		{
			_lastVer = -1;
			Present ();
		}

		public void InjectClickScreen (int x, int y)
		{
			_driver.InjectMouseMove (x, y, false);
			_driver.InjectMouseDown (x, y);
			_driver.InjectMouseUp (x, y);
		}

		public void SaveFrame (string path) { }

		public void Close ()
		{
			if (_closed)
				return;
			_closed = true;
			lock (s_hosts) s_hosts.Remove (this);
			_form.VisibleChanged -= OnVisibleChanged;
			_form.LocationChanged -= OnFormMoved;
			_form.SizeChanged -= OnFormSized;
			_form.TextChanged -= OnFormText;
			_form.HandleDestroyed -= OnHandleDestroyed;
			WaylandWindow.MouseInput -= OnMouse;
			WaylandWindow.KeyInput -= OnKey;
			CloseLayer ();
			_wgpu?.Dispose ();
			_wgpu = null;
			WaylandWindow window = _window;
			if (window != null) {
				window.Resized -= OnResized;
				window.Closed -= OnCloseRequested;
				window.ActiveChanged -= OnActiveChanged;
				if (_ready) OnPump (window.Destroy);
			}
		}
	}
}
