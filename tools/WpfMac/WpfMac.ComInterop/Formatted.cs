// Formatted types, custom marshalers and StringBuilder for rewritten COM calls
// (Documentation/comimport-rewriter.md), as built-in interop marshals them in a COM interface method:
//
// - a class with sequential or explicit layout (a "formatted class") goes as a pointer to its native
//   layout, copied in and, when it is [Out] or blittable (built-in interop pins a blittable one, so the
//   callee's writes show), copied back into the same object; a struct by reference or [MarshalAs(LPStruct)]
//   likewise -- through Marshal.StructureToPtr/PtrToStructure, which convert strings and nested structs
//   with no COM;
// - a C array (LPArray) of such structs: element by element into one buffer;
// - [MarshalAs(UnmanagedType.CustomMarshaler)]: the app's ICustomMarshaler, called in built-in interop's
//   order (ManagedToNative, the call, NativeToManaged for an out, CleanUpNativeData);
// - StringBuilder: a buffer of its capacity, the callee's string read back into it.

using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace WpfMac.ComInterop;

public static unsafe partial class Com
{
    // ---- formatted classes and structs ------------------------------------------------------------

    /// <summary>A formatted object's native layout in a new buffer (null for null). Free it with
    /// <see cref="FreeStruct"/>.</summary>
    public static IntPtr StructToNative(object? o, RuntimeTypeHandle type)
    {
        if (o == null) return IntPtr.Zero;
        Type t = Type.GetTypeFromHandle(type)!;
        IntPtr p = Marshal.AllocCoTaskMem(Math.Max(1, Marshal.SizeOf(t)));
        Marshal.StructureToPtr(o, p, fDeleteOld: false);
        return p;
    }

    /// <summary>A zeroed buffer for an out-only struct of <paramref name="type"/>.</summary>
    public static IntPtr NewStruct(RuntimeTypeHandle type)
    {
        Type t = Type.GetTypeFromHandle(type)!;
        int n = Math.Max(1, Marshal.SizeOf(t));
        IntPtr p = Marshal.AllocCoTaskMem(n);
        new Span<byte>((void*)p, n).Clear();
        return p;
    }

    /// <summary>The native layout at <paramref name="p"/> back: into <paramref name="target"/> when it is a
    /// class instance (the same object, as built-in interop fills it), else a new value. Null for null.</summary>
    public static object? StructFromNative(IntPtr p, object? target, RuntimeTypeHandle type)
    {
        if (p == IntPtr.Zero) return target;
        Type t = Type.GetTypeFromHandle(type)!;
        if (target != null && !t.IsValueType) { Marshal.PtrToStructure(p, target); return target; }
        return Marshal.PtrToStructure(p, t);
    }

    /// <summary>What <see cref="StructToNative"/> made: its strings and nested data, and the buffer.</summary>
    public static void FreeStruct(IntPtr p, RuntimeTypeHandle type)
    {
        if (p == IntPtr.Zero) return;
        Marshal.DestroyStructure(p, Type.GetTypeFromHandle(type)!);
        Marshal.FreeCoTaskMem(p);
    }

    /// <summary>For a CCW: writes <paramref name="o"/> over the caller's buffer, the old contents' strings freed.</summary>
    public static void StructBackToNative(object? o, IntPtr p, RuntimeTypeHandle type)
    {
        if (o == null || p == IntPtr.Zero) return;
        Marshal.StructureToPtr(o, p, fDeleteOld: true);
    }

    // ---- C arrays of formatted structs ------------------------------------------------------------------

    /// <summary>An array of formatted elements as one native buffer of their layouts (null for null).</summary>
    public static IntPtr StructArrayToNative(Array? a, RuntimeTypeHandle element)
    {
        if (a == null) return IntPtr.Zero;
        Type t = Type.GetTypeFromHandle(element)!;
        int size = Marshal.SizeOf(t);
        IntPtr p = Marshal.AllocCoTaskMem(Math.Max(1, size * a.Length));
        for (int i = 0; i < a.Length; i++)
        {
            object? e = a.GetValue(i);
            if (e != null) Marshal.StructureToPtr(e, p + i * size, fDeleteOld: false);
            else new Span<byte>((void*)(p + i * size), size).Clear();
        }
        return p;
    }

