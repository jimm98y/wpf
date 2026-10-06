// Hwnd.cs (the cross-platform window-handle table) carries an X11-driver-specific
// XEventQueue field. The X11 driver itself is excluded from this build, so a minimal
// stub type just satisfies the reference; our WebGPU driver never populates it.

namespace System.Windows.Forms
{
	internal sealed class XEventQueue
	{
		public System.Collections.Queue Paint;
		public int Count { get { return 0; } }
	}
}
