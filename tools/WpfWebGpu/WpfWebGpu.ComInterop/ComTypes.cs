// The BCL's own COM interfaces (System.Runtime.InteropServices.ComTypes) for rewritten assemblies
// (Documentation/comimport-rewriter.md). The rewriter cannot nest a ComImpl or ComCcw in a BCL type, so the
// ones it supports are written here by hand, both ways:
//
// - a [DynamicInterfaceCastableImplementation] interface per type, whose methods call through the native
//   object's vtable, so a ComObject casts to the BCL interface (ComObject.ImplOf asks BclComTypes.ImplOf);
// - a vtable of [UnmanagedCallersOnly] stubs per type, so a managed object implementing it can be handed to
//   native code (Com.Ccw.Build asks BclComTypes.VtableOf).
//
// The marshalling is built-in COM interop's for these declarations: byte[] pinned in, copied in a CCW;
// STATSTG's name a CoTaskMem LPWStr the receiver frees; IEnumString.Next's strings CoTaskMem LPWStrs the
// caller frees; object parameters marshalled as IUnknown.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using FILETIME = System.Runtime.InteropServices.ComTypes.FILETIME;

namespace WpfWebGpu.ComInterop;

internal static unsafe class BclComTypes
{
    /// <summary>The supported BCL interfaces' full names (the rewriter's list must match).</summary>
    public static readonly string[] Supported =
    {
        "System.Runtime.InteropServices.ComTypes.IStream",
        "System.Runtime.InteropServices.ComTypes.IBindCtx",
        "System.Runtime.InteropServices.ComTypes.IEnumString",
        "System.Runtime.InteropServices.ComTypes.IConnectionPointContainer",
        "System.Runtime.InteropServices.ComTypes.IConnectionPoint",
        "System.Runtime.InteropServices.ComTypes.IEnumConnectionPoints",
        "System.Runtime.InteropServices.ComTypes.IEnumConnections",
    };

    public static Type? ImplOf(Type iface) =>
        iface == typeof(IStream) ? typeof(IStreamImpl) :
        iface == typeof(IBindCtx) ? typeof(IBindCtxImpl) :
        iface == typeof(IEnumString) ? typeof(IEnumStringImpl) :
        iface == typeof(IConnectionPointContainer) ? typeof(IConnectionPointContainerImpl) :
        iface == typeof(IConnectionPoint) ? typeof(IConnectionPointImpl) :
        iface == typeof(IEnumConnectionPoints) ? typeof(IEnumConnectionPointsImpl) :
        iface == typeof(IEnumConnections) ? typeof(IEnumConnectionsImpl) : null;

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, IntPtr> s_vtables = new();

    public static IntPtr VtableOf(Type iface) => s_vtables.GetOrAdd(iface, static i =>
        i == typeof(IStream) ? StreamCcw.Vtable() :
        i == typeof(IBindCtx) ? BindCtxCcw.Vtable() :
        i == typeof(IEnumString) ? EnumStringCcw.Vtable() :
        i == typeof(IConnectionPointContainer) ? ConnectionPointContainerCcw.Vtable() :
        i == typeof(IConnectionPoint) ? ConnectionPointCcw.Vtable() :
        i == typeof(IEnumConnectionPoints) ? EnumConnectionPointsCcw.Vtable() :
        i == typeof(IEnumConnections) ? EnumConnectionsCcw.Vtable() : IntPtr.Zero);

    internal static IntPtr Slot(IntPtr self, int k) => (*(IntPtr**)self)[k];

    /// <summary>STATSTG as native code lays it out (its name an LPOLESTR).</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeStatstg
    {
        public IntPtr pwcsName;
        public int type;
        public long cbSize;
        public FILETIME mtime, ctime, atime;
        public int grfMode, grfLocksSupported;
        public Guid clsid;
        public int grfStateBits, reserved;
    }

    internal static STATSTG FromNative(ref NativeStatstg n)
    {
        var s = new STATSTG
        {
            pwcsName = n.pwcsName == IntPtr.Zero ? null! : Marshal.PtrToStringUni(n.pwcsName)!,
            type = n.type, cbSize = n.cbSize, mtime = n.mtime, ctime = n.ctime, atime = n.atime,
            grfMode = n.grfMode, grfLocksSupported = n.grfLocksSupported, clsid = n.clsid,
            grfStateBits = n.grfStateBits, reserved = n.reserved,
        };
        if (n.pwcsName != IntPtr.Zero) Marshal.FreeCoTaskMem(n.pwcsName);
        return s;
    }

