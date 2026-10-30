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
			_window.Resized += OnResized;
			EmbeddedScenes.PublishHostWindow (_window.Handle, (float)_scale, _form);
			_form.VisibleChanged += OnVisibleChanged;
			_form.HandleDestroyed += OnHandleDestroyed;
			_form.LocationChanged += OnFormMoved;
			AndroidWindow.MouseInput += OnTouch;
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
			// The frame's see-through margin needs a translucent surface, which a SurfaceView is not: the
			// form's own colour fills it.
			Color bc = _form.BackColor;
			_wgpu.ClearColor = new RgbaColor (SrgbToLinear (bc.R / 255f), SrgbToLinear (bc.G / 255f), SrgbToLinear (bc.B / 255f), 1f);
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
			int w = _form.Width, h = _form.Height;
			if (IsScreenWindow) {
				_window.GetPixelSize (out int pw, out int ph);
				w = (int)Math.Round (pw / _scale);
				h = (int)Math.Round (ph / _scale);
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

		private bool IsScreenWindow => _window != null && !_window.IsSecondary && !_window.IsBorderless;

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
		private void OnTouch (AndroidWindow.TouchMessage m)
		{
			// UIKit's touches come on the main thread; a form of another thread takes them on its own
			if (!_closed && _window != null && m.Window == _window.Handle && _form.InvokeRequired) {
				if (_form.IsHandleCreated) _form.BeginInvoke ((Action)(() => OnTouch (m)));
				return;
			}
			if (_dragging && !_closed && OnDrag (m))
				return;
			if (s_trace && m.Kind != 0)
				Console.WriteLine ($"[android form] touch {m.Kind} at ({m.X},{m.Y}) view=0x{m.Window:x} mine={Mine (m.Window)} '{_form.Text}'");
			if (!Mine (m.Window))
				return;
			// device pixels in the view -> the screen's points (the view is the window's outer rectangle,
			// whose origin is the form's Location)
			int x = (int)Math.Floor (m.X / _scale) + _form.Left;
			int y = (int)Math.Floor (m.Y / _scale) + _form.Top;
			if (m.Kind == 1 && _form.Enabled)
				Activate ();
			if (_chrome.HasFrame && OnFrame (m, x, y))
				return;
			IntPtr outerRoot = XplatUIWebGpu.InputRoot;
			XplatUIWebGpu.InputRoot = _form.Handle;
			try {
				switch (m.Kind) {
				case 0: _driver.InjectMouseMove (x, y, _leftDown); break;
				case 1: _leftDown = true; _driver.InjectMouseDown (x, y); break;
				case 2: _leftDown = false; _driver.InjectMouseUp (x, y); break;
				case 3: _driver.InjectWheel (x, y, m.Wheel); break;
				}
			} finally {
				XplatUIWebGpu.InputRoot = outerRoot;
			}
			Present ();
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
			var to = new Point (_dragOrigin.X + x - _dragFrom.X, _dragOrigin.Y + y - _dragFrom.Y);
			to.Y = Math.Max (0, to.Y);
			int fx = (int)Math.Round (to.X * _scale), fy = (int)Math.Round (to.Y * _scale);
			_window.SetFrameOrigin (fx, fy);
			XplatUI.SetWindowPos (_form.Handle, to.X, to.Y, _form.Width, _form.Height);
			Present ();
			return true;
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
			if (_window != null) {
				_window.Resized -= OnResized;
				_window.Destroy ();
			}
			_wgpu?.Dispose ();
			_wgpu = null;
		}
	}
}
