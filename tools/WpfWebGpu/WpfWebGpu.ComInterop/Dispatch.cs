// IDispatch for rewritten assemblies (Documentation/comimport-rewriter.md), both ways:
//
// - a dispinterface ([InterfaceType(InterfaceIsIDispatch)]) is called through IDispatch::Invoke: the
//   rewriter's I.ComImpl bodies box the arguments and call DispInvoke with the member's DISPID ([DispId],
//   or GetIDsOfNames by name), as built-in COM interop does;
// - a managed object handed to native code answers IDispatch (GetIDsOfNames, Invoke by reflection over the
//   interface) for its dispinterfaces and the IDispatch slots of its dual interfaces -- what an event sink
//   needs. GetTypeInfo is not offered (no type library), as for a built-in CCW without one.

using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.InteropServices;

namespace WpfWebGpu.ComInterop;

/// <summary>DISPPARAMS, as the SDK lays it out.</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct ComDispParams
{
    public ComVariant* Args;            // rgvarg: the last argument first
    public int* NamedArgs;              // rgdispidNamedArgs
    public uint ArgCount, NamedCount;
}

/// <summary>EXCEPINFO, as the SDK lays it out.</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct ComExcepInfo
{
    public ushort Code, Reserved;
    public IntPtr Source, Description, HelpFile;     // BSTRs
    public uint HelpContext;
    public IntPtr PvReserved;
    public IntPtr DeferredFillIn;                    // HRESULT (*)(EXCEPINFO*)
    public int Scode;
}

public static unsafe partial class Com
{
    public const int DISPATCH_METHOD = 1, DISPATCH_PROPERTYGET = 2, DISPATCH_PROPERTYPUT = 4, DISPATCH_PROPERTYPUTREF = 8;
    /// <summary>A member whose DISPID the rewriter did not find ([DispId] absent): asked by name.</summary>
    public const int DispIdUnknown = int.MinValue;
    private const int DISPID_PROPERTYPUT = -3;
    private const int DISP_E_MEMBERNOTFOUND = unchecked((int)0x80020003), DISP_E_TYPEMISMATCH = unchecked((int)0x80020005),
        DISP_E_UNKNOWNNAME = unchecked((int)0x80020006), DISP_E_EXCEPTION = unchecked((int)0x80020009), DISP_E_BADPARAMCOUNT = unchecked((int)0x8002000E),
        DISP_E_PARAMNOTFOUND = unchecked((int)0x80020004);

    private static readonly ConcurrentDictionary<(IntPtr Disp, string Name), int> s_dispIds = new();

    // ---- calls into native objects -----------------------------------------------------------------

    /// <summary>A dispinterface member called through IDispatch::Invoke. <paramref name="args"/> in declaration
    /// order; bit i of <paramref name="byRefMask"/>: argument i is by reference (passed as VT_BYREF|VT_VARIANT,
    /// and written back into <paramref name="args"/>). Returns the result VARIANT's value.</summary>
    public static object? DispInvoke(object self, RuntimeTypeHandle iface, int dispid, string name, int flags, object?[] args, long byRefMask)
    {
        if (self is not ComObject c) throw new InvalidCastException($"{self.GetType()} is not a COM object");
        IntPtr disp = c.For(IID_IDispatch, throwIfNot: true);
        if (dispid == DispIdUnknown) dispid = IdOfName(disp, name);
        int n = args.Length;
        var argv = (ComVariant*)NativeMemory.AllocZeroed((nuint)Math.Max(1, n), (nuint)sizeof(ComVariant));
        var inner = (ComVariant*)NativeMemory.AllocZeroed((nuint)Math.Max(1, n), (nuint)sizeof(ComVariant));
        try
        {
            for (int i = 0; i < n; i++)
            {
                ComVariant* slot = &argv[n - 1 - i];
                if ((byRefMask & (1L << i)) != 0)
                {
                    ToVariant(args[i], (IntPtr)(&inner[i]));
                    slot->Vt = VT_BYREF | VT_VARIANT;
                    slot->Data1 = (IntPtr)(&inner[i]);
                }
                else ToVariant(args[i], (IntPtr)slot);
            }
            int put = DISPID_PROPERTYPUT;
            var dp = new ComDispParams { Args = argv, ArgCount = (uint)n };
            if ((flags & (DISPATCH_PROPERTYPUT | DISPATCH_PROPERTYPUTREF)) != 0) { dp.NamedArgs = &put; dp.NamedCount = 1; }
            ComVariant result = default;
            ComExcepInfo ex = default;
            uint argErr = 0;
            Guid iidNull = Guid.Empty;
            int hr = ((delegate* unmanaged[Stdcall]<IntPtr, int, Guid*, int, ushort, ComDispParams*, ComVariant*, ComExcepInfo*, uint*, int>)(*(IntPtr**)disp)[6])
                (disp, dispid, &iidNull, 0, (ushort)flags, &dp, &result, &ex, &argErr);
            GC.KeepAlive(self);
            for (int i = 0; i < n; i++)
                if ((byRefMask & (1L << i)) != 0) args[i] = FromVariant((IntPtr)(&inner[i]));
            if (hr == DISP_E_EXCEPTION) throw ExceptionOf(ref ex);
            Check(hr);
            try { return FromVariant((IntPtr)(&result)); }
            finally { ClearVariant((IntPtr)(&result)); }
        }
        finally
        {
            for (int i = 0; i < n; i++)
            {
                if ((byRefMask & (1L << i)) == 0) ClearVariant((IntPtr)(&argv[n - 1 - i]));
                ClearVariant((IntPtr)(&inner[i]));
            }
            NativeMemory.Free(argv);
            NativeMemory.Free(inner);
        }
    }

