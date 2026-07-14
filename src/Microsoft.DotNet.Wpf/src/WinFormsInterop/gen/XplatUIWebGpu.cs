// Auto-generated non-core XplatUIDriver stub overrides (defaults). Core methods are in XplatUIWebGpu.Core.cs.
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
		internal override bool MenuAccessKeysUnderlined { get { return default(bool); } }
		internal override Size MinimizedWindowSpacingSize { get { return default(Size); } }
		internal override Size MinimumWindowSize { get { return default(Size); } }
		internal override Size SmallIconSize { get { return default(Size); } }
		internal override int MouseButtonCount { get { return default(int); } }
		internal override bool MouseButtonsSwapped { get { return default(bool); } }
		internal override bool MouseWheelPresent { get { return default(bool); } }
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
		internal override void OverrideCursor(IntPtr cursor) {  }
		internal override IntPtr DefineCursor(Bitmap bitmap, Bitmap mask, Color cursor_pixel, Color mask_pixel, int xHotSpot, int yHotSpot) { return IntPtr.Zero; }
		internal override IntPtr DefineStdCursor(StdCursor id) { return IntPtr.Zero; }
		internal override Bitmap DefineStdCursorBitmap(StdCursor id) { return default(Bitmap); }
		internal override void DestroyCursor(IntPtr cursor) {  }
		internal override void GetCursorInfo(IntPtr cursor, out int width, out int height, out int hotspot_x, out int hotspot_y) { width = default(int); height = default(int); hotspot_x = default(int); hotspot_y = default(int); }
		internal override void GrabInfo(out IntPtr hwnd, out bool GrabConfined, out Rectangle GrabArea) { hwnd = default(IntPtr); GrabConfined = default(bool); GrabArea = default(Rectangle); }
		internal override void SendAsyncMethod(AsyncMethodData method) {  }
		internal override void SetTimer(Timer timer) {  }
		internal override void KillTimer(Timer timer) {  }
		internal override IntPtr GetFocus() { return IntPtr.Zero; }
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
		internal override void ClipboardClose(IntPtr handle) {  }
		internal override IntPtr ClipboardOpen(bool primary_selection) { return IntPtr.Zero; }
		internal override int ClipboardGetID(IntPtr handle, string format) { return 0; }
		internal override void ClipboardStore(IntPtr handle, object obj, int id, XplatUI.ObjectToClipboard converter, bool copy) {  }
		internal override int[] ClipboardAvailableFormats(IntPtr handle) { return default(int[]); }
		internal override object ClipboardRetrieve(IntPtr handle, int id, XplatUI.ClipboardToObject converter) { return default(object); }
		internal override void DrawReversibleLine(Point start, Point end, Color backColor) {  }
		internal override void DrawReversibleRectangle(IntPtr handle, Rectangle rect, int line_width) {  }
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
