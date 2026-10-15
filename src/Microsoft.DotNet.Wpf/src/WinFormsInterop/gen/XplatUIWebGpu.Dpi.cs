// Per-monitor DPI for a per-monitor-aware application (ScaleHelper.IsDeviceScaled): each top-level
// window has the DPI of the display it is on, its children share it, and a change arrives the way
// Windows delivers one -- WM_DPICHANGED_BEFOREPARENT to the children, top down, WM_DPICHANGED to the
// window, WM_DPICHANGED_AFTERPARENT to the children -- so .NET's own handling of those messages
// (Control, Form, ContainerControl) does the rescaling. Head-agnostic: a host reports the DPI of
// the display its window is on (Win32Host from GetDpiForWindow; a backing scale factor elsewhere).
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;

namespace System.Windows.Forms {
	internal partial class XplatUIWebGpu {
		internal const int WM_DPICHANGED = 0x02E0;
		internal const int WM_DPICHANGED_BEFOREPARENT = 0x02E2;
		internal const int WM_DPICHANGED_AFTERPARENT = 0x02E3;
		internal const int WM_GETDPISCALEDSIZE = 0x02E4;

		// The DPI of each top-level window whose host has reported one.
		private readonly Dictionary<IntPtr, int> _windowDpi = new Dictionary<IntPtr, int>();

		/// <summary>
		/// The platform's part of entering per-monitor awareness: make the process per-monitor aware
		/// if it can still be made so, and say what it is -- 2 per-monitor, 1 system-aware, 0 not
		/// aware (or no DPI on this platform) -- and the system DPI.
		/// </summary>
		internal static int EnterPerMonitorAwareness(out int systemDpi)
		{
			systemDpi = 0;
			if (PlatformAwareness != null)
				return PlatformAwareness(out systemDpi);
			if (!OperatingSystem.IsWindows())
				return 0;
			try
			{
				SetProcessDpiAwarenessContext((IntPtr)(-4));   // PER_MONITOR_AWARE_V2; fails if already set
				int awareness = GetAwarenessFromDpiAwarenessContext(GetThreadDpiAwarenessContext());
				systemDpi = (int)GetDpiForSystem();
				return awareness;
			}
			catch (EntryPointNotFoundException) { return 0; }
			catch (DllNotFoundException) { return 0; }
		}

		internal delegate int AwarenessProvider(out int systemDpi);

		/// <summary>A non-Windows head's answer to <see cref="EnterPerMonitorAwareness"/>.</summary>
		internal static AwarenessProvider PlatformAwareness;

		/// <summary>The DPI a window lays out for: its top-level window's, as its host reported it;
		/// the system DPI until it has.</summary>
		internal int WindowDpi(IntPtr handle)
		{
			if (!ScaleHelper.IsDeviceScaled) return ScaleHelper.OneHundredPercentLogicalDpi;
			Hwnd h = Hwnd.ObjectFromHandle(handle);
			for (Hwnd w = h; w != null; w = w.parent)
			{
				lock (_windowDpi)
					if (_windowDpi.TryGetValue(w.Handle, out int dpi)) return dpi;
				if ((w.initial_style & WindowStyles.WS_CHILD) == 0) break;
			}
			return ScaleHelper.InitialSystemDpi;
		}

		/// <summary>GetDpiForWindow.</summary>
		internal int GetDpiForWindow(IntPtr handle) => WindowDpi(handle);

		/// <summary>A host says which DPI its window starts at (the display it was created on).</summary>
		internal void SetWindowDpi(IntPtr handle, int dpi)
		{
			if (dpi <= 0) return;
			lock (_windowDpi) _windowDpi[handle] = dpi;
		}

		/// <summary>
		/// A host's window moved to a display with another DPI (its WM_DPICHANGED): the window and all
		/// its children take it, and they are told as Windows tells them. <paramref name="suggested"/>
		/// is the window rectangle Windows proposes, in driver coordinates.
		/// </summary>
		internal unsafe void HostDpiChanged(IntPtr handle, int dpi, Rectangle suggested)
		{
			if (!ScaleHelper.IsThreadPerMonitorV2Aware || dpi <= 0) return;
			lock (_windowDpi)
			{
				if (_windowDpi.TryGetValue(handle, out int old) && old == dpi) return;
				_windowDpi[handle] = dpi;
			}
			var below = new List<IntPtr>();
			CollectDescendants(handle, below);
			foreach (IntPtr c in below)
				SendMessage(c, (Msg)WM_DPICHANGED_BEFOREPARENT, IntPtr.Zero, IntPtr.Zero);
			var rect = new DpiRect { Left = suggested.Left, Top = suggested.Top, Right = suggested.Right, Bottom = suggested.Bottom };
			SendMessage(handle, (Msg)WM_DPICHANGED, (IntPtr)((dpi << 16) | dpi), (IntPtr)(&rect));
			for (int i = below.Count - 1; i >= 0; i--)
				SendMessage(below[i], (Msg)WM_DPICHANGED_AFTERPARENT, IntPtr.Zero, IntPtr.Zero);
			_paintVersion++;
		}

		private void CollectDescendants(IntPtr handle, List<IntPtr> into)
		{
			Hwnd h = Hwnd.ObjectFromHandle(handle);
			if (h == null) return;
			foreach (Hwnd c in h.children)
			{
				if (c == null || (c.initial_style & WindowStyles.WS_CHILD) == 0) continue;
				into.Add(c.Handle);
				CollectDescendants(c.Handle, into);
			}
		}

		[StructLayout(LayoutKind.Sequential)]
		internal struct DpiRect { public int Left, Top, Right, Bottom; }

		[DllImport("user32.dll")] private static extern IntPtr SetProcessDpiAwarenessContext(IntPtr ctx);
		[DllImport("user32.dll")] private static extern IntPtr GetThreadDpiAwarenessContext();
		[DllImport("user32.dll")] private static extern int GetAwarenessFromDpiAwarenessContext(IntPtr ctx);
		[DllImport("user32.dll")] private static extern uint GetDpiForSystem();
		[DllImport("user32.dll")] private static extern bool AdjustWindowRectExForDpi(ref DpiRect rect, int style, bool menu, int exStyle, uint dpi);

		/// <summary>AdjustWindowRectExForDpi's frame, on Windows; null where it cannot be asked.</summary>
		internal static Padding? NativeFrameForDpi(int style, int exStyle, bool menu, int dpi)
		{
			if (!OperatingSystem.IsWindows()) return null;
			try
			{
				var r = new DpiRect();
				if (!AdjustWindowRectExForDpi(ref r, style, menu, exStyle, (uint)dpi)) return null;
				return new Padding(-r.Left, -r.Top, r.Right, r.Bottom);
			}
			catch (EntryPointNotFoundException) { return null; }
			catch (DllNotFoundException) { return null; }
		}
	}
}
