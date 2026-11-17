// Auto-generated non-core XplatUIDriver stub overrides (defaults). Core methods are in XplatUIWebGpu.Core.cs.
// Clipboard is implemented in XplatUIWebGpu.Clipboard.cs and drag and drop in XplatUIWebGpu.DragDrop.cs;
// neither belongs here, and a stub for either silently un-implements a working feature (see gen-driver.txt).
using System; using System.Drawing; using System.Threading; using System.Collections;
namespace System.Windows.Forms {
	internal partial class XplatUIWebGpu : XplatUIDriver {
		internal override IntPtr InitializeDriver() { return IntPtr.Zero; }
		internal override void ShutdownDriver(IntPtr token) {  }
		internal override int CaptionHeight { get { return default(int); } }
		internal override Size FrameBorderSize { get { return default(Size); } }
		// SM_CXMAXTRACK/SM_CYMAXTRACK: the virtual screen and a sizing frame (SM_CXSIZEFRAME + SM_CXPADDEDBORDER at
		// 96 dpi) on each side -- the largest a top-level window is sized (SetWindowPos clamps to it)
		internal override Size MaxWindowTrackSize { get { Rectangle v = VirtualScreen; return new Size(v.Width + 2 * (4 + 4), v.Height + 2 * (4 + 4)); } }
		// False here meant Control.show_focus_cues started false and nothing ever set it, so no
		// button, check box or radio button drew a focus rectangle -- ShouldPaintFocusRectangle
		// asks for ShowFocusCues and never got it -- and no menu mnemonic was ever underlined.
		/// <summary>Whether a menu shows the line under its access key before anyone has asked
		/// for it. Windows does not -- the line appears once the menu is reached from the
		/// keyboard, and the renderer already asks for it then -- and neither does any other
		/// desktop this runs on. Saying yes here underlined every caption in every menu, which is
		/// not what the same menu looks like in Windows.</summary>
		internal override bool MenuAccessKeysUnderlined { get { return false; } }
		internal override Size MinimizedWindowSpacingSize { get { return default(Size); } }
		internal override Size MinimumWindowSize { get { return default(Size); } }
		/// <summary>SM_CXMINTRACK x SM_CYMINTRACK, Windows 10 and 11's at 96 dpi: the smallest a window with a
		/// caption or a sizing border is ever made (WM_GETMINMAXINFO's ptMinTrackSize by default).</summary>
		internal override Size MinWindowTrackSize { get { return new Size(136, 39); } }
		internal override bool MouseButtonsSwapped { get { return default(bool); } }
		// Generated as default(bool) -- false -- which is a claim that the machine has no wheel.
		// Every head this driver serves has one, and SystemInformation.MouseWheelPresent is what
		// scrolling code consults before it bothers to listen.
		internal override bool MouseWheelPresent { get { return true; } }
		// VirtualScreen / WorkingArea / AllScreens implemented in XplatUIWebGpu.Core.cs
		// Generated as false, which told Application.RenderWithVisualStyles that themes were off, so
		// every public renderer -- ButtonRenderer, CheckBoxRenderer, ComboBoxRenderer, the ones a
		// DataGridView's cells draw with -- fell back to the classic bevels. The theme on every
		// head is the managed Windows 11 one, and it is always on.
		internal override bool ThemesEnabled { get { return true; } }
		// AudibleAlert is implemented in XplatUIWebGpu.System.cs (the host's beep)
		internal override void BeginMoveResize(IntPtr handle) {  }
		internal override void EnableThemes() {  }
		internal override void GetDisplaySize(out Size size) { size = default(Size); }
		internal override void SetWindowMinMax(IntPtr handle, Rectangle maximized, Size min, Size max) {  }
		internal override double GetWindowTransparency(IntPtr handle) { return 0; }
		// A form's Opacity (and TransparencyKey) is read straight off the form by the host, which presents
		// the window layered with it. What the host needs to hear is that it CHANGED: nothing else asked
		// for a frame, so a popup fading in on a timer stayed at whichever step happened to be presented
		// last -- half transparent -- until the pointer moved over it.
		internal override void SetWindowTransparency(IntPtr handle, double transparency, Color key) { _paintVersion++; }
		internal override TransparencySupport SupportsTransparency() { return TransparencySupport.Set; }
		internal override void SetMenu(IntPtr handle, Menu menu) {  }
		internal override void Activate(IntPtr handle) {  }
		internal override void HandleException(Exception e) {  }
		// SetClipRegion and GetClipRegion (a window's region) are in XplatUIWebGpu.Core.cs
		internal override void OverrideCursor(IntPtr cursor) { SetCursorOverride(cursor); }
		internal override IntPtr DefineCursor(Bitmap bitmap, Bitmap mask, Color cursor_pixel, Color mask_pixel, int xHotSpot, int yHotSpot) { return IntPtr.Zero; }
		// Plus one so the handle is never zero, which is how callers spell "no cursor".
		internal override IntPtr DefineStdCursor(StdCursor id) { return (IntPtr)((int)id + 1); }
		internal override void DestroyCursor(IntPtr cursor) {  }
		internal override void GetCursorInfo(IntPtr cursor, out int width, out int height, out int hotspot_x, out int hotspot_y) { width = default(int); height = default(int); hotspot_x = default(int); hotspot_y = default(int); }
		// GrabInfo is implemented in the CORE driver (XplatUIWebGpu.Core.cs): left as the generated
		// stub it said nothing held the mouse, so ShowDialog never released a capture.
		// SendAsyncMethod is implemented in the CORE driver (XplatUIWebGpu.Core.cs): as a no-op
		// here, Control.BeginInvoke accepted a delegate and silently never ran it.
		// Timers are implemented in the CORE driver (XplatUIWebGpu.Core.cs): System.Windows.Forms.Timer
		// is how ordinary WinForms code does anything periodic, and with these left as the generated
		// no-ops Timer.Tick never fired -- an Application.Run app whose only work is on a timer simply
		// hung. See TickTimers, driven from the message loop's idle path.
		// Answer with the window SetFocus last gave focus to. Returning Zero here meant
		// Control.InternalContainsFocus was false for EVERY control, and that is what
		// ContainerControl.ActiveControl consults before it actually hands a clicked control the
		// focus -- so clicking selected an item but never focused it. Every visual conditioned on
		// focus then stayed off: a selected ListView row or TreeView node kept its HighlightText
		// (white) text but lost the blue highlight behind it, and so became invisible.
		internal override IntPtr GetFocus() { return FocusHandle; }
		internal override IntPtr GetActive() { return _activeForm; }
		internal override IntPtr GetPreviousWindow(IntPtr hwnd) { return IntPtr.Zero; }
		// Generated as zero, so every run in a text box had no ascent and runs of different sizes were
		// hung from the top of the line instead of standing on one baseline. The caller (LineTag)
		// scales these by the font's height over its line spacing, so they are design units.
		internal override bool GetFontMetrics(Graphics g, Font font, out int ascent, out int descent)
		{
			ascent = descent = 0;
			if (font == null) return false;
			ascent = font.FontFamily.GetCellAscent(font.Style);
			descent = font.FontFamily.GetCellDescent(font.Style);
			return ascent > 0;
		}
		// NotifyIcon: the host's tray (an NSStatusItem in the macOS menu bar), which reports a click to the icon's
		// window as the shell does -- WM_USER with the mouse message in lParam. None of it existed anywhere.
		internal static Func<IntPtr, string, Icon, bool> SystrayAdded;
		internal static Action<IntPtr, string, Icon> SystrayChanged;
		internal static Action<IntPtr> SystrayRemoved;
		internal override bool SystrayAdd(IntPtr hwnd, string tip, Icon icon, out ToolTip tt)
		{
			tt = null;
			try { return SystrayAdded?.Invoke(hwnd, tip, icon) ?? false; }
			catch (Exception) { return false; }
		}
		internal override bool SystrayChange(IntPtr hwnd, string tip, Icon icon, ref ToolTip tt)
		{
			try { SystrayChanged?.Invoke(hwnd, tip, icon); return SystrayChanged != null; }
			catch (Exception) { return false; }
		}
		internal override void SystrayRemove(IntPtr hwnd, ref ToolTip tt)
		{
			try { SystrayRemoved?.Invoke(hwnd); }
			catch (Exception) { }
		}
		internal override void SystrayBalloon(IntPtr hwnd, int timeout, string title, string text, ToolTipIcon icon) {  }
		internal override Point GetMenuOrigin(IntPtr hwnd) { return default(Point); }
		internal override void MenuToScreen(IntPtr hwnd, ref int x, ref int y) {  }
		internal override void ScreenToMenu(IntPtr hwnd, ref int x, ref int y) {  }
		/// <summary>Form.Icon: the host shows it where its system shows a window's icon (the Dock on macOS stands
		/// in for the taskbar).</summary>
		internal static Action<IntPtr, Icon> IconChanged;
		internal override void SetIcon(IntPtr handle, Icon icon) { try { IconChanged?.Invoke(handle, icon); } catch (Exception) { } }
		/// <summary>ControlPaint.DrawReversibleLine, on the screen: the horizontal and vertical lines a splitter or a
		/// column divider draws while it is dragged, as a band a pixel thick (drawn twice, it is gone again).</summary>
		internal override void DrawReversibleLine(Point start, Point end, Color backColor)
		{
			var r = Rectangle.FromLTRB(Math.Min(start.X, end.X), Math.Min(start.Y, end.Y), Math.Max(start.X, end.X) + 1, Math.Max(start.Y, end.Y) + 1);
			ToggleReversible(IntPtr.Zero, r, 1);
		}
		/// <summary>The rubber band a drag draws: a splitter's position, a designer's selection.
		/// <para>Win32 draws it straight onto the screen with an XOR pen, so drawing the same
		/// rectangle twice rubs it out again -- which is exactly how callers use it, erasing the
		/// previous rectangle before drawing the next. There is no screen to scribble on here,
		/// so the rectangles are kept and the compositor draws them over the finished frame; the
		/// toggle is what preserves the caller's contract. A no-op was why dragging a splitter
		/// showed nothing at all until the mouse came up.</para></summary>
		internal override void DrawReversibleRectangle(IntPtr handle, Rectangle rect, int line_width)
		{
			ToggleReversible(handle, rect, line_width);
		}
		/// <summary>ControlPaint.FillReversibleRectangle, on the screen: a band as wide as it can be, which fills it.</summary>
		internal override void FillReversibleRectangle(Rectangle rectangle, Color backColor)
		{
			if (rectangle.Width <= 0 || rectangle.Height <= 0) return;
			ToggleReversible(IntPtr.Zero, rectangle, (Math.Min(rectangle.Width, rectangle.Height) + 1) / 2);
		}
		/// <summary>ControlPaint.DrawReversibleFrame, on the screen: a dashed frame a pixel wide, a thick one three.</summary>
		internal override void DrawReversibleFrame(Rectangle rectangle, Color backColor, FrameStyle style)
		{
			if (rectangle.Width <= 0 || rectangle.Height <= 0) return;
			ToggleReversible(IntPtr.Zero, rectangle, style == FrameStyle.Thick ? 3 : 1);
		}
		// Form.GetAutoScaleSize, as .NET has it: the font's average character width, measured on the screen the way
		// GDI+ measures a magic string, and its height. Generated as default(SizeF), so a form scaled the legacy
		// way (AutoScaleBaseSize in its resources, e.g. app's Find dialog) was scaled to 0/base + 0.08:
		// every control came out at a twelfth of its size.
		// Every Form asks for its font's size when it is made (Mono keeps the base size up to date), so the
		// measurement is remembered per font and resolution.
		private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string, float, FontStyle, GraphicsUnit, byte, float), float> s_autoScaleWidth = new();
		internal override SizeF GetAutoScaleSize(Font font)
		{
			float height = font.Height;
			var key = (font.Name, font.Size, font.Style, font.Unit, font.GdiCharSet, System.Drawing.WebGpuBackend.DisplayDpi.X);
			if (s_autoScaleWidth.TryGetValue(key, out float known))
				return new SizeF(known, height);
			float width = 9.0f;
			try {
				using var bmp = new Bitmap(1, 1);
				bmp.SetResolution(System.Drawing.WebGpuBackend.DisplayDpi.X, System.Drawing.WebGpuBackend.DisplayDpi.Y);
				using Graphics g = Graphics.FromImage(bmp);
				const string magicString = "The quick brown fox jumped over the lazy dog.";
				const double magicNumber = 44.549996948242189;   // .NET's, for compatibility with older Windows Forms
				width = (float)(g.MeasureString(magicString, font).Width / magicNumber);
			} catch (Exception) {
				// .NET answers 9 when it cannot measure
			}
			s_autoScaleWidth[key] = width;
			return new SizeF(width, height);
		}
		// SendInput (SendKeys) is implemented in XplatUIWebGpu.System.cs
		/// <summary>Another WM_MOUSEHOVER for the window the pointer rests on, after the hover time again.</summary>
		internal override void ResetMouseHover(IntPtr hwnd)
		{
			if (hwnd != _hoverWindow || hwnd == IntPtr.Zero) return;
			_hoverDone = false;
			_hoverSince = Timer.StopWatchNowMilliseconds;
		}
	}
}
