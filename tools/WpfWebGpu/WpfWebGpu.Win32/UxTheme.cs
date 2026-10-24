// uxtheme: visual styles on, drawn by the stack's own Windows 11 theme.
//
// WinForms draws its themed parts with a managed renderer of Windows 11's aero.msstyles on every head
// (VisualStylesWin11). An application that calls uxtheme itself -- DotNetBar's themed parts, a control
// that asks for a theme part on the HDC of its paint Graphics -- comes here, and is answered from that
// same renderer (System.Windows.Forms.VisualStyles.UxThemeBridge, called directly), so both ways a part
// is asked for look the same. They were answered "no visual styles" (IsThemeActive FALSE, no theme
// data), so such code drew its classic look beside WinForms' Windows 11 one.
//
// As on Windows, a class the theme does not have opens no theme (OpenThemeData answers NULL), and the
// caller draws that part classically; a property it does not define answers E_PROP_ID_UNSUPPORTED.
// Drawing goes to the DC's surface as a blit does (DestinationGraphics): a window's, a memory DC's DIB,
// or the Graphics a GetHdc came from.

using System.Drawing;
using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;
using UxThemeBridge = System.Windows.Forms.VisualStyles.UxThemeBridge;

namespace WpfWebGpu.Win32;

public static unsafe partial class Exports
{
    private const int E_PROP_ID_UNSUPPORTED = unchecked((int)0x80070490);


    [StructLayout(LayoutKind.Sequential)] public struct SIZE { public int cx, cy; }

    private static Rectangle Rect(RECT* r) => r == null ? Rectangle.Empty : Rectangle.FromLTRB(r->Left, r->Top, r->Right, r->Bottom);
    private static void Put(RECT* r, Rectangle v) { if (r != null) *r = new RECT { Left = v.Left, Top = v.Top, Right = v.Right, Bottom = v.Bottom }; }

    /// <summary>Draws into a DC's surface, as a blit does, and lets the drawing land there.</summary>
    private static int OnDc(nint hdc, Func<Graphics, int> draw)
    {
        Graphics? g = DestinationGraphics(hdc);
        if (g == null) return E_INVALIDARG;
        try { return draw(g); }
        catch (Exception e)
        {
            LogOnce("uxtheme:draw", $"win32 shim: a uxtheme drawing failed: {e.GetType().Name}: {e.Message}");
            return E_FAIL;
        }
        finally { g.Dispose(); }
    }

    /// <summary>The font selected into one of the shim's DCs, as GDI draws theme text in it; null for
    /// a DC System.Drawing handed out (the renderer's default font then).</summary>
    private static Font? DcFont(nint hdc)
    {
        if (!s_dcs.TryGetValue(hdc, out var dc) || !s_gdi.TryGetValue(dc.Font, out var font) || font.Stock) return null;
        try { return new Font(font.FontFace, Math.Max(1, Math.Abs(font.FontHeight)), GraphicsUnit.Pixel); }
        catch { return null; }
    }

    // These imports have no A/W pair (they are Unicode only), so an application's own declaration of
    // them reaches the bare name, and its strings are UTF-16 (CharSet.Unicode) or, for CharSet.Auto --
    // UTF-16 on Windows -- UTF-8 on macOS. DotNetBar's are CharSet.Auto: its class "WINDOW" came here
    // as "䥗䑎坏" and opened no theme. Read as either (ReadEither).
    private static string Text(char* text, int count)
    {
        if (text == null) return "";
        if (count < 0) return ReadEither((byte*)text) ?? "";
        byte* b = (byte*)text;
        return count > 0 && b[0] != 0 && b[1] == 0 ? new string(text, 0, count) : System.Text.Encoding.UTF8.GetString(b, count);
    }

    // ---- themes ----------------------------------------------------------------------------------

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<nint, nint> s_windowThemes = new();
    private static uint s_themeAppProperties = 7;   // STAP_ALLOW_NONCLIENT | STAP_ALLOW_CONTROLS | STAP_ALLOW_WEBCONTENT

