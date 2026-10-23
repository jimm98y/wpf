// The browser head's window for a top-level form: a canvas of the page, made and placed by the same
// window manager as WPF's windows on that head (MS.Internal.Interop.BrowserWindow, browser-window.js),
// so forms and WPF windows share one set of rules for placement, stacking and input. The present path
// is the one every host shares (the driver's scenes through WgpuPresenter); only the surface (a canvas)
// and the source of input (DOM events, drained by BrowserWindow.PumpEvents) are the browser's.
//
// The page is the screen: its CSS pixels are the forms' screen coordinates. The window manager decides
// where a window goes -- the first framed window fills the page, as the main window of an application
// on a screen of its own; later ones are placed over it -- and the form follows, as a form follows a
// window the system moved.

extern alias WindowsBase;

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.Versioning;
using BrowserWindow = WindowsBase::MS.Internal.Interop.BrowserWindow;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using Microsoft.Wpf.Interop.WebGpu.Composition.Platform;

namespace System.Windows.Forms
{
	[SupportedOSPlatform ("browser")]
	internal sealed class BrowserFormHost : IWinFormsHost
	{
		private readonly Form _form;
		private readonly XplatUIWebGpu _driver = XplatUIWebGpu.GetInstance ();
		private readonly System.Diagnostics.Stopwatch _blink = System.Diagnostics.Stopwatch.StartNew ();
		private BrowserWindow _window;
		private WgpuPresenter _wgpu;
		// device pixels per driver unit; and driver units per CSS pixel -- 1 for an application laid
		// out at 96 dpi, the devicePixelRatio for a per-monitor one, which lays out in device pixels
		// (ScaleHelper.HostSourceDpi 0) and is rendered 1:1
		private double _scale = 1, _k = 1;
		private int _lastVer = -1, _surfaceW, _surfaceH;
		private bool _lastCaretOn, _lastPresentOk, _leftDown, _closed;
		private int _traced, _tracedScenes;
		// the frame the page has no window manager to draw (WindowChrome): a dialog's title bar and edges
		private readonly WindowChrome _chrome;
		private bool _dragging;

		// The active window (WM_ACTIVATE), which the page has no window manager to keep: the one shown or
		// clicked last, as Windows activates a window; the windows in the order they were active.
		private static readonly List<BrowserFormHost> s_activation = new List<BrowserFormHost> ();
		private static BrowserFormHost s_active;
		private Point _dragFrom, _dragOrigin;
		private static readonly bool s_trace = Environment.GetEnvironmentVariable ("WF_TRACE_BROWSER") == "1";

		private static bool PerMonitor => ScaleHelper.IsDeviceScaled && ScaleHelper.HostSourceDpi == 0;

		/// <summary>The page's DPI as Windows would give it: 96 per CSS pixel.</summary>
		internal static int PageDpi ()
		{
			double ratio = new BrowserWindow ().GetBackingScale ();
			return ratio > 0 ? (int)Math.Round (96 * ratio) : 0;
		}

		/// <summary>A per-monitor application told when the page's devicePixelRatio changes (the page
		/// zoomed, the browser window moved to another display), as Windows tells a window that moved
		/// to another monitor: the same size in CSS pixels, in the new DPI's pixels.</summary>
		private void FollowBacking (double backing)
		{
			if (!PerMonitor || backing <= 0 || !_form.IsHandleCreated || _form.IsDisposed)
				return;
			int dpi = (int)Math.Round (96 * backing);
			if (Math.Abs (backing - _k) < 0.001) {
				if (_driver.WindowDpi (_form.Handle) != dpi)
					_driver.SetWindowDpi (_form.Handle, dpi);
				return;
			}
			double old = _k;
			_k = backing;
			var suggested = new Rectangle (_form.Location, new Size ((int)Math.Round (_form.Width * backing / old), (int)Math.Round (_form.Height * backing / old)));
			if (ScaleHelper.IsThreadPerMonitorV2Aware)
				_driver.HostDpiChanged (_form.Handle, dpi, suggested);
			else
				_driver.SetWindowDpi (_form.Handle, dpi);
			_lastVer = -1;
		}

		internal BrowserFormHost (Form form)
		{
			_form = form;
			_chrome = new WindowChrome (form);
			PresentationHost.Attach (this, form);
		}

