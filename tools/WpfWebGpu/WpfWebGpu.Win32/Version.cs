// version: a file's version resource, as version.dll reads it -- the RT_VERSION resource of the PE file
// (VS_VERSIONINFO: the fixed file info, then StringFileInfo and VarFileInfo blocks), and VerQueryValueW's
// walk of it by path ("\", "\VarFileInfo\Translation", "\StringFileInfo\040904b0\ProductVersion").
//
// .NET's FileVersionInfo off Windows reads a managed assembly's attributes instead, and nothing at all of a
// native file; this reads the resource itself, so both kinds answer as on Windows.

using System.IO;
using System.Reflection.PortableExecutable;

namespace WpfWebGpu.Win32;

public static unsafe partial class Exports
{
    private const int RtVersion = 16, ErrorResourceTypeNotFound = 1813, ErrorFileNotFound = 2;

    [Win32Export("GetFileVersionInfoSizeW")]
    public static uint GetFileVersionInfoSizeW(char* fileName, uint* handle) => GetFileVersionInfoSizeExW(0, fileName, handle);

    [Win32Export("GetFileVersionInfoSizeExW")]
    public static uint GetFileVersionInfoSizeExW(uint flags, char* fileName, uint* handle)
    {
        if (handle != null) *handle = 0;
        byte[]? resource = VersionResource(ReadUtf16(fileName), out int error);
        if (resource == null) { SetLastError(error); return 0; }
        SetLastError(0);
        return (uint)resource.Length;
    }

    [Win32Export("GetFileVersionInfoW")]
    public static int GetFileVersionInfoW(char* fileName, uint handle, uint length, void* data) => GetFileVersionInfoExW(0, fileName, handle, length, data);

    [Win32Export("GetFileVersionInfoExW")]
    public static int GetFileVersionInfoExW(uint flags, char* fileName, uint handle, uint length, void* data)
    {
        if (data == null) { SetLastError(87); return FALSE; }
        byte[]? resource = VersionResource(ReadUtf16(fileName), out int error);
        if (resource == null) { SetLastError(error); return FALSE; }
        // as much of it as the buffer holds: a caller that sized it with GetFileVersionInfoSize gets it whole
        resource.AsSpan(0, (int)Math.Min(length, (uint)resource.Length)).CopyTo(new Span<byte>(data, (int)length));
        return TRUE;
    }

    [Win32Export("VerQueryValueW")]
    public static int VerQueryValueW(void* block, char* subBlock, void** buffer, uint* length)
    {
        if (block == null || buffer == null || length == null) return FALSE;
        *buffer = null;
        *length = 0;
        string path = ReadUtf16(subBlock) ?? "";
        byte* node = (byte*)block;
        foreach (string part in path.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            node = Child(node, part);
            if (node == null) return FALSE;
        }
        // the node's value: the fixed file info at the root, a translation table's bytes, a string's characters
        ushort valueLength = ((ushort*)node)[1];
        if (valueLength == 0) return FALSE;
        *buffer = node + ValueOffset(node);
        *length = valueLength;      // bytes for binary values, characters (with the terminator) for text
        return TRUE;
    }

    // a node's key starts at 6; its value at the next 4-byte boundary after the key's terminator
    private static int ValueOffset(byte* node)
    {
        char* key = (char*)(node + 6);
        int n = 0;
        while (key[n] != '\0') n++;
        return Align4(6 + (n + 1) * 2);
    }

    private static int Align4(int at) => (at + 3) & ~3;

    // the child of a node whose key is the given one (case aside); its children follow its value
    private static byte* Child(byte* node, string key)
    {
        int total = ((ushort*)node)[0];
        ushort valueLength = ((ushort*)node)[1], type = ((ushort*)node)[2];
        int valueBytes = type == 1 ? valueLength * 2 : valueLength;
        int at = Align4(ValueOffset(node) + valueBytes);
        while (at + 6 < total)
        {
            byte* child = node + at;
            int length = ((ushort*)child)[0];
            if (length == 0) break;
            if (string.Equals(new string((char*)(child + 6)), key, StringComparison.OrdinalIgnoreCase)) return child;
            at = Align4(at + length);
        }
        return null;
    }

    /// <summary>The file's VS_VERSIONINFO resource (the first language of version resource 1), or null with the
    /// error Windows sets: no such file, or a file without one.</summary>
    internal static byte[]? VersionResource(string? fileName, out int error)
    {
        error = ErrorFileNotFound;
        string? path = WpfWebGpu.ComInterop.WindowsPaths.Disk(fileName);
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        error = ErrorResourceTypeNotFound;
        try
        {
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            var directory = pe.PEHeaders.PEHeader?.ResourceTableDirectory;
            if (directory is not { RelativeVirtualAddress: > 0 } d) return null;
            byte[] root = pe.GetSectionData(d.RelativeVirtualAddress).GetContent().ToArray();
            int types = Entry(root, 0, RtVersion);
            int names = types < 0 ? -1 : Entry(root, types, 1);
            if (names < 0 && types >= 0) names = FirstEntry(root, types);
            int leaf = names < 0 ? -1 : FirstEntry(root, names);
            if (leaf < 0 || leaf + 8 > root.Length) return null;
            int rva = BitConverter.ToInt32(root, leaf), size = BitConverter.ToInt32(root, leaf + 4);
            var data = pe.GetSectionData(rva);
            return size <= 0 || data.Length < size ? null : data.GetContent(0, size).ToArray();
        }
        catch (Exception e) when (e is BadImageFormatException or IOException or ArgumentException or InvalidOperationException or UnauthorizedAccessException)
        {
            error = 193;    // ERROR_BAD_EXE_FORMAT
            return null;
        }

        // IMAGE_RESOURCE_DIRECTORY: 16 bytes, its named entries, then its id entries (8 bytes each); an entry
        // whose offset has the top bit set is another directory
        static int Entry(byte[] r, int at, int id)
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
        static int FirstEntry(byte[] r, int at)
        {
            if (at + 24 > r.Length) return -1;
            int named = BitConverter.ToUInt16(r, at + 12), ids = BitConverter.ToUInt16(r, at + 14);
            return named + ids == 0 ? -1 : BitConverter.ToInt32(r, at + 20) & 0x7fffffff;
        }
    }
}
