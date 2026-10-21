// The COM-import rewriter (Documentation/comimport-rewriter.md): ComImportFixture, an assembly with
// [ComImport] interfaces as an app declares them, is rewritten and its rewritten copy is run against
// a COM object of our own (vtables of UnmanagedCallersOnly functions). On every OS, without built-in
// COM interop.

using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using WpfWebGpu.ComRewrite;
using Xunit;

namespace WpfWebGpu.Tests;

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
        vt[12] = (IntPtr)(delegate* unmanaged<Obj*, WpfWebGpu.ComInterop.ComVariant*, int>)&Variant;
        vt[13] = (IntPtr)(delegate* unmanaged<Obj*, char*, int>)&BLen;
        vt[14] = (IntPtr)(delegate* unmanaged<Obj*, char*, IntPtr*, int>)&Greet;
        vt[15] = (IntPtr)(delegate* unmanaged<Obj*, IntPtr*, int>)&Upper;
        vt[16] = (IntPtr)(delegate* unmanaged<Obj*, WpfWebGpu.ComInterop.ComVariant, WpfWebGpu.ComInterop.ComVariant*, int>)&Echo;
        vt[17] = (IntPtr)(delegate* unmanaged<Obj*, WpfWebGpu.ComInterop.ComVariant*, int>)&Bump;
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
        WpfWebGpu.ComInterop.ComVariant v, e;
        WpfWebGpu.ComInterop.Com.ToVariant(21, (IntPtr)(&v));
        hr = ((delegate* unmanaged<IntPtr, WpfWebGpu.ComInterop.ComVariant, WpfWebGpu.ComInterop.ComVariant*, int>)vt[6])(sink, v, &e);
        parts.Add(WpfWebGpu.ComInterop.Com.FromVariant((IntPtr)(&e))!.ToString()!);
        WpfWebGpu.ComInterop.Com.ClearVariant((IntPtr)(&e));
        parts.Add(((delegate* unmanaged<IntPtr, int>)vt[7])(sink).ToString("x8"));
        *r = Marshal.StringToBSTR(string.Join(",", parts));
        return 0;
    }
    [UnmanagedCallersOnly] private static int IsOdd(Obj* self, int x, short* r) { *r = (short)((x & 1) != 0 ? -1 : 0); return 0; }
    [UnmanagedCallersOnly] private static int Swap(Obj* self, int* p) { (p[0], p[1]) = (p[1], p[0]); return 0; }
    [UnmanagedCallersOnly] private static int Length(Obj* self, char* s) { int n = 0; while (s[n] != 0) n++; return n; }
    [UnmanagedCallersOnly] private static int MakeOther(Obj* self, int v, IntPtr* other) { *other = (IntPtr)New(s_otherVt, v); return 0; }
    // VARIANTs, through the same conversions the rewritten caller uses (a native callee would do the same by hand)
    [UnmanagedCallersOnly] private static int Variant(Obj* self, WpfWebGpu.ComInterop.ComVariant* r) { WpfWebGpu.ComInterop.Com.ToVariant("from native", (IntPtr)r); return 0; }
    [UnmanagedCallersOnly]
    private static int Echo(Obj* self, WpfWebGpu.ComInterop.ComVariant v, WpfWebGpu.ComInterop.ComVariant* r)
    {
        // the callee owns nothing of v; its copy goes back as a new VARIANT
        object? o = WpfWebGpu.ComInterop.Com.FromVariant((IntPtr)(&v));
        WpfWebGpu.ComInterop.Com.ToVariant(o, (IntPtr)r);
        return 0;
    }
    [UnmanagedCallersOnly]
    private static int Bump(Obj* self, WpfWebGpu.ComInterop.ComVariant* v)
    {
        object? o = WpfWebGpu.ComInterop.Com.FromVariant((IntPtr)v);
        WpfWebGpu.ComInterop.Com.ClearVariant((IntPtr)v);
        WpfWebGpu.ComInterop.Com.ToVariant(o is int i ? i + 1 : (object)"not an int", (IntPtr)v);
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

    // ---- the BCL's ComTypes: IStreams, a native IStream, a native IEnumString -------------------

    private static readonly Guid IID_IStreams = new("6B0B1E2A-0C55-4B0E-9F2B-6C1D3E4F5A05");
    private static readonly Guid IID_IStream = new("0000000c-0000-0000-C000-000000000046");
    private static readonly Guid IID_IEnumString = new("00000101-0000-0000-C000-000000000046");

    /// <summary>A native stream or enumerator: { vtable, refs, length, position } (Obj's first fields).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Cursor { public IntPtr* Vtbl; public int Refs; public int Len; public int Pos; public int Value0() => 0; }

    /// <summary>STATSTG as native code lays it out.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct NStatstg
    {
        public IntPtr Name; public int Type; public long Size;
        public long M, C, A; public int Mode, Locks; public Guid Clsid; public int State, Reserved;
    }

    private static readonly string[] s_names = { "one", "two", "three" };
    private static readonly IntPtr* s_streamsVt = BuildStreams();
    private static readonly IntPtr* s_streamVt = BuildStream();
    private static readonly IntPtr* s_enumVt = BuildEnum();

    private static IntPtr* BuildStreams()
    {
        var vt = (IntPtr*)NativeMemory.AllocZeroed(7, (nuint)sizeof(IntPtr));
        vt[0] = (IntPtr)(delegate* unmanaged<Obj*, Guid*, IntPtr*, int>)&CursorQueryInterface;
        vt[1] = (IntPtr)(delegate* unmanaged<Obj*, uint>)&AddRef;
        vt[2] = (IntPtr)(delegate* unmanaged<Obj*, uint>)&Release;
        vt[3] = (IntPtr)(delegate* unmanaged<Obj*, int, IntPtr*, int>)&Open;
        vt[4] = (IntPtr)(delegate* unmanaged<Obj*, IntPtr, int>)&Sum;
        vt[5] = (IntPtr)(delegate* unmanaged<Obj*, IntPtr*, int>)&Names;
        vt[6] = (IntPtr)(delegate* unmanaged<Obj*, IntPtr, int>)&Count;
        return vt;
    }

    private static IntPtr* BuildStream()
    {
        var vt = (IntPtr*)NativeMemory.AllocZeroed(14, (nuint)sizeof(IntPtr));
        vt[0] = (IntPtr)(delegate* unmanaged<Obj*, Guid*, IntPtr*, int>)&CursorQueryInterface;
        vt[1] = (IntPtr)(delegate* unmanaged<Obj*, uint>)&AddRef;
        vt[2] = (IntPtr)(delegate* unmanaged<Obj*, uint>)&Release;
        for (int k = 3; k < 14; k++) vt[k] = (IntPtr)(delegate* unmanaged<IntPtr, int>)&NotImpl;
        vt[3] = (IntPtr)(delegate* unmanaged<Cursor*, byte*, int, int*, int>)&StreamRead;
        vt[5] = (IntPtr)(delegate* unmanaged<Cursor*, long, int, long*, int>)&StreamSeek;
        vt[12] = (IntPtr)(delegate* unmanaged<Cursor*, NStatstg*, int, int>)&StreamStat;
        vt[13] = (IntPtr)(delegate* unmanaged<Cursor*, IntPtr*, int>)&CursorClone;
        return vt;
    }

    private static IntPtr* BuildEnum()
    {
        var vt = (IntPtr*)NativeMemory.AllocZeroed(7, (nuint)sizeof(IntPtr));
        vt[0] = (IntPtr)(delegate* unmanaged<Obj*, Guid*, IntPtr*, int>)&CursorQueryInterface;
        vt[1] = (IntPtr)(delegate* unmanaged<Obj*, uint>)&AddRef;
        vt[2] = (IntPtr)(delegate* unmanaged<Obj*, uint>)&Release;
        vt[3] = (IntPtr)(delegate* unmanaged<Cursor*, int, IntPtr*, int*, int>)&EnumNext;
        vt[4] = (IntPtr)(delegate* unmanaged<Cursor*, int, int>)&EnumSkip;
        vt[5] = (IntPtr)(delegate* unmanaged<Cursor*, int>)&EnumReset;
        vt[6] = (IntPtr)(delegate* unmanaged<Cursor*, IntPtr*, int>)&CursorClone;
        return vt;
    }

    private static Cursor* NewCursor(IntPtr* vt, int len, int pos)
    {
        var c = (Cursor*)NativeMemory.AllocZeroed((nuint)sizeof(Cursor));
        c->Vtbl = vt; c->Refs = 1; c->Len = len; c->Pos = pos;
        Interlocked.Increment(ref Live);
        return c;
    }

    [UnmanagedCallersOnly]
    private static int CursorQueryInterface(Obj* self, Guid* iid, IntPtr* ppv)
    {
        Guid own = self->Vtbl == s_streamsVt ? IID_IStreams : self->Vtbl == s_streamVt ? IID_IStream : IID_IEnumString;
        if (*iid == IID_IUnknown || *iid == own) { self->Refs++; *ppv = (IntPtr)self; return 0; }
        *ppv = IntPtr.Zero;
        return unchecked((int)0x80004002);
    }

    [UnmanagedCallersOnly] private static int NotImpl(IntPtr self) => unchecked((int)0x80004001);
    [UnmanagedCallersOnly] private static int Open(Obj* self, int len, IntPtr* r) { *r = (IntPtr)NewCursor(s_streamVt, len, 0); return 0; }
    [UnmanagedCallersOnly] private static int Names(Obj* self, IntPtr* r) { *r = (IntPtr)NewCursor(s_enumVt, s_names.Length, 0); return 0; }

    /// <summary>Native code reading a stream (bytes, then Stat): sum + 100 x name length + 10000 x size.</summary>
    [UnmanagedCallersOnly]
    private static int Sum(Obj* self, IntPtr s)
    {
        IntPtr* vt = *(IntPtr**)s;
        byte* buf = stackalloc byte[2];
        int sum = 0, got;
        do
        {
            int hr = ((delegate* unmanaged<IntPtr, byte*, int, int*, int>)vt[3])(s, buf, 2, &got);
            if (hr < 0) return hr;
            for (int i = 0; i < got; i++) sum += buf[i];
        } while (got > 0);
        NStatstg st;
        int h = ((delegate* unmanaged<IntPtr, NStatstg*, int, int>)vt[12])(s, &st, 0);
        if (h < 0) return h;
        string name = Marshal.PtrToStringUni(st.Name) ?? "";
        Marshal.FreeCoTaskMem(st.Name);
        return sum + 100 * name.Length + 10000 * (int)st.Size;
    }

    /// <summary>Native code enumerating strings: fetched x 100 + their total length.</summary>
    [UnmanagedCallersOnly]
    private static int Count(Obj* self, IntPtr e)
    {
        IntPtr* buf = stackalloc IntPtr[10];
        int fetched = 0;
        int hr = ((delegate* unmanaged<IntPtr, int, IntPtr*, int*, int>)(*(IntPtr**)e)[3])(e, 10, buf, &fetched);
        if (hr < 0) return hr;
        int len = 0;
        for (int i = 0; i < fetched; i++) { len += Marshal.PtrToStringUni(buf[i])!.Length; Marshal.FreeCoTaskMem(buf[i]); }
        return fetched * 100 + len;
    }

    // a stream of Len bytes, byte k being k + 1
    [UnmanagedCallersOnly]
    private static int StreamRead(Cursor* c, byte* pv, int cb, int* read)
    {
        int n = Math.Min(cb, c->Len - c->Pos);
        for (int i = 0; i < n; i++) pv[i] = (byte)(c->Pos + i + 1);
        c->Pos += n;
        if (read != null) *read = n;
        return n == cb ? 0 : 1;
    }

    [UnmanagedCallersOnly]
    private static int StreamSeek(Cursor* c, long move, int origin, long* pos)
    {
        c->Pos = (int)(origin == 0 ? move : origin == 1 ? c->Pos + move : c->Len + move);
        if (pos != null) *pos = c->Pos;
        return 0;
    }

    [UnmanagedCallersOnly]
    private static int StreamStat(Cursor* c, NStatstg* st, int flag)
    {
        *st = default;
        st->Name = Marshal.StringToCoTaskMemUni("native.bin");
        st->Type = 2;
        st->Size = c->Len;
        return 0;
    }

    [UnmanagedCallersOnly] private static int CursorClone(Cursor* c, IntPtr* r) { *r = (IntPtr)NewCursor(c->Vtbl, c->Len, c->Pos); return 0; }

    [UnmanagedCallersOnly]
    private static int EnumNext(Cursor* c, int celt, IntPtr* rgelt, int* fetched)
    {
        int n = 0;
        for (; n < celt && c->Pos < c->Len; n++) rgelt[n] = Marshal.StringToCoTaskMemUni(s_names[c->Pos++]);
        if (fetched != null) *fetched = n;
        return n == celt ? 0 : 1;
    }

    [UnmanagedCallersOnly] private static int EnumSkip(Cursor* c, int n) { c->Pos = Math.Min(c->Len, c->Pos + n); return 0; }
    [UnmanagedCallersOnly] private static int EnumReset(Cursor* c) { c->Pos = 0; return 0; }

    // ---- SAFEARRAYs: ISafeArrays, native, reading the descriptor as native code does ---------------

    private static readonly Guid IID_ISafeArrays = new("6B0B1E2A-0C55-4B0E-9F2B-6C1D3E4F5A06");
    private static readonly IntPtr* s_safeVt = BuildSafe();

    private static IntPtr* BuildSafe()
    {
        var vt = (IntPtr*)NativeMemory.AllocZeroed(9, (nuint)sizeof(IntPtr));
        vt[0] = (IntPtr)(delegate* unmanaged<Obj*, Guid*, IntPtr*, int>)&SafeQueryInterface;
        vt[1] = (IntPtr)(delegate* unmanaged<Obj*, uint>)&AddRef;
        vt[2] = (IntPtr)(delegate* unmanaged<Obj*, uint>)&Release;
        vt[3] = (IntPtr)(delegate* unmanaged<Obj*, IntPtr, int>)&SafeSum;
        vt[4] = (IntPtr)(delegate* unmanaged<Obj*, int, IntPtr*, int>)&SafeNames;
        vt[5] = (IntPtr)(delegate* unmanaged<Obj*, IntPtr*, int>)&SafeReverse;
        vt[6] = (IntPtr)(delegate* unmanaged<Obj*, WpfWebGpu.ComInterop.ComVariant*, int>)&SafeMatrix;
        vt[7] = (IntPtr)(delegate* unmanaged<Obj*, IntPtr, int>)&SafeWeighted;
        vt[8] = (IntPtr)(delegate* unmanaged<Obj*, IntPtr, IntPtr*, int>)&SafeUseSink;
        return vt;
    }

    [UnmanagedCallersOnly]
    private static int SafeQueryInterface(Obj* self, Guid* iid, IntPtr* ppv)
    {
        if (*iid == IID_IUnknown || *iid == IID_ISafeArrays) { self->Refs++; *ppv = (IntPtr)self; return 0; }
        *ppv = IntPtr.Zero;
        return unchecked((int)0x80004002);
    }

    // the SDK's SAFEARRAY: cDims, fFeatures, cbElements, cLocks, pvData, bounds (rightmost dimension first)
    private static int PvData => IntPtr.Size == 8 ? 16 : 12;
    private static int Bounds => PvData + IntPtr.Size;

    /// <summary>A one-dimensional VT_I4 SAFEARRAY read raw: its sum, or a negative code for what is wrong.</summary>
    [UnmanagedCallersOnly]
    private static int SafeSum(Obj* self, IntPtr psa)
    {
        byte* p = (byte*)psa;
        if (*(ushort*)p != 1) return -1;
        if (*(uint*)(p + 4) != 4) return -2;
        ushort features = *(ushort*)(p + 2);
        if ((features & 0x80) == 0 || *(int*)(p - 4) != 3) return -3;           // FADF_HAVEVARTYPE, VT_I4
        uint n = *(uint*)(p + Bounds);
        if (*(int*)(p + Bounds + 4) != 0) return -4;
        int* data = *(int**)(p + PvData);
        int s = 0;
        for (int i = 0; i < n; i++) s += data[i];
        return s + 10000 * (int)n;
    }

    [UnmanagedCallersOnly]
    private static int SafeNames(Obj* self, int n, IntPtr* r)
    {
        var a = new string[n];
        for (int i = 0; i < n; i++) a[i] = "n" + i;
        *r = WpfWebGpu.ComInterop.Com.ToSafeArray(a, 0);
        return 0;
    }

    /// <summary>[in, out] SAFEARRAY**: the callee frees the caller's and hands back its own.</summary>
    [UnmanagedCallersOnly]
    private static int SafeReverse(Obj* self, IntPtr* psa)
    {
        var a = (object[])WpfWebGpu.ComInterop.Com.FromSafeArray(*psa, typeof(object[]).TypeHandle)!;
        WpfWebGpu.ComInterop.Com.DestroySafeArray(*psa);
        Array.Reverse(a);
        *psa = WpfWebGpu.ComInterop.Com.ToSafeArray(a, 0);
        return 0;
    }

    [UnmanagedCallersOnly]
    private static int SafeMatrix(Obj* self, WpfWebGpu.ComInterop.ComVariant* r)
    {
        WpfWebGpu.ComInterop.Com.ToVariant(new double[,] { { 1, 2, 3 }, { 4, 5, 6 } }, (IntPtr)r);
        return 0;
    }

    /// <summary>A 2-D VT_I4 array read raw: each element times its position in memory (column-major).</summary>
    [UnmanagedCallersOnly]
    private static int SafeWeighted(Obj* self, IntPtr psa)
    {
        byte* p = (byte*)psa;
        if (*(ushort*)p != 2) return -1;
        uint right = *(uint*)(p + Bounds), left = *(uint*)(p + Bounds + 8);
        int* data = *(int**)(p + PvData);
        int s = 0;
        for (int k = 0; k < right * left; k++) s += data[k] * (k + 1);
        return s * 100 + (int)left * 10 + (int)right;
    }

    /// <summary>Native code calling a managed IArraySink: Sum (in), Make (retval), Double (by reference).</summary>
    [UnmanagedCallersOnly]
    private static int SafeUseSink(Obj* self, IntPtr sink, IntPtr* r)
    {
        IntPtr* vt = *(IntPtr**)sink;
        var parts = new List<string>();
        IntPtr shorts = WpfWebGpu.ComInterop.Com.ToSafeArray(new short[] { 5, 6, 7 }, 0);
        parts.Add(((delegate* unmanaged<IntPtr, IntPtr, int>)vt[3])(sink, shorts).ToString());
        WpfWebGpu.ComInterop.Com.DestroySafeArray(shorts);
        IntPtr made;
        int hr = ((delegate* unmanaged<IntPtr, int, IntPtr*, int>)vt[4])(sink, 3, &made);
        parts.Add(hr != 0 ? $"hr {hr:x8}" : string.Join("/", (string[])WpfWebGpu.ComInterop.Com.TakeSafeArray(made, typeof(string[]).TypeHandle)!));
        IntPtr ints = WpfWebGpu.ComInterop.Com.ToSafeArray(new[] { 1, 2 }, 0);
        hr = ((delegate* unmanaged<IntPtr, IntPtr*, int>)vt[5])(sink, &ints);
        parts.Add(hr != 0 ? $"hr {hr:x8}" : string.Join("/", (int[])WpfWebGpu.ComInterop.Com.TakeSafeArray(ints, typeof(int[]).TypeHandle)!));
        *r = Marshal.StringToBSTR(string.Join(",", parts));
        return 0;
    }

    private static T WithSafe<T>(Func<IntPtr, T> body)
    {
        var o = New(s_safeVt, 0);
        try { return body((IntPtr)o); }
        finally { GC.Collect(); GC.WaitForPendingFinalizers(); ((delegate* unmanaged<Obj*, uint>)s_safeVt[2])(o); }
    }

    // ---- IDispatch: IDispUser, and a native IDispatch-only counter -------------------------------

    private static readonly Guid IID_IDispUser = new("6B0B1E2A-0C55-4B0E-9F2B-6C1D3E4F5A0B");
    private static readonly Guid IID_IDispatch = new("00020400-0000-0000-C000-000000000046");
    private static readonly IntPtr* s_dispUserVt = BuildDispUser();
    private static readonly IntPtr* s_counterVt = BuildCounter();
    private static string? s_counterName;

    private static IntPtr* BuildDispUser()
    {
        var vt = (IntPtr*)NativeMemory.AllocZeroed(6, (nuint)sizeof(IntPtr));
        vt[0] = (IntPtr)(delegate* unmanaged<Obj*, Guid*, IntPtr*, int>)&DispQueryInterface;
        vt[1] = (IntPtr)(delegate* unmanaged<Obj*, uint>)&AddRef;
        vt[2] = (IntPtr)(delegate* unmanaged<Obj*, uint>)&Release;
        vt[3] = (IntPtr)(delegate* unmanaged<Obj*, IntPtr*, int>)&DispCounter;
        vt[4] = (IntPtr)(delegate* unmanaged<Obj*, IntPtr, IntPtr*, int>)&DispUseSink;
        vt[5] = (IntPtr)(delegate* unmanaged<Obj*, IntPtr, int>)&DispUseDual;
        return vt;
    }

    private static IntPtr* BuildCounter()
    {
        var vt = (IntPtr*)NativeMemory.AllocZeroed(7, (nuint)sizeof(IntPtr));
        vt[0] = (IntPtr)(delegate* unmanaged<Obj*, Guid*, IntPtr*, int>)&DispQueryInterface;
        vt[1] = (IntPtr)(delegate* unmanaged<Obj*, uint>)&AddRef;
        vt[2] = (IntPtr)(delegate* unmanaged<Obj*, uint>)&Release;
        vt[3] = (IntPtr)(delegate* unmanaged<Obj*, uint*, int>)&CounterTypeInfoCount;
        vt[4] = (IntPtr)(delegate* unmanaged<IntPtr, int>)&NotImpl;
        vt[5] = (IntPtr)(delegate* unmanaged<Obj*, Guid*, char**, uint, int, int*, int>)&CounterIdsOfNames;
        vt[6] = (IntPtr)(delegate* unmanaged<Obj*, int, Guid*, int, ushort, WpfWebGpu.ComInterop.ComDispParams*, WpfWebGpu.ComInterop.ComVariant*, WpfWebGpu.ComInterop.ComExcepInfo*, uint*, int>)&CounterInvoke;
        return vt;
    }

    [UnmanagedCallersOnly]
    private static int DispQueryInterface(Obj* self, Guid* iid, IntPtr* ppv)
    {
        // the counter answers IDispatch only, not its dispinterface's IID
        Guid own = self->Vtbl == s_dispUserVt ? IID_IDispUser : IID_IDispatch;
        if (*iid == IID_IUnknown || *iid == own) { self->Refs++; *ppv = (IntPtr)self; return 0; }
        *ppv = IntPtr.Zero;
        return unchecked((int)0x80004002);
    }

    [UnmanagedCallersOnly] private static int DispCounter(Obj* self, IntPtr* r) { *r = (IntPtr)New(s_counterVt, 0); return 0; }
    [UnmanagedCallersOnly] private static int CounterTypeInfoCount(Obj* self, uint* n) { *n = 0; return 0; }

    [UnmanagedCallersOnly]
    private static int CounterIdsOfNames(Obj* self, Guid* riid, char** names, uint count, int lcid, int* ids)
    {
        if (new string(names[0]) == "ByName") { ids[0] = 4; return 0; }
        ids[0] = -1;
        return unchecked((int)0x80020006);
    }

    [UnmanagedCallersOnly]
    private static int CounterInvoke(Obj* self, int id, Guid* riid, int lcid, ushort flags, WpfWebGpu.ComInterop.ComDispParams* dp, WpfWebGpu.ComInterop.ComVariant* res, WpfWebGpu.ComInterop.ComExcepInfo* ex, uint* argErr)
    {
        var a = dp->Args;
        switch (id)
        {
            case 1:          // Add(a, b): rgvarg is last first
                WpfWebGpu.ComInterop.Com.ToVariant((int)WpfWebGpu.ComInterop.Com.FromVariant((IntPtr)(&a[1]))! + (int)WpfWebGpu.ComInterop.Com.FromVariant((IntPtr)(&a[0]))!, (IntPtr)res);
                return 0;
            case 2:
                if ((flags & 4) != 0)
                {
                    if (dp->NamedCount != 1 || dp->NamedArgs[0] != -3) return unchecked((int)0x80020007);       // DISP_E_NONAMEDARGS
                    s_counterName = (string?)WpfWebGpu.ComInterop.Com.FromVariant((IntPtr)(&a[0]));
                }
                else WpfWebGpu.ComInterop.Com.ToVariant(s_counterName, (IntPtr)res);
                return 0;
            case 3:          // Bump(ref n): VT_BYREF | VT_VARIANT
            {
                if (a[0].Vt != (0x4000 | 12)) return unchecked((int)0x80020005);
                var inner = (WpfWebGpu.ComInterop.ComVariant*)a[0].Data1;
                int v = (int)WpfWebGpu.ComInterop.Com.FromVariant((IntPtr)inner)!;
                WpfWebGpu.ComInterop.Com.ClearVariant((IntPtr)inner);
                WpfWebGpu.ComInterop.Com.ToVariant(v + 1, (IntPtr)inner);
                return 0;
            }
            case 4:
                WpfWebGpu.ComInterop.Com.ToVariant(((string)WpfWebGpu.ComInterop.Com.FromVariant((IntPtr)(&a[0]))!).Length * 10, (IntPtr)res);
                return 0;
            case 5:
                ex->Description = Marshal.StringToBSTR("bad thing");
                ex->Scode = unchecked((int)0x80004005);
                return unchecked((int)0x80020009);
        }
        return unchecked((int)0x80020003);
    }

    /// <summary>IDispatch::Invoke from native code; the arguments given last first, as rgvarg holds them.</summary>
    private static int CallInvoke(IntPtr d, int id, ushort flags, WpfWebGpu.ComInterop.ComVariant* args, uint n, WpfWebGpu.ComInterop.ComVariant* res, out string? error)
    {
        var dp = new WpfWebGpu.ComInterop.ComDispParams { Args = args, ArgCount = n };
        WpfWebGpu.ComInterop.ComExcepInfo ex = default;
        uint argErr;
        Guid none = Guid.Empty;
        int hr = ((delegate* unmanaged<IntPtr, int, Guid*, int, ushort, WpfWebGpu.ComInterop.ComDispParams*, WpfWebGpu.ComInterop.ComVariant*, WpfWebGpu.ComInterop.ComExcepInfo*, uint*, int>)(*(IntPtr**)d)[6])(d, id, &none, 0, flags, &dp, res, &ex, &argErr);
        error = hr == unchecked((int)0x80020009) ? $"{Marshal.PtrToStringBSTR(ex.Description)}:{ex.Scode:x8}" : null;
        if (ex.Description != 0) Marshal.FreeBSTR(ex.Description);
        if (ex.Source != 0) Marshal.FreeBSTR(ex.Source);
        return hr;
    }

    private static int IdOf(IntPtr d, string name)
    {
        int id;
        Guid none = Guid.Empty;
        fixed (char* s = name)
        {
            char* names = s;
            int hr = ((delegate* unmanaged<IntPtr, Guid*, char**, uint, int, int*, int>)(*(IntPtr**)d)[5])(d, &none, &names, 1, 0, &id);
            return hr == 0 ? id : hr;
        }
    }

    /// <summary>Native code calling a managed dispinterface through its CCW's IDispatch.</summary>
    [UnmanagedCallersOnly]
    private static int DispUseSink(Obj* self, IntPtr sink, IntPtr* r)
    {
        var parts = new List<string> { IdOf(sink, "echo").ToString() };
        var args = stackalloc WpfWebGpu.ComInterop.ComVariant[2];
        WpfWebGpu.ComInterop.ComVariant res = default;
        WpfWebGpu.ComInterop.Com.ToVariant(3, (IntPtr)(&args[0]));
        WpfWebGpu.ComInterop.Com.ToVariant("ab", (IntPtr)(&args[1]));
        int hr = CallInvoke(sink, 7, 1, args, 2, &res, out _);
        parts.Add(hr == 0 ? (string)WpfWebGpu.ComInterop.Com.FromVariant((IntPtr)(&res))! : $"hr {hr:x8}");
        WpfWebGpu.ComInterop.Com.ClearVariant((IntPtr)(&res));
        WpfWebGpu.ComInterop.Com.ClearVariant((IntPtr)(&args[1]));
        hr = CallInvoke(sink, 8, 2, null, 0, &res, out _);
        parts.Add(hr == 0 ? WpfWebGpu.ComInterop.Com.FromVariant((IntPtr)(&res))!.ToString()! : $"hr {hr:x8}");
        // Twice(ref n), n a VT_BYREF | VT_I4
        int n = 20;
        args[0] = default;
        args[0].Vt = 0x4000 | 3;
        args[0].Data1 = (IntPtr)(&n);
        hr = CallInvoke(sink, 9, 1, args, 1, null, out _);
        parts.Add(hr == 0 ? n.ToString() : $"hr {hr:x8}");
        hr = CallInvoke(sink, 10, 1, null, 0, null, out string? error);
        parts.Add(error ?? $"hr {hr:x8}");
        *r = Marshal.StringToBSTR(string.Join("|", parts));
        return 0;
    }

    /// <summary>A managed dual object's IDispatch (it was passed as IDispatch): GetIDsOfNames, Invoke.</summary>
    [UnmanagedCallersOnly]
    private static int DispUseDual(Obj* self, IntPtr d)
    {
        int id = IdOf(d, "Square");
        var arg = stackalloc WpfWebGpu.ComInterop.ComVariant[1];
        WpfWebGpu.ComInterop.Com.ToVariant(7, (IntPtr)arg);
        WpfWebGpu.ComInterop.ComVariant res = default;
        int hr = CallInvoke(d, id, 1, arg, 1, &res, out _);
        if (hr != 0) return hr;
        return (id == 11 ? 1000 : 0) + (int)WpfWebGpu.ComInterop.Com.FromVariant((IntPtr)(&res))!;
    }

    private static T WithDisp<T>(Func<IntPtr, T> body)
    {
        var o = New(s_dispUserVt, 0);
        try { return body((IntPtr)o); }
        finally { GC.Collect(); GC.WaitForPendingFinalizers(); ((delegate* unmanaged<Obj*, uint>)s_dispUserVt[2])(o); }
    }

    // ---- formatted types: IFormatted, native -------------------------------------------------------

    private static readonly Guid IID_IFormatted = new("6B0B1E2A-0C55-4B0E-9F2B-6C1D3E4F5A0D");
    private static readonly IntPtr* s_fmtVt = BuildFormatted();

    [StructLayout(LayoutKind.Sequential)] private struct NTag { public IntPtr Text; public int N; }
    [StructLayout(LayoutKind.Sequential)] private struct NPerson { public IntPtr Name; public int Age; }

    private static IntPtr* BuildFormatted()
    {
        var vt = (IntPtr*)NativeMemory.AllocZeroed(12, (nuint)sizeof(IntPtr));
        vt[0] = (IntPtr)(delegate* unmanaged<Obj*, Guid*, IntPtr*, int>)&FmtQueryInterface;
        vt[1] = (IntPtr)(delegate* unmanaged<Obj*, uint>)&AddRef;
        vt[2] = (IntPtr)(delegate* unmanaged<Obj*, uint>)&Release;
        vt[3] = (IntPtr)(delegate* unmanaged<Obj*, Guid*, int>)&FmtGuidSum;
        vt[4] = (IntPtr)(delegate* unmanaged<Obj*, int*, int>)&FmtFill;
        vt[5] = (IntPtr)(delegate* unmanaged<Obj*, NPerson*, int>)&FmtNamed;
        vt[6] = (IntPtr)(delegate* unmanaged<Obj*, NTag*, int>)&FmtRename;
        vt[7] = (IntPtr)(delegate* unmanaged<Obj*, char*, int, int>)&FmtText;
        vt[8] = (IntPtr)(delegate* unmanaged<Obj*, int*, int>)&FmtCustom;
        vt[9] = (IntPtr)(delegate* unmanaged<Obj*, NTag*, int, int>)&FmtTags;
        vt[10] = (IntPtr)(delegate* unmanaged<Obj*, char*, int, int>)&FmtChars;
        vt[11] = (IntPtr)(delegate* unmanaged<Obj*, IntPtr, IntPtr*, int>)&FmtUseSink;
        return vt;
    }

    [UnmanagedCallersOnly]
    private static int FmtQueryInterface(Obj* self, Guid* iid, IntPtr* ppv)
    {
        if (*iid == IID_IUnknown || *iid == IID_IFormatted) { self->Refs++; *ppv = (IntPtr)self; return 0; }
        *ppv = IntPtr.Zero;
        return unchecked((int)0x80004002);
    }

    [UnmanagedCallersOnly] private static int FmtGuidSum(Obj* self, Guid* g) { int s = 0; foreach (byte b in g->ToByteArray()) s += b; return s; }
    [UnmanagedCallersOnly] private static int FmtFill(Obj* self, int* box) { box[0] = box[1] * 2; return box[1]; }
    [UnmanagedCallersOnly] private static int FmtNamed(Obj* self, NPerson* p) { int r = Marshal.PtrToStringUni(p->Name)!.Length * 100 + p->Age; p->Age = 99; return r; }

    /// <summary>[in, out] by reference: the callee frees the caller's string and hands back its own.</summary>
    [UnmanagedCallersOnly]
    private static int FmtRename(Obj* self, NTag* t)
    {
        string old = Marshal.PtrToStringUni(t->Text)!;
        Marshal.FreeCoTaskMem(t->Text);
        t->Text = Marshal.StringToCoTaskMemUni(old.ToUpperInvariant() + "!");
        t->N++;
        return 0;
    }

    [UnmanagedCallersOnly]
    private static int FmtText(Obj* self, char* buf, int cch)
    {
        string s = "native text";
        int n = Math.Min(s.Length, cch - 1);
        for (int i = 0; i < n; i++) buf[i] = s[i];
        buf[n] = '\0';
        return 0;
    }

    [UnmanagedCallersOnly] private static int FmtCustom(Obj* self, int* p) { int r = p[0] + p[1]; p[0] *= 10; p[1] *= 10; return r; }

    [UnmanagedCallersOnly]
    private static int FmtTags(Obj* self, NTag* tags, int n)
    {
        int r = 0;
        for (int i = 0; i < n; i++) { r += tags[i].N * 10 + Marshal.PtrToStringUni(tags[i].Text)!.Length; tags[i].N = 7 + i; }
        return r;
    }

    [UnmanagedCallersOnly] private static int FmtChars(Obj* self, char* c, int n) { int r = 0; for (int i = 0; i < n; i++) r += c[i]; return r; }

    /// <summary>Native code calling a managed IFormattedSink: a blittable class (written back), a struct by
    /// reference with a string, an LPStruct Guid, a formatted class with a string [In, Out].</summary>
    [UnmanagedCallersOnly]
    private static int FmtUseSink(Obj* self, IntPtr sink, IntPtr* r)
    {
        IntPtr* vt = *(IntPtr**)sink;
        var parts = new List<string>();
        int* box = stackalloc int[2] { 6, 7 };
        int area = ((delegate* unmanaged<IntPtr, int*, int>)vt[3])(sink, box);
        parts.Add($"{area}:{box[0]}");
        var tag = new NTag { Text = Marshal.StringToCoTaskMemUni("n"), N = 1 };
        int hr = ((delegate* unmanaged<IntPtr, NTag*, int>)vt[4])(sink, &tag);
        parts.Add(hr != 0 ? $"hr {hr:x8}" : $"{Marshal.PtrToStringUni(tag.Text)}:{tag.N}");
        Marshal.FreeCoTaskMem(tag.Text);
        Guid g = new("0000002a-0000-0000-0000-000000000000");
        parts.Add(((delegate* unmanaged<IntPtr, Guid*, int>)vt[5])(sink, &g).ToString());
        var person = new NPerson { Name = Marshal.StringToCoTaskMemUni("Bo"), Age = 70 };
        int len = ((delegate* unmanaged<IntPtr, NPerson*, int>)vt[6])(sink, &person);
        parts.Add($"{len}:{Marshal.PtrToStringUni(person.Name)}:{person.Age}");
        Marshal.FreeCoTaskMem(person.Name);
        *r = Marshal.StringToBSTR(string.Join("|", parts));
        return 0;
    }

    private static T WithFormatted<T>(Func<IntPtr, T> body)
    {
        var o = New(s_fmtVt, 0);
        try { return body((IntPtr)o); }
        finally { GC.Collect(); GC.WaitForPendingFinalizers(); ((delegate* unmanaged<Obj*, uint>)s_fmtVt[2])(o); }
    }

    // ---- C arrays: ICArrays, native ---------------------------------------------------------------

    private static readonly Guid IID_ICArrays = new("6B0B1E2A-0C55-4B0E-9F2B-6C1D3E4F5A0F");
    private static readonly IntPtr* s_carrVt = BuildCArrays();

    private static IntPtr* BuildCArrays()
    {
        var vt = (IntPtr*)NativeMemory.AllocZeroed(8, (nuint)sizeof(IntPtr));
        vt[0] = (IntPtr)(delegate* unmanaged<Obj*, Guid*, IntPtr*, int>)&CArrQueryInterface;
        vt[1] = (IntPtr)(delegate* unmanaged<Obj*, uint>)&AddRef;
        vt[2] = (IntPtr)(delegate* unmanaged<Obj*, uint>)&Release;
        vt[3] = (IntPtr)(delegate* unmanaged<Obj*, IntPtr*, int, int>)&CArrJoin;
        vt[4] = (IntPtr)(delegate* unmanaged<Obj*, IntPtr*, int, int>)&CArrFill;
        vt[5] = (IntPtr)(delegate* unmanaged<Obj*, WpfWebGpu.ComInterop.ComVariant*, int, int>)&CArrValues;
        vt[6] = (IntPtr)(delegate* unmanaged<Obj*, IntPtr*, int, int>)&CArrOthers;
        vt[7] = (IntPtr)(delegate* unmanaged<Obj*, IntPtr, IntPtr*, int>)&CArrUseSink;
        return vt;
    }

    [UnmanagedCallersOnly]
    private static int CArrQueryInterface(Obj* self, Guid* iid, IntPtr* ppv)
    {
        if (*iid == IID_IUnknown || *iid == IID_ICArrays) { self->Refs++; *ppv = (IntPtr)self; return 0; }
        *ppv = IntPtr.Zero;
        return unchecked((int)0x80004002);
    }

    [UnmanagedCallersOnly] private static int CArrJoin(Obj* self, IntPtr* s, int n) { int r = 100 * n; for (int i = 0; i < n; i++) r += Marshal.PtrToStringUni(s[i])!.Length; return r; }
    [UnmanagedCallersOnly] private static int CArrFill(Obj* self, IntPtr* s, int n) { for (int i = 0; i < n; i++) s[i] = Marshal.StringToBSTR("b" + i); return 0; }
    [UnmanagedCallersOnly] private static int CArrValues(Obj* self, WpfWebGpu.ComInterop.ComVariant* v, int n) { int r = 100 * n; for (int i = 0; i < n; i++) r += v[i].Vt; return r; }
    [UnmanagedCallersOnly] private static int CArrOthers(Obj* self, IntPtr* o, int n) { int r = 0; for (int i = 0; i < n; i++) r += ((delegate* unmanaged<IntPtr, int>)(*(IntPtr**)o[i])[3])(o[i]); return r; }

    /// <summary>Native code giving a managed object arrays sized by another parameter, and a StringBuilder buffer.</summary>
    [UnmanagedCallersOnly]
    private static int CArrUseSink(Obj* self, IntPtr sink, IntPtr* r)
    {
        IntPtr* vt = *(IntPtr**)sink;
        var parts = new List<string>();
        int* ints = stackalloc int[3] { 1, 2, 3 };
        parts.Add(((delegate* unmanaged<IntPtr, int*, int, int>)vt[3])(sink, ints, 3).ToString());
        int* sq = stackalloc int[4];
        int hr = ((delegate* unmanaged<IntPtr, int*, int, int>)vt[4])(sink, sq, 4);
        parts.Add(hr != 0 ? $"hr {hr:x8}" : $"{sq[0]}/{sq[1]}/{sq[2]}/{sq[3]}");
        IntPtr* names = stackalloc IntPtr[2] { IntPtr.Zero, IntPtr.Zero };
        hr = ((delegate* unmanaged<IntPtr, IntPtr*, int, int>)vt[5])(sink, names, 2);
        parts.Add(hr != 0 ? $"hr {hr:x8}" : $"{Marshal.PtrToStringUni(names[0])}/{Marshal.PtrToStringUni(names[1])}");
        Marshal.FreeCoTaskMem(names[0]); Marshal.FreeCoTaskMem(names[1]);
        char* buf = stackalloc char[8];
        for (int i = 0; i < 8; i++) buf[i] = 'X';                   // garbage the managed side must not read
        hr = ((delegate* unmanaged<IntPtr, char*, int, int>)vt[6])(sink, buf, 8);
        parts.Add(hr != 0 ? $"hr {hr:x8}" : new string(buf));
        var tags = stackalloc NTag[2];
        tags[0] = new NTag { Text = Marshal.StringToCoTaskMemUni("a"), N = 1 };
        tags[1] = new NTag { Text = Marshal.StringToCoTaskMemUni("bb"), N = 2 };
        int sum = ((delegate* unmanaged<IntPtr, NTag*, int, int>)vt[7])(sink, tags, 2);
        parts.Add($"{sum}:{tags[0].N}/{tags[1].N}:{Marshal.PtrToStringUni(tags[1].Text)}");
        Marshal.FreeCoTaskMem(tags[0].Text); Marshal.FreeCoTaskMem(tags[1].Text);
        *r = Marshal.StringToBSTR(string.Join("|", parts));
        return 0;
    }

    private static T WithCArrays<T>(Func<IntPtr, T> body)
    {
        var o = New(s_carrVt, 0);
        try { return body((IntPtr)o); }
        finally { GC.Collect(); GC.WaitForPendingFinalizers(); ((delegate* unmanaged<Obj*, uint>)s_carrVt[2])(o); }
    }

    // ---- shapes: IShapes, native -------------------------------------------------------------------

    private static readonly Guid IID_IShapes = new("6B0B1E2A-0C55-4B0E-9F2B-6C1D3E4F5A11");
    private static readonly IntPtr* s_shapesVt = BuildShapes();

    [StructLayout(LayoutKind.Sequential)] private struct NThumb { public fixed byte Key[16]; }
    [StructLayout(LayoutKind.Sequential)] private struct NNamed { public IntPtr S; public int N; }
    [StructLayout(LayoutKind.Sequential)] private struct NHolder { public IntPtr O; public int N; }

    private static IntPtr* BuildShapes()
    {
        var vt = (IntPtr*)NativeMemory.AllocZeroed(14, (nuint)sizeof(IntPtr));
        vt[0] = (IntPtr)(delegate* unmanaged<Obj*, Guid*, IntPtr*, int>)&ShapesQueryInterface;
        vt[11] = (IntPtr)(delegate* unmanaged<Obj*, int*, Guid**, int>)&ShapesModes;
        vt[12] = (IntPtr)(delegate* unmanaged<Obj*, int*>)&ShapesPeek;
        vt[13] = (IntPtr)(delegate* unmanaged<Obj*, int*, int>)&ShapesFetch;
        vt[1] = (IntPtr)(delegate* unmanaged<Obj*, uint>)&AddRef;
        vt[2] = (IntPtr)(delegate* unmanaged<Obj*, uint>)&Release;
        vt[3] = (IntPtr)(delegate* unmanaged<Obj*, NThumb, int>)&ShapesThumb;
        vt[4] = (IntPtr)(delegate* unmanaged<Obj*, NNamed, int>)&ShapesNamed;
        vt[5] = (IntPtr)(delegate* unmanaged<Obj*, int, NNamed*, int>)&ShapesMake;
        vt[6] = (IntPtr)(delegate* unmanaged<Obj*, int**, int>)&ShapesGrow;
        vt[7] = (IntPtr)(delegate* unmanaged<Obj*, char**, int>)&ShapesIcon;
        vt[8] = (IntPtr)(delegate* unmanaged<Obj*, NHolder*, int>)&ShapesHold;
        vt[9] = (IntPtr)(delegate* unmanaged<IntPtr, int>)&NotImpl;
        vt[10] = (IntPtr)(delegate* unmanaged<Obj*, IntPtr, IntPtr*, int>)&ShapesUseSink;
        return vt;
    }

    [UnmanagedCallersOnly]
    private static int ShapesQueryInterface(Obj* self, Guid* iid, IntPtr* ppv)
    {
        if (*iid == IID_IUnknown || *iid == IID_IShapes) { self->Refs++; *ppv = (IntPtr)self; return 0; }
        *ppv = IntPtr.Zero;
        return unchecked((int)0x80004002);
    }

    [UnmanagedCallersOnly]
    private static int ShapesModes(Obj* self, int* n, Guid** modes)
    {
        *n = 2;
        *modes = (Guid*)Marshal.AllocCoTaskMem(2 * sizeof(Guid));
        (*modes)[0] = new Guid("00000001-0000-0000-0000-000000000000");
        (*modes)[1] = new Guid("00000007-0000-0000-0000-000000000000");
        return 0;
    }

    private static readonly int* s_peek = Peeked();
    private static int* Peeked() { var p = (int*)NativeMemory.Alloc(8); p[0] = 7; p[1] = 8; return p; }
    [UnmanagedCallersOnly] private static int* ShapesPeek(Obj* self) => s_peek;
    [UnmanagedCallersOnly] private static int ShapesFetch(Obj* self, int* box) { box[0] = 5; box[1] = 6; return 0; }

    [UnmanagedCallersOnly] private static int ShapesThumb(Obj* self, NThumb t) { int s = 0; for (int i = 0; i < 16; i++) s += t.Key[i]; return s; }
    [UnmanagedCallersOnly] private static int ShapesNamed(Obj* self, NNamed n) => Marshal.PtrToStringUni(n.S)!.Length * 100 + n.N;
    [UnmanagedCallersOnly] private static int ShapesMake(Obj* self, int n, NNamed* r) { r->S = Marshal.StringToCoTaskMemUni("made" + n); r->N = n; return 0; }
    [UnmanagedCallersOnly] private static int ShapesGrow(Obj* self, int** box) { (*box)[0] *= 10; (*box)[1] = 99; return 0; }

    /// <summary>char**: the callee writes into the buffer it points at.</summary>
    [UnmanagedCallersOnly]
    private static int ShapesIcon(Obj* self, char** s)
    {
        string v = "ico:" + new string(*s);
        for (int i = 0; i < v.Length; i++) (*s)[i] = v[i];
        (*s)[v.Length] = '\0';
        return 0;
    }

    /// <summary>A struct with an interface: its Value, then the interface replaced by a native one (the old released).</summary>
    [UnmanagedCallersOnly]
    private static int ShapesHold(Obj* self, NHolder* h)
    {
        int v = ((delegate* unmanaged<IntPtr, int>)(*(IntPtr**)h->O)[3])(h->O);
        ((delegate* unmanaged<IntPtr, uint>)(*(IntPtr**)h->O)[2])(h->O);
        h->O = (IntPtr)New(s_otherVt, 5);
        h->N = 9;
        return v;
    }

    [UnmanagedCallersOnly]
    private static int ShapesUseSink(Obj* self, IntPtr sink, IntPtr* r)
    {
        IntPtr* vt = *(IntPtr**)sink;
        NThumb t;
        for (int i = 0; i < 16; i++) t.Key[i] = 2;
        int a = ((delegate* unmanaged<IntPtr, NThumb, int>)vt[3])(sink, t);
        var n = new NNamed { S = Marshal.StringToCoTaskMemUni("abc"), N = 7 };
        int b = ((delegate* unmanaged<IntPtr, NNamed, int>)vt[4])(sink, n);
        Marshal.FreeCoTaskMem(n.S);
        *r = Marshal.StringToBSTR($"{a}|{b}");
        return 0;
    }

    private static T WithShapes<T>(Func<IntPtr, T> body)
    {
        var o = New(s_shapesVt, 0);
        try { return body((IntPtr)o); }
        finally { GC.Collect(); GC.WaitForPendingFinalizers(); ((delegate* unmanaged<Obj*, uint>)s_shapesVt[2])(o); }
    }

    // ---- IData: a native IDataObject, IEnumFORMATETC, IEnumVARIANT, an IDispatch enumerable ----------

    private static readonly Dictionary<IntPtr, Guid> s_iids = new();
    private static readonly IntPtr* s_dataVt = BuildData();
    private static readonly IntPtr* s_dataObjVt = BuildDataObj();
    private static readonly IntPtr* s_fmtEnumVt = BuildFmtEnum();
    private static readonly IntPtr* s_varEnumVt = BuildVarEnum();
    private static readonly IntPtr* s_newEnumVt = BuildNewEnum();

    [StructLayout(LayoutKind.Sequential)] private struct NFormat { public short Cf; public IntPtr Ptd; public int Aspect, Lindex, Tymed; }
    [StructLayout(LayoutKind.Sequential)] private struct NStg { public int Tymed; public IntPtr Union; public IntPtr Unk; }

    private static IntPtr* Vt(int n, Guid iid)
    {
        var vt = (IntPtr*)NativeMemory.AllocZeroed((nuint)n, (nuint)sizeof(IntPtr));
        vt[0] = (IntPtr)(delegate* unmanaged<Obj*, Guid*, IntPtr*, int>)&KeyedQueryInterface;
        vt[1] = (IntPtr)(delegate* unmanaged<Obj*, uint>)&AddRef;
        vt[2] = (IntPtr)(delegate* unmanaged<Obj*, uint>)&Release;
        for (int k = 3; k < n; k++) vt[k] = (IntPtr)(delegate* unmanaged<IntPtr, int>)&NotImpl;
        lock (s_iids) s_iids[(IntPtr)vt] = iid;
        return vt;
    }

    [UnmanagedCallersOnly]
    private static int KeyedQueryInterface(Obj* self, Guid* iid, IntPtr* ppv)
    {
        Guid own; lock (s_iids) own = s_iids[(IntPtr)self->Vtbl];
        if (*iid == IID_IUnknown || *iid == own) { self->Refs++; *ppv = (IntPtr)self; return 0; }
        *ppv = IntPtr.Zero;
        return unchecked((int)0x80004002);
    }

    private static IntPtr* BuildData()
    {
        var vt = Vt(7, new Guid("6B0B1E2A-0C55-4B0E-9F2B-6C1D3E4F5A13"));
        vt[3] = (IntPtr)(delegate* unmanaged<Obj*, IntPtr*, int>)&DataMake;
        vt[4] = (IntPtr)(delegate* unmanaged<Obj*, IntPtr, int>)&DataUse;
        vt[5] = (IntPtr)(delegate* unmanaged<Obj*, IntPtr*, int>)&DataItems;
        vt[6] = (IntPtr)(delegate* unmanaged<Obj*, IntPtr*, int>)&DataItems2;
        return vt;
    }

    private static IntPtr* BuildDataObj()
    {
        var vt = Vt(12, new Guid("0000010e-0000-0000-C000-000000000046"));
        vt[3] = (IntPtr)(delegate* unmanaged<Obj*, NFormat*, NStg*, int>)&DoGetData;
        vt[5] = (IntPtr)(delegate* unmanaged<Obj*, NFormat*, int>)&DoQueryGetData;
        vt[8] = (IntPtr)(delegate* unmanaged<Obj*, int, IntPtr*, int>)&DoEnumFormatEtc;
        return vt;
    }

    private static IntPtr* BuildFmtEnum()
    {
        var vt = Vt(7, new Guid("00000103-0000-0000-C000-000000000046"));
        vt[3] = (IntPtr)(delegate* unmanaged<Cursor*, int, NFormat*, int*, int>)&FmtNext;
        return vt;
    }

    private static IntPtr* BuildVarEnum()
    {
        var vt = Vt(7, new Guid("00020404-0000-0000-C000-000000000046"));
        vt[3] = (IntPtr)(delegate* unmanaged<Cursor*, uint, WpfWebGpu.ComInterop.ComVariant*, uint*, int>)&VarNext;
        return vt;
    }

    private static IntPtr* BuildNewEnum()
    {
        var vt = Vt(7, IID_IDispatch);
        vt[6] = (IntPtr)(delegate* unmanaged<Obj*, int, Guid*, int, ushort, WpfWebGpu.ComInterop.ComDispParams*, WpfWebGpu.ComInterop.ComVariant*, WpfWebGpu.ComInterop.ComExcepInfo*, uint*, int>)&NewEnumInvoke;
        return vt;
    }

    [UnmanagedCallersOnly] private static int DataMake(Obj* self, IntPtr* r) { *r = (IntPtr)New(s_dataObjVt, 0); return 0; }
    [UnmanagedCallersOnly] private static int DataItems(Obj* self, IntPtr* r) { *r = (IntPtr)NewCursor(s_varEnumVt, 3, 0); return 0; }
    [UnmanagedCallersOnly] private static int DataItems2(Obj* self, IntPtr* r) { *r = (IntPtr)New(s_newEnumVt, 0); return 0; }

    [UnmanagedCallersOnly]
    private static int DoGetData(Obj* self, NFormat* f, NStg* m)
    {
        if (f->Cf != 13) return unchecked((int)0x80040064);
        *m = new NStg { Tymed = 1, Union = Marshal.StringToCoTaskMemUni("native data") };
        return 0;
    }

    [UnmanagedCallersOnly] private static int DoQueryGetData(Obj* self, NFormat* f) => f->Cf == 13 ? 0 : unchecked((int)0x80040064);
    [UnmanagedCallersOnly] private static int DoEnumFormatEtc(Obj* self, int dir, IntPtr* r) { *r = (IntPtr)NewCursor(s_fmtEnumVt, 1, 0); return 0; }

    [UnmanagedCallersOnly]
    private static int FmtNext(Cursor* c, int celt, NFormat* f, int* fetched)
    {
        int n = 0;
        for (; n < celt && c->Pos < c->Len; n++, c->Pos++) f[n] = new NFormat { Cf = 13, Aspect = 1, Lindex = -1, Tymed = 1 };
        if (fetched != null) *fetched = n;
        return n == celt ? 0 : 1;
    }

    [UnmanagedCallersOnly]
    private static int VarNext(Cursor* c, uint celt, WpfWebGpu.ComInterop.ComVariant* v, uint* fetched)
    {
        uint n = 0;
        for (; n < celt && c->Pos < c->Len; n++) WpfWebGpu.ComInterop.Com.ToVariant(++c->Pos + c->Value0(), (IntPtr)(&v[n]));
        if (fetched != null) *fetched = n;
        return n == celt ? 0 : 1;
    }

    /// <summary>IDispatch only: DISPID_NEWENUM gives an IEnumVARIANT of 4, 5.</summary>
    [UnmanagedCallersOnly]
    private static int NewEnumInvoke(Obj* self, int id, Guid* riid, int lcid, ushort flags, WpfWebGpu.ComInterop.ComDispParams* dp, WpfWebGpu.ComInterop.ComVariant* res, WpfWebGpu.ComInterop.ComExcepInfo* ex, uint* argErr)
    {
        if (id != -4) return unchecked((int)0x80020003);
        var e = NewCursor(s_varEnumVt, 5, 3);
        *res = default;
        res->Vt = 13;
        res->Data1 = (IntPtr)e;
        return 0;
    }

    /// <summary>Native code calling a managed IDataObject (the generated CCW): QueryGetData, GetData, EnumFormatEtc -> Next.</summary>
    [UnmanagedCallersOnly]
    private static int DataUse(Obj* self, IntPtr d)
    {
        IntPtr* vt = *(IntPtr**)d;
        var f = new NFormat { Cf = 13, Aspect = 1, Lindex = -1, Tymed = 1 };
        int q = ((delegate* unmanaged<IntPtr, NFormat*, int>)vt[5])(d, &f);
        NStg m;
        int hr = ((delegate* unmanaged<IntPtr, NFormat*, NStg*, int>)vt[3])(d, &f, &m);
        if (hr != 0) return hr;
        int len = Marshal.PtrToStringUni(m.Union)!.Length;
        Marshal.FreeCoTaskMem(m.Union);
        IntPtr e;
        hr = ((delegate* unmanaged<IntPtr, int, IntPtr*, int>)vt[8])(d, 1, &e);
        if (hr != 0) return hr;
        var got = stackalloc NFormat[1];
        int fetched = 0;
        int nhr = ((delegate* unmanaged<IntPtr, int, NFormat*, int*, int>)(*(IntPtr**)e)[3])(e, 1, got, &fetched);
        if (nhr < 0) return nhr;
        ((delegate* unmanaged<IntPtr, uint>)(*(IntPtr**)e)[2])(e);
        return len + 100 * fetched + (q == 0 ? 1000 : 0) + (got[0].Cf == 13 ? 10000 : 0);
    }

    private static T WithData<T>(Func<IntPtr, T> body)
    {
        var o = New(s_dataVt, 0);
        try { return body((IntPtr)o); }
        finally { GC.Collect(); GC.WaitForPendingFinalizers(); ((delegate* unmanaged<Obj*, uint>)s_dataVt[2])(o); }
    }

    private static T WithStreams<T>(Func<IntPtr, T> body)
    {
        var o = New(s_streamsVt, 0);
        try { return body((IntPtr)o); }
        finally { GC.Collect(); GC.WaitForPendingFinalizers(); ((delegate* unmanaged<Obj*, uint>)s_streamsVt[2])(o); }
    }

    // ---- the rewritten fixture ------------------------------------------------------------------

    /// <summary>ComUserFixture: no COM types of its own, ComImportFixture's used through an RCW it makes itself.</summary>
    internal static string UserFixtureFile => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(FixtureFile)!, "..", "..", "..", "..", "ComUserFixture", "bin",
        Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(FixtureFile))!)!, "net10.0", "ComUserFixture.dll"));
    /// <summary>ComReleaserFixture: no COM types or RCW makers, but Marshal.ReleaseComObject on ComImportFixture's objects.</summary>
    internal static string ReleaserFixtureFile => Path.Combine(Path.GetDirectoryName(UserFixtureFile)!.Replace("ComUserFixture", "ComReleaserFixture"), "ComReleaserFixture.dll");

    internal static string FixtureFile => typeof(ComRewriterTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().First(a => a.Key == "ComImportFixture").Value!;

    /// <summary>The fixture's original, copied to a folder of its own (with a native library beside it on
    /// Windows), as the launcher's loader finds an app's.</summary>
    internal static readonly Lazy<string> s_original = new(() =>
    {
        string dir = TestEnvironment.NewDirectory("comimport-app");
        string file = Path.Combine(dir, "ComImportFixture.dll");
        File.Copy(FixtureFile, file);
        File.Copy(Path.Combine(Path.GetDirectoryName(FixtureFile)!, "LocationAskerFixture.dll"), Path.Combine(dir, "LocationAskerFixture.dll"));
        File.Copy(UserFixtureFile, Path.Combine(dir, "ComUserFixture.dll"));
        File.Copy(ReleaserFixtureFile, Path.Combine(dir, "ComReleaserFixture.dll"));
        if (File.Exists(Path.ChangeExtension(FixtureFile, ".pdb"))) File.Copy(Path.ChangeExtension(FixtureFile, ".pdb"), Path.ChangeExtension(file, ".pdb"));
        if (OperatingSystem.IsWindows()) File.Copy(Path.Combine(Environment.SystemDirectory, "version.dll"), Path.Combine(dir, "fixturenative.dll"));
        return file;
    });

    private static readonly Lazy<(Type Scenario, RewriteResult Result)> s_fixture = new(() =>
    {
        // the verdict, as the launcher judges it
        var result = ComRewriter.Rewrite(FixtureFile, null, new[] { AppContext.BaseDirectory }, partial: true);
        // the load, as the startup hook does it: rewritten in memory on the first request
        // the fixture, and the helper that asks its callers where they are (caller rewriting)
        const ComImportLoader.Parts com = ComImportLoader.Parts.Com;
        var loader = new ComImportLoader(new Dictionary<string, (string, ComImportLoader.Parts)>
        {
            ["ComImportFixture"] = (s_original.Value, com),
            ["LocationAskerFixture"] = (Path.Combine(Path.GetDirectoryName(s_original.Value)!, "LocationAskerFixture.dll"), com),
            ["ComUserFixture"] = (Path.Combine(Path.GetDirectoryName(s_original.Value)!, "ComUserFixture.dll"), com),
            ["ComReleaserFixture"] = (Path.Combine(Path.GetDirectoryName(s_original.Value)!, "ComReleaserFixture.dll"), com),
        }, new[] { AppContext.BaseDirectory }, cacheDir: null, log: null);
        var alc = new AssemblyLoadContext("comimport-fixture");
        loader.Install(alc);
        var asm = alc.LoadFromAssemblyName(new AssemblyName("ComImportFixture"));
        s_user = alc.LoadFromAssemblyName(new AssemblyName("ComUserFixture")).GetType("ComUserFixture.User", throwOnError: true);
        s_releaser = alc.LoadFromAssemblyName(new AssemblyName("ComReleaserFixture")).GetType("ComReleaserFixture.Releaser", throwOnError: true);
        return (asm.GetType("ComImportFixture.Scenario", throwOnError: true)!, result);
    });

    private static Type? s_user, s_releaser;

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
        Assert.Equal(25 + 6, r.Interfaces);     // 25 of the fixture's, 6 generated BCL ones (IDataObject, IEnumFORMATETC, IAdviseSink, IEnumSTATDATA, IMoniker, IEnumMoniker)
        Assert.True(r.BuiltInCannot.Any(x => x.Contains("IShapes.Paint") && x.Contains("Color")), r.ToString() + " | " + string.Join("; ", r.Unsupported) + " | " + string.Join("; ", r.BuiltInCannot));
        Assert.Equal(1, r.EventInterfaces);
        Assert.Equal(6, r.PInvokes);
        string u = Assert.Single(r.Unsupported);
        Assert.Contains("Arrays", u);
        Assert.Equal(2, r.Delegates);
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
    public void SafeArrays_InRetvalByReferenceAndInAVariant()
    {
        // laid out as the SDK's (FADF_HAVEVARTYPE, VT_I4, zero-based), read raw by native code
        Assert.Equal(46 + 40000, WithSafe(p => (int)Run("SafeSum", p)!));
        Assert.Equal(0, WithSafe(p => (int)Run("SafeSumEmpty", p)!));
        Assert.Equal("n0,n1,n2", WithSafe(p => (string)Run("SafeNames", p)!));
        Assert.Equal("3.5,two,1|Double", WithSafe(p => (string)Run("SafeReverse", p)!));
        Assert.Equal("2x3:1,3,4,6", WithSafe(p => (string)Run("SafeMatrix", p)!));
    }

    [Fact]
    public void SafeArrays_TheManagedLayout_AsOffWindows()
    {
        // what every other OS uses (no oleaut32): the same descriptor, made and destroyed here
        AppContext.SetSwitch("WpfWebGpu.ComInterop.ManagedSafeArrays", true);
        try
        {
            Assert.Equal(46 + 40000, WithSafe(p => (int)Run("SafeSum", p)!));
            Assert.Equal("n0,n1,n2", WithSafe(p => (string)Run("SafeNames", p)!));
            Assert.Equal("3.5,two,1|Double", WithSafe(p => (string)Run("SafeReverse", p)!));
            Assert.Equal("2x3:1,3,4,6", WithSafe(p => (string)Run("SafeMatrix", p)!));
            Assert.Equal(86 * 100 + 2 * 10 + 3, WithSafe(p => (int)Run("SafeWeighted", p)!));
            Assert.Equal("18,m/mm/mmm,1/2/1/2", WithSafe(p => (string)Run("SafeSink", p)!));
        }
        finally { AppContext.SetSwitch("WpfWebGpu.ComInterop.ManagedSafeArrays", false); }
    }

    [Fact]
    public void SafeArrays_TwoDimensional_AreColumnMajor()
    {
        // memory order 1,4,2,5,3,6: 1*1 + 4*2 + 2*3 + 5*4 + 3*5 + 6*6 = 86; dims 2 (left) x 3 (right)
        Assert.Equal(86 * 100 + 2 * 10 + 3, WithSafe(p => (int)Run("SafeWeighted", p)!));
    }

    [Fact]
    public void SafeArrays_ToAManagedObject_ThroughItsCcw()
        => Assert.Equal("18,m/mm/mmm,1/2/1/2", WithSafe(p => (string)Run("SafeSink", p)!));

    [Fact]
    public void FormattedTypes_CustomMarshalers_AndStringBuilders()
        // LPStruct Guid (1+..+16 = 136); a blittable class pinned (A written back); a class with a string [In]
        // (not written back); a struct by reference with its string replaced by the callee; StringBuilder [Out];
        // a custom marshaler [In, Out]; a C array of structs [In, Out]; a char array as UTF-16
        => Assert.Equal("136|21:42|336:36|ABC!:2|native |7:30,40|33:7,8|131", WithFormatted(p => (string)Run("Formatted", p)!));

    [Fact]
    public void FormattedTypes_ToAManagedObject_ThroughItsCcw()
        => Assert.Equal("42:5|n?:11|42|6:Bo Sr.:71", WithFormatted(p => (string)Run("FormattedSink", p)!));

    [Fact]
    public void CArrays_OfStringsVariantsAndInterfaces()
        // LPWStr in (5 + 200), BSTR [Out] (the callee's), VARIANTs (VT_I4 3 + VT_R8 5 + VT_BSTR 8 + 300),
        // interfaces (two managed objects' CCWs, 77 each)
        => Assert.Equal("205|b0,b1|316|154", WithCArrays(p => (string)Run("CArrays", p)!));

    [Fact]
    public void CArrays_ToAManagedObject_SizedByAnotherParameter()
        // in, [Out], [Out] LPWStr, an [Out] StringBuilder cut to the buffer's size (the next parameter), [In, Out] structs
        => Assert.Equal("6|0/1/4/9|s0/s1|managed|3:2/4:bb", WithCArrays(p => (string)Run("CArraysSink", p)!));

    [Fact]
    public void Shapes_StructsByValue_Retvals_RefClasses_InterfaceFields()
        // 1+..+16; a string struct by value (4*100+4); a struct retval; Box** (the same object, written in place);
        // char** written in place; a struct's interface field used and replaced; Color refused as built-in interop does
        => Assert.Equal("136|404|made3:3|20,99,True|ico:old|77:9:5|refused", WithShapes(p => (string)Run("ShapesAll", p)!));

    [Fact]
    public void Shapes_CalleeArrays_AndClassReturns()
        // out T[] sized by an out parameter (the callee's CoTaskMem); a PreserveSig T*; an [out, retval] T*
        => Assert.Equal("2:2:7|7,8|5,6", WithShapes(p => (string)Run("ShapesReturns", p)!));

    [Fact]
    public void Shapes_ByValueStructs_ToAManagedObject()
        => Assert.Equal("32|307", WithShapes(p => (string)Run("ShapesSink", p)!));

    [Fact]
    public void GeneratedBclInterfaces_IDataObject_BothWays()
    {
        // a native IDataObject: QueryGetData, GetData (STGMEDIUM out), EnumFormatEtc -> a native IEnumFORMATETC
        Assert.Equal("0|native data|1:13", WithData(p => (string)Run("DataNative", p)!));
        // a managed IDataObject called by native code through the generated CCWs (and its IEnumFORMATETC's)
        Assert.Equal(12 + 100 + 1000 + 10000, WithData(p => (int)Run("DataManaged", p)!));
    }

    [Fact]
    public void IEnumerableReturns_AreIEnumVARIANT()
        // straight from IEnumVARIANT; through IDispatch's DISPID_NEWENUM
        => Assert.Equal("1,2,3|4,5", WithData(p => (string)Run("DataItems", p)!));

    [Fact]
    public void AnAssemblyReleasingAGroupsObjects_IsRewrittenWithThem()
    {
        // ComReleaserFixture makes no RCWs but calls Marshal.IsComObject/ReleaseComObject on ComImportFixture's
        // objects: built in, IsComObject says False and ReleaseComObject throws (not a __ComObject)
        _ = s_fixture.Value;
        Assert.Equal("5|True|0", WithCalc(0, p => (string)s_releaser!.GetMethod("AddAndRelease")!.Invoke(null, new[] { Run("Calc", p)! })!));
    }

    [Fact]
    public void AGroupMember_WithoutComTypes_IsRewrittenWithThem()
    {
        // ComUserFixture makes its own RCW (Marshal.GetObjectForIUnknown) and casts it to ComImportFixture's
        // ICalc: rewritten with the group, that is a ComObject; left alone, a built-in RCW (Windows) that no
        // longer casts to the rewritten interface
        _ = s_fixture.Value;
        Assert.Equal(5, WithCalc(0, p => (int)s_user!.GetMethod("Add")!.Invoke(null, new object[] { p, 2, 3 })!));
    }

    [Fact]
    public void PropVariantClearAndCopy_AsOle32()
    {
        // the shim's ole32/oleaut32 exports off Windows: what a PROPVARIANT owns is copied deeply and freed
        var a = stackalloc WpfWebGpu.ComInterop.ComVariant[1];
        var b = stackalloc WpfWebGpu.ComInterop.ComVariant[1];
        IntPtr pa = (IntPtr)a, pb = (IntPtr)b;
        string Str(WpfWebGpu.ComInterop.ComVariant* v) => Marshal.PtrToStringUni(v->Data1)!;

        // VT_LPWSTR: copied, not shared
        *a = default; a->Vt = 31; a->Data1 = Marshal.StringToCoTaskMemUni("lpwstr");
        Assert.Equal(0, WpfWebGpu.ComInterop.Com.PropVariantCopy(pb, pa));
        Assert.NotEqual(a->Data1, b->Data1);
        Assert.Equal("lpwstr", Str(b));
        Assert.Equal(0, WpfWebGpu.ComInterop.Com.PropVariantClear(pa));
        Assert.Equal(0, a->Vt);
        Assert.Equal(0, WpfWebGpu.ComInterop.Com.PropVariantClear(pb));

        // VT_BSTR
        *a = default; a->Vt = 8; a->Data1 = Marshal.StringToBSTR("bstr");
        WpfWebGpu.ComInterop.Com.PropVariantCopy(pb, pa);
        Assert.Equal("bstr", Marshal.PtrToStringBSTR(b->Data1));
        WpfWebGpu.ComInterop.Com.PropVariantClear(pa); WpfWebGpu.ComInterop.Com.PropVariantClear(pb);

        // VT_VECTOR | VT_LPWSTR: { cElems, pElems }
        var elems = (IntPtr*)Marshal.AllocCoTaskMem(2 * IntPtr.Size);
        elems[0] = Marshal.StringToCoTaskMemUni("x"); elems[1] = Marshal.StringToCoTaskMemUni("yy");
        *a = default; a->Vt = 0x1000 | 31; *(uint*)&a->Data1 = 2; a->Data2 = (IntPtr)elems;
        WpfWebGpu.ComInterop.Com.PropVariantCopy(pb, pa);
        var copied = (IntPtr*)b->Data2;
        Assert.Equal(2u, *(uint*)&b->Data1);
        Assert.Equal("yy", Marshal.PtrToStringUni(copied[1]));
        Assert.NotEqual(elems[1], copied[1]);
        Assert.Equal(0, WpfWebGpu.ComInterop.Com.PropVariantClear(pa));
        Assert.Equal(0, WpfWebGpu.ComInterop.Com.PropVariantClear(pb));

        // VT_UNKNOWN: a copy AddRefs, a clear Releases
        var o = New(s_otherVt, 3);
        *a = default; a->Vt = 13; a->Data1 = (IntPtr)o;
        WpfWebGpu.ComInterop.Com.PropVariantCopy(pb, pa);
        Assert.Equal(2, o->Refs);
        WpfWebGpu.ComInterop.Com.PropVariantClear(pb);
        Assert.Equal(1, o->Refs);
        WpfWebGpu.ComInterop.Com.PropVariantClear(pa);           // the last reference: the object is gone

        // VT_BLOB: { cbSize, pBlobData }
        var blob = (byte*)Marshal.AllocCoTaskMem(3); blob[0] = 1; blob[1] = 2; blob[2] = 3;
        *a = default; a->Vt = 65; *(uint*)&a->Data1 = 3; a->Data2 = (IntPtr)blob;
        WpfWebGpu.ComInterop.Com.PropVariantCopy(pb, pa);
        Assert.Equal(3, ((byte*)b->Data2)[2]);
        Assert.NotEqual(a->Data2, b->Data2);
        WpfWebGpu.ComInterop.Com.PropVariantClear(pa); WpfWebGpu.ComInterop.Com.PropVariantClear(pb);

        // VT_ARRAY | VT_I4
        *a = default; a->Vt = 0x2000 | 3; a->Data1 = WpfWebGpu.ComInterop.Com.ToSafeArray(new[] { 4, 5 }, 0);
        WpfWebGpu.ComInterop.Com.PropVariantCopy(pb, pa);
        Assert.Equal(new[] { 4, 5 }, (int[])WpfWebGpu.ComInterop.Com.FromSafeArray(b->Data1, typeof(int[]).TypeHandle)!);
        Assert.NotEqual(a->Data1, b->Data1);
        WpfWebGpu.ComInterop.Com.PropVariantClear(pa); WpfWebGpu.ComInterop.Com.PropVariantClear(pb);

        // VariantClear refuses what only a PROPVARIANT holds; an unknown type is left as it is
        *a = default; a->Vt = 31;
        Assert.Equal(unchecked((int)0x80020008), WpfWebGpu.ComInterop.Com.VariantClear(pa));
        *a = default; a->Vt = 0x0FFF;
        Assert.Equal(unchecked((int)0x80020008), WpfWebGpu.ComInterop.Com.PropVariantClear(pa));
        Assert.Equal(0x0FFF, a->Vt);
    }

    [Fact]
    public void ComEvents_ProviderConnectionPointAndSource()
        // the client side (event interface -> tlbimp's provider -> IConnectionPointContainer/IConnectionPoint ->
        // a sink helper's IDispatch) and the server side ([ComSourceInterfaces]: its .NET events call the
        // Advise'd sink), every step through native vtables; Unadvise stops the calls
        => Assert.Equal("t1,t2,t3,ran 3,ran 1", (string)Run("Events")!);

    [Fact]
    public void ADispinterface_IsCalledThroughInvoke()
        // DISPIDs, a property put (DISPID_PROPERTYPUT named), VT_BYREF, GetIDsOfNames, DISP_E_EXCEPTION's
        // description; the object answers IDispatch only, not the dispinterface's IID
        => Assert.Equal("5|abc|42|40|bad thing:80004005", WithDisp(p => (string)Run("DispCounter", p)!));

    [Fact]
    public void AManagedDispinterface_AnswersIDispatch()
        // GetIDsOfNames (case-insensitive), a method, a property get, VT_BYREF written back, an exception as
        // DISP_E_EXCEPTION with its message and HRESULT
        => Assert.Equal("7|ababab|99|40|sink failed:80131509", WithDisp(p => (string)Run("DispSink", p)!));

    [Fact]
    public void AManagedDualObject_PassedAsIDispatch_AnswersIt()
        => Assert.Equal(1049, WithDisp(p => (int)Run("DispDual", p)!));

    [Fact]
    public void TheBclsIStream_BothWays()
    {
        // a native stream: Read in pieces, Stat (its CoTaskMem name taken over), Seek, Clone
        Assert.Equal("1,2,3,4,5|native.bin|5|1|2", WithStreams(p => (string)Run("ReadNative", p)!));
        // a managed stream read by native code through its CCW (bytes copied, STATSTG's name allocated)
        Assert.Equal(10 + 1100 + 40000, WithStreams(p => (int)Run("SumManaged", p)!));
        // a native stream passed back: its own pointer
        Assert.Equal(10 + 1000 + 40000, WithStreams(p => (int)Run("SumNative", p)!));
    }

    [Fact]
    public void TheBclsIEnumString_BothWays()
    {
        Assert.Equal("0:2:one,two|1:1:three|two", WithStreams(p => (string)Run("NamesNative", p)!));
        Assert.Equal(306, WithStreams(p => (int)Run("CountManaged", p)!));
    }

    [Fact]
    public void TheBclsIBindCtx_FromOle32()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "ole32's CreateBindCtx");
        Assert.Equal("2|True|notimpl|0|True", (string)Run("BclBindCtx")!);
    }

    [Fact]
    public void ADelegateWithMarshalAsInterface_IsRetypedCleanly()
        // the interface parameter becomes a pointer: its old [MarshalAs(Interface)] must not survive (Cecil wrote the
        // original FieldMarshal row again), or GetDelegateForFunctionPointer refuses the delegate (Telerik's WMI)
        => Assert.Equal(1000 + 21, WithCalc(21, p => (int)Run("ThroughMarshalAsDelegate", p)!));

    [Fact]
    public void ACoClass_MadeByNewOrByActivator_IsACOMObject()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "a registered COM class");
        Assert.Equal("True|True|StringBuilder", (string)Run("CoClasses")!);
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
    public void APInvoke_WithCArraysOfInterfaces_ConvertsThemBothWays()
    {
        // MF.Net's MFTEnumEx/MFEnumDeviceSources (an out array the callee allocates, its length in another
        // parameter) and MFCreateStreamDescriptor/MFCreatePresentationDescriptor (arrays passed in): built-in
        // interop cannot marshal elements whose interfaces are no longer ComImport (RadWebCam's device names)
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Media Foundation");
        Assert.Equal("True|7|3|1", (string)Run("MfArrays")!);
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
    public void AnotherAssemblyAskingWhereItIs_IsToldTheOriginal_WhenItIsRewrittenToo()
        => Assert.Equal(new Uri(s_original.Value).AbsoluteUri, (string)Run("WhereByCaller")!);

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
          "targets": { "T": { "App/1.0.0": { "runtime": { "ComImportFixture.dll": {}, "LocationAskerFixture.dll": {}, "ComUserFixture.dll": {}, "ComReleaserFixture.dll": {}, "Other.dll": {} } } } },
          "libraries": { "App/1.0.0": { "type": "package", "path": "app" } }
        }
        """)!.AsObject();

    private static (System.Text.Json.Nodes.JsonObject Deps, List<string> Notes) Apply(bool windows, string? setting = null, string? entry = null)
    {
        string app = TestEnvironment.NewDirectory("comstep-app");
        File.Copy(Fixture, Path.Combine(app, "ComImportFixture.dll"));
        File.Copy(Path.Combine(Path.GetDirectoryName(Fixture)!, "LocationAskerFixture.dll"), Path.Combine(app, "LocationAskerFixture.dll"));
        File.Copy(ComRewriterTests.UserFixtureFile, Path.Combine(app, "ComUserFixture.dll"));
        File.Copy(ComRewriterTests.ReleaserFixtureFile, Path.Combine(app, "ComReleaserFixture.dll"));
        string cache = TestEnvironment.NewDirectory("comstep-cache");
        var deps = Deps();
        var notes = new List<string>();
        var list = new List<string>();
        string appCache = TestEnvironment.NewDirectory("comstep-appcache");
        WpfWebGpu.ComImportStep.Apply(deps, app, AppContext.BaseDirectory, cache, appCache, windows, setting, notes, list, entry);
        Assert.Empty(Directory.EnumerateFileSystemEntries(cache));       // nothing is written: the rewrite happens in memory, at load
        // ...but the verdicts, which the next launch reads instead of judging again
        Assert.All(Directory.EnumerateFileSystemEntries(appCache), f => Assert.Equal(WpfWebGpu.ComImportStep.VerdictFile, Path.GetFileName(f)));
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
    public void AnAssemblyMakingRcwsOfAGroupsTypes_IsJudgedWithTheGroup()
    {
        // off Windows: rewritten with ComImportFixture; on Windows: left with it (ComImportFixture is left there)
        var (_, notes) = Apply(windows: false);
        Assert.Contains(notes, n => n.Contains("ComUserFixture: rewritten in memory at load") && n.Contains("uses the COM types of ComImportFixture"));
        (_, notes) = Apply(windows: true);
        Assert.Contains(notes, n => n.Contains("ComUserFixture: left on built-in COM") && n.Contains("(left with them)"));
    }

    [Fact]
    public void OffWindows_TheSupportedPart_IsRewritten_AndLeavesTheDepsForTheLoader()
    {
        var (deps, notes) = Apply(windows: false);
        Assert.Contains(notes, n => n.Contains("ComImportFixture: rewritten in memory"));
        Assert.Null(deps["targets"]!["T"]!["App/1.0.0"]!["runtime"]!["ComImportFixture.dll"]);
        Assert.NotNull(deps["targets"]!["T"]!["App/1.0.0"]!["runtime"]!["Other.dll"]);
        Assert.Equal(4, s_lastList!.Count);       // the fixture, its group members ComUserFixture and ComReleaserFixture, the location asker
        Assert.Contains(s_lastList, e => e.StartsWith("ComReleaserFixture|") && !e.EndsWith("|location"));
        Assert.Contains(s_lastList, e => e.StartsWith("ComImportFixture|") && e.EndsWith("ComImportFixture.dll|com"));
        Assert.Contains(s_lastList, e => e.StartsWith("ComUserFixture|") && !e.EndsWith("|location"));
        // the helper it calls asks its callers where they are: rewritten for that (caller rewriting)
        Assert.Contains(s_lastList, e => e.StartsWith("LocationAskerFixture|"));
        Assert.Contains(notes, n => n.Contains("LocationAskerFixture: its location calls rewritten") && n.Contains("GetCallingAssembly"));
        Assert.Null(deps["targets"]!["T"]!["App/1.0.0"]!["runtime"]!["LocationAskerFixture.dll"]);
    }

    [Fact]
    public void TheEntryAssembly_IsNeverRewritten()
    {
        // the host loads it (dotnet exec) before the startup hook installs the loader: listing it would claim a
        // rewrite that never happens
        var (deps, notes) = Apply(windows: false, entry: "ComReleaserFixture");
        Assert.Contains(notes, n => n.Contains("ComReleaserFixture: left on built-in COM") && n.Contains("the entry assembly"));
        Assert.DoesNotContain(s_lastList!, e => e.StartsWith("ComReleaserFixture|"));
        Assert.NotNull(deps["targets"]!["T"]!["App/1.0.0"]!["runtime"]!["ComReleaserFixture.dll"]);
        Assert.Contains(s_lastList!, e => e.StartsWith("ComImportFixture|"));
    }

    [Fact]
    public void AnEarlierWpfrunsCopies_AreRemoved()
    {
        string cache = TestEnvironment.NewDirectory("comstep-old");
        Directory.CreateDirectory(Path.Combine(cache, WpfWebGpu.ComImportStep.Folder, "abc"));
        File.WriteAllText(Path.Combine(cache, WpfWebGpu.ComImportStep.Folder, "abc", "X.dll"), "");
        WpfWebGpu.ComImportStep.Apply(Deps(), TestEnvironment.NewDirectory("comstep-empty"), AppContext.BaseDirectory, cache, null, true, null, new(), new());
        Assert.False(Directory.Exists(Path.Combine(cache, WpfWebGpu.ComImportStep.Folder)));
    }

    [Fact]
    public void TheSwitch_TurnsItOff_OrNamesTheAssemblies()
    {
        Assert.Contains(Apply(windows: false, "off").Notes, n => n.Contains("left (WPF_COMIMPORT_REWRITE)"));
        Assert.Contains(Apply(windows: false, "Some*;ComImport*").Notes, n => n.Contains("rewritten in memory"));
        Assert.Contains(Apply(windows: false, "Other").Notes, n => n.Contains("left (WPF_COMIMPORT_REWRITE)"));
    }
}
