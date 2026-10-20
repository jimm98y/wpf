// The P/Invoke rewriter (Documentation/pinvoke-rewriter.md): PInvokeFixture, an assembly with Windows
// P/Invokes as apps declare them, is rewritten onto PInvokeImplFixture's implementations (with that
// assembly's own export table) and its rewritten copy is run: the marshalling, the entry-point lookup
// Windows does, the logged failure of a function nothing implements, the Windows routing.

using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using WpfMac.ComRewrite;
using Xunit;

namespace WpfMac.Tests;

public sealed class PInvokeRewriterTests
{
    private static string Meta(string key) => typeof(PInvokeRewriterTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().First(a => a.Key == key).Value!;

    private static readonly Lazy<Win32Catalog> s_catalog = new(() => Win32Catalog.Load(new[] { Meta("PInvokeImplFixture") }));
    private static readonly Lazy<(Type Calls, RewriteResult Result)> s_off = new(() => Load(windows: false));
    private static readonly Lazy<(Type Calls, RewriteResult Result)> s_win = new(() => Load(windows: true));

    private static (Type, RewriteResult) Load(bool windows)
    {
        string fixture = Meta("PInvokeFixture"), impl = Meta("PInvokeImplFixture");
        var (image, symbols, result) = ComRewriter.RewriteToMemory(fixture, new[] { Path.GetDirectoryName(fixture)!, Path.GetDirectoryName(impl)! },
            com: false, pinvokes: true, windows: windows, catalog: s_catalog.Value);
        symbols?.Dispose();
        var alc = new FixtureContext(Path.GetDirectoryName(impl)!);
        var asm = alc.LoadFromStream(image);
        return (asm.GetType("PInvokeFixture.Calls", throwOnError: true)!, result);
    }

    private sealed class FixtureContext(string implDir) : AssemblyLoadContext(isCollectible: false)
    {
        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name == "WpfMac.ComInterop") return typeof(WpfMac.ComInterop.PInvoke).Assembly;
            string p = Path.Combine(implDir, name.Name + ".dll");
            return File.Exists(p) ? LoadFromAssemblyPath(p) : null;
        }
    }

    private static object? Call(string method, params object[] args)
    {
        try { return s_off.Value.Calls.GetMethod(method)!.Invoke(null, args); }
        catch (TargetInvocationException e) { throw e.InnerException!; }
    }

    private static object? CallOnWindows(string method, params object[] args)
    {
        try { return s_win.Value.Calls.GetMethod(method)!.Invoke(null, args); }
        catch (TargetInvocationException e) { throw e.InnerException!; }
    }

    [Fact] public void Counts() => Assert.True(s_off.Value.Result.Win32Implemented >= 15, s_off.Value.Result.Win32Summary);

    [Fact] public void Unicode_import_finds_the_W_function() => Assert.Equal(5, Call("UnicodeString"));
    [Fact] public void Auto_is_Unicode_as_on_Windows() => Assert.Equal(5, Call("AutoString"));
    [Fact] public void Ansi_import_finds_the_A_function() => Assert.Equal(1004, Call("AnsiStringA"));
    [Fact] public void Null_string_is_a_null_pointer() => Assert.Equal(-1, Call("NullString"));
    [Fact] public void An_entry_point_no_DLL_exports_throws() => Assert.Throws<EntryPointNotFoundException>(() => Call("AnsiStringExact"));
    [Fact] public void Not_exported_throws_as_on_Windows() => Assert.Throws<EntryPointNotFoundException>(() => Call("NotExported"));
    [Fact] public void StringBuilder_comes_back() => Assert.Equal("hello", Call("StringBuilderOut"));
    [Fact] public void Bool_is_BOOL() { Assert.Equal(true, Call("BoolReturn", 3)); Assert.Equal(false, Call("BoolReturn", -3)); }
    [Fact] public void Bool_as_U1() => Assert.Equal(true, Call("BoolU1Return", 1));
    [Fact] public void Ref_struct_is_written_back() => Assert.Equal("True 6,8", Call("RefStruct"));
    [Fact] public void Struct_by_value_is_the_implementations_own() => Assert.Equal(11, Call("StructByValue"));
    [Fact] public void SetLastError_captures_and_clears() => Assert.Equal("False 5 0 False", Call("LastError"));
    [Fact] public void Unimplemented_returns_the_failure_value() => Assert.Equal("False 120", Call("Unimplemented"));
    [Fact] public void Unimplemented_HRESULT_throws_when_PreserveSig_is_false() => Assert.Throws<NotImplementedException>(() => Call("UnimplementedHr"));
    [Fact] public void Unimplemented_void_returns() => Call("UnimplementedVoid");
    [Fact] public void Delegate_is_a_function_pointer() => Assert.Equal(21, Call("Callback1", 7));
    [Fact] public void SafeHandles_in_and_out() => Assert.Equal("ThingHandle False 9", Call("SafeHandles"));
    [Fact] public void PreserveSig_false_returns_the_retval() => Assert.Equal(42, Call("HrRetval", 0));
    [Fact] public void PreserveSig_false_throws_the_HRESULT() => Assert.Equal(unchecked((int)0x80004005), Assert.Throws<COMException>(() => Call("HrRetval", 1)).HResult);
    [Fact] public void Arguments_beyond_or_short_of_the_function() => Assert.Equal("12 40", Call("Arity"));
    [Fact] public void Ansi_import_of_a_Unicode_only_implementation() => Assert.Equal(2003, Call("AnsiBridge"));
    [Fact] public void A_function_implemented_only_on_Windows_fails_elsewhere() => Assert.Equal(0, Call("Routed", (IntPtr)0x5747));

    [Fact] public void Windows_routes_the_stacks_windows_to_the_implementation() => Assert.Equal(7, CallOnWindows("Routed", (IntPtr)0x5747));

    [Fact]
    public void Windows_routes_other_windows_to_the_real_function()
    {
        // the real "testdll" does not exist: reaching it is the proof the call went there
        var e = Record.Exception(() => CallOnWindows("Routed", (IntPtr)0x1234));
        Assert.True(e is DllNotFoundException or EntryPointNotFoundException, e?.ToString());
    }

    [Fact]
    public void Windows_leaves_what_it_does_not_route()
    {
        Assert.Equal(1, s_win.Value.Result.Win32Routed);
        Assert.Throws<DllNotFoundException>(() => CallOnWindows("UnicodeString"));
    }

    [Fact]
    public void The_real_table_knows_what_apps_call()
    {
        string win32 = Meta("WpfMacWin32");
        Assert.SkipWhen(!File.Exists(win32), "WpfMac.Win32 is not built (tools/WpfMac/build.sh)");
        var cat = Win32Catalog.Load(new[] { win32 });
        foreach (var (module, name) in new[] { ("user32", "GetWindowLongW"), ("user32", "keybd_event"), ("kernel32", "GetTickCount"), ("ntdll", "RtlGetVersion"),
            ("shcore", "GetDpiForMonitor"), ("uxtheme", "#135"), ("winmm", "sndPlaySoundW") })
        {
            Assert.True(cat.IsWindowsModule(module), module);
            Assert.True(cat.Exports(module, name, out _), $"{module}!{name}");
        }
        Assert.False(cat.Exports("user32", "GetWindowLong", out _));         // undecorated: Windows has only the A and W
    }
}
