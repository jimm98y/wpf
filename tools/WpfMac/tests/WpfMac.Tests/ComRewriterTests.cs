// The COM-import rewriter (Documentation/comimport-rewriter.md): ComImportFixture, an assembly with
// [ComImport] interfaces as an app declares them, is rewritten and its rewritten copy is run against
// a COM object of our own (vtables of UnmanagedCallersOnly functions). On every OS, without built-in
// COM interop.

using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using WpfMac.ComRewrite;
using Xunit;

namespace WpfMac.Tests;

public sealed unsafe class ComRewriterTests
{
    private static readonly Guid IID_IUnknown = new("00000000-0000-0000-C000-000000000046");
    private static readonly Guid IID_ICalc = new("6B0B1E2A-0C55-4B0E-9F2B-6C1D3E4F5A01");
    private static readonly Guid IID_IOther = new("6B0B1E2A-0C55-4B0E-9F2B-6C1D3E4F5A02");

    // ---- the native test objects: { vtable, refs, value } ------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct Obj { public IntPtr* Vtbl; public int Refs; public int Value; }

    private static readonly IntPtr* s_calcVt = BuildCalc();
    private static readonly IntPtr* s_otherVt = BuildOther();
    internal static int Live;

    private static IntPtr* BuildCalc()
    {
        var vt = (IntPtr*)NativeMemory.AllocZeroed(20, (nuint)sizeof(IntPtr));
        vt[0] = (IntPtr)(delegate* unmanaged<Obj*, Guid*, IntPtr*, int>)&QueryInterface;
        vt[1] = (IntPtr)(delegate* unmanaged<Obj*, uint>)&AddRef;
        vt[2] = (IntPtr)(delegate* unmanaged<Obj*, uint>)&Release;
        vt[3] = (IntPtr)(delegate* unmanaged<Obj*, int, int, int>)&Add;
        vt[4] = (IntPtr)(delegate* unmanaged<Obj*, int, int*, int>)&Twice;
        vt[5] = (IntPtr)(delegate* unmanaged<Obj*, int>)&Fail;
        vt[6] = (IntPtr)(delegate* unmanaged<Obj*, IntPtr*, int>)&GetOther;
        vt[7] = (IntPtr)(delegate* unmanaged<Obj*, IntPtr, int*, int>)&ValueOf;
        vt[8] = (IntPtr)(delegate* unmanaged<Obj*, int, short*, int>)&IsOdd;
        vt[9] = (IntPtr)(delegate* unmanaged<Obj*, int*, int>)&Swap;
        vt[10] = (IntPtr)(delegate* unmanaged<Obj*, char*, int>)&Length;
        vt[11] = (IntPtr)(delegate* unmanaged<Obj*, int, IntPtr*, int>)&MakeOther;
        vt[12] = (IntPtr)(delegate* unmanaged<Obj*, WpfMac.ComInterop.ComVariant*, int>)&Variant;
        vt[13] = (IntPtr)(delegate* unmanaged<Obj*, char*, int>)&BLen;
        vt[14] = (IntPtr)(delegate* unmanaged<Obj*, char*, IntPtr*, int>)&Greet;
        vt[15] = (IntPtr)(delegate* unmanaged<Obj*, IntPtr*, int>)&Upper;
        vt[16] = (IntPtr)(delegate* unmanaged<Obj*, WpfMac.ComInterop.ComVariant, WpfMac.ComInterop.ComVariant*, int>)&Echo;
        vt[17] = (IntPtr)(delegate* unmanaged<Obj*, WpfMac.ComInterop.ComVariant*, int>)&Bump;
        vt[18] = (IntPtr)(delegate* unmanaged<Obj*, void*, int>)&Arrays;
        vt[19] = (IntPtr)(delegate* unmanaged<Obj*, IntPtr, IntPtr*, int>)&UseSink;
        return vt;
    }

    private static IntPtr* BuildOther()
    {
        var vt = (IntPtr*)NativeMemory.AllocZeroed(4, (nuint)sizeof(IntPtr));
        vt[0] = (IntPtr)(delegate* unmanaged<Obj*, Guid*, IntPtr*, int>)&QueryInterface;
        vt[1] = (IntPtr)(delegate* unmanaged<Obj*, uint>)&AddRef;
        vt[2] = (IntPtr)(delegate* unmanaged<Obj*, uint>)&Release;
        vt[3] = (IntPtr)(delegate* unmanaged<Obj*, int>)&Value;
        return vt;
    }

