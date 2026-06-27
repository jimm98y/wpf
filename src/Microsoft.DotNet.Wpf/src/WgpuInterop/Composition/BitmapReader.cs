// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Reads pixels from the native bitmap WPF hands the channel for an image source (via
// SendCommandBitmapSource). The handle is a milcore CWICWrapperBitmap exposing
// IWGXBitmapSource (milcore's clone of IWICBitmapSource: same vtable, but GetPixelFormat
// returns a MilPixelFormat enum and the IID differs). We read the pixels and return
// straight (non-premultiplied) RGBA so the WebGPU ImageBrush can sample it. Windows-only;
// handles the common BGR/BGRA/PBGRA (32bpp) and BGR/RGB (24bpp) formats.
//

using System;
using System.Runtime.InteropServices;

namespace Microsoft.Wpf.Interop.WebGpu.Composition
{
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    internal static class BitmapReader
    {
        // MilPixelFormat::Enum values (WpfGfx wincodec_private_generated.h).
        private const uint BGR24 = 0x0C, RGB24 = 0x0D, BGR32 = 0x0E, BGRA32 = 0x0F, PBGRA32 = 0x10;

        /// <summary>Read a milcore IWGXBitmapSource pointer to straight RGBA. False on failure.</summary>
        public static bool TryRead(IntPtr pBitmapSource, out byte[] rgba, out int width, out int height)
        {
            rgba = Array.Empty<byte>(); width = 0; height = 0;
            if (pBitmapSource == IntPtr.Zero) return false;
            try
            {
                var src = (IWGXBitmapSource)Marshal.GetObjectForIUnknown(pBitmapSource);
                src.GetSize(out uint w, out uint h);
                if (w == 0 || h == 0 || w > 16384 || h > 16384) return false;
                src.GetPixelFormat(out uint pf);
                Log($"  bitmap {w}x{h} milformat=0x{pf:x}");

                int bpp = pf switch { BGR24 or RGB24 => 3, BGR32 or BGRA32 or PBGRA32 => 4, _ => 0 };
                if (bpp == 0) return false;
                int stride = ((int)w * bpp * 8 + 31) / 32 * 4;     // 4-byte aligned rows

                var buf = new byte[stride * (int)h];
                GCHandle gch = GCHandle.Alloc(buf, GCHandleType.Pinned);
                try { src.CopyPixels(IntPtr.Zero, (uint)stride, (uint)buf.Length, gch.AddrOfPinnedObject()); }
                finally { gch.Free(); }

                var outPix = new byte[(int)w * (int)h * 4];
                bool premult = pf == PBGRA32;
                for (int y = 0; y < (int)h; y++)
                {
                    int row = y * stride, orow = y * (int)w * 4;
                    for (int x = 0; x < (int)w; x++)
                    {
                        int s = row + x * bpp, d = orow + x * 4;
                        byte rr, gg, bb, aa;
                        if (pf == RGB24) { rr = buf[s]; gg = buf[s + 1]; bb = buf[s + 2]; aa = 255; }
                        else             { bb = buf[s]; gg = buf[s + 1]; rr = buf[s + 2]; aa = bpp == 4 && pf != BGR32 ? buf[s + 3] : (byte)255; }
                        if (premult && aa != 0)
                        {
                            rr = (byte)Math.Min(255, rr * 255 / aa);
                            gg = (byte)Math.Min(255, gg * 255 / aa);
                            bb = (byte)Math.Min(255, bb * 255 / aa);
                        }
                        outPix[d] = rr; outPix[d + 1] = gg; outPix[d + 2] = bb; outPix[d + 3] = aa;
                    }
                }

                rgba = outPix; width = (int)w; height = (int)h;
                return true;
            }
            catch (Exception ex) { Log("  bitmap read threw: " + ex.GetType().Name + ": " + ex.Message); return false; }
        }

        private static readonly string? s_log = Environment.GetEnvironmentVariable("WPF_WEBGPU_SINK_LOG");
        private static void Log(string m) { if (s_log != null) try { System.IO.File.AppendAllText(s_log, m + Environment.NewLine); } catch { } }

        // IWGXBitmapSource (IID_IWGXBitmapSource from wgx_render.h) -- same vtable as
        // IWICBitmapSource, but GetPixelFormat yields a MilPixelFormat enum.
        [ComImport, Guid("dd0bf622-0650-4a1e-b20f-4b4ab6edfca3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IWGXBitmapSource
        {
            void GetSize(out uint puWidth, out uint puHeight);
            void GetPixelFormat(out uint pPixelFormat);
            void Reserved_GetResolution();
            void Reserved_CopyPalette();
            void CopyPixels(IntPtr prc, uint cbStride, uint cbBufferSize, IntPtr pvPixels);
        }
    }
}
