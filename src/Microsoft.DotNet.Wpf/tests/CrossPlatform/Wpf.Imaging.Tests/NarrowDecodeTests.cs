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

        // ---- GIF ---------------------------------------------------------------------------

        /// <summary>
        /// GIF LZW, emitting every pixel as a literal and a clear code often enough that the code
        /// width never has to grow. With a 256-entry table the codes are 9 bits and the dictionary
        /// has 253 free slots after a clear, so clearing every 200 pixels keeps it there. Larger
        /// than a real encoder would produce and perfectly valid, which is all a fixture needs.
        /// </summary>
        private static byte[] GifLzw(byte[] indices)
        {
            const int MinCodeSize = 8, Clear = 256, End = 257, CodeBits = 9;
            var bits = new List<bool>();
            void Emit(int code)
            {
                for (int i = 0; i < CodeBits; i++) bits.Add(((code >> i) & 1) != 0);   // LSB first
            }

            Emit(Clear);
            for (int i = 0; i < indices.Length; i++)
            {
                if (i > 0 && i % 200 == 0) Emit(Clear);
                Emit(indices[i]);
            }
            Emit(End);

            var packed = new List<byte>();
            for (int i = 0; i < bits.Count; i += 8)
            {
                int b = 0;
                for (int j = 0; j < 8 && i + j < bits.Count; j++)
                    if (bits[i + j]) b |= 1 << j;
                packed.Add((byte)b);
            }

            // Sub-blocks: a length byte then up to 255 bytes, terminated by a zero length.
            var body = new List<byte> { MinCodeSize };
            for (int off = 0; off < packed.Count; off += 255)
            {
                int n = Math.Min(255, packed.Count - off);
                body.Add((byte)n);
                body.AddRange(packed.GetRange(off, n));
            }
            body.Add(0);
            return body.ToArray();
        }

        /// <summary>A GIF with a 256-entry global table and one image block per entry in images.</summary>
        private static byte[] BuildGif(int width, int height, Color[] table,
            params (int Left, int Top, int W, int H, byte[] Indices)[] images)
        {
            var gif = new List<byte>();
            gif.AddRange(new byte[] { (byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a' });
            gif.Add((byte)(width & 0xFF)); gif.Add((byte)(width >> 8));
            gif.Add((byte)(height & 0xFF)); gif.Add((byte)(height >> 8));
            gif.Add(0xF7);      // global table present, 256 entries
            gif.Add(0); gif.Add(0);
            for (int i = 0; i < 256; i++)
            {
                Color c = i < table.Length ? table[i] : Colors.Black;
                gif.Add(c.R); gif.Add(c.G); gif.Add(c.B);
            }

            foreach ((int left, int top, int w, int h, byte[] indices) in images)
            {
                gif.Add(0x2C);
                gif.Add((byte)(left & 0xFF)); gif.Add((byte)(left >> 8));
                gif.Add((byte)(top & 0xFF)); gif.Add((byte)(top >> 8));
                gif.Add((byte)(w & 0xFF)); gif.Add((byte)(w >> 8));
                gif.Add((byte)(h & 0xFF)); gif.Add((byte)(h >> 8));
                gif.Add(0);     // no local table, not interlaced
                gif.AddRange(GifLzw(indices));
            }

            gif.Add(0x3B);      // trailer
            return gif.ToArray();
        }

        [Fact]
        public void AStaticGifDecodesAsIndexed8WithItsPalette()
        {
            var table = new Color[256];
            for (int i = 0; i < 256; i++) table[i] = Color.FromRgb((byte)i, (byte)(i / 2), 64);

            const int W = 5, H = 3;
            var indices = new byte[W * H];
            for (int i = 0; i < indices.Length; i++) indices[i] = (byte)(i * 7);

            BitmapFrame frame = Decode(BuildGif(W, H, table, (0, 0, W, H, indices)));

            Assert.Equal(PixelFormats.Indexed8, frame.Format);
            Assert.NotNull(frame.Palette);
            Assert.Equal(256, frame.Palette.Colors.Count);

            var got = new byte[W * H];
            frame.CopyPixels(got, W, 0);
            Assert.Equal(indices, got);
        }

        /// <summary>
        /// An ANIMATION must not take the indexed path. Its frames are composed one over another
        /// with transparency and may carry different local palettes, so the composed canvas is not
        /// any single palette's image -- reporting it as Indexed8 would be a lie about pixels that
        /// no longer correspond to entries in any one table.
        /// </summary>
        [Fact]
        public void AnAnimatedGifStays32Bpp()
        {
            var table = new Color[256];
            for (int i = 0; i < 256; i++) table[i] = Color.FromRgb((byte)i, 0, 0);

            const int W = 4, H = 2;
            var first = new byte[W * H];
            var second = new byte[W * H];
            for (int i = 0; i < first.Length; i++) { first[i] = 10; second[i] = 200; }

            byte[] gif = BuildGif(W, H, table, (0, 0, W, H, first), (0, 0, W, H, second));

            using var stream = new MemoryStream(gif);
            BitmapDecoder decoder = BitmapDecoder.Create(
                stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);

            Assert.Equal(2, decoder.Frames.Count);
            Assert.Equal(PixelFormats.Bgra32, decoder.Frames[0].Format);
        }

        /// <summary>
        /// A single image that does not cover the logical screen is a partial update, not the
        /// picture, so it keeps the composed 32bpp canvas as well.
        /// </summary>
        [Fact]
        public void AGifWhoseImageIsSmallerThanTheScreenStays32Bpp()
        {
            var table = new Color[256];
            for (int i = 0; i < 256; i++) table[i] = Color.FromRgb((byte)i, 0, 0);

            var indices = new byte[2 * 2];
            BitmapFrame frame = Decode(BuildGif(6, 4, table, (1, 1, 2, 2, indices)));

            Assert.Equal(PixelFormats.Bgra32, frame.Format);
        }

        // ---- encoding ------------------------------------------------------------------------

        /// <summary>
        /// Save a narrow bitmap and it comes back the same FORMAT, not just the same pixels. Widening
        /// everything to RGBA8 on the way out meant loading a 1-bit PNG and saving it produced a file
        /// thirty-two times the size, and a "lossless" round trip that kept every pixel still lost
        /// what the image was.
        /// </summary>
        [Theory]
        [InlineData(1)]
        [InlineData(4)]
        [InlineData(8)]
        public void AnIndexedBitmapSavesAsAnIndexedPng(int bpp)
        {
            var colors = new List<Color>();
            for (int i = 0; i < (1 << bpp); i++)
                colors.Add(Color.FromRgb((byte)(i * 7), (byte)(255 - i * 5), (byte)(i * 3)));
            var palette = new BitmapPalette(colors);

            PixelFormat format = bpp switch
            {
                1 => PixelFormats.Indexed1,
                4 => PixelFormats.Indexed4,
                _ => PixelFormats.Indexed8,
            };

            const int W = 9, H = 3;                  // odd width: the last byte of a row is partial
            int stride = (W * bpp + 7) / 8;
            var packed = new byte[stride * H];
            new Random(7).NextBytes(packed);

            BitmapSource src = BitmapSource.Create(W, H, 96, 96, format, palette, packed, stride);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(src));
            using var saved = new MemoryStream();
            encoder.Save(saved);

            BitmapFrame back = Decode(saved.ToArray());

            Assert.Equal(format, back.Format);
            Assert.Equal(colors.Count, back.Palette.Colors.Count);

            var got = new byte[stride * H];
            back.CopyPixels(got, stride, 0);
            Assert.Equal(packed, got);
        }

        [Fact]
        public void AGreyBitmapSavesAsAGreyPng()
        {
            const int W = 5, H = 2;
            byte[] grey = { 0, 40, 90, 160, 255, 255, 160, 90, 40, 0 };
            BitmapSource src = BitmapSource.Create(W, H, 96, 96, PixelFormats.Gray8, null, grey, W);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(src));
            using var saved = new MemoryStream();
            encoder.Save(saved);

            BitmapFrame back = Decode(saved.ToArray());

            Assert.Equal(PixelFormats.Gray8, back.Format);
            var got = new byte[W * H];
            back.CopyPixels(got, W, 0);
            Assert.Equal(grey, got);
        }

        /// <summary>
        /// A palette entry that is not opaque has to reach the file, as tRNS, and come back.
        /// </summary>
        [Fact]
        public void APaletteAlphaSurvivesTheRoundTrip()
        {
            var palette = new BitmapPalette(new[]
            {
                Color.FromArgb(0, 10, 20, 30),       // transparent
                Color.FromArgb(255, 200, 100, 50),
            });
            var packed = new byte[] { 0x40 };        // 1bpp: index 0 then index 1

            BitmapSource src = BitmapSource.Create(2, 1, 96, 96, PixelFormats.Indexed1, palette, packed, 1);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(src));
            using var saved = new MemoryStream();
            encoder.Save(saved);

            BitmapFrame back = Decode(saved.ToArray());

            Assert.Equal(PixelFormats.Indexed1, back.Format);
            Assert.Equal(0, back.Palette.Colors[0].A);
            Assert.Equal(255, back.Palette.Colors[1].A);
        }

        /// <summary>A 32bpp source must still save as truecolour -- the narrow path is not a catch-all.</summary>
        [Fact]
        public void A32BppBitmapStillSavesAsTruecolour()
        {
            var bgra = new byte[] { 10, 20, 30, 255, 40, 50, 60, 128 };
            BitmapSource src = BitmapSource.Create(2, 1, 96, 96, PixelFormats.Bgra32, null, bgra, 8);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(src));
            using var saved = new MemoryStream();
            encoder.Save(saved);

            BitmapFrame back = Decode(saved.ToArray());

            Assert.Equal(PixelFormats.Bgra32, back.Format);
            var got = new byte[8];
            back.CopyPixels(got, 8, 0);
            Assert.Equal(bgra, got);
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