    [Win32Export("IsThemeActive", "IsAppThemed", "IsCompositionActive")] public static int IsThemeActive() => TRUE;
    [Win32Export("SetWindowTheme")] public static int SetWindowTheme(nint hwnd, char* app, char* idList) => S_OK;
    [Win32Export("CloseThemeData")] public static int CloseThemeData(nint theme) => S_OK;
    [Win32Export("GetThemeAppProperties")] public static uint GetThemeAppProperties() => s_themeAppProperties;
    [Win32Export("SetThemeAppProperties")] public static void SetThemeAppProperties(uint flags) => s_themeAppProperties = flags;
    [Win32Export("EnableThemeDialogTexture")] public static int EnableThemeDialogTexture(nint hwnd, uint flags) => S_OK;
    [Win32Export("EnableTheming")] public static int EnableTheming(int enable) => S_OK;

    private static nint Open(nint hwnd, char* classList)
    {
        string list = Text(classList, -1);
        nint theme = list.Length == 0 ? 0 : UxThemeBridge.Open(list);
        LogOnce("uxtheme:open:" + list, $"win32 shim: uxtheme {list}: {(theme != 0 ? "themed" : "no theme (drawn classically)")}");
        if (theme == 0) { SetLastError(unchecked((int)0x80070490)); return 0; }
        if (hwnd != 0) s_windowThemes[hwnd] = theme;
        return theme;
    }

    [Win32Export("OpenThemeData")] public static nint OpenThemeData(nint hwnd, char* classList) => Open(hwnd, classList);
    [Win32Export("OpenThemeDataEx", "OpenThemeDataForDpi")] public static nint OpenThemeDataEx(nint hwnd, char* classList, uint flagsOrDpi) => Open(hwnd, classList);
    [Win32Export("GetWindowTheme")] public static nint GetWindowTheme(nint hwnd) => s_windowThemes.TryGetValue(hwnd, out nint t) ? t : 0;

    [Win32Export("GetCurrentThemeName")]
    public static int GetCurrentThemeName(char* file, int fileMax, char* color, int colorMax, char* size, int sizeMax)
    {
        if (file != null) WriteUtf16(file, fileMax, @"C:\Windows\resources\Themes\Aero\Aero.msstyles");
        if (color != null) WriteUtf16(color, colorMax, "NormalColor");
        if (size != null) WriteUtf16(size, sizeMax, "NormalSize");
        return S_OK;
    }

    // ---- drawing ---------------------------------------------------------------------------------

    [Win32Export("DrawThemeBackground")]
    public static int DrawThemeBackground(nint theme, nint hdc, int part, int state, RECT* rect, RECT* clip)
        => theme == 0 ? E_FAIL
         : OnDc(hdc, g => UxThemeBridge.DrawBackground(theme, g, part, state, Rect(rect), Rect(clip), clip != null));

    // DTBGOPTS: dwSize, dwFlags, RECT rcClip. DTBG_CLIPRECT = 1.
    [Win32Export("DrawThemeBackgroundEx")]
    public static int DrawThemeBackgroundEx(nint theme, nint hdc, int part, int state, RECT* rect, uint* options)
    {
        bool clipped = options != null && (options[1] & 1) != 0;
        Rectangle clip = clipped ? Rect((RECT*)(options + 2)) : Rectangle.Empty;
        return theme == 0 ? E_FAIL : OnDc(hdc, g => UxThemeBridge.DrawBackground(theme, g, part, state, Rect(rect), clip, clipped));
    }

    // DT_CALCRECT (0x400) is the caller's question, not a drawing flag.
    private const uint DT_CALCRECT = 0x400;