    internal static void ToNative(in STATSTG s, NativeStatstg* n)
    {
        n->pwcsName = s.pwcsName == null ? IntPtr.Zero : Marshal.StringToCoTaskMemUni(s.pwcsName);
        n->type = s.type; n->cbSize = s.cbSize; n->mtime = s.mtime; n->ctime = s.ctime; n->atime = s.atime;
        n->grfMode = s.grfMode; n->grfLocksSupported = s.grfLocksSupported; n->clsid = s.clsid;
        n->grfStateBits = s.grfStateBits; n->reserved = s.reserved;
    }

    internal static IntPtr Borrow<T>(object? o) => Com.Borrow(o, typeof(T).TypeHandle);
}

// ---- calls into native objects ------------------------------------------------------------------

[DynamicInterfaceCastableImplementation]
internal unsafe interface IStreamImpl : IStream
{
    private static IntPtr Self(object o) => Com.This(o, typeof(IStream).TypeHandle);

    void IStream.Read(byte[] pv, int cb, IntPtr pcbRead)
    {
        IntPtr p = Self(this);
        fixed (byte* b = pv) Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, byte*, int, IntPtr, int>)BclComTypes.Slot(p, 3))(p, b, cb, pcbRead));
    }

    void IStream.Write(byte[] pv, int cb, IntPtr pcbWritten)
    {
        IntPtr p = Self(this);
        fixed (byte* b = pv) Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, byte*, int, IntPtr, int>)BclComTypes.Slot(p, 4))(p, b, cb, pcbWritten));
    }

    void IStream.Seek(long dlibMove, int dwOrigin, IntPtr plibNewPosition)
    {
        IntPtr p = Self(this);
        Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, long, int, IntPtr, int>)BclComTypes.Slot(p, 5))(p, dlibMove, dwOrigin, plibNewPosition));
    }

    void IStream.SetSize(long libNewSize)
    {
        IntPtr p = Self(this);
        Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, long, int>)BclComTypes.Slot(p, 6))(p, libNewSize));
    }

    void IStream.CopyTo(IStream pstm, long cb, IntPtr pcbRead, IntPtr pcbWritten)
    {
        IntPtr p = Self(this);
        IntPtr other = BclComTypes.Borrow<IStream>(pstm);
        try { Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, long, IntPtr, IntPtr, int>)BclComTypes.Slot(p, 7))(p, other, cb, pcbRead, pcbWritten)); }
        finally { Com.Release(other); }
    }

    void IStream.Commit(int grfCommitFlags)
    {
        IntPtr p = Self(this);
        Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, int, int>)BclComTypes.Slot(p, 8))(p, grfCommitFlags));
    }

    void IStream.Revert()
    {
        IntPtr p = Self(this);
        Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, int>)BclComTypes.Slot(p, 9))(p));
    }

    void IStream.LockRegion(long libOffset, long cb, int dwLockType)
    {
        IntPtr p = Self(this);
        Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, long, long, int, int>)BclComTypes.Slot(p, 10))(p, libOffset, cb, dwLockType));
    }

    void IStream.UnlockRegion(long libOffset, long cb, int dwLockType)
    {
        IntPtr p = Self(this);
        Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, long, long, int, int>)BclComTypes.Slot(p, 11))(p, libOffset, cb, dwLockType));
    }

    void IStream.Stat(out STATSTG pstatstg, int grfStatFlag)
    {
        IntPtr p = Self(this);
        BclComTypes.NativeStatstg n = default;
        Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, BclComTypes.NativeStatstg*, int, int>)BclComTypes.Slot(p, 12))(p, &n, grfStatFlag));
        pstatstg = BclComTypes.FromNative(ref n);
    }

    void IStream.Clone(out IStream ppstm)
    {
        IntPtr p = Self(this);
        IntPtr r = IntPtr.Zero;
        Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)BclComTypes.Slot(p, 13))(p, &r));
        ppstm = (IStream)Com.Take(r)!;
    }
}

