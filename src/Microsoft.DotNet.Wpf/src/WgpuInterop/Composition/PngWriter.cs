// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A tiny dependency-free PNG encoder (zlib "stored" blocks) used only to dump a
// screenshot of an off-screen WebGPU render for diagnostics/demos. Takes straight
// RGBA, writes 8-bit RGB; optionally box-downsamples so the file stays viewable.
//

using System;
using System.IO;

namespace Microsoft.Wpf.Interop.WebGpu.Composition
{
    internal static class PngWriter
    {
        /// <summary>Write RGBA pixels as a PNG, box-downsampling so width is &lt;= maxWidth.</summary>
        public static void Write(string path, byte[] rgba, int width, int height, int maxWidth = 1100)
        {
            int step = Math.Max(1, (width + maxWidth - 1) / maxWidth);
            int ow = width / step, oh = height / step;
            var rgb = new byte[ow * oh * 3];
            for (int oy = 0; oy < oh; oy++)
                for (int ox = 0; ox < ow; ox++)
                {
                    int r = 0, g = 0, b = 0, n = 0;
                    for (int sy = 0; sy < step; sy++)
                        for (int sx = 0; sx < step; sx++)
                        {
                            int i = ((oy * step + sy) * width + (ox * step + sx)) * 4;
                            r += rgba[i]; g += rgba[i + 1]; b += rgba[i + 2]; n++;
                        }
                    int o = (oy * ow + ox) * 3;
                    rgb[o] = (byte)(r / n); rgb[o + 1] = (byte)(g / n); rgb[o + 2] = (byte)(b / n);
                }

            // Raw image data: each row prefixed with filter byte 0.
            var raw = new byte[oh * (ow * 3 + 1)];
            for (int y = 0; y < oh; y++)
            {
                raw[y * (ow * 3 + 1)] = 0;
                Array.Copy(rgb, y * ow * 3, raw, y * (ow * 3 + 1) + 1, ow * 3);
            }

            using var fs = new FileStream(path, FileMode.Create);
            fs.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, 0, 8);
            WriteChunk(fs, "IHDR", Ihdr(ow, oh));
            WriteChunk(fs, "IDAT", ZlibStore(raw));
            WriteChunk(fs, "IEND", Array.Empty<byte>());
        }

        private static byte[] Ihdr(int w, int h)
        {
            var b = new byte[13];
            Be(b, 0, w); Be(b, 4, h);
            b[8] = 8;   // bit depth
            b[9] = 2;   // colour type: truecolour RGB
            return b;
        }

        private static byte[] ZlibStore(byte[] data)
        {
            using var ms = new MemoryStream();
            ms.WriteByte(0x78); ms.WriteByte(0x01);           // zlib header
            int pos = 0;
            while (pos < data.Length)
            {
                int len = Math.Min(65535, data.Length - pos);
                bool final = pos + len >= data.Length;
                ms.WriteByte((byte)(final ? 1 : 0));          // BFINAL, BTYPE=00 (stored)
                ms.WriteByte((byte)(len & 0xff)); ms.WriteByte((byte)(len >> 8));
                ms.WriteByte((byte)(~len & 0xff)); ms.WriteByte((byte)((~len >> 8) & 0xff));
                ms.Write(data, pos, len);
                pos += len;
            }
            uint adler = Adler32(data);
            ms.WriteByte((byte)(adler >> 24)); ms.WriteByte((byte)(adler >> 16));
            ms.WriteByte((byte)(adler >> 8)); ms.WriteByte((byte)adler);
            return ms.ToArray();
        }

        private static void WriteChunk(Stream s, string type, byte[] data)
        {
            var len = new byte[4]; Be(len, 0, data.Length); s.Write(len, 0, 4);
            var typeBytes = new[] { (byte)type[0], (byte)type[1], (byte)type[2], (byte)type[3] };
            s.Write(typeBytes, 0, 4);
            s.Write(data, 0, data.Length);
            uint crc = Crc32(typeBytes, data);
            var c = new byte[4]; Be(c, 0, (int)crc); s.Write(c, 0, 4);
        }

        private static void Be(byte[] b, int o, int v)
        { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }

        private static uint Adler32(byte[] data)
        {
            uint a = 1, b = 0;
            foreach (byte x in data) { a = (a + x) % 65521; b = (b + a) % 65521; }
            return (b << 16) | a;
        }

        private static uint[] s_crcTable;
        private static uint Crc32(byte[] type, byte[] data)
        {
            if (s_crcTable == null)
            {
                s_crcTable = new uint[256];
                for (uint n = 0; n < 256; n++)
                {
                    uint c = n;
                    for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
                    s_crcTable[n] = c;
                }
            }
            uint crc = 0xFFFFFFFF;
            foreach (byte x in type) crc = s_crcTable[(crc ^ x) & 0xff] ^ (crc >> 8);
            foreach (byte x in data) crc = s_crcTable[(crc ^ x) & 0xff] ^ (crc >> 8);
            return crc ^ 0xFFFFFFFF;
        }
    }
}