    private static Obj* New(IntPtr* vt, int value)
    {
        var o = (Obj*)NativeMemory.AllocZeroed((nuint)sizeof(Obj));
        o->Vtbl = vt; o->Refs = 1; o->Value = value;
        Interlocked.Increment(ref Live);
        return o;
    }

    [UnmanagedCallersOnly]
    private static int QueryInterface(Obj* self, Guid* iid, IntPtr* ppv)
    {
        bool calc = self->Vtbl == s_calcVt;
        if (*iid == IID_IUnknown || (calc && *iid == IID_ICalc) || (!calc && *iid == IID_IOther))
        {
            self->Refs++;
            *ppv = (IntPtr)self;
            return 0;
        }
        *ppv = IntPtr.Zero;
        return unchecked((int)0x80004002);
    }

    [UnmanagedCallersOnly] private static uint AddRef(Obj* self) => (uint)++self->Refs;

    [UnmanagedCallersOnly]
    private static uint Release(Obj* self)
    {
        int n = --self->Refs;
        if (n == 0) { NativeMemory.Free(self); Interlocked.Decrement(ref Live); }
        return (uint)n;
    }

    [UnmanagedCallersOnly] private static int Add(Obj* self, int a, int b) => a + b;
    [UnmanagedCallersOnly] private static int Twice(Obj* self, int x, int* r) { *r = 2 * x; return 0; }
    [UnmanagedCallersOnly] private static int Fail(Obj* self) => unchecked((int)0x80070057);
    [UnmanagedCallersOnly] private static int GetOther(Obj* self, IntPtr* other) { *other = (IntPtr)New(s_otherVt, self->Value); return 0; }
    // through the other's vtable (slot 3, Value): a native one of ours, or a managed object's CCW
    [UnmanagedCallersOnly] private static int ValueOf(Obj* self, IntPtr other, int* r) { *r = other == 0 ? -1 : 1000 + ((delegate* unmanaged<IntPtr, int>)(*(IntPtr**)other)[3])(other); return 0; }

