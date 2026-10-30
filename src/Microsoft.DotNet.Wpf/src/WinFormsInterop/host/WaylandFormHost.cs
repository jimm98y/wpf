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
// POPUPS of the driver -- menus, drop-downs, their shadows -- are drawn into the form's own surface, as
// WPF's popups are drawn into their owner's on this head (NativePlatform.PopupsShareOwnerSurface: the GL
// backend has no translucent surfaces); one that reaches past the form is cut at its edge.
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

		internal WaylandFormHost (Form form)
		{
			_form = form;
			PresentationHost.Attach (this, form);
		}

		private Padding Frame => _form.IsHandleCreated ? _driver.FrameOf (_form.Handle) : Padding.Empty;

		/// <summary>The form's client area on the screen (driver units): what the toplevel's content shows.</summary>
		private Rectangle ClientScreen
		{
			get {
				Padding f = _chromeless ? Padding.Empty : Frame;
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
			OnPump (() => MakeWindow (title, ox, oy, ow, oh, client.Size));
			_form.VisibleChanged += OnVisibleChanged;
			_form.HandleDestroyed += OnHandleDestroyed;
			_form.LocationChanged += OnFormMoved;
			_form.SizeChanged += OnFormSized;
			_form.TextChanged += OnFormText;
			WaylandWindow.MouseInput += OnMouse;
			WaylandWindow.KeyInput += OnKey;
			lock (s_hosts) s_hosts.Add (this);
		}

		/// <summary>The toplevel, on the compositor's thread; then the form takes its place, on its own.</summary>
		private void MakeWindow (string title, int x, int y, int width, int height, Size client)
		{
			if (_closed)
				return;
			try {
				// the outer rectangle in device pixels, as WaylandWindow takes it; the content is then set to
				// the client exactly
				_window.Create (title, x, y, width, height, borderless: false);
			} catch (Exception e) {
				Console.Error.WriteLine ($"WaylandFormHost: '{title}' has no window: {e.Message}");
				_closed = true;
				return;
			}
			if (_chromeless)
				_window.SetChromeless (true);
			_sizedTo = client;
			_window.SetContentSize (Math.Max (1, (int)Math.Round (client.Width / _k)), Math.Max (1, (int)Math.Round (client.Height / _k)));
			_window.NonClientHitTest = NonClientHitTest;
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
			Padding f = _chromeless ? Padding.Empty : Frame;
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
			var client = new Size ((int)Math.Round (logicalW * _k), (int)Math.Round (logicalH * _k));
			_sizedTo = client;
			Padding f = _chromeless ? Padding.Empty : Frame;
			int w = client.Width + f.Horizontal, h = client.Height + f.Vertical;
			if (w != _form.Width || h != _form.Height)
				XplatUI.SetWindowPos (_form.Handle, _form.Left, _form.Top, w, h);
			SyncFromWindow ();
			_lastVer = -1;
		}

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
			Color bc = _form.BackColor;
			_wgpu.ClearColor = new RgbaColor (SrgbToLinear (bc.R / 255f), SrgbToLinear (bc.G / 255f), SrgbToLinear (bc.B / 255f), 1f);
			_lastVer = -1;
			return true;
		}

		public void Present ()
		{
			if (_closed || _window == null || !_ready || !_form.Visible)
				return;
			ApplyCursor ();
			if (!EnsurePresenter ())
				return;
			int ver = _driver.GetPaintVersion () + EmbeddedScenes.CurrentVersion ();
			bool caretOn = CaretOn ();
			if (ver == _lastVer && caretOn == _lastCaretOn && _lastPresentOk)
				return;
			Rectangle client = ClientScreen;
			var scenes = GetScenes (client.X, client.Y);
			Rectangle? caret = caretOn && _driver.GetCaret (_form.Handle, out int cx, out int cy, out int cw, out int ch)
				? new Rectangle (cx - client.X, cy - client.Y, Math.Max (1, cw), ch) : null;
			_surfaceW = client.Width;
			_surfaceH = client.Height;
			_lastPresentOk = _wgpu.PresentScenes (scenes, caret, _surfaceW, _surfaceH);
			if (s_trace && scenes.Count > 0 && ver != _lastVer)
				Console.WriteLine ($"[wayland form] '{_form.Text}' form={_form.Bounds} client={client} scale={_scale} ok={_lastPresentOk} scenes={scenes.Count}");
			_lastVer = ver;
			_lastCaretOn = caretOn;
		}

		/// <summary>The form's windows and the windows over it -- its popups, and the WPF trees it hosts --
		/// in client space (AndroidFormHost.GetScenes, without the surface growing to hold the popups).</summary>
		private List<(object, int, int)> GetScenes (int ox, int oy)
		{
			long[] wins = _driver.GetPresentWindows (_form.Handle);
			var list = new List<(object, int, int)> (wins.Length / 3);
			var roots = new List<IntPtr> (wins.Length / 3);
			for (int i = 0; i + 2 < wins.Length; i += 3) {
				IntPtr h = (IntPtr)wins [i];
				object scene = _driver.GetWindowScene (h);
				if (scene == null)
					continue;
				list.Add ((scene, (int)wins [i + 1] - ox, (int)wins [i + 2] - oy));
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

		private bool CaretOn () => _driver.GetCaret (_form.Handle, out _, out _, out _, out _) && (_blink.ElapsedMilliseconds / 530) % 2 == 0;

		private static float SrgbToLinear (float c) => c <= 0.04045f ? c / 12.92f : (float)Math.Pow ((c + 0.055) / 1.055, 2.4);

		/// <summary>The cursor the control under the pointer asks for (the driver's StdCursor), as Windows
		/// shows it on WM_SETCURSOR, by the name WPF's cursors go by on this head.</summary>
		private void ApplyCursor ()
		{
			if (!WaylandWindow.IsUiThread || (Form.ActiveForm != _form && !_pointerOver))
				return;
			int id = _driver.GetActiveCursor ();
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

		// ---- input ---------------------------------------------------------------------------

		public bool Pump () => !_closed;   // input arrives through WaylandWindow's events

		private bool Mine (IntPtr surface) => !_closed && _window != null && surface == _window.Handle && _form.IsHandleCreated && !_form.IsDisposed;

		/// <summary>A hosted WPF window (an ElementHost's) under a point of the virtual screen, or zero.</summary>
		private static IntPtr HostedWindowAt (int vx, int vy)
		{
			IntPtr h = WaylandWindow.HitTest (vx, vy);
			return h != IntPtr.Zero && HostedWpfContent.Any && HostedWpfContent.IsClaimed ((ulong)h) ? h : IntPtr.Zero;
		}

		private void OnMouse (WaylandWindow.MouseMessage m)
		{
			if (!Mine (m.Window))
				return;
			if (_form.InvokeRequired) {
				_form.BeginInvoke ((Action)(() => OnMouse (m)));
				return;
			}
			_window.GetClientScreenOriginPixels (out int cx, out int cy);
			int vx = cx + m.X, vy = cy + m.Y;
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
			if (wpf != IntPtr.Zero && WaylandWindow.FromHandle (wpf) is { } hosted) {
				hosted.GetClientScreenOriginPixels (out int hx, out int hy);
				if (m.Kind == 1) {
					s_pointerOwner = wpf;
					s_keyboardOwner = wpf;
				} else if (m.Kind == 2 && m.Button == 1) {
					s_pointerOwner = IntPtr.Zero;
				}
				_driver.InjectMouseLeaveAll ();
				WaylandWindow.DeliverMouse (wpf, m.Kind, m.Button, vx - hx, vy - hy, m.Wheel);
				Present ();
				return;
			}
			if (m.Kind == 1)
				s_keyboardOwner = IntPtr.Zero;

			int x = (int)Math.Floor (vx / _scale), y = (int)Math.Floor (vy / _scale);
			IntPtr outerRoot = XplatUIWebGpu.InputRoot;
			XplatUIWebGpu.InputRoot = _form.Handle;
			try {
				switch (m.Kind) {
				case 0: _driver.InjectMouseMove (x, y, s_leftDown); break;
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
