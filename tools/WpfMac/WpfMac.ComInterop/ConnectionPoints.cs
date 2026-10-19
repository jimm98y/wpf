// COM events for rewritten assemblies (Documentation/comimport-rewriter.md):
//
// - the BCL's IConnectionPointContainer, IConnectionPoint, IEnumConnectionPoints and IEnumConnections, by
//   hand both ways (as ComTypes.cs does IStream);
// - the client side of a tlbimp-style event interface ([ComEventInterface(typeof(Source), typeof(Provider))]):
//   its add_/remove_ go to the Provider made for the COM object (EventProvider), which finds the connection
//   point and Advises a sink helper -- a managed dispinterface, whose CCW answers IDispatch (Dispatch.cs);
// - the server side of a managed class with [ComSourceInterfaces]: its CCW answers IConnectionPointContainer,
//   and an Advise'd sink is called, through the source interface, whenever the class raises the .NET event
//   of the same name as a source method -- what built-in COM interop does.

using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace WpfMac.ComInterop;

public static unsafe partial class Com
{
    internal static readonly Guid IID_IConnectionPointContainer = typeof(IConnectionPointContainer).GUID;

    private static readonly ConditionalWeakTable<object, Dictionary<Type, object>> s_providers = new();

    /// <summary>An event interface's provider for COM object <paramref name="self"/> (one per object and
    /// provider type, made with the object, as built-in COM interop makes it).</summary>
    public static object EventProvider(object self, RuntimeTypeHandle provider)
    {
        Type t = Type.GetTypeFromHandle(provider)!;
        var byType = s_providers.GetValue(self, _ => new Dictionary<Type, object>());
        lock (byType)
        {
            if (!byType.TryGetValue(t, out object? p))
                byType[t] = p = Activator.CreateInstance(t, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { self }, null)!;
            return p;
        }
    }
}

internal static unsafe class BclEvents
{
    public static IntPtr Borrow<T>(object? o) => Com.Borrow(o, typeof(T).TypeHandle);
    public static IntPtr Slot(IntPtr self, int k) => (*(IntPtr**)self)[k];
}

// ---- calls into native objects ------------------------------------------------------------------

[DynamicInterfaceCastableImplementation]
internal unsafe interface IConnectionPointContainerImpl : IConnectionPointContainer
{
    private static IntPtr Self(object o) => Com.This(o, typeof(IConnectionPointContainer).TypeHandle);

    void IConnectionPointContainer.EnumConnectionPoints(out IEnumConnectionPoints ppEnum)
    {
        IntPtr p = Self(this), r = IntPtr.Zero;
        Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)BclEvents.Slot(p, 3))(p, &r));
        ppEnum = (IEnumConnectionPoints)Com.Take(r)!;
    }

    void IConnectionPointContainer.FindConnectionPoint(ref Guid riid, out IConnectionPoint ppCP)
    {
        IntPtr p = Self(this), r = IntPtr.Zero;
        fixed (Guid* g = &riid) Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)BclEvents.Slot(p, 4))(p, g, &r));
        ppCP = (IConnectionPoint)Com.Take(r)!;
    }
}

[DynamicInterfaceCastableImplementation]
internal unsafe interface IConnectionPointImpl : IConnectionPoint
{
    private static IntPtr Self(object o) => Com.This(o, typeof(IConnectionPoint).TypeHandle);

    void IConnectionPoint.GetConnectionInterface(out Guid pIID)
    {
        IntPtr p = Self(this);
        Guid g;
        Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, int>)BclEvents.Slot(p, 3))(p, &g));
        pIID = g;
    }

    void IConnectionPoint.GetConnectionPointContainer(out IConnectionPointContainer ppCPC)
    {
        IntPtr p = Self(this), r = IntPtr.Zero;
        Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)BclEvents.Slot(p, 4))(p, &r));
        ppCPC = (IConnectionPointContainer)Com.Take(r)!;
    }

    void IConnectionPoint.Advise(object pUnkSink, out int pdwCookie)
    {
        IntPtr p = Self(this);
        IntPtr u = Com.BorrowUnknown(pUnkSink);
        int cookie = 0;
        try { Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int*, int>)BclEvents.Slot(p, 5))(p, u, &cookie)); }
        finally { Com.Release(u); }
        pdwCookie = cookie;
    }

    void IConnectionPoint.Unadvise(int dwCookie)
    {
        IntPtr p = Self(this);
        Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, int, int>)BclEvents.Slot(p, 6))(p, dwCookie));
    }

    void IConnectionPoint.EnumConnections(out IEnumConnections ppEnum)
    {
        IntPtr p = Self(this), r = IntPtr.Zero;
        Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)BclEvents.Slot(p, 7))(p, &r));
        ppEnum = (IEnumConnections)Com.Take(r)!;
    }
}

