//
// System.Drawing.gdipFunctions.cs
//
// Authors: 
//	Alexandre Pigolkine (pigolkine@gmx.de)
//	Jordi Mas i Hernandez (jordi@ximian.com)
//	Sanjay Gupta (gsanjay@novell.com)
//	Ravindra (rkumar@novell.com)
//	Peter Dennis Bartok (pbartok@novell.com)
//	Sebastien Pouliot <sebastien@ximian.com>
//
// Copyright (C) 2004 - 2007 Novell, Inc (http://www.novell.com)
//
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
// 
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
//

using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.Security;
using System.Runtime.InteropServices.ComTypes;

namespace System.Drawing
{
	internal partial class SafeNativeMethods
	{
		internal partial class Gdip : GDIPlus
		{

		}
	}

	/// <summary>
	/// GDI+ API Functions
	/// </summary>
	internal /*static*/ partial class GDIPlus {
		public const int FACESIZE = 32;
		public const int LANG_NEUTRAL = 0;
		public static IntPtr Display = IntPtr.Zero;
		public static bool UseX11Drawable = false;
		public static bool UseCarbonDrawable = false;
		public static bool UseCocoaDrawable = false;

		// THERE IS NO GDI+ HERE. System.Drawing is managed on every platform -- images, paths,
		// regions, brushes, pens, text, metafiles, printing -- and gdiplus.dll / libgdiplus is never
		// loaded: nothing in this assembly binds to it. What remains below is plain Win32/X11 that a
		// few Windows- or X11-only members reach (screen copies, icons, HDCs).

		static void ProcessExit (object sender, EventArgs e)
		{
			// Called all pending objects and claim any pending handle before
			// shutting down
			GC.Collect ();
			GC.WaitForPendingFinalizers ();
#if false
			GdiPlusToken = 0;

			// This causes crashes in because this call occurs before all
			// managed GDI+ objects are finalized. When they are finalized
			// they call into a shutdown GDI+ and we crash.
			GdiplusShutdown (ref GdiPlusToken);

			// This causes crashes in Mono libgdiplus because this call
			// occurs before all managed GDI objects are finalized
			// When they are finalized they use the closed display and
			// crash
			if (UseX11Drawable && Display != IntPtr.Zero) {
				XCloseDisplay (Display);
			}
#endif
		}

		static GDIPlus ()
		{
#if NETSTANDARD1_6
			bool isUnix = !RuntimeInformation.IsOSPlatform (OSPlatform.Windows);
#else
			int platform = (int) Environment.OSVersion.Platform;
			bool isUnix = (platform == 4) || (platform == 6) || (platform == 128);
#endif

			if (isUnix) {
				if (Environment.GetEnvironmentVariable ("not_supported_MONO_MWF_USE_NEW_X11_BACKEND") != null || Environment.GetEnvironmentVariable ("MONO_MWF_MAC_FORCE_X11") != null) {
					UseX11Drawable = true;
				} else {
					IntPtr buf = Marshal.AllocHGlobal (8192);
					// This is kind of a hack but gets us sysname from uname (struct utsname *name) on
					// linux and darwin. uname may be unavailable (browser/wasm) -> default to X11.
					try {
						if (uname (buf) != 0) {
							// WTH: We couldn't detect the OS; lets default to X11
							UseX11Drawable = true;
						} else {
							string os = Marshal.PtrToStringAnsi (buf);
							if (os == "Darwin")
								UseCarbonDrawable = true;
							else
								UseX11Drawable = true;
						}
					} catch (Exception) { UseX11Drawable = true; }
					Marshal.FreeHGlobal (buf);
				}
			}

			// under MS 1.x this event is raised only for the default application domain
#if !NETSTANDARD1_6
			AppDomain.CurrentDomain.ProcessExit += new EventHandler (ProcessExit);
#endif
		}

		static public bool RunningOnWindows ()
		{
			// Ask the OS instead of INFERRING Windows from "no unix drawable was detected".
			//
			// On the browser none of the three unix backends get set: there is no X11, no Carbon and
			// no Cocoa, and the cctor above never even reaches its uname fallback because
			// Environment.OSVersion.Platform on wasm is not one of the PlatformID values it tests for.
			// The old expression therefore concluded "Windows" in a browser, and every caller took a
			// native Win32 path. KnownColors' cctor is the one that bites: it P/Invoked
			// user32!GetSysColor, threw DllNotFoundException, and surfaced as a
			// TypeInitializationException that killed the first WinForms control constructed.
			//
			// Every other caller guards Windows-only native work too (GDI+ handles, HBITMAP, metafiles),
			// so the honest answer is the safe one. This changes nothing on Windows, macOS or Linux,
			// where the inferred answer already matched the real one.
			return OperatingSystem.IsWindows ();
		}

		static public bool RunningOnUnix ()
		{
			return UseX11Drawable || UseCarbonDrawable || UseCocoaDrawable;
		}

		// Copies a Ptr to an array of Points and releases the memory
		static public void FromUnManagedMemoryToPointI (IntPtr prt, Point [] pts)
		{
			int nPointSize = Marshal.SizeOf (pts[0]);
			IntPtr pos = prt;
			for (int i=0; i<pts.Length; i++, pos = new IntPtr (pos.ToInt64 () + nPointSize))
				pts[i] = (Point) Marshal.PtrToStructure (pos, typeof (Point));

			Marshal.FreeHGlobal (prt);
		}

		// Copies a Ptr to an array of Points and releases the memory
		static public void FromUnManagedMemoryToPoint (IntPtr prt, PointF [] pts)
		{
			int nPointSize = Marshal.SizeOf (pts[0]);
			IntPtr pos = prt;
			for (int i=0; i<pts.Length; i++, pos = new IntPtr (pos.ToInt64 () + nPointSize))
				pts[i] = (PointF) Marshal.PtrToStructure (pos, typeof (PointF));

			Marshal.FreeHGlobal (prt);
		}

		// Copies an array of Points to unmanaged memory
		static public IntPtr FromPointToUnManagedMemoryI (Point [] pts)
		{
			int nPointSize = Marshal.SizeOf (pts[0]);
			IntPtr dest = Marshal.AllocHGlobal (nPointSize * pts.Length);
			IntPtr pos = dest;
			for (int i=0; i<pts.Length; i++, pos = new IntPtr (pos.ToInt64 () + nPointSize))
				Marshal.StructureToPtr (pts[i], pos, false);

			return dest;
		}

		// Copies a Ptr to an array of v and releases the memory
		static public void FromUnManagedMemoryToRectangles (IntPtr prt, RectangleF [] pts)
		{
			int nPointSize = Marshal.SizeOf (pts[0]);
			IntPtr pos = prt;
			for (int i = 0; i < pts.Length; i++, pos = new IntPtr (pos.ToInt64 () + nPointSize))
				pts[i] = (RectangleF) Marshal.PtrToStructure (pos, typeof (RectangleF));

			Marshal.FreeHGlobal (prt);
		}

		// Copies an array of Points to unmanaged memory
		static public IntPtr FromPointToUnManagedMemory (PointF [] pts)
		{
			int nPointSize = Marshal.SizeOf (pts[0]);
			IntPtr dest = Marshal.AllocHGlobal (nPointSize * pts.Length);
			IntPtr pos = dest;
			for (int i=0; i<pts.Length; i++, pos = new IntPtr (pos.ToInt64 () + nPointSize))
				Marshal.StructureToPtr (pts[i], pos, false);

			return dest;
		}

		// Converts a status into exception
		// TODO: Add more status code mappings here
		static internal void CheckStatus (Status status)
		{
			string msg;
			switch (status) {
			case Status.Ok:
				return;
			case Status.GenericError:
				msg = Locale.GetText ("Generic Error [GDI+ status: {0}]", status);
				throw new Exception (msg);
			case Status.InvalidParameter:
				msg = Locale.GetText ("A null reference or invalid value was found [GDI+ status: {0}]", status);
				throw new ArgumentException (msg);
			case Status.OutOfMemory:
				msg = Locale.GetText ("Not enough memory to complete operation [GDI+ status: {0}]", status);
				throw new OutOfMemoryException (msg);
			case Status.ObjectBusy:
				msg = Locale.GetText ("Object is busy and cannot state allow this operation [GDI+ status: {0}]", status);
				throw new MemberAccessException (msg);
			case Status.InsufficientBuffer:
				msg = Locale.GetText ("Insufficient buffer provided to complete operation [GDI+ status: {0}]", status);
				throw new InternalBufferOverflowException (msg);
			case Status.PropertyNotSupported:
				msg = Locale.GetText ("Property not supported [GDI+ status: {0}]", status);
				throw new NotSupportedException (msg);
			case Status.FileNotFound:
				msg = Locale.GetText ("Requested file was not found [GDI+ status: {0}]", status);
				throw new FileNotFoundException (msg);
			case Status.AccessDenied:
				msg = Locale.GetText ("Access to resource was denied [GDI+ status: {0}]", status);
				throw new UnauthorizedAccessException (msg);
			case Status.UnknownImageFormat:
				msg = Locale.GetText ("Either the image format is unknown or you don't have the required libraries to decode this format [GDI+ status: {0}]", status);
				throw new NotSupportedException (msg);
			case Status.NotImplemented:
				msg = Locale.GetText ("The requested feature is not implemented [GDI+ status: {0}]", status);
				throw new NotImplementedException (msg);
			case Status.WrongState:
				msg = Locale.GetText ("Object is not in a state that can allow this operation [GDI+ status: {0}]", status);
				throw new InvalidOperationException (msg);
			case Status.FontFamilyNotFound:
				msg = Locale.GetText ("The requested FontFamily could not be found [GDI+ status: {0}]", status);
				throw new ArgumentException (msg);
			case Status.ValueOverflow:
				msg = Locale.GetText ("Argument is out of range [GDI+ status: {0}]", status);
				throw new OverflowException (msg);
			case Status.Win32Error:
				msg = Locale.GetText ("The operation is invalid [GDI+ status: {0}]", status);
				throw new InvalidOperationException (msg);
			default:
				msg = Locale.GetText ("Unknown Error [GDI+ status: {0}]", status);
				throw new Exception (msg);
			}
		}

		// This is win32/gdi, not gdiplus, but it's easier to keep in here, also see above comment
		[DllImport("gdi32.dll", CallingConvention=CallingConvention.StdCall, CharSet = CharSet.Auto)]
		internal static extern IntPtr CreateFontIndirect (ref LOGFONT logfont);
		[DllImport("user32.dll", EntryPoint="GetDC", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
		internal static extern IntPtr GetDC(IntPtr hwnd);
		[DllImport("user32.dll", EntryPoint="ReleaseDC", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
		internal static extern int ReleaseDC (IntPtr hWnd, IntPtr hDC);
		[DllImport("gdi32.dll", EntryPoint="SelectObject", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
		internal static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
		[DllImport("user32.dll", SetLastError=true)]
		internal static extern bool GetIconInfo (IntPtr hIcon, out IconInfo iconinfo);
		[DllImport("user32.dll", CallingConvention = CallingConvention.StdCall, SetLastError=true)]
		internal static extern IntPtr CreateIconIndirect ([In] ref IconInfo piconinfo);
		[DllImport("user32.dll", CallingConvention = CallingConvention.StdCall, SetLastError=true)]
		internal static extern bool DestroyIcon (IntPtr hIcon);
		[DllImport("gdi32.dll")]
		internal static extern bool DeleteObject (IntPtr hObject);
		[DllImport("user32.dll")]
		internal static extern IntPtr GetDesktopWindow ();

		[DllImport("gdi32.dll", SetLastError=true)]
		public static extern int BitBlt(IntPtr hdcDest, int nXDest, int nYDest,
			int nWidth, int nHeight, IntPtr hdcSrc, int nXSrc, int nYSrc, int dwRop);

		[DllImport ("user32.dll", EntryPoint = "GetSysColor", CallingConvention = CallingConvention.StdCall)]
		public static extern uint Win32GetSysColor (GetSysColorIndex index);


		// Some special X11 stuff
		[DllImport("libX11", EntryPoint="XOpenDisplay")]
		internal extern static IntPtr XOpenDisplay(IntPtr display);

		[DllImport("libX11", EntryPoint="XCloseDisplay")]
		internal extern static int XCloseDisplay(IntPtr display);

		[DllImport ("libX11", EntryPoint="XRootWindow")]
		internal extern static IntPtr XRootWindow(IntPtr display, int screen);

		[DllImport ("libX11", EntryPoint="XDefaultScreen")]
		internal extern static int XDefaultScreen(IntPtr display);

		[DllImport ("libX11", EntryPoint="XDefaultDepth")]
		internal extern static uint XDefaultDepth(IntPtr display, int screen);

		[DllImport ("libX11", EntryPoint="XGetImage")]
		internal extern static IntPtr XGetImage(IntPtr display, IntPtr drawable, int src_x, int src_y, int width, int height, int pane, int format);

		[DllImport ("libX11", EntryPoint="XGetPixel")]
		internal extern static int XGetPixel(IntPtr image, int x, int y);

		[DllImport ("libX11", EntryPoint="XDestroyImage")]
		internal extern static int XDestroyImage(IntPtr image);

		[DllImport ("libX11", EntryPoint="XDefaultVisual")]
		internal extern static IntPtr XDefaultVisual(IntPtr display, int screen);

		[DllImport ("libX11", EntryPoint="XGetVisualInfo")]
		internal extern static IntPtr XGetVisualInfo (IntPtr display, int vinfo_mask, ref XVisualInfo vinfo_template, ref int nitems);

		[DllImport ("libX11", EntryPoint="XVisualIDFromVisual")]
		internal extern static IntPtr XVisualIDFromVisual (IntPtr visual);

		[DllImport ("libX11", EntryPoint="XFree")]
		internal extern static void XFree (IntPtr data);

		[DllImport ("libc")]
		static extern int uname (IntPtr buf);
	}
}
