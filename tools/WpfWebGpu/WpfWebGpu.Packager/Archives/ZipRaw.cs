// Zip, read and written at the level an APK needs: entries copied from a template as they are (their compressed
// bytes, never inflated and deflated again), new ones added, each entry's data placed where Android wants it
// (uncompressed data 4-byte aligned, native libraries on a 16 KB page so they can be mapped in place: what
// zipalign does), and nothing else -- no zip64, no encryption, no data descriptors.

using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace WpfWebGpu.Packager.Archives;

/// <summary>An entry of an existing zip: where its data is, as stored.</summary>
public sealed record ZipEntryInfo(string Name, ushort Method, uint Crc, long CompressedSize, long Size, long LocalHeaderOffset, ushort Time, ushort Date);

public sealed class ZipReader : IDisposable
{
    private readonly FileStream _file;
    public IReadOnlyList<ZipEntryInfo> Entries { get; }

    public ZipReader(string path)
    {
        _file = File.OpenRead(path);
        Entries = ReadCentralDirectory();
    }

    public void Dispose() => _file.Dispose();

    private List<ZipEntryInfo> ReadCentralDirectory()
    {
        // the end-of-central-directory record: the last 22 bytes, or before a comment of up to 64 KB
        long len = _file.Length;
        int tail = (int)Math.Min(len, 22 + 65535);
        var buf = new byte[tail];
        _file.Position = len - tail;
        _file.ReadExactly(buf);
        int eocd = -1;
        for (int i = tail - 22; i >= 0; i--)
            if (BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(i)) == 0x06054b50) { eocd = i; break; }
        if (eocd < 0) throw new PackagerError("not a zip file (no end of central directory)");
        int count = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(eocd + 10));
        uint cdSize = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(eocd + 12));
        uint cdOffset = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(eocd + 16));
        if (count == 0xFFFF || cdOffset == 0xFFFFFFFF) throw new PackagerError("zip64 archives are not supported");

        var cd = new byte[cdSize];
        _file.Position = cdOffset;
        _file.ReadExactly(cd);
        var entries = new List<ZipEntryInfo>(count);
        int p = 0;
        for (int n = 0; n < count; n++)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(cd.AsSpan(p)) != 0x02014b50) throw new PackagerError("bad central directory");
            ushort method = BinaryPrimitives.ReadUInt16LittleEndian(cd.AsSpan(p + 10));
            ushort time = BinaryPrimitives.ReadUInt16LittleEndian(cd.AsSpan(p + 12)), date = BinaryPrimitives.ReadUInt16LittleEndian(cd.AsSpan(p + 14));
            uint crc = BinaryPrimitives.ReadUInt32LittleEndian(cd.AsSpan(p + 16));
            uint csize = BinaryPrimitives.ReadUInt32LittleEndian(cd.AsSpan(p + 20)), size = BinaryPrimitives.ReadUInt32LittleEndian(cd.AsSpan(p + 24));
            int nameLen = BinaryPrimitives.ReadUInt16LittleEndian(cd.AsSpan(p + 28));
            int extraLen = BinaryPrimitives.ReadUInt16LittleEndian(cd.AsSpan(p + 30)), commentLen = BinaryPrimitives.ReadUInt16LittleEndian(cd.AsSpan(p + 32));
            uint local = BinaryPrimitives.ReadUInt32LittleEndian(cd.AsSpan(p + 42));
            string name = Encoding.UTF8.GetString(cd, p + 46, nameLen);
            entries.Add(new ZipEntryInfo(name, method, crc, csize, size, local, time, date));
            p += 46 + nameLen + extraLen + commentLen;
        }
        return entries;
    }

    /// <summary>The entry's data as stored (compressed if it is).</summary>
    public void CopyRaw(ZipEntryInfo e, Stream to)
    {
        var header = new byte[30];
        _file.Position = e.LocalHeaderOffset;
        _file.ReadExactly(header);
        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != 0x04034b50) throw new PackagerError($"bad local header for {e.Name}");
        int skip = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(26)) + BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(28));
        _file.Position = e.LocalHeaderOffset + 30 + skip;
        CopyBytes(_file, to, e.CompressedSize);
    }

    /// <summary>The entry's content, inflated.</summary>
    public byte[] Read(ZipEntryInfo e)
    {
        using var raw = new MemoryStream();
        CopyRaw(e, raw);
        raw.Position = 0;
        if (e.Method == 0) return raw.ToArray();
        using var inflate = new DeflateStream(raw, CompressionMode.Decompress);
        using var result = new MemoryStream((int)e.Size);
        inflate.CopyTo(result);
        return result.ToArray();
    }

    internal static void CopyBytes(Stream from, Stream to, long count)
    {
        var buffer = new byte[1 << 16];
        while (count > 0)
        {
            int n = from.Read(buffer, 0, (int)Math.Min(buffer.Length, count));
            if (n <= 0) throw new EndOfStreamException();
            to.Write(buffer, 0, n);
            count -= n;
        }
    }
}

