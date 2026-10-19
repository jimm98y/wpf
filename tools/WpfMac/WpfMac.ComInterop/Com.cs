// What an app assembly rewritten by wpfmac's COM-import rewriter calls instead of built-in COM interop
// (Documentation/comimport-rewriter.md). The rewriter turns:
//
// - each [ComImport] interface I into a plain interface, plus a nested [DynamicInterfaceCastable-
//   Implementation] interface I.ComImpl whose methods call through I's vtable slots;
// - every native pointer the app receives as an interface into a ComObject (one per COM identity),
//   which a cast to I QueryInterfaces;
// - Marshal.GetObjectForIUnknown, GetComInterfaceForObject, ReleaseComObject and their kin into the
//   members here, which take the same arguments.
//
// It works the same on every OS: it needs no built-in COM, only vtable calls.

using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace WpfMac.ComInterop;

/// <summary>The rewritten assemblies' entry points.</summary>
public static unsafe class Com
{
    private static readonly Guid IID_IUnknown = new("00000000-0000-0000-C000-000000000046");

    /// <summary>Messages for the compatibility log (the startup hook installs one); stderr otherwise.</summary>
    public static Action<string, string>? Log;

    private static readonly ConcurrentDictionary<string, bool> s_logged = new();

    internal static void LogOnce(string key, string message)
    {
        if (!s_logged.TryAdd(key, true)) return;
        // the compatibility log of wpfmac's startup hook (WpfMac.Bootstrap's Keys.LogOnceCallback)
        Log ??= AppContext.GetData("WpfMac.LogOnce") as Action<string, string>;
        if (Log != null) Log(key, "comimport: " + message);
        else Console.Error.WriteLine("comimport: " + message);
    }

    // ---- the vtable calls' side --------------------------------------------------------------

    /// <summary>The native pointer of <paramref name="self"/> (a ComObject) for the interface
    /// <paramref name="iface"/>, as I.ComImpl's methods call through it. Not AddRef'd: the object keeps
    /// it, and the caller keeps the object alive across the call.</summary>
    public static IntPtr This(object self, RuntimeTypeHandle iface)
    {
        if (self is not ComObject c) throw new InvalidCastException($"{self.GetType()} is not a COM object");
        return c.For(Type.GetTypeFromHandle(iface)!.GUID, throwIfNot: true);
    }

    /// <summary>An interface argument: <paramref name="o"/>'s pointer for <paramref name="iface"/>,
    /// AddRef'd. The caller passes it and then calls <see cref="Release"/> (the callee only borrows it,
    /// as with the built-in marshaller). Null for null.</summary>
    public static IntPtr Borrow(object? o, RuntimeTypeHandle iface) => o == null ? IntPtr.Zero : PointerFor(o, Type.GetTypeFromHandle(iface)!.GUID);

    /// <summary>An argument marshalled as IUnknown.</summary>
    public static IntPtr BorrowUnknown(object? o) => o == null ? IntPtr.Zero : PointerFor(o, IID_IUnknown);

    /// <summary>Releases a pointer <see cref="Borrow"/> gave.</summary>
    public static void Release(IntPtr p) { if (p != IntPtr.Zero) Marshal.Release(p); }

    /// <summary>An interface the callee returned (an out or retval pointer, which the caller owns): the
    /// object for it, which takes that reference over. Null for null.</summary>
    public static object? Take(IntPtr p)
    {
        if (p == IntPtr.Zero) return null;
        try { return ComObject.Get(p); }
        finally { Marshal.Release(p); }
    }

    // ---- where a rewritten assembly came from -------------------------------------------------
    //
    // A rewritten assembly is loaded from memory (LoadFromStream): the runtime knows no file for it. Its
    // calls to Assembly.Location, CodeBase, GetName() and Module.FullyQualifiedName are rewritten to these,
    // which answer with the original file's for an assembly of this table, and as the runtime does for
    // any other. The real Assembly objects are kept throughout.

    private static readonly ConditionalWeakTable<Assembly, string> s_original = new();

    /// <summary>The loader's: <paramref name="assembly"/> was rewritten from <paramref name="path"/>.</summary>
    public static void RegisterOriginal(Assembly assembly, string path) => s_original.AddOrUpdate(assembly, Path.GetFullPath(path));