    /// <summary>Native code calling a managed ISink through its CCW, every kind of argument.</summary>
    [UnmanagedCallersOnly]
    private static int UseSink(Obj* self, IntPtr sink, IntPtr* r)
    {
        IntPtr* vt = *(IntPtr**)sink;
        var parts = new List<string>();
        parts.Add(((delegate* unmanaged<IntPtr, int, int, int>)vt[3])(sink, 20, 22).ToString());
        IntPtr prefix = Marshal.StringToBSTR("my"), name;
        int hr = ((delegate* unmanaged<IntPtr, IntPtr, short, IntPtr*, int>)vt[4])(sink, prefix, -1, &name);
        Marshal.FreeBSTR(prefix);
        parts.Add(hr == 0 ? Marshal.PtrToStringBSTR(name) : $"hr {hr:x8}");
        if (hr == 0) Marshal.FreeBSTR(name);
        int n = 41; IntPtr made;
        hr = ((delegate* unmanaged<IntPtr, int*, IntPtr*, int>)vt[5])(sink, &n, &made);
        parts.Add($"{n}/{((delegate* unmanaged<IntPtr, int>)(*(IntPtr**)made)[3])(made)}");
        ((delegate* unmanaged<IntPtr, uint>)(*(IntPtr**)made)[2])(made);
        WpfMac.ComInterop.ComVariant v, e;
        WpfMac.ComInterop.Com.ToVariant(21, (IntPtr)(&v));
        hr = ((delegate* unmanaged<IntPtr, WpfMac.ComInterop.ComVariant, WpfMac.ComInterop.ComVariant*, int>)vt[6])(sink, v, &e);
        parts.Add(WpfMac.ComInterop.Com.FromVariant((IntPtr)(&e))!.ToString()!);
        WpfMac.ComInterop.Com.ClearVariant((IntPtr)(&e));
        parts.Add(((delegate* unmanaged<IntPtr, int>)vt[7])(sink).ToString("x8"));
        *r = Marshal.StringToBSTR(string.Join(",", parts));
        return 0;
    }
    [UnmanagedCallersOnly] private static int IsOdd(Obj* self, int x, short* r) { *r = (short)((x & 1) != 0 ? -1 : 0); return 0; }
    [UnmanagedCallersOnly] private static int Swap(Obj* self, int* p) { (p[0], p[1]) = (p[1], p[0]); return 0; }
    [UnmanagedCallersOnly] private static int Length(Obj* self, char* s) { int n = 0; while (s[n] != 0) n++; return n; }
    [UnmanagedCallersOnly] private static int MakeOther(Obj* self, int v, IntPtr* other) { *other = (IntPtr)New(s_otherVt, v); return 0; }
    // VARIANTs, through the same conversions the rewritten caller uses (a native callee would do the same by hand)
    [UnmanagedCallersOnly] private static int Variant(Obj* self, WpfMac.ComInterop.ComVariant* r) { WpfMac.ComInterop.Com.ToVariant("from native", (IntPtr)r); return 0; }
    [UnmanagedCallersOnly]
    private static int Echo(Obj* self, WpfMac.ComInterop.ComVariant v, WpfMac.ComInterop.ComVariant* r)
    {
        // the callee owns nothing of v; its copy goes back as a new VARIANT
        object? o = WpfMac.ComInterop.Com.FromVariant((IntPtr)(&v));
        WpfMac.ComInterop.Com.ToVariant(o, (IntPtr)r);
        return 0;
    }
    [UnmanagedCallersOnly]
    private static int Bump(Obj* self, WpfMac.ComInterop.ComVariant* v)
    {
        object? o = WpfMac.ComInterop.Com.FromVariant((IntPtr)v);
        WpfMac.ComInterop.Com.ClearVariant((IntPtr)v);
        WpfMac.ComInterop.Com.ToVariant(o is int i ? i + 1 : (object)"not an int", (IntPtr)v);
        return 0;
    }
    [UnmanagedCallersOnly] private static int Arrays(Obj* self, void* a) => 0;
    [UnmanagedCallersOnly] private static int Value(Obj* self) => self->Value;
    // BSTRs: the length is the prefix before the characters (in bytes)
    [UnmanagedCallersOnly] private static int BLen(Obj* self, char* s) => s == null ? -1 : (int)(((uint*)s)[-1] / 2);
    [UnmanagedCallersOnly] private static int Greet(Obj* self, char* name, IntPtr* r) { *r = Marshal.StringToBSTR("Hi " + Marshal.PtrToStringBSTR((IntPtr)name)); return 0; }
    [UnmanagedCallersOnly]
    private static int Upper(Obj* self, IntPtr* s)
    {
        string v = Marshal.PtrToStringBSTR(*s);
        Marshal.FreeBSTR(*s);
        *s = Marshal.StringToBSTR(v.ToUpperInvariant());
        return 0;
    }

    // ---- the rewritten fixture ------------------------------------------------------------------