[DynamicInterfaceCastableImplementation]
internal unsafe interface IBindCtxImpl : IBindCtx
{
    private static IntPtr Self(object o) => Com.This(o, typeof(IBindCtx).TypeHandle);

    private static void WithUnknown(object o, int slot, object? arg)
    {
        IntPtr p = Self(o);
        IntPtr u = Com.BorrowUnknown(arg);
        try { Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)BclComTypes.Slot(p, slot))(p, u)); }
        finally { Com.Release(u); }
    }

    void IBindCtx.RegisterObjectBound(object punk) => WithUnknown(this, 3, punk);

    void IBindCtx.RevokeObjectBound(object punk) => WithUnknown(this, 4, punk);

    void IBindCtx.ReleaseBoundObjects()
    {
        IntPtr p = Self(this);
        Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, int>)BclComTypes.Slot(p, 5))(p));
    }

    void IBindCtx.SetBindOptions(ref BIND_OPTS pbindopts)
    {
        IntPtr p = Self(this);
        fixed (BIND_OPTS* o = &pbindopts) Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, BIND_OPTS*, int>)BclComTypes.Slot(p, 6))(p, o));
    }

    void IBindCtx.GetBindOptions(ref BIND_OPTS pbindopts)
    {
        IntPtr p = Self(this);
        fixed (BIND_OPTS* o = &pbindopts) Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, BIND_OPTS*, int>)BclComTypes.Slot(p, 7))(p, o));
    }

    void IBindCtx.GetRunningObjectTable(out IRunningObjectTable pprot)
    {
        IntPtr p = Self(this);
        IntPtr r = IntPtr.Zero;
        Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)BclComTypes.Slot(p, 8))(p, &r));
        object? o = Com.Take(r);
        if (o is not null and not IRunningObjectTable)
            throw Com.Unsupported("IBindCtx.GetRunningObjectTable", "IRunningObjectTable is not among the BCL COM interfaces supported off built-in COM");
        pprot = (IRunningObjectTable)o!;
    }

    void IBindCtx.RegisterObjectParam(string pszKey, object punk)
    {
        IntPtr p = Self(this);
        IntPtr u = Com.BorrowUnknown(punk);
        try
        {
            fixed (char* k = pszKey) Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, char*, IntPtr, int>)BclComTypes.Slot(p, 9))(p, k, u));
        }
        finally { Com.Release(u); }
    }

    void IBindCtx.GetObjectParam(string pszKey, out object ppunk)
    {
        IntPtr p = Self(this);
        IntPtr r = IntPtr.Zero;
        int hr;
        fixed (char* k = pszKey) hr = ((delegate* unmanaged[Stdcall]<IntPtr, char*, IntPtr*, int>)BclComTypes.Slot(p, 10))(p, k, &r);
        Com.Check(hr);
        ppunk = Com.Take(r)!;
    }

    void IBindCtx.EnumObjectParam(out IEnumString ppenum)
    {
        IntPtr p = Self(this);
        IntPtr r = IntPtr.Zero;
        Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)BclComTypes.Slot(p, 11))(p, &r));
        ppenum = (IEnumString)Com.Take(r)!;
    }

    int IBindCtx.RevokeObjectParam(string pszKey)
    {
        IntPtr p = Self(this);
        fixed (char* k = pszKey) return ((delegate* unmanaged[Stdcall]<IntPtr, char*, int>)BclComTypes.Slot(p, 12))(p, k);
    }
}

[DynamicInterfaceCastableImplementation]
internal unsafe interface IEnumStringImpl : IEnumString
{
    private static IntPtr Self(object o) => Com.This(o, typeof(IEnumString).TypeHandle);

