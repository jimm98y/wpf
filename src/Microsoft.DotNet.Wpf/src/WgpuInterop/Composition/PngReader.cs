// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// PNG decoding, to straight (non-premultiplied) RGBA8.
//
// It began as the exact inverse of PngWriter -- 8-bit RGB, filter 0 on every row -- because all it
// had to read back was what PngWriter had just written for the render-baseline test. Colour BITMAP
// emoji fonts changed that: CBDT/CBLC and sbix store each glyph as a whole PNG produced by somebody
// else's encoder, and those are RGBA with the full set of row filters. A decoder that only handled
// its own output could not read a single emoji.
//
// So the scope now is: 8-bit, colour types 0/2/3/4/6 (grey, RGB, palette, grey+alpha, RGBA), all
// five row filters, interlaced or not. Inflate comes from the BCL.
//
// Interlacing (Adam7) is the same decode seven times over: each pass is an independently filtered
// sub-image on its own lattice, and the passes are concatenated in one zlib stream with no framing
// between them -- so a pass's byte count has to be computed, not found. Progressive display is not
// the point here; an interlaced file simply has to decode to the same pixels a non-interlaced one
// would, because whoever encoded the emoji PNG inside a font chose the flag, not us.
//
// What is still refused is refused LOUDLY, with a message naming the reason: bit depths other than
// 8. That was the original file's principle and it still holds -- a baseline comparison that
// silently decoded something wrong would fail in the direction that matters, by passing.
//

using System;
using System.IO;
using System.IO.Compression;

namespace Microsoft.Wpf.Interop.WebGpu.Composition
{
    internal static class PngReader
    {
        /// <summary>Reads a PNG file as straight RGBA8.</summary>
        public static byte[] Read(string path, out int width, out int height)
        {
            try
            {
                return Decode(File.ReadAllBytes(path), out width, out height);
            }
            catch (InvalidDataException e)
            {
                // The path is the useful half of the message when a baseline file is the problem.
                throw new InvalidDataException($"{path}: {e.Message}", e);
            }
        }

