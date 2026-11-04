// The Android head's window for a top-level form: a view of the activity, made and placed by the same
// window manager as WPF's windows on that head (MS.Internal.Interop.AndroidWindow), so forms and WPF
// windows share one set of rules for placement, stacking and input -- the browser's and iOS's
// (BrowserFormHost, AndroidFormHost), whose design this is. The present path is the one every host
// shares (the driver's scenes through WgpuPresenter); only the surface (a SurfaceView's, which comes,
// goes and comes back with the activity) and the source of input (Android's touches) are Android's.
//
// The screen is the activity: its pixels are the forms' screen coordinates at the app's DPI. The first
// framed window fills it; later ones float over it with the frame Windows would draw (WindowChrome).
// AndroidWindow's host does what is done to views on the main Looper, whichever thread asks, so a form
// of another thread (a splash screen) needs nothing of its own here; its touches are taken on its thread.

extern alias WindowsBase;

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.Versioning;
using System.Threading;
using AndroidWindow = WindowsBase::MS.Internal.Interop.AndroidWindow;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using Microsoft.Wpf.Interop.WebGpu.Composition.Platform;

namespace System.Windows.Forms
{
	[SupportedOSPlatform ("android")]
	internal sealed class AndroidFormHost : IWinFormsHost
	{
		private readonly Form _form;
		private readonly XplatUIWebGpu _driver = XplatUIWebGpu.GetInstance ();
		private readonly System.Diagnostics.Stopwatch _blink = System.Diagnostics.Stopwatch.StartNew ();
		private AndroidWindow _window;
		private WgpuPresenter _wgpu;
		private IntPtr _nativeWindow;         // the ANativeWindow the presenter was made on (the activity replaces it)
		// device pixels per driver unit; and driver units per point -- 1 for an application laid out at
		// 96 dpi, the screen's scale for a per-monitor one, which lays out in device pixels
		private double _scale = 1, _k = 1;
		private int _lastVer = -1, _surfaceW, _surfaceH;
		private bool _lastCaretOn, _lastPresentOk, _closed;
		// The finger as the system has it, not as each window saw it (BrowserFormHost._leftDown).
		private static bool _leftDown;
		private readonly WindowChrome _chrome;
		private bool _dragging;
		private Point _dragFrom, _dragOrigin;

		private static readonly List<AndroidFormHost> s_activation = new List<AndroidFormHost> ();
		private static AndroidFormHost s_active;
		private static readonly bool s_trace = Environment.GetEnvironmentVariable ("WF_TRACE_ANDROID") == "1";

		private static bool PerMonitor => ScaleHelper.IsDeviceScaled && ScaleHelper.HostSourceDpi == 0;

		// ---- the screen ----------------------------------------------------------------------

		private static Size s_screen;

		/// <summary>Called by the driver as it starts: WinForms' screen is the root view.</summary>
		internal static void InstallScreens ()
		{
			XplatUIWebGpu.ScreensProvider ??= RootScreens;
		}

		private static Screen[] RootScreens ()
		{
			AndroidWindow.GetPrimaryScreenPixels (out _, out _, out int right, out int bottom, out _, out _, out _, out _);
			double perDevice = PerMonitor || ScaleHelper.HostSourceDpi <= 0 ? 1 : 96.0 / ScaleHelper.HostSourceDpi * Math.Max (0.5, new AndroidWindow ().GetBackingScale ());
			var size = new Size (Math.Max (1, (int)Math.Round (right / perDevice)), Math.Max (1, (int)Math.Round (bottom / perDevice)));
			s_screen = size;
			var bounds = new Rectangle (Point.Empty, size);
			return new[] { new Screen (true, "DISPLAY1", bounds, bounds) };
		}

		/// <summary>The screen turned (rotation, split view): Screen keeps the list it read, so it is told.</summary>
		private static void ScreenChanged ()
		{
			if (XplatUIWebGpu.ScreensProvider != RootScreens) return;
			Size was = s_screen;
			if (RootScreens () [0].Bounds.Size != was) Screen.Rescan ();
		}

		/// <summary>The screen's DPI as Windows would give it: 96 per point.</summary>
		internal static int ScreenDpi ()
		{
			double scale = new AndroidWindow ().GetBackingScale ();
			return scale > 0 ? (int)Math.Round (96 * scale) : 0;
		}

