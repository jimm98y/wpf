// The system metrics a WinForms app reads through SystemInformation, answered with Windows 11's own
// values at 96 dpi (the windows are DPI-unaware and render at 1x on every head). They were generated
// stubs returning zero, so a cursor, an icon and a drag rectangle were all 0 x 0 everywhere: the
// cursor editor sized its rows to a zero-height cursor (16 of them where stock shows 8) and drew
// each cursor into a zero-width box.
using System.Drawing;

namespace System.Windows.Forms {
	internal partial class XplatUIWebGpu {
		internal override Size CursorSize { get { return new Size (32, 32); } }        // SM_CXCURSOR/SM_CYCURSOR
		internal override Size IconSize { get { return new Size (32, 32); } }          // SM_CXICON/SM_CYICON
		internal override Size SmallIconSize { get { return new Size (16, 16); } }     // SM_CXSMICON/SM_CYSMICON
		internal override Size DragSize { get { return new Size (4, 4); } }            // SM_CXDRAG/SM_CYDRAG
		internal override bool DragFullWindows { get { return true; } }                // SPI_GETDRAGFULLWINDOWS
		// SPI_GETDROPSHADOW: on, as Windows comes. DotNetBar draws its menus' shadows only when this
		// says so, and the base's false had it draw none.
		internal override bool IsDropShadowEnabled {
			get {
				if (OperatingSystem.IsWindows ()) {
					uint on = 0;
					if (SystemParametersInfoW (0x1024, 0, ref on, 0)) return on != 0;
				}
				return true;
			}
		}
		internal override int MouseButtonCount { get { return 3; } }                   // SM_CMOUSEBUTTONS
		internal override int KeyboardSpeed { get { return 31; } }                     // SPI_GETKEYBOARDSPEED default
		internal override int KeyboardDelay { get { return 1; } }                      // SPI_GETKEYBOARDDELAY default
		// The caption buttons and the sizing frame, as a stock app on Windows 11 reads them. Mono's
		// 18 x 18 buttons and 8-pixel frame put DotNetBar's ribbon-form caption buttons three rows
		// higher than Windows' (it sizes them from CaptionButtonSize less the resize border).
		// CaptionHeight and FrameBorderSize stay the driver's own (the base's).
		internal override Size CaptionButtonSize { get { return new Size (36, 22); } }        // SM_CXSIZE/SM_CYSIZE
		internal override Size SmallCaptionButtonSize { get { return new Size (22, 22); } }   // SM_CXSMSIZE/SM_CYSMSIZE
		public override Size ToolWindowCaptionButtonSize { get { return new Size (22, 22); } } // SM_CXSMSIZE/SM_CYSMSIZE
		public override Size MenuButtonSize { get { return new Size (19, 19); } }             // SM_CXMENUSIZE/SM_CYMENUSIZE
		internal override int HorizontalResizeBorderThickness { get { return 4; } }           // SM_CXSIZEFRAME
		internal override int VerticalResizeBorderThickness { get { return 4; } }             // SM_CYSIZEFRAME
		// SPI_GETFONTSMOOTHINGTYPE: ClearType (2) on a Windows 11 set up as it comes, and what this
		// stack renders text with everywhere; Windows' own answer where there is one. The base said
		// 1 (standard antialiasing), which stock never reports on such a machine.
		internal override int FontSmoothingType {
			get {
				if (OperatingSystem.IsWindows ()) {
					uint type = 0;
					if (SystemParametersInfoW (0x200A, 0, ref type, 0)) return (int) type;
				}
				return 2;
			}
		}

		[System.Runtime.InteropServices.DllImport ("user32", EntryPoint = "SystemParametersInfoW")]
		static extern bool SystemParametersInfoW (uint action, uint param, ref uint value, uint winIni);
	}
}