    /// <summary>The original file of a rewritten assembly; null for any other.</summary>
    public static string? OriginalPath(Assembly? assembly) => assembly != null && s_original.TryGetValue(assembly, out var p) ? p : null;

    public static string AssemblyLocation(Assembly a) => OriginalPath(a) ?? a.Location;

#pragma warning disable SYSLIB0012, SYSLIB0044
    public static string? AssemblyCodeBase(Assembly a) => OriginalPath(a) is { } p ? new Uri(p).AbsoluteUri : a.CodeBase;

    public static string AssemblyEscapedCodeBase(Assembly a) => OriginalPath(a) is { } p ? new Uri(p).AbsoluteUri : a.EscapedCodeBase;

    public static AssemblyName AssemblyGetName(Assembly a) => WithCodeBase(a, a.GetName());

    public static AssemblyName AssemblyGetName(Assembly a, bool copiedName) => WithCodeBase(a, a.GetName(copiedName));

    private static AssemblyName WithCodeBase(Assembly a, AssemblyName n)
    {
        if (OriginalPath(a) is { } p) n.CodeBase = new Uri(p).AbsoluteUri;
        return n;
    }
#pragma warning restore SYSLIB0012, SYSLIB0044

    public static string ModuleFullyQualifiedName(Module m) => OriginalPath(m.Assembly) ?? m.FullyQualifiedName;

    public static string ModuleName(Module m) => OriginalPath(m.Assembly) is { } p ? Path.GetFileName(p) : m.Name;

    // ---- strings ------------------------------------------------------------------------------

    /// <summary>A BSTR for an argument (null for null), which the caller frees with <see cref="FreeBstr"/>.</summary>
    public static IntPtr ToBstr(string? s) => s == null ? IntPtr.Zero : Marshal.StringToBSTR(s);

    public static void FreeBstr(IntPtr p) { if (p != IntPtr.Zero) Marshal.FreeBSTR(p); }

    /// <summary>A BSTR the callee returned (the caller's to free): its string, and freed.</summary>
    public static string? TakeBstr(IntPtr p)
    {
        if (p == IntPtr.Zero) return null;
        try { return Marshal.PtrToStringBSTR(p); }
        finally { Marshal.FreeBSTR(p); }
    }

    /// <summary>An LPWSTR the callee returned in CoTaskMem memory: its string, and freed.</summary>
    public static string? TakeCoTaskString(IntPtr p)
    {
        if (p == IntPtr.Zero) return null;
        try { return Marshal.PtrToStringUni(p); }
        finally { Marshal.FreeCoTaskMem(p); }
    }

    /// <summary>A failed HRESULT as built-in COM interop reports it.</summary>
    public static void Check(int hr)
    {
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
    }

    /// <summary>A method the rewriter could not marshal: called instead of it.</summary>
    public static Exception Unsupported(string member, string why)
    {
        LogOnce("unsupported:" + member, $"{member} was called; its signature is not supported off built-in COM ({why})");
        return new NotSupportedException($"COM call {member}: {why} is not supported by wpfmac's COM-import rewriter");
    }

    private static IntPtr PointerFor(object o, Guid iid)
    {
        if (o is ComObject c) return c.QueryInterface(iid);
        // a managed object: its CCW
        IntPtr unk = Ccw.Instance.GetOrCreateComInterfaceForObject(o, CreateComInterfaceFlags.None);
        if (iid == IID_IUnknown) return unk;
        try
        {
            int hr = Marshal.QueryInterface(unk, in iid, out IntPtr p);
            if (hr < 0)
            {
                LogOnce("ccw:" + o.GetType().FullName + iid, $"a managed {o.GetType().FullName} was passed to native code as interface {iid}: only IUnknown is implemented for managed objects");
                throw new InvalidCastException($"{o.GetType().FullName} has no COM interface {iid} for native code");
            }
            return p;
        }
        finally { Marshal.Release(unk); }
    }

    /// <summary>Before a cast to a rewritten interface: a built-in RCW (from an assembly left on built-in
    /// COM, an ActiveX control, the BCL) becomes the ComObject of the same native object; anything else
    /// is returned as it is.</summary>
    public static object? Adopt(object? o)
    {
        if (o == null || o is ComObject || !OperatingSystem.IsWindows() || !Marshal.IsComObject(o)) return o;
        return Take(Marshal.GetIUnknownForObject(o));
    }

