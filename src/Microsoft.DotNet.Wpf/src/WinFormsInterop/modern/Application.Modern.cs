// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// The application's colour mode, ported from .NET's Application (SetColorMode, ColorMode, SystemColorMode,
// IsDarkModeEnabled): dark mode is the alternative set of system colours (SystemColors.UseAlternativeColorSet), every
// window told the colours changed (WM_SYSCOLORCHANGE). The system's mode is Windows' AppsUseLightTheme on Windows,
// and the desktop's appearance elsewhere (macOS's, the freedesktop portal's colour scheme on Linux) -- what WPF's
// ThemeMode.System follows too.

extern alias WindowsBase;

using System.Drawing;
using Microsoft.Win32;

namespace System.Windows.Forms
{
	/// <summary>The colour mode of an application: light (classic), the system's, or dark.</summary>
	public enum SystemColorMode
	{
		Classic = 0,
		System = 1,
		Dark = 2,
	}

	public sealed partial class Application
	{
		private const string DarkModeKeyPath = "HKEY_CURRENT_USER\\Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize";
		private const string DarkModeKey = "AppsUseLightTheme";

		private static SystemColorMode? s_colorMode;

		/// <summary>The colour mode set by <see cref="SetColorMode(SystemColorMode)"/>; Classic until it is.</summary>
		public static SystemColorMode ColorMode => s_colorMode ?? SystemColorMode.Classic;

		internal static bool ColorModeSet => s_colorMode != null;

		/// <summary>Sets the application's colour mode, and redraws its windows in it.</summary>
		public static void SetColorMode (SystemColorMode systemColorMode)
		{
			try {
				if (systemColorMode != SystemColorMode.Classic && systemColorMode != SystemColorMode.System && systemColorMode != SystemColorMode.Dark)
					throw new ArgumentOutOfRangeException (nameof (systemColorMode));
				if (systemColorMode == s_colorMode)
					return;
				s_colorMode = systemColorMode;
			} finally {
#pragma warning disable SYSLIB5002
				bool darkModeEnabled = IsDarkModeEnabled;
				if (SystemColors.UseAlternativeColorSet != darkModeEnabled) {
					SystemColors.UseAlternativeColorSet = darkModeEnabled;
					NotifySystemColorsChanged ();
				}
#pragma warning restore SYSLIB5002
			}
		}

		// what Windows does for a change of the system colours: WM_SYSCOLORCHANGE to every top-level window
		private static void NotifySystemColorsChanged ()
		{
			var forms = new System.Collections.Generic.List<Form> ();
			foreach (Form f in OpenForms)
				forms.Add (f);
			foreach (Form form in forms) {
				if (form.IsDisposed || !form.IsHandleCreated)
					continue;
				Action notify = () => {
					SendSysColorChange (form);
					form.Invalidate (true);
				};
				if (form.InvokeRequired)
					form.BeginInvoke (notify);
				else
					notify ();
			}
		}

		private static void SendSysColorChange (Control control)
		{
			if (control.IsHandleCreated)
				XplatUI.SendMessage (control.Handle, Msg.WM_SYSCOLORCHANGE, IntPtr.Zero, IntPtr.Zero);
			foreach (Control child in control.Controls)
				SendSysColorChange (child);
		}

		/// <summary>The system's colour mode: Dark where dark mode is on, else Classic.</summary>
		public static SystemColorMode SystemColorMode => SystemIsDark () ? SystemColorMode.Dark : SystemColorMode.Classic;

		private static bool SystemIsDark ()
		{
			if (SystemInformation.HighContrast)
				return false;
			try {
				if (OperatingSystem.IsWindows ()) {
					// Windows 11 and later: 0 for dark, 1 for light
					if (Environment.OSVersion.Version.Build < 22000)
						return false;
					return Registry.GetValue (DarkModeKeyPath, DarkModeKey, 1) is int light && light == 0;
				}
				// every other platform's own (macOS, Linux, iOS, Android, the browser)
				return WindowsBase::MS.Internal.Interop.PlatformSettings.IsSystemDarkTheme ();
			} catch (Exception) {
			}
			return false;
		}

		/// <summary>Whether the application is drawn dark: its colour mode Dark, or System with the system's dark.</summary>
		public static bool IsDarkModeEnabled =>
			!SystemInformation.HighContrast
			&& (ColorMode == SystemColorMode.Dark || (ColorMode == SystemColorMode.System && SystemColorMode == SystemColorMode.Dark));

		/// <summary>Whether <see cref="EnableVisualStyles"/> has been called.</summary>
		public static bool UseVisualStyles => visual_styles_enabled;
	}
}