[DynamicInterfaceCastableImplementation]
internal unsafe interface IEnumConnectionPointsImpl : IEnumConnectionPoints
{
    private static IntPtr Self(object o) => Com.This(o, typeof(IEnumConnectionPoints).TypeHandle);

    int IEnumConnectionPoints.Next(int celt, IConnectionPoint[] rgelt, IntPtr pceltFetched)
    {
        IntPtr p = Self(this);
        int n = Math.Max(0, celt);
        var buf = (IntPtr*)NativeMemory.AllocZeroed((nuint)Math.Max(1, n), (nuint)sizeof(IntPtr));
        try
        {
            int hr = ((delegate* unmanaged[Stdcall]<IntPtr, int, IntPtr*, IntPtr, int>)BclEvents.Slot(p, 3))(p, celt, buf, pceltFetched);
            for (int i = 0; i < n; i++)
            {
                object? o = Com.Take(buf[i]);              // the callee's references, taken over
                if (rgelt != null && i < rgelt.Length) rgelt[i] = (IConnectionPoint)o!;
            }
            return hr;
        }
        finally { NativeMemory.Free(buf); }
    }

    int IEnumConnectionPoints.Skip(int celt) { IntPtr p = Self(this); return ((delegate* unmanaged[Stdcall]<IntPtr, int, int>)BclEvents.Slot(p, 4))(p, celt); }

    void IEnumConnectionPoints.Reset() { IntPtr p = Self(this); Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, int>)BclEvents.Slot(p, 5))(p)); }

    void IEnumConnectionPoints.Clone(out IEnumConnectionPoints ppenum)
    {
        IntPtr p = Self(this), r = IntPtr.Zero;
        Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)BclEvents.Slot(p, 6))(p, &r));
        ppenum = (IEnumConnectionPoints)Com.Take(r)!;
    }
}

/// <summary>CONNECTDATA as native code lays it out.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NativeConnectData { public IntPtr Unk; public int Cookie; }

[DynamicInterfaceCastableImplementation]
internal unsafe interface IEnumConnectionsImpl : IEnumConnections
{
    private static IntPtr Self(object o) => Com.This(o, typeof(IEnumConnections).TypeHandle);

    int IEnumConnections.Next(int celt, CONNECTDATA[] rgelt, IntPtr pceltFetched)
    {
        IntPtr p = Self(this);
        int n = Math.Max(0, celt);
        var buf = (NativeConnectData*)NativeMemory.AllocZeroed((nuint)Math.Max(1, n), (nuint)sizeof(NativeConnectData));
        try
        {
            int hr = ((delegate* unmanaged[Stdcall]<IntPtr, int, NativeConnectData*, IntPtr, int>)BclEvents.Slot(p, 3))(p, celt, buf, pceltFetched);
            for (int i = 0; i < n; i++)
            {
                object? o = Com.Take(buf[i].Unk);
                if (rgelt != null && i < rgelt.Length) rgelt[i] = new CONNECTDATA { pUnk = o!, dwCookie = buf[i].Cookie };
            }
            return hr;
        }
        finally { NativeMemory.Free(buf); }
    }

    int IEnumConnections.Skip(int celt) { IntPtr p = Self(this); return ((delegate* unmanaged[Stdcall]<IntPtr, int, int>)BclEvents.Slot(p, 4))(p, celt); }

    void IEnumConnections.Reset() { IntPtr p = Self(this); Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, int>)BclEvents.Slot(p, 5))(p)); }

    void IEnumConnections.Clone(out IEnumConnections ppenum)
    {
        IntPtr p = Self(this), r = IntPtr.Zero;
        Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)BclEvents.Slot(p, 6))(p, &r));
        ppenum = (IEnumConnections)Com.Take(r)!;
    }
}

// ---- managed objects handed to native code ----------------------------------------------------------

internal static unsafe class ConnectionPointContainerCcw
{
    /// <summary>The object behind the pointer: a managed IConnectionPointContainer, or a [ComSourceInterfaces]
    /// class (its container is made here).</summary>
    private static IConnectionPointContainer Target(IntPtr self)
    {
        object o = Com.CcwTarget(self);
        return o as IConnectionPointContainer ?? EventSource.ContainerFor(o);
    }

