// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Windows backend for NativePlatform: the ONLY file in the engine that calls
// kernel32/user32/gdi32. It creates a wgpu surface from an HWND (the milcore
// HWND/DXGI swap-chain analog) and presents Popups (ComboBox/Menu/ToolTip) to
// their WS_EX_LAYERED per-pixel-alpha windows via UpdateLayeredWindow. All Win32
// P/Invoke that used to be scattered across WpfCompositionSink + LayeredWindow
// now lives here.
//

using System;
using System.Runtime.InteropServices;

namespace Microsoft.Wpf.Interop.WebGpu.Composition.Platform
{
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    internal static unsafe class Win32Interop
    {
        // ---- wgpu surface from an HWND ----------------------------------------------

        public static IntPtr CreateSurface(IntPtr instance, IntPtr hwnd)
        {
            var hwndSource = new Wgpu.WGPUSurfaceSourceWindowsHWND
            {
                chain = new Wgpu.WGPUChainedStruct { next = null, sType = Wgpu.WGPUSType_SurfaceSourceWindowsHWND },
                hinstance = (void*)GetModuleHandleW(null),
                hwnd = (void*)hwnd,
            };
            var desc = new Wgpu.WGPUSurfaceDescriptor { nextInChain = (Wgpu.WGPUChainedStruct*)&hwndSource };
            return Wgpu.wgpuInstanceCreateSurface(instance, &desc);
        }

        // ---- layered-window (popup) present -----------------------------------------

        /// <summary>Push a premultiplied top-down RGBA frame to a layered window. Always true.</summary>
        public static bool PresentLayered(IntPtr hwnd, byte[] rgbaPremul, int width, int height)
        {
            if (width <= 0 || height <= 0 || rgbaPremul.Length < width * height * 4) return false;

            IntPtr screenDc = GetDC(IntPtr.Zero);
            IntPtr memDc = CreateCompatibleDC(screenDc);

            var bmi = new BITMAPINFO
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFO>(),
                biWidth = width,
                biHeight = -height,        // negative => top-down (row 0 is the top)
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0,         // BI_RGB
            };

            IntPtr dib = CreateDIBSection(memDc, ref bmi, 0, out IntPtr bits, IntPtr.Zero, 0);
            if (dib == IntPtr.Zero) { CleanUp(screenDc, memDc, IntPtr.Zero, IntPtr.Zero); return false; }

            // RGBA (GPU readback) -> BGRA (what GDI/UpdateLayeredWindow expects), both premultiplied.
            byte* dst = (byte*)bits;
            for (int i = 0; i < width * height * 4; i += 4)
            {
                dst[i + 0] = rgbaPremul[i + 2];   // B
                dst[i + 1] = rgbaPremul[i + 1];   // G
                dst[i + 2] = rgbaPremul[i + 0];   // R
                dst[i + 3] = rgbaPremul[i + 3];   // A
            }

            IntPtr oldBmp = SelectObject(memDc, dib);
            var size = new SIZE { cx = width, cy = height };
            var src = new POINT { x = 0, y = 0 };
            var blend = new BLENDFUNCTION
            {
                BlendOp = AC_SRC_OVER,
                BlendFlags = 0,
                SourceConstantAlpha = 255,
                AlphaFormat = AC_SRC_ALPHA,
            };
            // pptDst = null keeps the window's current position (WPF already placed it).
            UpdateLayeredWindow(hwnd, screenDc, IntPtr.Zero, ref size, memDc, ref src, 0, ref blend, ULW_ALPHA);

            CleanUp(screenDc, memDc, dib, oldBmp);
            return true;
        }

        private static void CleanUp(IntPtr screenDc, IntPtr memDc, IntPtr dib, IntPtr oldBmp)
        {
            if (oldBmp != IntPtr.Zero) SelectObject(memDc, oldBmp);
            if (dib != IntPtr.Zero) DeleteObject(dib);
            if (memDc != IntPtr.Zero) DeleteDC(memDc);
            if (screenDc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screenDc);
        }

        // ---- native declarations ----------------------------------------------------

        private const byte AC_SRC_OVER = 0x00;
        private const byte AC_SRC_ALPHA = 0x01;
        private const uint ULW_ALPHA = 0x00000002;

        [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int cx; public int cy; }
        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int x; public int y; }
        [StructLayout(LayoutKind.Sequential)]
        private struct BLENDFUNCTION { public byte BlendOp; public byte BlendFlags; public byte SourceConstantAlpha; public byte AlphaFormat; }

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFO
        {
            public uint biSize;
            public int biWidth;
            public int biHeight;
            public ushort biPlanes;
            public ushort biBitCount;
            public uint biCompression;
            public uint biSizeImage;
            public int biXPelsPerMeter;
            public int biYPelsPerMeter;
            public uint biClrUsed;
            public uint biClrImportant;
            public uint bmiColors;   // padding for the (unused) palette entry
        }

        [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandleW(string? lpModuleName);

        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr h);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, IntPtr pptDst, ref SIZE psize,
            IntPtr hdcSrc, ref POINT pptSrc, uint crKey, ref BLENDFUNCTION pblend, uint dwFlags);
    }
}
