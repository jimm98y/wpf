// The colour dialog Windows itself shows: comdlg32's ChooseColorW, called as .NET's
// ColorDialog.RunDialog calls it -- the flag word from the dialog's properties (Options) with
// CC_RGBINIT and CC_ENABLEHOOK added and CC_FULLOPEN taken away when full open is not allowed, the
// colour in and out as a COLORREF, the sixteen custom colours in and out, owned by the active
// window, inside the comctl32 v6 activation context, centred by the CommonDialog hook. A plain,
// platform-gated DllImport; no COM.
//
// The other platforms' bridges live next door (MacColorDialogBridge) and in the WPF host
// (WindowsFormsHost: iOS's UIColorPickerViewController and the browser's <input type=color>).
// Where none exists -- Linux, whose desktop portal has only a screen colour PICKER (an eyedropper,
// Screenshot.PickColor) and no colour chooser, and Android, which has no system colour picker --
// the managed dialog runs, laid out as this one.

using System;
using System.Drawing;
using System.Runtime.InteropServices;

namespace System.Windows.Forms
{
	/// <summary>What a colour dialog is asking, in ChooseColor's terms, and -- after a platform
	/// dialog answered OK -- what it answered.</summary>
	internal sealed class ColorDialogRequest
	{
		internal const int CC_RGBINIT = 0x1, CC_FULLOPEN = 0x2, CC_PREVENTFULLOPEN = 0x4, CC_SHOWHELP = 0x8,
			CC_ENABLEHOOK = 0x10, CC_ENABLETEMPLATE = 0x20, CC_ENABLETEMPLATEHANDLE = 0x40,
			CC_SOLIDCOLOR = 0x80, CC_ANYCOLOR = 0x100;

		/// <summary>The colour shown first, and on OK the colour chosen.</summary>
		public Color Color;
		/// <summary>The sixteen custom colours as COLORREFs (0x00BBGGRR), in and out.</summary>
		public int [] CustomColors = new int [16];
		/// <summary>ChooseColor's flag word as .NET builds it: Options | CC_RGBINIT, CC_FULLOPEN
		/// cleared when CC_PREVENTFULLOPEN is set. CC_ENABLEHOOK is the Windows bridge's own.</summary>
		public int Flags;
		/// <summary>ColorDialog.Instance, used by Windows only with a dialog template.</summary>
		public IntPtr Instance;
		/// <summary>The dialog asking, for its HelpRequest event.</summary>
		public CommonDialog Dialog;

		public bool AllowFullOpen => (Flags & CC_PREVENTFULLOPEN) == 0;
		public bool FullOpen => (Flags & CC_FULLOPEN) != 0;
		public bool ShowHelp => (Flags & CC_SHOWHELP) != 0;
		public bool SolidColorOnly => (Flags & CC_SOLIDCOLOR) != 0;
		public bool AnyColor => (Flags & CC_ANYCOLOR) != 0;

		/// <summary>A COLORREF, as ColorTranslator.ToWin32.</summary>
		internal static int ToColorRef (Color c) => c.R | (c.G << 8) | (c.B << 16);

		/// <summary>An opaque colour from a COLORREF, as ColorTranslator.FromWin32.</summary>
		internal static Color FromColorRef (int value) => Color.FromArgb (value & 0xFF, (value >> 8) & 0xFF, (value >> 16) & 0xFF);
	}

	/// <summary>A platform colour dialog. Null from Show means there is none to show here (or it
	/// could not be reached), and the managed dialog runs; true means OK and the request holds the
	/// answer.</summary>
	internal interface IColorDialogBridge
	{
		bool? Show (ColorDialogRequest request);

		/// <summary>The dialog without waiting: the only form iOS and the browser can serve. A bridge
		/// whose platform can block inherits this, which simply runs the call.</summary>
		System.Threading.Tasks.Task<bool?> ShowAsync (ColorDialogRequest request)
			=> System.Threading.Tasks.Task.FromResult (Show (request));
	}

