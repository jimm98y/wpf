// Formatted types, custom marshalers and StringBuilder for rewritten COM calls
// (Documentation/comimport-rewriter.md), as built-in interop marshals them in a COM interface method:
//
// - a class with sequential or explicit layout (a "formatted class") goes as a pointer to its native
//   layout, copied in and, when it is [Out] or blittable (built-in interop pins a blittable one, so the
//   callee's writes show), copied back into the same object; a struct by reference or [MarshalAs(LPStruct)]
//   likewise -- through Marshal.StructureToPtr/PtrToStructure, which convert strings and nested structs
//   with no COM (a type with interface fields through its twin, StructTwins.cs);
// - a C array (LPArray) of such structs: element by element into one buffer;
// - [MarshalAs(UnmanagedType.CustomMarshaler)]: the app's ICustomMarshaler, called in built-in interop's
//   order (ManagedToNative, the call, NativeToManaged for an out, CleanUpNativeData);
// - StringBuilder: a buffer of its capacity, the callee's string read back into it.

using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace WpfWebGpu.ComInterop;

public static unsafe partial class Com
{
    // ---- formatted classes and structs ------------------------------------------------------------

    /// <summary>A formatted object's native layout in a new buffer (null for null). Free it with
    /// <see cref="FreeStruct"/>.</summary>
    public static IntPtr StructToNative(object? o, RuntimeTypeHandle type)
    {
        if (o == null) return IntPtr.Zero;
        Type t = Type.GetTypeFromHandle(type)!;
        IntPtr p = Marshal.AllocCoTaskMem(Math.Max(1, NativeSizeOf(t)));
        WriteStruct(o, p, t, deleteOld: false);
        return p;
    }

    // A type with interface fields (an interface, or object as IUnknown/IDispatch) goes through its twin
    // (StructTwins.cs): Marshal lays it out with pointer slots, filled and read here, not with built-in
    // CCWs and RCWs -- which off Windows it cannot make at all.

    private static void WriteStruct(object o, IntPtr p, Type t, bool deleteOld)
    {
        if (TwinOf(t) is not { } twin)
        {
            var slots = DelegateSlots(t);
            if (slots.Length == 0) { Marshal.StructureToPtr(o, p, deleteOld); return; }
            // the struct without its delegates, then each delegate's pointer in its slot
            var saved = new object?[slots.Length];
            for (int i = 0; i < slots.Length; i++) { saved[i] = slots[i].Field.GetValue(o); slots[i].Field.SetValue(o, null); }
            try { Marshal.StructureToPtr(o, p, deleteOld); }
            finally { for (int i = 0; i < slots.Length; i++) slots[i].Field.SetValue(o, saved[i]); }
            for (int i = 0; i < slots.Length; i++) Marshal.WriteIntPtr(p, slots[i].Offset, Callbacks.ToNative((Delegate?)saved[i]));
            return;
        }
        if (deleteOld) ReleaseInterfaces(p, twin);
        Marshal.StructureToPtr(ToTwin(o, twin), p, deleteOld);
    }

    private static object? ReadStruct(IntPtr p, object? target, Type t)
    {
        if (TwinOf(t) is not { } twin)
        {
            var slots = DelegateSlots(t);
            if (slots.Length > 0) return ReadWithDelegates(p, target, t, slots);
            if (target != null && !t.IsValueType) { Marshal.PtrToStructure(p, target); return target; }
            return Marshal.PtrToStructure(p, t);
        }
        return FromTwin(Marshal.PtrToStructure(p, twin.Type)!, target != null && !t.IsValueType ? target : null, t, twin);
    }

    // ---- delegate fields ----
    //
    // A delegate field is a function pointer in the native layout (a window class's lpfnWndProc). Marshal
    // makes one with the runtime's thunk, which Mono on WebAssembly cannot make for an app's delegate type
    // ("No native to managed transition": Telerik's WindowChrome registering its message window, and
    // Workbench did not start). So such a struct goes over without its delegates and each slot gets the
    // pointer Callbacks makes -- the delegate's token in the browser, the runtime's thunk elsewhere; read
    // back, a slot's pointer is that delegate again. Every system goes this way.

    private static readonly ConcurrentDictionary<Type, (FieldInfo Field, int Offset)[]> s_delegateSlots = new();

    private static (FieldInfo Field, int Offset)[] DelegateSlots(Type t)
        => s_delegateSlots.GetOrAdd(t, static t => t.IsPrimitive || t.IsEnum || t.IsArray || t == typeof(string)
            ? Array.Empty<(FieldInfo, int)>()
            : t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(f => typeof(Delegate).IsAssignableFrom(f.FieldType))
                .Select(f => (f, (int)Marshal.OffsetOf(f.DeclaringType!, f.Name)))
                .ToArray());

    private static object ReadWithDelegates(IntPtr p, object? target, Type t, (FieldInfo Field, int Offset)[] slots)
    {
        // a copy with the slots cleared, so that Marshal has no function pointer to make a delegate of
        int n = Marshal.SizeOf(t);
        IntPtr copy = Marshal.AllocCoTaskMem(n);
        try
        {
            Buffer.MemoryCopy((void*)p, (void*)copy, n, n);
            foreach (var (_, offset) in slots) Marshal.WriteIntPtr(copy, offset, IntPtr.Zero);
            object r;
            if (target != null && !t.IsValueType) { Marshal.PtrToStructure(copy, target); r = target; }
            else r = Marshal.PtrToStructure(copy, t)!;
            foreach (var (field, offset) in slots)
            {
                IntPtr fn = Marshal.ReadIntPtr(p, offset);
                field.SetValue(r, fn == IntPtr.Zero ? null : Callbacks.FromNative(fn, field.FieldType));
            }
            return r;
        }
        finally { Marshal.FreeCoTaskMem(copy); }
    }