public sealed class ZipWriter : IDisposable
{
    private readonly Stream _out;
    private readonly List<(ZipEntryInfo Entry, long Offset)> _written = new();
    private readonly HashSet<string> _names = new(StringComparer.Ordinal);

    public ZipWriter(Stream output) => _out = output;

    public IReadOnlyCollection<string> Names => _names;

    /// <summary>Where the data of an uncompressed entry starts: native libraries on a page, the rest on 4 bytes.</summary>
    public static int AlignmentFor(string name) => name.EndsWith(".so", StringComparison.Ordinal) ? 16384 : 4;

    /// <summary>An existing entry, its stored bytes copied.</summary>
    public void CopyFrom(ZipReader reader, ZipEntryInfo e)
    {
        long offset = WriteLocalHeader(e.Name, e.Method, e.Crc, e.CompressedSize, e.Size, e.Time, e.Date);
        reader.CopyRaw(e, _out);
        _written.Add((e with { LocalHeaderOffset = offset }, offset));
    }

    /// <summary>A new entry: deflated, or stored where compressing gains nothing (or Android maps it).</summary>
    public void Add(string name, Stream content, bool compress)
    {
        // the data is read twice (its CRC and size go before it): once into a buffer file when it is large
        using var source = content.CanSeek ? null : new MemoryStream();
        Stream input = content;
        if (source != null) { content.CopyTo(source); source.Position = 0; input = source; }
        long start = input.Position;
        uint crc = Crc32.Compute(input);
        long size = input.Position - start;
        input.Position = start;

        // an empty entry stored: deflating nothing writes nothing, which is no deflate stream at all
        if (!compress || size == 0)
        {
            WriteLocalHeader(name, 0, crc, size, size, DosTime, DosDate, out long offset);
            ZipReader.CopyBytes(input, _out, size);
            _written.Add((new ZipEntryInfo(name, 0, crc, size, size, offset, DosTime, DosDate), offset));
            return;
        }
        // deflated to a temporary file first: its compressed size goes in the header before it
        string temp = Path.GetTempFileName();
        try
        {
            long csize;
            using (var t = new FileStream(temp, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 16, FileOptions.DeleteOnClose))
            {
                using (var deflate = new DeflateStream(t, CompressionLevel.Optimal, leaveOpen: true))
                    ZipReader.CopyBytes(input, deflate, size);
                csize = t.Length;
                WriteLocalHeader(name, 8, crc, csize, size, DosTime, DosDate, out long offset);
                t.Position = 0;
                t.CopyTo(_out);
                _written.Add((new ZipEntryInfo(name, 8, crc, csize, size, offset, DosTime, DosDate), offset));
            }
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    public void Add(string name, byte[] content, bool compress) => Add(name, new MemoryStream(content, writable: false), compress);

    // 1980-01-01: reproducible output, as the Android build tools write it
    private const ushort DosTime = 0, DosDate = (0 << 9) | (1 << 5) | 1;

    private long WriteLocalHeader(string name, ushort method, uint crc, long csize, long size, ushort time, ushort date)
    {
        WriteLocalHeader(name, method, crc, csize, size, time, date, out long offset);
        return offset;
    }

    private void WriteLocalHeader(string name, ushort method, uint crc, long csize, long size, ushort time, ushort date, out long offset)
    {
        if (!_names.Add(name)) throw new PackagerError($"two entries named {name}");
        if (csize > uint.MaxValue - 1 || size > uint.MaxValue - 1 || _out.Position > uint.MaxValue - 1)
            throw new PackagerError($"{name}: the archive passes 4 GB, which an APK cannot");
        offset = _out.Position;
        byte[] nameBytes = Encoding.UTF8.GetBytes(name);
        // an uncompressed entry's data aligned by padding the extra field (zipalign's way)
        int extra = 0;
        if (method == 0)
        {
            int align = AlignmentFor(name);
            long dataAt = offset + 30 + nameBytes.Length;
            extra = (int)((align - dataAt % align) % align);
        }
        var h = new byte[30];
        BinaryPrimitives.WriteUInt32LittleEndian(h, 0x04034b50);
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(4), method == 0 ? (ushort)10 : (ushort)20);
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(6), 0x0800);        // UTF-8 names
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(8), method);
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(10), time);
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(12), date);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(14), crc);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(18), (uint)csize);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(22), (uint)size);
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(26), (ushort)nameBytes.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(28), (ushort)extra);
        _out.Write(h);
        _out.Write(nameBytes);
        if (extra > 0) _out.Write(new byte[extra]);
    }

    /// <summary>The central directory and its end record; returns where the central directory starts.</summary>
    public long Finish()
    {
        long cdStart = _out.Position;
        foreach (var (e, offset) in _written)
        {
            byte[] nameBytes = Encoding.UTF8.GetBytes(e.Name);
            var h = new byte[46];
            BinaryPrimitives.WriteUInt32LittleEndian(h, 0x02014b50);
            BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(4), 20);
            BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(6), e.Method == 0 ? (ushort)10 : (ushort)20);
            BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(8), 0x0800);
            BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(10), e.Method);
            BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(12), e.Time);
            BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(14), e.Date);
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(16), e.Crc);
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(20), (uint)e.CompressedSize);
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(24), (uint)e.Size);
            BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(28), (ushort)nameBytes.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(42), (uint)offset);
            _out.Write(h);
            _out.Write(nameBytes);
        }
        long cdSize = _out.Position - cdStart;
        if (_written.Count >= 0xFFFF) throw new PackagerError("more than 65534 entries, which needs zip64");
        var end = new byte[22];
        BinaryPrimitives.WriteUInt32LittleEndian(end, 0x06054b50);
        BinaryPrimitives.WriteUInt16LittleEndian(end.AsSpan(8), (ushort)_written.Count);
        BinaryPrimitives.WriteUInt16LittleEndian(end.AsSpan(10), (ushort)_written.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(end.AsSpan(12), (uint)cdSize);
        BinaryPrimitives.WriteUInt32LittleEndian(end.AsSpan(16), (uint)cdStart);
        _out.Write(end);
        _out.Flush();
        return cdStart;
    }

    public void Dispose() { }
}

/// <summary>CRC-32 (IEEE), as zip and PNG use it.</summary>
public static class Crc32
{
    private static readonly uint[] s_table = Enumerable.Range(0, 256).Select(n =>
    {
        uint c = (uint)n;
        for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    public static uint Update(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (byte b in data) crc = s_table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    /// <summary>The CRC of the rest of the stream, which is left at its end.</summary>
    public static uint Compute(Stream s)
    {
        var buffer = new byte[1 << 16];
        uint crc = 0xFFFFFFFF;
        int n;
        while ((n = s.Read(buffer, 0, buffer.Length)) > 0) crc = Update(crc, buffer.AsSpan(0, n));
        return crc ^ 0xFFFFFFFF;
    }

    public static uint Compute(ReadOnlySpan<byte> data) => Update(0xFFFFFFFF, data) ^ 0xFFFFFFFF;
}
