// The window a Windows common dialog is owned by. .NET's CommonDialog passes the owner's handle, or
// the active window, and parks the dialog on a window of its own when there is neither, because an
// ownerless dialog is not modal to the application and PrintDlgEx refuses one outright
// (E_INVALIDARG). The active window is null whenever the process is not in the foreground -- an app
// started from a script, a dialog opened from a timer -- so this falls back to the calling thread's
// own visible top-level window: the host window of the form the user is in.

using System;
using System.Runtime.InteropServices;

namespace System.Windows.Forms
{
	internal static class Win32DialogOwner
	{
		private delegate bool EnumProc (IntPtr hwnd, IntPtr lParam);

		[DllImport ("user32.dll")]
		private static extern IntPtr GetActiveWindow ();
		[DllImport ("user32.dll")]
		private static extern bool EnumThreadWindows (uint thread, EnumProc proc, IntPtr lParam);
		[DllImport ("user32.dll")]
		private static extern bool IsWindowVisible (IntPtr hwnd);
		[DllImport ("kernel32.dll")]
		private static extern uint GetCurrentThreadId ();

		internal static IntPtr Get ()
		{
			IntPtr active = GetActiveWindow ();
			if (active != IntPtr.Zero)
				return active;
			IntPtr found = IntPtr.Zero;
			EnumThreadWindows (GetCurrentThreadId (), (h, l) => {
				if (!IsWindowVisible (h))
					return true;
				found = h;
				return false;
			}, IntPtr.Zero);
			return found;
		}
	}
}