    // ---- System.Runtime.InteropServices.Marshal, rewritten -----------------------------------

    public static object? GetObjectForIUnknown(IntPtr p)
    {
        if (p == IntPtr.Zero) throw new ArgumentNullException(nameof(p));
        return ComObject.Get(p);
    }

    public static object? GetTypedObjectForIUnknown(IntPtr p, Type t) => GetObjectForIUnknown(p);

    public static object? GetUniqueObjectForIUnknown(IntPtr p)
    {
        if (p == IntPtr.Zero) throw new ArgumentNullException(nameof(p));
        return ComObject.GetUnique(p);
    }

    public static IntPtr GetIUnknownForObject(object o) => PointerFor(o ?? throw new ArgumentNullException(nameof(o)), IID_IUnknown);

    public static IntPtr GetComInterfaceForObject(object o, Type t) => PointerFor(o ?? throw new ArgumentNullException(nameof(o)), t.GUID);

    public static IntPtr GetComInterfaceForObject(object o, Type t, CustomQueryInterfaceMode mode) => GetComInterfaceForObject(o, t);

    public static IntPtr GetComInterfaceForObject<T, TInterface>(T o) where T : notnull => GetComInterfaceForObject(o, typeof(TInterface));

    public static int ReleaseComObject(object o)
    {
        if (o is ComObject c) return c.ReleaseRcw(final: false);
        if (OperatingSystem.IsWindows()) return Marshal.ReleaseComObject(o);
        throw new ArgumentException("The object's type must be __ComObject or derived from __ComObject.", nameof(o));
    }

    public static int FinalReleaseComObject(object o)
    {
        if (o is ComObject c) return c.ReleaseRcw(final: true);
        if (OperatingSystem.IsWindows()) return Marshal.FinalReleaseComObject(o);
        throw new ArgumentException("The object's type must be __ComObject or derived from __ComObject.", nameof(o));
    }

    public static bool IsComObject(object o) => o is ComObject || (OperatingSystem.IsWindows() && Marshal.IsComObject(o));

    /// <summary>new C() of a ComImport coclass: CoCreateInstance where the OS has it.</summary>
    public static object CreateInstance(Guid clsid)
    {
        if (!OperatingSystem.IsWindows())
        {
            LogOnce("cocreate:" + clsid, $"CoCreateInstance of {{{clsid}}}: there is no COM class registry on this OS");
            throw new COMException($"Class {{{clsid}}} is not registered", unchecked((int)0x80040154));
        }
        Guid iid = IID_IUnknown;
        int hr = CoCreateInstance(in clsid, IntPtr.Zero, 0x17 /* CLSCTX_ALL */, in iid, out IntPtr p);
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
        return Take(p)!;
    }

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(in Guid clsid, IntPtr outer, uint context, in Guid iid, out IntPtr p);

    /// <summary>Managed objects handed to native code: IUnknown only, for now (the rewriter logs a managed
    /// implementation of a ComImport interface that would need more).</summary>
    private sealed class Ccw : ComWrappers
    {
        public static readonly Ccw Instance = new();

        protected override ComInterfaceEntry* ComputeVtables(object obj, CreateComInterfaceFlags flags, out int count)
        {
            count = 0;
            return null;
        }

        protected override object? CreateObject(IntPtr externalComObject, CreateObjectFlags flags) => null;

        protected override void ReleaseObjects(System.Collections.IEnumerable objects) { }
    }
}

/// <summary>A native COM object, one per identity (its IUnknown): what a rewritten assembly holds where
/// built-in interop would hold an RCW. A cast to a rewritten ComImport interface QueryInterfaces for it.</summary>
public sealed unsafe class ComObject : IDynamicInterfaceCastable
{
    private static readonly Guid IID_IUnknown = new("00000000-0000-0000-C000-000000000046");
    private static readonly Dictionary<IntPtr, WeakReference<ComObject>> s_byIdentity = new();
    private static readonly ConditionalWeakTable<Type, Type?> s_impl = new();

