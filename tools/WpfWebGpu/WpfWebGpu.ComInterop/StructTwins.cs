// A formatted type with interface fields, laid out without built-in COM (Formatted.cs). Such a field -- an
// interface, or object as IUnknown/IDispatch -- is a COM pointer in the native layout, which built-in interop
// makes and reads with its own CCWs and RCWs. Off Windows it cannot at all: Marshal refuses the whole type
// (SizeOf, OffsetOf, StructureToPtr: "cannot be marshaled as an unmanaged structure"), so a STGMEDIUM --
// IDataObject.GetData's -- could not even be allocated.
//
// So the type is laid out through its twin: a struct made at run time with the same fields in the same order,
// the same layout (sequential or explicit, its offsets, pack, size, character set) and the same MarshalAs, an
// interface field an IntPtr and a nested struct with interface fields its own twin. Marshal lays the twin out
// on every system, as it would the original on Windows, and its pointer slots are filled and read here, as
// rewritten code holds COM objects (Borrow, Wrap). Every system goes this way, Windows too: one layout path.

using System.Collections.Concurrent;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace WpfWebGpu.ComInterop;

public static unsafe partial class Com
{
    private enum TwinField : byte { Same, Interface, Dispatch, Unknown, Nested }

    private sealed class Twin
    {
        public required Type Type;
        public required (FieldInfo From, FieldInfo To, TwinField Kind, int Offset)[] Fields;
    }

    private static readonly ConcurrentDictionary<Type, Twin?> s_twins = new();
    private static readonly object s_twinLock = new();
    private static ModuleBuilder? s_twinModule;

    /// <summary>The type's twin, or null when it has no interface field (directly or in a nested struct).</summary>
    private static Twin? TwinOf(Type t)
    {
        if (s_twins.TryGetValue(t, out Twin? known)) return known;
        lock (s_twinLock)
        {
            if (s_twins.TryGetValue(t, out known)) return known;
            Twin? made = MakeTwin(t);
            s_twins[t] = made;
            return made;
        }
    }

    /// <summary>The size of the type's native layout.</summary>
    private static int NativeSizeOf(Type t) => Marshal.SizeOf(TwinOf(t)?.Type ?? t);

    // the instance fields as Marshal lays them out: a base class's first, each type's in declaration order
    private static List<FieldInfo> LayoutFields(Type t)
    {
        var levels = new List<FieldInfo[]>();
        for (Type? x = t; x != null && x != typeof(object) && x != typeof(ValueType); x = x.BaseType)
            levels.Insert(0, x.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .OrderBy(f => f.MetadataToken).ToArray());
        return levels.SelectMany(l => l).ToList();
    }

    private static TwinField KindOf(FieldInfo f)
    {
        var ma = f.GetCustomAttribute<MarshalAsAttribute>();
        if (f.FieldType.IsInterface) return ma?.Value == UnmanagedType.IDispatch ? TwinField.Dispatch : TwinField.Interface;
        if (f.FieldType == typeof(object) && ma?.Value is UnmanagedType.IUnknown or UnmanagedType.Interface) return TwinField.Unknown;
        if (f.FieldType == typeof(object) && ma?.Value is UnmanagedType.IDispatch) return TwinField.Dispatch;
        if (f.FieldType.IsValueType && !f.FieldType.IsPrimitive && !f.FieldType.IsEnum && TwinOf(f.FieldType) != null) return TwinField.Nested;
        return TwinField.Same;
    }

