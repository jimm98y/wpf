// An app's icon for each head: from a Windows icon (.ico: the sizes it has, each a PNG or a DIB) to the PNGs
// a head asks for at its sizes, scaled from the nearest larger image.

using System.Buffers.Binary;
using System.IO.Compression;
using Microsoft.Wpf.Interop.WebGpu.Composition;

namespace WpfWebGpu.Packager;

public static class Icons
{
    /// <summary>Android's launcher icon sizes, by density (48 dp).</summary>
    public static readonly IReadOnlyList<(string Density, int Size)> AndroidMipmaps = new[]
    {
        ("mdpi", 48), ("hdpi", 72), ("xhdpi", 96), ("xxhdpi", 144), ("xxxhdpi", 192),
    };

    /// <summary>The icon's largest image as straight RGBA, or null when it has none it can read.</summary>
    public static (byte[] Rgba, int Width, int Height)? Largest(byte[] ico)
    {
        if (ico.Length >= 8 && ico[0] == 0x89 && ico[1] == (byte)'P') return Png(ico);       // a .png given as the icon
        if (ico.Length < 6 || BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(2)) != 1) return null;
        int count = BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(4));
        (byte[] Rgba, int Width, int Height)? best = null;
        for (int i = 0; i < count && 6 + i * 16 + 16 <= ico.Length; i++)
        {
            var entry = ico.AsSpan(6 + i * 16, 16);
            int size = BinaryPrimitives.ReadInt32LittleEndian(entry[8..]), offset = BinaryPrimitives.ReadInt32LittleEndian(entry[12..]);
            if (offset < 0 || size <= 0 || offset + size > ico.Length) continue;
            var image = ico.AsSpan(offset, size);
            var decoded = image.Length >= 8 && image[0] == 0x89 && image[1] == (byte)'P' ? Png(image.ToArray()) : Dib(image);
            if (decoded is { } d && (best == null || d.Width > best.Value.Width)) best = d;
        }
        return best;
    }

    /// <summary>The image at <paramref name="size"/> square, as a PNG.</summary>
    public static byte[] PngAt((byte[] Rgba, int Width, int Height) image, int size) =>
        EncodePng(Scale(image.Rgba, image.Width, image.Height, size, size), size, size);

    /// <summary>A macOS icon (.icns): the image as PNGs at 16 to 1024 pixels, each under its type.</summary>
    public static byte[] Icns((byte[] Rgba, int Width, int Height) image)
    {
        (string Type, int Size)[] sizes = { ("icp4", 16), ("icp5", 32), ("icp6", 64), ("ic07", 128), ("ic08", 256), ("ic09", 512), ("ic10", 1024) };
        using var ms = new MemoryStream();
        ms.Write("icns"u8);
        ms.Write(new byte[4]);
        Span<byte> len = stackalloc byte[4];
        foreach (var (type, size) in sizes)
        {
            byte[] png = PngAt(image, size);
            ms.Write(System.Text.Encoding.ASCII.GetBytes(type));
            BinaryPrimitives.WriteInt32BigEndian(len, png.Length + 8);
            ms.Write(len);
            ms.Write(png);
        }
        byte[] icns = ms.ToArray();
        BinaryPrimitives.WriteInt32BigEndian(icns.AsSpan(4), icns.Length);
        return icns;
    }

    /// <summary>A plain icon for a template with no app's yet: a rounded square in a neutral blue.</summary>
    public static (byte[] Rgba, int Width, int Height) Placeholder(int size = 192)
    {
        var rgba = new byte[size * size * 4];
        float r = size * 0.18f;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float dx = Math.Max(Math.Max(r - x, x - (size - 1 - r)), 0), dy = Math.Max(Math.Max(r - y, y - (size - 1 - r)), 0);
                float a = Math.Clamp(r + 0.5f - MathF.Sqrt(dx * dx + dy * dy), 0, 1);
                int i = (y * size + x) * 4;
                rgba[i] = 0x2b; rgba[i + 1] = 0x6c; rgba[i + 2] = 0xd4; rgba[i + 3] = (byte)(a * 255);
            }
        return (rgba, size, size);
    }

    private static (byte[] Rgba, int Width, int Height)? Png(byte[] png)
    {
        try { return (PngReader.Decode(png, out int w, out int h), w, h); }
        catch (Exception e) when (e is InvalidDataException or NotSupportedException or IndexOutOfRangeException or ArgumentException) { return null; }
    }

    // A BITMAPINFOHEADER image as an icon stores it: twice its height (the colour rows, then the AND mask), bottom-up.
    private static (byte[] Rgba, int Width, int Height)? Dib(ReadOnlySpan<byte> dib)
    {
        if (dib.Length < 40) return null;
        int headerSize = BinaryPrimitives.ReadInt32LittleEndian(dib);
        int w = BinaryPrimitives.ReadInt32LittleEndian(dib[4..]), h = BinaryPrimitives.ReadInt32LittleEndian(dib[8..]) / 2;
        int bpp = BinaryPrimitives.ReadUInt16LittleEndian(dib[14..]);
        if (w <= 0 || h <= 0 || w > 1024 || h > 1024 || bpp is not (32 or 24)) return null;   // palette icons: too small to matter
        int stride = (w * bpp / 8 + 3) & ~3, maskStride = ((w + 31) / 32) * 4;
        int pixels = headerSize, mask = pixels + stride * h;
        if (mask + maskStride * h > dib.Length) return null;
        var rgba = new byte[w * h * 4];
        bool anyAlpha = false;
        for (int y = 0; y < h; y++)
        {
            var row = dib.Slice(pixels + (h - 1 - y) * stride, stride);
            for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 4, s = x * bpp / 8;
                rgba[o] = row[s + 2]; rgba[o + 1] = row[s + 1]; rgba[o + 2] = row[s];
                rgba[o + 3] = bpp == 32 ? row[s + 3] : (byte)255;
                anyAlpha |= bpp == 32 && row[s + 3] != 0;
            }
        }
        if (!anyAlpha)
            // no alpha channel: the AND mask says which pixels are transparent
            for (int y = 0; y < h; y++)
            {
                var row = dib.Slice(mask + (h - 1 - y) * maskStride, maskStride);
                for (int x = 0; x < w; x++)
                    rgba[(y * w + x) * 4 + 3] = (row[x >> 3] & (0x80 >> (x & 7))) != 0 ? (byte)0 : (byte)255;
            }
        return (rgba, w, h);
    }

    // A box filter over premultiplied colour: each target pixel the mean of the source area it covers.
    private static byte[] Scale(byte[] src, int sw, int sh, int dw, int dh)
    {
        var dst = new byte[dw * dh * 4];
        double fx = (double)sw / dw, fy = (double)sh / dh;
        for (int y = 0; y < dh; y++)
        {
            double y0 = y * fy, y1 = Math.Max(y0 + 1e-9, (y + 1) * fy);
            for (int x = 0; x < dw; x++)
            {
                double x0 = x * fx, x1 = Math.Max(x0 + 1e-9, (x + 1) * fx);
                double r = 0, g = 0, b = 0, a = 0, area = 0;
                for (int sy = (int)y0; sy < Math.Min(sh, (int)Math.Ceiling(y1)); sy++)
                {
                    double wy = Math.Min(y1, sy + 1) - Math.Max(y0, sy);
                    for (int sx = (int)x0; sx < Math.Min(sw, (int)Math.Ceiling(x1)); sx++)
                    {
                        double wgt = wy * (Math.Min(x1, sx + 1) - Math.Max(x0, sx));
                        int i = (sy * sw + sx) * 4;
                        double al = src[i + 3] / 255.0 * wgt;
                        r += src[i] * al; g += src[i + 1] * al; b += src[i + 2] * al; a += al; area += wgt;
                    }
                }
                int o = (y * dw + x) * 4;
                if (a > 0)
                {
                    dst[o] = (byte)Math.Clamp(Math.Round(r / a), 0, 255);
                    dst[o + 1] = (byte)Math.Clamp(Math.Round(g / a), 0, 255);
                    dst[o + 2] = (byte)Math.Clamp(Math.Round(b / a), 0, 255);
                }
                dst[o + 3] = (byte)Math.Clamp(Math.Round(a / Math.Max(area, 1e-9) * 255), 0, 255);
            }
        }
        return dst;
    }

    /// <summary>Straight RGBA8 as a PNG (colour type 6, no filtering).</summary>
    public static byte[] EncodePng(byte[] rgba, int width, int height)
    {
        using var ms = new MemoryStream();
        ms.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height);
        ihdr[8] = 8; ihdr[9] = 6;
        Chunk(ms, "IHDR", ihdr);
        using (var raw = new MemoryStream())
        {
            using (var z = new ZLibStream(raw, CompressionLevel.Optimal, leaveOpen: true))
                for (int y = 0; y < height; y++)
                {
                    z.WriteByte(0);
                    z.Write(rgba, y * width * 4, width * 4);
                }
            Chunk(ms, "IDAT", raw.ToArray());
        }
        Chunk(ms, "IEND", Array.Empty<byte>());
        return ms.ToArray();
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        s.Write(len);
        byte[] t = System.Text.Encoding.ASCII.GetBytes(type);
        s.Write(t);
        s.Write(data);
        uint crc = Crc32(t, 0xFFFFFFFF);
        crc = Crc32(data, crc) ^ 0xFFFFFFFF;
        BinaryPrimitives.WriteUInt32BigEndian(len, crc);
        s.Write(len);
    }

    private static uint[]? s_crcTable;

    private static uint Crc32(byte[] data, uint crc)
    {
        var table = s_crcTable ??= Enumerable.Range(0, 256).Select(n =>
        {
            uint c = (uint)n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            return c;
        }).ToArray();
        foreach (byte b in data) crc = table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }
}
