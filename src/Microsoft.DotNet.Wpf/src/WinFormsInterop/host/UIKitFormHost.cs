// The iOS head's window for a top-level form: a view of the screen, made and placed by the same window
// manager as WPF's windows on that head (MS.Internal.Interop.UIKitWindow), so forms and WPF windows
// share one set of rules for placement, stacking and input -- the browser head's (BrowserFormHost),
// whose design this is. The present path is the one every host shares (the driver's scenes through
// WgpuPresenter); only the surface (a Metal-backed view) and the source of input (UIKit's touches) are
// iOS's.
//
// The screen is the root view: its points are the forms' screen coordinates at 96 dpi. The first framed
// window fills it, as the main window of an application on a screen of its own; later ones float over
// it, with the frame Windows would draw (WindowChrome), and the form follows where its window goes.
//
// UIKit's views are the main thread's. A form of another thread -- a splash screen an app shows from a
// thread of its own while the main thread loads -- has its view made, moved and shown there (OnMain,
// CocoaHost's arrangement), and is presented from its own thread once its surface exists.

extern alias WindowsBase;

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using UIKitWindow = WindowsBase::MS.Internal.Interop.UIKitWindow;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using Microsoft.Wpf.Interop.WebGpu.Composition.Platform;

namespace System.Windows.Forms
{
	[SupportedOSPlatform ("ios")]
	internal sealed unsafe class UIKitFormHost : IWinFormsHost
	{
		private readonly Form _form;
		private readonly XplatUIWebGpu _driver = XplatUIWebGpu.GetInstance ();
		private readonly System.Diagnostics.Stopwatch _blink = System.Diagnostics.Stopwatch.StartNew ();
		private UIKitWindow _window;
		private WgpuPresenter _wgpu;
		private IntPtr _surface;              // made on the main thread with the view (MakeWindow)
		private bool _shown;                  // the presenter made, on the form's thread (FinishShow)
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

		private static readonly List<UIKitFormHost> s_activation = new List<UIKitFormHost> ();
		private static UIKitFormHost s_active;
		private static readonly bool s_trace = Environment.GetEnvironmentVariable ("WF_TRACE_IOS") == "1";

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
			UIKitWindow.GetPrimaryScreenPixels (out _, out _, out int right, out int bottom, out _, out _, out _, out _);
			double perDevice = PerMonitor || ScaleHelper.HostSourceDpi <= 0 ? 1 : 96.0 / ScaleHelper.HostSourceDpi * Math.Max (0.5, new UIKitWindow ().GetBackingScale ());
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
			double scale = new UIKitWindow ().GetBackingScale ();
			return scale > 0 ? (int)Math.Round (96 * scale) : 0;
		}

		static UIKitFormHost ()
		{
			// for long work the main thread does outside WinForms -- the app's own start-up, in WPF, while
			// its splash screen waits on a thread of its own for the view the main thread makes: WPF's
			// dispatcher and the assembly loader keep the work other threads queue for it moving (MainWork)
			if (AppContext.GetData ("WinFormsWebGpu.DrainMainWork") == null) AppContext.SetData ("WinFormsWebGpu.DrainMainWork", (Action)DrainMainWork);
			// a form's Metal layer is UIKit's: its surface configured on the main thread (WgpuPresenter.ConfigureOn)
			WgpuPresenter.ConfigureOn ??= OnMainAndWait;
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
			UIKitFormHost host = null;
			if (Control.FromHandle (hwnd)?.TopLevelControl is Form top)
				foreach (UIKitFormHost h in s_activation)
					if (h._form == top)
						host = h;
			host ??= s_active;
			if (host?._window == null || host._closed)
				return;
			s_capturedWindow = host._window.Handle;
			WindowsBase::MS.Internal.Interop.PlatformWindow.MouseCaptureHandle = s_capturedWindow;
		}

		internal UIKitFormHost (Form form)
		{
			_form = form;
			_chrome = new WindowChrome (form);
			PresentationHost.Attach (this, form);
		}

