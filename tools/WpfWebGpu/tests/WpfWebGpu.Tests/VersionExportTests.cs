using System.Reflection;
using Xunit;

namespace WpfWebGpu.Tests;

// version's file version queries in the stack's user-mode Windows (WpfWebGpu.Win32/Version.cs): the version resource
// read from the file, walked as VerQueryValueW walks it. The file is this assembly, which the compiler gave one.
public unsafe class VersionExportTests
{
    private static MethodInfo Export(string name) => Win32Exports.Method(name);

    private static object P(void* p, Type t) => Pointer.Box(p, t);

    private static bool Query(byte[] block, string path, out IntPtr value, out uint length)
    {
        void* found = null;
        uint size = 0;
        fixed (byte* b = block)
        fixed (char* p = path)
        {
            bool ok = (int)Export("VerQueryValueW").Invoke(null, new object[] { P(b, typeof(void*)), P(p, typeof(char*)), P(&found, typeof(void**)), P(&size, typeof(uint*)) })! != 0;
            value = (IntPtr)found;          // into block, valid while it is pinned -- read below only through offsets
            length = size;
            if (ok) value = (IntPtr)((byte*)found - b);
            return ok;
        }
    }

    [Fact]
    public void A_file_s_version_resource_reads_as_version_dll_reads_it()
    {
        string file = typeof(VersionExportTests).Assembly.Location;
        uint handle = 7, size;
        fixed (char* name = file)
            size = (uint)Export("GetFileVersionInfoSizeW").Invoke(null, new object[] { P(name, typeof(char*)), P(&handle, typeof(uint*)) })!;
        Assert.True(size > 0);
        Assert.Equal(0u, handle);

        var block = new byte[size];
        fixed (char* name = file)
        fixed (byte* data = block)
            Assert.NotEqual(0, (int)Export("GetFileVersionInfoW").Invoke(null, new object[] { P(name, typeof(char*)), 0u, size, P(data, typeof(void*)) })!);

        // the root is VS_FIXEDFILEINFO, its signature first and the file version's major.minor next but one
        Assert.True(Query(block, "\\", out IntPtr fixedAt, out uint fixedLength));
        Assert.Equal(52u, fixedLength);
        Assert.Equal(0xFEEF04BDu, BitConverter.ToUInt32(block, (int)fixedAt));
        var fileVersion = Version.Parse(typeof(VersionExportTests).Assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()!.Version);
        Assert.Equal((uint)(fileVersion.Major << 16 | fileVersion.Minor), BitConverter.ToUInt32(block, (int)fixedAt + 8));

        // the translation names the string table; its FileVersion is the attribute's
        Assert.True(Query(block, "\\VarFileInfo\\Translation", out IntPtr translationAt, out uint translationLength));
        Assert.Equal(4u, translationLength);
        string table = $"{BitConverter.ToUInt16(block, (int)translationAt):x4}{BitConverter.ToUInt16(block, (int)translationAt + 2):x4}";
        Assert.True(Query(block, $"\\StringFileInfo\\{table}\\FileVersion", out IntPtr textAt, out uint characters));
        string text = System.Text.Encoding.Unicode.GetString(block, (int)textAt, (int)(characters - 1) * 2);
        Assert.Equal(fileVersion.ToString(), text);
        Assert.False(Query(block, "\\StringFileInfo\\nosuchtable\\FileVersion", out _, out _));

        // a file that does not exist: 0, ERROR_FILE_NOT_FOUND
        fixed (char* name = "C:\\no\\such\\file.dll")
            Assert.Equal(0u, (uint)Export("GetFileVersionInfoSizeW").Invoke(null, new object[] { P(name, typeof(char*)), P(null, typeof(uint*)) })!);
    }
}
