// SAFEARRAY marshalling for rewritten COM calls (Documentation/comimport-rewriter.md): what built-in COM
// interop does for an array parameter of a COM interface method (COM's default for arrays), an array by
// reference, an array retval, and a VARIANT holding an array (VT_ARRAY).
//
// The descriptor is the Windows SDK's (cDims, fFeatures, cbElements, cLocks, pvData, then the bounds with
// the RIGHTMOST dimension first), its data column-major (the leftmost index varies fastest). On Windows the
// arrays are oleaut32's own (SafeArrayCreate/SafeArrayDestroy, plain exports, no COM), because native
// callees free and replace them; elsewhere the same layout is made here, the VARTYPE 4 bytes before the
// descriptor as oleaut32 keeps it (FADF_HAVEVARTYPE).

using System.Runtime.InteropServices;

namespace WpfWebGpu.ComInterop;

public static unsafe partial class Com
{
    private const ushort FADF_HAVEVARTYPE = 0x80, FADF_BSTR = 0x100, FADF_UNKNOWN = 0x200, FADF_DISPATCH = 0x400, FADF_VARIANT = 0x800,
        FADF_RECORD = 0x20, FADF_HAVEIID = 0x40;
    internal static readonly Guid IID_IDispatch = new("00020400-0000-0000-C000-000000000046");

    /// <summary>oleaut32's arrays (Windows), or the managed layout (elsewhere; on Windows too with the
    /// AppContext switch WpfWebGpu.ComInterop.ManagedSafeArrays, which the tests use).</summary>
    private static bool OleAut => OperatingSystem.IsWindows() && !(AppContext.TryGetSwitch("WpfWebGpu.ComInterop.ManagedSafeArrays", out bool m) && m);

    private static int PvDataOffset => IntPtr.Size == 8 ? 16 : 12;
    private static int BoundsOffset => PvDataOffset + IntPtr.Size;

    private static ushort Dims(IntPtr psa) => *(ushort*)psa;
    private static ushort Features(IntPtr psa) => *(ushort*)((byte*)psa + 2);
    private static uint ElementSize(IntPtr psa) => *(uint*)((byte*)psa + 4);
    private static byte* Data(IntPtr psa) => *(byte**)((byte*)psa + PvDataOffset);

    /// <summary>Dimension <paramref name="d"/> (0 = leftmost): its element count and lower bound.</summary>
    private static (int Count, int Lower) Bound(IntPtr psa, int d)
    {
        int dims = Dims(psa);
        byte* b = (byte*)psa + BoundsOffset + 8 * (dims - 1 - d);
        return ((int)*(uint*)b, *(int*)(b + 4));
    }

    /// <summary>An array argument as a SAFEARRAY (the caller destroys it after the call with
    /// <see cref="DestroySafeArray"/>). <paramref name="vt"/>: the element VARTYPE, 0 for the array's own
    /// element type's default. Null for null.</summary>
    public static IntPtr ToSafeArray(object? array, int vt)
    {
        if (array == null) return IntPtr.Zero;
        var a = (Array)array;
        Type elem = a.GetType().GetElementType()!;
        ushort t = vt != 0 ? (ushort)vt : VtOfElement(elem);
        int rank = a.Rank;
        var counts = new int[rank];
        var lowers = new int[rank];
        for (int d = 0; d < rank; d++) { counts[d] = a.GetLength(d); lowers[d] = a.GetLowerBound(d); }
        IntPtr psa = CreateSafeArray(t, counts, lowers);
        try
        {
            byte* data = Data(psa);
            int cb = (int)ElementSize(psa);
            int total = a.Length;
            var idx = new int[rank];
            for (int k = 0; k < total; k++)
            {
                // column-major: k's leftmost digit varies fastest
                int rem = k;
                for (int d = 0; d < rank; d++) { idx[d] = lowers[d] + rem % Math.Max(1, counts[d]); rem /= Math.Max(1, counts[d]); }
                WriteElement(data + (long)k * cb, t, rank == 1 ? a.GetValue(idx[0]) : a.GetValue(idx));
            }
            return psa;
        }
        catch { DestroySafeArray(psa); throw; }
    }

