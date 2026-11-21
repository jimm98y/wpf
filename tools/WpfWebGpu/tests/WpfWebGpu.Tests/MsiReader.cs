// A reader of Windows Installer databases for the tests: the compound file's streams, the string pool, the tables'
// columns and rows -- to read back what the Packager's MSI writer wrote, and real MSIs to compare it with.

using System.Buffers.Binary;
using System.Text;

namespace WpfWebGpu.Tests;

public sealed class CompoundFileReader
{
    private readonly byte[] _file;
    private readonly int _sectorSize;
    private readonly List<uint> _fat = new();
    private readonly List<uint> _miniFat = new();
    private readonly byte[] _miniStream;

    public sealed record Entry(string Name, int Type, byte[] RawName, Guid Clsid, uint Start, long Size, int Color, uint Left, uint Right, uint Child);
    public List<Entry> Entries { get; } = new();

    public CompoundFileReader(byte[] file)
    {
        _file = file;
        if (BinaryPrimitives.ReadUInt64LittleEndian(file) != 0xE11AB1A1E011CFD0) throw new InvalidDataException("not a compound file");
        _sectorSize = 1 << BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(30));
        uint fatSectors = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(44));
        uint dirStart = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(48));
        uint miniFatStart = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(60));
        uint difatStart = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(68));
        var difat = new List<uint>();
        for (int i = 0; i < 109; i++) difat.Add(BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(76 + i * 4)));
        for (uint s = difatStart; s < 0xFFFFFFFA;)
        {
            var sector = Sector(s);
            for (int i = 0; i < _sectorSize / 4 - 1; i++) difat.Add(BinaryPrimitives.ReadUInt32LittleEndian(sector[(i * 4)..]));
            s = BinaryPrimitives.ReadUInt32LittleEndian(sector[(_sectorSize - 4)..]);
        }
        foreach (uint s in difat.Take((int)fatSectors))
        {
            var sector = Sector(s);
            for (int i = 0; i < _sectorSize / 4; i++) _fat.Add(BinaryPrimitives.ReadUInt32LittleEndian(sector[(i * 4)..]));
        }
        byte[] dir = Chain(dirStart, -1);
        for (int at = 0; at + 128 <= dir.Length; at += 128)
        {
            var e = dir.AsSpan(at, 128);
            int nameLen = BinaryPrimitives.ReadUInt16LittleEndian(e[64..]);
            int type = e[66];
            byte[] raw = e[..Math.Max(0, Math.Min(64, nameLen))].ToArray();
            string name = Encoding.Unicode.GetString(raw).TrimEnd('\0');
            Entries.Add(new Entry(name, type, raw, new Guid(e.Slice(80, 16)), BinaryPrimitives.ReadUInt32LittleEndian(e[116..]),
                (long)BinaryPrimitives.ReadUInt64LittleEndian(e[120..]), e[67],
                BinaryPrimitives.ReadUInt32LittleEndian(e[68..]), BinaryPrimitives.ReadUInt32LittleEndian(e[72..]), BinaryPrimitives.ReadUInt32LittleEndian(e[76..])));
        }
        byte[] miniFat = miniFatStart < 0xFFFFFFFA ? Chain(miniFatStart, -1) : Array.Empty<byte>();
        for (int i = 0; i + 4 <= miniFat.Length; i += 4) _miniFat.Add(BinaryPrimitives.ReadUInt32LittleEndian(miniFat.AsSpan(i)));
        _miniStream = Entries[0].Start < 0xFFFFFFFA ? Chain(Entries[0].Start, Entries[0].Size) : Array.Empty<byte>();
    }

    private ReadOnlySpan<byte> Sector(uint s) => _file.AsSpan((int)((s + 1) * _sectorSize), _sectorSize);

    private byte[] Chain(uint start, long size)
    {
        var ms = new MemoryStream();
        for (uint s = start; s < 0xFFFFFFFA; s = _fat[(int)s]) ms.Write(Sector(s));
        var all = ms.ToArray();
        return size >= 0 ? all[..(int)size] : all;
    }

    public byte[] Read(Entry e)
    {
        if (e.Size == 0) return Array.Empty<byte>();
        if (e.Size >= 4096) return Chain(e.Start, e.Size);
        var ms = new MemoryStream();
        for (uint s = e.Start; s < 0xFFFFFFFA; s = _miniFat[(int)s]) ms.Write(_miniStream, (int)(s * 64), 64);
        return ms.ToArray()[..(int)e.Size];
    }

    public byte[]? Read(string name) => Entries.FirstOrDefault(e => e.Name == name && e.Type == 2) is { } e ? Read(e) : null;
}

public sealed class MsiReader
{
    public CompoundFileReader File { get; }
    public int Codepage { get; }
    public List<string?> Strings { get; } = new() { null };
    public List<int> RefCounts { get; } = new() { 0 };
    public int StringRefSize { get; }
    public sealed record Column(string Table, int Number, string Name, int Type);
    public List<Column> Columns { get; } = new();
    public List<string> Tables { get; } = new();

