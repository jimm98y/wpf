// PROPVARIANT and VARIANT housekeeping as ole32/oleaut32 do it (PropVariantClear, PropVariantCopy, VariantClear,
// VariantCopy, VariantInit), for the Win32 shim's exports off Windows (WpfWebGpu.Win32/Shell.cs): apps P/Invoke these
// to free what a PROPVARIANT owns -- MediaFoundation's and the Windows API Code Pack's PropVariant classes call
// PropVariantClear from their Dispose. A PROPVARIANT is 16 bytes on 32-bit, 24 on 64-bit, like a VARIANT.

using System.Runtime.InteropServices;

namespace WpfWebGpu.ComInterop;

public static unsafe partial class Com
{
    private const ushort VT_HRESULT = 25, VT_LPSTR = 30, VT_LPWSTR = 31, VT_FILETIME = 64, VT_BLOB = 65, VT_STREAM = 66, VT_STORAGE = 67,
        VT_STREAMED_OBJECT = 68, VT_STORED_OBJECT = 69, VT_BLOB_OBJECT = 70, VT_CF = 71, VT_CLSID = 72, VT_VERSIONED_STREAM = 73, VT_VECTOR = 0x1000;
    private const int DISP_E_BADVARTYPE = unchecked((int)0x80020008);

    private static int PropVariantSize => sizeof(ComVariant);

    /// <summary>PropVariantClear: what the PROPVARIANT owns is freed (strings, interfaces, blobs, vectors, arrays) and it
    /// is VT_EMPTY; DISP_E_BADVARTYPE for a type it does not know (left as it is, as ole32 leaves it).</summary>
    public static int PropVariantClear(IntPtr pv)
    {
        if (pv == IntPtr.Zero) return S_OK0;
        var v = (ComVariant*)pv;
        ushort vt = v->Vt;
        void* d = &v->Data1;
        if ((vt & VT_BYREF) != 0) { *v = default; return S_OK0; }
        if ((vt & VT_ARRAY) != 0) { DestroySafeArray(*(IntPtr*)d); *v = default; return S_OK0; }
        if ((vt & VT_VECTOR) != 0)
        {
            ushort et = (ushort)(vt & ~VT_VECTOR);
            uint n = *(uint*)d;
            byte* e = *(byte**)((byte*)d + IntPtr.Size);
            int size = VectorElementSize(et);
            if (size < 0) return DISP_E_BADVARTYPE;
            if (e != null)
            {
                for (uint i = 0; i < n; i++) ClearVectorElement(e + i * size, et);
                Marshal.FreeCoTaskMem((IntPtr)e);
            }
            *v = default;
            return S_OK0;
        }
        switch (vt)
        {
            case VT_EMPTY: case VT_NULL: case VT_I1: case VT_UI1: case VT_I2: case VT_UI2: case VT_I4: case VT_UI4: case VT_I8: case VT_UI8:
            case VT_INT: case VT_UINT: case VT_R4: case VT_R8: case VT_BOOL: case VT_ERROR: case VT_CY: case VT_DATE: case VT_FILETIME:
            case VT_HRESULT: case VT_DECIMAL:
                break;
            case VT_BSTR: FreeBstr(*(IntPtr*)d); break;
            case VT_LPSTR: case VT_LPWSTR: case VT_CLSID: FreeCoTaskMem(*(IntPtr*)d); break;
            case VT_UNKNOWN: case VT_DISPATCH: case VT_STREAM: case VT_STORAGE: case VT_STREAMED_OBJECT: case VT_STORED_OBJECT:
                Release(*(IntPtr*)d); break;
            case VT_BLOB: case VT_BLOB_OBJECT: FreeCoTaskMem(*(IntPtr*)((byte*)d + IntPtr.Size)); break;
            case VT_CF:
            {
                // CLIPDATA*: { ULONG cbSize; LONG ulClipFmt; BYTE* pClipData }
                byte* cf = *(byte**)d;
                if (cf != null) { FreeCoTaskMem(*(IntPtr*)(cf + 8)); Marshal.FreeCoTaskMem((IntPtr)cf); }
                break;
            }
            case VT_VERSIONED_STREAM:
            {
                // VERSIONEDSTREAM*: { GUID guidVersion; IStream* pStream }
                byte* vs = *(byte**)d;
                if (vs != null) { Release(*(IntPtr*)(vs + 16)); Marshal.FreeCoTaskMem((IntPtr)vs); }
                break;
            }
            default:
                return DISP_E_BADVARTYPE;
        }
        *v = default;
        return S_OK0;
    }

    private const int S_OK0 = 0;