    /// <summary>The managed array a SAFEARRAY holds (the SAFEARRAY stays the caller's). <paramref name="arrayType"/>
    /// is the declared type: T[] (one dimension, made zero-based) or System.Array (any rank and bounds,
    /// its element type from the VARTYPE). Null for null.</summary>
    public static object? FromSafeArray(IntPtr psa, RuntimeTypeHandle arrayType)
    {
        if (psa == IntPtr.Zero) return null;
        Type target = Type.GetTypeFromHandle(arrayType)!;
        ushort vt = SafeArrayVartype(psa);
        int rank = Dims(psa);
        Type elem = target.IsArray ? target.GetElementType()! : TypeOfVt(vt);
        if (target.IsArray && target.IsSZArray && rank != 1)
            throw new SafeArrayRankMismatchException($"a SAFEARRAY of rank {rank} where a one-dimensional array is expected");
        var counts = new int[rank];
        var lowers = new int[rank];
        for (int d = 0; d < rank; d++) (counts[d], lowers[d]) = Bound(psa, d);
        Array a = rank == 1 && (target.IsSZArray || lowers[0] == 0) ? Array.CreateInstance(elem, counts[0]) : Array.CreateInstance(elem, counts, lowers);
        bool zeroBased = a.Rank == 1 && a.GetLowerBound(0) == 0;
        byte* data = Data(psa);
        int cb = (int)ElementSize(psa);
        int total = a.Length;
        var idx = new int[rank];
        for (int k = 0; k < total; k++)
        {
            object? v = ReadElement(data + (long)k * cb, vt);
            v = ConvertElement(v, elem);
            if (rank == 1) a.SetValue(v, zeroBased ? k : lowers[0] + k);
            else
            {
                int rem = k;
                for (int d = 0; d < rank; d++) { idx[d] = lowers[d] + rem % Math.Max(1, counts[d]); rem /= Math.Max(1, counts[d]); }
                a.SetValue(v, idx);
            }
        }
        return a;
    }

    /// <summary>A SAFEARRAY the callee returned (the caller owns it): its array, and the SAFEARRAY destroyed.</summary>
    public static object? TakeSafeArray(IntPtr psa, RuntimeTypeHandle arrayType)
    {
        try { return FromSafeArray(psa, arrayType); }
        finally { DestroySafeArray(psa); }
    }

    /// <summary>SafeArrayDestroy: the elements let go of (BSTRs freed, interfaces released, VARIANTs
    /// cleared), the data and the descriptor freed.</summary>
    public static void DestroySafeArray(IntPtr psa)
    {
        if (psa == IntPtr.Zero) return;
        if (OleAut) { SafeArrayDestroy(psa); return; }
        ushort vt = SafeArrayVartype(psa);
        byte* data = Data(psa);
        if (data != null)
        {
            int cb = (int)ElementSize(psa);
            long total = 1;
            for (int d = 0; d < Dims(psa); d++) total *= Bound(psa, d).Count;
            for (long k = 0; k < total; k++) ClearElement(data + k * cb, vt);
            NativeMemory.Free(data);
        }
        NativeMemory.Free((byte*)psa - 16);
    }

    /// <summary>The VARTYPE of a SAFEARRAY's elements (SafeArrayGetVartype).</summary>
    private static ushort SafeArrayVartype(IntPtr psa)
    {
        ushort f = Features(psa);
        if ((f & FADF_HAVEVARTYPE) != 0) return (ushort)*(int*)((byte*)psa - 4);
        if ((f & FADF_BSTR) != 0) return VT_BSTR;
        if ((f & FADF_UNKNOWN) != 0) return VT_UNKNOWN;
        if ((f & FADF_DISPATCH) != 0) return VT_DISPATCH;
        if ((f & FADF_VARIANT) != 0) return VT_VARIANT;
        if ((f & FADF_RECORD) != 0) throw Unsupported("SAFEARRAY", "an array of records (VT_RECORD)");
        // an array made without its type (SafeArrayAllocDescriptor): by the element size
        return ElementSize(psa) switch { 1 => VT_UI1, 2 => VT_I2, 4 => VT_I4, 8 => VT_R8, _ => throw Unsupported("SAFEARRAY", $"an array of {ElementSize(psa)}-byte elements without a VARTYPE") };
    }

    private static IntPtr CreateSafeArray(ushort vt, int[] counts, int[] lowers)
    {
        int rank = counts.Length;
        if (OleAut)
        {
            // SafeArrayCreate takes the bounds leftmost first (it stores them reversed)
            var b = stackalloc int[2 * rank];
            for (int d = 0; d < rank; d++) { b[2 * d] = counts[d]; b[2 * d + 1] = lowers[d]; }
            IntPtr p = SafeArrayCreate(vt, (uint)rank, b);
            if (p == IntPtr.Zero) throw new OutOfMemoryException($"SafeArrayCreate of VARTYPE {vt} failed");
            return p;
        }
        int cb = ElementSizeOf(vt);
        byte* block = (byte*)NativeMemory.AllocZeroed((nuint)(16 + BoundsOffset + 8 * rank));
        IntPtr psa = (IntPtr)(block + 16);
        *(int*)(block + 12) = vt;
        *(ushort*)psa = (ushort)rank;
        *(ushort*)(block + 18) = (ushort)(FADF_HAVEVARTYPE | vt switch { VT_BSTR => FADF_BSTR, VT_UNKNOWN => FADF_UNKNOWN, VT_DISPATCH => FADF_DISPATCH, VT_VARIANT => FADF_VARIANT, _ => 0 });
        *(uint*)(block + 20) = (uint)cb;
        long total = 1;
        for (int d = 0; d < rank; d++)
        {
            byte* bd = (byte*)psa + BoundsOffset + 8 * (rank - 1 - d);
            *(uint*)bd = (uint)counts[d];
            *(int*)(bd + 4) = lowers[d];
            total *= counts[d];
        }
        *(byte**)((byte*)psa + PvDataOffset) = (byte*)NativeMemory.AllocZeroed((nuint)Math.Max(1, total * cb));
        return psa;
    }