    internal static string FixtureFile => typeof(ComRewriterTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().First(a => a.Key == "ComImportFixture").Value!;

    /// <summary>The fixture's original, copied to a folder of its own (with a native library beside it on
    /// Windows), as the launcher's loader finds an app's.</summary>
    internal static readonly Lazy<string> s_original = new(() =>
    {
        string dir = TestEnvironment.NewDirectory("comimport-app");
        string file = Path.Combine(dir, "ComImportFixture.dll");
        File.Copy(FixtureFile, file);
        if (File.Exists(Path.ChangeExtension(FixtureFile, ".pdb"))) File.Copy(Path.ChangeExtension(FixtureFile, ".pdb"), Path.ChangeExtension(file, ".pdb"));
        if (OperatingSystem.IsWindows()) File.Copy(Path.Combine(Environment.SystemDirectory, "version.dll"), Path.Combine(dir, "fixturenative.dll"));
        return file;
    });

    private static readonly Lazy<(Type Scenario, RewriteResult Result)> s_fixture = new(() =>
    {
        // the verdict, as the launcher judges it
        var result = ComRewriter.Rewrite(FixtureFile, null, new[] { AppContext.BaseDirectory }, partial: true);
        // the load, as the startup hook does it: rewritten in memory on the first request
        var loader = new ComImportLoader(new Dictionary<string, string> { ["ComImportFixture"] = s_original.Value }, new[] { AppContext.BaseDirectory }, null);
        var alc = new AssemblyLoadContext("comimport-fixture");
        loader.Install(alc);
        var asm = alc.LoadFromAssemblyName(new AssemblyName("ComImportFixture"));
        return (asm.GetType("ComImportFixture.Scenario", throwOnError: true)!, result);
    });

    private static object? Run(string method, params object[] args)
    {
        var m = s_fixture.Value.Scenario.GetMethod(method)!;
        try { return m.Invoke(null, args); }
        catch (TargetInvocationException e) when (e.InnerException != null) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException).Throw(); throw; }
    }

    private static T WithCalc<T>(int value, Func<IntPtr, T> body)
    {
        var o = New(s_calcVt, value);
        try { return body((IntPtr)o); }
        finally { GC.Collect(); GC.WaitForPendingFinalizers(); ((delegate* unmanaged<Obj*, uint>)s_calcVt[2])(o); }
    }

    [Fact]
    public void TheFixture_IsRewritten_AndOnlyTheVariantIsUnsupported()
    {
        var r = s_fixture.Value.Result;
        Assert.True(r.UsesComImport);
        Assert.Equal(5, r.Interfaces);
        Assert.Equal(1, r.PInvokes);
        string u = Assert.Single(r.Unsupported);
        Assert.Contains("Arrays", u);
        Assert.Equal(1, r.Delegates);
    }

    [Fact]
    public void Calls_GoThroughTheVtable()
    {
        Assert.Equal(5, WithCalc(0, p => (int)Run("Add", p, 2, 3)!));
        Assert.Equal(42, WithCalc(0, p => (int)Run("Twice", p, 21)!));
        Assert.Equal(17, WithCalc(17, p => (int)Run("OtherValue", p)!));
        Assert.Equal(1009, WithCalc(9, p => (int)Run("ValueOfOther", p)!));
        Assert.True(WithCalc(0, p => (bool)Run("IsOdd", p, 7)!));
        Assert.False(WithCalc(0, p => (bool)Run("IsOdd", p, 8)!));
        Assert.Equal(5, WithCalc(0, p => (int)Run("Length", p, "hello")!));
        Assert.Equal(33, WithCalc(0, p => (int)Run("MadeOther", p, 33)!));
    }

    [Fact]
    public void Bstrs_InOutAndByReference()
    {
        Assert.Equal(7, WithCalc(0, p => (int)Run("BLen", p, "a bcdef")!));       // a BSTR keeps an embedded NUL
        Assert.Equal("Hi there", WithCalc(0, p => (string)Run("Greet", p, "there")!));
        Assert.Equal("LOUD", WithCalc(0, p => (string)Run("Upper", p, "loud")!));
    }

    [Fact]
    public void AManagedObject_HandedToNativeCode_IsCalledThroughItsCcw()
    {
        Assert.Equal(1077, WithCalc(0, p => (int)Run("ValueOfManaged", p)!));
        // Add, Name (BSTR, VARIANT_BOOL, BSTR retval), Count (ref int, out interface), Echo (VARIANTs),
        // Fail (InvalidOperationException's HRESULT, COR_E_INVALIDOPERATION)
        Assert.Equal("42,MY SINK,42/77,42,80131509", WithCalc(0, p => (string)Run("UseSink", p)!));
    }

    [Fact]
    public void AStructByReference_IsWrittenBack()
    {
        Type pair = s_fixture.Value.Scenario.Assembly.GetType("ComImportFixture.Pair")!;
        object v = Activator.CreateInstance(pair)!;
        pair.GetField("A")!.SetValue(v, 1);
        pair.GetField("B")!.SetValue(v, 2);
        object r = WithCalc(0, p => Run("Swap", p, v)!);
        Assert.Equal(2, pair.GetField("A")!.GetValue(r));
        Assert.Equal(1, pair.GetField("B")!.GetValue(r));
    }

    [Fact]
    public void AFailedHResult_ThrowsAsBuiltInComInterop()
        => Assert.Equal("ArgumentException 80070057", WithCalc(0, p => (string)Run("Fail", p)!));

    [Fact]
    public void AnUnsupportedSignature_ThrowsNotSupported()
        => Assert.Equal("NotSupported", WithCalc(0, p => (string)Run("Arrays", p)!));

    [Theory]
    [InlineData(42)]
    [InlineData(-7L)]
    [InlineData(2.5)]
    [InlineData(true)]
    [InlineData("text")]
    [InlineData(null)]
    public void Variants_RoundTrip(object? value)
        => Assert.Equal(value, WithCalc(0, p => Run("Echo", p, value!)));

    [Fact]
    public void Variants_DecimalDateAndDBNull_RoundTrip()
    {
        Assert.Equal(123.4567m, WithCalc(0, p => Run("Echo", p, 123.4567m)));
        Assert.Equal(new DateTime(2026, 10, 19, 12, 30, 0), WithCalc(0, p => Run("Echo", p, new DateTime(2026, 10, 19, 12, 30, 0))));
        Assert.Equal(DBNull.Value, WithCalc(0, p => Run("Echo", p, DBNull.Value)));
    }

    [Fact]
    public void Variants_RetvalAndByReference()
    {
        Assert.Equal("from native", WithCalc(0, p => Run("Variant", p)));
        Assert.Equal(6, WithCalc(0, p => Run("Bump", p, 5)));
    }

    [Fact]
    public void ACast_QueryInterfaces_AndTheObjectIsOnePerIdentity()
    {
        Assert.False(WithCalc(0, p => (bool)Run("CastsToMissing", p)!));
        Assert.True(WithCalc(0, p => (bool)Run("SameObject", p)!));
    }

    [Fact]
    public void ADelegateCall_ThroughAVtableSlot_AsDXEngineMakesIt()
        => Assert.Equal(55, WithCalc(55, p => (int)Run("ThroughDelegate", p)!));

    [Fact]
    public void APInvoke_ReturningACOMInterface_IsWrapped()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "ole32's CreateBindCtx");
        Assert.Equal("ok", (string)Run("BindCtx")!);
    }

    [Fact]
    public void ABuiltInRcw_CastToARewrittenInterface_IsAdopted()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "built-in COM makes the RCW");
        Assert.Equal(9, WithCalc(0, p =>
        {
            object rcw = Marshal.GetObjectForIUnknown(p);     // the test assembly is not rewritten: a real RCW
            try { return (int)Run("AddThroughObject", rcw, 4, 5)!; }
            finally { Marshal.ReleaseComObject(rcw); }
        }));
    }

    [Fact]
    public void TheAssemblyLoadedFromMemory_SaysItIsItsOriginalFile()
    {
        string original = s_original.Value;
        string uri = new Uri(original).AbsoluteUri;
        var w = (string[])Run("Where")!;
        Assert.Equal(original, w[0]);              // typeof(X).Assembly.Location
        Assert.Equal(original, w[1]);              // GetExecutingAssembly().Location
        Assert.Equal(uri, w[2]);                   // CodeBase
        Assert.Equal(uri, w[3]);                   // EscapedCodeBase
        Assert.Equal(uri, w[4]);                   // GetName().CodeBase
        Assert.Equal(original, w[5]);              // Module.FullyQualifiedName
        Assert.Equal("ComImportFixture.dll", w[6]);
        Assert.Equal("True", w[7]);                // typeof(X).Assembly == Assembly.Load(name) == GetExecutingAssembly()
    }

    [Fact]
    public void ItsPInvokes_FindLibrariesBesideTheOriginal()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "the library beside it is a copy of version.dll");
        Assert.True((uint)Run("NativeBeside", Path.Combine(Environment.SystemDirectory, "kernel32.dll"))! > 0);
    }

    [Fact]
    public void ReleaseComObject_LetsGoOfTheNativeObject()
    {
        var o = New(s_calcVt, 0);
        int count = (int)Run("Release", (IntPtr)o)!;
        Assert.Equal(0, count);
        Assert.Equal(1, o->Refs);       // only ours left
        ((delegate* unmanaged<Obj*, uint>)s_calcVt[2])(o);
    }
}

