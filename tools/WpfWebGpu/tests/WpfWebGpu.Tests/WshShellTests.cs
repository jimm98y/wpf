// WScript.Shell off Windows (WpfWebGpu.ComInterop's WshShell): its shortcuts, Windows' .lnk files, and the
// COM vtable a cast to an app's own IWshShortcut reaches them through.

using System.Runtime.InteropServices;
using System.Text;
using WpfWebGpu.ComInterop;
using Xunit;

namespace WpfWebGpu.Tests;

public sealed unsafe class WshShellTests
{
    [Fact]
    public void A_shortcut_is_saved_as_a_shell_link_and_read_back()
    {
        string dir = TestEnvironment.NewDirectory("lnk");
        string target = Path.Combine(dir, "Tool.exe");
        File.WriteAllBytes(target, new byte[1234]);
        var shell = new WshShell();
        var link = (WshShortcut)shell.CreateShortcut(Path.Combine(dir, "Tool.lnk"));
        link.TargetPath = target;
        link.Arguments = "/runtime \"ü\"";
        link.Description = "The tool";
        link.WorkingDirectory = dir;
        link.WindowStyle = 7;
        link.IconLocation = target + ",2";
        link.Hotkey = "Ctrl+Alt+T";
        link.Save();

        byte[] bytes = File.ReadAllBytes(link.FullName);
        Assert.Equal(0x4C, BitConverter.ToInt32(bytes, 0));
        Assert.Equal(new Guid("00021401-0000-0000-C000-000000000046"), new Guid(bytes.AsSpan(4, 16)));
        Assert.Equal(1234u, BitConverter.ToUInt32(bytes, 52));                 // the target's size, as the shell records it

        var again = (WshShortcut)shell.CreateShortcut(link.FullName);
        Assert.Equal((target, "/runtime \"ü\"", "The tool", dir, 7, target + ",2", "Alt+Ctrl+T"),
            (again.TargetPath, again.Arguments, again.Description, again.WorkingDirectory, again.WindowStyle, again.IconLocation, again.Hotkey));
    }

    [Fact]
    public void A_link_with_an_item_id_list_and_an_environment_target_is_read()
    {
        var link = new ShellLinkFile { TargetPath = @"C:\Program Files\Product\Tool.exe", Arguments = "-x" };
        byte[] plain = link.ToArray();
        // as Explorer writes one: an item ID list ahead of the LinkInfo
        byte[] idList = { 0x06, 0x00, 0x04, 0x00, 0x1F, 0x50, 0x00, 0x00 };   // IDListSize 6: one item of 4 bytes, then the terminator
        var withList = new List<byte>(plain[..0x4C]);
        withList.AddRange(idList);
        withList.AddRange(plain[0x4C..]);
        byte[] b = withList.ToArray();
        BitConverter.TryWriteBytes(b.AsSpan(20), BitConverter.ToUInt32(b, 20) | 0x1);
        var read = ShellLinkFile.Read(b);
        Assert.Equal((@"C:\Program Files\Product\Tool.exe", "-x"), (read.TargetPath, read.Arguments));

        // a target given only with environment variables (HasExpString and its data block)
        Environment.SetEnvironmentVariable("WPFWEBGPU_TEST_ROOT", "/opt/product");
        var env = new List<byte>(new ShellLinkFile().ToArray()[..^4]);
        var block = new byte[8 + 260 + 520];
        BitConverter.TryWriteBytes(block.AsSpan(0), (uint)block.Length);
        BitConverter.TryWriteBytes(block.AsSpan(4), 0xA0000001u);
        Encoding.Unicode.GetBytes("%WPFWEBGPU_TEST_ROOT%/Tool.exe").CopyTo(block, 8 + 260);
        env.AddRange(block);
        env.AddRange(new byte[4]);
        byte[] e = env.ToArray();
        BitConverter.TryWriteBytes(e.AsSpan(20), BitConverter.ToUInt32(e, 20) | 0x200);
        Assert.Equal("/opt/product/Tool.exe", ShellLinkFile.Read(e).TargetPath);
    }

    [Fact]
    public void A_shortcut_answers_IWshShortcut_through_its_vtable()
    {
        string dir = TestEnvironment.NewDirectory("lnkcom");
        var link = (WshShortcut)new WshShell().CreateShortcut(Path.Combine(dir, "A.lnk"));
        Guid iid = new("F935DC23-1CF0-11D0-ADB9-00C04FD58A0B");
        IntPtr unknown = Com.GetIUnknownForObject(link);
        Assert.Equal(0, Marshal.QueryInterface(unknown, in iid, out IntPtr p));
        try
        {
            IntPtr* vt = *(IntPtr**)p;
            IntPtr bstr = Marshal.StringToBSTR("/opt/tool");
            Assert.Equal(0, ((delegate* unmanaged<IntPtr, IntPtr, int>)vt[18])(p, bstr));          // put_TargetPath
            Marshal.FreeBSTR(bstr);
            Assert.Equal(0, ((delegate* unmanaged<IntPtr, int, int>)vt[20])(p, 3));               // put_WindowStyle
            IntPtr got;
            Assert.Equal(0, ((delegate* unmanaged<IntPtr, IntPtr*, int>)vt[17])(p, &got));        // get_TargetPath
            Assert.Equal("/opt/tool", Marshal.PtrToStringBSTR(got));
            Marshal.FreeBSTR(got);
            Assert.Equal(0, ((delegate* unmanaged<IntPtr, int>)vt[24])(p));                       // Save
            Assert.Equal("/opt/tool", ShellLinkFile.Read(link.FullName).TargetPath);
            Assert.Equal(3, ShellLinkFile.Read(link.FullName).ShowCommand);
        }
        finally { Marshal.Release(p); Marshal.Release(unknown); }
    }

