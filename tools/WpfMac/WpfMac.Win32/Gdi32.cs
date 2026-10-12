// gdi32 (and user32's DC functions): device contexts and GDI objects.
//
// What apps use GDI for, around WPF and WinForms, is mostly asking questions: the screen's DPI and
// size through GetDeviceCaps on GetDC(NULL), a font's metrics, whether an object is a brush. Those are
// answered for real. Drawing with GDI onto a window is not possible here (the stack's windows are not
// GDI surfaces), so blits and fills onto a window DC are accepted and logged once; drawing into a DIB
// section the app owns has real memory behind it, so the app's own pixel code keeps working.

using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace WpfMac.Win32;

public static unsafe partial class Exports
{
    private enum GdiKind { Pen = 1, Brush = 2, DC = 3, Palette = 5, Font = 6, Bitmap = 7, Region = 8, MemDC = 10 }

    private sealed class GdiObject
    {
        public GdiKind Kind;
        public uint Color;
        public int Width, Height, BitsPerPixel = 32;
        public void* Bits;
        public int FontHeight = -12;
        public string FontFace = "Segoe UI";
        public RECT Region;
        public bool Stock;
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
        R((delegate* unmanaged<nint, nint>)&GetDC, "GetDC", "GetWindowDC");
        R((delegate* unmanaged<nint, nint, uint, nint>)&GetDCEx, "GetDCEx");
        R((delegate* unmanaged<nint, nint, int>)&ReleaseDC, "ReleaseDC");
        R((delegate* unmanaged<nint, byte*, nint>)&BeginPaint, "BeginPaint");
        R((delegate* unmanaged<nint, byte*, int>)&EndPaint, "EndPaint");
        R((delegate* unmanaged<char*, char*, char*, void*, nint>)&CreateDCW, "CreateDCW", "CreateDC", "CreateICW", "CreateIC");
        R((delegate* unmanaged<nint, nint>)&CreateCompatibleDC, "CreateCompatibleDC");
        R((delegate* unmanaged<nint, int>)&DeleteDC, "DeleteDC");
        R((delegate* unmanaged<nint, int, int>)&GetDeviceCaps, "GetDeviceCaps");
        R((delegate* unmanaged<int, nint>)&GetStockObject, "GetStockObject");
        R((delegate* unmanaged<nint, nint, nint>)&SelectObject, "SelectObject");
        R((delegate* unmanaged<nint, int>)&DeleteObject, "DeleteObject");
        R((delegate* unmanaged<nint, uint>)&GetObjectType, "GetObjectType");
        R((delegate* unmanaged<nint, int, void*, int>)&GetObjectW, "GetObjectW", "GetObject", "GetObjectA");
        R((delegate* unmanaged<nint, uint, nint>)&GetCurrentObject, "GetCurrentObject");
        R((delegate* unmanaged<uint, nint>)&CreateSolidBrush, "CreateSolidBrush");
        R((delegate* unmanaged<int, int, uint, nint>)&CreatePen, "CreatePen");
        R((delegate* unmanaged<byte*, nint>)&CreateFontIndirectW, "CreateFontIndirectW", "CreateFontIndirect");
        R((delegate* unmanaged<nint, int, int, nint>)&CreateCompatibleBitmap, "CreateCompatibleBitmap", "CreateDiscardableBitmap");
        R((delegate* unmanaged<int, int, uint, uint, void*, nint>)&CreateBitmap, "CreateBitmap");
        R((delegate* unmanaged<nint, byte*, uint, void**, nint, uint, nint>)&CreateDIBSection, "CreateDIBSection");
        R((delegate* unmanaged<int, int, int, int, nint>)&CreateRectRgn, "CreateRectRgn", "CreateEllipticRgn");
        R((delegate* unmanaged<RECT*, nint>)&CreateRectRgnIndirect, "CreateRectRgnIndirect", "CreateEllipticRgnIndirect");
        R((delegate* unmanaged<int, int, int, int, int, int, nint>)&CreateRoundRectRgn, "CreateRoundRectRgn");
        R((delegate* unmanaged<nint, nint, nint, int, int>)&CombineRgn, "CombineRgn");
        R((delegate* unmanaged<nint, RECT*, int>)&GetRgnBox, "GetRgnBox");
        R((delegate* unmanaged<nint, byte*, int>)&GetTextMetricsW, "GetTextMetricsW", "GetTextMetrics");
        R((delegate* unmanaged<nint, int, int>)&SetBkMode, "SetBkMode");
        R((delegate* unmanaged<nint, uint, uint>)&SetTextColor, "SetTextColor");
        R((delegate* unmanaged<nint, uint, uint>)&SetBkColor, "SetBkColor");
        R((delegate* unmanaged<nint, int, int>)&SetROP2, "SetROP2");
        R((delegate* unmanaged<nint, int, int>)&SetStretchBltMode, "SetStretchBltMode");
        R((delegate* unmanaged<nint, int, int>)&SetGraphicsMode, "SetGraphicsMode");
        R((delegate* unmanaged<nint, int, int>)&SetMapMode, "SetMapMode");
        R((delegate* unmanaged<nint, int, int>)&SetICMMode, "SetICMMode");
        R((delegate* unmanaged<nint, int, int, POINT*, int>)&SetBrushOrgEx, "SetBrushOrgEx", "SetViewportOrgEx", "SetWindowOrgEx", "OffsetViewportOrgEx", "OffsetWindowOrgEx");
        R((delegate* unmanaged<nint, nint, int>)&SelectClipRgn, "SelectClipRgn");
        R((delegate* unmanaged<nint, int, int, int, int, int>)&ExcludeClipRect, "ExcludeClipRect", "IntersectClipRect");
        R((delegate* unmanaged<nint, int, int, int, int, nint, int, int, uint, int>)&BitBlt, "BitBlt");
        R((delegate* unmanaged<nint, int, int, int, int, nint, int, int, int, int, uint, int>)&StretchBlt, "StretchBlt");
        R((delegate* unmanaged<nint, int, int, uint>)&GetPixel, "GetPixel");
        R((delegate* unmanaged<void*, uint, void*, uint*, nint>)&AddFontMemResourceEx, "AddFontMemResourceEx");
        R((delegate* unmanaged<nint, int>)&RemoveFontMemResourceEx, "RemoveFontMemResourceEx");
        R((delegate* unmanaged<nint, RECT*, nint, int>)&FillRect, "FillRect", "FrameRect");
    }