    private static int IdOfName(IntPtr disp, string name)
    {
        if (s_dispIds.TryGetValue((disp, name), out int id)) return id;
        Guid iidNull = Guid.Empty;
        fixed (char* s = name)
        {
            char* names = s;
            int hr = ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, char**, uint, int, int*, int>)(*(IntPtr**)disp)[5])(disp, &iidNull, &names, 1, 0, &id);
            Check(hr);
        }
        s_dispIds[(disp, name)] = id;
        return id;
    }

    /// <summary>DISP_E_EXCEPTION's EXCEPINFO as the exception built-in COM interop throws (a COMException
    /// with the server's description); its BSTRs are freed.</summary>
    private static Exception ExceptionOf(ref ComExcepInfo ex)
    {
        if (ex.DeferredFillIn != IntPtr.Zero)
            fixed (ComExcepInfo* p = &ex) ((delegate* unmanaged[Stdcall]<ComExcepInfo*, int>)ex.DeferredFillIn)(p);
        string? description = StringOfBstr(ex.Description);
        string? source = StringOfBstr(ex.Source);
        int scode = ex.Scode != 0 ? ex.Scode : ex.Code != 0 ? unchecked((int)0x80040200) + ex.Code : DISP_E_EXCEPTION;
        FreeBstr(ex.Source); FreeBstr(ex.Description); FreeBstr(ex.HelpFile);
        return new COMException(description ?? $"Exception from HRESULT: 0x{scode:X8}", scode) { Source = source };
    }

    /// <summary>A dispinterface call's result (or a by-reference argument) as the declared type.</summary>
    public static object? DispatchResult(object? v, RuntimeTypeHandle type)
    {
        Type t = Type.GetTypeFromHandle(type)!;
        if (t == typeof(void)) return null;
        if (v == null) return t.IsValueType ? Activator.CreateInstance(t) : null;
        return ConvertTo(v, t);
    }

    private static object? ConvertTo(object? v, Type t)
    {
        if (v == null) return t.IsValueType && Nullable.GetUnderlyingType(t) == null ? Activator.CreateInstance(t) : null;
        if (t.IsInstanceOfType(v)) return v;
        if (t.IsEnum) return Enum.ToObject(t, Convert.ChangeType(v, Enum.GetUnderlyingType(t), System.Globalization.CultureInfo.InvariantCulture));
        if (t.IsInterface || t == typeof(object)) return v;               // the cast is the caller's (IDynamicInterfaceCastable)
        if (v is IConvertible && (t.IsPrimitive || t == typeof(string) || t == typeof(decimal) || t == typeof(DateTime)))
            return Convert.ChangeType(v, t, System.Globalization.CultureInfo.InvariantCulture);
        if (t.IsArray && v is Array a && t.GetElementType() is { } e)
        {
            var r = Array.CreateInstance(e, a.Length);
            for (int i = 0; i < a.Length; i++) r.SetValue(ConvertTo(a.GetValue(i), e), i);
            return r;
        }
        throw new InvalidCastException($"a {v.GetType()} where {t} is expected");
    }

    /// <summary>An argument marshalled as IDispatch: <paramref name="o"/>'s IDispatch, AddRef'd. Null for null.</summary>
    public static IntPtr BorrowDispatch(object? o) => o == null ? IntPtr.Zero : PointerFor(o, IID_IDispatch);

    // ---- managed objects handed to native code -------------------------------------------------------

    private static readonly ConcurrentDictionary<IntPtr, Type> s_dispVtables = new();
    private static readonly ConcurrentDictionary<Type, DispMap> s_dispMaps = new();

    /// <summary>For I.ComCcw's Vtable(): IDispatch's four slots (3-6) of <paramref name="vtable"/>, answering
    /// for the interface <paramref name="iface"/> by reflection.</summary>
    public static void FillDispatchSlots(IntPtr vtable, RuntimeTypeHandle iface)
    {
        var vt = (IntPtr*)vtable;
        vt[3] = (IntPtr)(delegate* unmanaged<IntPtr, uint*, int>)&GetTypeInfoCount;
        vt[4] = (IntPtr)(delegate* unmanaged<IntPtr, uint, int, IntPtr*, int>)&GetTypeInfo;
        vt[5] = (IntPtr)(delegate* unmanaged<IntPtr, Guid*, char**, uint, int, int*, int>)&GetIDsOfNames;
        vt[6] = (IntPtr)(delegate* unmanaged<IntPtr, int, Guid*, int, ushort, ComDispParams*, ComVariant*, ComExcepInfo*, uint*, int>)&Invoke;
        s_dispVtables[vtable] = Type.GetTypeFromHandle(iface)!;
    }

    /// <summary>Whether a CCW vtable answers IDispatch (Ccw.Build's IDispatch entry).</summary>
    internal static bool IsDispatchVtable(IntPtr vtable) => s_dispVtables.ContainsKey(vtable);

    private sealed class DispMap
    {
        public readonly Dictionary<int, (MethodInfo? Method, PropertyInfo? Property)> ById = new();
        public readonly Dictionary<string, int> ByName = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The interface's members by DISPID: [DispId], or 0x60020000 + declaration index as a type
    /// library numbers them.</summary>
    private static DispMap MapOf(Type iface) => s_dispMaps.GetOrAdd(iface, t =>
    {
        var map = new DispMap();
        int next = 0x60020000;
        foreach (var p in t.GetProperties().OrderBy(x => x.MetadataToken))
        {
            int id = p.GetCustomAttribute<DispIdAttribute>()?.Value ?? next++;
            map.ById[id] = (null, p);
            map.ByName[p.Name] = id;
        }
        foreach (var m in t.GetMethods().Where(m => !m.IsSpecialName).OrderBy(x => x.MetadataToken))
        {
            int id = m.GetCustomAttribute<DispIdAttribute>()?.Value ?? next++;
            map.ById.TryAdd(id, (m, null));
            map.ByName.TryAdd(m.Name, id);
        }
        return map;
    });

    private static Type InterfaceOf(IntPtr self) => s_dispVtables[*(IntPtr*)self];

    [UnmanagedCallersOnly]
    private static int GetTypeInfoCount(IntPtr self, uint* count) { *count = 0; return 0; }

    [UnmanagedCallersOnly]
    private static int GetTypeInfo(IntPtr self, uint index, int lcid, IntPtr* info) { *info = IntPtr.Zero; return unchecked((int)0x80004001); }

    [UnmanagedCallersOnly]
    private static int GetIDsOfNames(IntPtr self, Guid* riid, char** names, uint count, int lcid, int* ids)
    {
        try
        {
            var map = MapOf(InterfaceOf(self));
            string member = new(names[0]);
            int hr = 0;
            if (!map.ByName.TryGetValue(member, out int id)) { id = -1; hr = DISP_E_UNKNOWNNAME; }
            ids[0] = id;
            // the parameters' names: their positions
            ParameterInfo[] ps = hr == 0 && map.ById[id] is var (m, p) ? (m?.GetParameters() ?? p!.GetIndexParameters()) : Array.Empty<ParameterInfo>();
            for (uint i = 1; i < count; i++)
            {
                string pn = new(names[i]);
                int k = Array.FindIndex(ps, x => string.Equals(x.Name, pn, StringComparison.OrdinalIgnoreCase));
                ids[i] = k;
                if (k < 0) hr = DISP_E_UNKNOWNNAME;
            }
            return hr;
        }
        catch (Exception e) { return HResultOf(e); }
    }

    [UnmanagedCallersOnly]
    private static int Invoke(IntPtr self, int dispid, Guid* riid, int lcid, ushort flags, ComDispParams* dp, ComVariant* result, ComExcepInfo* ex, uint* argErr)
    {
        object target;
        DispMap map;
        try { target = CcwTarget(self); map = MapOf(InterfaceOf(self)); }
        catch (Exception e) { return HResultOf(e); }
        if (!map.ById.TryGetValue(dispid, out var member)) return DISP_E_MEMBERNOTFOUND;
        int argCount = (int)dp->ArgCount;
        bool put = (flags & (DISPATCH_PROPERTYPUT | DISPATCH_PROPERTYPUTREF)) != 0;
        // positional arguments, in declaration order (rgvarg holds them last first; a put's value is the
        // named DISPID_PROPERTYPUT one at rgvarg[0])
        int named = (int)dp->NamedCount;
        int positional = argCount - named;
        ComVariant* Positional(int j) => &dp->Args[argCount - 1 - j];
        ParameterInfo[] ps = member.Method?.GetParameters() ?? member.Property!.GetIndexParameters();
        var values = new object?[ps.Length];
        int j = 0;
        try
        {
            if (positional > ps.Length) return DISP_E_BADPARAMCOUNT;
            for (j = 0; j < ps.Length; j++)
            {
                Type pt = ps[j].ParameterType.IsByRef ? ps[j].ParameterType.GetElementType()! : ps[j].ParameterType;
                if (j < positional)
                {
                    object? v = ArgFromVariant(Positional(j));
                    values[j] = v is System.Reflection.Missing ? Default(ps[j], pt) : ConvertTo(v, pt);
                }
                else if (ps[j].IsOptional || ps[j].IsOut) values[j] = Default(ps[j], pt);
                else return DISP_E_PARAMNOTFOUND;
            }
        }
        catch (Exception) { if (argErr != null) *argErr = (uint)(argCount - 1 - j); return DISP_E_TYPEMISMATCH; }

        object? ret;
        try
        {
            if (member.Method != null) ret = member.Method.Invoke(target, values);
            else if (put)
            {
                if (named < 1) return DISP_E_PARAMNOTFOUND;
                object? value = ConvertTo(ArgFromVariant(&dp->Args[0]), member.Property!.PropertyType);
                member.Property!.SetValue(target, value, values.Length == 0 ? null : values);
                ret = null;
            }
            else ret = member.Property!.GetValue(target, values.Length == 0 ? null : values);
        }
        catch (Exception e)
        {
            var inner = e is TargetInvocationException { InnerException: { } i } ? i : e;
            if (ex == null) return HResultOf(inner);
            *ex = default;
            ex->Source = ToBstr(inner.Source);
            ex->Description = ToBstr(inner.Message);
            ex->Scode = HResultOf(inner);
            return DISP_E_EXCEPTION;
        }
        try
        {
            // by-reference arguments back into the caller's variables
            for (int k = 0; k < Math.Min(positional, ps.Length); k++)
            {
                if (!ps[k].ParameterType.IsByRef) continue;
                ComVariant* v = Positional(k);
                if ((v->Vt & VT_BYREF) == 0) continue;
                ushort baseVt = (ushort)(v->Vt & ~VT_BYREF);
                byte* p = (byte*)v->Data1;
                if (baseVt == VT_VARIANT) { ClearVariant((IntPtr)p); ToVariant(values[k], (IntPtr)p); }
                else if ((baseVt & VT_ARRAY) != 0) { DestroySafeArray(*(IntPtr*)p); *(IntPtr*)p = ToSafeArray(values[k], baseVt & ~VT_ARRAY); }
                else { ClearElement(p, baseVt); WriteElement(p, baseVt, values[k]); }
            }
            if (result != null) ToVariant(ret, (IntPtr)result);
            return 0;
        }
        catch (Exception e) { return HResultOf(e); }
    }

    private static object? Default(ParameterInfo p, Type t) =>
        p.HasDefaultValue && p.DefaultValue is not DBNull ? p.DefaultValue : t.IsValueType ? Activator.CreateInstance(t) : (t == typeof(object) ? System.Reflection.Missing.Value : null);

    /// <summary>A DISPPARAMS argument's value: by value, or VT_BYREF (what it points at).</summary>
    private static object? ArgFromVariant(ComVariant* v)
    {
        if ((v->Vt & VT_BYREF) == 0)
        {
            // DISP_E_PARAMNOTFOUND as VT_ERROR: an omitted optional argument
            if (v->Vt == VT_ERROR && (int)v->Data1 == DISP_E_PARAMNOTFOUND) return System.Reflection.Missing.Value;
            return FromVariant((IntPtr)v);
        }
        ushort baseVt = (ushort)(v->Vt & ~VT_BYREF);
        byte* p = (byte*)v->Data1;
        if (baseVt == VT_VARIANT) return FromVariant((IntPtr)p);
        if ((baseVt & VT_ARRAY) != 0) return FromSafeArray(*(IntPtr*)p, typeof(Array).TypeHandle);
        return ReadElement(p, baseVt);
    }
}
