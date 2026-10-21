// C arrays (LPArray) of strings, interfaces and VARIANTs for rewritten COM calls, and the arrays a CCW
// receives from native code with their length in another parameter (SizeParamIndex/SizeConst)
// (Documentation/comimport-rewriter.md). Element kinds, as the rewriter passes them:
//   1 LPWStr, 2 LPStr, 3 BSTR, 4 an interface (its IID; IUnknown for object), 5 VARIANT, 6 IDispatch.
// Native strings are CoTaskMem (BSTRs SysAllocString), interfaces AddRef'd, VARIANTs cleared -- as built-in
// interop allocates and frees them.

using System.Runtime.InteropServices;
using System.Text;

namespace WpfWebGpu.ComInterop;

public static unsafe partial class Com
{
    public const int ElemLPWStr = 1, ElemLPStr = 2, ElemBStr = 3, ElemIface = 4, ElemVariant = 5, ElemDispatch = 6;

    private static int ElemSize(int kind) => kind == ElemVariant ? sizeof(ComVariant) : IntPtr.Size;

    /// <summary>An array as a native buffer of its elements' native forms (nothing converted in when
    /// <paramref name="outOnly"/>). Null for null.</summary>
    public static IntPtr ElemArrayToNative(Array? a, int kind, RuntimeTypeHandle element, bool outOnly)
    {
        if (a == null) return IntPtr.Zero;
        int size = ElemSize(kind);
        byte* p = (byte*)Marshal.AllocCoTaskMem(Math.Max(1, size * a.Length));
        new Span<byte>(p, Math.Max(1, size * a.Length)).Clear();
        if (!outOnly)
            for (int i = 0; i < a.Length; i++) WriteElem(p + i * size, kind, element, a.GetValue(i));
        return (IntPtr)p;
    }

    private static void WriteElem(byte* slot, int kind, RuntimeTypeHandle element, object? v)
    {
        switch (kind)
        {
            case ElemLPWStr: *(IntPtr*)slot = v == null ? IntPtr.Zero : Marshal.StringToCoTaskMemUni((string)v); break;
            case ElemLPStr: *(IntPtr*)slot = v == null ? IntPtr.Zero : Marshal.StringToCoTaskMemAnsi((string)v); break;
            case ElemBStr: *(IntPtr*)slot = ToBstr((string?)v); break;
            case ElemIface:
                Type t = Type.GetTypeFromHandle(element)!;
                *(IntPtr*)slot = v == null ? IntPtr.Zero : t == typeof(object) ? BorrowUnknown(v) : Borrow(v, element);
                break;
            case ElemDispatch: *(IntPtr*)slot = BorrowDispatch(v); break;
            case ElemVariant: ToVariant(v, (IntPtr)slot); break;
            default: throw Unsupported("C array", $"element kind {kind}");
        }
    }

    private static object? ReadElem(byte* slot, int kind, RuntimeTypeHandle element) => kind switch
    {
        ElemLPWStr => StringOfUni(*(IntPtr*)slot),
        ElemLPStr => StringOfAnsi(*(IntPtr*)slot),
        ElemBStr => StringOfBstr(*(IntPtr*)slot),
        ElemIface or ElemDispatch => Wrap(*(IntPtr*)slot),
        ElemVariant => FromVariant((IntPtr)slot),
        _ => throw Unsupported("C array", $"element kind {kind}"),
    };

    private static void FreeElem(byte* slot, int kind)
    {
        switch (kind)
        {
            case ElemLPWStr: case ElemLPStr: if (*(IntPtr*)slot != IntPtr.Zero) Marshal.FreeCoTaskMem(*(IntPtr*)slot); break;
            case ElemBStr: FreeBstr(*(IntPtr*)slot); break;
            case ElemIface: case ElemDispatch: Release(*(IntPtr*)slot); break;
            case ElemVariant: ClearVariant((IntPtr)slot); return;
        }
        *(IntPtr*)slot = IntPtr.Zero;
    }

