// A macOS package's Bom ("bill of materials": the installed files, their modes, sizes and checksums), as mkbom writes
// it from a listing -- here in managed code, so that a macOS package is made on any machine.
//
// The BOMStore file: a header (magic, the block index's and the variables' place), numbered blocks, the index of their
// offsets and lengths, and the named variables -- BomInfo, and B-trees: Paths (every entry, keyed by its parent's id and
// its name, in that order), and the empty HLIndex, VIndex and Size64 trees mkbom writes beside it. All big-endian.
//
//   Paths' leaves      up to 256 entries, chained both ways; an entry is (its info's block, its key's block)
//   Paths' branches    a pair (child, the child's last key) per child, the last pair's key empty
//   an entry's info    its id, and the block of its details: type (file 1, directory 2, link 3), mode, owner and
//                      group, size, checksum (the cksum of a file's bytes, of a link's target), a link's target

using System.Buffers.Binary;
using System.Text;

namespace WpfWebGpu.Packager.Desktop;

internal static class Bom
{
    /// <summary>One installed entry, as a listing line has it ("./path", mode, uid/gid, size, cksum, link target).</summary>
    internal sealed record Entry(string Path, int Mode, int Uid, int Gid, long Size = 0, uint Checksum = 0, string? LinkTarget = null);

    private const int LeafCapacity = 256, NodeSize = 4096;

    /// <summary>Reads a listing as mkbom -i reads it.</summary>
    public static List<Entry> ReadListing(string text)
    {
        var entries = new List<Entry>();
        foreach (string line in text.Split('\n'))
        {
            if (line.Length == 0) continue;
            string[] f = line.Split('\t');
            int mode = Convert.ToInt32(f[1], 8);
            string[] owner = f[2].Split('/');
            long size = f.Length > 3 ? long.Parse(f[3], System.Globalization.CultureInfo.InvariantCulture) : 0;
            uint sum = f.Length > 4 ? uint.Parse(f[4], System.Globalization.CultureInfo.InvariantCulture) : 0;
            string? target = f.Length > 5 ? f[5] : null;
            entries.Add(new Entry(f[0], mode, int.Parse(owner[0]), int.Parse(owner[1]), size, sum, target));
        }
        return entries;
    }

