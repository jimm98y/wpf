// Windows: UpdateLayeredWindow on one of the stack's windows.
//
// On Windows the source DC is a real GDI DC (usually a memory DC with a 32-bit DIB section selected,
// as DotNetBar's drop shadow and soft-edged splash screens use), so its bits are read from GDI
// itself; the macOS implementation (Gdi32.cs) reads them from its own emulated bitmaps. The driver
// then shows that bitmap as the window's picture (XplatUIWebGpu.SetLayeredImage), with the blend's
// per-pixel and constant alpha; what the window paints is not shown. Without it a shadow window was
// an opaque grey box laid over the menu it shades.

using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;

namespace WpfMac.Win32;

public static unsafe partial class Exports
{
    [Win32ExportOnWindows(A0, "UpdateLayeredWindow")]
    public static int WinUpdateLayeredWindow(nint hwnd, nint hdcDst, POINT* pptDst, int* psize, nint hdcSrc, POINT* pptSrc, uint key, void* blend, uint flags)
    {
        const uint ULW_COLORKEY = 1, ULW_ALPHA = 2;
        object? o = Resolve(hwnd);
        if (o is not Forms.Control control) return TRUE;
        if (pptDst != null || psize != null)
            Place(o, pptDst != null ? pptDst->X : null, pptDst != null ? pptDst->Y : null,
                  psize != null ? psize[0] : null, psize != null ? psize[1] : null);
        float opacity = (flags & ULW_ALPHA) != 0 && blend != null ? ((byte*)blend)[2] / 255f : 1f;
        if (Environment.GetEnvironmentVariable("WF_TRACE_ULW") == "1")
            Console.Error.WriteLine($"[ulw] {DateTime.Now:HH:mm:ss.fff} hwnd=0x{hwnd:x} flags={flags} alpha={(blend != null ? ((byte*)blend)[2] : -1)} fmt={(blend != null ? ((byte*)blend)[3] : -1)} src={(hdcSrc != 0)}");
        bool perPixel = (flags & ULW_ALPHA) != 0 && blend != null && ((byte*)blend)[3] != 0;
        if (hdcSrc == 0) return TRUE;                                   // a move, resize or new alpha only

        bool timing = Environment.GetEnvironmentVariable("WF_TRACE_ULW") == "1";
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        (AppContext.GetData("System.Drawing.FlushHdc") as Action<IntPtr>)?.Invoke(hdcSrc);
        GdiFlushW();
        nint hbm = RealGetCurrentObject(hdcSrc, 7 /* OBJ_BITMAP */);
        BITMAP bm;
        if (hbm == 0 || RealGetObject(hbm, sizeof(BITMAP), &bm) < sizeof(BITMAP) || bm.bmBitsPixel != 32)
        {
            LogOnce("ulw-win-nobits", "win32: UpdateLayeredWindow from a DC with no 32-bit bitmap selected: only the position, size and alpha change");
            return TRUE;
        }
        // The bits through GetDIBits, asked for TOP-DOWN: a DIB section's own memory is bottom-up or
        // top-down by how it was created, and GetObject does not say which (it reports a positive
        // height either way), so reading the memory directly turned a top-down bitmap upside down.
        int bw = bm.bmWidth, bh = Math.Abs(bm.bmHeight);
        var bits = new uint[bw * bh];
        var bi = new BITMAPINFOHEADER { biSize = sizeof(BITMAPINFOHEADER), biWidth = bw, biHeight = -bh, biPlanes = 1, biBitCount = 32 };
        byte* info = stackalloc byte[sizeof(BITMAPINFOHEADER) + 16];
        *(BITMAPINFOHEADER*)info = bi;
        fixed (uint* p = bits)
            if (RealGetDIBits(hdcSrc, hbm, 0, (uint)bh, p, info, 0) == 0) return TRUE;
        long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
        int w = psize != null ? psize[0] : bw, h = psize != null ? psize[1] : bh;
        int sx = pptSrc != null ? pptSrc->X : 0, sy = pptSrc != null ? pptSrc->Y : 0;
        if (w <= 0 || h <= 0) return TRUE;
        var rgba = new byte[w * h * 4];
        var raw = new byte[w * h * 4];   // the picture as handed over: premultiplied BGRA, for a host that shows it as is
        for (int y = 0; y < h && sy + y < bh; y++)
        {
            for (int x = 0; x < w && sx + x < bw; x++)
            {
                uint c = bits[(sy + y) * bw + sx + x];
                byte b = (byte)c, g = (byte)(c >> 8), r = (byte)(c >> 16), a = perPixel ? (byte)(c >> 24) : (byte)255;
                if ((flags & ULW_COLORKEY) != 0 && (uint)(r | g << 8 | b << 16) == (key & 0xFFFFFF)) a = 0;
                else if (perPixel && a != 0 && a != 255)
                {
                    r = (byte)Math.Min(255, r * 255 / a); g = (byte)Math.Min(255, g * 255 / a); b = (byte)Math.Min(255, b * 255 / a);
                }
                int i = (y * w + x) * 4;
                rgba[i] = r; rgba[i + 1] = g; rgba[i + 2] = b; rgba[i + 3] = a;
                if (a != 0) { raw[i] = (byte)c; raw[i + 1] = (byte)(c >> 8); raw[i + 2] = (byte)(c >> 16); raw[i + 3] = a; }
            }
        }
        long t2 = System.Diagnostics.Stopwatch.GetTimestamp();
        Ui(o, () =>
        {
            if (!control.IsHandleCreated) return 0;
            object? driver = typeof(Forms.Control).Assembly.GetType("System.Windows.Forms.XplatUI")
                ?.GetField("driver", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)?.GetValue(null);
            driver?.GetType().GetMethod("SetLayeredImage", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                ?.Invoke(driver, new object[] { control.Handle, rgba, w, h, opacity });
            driver?.GetType().GetMethod("SetLayeredBits", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                ?.Invoke(driver, new object[] { control.Handle, raw, w, h, (byte)Math.Round(opacity * 255) });
            return 0;
        });
        if (timing)
        {
            double ms(long a, long b) => (b - a) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            Console.Error.WriteLine($"[ulw] flush+bits {ms(t0, t1):0.0} ms, convert {ms(t1, t2):0.0} ms, driver {ms(t2, System.Diagnostics.Stopwatch.GetTimestamp()):0.0} ms");
        }
        return TRUE;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP { public int bmType, bmWidth, bmHeight, bmWidthBytes; public ushort bmPlanes, bmBitsPixel; public void* bmBits; }


    [DllImport("gdi32", EntryPoint = "GetCurrentObject")] private static extern nint RealGetCurrentObject(nint hdc, uint type);
    [DllImport("gdi32", EntryPoint = "GetObjectW")] private static extern int RealGetObject(nint h, int size, void* buffer);
    [DllImport("gdi32", EntryPoint = "GdiFlush")] private static extern int GdiFlushW();
    [DllImport("gdi32", EntryPoint = "GetDIBits")] private static extern int RealGetDIBits(nint hdc, nint hbm, uint start, uint lines, void* bits, void* info, uint usage);
}