    /// <summary>An [Out] array's elements back from the native buffer (the buffer keeps them: <see cref="FreeElemArray"/>).</summary>
    public static void ElemArrayFromNative(IntPtr p, Array? a, int kind, RuntimeTypeHandle element)
    {
        if (p == IntPtr.Zero || a == null) return;
        int size = ElemSize(kind);
        Type t = Type.GetTypeFromHandle(element)!;
        for (int i = 0; i < a.Length; i++)
        {
            object? v = ReadElem((byte*)p + i * size, kind, element);
            a.SetValue(v == null || t.IsInstanceOfType(v) || t.IsInterface ? v : ConvertTo(v, t), i);
        }
    }

    /// <summary>The buffer's elements let go of, and the buffer.</summary>
    public static void FreeElemArray(IntPtr p, int length, int kind)
    {
        if (p == IntPtr.Zero) return;
        int size = ElemSize(kind);
        for (int i = 0; i < length; i++) FreeElem((byte*)p + i * size, kind);
        Marshal.FreeCoTaskMem(p);
    }

    // ---- a CCW's arrays: the length from another parameter -------------------------------------

    /// <summary>A blittable array native code passed (<paramref name="n"/> elements): a managed copy.</summary>
    public static Array? BlitArrayFromNative(IntPtr p, int n, RuntimeTypeHandle arrayType)
    {
        Type t = Type.GetTypeFromHandle(arrayType)!;
        if (p == IntPtr.Zero) return null;
        var a = Array.CreateInstance(t.GetElementType()!, Math.Max(0, n));
        int bytes = BlitSize(t.GetElementType()!) * a.Length;
        if (bytes > 0) fixed (byte* d = &MemoryMarshal.GetArrayDataReference(a)) Buffer.MemoryCopy((void*)p, d, bytes, bytes);
        return a;
    }

    /// <summary>A blittable array's contents written back to native code's buffer (at most <paramref name="n"/> elements).</summary>
    public static void BlitArrayToNative(Array? a, IntPtr p, int n)
    {
        if (a == null || p == IntPtr.Zero) return;
        int elem = BlitSize(a.GetType().GetElementType()!);
        int bytes = elem * Math.Min(Math.Max(0, n), a.Length);
        if (bytes > 0) fixed (byte* s = &MemoryMarshal.GetArrayDataReference(a)) Buffer.MemoryCopy(s, (void*)p, bytes, bytes);
    }

    /// <summary>A blittable element's size (an enum's underlying type's; a char is UTF-16).</summary>
    private static int BlitSize(Type e) => e.IsEnum ? BlitSize(Enum.GetUnderlyingType(e)) : e == typeof(char) ? 2 : e == typeof(bool) ? 1
        : e.IsPrimitive ? Buffer.ByteLength(Array.CreateInstance(e, 1)) : Marshal.SizeOf(e);

    /// <summary>Native elements (<paramref name="n"/>) as a managed array; the buffer stays the caller's.</summary>
    public static Array? ElemArrayFromNativeN(IntPtr p, int n, int kind, RuntimeTypeHandle arrayType, bool outOnly)
    {
        if (p == IntPtr.Zero) return null;
        Type t = Type.GetTypeFromHandle(arrayType)!;
        var a = Array.CreateInstance(t.GetElementType()!, Math.Max(0, n));
        if (!outOnly) ElemArrayFromNative(p, a, kind, t.GetElementType()!.TypeHandle);
        return a;
    }

    /// <summary>A callee-allocated array (a P/Invoke's [Out] out T[] with its length in another parameter): its
    /// <paramref name="n"/> elements as a managed array, then the elements let go of and the buffer freed, as
    /// built-in interop does.</summary>
    public static Array? TakeElemArray(IntPtr p, int n, int kind, RuntimeTypeHandle arrayType)
    {
        if (p == IntPtr.Zero) return null;
        try { return ElemArrayFromNativeN(p, n, kind, arrayType, outOnly: false); }
        finally { FreeElemArray(p, Math.Max(0, n), kind); }
    }

