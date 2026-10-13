// Windows: device contexts for the stack's windows.
//
// GetDC, GetWindowDC, GetDCEx and BeginPaint on a stack window hand out a REAL GDI device context: a
// memory DC over a 32-bit DIB section the size of the window. Everything the app then does with it is
// real gdi32 (SelectObject, BitBlt, FillRect, DrawText, uxtheme's DrawThemeBackground,
// Graphics.FromHdc), exactly as on a window Windows owns. ReleaseDC and EndPaint take what was drawn
// and put it on the window: the pixels become an image drawn into the window's picture through
// Control.CreateGraphics, which is how this stack's WinForms puts drawing done outside a paint on
// screen.
//
// Only what the app drew is put there. The DIB starts filled with a marker colour, RGB(2,3,4), and
// pixels still holding it (whatever their alpha: GDI writes 0 there, and a Graphics over the DC
// copies the DIB through a 32bpp RGB bitmap and back) are left out, so drawing a part of a window
// does not paint the rest of it. The marker is also what such a DC
// reads back where nothing was drawn yet: blending onto it (an antialiased edge drawn straight onto a
// window DC) blends with the marker rather than with what the window shows.

using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;

namespace WpfMac.Win32;

public static unsafe partial class Exports
{
    private const uint Untouched = 0x01020304;

    private sealed class WindowDC
    {
        public nint Hwnd, Dc, Dib, OldBitmap;
        public uint* Bits;
        public int Width, Height;
        public int OffsetX, OffsetY;      // where the DIB's origin is in client coordinates
    }

    private static readonly ConcurrentDictionary<nint, WindowDC> s_windowDcs = new();