	internal sealed class Win32ColorDialogBridge : IColorDialogBridge
	{
		internal static readonly IColorDialogBridge Default =
			OperatingSystem.IsWindows () ? new Win32ColorDialogBridge () : null;

		private const int WM_INITDIALOG = 0x0110, WM_COMMAND = 0x0111, pshHelp = 0x040e;

		// CHOOSECOLORW with the natural 64-bit layout (32-bit packs it to 1, which this port does not ship).
		[StructLayout (LayoutKind.Sequential)]
		internal struct CHOOSECOLORW
		{
			public int lStructSize;
			public IntPtr hwndOwner, hInstance;
			public int rgbResult;
			public IntPtr lpCustColors;
			public int Flags;
			public IntPtr lCustData, lpfnHook, lpTemplateName;
		}

		private delegate IntPtr HookProc (IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

		[DllImport ("comdlg32.dll", CharSet = CharSet.Unicode)]
		private static extern bool ChooseColorW (ref CHOOSECOLORW cc);
		[DllImport ("user32.dll")]
		private static extern IntPtr SetFocus (IntPtr hwnd);

		/// <summary>The structure ChooseColor is handed for this request (minus the hook), so the
		/// mapping can be checked without opening a dialog.</summary>
		internal static CHOOSECOLORW Build (ColorDialogRequest request, IntPtr owner, IntPtr customColors)
		{
			int flags = request.Flags | ColorDialogRequest.CC_RGBINIT | ColorDialogRequest.CC_ENABLEHOOK;
			if (!request.AllowFullOpen)
				flags &= ~ColorDialogRequest.CC_FULLOPEN;
			bool template = (flags & (ColorDialogRequest.CC_ENABLETEMPLATE | ColorDialogRequest.CC_ENABLETEMPLATEHANDLE)) != 0;
			return new CHOOSECOLORW {
				lStructSize = Marshal.SizeOf<CHOOSECOLORW> (),
				hwndOwner = owner,
				hInstance = template ? request.Instance : IntPtr.Zero,
				rgbResult = ColorDialogRequest.ToColorRef (request.Color),
				lpCustColors = customColors,
				Flags = flags,
			};
		}

		public bool? Show (ColorDialogRequest request)
		{
			IntPtr custom = Marshal.AllocHGlobal (16 * sizeof (int));
			IntPtr cookie = IntPtr.Zero;
			try {
				for (int i = 0; i < 16; i++)
					Marshal.WriteInt32 (custom, i * sizeof (int), i < request.CustomColors.Length ? request.CustomColors [i] : 0x00FFFFFF);

				HookProc hook = (hWnd, msg, wParam, lParam) => {
					// CommonDialog.HookProc: centred on the screen the mouse is on, its control focused.
					if (msg == WM_INITDIALOG) {
						Win32PrintDialogBridge.MoveToScreenCenter (hWnd);
						SetFocus (wParam);
					} else if (msg == WM_COMMAND && ((int) (long) wParam & 0xFFFF) == pshHelp) {
						// The Help button raises HelpRequest, as .NET's dialog does.
						request.Dialog?.RaiseHelpRequest ();
						return (IntPtr) 1;
					}
					return IntPtr.Zero;
				};
				CHOOSECOLORW cc = Build (request, Win32DialogOwner.Get (), custom);
				cc.lpfnHook = Marshal.GetFunctionPointerForDelegate (hook);

				cookie = Win32ThemingScope.Enter ();
				bool ok = ChooseColorW (ref cc);
				GC.KeepAlive (hook);
				if (!ok)
					return false;

				// .NET keeps a named colour when the dialog hands back the same value.
				if (cc.rgbResult != ColorDialogRequest.ToColorRef (request.Color))
					request.Color = ColorDialogRequest.FromColorRef (cc.rgbResult);
				for (int i = 0; i < request.CustomColors.Length && i < 16; i++)
					request.CustomColors [i] = Marshal.ReadInt32 (custom, i * sizeof (int));
				return true;
			} finally {
				Win32ThemingScope.Leave (cookie);
				Marshal.FreeHGlobal (custom);
			}
		}
	}
}
