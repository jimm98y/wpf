// A compound file (MS-CFB), written here: the container a Windows Installer database is, its tables and streams
// each a stream in it. Version 3: 512-byte sectors, 64-byte mini sectors for the streams under 4096 bytes, each
// stream's sectors in one run. The streams are given as files or bytes and copied in once; nothing large is held in
// memory. The directory is a red-black tree of the root's children, built balanced (its deepest level red where the
// tree is not complete, so every path has the same number of black nodes).
//
// A stream is under 2 GB (version 3's limit), and so is the whole file for Windows Installer to open it.

using System.Buffers.Binary;
using System.Text;

namespace WpfWebGpu.Packager.Desktop.Msi;

public sealed class CompoundFile
{
    private const int SectorSize = 512, MiniSectorSize = 64, MiniCutoff = 4096;
    private const uint EndOfChain = 0xFFFFFFFE, FreeSect = 0xFFFFFFFF, FatSect = 0xFFFFFFFD, DifSect = 0xFFFFFFFC, NoStream = 0xFFFFFFFF;

    private sealed class Item
    {
        public required string Name { get; init; }
        public byte[]? Bytes { get; init; }
        public string? Path { get; init; }
        public required long Length { get; init; }
        public uint Start;
        public int Left = -1, Right = -1;
        public bool Red;
        public Stream Open() => Bytes != null ? new MemoryStream(Bytes, writable: false) : File.OpenRead(Path!);
    }

    private readonly List<Item> _items = new();

    /// <summary>The root storage's class: a Windows Installer database's is {000C1084-0000-0000-C000-000000000046}.</summary>
    public Guid RootClsid { get; set; }

    public void Add(string name, byte[] data) => Add(new Item { Name = name, Bytes = data, Length = data.Length });

    public void Add(string name, string path) => Add(new Item { Name = name, Path = path, Length = new FileInfo(path).Length });

    private void Add(Item item)
    {
        if (item.Name.Length > 31) throw new PackagerError($"a compound file's stream name is at most 31 characters: {item.Name.Length}");
        if (item.Length >= 0x80000000L) throw new PackagerError($"a stream is under 2 GB: {item.Length >> 20} MB");
        if (_items.Any(i => Compare(i.Name, item.Name) == 0)) throw new PackagerError($"two streams named {Escape(item.Name)}");
        _items.Add(item);
    }

    public bool Contains(string name) => _items.Any(i => i.Name == name);

    private static string Escape(string s) => string.Concat(s.Select(c => c < 0x20 || c > 0x7E ? $"\\u{(int)c:X4}" : c.ToString()));

    // the directory's order: the shorter name first, then by each character, upper-cased
    private static int Compare(string a, string b)
    {
        if (a.Length != b.Length) return a.Length.CompareTo(b.Length);
        for (int i = 0; i < a.Length; i++)
        {
            int d = char.ToUpperInvariant(a[i]).CompareTo(char.ToUpperInvariant(b[i]));
            if (d != 0) return d;
        }
        return 0;
    }

    /// <summary>The Authenticode digest of a Windows Installer database: each stream's contents in the order of their
    /// names' UTF-16 bytes (the digital signature's own left out, as are empty streams), then the root's class.</summary>
    public byte[] ContentDigest(System.Security.Cryptography.HashAlgorithmName algorithm, params string[] excluded)
    {
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(algorithm);
        var buffer = new byte[1 << 20];
        foreach (var item in _items.Where(i => !excluded.Contains(i.Name)).OrderBy(i => i, Comparer<Item>.Create(HashOrder)))
        {
            if (item.Length == 0) continue;
            using var s = item.Open();
            int n;
            while ((n = s.Read(buffer, 0, buffer.Length)) > 0) hash.AppendData(buffer, 0, n);
        }
        hash.AppendData(RootClsid.ToByteArray());
        return hash.GetHashAndReset();
    }

    // the names as the directory holds them (UTF-16LE with the terminating zero), compared byte by byte; of two
    // that agree as far as the shorter goes, the longer first
    private static int HashOrder(Item a, Item b)
    {
        byte[] x = Encoding.Unicode.GetBytes(a.Name + "\0"), y = Encoding.Unicode.GetBytes(b.Name + "\0");
        int n = Math.Min(x.Length, y.Length);
        for (int i = 0; i < n; i++)
            if (x[i] != y[i]) return x[i].CompareTo(y[i]);
        return x.Length == y.Length ? 0 : x.Length > y.Length ? -1 : 1;
    }

