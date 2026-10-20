// Managed objects handed to native code as a rewritten COM interface (CCWs; Documentation/
// comimport-rewriter.md). The rewriter gives each rewritten interface I a nested static class I.ComCcw
// whose Vtable() is I's vtable of [UnmanagedCallersOnly] stubs: each finds the managed object behind the
// pointer, converts the arguments the other way round, calls I's method and returns its HRESULT. The
// ComWrappers here answers, for an object, with an entry per rewritten interface it implements.

using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace WpfMac.ComInterop;

public static unsafe partial class Com
{
    /// <summary>The BCL COM interfaces (System.Runtime.InteropServices.ComTypes) supported off built-in COM, by
    /// full name: written by hand here (ComTypes.cs), which a rewritten assembly may use.</summary>
    public static IReadOnlyList<string> SupportedBclComTypes => BclComTypes.Supported;

    /// <summary>A stub's first argument: the managed object the CCW stands for.</summary>
    public static object CcwTarget(IntPtr dispatch) => ComWrappers.ComInterfaceDispatch.GetInstance<object>((ComWrappers.ComInterfaceDispatch*)dispatch);

    /// <summary>A vtable of <paramref name="slots"/> entries with IUnknown's three filled (ComWrappers' own),
    /// and IDispatch's four (when there are more than three) answering E_NOTIMPL; never freed.</summary>
    public static IntPtr NewVtable(int slots)
    {
        var vt = (IntPtr*)NativeMemory.AllocZeroed((nuint)slots, (nuint)sizeof(IntPtr));
        ComWrappers.GetIUnknownImpl(out vt[0], out vt[1], out vt[2]);
        return (IntPtr)vt;
    }

    /// <summary>For a dual interface's IDispatch slots (3-6): late binding is not offered.</summary>
    public static IntPtr NotImplementedSlot => (IntPtr)(delegate* unmanaged<IntPtr, int>)&NotImplemented;

    [UnmanagedCallersOnly]
    private static int NotImplemented(IntPtr self) => unchecked((int)0x80004001);

    /// <summary>A stub's failure: the exception as an HRESULT, as built-in COM interop returns it.</summary>
    public static int HResultOf(Exception e) => e.HResult != 0 ? e.HResult : unchecked((int)0x80004005);

    /// <summary>An interface argument a native caller passed to a managed method: the object for it (the
    /// caller keeps its reference).</summary>
    public static object? Wrap(IntPtr p) => p == IntPtr.Zero ? null : ComObject.Get(p);

    private sealed class Ccw : ComWrappers
    {
        public static readonly Ccw Instance = new();
        private static readonly ConditionalWeakTable<Type, Entries> s_entries = new();

        private sealed class Entries { public ComInterfaceEntry* Table; public int Count; }

        protected override ComInterfaceEntry* ComputeVtables(object obj, CreateComInterfaceFlags flags, out int count)
        {
            var e = s_entries.GetValue(obj.GetType(), Build);
            count = e.Count;
            return e.Table;
        }

        private static Entries Build(Type type)
        {
            var list = new List<(Guid Iid, IntPtr Vtable)>();
            foreach (Type i in type.GetInterfaces())
            {
                Type? ccw = i.GetNestedType("ComCcw", BindingFlags.Public | BindingFlags.NonPublic);
                if (ccw?.GetMethod("Vtable", BindingFlags.Public | BindingFlags.Static) is { } vt) list.Add((i.GUID, (IntPtr)vt.Invoke(null, null)!));
                else if (BclComTypes.VtableOf(i) is var bcl && bcl != IntPtr.Zero) list.Add((i.GUID, bcl));
                else if (Generated.TryGetValue(i, out var g) && g.Ccw.GetMethod("Vtable", BindingFlags.Public | BindingFlags.Static) is { } gv) list.Add((i.GUID, (IntPtr)gv.Invoke(null, null)!));
            }
            // an event source ([ComSourceInterfaces]): IConnectionPointContainer, its connection points made on demand
            if (EventSource.SourceInterfaces(type) != null && !list.Exists(e => e.Iid == IID_IConnectionPointContainer))
                list.Add((IID_IConnectionPointContainer, BclComTypes.VtableOf(typeof(System.Runtime.InteropServices.ComTypes.IConnectionPointContainer))));
            // IDispatch: the first dispinterface's or dual interface's (their slots 3-6 answer it)
            foreach (var (_, v) in list.ToArray())
                if (IsDispatchVtable(v)) { list.Add((IID_IDispatch, v)); break; }
            if (list.Count == 0)
                LogOnce("ccw:" + type.FullName, $"a managed {type.FullName} was handed to native code: it implements no rewritten COM interface, so only IUnknown is offered");
            var table = (ComInterfaceEntry*)NativeMemory.AllocZeroed((nuint)Math.Max(1, list.Count), (nuint)sizeof(ComInterfaceEntry));
            for (int k = 0; k < list.Count; k++) { table[k].IID = list[k].Iid; table[k].Vtable = list[k].Vtable; }
            return new Entries { Table = table, Count = list.Count };
        }

        protected override object? CreateObject(IntPtr externalComObject, CreateObjectFlags flags) => null;

        protected override void ReleaseObjects(System.Collections.IEnumerable objects) { }
    }

    private static IntPtr CcwUnknown(object o) => Ccw.Instance.GetOrCreateComInterfaceForObject(o, CreateComInterfaceFlags.None);
}
