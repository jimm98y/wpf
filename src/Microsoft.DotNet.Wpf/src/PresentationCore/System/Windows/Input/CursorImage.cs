// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A custom cursor's picture (a .cur or .ani file, or a stream of one) for the heads that have no Win32 cursor to
// load it into: the image of the file nearest the size wanted -- as LoadImage picks one -- as 32-bit BGRA, straight
// (not premultiplied) alpha, with its hot spot.
//
//   .cur / .ico   ICONDIR, its entries (a cursor's hot spot in the planes/bit-count fields), each image a PNG or a
//                 DIB: a BITMAPINFOHEADER of twice the height, its colours (1, 4, 8, 24 or 32 bits, a palette below
//                 16), then the AND mask. A pixel the mask leaves is transparent; one it leaves AND inverts (the
//                 colour bit set) is drawn black, as a picture cannot invert the screen beneath it.
//   .ani          RIFF ACON: its first frame (LIST fram, the first icon chunk), a .cur or .ico of its own.
//

using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace System.Windows.Input
{
    internal sealed class CursorImage
    {
        public int Width { get; private init; }
        public int Height { get; private init; }
        public int HotspotX { get; private init; }
        public int HotspotY { get; private init; }
        /// <summary>Width * Height pixels, top row first, each B, G, R, A (straight alpha).</summary>
        public byte[] Bgra { get; private init; } = Array.Empty<byte>();

        /// <summary>The picture as a PNG (what AppKit's NSImage and a browser's CSS cursor take), made once.</summary>
        public byte[] Png
        {
            get
            {
                if (_png != null) return _png;
                var encoder = new PngBitmapEncoder();
                // 72 dots per inch: an NSImage is as many points as the cursor has pixels, as Windows shows it at 100%
                encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(Width, Height, 72, 72, PixelFormats.Bgra32, null, Bgra, Width * 4)));
                using var png = new MemoryStream();
                encoder.Save(png);
                return _png = png.ToArray();
            }
        }

        private byte[] _png;

        /// <summary>The CSS cursor a browser shows it as: the PNG as a data URL, its hot spot, made once.</summary>
        public string Css => _css ??= $"url(data:image/png;base64,{Convert.ToBase64String(Png)}) {HotspotX} {HotspotY}, auto";

        private string _css;

        /// <summary>The file's image nearest <paramref name="size"/> pixels, or null where the file is not one.</summary>
        public static CursorImage Decode(byte[] file, int size)
        {
            try
            {
                if (file.Length >= 12 && file[0] == 'R' && file[1] == 'I' && file[2] == 'F' && file[3] == 'F'
                    && file[8] == 'A' && file[9] == 'C' && file[10] == 'O' && file[11] == 'N')
                    return FirstFrame(file, size);
                return FromIconFile(file, size);
            }
            catch (Exception e) when (e is ArgumentException or IndexOutOfRangeException or IOException or NotSupportedException or FileFormatException or OverflowException)
            {
                return null;
            }
        }

        // ---- .cur / .ico ------------------------------------------------------------------------------------

        private static CursorImage FromIconFile(byte[] f, int size)
        {
            if (f.Length < 6 || U16(f, 0) != 0) return null;
            int type = U16(f, 2), count = U16(f, 4);
            if ((type != 1 && type != 2) || count == 0 || f.Length < 6 + 16 * count) return null;
            int best = -1, bestDistance = int.MaxValue, bestDepth = -1;
            for (int i = 0; i < count; i++)
            {
                int at = 6 + 16 * i;
                int w = f[at] == 0 ? 256 : f[at];
                int depth = type == 1 ? U16(f, at + 6) : 0;
                int distance = Math.Abs(w - size);
                // the nearest size; of those, the deepest colours
                if (distance < bestDistance || (distance == bestDistance && depth > bestDepth)) { best = i; bestDistance = distance; bestDepth = depth; }
            }
            int e = 6 + 16 * best;
            int hotX = type == 2 ? U16(f, e + 4) : 0, hotY = type == 2 ? U16(f, e + 6) : 0;
            int length = (int)U32(f, e + 8), offset = (int)U32(f, e + 12);
            if (offset < 0 || length <= 0 || offset + length > f.Length) return null;
            var (w2, h2, bgra) = IsPng(f, offset) ? DecodePng(f, offset, length) : Dib(f, offset);
            if (bgra == null) return null;
            return new CursorImage { Width = w2, Height = h2, HotspotX = Math.Min(hotX, w2 - 1), HotspotY = Math.Min(hotY, h2 - 1), Bgra = bgra };
        }

        private static bool IsPng(byte[] f, int at) => f.Length > at + 8 && f[at] == 0x89 && f[at + 1] == 'P' && f[at + 2] == 'N' && f[at + 3] == 'G';

        private static (int, int, byte[]) DecodePng(byte[] f, int offset, int length)
        {
            var decoder = BitmapDecoder.Create(new MemoryStream(f, offset, length, writable: false), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            BitmapSource frame = decoder.Frames[0];
            if (frame.Format != PixelFormats.Bgra32) frame = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
            int w = frame.PixelWidth, h = frame.PixelHeight;
            var pixels = new byte[w * h * 4];
            frame.CopyPixels(pixels, w * 4, 0);
            return (w, h, pixels);
        }

        private static (int, int, byte[]) Dib(byte[] f, int at)
        {
            int header = (int)U32(f, at);
            int w = (int)U32(f, at + 4), h = Math.Abs((int)U32(f, at + 8)) / 2;
            int bpp = U16(f, at + 14);
            int compression = (int)U32(f, at + 16);
            int colorsUsed = (int)U32(f, at + 32);
            if (w <= 0 || h <= 0 || w > 1024 || h > 1024 || compression != 0 || bpp is not (1 or 4 or 8 or 24 or 32)) return (0, 0, null);
            int paletteCount = bpp <= 8 ? (colorsUsed != 0 ? colorsUsed : 1 << bpp) : 0;
            int palette = at + header;
            int xor = palette + 4 * paletteCount;
            int xorStride = (w * bpp + 31) / 32 * 4;
            int and = xor + xorStride * h;
            int andStride = (w + 31) / 32 * 4;
            bool hasMask = and + andStride * h <= f.Length;
            var pixels = new byte[w * h * 4];
            bool anyAlpha = false;
            for (int y = 0; y < h; y++)
            {
                int row = xor + (h - 1 - y) * xorStride;   // bottom-up
                int maskRow = and + (h - 1 - y) * andStride;
                for (int x = 0; x < w; x++)
                {
                    byte b, g, r, a = 255;
                    uint index = 0;
                    switch (bpp)
                    {
                        case 32: b = f[row + x * 4]; g = f[row + x * 4 + 1]; r = f[row + x * 4 + 2]; a = f[row + x * 4 + 3]; anyAlpha |= a != 0; break;
                        case 24: b = f[row + x * 3]; g = f[row + x * 3 + 1]; r = f[row + x * 3 + 2]; break;
                        default:
                            index = bpp switch
                            {
                                8 => f[row + x],
                                4 => (uint)((f[row + x / 2] >> (x % 2 == 0 ? 4 : 0)) & 0xF),
                                _ => (uint)((f[row + x / 8] >> (7 - x % 8)) & 1),
                            };
                            int p = palette + 4 * (int)Math.Min(index, (uint)paletteCount - 1);
                            b = f[p]; g = f[p + 1]; r = f[p + 2];
                            break;
                    }
                    int o = (y * w + x) * 4;
                    pixels[o] = b; pixels[o + 1] = g; pixels[o + 2] = r; pixels[o + 3] = a;
                    if (bpp != 32 && hasMask && ((f[maskRow + x / 8] >> (7 - x % 8)) & 1) != 0)
                    {
                        // left by the mask: transparent, or where the colour inverts the screen, black
                        bool inverts = (b | g | r) != 0;
                        pixels[o] = pixels[o + 1] = pixels[o + 2] = 0;
                        pixels[o + 3] = inverts ? (byte)255 : (byte)0;
                    }
                }
            }
            // a 32-bit image with no alpha at all is masked as the others are
            if (bpp == 32 && !anyAlpha && hasMask)
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                        pixels[(y * w + x) * 4 + 3] = ((f[and + (h - 1 - y) * andStride + x / 8] >> (7 - x % 8)) & 1) != 0 ? (byte)0 : (byte)255;
            return (w, h, pixels);
        }

        // ---- .ani -------------------------------------------------------------------------------------------

        private static CursorImage FirstFrame(byte[] f, int size)
        {
            for (int at = 12; at + 8 <= f.Length;)
            {
                string id = Id(f, at);
                int length = (int)U32(f, at + 4);
                if (id == "LIST" && at + 12 <= f.Length && Id(f, at + 8) == "fram")
                {
                    for (int c = at + 12; c + 8 <= at + 8 + length && c + 8 <= f.Length;)
                    {
                        int chunk = (int)U32(f, c + 4);
                        if (Id(f, c) == "icon" && c + 8 + chunk <= f.Length)
                            return FromIconFile(f.AsSpan(c + 8, chunk).ToArray(), size);
                        c += 8 + chunk + (chunk & 1);
                    }
                }
                at += 8 + length + (length & 1);
            }
            return null;
        }

        private static string Id(byte[] f, int at) => System.Text.Encoding.ASCII.GetString(f, at, 4);
        private static int U16(byte[] f, int at) => f[at] | f[at + 1] << 8;
        private static uint U32(byte[] f, int at) => (uint)(f[at] | f[at + 1] << 8 | f[at + 2] << 16 | f[at + 3] << 24);
    }
}
