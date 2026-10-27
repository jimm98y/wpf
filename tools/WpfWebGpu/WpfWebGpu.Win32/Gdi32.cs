// gdi32 (and user32's DC functions): device contexts and GDI objects.
//
// What apps use GDI for, around WPF and WinForms, is mostly asking questions: the screen's DPI and
// size through GetDeviceCaps on GetDC(NULL), a font's metrics, whether an object is a brush. Those are
// answered for real. Drawing with GDI onto a window is not possible here (the stack's windows are not
// GDI surfaces), so blits and fills onto a window DC are accepted and logged once; drawing into a DIB
// section the app owns has real memory behind it, so the app's own pixel code keeps working.

using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace WpfWebGpu.Win32;

public static unsafe partial class Exports
{
    private enum GdiKind { Pen = 1, Brush = 2, DC = 3, Palette = 5, Font = 6, Bitmap = 7, Region = 8, MemDC = 10 }

    private sealed class GdiObject
    {
        public GdiKind Kind;
        public uint Color;
        public bool BottomUp;
        public int Width, Height, BitsPerPixel = 32;
        public void* Bits;
        public int FontHeight = -12;
        public string FontFace = "Segoe UI";
        public RECT Region;
        public bool Stock;
        // What GDI+ draws into a 32-bit DIB section (Graphics.FromHdc of a DC it is selected into, a
        // blit into that DC). See DrawingSurface.
        public System.Drawing.Bitmap? Surface;
    }

    private sealed class DeviceContext
    {
        public nint Hwnd;
        public bool Memory;
        public nint Pen, Brush, Font, Bitmap, Region;
        public int BkMode = 2, Rop2 = 13, StretchMode = 1, GraphicsMode = 1, MapMode = 1, IcmMode = 1;
        public uint TextColor = 0, BkColor = 0xFFFFFF;
    }

    private static readonly ConcurrentDictionary<nint, GdiObject> s_gdi = new();
    private static readonly ConcurrentDictionary<nint, DeviceContext> s_dcs = new();
    private static long s_nextGdi = 0x7A0000;

    private static nint NewGdi(GdiObject o)
    {
        nint h = (nint)Interlocked.Add(ref s_nextGdi, 4);
        s_gdi[h] = o;
        return h;
    }

    private static nint NewDC(DeviceContext dc)
    {
        nint h = (nint)Interlocked.Add(ref s_nextGdi, 4);
        dc.Pen = StockObject(7);    // BLACK_PEN
        dc.Brush = StockObject(0);  // WHITE_BRUSH
        dc.Font = StockObject(13);  // SYSTEM_FONT
        dc.Bitmap = dc.Memory ? StockBitmap : 0;
        s_dcs[h] = dc;
        return h;
    }

    // Stock objects: fixed handles, never freed. Index -> handle 0x7F0000 + index.
    private static readonly nint StockBitmap = 0x7F00FF;

    private static nint StockObject(int index)
    {
        nint h = 0x7F0000 + index;
        s_gdi.GetOrAdd(h, _ => new GdiObject
        {
            Stock = true,
            Kind = index switch { <= 5 or 18 => GdiKind.Brush, 6 or 7 or 8 or 19 => GdiKind.Pen, 15 => GdiKind.Palette, _ => GdiKind.Font },
            Color = index switch { 0 => 0xFFFFFF, 1 => 0xC0C0C0, 2 => 0x808080, 3 => 0x404040, 6 => 0xFFFFFF, _ => 0 },
        });
        return h;
    }

    private static void RegisterGdi()
    {
        // What the stack's System.Drawing asks when an app does Graphics.FromHdc on a DC from here.
        AppContext.SetData("System.Drawing.DcSurfaceResolver", (Func<IntPtr, object?>)SurfaceOf);
    }