    public static IntPtr Vtable()
    {
        var vt = (IntPtr*)Com.NewVtable(5);
        vt[3] = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr*, int>)&EnumConnectionPoints;
        vt[4] = (IntPtr)(delegate* unmanaged<IntPtr, Guid*, IntPtr*, int>)&FindConnectionPoint;
        return (IntPtr)vt;
    }

    [UnmanagedCallersOnly]
    private static int EnumConnectionPoints(IntPtr self, IntPtr* r)
    {
        try { *r = IntPtr.Zero; Target(self).EnumConnectionPoints(out var e); *r = BclEvents.Borrow<IEnumConnectionPoints>(e); return 0; }
        catch (Exception e) { return Com.HResultOf(e); }
    }

    [UnmanagedCallersOnly]
    private static int FindConnectionPoint(IntPtr self, Guid* riid, IntPtr* r)
    {
        try { *r = IntPtr.Zero; Guid g = *riid; Target(self).FindConnectionPoint(ref g, out var cp); *r = BclEvents.Borrow<IConnectionPoint>(cp); return 0; }
        catch (Exception e) { return Com.HResultOf(e); }
    }
}

internal static unsafe class ConnectionPointCcw
{
    private static IConnectionPoint Target(IntPtr self) => (IConnectionPoint)Com.CcwTarget(self);

    public static IntPtr Vtable()
    {
        var vt = (IntPtr*)Com.NewVtable(8);
        vt[3] = (IntPtr)(delegate* unmanaged<IntPtr, Guid*, int>)&GetConnectionInterface;
        vt[4] = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr*, int>)&GetConnectionPointContainer;
        vt[5] = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, int*, int>)&Advise;
        vt[6] = (IntPtr)(delegate* unmanaged<IntPtr, int, int>)&Unadvise;
        vt[7] = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr*, int>)&EnumConnections;
        return (IntPtr)vt;
    }

    [UnmanagedCallersOnly]
    private static int GetConnectionInterface(IntPtr self, Guid* iid)
    {
        try { Target(self).GetConnectionInterface(out Guid g); *iid = g; return 0; }
        catch (Exception e) { return Com.HResultOf(e); }
    }

    [UnmanagedCallersOnly]
    private static int GetConnectionPointContainer(IntPtr self, IntPtr* r)
    {
        try { *r = IntPtr.Zero; Target(self).GetConnectionPointContainer(out var c); *r = BclEvents.Borrow<IConnectionPointContainer>(c); return 0; }
        catch (Exception e) { return Com.HResultOf(e); }
    }

    [UnmanagedCallersOnly]
    private static int Advise(IntPtr self, IntPtr sink, int* cookie)
    {
        try { *cookie = 0; Target(self).Advise(Com.Wrap(sink)!, out int c); *cookie = c; return 0; }
        catch (Exception e) { return Com.HResultOf(e); }
    }

    [UnmanagedCallersOnly]
    private static int Unadvise(IntPtr self, int cookie)
    {
        try { Target(self).Unadvise(cookie); return 0; }
        catch (Exception e) { return Com.HResultOf(e); }
    }

    [UnmanagedCallersOnly]
    private static int EnumConnections(IntPtr self, IntPtr* r)
    {
        try { *r = IntPtr.Zero; Target(self).EnumConnections(out var e); *r = BclEvents.Borrow<IEnumConnections>(e); return 0; }
        catch (Exception e) { return Com.HResultOf(e); }
    }
}

internal static unsafe class EnumConnectionPointsCcw
{
    private static IEnumConnectionPoints Target(IntPtr self) => (IEnumConnectionPoints)Com.CcwTarget(self);

    public static IntPtr Vtable()
    {
        var vt = (IntPtr*)Com.NewVtable(7);
        vt[3] = (IntPtr)(delegate* unmanaged<IntPtr, int, IntPtr*, IntPtr, int>)&Next;
        vt[4] = (IntPtr)(delegate* unmanaged<IntPtr, int, int>)&Skip;
        vt[5] = (IntPtr)(delegate* unmanaged<IntPtr, int>)&Reset;
        vt[6] = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr*, int>)&Clone;
        return (IntPtr)vt;
    }

    [UnmanagedCallersOnly]
    private static int Next(IntPtr self, int celt, IntPtr* rgelt, IntPtr fetched)
    {
        try
        {
            var a = new IConnectionPoint[Math.Max(0, celt)];
            int hr = Target(self).Next(celt, a, fetched);
            for (int i = 0; i < a.Length; i++) rgelt[i] = BclEvents.Borrow<IConnectionPoint>(a[i]);
            return hr;
        }
        catch (Exception e) { return Com.HResultOf(e); }
    }

    [UnmanagedCallersOnly] private static int Skip(IntPtr self, int celt) { try { return Target(self).Skip(celt); } catch (Exception e) { return Com.HResultOf(e); } }
    [UnmanagedCallersOnly] private static int Reset(IntPtr self) { try { Target(self).Reset(); return 0; } catch (Exception e) { return Com.HResultOf(e); } }

    [UnmanagedCallersOnly]
    private static int Clone(IntPtr self, IntPtr* r)
    {
        try { *r = IntPtr.Zero; Target(self).Clone(out var c); *r = BclEvents.Borrow<IEnumConnectionPoints>(c); return 0; }
        catch (Exception e) { return Com.HResultOf(e); }
    }
}