    /// <summary>The native elements back into the array (an [Out] array: the callee's writes).</summary>
    public static void StructArrayFromNative(IntPtr p, Array? a, RuntimeTypeHandle element)
    {
        if (p == IntPtr.Zero || a == null) return;
        Type t = Type.GetTypeFromHandle(element)!;
        int size = Marshal.SizeOf(t);
        for (int i = 0; i < a.Length; i++)
        {
            object? e = a.GetValue(i);
            if (e != null && !t.IsValueType) Marshal.PtrToStructure(p + i * size, e);
            else a.SetValue(Marshal.PtrToStructure(p + i * size, t), i);
        }
    }

    public static void FreeStructArray(IntPtr p, int length, RuntimeTypeHandle element)
    {
        if (p == IntPtr.Zero) return;
        Type t = Type.GetTypeFromHandle(element)!;
        int size = Marshal.SizeOf(t);
        for (int i = 0; i < length; i++) Marshal.DestroyStructure(p + i * size, t);
        Marshal.FreeCoTaskMem(p);
    }

    /// <summary>An array's length, 0 for null (for the IL that frees a struct array).</summary>
    public static int LengthOf(Array? a) => a?.Length ?? 0;

    // ---- custom marshalers ----------------------------------------------------------------------------

    private static readonly ConcurrentDictionary<(Type, string), ICustomMarshaler> s_marshalers = new();

    /// <summary>The ICustomMarshaler named by [MarshalAs(UnmanagedType.CustomMarshaler, MarshalTypeRef = M,
    /// MarshalCookie = c)]: M.GetInstance(c), once per type and cookie, as built-in interop keeps it.</summary>
    public static object CustomMarshaler(RuntimeTypeHandle marshaler, string? cookie) =>
        s_marshalers.GetOrAdd((Type.GetTypeFromHandle(marshaler)!, cookie ?? ""), k =>
        {
            var get = k.Item1.GetMethod("GetInstance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, new[] { typeof(string) })
                ?? throw new MissingMethodException(k.Item1.FullName, "GetInstance");
            return (ICustomMarshaler)get.Invoke(null, new object[] { k.Item2 })!;
        });

    public static IntPtr CustomToNative(object marshaler, object? o) => o == null ? IntPtr.Zero : ((ICustomMarshaler)marshaler).MarshalManagedToNative(o);

    public static object? CustomFromNative(object marshaler, IntPtr p) => ((ICustomMarshaler)marshaler).MarshalNativeToManaged(p);

    public static void CustomCleanUp(object marshaler, IntPtr p) { if (p != IntPtr.Zero) ((ICustomMarshaler)marshaler).CleanUpNativeData(p); }

    // ---- StringBuilder -------------------------------------------------------------------------------

    /// <summary>A StringBuilder as a native buffer of its capacity (+1 for the terminator), its contents
    /// copied in unless <paramref name="outOnly"/>; UTF-16, or ANSI when <paramref name="ansi"/>. Null for null.</summary>
    public static IntPtr StringBuilderToNative(StringBuilder? sb, bool ansi, bool outOnly)
    {
        if (sb == null) return IntPtr.Zero;
        int cap = Math.Max(sb.Capacity, sb.Length) + 1;
        int unit = ansi ? 1 : 2;
        IntPtr p = Marshal.AllocCoTaskMem(cap * unit + unit);
        new Span<byte>((void*)p, cap * unit + unit).Clear();
        if (!outOnly && sb.Length > 0)
        {
            string s = sb.ToString();
            if (ansi) { byte[] b = Encoding.Default.GetBytes(s); b.AsSpan(0, Math.Min(b.Length, cap - 1)).CopyTo(new Span<byte>((void*)p, cap)); }
            else s.AsSpan(0, Math.Min(s.Length, cap - 1)).CopyTo(new Span<char>((void*)p, cap));
        }
        return p;
    }

    /// <summary>The callee's string (up to its terminator, or the buffer's end) back into the StringBuilder.</summary>
    public static void StringBuilderFromNative(StringBuilder? sb, IntPtr p, bool ansi)
    {
        if (sb == null || p == IntPtr.Zero) return;
        int cap = Math.Max(sb.Capacity, sb.Length) + 1;
        string s;
        if (ansi)
        {
            var span = new ReadOnlySpan<byte>((void*)p, cap);
            int n = span.IndexOf((byte)0); if (n < 0) n = cap;
            s = Encoding.Default.GetString(span[..n]);
        }
        else
        {
            var span = new ReadOnlySpan<char>((void*)p, cap);
            int n = span.IndexOf('\0'); if (n < 0) n = cap;
            s = new string(span[..n]);
        }
        sb.Clear().Append(s);
    }

    public static void FreeCoTaskMem(IntPtr p) { if (p != IntPtr.Zero) Marshal.FreeCoTaskMem(p); }
}
