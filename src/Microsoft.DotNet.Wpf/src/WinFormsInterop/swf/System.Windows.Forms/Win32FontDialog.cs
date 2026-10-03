// The font chooser Windows itself shows, for the Windows head.
//
// Stock WinForms' FontDialog is no dialog of its own: RunDialog fills a CHOOSEFONTW and calls
// comdlg32's ChooseFont, so what the user sees is Windows' window -- its layout, its grouping of a
// family's weights into styles ("Semibold Italic", "Black"), its sample, its script list. Mono's
// managed dialog is a hand-built imitation of the Windows 2000 one and cannot be made the same
// window by laying out controls. So on Windows the port calls ChooseFont the way .NET does -- the
// same flags, the same hook, the same centring -- and the managed dialog stays the fallback where
// there is no common dialog to call. Plain DllImports gated on OperatingSystem.IsWindows(), as in
// Win32FileDialogBridge next door; no COM.

using System.Drawing;
using System.Runtime.InteropServices;

namespace System.Windows.Forms
{
	internal static class Win32FontDialog
	{
		internal static bool Available => OperatingSystem.IsWindows ();

		// CHOOSEFONT flags, as .NET's FontDialog names them.
		internal const int CF_SCREENFONTS = 0x00000001;
		internal const int CF_SHOWHELP = 0x00000004;
		private const int CF_ENABLEHOOK = 0x00000008;
		private const int CF_INITTOLOGFONTSTRUCT = 0x00000040;
		internal const int CF_EFFECTS = 0x00000100;
		internal const int CF_APPLY = 0x00000200;
		internal const int CF_SCRIPTSONLY = 0x00000400;
		internal const int CF_NOVECTORFONTS = 0x00000800;
		internal const int CF_NOSIMULATIONS = 0x00001000;
		private const int CF_LIMITSIZE = 0x00002000;
		internal const int CF_FIXEDPITCHONLY = 0x00004000;
		internal const int CF_FORCEFONTEXIST = 0x00010000;
		internal const int CF_TTONLY = 0x00040000;
		internal const int CF_SELECTSCRIPT = 0x00400000;
		internal const int CF_NOVERTFONTS = 0x01000000;

		private const int WM_INITDIALOG = 0x0110;
		private const int WM_COMMAND = 0x0111;
		private const int WM_SETFOCUS = 0x0007;
		private const int WM_CHOOSEFONT_GETLOGFONT = 0x0401;
		private const int CDM_SETDEFAULTFOCUS = 0x0451;
		private const int CB_GETCURSEL = 0x0147;
		private const int CB_GETITEMDATA = 0x0150;
		private const int IDC_APPLY = 0x402;
		private const int IDC_COLOR_COMBO = 0x473;    // cmb4
		private const int IDC_COLOR_LABEL = 0x443;    // stc4
		private const int LOGPIXELSY = 90;

		[StructLayout (LayoutKind.Sequential, CharSet = CharSet.Unicode)]
		private struct LOGFONTW
		{
			public int lfHeight, lfWidth, lfEscapement, lfOrientation, lfWeight;
			public byte lfItalic, lfUnderline, lfStrikeOut, lfCharSet;
			public byte lfOutPrecision, lfClipPrecision, lfQuality, lfPitchAndFamily;
			[MarshalAs (UnmanagedType.ByValTStr, SizeConst = 32)]
			public string lfFaceName;
		}

		[StructLayout (LayoutKind.Sequential, CharSet = CharSet.Unicode)]
		private struct CHOOSEFONTW
		{
			public int lStructSize;
			public IntPtr hwndOwner;
			public IntPtr hDC;
			public IntPtr lpLogFont;
			public int iPointSize;
			public int Flags;
			public int rgbColors;
			public IntPtr lCustData;
			public IntPtr lpfnHook;
			public IntPtr lpTemplateName;
			public IntPtr hInstance;
			public IntPtr lpszStyle;
			public short nFontType;
			public short alignment;
			public int nSizeMin;
			public int nSizeMax;
		}

		private delegate IntPtr HookProc (IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

		[StructLayout (LayoutKind.Sequential)]
		private struct RECT { public int left, top, right, bottom; }

		[StructLayout (LayoutKind.Sequential)]
		private struct POINT { public int x, y; }

		[StructLayout (LayoutKind.Sequential)]
		private struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public int dwFlags; }