internal static unsafe class EnumConnectionsCcw
{
    private static IEnumConnections Target(IntPtr self) => (IEnumConnections)Com.CcwTarget(self);

    public static IntPtr Vtable()
    {
        var vt = (IntPtr*)Com.NewVtable(7);
        vt[3] = (IntPtr)(delegate* unmanaged<IntPtr, int, NativeConnectData*, IntPtr, int>)&Next;
        vt[4] = (IntPtr)(delegate* unmanaged<IntPtr, int, int>)&Skip;
        vt[5] = (IntPtr)(delegate* unmanaged<IntPtr, int>)&Reset;
        vt[6] = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr*, int>)&Clone;
        return (IntPtr)vt;
    }

    [UnmanagedCallersOnly]
    private static int Next(IntPtr self, int celt, NativeConnectData* rgelt, IntPtr fetched)
    {
        try
        {
            var a = new CONNECTDATA[Math.Max(0, celt)];
            int hr = Target(self).Next(celt, a, fetched);
            for (int i = 0; i < a.Length; i++) { rgelt[i].Unk = Com.BorrowUnknown(a[i].pUnk); rgelt[i].Cookie = a[i].dwCookie; }
            return hr;
        }
        catch (Exception e) { return Com.HResultOf(e); }
    }

    [UnmanagedCallersOnly] private static int Skip(IntPtr self, int celt) { try { return Target(self).Skip(celt); } catch (Exception e) { return Com.HResultOf(e); } }
    [UnmanagedCallersOnly] private static int Reset(IntPtr self) { try { Target(self).Reset(); return 0; } catch (Exception e) { return Com.HResultOf(e); } }

    [UnmanagedCallersOnly]
    private static int Clone(IntPtr self, IntPtr* r)
    {
        try { *r = IntPtr.Zero; Target(self).Clone(out var c); *r = BclEvents.Borrow<IEnumConnections>(c); return 0; }
        catch (Exception e) { return Com.HResultOf(e); }
    }
}

// ---- the server side: a managed class with [ComSourceInterfaces] -------------------------------------

/// <summary>The connection points of a managed event source, made when native code first asks for its
/// IConnectionPointContainer.</summary>
internal static class EventSource
{
    private static readonly ConditionalWeakTable<object, Container> s_containers = new();

    /// <summary>The source interfaces of <paramref name="t"/>'s [ComSourceInterfaces] (null when it has none).</summary>
    public static Type[]? SourceInterfaces(Type t)
    {
        var a = t.GetCustomAttribute<ComSourceInterfacesAttribute>(inherit: true);
        if (a == null) return null;
        return a.Value.Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Select(n => Type.GetType(n, throwOnError: false) ?? t.Assembly.GetType(n.Split(',')[0].Trim(), throwOnError: false))
            .Where(x => x != null).ToArray()!;
    }

    public static IConnectionPointContainer ContainerFor(object source) =>
        s_containers.GetValue(source, o => new Container(o, SourceInterfaces(o.GetType()) ?? throw new InvalidCastException($"{o.GetType()} has no [ComSourceInterfaces]")));

    private sealed class Container : IConnectionPointContainer
    {
        private readonly Point[] _points;
        public Container(object source, Type[] interfaces) { _points = interfaces.Select(i => new Point(this, source, i)).ToArray(); }

        public void EnumConnectionPoints(out IEnumConnectionPoints ppEnum) => ppEnum = new PointEnum(_points, 0);

        public void FindConnectionPoint(ref Guid riid, out IConnectionPoint ppCP)
        {
            Guid g = riid;
            ppCP = _points.FirstOrDefault(p => p.Interface.GUID == g) ?? throw new COMException("No such connection point", unchecked((int)0x80040200));   // CONNECT_E_NOCONNECTION
        }
    }

