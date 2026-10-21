using System.Buffers.Binary;
using System.IO.Compression;
using Xunit;

namespace WpfWebGpu.Tests;

public class PngTests
{
    [Fact]
    public void Writes_a_valid_RGBA_image_from_BGRA_pixels()
    {
        string path = Path.Combine(TestEnvironment.NewDirectory("png"), "a.png");
        // 2x1: blue, then half-transparent red (BGRA order in, RGBA expected out).
        Png.Write(path, 2, 1, new byte[] { 255, 0, 0, 255, 0, 0, 255, 128 });
        byte[] png = File.ReadAllBytes(path);

        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, png[..8]);
        var chunks = Chunks(png).ToList();
        Assert.Equal(new[] { "IHDR", "IDAT", "IEND" }, chunks.Select(c => c.Type));
        foreach (var c in chunks) Assert.Equal(Crc(c.Type, c.Data), c.Crc);

        byte[] ihdr = chunks[0].Data;
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32BigEndian(ihdr));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32BigEndian(ihdr.AsSpan(4)));
        Assert.Equal(8, ihdr[8]);                 // bit depth
        Assert.Equal(6, ihdr[9]);                 // RGBA

        using var z = new ZLibStream(new MemoryStream(chunks[1].Data), CompressionMode.Decompress);
        using var raw = new MemoryStream();
        z.CopyTo(raw);
        Assert.Equal(new byte[] { 0, 0, 0, 255, 255, 255, 0, 0, 128 }, raw.ToArray());
    }

    private static IEnumerable<(string Type, byte[] Data, uint Crc)> Chunks(byte[] png)
    {
        for (int at = 8; at < png.Length;)
        {
            int length = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(at));
            string type = System.Text.Encoding.ASCII.GetString(png, at + 4, 4);
            byte[] data = png.AsSpan(at + 8, length).ToArray();
            uint crc = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(at + 8 + length));
            yield return (type, data, crc);
            at += 12 + length;
        }
    }

    private static uint Crc(string type, byte[] data)
    {
        uint c = 0xFFFFFFFF;
        foreach (byte b in System.Text.Encoding.ASCII.GetBytes(type).Concat(data))
        {
            c ^= b;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        }
        return c ^ 0xFFFFFFFF;
    }
}