/// <summary>The launcher's step: which app assemblies the deps then name rewritten copies of.</summary>
public sealed class ComImportStepTests
{
    private static string Fixture => typeof(ComImportStepTests).Assembly.GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>().First(a => a.Key == "ComImportFixture").Value!;

    private static System.Text.Json.Nodes.JsonObject Deps() => System.Text.Json.Nodes.JsonNode.Parse("""
        {
          "runtimeTarget": { "name": "T" },
          "targets": { "T": { "App/1.0.0": { "runtime": { "ComImportFixture.dll": {}, "Other.dll": {} } } } },
          "libraries": { "App/1.0.0": { "type": "package", "path": "app" } }
        }
        """)!.AsObject();

    private static (System.Text.Json.Nodes.JsonObject Deps, List<string> Notes) Apply(bool windows, string? setting = null)
    {
        string app = TestEnvironment.NewDirectory("comstep-app");
        File.Copy(Fixture, Path.Combine(app, "ComImportFixture.dll"));
        string cache = TestEnvironment.NewDirectory("comstep-cache");
        var deps = Deps();
        var notes = new List<string>();
        var list = new List<string>();
        WpfMac.ComImportStep.Apply(deps, app, AppContext.BaseDirectory, cache, windows, setting, notes, list);
        Assert.Empty(Directory.EnumerateFileSystemEntries(cache));       // nothing is written: the rewrite happens in memory, at load
        s_lastList = list;
        return (deps, notes);
    }

