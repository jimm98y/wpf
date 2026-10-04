// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A Bitmap's pixels with no GDI+ to hold them (Image.managedPixels): the format conversions LockBits
// and the scan0 constructor need, and the two codecs a managed path can carry without a platform
// imaging library -- PNG (the renderer's own decoder, and a small encoder here) and uncompressed BMP.
//

using System;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace System.Drawing
{
    internal static class ManagedPixels
    {
        internal static int BytesPerPixel(PixelFormat f) => f switch
        {
            PixelFormat.Format32bppArgb or PixelFormat.Format32bppPArgb or PixelFormat.Format32bppRgb => 4,
            PixelFormat.Format24bppRgb => 3,
            _ => 0,
        };

        /// <summary>Copies <paramref name="rect"/> of straight ARGB pixels out into a locked buffer.</summary>
        internal static void Write(int[] src, int srcWidth, Rectangle rect, IntPtr dst, int stride, PixelFormat f)
        {
            int bpp = BytesPerPixel(f);
            var row = new byte[stride];
            for (int y = 0; y < rect.Height; y++)
            {
                int o = (rect.Y + y) * srcWidth + rect.X;
                for (int x = 0; x < rect.Width; x++)
                {
                    int v = src[o + x];
                    int a = (v >> 24) & 0xff, r = (v >> 16) & 0xff, g = (v >> 8) & 0xff, b = v & 0xff;
                    if (f == PixelFormat.Format32bppPArgb) { r = r * a / 255; g = g * a / 255; b = b * a / 255; }
                    int d = x * bpp;
                    row[d] = (byte)b; row[d + 1] = (byte)g; row[d + 2] = (byte)r;
                    if (bpp == 4) row[d + 3] = f == PixelFormat.Format32bppRgb ? (byte)255 : (byte)a;
                }
                Marshal.Copy(row, 0, dst + y * stride, stride);
            }
        }

        /// <summary>Copies a locked buffer (or a caller's scan0) back into straight ARGB pixels.</summary>
        internal static void Read(IntPtr src, int stride, PixelFormat f, int[] dst, int dstWidth, Rectangle rect)
        {
            int bpp = BytesPerPixel(f);
            if (bpp == 0) return;
            int absStride = Math.Abs(stride);
            var row = new byte[absStride];
            for (int y = 0; y < rect.Height; y++)
            {
                Marshal.Copy(src + y * stride, row, 0, Math.Min(absStride, rect.Width * bpp));
                int o = (rect.Y + y) * dstWidth + rect.X;
                for (int x = 0; x < rect.Width; x++)
                {
                    int s = x * bpp;
                    int b = row[s], g = row[s + 1], r = row[s + 2];
                    int a = bpp == 4 && f != PixelFormat.Format32bppRgb ? row[s + 3] : 255;
                    if (f == PixelFormat.Format32bppPArgb && a != 0 && a != 255)
                    {
                        r = Math.Min(255, r * 255 / a); g = Math.Min(255, g * 255 / a); b = Math.Min(255, b * 255 / a);
                    }
                    dst[o + x] = (a << 24) | (r << 16) | (g << 8) | b;
                }
            }
        }

        /// <summary>Decodes a PNG or an uncompressed BMP to straight ARGB.</summary>
        internal static bool Decode(byte[] data, out int width, out int height, out int[] argb, out float dpiX, out float dpiY)
        {
            width = height = 0; argb = null; dpiX = dpiY = 96f;
            if (data.Length >= 8 && data[0] == 0x89 && data[1] == (byte)'P')
            {
                byte[] rgba;
                try { rgba = Microsoft.Wpf.Interop.WebGpu.Composition.PngReader.Decode(data, out width, out height); }
                catch (InvalidDataException) { return false; }
                argb = new int[width * height];
                for (int i = 0; i < argb.Length; i++)
                    argb[i] = (rgba[i * 4 + 3] << 24) | (rgba[i * 4] << 16) | (rgba[i * 4 + 1] << 8) | rgba[i * 4 + 2];
                PngDpi(data, ref dpiX, ref dpiY);
                return true;
            }
            if (data.Length >= 54 && data[0] == (byte)'B' && data[1] == (byte)'M')
                return DecodeBmp(data, out width, out height, out argb, ref dpiX, ref dpiY);
            return false;
        }

        // pHYs, when the file states one in pixels per metre.
        private static void PngDpi(byte[] d, ref float dpiX, ref float dpiY)
        {
            int p = 8;
            while (p + 12 <= d.Length)
            {
                int len = (d[p] << 24) | (d[p + 1] << 16) | (d[p + 2] << 8) | d[p + 3];
                if (len < 0) return;
                string type = System.Text.Encoding.ASCII.GetString(d, p + 4, 4);
                if (type == "pHYs" && len >= 9 && p + 17 <= d.Length && d[p + 16] == 1)
                {
                    uint x = (uint)((d[p + 8] << 24) | (d[p + 9] << 16) | (d[p + 10] << 8) | d[p + 11]);
                    uint y = (uint)((d[p + 12] << 24) | (d[p + 13] << 16) | (d[p + 14] << 8) | d[p + 15]);
                    if (x > 0) dpiX = x * 0.0254f;
                    if (y > 0) dpiY = y * 0.0254f;
                    return;
                }
                if (type == "IDAT") return;
                p += 12 + len;
            }
        }

        private static bool DecodeBmp(byte[] d, out int width, out int height, out int[] argb, ref float dpiX, ref float dpiY)
        {
            width = height = 0; argb = null;
            int offset = BitConverter.ToInt32(d, 10);
            int headerSize = BitConverter.ToInt32(d, 14);
            width = BitConverter.ToInt32(d, 18);
            int h = BitConverter.ToInt32(d, 22);
            int bpp = BitConverter.ToInt16(d, 28);
            int compression = BitConverter.ToInt32(d, 30);
            if (headerSize >= 40)
            {
                int px = BitConverter.ToInt32(d, 38), py = BitConverter.ToInt32(d, 42);
                if (px > 0) dpiX = px * 0.0254f;
                if (py > 0) dpiY = py * 0.0254f;
            }
            bool bottomUp = h > 0;
            height = Math.Abs(h);
            if (width <= 0 || height <= 0 || (bpp != 24 && bpp != 32) || (compression != 0 && compression != 3)) return false;
            int stride = (width * bpp / 8 + 3) & ~3;
            if (offset + stride * height > d.Length) return false;
            argb = new int[width * height];
            for (int y = 0; y < height; y++)
            {
                int row = offset + (bottomUp ? height - 1 - y : y) * stride;
                for (int x = 0; x < width; x++)
                {
                    int s = row + x * bpp / 8;
                    int a = bpp == 32 ? d[s + 3] : 255;
                    argb[y * width + x] = (a << 24) | (d[s + 2] << 16) | (d[s + 1] << 8) | d[s];
                }
            }
            // A 32-bit BMP whose alpha is all zero means "no alpha", as GDI+ reads it.
            if (bpp == 32)
            {
                bool anyAlpha = false;
                foreach (int v in argb) if ((v >> 24) != 0) { anyAlpha = true; break; }
                if (!anyAlpha) for (int i = 0; i < argb.Length; i++) argb[i] |= unchecked((int)0xff000000);
            }
            return true;
        }

        /// <summary>Writes straight RGBA8 as a PNG (8-bit RGBA, no filtering beyond 'none').</summary>
        internal static void EncodePng(Stream output, byte[] rgba, int width, int height)
        {
            output.Write(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 13, 10, 26, 10 });
            var ihdr = new byte[13];
            Be(ihdr, 0, width); Be(ihdr, 4, height);
            ihdr[8] = 8; ihdr[9] = 6;
            Chunk(output, "IHDR", ihdr);
            using (var raw = new MemoryStream())
            {
                using (var z = new ZLibStream(raw, CompressionLevel.Optimal, leaveOpen: true))
                {
                    for (int y = 0; y < height; y++)
                    {
                        z.WriteByte(0);
                        z.Write(rgba, y * width * 4, width * 4);
                    }
                }
                Chunk(output, "IDAT", raw.ToArray());
            }
            Chunk(output, "IEND", Array.Empty<byte>());
        }

        private static void Chunk(Stream s, string type, byte[] data)
        {
            var len = new byte[4]; Be(len, 0, data.Length); s.Write(len);
            byte[] t = System.Text.Encoding.ASCII.GetBytes(type);
            s.Write(t); s.Write(data);
            uint crc = Crc(t, 0xffffffffu); crc = Crc(data, crc) ^ 0xffffffffu;
            var c = new byte[4]; Be(c, 0, (int)crc); s.Write(c);
        }

        private static uint[] s_crc;

        private static uint Crc(byte[] data, uint crc)
        {
            if (s_crc == null)
            {
                var table = new uint[256];
                for (uint n = 0; n < 256; n++)
                {
                    uint c = n;
                    for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xedb88320u ^ (c >> 1) : c >> 1;
                    table[n] = c;
                }
                s_crc = table;
            }
            foreach (byte b in data) crc = s_crc[(crc ^ b) & 0xff] ^ (crc >> 8);
            return crc;
        }

        private static void Be(byte[] b, int o, int v)
        {
            b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v;
        }
    }
}