    /// <summary>A CCW's [Out] array back into native code's buffer: the old elements let go of, the new written.</summary>
    public static void ElemArrayBackToNative(Array? a, IntPtr p, int n, int kind, RuntimeTypeHandle arrayType)
    {
        if (a == null || p == IntPtr.Zero) return;
        var element = Type.GetTypeFromHandle(arrayType)!.GetElementType()!.TypeHandle;
        int size = ElemSize(kind);
        for (int i = 0; i < Math.Min(Math.Max(0, n), a.Length); i++)
        {
            FreeElem((byte*)p + i * size, kind);
            WriteElem((byte*)p + i * size, kind, element, a.GetValue(i));
        }
    }

    /// <summary>Formatted elements (<paramref name="n"/>) native code passed, as a managed array.</summary>
    public static Array? StructArrayFromNativeN(IntPtr p, int n, RuntimeTypeHandle arrayType, bool outOnly)
    {
        if (p == IntPtr.Zero) return null;
        Type t = Type.GetTypeFromHandle(arrayType)!.GetElementType()!;
        var a = Array.CreateInstance(t, Math.Max(0, n));
        if (outOnly) { if (!t.IsValueType) for (int i = 0; i < a.Length; i++) a.SetValue(Activator.CreateInstance(t), i); }
        else StructArrayFromNative(p, a, t.TypeHandle);
        return a;
    }

    /// <summary>A CCW's [Out] struct array back into native code's buffer (the old elements' strings freed).</summary>
    public static void StructArrayBackToNative(Array? a, IntPtr p, int n)
    {
        if (a == null || p == IntPtr.Zero) return;
        Type t = a.GetType().GetElementType()!;
        int size = Marshal.SizeOf(t);
        for (int i = 0; i < Math.Min(Math.Max(0, n), a.Length); i++)
            if (a.GetValue(i) is { } e) WriteStruct(e, p + i * size, t, deleteOld: true);
    }

    // ---- a CCW's StringBuilder ---------------------------------------------------------------

    /// <summary>A StringBuilder over native code's buffer, as built-in reverse interop makes it: its current
    /// string (to the terminator); the buffer's own size is not known.</summary>
    public static StringBuilder? StringBuilderOfNative(IntPtr p, bool ansi)
    {
        if (p == IntPtr.Zero) return null;
        string s = (ansi ? Marshal.PtrToStringAnsi(p) : Marshal.PtrToStringUni(p)) ?? "";
        return new StringBuilder(s, Math.Max(s.Length, 16));
    }

    /// <summary>The StringBuilder back into native code's buffer: no longer than the string that was there
    /// (the buffer's size is unknown), terminated.</summary>
    public static void StringBuilderBackToNative(StringBuilder? sb, IntPtr p, bool ansi, int bufferChars)
    {
        if (sb == null || p == IntPtr.Zero) return;
        string s = sb.ToString();
        int room = Math.Max(0, bufferChars - 1);
        if (s.Length > room) s = s[..room];
        if (ansi) { byte[] b = Encoding.Default.GetBytes(s); int n = Math.Min(b.Length, room); b.AsSpan(0, n).CopyTo(new Span<byte>((void*)p, n)); ((byte*)p)[n] = 0; }
        else { s.AsSpan().CopyTo(new Span<char>((void*)p, s.Length)); ((char*)p)[s.Length] = '\0'; }
    }

    /// <summary>The characters a native string buffer holds now, with its terminator (the room a CCW's
    /// StringBuilder may write back into when the buffer's size is not passed).</summary>
    public static int NativeStringRoom(IntPtr p, bool ansi) =>
        p == IntPtr.Zero ? 0 : 1 + (ansi ? (Marshal.PtrToStringAnsi(p)?.Length ?? 0) : (Marshal.PtrToStringUni(p)?.Length ?? 0));
}