		[DllImport ("comdlg32.dll", CharSet = CharSet.Unicode)]
		private static extern bool ChooseFontW (ref CHOOSEFONTW cf);
		[DllImport ("comdlg32.dll")]
		private static extern int CommDlgExtendedError ();
		[DllImport ("user32.dll")]
		private static extern IntPtr GetActiveWindow ();
		[DllImport ("user32.dll")]
		private static extern IntPtr GetDC (IntPtr hwnd);
		[DllImport ("user32.dll")]
		private static extern int ReleaseDC (IntPtr hwnd, IntPtr hdc);
		[DllImport ("gdi32.dll")]
		private static extern int GetDeviceCaps (IntPtr hdc, int index);
		[DllImport ("kernel32.dll", CharSet = CharSet.Unicode)]
		private static extern IntPtr GetModuleHandleW (string name);
		[DllImport ("user32.dll")]
		private static extern IntPtr GetDlgItem (IntPtr hDlg, int id);
		[DllImport ("user32.dll")]
		private static extern bool ShowWindow (IntPtr hwnd, int cmd);
		[DllImport ("user32.dll")]
		private static extern IntPtr SetFocus (IntPtr hwnd);
		[DllImport ("user32.dll")]
		private static extern bool PostMessageW (IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
		[DllImport ("user32.dll")]
		private static extern IntPtr SendMessageW (IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
		[DllImport ("user32.dll")]
		private static extern IntPtr SendDlgItemMessageW (IntPtr hDlg, int id, int msg, IntPtr wParam, IntPtr lParam);
		[DllImport ("user32.dll")]
		private static extern bool GetWindowRect (IntPtr hwnd, out RECT r);
		[DllImport ("user32.dll")]
		private static extern bool SetWindowPos (IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, int flags);
		[DllImport ("user32.dll")]
		private static extern bool GetCursorPos (out POINT p);
		[DllImport ("user32.dll")]
		private static extern IntPtr MonitorFromPoint (POINT p, int flags);
		[DllImport ("user32.dll")]
		private static extern bool GetMonitorInfoW (IntPtr monitor, ref MONITORINFO info);

		/// <summary>Runs ChooseFont as .NET's FontDialog.RunDialog does. False if the user cancelled
		/// (or the dialog refused); otherwise <paramref name="font"/> and <paramref name="color"/>
		/// are what was chosen. <paramref name="apply"/> is called for the Apply button.</summary>
		internal static bool Show (int options, Font initial, int minSize, int maxSize, bool showColor,
		                           ref Font font, ref Color color, Action<Font, Color> apply)
		{
			IntPtr defaultControl = IntPtr.Zero;
			Color current = color;
			HookProc hook = (hWnd, msg, wParam, lParam) => {
				switch (msg) {
				case WM_COMMAND:
					if ((int) (long) wParam == IDC_APPLY) {
						Font applied = ReadDialogFont (hWnd);
						int index = (int) SendDlgItemMessageW (hWnd, IDC_COLOR_COMBO, CB_GETCURSEL, IntPtr.Zero, IntPtr.Zero);
						if (index != -1)
							current = ColorTranslator.FromWin32 ((int) SendDlgItemMessageW (hWnd, IDC_COLOR_COMBO, CB_GETITEMDATA, (IntPtr) index, IntPtr.Zero));
						if (applied != null)
							apply (applied, current);
					}
					break;
				case WM_INITDIALOG:
					// ChooseFont shows its colour list with the effects; .NET hides it unless asked.
					if (!showColor) {
						ShowWindow (GetDlgItem (hWnd, IDC_COLOR_COMBO), 0);
						ShowWindow (GetDlgItem (hWnd, IDC_COLOR_LABEL), 0);
					}
					// CommonDialog.HookProc: centred on the screen the mouse is on, the control the
					// dialog proposes focused.
					MoveToScreenCenter (hWnd);
					defaultControl = wParam;
					if (defaultControl != IntPtr.Zero)
						SetFocus (defaultControl);
					break;
				case WM_SETFOCUS:
					PostMessageW (hWnd, CDM_SETDEFAULTFOCUS, IntPtr.Zero, IntPtr.Zero);
					break;
				case CDM_SETDEFAULTFOCUS:
					SetFocus (defaultControl);
					break;
				}
				return IntPtr.Zero;
			};

			IntPtr logFont = Marshal.AllocHGlobal (Marshal.SizeOf<LOGFONTW> ());
			try {
				Marshal.StructureToPtr (ToLogFont (initial), logFont, false);
				var cf = new CHOOSEFONTW {
					lStructSize = Marshal.SizeOf<CHOOSEFONTW> (),
					hwndOwner = GetActiveWindow (),
					lpLogFont = logFont,
					Flags = options | CF_INITTOLOGFONTSTRUCT | CF_ENABLEHOOK,
					lpfnHook = Marshal.GetFunctionPointerForDelegate (hook),
					hInstance = GetModuleHandleW (null),
					nSizeMin = minSize,
					nSizeMax = maxSize == 0 ? int.MaxValue : maxSize,
					rgbColors = ColorTranslator.ToWin32 (showColor || (options & CF_EFFECTS) != 0 ? color : SystemColors.ControlText),
				};
				if (minSize > 0 || maxSize > 0)
					cf.Flags |= CF_LIMITSIZE;

				// CommonDialog.ShowDialog runs every dialog in a ThemingScope -- comctl32 v6, so the
				// dialog's controls are themed -- and FontDialog.RunDialog in a system-aware DPI scope,
				// because "the native font dialog does not support Per Monitor V2". On a mixed-DPI
				// machine that makes Windows' dialog a system-DPI window scaled by the compositor,
				// with a size readout that disagrees with the font's; stock shows exactly that.
				bool ok;
				IntPtr cookie = Win32ThemingScope.Enter ();
				IntPtr dpi = SetThreadDpiAwarenessContextSafe ((IntPtr) (-2) /* SYSTEM_AWARE */);
				try {
					ok = ChooseFontW (ref cf);
				} finally {
					if (dpi != IntPtr.Zero)
						SetThreadDpiAwarenessContextSafe (dpi);
					Win32ThemingScope.Leave (cookie);
				}
				GC.KeepAlive (hook);
				if (!ok) {
					int why = CommDlgExtendedError ();
					if (why != 0)
						Console.Error.WriteLine ($"[fontdialog] comdlg32 refused: 0x{why:X4}");
					return false;
				}
				var lf = Marshal.PtrToStructure<LOGFONTW> (logFont);
				if (!string.IsNullOrEmpty (lf.lfFaceName)) {
					font = FromLogFont (lf);
					color = ColorTranslator.FromWin32 (cf.rgbColors);
				}
				return true;
			} finally {
				Marshal.FreeHGlobal (logFont);
			}
		}

		private static Font ReadDialogFont (IntPtr hWnd)
		{
			IntPtr buffer = Marshal.AllocHGlobal (Marshal.SizeOf<LOGFONTW> ());
			try {
				SendMessageW (hWnd, WM_CHOOSEFONT_GETLOGFONT, IntPtr.Zero, buffer);
				var lf = Marshal.PtrToStructure<LOGFONTW> (buffer);
				return string.IsNullOrEmpty (lf.lfFaceName) ? null : FromLogFont (lf);
			} finally {
				Marshal.FreeHGlobal (buffer);
			}
		}

		/// <summary>CommonDialog.MoveToScreenCenter: across the middle and a third of the way down
		/// the working area of the monitor under the mouse.</summary>
		private static void MoveToScreenCenter (IntPtr hWnd)
		{
			GetWindowRect (hWnd, out RECT r);
			GetCursorPos (out POINT p);
			var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO> () };
			if (!GetMonitorInfoW (MonitorFromPoint (p, 2 /* MONITOR_DEFAULTTONEAREST */), ref info))
				return;
			RECT s = info.rcWork;
			int x = s.left + (s.right - s.left - (r.right - r.left)) / 2;
			int y = s.top + (s.bottom - s.top - (r.bottom - r.top)) / 3;
			SetWindowPos (hWnd, IntPtr.Zero, x, y, 0, 0, 0x0001 | 0x0004 | 0x0010 /* NOSIZE|NOZORDER|NOACTIVATE */);
		}

		[DllImport ("user32.dll")]
		private static extern IntPtr SetThreadDpiAwarenessContext (IntPtr context);

		/// <summary>The previous context, or zero where the call does not exist (before Windows 10 1607).</summary>
		private static IntPtr SetThreadDpiAwarenessContextSafe (IntPtr context)
		{
			try {
				return SetThreadDpiAwarenessContext (context);
			} catch (EntryPointNotFoundException) {
				return IntPtr.Zero;
			}
		}

		private static int ScreenDpi ()
		{
			IntPtr dc = GetDC (IntPtr.Zero);
			try {
				return GetDeviceCaps (dc, LOGPIXELSY);
			} finally {
				ReleaseDC (IntPtr.Zero, dc);
			}
		}

		/// <summary>Font.ToLogFont against the screen: the em height in device pixels, negative.</summary>
		private static LOGFONTW ToLogFont (Font font)
		{
			return new LOGFONTW {
				lfHeight = -(int) Math.Round (font.SizeInPoints * ScreenDpi () / 72f),
				lfWeight = font.Bold ? 700 : 400,
				lfItalic = (byte) (font.Italic ? 1 : 0),
				lfUnderline = (byte) (font.Underline ? 1 : 0),
				lfStrikeOut = (byte) (font.Strikeout ? 1 : 0),
				lfCharSet = font.GdiCharSet,
				lfFaceName = font.GdiVerticalFont ? "@" + font.Name : font.Name,
			};
		}

		/// <summary>FontDialog.UpdateFont: Font.FromLogFont against the screen, then in points
		/// (ControlPaint.FontInPoints).</summary>
		private static Font FromLogFont (LOGFONTW lf)
		{
			float pixels = Math.Abs (lf.lfHeight);
			FontStyle style = FontStyle.Regular;
			if (lf.lfWeight >= 700) style |= FontStyle.Bold;
			if (lf.lfItalic != 0) style |= FontStyle.Italic;
			if (lf.lfUnderline != 0) style |= FontStyle.Underline;
			if (lf.lfStrikeOut != 0) style |= FontStyle.Strikeout;
			bool vertical = lf.lfFaceName.StartsWith ("@", StringComparison.Ordinal);
			string face = vertical ? lf.lfFaceName.Substring (1) : lf.lfFaceName;
			return new Font (face, pixels * 72f / ScreenDpi (), style, GraphicsUnit.Point, lf.lfCharSet, vertical);
		}
	}

