// A Windows Installer database's summary information ("\u0005SummaryInformation"): an OLE property set (MS-OLEPS)
// of one section, FMTID_SummaryInformation, its properties by id in order -- the codepage (VT_I2), strings in that
// codepage (VT_LPSTR: their length with the terminating zero, then the bytes, padded to 4), integers (VT_I4) and
// times (VT_FILETIME).

using System.Buffers.Binary;
using System.Text;

namespace WpfWebGpu.Packager.Desktop.Msi;

public sealed class SummaryInformation
{
    public const string StreamName = "\u0005SummaryInformation";

    public int Codepage { get; set; } = 1252;
    public string Title { get; set; } = "Installation Database";
    public string Subject { get; set; } = "";
    public string Author { get; set; } = "";
    public string Keywords { get; set; } = "Installer";
    public string Comments { get; set; } = "";
    /// <summary>The platform and languages: "x64;1033", "Arm64;1033".</summary>
    public string Template { get; set; } = "x64;1033";
    /// <summary>The package code, a GUID in braces: each package its own.</summary>
    public string Revision { get; set; } = "";
    public DateTime Created { get; set; } = DateTime.UtcNow;
    /// <summary>The Windows Installer schema it needs: 500 for Arm64.</summary>
    public int PageCount { get; set; } = 500;
    /// <summary>2: the files are compressed in cabinets, under long names.</summary>
    public int WordCount { get; set; } = 2;
    public string Application { get; set; } = "Packager";
    /// <summary>2: read-only recommended.</summary>
    public int Security { get; set; } = 2;

    public byte[] ToBytes()
    {
        Encoding encoding = MsiDatabase.Encode(Codepage);
        var props = new List<(int Id, byte[] Value)>();
        byte[] I2(int v) { var b = new byte[8]; BinaryPrimitives.WriteInt32LittleEndian(b, 2); BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(4), (short)v); return b; }
        byte[] I4(int v) { var b = new byte[8]; BinaryPrimitives.WriteInt32LittleEndian(b, 3); BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(4), v); return b; }
        byte[] Str(string s)
        {
            byte[] text = encoding.GetBytes(s + "\0");
            var b = new byte[8 + (text.Length + 3) / 4 * 4];
            BinaryPrimitives.WriteInt32LittleEndian(b, 30);
            BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(4), text.Length);
            text.CopyTo(b, 8);
            return b;
        }
        byte[] Time(DateTime t) { var b = new byte[12]; BinaryPrimitives.WriteInt32LittleEndian(b, 64); BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(4), t.ToFileTimeUtc()); return b; }

        props.Add((1, I2(Codepage)));
        props.Add((2, Str(Title)));
        props.Add((3, Str(Subject)));
        props.Add((4, Str(Author)));
        props.Add((5, Str(Keywords)));
        props.Add((6, Str(Comments)));
        props.Add((7, Str(Template)));
        props.Add((8, Str(Author)));
        props.Add((9, Str(Revision)));
        props.Add((12, Time(Created)));
        props.Add((13, Time(Created)));
        props.Add((14, I4(PageCount)));
        props.Add((15, I4(WordCount)));
        props.Add((18, Str(Application)));
        props.Add((19, I4(Security)));

        int headerLength = 8 + props.Count * 8;
        int sectionLength = headerLength + props.Sum(p => p.Value.Length);
        var section = new byte[sectionLength];
        BinaryPrimitives.WriteInt32LittleEndian(section, sectionLength);
        BinaryPrimitives.WriteInt32LittleEndian(section.AsSpan(4), props.Count);
        int at = headerLength;
        for (int i = 0; i < props.Count; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(section.AsSpan(8 + i * 8), props[i].Id);
            BinaryPrimitives.WriteInt32LittleEndian(section.AsSpan(12 + i * 8), at);
            props[i].Value.CopyTo(section, at);
            at += props[i].Value.Length;
        }

        var stream = new byte[48 + sectionLength];
        BinaryPrimitives.WriteUInt16LittleEndian(stream, 0xFFFE);
        BinaryPrimitives.WriteUInt16LittleEndian(stream.AsSpan(2), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(stream.AsSpan(4), 0x00020006);   // the OS that wrote it: Windows 6.0
        BinaryPrimitives.WriteInt32LittleEndian(stream.AsSpan(24), 1);
        new Guid("F29F85E0-4FF9-1068-AB91-08002B27B3D9").TryWriteBytes(stream.AsSpan(28, 16));
        BinaryPrimitives.WriteInt32LittleEndian(stream.AsSpan(44), 48);
        section.CopyTo(stream, 48);
        return stream;
    }
}
