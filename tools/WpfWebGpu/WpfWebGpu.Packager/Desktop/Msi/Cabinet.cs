// A cabinet (MS-CAB), written here: the files Windows Installer installs, each under its File table key, in the
// File table's sequence. One or more folders (each one stream of its files back to back, cut into data blocks of
// 32 KB), MSZIP compressed -- each block "CK" and a deflate stream of its own, so the blocks are compressed in
// parallel -- or stored, for a fast build.
//
//   CFHEADER   "MSCF", the cabinet's size, where the CFFILEs start, version 1.3, the folder and file counts
//   CFFOLDER   per folder: where its first block is, how many blocks, the compression (1 MSZIP, 0 none)
//   CFFILE     per file: its size, its offset in its folder's stream, the folder, its DOS date and time, its name
//   CFDATA     per block: checksum (0: none), compressed and uncompressed sizes, the data
//
// A folder has at most 65535 blocks (2 GB), a cabinet at most 65535 files.

using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace WpfWebGpu.Packager.Desktop.Msi;

public static class Cabinet
{
    public sealed record Item(string Name, string Path, long Length, DateTime Modified);

    private const int BlockSize = 32768;
    private const long FolderLimit = 60000L * BlockSize;

    /// <summary>Writes <paramref name="items"/>, in their order, into the cabinet <paramref name="path"/>.</summary>
    public static void Write(string path, IReadOnlyList<Item> items, bool compress)
    {
        if (items.Count > 0xFFFF) throw new PackagerError($"a cabinet holds at most 65535 files: {items.Count}");
        // the folders: runs of files of at most FolderLimit bytes (a file larger than that has one of its own)
        var folders = new List<List<Item>>();
        long inFolder = 0;
        foreach (var item in items)
        {
            if (item.Length > uint.MaxValue) throw new PackagerError($"{item.Path}: a cabinet's files are under 4 GB");
            if (folders.Count == 0 || inFolder > 0 && inFolder + item.Length > FolderLimit) { folders.Add(new List<Item>()); inFolder = 0; }
            folders[^1].Add(item);
            inFolder += item.Length;
        }
        foreach (var f in folders)
            if (f.Sum(i => i.Length) > 0xFFFFL * BlockSize) throw new PackagerError($"{f[0].Path}: too large for one cabinet folder");

        using var cab = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 20);
        int filesOffset = 36 + 8 * folders.Count;
        var header = new byte[filesOffset];
        cab.Write(header);   // filled in at the end

        // CFFILE
        for (int f = 0; f < folders.Count; f++)
        {
            long offset = 0;
            foreach (var item in folders[f])
            {
                byte[] name = Encoding.ASCII.GetBytes(item.Name + "\0");
                var entry = new byte[16];
                BinaryPrimitives.WriteUInt32LittleEndian(entry, (uint)item.Length);
                BinaryPrimitives.WriteUInt32LittleEndian(entry.AsSpan(4), (uint)offset);
                BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(8), (ushort)f);
                var (date, time) = Dos(item.Modified);
                BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(10), date);
                BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(12), time);
                BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(14), 0x20);   // archive
                cab.Write(entry);
                cab.Write(name);
                offset += item.Length;
            }
        }

        // CFDATA, folder by folder
        var folderStarts = new uint[folders.Count];
        var folderBlocks = new int[folders.Count];
        for (int f = 0; f < folders.Count; f++)
        {
            folderStarts[f] = checked((uint)cab.Position);
            folderBlocks[f] = WriteFolder(cab, folders[f], compress);
        }
        long size = cab.Length;
        if (size > uint.MaxValue) throw new PackagerError($"{path}: a cabinet is under 4 GB");

        BinaryPrimitives.WriteUInt32LittleEndian(header, 0x4643534D);                 // "MSCF"
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), (uint)size);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), (uint)filesOffset);
        header[24] = 3;                                                                // version 1.3
        header[25] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(26), (ushort)folders.Count);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(28), (ushort)items.Count);
        for (int f = 0; f < folders.Count; f++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(36 + f * 8), folderStarts[f]);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(40 + f * 8), (ushort)folderBlocks[f]);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(42 + f * 8), (ushort)(compress ? 1 : 0));
        }
        cab.Position = 0;
        cab.Write(header);
    }

    // the folder's files as one stream, in blocks, a batch of blocks compressed at a time in parallel
    private static int WriteFolder(Stream cab, List<Item> files, bool compress)
    {
        const int batch = 256;
        var raw = new byte[batch * BlockSize];
        var blocks = new byte[batch][];
        var lengths = new int[batch];
        int count = 0;
        int fileIndex = 0;
        Stream? current = null;
        long currentLeft = 0;
        try
        {
            while (true)
            {
                // fill up to a batch of blocks
                int filled = 0;
                while (filled < raw.Length)
                {
                    if (currentLeft == 0)
                    {
                        current?.Dispose();
                        current = null;
                        if (fileIndex >= files.Count) break;
                        current = File.OpenRead(files[fileIndex].Path);
                        currentLeft = files[fileIndex].Length;
                        fileIndex++;
                        continue;
                    }
                    int n = current!.Read(raw, filled, (int)Math.Min(raw.Length - filled, currentLeft));
                    if (n <= 0) throw new PackagerError($"{files[fileIndex - 1].Path} changed while it was written");
                    filled += n;
                    currentLeft -= n;
                }
                if (filled == 0) break;
                int n2 = (filled + BlockSize - 1) / BlockSize;
                for (int b = 0; b < n2; b++) lengths[b] = Math.Min(BlockSize, filled - b * BlockSize);
                if (compress) Parallel.For(0, n2, b => blocks[b] = Mszip(raw.AsSpan(b * BlockSize, lengths[b])));
                for (int b = 0; b < n2; b++)
                {
                    ReadOnlySpan<byte> data = compress ? blocks[b] : raw.AsSpan(b * BlockSize, lengths[b]);
                    var head = new byte[8];
                    BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(4), (ushort)data.Length);
                    BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(6), (ushort)lengths[b]);
                    cab.Write(head);
                    cab.Write(data);
                    count++;
                }
                if (filled < raw.Length) break;
            }
        }
        finally
        {
            current?.Dispose();
        }
        if (count > 0xFFFF) throw new InvalidOperationException("a folder of more than 65535 blocks");
        return count;
    }

    // "CK", then the block as a deflate stream of its own; where deflate does not make it smaller, the block stored
    // (a final stored deflate block)
    private static byte[] Mszip(ReadOnlySpan<byte> block)
    {
        var ms = new MemoryStream(block.Length + 64);
        ms.WriteByte((byte)'C');
        ms.WriteByte((byte)'K');
        using (var deflate = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true)) deflate.Write(block);
        if (ms.Length <= block.Length + 2 + 5) return ms.ToArray();
        var stored = new byte[2 + 5 + block.Length];
        stored[0] = (byte)'C';
        stored[1] = (byte)'K';
        stored[2] = 1;   // BFINAL, stored
        BinaryPrimitives.WriteUInt16LittleEndian(stored.AsSpan(3), (ushort)block.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(stored.AsSpan(5), (ushort)~block.Length);
        block.CopyTo(stored.AsSpan(7));
        return stored;
    }

    private static (ushort Date, ushort Time) Dos(DateTime t)
    {
        if (t.Year < 1980) t = new DateTime(1980, 1, 1);
        if (t.Year > 2107) t = new DateTime(2107, 12, 31);
        return ((ushort)(((t.Year - 1980) << 9) | (t.Month << 5) | t.Day), (ushort)((t.Hour << 11) | (t.Minute << 5) | (t.Second / 2)));
    }
}