    private static Twin? MakeTwin(Type t)
    {
        if (t.IsPrimitive || t.IsEnum || t.IsArray || t.IsInterface || t == typeof(string)) return null;
        List<FieldInfo> fields = LayoutFields(t);
        var kinds = fields.Select(KindOf).ToArray();
        if (kinds.All(k => k == TwinField.Same)) return null;
        if (!RuntimeFeature.IsDynamicCodeSupported)
            throw new PlatformNotSupportedException($"comimport: {t} has COM interface fields, whose layout is made at run time, and this runtime cannot make code");

        s_twinModule ??= AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("WpfWebGpu.ComInterop.Twins"), AssemblyBuilderAccess.Run)
            .DefineDynamicModule("Twins");
        StructLayoutAttribute? layout = t.StructLayoutAttribute;
        bool explicitLayout = layout?.Value == LayoutKind.Explicit;
        TypeAttributes attrs = TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit
            | (explicitLayout ? TypeAttributes.ExplicitLayout : TypeAttributes.SequentialLayout)
            | (layout?.CharSet switch { CharSet.Unicode => TypeAttributes.UnicodeClass, CharSet.Auto => TypeAttributes.AutoClass, _ => TypeAttributes.AnsiClass });
        int pack = layout?.Pack ?? 0;
        TypeBuilder tb = s_twinModule.DefineType($"{t.FullName}+Twin{s_twins.Count}", attrs, typeof(ValueType),
            pack is 1 or 2 or 4 or 8 or 16 or 32 or 64 or 128 ? (PackingSize)pack : PackingSize.Unspecified, layout?.Size ?? 0);
        var builders = new FieldBuilder[fields.Count];
        for (int i = 0; i < fields.Count; i++)
        {
            FieldInfo f = fields[i];
            Type ft = kinds[i] switch
            {
                TwinField.Same => f.FieldType,
                TwinField.Nested => TwinOf(f.FieldType)!.Type,
                _ => typeof(IntPtr),
            };
            FieldBuilder fb = tb.DefineField(f.Name, ft, FieldAttributes.Public);
            if (explicitLayout && f.GetCustomAttribute<FieldOffsetAttribute>() is { } fo) fb.SetOffset(fo.Value);
            if (kinds[i] == TwinField.Same && f.GetCustomAttribute<MarshalAsAttribute>() is { } ma) fb.SetCustomAttribute(MarshalAsBuilder(ma));
            builders[i] = fb;
        }
        Type twin = tb.CreateType();
        var map = new (FieldInfo, FieldInfo, TwinField, int)[fields.Count];
        for (int i = 0; i < fields.Count; i++)
            map[i] = (fields[i], twin.GetField(fields[i].Name)!, kinds[i], (int)Marshal.OffsetOf(twin, fields[i].Name));
        return new Twin { Type = twin, Fields = map };
    }

    // the field's MarshalAs as written: its unmanaged type and what that type reads of the rest
    private static CustomAttributeBuilder MarshalAsBuilder(MarshalAsAttribute ma)
    {
        var names = new List<FieldInfo>();
        var values = new List<object?>();
        void Set(string name, object? value) { names.Add(typeof(MarshalAsAttribute).GetField(name)!); values.Add(value); }
        switch (ma.Value)
        {
            case UnmanagedType.ByValTStr:
                Set(nameof(MarshalAsAttribute.SizeConst), ma.SizeConst);
                break;
            case UnmanagedType.ByValArray:
                Set(nameof(MarshalAsAttribute.SizeConst), ma.SizeConst);
                if (ma.ArraySubType != 0) Set(nameof(MarshalAsAttribute.ArraySubType), ma.ArraySubType);
                break;
            case UnmanagedType.SafeArray:
                if (ma.SafeArraySubType != 0) Set(nameof(MarshalAsAttribute.SafeArraySubType), ma.SafeArraySubType);
                if (ma.SafeArrayUserDefinedSubType != null) Set(nameof(MarshalAsAttribute.SafeArrayUserDefinedSubType), ma.SafeArrayUserDefinedSubType);
                break;
            case UnmanagedType.CustomMarshaler:
                if (ma.MarshalTypeRef != null) Set(nameof(MarshalAsAttribute.MarshalTypeRef), ma.MarshalTypeRef);
                else if (ma.MarshalType != null) Set(nameof(MarshalAsAttribute.MarshalType), ma.MarshalType);
                if (ma.MarshalCookie != null) Set(nameof(MarshalAsAttribute.MarshalCookie), ma.MarshalCookie);
                break;
        }
        return new CustomAttributeBuilder(typeof(MarshalAsAttribute).GetConstructor(new[] { typeof(UnmanagedType) })!, new object[] { ma.Value },
            names.ToArray(), values.ToArray());
    }

    /// <summary>The twin's value for <paramref name="o"/>: its interface fields as owned COM pointers.</summary>
    private static object ToTwin(object o, Twin twin)
    {
        object tw = Activator.CreateInstance(twin.Type)!;
        foreach (var (from, to, kind, _) in twin.Fields)
        {
            object? v = from.GetValue(o);
            to.SetValue(tw, kind switch
            {
                TwinField.Same => v,
                TwinField.Nested => ToTwin(v!, TwinOf(from.FieldType)!),
                TwinField.Interface => v == null ? IntPtr.Zero : Borrow(v, from.FieldType.TypeHandle),
                TwinField.Dispatch => v == null ? IntPtr.Zero : BorrowDispatch(v),
                _ => v == null ? IntPtr.Zero : BorrowUnknown(v),
            });
        }
        return tw;
    }

    /// <summary>The value the twin <paramref name="tw"/> holds, as <paramref name="t"/>: into <paramref name="target"/>
    /// when that is a class instance, else a new one; its COM pointers wrapped.</summary>
    private static object FromTwin(object tw, object? target, Type t, Twin twin)
    {
        object r = target ?? (t.IsValueType ? Activator.CreateInstance(t)! : RuntimeHelpers.GetUninitializedObject(t));
        foreach (var (from, to, kind, _) in twin.Fields)
        {
            object? v = to.GetValue(tw);
            from.SetValue(r, kind switch
            {
                TwinField.Same => v,
                TwinField.Nested => FromTwin(v!, null, from.FieldType, TwinOf(from.FieldType)!),
                _ => Wrap((IntPtr)v!),
            });
        }
        return r;
    }

    /// <summary>The COM pointers in the native layout at <paramref name="p"/> released and cleared.</summary>
    private static void ReleaseInterfaces(IntPtr p, Twin twin)
    {
        foreach (var (from, _, kind, offset) in twin.Fields)
        {
            if (kind == TwinField.Same) continue;
            if (kind == TwinField.Nested) { ReleaseInterfaces(p + offset, TwinOf(from.FieldType)!); continue; }
            Release(Marshal.ReadIntPtr(p, offset));
            Marshal.WriteIntPtr(p, offset, IntPtr.Zero);
        }
    }
}
