// VARIANT marshalling for rewritten COM calls (Documentation/comimport-rewriter.md): what built-in COM
// interop does for an object parameter, an object by reference and an object retval, for the common
// VT_ types. Arrays (SAFEARRAY) and VT_BYREF are refused by name.

using System.Runtime.InteropServices;

namespace WpfMac.ComInterop;

/// <summary>A VARIANT: 16 bytes on 32-bit, 24 on 64-bit, as the Windows SDK lays it out.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct ComVariant
{
    public ushort Vt;
    public ushort Reserved1, Reserved2, Reserved3;
    public IntPtr Data1;
    public IntPtr Data2;
}

public static unsafe partial class Com
{
    private const ushort VT_EMPTY = 0, VT_NULL = 1, VT_I2 = 2, VT_I4 = 3, VT_R4 = 4, VT_R8 = 5, VT_CY = 6, VT_DATE = 7, VT_BSTR = 8,
        VT_DISPATCH = 9, VT_ERROR = 10, VT_BOOL = 11, VT_VARIANT = 12, VT_UNKNOWN = 13, VT_DECIMAL = 14, VT_I1 = 16, VT_UI1 = 17,
        VT_UI2 = 18, VT_UI4 = 19, VT_I8 = 20, VT_UI8 = 21, VT_INT = 22, VT_UINT = 23, VT_ARRAY = 0x2000, VT_BYREF = 0x4000;

    /// <summary>Writes <paramref name="o"/> as a VARIANT at <paramref name="dst"/> (which the caller clears
    /// with <see cref="ClearVariant"/> after the call).</summary>
    public static void ToVariant(object? o, IntPtr dst)
    {
        var v = (ComVariant*)dst;
        *v = default;
        void* d = &v->Data1;
        switch (o)
        {
            case null: v->Vt = VT_EMPTY; return;
            case DBNull: v->Vt = VT_NULL; return;
            case System.Reflection.Missing: v->Vt = VT_ERROR; *(int*)d = unchecked((int)0x80020004); return;     // DISP_E_PARAMNOTFOUND
            case bool b: v->Vt = VT_BOOL; *(short*)d = (short)(b ? -1 : 0); return;
            case sbyte x: v->Vt = VT_I1; *(sbyte*)d = x; return;
            case byte x: v->Vt = VT_UI1; *(byte*)d = x; return;
            case short x: v->Vt = VT_I2; *(short*)d = x; return;
            case ushort x: v->Vt = VT_UI2; *(ushort*)d = x; return;
            case int x: v->Vt = VT_I4; *(int*)d = x; return;
            case uint x: v->Vt = VT_UI4; *(uint*)d = x; return;
            case long x: v->Vt = VT_I8; *(long*)d = x; return;
            case ulong x: v->Vt = VT_UI8; *(ulong*)d = x; return;
            case float x: v->Vt = VT_R4; *(float*)d = x; return;
            case double x: v->Vt = VT_R8; *(double*)d = x; return;
            case DateTime x: v->Vt = VT_DATE; *(double*)d = x.ToOADate(); return;
            case string s: v->Vt = VT_BSTR; *(IntPtr*)d = Marshal.StringToBSTR(s); return;
            case char c: v->Vt = VT_UI2; *(ushort*)d = c; return;
            case decimal m:
            {
                // DECIMAL overlays the whole VARIANT; its first word is the VARIANT's type
                int[] bits = decimal.GetBits(m);
                byte* p = (byte*)v;
                *(ushort*)(p + 2) = (ushort)(((bits[3] >> 16) & 0xFF) | (bits[3] < 0 ? 0x8000 : 0));       // scale, sign
                *(int*)(p + 4) = bits[2];                                                                    // Hi32
                *(long*)(p + 8) = (uint)bits[0] | ((long)bits[1] << 32);                                     // Lo64
                v->Vt = VT_DECIMAL;
                return;
            }
            case Enum e: ToVariant(Convert.ChangeType(e, Enum.GetUnderlyingType(e.GetType())), dst); return;
            case ComObject c: v->Vt = VT_UNKNOWN; *(IntPtr*)d = c.QueryInterface(IID_IUnknown); return;
            case Array: throw Unsupported("VARIANT", $"an array ({o.GetType()}) as a VARIANT (a SAFEARRAY)");
        }
        // a managed object handed to native code: its CCW (IUnknown)
        v->Vt = VT_UNKNOWN;
        *(IntPtr*)d = BorrowUnknown(o);
    }

    /// <summary>The object a VARIANT holds (the VARIANT keeps what it owns: clear it after).</summary>
    public static object? FromVariant(IntPtr src)
    {
        var v = (ComVariant*)src;
        void* d = &v->Data1;
        ushort vt = v->Vt;
        if ((vt & (VT_ARRAY | VT_BYREF)) != 0) throw Unsupported("VARIANT", $"a VARIANT of type 0x{vt:x4} (an array or by reference)");
        switch (vt)
        {
            case VT_EMPTY: return null;
            case VT_NULL: return DBNull.Value;
            case VT_BOOL: return *(short*)d != 0;
            case VT_I1: return *(sbyte*)d;
            case VT_UI1: return *(byte*)d;
            case VT_I2: return *(short*)d;
            case VT_UI2: return *(ushort*)d;
            case VT_I4: case VT_INT: return *(int*)d;
            case VT_UI4: case VT_UINT: return *(uint*)d;
            case VT_ERROR: return *(int*)d;
            case VT_I8: return *(long*)d;
            case VT_UI8: return *(ulong*)d;
            case VT_R4: return *(float*)d;
            case VT_R8: return *(double*)d;
            case VT_CY: return decimal.FromOACurrency(*(long*)d);
            case VT_DATE: return DateTime.FromOADate(*(double*)d);
            case VT_BSTR: return *(IntPtr*)d == IntPtr.Zero ? null : Marshal.PtrToStringBSTR(*(IntPtr*)d);
            case VT_UNKNOWN: case VT_DISPATCH: return *(IntPtr*)d == IntPtr.Zero ? null : ComObject.Get(*(IntPtr*)d);
            case VT_DECIMAL:
            {
                byte* p = (byte*)v;
                ushort signScale = *(ushort*)(p + 2);
                long lo = *(long*)(p + 8);
                return new decimal((int)lo, (int)(lo >> 32), *(int*)(p + 4), (signScale & 0x8000) != 0, (byte)(signScale & 0xFF));
            }
        }
        throw Unsupported("VARIANT", $"a VARIANT of type {vt}");
    }

    /// <summary>VariantClear: what the VARIANT owns is let go of, and it is VT_EMPTY.</summary>
    public static void ClearVariant(IntPtr p)
    {
        var v = (ComVariant*)p;
        void* d = &v->Data1;
        switch (v->Vt)
        {
            case VT_BSTR: if (*(IntPtr*)d != IntPtr.Zero) Marshal.FreeBSTR(*(IntPtr*)d); break;
            case VT_UNKNOWN: case VT_DISPATCH: if (*(IntPtr*)d != IntPtr.Zero) Marshal.Release(*(IntPtr*)d); break;
        }
        *v = default;
    }
}