		public void Show ()
		{
			double backing = Math.Max (0.5, new UIKitWindow ().GetBackingScale ());
			_k = PerMonitor ? _driver.WindowDpi (_form.Handle) / 96.0 : ScaleHelper.HostSourceDpi / 96.0;
			_scale = backing / _k;
			bool borderless = _form.FormBorderStyle == FormBorderStyle.None;
			var bounds = new Rectangle ((int)Math.Round (_form.Left * _scale), (int)Math.Round (_form.Top * _scale),
				Math.Max (1, (int)Math.Round (_form.Width * _scale)), Math.Max (1, (int)Math.Round (_form.Height * _scale)));
			string title = _form.Text;
			_form.VisibleChanged += OnVisibleChanged;
			_form.HandleDestroyed += OnHandleDestroyed;
			_form.LocationChanged += OnFormMoved;
			OnMain (() => MakeWindow (title, bounds, borderless));
			if (_surface != IntPtr.Zero)
				FinishShow ();                 // the main thread's own form: all done here
		}

		/// <summary>The view and its surface, on the main thread (UIKit's).</summary>
		private void MakeWindow (string title, Rectangle bounds, bool borderless)
		{
			if (_closed)
				return;
			var window = new UIKitWindow ();
			window.Create (title, bounds.X, bounds.Y, bounds.Width, bounds.Height, borderless, ownSurface: true);
			window.Resized += OnResized;
			EmbeddedScenes.PublishHostWindow (window.Handle, (float)_scale, _form);
			UIKitWindow.MouseInput += OnTouch;
			UIKitWindow.KeyInput += OnKey;
			var ctx = ProcessGpu.Acquire ();
			IntPtr surface = NativePlatform.CreateWindowSurface (ctx.Instance, window.Handle);
			if (surface == IntPtr.Zero)
				throw new InvalidOperationException ("WinForms: no WebGPU surface for the form's view");
			_window = window;
			Volatile.Write (ref _surface, surface);
		}

		/// <summary>The presenter, on the form's thread, once its view and surface exist.</summary>
		private void FinishShow ()
		{
			_shown = true;
			SyncFromWindow ();
			_surfaceW = _form.Width;
			_surfaceH = _form.Height;
			_wgpu = new WgpuPresenter (ProcessGpu.Acquire (), _surface, _surfaceW, _surfaceH, _scale, srgb: true, threadDevice: true);
			if (_chrome.HasFrame) {
				// the resize margin outside the visible edge is see-through, as Windows'; the chrome draws its shadow
				_wgpu.SetTransparent (true);
				OnMain (() => _window?.SetOwnFrame ());
			} else {
				Color bc = _form.BackColor;
				_wgpu.ClearColor = new RgbaColor (SrgbToLinear (bc.R / 255f), SrgbToLinear (bc.G / 255f), SrgbToLinear (bc.B / 255f), 1f);
			}
			Activate ();
			Present ();
		}

		// ---- the main thread ------------------------------------------------------------------
		//
		// Work for the main thread is queued twice, as CocoaHost queues it: to the main dispatch queue,
		// which runs it when the main thread next turns its run loop, and here, where the main thread picks
		// it up at its next call into the driver (DrainMainWork) -- a main thread busy loading the app
		// does not turn its run loop for seconds, and a splash screen's window waited for it.

		private sealed class MainWork
		{
			private Action _action;
			public MainWork (Action action) => _action = action;

			public void Run ()
			{
				Action action = Interlocked.Exchange (ref _action, null);
				if (action == null) return;
				try { action (); }
				catch (Exception e) { Console.Error.WriteLine ($"[UIKitFormHost] main-thread work failed: {e}"); }
				// Committed now: Core Animation commits a run-loop pass's changes as the pass ends, and the
				// main thread runs this from inside its dispatcher's wait, then goes back to the app's own
				// work -- a splash screen's view, made and shown here while the app loads, came on screen
				// only when the loading was over, for a moment before the splash closed.
				try { NativePlatform.CommitPresent (); }
				catch (Exception) { }
			}
		}

		private static readonly System.Collections.Concurrent.ConcurrentQueue<MainWork> s_mainWork = new ();
		private static IntPtr s_mainQueue;

