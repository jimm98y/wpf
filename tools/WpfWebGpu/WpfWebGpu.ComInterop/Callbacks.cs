// Delegates an app hands to Windows as function pointers -- a window procedure, a hook, a timer or
// enumeration callback -- in the browser. Off Windows the stack's managed Win32 (WpfWebGpu.Win32) is what
// calls them back, and natively the runtime's thunk for the delegate (Marshal.GetFunctionPointerForDelegate)
// is the pointer it calls. Mono on WebAssembly has no such thunks: it can make one only for a signature it
// generated one for at build, which an app's delegate types are not, and asking for any other aborts the
// runtime (memory access out of bounds in get_native_to_interp). Nothing native calls these pointers in
// the browser anyway, so there the pointer is a token of the delegate, the delegate is what is called when
// the stack calls the pointer (Invoke), and a pointer turned back into a delegate is the delegate itself.
//
// As with the runtime's thunk, the token does not keep its delegate alive: an app keeps a delegate it
// handed to native code alive itself, as it must on Windows.

using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;

namespace WpfWebGpu.ComInterop;

public static unsafe class Callbacks
{
    private const long First = 0x7E000010, Step = 0x10;
    private static readonly ConditionalWeakTable<Delegate, object> s_tokens = new();
    private static readonly Dictionary<nint, WeakReference<Delegate>> s_delegates = new();
    private static readonly object s_gate = new();
    private static long s_next = First;

    private static bool Tokens => OperatingSystem.IsBrowser();

    /// <summary>The function pointer native code is handed for <paramref name="d"/>: the runtime's thunk, or
    /// in the browser its token (the same one each time, as the thunk is).</summary>
    public static nint ToNative(Delegate? d)
    {
        if (d == null) return 0;
        if (!Tokens) return Marshal.GetFunctionPointerForDelegate(d);
        lock (s_gate)
        {
            if (s_tokens.TryGetValue(d, out object? t)) return (nint)t;
            nint token = (nint)s_next;
            s_next += Step;
            s_tokens.Add(d, token);
            s_delegates[token] = new WeakReference<Delegate>(d);
            return token;
        }
    }

    /// <summary>The delegate <paramref name="p"/> is the token of; null for any other pointer.</summary>
    public static Delegate? Of(nint p)
    {
        if (!Tokens || p == 0) return null;
        lock (s_gate)
            return s_delegates.TryGetValue(p, out var w) && w.TryGetTarget(out Delegate? d) ? d : null;
    }

    /// <summary>A delegate of <paramref name="type"/> for the function pointer <paramref name="p"/>.</summary>
    public static Delegate? FromNative(nint p, Type type)
    {
        if (p == 0) return null;
        if (Of(p) is not { } d) return Marshal.GetDelegateForFunctionPointer(p, type);
        if (type.IsInstanceOfType(d)) return d;
        // the pointer of one delegate type read back as another (a WNDPROC kept as an IntPtr, called through
        // the app's own declaration): a delegate of that type which hands its arguments on as words, as
        // calling the pointer would
        MethodInfo invoke = type.GetMethod("Invoke")!;
        var ps = invoke.GetParameters().Select(p => System.Linq.Expressions.Expression.Parameter(p.ParameterType, p.Name)).ToArray();
        var words = System.Linq.Expressions.Expression.NewArrayInit(typeof(nint),
            ps.Select(p => System.Linq.Expressions.Expression.Call(typeof(Callbacks).GetMethod(nameof(WordOf), BindingFlags.NonPublic | BindingFlags.Static)!,
                System.Linq.Expressions.Expression.Convert(p, typeof(object)))));
        System.Linq.Expressions.Expression call = System.Linq.Expressions.Expression.Call(
            typeof(Callbacks).GetMethod(nameof(Call), BindingFlags.NonPublic | BindingFlags.Static)!, System.Linq.Expressions.Expression.Constant(d), words);
        if (invoke.ReturnType == typeof(void)) call = System.Linq.Expressions.Expression.Block(typeof(void), call);
        else call = System.Linq.Expressions.Expression.Convert(System.Linq.Expressions.Expression.Call(
            typeof(Callbacks).GetMethod(nameof(FromWord), BindingFlags.NonPublic | BindingFlags.Static)!, call,
            System.Linq.Expressions.Expression.Constant(invoke.ReturnType)), invoke.ReturnType);
        return System.Linq.Expressions.Expression.Lambda(type, call, ps).Compile();
    }

    private static nint WordOf(object? v) => v is Pointer ptr ? (nint)Pointer.Unbox(ptr) : ToWord(v);
    private static object? FromWord(nint w, Type t) => ToManaged(w, t);

    public static nint Invoke(Delegate d, nint a0) => Call(d, [a0]);
    public static nint Invoke(Delegate d, nint a0, nint a1) => Call(d, [a0, a1]);
    public static nint Invoke(Delegate d, nint a0, nint a1, nint a2) => Call(d, [a0, a1, a2]);
    public static nint Invoke(Delegate d, nint a0, nint a1, nint a2, nint a3) => Call(d, [a0, a1, a2, a3]);
    public static nint Invoke(Delegate d, nint a0, nint a1, nint a2, nint a3, nint a4) => Call(d, [a0, a1, a2, a3, a4]);

