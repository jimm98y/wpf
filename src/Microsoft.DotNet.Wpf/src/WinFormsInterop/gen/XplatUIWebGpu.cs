// Auto-generated non-core XplatUIDriver stub overrides (defaults). Core methods are in XplatUIWebGpu.Core.cs.
// Clipboard is implemented in XplatUIWebGpu.Clipboard.cs and drag and drop in XplatUIWebGpu.DragDrop.cs;
// neither belongs here, and a stub for either silently un-implements a working feature (see gen-driver.txt).
using System; using System.Drawing; using System.Threading; using System.Collections;
namespace System.Windows.Forms {
	internal partial class XplatUIWebGpu : XplatUIDriver {
		internal override IntPtr InitializeDriver() { return IntPtr.Zero; }
		internal override void ShutdownDriver(IntPtr token) {  }
		internal override int CaptionHeight { get { return default(int); } }
		internal override Size CursorSize { get { return default(Size); } }
		internal override bool DragFullWindows { get { return default(bool); } }
		internal override Size DragSize { get { return default(Size); } }
		internal override Size FrameBorderSize { get { return default(Size); } }
		internal override Size IconSize { get { return default(Size); } }
		internal override Size MaxWindowTrackSize { get { return default(Size); } }
		// False here meant Control.show_focus_cues started false and nothing ever set it, so no
		// button, check box or radio button drew a focus rectangle -- ShouldPaintFocusRectangle
		// asks for ShowFocusCues and never got it -- and no menu mnemonic was ever underlined.
		internal override bool MenuAccessKeysUnderlined { get { return true; } }
		internal override Size MinimizedWindowSpacingSize { get { return default(Size); } }
		internal override Size MinimumWindowSize { get { return default(Size); } }
		internal override Size SmallIconSize { get { return default(Size); } }
		internal override int MouseButtonCount { get { return default(int); } }
		internal override bool MouseButtonsSwapped { get { return default(bool); } }
		// Generated as default(bool) -- false -- which is a claim that the machine has no wheel.
		// Every head this driver serves has one, and SystemInformation.MouseWheelPresent is what
		// scrolling code consults before it bothers to listen.
		internal override bool MouseWheelPresent { get { return true; } }
		// VirtualScreen / WorkingArea / AllScreens implemented in XplatUIWebGpu.Core.cs
		internal override bool ThemesEnabled { get { return default(bool); } }
		internal override event EventHandler Idle;
		internal override void AudibleAlert(AlertType alert) {  }
		internal override void BeginMoveResize(IntPtr handle) {  }
		internal override void EnableThemes() {  }
		internal override void GetDisplaySize(out Size size) { size = default(Size); }
		internal override void SetWindowMinMax(IntPtr handle, Rectangle maximized, Size min, Size max) {  }
		internal override double GetWindowTransparency(IntPtr handle) { return 0; }
		internal override void SetWindowTransparency(IntPtr handle, double transparency, Color key) {  }
		internal override TransparencySupport SupportsTransparency() { return default(TransparencySupport); }
		internal override void SetMenu(IntPtr handle, Menu menu) {  }
		internal override bool IsEnabled(IntPtr handle) { return false; }
		internal override void Activate(IntPtr handle) {  }
		internal override void EnableWindow(IntPtr handle, bool Enable) {  }
		internal override void HandleException(Exception e) {  }
		internal override Region GetClipRegion(IntPtr hwnd) { return default(Region); }
		internal override void SetClipRegion(IntPtr hwnd, Region region) {  }
		internal override void OverrideCursor(IntPtr cursor) { SetCursorOverride(cursor); }
		internal override IntPtr DefineCursor(Bitmap bitmap, Bitmap mask, Color cursor_pixel, Color mask_pixel, int xHotSpot, int yHotSpot) { return IntPtr.Zero; }
		// Plus one so the handle is never zero, which is how callers spell "no cursor".
		internal override IntPtr DefineStdCursor(StdCursor id) { return (IntPtr)((int)id + 1); }
		internal override Bitmap DefineStdCursorBitmap(StdCursor id) { return default(Bitmap); }
		internal override void DestroyCursor(IntPtr cursor) {  }
		internal override void GetCursorInfo(IntPtr cursor, out int width, out int height, out int hotspot_x, out int hotspot_y) { width = default(int); height = default(int); hotspot_x = default(int); hotspot_y = default(int); }
		internal override void GrabInfo(out IntPtr hwnd, out bool GrabConfined, out Rectangle GrabArea) { hwnd = default(IntPtr); GrabConfined = default(bool); GrabArea = default(Rectangle); }
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
		internal override IntPtr GetActive() { return IntPtr.Zero; }
		internal override IntPtr GetPreviousWindow(IntPtr hwnd) { return IntPtr.Zero; }
		internal override bool GetFontMetrics(Graphics g, Font font, out int ascent, out int descent) { ascent = default(int); descent = default(int); return false; }
		internal override bool SystrayAdd(IntPtr hwnd, string tip, Icon icon, out ToolTip tt) { tt = default(ToolTip); return false; }
		internal override bool SystrayChange(IntPtr hwnd, string tip, Icon icon, ref ToolTip tt) { return false; }
		internal override void SystrayRemove(IntPtr hwnd, ref ToolTip tt) {  }
		internal override void SystrayBalloon(IntPtr hwnd, int timeout, string title, string text, ToolTipIcon icon) {  }
		internal override Point GetMenuOrigin(IntPtr hwnd) { return default(Point); }
		internal override void MenuToScreen(IntPtr hwnd, ref int x, ref int y) {  }
		internal override void ScreenToMenu(IntPtr hwnd, ref int x, ref int y) {  }
		internal override void SetIcon(IntPtr handle, Icon icon) {  }
		internal override void DrawReversibleLine(Point start, Point end, Color backColor) {  }
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
		internal override void FillReversibleRectangle(Rectangle rectangle, Color backColor) {  }
		internal override void DrawReversibleFrame(Rectangle rectangle, Color backColor, FrameStyle style) {  }
		internal override SizeF GetAutoScaleSize(Font font) { return default(SizeF); }
		internal override int SendInput(IntPtr hwnd, System.Collections.Queue keys) { return 0; }
		internal override void ResetMouseHover(IntPtr hwnd) {  }
		internal override void RaiseIdle(EventArgs e) {  }
		internal override int KeyboardSpeed { get { return default(int); } }
		internal override int KeyboardDelay { get { return default(int); } }
	}
}