    int IEnumString.Next(int celt, string[] rgelt, IntPtr pceltFetched)
    {
        IntPtr p = Self(this);
        int n = Math.Max(0, celt);
        IntPtr* buf = (IntPtr*)NativeMemory.AllocZeroed((nuint)Math.Max(1, n), (nuint)sizeof(IntPtr));
        try
        {
            int hr = ((delegate* unmanaged[Stdcall]<IntPtr, int, IntPtr*, IntPtr, int>)BclComTypes.Slot(p, 3))(p, celt, buf, pceltFetched);
            // [Out] string[]: every element converted back, as the built-in marshaller does
            for (int i = 0; i < n && rgelt != null && i < rgelt.Length; i++)
            {
                rgelt[i] = buf[i] == IntPtr.Zero ? null! : Marshal.PtrToStringUni(buf[i])!;
                if (buf[i] != IntPtr.Zero) Marshal.FreeCoTaskMem(buf[i]);
            }
            return hr;
        }
        finally { NativeMemory.Free(buf); }
    }

    int IEnumString.Skip(int celt)
    {
        IntPtr p = Self(this);
        return ((delegate* unmanaged[Stdcall]<IntPtr, int, int>)BclComTypes.Slot(p, 4))(p, celt);
    }

    void IEnumString.Reset()
    {
        IntPtr p = Self(this);
        Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, int>)BclComTypes.Slot(p, 5))(p));
    }

    void IEnumString.Clone(out IEnumString ppenum)
    {
        IntPtr p = Self(this);
        IntPtr r = IntPtr.Zero;
        Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)BclComTypes.Slot(p, 6))(p, &r));
        ppenum = (IEnumString)Com.Take(r)!;
    }
}

// ---- managed objects handed to native code ----------------------------------------------------------

internal static unsafe class StreamCcw
{
    private static IStream Target(IntPtr self) => (IStream)Com.CcwTarget(self);

    public static IntPtr Vtable()
    {
        var vt = (IntPtr*)Com.NewVtable(14);
        vt[3] = (IntPtr)(delegate* unmanaged<IntPtr, byte*, int, IntPtr, int>)&Read;
        vt[4] = (IntPtr)(delegate* unmanaged<IntPtr, byte*, int, IntPtr, int>)&Write;
        vt[5] = (IntPtr)(delegate* unmanaged<IntPtr, long, int, IntPtr, int>)&Seek;
        vt[6] = (IntPtr)(delegate* unmanaged<IntPtr, long, int>)&SetSize;
        vt[7] = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, long, IntPtr, IntPtr, int>)&CopyTo;
        vt[8] = (IntPtr)(delegate* unmanaged<IntPtr, int, int>)&Commit;
        vt[9] = (IntPtr)(delegate* unmanaged<IntPtr, int>)&Revert;
        vt[10] = (IntPtr)(delegate* unmanaged<IntPtr, long, long, int, int>)&LockRegion;
        vt[11] = (IntPtr)(delegate* unmanaged<IntPtr, long, long, int, int>)&UnlockRegion;
        vt[12] = (IntPtr)(delegate* unmanaged<IntPtr, BclComTypes.NativeStatstg*, int, int>)&Stat;
        vt[13] = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr*, int>)&Clone;
        return (IntPtr)vt;
    }

    [UnmanagedCallersOnly]
    private static int Read(IntPtr self, byte* pv, int cb, IntPtr pcbRead)
    {
        try
        {
            var a = new byte[Math.Max(0, cb)];
            Target(self).Read(a, cb, pcbRead);
            // [Out] byte[]: the whole array copied back, as the built-in marshaller does
            if (a.Length > 0) a.AsSpan().CopyTo(new Span<byte>(pv, a.Length));
            return 0;
        }
        catch (Exception e) { return Com.HResultOf(e); }
    }

    [UnmanagedCallersOnly]
    private static int Write(IntPtr self, byte* pv, int cb, IntPtr pcbWritten)
    {
        try
        {
            var a = cb > 0 ? new ReadOnlySpan<byte>(pv, cb).ToArray() : Array.Empty<byte>();
            Target(self).Write(a, cb, pcbWritten);
            return 0;
        }
        catch (Exception e) { return Com.HResultOf(e); }
    }

    [UnmanagedCallersOnly]
    private static int Seek(IntPtr self, long move, int origin, IntPtr newPosition)
    {
        try { Target(self).Seek(move, origin, newPosition); return 0; }
        catch (Exception e) { return Com.HResultOf(e); }
    }

    [UnmanagedCallersOnly]
    private static int SetSize(IntPtr self, long size)
    {
        try { Target(self).SetSize(size); return 0; }
        catch (Exception e) { return Com.HResultOf(e); }
    }

    [UnmanagedCallersOnly]
    private static int CopyTo(IntPtr self, IntPtr other, long cb, IntPtr pcbRead, IntPtr pcbWritten)
    {
        try { Target(self).CopyTo((IStream)Com.Wrap(other)!, cb, pcbRead, pcbWritten); return 0; }
        catch (Exception e) { return Com.HResultOf(e); }
    }

    [UnmanagedCallersOnly]
    private static int Commit(IntPtr self, int flags)
    {
        try { Target(self).Commit(flags); return 0; }
        catch (Exception e) { return Com.HResultOf(e); }
    }

    [UnmanagedCallersOnly]
    private static int Revert(IntPtr self)
    {
        try { Target(self).Revert(); return 0; }
        catch (Exception e) { return Com.HResultOf(e); }
    }

    [UnmanagedCallersOnly]
    private static int LockRegion(IntPtr self, long offset, long cb, int type)
    {
        try { Target(self).LockRegion(offset, cb, type); return 0; }
        catch (Exception e) { return Com.HResultOf(e); }
    }

    [UnmanagedCallersOnly]
    private static int UnlockRegion(IntPtr self, long offset, long cb, int type)
    {
        try { Target(self).UnlockRegion(offset, cb, type); return 0; }
        catch (Exception e) { return Com.HResultOf(e); }
    }

    [UnmanagedCallersOnly]
    private static int Stat(IntPtr self, BclComTypes.NativeStatstg* s, int flag)
    {
        try { Target(self).Stat(out STATSTG m, flag); BclComTypes.ToNative(m, s); return 0; }
        catch (Exception e) { return Com.HResultOf(e); }
    }

    [UnmanagedCallersOnly]
    private static int Clone(IntPtr self, IntPtr* r)
    {
        try { *r = IntPtr.Zero; Target(self).Clone(out IStream c); *r = BclComTypes.Borrow<IStream>(c); return 0; }
        catch (Exception e) { return Com.HResultOf(e); }
    }
}