    /// <summary>
    /// The surface a DC stands for, to Graphics.FromHdc: a WinForms window's own Graphics for its DC
    /// (so what the app draws there is shown), a bitmap over a DIB section's memory for a memory DC it
    /// is selected into, and otherwise a scratch bitmap the size of the DC.
    /// </summary>
    /// <summary>
    /// The one bitmap GDI+ draws into for a 32-bit DIB section, made from its memory the first time.
    /// GDI+ draws straight into a DIB's memory; a Bitmap here copies the memory in and never writes
    /// it back, and a bottom-up DIB got a blank bitmap of its own each time. Whatever was drawn into a
    /// memory DC was gone by the time it was blitted out: DotNetBar draws each combo box into one
    /// and blits it to the window, and they showed nothing but their text. All drawing into the DIB
    /// now goes to this bitmap, and readers of the DIB take their pixels from it (SyncBits).
    /// </summary>
    private static System.Drawing.Bitmap DrawingSurface(GdiObject bmp)
    {
        if (bmp.Surface != null) return bmp.Surface;
        var surface = new System.Drawing.Bitmap(Math.Max(1, bmp.Width), Math.Max(1, bmp.Height), System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        var data = surface.LockBits(new System.Drawing.Rectangle(0, 0, surface.Width, surface.Height),
            System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        try
        {
            int row = bmp.Width * 4;
            for (int y = 0; y < bmp.Height; y++)
                Buffer.MemoryCopy((byte*)bmp.Bits + (long)(bmp.BottomUp ? bmp.Height - 1 - y : y) * row, (byte*)data.Scan0 + (long)y * data.Stride, data.Stride, row);
        }
        finally
        {
            surface.UnlockBits(data);
        }
        return bmp.Surface = surface;
    }

    /// <summary>Brings a DIB section's memory up to date with what was drawn into it, before
    /// anything reads that memory.</summary>
    private static void SyncBits(GdiObject bmp)
    {
        if (bmp.Surface == null || bmp.Bits == null) return;
        var data = bmp.Surface.LockBits(new System.Drawing.Rectangle(0, 0, bmp.Surface.Width, bmp.Surface.Height),
            System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        try
        {
            int row = bmp.Width * 4;
            for (int y = 0; y < bmp.Height; y++)
                Buffer.MemoryCopy((byte*)data.Scan0 + (long)y * data.Stride, (byte*)bmp.Bits + (long)(bmp.BottomUp ? bmp.Height - 1 - y : y) * row, row, row);
        }
        finally
        {
            bmp.Surface.UnlockBits(data);
        }
    }

    private static object? SurfaceOf(IntPtr hdc)
    {
        if (!s_dcs.TryGetValue(hdc, out var dc)) return null;
        if (dc.Hwnd != 0 && Resolve(dc.Hwnd) is System.Windows.Forms.Control control && control.IsHandleCreated)
        {
            return control.CreateGraphics();
        }
        if (dc.Memory && s_gdi.TryGetValue(dc.Bitmap, out var bmp) && !bmp.Stock && bmp.Bits != null)
        {
            if (bmp.BitsPerPixel == 32) return DrawingSurface(bmp);
            return new System.Drawing.Bitmap(Math.Max(1, bmp.Width), Math.Max(1, bmp.Height));
        }
        RECT r = dc.Hwnd != 0 ? WindowRect(dc.Hwnd, out _) : default;
        return new System.Drawing.Bitmap(Math.Max(1, r.Right - r.Left), Math.Max(1, r.Bottom - r.Top));
    }

    // ---- DCs ----------------------------------------------------------------------------------------

    [Win32Export("GetDC", "GetWindowDC")] public static nint GetDC(nint hwnd) => NewDC(new DeviceContext { Hwnd = hwnd });
    [Win32Export("GetDCEx")] public static nint GetDCEx(nint hwnd, nint clip, uint flags) => NewDC(new DeviceContext { Hwnd = hwnd });
    [Win32Export("ReleaseDC")] public static int ReleaseDC(nint hwnd, nint hdc) => s_dcs.TryRemove(hdc, out _) ? 1 : 0;

    /// <summary>Tells the WinForms driver a window of its was painted (BeginPaint) or validated
    /// (ValidateRect) by code that draws it through GDI.</summary>
    internal static void ValidateWinFormsWindow(nint hwnd, bool repaint)
    {
        if (Resolve(hwnd) is not System.Windows.Forms.Control c) return;
        Ui(c, () =>
        {
            System.Windows.Forms.Win32Bridge.ValidateWindow(hwnd, repaint);
            return 0;
        });
    }

    // PAINTSTRUCT: hdc, fErase, RECT rcPaint, fRestore, fIncUpdate, rgbReserved[32].
    [Win32Export("BeginPaint")]
    public static nint BeginPaint(nint hwnd, byte* ps)
    {
        nint hdc = NewDC(new DeviceContext { Hwnd = hwnd });
        ValidateWinFormsWindow(hwnd, repaint: true);
        NativeMemory.Clear(ps, 72);
        *(nint*)ps = hdc;
        RECT r = WindowRect(hwnd, out _);
        *(RECT*)(ps + 12) = new RECT { Right = r.Right - r.Left, Bottom = r.Bottom - r.Top };
        return hdc;
    }

    [Win32Export("EndPaint")] public static int EndPaint(nint hwnd, byte* ps) { s_dcs.TryRemove(*(nint*)ps, out _); return TRUE; }
    [Win32Export("CreateDCW", "CreateDC", "CreateICW", "CreateIC")] public static nint CreateDCW(char* driver, char* device, char* output, void* mode) => NewDC(new DeviceContext());
    // the names are not read: every DC here is the display's
    [Win32Export("CreateDCA", "CreateICA")] public static nint CreateDCA(byte* driver, byte* device, byte* output, void* mode) => NewDC(new DeviceContext());
    [Win32Export("CreateCompatibleDC")] public static nint CreateCompatibleDC(nint hdc) => NewDC(new DeviceContext { Memory = true });
    [Win32Export("DeleteDC")] public static int DeleteDC(nint hdc) => s_dcs.TryRemove(hdc, out _) ? TRUE : FALSE;

    [Win32Export("GetDeviceCaps")]
    public static int GetDeviceCaps(nint hdc, int index)
    {
        var main = Screens.All()[0];
        int w = main.Bounds.Right - main.Bounds.Left, h = main.Bounds.Bottom - main.Bounds.Top;
        if (s_dcs.TryGetValue(hdc, out var dc) && dc.Memory && s_gdi.TryGetValue(dc.Bitmap, out var bmp) && !bmp.Stock)
        {
            w = bmp.Width; h = bmp.Height;
        }
        return index switch
        {
            0 => 0x400,                        // DRIVERVERSION
            2 => 1,                            // TECHNOLOGY: DT_RASDISPLAY
            4 => (int)Math.Round(w * 25.4 / 96), // HORZSIZE (mm)
            6 => (int)Math.Round(h * 25.4 / 96), // VERTSIZE (mm)
            8 => w, 10 => h,                   // HORZRES, VERTRES
            12 => 32, 14 => 1,                 // BITSPIXEL, PLANES
            24 => -1,                          // NUMCOLORS: more than 256
            38 => 0x7E99,                      // RASTERCAPS: the usual display set
            40 => 36, 42 => 36, 44 => 51,      // ASPECTX, ASPECTY, ASPECTXY
            88 or 90 => 96,                    // LOGPIXELSX, LOGPIXELSY
            104 => 0, 108 => 32,               // SIZEPALETTE, COLORRES
            116 => 60,                         // VREFRESH
            117 => h, 118 => w,                // DESKTOPVERTRES, DESKTOPHORZRES
            119 => 0,                          // BLTALIGNMENT
            120 => 0xFF,                       // SHADEBLENDCAPS
            _ => 0,
        };
    }

    // ---- objects ------------------------------------------------------------------------------------

    [Win32Export("GetStockObject")] public static nint GetStockObject(int index) => index is >= 0 and <= 19 ? StockObject(index) : 0;

    /// <summary>
    /// An HBITMAP System.Drawing made (Bitmap.GetHbitmap): its pixels, adopted as a 32-bit top-down
    /// DIB under the same handle, so a DC it is selected into has them, and DeleteObject frees them.
    /// </summary>
    private static GdiObject? AdoptManagedBitmap(nint handle)
    {
        if (AppContext.GetData("System.Drawing.ManagedHbitmapImage") is not Func<IntPtr, System.Drawing.Image?> resolve) return null;
        if (resolve(handle) is not System.Drawing.Bitmap image) return null;
        using (image)
        {
            int w = image.Width, h = image.Height;
            var o = new GdiObject { Kind = GdiKind.Bitmap, Width = w, Height = h, BitsPerPixel = 32, Bits = NativeMemory.AllocZeroed((nuint)(w * h * 4)) };
            var data = image.LockBits(new System.Drawing.Rectangle(0, 0, w, h), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            try
            {
                for (int y = 0; y < h; y++) Buffer.MemoryCopy((byte*)data.Scan0 + (long)y * data.Stride, (byte*)o.Bits + (long)y * w * 4, w * 4, w * 4);
            }
            finally
            {
                image.UnlockBits(data);
            }
            s_gdi[handle] = o;
            return o;
        }
    }

    [Win32Export("SelectObject")]
    public static nint SelectObject(nint hdc, nint obj)
    {
        if (!s_dcs.TryGetValue(hdc, out var dc)) return 0;
        if (!s_gdi.TryGetValue(obj, out var o) && (o = AdoptManagedBitmap(obj)) == null) return 0;
        nint previous;
        switch (o.Kind)
        {
            case GdiKind.Pen: previous = dc.Pen; dc.Pen = obj; break;
            case GdiKind.Brush: previous = dc.Brush; dc.Brush = obj; break;
            case GdiKind.Font: previous = dc.Font; dc.Font = obj; break;
            case GdiKind.Bitmap: previous = dc.Bitmap; dc.Bitmap = obj; break;
            case GdiKind.Region: dc.Region = obj; return 2;   // SIMPLEREGION
            default: return 0;
        }
        return previous;
    }

    [Win32Export("DeleteObject")]
    public static int DeleteObject(nint obj)
    {
        if (!s_gdi.TryGetValue(obj, out var o)) return FALSE;
        if (o.Stock) return TRUE;
        s_gdi.TryRemove(obj, out _);
        if (o.Bits != null) NativeMemory.Free(o.Bits);
        o.Surface?.Dispose();
        return TRUE;
    }

    [Win32Export("GetObjectType")]
    public static uint GetObjectType(nint obj)
    {
        if (s_dcs.TryGetValue(obj, out var dc)) return dc.Memory ? (uint)GdiKind.MemDC : (uint)GdiKind.DC;
        return s_gdi.TryGetValue(obj, out var o) ? (uint)o.Kind : 0;
    }

    [Win32Export("GetObjectW", "GetObject")]
    public static int GetObjectW(nint obj, int size, void* buffer) => GetObjectCore(obj, size, buffer, wide: true);

    /// <summary>The ANSI one: a font is a LOGFONTA (60 bytes); everything else is the same.</summary>
    [Win32Export("GetObjectA")]
    public static int GetObjectA(nint obj, int size, void* buffer) => GetObjectCore(obj, size, buffer, wide: false);

    private static int GetObjectCore(nint obj, int size, void* buffer, bool wide)
    {
        if (!s_gdi.TryGetValue(obj, out var o)) return 0;
        switch (o.Kind)
        {
            case GdiKind.Font:
                int lf = wide ? 92 : 60;
                if (buffer == null) return lf;
                WriteLogFont((byte*)buffer, o.FontHeight, o.FontFace, wide);
                return lf;
            case GdiKind.Bitmap:
                // BITMAP: bmType, bmWidth, bmHeight, bmWidthBytes, bmPlanes(2), bmBitsPixel(2), bmBits.
                if (buffer == null) return 32;
                int* b = (int*)buffer;
                b[0] = 0; b[1] = o.Width; b[2] = o.Height; b[3] = o.Width * o.BitsPerPixel / 8;
                ((ushort*)buffer)[8] = 1; ((ushort*)buffer)[9] = (ushort)o.BitsPerPixel;
                *(void**)((byte*)buffer + 24) = o.Bits;
                return 32;
            case GdiKind.Brush:
                // LOGBRUSH: lbStyle, lbColor, lbHatch.
                if (buffer == null) return 16;
                ((uint*)buffer)[0] = 0; ((uint*)buffer)[1] = o.Color; *(nint*)((byte*)buffer + 8) = 0;
                return 16;
            default:
                return 0;
        }
    }

    [Win32Export("GetCurrentObject")]
    public static nint GetCurrentObject(nint hdc, uint type)
    {
        if (!s_dcs.TryGetValue(hdc, out var dc)) return 0;
        return type switch { 1 => dc.Pen, 2 => dc.Brush, 6 => dc.Font, 7 => dc.Bitmap, _ => 0 };
    }

    [Win32Export("CreateSolidBrush")] public static nint CreateSolidBrush(uint color) => NewGdi(new GdiObject { Kind = GdiKind.Brush, Color = color });
    [Win32Export("CreatePen")] public static nint CreatePen(int style, int width, uint color) => NewGdi(new GdiObject { Kind = GdiKind.Pen, Color = color, Width = width });

    [Win32Export("CreateFontIndirectW", "CreateFontIndirect")]
    public static nint CreateFontIndirectW(byte* lf) => NewGdi(new GdiObject
    {
        Kind = GdiKind.Font,
        FontHeight = *(int*)lf == 0 ? -12 : *(int*)lf,
        FontFace = ReadUtf16((char*)(lf + 28)) is { Length: > 0 } face ? face : "Segoe UI",
    });

    private static nint NewBitmap(int width, int height, int bpp, bool zero)
    {
        nuint bytes = (nuint)Math.Max(1, ((width * bpp + 31) / 32) * 4 * Math.Abs(height));
        return NewGdi(new GdiObject
        {
            Kind = GdiKind.Bitmap, Width = width, Height = Math.Abs(height), BitsPerPixel = bpp,
            Bits = zero ? NativeMemory.AllocZeroed(bytes) : NativeMemory.Alloc(bytes),
        });
    }

    [Win32Export("CreateCompatibleBitmap", "CreateDiscardableBitmap")] public static nint CreateCompatibleBitmap(nint hdc, int w, int h) => NewBitmap(w, h, 32, true);

    [Win32Export("CreateBitmap")]
    public static nint CreateBitmap(int w, int h, uint planes, uint bpp, void* bits)
    {
        nint handle = NewBitmap(w, h, (int)Math.Max(1, bpp), bits == null);
        if (bits != null && s_gdi.TryGetValue(handle, out var o))
        {
            nuint bytes = (nuint)(((w * (int)bpp + 15) / 16) * 2 * h);
            Buffer.MemoryCopy(bits, o.Bits, bytes, Math.Min(bytes, (nuint)(((w * (int)bpp + 31) / 32) * 4 * h)));
        }
        return handle;
    }

    // BITMAPINFOHEADER: biSize, biWidth, biHeight, biPlanes(2), biBitCount(2), ...
    [Win32Export("CreateDIBSection")]
    public static nint CreateDIBSection(nint hdc, byte* info, uint usage, void** bits, nint section, uint offset)
    {
        int width = *(int*)(info + 4), height = *(int*)(info + 8);
        int bpp = *(ushort*)(info + 14);
        nint handle = NewBitmap(width, height, bpp == 0 ? 32 : bpp, true);
        s_gdi[handle].BottomUp = height > 0;   // a positive height is a bottom-up DIB
        if (bits != null) *bits = s_gdi[handle].Bits;
        return handle;
    }

    [Win32Export("CreateRectRgn", "CreateEllipticRgn")] public static nint CreateRectRgn(int l, int t, int r, int b) => NewGdi(new GdiObject { Kind = GdiKind.Region, Region = new RECT { Left = l, Top = t, Right = r, Bottom = b } });
    [Win32Export("CreateRectRgnIndirect", "CreateEllipticRgnIndirect")] public static nint CreateRectRgnIndirect(RECT* r) => NewGdi(new GdiObject { Kind = GdiKind.Region, Region = *r });
    [Win32Export("CreateRoundRectRgn")] public static nint CreateRoundRectRgn(int l, int t, int r, int b, int w, int h) => NewGdi(new GdiObject { Kind = GdiKind.Region, Region = new RECT { Left = l, Top = t, Right = r, Bottom = b } });

    [Win32Export("CombineRgn")]
    public static int CombineRgn(nint dest, nint a, nint b, int mode)
    {
        // The bounding boxes, combined; regions here are rectangles.
        if (!s_gdi.TryGetValue(dest, out var d) || !s_gdi.TryGetValue(a, out var ra)) return 0;   // ERROR
        RECT r = ra.Region;
        if (mode != 5 && s_gdi.TryGetValue(b, out var rb))   // RGN_COPY = 5
        {
            RECT o = rb.Region;
            r = mode == 1   // RGN_AND
                ? new RECT { Left = Math.Max(r.Left, o.Left), Top = Math.Max(r.Top, o.Top), Right = Math.Min(r.Right, o.Right), Bottom = Math.Min(r.Bottom, o.Bottom) }
                : new RECT { Left = Math.Min(r.Left, o.Left), Top = Math.Min(r.Top, o.Top), Right = Math.Max(r.Right, o.Right), Bottom = Math.Max(r.Bottom, o.Bottom) };
        }
        d.Region = r;
        return r.Right <= r.Left || r.Bottom <= r.Top ? 1 : 2;   // NULLREGION : SIMPLEREGION
    }

    [Win32Export("GetRgnBox")]
    public static int GetRgnBox(nint rgn, RECT* r)
    {
        if (!s_gdi.TryGetValue(rgn, out var o)) return 0;
        *r = o.Region;
        return 2;
    }

    // TEXTMETRICW: tmHeight, tmAscent, tmDescent, tmInternalLeading, tmExternalLeading, tmAveCharWidth,
    // tmMaxCharWidth, tmWeight, tmOverhang, tmDigitizedAspectX, tmDigitizedAspectY, 4 WCHARs, 5 BYTEs.
    [Win32Export("GetTextMetricsW", "GetTextMetrics")]
    public static int GetTextMetricsW(nint hdc, byte* tm)
    {
        int em = 12;
        if (s_dcs.TryGetValue(hdc, out var dc) && s_gdi.TryGetValue(dc.Font, out var f) && f.Kind == GdiKind.Font) em = Math.Abs(f.FontHeight);
        NativeMemory.Clear(tm, 60);
        int* m = (int*)tm;
        int ascent = (int)Math.Round(em * 1.07), descent = (int)Math.Round(em * 0.26);
        m[0] = ascent + descent; m[1] = ascent; m[2] = descent; m[3] = ascent + descent - em; m[4] = 0;
        m[5] = (int)Math.Round(em * 0.45); m[6] = em * 2; m[7] = 400; m[8] = 0; m[9] = 96; m[10] = 96;
        char* c = (char*)(tm + 44);
        c[0] = ' '; c[1] = '￼'; c[2] = '?'; c[3] = ' ';
        tm[56] = 0; tm[57] = 0; tm[58] = 0; tm[59] = 0x26;   // tmPitchAndFamily: variable pitch, TrueType, swiss
        return TRUE;
    }

    private static int Swap(nint hdc, Func<DeviceContext, int> get, Action<DeviceContext> set)
    {
        if (!s_dcs.TryGetValue(hdc, out var dc)) return 0;
        int old = get(dc);
        set(dc);
        return old;
    }

    [Win32Export("SetBkMode")] public static int SetBkMode(nint hdc, int mode) => Swap(hdc, d => d.BkMode, d => d.BkMode = mode);
    [Win32Export("SetTextColor")] public static uint SetTextColor(nint hdc, uint c) => (uint)Swap(hdc, d => (int)d.TextColor, d => d.TextColor = c);
    [Win32Export("SetBkColor")] public static uint SetBkColor(nint hdc, uint c) => (uint)Swap(hdc, d => (int)d.BkColor, d => d.BkColor = c);
    [Win32Export("SetROP2")] public static int SetROP2(nint hdc, int v) => Swap(hdc, d => d.Rop2, d => d.Rop2 = v);
    [Win32Export("SetStretchBltMode")] public static int SetStretchBltMode(nint hdc, int v) => Swap(hdc, d => d.StretchMode, d => d.StretchMode = v);
    [Win32Export("SetGraphicsMode")] public static int SetGraphicsMode(nint hdc, int v) => Swap(hdc, d => d.GraphicsMode, d => d.GraphicsMode = v);
    [Win32Export("SetMapMode")] public static int SetMapMode(nint hdc, int v) => Swap(hdc, d => d.MapMode, d => d.MapMode = v);
    [Win32Export("SetICMMode")] public static int SetICMMode(nint hdc, int v) => Swap(hdc, d => d.IcmMode, d => d.IcmMode = v);
    [Win32Export("SetBrushOrgEx", "SetViewportOrgEx", "SetWindowOrgEx", "OffsetViewportOrgEx", "OffsetWindowOrgEx")] public static int SetBrushOrgEx(nint hdc, int x, int y, POINT* old) { if (old != null) *old = default; return TRUE; }
    [Win32Export("SelectClipRgn")] public static int SelectClipRgn(nint hdc, nint rgn) => 2;
    [Win32Export("ExcludeClipRect", "IntersectClipRect")] public static int ExcludeClipRect(nint hdc, int l, int t, int r, int b) => 2;

    /// <summary>
    /// The source pixels of a blit: the bitmap selected into a memory DC, as a System.Drawing image
    /// (copied, top-down). Null when the DC has none, which a blit from a window DC is: what a window
    /// shows is the stack's to compose, not a buffer that can be read back here.
    /// </summary>
    /// <summary>The screen's area under a screen or window DC (GetDC(NULL), GetDC(hwnd)) as an image:
    /// what the platform shows there (ScreenPixels). Null for any other DC, or where it cannot be read.</summary>
    private static System.Drawing.Bitmap? ScreenImage(DeviceContext dc, int sx, int sy, int sw, int sh)
    {
        if (dc.Memory || sw <= 0 || sh <= 0) return null;
        POINT at = ScreenPointOf(dc, sx, sy);
        byte[]? bgra = MS.Internal.Interop.ScreenPixels.Capture(at.X, at.Y, sw, sh);
        if (bgra == null) return null;
        var image = new System.Drawing.Bitmap(sw, sh, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
        var data = image.LockBits(new System.Drawing.Rectangle(0, 0, sw, sh), System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
        try
        {
            for (int y = 0; y < sh; y++)
                Marshal.Copy(bgra, y * sw * 4, data.Scan0 + y * data.Stride, sw * 4);
        }
        finally
        {
            image.UnlockBits(data);
        }
        return image;
    }

    // a screen DC's point as it is; a window DC's from the window's client origin
    private static POINT ScreenPointOf(DeviceContext dc, int x, int y)
    {
        var p = new POINT { X = x, Y = y };
        if (dc.Hwnd != 0) ClientToScreen(dc.Hwnd, &p);
        return p;
    }

    private static System.Drawing.Bitmap? SourceImage(nint src)
    {
        if (!s_dcs.TryGetValue(src, out var dc) || !dc.Memory) return null;
        if (!s_gdi.TryGetValue(dc.Bitmap, out var bmp) || bmp.Stock || bmp.Bits == null || bmp.BitsPerPixel != 32) return null;
        SyncBits(bmp);
        // RGB, not ARGB: BitBlt copies colour and ignores alpha, and the DIBs GDI code fills usually leave
        // alpha at 0, which drawn as ARGB would be invisible.
        var image = new System.Drawing.Bitmap(bmp.Width, bmp.Height, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
        var data = image.LockBits(new System.Drawing.Rectangle(0, 0, bmp.Width, bmp.Height),
            System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
        try
        {
            int row = bmp.Width * 4;
            for (int y = 0; y < bmp.Height; y++)
            {
                byte* from = (byte*)bmp.Bits + (long)(bmp.BottomUp ? bmp.Height - 1 - y : y) * row;
                Buffer.MemoryCopy(from, (byte*)data.Scan0 + (long)y * data.Stride, data.Stride, row);
            }
        }
        finally
        {
            image.UnlockBits(data);
        }
        return image;
    }

    /// <summary>
    /// What the destination of a blit draws with: the window's own Graphics for a window DC, a
    /// Graphics over the DIB for a memory DC, or (via Graphics.FromHdc) whatever DC System.Drawing
    /// handed out itself.
    /// </summary>
    private static System.Drawing.Graphics? DestinationGraphics(nint dst)
    {
        try
        {
            return SurfaceOf(dst) switch
            {
                System.Drawing.Graphics g => g,
                System.Drawing.Image image => System.Drawing.Graphics.FromImage(image),
                _ => System.Drawing.Graphics.FromHdc(dst),
            };
        }
        catch
        {
            return null;
        }
    }

    private static int Blit(nint dst, int x, int y, int w, int h, nint src, int sx, int sy, int sw, int sh, uint rop)
    {
        // Not disposed here: a recording destination draws it later, when it is composited.
        System.Drawing.Bitmap? image = SourceImage(src);
        // From the screen or a window (a magnifier, an eye dropper's loupe): what is shown there.
        if (image == null && s_dcs.TryGetValue(src, out var from) && !from.Memory
            && ScreenImage(from, sx, sy, sw, sh) is { } shown)
        {
            image = shown;
            sx = 0; sy = 0;
        }
        if (image == null)
        {
            LogOnce("gdi:blit", "win32: a blit whose source is not a memory DC with a 32-bit DIB is accepted but not drawn");
            return TRUE;
        }
        System.Drawing.Graphics? g = DestinationGraphics(dst);
        if (g == null) return TRUE;
        try
        {
            // SRCCOPY and its relatives copy; the rasterops that combine with the destination are
            // drawn as a copy too, the nearest GDI+ has.
            g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
            g.DrawImage(image, new System.Drawing.Rectangle(x, y, w, h), new System.Drawing.Rectangle(sx, sy, sw, sh), System.Drawing.GraphicsUnit.Pixel);
            // A memory DC's DIB is written back to its memory; see SurfaceOf (the bitmap wraps it).
        }
        finally
        {
            g.Dispose();
        }
        return TRUE;
    }

    [Win32Export("BitBlt")] public static int BitBlt(nint dst, int x, int y, int w, int h, nint src, int sx, int sy, uint rop) => Blit(dst, x, y, w, h, src, sx, sy, w, h, rop);
    [Win32Export("StretchBlt")] public static int StretchBlt(nint dst, int x, int y, int w, int h, nint src, int sx, int sy, int sw, int sh, uint rop) => Blit(dst, x, y, w, h, src, sx, sy, sw, sh, rop);
    // GetPixel: from a memory DC, its bitmap's pixel; from the screen or a window, what is shown there
    // (an eye dropper: app's colour picker reads GetPixel(GetDC(NULL), x, y) under the pointer).
    // CLR_INVALID outside the bitmap, or where the screen cannot be read.
    [Win32Export("GetPixel")]
    public static uint GetPixel(nint hdc, int x, int y)
    {
        const uint CLR_INVALID = 0xFFFFFFFF;
        if (!s_dcs.TryGetValue(hdc, out var dc)) return CLR_INVALID;
        if (!dc.Memory)
        {
            POINT at = ScreenPointOf(dc, x, y);
            return MS.Internal.Interop.ScreenPixels.GetPixel(at.X, at.Y);
        }
        if (!s_gdi.TryGetValue(dc.Bitmap, out var bmp) || bmp.Stock || bmp.Bits == null || bmp.BitsPerPixel != 32
            || x < 0 || y < 0 || x >= bmp.Width || y >= bmp.Height) return CLR_INVALID;
        SyncBits(bmp);
        byte* px = (byte*)bmp.Bits + ((long)(bmp.BottomUp ? bmp.Height - 1 - y : y) * bmp.Width + x) * 4;
        return (uint)(px[2] | px[1] << 8 | px[0] << 16);
    }

    [Win32Export("FillRect", "FrameRect")]
    public static int FillRect(nint hdc, RECT* r, nint brush)
    {
        LogOnce("gdi:fill", "win32: GDI drawing onto a window (FillRect and friends) is accepted but not shown");
        return TRUE;
    }

    // A layered window's whole content is the bitmap it is updated with: a splash screen with soft
    // edges, a drop shadow that is a window of its own. The WinForms driver composites that bitmap
    // as the window, with its per-pixel alpha (ULW_ALPHA, premultiplied as Windows takes it) or a
    // colour key (ULW_COLORKEY), and the blend's constant alpha on top; what the window paints is not
    // shown. Position and size come with the update, as on Windows.
    // pptDst, psize, pptSrc: POINT*, SIZE*, POINT*; pblend: BLENDFUNCTION*.
    [Win32Export("UpdateLayeredWindow")]
    public static int UpdateLayeredWindow(nint hwnd, nint hdcDst, POINT* pptDst, int* psize, nint hdcSrc, POINT* pptSrc, uint key, void* blend, uint flags)
    {
        const uint ULW_COLORKEY = 1, ULW_ALPHA = 2;
        object? o = Resolve(hwnd);
        if (o is not System.Windows.Forms.Control control) { LogOnce("ulw", "win32: UpdateLayeredWindow on a window that is not a WinForms window is accepted but not shown"); return TRUE; }
        if (pptDst != null || psize != null)
        {
            Place(o, pptDst != null ? pptDst->X : null, pptDst != null ? pptDst->Y : null,
                  psize != null ? psize[0] : null, psize != null ? psize[1] : null);
        }
        float opacity = (flags & ULW_ALPHA) != 0 && blend != null ? ((byte*)blend)[2] / 255f : 1f;   // SourceConstantAlpha
        bool perPixel = (flags & ULW_ALPHA) != 0 && blend != null && ((byte*)blend)[3] != 0;           // AC_SRC_ALPHA
        if (hdcSrc == 0 || !s_dcs.TryGetValue(hdcSrc, out var dc) || !s_gdi.TryGetValue(dc.Bitmap, out var bmp) || bmp.Stock || bmp.Bits == null || bmp.BitsPerPixel != 32)
        {
            if (hdcSrc != 0) LogOnce("ulw-nobits", "win32: UpdateLayeredWindow from a DC with no 32-bit bitmap selected: only the position, size and alpha change");
            return TRUE;   // a move, resize or new alpha only; the last picture stays
        }
        SyncBits(bmp);

        int w = psize != null ? psize[0] : bmp.Width, h = psize != null ? psize[1] : bmp.Height;
        int sx = pptSrc != null ? pptSrc->X : 0, sy = pptSrc != null ? pptSrc->Y : 0;
        if (w <= 0 || h <= 0) return TRUE;
        var rgba = new byte[w * h * 4];
        for (int y = 0; y < h && sy + y < bmp.Height; y++)
        {
            int srcRow = bmp.BottomUp ? bmp.Height - 1 - (sy + y) : sy + y;
            byte* src = (byte*)bmp.Bits + (long)srcRow * bmp.Width * 4 + sx * 4;
            for (int x = 0; x < w && sx + x < bmp.Width; x++, src += 4)
            {
                byte b = src[0], g = src[1], r = src[2], a = perPixel ? src[3] : (byte)255;
                if ((flags & ULW_COLORKEY) != 0 && (uint)(r | g << 8 | b << 16) == (key & 0xFFFFFF)) a = 0;
                else if (perPixel && a != 0 && a != 255)
                {
                    // Premultiplied in, straight out.
                    r = (byte)Math.Min(255, r * 255 / a); g = (byte)Math.Min(255, g * 255 / a); b = (byte)Math.Min(255, b * 255 / a);
                }
                int i = (y * w + x) * 4;
                rgba[i] = r; rgba[i + 1] = g; rgba[i + 2] = b; rgba[i + 3] = a;
            }
        }
        Ui(o, () =>
        {
            if (!control.IsHandleCreated) return 0;
            System.Windows.Forms.Win32Bridge.SetLayeredImage(control.Handle, rgba, w, h, opacity);
            return 0;
        });
        return TRUE;
    }

    // Fonts an app carries in its resources and registers for itself. Accepted; the stack's own font
    // catalogue is what text is rendered with.
    [Win32Export("AddFontMemResourceEx")]
    public static nint AddFontMemResourceEx(void* data, uint length, void* reserved, uint* count)
    {
        if (count != null) *count = 1;
        return NewHandle(new object());
    }

    [Win32Export("RemoveFontMemResourceEx")] public static int RemoveFontMemResourceEx(nint h) { s_handles.TryRemove(h, out _); return TRUE; }
}