    /// <summary>A zeroed buffer for an out-only struct of <paramref name="type"/>.</summary>
    public static IntPtr NewStruct(RuntimeTypeHandle type)
    {
        Type t = Type.GetTypeFromHandle(type)!;
        int n = Math.Max(1, NativeSizeOf(t));
        IntPtr p = Marshal.AllocCoTaskMem(n);
        new Span<byte>((void*)p, n).Clear();
        return p;
    }

    /// <summary>The native layout at <paramref name="p"/> back: into <paramref name="target"/> when it is a
    /// class instance (the same object, as built-in interop fills it), else a new value. Null for null.</summary>
    public static object? StructFromNative(IntPtr p, object? target, RuntimeTypeHandle type)
    {
        if (p == IntPtr.Zero) return target;
        return ReadStruct(p, target, Type.GetTypeFromHandle(type)!);
    }

    /// <summary>What <see cref="StructToNative"/> made: its strings and nested data, and the buffer.</summary>
    public static void FreeStruct(IntPtr p, RuntimeTypeHandle type)
    {
        if (p == IntPtr.Zero) return;
        DestroyStruct(p, Type.GetTypeFromHandle(type)!);
        Marshal.FreeCoTaskMem(p);
    }

    /// <summary>For a CCW: writes <paramref name="o"/> over the caller's buffer, the old contents' strings freed.</summary>
    public static void StructBackToNative(object? o, IntPtr p, RuntimeTypeHandle type)
    {
        if (o == null || p == IntPtr.Zero) return;
        WriteStruct(o, p, Type.GetTypeFromHandle(type)!, deleteOld: true);
    }

    /// <summary>DestroyStructure, with interface fields released here.</summary>
    private static void DestroyStruct(IntPtr p, Type t)
    {
        Twin? twin = TwinOf(t);
        if (twin != null) ReleaseInterfaces(p, twin);
        Marshal.DestroyStructure(p, twin?.Type ?? t);
    }

    /// <summary>A struct by value: its native layout into <paramref name="dst"/> (the rewriter's blittable shadow of
    /// the same layout), and what that made freed after the call with <see cref="FreeStructAt"/>.</summary>
    public static void StructToShadow(object? o, IntPtr dst, int shadowSize, RuntimeTypeHandle type)
    {
        Type t = Type.GetTypeFromHandle(type)!;
        CheckShadow(t, shadowSize);
        if (o == null) return;
        WriteStruct(o, dst, t, deleteOld: false);
    }

    /// <summary>The rewriter computed the shadow's size from the metadata; the runtime's own layout must agree, or
    /// the call would pass the wrong bytes.</summary>
    public static void CheckShadow(Type t, int shadowSize)
    {
        int n = NativeSizeOf(t);
        if (n != shadowSize) throw new MarshalDirectiveException($"comimport: {t} is {n} bytes natively, but the rewriter laid it out as {shadowSize}");
    }

    /// <summary>A CCW's by-value struct: its value from the shadow native code passed (which stays the caller's).</summary>
    public static object? StructFromShadow(IntPtr src, int shadowSize, RuntimeTypeHandle type)
    {
        Type t = Type.GetTypeFromHandle(type)!;
        CheckShadow(t, shadowSize);
        return ReadStruct(src, null, t);
    }

    public static void FreeStructAt(IntPtr p, RuntimeTypeHandle type) { if (p != IntPtr.Zero) DestroyStruct(p, Type.GetTypeFromHandle(type)!); }

    // ---- C arrays of formatted structs ------------------------------------------------------------------

    /// <summary>An array of formatted elements as one native buffer of their layouts (null for null).</summary>
    public static IntPtr StructArrayToNative(Array? a, RuntimeTypeHandle element)
    {
        if (a == null) return IntPtr.Zero;
        Type t = Type.GetTypeFromHandle(element)!;
        int size = NativeSizeOf(t);
        IntPtr p = Marshal.AllocCoTaskMem(Math.Max(1, size * a.Length));
        for (int i = 0; i < a.Length; i++)
        {
            object? e = a.GetValue(i);
            if (e != null) WriteStruct(e, p + i * size, t, deleteOld: false);
            else new Span<byte>((void*)(p + i * size), size).Clear();
        }
        return p;
    }

    /// <summary>The native elements back into the array (an [Out] array: the callee's writes).</summary>
    public static void StructArrayFromNative(IntPtr p, Array? a, RuntimeTypeHandle element)
    {
        if (p == IntPtr.Zero || a == null) return;
        Type t = Type.GetTypeFromHandle(element)!;
        int size = NativeSizeOf(t);
        for (int i = 0; i < a.Length; i++)
        {
            object? e = a.GetValue(i);
            object? r = ReadStruct(p + i * size, e != null && !t.IsValueType ? e : null, t);
            if (t.IsValueType || e == null) a.SetValue(r, i);
        }
    }

    public static void FreeStructArray(IntPtr p, int length, RuntimeTypeHandle element)
    {
        if (p == IntPtr.Zero) return;
        Type t = Type.GetTypeFromHandle(element)!;
        int size = NativeSizeOf(t);
        for (int i = 0; i < length; i++) DestroyStruct(p + i * size, t);
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
