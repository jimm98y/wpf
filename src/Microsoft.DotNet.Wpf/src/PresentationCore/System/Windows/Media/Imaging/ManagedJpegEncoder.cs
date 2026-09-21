// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Managed JPEG encoder for platforms without native WIC -- the counterpart to ManagedJpegDecoder, and
// the JPEG twin of ManagedPngEncoder.
//
// Baseline sequential DCT (SOF0), YCbCr with 4:2:0 chroma subsampling, and the standard quantization
// and Huffman tables from ITU T.81 Annex K. Those tables are what essentially every JPEG in the world
// uses, so writing them verbatim keeps this decodable by anything and keeps the code to the parts that
// actually have to be computed.
//
// Two properties worth naming because they are choices, not accidents:
//
//   * ALPHA IS DROPPED. JPEG has no alpha channel. The colour channels are written as-is, which is what
//     WIC's JpegBitmapEncoder does too -- a semi-transparent pixel keeps its colour rather than being
//     composited onto an assumed background, because guessing white would be wrong on a dark page.
//   * QUALITY SCALES THE TABLES the way libjpeg does (the 5000/q .. 200-2q curve), so a given quality
//     number here means roughly what it means everywhere else.
//

using System.IO;

namespace System.Windows.Media.Imaging
{
    internal static class ManagedJpegEncoder
    {
        internal static void Save(BitmapSource source, Stream stream, int quality)
        {
            byte[] bgra = source.CopyPixelsForManagedComposition(out int width, out int height, out int stride);
            if (bgra == null || width <= 0 || height <= 0)
            {
                throw new InvalidOperationException("The bitmap has no pixels to encode.");
            }

            byte[] luminanceQuant = ScaleTable(s_luminanceQuant, quality);
            byte[] chrominanceQuant = ScaleTable(s_chrominanceQuant, quality);

            WriteMarkers(stream, width, height, source.DpiX, source.DpiY, luminanceQuant, chrominanceQuant);
            WriteScan(stream, bgra, width, height, stride, luminanceQuant, chrominanceQuant);

            stream.WriteByte(0xFF);
            stream.WriteByte(0xD9);     // EOI
        }

        // ------------------------------------------------------------------ headers ----

