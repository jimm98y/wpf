// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// PngReader, and specifically the Adam7 (interlaced) path.
//
// A PNG's interlace flag is chosen by whoever encoded it, and for the PNGs this stack reads that is
// not us: CBDT/CBLC and sbix colour emoji store one whole third-party PNG per glyph. An interlaced
// one used to throw, so a single such glyph took down the whole text run.
//
// The encoder here is deliberately NOT written from the decoder's origin/step tables. It walks the
// canonical 8x8 pass grid from the PNG spec instead, so a transposed or otherwise wrong lattice in
// the decoder shows up as scrambled pixels rather than round-tripping through the same mistake
// twice. The other half of the check is that the same image encoded BOTH ways must decode to the
// same pixels -- interlacing is a transport detail and nothing downstream may be able to tell.
//
// No GPU: this is a pure codec test.
//

using System;
using System.IO;
using System.IO.Compression;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using Xunit;

namespace WgpuInterop.Tests.Protocol
{
    public sealed class PngDecodeTests
    {
        // Adam7, as the spec draws it: the pass (1-based) each pixel of every 8x8 block belongs to.
        private static readonly int[,] PassGrid =
        {
            { 1, 6, 4, 6, 2, 6, 4, 6 },
            { 7, 7, 7, 7, 7, 7, 7, 7 },
            { 5, 6, 5, 6, 5, 6, 5, 6 },
            { 7, 7, 7, 7, 7, 7, 7, 7 },
            { 3, 6, 4, 6, 3, 6, 4, 6 },
            { 7, 7, 7, 7, 7, 7, 7, 7 },
            { 5, 6, 5, 6, 5, 6, 5, 6 },
            { 7, 7, 7, 7, 7, 7, 7, 7 },
        };

        /// <summary>A deterministic, non-repeating pattern: every pixel differs from its neighbours,
        /// so a pass landing at the wrong coordinate cannot coincidentally match.</summary>
        private static byte[] Pattern(int width, int height, int channels)
        {
            var s = new byte[width * height * channels];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                    for (int c = 0; c < channels; c++)
                        s[(y * width + x) * channels + c] = (byte)(x * 37 + y * 11 + c * 71 + 3);
            return s;
        }

        [Fact]
        public void AnInterlacedImageDecodesToTheSamePixelsAsANonInterlacedOne()
        {
            const int W = 9, H = 7;   // odd both ways: several passes come out ragged
            byte[] samples = Pattern(W, H, 4);

            byte[] flat = PngReader.Decode(Encode(samples, W, H, 4, colorType: 6, interlaced: false), out int fw, out int fh);
            byte[] adam7 = PngReader.Decode(Encode(samples, W, H, 4, colorType: 6, interlaced: true), out int iw, out int ih);

            Assert.Equal((W, H), (fw, fh));
            Assert.Equal((W, H), (iw, ih));
            Assert.Equal(samples, flat);    // colour type 6 is a passthrough, so this is the source image
            Assert.Equal(flat, adam7);
        }

        /// <summary>
        /// One channel and a size smaller than the 8x8 interlace block: passes 2, 4 and 6 have no
        /// columns and pass 7 no rows, and an empty pass must consume NO bytes from the stream. Get
        /// that wrong and every later pass reads at the wrong offset.
        /// </summary>
        [Fact]
        public void AnInterlacedImageSmallerThanTheAdam7BlockDecodes()
        {
            const int W = 3, H = 3;
            byte[] samples = Pattern(W, H, 1);

            byte[] rgba = PngReader.Decode(Encode(samples, W, H, 1, colorType: 0, interlaced: true), out int w, out int h);

            Assert.Equal((W, H), (w, h));
            for (int i = 0; i < W * H; i++)
            {
                byte v = samples[i];
                Assert.Equal(new byte[] { v, v, v, 255 }, new[] { rgba[i * 4], rgba[i * 4 + 1], rgba[i * 4 + 2], rgba[i * 4 + 3] });
            }
        }

        /// <summary>A 1x1 image puts its only pixel in pass 1 and leaves the other six empty.</summary>
        [Fact]
        public void AOnePixelInterlacedImageDecodes()
        {
            byte[] samples = { 12, 34, 56, 78 };

            byte[] rgba = PngReader.Decode(Encode(samples, 1, 1, 4, colorType: 6, interlaced: true), out int w, out int h);

            Assert.Equal((1, 1), (w, h));
            Assert.Equal(samples, rgba);
        }