    [Fact]
    public void WScript_Shell_is_made_from_its_ProgID()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "on Windows the system's");
        Type? type = WindowsPaths.Type_GetTypeFromProgID("WScript.Shell");
        Assert.Equal(typeof(WshShell), type);
        object shell = Activator.CreateInstance(type!)!;
        string dir = TestEnvironment.NewDirectory("progid");
        object link = type!.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { Path.Combine(dir, "x.lnk") })!;
        Assert.IsType<WshShortcut>(link);
        Assert.Null(WindowsPaths.Type_GetTypeFromProgID("No.Such.Class"));
        Assert.Throws<COMException>(() => WindowsPaths.Type_GetTypeFromProgID("No.Such.Class", throwOnError: true));
        Assert.NotEmpty((string)new WshShell().SpecialFolders.Item("Startup"));
    }

    [Theory]
    [InlineData("/runtime /x", new[] { "/runtime", "/x" })]
    [InlineData("\"C:\\Program Files\\a b\" -c", new[] { "C:\\Program Files\\a b", "-c" })]
    [InlineData("a\\\\\"b c\"", new[] { "a\\b c" })]
    [InlineData("\"say \\\"hi\\\"\"", new[] { "say \"hi\"" })]
    [InlineData("", new string[0])]
    public void A_shortcut_s_arguments_are_split_as_CommandLineToArgvW_splits_them(string line, string[] expected) =>
        Assert.Equal(expected, WpfWebGpu.Startup.SplitCommandLine(line));
}

// The rewriter's cache (ComImportLoader): an image named by what it is made from, so the copies of a library in an
// installation's program folders share one, and it is kept in the cache's rewritten/ every app run from it shares.
public sealed class RewriteCacheTests
{
    [Fact]
    public void Copies_of_an_assembly_in_two_programs_share_one_image_and_other_references_do_not()
    {
        string root = TestEnvironment.NewDirectory("rwcache");
        string one = Path.Combine(root, "one"), two = Path.Combine(root, "two"), three = Path.Combine(root, "three");
        foreach (string d in new[] { one, two, three }) Directory.CreateDirectory(d);
        // an assembly and what it references, copied into two programs' folders alike
        string library = typeof(WpfWebGpu.ComRewrite.ComImportLoader).Assembly.Location;     // references WpfWebGpu.ComInterop
        string dependency = typeof(WpfWebGpu.ComInterop.Com).Assembly.Location;
        foreach (string d in new[] { one, two }) { File.Copy(library, Path.Combine(d, "WpfWebGpu.ComRewrite.dll")); File.Copy(dependency, Path.Combine(d, "WpfWebGpu.ComInterop.dll")); }
        // the third has the same assembly beside another build of what it references
        File.Copy(library, Path.Combine(three, "WpfWebGpu.ComRewrite.dll"));
        File.Copy(typeof(Xunit.Assert).Assembly.Location, Path.Combine(three, "WpfWebGpu.ComInterop.dll"));

        string cache = Path.Combine(root, "cache", "apps");
        string? PathFor(string dir, string app)
        {
            var originals = new Dictionary<string, (string, WpfWebGpu.ComRewrite.ComImportLoader.Parts)>
            {
                ["WpfWebGpu.ComRewrite"] = (Path.Combine(dir, "WpfWebGpu.ComRewrite.dll"), WpfWebGpu.ComRewrite.ComImportLoader.Parts.Paths),
            };
            var loader = new WpfWebGpu.ComRewrite.ComImportLoader(originals, new[] { dir },
                (string?)typeof(WpfWebGpu.ComRewrite.ComImportLoader).GetMethod("RewrittenDir", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.Invoke(null, new object?[] { Path.Combine(cache, app) }), null);
            return (string?)typeof(WpfWebGpu.ComRewrite.ComImportLoader).GetMethod("CachePath", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .Invoke(loader, new object[] { "WpfWebGpu.ComRewrite", Path.Combine(dir, "WpfWebGpu.ComRewrite.dll"), WpfWebGpu.ComRewrite.ComImportLoader.Parts.Paths, false });
        }
        string? a = PathFor(one, "ServiceA-1"), b = PathFor(two, "ServiceB-2"), c = PathFor(three, "ServiceC-3");
        // in the cache's rewritten/, in the folder of this build's generation
        Assert.Equal(Path.Combine(root, "cache", "rewritten"), Path.GetDirectoryName(Path.GetDirectoryName(a)));
        Assert.StartsWith("g-", Path.GetFileName(Path.GetDirectoryName(a)));
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }
}