    /// <summary>Calls <paramref name="d"/> as native code calls its thunk: each argument a machine word,
    /// marshalled to the parameter it is (a number, a handle, a pointer, a structure it points at, a string),
    /// what a by-reference parameter or an out structure ends up as written back, the result a word.</summary>
    private static nint Call(Delegate d, nint[] words)
    {
        ParameterInfo[] ps = d.Method.GetParameters();
        if (ps.Length != words.Length) throw new ArgumentException($"{d.GetType()} takes {ps.Length} arguments, called with {words.Length}.");
        var args = new object?[ps.Length];
        for (int i = 0; i < ps.Length; i++)
        {
            Type t = ps[i].ParameterType;
            args[i] = t.IsByRef ? (ps[i].IsOut && !ps[i].IsIn ? Default(t.GetElementType()!) : Read(words[i], t.GetElementType()!)) : ToManaged(words[i], t);
        }
        object? result;
        try { result = d.DynamicInvoke(args); }
        catch (TargetInvocationException e) when (e.InnerException != null) { ExceptionDispatchInfo.Throw(e.InnerException); throw; }
        for (int i = 0; i < ps.Length; i++)
        {
            Type t = ps[i].ParameterType;
            if (words[i] == 0) continue;
            if (t.IsByRef && !(ps[i].IsIn && !ps[i].IsOut)) Write(words[i], t.GetElementType()!, args[i]);
            else if (!t.IsByRef && ps[i].IsOut && IsLayoutClass(t) && args[i] != null) Marshal.StructureToPtr(args[i]!, words[i], false);
        }
        return ToWord(result);
    }

    private static object? Default(Type t) => t.IsValueType ? Activator.CreateInstance(t) : null;

    private static bool IsLayoutClass(Type t) => t.IsClass && !typeof(Delegate).IsAssignableFrom(t) && t != typeof(string) && !t.IsAutoLayout;

    private static object? ToManaged(nint w, Type t)
    {
        if (t == typeof(IntPtr)) return w;
        if (t == typeof(UIntPtr)) return (nuint)w;
        if (t.IsPointer) return Pointer.Box((void*)w, t);
        if (t.IsEnum) return Enum.ToObject(t, Number(w, Enum.GetUnderlyingType(t)));
        if (t.IsPrimitive) return Number(w, t);
        if (t == typeof(string)) return Marshal.PtrToStringUni(w);
        if (typeof(Delegate).IsAssignableFrom(t)) return FromNative(w, t);
        if (IsLayoutClass(t)) return w == 0 ? null : Marshal.PtrToStructure(w, t);
        if (t == typeof(object)) return w;
        throw new NotSupportedException($"A callback parameter of type {t} from a machine word.");
    }

    private static object Number(nint w, Type t) => Type.GetTypeCode(t) switch
    {
        TypeCode.Boolean => w != 0,
        TypeCode.Char => (char)w,
        TypeCode.SByte => (sbyte)w,
        TypeCode.Byte => (byte)w,
        TypeCode.Int16 => (short)w,
        TypeCode.UInt16 => (ushort)w,
        TypeCode.Int32 => (int)w,
        TypeCode.UInt32 => (uint)w,
        TypeCode.Int64 => (long)w,
        TypeCode.UInt64 => (ulong)w,
        TypeCode.Single => BitConverter.Int32BitsToSingle((int)w),
        _ => t == typeof(IntPtr) ? w : t == typeof(UIntPtr) ? (nuint)w : throw new NotSupportedException($"A callback parameter of type {t}."),
    };

    // what a by-reference parameter points at
    private static object? Read(nint p, Type t)
    {
        if (p == 0) return Default(t);
        if (t == typeof(IntPtr)) return *(nint*)p;
        if (t == typeof(UIntPtr)) return *(nuint*)p;
        if (t.IsEnum) return Enum.ToObject(t, Read(p, Enum.GetUnderlyingType(t))!);
        if (t == typeof(bool)) return *(int*)p != 0;     // a BOOL, as the runtime marshals bool
        if (t.IsPrimitive) return Marshal.PtrToStructure(p, t);
        if (t == typeof(string)) return Marshal.PtrToStringUni(*(nint*)p);
        return Marshal.PtrToStructure(p, t);
    }

    private static void Write(nint p, Type t, object? v)
    {
        if (t == typeof(IntPtr)) *(nint*)p = (nint)(v ?? (nint)0);
        else if (t == typeof(UIntPtr)) *(nuint*)p = (nuint)(v ?? (nuint)0);
        else if (t.IsEnum) Write(p, Enum.GetUnderlyingType(t), v == null ? null : Convert.ChangeType(v, Enum.GetUnderlyingType(t)));
        else if (t == typeof(bool)) *(int*)p = v is true ? 1 : 0;
        else if (t == typeof(string)) { }     // a string written back is the runtime's to allocate; not here
        else if (v != null) Marshal.StructureToPtr(v, p, false);
    }

    private static nint ToWord(object? r) => r switch
    {
        null => 0,
        IntPtr p => p,
        UIntPtr u => (nint)u,
        bool b => b ? 1 : 0,
        Enum e => (nint)Convert.ToInt64(e),
        float f => BitConverter.SingleToInt32Bits(f),
        IConvertible c => (nint)c.ToInt64(null),
        _ => throw new NotSupportedException($"A callback result of type {r.GetType()}."),
    };
}
