// The driver's windows in the process's one focus model (WindowsBase's PlatformFocus): SetFocus and
// GetFocus are its, and the WM_KILLFOCUS / WM_SETFOCUS it sends a driver window come through
// SendMessage, as the driver sent them itself before. Focus can now be on a WPF window as well -- an
// ElementHost's tree, which takes the keyboard when it is pressed -- and a WinForms control losing
// it to one is told so, with the window that has it, as on Windows.
extern alias WindowsBase;

using System;
using PlatformFocus = WindowsBase::MS.Internal.Interop.PlatformFocus;

namespace System.Windows.Forms {
	internal partial class XplatUIWebGpu {
		private sealed class FocusWindows : PlatformFocus.IWindowSystem
		{
			private readonly XplatUIWebGpu _driver;
			internal FocusWindows(XplatUIWebGpu driver) => _driver = driver;

			public bool Owns(IntPtr handle) => Hwnd.ObjectFromHandle(handle) != null;

			public void Deliver(IntPtr handle, int msg, IntPtr wParam, IntPtr lParam)
				=> _driver.SendMessage(handle, (Msg)msg, wParam, lParam);

			// the driver's windows are drawn into a host's window: no OS sends them anything
			public bool GetsNativeFocusMessages(IntPtr handle) => false;

			// The keyboard reaches a driver window through its form's host window, which has it
			// whenever the form is active: a press on the form gives it back from a child window that
			// took it (CocoaHost, browser-window.js). In the browser a control focused from code takes
			// it back from such a child too.
			public void TakeNativeKeyboard(IntPtr handle)
			{
				if (OperatingSystem.IsBrowser())
					WindowsBase::MS.Internal.Interop.BrowserWindow.ReleaseKeyboard();
			}
		}

		private bool _focusRegistered;

		private void EnsureFocusRegistered()
		{
			if (_focusRegistered) return;
			_focusRegistered = true;
			PlatformFocus.Register(new FocusWindows(this));
		}

		/// <summary>The driver window with the keyboard (the WM_KEYDOWN / WM_CHAR target), zero when it
		/// is on none of them -- nowhere, or on a WPF window.</summary>
		private IntPtr _focusHandle
		{
			get
			{
				IntPtr f = PlatformFocus.GetFocus();
				return f != IntPtr.Zero && Hwnd.ObjectFromHandle(f) != null ? f : IntPtr.Zero;
			}
		}

		/// <summary>GetFocus: whichever window has the keyboard, the driver's or not.</summary>
		internal IntPtr FocusHandle { get { EnsureFocusRegistered(); return PlatformFocus.GetFocus(); } }

		internal override void SetFocus(IntPtr handle)
		{
			EnsureFocusRegistered();
			PlatformFocus.SetFocus(handle);
		}
	}
}
