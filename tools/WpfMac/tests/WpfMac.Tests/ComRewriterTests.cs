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
        var vt = (IntPtr*)NativeMemory.AllocZeroed(13, (nuint)sizeof(IntPtr));
        vt[0] = (IntPtr)(delegate* unmanaged<Obj*, Guid*, IntPtr*, int>)&QueryInterface;
        vt[1] = (IntPtr)(delegate* unmanaged<Obj*, uint>)&AddRef;
        vt[2] = (IntPtr)(delegate* unmanaged<Obj*, uint>)&Release;
        vt[3] = (IntPtr)(delegate* unmanaged<Obj*, int, int, int>)&Add;
        vt[4] = (IntPtr)(delegate* unmanaged<Obj*, int, int*, int>)&Twice;
        vt[5] = (IntPtr)(delegate* unmanaged<Obj*, int>)&Fail;
        vt[6] = (IntPtr)(delegate* unmanaged<Obj*, IntPtr*, int>)&GetOther;
        vt[7] = (IntPtr)(delegate* unmanaged<Obj*, Obj*, int*, int>)&ValueOf;
        vt[8] = (IntPtr)(delegate* unmanaged<Obj*, int, short*, int>)&IsOdd;
        vt[9] = (IntPtr)(delegate* unmanaged<Obj*, int*, int>)&Swap;
        vt[10] = (IntPtr)(delegate* unmanaged<Obj*, char*, int>)&Length;
        vt[11] = (IntPtr)(delegate* unmanaged<Obj*, int, IntPtr*, int>)&MakeOther;
        vt[12] = (IntPtr)(delegate* unmanaged<Obj*, void*, int>)&Variant;
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
    [UnmanagedCallersOnly] private static int ValueOf(Obj* self, Obj* other, int* r) { *r = other == null ? -1 : 1000 + other->Value; return 0; }
    [UnmanagedCallersOnly] private static int IsOdd(Obj* self, int x, short* r) { *r = (short)((x & 1) != 0 ? -1 : 0); return 0; }
    [UnmanagedCallersOnly] private static int Swap(Obj* self, int* p) { (p[0], p[1]) = (p[1], p[0]); return 0; }
    [UnmanagedCallersOnly] private static int Length(Obj* self, char* s) { int n = 0; while (s[n] != 0) n++; return n; }
    [UnmanagedCallersOnly] private static int MakeOther(Obj* self, int v, IntPtr* other) { *other = (IntPtr)New(s_otherVt, v); return 0; }
    [UnmanagedCallersOnly] private static int Variant(Obj* self, void* v) => 0;
    [UnmanagedCallersOnly] private static int Value(Obj* self) => self->Value;

    // ---- the rewritten fixture ------------------------------------------------------------------

    private static readonly Lazy<(Type Scenario, RewriteResult Result)> s_fixture = new(() =>
    {
        string input = typeof(ComRewriterTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().First(a => a.Key == "ComImportFixture").Value!;
        string output = Path.Combine(TestEnvironment.NewDirectory("comrewrite"), "ComImportFixture.dll");
        var result = ComRewriter.Rewrite(input, output, new[] { AppContext.BaseDirectory }, partial: true);
        var alc = new AssemblyLoadContext("comimport-fixture");
        var asm = alc.LoadFromAssemblyPath(output);
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
        Assert.True(r.Written);
        Assert.Equal(4, r.Interfaces);
        Assert.Equal(1, r.PInvokes);
        string u = Assert.Single(r.Unsupported);
        Assert.Contains("Variant", u);
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
        => Assert.Equal("NotSupported", WithCalc(0, p => (string)Run("Variant", p)!));

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
        WpfMac.ComImportStep.Apply(deps, app, AppContext.BaseDirectory, cache, windows, setting, notes);
        return (deps, notes);
    }

    [Fact]
    public void OnWindows_AnAssemblyWithAnUnsupportedUse_IsLeftOnBuiltInCom()
    {
        var (deps, notes) = Apply(windows: true);
        Assert.Contains(notes, n => n.Contains("ComImportFixture: left on built-in COM") && n.Contains("Variant"));
        Assert.NotNull(deps["targets"]!["T"]!["App/1.0.0"]!["runtime"]!["ComImportFixture.dll"]);
        Assert.Null(deps["targets"]!["T"]![WpfMac.ComImportStep.Library]);
    }

    [Fact]
    public void OffWindows_TheSupportedPart_IsRewritten_AndTheDepsNameTheCopy()
    {
        var (deps, notes) = Apply(windows: false);
        Assert.Contains(notes, n => n.Contains("ComImportFixture: rewritten"));
        Assert.Null(deps["targets"]!["T"]!["App/1.0.0"]!["runtime"]!["ComImportFixture.dll"]);
        Assert.NotNull(deps["targets"]!["T"]!["App/1.0.0"]!["runtime"]!["Other.dll"]);
        var copy = Assert.Single(deps["targets"]!["T"]![WpfMac.ComImportStep.Library]!["runtime"]!.AsObject());
        Assert.EndsWith("/ComImportFixture.dll", copy.Key);
        Assert.Equal("comimport", deps["libraries"]![WpfMac.ComImportStep.Library]!["path"]!.GetValue<string>());
    }

    [Fact]
    public void TheSwitch_TurnsItOff_OrNamesTheAssemblies()
    {
        Assert.Contains(Apply(windows: false, "off").Notes, n => n.Contains("left (WPF_COMIMPORT_REWRITE)"));
        Assert.Contains(Apply(windows: false, "Some*;ComImport*").Notes, n => n.Contains("rewritten"));
        Assert.Contains(Apply(windows: false, "Other").Notes, n => n.Contains("left (WPF_COMIMPORT_REWRITE)"));
    }
}