    [Win32Export("DrawThemeText")]
    public static int DrawThemeText(nint theme, nint hdc, int part, int state, char* text, int count, uint flags, uint flags2, RECT* rect)
    {
        if (theme == 0) return E_FAIL;
        string s = Text(text, count);
        Font? font = DcFont(hdc);
        return OnDc(hdc, g => UxThemeBridge.DrawText(theme, g, part, state, s, (int)(flags & ~DT_CALCRECT), Rect(rect), font, 0, false, (flags2 & 1) != 0));   // DTT_GRAYED
    }

    // DTTOPTS: dwSize, dwFlags, crText, ... DTT_TEXTCOLOR = 1, DTT_CALCRECT = 0x200.
    [Win32Export("DrawThemeTextEx")]
    public static int DrawThemeTextEx(nint theme, nint hdc, int part, int state, char* text, int count, uint flags, RECT* rect, uint* options)
    {
        if (theme == 0) return E_FAIL;
        string s = Text(text, count);
        Font? font = DcFont(hdc);
        uint opts = options != null ? options[1] : 0;
        if ((opts & 0x200) != 0 || (flags & DT_CALCRECT) != 0)
        {
            Rectangle r = Rect(rect);
            int hr = OnDc(hdc, g =>
            {
                Size size = UxThemeBridge.MeasureText(g, s, (int)(flags & ~DT_CALCRECT), r, font);
                Put(rect, new Rectangle(r.Location, size));
                return S_OK;
            });
            return hr;
        }
        bool hasColor = (opts & 1) != 0;
        uint bgr = hasColor ? options[2] : 0;
        int argb = unchecked((int)(0xFF000000 | ((bgr & 0xFF) << 16) | (bgr & 0xFF00) | ((bgr >> 16) & 0xFF)));
        return OnDc(hdc, g => UxThemeBridge.DrawText(theme, g, part, state, s, (int)flags, Rect(rect), font, argb, hasColor, false));
    }

    [Win32Export("DrawThemeEdge")]
    public static int DrawThemeEdge(nint theme, nint hdc, int part, int state, RECT* rect, uint edge, uint flags, RECT* content)
    {
        if (theme == 0) return E_FAIL;
        return OnDc(hdc, g =>
        {
            int hr = UxThemeBridge.DrawEdge(theme, g, part, state, Rect(rect), (int)edge, (int)flags, out Rectangle inner);
            Put(content, inner);
            return hr;
        });
    }

    private static int ParentBackground(nint hwnd, nint hdc, RECT* rect)
    {
        if (Resolve(hwnd) is not Forms.Control child) return S_OK;
        Rectangle r = rect != null ? Rect(rect) : new Rectangle(Point.Empty, child.Size);
        return OnDc(hdc, g => UxThemeBridge.DrawParentBackground(g, r, child));
    }

    [Win32Export("DrawThemeParentBackground")] public static int DrawThemeParentBackground(nint hwnd, nint hdc, RECT* rect) => ParentBackground(hwnd, hdc, rect);
    [Win32Export("DrawThemeParentBackgroundEx")] public static int DrawThemeParentBackgroundEx(nint hwnd, nint hdc, uint flags, RECT* rect) => ParentBackground(hwnd, hdc, rect);

    // ---- metrics and properties -----------------------------------------------------------------

    [Win32Export("GetThemePartSize")]
    public static int GetThemePartSize(nint theme, nint hdc, int part, int state, RECT* rect, int type, SIZE* size)
    {
        if (theme == 0 || size == null) return E_INVALIDARG;
        int hr = UxThemeBridge.PartSize(theme, null, part, state, Rect(rect), rect != null, type, out Size s);
        *size = new SIZE { cx = s.Width, cy = s.Height };
        return hr;
    }

    // MARGINS: cxLeftWidth, cxRightWidth, cyTopHeight, cyBottomHeight.
    [Win32Export("GetThemeMargins")]
    public static int GetThemeMargins(nint theme, nint hdc, int part, int state, int property, RECT* rect, int* margins)
    {
        if (theme == 0 || margins == null) return E_INVALIDARG;
        int hr = UxThemeBridge.Margins(theme, null, part, state, property, out Forms.Padding m);
        margins[0] = m.Left; margins[1] = m.Right; margins[2] = m.Top; margins[3] = m.Bottom;
        return hr;
    }