    // ---- DCs ----------------------------------------------------------------------------------------

    [UnmanagedCallersOnly] private static nint GetDC(nint hwnd) => NewDC(new DeviceContext { Hwnd = hwnd });
    [UnmanagedCallersOnly] private static nint GetDCEx(nint hwnd, nint clip, uint flags) => NewDC(new DeviceContext { Hwnd = hwnd });
    [UnmanagedCallersOnly] private static int ReleaseDC(nint hwnd, nint hdc) => s_dcs.TryRemove(hdc, out _) ? 1 : 0;

    // PAINTSTRUCT: hdc, fErase, RECT rcPaint, fRestore, fIncUpdate, rgbReserved[32].
    [UnmanagedCallersOnly]
    private static nint BeginPaint(nint hwnd, byte* ps)
    {
        nint hdc = NewDC(new DeviceContext { Hwnd = hwnd });
        NativeMemory.Clear(ps, 72);
        *(nint*)ps = hdc;
        RECT r = WindowRect(hwnd, out _);
        *(RECT*)(ps + 12) = new RECT { Right = r.Right - r.Left, Bottom = r.Bottom - r.Top };
        return hdc;
    }

    [UnmanagedCallersOnly] private static int EndPaint(nint hwnd, byte* ps) { s_dcs.TryRemove(*(nint*)ps, out _); return TRUE; }
    [UnmanagedCallersOnly] private static nint CreateDCW(char* driver, char* device, char* output, void* mode) => NewDC(new DeviceContext());
    [UnmanagedCallersOnly] private static nint CreateCompatibleDC(nint hdc) => NewDC(new DeviceContext { Memory = true });
    [UnmanagedCallersOnly] private static int DeleteDC(nint hdc) => s_dcs.TryRemove(hdc, out _) ? TRUE : FALSE;

