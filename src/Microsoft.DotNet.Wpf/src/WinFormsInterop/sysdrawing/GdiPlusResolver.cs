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
        [ModuleInitializer]
        internal static void Init()
        {
            NativeLibrary.SetDllImportResolver(typeof(GdiPlusResolver).Assembly, (name, asm, searchPath) =>
            {
                if (name != "gdiplus") return IntPtr.Zero;   // let other imports resolve normally
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
