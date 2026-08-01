// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Minimal PNG decoder, the exact inverse of PngWriter: 8-bit non-interlaced RGB with
// filter byte 0 on every row. Exists so the render-baseline test can read back the
// images PngWriter produced; inflate comes from the BCL, so both the deflated form
// PngWriter emits now and the older "stored"-block form still decode.
//
// Anything outside that scope is rejected with a specific message rather than decoded
// approximately. A baseline that silently decoded wrong would make the comparison
// meaningless in the direction that matters (falsely passing).
//

using System;
using System.IO;
using System.IO.Compression;

namespace Microsoft.Wpf.Interop.WebGpu.Composition
{
    internal static class PngReader
    {
        /// <summary>Reads a PngWriter-produced PNG as straight RGBA (alpha forced to 255).</summary>
        public static byte[] Read(string path, out int width, out int height)
        {
            byte[] f = File.ReadAllBytes(path);
            if (f.Length < 8 || f[0] != 0x89 || f[1] != 'P' || f[2] != 'N' || f[3] != 'G')
                throw new InvalidDataException($"{path}: not a PNG.");

            width = height = 0;
            var idat = new MemoryStream();
            int pos = 8;
            while (pos + 8 <= f.Length)
            {
                int len = BE(f, pos);
                string type = System.Text.Encoding.ASCII.GetString(f, pos + 4, 4);
                int dataAt = pos + 8;
                if (dataAt + len > f.Length) throw new InvalidDataException($"{path}: truncated chunk '{type}'.");

                if (type == "IHDR")
                {
                    width = BE(f, dataAt);
                    height = BE(f, dataAt + 4);
                    int bitDepth = f[dataAt + 8], colorType = f[dataAt + 9], interlace = f[dataAt + 12];
                    if (bitDepth != 8 || colorType != 2 || interlace != 0)
                        throw new InvalidDataException(
                            $"{path}: only 8-bit non-interlaced RGB is supported (got depth {bitDepth}, colour type {colorType}).");
                }
                else if (type == "IDAT") idat.Write(f, dataAt, len);
                else if (type == "IEND") break;

                pos = dataAt + len + 4;   // + CRC
            }
            if (width <= 0 || height <= 0) throw new InvalidDataException($"{path}: missing IHDR.");

            byte[] raw = Inflate(idat.ToArray(), path);
            int stride = width * 3 + 1;
            if (raw.Length < stride * height)
                throw new InvalidDataException($"{path}: image data short ({raw.Length} < {stride * height}).");

            var rgba = new byte[width * height * 4];
            for (int y = 0; y < height; y++)
            {
                int rowAt = y * stride;
                if (raw[rowAt] != 0) throw new InvalidDataException($"{path}: row {y} uses filter {raw[rowAt]}; only 0 is supported.");
                for (int x = 0; x < width; x++)
                {
                    int s = rowAt + 1 + x * 3, d = (y * width + x) * 4;
                    rgba[d] = raw[s]; rgba[d + 1] = raw[s + 1]; rgba[d + 2] = raw[s + 2]; rgba[d + 3] = 255;
                }
            }
            return rgba;
        }

        private static byte[] Inflate(byte[] z, string path)
        {
            if (z.Length < 6) throw new InvalidDataException($"{path}: empty IDAT.");
            if ((z[0] & 0x0F) != 8) throw new InvalidDataException($"{path}: not zlib deflate.");
            // Skip the 2-byte zlib header and the trailing 4-byte Adler-32.
            using var src = new MemoryStream(z, 2, z.Length - 6);
            using var ds = new DeflateStream(src, CompressionMode.Decompress);
            using var outBuf = new MemoryStream();
            ds.CopyTo(outBuf);
            return outBuf.ToArray();
        }

        private static int BE(byte[] b, int i) => (b[i] << 24) | (b[i + 1] << 16) | (b[i + 2] << 8) | b[i + 3];
    }
}