		static AndroidFormHost ()
		{
			XplatUIWebGpu.CaptureChanged += OnCaptureChanged;
			// a form that draws its own caption and edges (a ribbon's) leaves a press on them to DefWindowProc, whose
			// move and size loops (SC_MOVE, SC_SIZE) and Form.WindowState come here, as WaylandFormHost has them
			XplatUIWebGpu.BeginWindowMove ??= h => { if (s_trace) Console.WriteLine ($"[android form] move loop for 0x{h:x}: host {(HostOf (h) != null ? "found" : "none")}"); HostOf (h)?.BeginSystemMove (); };
			XplatUIWebGpu.BeginWindowSize ??= (h, edge) => { if (s_trace) Console.WriteLine ($"[android form] size loop {edge} for 0x{h:x}: host {(HostOf (h) != null ? "found" : "none")}"); HostOf (h)?.BeginSystemSize (edge); };
			XplatUIWebGpu.WindowStateRequested ??= (h, state) => HostOf (h)?.ApplyWindowState (state);
		}

		private static AndroidFormHost HostOf (IntPtr formHandle)
		{
			foreach (AndroidFormHost h in s_activation)
				if (!h._closed && h._window != null && h._form.IsHandleCreated && h._form.Handle == formHandle)
					return h;
			return null;
		}

		// the last press and where the finger is, in the screen's points: a move or size loop begins from it
		private Point _pointer;
		private int _sizing;                    // WMSZ_LEFT (1) .. WMSZ_BOTTOMRIGHT (8) while sizing, else 0
		private Rectangle _sizeOrigin;

		/// <summary>SC_MOVE from the press in hand: the window follows the pointer (OnDrag), a maximized one
		/// restored under it first, as on Windows.</summary>
		private void BeginSystemMove ()
		{
			if (_window == null || IsScreenWindow)
				return;
			_dragging = true;
			_dragFrom = _pointer;
			_dragOrigin = _form.Location;
			_restoreOnDrag = _form.WindowState == FormWindowState.Maximized;
		}
		private bool _restoreOnDrag;

		/// <summary>SC_SIZE from the press in hand on an edge (WMSZ_*): the window's edges follow the pointer.</summary>
		private void BeginSystemSize (int edge)
		{
			if (_window == null || IsScreenWindow || edge < 1 || edge > 8 || _form.WindowState == FormWindowState.Maximized)
				return;
			_sizing = edge;
			_dragFrom = _pointer;
			_sizeOrigin = _form.Bounds;
		}

		private bool OnSize (AndroidWindow.TouchMessage m)
		{
			if (m.Kind == 2) { _sizing = 0; return true; }
			if (m.Kind != 0) return true;
			AndroidWindow over = AndroidWindow.FromHandle (m.Window);
			if (over == null) return true;
			over.GetClientScreenOriginPixels (out int sx, out int sy);
			int x = (int)Math.Floor ((m.X + sx) / _scale), y = (int)Math.Floor ((m.Y + sy) / _scale);
			int dx = x - _dragFrom.X, dy = y - _dragFrom.Y;
			bool left = _sizing is 1 or 4 or 7, right = _sizing is 2 or 5 or 8, top = _sizing is 3 or 4 or 5, bottom = _sizing is 6 or 7 or 8;
			Rectangle r = _sizeOrigin;
			Size min = _form.MinimumSize, max = _form.MaximumSize;
			int w = r.Width + (right ? dx : left ? -dx : 0), h = r.Height + (bottom ? dy : top ? -dy : 0);
			w = Math.Max (Math.Max (w, min.Width), 100); h = Math.Max (Math.Max (h, min.Height), 40);
			if (max.Width > 0) w = Math.Min (w, max.Width);
			if (max.Height > 0) h = Math.Min (h, max.Height);
			int fx = left ? r.Right - w : r.Left, fy = top ? Math.Max (0, r.Bottom - h) : r.Top;
			XplatUI.SetWindowPos (_form.Handle, fx, fy, w, h);
			_window.SetFrameOrigin ((int)Math.Round (fx * _scale), (int)Math.Round (fy * _scale));
			_window.SetContentSizePixels ((int)Math.Round (w * _scale), (int)Math.Round (h * _scale));
			Present ();
			return true;
		}

