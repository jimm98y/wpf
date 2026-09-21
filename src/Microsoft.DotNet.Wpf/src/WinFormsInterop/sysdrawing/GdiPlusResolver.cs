// Mono's System.Drawing binds its GDI+ P/Invokes to "gdiplus" and historically remapped that to
// libgdiplus via a .config <dllmap>. .NET Core dropped dllmap, so we register a DllImportResolver
// (once, at module load) that maps "gdiplus" -> libgdiplus on the current OS. This is what lets the
// vendored System.Drawing find the native rasterizer off-Windows. (When the GPU-raster backend
// replaces the gdip* draw calls, this native dependency goes away for those paths.)

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
            // there). The GPU-raster path is libgdiplus-free, so skip it entirely.
            if (OperatingSystem.IsBrowser()) return;

            NativeLibrary.SetDllImportResolver(typeof(GdiPlusResolver).Assembly, (name, asm, searchPath) =>
            {
                if (name != "gdiplus") return IntPtr.Zero;   // let other imports resolve normally
                // WF_NO_GDIPLUS=1 refuses to load libgdiplus — proves the GPU-raster path is truly
                // libgdiplus-free (the browser has no libgdiplus). Any remaining gdip call then throws.
                if (Environment.GetEnvironmentVariable("WF_NO_GDIPLUS") == "1") return IntPtr.Zero;
                foreach (string candidate in Candidates)
                    if (NativeLibrary.TryLoad(candidate, out IntPtr handle))
                        return handle;
                return IntPtr.Zero;
            });
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