    private static int VectorElementSize(ushort et) => et switch
    {
        VT_I1 or VT_UI1 => 1,
        VT_I2 or VT_UI2 or VT_BOOL => 2,
        VT_I4 or VT_UI4 or VT_R4 or VT_ERROR or VT_INT or VT_UINT => 4,
        VT_I8 or VT_UI8 or VT_R8 or VT_CY or VT_DATE or VT_FILETIME => 8,
        VT_CLSID => 16,
        VT_BSTR or VT_LPSTR or VT_LPWSTR or VT_UNKNOWN or VT_DISPATCH => IntPtr.Size,
        VT_VARIANT => PropVariantSize,           // a vector of PROPVARIANTs
        VT_CF => IntPtr.Size == 8 ? 16 : 12,     // CLIPDATA by value
        _ => -1,
    };

    private static void ClearVectorElement(byte* p, ushort et)
    {
        switch (et)
        {
            case VT_BSTR: FreeBstr(*(IntPtr*)p); break;
            case VT_LPSTR: case VT_LPWSTR: FreeCoTaskMem(*(IntPtr*)p); break;
            case VT_UNKNOWN: case VT_DISPATCH: Release(*(IntPtr*)p); break;
            case VT_VARIANT: PropVariantClear((IntPtr)p); break;
            case VT_CF: FreeCoTaskMem(*(IntPtr*)(p + 8)); break;
        }
    }

    /// <summary>PropVariantCopy: a deep copy into <paramref name="dst"/> (whose old contents are not freed, as ole32
    /// does not free them).</summary>
    public static int PropVariantCopy(IntPtr dst, IntPtr src)
    {
        if (dst == IntPtr.Zero || src == IntPtr.Zero) return E_INVALIDARG0;
        var s = (ComVariant*)src;
        var t = (ComVariant*)dst;
        *t = *s;                                  // the scalar types, and the shape for the rest
        ushort vt = s->Vt;
        void* sd = &s->Data1, td = &t->Data1;
        if ((vt & VT_BYREF) != 0) return S_OK0;
        if ((vt & VT_ARRAY) != 0)
        {
            IntPtr psa = *(IntPtr*)sd;
            *(IntPtr*)td = psa == IntPtr.Zero ? IntPtr.Zero : ToSafeArray(FromSafeArray(psa, typeof(Array).TypeHandle), SafeArrayVartype(psa));
            return S_OK0;
        }
        if ((vt & VT_VECTOR) != 0)
        {
            ushort et = (ushort)(vt & ~VT_VECTOR);
            int size = VectorElementSize(et);
            if (size < 0) { *t = default; return DISP_E_BADVARTYPE; }
            uint n = *(uint*)sd;
            byte* e = *(byte**)((byte*)sd + IntPtr.Size);
            byte* c = n == 0 || e == null ? null : (byte*)Marshal.AllocCoTaskMem((int)(n * size));
            if (c != null) Buffer.MemoryCopy(e, c, n * size, n * size);
            for (uint i = 0; c != null && i < n; i++) CopyVectorElement(c + i * size, e + i * size, et);
            *(byte**)((byte*)td + IntPtr.Size) = c;
            return S_OK0;
        }
        switch (vt)
        {
            case VT_BSTR:
            {
                IntPtr b = *(IntPtr*)sd;
                *(IntPtr*)td = b == IntPtr.Zero ? IntPtr.Zero : Marshal.StringToBSTR(Marshal.PtrToStringBSTR(b));
                break;
            }
            case VT_LPWSTR: *(IntPtr*)td = *(IntPtr*)sd == IntPtr.Zero ? IntPtr.Zero : Marshal.StringToCoTaskMemUni(Marshal.PtrToStringUni(*(IntPtr*)sd)); break;
            case VT_LPSTR: *(IntPtr*)td = *(IntPtr*)sd == IntPtr.Zero ? IntPtr.Zero : Marshal.StringToCoTaskMemAnsi(Marshal.PtrToStringAnsi(*(IntPtr*)sd)); break;
            case VT_CLSID:
            {
                Guid* g = *(Guid**)sd;
                if (g != null) { var c = (Guid*)Marshal.AllocCoTaskMem(16); *c = *g; *(Guid**)td = c; }
                break;
            }
            case VT_UNKNOWN: case VT_DISPATCH: case VT_STREAM: case VT_STORAGE: case VT_STREAMED_OBJECT: case VT_STORED_OBJECT:
                if (*(IntPtr*)sd != IntPtr.Zero) Marshal.AddRef(*(IntPtr*)sd);
                break;
            case VT_BLOB: case VT_BLOB_OBJECT:
            {
                uint n = *(uint*)sd;
                byte* b = *(byte**)((byte*)sd + IntPtr.Size);
                byte* c = n == 0 || b == null ? null : (byte*)Marshal.AllocCoTaskMem((int)n);
                if (c != null) Buffer.MemoryCopy(b, c, n, n);
                *(byte**)((byte*)td + IntPtr.Size) = c;
                break;
            }
            case VT_CF:
            {
                byte* cf = *(byte**)sd;
                if (cf == null) break;
                uint cb = *(uint*)cf;
                byte* c = (byte*)Marshal.AllocCoTaskMem(IntPtr.Size == 8 ? 16 : 12);
                Buffer.MemoryCopy(cf, c, 8, 8);
                byte* data = *(byte**)(cf + 8);
                int bytes = (int)Math.Max(0, (long)cb - 4);            // cbSize counts ulClipFmt
                byte* nd = data == null || bytes == 0 ? null : (byte*)Marshal.AllocCoTaskMem(bytes);
                if (nd != null) Buffer.MemoryCopy(data, nd, bytes, bytes);
                *(byte**)(c + 8) = nd;
                *(byte**)td = c;
                break;
            }
            case VT_EMPTY: case VT_NULL: case VT_I1: case VT_UI1: case VT_I2: case VT_UI2: case VT_I4: case VT_UI4: case VT_I8: case VT_UI8:
            case VT_INT: case VT_UINT: case VT_R4: case VT_R8: case VT_BOOL: case VT_ERROR: case VT_CY: case VT_DATE: case VT_FILETIME:
            case VT_HRESULT: case VT_DECIMAL:
                break;
            default:
                *t = default;
                return DISP_E_BADVARTYPE;
        }
        return S_OK0;
    }

