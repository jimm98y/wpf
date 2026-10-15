// Windows: a form that is see-through -- a layered window (WS_EX_LAYERED with UpdateLayeredWindow's
// per-pixel alpha, as soft-edged splash screens draw), a TransparencyKey, or an Opacity below one.
//
// A swap chain on an ordinary HWND is composited as opaque by DWM, whatever alpha the frame carries,
// so such a form showed its transparent corners white. Windows has its own way to put a window on
// screen with per-pixel alpha, and it is the one the app itself would use: the host window is made
// WS_EX_LAYERED and each frame is rendered offscreen (cleared to transparent) and handed to
// UpdateLayeredWindow with ULW_ALPHA -- the TransparencyKey colour keyed out, the Opacity as the
// constant alpha. Ordinary forms keep the swap chain.

using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

internal sealed unsafe partial class Win32Host
{
    private bool _layeredHost;

    /// <summary>Whether the form asks to be see-through.</summary>
    private bool WantsLayeredHost()
    {
        if (FormGone) return false;
        if (_form.Opacity < 1.0 || !_form.TransparencyKey.IsEmpty) return true;
        // An app that makes its form WS_EX_LAYERED itself (CreateParams) and draws it through
        // UpdateLayeredWindow: the driver holds that picture (XplatUIWebGpu.SetLayeredImage).
        return _hasLayeredImage != null && (bool)_hasLayeredImage.Invoke(_driver, new object[] { _formHandle });
    }

    /// <summary>Present one frame through UpdateLayeredWindow. False when that cannot be done, and
    /// the swap chain is used as before.</summary>
    private bool PresentLayered(System.Collections.Generic.List<(object, int, int)> scenes, System.Drawing.Rectangle? caret)
    {
        if (!_layeredHost)
        {
            _layeredHost = true;
            long ex = (long)GetWindowLongPtrW(_hwnd, -20);
            SetWindowLongPtrW(_hwnd, -20, (IntPtr)(ex | 0x00080000));      // WS_EX_LAYERED
            _wgpu.SetTransparent(true);
        }
        // A window the app draws through UpdateLayeredWindow shows exactly that bitmap, and Windows
        // draws nothing else of it (not its children either): hand the app's own premultiplied bits
        // on as they are, with its constant alpha.
        if (_getLayeredBits?.Invoke(_driver, new object[] { _formHandle }) is ValueTuple<byte[], int, int, byte> lb && lb.Item2 > 0 && lb.Item3 > 0)
        {
            // An application that is not per-monitor aware hands over bits in its own (96-dpi or
            // system-dpi) pixels, and Windows' DPI virtualisation shows them stretched to the
            // monitor's scale, nearest neighbour: measured at 200%, a 64x48 picture becomes 128x96
            // with every pixel doubled. The host is per-monitor aware, so it stretches them itself.
            if (Math.Abs(_scale - 1f) > 0.001f)
            {
                var (sb, sw, sh) = StretchBits(lb.Item1, lb.Item2, lb.Item3, _scale);
                return PresentBits(sb, sw, sh, lb.Item4, null, true);
            }
            return PresentBits(lb.Item1, lb.Item2, lb.Item3, lb.Item4, null, true);
        }
        byte[] rgba = _wgpu.RenderScenesToRgba(scenes, caret);
        int w = _wgpu.DeviceWidth, h = _wgpu.DeviceHeight;
        if (rgba == null || w <= 0 || h <= 0 || rgba.Length < w * h * 4) return false;

        System.Drawing.Color key = _form.TransparencyKey;
        byte opacity = (byte)Math.Round(Math.Clamp(_form.Opacity, 0.0, 1.0) * 255);
        return PresentBits(rgba, w, h, opacity, key.IsEmpty ? null : key, false);
    }

    /// <summary>32-bit pixels stretched by <paramref name="scale"/>, each destination pixel taking
    /// the source pixel under its centre.</summary>
    internal static (byte[] Bits, int Width, int Height) StretchBits(byte[] src, int w, int h, float scale)
    {
        int dw = Math.Max(1, (int)Math.Round(w * scale)), dh = Math.Max(1, (int)Math.Round(h * scale));
        var dst = new byte[dw * dh * 4];
        fixed (byte* s = src, d = dst)
        {
            uint* sp = (uint*)s, dp = (uint*)d;
            for (int y = 0; y < dh; y++)
            {
                int sy = Math.Min((int)((y + 0.5) * h / dh), h - 1);
                for (int x = 0; x < dw; x++)
                {
                    int sx = Math.Min((int)((x + 0.5) * w / dw), w - 1);
                    dp[y * dw + x] = sp[sy * w + sx];
                }
            }
        }
        return (dst, dw, dh);
    }

    /// <summary>UpdateLayeredWindow with <paramref name="px"/> as the picture: the app's premultiplied
    /// BGRA as it is (<paramref name="appBgra"/>), else the renderer's premultiplied RGBA with the
    /// TransparencyKey colour (<paramref name="key"/>) keyed out.</summary>
    private bool PresentBits(byte[] px, int w, int h, byte opacity, System.Drawing.Color? key, bool appBgra)
    {
        var bi = new BITMAPINFOHEADER { biSize = sizeof(BITMAPINFOHEADER), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
        IntPtr screen = GetDC(IntPtr.Zero), mem = CreateCompatibleDC(screen);
        void* bits;
        IntPtr dib = CreateDIBSection(screen, &bi, 0, &bits, IntPtr.Zero, 0);
        if (dib == IntPtr.Zero) { DeleteDC(mem); ReleaseDC(IntPtr.Zero, screen); return false; }
        IntPtr old = SelectObject(mem, dib);
        try
        {
            byte* dst = (byte*)bits;
            if (appBgra) Marshal.Copy(px, 0, (IntPtr)dst, w * h * 4);
            else
            {
                bool keyed = key != null;
                System.Drawing.Color k = key ?? default;
                for (int i = 0; i < w * h; i++)
                {
                    // The renderer's colour is premultiplied, which is what ULW_ALPHA takes.
                    byte r = px[i * 4], g = px[i * 4 + 1], b = px[i * 4 + 2], a = px[i * 4 + 3];
                    if (keyed && a == 255 && r == k.R && g == k.G && b == k.B) { r = g = b = a = 0; }
                    dst[i * 4] = b; dst[i * 4 + 1] = g; dst[i * 4 + 2] = r; dst[i * 4 + 3] = a;
                }
            }
            var size = new SIZE { cx = w, cy = h };
            var src = new POINT { x = 0, y = 0 };
            uint blend = 0u | 0u << 8 | (uint)opacity << 16 | 1u << 24;          // AC_SRC_OVER, SourceConstantAlpha, AC_SRC_ALPHA
            return UpdateLayeredWindow(_hwnd, screen, IntPtr.Zero, &size, mem, &src, 0, &blend, 2 /* ULW_ALPHA */);
        }
        finally
        {
            SelectObject(mem, old);
            DeleteObject(dib);
            DeleteDC(mem);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }
    [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int cx, cy; }

    [DllImport("user32", EntryPoint = "UpdateLayeredWindow")]
    private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, IntPtr pptDst, SIZE* psize, IntPtr hdcSrc, POINT* pptSrc, uint key, uint* blend, uint flags);
    [DllImport("gdi32")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32")] private static extern IntPtr CreateDIBSection(IntPtr hdc, BITMAPINFOHEADER* bmi, uint usage, void** bits, IntPtr section, uint offset);
    [DllImport("gdi32")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32")] private static extern bool DeleteDC(IntPtr hdc);
}