    private static int ElementSizeOf(ushort vt) => vt switch
    {
        VT_I1 or VT_UI1 => 1,
        VT_I2 or VT_UI2 or VT_BOOL => 2,
        VT_I4 or VT_UI4 or VT_INT or VT_UINT or VT_R4 or VT_ERROR => 4,
        VT_I8 or VT_UI8 or VT_R8 or VT_CY or VT_DATE => 8,
        VT_BSTR or VT_UNKNOWN or VT_DISPATCH => IntPtr.Size,
        VT_DECIMAL => 16,
        VT_VARIANT => sizeof(ComVariant),
        _ => throw Unsupported("SAFEARRAY", $"elements of VARTYPE {vt}"),
    };

    /// <summary>Built-in COM interop's element VARTYPE for a managed element type.</summary>
    private static ushort VtOfElement(Type t)
    {
        if (t.IsEnum) t = Enum.GetUnderlyingType(t);
        return Type.GetTypeCode(t) switch
        {
            TypeCode.SByte => VT_I1, TypeCode.Byte => VT_UI1, TypeCode.Int16 => VT_I2, TypeCode.UInt16 => VT_UI2,
            TypeCode.Int32 => VT_I4, TypeCode.UInt32 => VT_UI4, TypeCode.Int64 => VT_I8, TypeCode.UInt64 => VT_UI8,
            TypeCode.Single => VT_R4, TypeCode.Double => VT_R8, TypeCode.Boolean => VT_BOOL, TypeCode.Char => VT_UI2,
            TypeCode.String => VT_BSTR, TypeCode.Decimal => VT_DECIMAL, TypeCode.DateTime => VT_DATE,
            _ => t == typeof(object) ? VT_VARIANT
                : t == typeof(IntPtr) || t == typeof(UIntPtr) ? (IntPtr.Size == 8 ? VT_I8 : VT_I4)
                : t.IsInterface || t.IsClass ? VT_UNKNOWN
                : throw Unsupported("SAFEARRAY", $"an array of {t} (a VT_RECORD)"),
        };
    }

    /// <summary>The managed element type built-in COM interop gives a VARTYPE (for a System.Array).</summary>
    private static Type TypeOfVt(ushort vt) => vt switch
    {
        VT_I1 => typeof(sbyte), VT_UI1 => typeof(byte), VT_I2 => typeof(short), VT_UI2 => typeof(ushort),
        VT_I4 or VT_INT or VT_ERROR => typeof(int), VT_UI4 or VT_UINT => typeof(uint), VT_I8 => typeof(long), VT_UI8 => typeof(ulong),
        VT_R4 => typeof(float), VT_R8 => typeof(double), VT_BOOL => typeof(bool), VT_BSTR => typeof(string),
        VT_CY or VT_DECIMAL => typeof(decimal), VT_DATE => typeof(DateTime), VT_VARIANT or VT_UNKNOWN or VT_DISPATCH => typeof(object),
        _ => throw Unsupported("SAFEARRAY", $"elements of VARTYPE {vt}"),
    };

