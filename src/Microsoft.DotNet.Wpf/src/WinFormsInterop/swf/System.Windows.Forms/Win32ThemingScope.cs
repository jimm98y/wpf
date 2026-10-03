// .NET's ThemingScope for the common dialogs Windows draws (the file dialogs): comctl32 v6 active on
// the thread while one runs, as CommonDialog.ShowDialog does. Platform-gated DllImports; no COM.

using System.Runtime.InteropServices;

namespace System.Windows.Forms
{
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
