// A shell link (.lnk) as Windows writes and reads one ([MS-SHLLINK]): the header, the target's LinkInfo (its local
// path, or a share's), the strings (description, relative path, working directory, arguments, icon) and the extra
// data's environment-variable target. What is written is what Windows needs to follow the link -- the target's
// LinkInfo and Unicode strings, no item ID list -- and what is read is any link Windows wrote, its target from the
// LinkInfo or the environment-variable block (an item ID list alone, a link made for a shell namespace object, has
// no path to read).

using System.Text;

namespace WpfWebGpu.ComInterop;

public sealed class ShellLinkFile
{
    private static readonly Guid LinkClsid = new("00021401-0000-0000-C000-000000000046");
    private const int HeaderSize = 0x4C;
    private const uint HasLinkTargetIDList = 0x1, HasLinkInfo = 0x2, HasName = 0x4, HasRelativePath = 0x8, HasWorkingDir = 0x10,
        HasArguments = 0x20, HasIconLocation = 0x40, IsUnicode = 0x80, HasExpString = 0x200;
    private const uint VolumeIDAndLocalBasePath = 0x1, CommonNetworkRelativeLinkAndPathSuffix = 0x2;
    private const uint EnvironmentVariableDataBlock = 0xA0000001;
    private const int SW_SHOWNORMAL = 1;

    public string TargetPath { get; set; } = "";
    public string Arguments { get; set; } = "";
    public string Description { get; set; } = "";
    public string WorkingDirectory { get; set; } = "";
    public string RelativePath { get; set; } = "";
    public string IconPath { get; set; } = "";
    public int IconIndex { get; set; }
    public int ShowCommand { get; set; } = SW_SHOWNORMAL;
    /// <summary>The hot key as the header keeps it: the virtual key low, HOTKEYF_SHIFT/CONTROL/ALT (1, 2, 4) high.</summary>
    public ushort HotKey { get; set; }

    public static ShellLinkFile Read(string path) => Read(File.ReadAllBytes(path));

    public static ShellLinkFile Read(byte[] b)
    {
        if (b.Length < HeaderSize || U32(b, 0) != HeaderSize || new Guid(b.AsSpan(4, 16)) != LinkClsid)
            throw new InvalidDataException("not a shell link");
        var link = new ShellLinkFile();
        uint flags = U32(b, 20);
        link.IconIndex = (int)U32(b, 56);
        link.ShowCommand = (int)U32(b, 60);
        link.HotKey = U16(b, 64);
        int pos = HeaderSize;
        if ((flags & HasLinkTargetIDList) != 0) pos += 2 + U16(b, pos);
        if ((flags & HasLinkInfo) != 0)
        {
            link.TargetPath = LinkInfoPath(b, pos);
            pos += (int)U32(b, pos);
        }
        bool unicode = (flags & IsUnicode) != 0;
        string Next()
        {
            int count = U16(b, pos);
            pos += 2;
            string s = unicode ? Encoding.Unicode.GetString(b, pos, count * 2) : Encoding.Latin1.GetString(b, pos, count);
            pos += unicode ? count * 2 : count;
            return s;
        }
        if ((flags & HasName) != 0) link.Description = Next();
        if ((flags & HasRelativePath) != 0) link.RelativePath = Next();
        if ((flags & HasWorkingDir) != 0) link.WorkingDirectory = Next();
        if ((flags & HasArguments) != 0) link.Arguments = Next();
        if ((flags & HasIconLocation) != 0) link.IconPath = Next();
        // the extra data: a target given with environment variables (%ProgramFiles%\...)
        while (pos + 8 <= b.Length && U32(b, pos) is var size && size >= 8 && pos + size <= b.Length)
        {
            if (U32(b, pos + 4) == EnvironmentVariableDataBlock && size >= 8 + 260 + 520 && (flags & HasExpString) != 0 && link.TargetPath.Length == 0)
            {
                string unicodeTarget = ZString(Encoding.Unicode.GetString(b, pos + 8 + 260, 520));
                string target = unicodeTarget.Length > 0 ? unicodeTarget : ZString(Encoding.Latin1.GetString(b, pos + 8, 260));
                link.TargetPath = Environment.ExpandEnvironmentVariables(target);
            }
            pos += (int)size;
        }
        return link;
    }

    // the LinkInfo's path: a local one (its base path and suffix), or a share's (its name and the suffix)
    private static string LinkInfoPath(byte[] b, int at)
    {
        uint headerSize = U32(b, at + 4), flags = U32(b, at + 8);
        bool unicode = headerSize >= 0x24;
        string suffix = unicode && U32(b, at + 32) != 0 ? UnicodeZ(b, at + (int)U32(b, at + 32)) : AnsiZ(b, at + (int)U32(b, at + 24));
        if ((flags & VolumeIDAndLocalBasePath) != 0)
        {
            string basePath = unicode && U32(b, at + 28) != 0 ? UnicodeZ(b, at + (int)U32(b, at + 28)) : AnsiZ(b, at + (int)U32(b, at + 16));
            return basePath + suffix;
        }
        if ((flags & CommonNetworkRelativeLinkAndPathSuffix) != 0)
        {
            int cnrl = at + (int)U32(b, at + 20);
            uint netNameOffset = U32(b, cnrl + 8);
            string share = netNameOffset > 0x14 && U32(b, cnrl + 20) != 0 ? UnicodeZ(b, cnrl + (int)U32(b, cnrl + 20)) : AnsiZ(b, cnrl + (int)netNameOffset);
            return suffix.Length > 0 ? share.TrimEnd('\\') + "\\" + suffix : share;
        }
        return "";
    }