		public void Show ()
		{
			bool borderless = _form.FormBorderStyle == FormBorderStyle.None;
			_window = new BrowserWindow ();
			double backing = Math.Max (0.5, _window.GetBackingScale ());
			_k = PerMonitor ? _driver.WindowDpi (_form.Handle) / 96.0 : ScaleHelper.HostSourceDpi / 96.0;
			_scale = backing / _k;
			// device pixels for the origin, CSS pixels for the size; CW_USEDEFAULT lets the page place it
			bool placed = _form.StartPosition == FormStartPosition.Manual;
			_window.Create (_form.Text, placed ? (int)(_form.Left * _scale) : unchecked((int)0x80000000), placed ? (int)(_form.Top * _scale) : unchecked((int)0x80000000),
				Math.Max (1, (int)Math.Round (_form.Width / _k)), Math.Max (1, (int)Math.Round (_form.Height / _k)), borderless);
			// the page's DPI: the driver's for the window, and the form's if it is another
			FollowBacking (backing);
			_scale = backing / _k;
			_window.Resized += OnResized;
			// the form's window, for content that is a window of its own inside it: an ElementHost's WPF tree
			EmbeddedScenes.PublishHostWindow (_window.Handle, (float)_scale, _form);
			// hidden is not closed (a form is shown again, a drop-down reused): the canvas follows
			_form.VisibleChanged += OnVisibleChanged;
			// ShowDialog's end destroys the dialog's handle: its window goes, and a later Show makes another
			_form.HandleDestroyed += OnHandleDestroyed;
			BrowserWindow.MouseInput += OnMouse;
			BrowserWindow.KeyInput += OnKey;

			// the process's device (ProcessGpu.cs; on the page, navigator.gpu's), shared with WPF's windows
			var ctx = ProcessGpu.Acquire ();
			IntPtr surface = NativePlatform.CreateWindowSurface (ctx.Instance, _window.Handle);
			if (surface == IntPtr.Zero)
				throw new InvalidOperationException ("WinForms: no WebGPU surface for the form's canvas");
			SyncFromWindow ();
			_surfaceW = _form.Width;
			_surfaceH = _form.Height;
			_wgpu = new WgpuPresenter (ctx, surface, _surfaceW, _surfaceH, _scale, srgb: true, threadDevice: true);
			if (_chrome.HasFrame) {
				// the resize margin outside the visible edge is see-through, as Windows'; the chrome draws its shadow
				_wgpu.SetTransparent (true);
				_window.SetOwnFrame ();
			} else {
				Color bc = _form.BackColor;
				_wgpu.ClearColor = new RgbaColor (SrgbToLinear (bc.R / 255f), SrgbToLinear (bc.G / 255f), SrgbToLinear (bc.B / 255f), 1f);
			}
			Activate ();
			Present ();
		}

		/// <summary>This window active, the one that was deactivated (WM_ACTIVATE both ways).</summary>
		private void Activate ()
		{
			if (_closed || !_form.IsHandleCreated)
				return;
			BrowserFormHost previous = s_active;
			s_activation.Remove (this);
			s_activation.Add (this);
			if (previous == this)
				return;
			s_active = this;
			// another window active: the keyboard leaves a child window of the one before
			BrowserWindow.ReleaseKeyboard ();
			if (previous != null && !previous._closed && previous._form.IsHandleCreated) {
				_driver.HostActivate ((long)previous._form.Handle, false);
				previous.Repaint ();
			}
			_driver.HostActivate ((long)_form.Handle, true);
			Repaint ();
		}

		/// <summary>This window gone or hidden: the one active before it is again, its owner first.</summary>
		private void Deactivate ()
		{
			bool wasActive = s_active == this;
			s_activation.Remove (this);
			if (!wasActive)
				return;
			s_active = null;
			if (_form.IsHandleCreated)
				_driver.HostActivate ((long)_form.Handle, false);
			BrowserFormHost next = null;
			foreach (BrowserFormHost h in s_activation)
				if (h._form == _form.Owner)
					next = h;
			next ??= s_activation.Count > 0 ? s_activation [s_activation.Count - 1] : null;
			next?.Activate ();
		}