    private static WindowDC? NewWindowDC(nint hwnd, bool window)
    {
        if (Resolve(hwnd) is not Forms.Control c) return null;
        (int w, int h, int ox, int oy) = Ui(c, () =>
        {
            System.Drawing.Size client = c.IsHandleCreated ? c.ClientSize : c.Size;
            if (!window) return (client.Width, client.Height, 0, 0);
            // The window rectangle: the client plus whatever frame the window has.
            var screen = c.RectangleToScreen(new System.Drawing.Rectangle(0, 0, client.Width, client.Height));
            var outer = c.Parent != null ? c.Parent.RectangleToScreen(c.Bounds) : c.Bounds;
            return (Math.Max(client.Width, outer.Width), Math.Max(client.Height, outer.Height), outer.X - screen.X, outer.Y - screen.Y);
        });
        w = Math.Max(1, w); h = Math.Max(1, h);
        nint dc = GdiCreateCompatibleDC(0);
        if (dc == 0) return null;
        var bi = new BITMAPINFOHEADER { biSize = sizeof(BITMAPINFOHEADER), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
        void* bits;
        nint dib = GdiCreateDIBSection(dc, &bi, 0, &bits, 0, 0);
        if (dib == 0) { GdiDeleteDC(dc); return null; }
        new Span<uint>(bits, w * h).Fill(Untouched);
        var wdc = new WindowDC { Hwnd = hwnd, Dc = dc, Dib = dib, Bits = (uint*)bits, Width = w, Height = h, OffsetX = ox, OffsetY = oy };
        wdc.OldBitmap = GdiSelectObject(dc, dib);
        s_windowDcs[dc] = wdc;
        return wdc;
    }

    /// <summary>Puts what was drawn into <paramref name="wdc"/> on its window, and frees the DC.</summary>
    private static void FlushWindowDC(WindowDC wdc)
    {
        try
        {
            // A Graphics still open over the DC writes what it drew first.
            (AppContext.GetData("System.Drawing.FlushHdc") as Action<IntPtr>)?.Invoke(wdc.Dc);
            GdiFlush();
            int w = wdc.Width, h = wdc.Height;
            int minX = w, minY = h, maxX = -1, maxY = -1;
            for (int y = 0; y < h; y++)
            {
                uint* row = wdc.Bits + (long)y * w;
                for (int x = 0; x < w; x++)
                {
                    if ((row[x] & 0xFFFFFF) == (Untouched & 0xFFFFFF)) continue;
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }
            if (maxX < 0 || Resolve(wdc.Hwnd) is not Forms.Control c) return;
            int bw = maxX - minX + 1, bh = maxY - minY + 1;
            using var bmp = new Bitmap(bw, bh, PixelFormat.Format32bppArgb);
            BitmapData data = bmp.LockBits(new Rectangle(0, 0, bw, bh), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                for (int y = 0; y < bh; y++)
                {
                    uint* src = wdc.Bits + (long)(y + minY) * w + minX;
                    uint* dst = (uint*)((byte*)data.Scan0 + (long)y * data.Stride);
                    for (int x = 0; x < bw; x++) dst[x] = (src[x] & 0xFFFFFF) == (Untouched & 0xFFFFFF) ? 0u : src[x] | 0xFF000000;
                }
            }
            finally
            {
                bmp.UnlockBits(data);
            }
            Ui(c, () =>
            {
                if (!c.IsHandleCreated || c.IsDisposed) return 0;
                using Graphics g = c.CreateGraphics();
                g.DrawImageUnscaled(bmp, minX + wdc.OffsetX, minY + wdc.OffsetY);
                return 0;
            });
        }
        finally
        {
            GdiSelectObject(wdc.Dc, wdc.OldBitmap);
            GdiDeleteObject(wdc.Dib);
            GdiDeleteDC(wdc.Dc);
        }
    }

    [UnmanagedCallersOnly] private static nint WinGetDC(nint hwnd) => NewWindowDC(hwnd, window: false)?.Dc ?? 0;
    [UnmanagedCallersOnly] private static nint WinGetWindowDC(nint hwnd) => NewWindowDC(hwnd, window: true)?.Dc ?? 0;
    // DCX_WINDOW (1): the window rectangle rather than the client's. The clip region is the app's own.
    [UnmanagedCallersOnly] private static nint WinGetDCEx(nint hwnd, nint clip, uint flags) => NewWindowDC(hwnd, window: (flags & 1) != 0)?.Dc ?? 0;

    [UnmanagedCallersOnly]
    private static int WinReleaseDC(nint hwnd, nint hdc)
    {
        if (!s_windowDcs.TryRemove(hdc, out var wdc)) return 0;
        FlushWindowDC(wdc);
        return 1;
    }

    // PAINTSTRUCT: hdc, fErase, RECT rcPaint, fRestore, fIncUpdate, rgbReserved[32].
    [UnmanagedCallersOnly]
    private static nint WinBeginPaint(nint hwnd, byte* ps)
    {
        NativeMemory.Clear(ps, 72);
        ValidateWinFormsWindow(hwnd, repaint: true);
        WindowDC? wdc = NewWindowDC(hwnd, window: false);
        if (wdc == null) return 0;
        *(nint*)ps = wdc.Dc;
        *(RECT*)(ps + 12) = new RECT { Right = wdc.Width, Bottom = wdc.Height };
        return wdc.Dc;
    }

    [UnmanagedCallersOnly]
    private static int WinEndPaint(nint hwnd, byte* ps)
    {
        if (ps != null && s_windowDcs.TryRemove(*(nint*)ps, out var wdc)) FlushWindowDC(wdc);
        return TRUE;
    }

    /// <summary>The window's pending update area: all of it while a paint is due, nothing otherwise.</summary>
    [UnmanagedCallersOnly]
    private static int GetUpdateRect(nint hwnd, RECT* r, int erase)
    {
        if (Resolve(hwnd) is not Forms.Control c) return FALSE;
        RECT area = Ui(c, () =>
        {
            object? h = typeof(Forms.Control).Assembly.GetType("System.Windows.Forms.Hwnd")
                ?.GetMethod("ObjectFromHandle")?.Invoke(null, new object[] { (IntPtr)hwnd });
            var invalid = h?.GetType().GetProperty("Invalid")?.GetValue(h) is System.Drawing.Rectangle rc ? rc : System.Drawing.Rectangle.Empty;
            return new RECT { Left = invalid.Left, Top = invalid.Top, Right = invalid.Right, Bottom = invalid.Bottom };
        });
        if (r != null) *r = area;
        return area.Right > area.Left && area.Bottom > area.Top ? TRUE : FALSE;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [DllImport("gdi32", EntryPoint = "CreateCompatibleDC")] private static extern nint GdiCreateCompatibleDC(nint hdc);
    [DllImport("gdi32", EntryPoint = "CreateDIBSection")] private static extern nint GdiCreateDIBSection(nint hdc, BITMAPINFOHEADER* bmi, uint usage, void** bits, nint section, uint offset);
    [DllImport("gdi32", EntryPoint = "SelectObject")] private static extern nint GdiSelectObject(nint hdc, nint obj);
    [DllImport("gdi32", EntryPoint = "DeleteObject")] private static extern int GdiDeleteObject(nint obj);
    [DllImport("gdi32", EntryPoint = "DeleteDC")] private static extern int GdiDeleteDC(nint hdc);
    [DllImport("gdi32", EntryPoint = "GdiFlush")] private static extern int GdiFlush();
}