    private IntPtr _identity;                         // its IUnknown, AddRef'd
    private readonly Dictionary<Guid, IntPtr> _ifaces = new();   // QueryInterface results, AddRef'd
    private int _rcwCount = 1;                        // as an RCW's: one per time it was handed out
    private readonly bool _unique;

    private ComObject(IntPtr identity, bool unique) { _identity = identity; _unique = unique; }

    /// <summary>The object for <paramref name="p"/> (the caller keeps its own reference).</summary>
    internal static ComObject Get(IntPtr p)
    {
        IntPtr id = Identity(p);
        lock (s_byIdentity)
        {
            if (s_byIdentity.TryGetValue(id, out var w) && w.TryGetTarget(out var known) && known._identity != IntPtr.Zero)
            {
                Marshal.Release(id);
                Interlocked.Increment(ref known._rcwCount);
                return known;
            }
            var c = new ComObject(id, unique: false);
            s_byIdentity[id] = new WeakReference<ComObject>(c);
            return c;
        }
    }

    internal static ComObject GetUnique(IntPtr p) => new(Identity(p), unique: true);

    private static IntPtr Identity(IntPtr p)
    {
        Guid iid = IID_IUnknown;
        int hr = Marshal.QueryInterface(p, in iid, out IntPtr id);
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
        return id;
    }

    /// <summary>The pointer for <paramref name="iid"/>, kept (not AddRef'd for the caller).</summary>
    internal IntPtr For(Guid iid, bool throwIfNot)
    {
        lock (_ifaces)
        {
            if (_identity == IntPtr.Zero) throw new InvalidComObjectException("COM object that has been separated from its underlying RCW cannot be used.");
            if (_ifaces.TryGetValue(iid, out IntPtr p)) return p;
            int hr = Marshal.QueryInterface(_identity, in iid, out p);
            if (hr < 0)
            {
                if (throwIfNot) throw new InvalidCastException($"The COM object does not support interface {{{iid}}} (0x{hr:x8})");
                return IntPtr.Zero;
            }
            _ifaces[iid] = p;
            return p;
        }
    }

    /// <summary>The pointer for <paramref name="iid"/>, AddRef'd for the caller.</summary>
    internal IntPtr QueryInterface(Guid iid)
    {
        IntPtr p = For(iid, throwIfNot: true);
        Marshal.AddRef(p);
        return p;
    }

    internal int ReleaseRcw(bool final)
    {
        int n = final ? 0 : Interlocked.Decrement(ref _rcwCount);
        if (final) _rcwCount = 0;
        if (n <= 0) Separate();
        return Math.Max(0, n);
    }

    private void Separate()
    {
        IntPtr id;
        lock (_ifaces)
        {
            foreach (IntPtr p in _ifaces.Values) Marshal.Release(p);
            _ifaces.Clear();
            id = _identity;
            _identity = IntPtr.Zero;
        }
        if (id == IntPtr.Zero) return;
        if (!_unique)
            lock (s_byIdentity)
                if (s_byIdentity.TryGetValue(id, out var w) && w.TryGetTarget(out var o) && ReferenceEquals(o, this)) s_byIdentity.Remove(id);
        Marshal.Release(id);
    }

    ~ComObject()
    {
        try { Separate(); } catch { /* finalizer */ }
    }

    private static Type? ImplOf(Type iface) => s_impl.GetValue(iface, t => t.GetNestedType("ComImpl", BindingFlags.Public | BindingFlags.NonPublic));

    bool IDynamicInterfaceCastable.IsInterfaceImplemented(RuntimeTypeHandle interfaceType, bool throwIfNotImplemented)
    {
        Type t = Type.GetTypeFromHandle(interfaceType)!;
        if (ImplOf(t) == null)
        {
            if (throwIfNotImplemented) throw new InvalidCastException($"{t} is not a COM interface wpfmac rewrote");
            return false;
        }
        return For(t.GUID, throwIfNotImplemented) != IntPtr.Zero;
    }

    RuntimeTypeHandle IDynamicInterfaceCastable.GetInterfaceImplementation(RuntimeTypeHandle interfaceType)
        => ImplOf(Type.GetTypeFromHandle(interfaceType)!)!.TypeHandle;
}