		/// <summary>Form.WindowState set (its own buttons, a caption's double-click): the window maximized to the
		/// screen or restored to its frame (AndroidWindow.SetWindowState), and the form given its new bounds.</summary>
		private void ApplyWindowState (FormWindowState state)
		{
			if (_window == null || IsScreenWindow)
				return;
			_window.SetWindowState (state switch { FormWindowState.Minimized => 6, FormWindowState.Maximized => 3, _ => 9 });
		}

		private static IntPtr s_capturedWindow;

		private static void OnCaptureChanged (IntPtr hwnd)
		{
			if (hwnd == IntPtr.Zero) {
				if (s_capturedWindow != IntPtr.Zero && WindowsBase::MS.Internal.Interop.PlatformWindow.MouseCaptureHandle == s_capturedWindow)
					WindowsBase::MS.Internal.Interop.PlatformWindow.MouseCaptureHandle = IntPtr.Zero;
				s_capturedWindow = IntPtr.Zero;
				return;
			}
			AndroidFormHost host = null;
			if (Control.FromHandle (hwnd)?.TopLevelControl is Form top)
				foreach (AndroidFormHost h in s_activation)
					if (h._form == top)
						host = h;
			host ??= s_active;
			if (host?._window == null || host._closed)
				return;
			s_capturedWindow = host._window.Handle;
			WindowsBase::MS.Internal.Interop.PlatformWindow.MouseCaptureHandle = s_capturedWindow;
		}

		internal AndroidFormHost (Form form)
		{
			_form = form;
			_chrome = new WindowChrome (form);
			PresentationHost.Attach (this, form);
		}

		public void Show ()
		{
			_window = new AndroidWindow ();
			double backing = Math.Max (0.5, _window.GetBackingScale ());
			_k = PerMonitor ? _driver.WindowDpi (_form.Handle) / 96.0 : ScaleHelper.HostSourceDpi / 96.0;
			_scale = backing / _k;
			bool borderless = _form.FormBorderStyle == FormBorderStyle.None;
			// device pixels, origin and size alike
			_window.Create (_form.Text, (int)Math.Round (_form.Left * _scale), (int)Math.Round (_form.Top * _scale),
				Math.Max (1, (int)Math.Round (_form.Width * _scale)), Math.Max (1, (int)Math.Round (_form.Height * _scale)), borderless, ownSurface: true);
			// the resize margin outside the visible edge is see-through, as Windows'; the chrome draws its shadow
			if (_chrome.HasFrame)
				_window.SetOwnFrame ();
			_window.Resized += OnResized;
			EmbeddedScenes.PublishHostWindow (_window.Handle, (float)_scale, _form);
			_form.VisibleChanged += OnVisibleChanged;
			_form.HandleDestroyed += OnHandleDestroyed;
			_form.LocationChanged += OnFormMoved;
			AndroidWindow.MouseInput += OnTouch;
			AndroidWindow.KeyInput += OnKey;
			SyncFromWindow ();
			Activate ();
			Present ();
		}

		/// <summary>The presenter, on the form's thread, for the native window the view has now: it arrives
		/// after the view is made (surfaceCreated), and the activity replaces it each time it stops.</summary>
		private bool EnsurePresenter ()
		{
			IntPtr native = AndroidWindow.GetNativeWindow (_window.Handle);
			if (native == IntPtr.Zero)
				return false;                  // no surface yet, or the activity is stopped
			if (native == _nativeWindow && _wgpu != null)
				return true;
			_wgpu?.Dispose ();
			_wgpu = null;
			var ctx = ProcessGpu.Acquire ();
			IntPtr surface = NativePlatform.CreateWindowSurface (ctx.Instance, _window.Handle);
			if (surface == IntPtr.Zero)
				return false;
			_nativeWindow = native;
			_surfaceW = _form.Width;
			_surfaceH = _form.Height;
			_wgpu = new WgpuPresenter (ctx, surface, _surfaceW, _surfaceH, _scale, srgb: true, threadDevice: true);
			// The frame's margin is see-through (its view's surface is translucent, SetOwnFrame); a window
			// without a frame is cleared to its own colour.
			if (_chrome.HasFrame) {
				_wgpu.SetTransparent (true);
			} else {
				Color bc = _form.BackColor;
				_wgpu.ClearColor = new RgbaColor (SrgbToLinear (bc.R / 255f), SrgbToLinear (bc.G / 255f), SrgbToLinear (bc.B / 255f), 1f);
			}
			_lastVer = -1;
			return true;
		}