    public void Write(string path)
    {
        var large = _items.Where(i => i.Length >= MiniCutoff).ToList();
        var small = _items.Where(i => i.Length > 0 && i.Length < MiniCutoff).ToList();

        // the mini stream: the small streams, each in a run of mini sectors
        uint miniSectors = 0;
        foreach (var item in small) { item.Start = miniSectors; miniSectors += (uint)((item.Length + MiniSectorSize - 1) / MiniSectorSize); }
        foreach (var item in _items.Where(i => i.Length == 0)) item.Start = EndOfChain;
        long miniStreamLength = (long)miniSectors * MiniSectorSize;

        // the sectors, in this order: the large streams, the mini stream, the mini FAT, the directory, the FAT, the DIFAT
        uint next = 0;
        foreach (var item in large) { item.Start = next; next += Sectors(item.Length); }
        uint miniStreamStart = miniStreamLength > 0 ? next : EndOfChain;
        next += Sectors(miniStreamLength);
        uint miniFatSectors = Sectors(miniSectors * 4L);
        uint miniFatStart = miniFatSectors > 0 ? next : EndOfChain;
        next += miniFatSectors;
        uint directoryEntries = (uint)_items.Count + 1;
        uint directorySectors = (directoryEntries + 3) / 4;
        uint directoryStart = next;
        next += directorySectors;
        uint fatSectors = 0, difatSectors = 0;
        while (true)
        {
            uint total = next + fatSectors + difatSectors;
            uint fat = (total + 127) / 128;
            uint dif = fat > 109 ? (fat - 109 + 126) / 127 : 0;
            if (fat == fatSectors && dif == difatSectors) break;
            fatSectors = fat;
            difatSectors = dif;
        }
        uint fatStart = next, difatStart = next + fatSectors;
        uint totalSectors = next + fatSectors + difatSectors;

        var fatTable = new uint[fatSectors * 128];
        Array.Fill(fatTable, FreeSect);
        void Run(uint start, uint count)
        {
            for (uint i = 0; i < count; i++) fatTable[start + i] = i + 1 < count ? start + i + 1 : EndOfChain;
        }
        foreach (var item in large) Run(item.Start, Sectors(item.Length));
        if (miniStreamLength > 0) Run(miniStreamStart, Sectors(miniStreamLength));
        Run(miniFatStart, miniFatSectors);
        Run(directoryStart, directorySectors);
        for (uint i = 0; i < fatSectors; i++) fatTable[fatStart + i] = FatSect;
        for (uint i = 0; i < difatSectors; i++) fatTable[difatStart + i] = DifSect;

        // the tree of the root's children
        var sorted = _items.OrderBy(i => i.Name, Comparer<string>.Create(Compare)).ToList();
        int root = Build(sorted, 0, sorted.Count - 1, 0, out int maxDepth);
        int minLeaf = MinLeafDepth(sorted, root, 0);
        if (minLeaf != maxDepth) Colour(sorted, root, 0, maxDepth);

        using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        var header = new byte[SectorSize];
        BinaryPrimitives.WriteUInt64LittleEndian(header, 0xE11AB1A1E011CFD0);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(24), 0x003E);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(26), 3);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(28), 0xFFFE);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(30), 9);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(32), 6);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(44), fatSectors);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(48), directoryStart);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(56), MiniCutoff);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(60), miniFatStart);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(64), miniFatSectors);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(68), difatSectors > 0 ? difatStart : EndOfChain);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(72), difatSectors);
        for (int i = 0; i < 109; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(76 + i * 4), i < fatSectors ? fatStart + (uint)i : FreeSect);
        file.Write(header);

        var buffer = new byte[1 << 20];
        void Copy(Item item)
        {
            using var s = item.Open();
            long left = item.Length;
            while (left > 0)
            {
                int n = s.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
                if (n <= 0) throw new PackagerError($"{item.Path ?? Escape(item.Name)} ended early");
                file.Write(buffer, 0, n);
                left -= n;
            }
        }
        void Pad(long length, int unit)
        {
            int rest = (int)(length % unit);
            if (rest != 0) file.Write(new byte[unit - rest]);
        }
        foreach (var item in large) { Copy(item); Pad(item.Length, SectorSize); }
        foreach (var item in small) { Copy(item); Pad(item.Length, MiniSectorSize); }
        Pad(miniStreamLength, SectorSize);

        var miniFat = new byte[miniFatSectors * SectorSize];
        miniFat.AsSpan().Fill(0xFF);
        foreach (var item in small)
        {
            uint count = (uint)((item.Length + MiniSectorSize - 1) / MiniSectorSize);
            for (uint i = 0; i < count; i++)
                BinaryPrimitives.WriteUInt32LittleEndian(miniFat.AsSpan((int)(item.Start + i) * 4), i + 1 < count ? item.Start + i + 1 : EndOfChain);
        }
        file.Write(miniFat);

        var directory = new byte[directorySectors * SectorSize];
        Entry(directory.AsSpan(0, 128), "Root Entry", 5, red: false, -1, -1, root, RootClsid, miniStreamStart, miniStreamLength);
        for (int i = 0; i < sorted.Count; i++)
        {
            var item = sorted[i];
            Entry(directory.AsSpan((i + 1) * 128, 128), item.Name, 2, item.Red, item.Left, item.Right, -1, Guid.Empty, item.Start, item.Length);
        }
        for (int i = sorted.Count + 1; i < directorySectors * 4; i++)
        {
            var e = directory.AsSpan(i * 128, 128);
            BinaryPrimitives.WriteUInt32LittleEndian(e[68..], NoStream);
            BinaryPrimitives.WriteUInt32LittleEndian(e[72..], NoStream);
            BinaryPrimitives.WriteUInt32LittleEndian(e[76..], NoStream);
        }
        file.Write(directory);

        var fatBytes = new byte[fatTable.Length * 4];
        for (int i = 0; i < fatTable.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(fatBytes.AsSpan(i * 4), fatTable[i]);
        file.Write(fatBytes);

        var difat = new byte[difatSectors * SectorSize];
        difat.AsSpan().Fill(0xFF);
        for (uint f = 109; f < fatSectors; f++)
        {
            uint slot = (f - 109) % 127, d = (f - 109) / 127;
            BinaryPrimitives.WriteUInt32LittleEndian(difat.AsSpan((int)(d * SectorSize + slot * 4)), fatStart + f);
        }
        for (uint d = 0; d < difatSectors; d++)
            BinaryPrimitives.WriteUInt32LittleEndian(difat.AsSpan((int)(d * SectorSize + 127 * 4)), d + 1 < difatSectors ? difatStart + d + 1 : EndOfChain);
        file.Write(difat);
        if (file.Length != (totalSectors + 1L) * SectorSize) throw new InvalidOperationException("compound file length");
    }

    private static uint Sectors(long length) => (uint)((length + SectorSize - 1) / SectorSize);

    // the directory's entries are numbered from 1 (0 is the root); a subtree's root is its middle name
    private static int Build(List<Item> sorted, int from, int to, int depth, out int maxDepth)
    {
        maxDepth = depth - 1;
        if (from > to) return -1;
        int mid = (from + to) / 2;
        sorted[mid].Left = Build(sorted, from, mid - 1, depth + 1, out int l);
        sorted[mid].Right = Build(sorted, mid + 1, to, depth + 1, out int r);
        maxDepth = Math.Max(depth, Math.Max(l, r));
        return mid + 1;
    }

    private static int MinLeafDepth(List<Item> sorted, int node, int depth)
    {
        if (node < 0) return depth - 1;
        var item = sorted[node - 1];
        if (item.Left < 0 || item.Right < 0) return depth;
        return Math.Min(MinLeafDepth(sorted, item.Left, depth + 1), MinLeafDepth(sorted, item.Right, depth + 1));
    }

    private static void Colour(List<Item> sorted, int node, int depth, int maxDepth)
    {
        if (node < 0) return;
        var item = sorted[node - 1];
        item.Red = depth == maxDepth;
        Colour(sorted, item.Left, depth + 1, maxDepth);
        Colour(sorted, item.Right, depth + 1, maxDepth);
    }

    private static void Entry(Span<byte> e, string name, byte type, bool red, int left, int right, int child, Guid clsid, uint start, long size)
    {
        byte[] n = Encoding.Unicode.GetBytes(name + "\0");
        n.CopyTo(e);
        BinaryPrimitives.WriteUInt16LittleEndian(e[64..], (ushort)n.Length);
        e[66] = type;
        e[67] = (byte)(red ? 0 : 1);
        BinaryPrimitives.WriteUInt32LittleEndian(e[68..], left < 0 ? NoStream : (uint)left);
        BinaryPrimitives.WriteUInt32LittleEndian(e[72..], right < 0 ? NoStream : (uint)right);
        BinaryPrimitives.WriteUInt32LittleEndian(e[76..], child < 0 ? NoStream : (uint)child);
        clsid.TryWriteBytes(e.Slice(80, 16));
        BinaryPrimitives.WriteUInt32LittleEndian(e[116..], size == 0 ? EndOfChain : start);
        BinaryPrimitives.WriteUInt64LittleEndian(e[120..], (ulong)size);
    }
}