		/// <summary>Runs <paramref name="action"/> on the main thread: now if this is it, else queued.</summary>
		private static unsafe void OnMain (Action action)
		{
			if (pthread_main_np () != 0) { action (); return; }
			if (s_mainQueue == IntPtr.Zero)
				s_mainQueue = NativeLibrary.GetExport (NativeLibrary.Load ("/usr/lib/libSystem.dylib"), "_dispatch_main_q");
			var work = new MainWork (action);
			s_mainWork.Enqueue (work);
			GCHandle handle = GCHandle.Alloc (work);
			dispatch_async_f (s_mainQueue, GCHandle.ToIntPtr (handle), (IntPtr)(delegate* unmanaged<IntPtr, void>)&RunQueued);
		}

		/// <summary>Runs <paramref name="action"/> on the main thread and waits for it: now if this is it.</summary>
		private static void OnMainAndWait (Action action)
		{
			if (pthread_main_np () != 0) { action (); return; }
			using var done = new ManualResetEventSlim ();
			Exception failed = null;
			OnMain (() => { try { action (); } catch (Exception e) { failed = e; } finally { done.Set (); } });
			done.Wait ();
			if (failed != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture (failed).Throw ();
		}

		/// <summary>On the main thread, runs the work other threads have queued for it (see MainWork).</summary>
		internal static void DrainMainWork ()
		{
			if (s_mainWork.IsEmpty || pthread_main_np () == 0) return;
			while (s_mainWork.TryDequeue (out MainWork work)) work.Run ();
		}

		[UnmanagedCallersOnly]
		private static void RunQueued (IntPtr context)
		{
			GCHandle handle = GCHandle.FromIntPtr (context);
			var work = (MainWork)handle.Target;
			handle.Free ();
			work.Run ();
		}

		[DllImport ("/usr/lib/libSystem.dylib")] private static extern int pthread_main_np ();
		[DllImport ("/usr/lib/libSystem.dylib")] private static extern void dispatch_async_f (IntPtr queue, IntPtr context, IntPtr work);
		/// <summary>A press on the form while a modal dialog has disabled it (EnableWindow -- WPF's dialog's, or this
		/// toolkit's own modal loop's): Windows delivers nothing and activates the dialog instead, which stays over
		/// the form. A press activated the disabled form and brought it over its own modal dialog.</summary>
		private void PressOnDisabled ()
		{
			if (WindowsBase::MS.Internal.Interop.PlatformWindow.DisabledWindowPressed is { } dialog) {
				dialog (_window?.Handle ?? IntPtr.Zero);
				return;
			}
			foreach (var h in s_activation.ToArray ())
				if (h != this && !h._closed && h._form.Modal && h._form.Visible) {
					h.Activate ();
					return;
				}
		}


		private void Activate ()
		{
			if (_closed || !_form.IsHandleCreated)
				return;
			UIKitFormHost previous = s_active;
			s_activation.Remove (this);
			s_activation.Add (this);
			OnMain (() => _window?.Activate ());
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
			UIKitFormHost next = null;
			foreach (UIKitFormHost h in s_activation)
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
				Padding over = MaximizedOverhang;
				_syncedOverhang = over;
				x -= over.Left;
				y -= over.Top;
				w = (int)Math.Round (pw / _scale) + over.Horizontal;
				h = (int)Math.Round (ph / _scale) + over.Vertical;
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
			OnMain (() => _window?.SetFrameOrigin (x, y));
			_lastVer = -1;
		}

		private bool IsScreenWindow => _window != null && !_window.IsSecondary && !_window.IsBorderless;
		private Padding _syncedOverhang;

		/// <summary>How far a maximized form reaches past the screen it fills: as Windows maximizes a window --
		/// the screen's size and its sizing borders past each edge (WM_GETMINMAXINFO's default position), so
		/// what shows is the client and the caption, not the frame's edges, resize margin and shadow. A form
		/// left normal in the screen's window keeps its frame on screen, as a normal window that size has.</summary>
		private Padding MaximizedOverhang {
			get {
				if (!IsScreenWindow || !_chrome.HasFrame || _driver.GetWindowState (_form.Handle) != FormWindowState.Maximized)
					return Padding.Empty;
				Padding f = _chrome.Frame;
				return new Padding (f.Left, f.Bottom, f.Right, f.Bottom);
			}
		}

		private void OnVisibleChanged (object sender, EventArgs e)
		{
			if (_closed || _window == null)
				return;
			bool visible = _form.Visible;
			OnMain (() => _window?.SetVisible (visible));
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
			if (!_shown) {
				if (Volatile.Read (ref _surface) == IntPtr.Zero)
					return;                    // its view is still being made on the main thread
				FinishShow ();
				return;
			}
			if (_wgpu == null)
				return;
			int ver = _driver.GetPaintVersion () + EmbeddedScenes.CurrentVersion ();
			bool caretOn = CaretOn ();
			if (ver == _lastVer && caretOn == _lastCaretOn && _lastPresentOk)
				return;

			// maximized or restored since it was placed (the caption's buttons, Form.WindowState): placed again
			if (IsScreenWindow && MaximizedOverhang != _syncedOverhang)
				SyncFromWindow ();
			var scenes = GetScenes (out int ox, out int oy, out int needW, out int needH);
			if (_chrome.HasFrame)
				scenes.Insert (0, (_chrome.Paint (_form.Size, Form.ActiveForm == _form), 0, 0));
			// a maximized form reaches past the screen's window (MaximizedOverhang): drawn where it is in it
			Padding over = MaximizedOverhang;
			if (over != Padding.Empty) {
				for (int i = 0; i < scenes.Count; i++)
					scenes [i] = (scenes [i].Item1, scenes [i].Item2 - over.Left, scenes [i].Item3 - over.Top);
				ox += over.Left;
				oy += over.Top;
				needW -= over.Horizontal;
				needH -= over.Vertical;
			}
			if (needW != _surfaceW || needH != _surfaceH) {
				// a drop-down reaching past the form: the view grows to hold it (shrinks back after)
				_surfaceW = needW;
				_surfaceH = needH;
				if (!IsScreenWindow) {
					int growW = (int)Math.Round (needW * _scale), growH = (int)Math.Round (needH * _scale);
					OnMain (() => _window?.SetContentSizePixels (growW, growH));
				}
			}
			Rectangle? caret = caretOn && _driver.GetCaret (_form.Handle, out int cx, out int cy, out int cw, out int ch)
				? new Rectangle (cx - ox, cy - oy, Math.Max (1, cw), ch) : null;
			_lastPresentOk = _wgpu.PresentScenes (scenes, caret, needW, needH);
			if (s_trace && scenes.Count > 0)
				Console.WriteLine ($"[ios form] '{_form.Text}' form={_form.Bounds} scale={_scale} surface={needW}x{needH} ok={_lastPresentOk} scenes={scenes.Count}");
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

		public bool Pump () => !_closed;   // input arrives through UIKitWindow's events

		private bool Mine (IntPtr view) => !_closed && _window != null && view == _window.Handle && _form.IsHandleCreated && !_form.IsDisposed;

		/// <summary>A touch, as the left button: kind 0 move, 1 press, 2 release, 3 wheel.</summary>
		// where a press was aimed, from where the finger is (TouchTarget), for the rest of its drag
		private int _aimX, _aimY;

		private void OnTouch (UIKitWindow.TouchMessage m)
		{
			// UIKit's touches come on the main thread; a form of another thread takes them on its own
			if (!_closed && _window != null && m.View == _window.Handle && _form.InvokeRequired) {
				if (_form.IsHandleCreated) _form.BeginInvoke ((Action)(() => OnTouch (m)));
				return;
			}
			if (_dragging && !_closed && OnDrag (m))
				return;
			if (s_trace && m.Kind != 0)
				Console.WriteLine ($"[ios form] touch {m.Kind} at ({m.X},{m.Y}) view=0x{m.View:x} mine={Mine (m.View)} '{_form.Text}'");
			if (!Mine (m.View))
				return;
			// device pixels in the view -> the screen's points (the view is the window's outer rectangle,
			// whose origin is the form's Location)
			int x = (int)Math.Floor (m.X / _scale) + _form.Left;
			int y = (int)Math.Floor (m.Y / _scale) + _form.Top;
			// disabled by a modal dialog: the press is the dialog's (PressOnDisabled), nothing reaches the form
			if (_form.IsHandleCreated && !XplatUI.IsEnabled (_form.Handle)) {
				if (m.Kind == 1)
					PressOnDisabled ();
				return;
			}
			if (m.Kind == 1 && _form.Enabled)
				Activate ();
			if (_chrome.HasFrame && OnFrame (m, x, y))
				return;
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
					// a finger leaves no pointer behind (LiftPointer): the button tapped stayed lit
					_driver.LiftPointer ();
					break;
				case 3: _driver.InjectWheel (x, y, m.Wheel); break;
				case UIKitWindow.RightPress: _driver.InjectRightDown (x, y); break;   // press and hold
				case UIKitWindow.RightRelease: _driver.InjectRightUp (x, y); break;
				}
			} finally {
				XplatUIWebGpu.InputRoot = outerRoot;
			}
			Present ();
		}

