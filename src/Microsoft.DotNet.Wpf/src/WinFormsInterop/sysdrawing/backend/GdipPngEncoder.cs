// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The PNG GDI+ writes: Image.Save(png) and every bitmap GDI+ serialises into an EMF+ image object
// (CopyOnWriteBitmap::GetData @180080d90 saves through PngCodecClsID with no encoder parameters).
// gdiplus.dll hands the bitmap to WIC's PNG encoder (GpWicPngEncoder), and WindowsCodecs.dll
// (10.0.26100, arm64, public PDB) writes it with SPNGWRITE over its Chromium-flavoured zlib:
//
//   GpWicPngEncoder::CreateNewFrame @180105c20   sets "FilterOption" = 1 (WICPngFilterNone) on
//            every frame, so each row goes down with filter byte 0
//   SPNGWRITE::ResolveData @18009c170            the zlib parameters: strategy = (filters != 0),
//            so Z_DEFAULT_STRATEGY; level 7 for a palette or greyscale image under 8 bits, else 4;
//            windowBits = ceil(log2(filtered size + 256)) when that is under 15, at least 8
//   SPNGWRITE::FInitZlib @18009bdb0              deflateInit2_(level, windowBits, memLevel 9, strategy)
//   deflateInit2_ @1800a29d0, deflate_slow @1800a31d0   Chromium's CRC32C string hash (ZlibDeflate)
//   CPngFrameEncode::HrWriteFrameHeader @180079580   IHDR, the sRGB and gAMA metadata, PLTE and
//            tRNS (HrWritePalette), then pHYs
//   SPNGWRITE::FWriteCbIDAT @18009bf08 / FFlushIDAT @18009bd10 / FEndIDAT @18009bbd0   the IDAT
//            data fills SPNGWRITE's 64K buffer: the first chunk is as long as the buffer has room
//            for (0xfff8 less what precedes it), each further one 0xfff4, the last what is left
//
// SPNGWRITE's own buffer constructs (CbWrite, the private cmPP chunk) do not arise for GDI+:
// the cmPP flag starts 0xff (CPngEncoder::HrInit's defaults @180071eb0) and nothing clears it.
//

using System.IO;
using System.Windows.Media.Imaging;

namespace System.Drawing.WebGpuBackend
{
    internal static class GdipPngEncoder
    {
        const int Buffer = 0x10000;

        public static void Write(Stream stream, ManagedRaster image, uint ppmX, uint ppmY)
        {
            int width = image.Width, height = image.Height;
            if (image.Pixels == null || width <= 0 || height <= 0)
                throw new InvalidOperationException("The bitmap has no pixels to encode.");
            ManagedPixelLayout layout = image.Layout;
            bool indexed = ManagedPixelLayouts.IsIndexed(layout);
            bool gray = ManagedPixelLayouts.IsGray(layout);
            int bitDepth, colorType, channels;
            if (indexed || gray) { bitDepth = ManagedPixelLayouts.BitsPerPixel(layout); colorType = indexed ? 3 : 0; channels = 1; }
            else if (layout == ManagedPixelLayout.Bgr24) { bitDepth = 8; colorType = 2; channels = 3; }
            else if (layout == ManagedPixelLayout.Bgra32) { bitDepth = 8; colorType = 6; channels = 4; }
            else throw new NotSupportedException($"A {layout} picture cannot be written as PNG.");

            var o = new MemoryStream();
            o.Write(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A });
            var ihdr = new byte[13];
            U32(ihdr, 0, (uint)width); U32(ihdr, 4, (uint)height);
            ihdr[8] = (byte)bitDepth; ihdr[9] = (byte)colorType;
            Chunk(o, "IHDR", ihdr);
            Chunk(o, "sRGB", new byte[] { 0 });
            var gama = new byte[4]; U32(gama, 0, 45455);
            Chunk(o, "gAMA", gama);
            if (indexed)
            {
                uint[] colors = image.Palette ?? Array.Empty<uint>();
                var plte = new byte[colors.Length * 3];
                for (int i = 0; i < colors.Length; i++)
                {
                    plte[i * 3] = (byte)(colors[i] >> 16); plte[i * 3 + 1] = (byte)(colors[i] >> 8); plte[i * 3 + 2] = (byte)colors[i];
                }
                Chunk(o, "PLTE", plte);
                int last = -1;
                for (int i = 0; i < colors.Length; i++) if ((colors[i] >> 24) != 255) last = i;
                if (last >= 0)
                {
                    var trns = new byte[last + 1];
                    for (int i = 0; i <= last; i++) trns[i] = (byte)(colors[i] >> 24);
                    Chunk(o, "tRNS", trns);
                }
            }
            if (ppmX != 0 && ppmY != 0)
            {
                var phys = new byte[9]; U32(phys, 0, ppmX); U32(phys, 4, ppmY); phys[8] = 1;
                Chunk(o, "pHYs", phys);
            }

