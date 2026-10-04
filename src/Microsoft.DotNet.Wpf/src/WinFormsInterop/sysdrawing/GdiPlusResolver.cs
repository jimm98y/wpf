// Mono's System.Drawing binds its GDI+ P/Invokes to "gdiplus" and historically remapped that to
// libgdiplus via a .config <dllmap>. .NET Core dropped dllmap, so we register a DllImportResolver
// (once, at module load) that maps "gdiplus" -> libgdiplus on the current OS.
//
// NOTHING IS LOADED UP FRONT. System.Drawing is managed on every platform; the only GDI+ entry
// points left belong to the few objects that are still GDI+'s (a Metafile). The first time one of
// those P/Invokes is resolved, the library is loaded HERE and GdiplusStartup is called on it, so a
// process that never touches a metafile never loads gdiplus.dll or libgdiplus at all.

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SystemDrawingWebGpu
{
    internal static class GdiPlusResolver
    {
        private static bool s_done;

        // Called from the GDIPlus static ctor (NOT a [ModuleInitializer]: the wasm interpreter NIYs on
        // running a <Module>.cctor in mixed AOT+interp mode, which is fatal).
        internal static void Init()
        {
            if (s_done) return;
            s_done = true;

            // The browser (wasm) has NO libgdiplus, and registering a managed DllImportResolver delegate
            // traps on the mono-wasm interpreter (native callback / GetFunctionPointerForDelegate is NIY
            // there). Nothing there reaches GDI+, so skip it entirely.
            if (OperatingSystem.IsBrowser()) return;

            NativeLibrary.SetDllImportResolver(typeof(GdiPlusResolver).Assembly, (name, asm, searchPath) =>
            {
                if (name != "gdiplus") return IntPtr.Zero;   // let other imports resolve normally
                // WF_NO_GDIPLUS=1 refuses GDI+ outright -- the proof that nothing still needs it.
                // Returning Zero would NOT refuse it: the runtime then carries on with its default
                // probing, which finds the system's gdiplus.dll on Windows. So it resolves to a library
                // that has none of GDI+'s exports, and every call still reaching GDI+ fails visibly
                // (EntryPointNotFoundException) instead of quietly working.
                if (Environment.GetEnvironmentVariable("WF_NO_GDIPLUS") == "1")
                    return NativeLibrary.TryLoad(OperatingSystem.IsWindows() ? "kernel32.dll" : "libc", out IntPtr none) ? none : IntPtr.Zero;
                foreach (string candidate in Candidates)
                    if (NativeLibrary.TryLoad(candidate, out IntPtr handle))
                    {
                        Start(handle);
                        return handle;
                    }
                return IntPtr.Zero;
            });
        }

        private static readonly object s_startLock = new object();

        // GdiplusStartup, once, on the library just loaded -- before the call that needed it runs.
        private static unsafe void Start(IntPtr library)
        {
            lock (s_startLock)
            {
                if (System.Drawing.GDIPlus.GdiPlusToken != 0) return;
                if (!NativeLibrary.TryGetExport(library, "GdiplusStartup", out IntPtr fn)) return;
                var input = System.Drawing.GdiplusStartupInput.MakeGdiplusStartupInput();
                var output = System.Drawing.GdiplusStartupOutput.MakeGdiplusStartupOutput();
                ulong token = 0;
                var startup = (delegate* unmanaged<ulong*, System.Drawing.GdiplusStartupInput*, System.Drawing.GdiplusStartupOutput*, int>)fn;
                if (startup(&token, &input, &output) == 0)
                    System.Drawing.GDIPlus.GdiPlusToken = token;
            }
        }

        private static readonly string[] Candidates =
        {
            "libgdiplus.dylib",                       // via DYLD_*_LIBRARY_PATH
            "/opt/homebrew/lib/libgdiplus.dylib",     // Homebrew (Apple Silicon)
            "/usr/local/lib/libgdiplus.dylib",        // Homebrew (Intel)
            "libgdiplus.so.0", "libgdiplus.so",       // Linux
            "gdiplus",                                // Windows (real gdiplus.dll)
        };
    }
}
