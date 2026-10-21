// IEnumerable and IEnumerator returned by a COM method (Documentation/comimport-rewriter.md), as built-in
// interop's EnumerableToDispatchMarshaler / EnumeratorToEnumVariantMarshaler give them: an IEnumerable is the
// object's IEnumVARIANT -- asked for directly, or from IDispatch's DISPID_NEWENUM -- and its elements the
// VARIANTs it hands out (ComObjects for interfaces).

using System.Collections;
using System.Runtime.InteropServices;

namespace WpfWebGpu.ComInterop;

public static unsafe partial class Com
{
    private static readonly Guid IID_IEnumVARIANT = new("00020404-0000-0000-C000-000000000046");
    private const int DISPID_NEWENUM = -4;

    /// <summary>A returned IEnumerable (the callee's reference, taken over).</summary>
    public static object? TakeEnumerable(IntPtr p)
    {
        if (p == IntPtr.Zero) return null;
        try { return new NativeEnumerable(ComObject.Get(p)); }
        finally { Marshal.Release(p); }
    }

    /// <summary>A returned IEnumerator: the IEnumVARIANT itself (the callee's reference, taken over).</summary>
    public static object? TakeEnumerator(IntPtr p)
    {
        if (p == IntPtr.Zero) return null;
        try { return new VariantEnumerator(ComObject.Get(p).QueryInterface(IID_IEnumVARIANT)); }
        finally { Marshal.Release(p); }
    }

    private sealed class NativeEnumerable : IEnumerable
    {
        private readonly ComObject _o;
        public NativeEnumerable(ComObject o) { _o = o; }

        public IEnumerator GetEnumerator()
        {
            IntPtr e = _o.For(IID_IEnumVARIANT, throwIfNot: false);
            if (e != IntPtr.Zero) { Marshal.AddRef(e); return new VariantEnumerator(e); }
            // IDispatch's DISPID_NEWENUM: an IUnknown whose IEnumVARIANT enumerates
            IntPtr disp = _o.For(IID_IDispatch, throwIfNot: true);
            var dp = new ComDispParams();
            ComVariant r = default;
            Guid none = Guid.Empty;
            int hr = ((delegate* unmanaged[Stdcall]<IntPtr, int, Guid*, int, ushort, ComDispParams*, ComVariant*, ComExcepInfo*, uint*, int>)(*(IntPtr**)disp)[6])
                (disp, DISPID_NEWENUM, &none, 0, (ushort)(DISPATCH_METHOD | DISPATCH_PROPERTYGET), &dp, &r, null, null);
            Check(hr);
            try
            {
                if ((r.Vt != VT_UNKNOWN && r.Vt != VT_DISPATCH) || r.Data1 == IntPtr.Zero) throw new InvalidCastException("DISPID_NEWENUM gave no enumerator");
                Guid iid = IID_IEnumVARIANT;
                Check(Marshal.QueryInterface(r.Data1, in iid, out IntPtr ev));
                return new VariantEnumerator(ev);
            }
            finally { ClearVariant((IntPtr)(&r)); }
        }
    }

    /// <summary>IEnumVARIANT as an IEnumerator (owns one reference).</summary>
    private sealed class VariantEnumerator : IEnumerator
    {
        private IntPtr _e;
        private object? _current;
        public VariantEnumerator(IntPtr e) { _e = e; }
        ~VariantEnumerator() { IntPtr e = Interlocked.Exchange(ref _e, IntPtr.Zero); if (e != IntPtr.Zero) Marshal.Release(e); }

        public object? Current => _current;

        public bool MoveNext()
        {
            ComVariant v = default;
            uint fetched = 0;
            int hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, ComVariant*, uint*, int>)(*(IntPtr**)_e)[3])(_e, 1, &v, &fetched);
            Check(hr);
            if (hr != 0 || fetched == 0) { _current = null; return false; }
            try { _current = FromVariant((IntPtr)(&v)); }
            finally { ClearVariant((IntPtr)(&v)); }
            return true;
        }

        public void Reset() => Check(((delegate* unmanaged[Stdcall]<IntPtr, int>)(*(IntPtr**)_e)[5])(_e));
    }
}
