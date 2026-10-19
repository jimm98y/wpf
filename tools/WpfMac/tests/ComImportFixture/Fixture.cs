// Interfaces as an app declares them for built-in COM interop. ComRewriterTests rewrites this assembly
// and calls Scenario's members against a native test object.
using System;
using System.Runtime.InteropServices;

namespace ComImportFixture
{
    [StructLayout(LayoutKind.Sequential)]
    public struct Pair { public int A, B; }

    [ComImport, Guid("6B0B1E2A-0C55-4B0E-9F2B-6C1D3E4F5A01"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ICalc
    {
        [PreserveSig] int Add(int a, int b);                    // slot 3
        int Twice(int x);                                       // slot 4: HRESULT + retval
        void Fail();                                            // slot 5: returns E_INVALIDARG
        void GetOther(out IOther other);                        // slot 6
        int ValueOf(IOther other);                              // slot 7: an interface in
        bool IsOdd(int x);                                      // slot 8: VARIANT_BOOL retval
        void Swap(ref Pair p);                                  // slot 9
        [PreserveSig] int Length([MarshalAs(UnmanagedType.LPWStr)] string s);   // slot 10
        IOther MakeOther(int value);                            // slot 11: an interface retval
        object Variant();                                       // slot 12: a VARIANT retval
        [PreserveSig] int BLen(string s);                       // slot 13: a BSTR in
        string Greet(string name);                              // slot 14: a BSTR retval
        void Upper(ref string s);                               // slot 15: a BSTR by reference
        object Echo(object v);                                  // slot 16: a VARIANT in, one back
        void Bump(ref object v);                                // slot 17: a VARIANT by reference
        [PreserveSig] int Arrays([MarshalAs(UnmanagedType.SafeArray)] int[] a);   // slot 18: unsupported (SAFEARRAY)
        string UseSink(ISink sink);                              // slot 19: native code calls a managed object
    }

    [ComImport, Guid("6B0B1E2A-0C55-4B0E-9F2B-6C1D3E4F5A02"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IOther
    {
        [PreserveSig] int Value();                              // slot 3
    }

    /// <summary>Implemented by managed code, called by native code (a CCW).</summary>
    [ComImport, Guid("6B0B1E2A-0C55-4B0E-9F2B-6C1D3E4F5A04"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ISink
    {
        [PreserveSig] int Add(int a, int b);                     // slot 3
        string Name(string prefix, bool loud);                   // slot 4: BSTR in, VARIANT_BOOL in, BSTR retval
        void Count(ref int n, out IOther made);                  // slot 5: by reference, an interface out
        object Echo(object v);                                   // slot 6: VARIANT in, VARIANT retval
        void Fail();                                             // slot 7: an exception -> its HRESULT
    }

    internal sealed class ManagedOther : IOther { public int Value() => 77; }

    internal sealed class ManagedSink : ISink
    {
        public int Add(int a, int b) => a + b;
        public string Name(string prefix, bool loud) => loud ? (prefix + " sink").ToUpperInvariant() : prefix + " sink";
        public void Count(ref int n, out IOther made) { n++; made = new ManagedOther(); }
        public object Echo(object v) => v is int i ? i * 2 : v;
        public void Fail() => throw new InvalidOperationException("no");
    }

    [ComImport, Guid("6B0B1E2A-0C55-4B0E-9F2B-6C1D3E4F5A03"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface INotThere { void Nothing(); }

    /// <summary>A call through a function pointer read from the vtable, as Ab3d.DXEngine.Wpf calls D3D9.</summary>
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int GetOtherFn(ICalc self, out IOther other);

    /// <summary>IBindCtx, declared by the app (Windows' ole32 makes one).</summary>
    [ComImport, Guid("0000000e-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAppBindCtx
    {
        void RegisterObjectBound([MarshalAs(UnmanagedType.IUnknown)] object o);
    }

    public static class Scenario
    {
        [DllImport("fixturenative", EntryPoint = "GetFileVersionInfoSizeW", CharSet = CharSet.Unicode)]
        private static extern uint FileVersionInfoSize(string file, out uint handle);

        /// <summary>A library beside the assembly's original file (Windows: a copy of version.dll).</summary>
        public static uint NativeBeside(string file) => FileVersionInfoSize(file, out _);

#pragma warning disable SYSLIB0012, SYSLIB0044
        /// <summary>Where the assembly says it is, every way it can be asked.</summary>
        public static string[] Where()
        {
            var a = typeof(Scenario).Assembly;
            return new[]
            {
                a.Location,
                System.Reflection.Assembly.GetExecutingAssembly().Location,
                a.CodeBase ?? "",
                a.EscapedCodeBase,
                a.GetName().CodeBase ?? "",
                a.ManifestModule.FullyQualifiedName,
                a.ManifestModule.Name,
                (a == System.Reflection.Assembly.Load("ComImportFixture") && a == System.Reflection.Assembly.GetExecutingAssembly()).ToString(),
            };
        }
        /// <summary>Where another assembly (not rewritten for COM: rewritten for its location calls) says this one is.</summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        public static string WhereByCaller() => LocationAskerFixture.Asker.CallersCodeBase();
#pragma warning restore SYSLIB0012, SYSLIB0044

        [DllImport("ole32.dll")]
        private static extern int CreateBindCtx(uint reserved, out IAppBindCtx ctx);

        /// <summary>A P/Invoke that returns a COM interface (Windows).</summary>
        public static string BindCtx()
        {
            int hr = CreateBindCtx(0, out IAppBindCtx ctx);
            if (hr < 0 || ctx == null) return "failed " + hr.ToString("x8");
            return Marshal.ReleaseComObject(ctx) == 0 ? "ok" : "still referenced";
        }

        internal static ICalc Wrap(IntPtr p) => (ICalc)Marshal.GetObjectForIUnknown(p);

        public static int Add(IntPtr p, int a, int b) => Wrap(p).Add(a, b);
        /// <summary>An object from somewhere else (a built-in RCW, on Windows) cast to a rewritten interface.</summary>
        public static int AddThroughObject(object o, int a, int b) => ((ICalc)o).Add(a, b);
        public static int Twice(IntPtr p, int x) => Wrap(p).Twice(x);
        public static string Fail(IntPtr p)
        {
            try { Wrap(p).Fail(); return "no exception"; }
            catch (ArgumentException e) { return "ArgumentException " + e.HResult.ToString("x8"); }
            catch (Exception e) { return e.GetType().Name; }
        }
        public static int OtherValue(IntPtr p) { Wrap(p).GetOther(out IOther o); return o.Value(); }
        public static int ValueOfOther(IntPtr p) { var c = Wrap(p); c.GetOther(out IOther o); return c.ValueOf(o); }
        public static int ValueOfManaged(IntPtr p) => Wrap(p).ValueOf(new ManagedOther());
        public static string UseSink(IntPtr p) => Wrap(p).UseSink(new ManagedSink());
        public static bool IsOdd(IntPtr p, int x) => Wrap(p).IsOdd(x);
        public static Pair Swap(IntPtr p, Pair x) { Wrap(p).Swap(ref x); return x; }
        public static int Length(IntPtr p, string s) => Wrap(p).Length(s);
        public static int BLen(IntPtr p, string s) => Wrap(p).BLen(s);
        public static string Greet(IntPtr p, string s) => Wrap(p).Greet(s);
        public static string Upper(IntPtr p, string s) { Wrap(p).Upper(ref s); return s; }
        public static int MadeOther(IntPtr p, int v) => Wrap(p).MakeOther(v).Value();
        public static object Variant(IntPtr p) => Wrap(p).Variant();
        public static object Echo(IntPtr p, object v) => Wrap(p).Echo(v);
        public static object Bump(IntPtr p, object v) { Wrap(p).Bump(ref v); return v; }
        public static string Arrays(IntPtr p)
        {
            try { Wrap(p).Arrays(new[] { 1 }); return "no exception"; }
            catch (NotSupportedException) { return "NotSupported"; }
        }
        public static bool CastsToMissing(IntPtr p) => Wrap(p) is INotThere;
        public static bool SameObject(IntPtr p) => ReferenceEquals(Marshal.GetObjectForIUnknown(p), Marshal.GetObjectForIUnknown(p));
        public static int Release(IntPtr p) { var c = Wrap(p); c.Add(1, 1); return Marshal.ReleaseComObject(c); }

        /// <summary>DXEngine's way: the slot through GetComInterfaceForObject, the call through a delegate.</summary>
        public static int ThroughDelegate(IntPtr p)
        {
            ICalc c = Wrap(p);
            IntPtr itf = Marshal.GetComInterfaceForObject(c, typeof(ICalc));
            try
            {
                IntPtr fn = Marshal.ReadIntPtr(Marshal.ReadIntPtr(itf), 6 * IntPtr.Size);
                var d = Marshal.GetDelegateForFunctionPointer<GetOtherFn>(fn);
                int hr = d(c, out IOther other);
                return hr < 0 ? hr : other.Value();
            }
            finally { Marshal.Release(itf); }
        }
    }
}
