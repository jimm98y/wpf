// xar, the archive a flat macOS package is: what pkgutil --flatten and productbuild write, here in managed code.
//
//   header      "xar!", its size (28), version 1, the table of contents' compressed and full lengths, SHA-1 as the
//               checksum's algorithm -- big-endian
//   TOC         XML, zlib-compressed: every file and folder, and where each file's data is in the heap
//   heap        first the TOC's checksum (the SHA-1 of the compressed TOC), then the files' data, each with its
//               archived and extracted SHA-1 and its encoding: stored (a Payload, already gzipped) or zlib
//               ("application/x-gzip": a Bom, PackageInfo, the Distribution)
//
// A file's data is read twice, once for its checksum and once into the archive, so that a payload of gigabytes is
// never held: the TOC, before it, has to know where everything is.

using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Security;
using System.Text;
using System.Xml.Linq;

namespace WpfWebGpu.Packager.Desktop;

internal sealed class XarWriter
{
    private abstract record Node(string Name);
    private sealed record Folder(string Name, List<Node> Children) : Node(Name);
    private sealed record Data(string Name, Func<Stream> Open, bool Compress, int Mode) : Node(Name);
    /// <summary>A file copied from another archive as it is archived there: its bytes, encoding and checksums.</summary>
    private sealed record Copied(string Name, string Source, long Offset, long Length, long Size, string Encoding, string Archived, string Extracted) : Node(Name);

    private readonly Folder _root = new("", new List<Node>());

    private Folder FolderOf(string path)
    {
        Folder at = _root;
        foreach (string part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (at.Children.FirstOrDefault(c => c.Name == part) is not Folder f)
            {
                f = new Folder(part, new List<Node>());
                at.Children.Add(f);
            }
            at = f;
        }
        return at;
    }

    /// <summary>A file at <paramref name="path"/> ('/'-separated), zlib-compressed or stored.</summary>
    public void AddFile(string path, string sourceFile, bool compress, int mode = 0b110_100_100) =>
        Add(path, new Data(Path.GetFileName(path), () => File.OpenRead(sourceFile), compress, mode));

    public void AddBytes(string path, byte[] bytes, bool compress, int mode = 0b110_100_100) =>
        Add(path, new Data(Path.GetFileName(path), () => new MemoryStream(bytes, writable: false), compress, mode));

    private void Add(string path, Node node)
    {
        int slash = path.LastIndexOf('/');
        FolderOf(slash < 0 ? "" : path[..slash]).Children.Add(node);
    }

    /// <summary>Every file of the flat package (or any xar) <paramref name="archive"/>, under <paramref name="folder"/>,
    /// copied as it is archived there.</summary>
    public void AddArchive(string folder, string archive)
    {
        using var input = File.OpenRead(archive);
        var (toc, heap) = XarReader.Toc(input);
        void Walk(XElement parent, string at)
        {
            foreach (XElement f in parent.Elements("file"))
            {
                string name = (string)f.Element("name")!;
                string path = at.Length == 0 ? name : at + "/" + name;
                if ((string?)f.Element("type") == "directory") { FolderOf(path); Walk(f, path); continue; }
                XElement d = f.Element("data")!;
                Add(path, new Copied(name, archive, heap + (long)d.Element("offset")!, (long)d.Element("length")!, (long)d.Element("size")!,
                    (string)d.Element("encoding")!.Attribute("style")!, (string)d.Element("archived-checksum")!, (string)d.Element("extracted-checksum")!));
            }
        }
        Walk(toc.Root!.Element("toc")!, folder);
    }