		private void Activate ()
		{
			if (_closed || !_form.IsHandleCreated)
				return;
			AndroidFormHost previous = s_active;
			s_activation.Remove (this);
			s_activation.Add (this);
			_window?.Activate ();
			if (previous == this)
				return;
			s_active = this;
			if (previous != null && !previous._closed && previous._form.IsHandleCreated) {
				_driver.HostActivate ((long)previous._form.Handle, false);
				previous.Repaint ();
			}
			_driver.HostActivate ((long)_form.Handle, true);
			Repaint ();
		}

		private void Deactivate ()
		{
			bool wasActive = s_active == this;
			s_activation.Remove (this);
			if (!wasActive)
				return;
			s_active = null;
			if (_form.IsHandleCreated)
				_driver.HostActivate ((long)_form.Handle, false);
			AndroidFormHost next = null;
			foreach (AndroidFormHost h in s_activation)
				if (h._form == _form.Owner)
					next = h;
			next ??= s_activation.Count > 0 ? s_activation [s_activation.Count - 1] : null;
			next?.Activate ();
		}

		/// <summary>The form takes the place its window is in; the screen's own window its size too
		/// (BrowserFormHost.SyncFromWindow).</summary>
		private void SyncFromWindow ()
		{
			if (_window == null || _form.IsDisposed || !_form.IsHandleCreated)
				return;
			_window.GetClientScreenOriginPixels (out int px, out int py);
			int x = (int)Math.Round (px / _scale), y = (int)Math.Round (py / _scale);
			// the window's size, whoever changed it: the screen's (a pinned one), or maximized, restored, sized
			_window.GetPixelSize (out int pw, out int ph);
			int w = (int)Math.Round (pw / _scale), h = (int)Math.Round (ph / _scale);
			if (!IsScreenWindow) {
				FormWindowState state = _window.GetWindowState () == 3 ? FormWindowState.Maximized : FormWindowState.Normal;
				if (_form.WindowState != FormWindowState.Minimized && _driver.GetWindowState (_form.Handle) != state)
					_driver.HostWindowState (_form.Handle, state);
			}
			var bounds = new Rectangle (x, y, w, h);
			if (bounds.Width > 0 && bounds.Height > 0 && _form.Bounds != bounds)
				XplatUI.SetWindowPos (_form.Handle, bounds.X, bounds.Y, bounds.Width, bounds.Height);
		}

		private void OnFormMoved (object sender, EventArgs e)
		{
			if (_closed || _window == null || IsScreenWindow || _form.IsDisposed || !_form.IsHandleCreated)
				return;
			_window.GetClientScreenOriginPixels (out int px, out int py);
			int x = (int)Math.Round (_form.Left * _scale), y = (int)Math.Round (_form.Top * _scale);
			if (x == px && y == py)
				return;
			_window.SetFrameOrigin (x, y);
			_lastVer = -1;
		}

		// the app's own window, pinned to the screen it fills (not on a headset's desktop: AndroidWindow.FillsScreen)
		private bool IsScreenWindow => _window != null && _window.FillsScreen;

		private void OnVisibleChanged (object sender, EventArgs e)
		{
			if (_closed || _window == null)
				return;
			bool visible = _form.Visible;
			_window.SetVisible (visible);
			_lastVer = -1;
			if (_form.Visible)
				Activate ();
			else
				Deactivate ();
		}

		private void OnHandleDestroyed (object sender, EventArgs e)
		{
			Close ();
			PresentationHost.Detach (this);
		}

		private void OnResized (int widthPixels, int heightPixels)
		{
			if (_form.InvokeRequired) {
				if (!_closed && _form.IsHandleCreated) _form.BeginInvoke ((Action)(() => OnResized (widthPixels, heightPixels)));
				return;
			}
			ScreenChanged ();
			SyncFromWindow ();
			_lastVer = -1;
		}

		// ---- present -------------------------------------------------------------------------