        /// <summary>
        /// The file still says LOUDLY what it cannot do. Interlace method 2 does not exist; silently
        /// treating it as either of the two that do would decode garbage.
        /// </summary>
        [Fact]
        public void AnUnknownInterlaceMethodIsRefused()
        {
            byte[] png = Encode(Pattern(2, 2, 4), 2, 2, 4, colorType: 6, interlaced: false);
            png[8 + 8 + 12] = 2;   // IHDR data starts at 16; interlace is its 13th byte

            InvalidDataException e = Assert.Throws<InvalidDataException>(() => PngReader.Decode(png, out _, out _));
            Assert.Contains("interlace", e.Message, StringComparison.OrdinalIgnoreCase);
        }

        // ---- a minimal PNG encoder, filter 0 on every row ----

        private static byte[] Encode(byte[] samples, int width, int height, int channels, int colorType, bool interlaced)
        {
            using var raw = new MemoryStream();
            if (!interlaced)
            {
                EmitSubImage(raw, samples, width, channels,PixelsOf(width, height));
            }
            else
            {
                for (int pass = 1; pass <= 7; pass++)
                    EmitSubImage(raw, samples, width, channels,PixelsOfPass(width, height, pass));
            }

            using var png = new MemoryStream();
            png.Write(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A });
            var ihdr = new byte[13];
            WriteBE(ihdr, 0, width);
            WriteBE(ihdr, 4, height);
            ihdr[8] = 8;                            // bit depth
            ihdr[9] = (byte)colorType;
            ihdr[12] = (byte)(interlaced ? 1 : 0);
            WriteChunk(png, "IHDR", ihdr);
            WriteChunk(png, "IDAT", Zlib(raw.ToArray()));
            WriteChunk(png, "IEND", Array.Empty<byte>());
            return png.ToArray();
        }

        /// <summary>The (x, y) of every pixel of the whole image, in raster order.</summary>
        private static (int X, int Y)[] PixelsOf(int width, int height)
        {
            var list = new (int, int)[width * height];
            int n = 0;
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                    list[n++] = (x, y);
            return list;
        }

        /// <summary>The pixels belonging to one Adam7 pass, in that pass's own raster order.</summary>
        private static (int X, int Y)[] PixelsOfPass(int width, int height, int pass)
        {
            var list = new System.Collections.Generic.List<(int, int)>();
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                    if (PassGrid[y % 8, x % 8] == pass)
                        list.Add((x, y));
            return list.ToArray();
        }

        /// <summary>
        /// Writes one sub-image's filtered scanlines. The pixels arrive in raster order, so a new row
        /// starts wherever y changes -- which is also how the sub-image's width gets decided, and it
        /// is exactly the ragged-row behaviour the decoder has to reproduce from origin and step.
        /// </summary>
        private static void EmitSubImage(Stream to, byte[] samples, int width, int channels, (int X, int Y)[] pixels)
        {
            int lastY = int.MinValue;
            foreach ((int x, int y) in pixels)
            {
                if (y != lastY) { to.WriteByte(0); lastY = y; }   // filter: none
                to.Write(samples, (y * width + x) * channels, channels);
            }
        }

        private static byte[] Zlib(byte[] data)
        {
            using var outBuf = new MemoryStream();
            outBuf.Write(new byte[] { 0x78, 0x9C });   // deflate, default window
            using (var ds = new DeflateStream(outBuf, CompressionLevel.Optimal, leaveOpen: true))
                ds.Write(data, 0, data.Length);
            outBuf.Write(Adler32(data));
            return outBuf.ToArray();
        }

        private static byte[] Adler32(byte[] data)
        {
            uint a = 1, b = 0;
            foreach (byte v in data) { a = (a + v) % 65521; b = (b + a) % 65521; }
            var s = new byte[4];
            WriteBE(s, 0, (int)((b << 16) | a));
            return s;
        }

        private static void WriteChunk(Stream s, string type, byte[] data)
        {
            var len = new byte[4];
            WriteBE(len, 0, data.Length);
            s.Write(len);
            byte[] typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
            s.Write(typeBytes);
            s.Write(data);
            var crc = new byte[4];
            WriteBE(crc, 0, (int)Crc32(typeBytes, data));
            s.Write(crc);
        }

        private static uint Crc32(byte[] type, byte[] data)
        {
            uint c = 0xFFFFFFFF;
            foreach (byte b in type) c = Step(c, b);
            foreach (byte b in data) c = Step(c, b);
            return c ^ 0xFFFFFFFF;

            static uint Step(uint c, byte b)
            {
                c ^= b;
                for (int i = 0; i < 8; i++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
                return c;
            }
        }

        private static void WriteBE(byte[] b, int i, int v)
        {
            b[i] = (byte)(v >> 24); b[i + 1] = (byte)(v >> 16); b[i + 2] = (byte)(v >> 8); b[i + 3] = (byte)v;
        }
    }
}
