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
		internal override int MouseButtonCount { get { return 3; } }                   // SM_CMOUSEBUTTONS
		internal override int KeyboardSpeed { get { return 31; } }                     // SPI_GETKEYBOARDSPEED default
		internal override int KeyboardDelay { get { return 1; } }                      // SPI_GETKEYBOARDDELAY default
	}
}