		/// <summary>The form takes the place the page gave its window (the window manager's decision,
		/// as AppKit's or Windows' would be), and the screen's own window its size too: it fills the page
		/// and follows it. Any other window's size is the form's own -- its canvas may be larger, to hold
		/// a drop-down hanging past it -- and taking the canvas's back grew a dialog without end (the
		/// canvas grown for a child reaching past the form, the form grown to the canvas, its anchored
		/// children with it, and round again).</summary>
		private void SyncFromWindow ()
		{
			if (_window == null || _form.IsDisposed || !_form.IsHandleCreated)
				return;
			// the canvas is the window's outer rectangle: its origin is the form's Location
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

		// the page's own window, which fills it (the first framed one the window manager is given)
		private bool IsScreenWindow => _window != null && !_window.IsSecondary;

		private void OnVisibleChanged (object sender, EventArgs e)
		{
			if (_closed || _window == null)
				return;
			_window.SetVisible (_form.Visible);
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
			SyncFromWindow ();
			_lastVer = -1;
		}

		// ---- present -------------------------------------------------------------------------

		public void Present ()
		{
			if (_wgpu == null || _closed)
				return;
			// the page's devicePixelRatio changed (zoom, another display): a per-monitor form lays itself
			// out again, any other is drawn at the new scale
			double backing = Math.Max (0.5, _window.GetBackingScale ());
			FollowBacking (backing);
			if ((float)(backing / _k) != _wgpu.Scale) {
				_scale = backing / _k;
				_wgpu.SetScale (_scale);
				EmbeddedScenes.PublishHostWindow (_window.Handle, (float)_scale, _form);
				_surfaceW = -1;                // the canvas's CSS size follows
				_lastVer = -1;
			}
			// the embedded content's changes too (a hosted WPF tree animating by itself)
			int ver = _driver.GetPaintVersion () + EmbeddedScenes.CurrentVersion ();
			bool caretOn = CaretOn ();
			if (ver == _lastVer && caretOn == _lastCaretOn && _lastPresentOk)
				return;   // nothing changed

			var scenes = GetScenes (out int ox, out int oy, out int needW, out int needH);
			if (_chrome.HasFrame)
				scenes.Insert (0, (_chrome.Paint (_form.Size, Form.ActiveForm == _form), 0, 0));
			if (needW != _surfaceW || needH != _surfaceH) {
				// a drop-down reaching past the form: the canvas grows to hold it (shrinks back after)
				_surfaceW = needW;
				_surfaceH = needH;
				_window.SetContentSize ((int)Math.Round (needW / _k), (int)Math.Round (needH / _k));
			}
			Rectangle? caret = caretOn && _driver.GetCaret (out int cx, out int cy, out int cw, out int ch)
				? new Rectangle (cx - ox, cy - oy, Math.Max (1, cw), ch) : null;
			_lastPresentOk = _wgpu.PresentScenes (scenes, caret, needW, needH);
			if (s_trace && (_traced++ < 6 || scenes.Count != _tracedScenes)) {
				_tracedScenes = scenes.Count;
				_window.GetPixelSize (out int pw, out int ph);
				_window.GetContentSize (out int cw2, out int ch2);
				Console.WriteLine ($"[browser form] '{_form.Text}' form={_form.Bounds} client={_form.ClientSize} window px={pw}x{ph} content={cw2}x{ch2} scale={_scale} surface={needW}x{needH} origin=({ox},{oy}) ok={_lastPresentOk} scenes=" +
					string.Join (" ", SceneSizes ()));
			}
			_lastVer = ver;
			_lastCaretOn = caretOn;
		}

		/// <summary>The form's window and the windows over it (menus, drop-downs) in form space, and
		/// the size that holds them all.</summary>
		private List<(object, int, int)> GetScenes (out int ox, out int oy, out int needW, out int needH)
		{
			long[] wins = _driver.GetPresentWindows (_form.Handle);
			// the canvas's origin is the window's: its client is the frame's width in (WindowChrome)
			Padding frame = _chrome.Frame;
			ox = (wins.Length >= 3 ? (int)wins [1] : _form.Left + frame.Left) - frame.Left;
			oy = (wins.Length >= 3 ? (int)wins [2] : _form.Top + frame.Top) - frame.Top;
			needW = _form.Width;
			needH = _form.Height;
			var list = new List<(object, int, int)> (wins.Length / 3);
			// the top-level window each scene belongs to, so embedded content goes right after its own
			var roots = new List<IntPtr> (wins.Length / 3);
			for (int i = 0; i + 2 < wins.Length; i += 3) {
				IntPtr h = (IntPtr)wins [i];
				object scene = _driver.GetWindowScene (h);
				if (scene == null)
					continue;
				int px = (int)wins [i + 1] - ox, py = (int)wins [i + 2] - oy;
				list.Add ((scene, px, py));
				roots.Add (Control.FromHandle (h)?.TopLevelControl is Control top && top.IsHandleCreated ? top.Handle : h);
				// Only a popup (a menu, a drop-down) stands outside the form; any other window is cut to
				// its parent, so what it has past the form never shows. app's designer parks its
				// root control 32000 pixels off, 32000 across, and the canvas grew to 64000 square.
				if (!_driver.IsInPopupWindow (h))
					continue;
				long size = _driver.GetWindowSizePacked (h);
				int w = (int)(size >> 32), hh = (int)(size & 0xFFFFFFFF);
				needW = Math.Max (needW, px + w);
				needH = Math.Max (needH, py + hh);
			}
			// Embedded non-WinForms content (an ElementHost's WPF tree, composited into this frame as on
			// Windows; see ElementHost.CompositeIntoHostFrame) right after the windows of the top-level window
			// it is in -- over the control whose area it occupies, under anything above that window (a menu,
			// a dialog of the same thread) -- and only in this window's frame: every host drew every hosted
			// tree, and a dialog's showed the main window's display canvas through it.
			var embedded = EmbeddedScenes.GetWithOwners (ox, oy);
			if (embedded != null) {
				for (int e = embedded.Count - 1; e >= 0; e--) {
					var (scene, x, y, owner) = embedded [e];
					int last = roots.LastIndexOf (owner);
					if (last < 0)
						continue;                    // another window's content
					list.Insert (last + 1, (scene, x, y));
					roots.Insert (last + 1, owner);
				}
			}
			return list;
		}

		private IEnumerable<string> SceneSizes ()
		{
			long[] wins = _driver.GetPresentWindows (_form.Handle);
			for (int i = 0; i + 2 < wins.Length; i += 3) {
				long size = _driver.GetWindowSizePacked ((IntPtr)wins [i]);
				string name = Control.FromHandle ((IntPtr)wins [i]) is Control c ? c.GetType ().Name : "?";
				yield return $"{name}@({wins [i + 1]},{wins [i + 2]}) {size >> 32}x{size & 0xFFFFFFFF}";
			}
		}

		private bool CaretOn () => _driver.GetCaret (out _, out _, out _, out _) && (_blink.ElapsedMilliseconds / 530) % 2 == 0;

		private static float SrgbToLinear (float c) => c <= 0.04045f ? c / 12.92f : (float)Math.Pow ((c + 0.055) / 1.055, 2.4);

		// ---- input ---------------------------------------------------------------------------

		public bool Pump () => !_closed;   // input arrives through BrowserWindow's events

		private bool Mine (IntPtr window) => !_closed && _window != null && window == _window.Handle && _form.IsHandleCreated && !_form.IsDisposed;

		private void OnMouse (BrowserWindow.BrowserMouseMessage m)
		{
			if (_dragging && !_closed && OnDrag (m))
				return;
			if (!Mine (m.Window))
				return;
			// device pixels in the canvas -> the page's points, the forms' screen (the canvas is the
			// window's outer rectangle, whose origin is the form's Location)
			int x = (int)Math.Floor (m.X / _scale) + _form.Left;
			int y = (int)Math.Floor (m.Y / _scale) + _form.Top;
			// a click activates the window, as Windows' WM_MOUSEACTIVATE does -- not one a modal dialog disabled
			if (m.Kind == 1 && _form.Enabled)
				Activate ();
			if (_chrome.HasFrame && OnFrame (m, x, y))
				return;
			IntPtr outerRoot = XplatUIWebGpu.InputRoot;
			XplatUIWebGpu.InputRoot = _form.Handle;
			try {
				// a move without the button down is Windows' WM_SETCURSOR, which ends a Cursor.Current
				if (m.Kind == 0 && !_leftDown)
					_driver.EndCursorOverride ();
				switch (m.Kind) {
				case 0: _driver.InjectMouseMove (x, y, _leftDown); break;
				case 1:
					if (m.Button == 2) _driver.InjectRightDown (x, y);
					else if (m.Button == 0) { _leftDown = true; _driver.InjectMouseDown (x, y); }
					break;
				case 2:
					if (m.Button == 2) _driver.InjectRightUp (x, y);
					else if (m.Button == 0) { _leftDown = false; _driver.InjectMouseUp (x, y); }
					break;
				case 3: _driver.InjectWheel (x, y, m.Wheel); break;
				}
			} finally {
				XplatUIWebGpu.InputRoot = outerRoot;
			}
			if (m.Kind == 0)
				ApplyCursor ();
			Present ();
		}

		/// <summary>The cursor the control under the pointer asks for (the driver's StdCursor), as Windows
		/// shows it on WM_SETCURSOR, as the page's CSS cursor. Nothing set one: an arrow over every
		/// splitter, text box and link.</summary>
		private void ApplyCursor ()
		{
			string css = _driver.GetActiveCursor () switch {
				1 => "progress", 3 => "crosshair", 4 => "pointer", 5 => "help",
				6 => "row-resize", 7 => "text", 8 => "not-allowed",
				>= 9 and <= 19 => "all-scroll", 20 => "move", 21 => "nesw-resize", 22 => "ns-resize",
				23 => "nwse-resize", 24 => "ew-resize", 26 => "col-resize", 27 => "wait",
				_ => "default",
			};
			BrowserWindow.SetCursor (css);
		}

		// ---- the frame (WindowChrome): DefWindowProc's part --------------------------------------

		/// <summary>The pointer on the window's frame: true when it was the frame's (not the client's).</summary>
		private bool OnFrame (BrowserWindow.BrowserMouseMessage m, int x, int y)
		{
			int hit = _chrome.HitTest (new Point (x - _form.Left, y - _form.Top), _form.Size);
			bool down = m.Kind == 1 && m.Button == 0, up = m.Kind == 2 && m.Button == 0;
			if (_chrome.Pressed == WindowChrome.HTCLOSE) {
				// the close button holds the mouse until it is let go, and closes only if let go on it
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
				return true;                   // a window a modal dialog disabled takes no clicks, frame included
			if (down && hit == WindowChrome.HTCAPTION) {
				// SC_MOVE: the window follows the pointer until the button is let go
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

		/// <summary>A move in progress: the window where the pointer has taken it, wherever on the page
		/// the pointer now is.</summary>
		private bool OnDrag (BrowserWindow.BrowserMouseMessage m)
		{
			if (m.Kind == 2 && m.Button == 0) {
				_dragging = false;
				return true;
			}
			if (m.Kind != 0)
				return true;
			BrowserWindow over = BrowserWindow.FromHandle (m.Window);
			if (over == null)
				return true;
			over.GetClientScreenOriginPixels (out int sx, out int sy);
			int x = (int)Math.Floor ((m.X + sx) / _scale), y = (int)Math.Floor ((m.Y + sy) / _scale);
			var to = new Point (_dragOrigin.X + x - _dragFrom.X, _dragOrigin.Y + y - _dragFrom.Y);
			// the title bar stays on the page, as Windows keeps a caption on the screen
			to.Y = Math.Max (0, to.Y);
			_window.SetFrameOrigin ((int)Math.Round (to.X * _scale), (int)Math.Round (to.Y * _scale));
			XplatUI.SetWindowPos (_form.Handle, to.X, to.Y, _form.Width, _form.Height);
			Present ();
			return true;
		}

		private void Repaint ()
		{
			_lastVer = -1;
			Present ();
		}

		private void OnKey (BrowserWindow.BrowserKeyMessage k)
		{
			// The page's keys reach the first window; on Windows they go to the active one.
			if (_closed || !_form.IsHandleCreated || _form.IsDisposed)
				return;
			// a child window that took the keyboard (an ElementHost's WPF tree) has its own keys
			if (BrowserWindow.FromHandle (k.Window) is { TakesKeyboardOnPress: true })
				return;
			Form active = Form.ActiveForm;
			if (active != null ? active != _form : !Mine (k.Window))
				return;
			Keys modifiers = (k.Ctrl || k.Meta ? Keys.Control : 0) | (k.Shift ? Keys.Shift : 0) | (k.Alt ? Keys.Alt : 0);
			_driver.SetModifierKeys (modifiers);
			int vk = VirtualKey (k.Code);
			if (!k.IsDown) {
				if (vk != 0) _driver.InjectKeyUp (vk);
				return;
			}
			bool consumed = vk != 0 && _driver.InjectKeyDown (vk);
			// WM_CHAR, as TranslateMessage makes it: the typed character, and the control characters
			// Windows translates keys into (Backspace, Tab, Enter, Escape)
			if (!consumed) {
				if (k.Key != null && k.Key.Length == 1 && !k.Ctrl && !k.Meta)
					_driver.InjectChar (k.Key [0]);
				else if (vk is 0x08 or 0x09 or 0x0D or 0x1B)
					_driver.InjectChar ((char)vk);
			}
			Present ();
		}

		/// <summary>DOM KeyboardEvent.code to a Win32 virtual key.</summary>
		internal static int VirtualKey (string code)
		{
			if (string.IsNullOrEmpty (code))
				return 0;
			if (code.Length == 4 && code.StartsWith ("Key", StringComparison.Ordinal))
				return code [3];                                     // 'A'..'Z'
			if (code.Length == 6 && code.StartsWith ("Digit", StringComparison.Ordinal))
				return code [5];                                     // '0'..'9'
			if (code.Length >= 2 && code [0] == 'F' && int.TryParse (code.AsSpan (1), out int f) && f >= 1 && f <= 24)
				return 0x70 + f - 1;                                 // F1..F24
			if (code.StartsWith ("Numpad", StringComparison.Ordinal) && code.Length == 7 && char.IsDigit (code [6]))
				return 0x60 + (code [6] - '0');
			return code switch {
				"Backspace" => 0x08, "Tab" => 0x09, "Enter" or "NumpadEnter" => 0x0D, "Escape" => 0x1B, "Space" => 0x20,
				"PageUp" => 0x21, "PageDown" => 0x22, "End" => 0x23, "Home" => 0x24,
				"ArrowLeft" => 0x25, "ArrowUp" => 0x26, "ArrowRight" => 0x27, "ArrowDown" => 0x28,
				"Insert" => 0x2D, "Delete" => 0x2E,
				"ShiftLeft" => 0xA0, "ShiftRight" => 0xA1, "ControlLeft" => 0xA2, "ControlRight" => 0xA3,
				"AltLeft" => 0xA4, "AltRight" => 0xA5, "MetaLeft" => 0x5B, "MetaRight" => 0x5C,
				"CapsLock" => 0x14, "ContextMenu" => 0x5D,
				"Minus" => 0xBD, "Equal" => 0xBB, "BracketLeft" => 0xDB, "BracketRight" => 0xDD, "Backslash" => 0xDC,
				"Semicolon" => 0xBA, "Quote" => 0xDE, "Comma" => 0xBC, "Period" => 0xBE, "Slash" => 0xBF, "Backquote" => 0xC0,
				"NumpadMultiply" => 0x6A, "NumpadAdd" => 0x6B, "NumpadSubtract" => 0x6D, "NumpadDecimal" => 0x6E, "NumpadDivide" => 0x6F,
				_ => 0,
			};
		}

		public void InjectClickScreen (int x, int y)
		{
			_driver.InjectMouseMove (x, y, false);
			_driver.InjectMouseDown (x, y);
			_driver.InjectMouseUp (x, y);
		}

		public void SaveFrame (string path) { }   // no readback of a page's canvas here

		public void Close ()
		{
			if (_closed)
				return;
			Deactivate ();
			_closed = true;
			_form.VisibleChanged -= OnVisibleChanged;
			_form.HandleDestroyed -= OnHandleDestroyed;
			BrowserWindow.MouseInput -= OnMouse;
			BrowserWindow.KeyInput -= OnKey;
			if (_window != null) {
				_window.Resized -= OnResized;
				_window.Destroy ();
			}
			_wgpu?.Dispose ();
			_wgpu = null;
		}
	}
}
