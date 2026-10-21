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
		private double _scale = 1;
		private int _lastVer = -1, _surfaceW, _surfaceH;
		private bool _lastCaretOn, _lastPresentOk, _leftDown, _closed;
		private int _traced;
		private static readonly bool s_trace = Environment.GetEnvironmentVariable ("WF_TRACE_BROWSER") == "1";

		internal BrowserFormHost (Form form)
		{
			_form = form;
			PresentationHost.Attach (this, form);
		}

		public void Show ()
		{
			bool borderless = _form.FormBorderStyle == FormBorderStyle.None;
			_window = new BrowserWindow ();
			_scale = Math.Max (0.5, _window.GetBackingScale ());
			// device pixels for the origin, points for the size; CW_USEDEFAULT lets the page place it
			bool placed = _form.StartPosition == FormStartPosition.Manual;
			_window.Create (_form.Text, placed ? (int)(_form.Left * _scale) : unchecked((int)0x80000000), placed ? (int)(_form.Top * _scale) : unchecked((int)0x80000000),
				Math.Max (1, _form.Width), Math.Max (1, _form.Height), borderless);
			_window.Resized += OnResized;
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
			Color bc = _form.BackColor;
			_wgpu.ClearColor = new RgbaColor (SrgbToLinear (bc.R / 255f), SrgbToLinear (bc.G / 255f), SrgbToLinear (bc.B / 255f), 1f);
			Present ();
		}

		/// <summary>The form takes the place and size the page gave its window (the window manager's
		/// decision, as AppKit's or Windows' would be).</summary>
		private void SyncFromWindow ()
		{
			if (_window == null || _form.IsDisposed || !_form.IsHandleCreated)
				return;
			_window.GetClientScreenOriginPixels (out int px, out int py);
			_window.GetPixelSize (out int pw, out int ph);
			var bounds = new Rectangle ((int)Math.Round (px / _scale), (int)Math.Round (py / _scale),
				(int)Math.Round (pw / _scale), (int)Math.Round (ph / _scale));
			if (bounds.Width > 0 && bounds.Height > 0 && _form.Bounds != bounds)
				XplatUI.SetWindowPos (_form.Handle, bounds.X, bounds.Y, bounds.Width, bounds.Height);
		}

		private void OnVisibleChanged (object sender, EventArgs e)
		{
			if (_closed || _window == null)
				return;
			_window.SetVisible (_form.Visible);
			_lastVer = -1;
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
			int ver = _driver.GetPaintVersion ();
			bool caretOn = CaretOn ();
			if (ver == _lastVer && caretOn == _lastCaretOn && _lastPresentOk)
				return;   // nothing changed

			var scenes = GetScenes (out int ox, out int oy, out int needW, out int needH);
			if (needW != _surfaceW || needH != _surfaceH) {
				// a drop-down reaching past the form: the canvas grows to hold it (shrinks back after)
				_surfaceW = needW;
				_surfaceH = needH;
				_window.SetContentSize (needW, needH);
			}
			Rectangle? caret = caretOn && _driver.GetCaret (out int cx, out int cy, out int cw, out int ch)
				? new Rectangle (cx - ox, cy - oy, Math.Max (1, cw), ch) : null;
			_lastPresentOk = _wgpu.PresentScenes (scenes, caret, needW, needH);
			if (s_trace && _traced++ < 6) {
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
			ox = wins.Length >= 3 ? (int)wins [1] : _form.Left;
			oy = wins.Length >= 3 ? (int)wins [2] : _form.Top;
			needW = _form.Width;
			needH = _form.Height;
			var list = new List<(object, int, int)> (wins.Length / 3);
			for (int i = 0; i + 2 < wins.Length; i += 3) {
				IntPtr h = (IntPtr)wins [i];
				object scene = _driver.GetWindowScene (h);
				if (scene == null)
					continue;
				int px = (int)wins [i + 1] - ox, py = (int)wins [i + 2] - oy;
				list.Add ((scene, px, py));
				long size = _driver.GetWindowSizePacked (h);
				int w = (int)(size >> 32), hh = (int)(size & 0xFFFFFFFF);
				needW = Math.Max (needW, px + w);
				needH = Math.Max (needH, py + hh);
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
			if (!Mine (m.Window))
				return;
			// device pixels in the canvas -> the page's points, the forms' screen
			int x = (int)Math.Floor (m.X / _scale) + _form.Left;
			int y = (int)Math.Floor (m.Y / _scale) + _form.Top;
			IntPtr outerRoot = XplatUIWebGpu.InputRoot;
			XplatUIWebGpu.InputRoot = _form.Handle;
			try {
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
			Present ();
		}

		private void OnKey (BrowserWindow.BrowserKeyMessage k)
		{
			// The page's keys reach the first window; on Windows they go to the active one.
			if (_closed || !_form.IsHandleCreated || _form.IsDisposed)
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