		public void Present ()
		{
			if (_closed)
				return;
			if (_window == null || !EnsurePresenter ())
				return;
			int ver = _driver.GetPaintVersion () + EmbeddedScenes.CurrentVersion ();
			bool caretOn = CaretOn ();
			if (ver == _lastVer && caretOn == _lastCaretOn && _lastPresentOk)
				return;

			var scenes = GetScenes (out int ox, out int oy, out int needW, out int needH);
			if (_chrome.HasFrame)
				scenes.Insert (0, (_chrome.Paint (_form.Size, Form.ActiveForm == _form), 0, 0));
			if (needW != _surfaceW || needH != _surfaceH) {
				// a drop-down reaching past the form: the view grows to hold it (shrinks back after)
				_surfaceW = needW;
				_surfaceH = needH;
				if (!IsScreenWindow) {
					int growW = (int)Math.Round (needW * _scale), growH = (int)Math.Round (needH * _scale);
					_window.SetContentSizePixels (growW, growH);
				}
			}
			Rectangle? caret = caretOn && _driver.GetCaret (_form.Handle, out int cx, out int cy, out int cw, out int ch)
				? new Rectangle (cx - ox, cy - oy, Math.Max (1, cw), ch) : null;
			// a headset's window -- an XR host's, a WebXR session's -- shown to the two eyes apart (WgpuPresenter.Stereo)
			_wgpu.Stereo = NativePlatform.IsStereoWindow (_window.Handle);
			_lastPresentOk = _wgpu.PresentScenes (scenes, caret, needW, needH);
			if (s_trace && scenes.Count > 0)
				Console.WriteLine ($"[android form] '{_form.Text}' form={_form.Bounds} scale={_scale} surface={needW}x{needH} ok={_lastPresentOk} scenes={scenes.Count}");
			_lastVer = ver;
			_lastCaretOn = caretOn;
		}