    public void Save(string path) => File.WriteAllBytes(path, ToArray(path));

    public byte[] ToArray(string? linkPath = null)
    {
        uint flags = IsUnicode | (TargetPath.Length > 0 ? HasLinkInfo : 0)
            | (Description.Length > 0 ? HasName : 0) | (RelativePath.Length > 0 ? HasRelativePath : 0)
            | (WorkingDirectory.Length > 0 ? HasWorkingDir : 0) | (Arguments.Length > 0 ? HasArguments : 0)
            | (IconPath.Length > 0 ? HasIconLocation : 0);
        var o = new MemoryStream();
        var w = new BinaryWriter(o);

        // the header, with the target's attributes, times and size as the shell records them
        string target = TargetPath.Length > 0 ? WindowsPaths.Disk(TargetPath)! : "";
        var info = target.Length > 0 && (File.Exists(target) || Directory.Exists(target)) ? (FileSystemInfo)(Directory.Exists(target) ? new DirectoryInfo(target) : new FileInfo(target)) : null;
        w.Write(HeaderSize);
        w.Write(LinkClsid.ToByteArray());
        w.Write(flags);
        w.Write(info is DirectoryInfo ? 0x10u : 0x20u);                   // FILE_ATTRIBUTE_DIRECTORY / _ARCHIVE
        w.Write(info?.CreationTimeUtc.ToFileTimeUtc() ?? 0);
        w.Write(info?.LastAccessTimeUtc.ToFileTimeUtc() ?? 0);
        w.Write(info?.LastWriteTimeUtc.ToFileTimeUtc() ?? 0);
        w.Write(info is FileInfo f ? (uint)Math.Min(f.Length, uint.MaxValue) : 0u);
        w.Write(IconIndex);
        w.Write(ShowCommand);
        w.Write(HotKey);
        w.Write((ushort)0);
        w.Write(0u);
        w.Write(0u);

        if ((flags & HasLinkInfo) != 0) WriteLinkInfo(w, TargetPath);
        void Str(string s) { w.Write((ushort)s.Length); w.Write(Encoding.Unicode.GetBytes(s)); }
        if (Description.Length > 0) Str(Description);
        if (RelativePath.Length > 0) Str(RelativePath);
        if (WorkingDirectory.Length > 0) Str(WorkingDirectory);
        if (Arguments.Length > 0) Str(Arguments);
        if (IconPath.Length > 0) Str(IconPath);
        w.Write(0u);                                                        // the terminal block
        return o.ToArray();
    }

    // LinkInfo with a VolumeID (a fixed drive, no label) and the local base path, ANSI and Unicode
    private static void WriteLinkInfo(BinaryWriter w, string path)
    {
        byte[] volume = new byte[17];                                       // VolumeIDSize, DriveType, serial, label offset, ""
        BitConverter.TryWriteBytes(volume.AsSpan(0), 17u);
        BitConverter.TryWriteBytes(volume.AsSpan(4), 3u);                   // DRIVE_FIXED
        BitConverter.TryWriteBytes(volume.AsSpan(12), 0x10u);
        byte[] ansi = Encoding.Latin1.GetBytes(path.Select(c => c < 0x100 ? c : '?').ToArray());
        byte[] unicode = Encoding.Unicode.GetBytes(path);
        const int headerSize = 0x24;
        int volumeAt = headerSize, baseAt = volumeAt + volume.Length, suffixAt = baseAt + ansi.Length + 1;
        int baseUnicodeAt = suffixAt + 1, suffixUnicodeAt = baseUnicodeAt + unicode.Length + 2;
        int size = suffixUnicodeAt + 2;
        w.Write(size);
        w.Write(headerSize);
        w.Write(VolumeIDAndLocalBasePath);
        w.Write(volumeAt);
        w.Write(baseAt);
        w.Write(0);                                                         // CommonNetworkRelativeLinkOffset
        w.Write(suffixAt);
        w.Write(baseUnicodeAt);
        w.Write(suffixUnicodeAt);
        w.Write(volume);
        w.Write(ansi); w.Write((byte)0);
        w.Write((byte)0);                                                   // the suffix, empty
        w.Write(unicode); w.Write((ushort)0);
        w.Write((ushort)0);
    }

    private static uint U32(byte[] b, int at) => at + 4 <= b.Length ? BitConverter.ToUInt32(b, at) : 0;
    private static ushort U16(byte[] b, int at) => at + 2 <= b.Length ? BitConverter.ToUInt16(b, at) : (ushort)0;

    private static string AnsiZ(byte[] b, int at)
    {
        int end = at;
        while (end < b.Length && b[end] != 0) end++;
        return at < b.Length ? Encoding.Latin1.GetString(b, at, end - at) : "";
    }

    private static string UnicodeZ(byte[] b, int at)
    {
        int end = at;
        while (end + 1 < b.Length && (b[end] != 0 || b[end + 1] != 0)) end += 2;
        return at < b.Length ? Encoding.Unicode.GetString(b, at, end - at) : "";
    }

    private static string ZString(string s) => s.IndexOf('\0') is var i and >= 0 ? s[..i] : s;
}