    [Win32Export("GetThemeBackgroundContentRect")]
    public static int GetThemeBackgroundContentRect(nint theme, nint hdc, int part, int state, RECT* bounds, RECT* content)
    {
        if (theme == 0 || bounds == null || content == null) return E_INVALIDARG;
        int hr = UxThemeBridge.ContentRect(theme, null, part, state, Rect(bounds), out Rectangle r);
        Put(content, r);
        return hr;
    }

    [Win32Export("GetThemeBackgroundExtent")]
    public static int GetThemeBackgroundExtent(nint theme, nint hdc, int part, int state, RECT* content, RECT* extent)
    {
        if (theme == 0 || content == null || extent == null) return E_INVALIDARG;
        int hr = UxThemeBridge.Extent(theme, null, part, state, Rect(content), out Rectangle r);
        Put(extent, r);
        return hr;
    }

    // The renderer's parts are rectangles: the region is the part's rectangle.
    [Win32Export("GetThemeBackgroundRegion")]
    public static int GetThemeBackgroundRegion(nint theme, nint hdc, int part, int state, RECT* rect, nint* region)
    {
        if (theme == 0 || rect == null || region == null) return E_INVALIDARG;
        *region = NewGdi(new GdiObject { Kind = GdiKind.Region, Region = *rect });
        return S_OK;
    }

    [Win32Export("GetThemeTextExtent")]
    public static int GetThemeTextExtent(nint theme, nint hdc, int part, int state, char* text, int count, uint flags, RECT* bounds, RECT* extent)
    {
        if (theme == 0 || extent == null) return E_INVALIDARG;
        string s = Text(text, count);
        Font? font = DcFont(hdc);
        Rectangle r = Rect(bounds);
        return OnDc(hdc, g =>
        {
            Size size = UxThemeBridge.MeasureText(g, s, (int)(flags & ~DT_CALCRECT), r, font);
            Put(extent, new Rectangle(r.Location, size));
            return S_OK;
        });
    }

    [Win32Export("GetThemeColor")]
    public static int GetThemeColor(nint theme, int part, int state, int property, uint* color)
    {
        if (theme == 0 || color == null) return E_INVALIDARG;
        int hr = UxThemeBridge.ThemeColor(theme, part, state, property, out int argb);
        if (hr != S_OK) return E_PROP_ID_UNSUPPORTED;
        uint c = (uint)argb;
        *color = ((c >> 16) & 0xFF) | (c & 0xFF00) | ((c & 0xFF) << 16);   // COLORREF 0x00BBGGRR
        return S_OK;
    }

    // Properties the renderer does not model: not defined, as Windows answers for a property a part lacks.
    [Win32Export("GetThemeInt", "GetThemeEnumValue", "GetThemeBool")] public static int GetThemeInt(nint theme, int part, int state, int property, int* value) => E_PROP_ID_UNSUPPORTED;
    [Win32Export("GetThemeFilename", "GetThemeString")] public static int GetThemeFilename(nint theme, int part, int state, int property, char* buffer, int max) => E_PROP_ID_UNSUPPORTED;

    [Win32Export("GetThemeSysColor")] public static uint GetThemeSysColor(nint theme, int index) => SysColor(index);

    [Win32Export("IsThemePartDefined")] public static int IsThemePartDefined(nint theme, int part, int state) => theme != 0 && UxThemeBridge.IsPartDefined(theme, part) ? TRUE : FALSE;

    [Win32Export("IsThemeBackgroundPartiallyTransparent")]
    public static int IsThemeBackgroundPartiallyTransparent(nint theme, int part, int state)
        => theme != 0 && UxThemeBridge.IsPartiallyTransparent(theme, part, state) ? TRUE : FALSE;
}