    private const int E_INVALIDARG0 = unchecked((int)0x80070057);

    private static void CopyVectorElement(byte* dst, byte* src, ushort et)
    {
        switch (et)
        {
            case VT_BSTR: *(IntPtr*)dst = *(IntPtr*)src == IntPtr.Zero ? IntPtr.Zero : Marshal.StringToBSTR(Marshal.PtrToStringBSTR(*(IntPtr*)src)); break;
            case VT_LPWSTR: *(IntPtr*)dst = *(IntPtr*)src == IntPtr.Zero ? IntPtr.Zero : Marshal.StringToCoTaskMemUni(Marshal.PtrToStringUni(*(IntPtr*)src)); break;
            case VT_LPSTR: *(IntPtr*)dst = *(IntPtr*)src == IntPtr.Zero ? IntPtr.Zero : Marshal.StringToCoTaskMemAnsi(Marshal.PtrToStringAnsi(*(IntPtr*)src)); break;
            case VT_UNKNOWN: case VT_DISPATCH: if (*(IntPtr*)src != IntPtr.Zero) Marshal.AddRef(*(IntPtr*)src); break;
            case VT_VARIANT: PropVariantCopy((IntPtr)dst, (IntPtr)src); break;
        }
    }

    /// <summary>VariantClear: as PropVariantClear, for the types a VARIANT may hold (DISP_E_BADVARTYPE for the
    /// PROPVARIANT-only ones).</summary>
    public static int VariantClear(IntPtr pv)
    {
        if (pv == IntPtr.Zero) return S_OK0;
        ushort vt = ((ComVariant*)pv)->Vt;
        ushort b = (ushort)(vt & ~(VT_ARRAY | VT_BYREF));
        if ((vt & VT_VECTOR) != 0 || b is VT_LPSTR or VT_LPWSTR or VT_FILETIME or VT_BLOB or VT_STREAM or VT_STORAGE or VT_STREAMED_OBJECT
            or VT_STORED_OBJECT or VT_BLOB_OBJECT or VT_CF or VT_CLSID or VT_VERSIONED_STREAM)
            return DISP_E_BADVARTYPE;
        return PropVariantClear(pv);
    }

    /// <summary>VariantCopy: <paramref name="dst"/> cleared first (as oleaut32 does), then a deep copy.</summary>
    public static int VariantCopy(IntPtr dst, IntPtr src)
    {
        if (dst == src) return S_OK0;
        int hr = VariantClear(dst);
        if (hr < 0) return hr;
        return PropVariantCopy(dst, src);
    }

    /// <summary>VariantInit: VT_EMPTY (the rest is not touched, as oleaut32 leaves it).</summary>
    public static void VariantInit(IntPtr pv) { if (pv != IntPtr.Zero) ((ComVariant*)pv)->Vt = VT_EMPTY; }
}
