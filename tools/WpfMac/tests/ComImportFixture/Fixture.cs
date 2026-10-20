// Interfaces as an app declares them for built-in COM interop. ComRewriterTests rewrites this assembly
// and calls Scenario's members against a native test object.
using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

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
        [PreserveSig] int Arrays([MarshalAs(UnmanagedType.SafeArray)] Pair[] a);  // slot 18: unsupported (a SAFEARRAY of records)
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

    /// <summary>The BCL's own COM interfaces (System.Runtime.InteropServices.ComTypes) in an app's interface.</summary>
    [ComImport, Guid("6B0B1E2A-0C55-4B0E-9F2B-6C1D3E4F5A05"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IStreams
    {
        IStream Open(int length);                                // slot 3: a native IStream
        [PreserveSig] int Sum(IStream s);                        // slot 4: native code reads a stream
        IEnumString Names();                                     // slot 5: a native IEnumString
        [PreserveSig] int Count(IEnumString e);                  // slot 6: native code enumerates
    }

    /// <summary>SAFEARRAYs: COM's default for an array in a COM interface method.</summary>
    [ComImport, Guid("6B0B1E2A-0C55-4B0E-9F2B-6C1D3E4F5A06"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ISafeArrays
    {
        [PreserveSig] int Sum(int[] a);                          // slot 3: native code reads the descriptor itself
        string[] Names(int n);                                   // slot 4: a SAFEARRAY of BSTRs as retval
        void Reverse(ref object[] a);                            // slot 5: VARIANTs, replaced by the callee
        object Matrix();                                         // slot 6: a VARIANT holding a 2x3 VT_R8 array
        [PreserveSig] int Weighted(Array m);                     // slot 7: a 2-D array: column-major, as COM lays it out
        string UseSink(IArraySink sink);                         // slot 8: native code calls a managed object with arrays
    }

    [ComImport, Guid("6B0B1E2A-0C55-4B0E-9F2B-6C1D3E4F5A07"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IArraySink
    {
        [PreserveSig] int Sum(short[] a);                        // slot 3
        string[] Make(int n);                                    // slot 4: retval
        void Double(ref int[] a);                                // slot 5: by reference, replaced
    }

    internal sealed class ManagedArraySink : IArraySink
    {
        public int Sum(short[] a) { int s = 0; foreach (short x in a) s += x; return s; }
        public string[] Make(int n) { var r = new string[n]; for (int i = 0; i < n; i++) r[i] = new string('m', i + 1); return r; }
        public void Double(ref int[] a) { var r = new int[a.Length * 2]; for (int i = 0; i < r.Length; i++) r[i] = a[i % a.Length]; a = r; }
    }

    /// <summary>A dispinterface: called through IDispatch::Invoke (the object answers IDispatch only).</summary>
    [ComImport, Guid("6B0B1E2A-0C55-4B0E-9F2B-6C1D3E4F5A08"), InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    internal interface ICounterDisp
    {
        [DispId(1)] int Add(int a, int b);
        [DispId(2)] string Name { get; set; }
        [DispId(3)] void Bump(ref int n);
        int ByName(string s);                                    // no [DispId]: GetIDsOfNames
        [DispId(5)] void Fail();                                 // DISP_E_EXCEPTION
    }

    /// <summary>A dispinterface implemented by managed code (an event sink's shape), called by native code.</summary>
    [ComImport, Guid("6B0B1E2A-0C55-4B0E-9F2B-6C1D3E4F5A09"), InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    internal interface IDispSink
    {
        [DispId(7)] string Echo(string s, int times);
        [DispId(8)] int Total { get; }
        [DispId(9)] void Twice(ref int n);
        [DispId(10)] void Throw();
    }

    [ComImport, Guid("6B0B1E2A-0C55-4B0E-9F2B-6C1D3E4F5A0A"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    internal interface IDualThing
    {
        [DispId(11)] int Square(int x);
    }

    [ComImport, Guid("6B0B1E2A-0C55-4B0E-9F2B-6C1D3E4F5A0B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IDispUser
    {
        [return: MarshalAs(UnmanagedType.IDispatch)] object Counter();          // slot 3: a native IDispatch-only object
        string UseSink(IDispSink sink);                                          // slot 4
        [PreserveSig] int UseDual([MarshalAs(UnmanagedType.IDispatch)] object o);   // slot 5: a managed dual object's IDispatch
    }

    internal sealed class ManagedDispSink : IDispSink
    {
        public string Echo(string s, int times) { var r = ""; for (int i = 0; i < times; i++) r += s; return r; }
        public int Total => 99;
        public void Twice(ref int n) => n *= 2;
        public void Throw() => throw new InvalidOperationException("sink failed");
    }

    internal sealed class ManagedDual : IDualThing { public int Square(int x) => x * x; }

    // ---- COM events, as tlbimp writes them: a source dispinterface, its event interface, provider and sink helper ----

    [ComImport, Guid("6B0B1E2A-0C55-4B0E-9F2B-6C1D3E4F5A0C"), InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    internal interface ITickEvents
    {
        [DispId(1)] void Tick(int n);
        [DispId(2)] void Done(string why);
    }

    public delegate void ITickEvents_TickEventHandler(int n);
    public delegate void ITickEvents_DoneEventHandler(string why);

    [ComEventInterface(typeof(ITickEvents), typeof(ITickEvents_EventProvider)), ComVisible(false)]
    internal interface ITickEvents_Event
    {
        event ITickEvents_TickEventHandler Tick;
        event ITickEvents_DoneEventHandler Done;
    }

    /// <summary>tlbimp's provider: finds the source's connection point and Advises a sink helper per handler.</summary>
    internal sealed class ITickEvents_EventProvider : ITickEvents_Event
    {
        private readonly IConnectionPointContainer _container;
        private IConnectionPoint? _point;
        private readonly System.Collections.Generic.List<ITickEvents_SinkHelper> _sinks = new();

        public ITickEvents_EventProvider(object source) { _container = (IConnectionPointContainer)source; }

        private void Init()
        {
            if (_point != null) return;
            Guid g = typeof(ITickEvents).GUID;
            _container.FindConnectionPoint(ref g, out _point);
        }

        private void Add(ITickEvents_SinkHelper h)
        {
            lock (_sinks) { Init(); _point!.Advise(h, out h.Cookie); _sinks.Add(h); }
        }

        private void Remove(Func<ITickEvents_SinkHelper, bool> match)
        {
            lock (_sinks)
            {
                var h = _sinks.Find(x => match(x));
                if (h == null) return;
                _point!.Unadvise(h.Cookie);
                _sinks.Remove(h);
            }
        }

        public event ITickEvents_TickEventHandler Tick
        {
            add => Add(new ITickEvents_SinkHelper { TickDelegate = value });
            remove => Remove(h => h.TickDelegate == value);
        }

        public event ITickEvents_DoneEventHandler Done
        {
            add => Add(new ITickEvents_SinkHelper { DoneDelegate = value });
            remove => Remove(h => h.DoneDelegate == value);
        }
    }

    [ClassInterface(ClassInterfaceType.None)]
    internal sealed class ITickEvents_SinkHelper : ITickEvents
    {
        public ITickEvents_TickEventHandler? TickDelegate;
        public ITickEvents_DoneEventHandler? DoneDelegate;
        public int Cookie;
        public void Tick(int n) => TickDelegate?.Invoke(n);
        public void Done(string why) => DoneDelegate?.Invoke(why);
    }

    /// <summary>An event source: raises the .NET events named as ITickEvents's methods.</summary>
    [ComSourceInterfaces(typeof(ITickEvents)), ClassInterface(ClassInterfaceType.None)]
    public sealed class Ticker
    {
        public event ITickEvents_TickEventHandler? Tick;
        public event ITickEvents_DoneEventHandler? Done;
        public void Run(int n) { for (int i = 1; i <= n; i++) Tick?.Invoke(i); Done?.Invoke("ran " + n); }
    }

    // ---- formatted types, custom marshalers, StringBuilder ----

    /// <summary>A blittable formatted class (built-in interop pins it: the callee's writes show).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public class Box { public int A; public int B; }

    /// <summary>A formatted class with a string: copied in, and back only when [Out].</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public class Person { [MarshalAs(UnmanagedType.LPWStr)] public string Name = ""; public int Age; }

    /// <summary>A struct with a string, by reference and in C arrays.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Tag { [MarshalAs(UnmanagedType.LPWStr)] public string Text; public int N; }

    /// <summary>An app's custom marshaler (as MediaFoundation's PVMarshaler): native {A, B}, written back into
    /// the same object.</summary>
    public sealed class BoxMarshaler : ICustomMarshaler
    {
        private static readonly BoxMarshaler s_one = new();
        private Box? _last;
        public static ICustomMarshaler GetInstance(string cookie) => s_one;
        public IntPtr MarshalManagedToNative(object o) { _last = (Box)o; IntPtr p = Marshal.AllocCoTaskMem(8); Marshal.WriteInt32(p, _last.A); Marshal.WriteInt32(p, 4, _last.B); return p; }
        public object MarshalNativeToManaged(IntPtr p) { var b = _last ?? new Box(); b.A = Marshal.ReadInt32(p); b.B = Marshal.ReadInt32(p, 4); return b; }
        public void CleanUpNativeData(IntPtr p) => Marshal.FreeCoTaskMem(p);
        public void CleanUpManagedData(object o) { }
        public int GetNativeDataSize() => 8;
    }

    [ComImport, Guid("6B0B1E2A-0C55-4B0E-9F2B-6C1D3E4F5A0D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IFormatted
    {
        [PreserveSig] int GuidSum([In, MarshalAs(UnmanagedType.LPStruct)] Guid g);                       // slot 3
        [PreserveSig] int Fill(Box b);                                                                  // slot 4
        [PreserveSig] int Named([In] Person p);                                                         // slot 5
        void Rename(ref Tag t);                                                                         // slot 6
        void Text([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder sb, int cch);        // slot 7
        [PreserveSig] int Custom([In, Out, MarshalAs(UnmanagedType.CustomMarshaler, MarshalTypeRef = typeof(BoxMarshaler))] Box b);   // slot 8
        [PreserveSig] int Tags([In, Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] Tag[] tags, int n);                    // slot 9
        [PreserveSig] int Chars([MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] char[] c, int n);                              // slot 10
        string UseSink(IFormattedSink sink);                                                            // slot 11
    }

    [ComImport, Guid("6B0B1E2A-0C55-4B0E-9F2B-6C1D3E4F5A0E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IFormattedSink
    {
        [PreserveSig] int Area(Box b);                                                                  // slot 3
        void Retag(ref Tag t);                                                                          // slot 4
        [PreserveSig] int First([In, MarshalAs(UnmanagedType.LPStruct)] Guid g);                       // slot 5
        [PreserveSig] int Older([In, Out] Person p);                                                    // slot 6
    }

    internal sealed class ManagedFormattedSink : IFormattedSink
    {
        public int Area(Box b) { int r = b.A * b.B; b.A = 5; return r; }
        public void Retag(ref Tag t) { t.Text += "?"; t.N += 10; }
        public int First(Guid g) => g.ToByteArray()[0];
        public int Older(Person p) { p.Age++; p.Name += " Sr."; return p.Name.Length; }
    }

    /// <summary>A managed IStream over bytes, handed to native code.</summary>
    internal sealed class ManagedStream : IStream
    {
        private readonly byte[] _bytes; private int _pos;
        public ManagedStream(params byte[] bytes) { _bytes = bytes; }
        public void Read(byte[] pv, int cb, IntPtr pcbRead)
        {
            int n = Math.Min(cb, _bytes.Length - _pos);
            Array.Copy(_bytes, _pos, pv, 0, n); _pos += n;
            if (pcbRead != IntPtr.Zero) Marshal.WriteInt32(pcbRead, n);
        }
        public void Write(byte[] pv, int cb, IntPtr pcbWritten) => throw new NotSupportedException();
        public void Seek(long dlibMove, int dwOrigin, IntPtr plibNewPosition)
        {
            _pos = (int)(dwOrigin == 0 ? dlibMove : dwOrigin == 1 ? _pos + dlibMove : _bytes.Length + dlibMove);
            if (plibNewPosition != IntPtr.Zero) Marshal.WriteInt64(plibNewPosition, _pos);
        }
        public void SetSize(long libNewSize) => throw new NotSupportedException();
        public void CopyTo(IStream pstm, long cb, IntPtr pcbRead, IntPtr pcbWritten) => throw new NotSupportedException();
        public void Commit(int grfCommitFlags) { }
        public void Revert() { }
        public void LockRegion(long libOffset, long cb, int dwLockType) => throw new NotSupportedException();
        public void UnlockRegion(long libOffset, long cb, int dwLockType) => throw new NotSupportedException();
        public void Stat(out STATSTG pstatstg, int grfStatFlag) => pstatstg = new STATSTG { pwcsName = "managed.bin", cbSize = _bytes.Length, type = 2 };
        public void Clone(out IStream ppstm) => ppstm = new ManagedStream(_bytes) { _pos = _pos };
    }

    internal sealed class ManagedNames : IEnumString
    {
        private readonly string[] _names; private int _at;
        public ManagedNames(params string[] names) { _names = names; }
        public int Next(int celt, string[] rgelt, IntPtr pceltFetched)
        {
            int n = 0;
            for (; n < celt && _at < _names.Length; n++) rgelt[n] = _names[_at++];
            if (pceltFetched != IntPtr.Zero) Marshal.WriteInt32(pceltFetched, n);
            return n == celt ? 0 : 1;
        }
        public int Skip(int celt) { _at += celt; return 0; }
        public void Reset() => _at = 0;
        public void Clone(out IEnumString ppenum) => ppenum = new ManagedNames(_names) { _at = _at };
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

        // ---- the BCL's ComTypes ----
        private static IStreams Streams(IntPtr p) => (IStreams)Marshal.GetObjectForIUnknown(p);

        // ---- SAFEARRAYs ----
        private static ISafeArrays Arrays2(IntPtr p) => (ISafeArrays)Marshal.GetObjectForIUnknown(p);
        public static int SafeSum(IntPtr p) => Arrays2(p).Sum(new[] { 1, 2, 3, 40 });
        public static int SafeSumEmpty(IntPtr p) => Arrays2(p).Sum(new int[0]);
        public static string SafeNames(IntPtr p) => string.Join(",", Arrays2(p).Names(3));
        public static string SafeReverse(IntPtr p)
        {
            object[] a = { 1, "two", 3.5 };
            Arrays2(p).Reverse(ref a);
            return string.Join(",", a) + "|" + a[0].GetType().Name;
        }
        public static string SafeMatrix(IntPtr p)
        {
            object m = Arrays2(p).Matrix();
            var d = (double[,])m;
            return $"{d.GetLength(0)}x{d.GetLength(1)}:{d[0, 0]},{d[0, 2]},{d[1, 0]},{d[1, 2]}";
        }
        public static int SafeWeighted(IntPtr p) => Arrays2(p).Weighted(new int[,] { { 1, 2, 3 }, { 4, 5, 6 } });
        public static string SafeSink(IntPtr p) => Arrays2(p).UseSink(new ManagedArraySink());

        // ---- formatted ----
        private static IFormatted Fmt(IntPtr p) => (IFormatted)Marshal.GetObjectForIUnknown(p);
        public static string Formatted(IntPtr p)
        {
            var f = Fmt(p);
            var parts = new System.Collections.Generic.List<string>();
            parts.Add(f.GuidSum(new Guid("01020304-0506-0708-090a-0b0c0d0e0f10")).ToString());
            var box = new Box { A = 1, B = 21 };
            parts.Add($"{f.Fill(box)}:{box.A}");
            var person = new Person { Name = "Ada", Age = 36 };
            parts.Add($"{f.Named(person)}:{person.Age}");
            var tag = new Tag { Text = "abc", N = 1 };
            f.Rename(ref tag);
            parts.Add($"{tag.Text}:{tag.N}");
            var sb = new System.Text.StringBuilder(8);
            f.Text(sb, 8);
            parts.Add(sb.ToString());
            var cb = new Box { A = 3, B = 4 };
            parts.Add($"{f.Custom(cb)}:{cb.A},{cb.B}");
            var tags = new[] { new Tag { Text = "x", N = 1 }, new Tag { Text = "yy", N = 2 } };
            parts.Add($"{f.Tags(tags, 2)}:{tags[0].N},{tags[1].N}");
            parts.Add(f.Chars("AB".ToCharArray(), 2).ToString());
            return string.Join("|", parts);
        }
        public static string FormattedSink(IntPtr p) => Fmt(p).UseSink(new ManagedFormattedSink());

        // ---- events ----
        /// <summary>A managed event source seen as a COM object (its CCW, wrapped back): subscribed through the
        /// event interface, which goes provider -> FindConnectionPoint -> Advise(sink helper) over vtables, and
        /// called back source -> sink (IDispatch::Invoke) -> sink helper -> the handler.</summary>
        public static string Events()
        {
            var ticker = new Ticker();
            IntPtr unk = Marshal.GetIUnknownForObject(ticker);
            object com = Marshal.GetObjectForIUnknown(unk);
            Marshal.Release(unk);
            var ev = (ITickEvents_Event)com;
            var log = new System.Collections.Generic.List<string>();
            ITickEvents_TickEventHandler tick = n => log.Add("t" + n);
            ev.Tick += tick;
            ev.Done += why => log.Add(why);
            ticker.Run(3);
            ev.Tick -= tick;
            ticker.Run(1);
            return string.Join(",", log);
        }

        // ---- IDispatch ----
        private static IDispUser Disp(IntPtr p) => (IDispUser)Marshal.GetObjectForIUnknown(p);
        public static string DispCounter(IntPtr p)
        {
            var c = (ICounterDisp)Disp(p).Counter();
            int sum = c.Add(2, 3);
            c.Name = "abc";
            int n = 41;
            c.Bump(ref n);
            string fail;
            try { c.Fail(); fail = "no exception"; }
            catch (COMException e) { fail = $"{e.Message}:{e.HResult:x8}"; }
            return $"{sum}|{c.Name}|{n}|{c.ByName("four")}|{fail}";
        }
        public static string DispSink(IntPtr p) => Disp(p).UseSink(new ManagedDispSink());
        public static int DispDual(IntPtr p) => Disp(p).UseDual(new ManagedDual());

        /// <summary>A native IStream: read in pieces, Stat, Seek and Clone.</summary>
        public static unsafe string ReadNative(IntPtr p)
        {
            IStream s = Streams(p).Open(5);
            var all = new System.Collections.Generic.List<byte>();
            var buf = new byte[3];
            int got;
            do { s.Read(buf, buf.Length, (IntPtr)(&got)); for (int i = 0; i < got; i++) all.Add(buf[i]); } while (got > 0);
            s.Stat(out STATSTG st, 0);
            long pos;
            s.Seek(1, 0, (IntPtr)(&pos));
            s.Clone(out IStream c);
            c.Read(buf, 1, (IntPtr)(&got));
            return $"{string.Join(",", all)}|{st.pwcsName}|{st.cbSize}|{pos}|{buf[0]}";
        }

        /// <summary>A managed IStream read by native code.</summary>
        public static int SumManaged(IntPtr p) => Streams(p).Sum(new ManagedStream(1, 2, 3, 4));

        /// <summary>A native IStream handed back to native code.</summary>
        public static int SumNative(IntPtr p) { var x = Streams(p); return x.Sum(x.Open(4)); }

        /// <summary>A native IEnumString: Next in pieces, Skip, Reset, Clone.</summary>
        public static unsafe string NamesNative(IntPtr p)
        {
            IEnumString e = Streams(p).Names();
            var a = new string[2];
            int n;
            int hr = e.Next(2, a, (IntPtr)(&n));
            string first = $"{hr}:{n}:{a[0]},{a[1]}";
            hr = e.Next(2, a, (IntPtr)(&n));
            string second = $"{hr}:{n}:{a[0]}";
            e.Reset();
            e.Skip(1);
            e.Clone(out IEnumString c);
            c.Next(1, a, IntPtr.Zero);
            return $"{first}|{second}|{a[0]}";
        }

        /// <summary>A managed IEnumString enumerated by native code.</summary>
        public static int CountManaged(IntPtr p) => Streams(p).Count(new ManagedNames("a", "bb", "ccc"));

        [DllImport("ole32.dll")]
        private static extern int CreateBindCtx(uint reserved, out IBindCtx ctx);

        /// <summary>The BCL's IBindCtx, from ole32 (Windows): options, an object parameter, its enumeration.</summary>
        public static string BclBindCtx()
        {
            Marshal.ThrowExceptionForHR(CreateBindCtx(0, out IBindCtx ctx));
            var o = new BIND_OPTS { cbStruct = Marshal.SizeOf<BIND_OPTS>(), grfMode = 2 };
            ctx.SetBindOptions(ref o);
            var back = new BIND_OPTS { cbStruct = o.cbStruct };
            ctx.GetBindOptions(ref back);
            ctx.RegisterObjectParam("key", new ManagedNames("x"));
            ctx.GetObjectParam("key", out object got);
            // ole32 answers E_NOTIMPL here: built-in COM throws NotImplementedException
            string keys;
            try { ctx.EnumObjectParam(out IEnumString e); keys = "enumerated"; }
            catch (NotImplementedException) { keys = "notimpl"; }
            int revoked = ctx.RevokeObjectParam("key");
            int again = ctx.RevokeObjectParam("key");
            return $"{back.grfMode}|{got != null}|{keys}|{revoked}|{again != 0}";
        }

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
            try { Wrap(p).Arrays(new[] { new Pair() }); return "no exception"; }
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