    private static object? ConvertElement(object? v, Type elem)
    {
        if (v == null || elem.IsInstanceOfType(v)) return v;
        if (elem.IsEnum) return Enum.ToObject(elem, v);
        if (elem == typeof(char) && v is ushort u) return (char)u;
        if (elem.IsInterface || elem == typeof(object)) return v;        // the cast is SetValue's
        return Convert.ChangeType(v, elem, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void WriteElement(byte* p, ushort vt, object? o)
    {
        if (o is Enum e) o = Convert.ChangeType(e, Enum.GetUnderlyingType(e.GetType()));
        switch (vt)
        {
            case VT_I1: *(sbyte*)p = Convert.ToSByte(o); break;
            case VT_UI1: *p = Convert.ToByte(o); break;
            case VT_I2: *(short*)p = Convert.ToInt16(o); break;
            case VT_UI2: *(ushort*)p = o is char c ? c : Convert.ToUInt16(o); break;
            case VT_I4: case VT_INT: case VT_ERROR: *(int*)p = Convert.ToInt32(o); break;
            case VT_UI4: case VT_UINT: *(uint*)p = Convert.ToUInt32(o); break;
            case VT_I8: *(long*)p = o is IntPtr ip ? ip : Convert.ToInt64(o); break;
            case VT_UI8: *(ulong*)p = Convert.ToUInt64(o); break;
            case VT_R4: *(float*)p = Convert.ToSingle(o); break;
            case VT_R8: *(double*)p = Convert.ToDouble(o); break;
            case VT_BOOL: *(short*)p = (short)((o is bool b ? b : Convert.ToBoolean(o)) ? -1 : 0); break;
            case VT_CY: *(long*)p = decimal.ToOACurrency(Convert.ToDecimal(o)); break;
            case VT_DATE: *(double*)p = ((DateTime)o!).ToOADate(); break;
            case VT_BSTR: *(IntPtr*)p = o == null ? IntPtr.Zero : Marshal.StringToBSTR((string)o); break;
            case VT_UNKNOWN: *(IntPtr*)p = BorrowUnknown(o); break;
            case VT_DISPATCH: *(IntPtr*)p = o == null ? IntPtr.Zero : PointerFor(o, IID_IDispatch); break;
            case VT_VARIANT: ToVariant(o, (IntPtr)p); break;
            case VT_DECIMAL: WriteDecimal(p, Convert.ToDecimal(o)); break;
            default: throw Unsupported("SAFEARRAY", $"elements of VARTYPE {vt}");
        }
    }

    private static object? ReadElement(byte* p, ushort vt) => vt switch
    {
        VT_I1 => *(sbyte*)p, VT_UI1 => *p, VT_I2 => *(short*)p, VT_UI2 => *(ushort*)p,
        VT_I4 or VT_INT or VT_ERROR => *(int*)p, VT_UI4 or VT_UINT => *(uint*)p,
        VT_I8 => *(long*)p, VT_UI8 => *(ulong*)p, VT_R4 => *(float*)p, VT_R8 => *(double*)p,
        VT_BOOL => *(short*)p != 0,
        VT_CY => decimal.FromOACurrency(*(long*)p),
        VT_DATE => DateTime.FromOADate(*(double*)p),
        VT_BSTR => *(IntPtr*)p == IntPtr.Zero ? null : Marshal.PtrToStringBSTR(*(IntPtr*)p),
        VT_UNKNOWN or VT_DISPATCH => *(IntPtr*)p == IntPtr.Zero ? null : ComObject.Get(*(IntPtr*)p),
        VT_VARIANT => FromVariant((IntPtr)p),
        VT_DECIMAL => ReadDecimal(p),
        _ => throw Unsupported("SAFEARRAY", $"elements of VARTYPE {vt}"),
    };

    private static void ClearElement(byte* p, ushort vt)
    {
        switch (vt)
        {
            case VT_BSTR: if (*(IntPtr*)p != IntPtr.Zero) Marshal.FreeBSTR(*(IntPtr*)p); *(IntPtr*)p = IntPtr.Zero; break;
            case VT_UNKNOWN: case VT_DISPATCH: if (*(IntPtr*)p != IntPtr.Zero) Marshal.Release(*(IntPtr*)p); *(IntPtr*)p = IntPtr.Zero; break;
            case VT_VARIANT: ClearVariant((IntPtr)p); break;
        }
    }

    /// <summary>A DECIMAL (wReserved, scale, sign, Hi32, Lo64); wReserved left as it is (a VARIANT's type).</summary>
    private static void WriteDecimal(byte* p, decimal m)
    {
        int[] bits = decimal.GetBits(m);
        *(ushort*)(p + 2) = (ushort)(((bits[3] >> 16) & 0xFF) | (bits[3] < 0 ? 0x8000 : 0));
        *(int*)(p + 4) = bits[2];
        *(long*)(p + 8) = (uint)bits[0] | ((long)bits[1] << 32);
    }

    private static decimal ReadDecimal(byte* p)
    {
        ushort signScale = *(ushort*)(p + 2);
        long lo = *(long*)(p + 8);
        return new decimal((int)lo, (int)(lo >> 32), *(int*)(p + 4), (signScale & 0x8000) != 0, (byte)(signScale & 0xFF));
    }

    [DllImport("oleaut32.dll")]
    private static extern IntPtr SafeArrayCreate(ushort vt, uint cDims, int* rgsabound);

    [DllImport("oleaut32.dll")]
    private static extern int SafeArrayDestroy(IntPtr psa);
}