    /// <summary>Writes the Bom of <paramref name="entries"/> (in their listing order: parents before children).</summary>
    public static void Write(IReadOnlyList<Entry> entries, string path)
    {
        var blocks = new List<byte[]> { Array.Empty<byte>() };          // block 0 is null
        int Add(byte[] b) { blocks.Add(b); return blocks.Count - 1; }

        // ids in listing order; each entry's parent by its path
        var ids = new Dictionary<string, int>(StringComparer.Ordinal);
        var keyed = new List<(int Parent, string Name, int Info, int Key)>();
        long total = 0;
        for (int i = 0; i < entries.Count; i++)
        {
            Entry e = entries[i];
            int id = i + 1;
            ids[e.Path] = id;
            int slash = e.Path.LastIndexOf('/');
            string parentPath = slash < 0 ? "" : e.Path[..slash];
            int parent = e.Path == "." ? 0 : ids.TryGetValue(parentPath, out int p) ? p : throw new PackagerError($"bom: {e.Path} is listed before its folder");
            string name = e.Path == "." ? "." : e.Path[(slash + 1)..];

            int type = (e.Mode & 0xF000) switch { 0x4000 => 2, 0xA000 => 3, _ => 1 };
            uint checksum = type == 3 ? MacComponent.Cksum.Of(Encoding.UTF8.GetBytes(e.LinkTarget ?? "")) : e.Checksum;
            if (type != 2) total += e.Size;
            byte[] link = type == 3 ? Encoding.UTF8.GetBytes((e.LinkTarget ?? "") + "\0") : Array.Empty<byte>();
            var details = new byte[31 + link.Length + (type == 1 ? 4 : type == 3 ? 8 : 0)];
            details[0] = (byte)type;
            details[1] = 1;
            BinaryPrimitives.WriteUInt16BigEndian(details.AsSpan(2), 3);            // architecture, as mkbom writes it
            BinaryPrimitives.WriteUInt16BigEndian(details.AsSpan(4), (ushort)e.Mode);
            BinaryPrimitives.WriteUInt32BigEndian(details.AsSpan(6), (uint)e.Uid);
            BinaryPrimitives.WriteUInt32BigEndian(details.AsSpan(10), (uint)e.Gid);
            // modification time (14) 0, as a listing has none
            BinaryPrimitives.WriteUInt32BigEndian(details.AsSpan(18), type == 2 ? 0 : (uint)e.Size);
            details[22] = 1;
            BinaryPrimitives.WriteUInt32BigEndian(details.AsSpan(23), type == 2 ? 0 : checksum);
            BinaryPrimitives.WriteUInt32BigEndian(details.AsSpan(27), (uint)link.Length);
            link.CopyTo(details, 31);
            int detailsBlock = Add(details);

            var info = new byte[8];
            BinaryPrimitives.WriteUInt32BigEndian(info, (uint)id);
            BinaryPrimitives.WriteUInt32BigEndian(info.AsSpan(4), (uint)detailsBlock);
            int infoBlock = Add(info);

            byte[] nameBytes = Encoding.UTF8.GetBytes(name + "\0");
            var key = new byte[4 + nameBytes.Length];
            BinaryPrimitives.WriteUInt32BigEndian(key, (uint)parent);
            nameBytes.CopyTo(key, 4);
            keyed.Add((parent, name, infoBlock, Add(key)));
        }
        keyed.Sort((a, b) => a.Parent != b.Parent ? a.Parent.CompareTo(b.Parent) : string.CompareOrdinal(a.Name, b.Name));

        // the leaves, then branches over them until one node is left
        var level = new List<(int Block, int LastKey)>();
        var leafBlocks = new List<int>();
        for (int at = 0; at < keyed.Count || at == 0; at += LeafCapacity)
        {
            var chunk = keyed.Skip(at).Take(LeafCapacity).ToList();
            int block = Add(new byte[NodeSize]);
            leafBlocks.Add(block);
            byte[] node = blocks[block];
            BinaryPrimitives.WriteUInt16BigEndian(node, 1);
            BinaryPrimitives.WriteUInt16BigEndian(node.AsSpan(2), (ushort)chunk.Count);
            for (int i = 0; i < chunk.Count; i++)
            {
                BinaryPrimitives.WriteUInt32BigEndian(node.AsSpan(12 + i * 8), (uint)chunk[i].Info);
                BinaryPrimitives.WriteUInt32BigEndian(node.AsSpan(16 + i * 8), (uint)chunk[i].Key);
            }
            level.Add((block, chunk.Count > 0 ? chunk[^1].Key : 0));
            if (keyed.Count == 0) break;
        }
        for (int i = 0; i < leafBlocks.Count; i++)
        {
            byte[] node = blocks[leafBlocks[i]];
            BinaryPrimitives.WriteUInt32BigEndian(node.AsSpan(4), i + 1 < leafBlocks.Count ? (uint)leafBlocks[i + 1] : 0);
            BinaryPrimitives.WriteUInt32BigEndian(node.AsSpan(8), i > 0 ? (uint)leafBlocks[i - 1] : 0);
        }
        int maxChildren = (NodeSize - 12) / 8;
        while (level.Count > 1)
        {
            var up = new List<(int Block, int LastKey)>();
            for (int at = 0; at < level.Count; at += maxChildren)
            {
                var children = level.Skip(at).Take(maxChildren).ToList();
                int block = Add(new byte[NodeSize]);
                byte[] node = blocks[block];
                BinaryPrimitives.WriteUInt16BigEndian(node, 0);
                BinaryPrimitives.WriteUInt16BigEndian(node.AsSpan(2), (ushort)(children.Count - 1));
                for (int i = 0; i < children.Count; i++)
                {
                    BinaryPrimitives.WriteUInt32BigEndian(node.AsSpan(12 + i * 8), (uint)children[i].Block);
                    BinaryPrimitives.WriteUInt32BigEndian(node.AsSpan(16 + i * 8), i + 1 < children.Count ? (uint)children[i].LastKey : 0);
                }
                up.Add((block, children[^1].LastKey));
            }
            level = up;
        }
        int paths = Add(Tree(level[0].Block, NodeSize, keyed.Count));

        // BomInfo, and the empty trees mkbom writes beside Paths
        var bomInfo = new byte[28];
        BinaryPrimitives.WriteUInt32BigEndian(bomInfo, 1);
        BinaryPrimitives.WriteUInt32BigEndian(bomInfo.AsSpan(4), (uint)(entries.Count + 1));
        BinaryPrimitives.WriteUInt32BigEndian(bomInfo.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt32BigEndian(bomInfo.AsSpan(20), (uint)Math.Min(total, uint.MaxValue));
        int info0 = Add(bomInfo);
        int hl = Add(Tree(Add(EmptyLeaf(NodeSize)), NodeSize, 0));
        var vindex = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(vindex, 1);
        BinaryPrimitives.WriteUInt32BigEndian(vindex.AsSpan(4), (uint)Add(Tree(Add(EmptyLeaf(128)), 128, 0)));
        int vi = Add(vindex);
        int size64 = Add(Tree(Add(EmptyLeaf(NodeSize)), NodeSize, 0));

        // the file: header, blocks, variables, block index
        using var output = new MemoryStream();
        output.Write(new byte[512]);
        var offsets = new (uint Address, uint Length)[blocks.Count];
        for (int i = 1; i < blocks.Count; i++)
        {
            offsets[i] = ((uint)output.Position, (uint)blocks[i].Length);
            output.Write(blocks[i]);
        }
        uint varsOffset = (uint)output.Position;
        var vars = new MemoryStream();
        void Var(string name, int block)
        {
            Span<byte> id = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(id, (uint)block);
            vars.Write(id);
            vars.WriteByte((byte)name.Length);
            vars.Write(Encoding.ASCII.GetBytes(name));
        }
        Span<byte> count = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(count, 5);
        vars.Write(count);
        Var("BomInfo", info0);
        Var("Paths", paths);
        Var("HLIndex", hl);
        Var("VIndex", vi);
        Var("Size64", size64);
        output.Write(vars.ToArray());
        uint indexOffset = (uint)output.Position;
        var index = new byte[4 + blocks.Count * 8 + 4 + 2 * 8];
        BinaryPrimitives.WriteUInt32BigEndian(index, (uint)blocks.Count);
        for (int i = 0; i < blocks.Count; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(index.AsSpan(4 + i * 8), offsets[i].Address);
            BinaryPrimitives.WriteUInt32BigEndian(index.AsSpan(8 + i * 8), offsets[i].Length);
        }
        BinaryPrimitives.WriteUInt32BigEndian(index.AsSpan(4 + blocks.Count * 8), 2);   // an empty free list of two
        output.Write(index);

        byte[] file = output.ToArray();
        Encoding.ASCII.GetBytes("BOMStore").CopyTo(file, 0);
        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(12), (uint)(blocks.Count - 1));
        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(16), indexOffset);
        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(20), (uint)index.Length);
        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(24), varsOffset);
        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(28), (uint)vars.Length);
        File.WriteAllBytes(path, file);
    }

    // "tree", version 1, the root node's block, the node size, the entries, 0
    private static byte[] Tree(int root, int nodeSize, int count)
    {
        var t = new byte[21];
        Encoding.ASCII.GetBytes("tree").CopyTo(t, 0);
        BinaryPrimitives.WriteUInt32BigEndian(t.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32BigEndian(t.AsSpan(8), (uint)root);
        BinaryPrimitives.WriteUInt32BigEndian(t.AsSpan(12), (uint)nodeSize);
        BinaryPrimitives.WriteUInt32BigEndian(t.AsSpan(16), (uint)count);
        return t;
    }

    private static byte[] EmptyLeaf(int size)
    {
        var node = new byte[size];
        BinaryPrimitives.WriteUInt16BigEndian(node, 1);
        return node;
    }
}
