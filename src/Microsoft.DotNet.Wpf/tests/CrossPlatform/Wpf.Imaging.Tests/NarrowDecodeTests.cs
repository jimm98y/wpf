// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A PNG that is not 32 bits per pixel decodes as what it actually is.
//
// The managed decoder expanded everything to Bgra32 on the way out. The pixels were right, so
// nothing looked wrong -- but Format said Bgra32 for a file that is Indexed4, Palette came back
// null for a file that has a PLTE chunk, and a 1bpp image cost thirty-two times the memory it
// needed. An application that inspects a loaded image (an editor, a converter, anything that
// re-saves) was told something untrue about it.
//
// The fixtures are built here rather than checked in as files. A palette PNG is a few hundred bytes
// of header, PLTE and one deflate stream, and building it in the test means the bit depth, the
// palette and the expected pixels are all visible next to the assertions instead of hidden inside
// a binary somebody would have to trust.
//

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Xunit;

namespace Wpf.Imaging.Tests
{
    public class NarrowDecodeTests
    {
        // ---- a minimal PNG writer, enough to make the fixtures --------------------------

        private static void BeUInt32(Stream s, uint v)
        {
            s.WriteByte((byte)(v >> 24)); s.WriteByte((byte)(v >> 16));
            s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)v);
        }

        private static uint Crc32(byte[] data)
        {
            uint crc = 0xFFFFFFFF;
            foreach (byte b in data)
            {
                crc ^= b;
                for (int i = 0; i < 8; i++)
                    crc = (crc >> 1) ^ (0xEDB88320 & (uint)(-(crc & 1)));
            }
            return ~crc;
        }

        private static void Chunk(Stream png, string type, byte[] body)
        {
            BeUInt32(png, (uint)body.Length);
            var typed = new byte[4 + body.Length];
            for (int i = 0; i < 4; i++) typed[i] = (byte)type[i];
            Array.Copy(body, 0, typed, 4, body.Length);
            png.Write(typed, 0, typed.Length);
            BeUInt32(png, Crc32(typed));
        }

        /// <summary>Zlib-wrapped deflate of <paramref name="raw"/>, as an IDAT body.</summary>
        private static byte[] ZlibDeflate(byte[] raw)
        {
            using var compressed = new MemoryStream();
            compressed.WriteByte(0x78); compressed.WriteByte(0x01);      // zlib header
            using (var deflate = new DeflateStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            {
                deflate.Write(raw, 0, raw.Length);
            }
            // Adler-32 of the uncompressed data. The decoder stops at the end of the deflate stream
            // and never reads this, but a fixture that is not a real PNG is a poor fixture.
            uint a = 1, b = 0;
            foreach (byte v in raw) { a = (a + v) % 65521; b = (b + a) % 65521; }
            BeUInt32(compressed, (b << 16) | a);
            return compressed.ToArray();
        }

        /// <summary>
        /// A PNG whose rows are <paramref name="packedRows"/> already in the file's own packing --
        /// every scanline written with filter 0, so the bytes reach the decoder untouched.
        /// </summary>
        private static byte[] BuildPng(int width, int height, byte colorType, byte bitDepth,
            byte[] packedRows, int stride, byte[] plte = null, byte[] trns = null)
        {
            using var png = new MemoryStream();
            png.Write(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A }, 0, 8);

            using (var ihdr = new MemoryStream())
            {
                BeUInt32(ihdr, (uint)width);
                BeUInt32(ihdr, (uint)height);
                ihdr.WriteByte(bitDepth);
                ihdr.WriteByte(colorType);
                ihdr.WriteByte(0);      // deflate
                ihdr.WriteByte(0);      // adaptive filtering
                ihdr.WriteByte(0);      // no interlace
                Chunk(png, "IHDR", ihdr.ToArray());
            }

            if (plte != null) Chunk(png, "PLTE", plte);
            if (trns != null) Chunk(png, "tRNS", trns);

            var filtered = new byte[(stride + 1) * height];
            for (int y = 0; y < height; y++)
            {
                filtered[y * (stride + 1)] = 0;     // filter: None
                Array.Copy(packedRows, y * stride, filtered, y * (stride + 1) + 1, stride);
            }
            Chunk(png, "IDAT", ZlibDeflate(filtered));
            Chunk(png, "IEND", Array.Empty<byte>());
            return png.ToArray();
        }

        private static BitmapFrame Decode(byte[] png)
            => BitmapFrame.Create(new MemoryStream(png), BitmapCreateOptions.PreservePixelFormat,
                                  BitmapCacheOption.OnLoad);

        // ---- the assertions ---------------------------------------------------------------

        /// <summary>
        /// A 4-bit palette PNG. Format, Palette and the packed bytes all have to survive: this is
        /// the file shape that most screenshots, diagrams and icons are saved as.
        /// </summary>
        [Fact]
        public void APalettePngDecodesAsIndexed4WithItsPalette()
        {
            var plte = new byte[]
            {
                0x00, 0x00, 0x00,  0xFF, 0x00, 0x00,
                0x00, 0xFF, 0x00,  0x00, 0x00, 0xFF,
            };
            const int W = 6, H = 2;
            int stride = (W * 4 + 7) / 8;           // 3
            byte[] indices = { 0, 1, 2, 3, 1, 2 };
            var rows = new byte[stride * H];
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                    rows[y * stride + x / 2] |= (byte)(indices[x] << ((x & 1) == 0 ? 4 : 0));

            BitmapFrame frame = Decode(BuildPng(W, H, colorType: 3, bitDepth: 4, rows, stride, plte));

            Assert.Equal(PixelFormats.Indexed4, frame.Format);
            Assert.NotNull(frame.Palette);
            Assert.Equal(4, frame.Palette.Colors.Count);
            Assert.Equal(Color.FromRgb(0xFF, 0, 0), frame.Palette.Colors[1]);

            var got = new byte[stride * H];
            frame.CopyPixels(got, stride, 0);
            Assert.Equal(rows, got);
        }

        /// <summary>tRNS on a palette image is a per-entry alpha table, and belongs in the palette.</summary>
        [Fact]
        public void APalettePngKeepsItsPerEntryAlpha()
        {
            var plte = new byte[] { 0x10, 0x20, 0x30, 0x40, 0x50, 0x60 };
            var trns = new byte[] { 0x00 };          // entry 0 transparent, entry 1 opaque by omission
            var rows = new byte[] { 0x40 };          // 1bpp: pixel 0 = index 0, pixel 1 = index 1

            BitmapFrame frame = Decode(BuildPng(2, 1, colorType: 3, bitDepth: 1, rows, 1, plte, trns));

            Assert.Equal(PixelFormats.Indexed1, frame.Format);
            Assert.Equal(0, frame.Palette.Colors[0].A);
            Assert.Equal(255, frame.Palette.Colors[1].A);
        }

        [Fact]
        public void AOneBitGreyPngDecodesAsBlackWhite()
        {
            var rows = new byte[] { 0b10110000 };    // 8 pixels
            BitmapFrame frame = Decode(BuildPng(8, 1, colorType: 0, bitDepth: 1, rows, 1));

            Assert.Equal(PixelFormats.BlackWhite, frame.Format);

            var got = new byte[1];
            frame.CopyPixels(got, 1, 0);
            Assert.Equal(0b10110000, got[0]);
        }

        [Fact]
        public void AnEightBitGreyPngDecodesAsGray8()
        {
            var rows = new byte[] { 0, 64, 128, 255 };
            BitmapFrame frame = Decode(BuildPng(4, 1, colorType: 0, bitDepth: 8, rows, 4));

            Assert.Equal(PixelFormats.Gray8, frame.Format);
            var got = new byte[4];
            frame.CopyPixels(got, 4, 0);
            Assert.Equal(rows, got);
        }

        /// <summary>
        /// A greyscale PNG with a tRNS transparent-colour key must NOT become a Gray format: the
        /// transparency cannot be carried there, and dropping it would quietly turn transparent
        /// pixels opaque. It stays 32bpp, where the alpha survives.
        /// </summary>
        [Fact]
        public void AGreyPngWithATransparentColourStays32Bpp()
        {
            var rows = new byte[] { 0, 128, 255, 64 };
            var trns = new byte[] { 0x00, 0x80 };    // the mid-grey is the transparent colour
            BitmapFrame frame = Decode(BuildPng(4, 1, colorType: 0, bitDepth: 8, rows, 4, null, trns));

            Assert.Equal(PixelFormats.Bgra32, frame.Format);

            var got = new byte[4 * 4];
            frame.CopyPixels(got, 16, 0);
            Assert.Equal(0, got[1 * 4 + 3]);         // the keyed pixel is transparent
            Assert.Equal(255, got[0 * 4 + 3]);
        }

        /// <summary>
        /// A truecolour PNG has no narrow format to fall back to and keeps the expanded path. This
        /// is the guard that the narrow branch has not swallowed everything.
        /// </summary>
        [Fact]
        public void ATruecolourPngStillDecodesAs32Bpp()
        {
            var rows = new byte[] { 10, 20, 30, 200, 210, 220 };   // 2 pixels, RGB8
            BitmapFrame frame = Decode(BuildPng(2, 1, colorType: 2, bitDepth: 8, rows, 6));

            Assert.Equal(PixelFormats.Bgra32, frame.Format);
            var got = new byte[2 * 4];
            frame.CopyPixels(got, 8, 0);
            Assert.Equal(30, got[0]); Assert.Equal(20, got[1]); Assert.Equal(10, got[2]);
        }

        // ---- BMP ---------------------------------------------------------------------------

        /// <summary>
        /// A palettised BMP, built bottom-up with four-byte row padding as the format requires. These
        /// used to be REJECTED outright -- "only uncompressed 24/32-bit BMP is supported" -- so 1, 4
        /// and 8-bit BMPs, which is what icons and a great many old assets are, would not load at all.
        /// </summary>
        private static byte[] BuildBmp(int width, int height, int bpp, byte[] topDownRows, Color[] table)
        {
            int srcStride = ((width * bpp + 7) / 8 + 3) & ~3;
            int packedStride = (width * bpp + 7) / 8;
            int tableBytes = table.Length * 4;
            int pixelOffset = 14 + 40 + tableBytes;
            var bmp = new byte[pixelOffset + srcStride * height];

            bmp[0] = (byte)'B'; bmp[1] = (byte)'M';
            BitConverter.GetBytes(bmp.Length).CopyTo(bmp, 2);
            BitConverter.GetBytes(pixelOffset).CopyTo(bmp, 10);
            BitConverter.GetBytes(40).CopyTo(bmp, 14);           // BITMAPINFOHEADER
            BitConverter.GetBytes(width).CopyTo(bmp, 18);
            BitConverter.GetBytes(height).CopyTo(bmp, 22);       // positive: bottom-up
            BitConverter.GetBytes((short)1).CopyTo(bmp, 26);
            BitConverter.GetBytes((short)bpp).CopyTo(bmp, 28);
            BitConverter.GetBytes(0).CopyTo(bmp, 30);            // BI_RGB
            BitConverter.GetBytes(table.Length).CopyTo(bmp, 46); // biClrUsed

            for (int i = 0; i < table.Length; i++)
            {
                int e = 14 + 40 + i * 4;
                bmp[e] = table[i].B; bmp[e + 1] = table[i].G; bmp[e + 2] = table[i].R;
            }

            // Rows are stored bottom-up.
            for (int y = 0; y < height; y++)
            {
                Array.Copy(topDownRows, y * packedStride,
                           bmp, pixelOffset + (height - 1 - y) * srcStride, packedStride);
            }
            return bmp;
        }

        [Fact]
        public void AFourBitBmpDecodesAsIndexed4RightWayUp()
        {
            var table = new[]
            {
                Color.FromRgb(0, 0, 0), Color.FromRgb(255, 0, 0),
                Color.FromRgb(0, 255, 0), Color.FromRgb(0, 0, 255),
            };
            const int W = 4, H = 2;
            int stride = (W * 4 + 7) / 8;               // 2
            // Two DIFFERENT rows, so a decoder that forgot BMP is stored bottom-up cannot pass.
            byte[] rows = { 0x01, 0x23, 0x32, 0x10 };

            BitmapFrame frame = Decode(BuildBmp(W, H, 4, rows, table));

            Assert.Equal(PixelFormats.Indexed4, frame.Format);
            Assert.Equal(4, frame.Palette.Colors.Count);
            Assert.Equal(Color.FromRgb(0, 255, 0), frame.Palette.Colors[2]);

            var got = new byte[stride * H];
            frame.CopyPixels(got, stride, 0);
            Assert.Equal(rows, got);
        }

        [Fact]
        public void AnEightBitBmpDecodesAsIndexed8()
        {
            var table = new Color[256];
            for (int i = 0; i < 256; i++) table[i] = Color.FromRgb((byte)i, (byte)(255 - i), 0);

            const int W = 3, H = 2;
            byte[] rows = { 0, 1, 2, 200, 201, 202 };   // stride 3 pads to 4 in the file

            BitmapFrame frame = Decode(BuildBmp(W, H, 8, rows, table));

            Assert.Equal(PixelFormats.Indexed8, frame.Format);
            var got = new byte[W * H];
            frame.CopyPixels(got, W, 0);
            Assert.Equal(rows, got);
        }

        /// <summary>A 1-bit BMP, the shape an icon mask takes.</summary>
        [Fact]
        public void AOneBitBmpDecodesAsIndexed1()
        {
            var table = new[] { Color.FromRgb(0, 0, 0), Color.FromRgb(255, 255, 255) };
            byte[] rows = { 0b10100000 };               // 8 pixels, one row

            BitmapFrame frame = Decode(BuildBmp(8, 1, 1, rows, table));

            Assert.Equal(PixelFormats.Indexed1, frame.Format);
            var got = new byte[1];
            frame.CopyPixels(got, 1, 0);
            Assert.Equal(0b10100000, got[0]);
        }

        /// <summary>
        /// And the point of all of it: a narrow frame still renders. The composition path converts
        /// it, so an Indexed4 frame has to arrive at the backend as the colours its palette names.
        /// </summary>
        [Fact]
        public void ANarrowFrameStillConvertsToItsRealColours()
        {
            var plte = new byte[] { 0x00, 0x00, 0x00, 0x12, 0x34, 0x56 };
            var rows = new byte[] { 0x40 };          // 1bpp: index 0 then index 1
            BitmapFrame frame = Decode(BuildPng(2, 1, colorType: 3, bitDepth: 1, rows, 1, plte));

            var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
            var bgra = new byte[2 * 4];
            converted.CopyPixels(bgra, 8, 0);

            Assert.Equal(0, bgra[0]); Assert.Equal(0, bgra[1]); Assert.Equal(0, bgra[2]);
            Assert.Equal(0x56, bgra[4]); Assert.Equal(0x34, bgra[5]); Assert.Equal(0x12, bgra[6]);
        }
    }
}
