// The window manager's side of Win32 for native callers. An application's user32/gdi32 P/Invokes are
// rewritten to wpfrun's Win32 implementations (WpfWebGpu.Win32), not this assembly; where Windows
// answers from its window manager -- a window's frame, its non-client area, its update region, a
// layered window's picture, AnimateWindow -- those answer through here, from this driver's own state.
//
// Public, so the shim calls it directly: it was reaching the driver and Hwnd by reflection, which the
// browser's AOT compiler cannot follow. Only public types cross, and nothing of the backend's internals.

using System.Collections.Generic;
using System.Drawing;

namespace System.Windows.Forms
{
	public static class Win32Bridge
	{
		private static XplatUIWebGpu Driver => XplatUIWebGpu.GetInstance ();

		/// <summary>Whether <paramref name="handle"/> is a window of this WinForms.</summary>
		public static bool IsWindow (IntPtr handle) => handle != IntPtr.Zero && Hwnd.ObjectFromHandle (handle) != null;

		/// <summary>Every window handle of this WinForms, a snapshot.</summary>
		public static IntPtr[] Windows () => Hwnd.Handles ();

		/// <summary>The window's update region's bounds (GetUpdateRect), empty when it has none.</summary>
		public static Rectangle UpdateBounds (IntPtr handle) => Hwnd.ObjectFromHandle (handle)?.Invalid ?? Rectangle.Empty;

		/// <summary>The controls of <paramref name="control"/>, its implicit ones (its own scroll bars) too.</summary>
		public static Control[] AllControls (Control control) => control.Controls.GetAllControls ();

		/// <summary>The DPI the process started at (GetDpiForSystem).</summary>
		public static int InitialSystemDpi => ScaleHelper.InitialSystemDpi;

		/// <summary>The window's DPI (GetDpiForWindow).</summary>
		public static int GetDpiForWindow (IntPtr handle) => Driver.GetDpiForWindow (handle);

		/// <summary>The width of the window's border, on each side.</summary>
		public static int NonClientInset (IntPtr handle) => Driver.NonClientInset (handle);

		/// <summary>The window's non-client area drawn again (after a caller drew over it).</summary>
		public static void RedrawNonClient (IntPtr handle) => Driver.RedrawNonClient (handle);

		/// <summary>The window's frame worked out again from its style (SetWindowPos SWP_FRAMECHANGED).</summary>
		public static void RecalculateFrame (IntPtr handle) => Driver.RecalculateFrame (handle);

		/// <summary>The window's update region validated (ValidateRect/ValidateRgn with no area).</summary>
		public static void ValidateWindow (IntPtr handle, bool repaint) => Driver.ValidateWindow (handle, repaint);

		public static bool SetTopmost (IntPtr handle, bool topmost) => XplatUI.SetTopmost (handle, topmost);

		/// <summary>A message through the window's procedure, as SendMessage does.</summary>
		public static IntPtr SendMessage (IntPtr handle, int message, IntPtr wParam, IntPtr lParam)
			=> XplatUI.SendMessage (handle, (Msg) message, wParam, lParam);

		public static bool AnimateWindow (IntPtr handle, int time, int flags) => Driver.AnimateWindow (handle, time, flags);

		/// <summary>A layered window's picture: straight RGBA, at <paramref name="opacity"/> over its own alpha.</summary>
		public static void SetLayeredImage (IntPtr handle, byte[] rgba, int width, int height, float opacity)
			=> Driver.SetLayeredImage (handle, rgba, width, height, opacity);

		/// <summary>A layered window's picture as UpdateLayeredWindow hands it: premultiplied BGRA.</summary>
		public static void SetLayeredBits (IntPtr handle, byte[] bgra, int width, int height, byte alpha)
			=> Driver.SetLayeredBits (handle, bgra, width, height, alpha);

		// ---- diagnostics (wpfrun --screenshots, WPFRUN_DUMP_WINDOWS, WPFRUN_DEBUG_WHEEL) -----------

		/// <summary>A wheel turn at a screen point, as the OS would deliver it.</summary>
		public static void InjectWheel (int screenX, int screenY, int delta) => Driver.InjectWheel (screenX, screenY, delta);

		/// <summary>What the host composites for a form: handle, x, y per window, back to front.</summary>
		public static long[] PresentWindows (IntPtr form) => Driver.GetPresentWindows (form);

		/// <summary>The forms on screen, each with its host's kind and native window (zero where it has none).</summary>
		public static (Form Form, string Host, IntPtr NativeWindow)[] HostedForms ()
		{
			var list = new List<(Form, string, IntPtr)> ();
			foreach (var (host, form) in PresentationHost.HostedForms ())
				list.Add ((form, host.GetType ().Name, host.NativeWindow));
			return list.ToArray ();
		}

		/// <summary>The frame a form's host last presented, saved as a PNG; false when it has no host.</summary>
		public static bool SaveFrame (Form form, string path)
		{
			IWinFormsHost host = PresentationHost.HostOf (form);
			if (host == null) return false;
			host.SaveFrame (path);
			return true;
		}
	}
}