internal static unsafe class BindCtxCcw
{
    private static IBindCtx Target(IntPtr self) => (IBindCtx)Com.CcwTarget(self);

    public static IntPtr Vtable()
    {
        var vt = (IntPtr*)Com.NewVtable(13);
        vt[3] = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, int>)&RegisterObjectBound;
        vt[4] = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, int>)&RevokeObjectBound;
        vt[5] = (IntPtr)(delegate* unmanaged<IntPtr, int>)&ReleaseBoundObjects;
        vt[6] = (IntPtr)(delegate* unmanaged<IntPtr, BIND_OPTS*, int>)&SetBindOptions;
        vt[7] = (IntPtr)(delegate* unmanaged<IntPtr, BIND_OPTS*, int>)&GetBindOptions;
        vt[8] = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr*, int>)&GetRunningObjectTable;
        vt[9] = (IntPtr)(delegate* unmanaged<IntPtr, char*, IntPtr, int>)&RegisterObjectParam;
        vt[10] = (IntPtr)(delegate* unmanaged<IntPtr, char*, IntPtr*, int>)&GetObjectParam;
        vt[11] = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr*, int>)&EnumObjectParam;
        vt[12] = (IntPtr)(delegate* unmanaged<IntPtr, char*, int>)&RevokeObjectParam;
        return (IntPtr)vt;
    }

    [UnmanagedCallersOnly]
    private static int RegisterObjectBound(IntPtr self, IntPtr u)
    {
        try { Target(self).RegisterObjectBound(Com.Wrap(u)!); return 0; }
        catch (Exception e) { return Com.HResultOf(e); }
    }

    [UnmanagedCallersOnly]
    private static int RevokeObjectBound(IntPtr self, IntPtr u)
    {
        try { Target(self).RevokeObjectBound(Com.Wrap(u)!); return 0; }
        catch (Exception e) { return Com.HResultOf(e); }
    }

    [UnmanagedCallersOnly]
    private static int ReleaseBoundObjects(IntPtr self)
    {
        try { Target(self).ReleaseBoundObjects(); return 0; }
        catch (Exception e) { return Com.HResultOf(e); }
    }

    [UnmanagedCallersOnly]
    private static int SetBindOptions(IntPtr self, BIND_OPTS* o)
    {
        try { var v = *o; Target(self).SetBindOptions(ref v); return 0; }
        catch (Exception e) { return Com.HResultOf(e); }
    }

    [UnmanagedCallersOnly]
    private static int GetBindOptions(IntPtr self, BIND_OPTS* o)
    {
        try { var v = *o; Target(self).GetBindOptions(ref v); *o = v; return 0; }
        catch (Exception e) { return Com.HResultOf(e); }
    }

    [UnmanagedCallersOnly]
    private static int GetRunningObjectTable(IntPtr self, IntPtr* r)
    {
        try { *r = IntPtr.Zero; Target(self).GetRunningObjectTable(out IRunningObjectTable t); *r = BclComTypes.Borrow<IRunningObjectTable>(t); return 0; }
        catch (Exception e) { return Com.HResultOf(e); }
    }

    [UnmanagedCallersOnly]
    private static int RegisterObjectParam(IntPtr self, char* key, IntPtr u)
    {
        try { Target(self).RegisterObjectParam(new string(key), Com.Wrap(u)!); return 0; }
        catch (Exception e) { return Com.HResultOf(e); }
    }

    [UnmanagedCallersOnly]
    private static int GetObjectParam(IntPtr self, char* key, IntPtr* r)
    {
        try { *r = IntPtr.Zero; Target(self).GetObjectParam(new string(key), out object o); *r = Com.BorrowUnknown(o); return 0; }
        catch (Exception e) { return Com.HResultOf(e); }
    }

    [UnmanagedCallersOnly]
    private static int EnumObjectParam(IntPtr self, IntPtr* r)
    {
        try { *r = IntPtr.Zero; Target(self).EnumObjectParam(out IEnumString en); *r = BclComTypes.Borrow<IEnumString>(en); return 0; }
        catch (Exception e) { return Com.HResultOf(e); }
    }

    [UnmanagedCallersOnly]
    private static int RevokeObjectParam(IntPtr self, char* key)
    {
        try { return Target(self).RevokeObjectParam(new string(key)); }
        catch (Exception e) { return Com.HResultOf(e); }
    }
}