    public void Write(string output)
    {
        // the data first, in memory where it is compressed (small files) or as a promise to copy (the rest), with offsets
        long offset = 20;                                       // after the TOC's checksum
        var placed = new List<(Node Node, byte[]? Bytes, long Offset, long Length, long Size, string Archived, string Extracted)>();
        int id = 0;
        XElement Element(Node node)
        {
            var file = new XElement("file", new XAttribute("id", ++id), new XElement("name", node.Name));
            switch (node)
            {
                case Folder f:
                    file.Add(new XElement("type", "directory"), Owner(0b111_000_000));
                    foreach (Node child in f.Children) file.Add(Element(child));
                    break;
                case Data d:
                {
                    byte[]? bytes = null;
                    string extracted, archived;
                    long size, length;
                    using (Stream s = d.Open())
                    {
                        if (d.Compress)
                        {
                            byte[] raw = ReadAll(s);
                            using var ms = new MemoryStream();
                            using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(raw);
                            bytes = ms.ToArray();
                            size = raw.Length;
                            extracted = Hex(SHA1.HashData(raw));
                            archived = Hex(SHA1.HashData(bytes));
                            length = bytes.Length;
                        }
                        else
                        {
                            size = length = s.Length;
                            extracted = archived = Hex(SHA1.HashData(s));
                        }
                    }
                    placed.Add((node, bytes, offset, length, size, archived, extracted));
                    file.Add(new XElement("type", "file"), Owner(d.Mode), DataElement(offset, length, size, d.Compress ? "application/x-gzip" : "application/octet-stream", archived, extracted));
                    offset += length;
                    break;
                }
                case Copied c:
                    placed.Add((node, null, offset, c.Length, c.Size, c.Archived, c.Extracted));
                    file.Add(new XElement("type", "file"), Owner(0b110_100_100), DataElement(offset, c.Length, c.Size, c.Encoding, c.Archived, c.Extracted));
                    offset += c.Length;
                    break;
            }
            return file;
        }

        var toc = new XElement("toc",
            new XElement("checksum", new XAttribute("style", "sha1"), new XElement("offset", 0), new XElement("size", 20)),
            new XElement("creation-time", DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)));
        foreach (Node child in _root.Children) toc.Add(Element(child));
        var doc = new XDocument(new XDeclaration("1.0", "UTF-8", null), new XElement("xar", toc));
        byte[] xml = Encoding.UTF8.GetBytes(doc.Declaration + "\n" + doc.Root!.ToString());
        byte[] compressedToc;
        using (var ms = new MemoryStream())
        {
            using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(xml);
            compressedToc = ms.ToArray();
        }

        using var file = new FileStream(output, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        var header = new byte[28];
        Encoding.ASCII.GetBytes("xar!").CopyTo(header, 0);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(4), 28);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(6), 1);
        BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(8), (ulong)compressedToc.Length);
        BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(16), (ulong)xml.Length);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(24), 1);         // SHA-1
        file.Write(header);
        file.Write(compressedToc);
        file.Write(SHA1.HashData(compressedToc));
        foreach (var p in placed)
        {
            switch (p.Node)
            {
                case Data d when p.Bytes != null: file.Write(p.Bytes); break;
                case Data d:
                    using (Stream s = d.Open()) s.CopyTo(file, 1 << 20);
                    break;
                case Copied c:
                    using (var s = File.OpenRead(c.Source))
                    {
                        s.Position = c.Offset;
                        CopyBytes(s, file, c.Length);
                    }
                    break;
            }
        }
    }

    private static XElement[] Owner(int mode) => new[]
    {
        new XElement("mode", "0" + Convert.ToString(mode, 8)), new XElement("uid", 0), new XElement("user", "root"),
        new XElement("gid", 0), new XElement("group", "wheel"),
    };

    private static XElement DataElement(long offset, long length, long size, string encoding, string archived, string extracted) =>
        new("data",
            new XElement("length", length), new XElement("offset", offset), new XElement("size", size),
            new XElement("encoding", new XAttribute("style", encoding)),
            new XElement("archived-checksum", new XAttribute("style", "sha1"), archived),
            new XElement("extracted-checksum", new XAttribute("style", "sha1"), extracted));

    private static byte[] ReadAll(Stream s)
    {
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    private static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();

    private static void CopyBytes(Stream from, Stream to, long count)
    {
        byte[] buffer = new byte[1 << 20];
        while (count > 0)
        {
            int n = from.Read(buffer, 0, (int)Math.Min(buffer.Length, count));
            if (n == 0) throw new PackagerError("an archive ended early");
            to.Write(buffer, 0, n);
            count -= n;
        }
    }
}

internal static class XarReader
{
    /// <summary>A xar's table of contents, and where its heap starts.</summary>
    public static (XDocument Toc, long Heap) Toc(Stream s)
    {
        var header = new byte[28];
        s.ReadExactly(header);
        if (Encoding.ASCII.GetString(header, 0, 4) != "xar!") throw new PackagerError("not a xar archive");
        int headerSize = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4));
        long compressed = (long)BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(8));
        s.Position = headerSize;
        var toc = new byte[compressed];
        s.ReadExactly(toc);
        using var z = new ZLibStream(new MemoryStream(toc), CompressionMode.Decompress);
        return (XDocument.Load(z), headerSize + compressed);
    }
}