	/// <summary>.NET's ThemingScope: an activation context naming comctl32 v6, made once from the
	/// same manifest .NET writes to a temporary file, active on this thread while a common dialog
	/// runs -- and only when Application.EnableVisualStyles was called, as in .NET.</summary>
	internal static class Win32ThemingScope
	{
		private const string Manifest =
			@"<?xml version=""1.0"" encoding=""UTF-8"" standalone=""yes""?>
<assembly xmlns=""urn:schemas-microsoft-com:asm.v1"" manifestVersion=""1.0"">
  <dependency>
    <dependentAssembly>
      <assemblyIdentity type=""win32"" name=""Microsoft.Windows.Common-Controls"" version=""6.0.0.0"" processorArchitecture=""*"" publicKeyToken=""6595b64144ccf1df"" language=""*"" />
    </dependentAssembly>
  </dependency>
</assembly>
";

		[StructLayout (LayoutKind.Sequential, CharSet = CharSet.Unicode)]
		private struct ACTCTXW
		{
			public int cbSize;
			public int dwFlags;
			public string lpSource;
			public short wProcessorArchitecture;
			public short wLangId;
			public string lpAssemblyDirectory;
			public IntPtr lpResourceName;
			public string lpApplicationName;
			public IntPtr hModule;
		}