            // The rows, each with filter byte 0 (WICPngFilterNone).
            int rowBytes = (width * bitDepth * channels + 7) / 8;
            var raw = new byte[height * (1 + rowBytes)];
            byte[] src = image.Pixels;
            int k = 0;
            for (int y = 0; y < height; y++)
            {
                raw[k++] = 0;
                int i = y * image.Stride;
                if (colorType == 6)
                    for (int x = 0; x < width; x++, i += 4) { raw[k++] = src[i + 2]; raw[k++] = src[i + 1]; raw[k++] = src[i]; raw[k++] = src[i + 3]; }
                else if (colorType == 2)
                    for (int x = 0; x < width; x++, i += 3) { raw[k++] = src[i + 2]; raw[k++] = src[i + 1]; raw[k++] = src[i]; }
                else { Array.Copy(src, i, raw, k, rowBytes); k += rowBytes; }
            }

            // ResolveData's zlib parameters.
            int level = bitDepth < 8 && ((colorType & 1) != 0 || (colorType & 2) == 0) ? 7 : 4;
            int windowBits = 15;
            long size = (long)((width > 0 ? 1 : 0) + rowBytes) * height + 0x100;
            int log = 0;
            while ((1L << (log + 1)) <= size) log++;
            if ((1L << log) < size) log++;
            if (log < windowBits) windowBits = Math.Max(8, log);
            var z = new ZlibDeflate(level, windowBits, 9, ZlibDeflate.DefaultStrategy, crcHash: true);
            for (int y = 0; y < height; y++)
                z.Deflate(raw, y * (1 + rowBytes), 1 + rowBytes, ZlibDeflate.NoFlush);
            z.Deflate(raw, raw.Length, 0, ZlibDeflate.Finish);
            byte[] data = z.ToArray();

            // FWriteCbIDAT: no room left for a chunk header writes an empty msOD chunk first.
            int pos = (int)(o.Length % Buffer);
            if (pos + 8 >= Buffer)
            {
                Chunk(o, "msOD", Array.Empty<byte>());
                pos = (int)(o.Length % Buffer);
            }
            int at = 0, cap = 0xfff8 - pos;
            while (at < data.Length)
            {
                int n = Math.Min(cap, data.Length - at);
                Chunk(o, "IDAT", new ReadOnlySpan<byte>(data, at, n));
                at += n;
                cap = 0xfff4;
            }
            Chunk(o, "IEND", Array.Empty<byte>());
            o.Position = 0;
            o.CopyTo(stream);
        }

        static void U32(byte[] b, int o, uint v) { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }

        static void Chunk(Stream s, string type, ReadOnlySpan<byte> data)
        {
            var head = new byte[8];
            U32(head, 0, (uint)data.Length);
            for (int i = 0; i < 4; i++) head[4 + i] = (byte)type[i];
            s.Write(head);
            s.Write(data);
            uint crc = Crc(0xffffffff, head.AsSpan(4, 4));
            crc = Crc(crc, data) ^ 0xffffffff;
            var c = new byte[4]; U32(c, 0, crc);
            s.Write(c);
        }

        static readonly uint[] s_crc = BuildCrc();

        static uint[] BuildCrc()
        {
            var t = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
                t[n] = c;
            }
            return t;
        }

        static uint Crc(uint crc, ReadOnlySpan<byte> d)
        {
            foreach (byte b in d) crc = s_crc[(crc ^ b) & 0xff] ^ (crc >> 8);
            return crc;
        }
    }
}