		// ---- the frame (WindowChrome): DefWindowProc's part (BrowserFormHost.OnFrame) -------------

		private bool OnFrame (UIKitWindow.TouchMessage m, int x, int y)
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
		private bool OnDrag (UIKitWindow.TouchMessage m)
		{
			if (m.Kind == 2) {
				_dragging = false;
				return true;
			}
			if (m.Kind != 0)
				return true;
			UIKitWindow over = UIKitWindow.FromHandle (m.View);
			if (over == null)
				return true;
			over.GetClientScreenOriginPixels (out int sx, out int sy);
			int x = (int)Math.Floor ((m.X + sx) / _scale), y = (int)Math.Floor ((m.Y + sy) / _scale);
			var to = new Point (_dragOrigin.X + x - _dragFrom.X, _dragOrigin.Y + y - _dragFrom.Y);
			to.Y = Math.Max (0, to.Y);
			int fx = (int)Math.Round (to.X * _scale), fy = (int)Math.Round (to.Y * _scale);
			OnMain (() => _window?.SetFrameOrigin (fx, fy));
			XplatUI.SetWindowPos (_form.Handle, to.X, to.Y, _form.Width, _form.Height);
			Present ();
			return true;
		}

		/// <summary>A hardware key, for the window that has the keys (UIKitWindow.KeyInput), as BrowserFormHost
		/// takes the page's: the key down or up (WM_KEYDOWN/UP), and the typed character (WM_CHAR).</summary>
		private void OnKey (UIKitWindow.UIKitKeyMessage k)
		{
			if (_closed || _window == null || !_form.IsHandleCreated || _form.IsDisposed)
				return;
			// a child window that took the keyboard (an ElementHost's WPF tree) has its own keys
			if (UIKitWindow.FromHandle (k.View) is { TakesKeyboardOnPress: true })
				return;
			Form active = Form.ActiveForm;
			if (active != null ? active != _form : k.View != _window.Handle)
				return;
			if (_form.InvokeRequired) {
				_form.BeginInvoke ((Action)(() => OnKey (k)));
				return;
			}
			// Command is Control, as on the other Apple head
			Keys modifiers = (k.Ctrl || k.Meta ? Keys.Control : 0) | (k.Shift ? Keys.Shift : 0) | (k.Alt ? Keys.Alt : 0);
			_driver.SetModifierKeys (modifiers);
			int vk = k.VirtualKey;
			if (!k.IsDown) {
				if (vk != 0) _driver.InjectKeyUp (vk);
				return;
			}
			bool consumed = vk != 0 && _driver.InjectKeyDown (vk);
			// WM_CHAR, as TranslateMessage makes it: the typed character, and the control characters
			// Windows translates keys into (Backspace, Tab, Enter, Escape); not Apple's private-use
			// characters of the arrows and function keys
			if (!consumed) {
				string chars = k.Characters;
				if (chars is { Length: 1 } && chars [0] >= ' ' && chars [0] != '\x7f' && (chars [0] < '\uF700' || chars [0] > '\uF8FF') && !k.Ctrl && !k.Meta)
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
			OnMain (() => {
				UIKitWindow.MouseInput -= OnTouch;
				UIKitWindow.KeyInput -= OnKey;
				if (_window != null) {
					_window.Resized -= OnResized;
					_window.Destroy ();
				}
			});
			_wgpu?.Dispose ();
			_wgpu = null;
		}
	}
}