    [Fact]
    public void OnWindows_AnAssemblyWithAnUnsupportedUse_IsLeftOnBuiltInCom()
    {
        var (deps, notes) = Apply(windows: true);
        Assert.Contains(notes, n => n.Contains("ComImportFixture: left on built-in COM") && n.Contains("Arrays"));
        Assert.NotNull(deps["targets"]!["T"]!["App/1.0.0"]!["runtime"]!["ComImportFixture.dll"]);
        Assert.Empty(s_lastList!);
    }

    [ThreadStatic] private static List<string>? s_lastList;

    [Fact]
    public void OffWindows_TheSupportedPart_IsRewritten_AndLeavesTheDepsForTheLoader()
    {
        var (deps, notes) = Apply(windows: false);
        Assert.Contains(notes, n => n.Contains("ComImportFixture: rewritten in memory"));
        Assert.Null(deps["targets"]!["T"]!["App/1.0.0"]!["runtime"]!["ComImportFixture.dll"]);
        Assert.NotNull(deps["targets"]!["T"]!["App/1.0.0"]!["runtime"]!["Other.dll"]);
        string entry = Assert.Single(s_lastList!);
        Assert.StartsWith("ComImportFixture|", entry);
        Assert.EndsWith("ComImportFixture.dll", entry);
    }

    [Fact]
    public void AnEarlierWpfmacsCopies_AreRemoved()
    {
        string cache = TestEnvironment.NewDirectory("comstep-old");
        Directory.CreateDirectory(Path.Combine(cache, WpfMac.ComImportStep.Folder, "abc"));
        File.WriteAllText(Path.Combine(cache, WpfMac.ComImportStep.Folder, "abc", "X.dll"), "");
        WpfMac.ComImportStep.Apply(Deps(), TestEnvironment.NewDirectory("comstep-empty"), AppContext.BaseDirectory, cache, true, null, new(), new());
        Assert.False(Directory.Exists(Path.Combine(cache, WpfMac.ComImportStep.Folder)));
    }

    [Fact]
    public void TheSwitch_TurnsItOff_OrNamesTheAssemblies()
    {
        Assert.Contains(Apply(windows: false, "off").Notes, n => n.Contains("left (WPF_COMIMPORT_REWRITE)"));
        Assert.Contains(Apply(windows: false, "Some*;ComImport*").Notes, n => n.Contains("rewritten in memory"));
        Assert.Contains(Apply(windows: false, "Other").Notes, n => n.Contains("left (WPF_COMIMPORT_REWRITE)"));
    }
}