        private static void WriteMarkers(Stream stream, int width, int height, double dpiX, double dpiY,
                                         byte[] luminanceQuant, byte[] chrominanceQuant)
        {
            stream.WriteByte(0xFF); stream.WriteByte(0xD8);     // SOI

            // APP0 / JFIF, carrying the DPI so the image round-trips its physical size.
            ushort xDensity = (ushort)Math.Clamp((int)Math.Round(dpiX <= 0 ? 96 : dpiX), 1, ushort.MaxValue);
            ushort yDensity = (ushort)Math.Clamp((int)Math.Round(dpiY <= 0 ? 96 : dpiY), 1, ushort.MaxValue);
            WriteSegment(stream, 0xE0, new byte[]
            {
                (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0,
                1, 1,                                   // version 1.1
                1,                                      // units: dots per inch
                (byte)(xDensity >> 8), (byte)xDensity,
                (byte)(yDensity >> 8), (byte)yDensity,
                0, 0,                                   // no thumbnail
            });

            // DQT: both tables, each written in zigzag order as the format requires.
            var dqt = new byte[2 * (1 + 64)];
            dqt[0] = 0x00;                              // 8-bit precision, table id 0
            for (int i = 0; i < 64; i++) dqt[1 + i] = luminanceQuant[s_zigZag[i]];
            dqt[65] = 0x01;                             // table id 1
            for (int i = 0; i < 64; i++) dqt[66 + i] = chrominanceQuant[s_zigZag[i]];
            WriteSegment(stream, 0xDB, dqt);

            // SOF0: 8-bit, three components. Y is sampled 2x2 against the chroma planes' 1x1, which is
            // what makes this 4:2:0.
            WriteSegment(stream, 0xC0, new byte[]
            {
                8,
                (byte)(height >> 8), (byte)height,
                (byte)(width >> 8), (byte)width,
                3,
                1, 0x22, 0,                             // Y  : 2x2 sampling, quant table 0
                2, 0x11, 1,                             // Cb : 1x1 sampling, quant table 1
                3, 0x11, 1,                             // Cr : 1x1 sampling, quant table 1
            });

            WriteHuffmanTable(stream, 0x00, s_dcLuminanceBits, s_dcLuminanceValues);
            WriteHuffmanTable(stream, 0x10, s_acLuminanceBits, s_acLuminanceValues);
            WriteHuffmanTable(stream, 0x01, s_dcChrominanceBits, s_dcChrominanceValues);
            WriteHuffmanTable(stream, 0x11, s_acChrominanceBits, s_acChrominanceValues);

            WriteSegment(stream, 0xDA, new byte[]
            {
                3,
                1, 0x00,                                // Y  : DC table 0, AC table 0
                2, 0x11,                                // Cb : DC table 1, AC table 1
                3, 0x11,                                // Cr : DC table 1, AC table 1
                0, 63, 0,                               // Ss, Se, Ah/Al -- baseline sequential
            });
        }

        private static void WriteSegment(Stream stream, byte marker, byte[] payload)
        {
            stream.WriteByte(0xFF);
            stream.WriteByte(marker);
            int length = payload.Length + 2;            // the length field counts itself
            stream.WriteByte((byte)(length >> 8));
            stream.WriteByte((byte)length);
            stream.Write(payload, 0, payload.Length);
        }

        private static void WriteHuffmanTable(Stream stream, byte id, byte[] bits, byte[] values)
        {
            var payload = new byte[1 + 16 + values.Length];
            payload[0] = id;
            Array.Copy(bits, 0, payload, 1, 16);
            Array.Copy(values, 0, payload, 17, values.Length);
            WriteSegment(stream, 0xC4, payload);
        }

        /// <summary>
        /// Scale a base quantization table for a quality level, using libjpeg's curve so that a quality
        /// number means the same thing here as everywhere else.
        /// </summary>
        private static byte[] ScaleTable(byte[] baseTable, int quality)
        {
            quality = Math.Clamp(quality, 1, 100);
            int scale = quality < 50 ? 5000 / quality : 200 - quality * 2;

            var scaled = new byte[64];
            for (int i = 0; i < 64; i++)
            {
                int value = (baseTable[i] * scale + 50) / 100;
                scaled[i] = (byte)Math.Clamp(value, 1, 255);
            }
            return scaled;
        }

        // --------------------------------------------------------------------- scan ----

        private static void WriteScan(Stream stream, byte[] bgra, int width, int height, int stride,
                                      byte[] luminanceQuant, byte[] chrominanceQuant)
        {
            var writer = new BitWriter(stream);
            int previousDcY = 0, previousDcCb = 0, previousDcCr = 0;

            // 4:2:0 means one MCU covers 16x16 pixels: four luma blocks and one of each chroma.
            int mcusX = (width + 15) / 16;
            int mcusY = (height + 15) / 16;

            var block = new double[64];
            var coefficients = new int[64];

            for (int mcuY = 0; mcuY < mcusY; mcuY++)
            {
                for (int mcuX = 0; mcuX < mcusX; mcuX++)
                {
                    int originX = mcuX * 16;
                    int originY = mcuY * 16;

                    for (int subY = 0; subY < 2; subY++)
                    {
                        for (int subX = 0; subX < 2; subX++)
                        {
                            ExtractLuma(bgra, width, height, stride, originX + subX * 8, originY + subY * 8, block);
                            Encode(writer, block, luminanceQuant, coefficients, ref previousDcY,
                                   s_dcLuminanceCodes, s_acLuminanceCodes);
                        }
                    }

                    ExtractChroma(bgra, width, height, stride, originX, originY, blue: true, block);
                    Encode(writer, block, chrominanceQuant, coefficients, ref previousDcCb,
                           s_dcChrominanceCodes, s_acChrominanceCodes);

                    ExtractChroma(bgra, width, height, stride, originX, originY, blue: false, block);
                    Encode(writer, block, chrominanceQuant, coefficients, ref previousDcCr,
                           s_dcChrominanceCodes, s_acChrominanceCodes);
                }
            }

            writer.Flush();
        }

        /// <summary>
        /// Read one 8x8 luma block, level-shifted by -128 ready for the DCT. Coordinates past the edge
        /// clamp to the last real pixel: the image is padded up to whole MCUs, and repeating the edge
        /// keeps the padding from generating high-frequency energy that would cost bits and ring.
        /// </summary>
        private static void ExtractLuma(byte[] bgra, int width, int height, int stride, int x0, int y0, double[] block)
        {
            for (int y = 0; y < 8; y++)
            {
                int sy = Math.Min(y0 + y, height - 1);
                for (int x = 0; x < 8; x++)
                {
                    int sx = Math.Min(x0 + x, width - 1);
                    int i = sy * stride + sx * 4;
                    block[y * 8 + x] = Luma(bgra[i + 2], bgra[i + 1], bgra[i]) - 128.0;
                }
            }
        }

        /// <summary>
        /// Read one 8x8 chroma block for a 16x16 MCU, box-averaging each 2x2 group of source pixels --
        /// the downsampling half of 4:2:0.
        /// </summary>
        private static void ExtractChroma(byte[] bgra, int width, int height, int stride, int x0, int y0,
                                          bool blue, double[] block)
        {
            for (int y = 0; y < 8; y++)
            {
                for (int x = 0; x < 8; x++)
                {
                    double sum = 0;
                    for (int dy = 0; dy < 2; dy++)
                    {
                        int sy = Math.Min(y0 + y * 2 + dy, height - 1);
                        for (int dx = 0; dx < 2; dx++)
                        {
                            int sx = Math.Min(x0 + x * 2 + dx, width - 1);
                            int i = sy * stride + sx * 4;
                            byte r = bgra[i + 2], g = bgra[i + 1], b = bgra[i];
                            sum += blue ? Cb(r, g, b) : Cr(r, g, b);
                        }
                    }
                    block[y * 8 + x] = sum / 4.0 - 128.0;
                }
            }
        }

        // JFIF (ITU-R BT.601) RGB -> YCbCr.
        private static double Luma(byte r, byte g, byte b) => 0.299 * r + 0.587 * g + 0.114 * b;
        private static double Cb(byte r, byte g, byte b) => 128.0 - 0.168736 * r - 0.331264 * g + 0.5 * b;
        private static double Cr(byte r, byte g, byte b) => 128.0 + 0.5 * r - 0.418688 * g - 0.081312 * b;

        private static void Encode(BitWriter writer, double[] block, byte[] quant, int[] coefficients,
                                   ref int previousDc, uint[][] dcCodes, uint[][] acCodes)
        {
            ForwardDct(block);

            for (int i = 0; i < 64; i++)
            {
                coefficients[i] = (int)Math.Round(block[i] / quant[i]);
            }

            // DC is coded as the difference from the previous block of the same component.
            int dc = coefficients[0];
            int diff = dc - previousDc;
            previousDc = dc;

            int category = Category(diff);
            writer.Write(dcCodes[category]);
            if (category > 0) writer.WriteBits(Magnitude(diff, category), category);

            // AC coefficients in zigzag order, run-length coded over zeros.
            int runOfZeros = 0;
            for (int i = 1; i < 64; i++)
            {
                int value = coefficients[s_zigZag[i]];
                if (value == 0)
                {
                    runOfZeros++;
                    continue;
                }

                // A run longer than 15 needs explicit ZRL (16 zeros) symbols first.
                while (runOfZeros > 15)
                {
                    writer.Write(acCodes[0xF0]);
                    runOfZeros -= 16;
                }

                category = Category(value);
                writer.Write(acCodes[(runOfZeros << 4) | category]);
                writer.WriteBits(Magnitude(value, category), category);
                runOfZeros = 0;
            }

            if (runOfZeros > 0) writer.Write(acCodes[0x00]);     // EOB
        }

        /// <summary>Bits needed to carry a coefficient: JPEG's "category" or SSSS value.</summary>
        private static int Category(int value)
        {
            int magnitude = Math.Abs(value);
            int bits = 0;
            while (magnitude > 0) { bits++; magnitude >>= 1; }
            return bits;
        }

        /// <summary>
        /// The value bits for a coefficient. Negatives are stored as one-less-than the magnitude's
        /// complement, which is what makes the code self-terminating: a leading 0 bit means negative.
        /// </summary>
        private static uint Magnitude(int value, int category) =>
            (uint)(value > 0 ? value : value + (1 << category) - 1) & (uint)((1 << category) - 1);

        /// <summary>
        /// Separable 8x8 forward DCT-II, rows then columns, in place. Straightforward rather than one of
        /// the fast integer approximations: this runs once per block on a save, not per frame.
        /// </summary>
        private static void ForwardDct(double[] block)
        {
            Span<double> temp = stackalloc double[64];

            for (int y = 0; y < 8; y++)
            {
                for (int u = 0; u < 8; u++)
                {
                    double sum = 0;
                    for (int x = 0; x < 8; x++) sum += block[y * 8 + x] * s_cosine[x * 8 + u];
                    temp[y * 8 + u] = sum * (u == 0 ? s_invSqrt2 : 1.0) * 0.5;
                }
            }

            for (int u = 0; u < 8; u++)
            {
                for (int v = 0; v < 8; v++)
                {
                    double sum = 0;
                    for (int y = 0; y < 8; y++) sum += temp[y * 8 + u] * s_cosine[y * 8 + v];
                    block[v * 8 + u] = sum * (v == 0 ? s_invSqrt2 : 1.0) * 0.5;
                }
            }
        }

        private static readonly double s_invSqrt2 = 1.0 / Math.Sqrt(2.0);

        /// <summary>cos((2x+1) * u * pi / 16), indexed [x*8 + u].</summary>
        private static readonly double[] s_cosine = BuildCosineTable();

        private static double[] BuildCosineTable()
        {
            var table = new double[64];
            for (int x = 0; x < 8; x++)
            {
                for (int u = 0; u < 8; u++)
                {
                    table[x * 8 + u] = Math.Cos((2 * x + 1) * u * Math.PI / 16.0);
                }
            }
            return table;
        }

        // ---------------------------------------------------------------- bit writer ----

        /// <summary>
        /// MSB-first bit writer with JPEG's byte stuffing: a literal 0xFF in the entropy-coded data must
        /// be followed by 0x00, or a decoder would read it as the start of a marker.
        /// </summary>
        private sealed class BitWriter
        {
            private readonly Stream _stream;
            private uint _buffer;
            private int _count;

            internal BitWriter(Stream stream) => _stream = stream;

            internal void Write(uint[] code) => WriteBits(code[0], (int)code[1]);

            internal void WriteBits(uint bits, int length)
            {
                for (int i = length - 1; i >= 0; i--)
                {
                    _buffer = (_buffer << 1) | ((bits >> i) & 1);
                    if (++_count != 8) continue;

                    byte value = (byte)_buffer;
                    _stream.WriteByte(value);
                    if (value == 0xFF) _stream.WriteByte(0x00);
                    _buffer = 0;
                    _count = 0;
                }
            }

            /// <summary>Pad the final partial byte with 1-bits, as T.81 requires.</summary>
            internal void Flush()
            {
                while (_count != 0) WriteBits(1, 1);
            }
        }

        // ------------------------------------------------------------ standard tables ----

        private static readonly int[] s_zigZag =
        {
             0,  1,  8, 16,  9,  2,  3, 10,
            17, 24, 32, 25, 18, 11,  4,  5,
            12, 19, 26, 33, 40, 48, 41, 34,
            27, 20, 13,  6,  7, 14, 21, 28,
            35, 42, 49, 56, 57, 50, 43, 36,
            29, 22, 15, 23, 30, 37, 44, 51,
            58, 59, 52, 45, 38, 31, 39, 46,
            53, 60, 61, 54, 47, 55, 62, 63,
        };

        private static readonly byte[] s_luminanceQuant =
        {
            16, 11, 10, 16,  24,  40,  51,  61,
            12, 12, 14, 19,  26,  58,  60,  55,
            14, 13, 16, 24,  40,  57,  69,  56,
            14, 17, 22, 29,  51,  87,  80,  62,
            18, 22, 37, 56,  68, 109, 103,  77,
            24, 35, 55, 64,  81, 104, 113,  92,
            49, 64, 78, 87, 103, 121, 120, 101,
            72, 92, 95, 98, 112, 100, 103,  99,
        };

        private static readonly byte[] s_chrominanceQuant =
        {
            17, 18, 24, 47, 99, 99, 99, 99,
            18, 21, 26, 66, 99, 99, 99, 99,
            24, 26, 56, 99, 99, 99, 99, 99,
            47, 66, 99, 99, 99, 99, 99, 99,
            99, 99, 99, 99, 99, 99, 99, 99,
            99, 99, 99, 99, 99, 99, 99, 99,
            99, 99, 99, 99, 99, 99, 99, 99,
            99, 99, 99, 99, 99, 99, 99, 99,
        };

        private static readonly byte[] s_dcLuminanceBits = { 0, 1, 5, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0 };
        private static readonly byte[] s_dcLuminanceValues = { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 };

        private static readonly byte[] s_dcChrominanceBits = { 0, 3, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0 };
        private static readonly byte[] s_dcChrominanceValues = { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 };

        private static readonly byte[] s_acLuminanceBits = { 0, 2, 1, 3, 3, 2, 4, 3, 5, 5, 4, 4, 0, 0, 1, 0x7D };
        private static readonly byte[] s_acLuminanceValues =
        {
            0x01, 0x02, 0x03, 0x00, 0x04, 0x11, 0x05, 0x12,
            0x21, 0x31, 0x41, 0x06, 0x13, 0x51, 0x61, 0x07,
            0x22, 0x71, 0x14, 0x32, 0x81, 0x91, 0xA1, 0x08,
            0x23, 0x42, 0xB1, 0xC1, 0x15, 0x52, 0xD1, 0xF0,
            0x24, 0x33, 0x62, 0x72, 0x82, 0x09, 0x0A, 0x16,
            0x17, 0x18, 0x19, 0x1A, 0x25, 0x26, 0x27, 0x28,
            0x29, 0x2A, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39,
            0x3A, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49,
            0x4A, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59,
            0x5A, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69,
            0x6A, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79,
            0x7A, 0x83, 0x84, 0x85, 0x86, 0x87, 0x88, 0x89,
            0x8A, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98,
            0x99, 0x9A, 0xA2, 0xA3, 0xA4, 0xA5, 0xA6, 0xA7,
            0xA8, 0xA9, 0xAA, 0xB2, 0xB3, 0xB4, 0xB5, 0xB6,
            0xB7, 0xB8, 0xB9, 0xBA, 0xC2, 0xC3, 0xC4, 0xC5,
            0xC6, 0xC7, 0xC8, 0xC9, 0xCA, 0xD2, 0xD3, 0xD4,
            0xD5, 0xD6, 0xD7, 0xD8, 0xD9, 0xDA, 0xE1, 0xE2,
            0xE3, 0xE4, 0xE5, 0xE6, 0xE7, 0xE8, 0xE9, 0xEA,
            0xF1, 0xF2, 0xF3, 0xF4, 0xF5, 0xF6, 0xF7, 0xF8,
            0xF9, 0xFA,
        };

        private static readonly byte[] s_acChrominanceBits = { 0, 2, 1, 2, 4, 4, 3, 4, 7, 5, 4, 4, 0, 1, 2, 0x77 };
        private static readonly byte[] s_acChrominanceValues =
        {
            0x00, 0x01, 0x02, 0x03, 0x11, 0x04, 0x05, 0x21,
            0x31, 0x06, 0x12, 0x41, 0x51, 0x07, 0x61, 0x71,
            0x13, 0x22, 0x32, 0x81, 0x08, 0x14, 0x42, 0x91,
            0xA1, 0xB1, 0xC1, 0x09, 0x23, 0x33, 0x52, 0xF0,
            0x15, 0x62, 0x72, 0xD1, 0x0A, 0x16, 0x24, 0x34,
            0xE1, 0x25, 0xF1, 0x17, 0x18, 0x19, 0x1A, 0x26,
            0x27, 0x28, 0x29, 0x2A, 0x35, 0x36, 0x37, 0x38,
            0x39, 0x3A, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48,
            0x49, 0x4A, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58,
            0x59, 0x5A, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68,
            0x69, 0x6A, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78,
            0x79, 0x7A, 0x82, 0x83, 0x84, 0x85, 0x86, 0x87,
            0x88, 0x89, 0x8A, 0x92, 0x93, 0x94, 0x95, 0x96,
            0x97, 0x98, 0x99, 0x9A, 0xA2, 0xA3, 0xA4, 0xA5,
            0xA6, 0xA7, 0xA8, 0xA9, 0xAA, 0xB2, 0xB3, 0xB4,
            0xB5, 0xB6, 0xB7, 0xB8, 0xB9, 0xBA, 0xC2, 0xC3,
            0xC4, 0xC5, 0xC6, 0xC7, 0xC8, 0xC9, 0xCA, 0xD2,
            0xD3, 0xD4, 0xD5, 0xD6, 0xD7, 0xD8, 0xD9, 0xDA,
            0xE2, 0xE3, 0xE4, 0xE5, 0xE6, 0xE7, 0xE8, 0xE9,
            0xEA, 0xF2, 0xF3, 0xF4, 0xF5, 0xF6, 0xF7, 0xF8,
            0xF9, 0xFA,
        };

        // Canonical Huffman codes derived from the bit-length counts above, indexed by symbol.
        // Each entry is { code, length }; unused symbols stay null and are never looked up.
        private static readonly uint[][] s_dcLuminanceCodes = BuildCodes(s_dcLuminanceBits, s_dcLuminanceValues);
        private static readonly uint[][] s_acLuminanceCodes = BuildCodes(s_acLuminanceBits, s_acLuminanceValues);
        private static readonly uint[][] s_dcChrominanceCodes = BuildCodes(s_dcChrominanceBits, s_dcChrominanceValues);
        private static readonly uint[][] s_acChrominanceCodes = BuildCodes(s_acChrominanceBits, s_acChrominanceValues);

        /// <summary>
        /// Assign canonical Huffman codes: walk lengths 1..16, handing out consecutive codes and
        /// shifting left at each new length. This is the same construction a decoder performs from the
        /// same table, which is why only the counts and symbols go in the file.
        /// </summary>
        private static uint[][] BuildCodes(byte[] bits, byte[] values)
        {
            var codes = new uint[256][];
            uint code = 0;
            int k = 0;

            for (int length = 1; length <= 16; length++)
            {
                for (int i = 0; i < bits[length - 1]; i++, k++)
                {
                    codes[values[k]] = new[] { code, (uint)length };
                    code++;
                }
                code <<= 1;
            }
            return codes;
        }
    }
}