    private sealed class Point : IConnectionPoint
    {
        private readonly Container _container;
        private readonly object _source;
        public readonly Type Interface;
        private readonly Dictionary<int, (object Sink, List<(EventInfo Event, Delegate Handler)> Hooks)> _sinks = new();
        private int _next = 1;

        public Point(Container c, object source, Type iface) { _container = c; _source = source; Interface = iface; }

        public void GetConnectionInterface(out Guid pIID) => pIID = Interface.GUID;
        public void GetConnectionPointContainer(out IConnectionPointContainer ppCPC) => ppCPC = _container;

        /// <summary>The sink is called through the source interface whenever the source raises the event
        /// named as a source method (the delegate's arguments converted to the method's).</summary>
        public void Advise(object pUnkSink, out int pdwCookie)
        {
            if (!Interface.IsInstanceOfType(pUnkSink)) throw new InvalidCastException($"the sink does not implement {Interface}");      // CONNECT_E_CANNOTCONNECT
            var hooks = new List<(EventInfo, Delegate)>();
            foreach (var m in Interface.GetMethods())
            {
                var ev = _source.GetType().GetEvent(m.Name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (ev?.EventHandlerType == null) continue;
                var invoke = ev.EventHandlerType.GetMethod("Invoke")!;
                var ps = invoke.GetParameters().Select(p => Expression.Parameter(p.ParameterType, p.Name)).ToArray();
                var mps = m.GetParameters();
                if (mps.Length != ps.Length) continue;
                Expression call = Expression.Call(Expression.Convert(Expression.Constant(pUnkSink), Interface), m,
                    ps.Select((p, i) => p.Type == mps[i].ParameterType ? (Expression)p : Expression.Convert(p, mps[i].ParameterType)));
                if (invoke.ReturnType == typeof(void)) { if (m.ReturnType != typeof(void)) call = Expression.Block(typeof(void), call); }
                else call = m.ReturnType == invoke.ReturnType ? call : Expression.Convert(call, invoke.ReturnType);
                var handler = Expression.Lambda(ev.EventHandlerType, call, ps).Compile();
                ev.AddEventHandler(_source, handler);
                hooks.Add((ev, handler));
            }
            lock (_sinks) { pdwCookie = _next++; _sinks[pdwCookie] = (pUnkSink, hooks); }
        }

        public void Unadvise(int dwCookie)
        {
            (object Sink, List<(EventInfo Event, Delegate Handler)> Hooks) s;
            lock (_sinks) { if (!_sinks.Remove(dwCookie, out s)) throw new COMException("Not connected", unchecked((int)0x80040200)); }
            foreach (var (ev, h) in s.Hooks) ev.RemoveEventHandler(_source, h);
        }

        public void EnumConnections(out IEnumConnections ppEnum)
        {
            lock (_sinks) ppEnum = new ConnectionEnum(_sinks.Select(kv => new CONNECTDATA { pUnk = kv.Value.Sink, dwCookie = kv.Key }).ToArray(), 0);
        }
    }

    private sealed class PointEnum : IEnumConnectionPoints
    {
        private readonly IConnectionPoint[] _all; private int _at;
        public PointEnum(IConnectionPoint[] all, int at) { _all = all; _at = at; }
        public int Next(int celt, IConnectionPoint[] rgelt, IntPtr pceltFetched)
        {
            int n = 0;
            for (; n < celt && _at < _all.Length; n++) rgelt[n] = _all[_at++];
            if (pceltFetched != IntPtr.Zero) Marshal.WriteInt32(pceltFetched, n);
            return n == celt ? 0 : 1;
        }
        public int Skip(int celt) { _at = Math.Min(_all.Length, _at + celt); return 0; }
        public void Reset() => _at = 0;
        public void Clone(out IEnumConnectionPoints ppenum) => ppenum = new PointEnum(_all, _at);
    }

    private sealed class ConnectionEnum : IEnumConnections
    {
        private readonly CONNECTDATA[] _all; private int _at;
        public ConnectionEnum(CONNECTDATA[] all, int at) { _all = all; _at = at; }
        public int Next(int celt, CONNECTDATA[] rgelt, IntPtr pceltFetched)
        {
            int n = 0;
            for (; n < celt && _at < _all.Length; n++) rgelt[n] = _all[_at++];
            if (pceltFetched != IntPtr.Zero) Marshal.WriteInt32(pceltFetched, n);
            return n == celt ? 0 : 1;
        }
        public int Skip(int celt) { _at = Math.Min(_all.Length, _at + celt); return 0; }
        public void Reset() => _at = 0;
        public void Clone(out IEnumConnections ppenum) => ppenum = new ConnectionEnum(_all, _at);
    }
}