    public MsiReader(byte[] bytes)
    {
        File = new CompoundFileReader(bytes);
        byte[] pool = Stream("_StringPool", table: true) ?? Array.Empty<byte>();
        byte[] data = Stream("_StringData", table: true) ?? Array.Empty<byte>();
        int cp = pool.Length >= 4 ? BinaryPrimitives.ReadUInt16LittleEndian(pool) | (BinaryPrimitives.ReadUInt16LittleEndian(pool.AsSpan(2)) & 0x7FFF) << 16 : 0;
        Codepage = cp;
        StringRefSize = pool.Length >= 4 && (BinaryPrimitives.ReadUInt16LittleEndian(pool.AsSpan(2)) & 0x8000) != 0 ? 3 : 2;
        Encoding enc = cp == 65001 ? Encoding.UTF8 : Encoding.Latin1;
        int offset = 0;
        for (int i = 1; i * 4 + 4 <= pool.Length;)
        {
            int len = BinaryPrimitives.ReadUInt16LittleEndian(pool.AsSpan(i * 4));
            int refs = BinaryPrimitives.ReadUInt16LittleEndian(pool.AsSpan(i * 4 + 2));
            if (len == 0 && refs == 0) { Strings.Add(null); RefCounts.Add(0); i++; continue; }
            if (len == 0)
            {
                len = BinaryPrimitives.ReadUInt16LittleEndian(pool.AsSpan(i * 4 + 4)) | BinaryPrimitives.ReadUInt16LittleEndian(pool.AsSpan(i * 4 + 6)) << 16;
                i += 2;
            }
            else i++;
            Strings.Add(enc.GetString(data, offset, len));
            RefCounts.Add(refs);
            offset += len;
        }
        if (offset != data.Length) throw new InvalidDataException($"string data {data.Length} bytes, the pool {offset}");

        var columnsCols = new[] { new Column("_Columns", 1, "Table", 0x2D40), new Column("_Columns", 2, "Number", 0x2502), new Column("_Columns", 3, "Name", 0x0D40), new Column("_Columns", 4, "Type", 0x0502) };
        foreach (var row in RawRows("_Columns", columnsCols))
            Columns.Add(new Column((string)row[0]!, (int)row[1]!, (string)row[2]!, (int)row[3]!));
        foreach (var row in RawRows("_Tables", new[] { new Column("_Tables", 1, "Name", 0x2D40) })) Tables.Add((string)row[0]!);
    }

    public static string EncodeName(string name, bool table)
    {
        static int Mime(char c) => c switch
        {
            >= '0' and <= '9' => c - '0',
            >= 'A' and <= 'Z' => c - 'A' + 10,
            >= 'a' and <= 'z' => c - 'a' + 36,
            '.' => 62,
            '_' => 63,
            _ => -1,
        };
        var sb = new StringBuilder();
        if (table) sb.Append('䡀');
        for (int i = 0; i < name.Length; i++)
        {
            int a = Mime(name[i]);
            if (a < 0) { sb.Append(name[i]); continue; }
            if (i + 1 < name.Length && Mime(name[i + 1]) is int b and >= 0) { sb.Append((char)(0x3800 + a + (b << 6))); i++; }
            else sb.Append((char)(0x4800 + a));
        }
        return sb.ToString();
    }

    public byte[]? Stream(string name, bool table = false) => File.Read(EncodeName(name, table));

    public List<object?[]> Rows(string table) => RawRows(table, Columns.Where(c => c.Table == table).OrderBy(c => c.Number).ToArray());

    /// <summary>A table's stored values as they are: string ids resolved, integers decoded, binary columns raw.</summary>
    public List<object?[]> RawRows(string table, Column[] cols, bool raw = false)
    {
        byte[] data = Stream(table, table: true) ?? Array.Empty<byte>();
        int Width(Column c) => (c.Type & 0x0900) == 0x0900 && (c.Type & 0x0400) == 0 ? 2 : (c.Type & 0x0800) != 0 ? StringRefSize : (c.Type & 0xFF) <= 2 ? 2 : 4;
        int rowSize = cols.Sum(Width);
        if (rowSize == 0) return new();
        int count = data.Length / rowSize;
        var rows = new List<object?[]>();
        for (int r = 0; r < count; r++)
        {
            var row = new object?[cols.Length];
            int colOffset = 0;
            for (int c = 0; c < cols.Length; c++)
            {
                int w = Width(cols[c]);
                int at = colOffset * count + r * w;
                uint v = 0;
                for (int k = 0; k < w; k++) v |= (uint)data[at + k] << (8 * k);
                bool binary = (cols[c].Type & 0x0900) == 0x0900 && (cols[c].Type & 0x0400) == 0;
                if (raw || binary) row[c] = (int)v;
                else if ((cols[c].Type & 0x0800) != 0) row[c] = v == 0 ? null : Strings[(int)v];
                else if (v == 0) row[c] = null;
                else row[c] = w == 2 ? (int)v - 0x8000 : (int)(v ^ 0x80000000);
                colOffset += w;
            }
            rows.Add(row);
        }
        return rows;
    }
}
