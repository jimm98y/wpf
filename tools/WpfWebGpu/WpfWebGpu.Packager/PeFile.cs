// What the scanner reads of a Windows executable or assembly: whether it is a window app or a console one
// (the subsystem), its machine, whether it is managed and IL-only, what it references, and its icon.

using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace WpfWebGpu.Packager;

public sealed class PeFile
{
    public bool Gui { get; private init; }
    public Machine Machine { get; private init; }
    public bool Managed { get; private init; }
    /// <summary>Managed and IL-only: no native code of its own (C++/CLI's mixed-mode assemblies are not).</summary>
    public bool ILOnly { get; private init; }
    /// <summary>Managed and compiled for 32-bit only (x86, or AnyCPU with 32-bit required).</summary>
    public bool Requires32Bit { get; private init; }
    public IReadOnlyList<string> AssemblyReferences { get; private init; } = Array.Empty<string>();
    public IReadOnlyList<string> TypeReferences { get; private init; } = Array.Empty<string>();

    public static PeFile? TryRead(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            var headers = pe.PEHeaders;
            if (headers.PEHeader == null) return null;
            var cor = headers.CorHeader;
            var asmRefs = new List<string>();
            var typeRefs = new List<string>();
            if (pe.HasMetadata)
            {
                var md = pe.GetMetadataReader();
                foreach (var h in md.AssemblyReferences) asmRefs.Add(md.GetString(md.GetAssemblyReference(h).Name));
                foreach (var h in md.TypeReferences) typeRefs.Add(md.GetString(md.GetTypeReference(h).Name));
            }
            return new PeFile
            {
                Gui = headers.PEHeader.Subsystem == Subsystem.WindowsGui,
                Machine = headers.CoffHeader.Machine,
                Managed = pe.HasMetadata,
                ILOnly = cor != null && (cor.Flags & CorFlags.ILOnly) != 0,
                Requires32Bit = cor != null && ((cor.Flags & CorFlags.Requires32Bit) != 0
                    || (headers.CoffHeader.Machine == Machine.I386 && (cor.Flags & CorFlags.ILOnly) == 0)),
                AssemblyReferences = asmRefs,
                TypeReferences = typeRefs,
            };
        }
        catch (BadImageFormatException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    // ---- the icon -------------------------------------------------------------------------------------
    //
    // An .exe's icon is a group (RT_GROUP_ICON: the sizes it has) of images (RT_ICON, by id); the first
    // group is the one Explorer shows. An .ico file is the same group with each image's offset in the file
    // in place of its id.

    private const int RtIcon = 3, RtGroupIcon = 14;

    /// <summary>The executable's icon as an .ico file, or null when it has none.</summary>
    public static byte[]? ReadIcon(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            var dir = pe.PEHeaders.PEHeader?.ResourceTableDirectory;
            if (dir is not { RelativeVirtualAddress: > 0 } d) return null;
            // the directory's offsets are from its own start; a leaf's data is at an RVA
            var root = pe.GetSectionData(d.RelativeVirtualAddress).GetContent().ToArray();

            byte[]? Data(int type, int? id)
            {
                int typeDir = Find(root, 0, type);
                if (typeDir < 0) return null;
                int nameDir = id is int i ? Find(root, typeDir, i) : First(root, typeDir);
                if (nameDir < 0) return null;
                int leaf = First(root, nameDir);            // the first language
                if (leaf < 0 || leaf + 8 > root.Length) return null;
                int rva = BitConverter.ToInt32(root, leaf), size = BitConverter.ToInt32(root, leaf + 4);
                var data = pe.GetSectionData(rva);
                return size <= 0 || data.Length < size ? null : data.GetContent(0, size).ToArray();
            }

            byte[]? group = Data(RtGroupIcon, null);
            if (group == null || group.Length < 6) return null;
            int count = BitConverter.ToUInt16(group, 4);
            var images = new List<(byte[] Entry, byte[] Image)>();
            for (int n = 0; n < count && 6 + n * 14 + 14 <= group.Length; n++)
            {
                int at = 6 + n * 14;
                int id = BitConverter.ToUInt16(group, at + 12);
                byte[]? image = Data(RtIcon, id);
                if (image != null) images.Add((group[at..(at + 12)], image));
            }
            if (images.Count == 0) return null;

            using var ico = new MemoryStream();
            using var w = new BinaryWriter(ico);
            w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)images.Count);
            int offset = 6 + 16 * images.Count;
            foreach (var (entry, image) in images)
            {
                w.Write(entry, 0, 8);                       // width, height, colours, reserved, planes, bit count
                w.Write(image.Length);
                w.Write(offset);
                offset += image.Length;
            }
            foreach (var (_, image) in images) w.Write(image);
            return ico.ToArray();

            // IMAGE_RESOURCE_DIRECTORY: 16 bytes, then its named entries, then its id entries (8 bytes each).
            // An entry's offset has its top bit set when it is another directory.
            static int Find(byte[] r, int at, int id)
            {
                if (at + 16 > r.Length) return -1;
                int named = BitConverter.ToUInt16(r, at + 12), ids = BitConverter.ToUInt16(r, at + 14);
                for (int e = named; e < named + ids; e++)
                {
                    int p = at + 16 + e * 8;
                    if (p + 8 > r.Length) return -1;
                    if (BitConverter.ToInt32(r, p) == id) return BitConverter.ToInt32(r, p + 4) & 0x7fffffff;
                }
                return -1;
            }
            static int First(byte[] r, int at)
            {
                if (at + 24 > r.Length) return -1;
                int named = BitConverter.ToUInt16(r, at + 12), ids = BitConverter.ToUInt16(r, at + 14);
                return named + ids == 0 ? -1 : BitConverter.ToInt32(r, at + 20) & 0x7fffffff;
            }
        }
        catch (Exception e) when (e is BadImageFormatException or IOException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }
}