    [UnmanagedCallersOnly]
    private static int GetDeviceCaps(nint hdc, int index)
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

    [UnmanagedCallersOnly] private static nint GetStockObject(int index) => index is >= 0 and <= 19 ? StockObject(index) : 0;

    [UnmanagedCallersOnly]
    private static nint SelectObject(nint hdc, nint obj)
    {
        if (!s_dcs.TryGetValue(hdc, out var dc) || !s_gdi.TryGetValue(obj, out var o)) return 0;
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

    [UnmanagedCallersOnly]
    private static int DeleteObject(nint obj)
    {
        if (!s_gdi.TryGetValue(obj, out var o)) return FALSE;
        if (o.Stock) return TRUE;
        s_gdi.TryRemove(obj, out _);
        if (o.Bits != null) NativeMemory.Free(o.Bits);
        return TRUE;
    }

    [UnmanagedCallersOnly]
    private static uint GetObjectType(nint obj)
    {
        if (s_dcs.TryGetValue(obj, out var dc)) return dc.Memory ? (uint)GdiKind.MemDC : (uint)GdiKind.DC;
        return s_gdi.TryGetValue(obj, out var o) ? (uint)o.Kind : 0;
    }

    [UnmanagedCallersOnly]
    private static int GetObjectW(nint obj, int size, void* buffer)
    {
        if (!s_gdi.TryGetValue(obj, out var o)) return 0;
        switch (o.Kind)
        {
            case GdiKind.Font:
                if (buffer == null) return 92;
                WriteLogFont((byte*)buffer, o.FontHeight, o.FontFace);
                return 92;
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

    [UnmanagedCallersOnly]
    private static nint GetCurrentObject(nint hdc, uint type)
    {
        if (!s_dcs.TryGetValue(hdc, out var dc)) return 0;
        return type switch { 1 => dc.Pen, 2 => dc.Brush, 6 => dc.Font, 7 => dc.Bitmap, _ => 0 };
    }

    [UnmanagedCallersOnly] private static nint CreateSolidBrush(uint color) => NewGdi(new GdiObject { Kind = GdiKind.Brush, Color = color });
    [UnmanagedCallersOnly] private static nint CreatePen(int style, int width, uint color) => NewGdi(new GdiObject { Kind = GdiKind.Pen, Color = color, Width = width });

    [UnmanagedCallersOnly]
    private static nint CreateFontIndirectW(byte* lf) => NewGdi(new GdiObject
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

    [UnmanagedCallersOnly] private static nint CreateCompatibleBitmap(nint hdc, int w, int h) => NewBitmap(w, h, 32, true);

    [UnmanagedCallersOnly]
    private static nint CreateBitmap(int w, int h, uint planes, uint bpp, void* bits)
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
    [UnmanagedCallersOnly]
    private static nint CreateDIBSection(nint hdc, byte* info, uint usage, void** bits, nint section, uint offset)
    {
        int width = *(int*)(info + 4), height = *(int*)(info + 8);
        int bpp = *(ushort*)(info + 14);
        nint handle = NewBitmap(width, height, bpp == 0 ? 32 : bpp, true);
        if (bits != null) *bits = s_gdi[handle].Bits;
        return handle;
    }

    [UnmanagedCallersOnly] private static nint CreateRectRgn(int l, int t, int r, int b) => NewGdi(new GdiObject { Kind = GdiKind.Region, Region = new RECT { Left = l, Top = t, Right = r, Bottom = b } });
    [UnmanagedCallersOnly] private static nint CreateRectRgnIndirect(RECT* r) => NewGdi(new GdiObject { Kind = GdiKind.Region, Region = *r });
    [UnmanagedCallersOnly] private static nint CreateRoundRectRgn(int l, int t, int r, int b, int w, int h) => NewGdi(new GdiObject { Kind = GdiKind.Region, Region = new RECT { Left = l, Top = t, Right = r, Bottom = b } });

    [UnmanagedCallersOnly]
    private static int CombineRgn(nint dest, nint a, nint b, int mode)
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

    [UnmanagedCallersOnly]
    private static int GetRgnBox(nint rgn, RECT* r)
    {
        if (!s_gdi.TryGetValue(rgn, out var o)) return 0;
        *r = o.Region;
        return 2;
    }

    // TEXTMETRICW: tmHeight, tmAscent, tmDescent, tmInternalLeading, tmExternalLeading, tmAveCharWidth,
    // tmMaxCharWidth, tmWeight, tmOverhang, tmDigitizedAspectX, tmDigitizedAspectY, 4 WCHARs, 5 BYTEs.
    [UnmanagedCallersOnly]
    private static int GetTextMetricsW(nint hdc, byte* tm)
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

    [UnmanagedCallersOnly] private static int SetBkMode(nint hdc, int mode) => Swap(hdc, d => d.BkMode, d => d.BkMode = mode);
    [UnmanagedCallersOnly] private static uint SetTextColor(nint hdc, uint c) => (uint)Swap(hdc, d => (int)d.TextColor, d => d.TextColor = c);
    [UnmanagedCallersOnly] private static uint SetBkColor(nint hdc, uint c) => (uint)Swap(hdc, d => (int)d.BkColor, d => d.BkColor = c);
    [UnmanagedCallersOnly] private static int SetROP2(nint hdc, int v) => Swap(hdc, d => d.Rop2, d => d.Rop2 = v);
    [UnmanagedCallersOnly] private static int SetStretchBltMode(nint hdc, int v) => Swap(hdc, d => d.StretchMode, d => d.StretchMode = v);
    [UnmanagedCallersOnly] private static int SetGraphicsMode(nint hdc, int v) => Swap(hdc, d => d.GraphicsMode, d => d.GraphicsMode = v);
    [UnmanagedCallersOnly] private static int SetMapMode(nint hdc, int v) => Swap(hdc, d => d.MapMode, d => d.MapMode = v);
    [UnmanagedCallersOnly] private static int SetICMMode(nint hdc, int v) => Swap(hdc, d => d.IcmMode, d => d.IcmMode = v);
    [UnmanagedCallersOnly] private static int SetBrushOrgEx(nint hdc, int x, int y, POINT* old) { if (old != null) *old = default; return TRUE; }
    [UnmanagedCallersOnly] private static int SelectClipRgn(nint hdc, nint rgn) => 2;
    [UnmanagedCallersOnly] private static int ExcludeClipRect(nint hdc, int l, int t, int r, int b) => 2;

    private static int Blit(nint dst, nint src)
    {
        // Memory DC to memory DC copies the pixels; anything involving a window is not drawable here.
        if (s_dcs.TryGetValue(dst, out var d) && d.Memory && s_dcs.TryGetValue(src, out var s) && s.Memory) return TRUE;
        LogOnce("gdi:blit", "win32: GDI drawing onto a window (BitBlt and friends) is accepted but not shown: the stack's windows are not GDI surfaces");
        return TRUE;
    }

    [UnmanagedCallersOnly] private static int BitBlt(nint dst, int x, int y, int w, int h, nint src, int sx, int sy, uint rop) => Blit(dst, src);
    [UnmanagedCallersOnly] private static int StretchBlt(nint dst, int x, int y, int w, int h, nint src, int sx, int sy, int sw, int sh, uint rop) => Blit(dst, src);
    [UnmanagedCallersOnly] private static uint GetPixel(nint hdc, int x, int y) => 0xFFFFFFFF;   // CLR_INVALID

    [UnmanagedCallersOnly]
    private static int FillRect(nint hdc, RECT* r, nint brush)
    {
        LogOnce("gdi:fill", "win32: GDI drawing onto a window (FillRect and friends) is accepted but not shown");
        return TRUE;
    }

    // Fonts an app carries in its resources and registers for itself. Accepted; the stack's own font
    // catalogue is what text is rendered with.
    [UnmanagedCallersOnly]
    private static nint AddFontMemResourceEx(void* data, uint length, void* reserved, uint* count)
    {
        if (count != null) *count = 1;
        return NewHandle(new object());
    }

    [UnmanagedCallersOnly] private static int RemoveFontMemResourceEx(nint h) { s_handles.TryRemove(h, out _); return TRUE; }
}