		/// <summary>The form's window and the windows over it, in form space (BrowserFormHost.GetScenes).</summary>
		private List<(object, int, int)> GetScenes (out int ox, out int oy, out int needW, out int needH)
		{
			long[] wins = _driver.GetPresentWindows (_form.Handle);
			Padding frame = _chrome.Frame;
			ox = (wins.Length >= 3 ? (int)wins [1] : _form.Left + frame.Left) - frame.Left;
			oy = (wins.Length >= 3 ? (int)wins [2] : _form.Top + frame.Top) - frame.Top;
			needW = _form.Width;
			needH = _form.Height;
			var list = new List<(object, int, int)> (wins.Length / 3);
			var roots = new List<IntPtr> (wins.Length / 3);
			for (int i = 0; i + 2 < wins.Length; i += 3) {
				IntPtr h = (IntPtr)wins [i];
				object scene = _driver.GetWindowScene (h);
				if (scene == null)
					continue;
				int px = (int)wins [i + 1] - ox, py = (int)wins [i + 2] - oy;
				list.Add ((scene, px, py));
				roots.Add (Control.FromHandle (h)?.TopLevelControl is Control top && top.IsHandleCreated ? top.Handle : h);
				if (!_driver.IsInPopupWindow (h))
					continue;
				long size = _driver.GetWindowSizePacked (h);
				int w = (int)(size >> 32), hh = (int)(size & 0xFFFFFFFF);
				needW = Math.Max (needW, px + w);
				needH = Math.Max (needH, py + hh);
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

		// ---- input ---------------------------------------------------------------------------

		public bool Pump () => !_closed;   // input arrives through AndroidWindow's events

		private bool Mine (IntPtr view) => !_closed && _window != null && view == _window.Handle && _form.IsHandleCreated && !_form.IsDisposed;

		/// <summary>A touch, as the left button: kind 0 move, 1 press, 2 release, 3 wheel.</summary>
		// where a press was aimed, from where the finger is (TouchTarget), for the rest of its drag
		private int _aimX, _aimY;

		private void OnTouch (AndroidWindow.TouchMessage m)
		{
			// UIKit's touches come on the main thread; a form of another thread takes them on its own
			if (!_closed && _window != null && m.Window == _window.Handle && _form.InvokeRequired) {
				if (_form.IsHandleCreated) _form.BeginInvoke ((Action)(() => OnTouch (m)));
				return;
			}
			if (_dragging && !_closed && OnDrag (m))
				return;
			if (_sizing != 0 && !_closed && OnSize (m))
				return;
			if (s_trace && m.Kind != 0)
				Console.WriteLine ($"[android form] touch {m.Kind} at ({m.X},{m.Y}) view=0x{m.Window:x} mine={Mine (m.Window)} '{_form.Text}'");
			if (!Mine (m.Window))
				return;
			// device pixels in the view -> the screen's points (the view is the window's outer rectangle,
			// whose origin is the form's Location)
			int x = (int)Math.Floor (m.X / _scale) + _form.Left;
			int y = (int)Math.Floor (m.Y / _scale) + _form.Top;
			_pointer = new Point (x, y);
			if (m.Kind == 1 && _form.Enabled)
				Activate ();
			if (_chrome.HasFrame && OnFrame (m, x, y))
				return;
			if (!IsScreenWindow && OnNonClient (m, x, y)) {
				Present ();
				return;
			}
			IntPtr outerRoot = XplatUIWebGpu.InputRoot;
			XplatUIWebGpu.InputRoot = _form.Handle;
			try {
				switch (m.Kind) {
				case 0: _driver.InjectMouseMove (x + _aimX, y + _aimY, _leftDown); break;
				case 1:
					// a finger is wider than a splitter: the press goes where a resize cursor is within
					// its reach (TouchTarget), and the drag keeps that offset
					var aim = _driver.TouchTarget (x, y);
					_aimX = aim.X - x; _aimY = aim.Y - y;
					_leftDown = true;
					_driver.InjectMouseDown (aim.X, aim.Y);
					break;
				case 2:
					_leftDown = false;
					x += _aimX; y += _aimY;
					_aimX = _aimY = 0;
					_driver.InjectMouseUp (x, y);
					// a finger leaves no pointer behind (LiftPointer): the button tapped stayed lit; a
					// controller's ray stays, as a mouse does -- lifted, the menu it opened closed at once
					if (!m.Pointer)
						_driver.LiftPointer ();
					break;
				case 3: _driver.InjectWheel (x, y, m.Wheel); break;
				case AndroidWindow.RightPress: _driver.InjectRightDown (x, y); break;   // press and hold
				case AndroidWindow.RightRelease: _driver.InjectRightUp (x, y); break;
				}
			} finally {
				XplatUIWebGpu.InputRoot = outerRoot;
			}
			Present ();
		}

		// ---- a frame the form draws itself (a ribbon's): its WM_NCHITTEST, as WaylandFormHost asks it ----

		private const int HTCLIENT = 1;
		private int _ncHit;                  // the frame part the pointer was last over, 0 when the client
		private long _ncPressTime;
		private Point _ncPressAt;

		/// <summary>A press, move or release over the form's own frame (x, y: the screen's points): sent to the
		/// form as Windows sends it (WM_NCLBUTTONDOWN and the rest, with its hit), which leaves a caption or an
		/// edge to DefWindowProc -- whose move and size come back here (BeginSystemMove/Size); false when the
		/// point is the client's.</summary>
		private bool OnNonClient (AndroidWindow.TouchMessage m, int x, int y)
		{
			// a window holding the mouse (a control that captured it) gets everything as its client's
			if (_driver.GrabHandle != IntPtr.Zero || m.Kind > 2)
				return false;
			IntPtr lp = (IntPtr)(((y & 0xFFFF) << 16) | (x & 0xFFFF));
			// The sizing border is the form's, whatever lies under it: on Windows the frame is outside the
			// children (a ribbon form keeps a 5-pixel border there). The rest the deepest window answers first
			// (a ribbon's caption buttons are its client, though the form calls the strip around them
			// caption); only a caption or an edge is the frame's.
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
			if (s_trace && m.Kind != 0) Console.WriteLine ($"[android form] '{_form.Text}' 0x{message:x} hit {hit} at ({x},{y})");
			XplatUI.SendMessage (_form.Handle, (Msg)message, (IntPtr)hit, lp);
			return true;
		}

		// ---- the frame (WindowChrome): DefWindowProc's part (BrowserFormHost.OnFrame) -------------

		private bool OnFrame (AndroidWindow.TouchMessage m, int x, int y)
		{
			int hit = _chrome.HitTest (new Point (x - _form.Left, y - _form.Top), _form.Size);
			bool down = m.Kind == 1, up = m.Kind == 2;
			if (_chrome.Pressed == WindowChrome.HTCLOSE) {
				if (up) {
					_chrome.Track (hit, WindowChrome.HTNOWHERE);
					Repaint ();
					if (hit == WindowChrome.HTCLOSE)
						_chrome.Close ();
				} else if (_chrome.Track (hit, WindowChrome.HTCLOSE)) {
					Repaint ();
				}
				return true;
			}
			if (hit == WindowChrome.HTCLIENT || hit == WindowChrome.HTNOWHERE) {
				if (_chrome.Track (WindowChrome.HTNOWHERE, WindowChrome.HTNOWHERE))
					Repaint ();
				return false;
			}
			if (down && !_form.Enabled)
				return true;
			if (down && hit == WindowChrome.HTCAPTION) {
				_dragging = true;
				_dragFrom = new Point (x, y);
				_dragOrigin = _form.Location;
				Activate ();
				return true;
			}
			if (_chrome.Track (hit, down && hit == WindowChrome.HTCLOSE ? WindowChrome.HTCLOSE : WindowChrome.HTNOWHERE))
				Repaint ();
			return true;
		}

		/// <summary>A move in progress: the window where the finger has taken it.</summary>
		private bool OnDrag (AndroidWindow.TouchMessage m)
		{
			if (m.Kind == 2) {
				_dragging = false;
				return true;
			}
			if (m.Kind != 0)
				return true;
			AndroidWindow over = AndroidWindow.FromHandle (m.Window);
			if (over == null)
				return true;
			over.GetClientScreenOriginPixels (out int sx, out int sy);
			int x = (int)Math.Floor ((m.X + sx) / _scale), y = (int)Math.Floor ((m.Y + sy) / _scale);
			if (_restoreOnDrag && (Math.Abs (x - _dragFrom.X) > 4 || Math.Abs (y - _dragFrom.Y) > 4)) {
				// a maximized window taken by its caption: restored under the pointer as it moves, as on Windows --
				// the same fraction of its width under the pointer, the caption where it is held
				_restoreOnDrag = false;
				Rectangle was = _form.Bounds;
				double across = was.Width > 0 ? (double)(_dragFrom.X - was.Left) / was.Width : 0.5;
				int down = _dragFrom.Y - was.Top;
				_window.SetWindowState (9);
				SyncFromWindow ();
				_dragOrigin = new Point (_dragFrom.X - (int)Math.Round (across * _form.Width), _dragFrom.Y - Math.Min (down, Math.Max (0, _form.Height - 1)));
			}
			var to = new Point (_dragOrigin.X + x - _dragFrom.X, _dragOrigin.Y + y - _dragFrom.Y);
			to.Y = Math.Max (0, to.Y);
			int fx = (int)Math.Round (to.X * _scale), fy = (int)Math.Round (to.Y * _scale);
			_window.SetFrameOrigin (fx, fy);
			XplatUI.SetWindowPos (_form.Handle, to.X, to.Y, _form.Width, _form.Height);
			Present ();
			return true;
		}

		/// <summary>A hardware key, for the window that has the keys (AndroidWindow.KeyInput), as BrowserFormHost
		/// takes the page's: the key down or up (WM_KEYDOWN/UP), and the typed character (WM_CHAR).</summary>
		private void OnKey (AndroidWindow.KeyMessage k)
		{
			if (_closed || _window == null || !_form.IsHandleCreated || _form.IsDisposed)
				return;
			// a child window that took the keyboard (an ElementHost's WPF tree) has its own keys
			if (AndroidWindow.FromHandle (k.Window) is { TakesKeyboardOnPress: true })
				return;
			Form active = Form.ActiveForm;
			if (active != null ? active != _form : k.Window != _window.Handle)
				return;
			if (_form.InvokeRequired) {
				_form.BeginInvoke ((Action)(() => OnKey (k)));
				return;
			}
			Keys modifiers = (k.Ctrl ? Keys.Control : 0) | (k.Shift ? Keys.Shift : 0) | (k.Alt ? Keys.Alt : 0);
			_driver.SetModifierKeys (modifiers);
			int vk = k.VirtualKey;
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
			Deactivate ();
			_closed = true;
			_form.VisibleChanged -= OnVisibleChanged;
			_form.LocationChanged -= OnFormMoved;
			_form.HandleDestroyed -= OnHandleDestroyed;
			AndroidWindow.MouseInput -= OnTouch;
			AndroidWindow.KeyInput -= OnKey;
			if (_window != null) {
				_window.Resized -= OnResized;
				_window.Destroy ();
			}
			_wgpu?.Dispose ();
			_wgpu = null;
		}
	}
}