		[DllImport ("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
		private static extern IntPtr CreateActCtxW (ref ACTCTXW actctx);
		[DllImport ("kernel32.dll", SetLastError = true)]
		private static extern bool ActivateActCtx (IntPtr hActCtx, out IntPtr cookie);
		[DllImport ("kernel32.dll", SetLastError = true)]
		private static extern bool DeactivateActCtx (int flags, IntPtr cookie);

		private static readonly object s_lock = new object ();
		private static IntPtr s_context;
		private static bool s_tried;

		private static IntPtr Context ()
		{
			lock (s_lock) {
				if (s_tried)
					return s_context;
				s_tried = true;
				string path = IO.Path.Combine (IO.Path.GetTempPath (), IO.Path.GetRandomFileName ());
				try {
					IO.File.WriteAllText (path, Manifest);
					var act = new ACTCTXW { cbSize = Marshal.SizeOf<ACTCTXW> (), lpSource = path };
					IntPtr h = CreateActCtxW (ref act);
					s_context = h == (IntPtr) (-1) ? IntPtr.Zero : h;
				} catch (Exception) {
					s_context = IntPtr.Zero;
				} finally {
					try { IO.File.Delete (path); } catch (Exception) { }
				}
				return s_context;
			}
		}

		/// <summary>A cookie for Leave, or zero when nothing was activated.</summary>
		internal static IntPtr Enter ()
		{
			if (!Application.VisualStylesEnabled)
				return IntPtr.Zero;
			IntPtr context = Context ();
			if (context == IntPtr.Zero || !ActivateActCtx (context, out IntPtr cookie))
				return IntPtr.Zero;
			return cookie;
		}

		internal static void Leave (IntPtr cookie)
		{
			if (cookie != IntPtr.Zero)
				DeactivateActCtx (0, cookie);
		}
	}
}