        /// <summary>Decodes a PNG held in memory as straight RGBA8, row-major, top-down.</summary>
        public static byte[] Decode(ReadOnlySpan<byte> file, out int width, out int height)
        {
            if (file.Length < 8 || file[0] != 0x89 || file[1] != 'P' || file[2] != 'N' || file[3] != 'G')
                throw new InvalidDataException("not a PNG.");

            width = height = 0;
            int bitDepth = 0, colorType = 0, interlace = 0;
            byte[]? palette = null;      // RGB triples
            byte[]? paletteAlpha = null; // tRNS for colour type 3
            var idat = new MemoryStream();

            int pos = 8;
            while (pos + 8 <= file.Length)
            {
                int len = BE(file, pos);
                if (len < 0) throw new InvalidDataException("chunk length overflow.");
                string type = System.Text.Encoding.ASCII.GetString(file.Slice(pos + 4, 4));
                int dataAt = pos + 8;
                if (dataAt + len > file.Length) throw new InvalidDataException($"truncated chunk '{type}'.");

                switch (type)
                {
                    case "IHDR":
                        width = BE(file, dataAt);
                        height = BE(file, dataAt + 4);
                        bitDepth = file[dataAt + 8];
                        colorType = file[dataAt + 9];
                        interlace = file[dataAt + 12];
                        if (interlace != 0 && interlace != 1)
                            throw new InvalidDataException($"unknown interlace method {interlace}.");
                        if (bitDepth != 8)
                            throw new InvalidDataException($"only 8-bit channels are supported (got {bitDepth}).");
                        break;

                    case "PLTE":
                        palette = file.Slice(dataAt, len).ToArray();
                        break;

                    case "tRNS":
                        if (colorType == 3) paletteAlpha = file.Slice(dataAt, len).ToArray();
                        break;

                    case "IDAT":
                        idat.Write(file.Slice(dataAt, len));
                        break;

                    case "IEND":
                        pos = file.Length;   // stop
                        continue;
                }

                pos = dataAt + len + 4;   // + CRC
            }

            if (width <= 0 || height <= 0) throw new InvalidDataException("missing IHDR.");

            int channels = colorType switch
            {
                0 => 1,   // grey
                2 => 3,   // RGB
                3 => 1,   // palette index
                4 => 2,   // grey + alpha
                6 => 4,   // RGBA
                _ => throw new InvalidDataException($"unsupported colour type {colorType}."),
            };
            if (colorType == 3 && palette is null) throw new InvalidDataException("palette image with no PLTE.");

            byte[] raw = Inflate(idat.ToArray());
            byte[] samples = interlace == 1
                ? Deinterlace(raw, width, height, channels)
                : Unfilter(raw, 0, width, height, channels);

            var rgba = new byte[width * height * 4];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int s = (y * width + x) * channels, d = (y * width + x) * 4;
                    switch (colorType)
                    {
                        case 0:
                            rgba[d] = rgba[d + 1] = rgba[d + 2] = samples[s];
                            rgba[d + 3] = 255;
                            break;
                        case 2:
                            rgba[d] = samples[s]; rgba[d + 1] = samples[s + 1]; rgba[d + 2] = samples[s + 2];
                            rgba[d + 3] = 255;
                            break;
                        case 3:
                        {
                            int idx = samples[s], p = idx * 3;
                            if (p + 2 >= palette!.Length) throw new InvalidDataException("palette index out of range.");
                            rgba[d] = palette[p]; rgba[d + 1] = palette[p + 1]; rgba[d + 2] = palette[p + 2];
                            rgba[d + 3] = paletteAlpha != null && idx < paletteAlpha.Length ? paletteAlpha[idx] : (byte)255;
                            break;
                        }
                        case 4:
                            rgba[d] = rgba[d + 1] = rgba[d + 2] = samples[s];
                            rgba[d + 3] = samples[s + 1];
                            break;
                        default:   // 6
                            rgba[d] = samples[s]; rgba[d + 1] = samples[s + 1]; rgba[d + 2] = samples[s + 2]; rgba[d + 3] = samples[s + 3];
                            break;
                    }
                }
            }
            return rgba;
        }

        /// <summary>
        /// Reverses the per-row filters of one (sub-)image starting at <paramref name="offset"/> and
        /// returns its samples packed without the filter bytes. Every filter predicts a byte from its
        /// left (a), the byte above (b) and the byte above-left (c); the stored value is the residual,
        /// so decoding is add-back, and it must run in order because each row's prediction reads the
        /// row above AFTER that row has been reconstructed. Unfiltering happens in place in
        /// <paramref name="raw"/> for exactly that reason; the packed copy is taken row by row after.
        /// </summary>
        private static byte[] Unfilter(byte[] raw, int offset, int width, int height, int channels)
        {
            int rowBytes = width * channels;
            int stride = rowBytes + 1;
            long need = (long)offset + (long)stride * height;
            if (raw.Length < need)
                throw new InvalidDataException($"image data short ({raw.Length} < {need}).");

            var samples = new byte[rowBytes * height];
            for (int y = 0; y < height; y++)
            {
                int row = offset + y * stride;
                int filter = raw[row];
                int line = row + 1;
                int prev = line - stride;   // same x on the previous row

                for (int i = 0; i < rowBytes; i++)
                {
                    int a = i >= channels ? raw[line + i - channels] : 0;
                    int b = y > 0 ? raw[prev + i] : 0;
                    int c = (y > 0 && i >= channels) ? raw[prev + i - channels] : 0;

                    int add = filter switch
                    {
                        0 => 0,
                        1 => a,
                        2 => b,
                        3 => (a + b) >> 1,
                        4 => Paeth(a, b, c),
                        _ => throw new InvalidDataException($"row {y} uses unknown filter {filter}."),
                    };
                    raw[line + i] = (byte)(raw[line + i] + add);
                }

                Array.Copy(raw, line, samples, y * rowBytes, rowBytes);
            }
            return samples;
        }

        // Adam7: seven passes, each sampling the image on a lattice with this origin and step. Pass i
        // carries the pixels at (XOrig[i] + n*XStep[i], YOrig[i] + m*YStep[i]).
        private static readonly int[] XOrig = { 0, 4, 0, 2, 0, 1, 0 };
        private static readonly int[] YOrig = { 0, 0, 4, 0, 2, 0, 1 };
        private static readonly int[] XStep = { 8, 8, 4, 4, 2, 2, 1 };
        private static readonly int[] YStep = { 8, 8, 8, 4, 4, 2, 2 };

        /// <summary>
        /// Reassembles the seven Adam7 passes into one top-down sample buffer. Each pass is filtered
        /// independently and against ITS OWN width, so the filters have to be reversed per pass and
        /// the pixels scattered afterwards -- reversing them over the whole stream would predict every
        /// byte from the wrong neighbour. Nothing frames the passes in the zlib stream either, so the
        /// next pass starts exactly where this one's (rowBytes + 1) * rows ended, and a pass with no
        /// rows or no columns (small images have several) contributes no bytes at all.
        /// </summary>
        private static byte[] Deinterlace(byte[] raw, int width, int height, int channels)
        {
            var samples = new byte[width * height * channels];
            int offset = 0;

            for (int i = 0; i < 7; i++)
            {
                if (width <= XOrig[i] || height <= YOrig[i]) continue;
                int pw = (width - XOrig[i] + XStep[i] - 1) / XStep[i];
                int ph = (height - YOrig[i] + YStep[i] - 1) / YStep[i];

                byte[] pass = Unfilter(raw, offset, pw, ph, channels);
                offset += (pw * channels + 1) * ph;

                for (int y = 0; y < ph; y++)
                {
                    int dy = YOrig[i] + y * YStep[i];
                    for (int x = 0; x < pw; x++)
                    {
                        int dx = XOrig[i] + x * XStep[i];
                        Array.Copy(pass, (y * pw + x) * channels, samples, (dy * width + dx) * channels, channels);
                    }
                }
            }
            return samples;
        }

        // The PNG Paeth predictor: whichever of left/above/above-left is closest to a + b - c.
        private static int Paeth(int a, int b, int c)
        {
            int p = a + b - c;
            int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
            if (pa <= pb && pa <= pc) return a;
            return pb <= pc ? b : c;
        }

        private static byte[] Inflate(byte[] z)
        {
            if (z.Length < 6) throw new InvalidDataException("empty IDAT.");
            if ((z[0] & 0x0F) != 8) throw new InvalidDataException("not zlib deflate.");
            // Skip the 2-byte zlib header and the trailing 4-byte Adler-32.
            using var src = new MemoryStream(z, 2, z.Length - 6);
            using var ds = new DeflateStream(src, CompressionMode.Decompress);
            using var outBuf = new MemoryStream();
            ds.CopyTo(outBuf);
            return outBuf.ToArray();
        }

        private static int BE(ReadOnlySpan<byte> b, int i)
            => (b[i] << 24) | (b[i + 1] << 16) | (b[i + 2] << 8) | b[i + 3];
    }
}