internal static unsafe class EnumStringCcw
{
    private static IEnumString Target(IntPtr self) => (IEnumString)Com.CcwTarget(self);

    public static IntPtr Vtable()
    {
        var vt = (IntPtr*)Com.NewVtable(7);
        vt[3] = (IntPtr)(delegate* unmanaged<IntPtr, int, IntPtr*, IntPtr, int>)&Next;
        vt[4] = (IntPtr)(delegate* unmanaged<IntPtr, int, int>)&Skip;
        vt[5] = (IntPtr)(delegate* unmanaged<IntPtr, int>)&Reset;
        vt[6] = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr*, int>)&Clone;
        return (IntPtr)vt;
    }

    [UnmanagedCallersOnly]
    private static int Next(IntPtr self, int celt, IntPtr* rgelt, IntPtr fetched)
    {
        try
        {
            var a = new string[Math.Max(0, celt)];
            int hr = Target(self).Next(celt, a, fetched);
            for (int i = 0; i < a.Length; i++) rgelt[i] = a[i] == null ? IntPtr.Zero : Marshal.StringToCoTaskMemUni(a[i]);
            return hr;
        }
        catch (Exception e) { return Com.HResultOf(e); }
    }

    [UnmanagedCallersOnly]
    private static int Skip(IntPtr self, int celt)
    {
        try { return Target(self).Skip(celt); }
        catch (Exception e) { return Com.HResultOf(e); }
    }

    [UnmanagedCallersOnly]
    private static int Reset(IntPtr self)
    {
        try { Target(self).Reset(); return 0; }
        catch (Exception e) { return Com.HResultOf(e); }
    }

    [UnmanagedCallersOnly]
    private static int Clone(IntPtr self, IntPtr* r)
    {
        try { *r = IntPtr.Zero; Target(self).Clone(out IEnumString c); *r = BclComTypes.Borrow<IEnumString>(c); return 0; }
        catch (Exception e) { return Com.HResultOf(e); }
    }
}
