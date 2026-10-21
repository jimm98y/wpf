// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The COM objects of the Direct3D shim (Documentation/d3d11-webgpu-shim.md, section 2). No COM interop:
// an object's identity is a block of native memory per interface it exposes,
//
//     [ vtable* ][ GCHandle of the owner ][ index of the interface ]
//
// and every vtable slot is an [UnmanagedCallersOnly] static that finds its owner through the handle.
// SharpDX calls these through calli by slot number; on Windows DXEngine's [ComImport] wrappers call
// them through built-in COM interop, which needs nothing more than a correct IUnknown.
//

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Microsoft.Wpf.Interop.WebGpu.Direct3D
{
    internal static class HResult
    {
        public const int S_OK = 0;
        public const int S_FALSE = 1;
        public const int E_NOTIMPL = unchecked((int)0x80004001);
        public const int E_NOINTERFACE = unchecked((int)0x80004002);
        public const int E_POINTER = unchecked((int)0x80004003);
        public const int E_FAIL = unchecked((int)0x80004005);
        public const int E_INVALIDARG = unchecked((int)0x80070057);
        public const int E_OUTOFMEMORY = unchecked((int)0x8007000E);
        public const int DXGI_ERROR_NOT_FOUND = unchecked((int)0x887A0002);
        public const int DXGI_ERROR_INVALID_CALL = unchecked((int)0x887A0001);
        public const int DXGI_ERROR_DEVICE_REMOVED = unchecked((int)0x887A0005);
        public const int DXGI_ERROR_WAS_STILL_DRAWING = unchecked((int)0x887A000A);
        public const int DXGI_ERROR_UNSUPPORTED = unchecked((int)0x887A0004);
    }

    /// <summary>The shim's log: each distinct message once, to the compatibility log when the launcher
    /// gave us one, else stderr.</summary>
    internal static class D3DLog
    {
        private static readonly HashSet<string> s_seen = new();
        internal static Action<string, string>? LogOnce;
        internal static readonly bool Trace = Environment.GetEnvironmentVariable("WPF_D3D11_TRACE") == "1";

        public static void Once(string key, string message)
        {
            lock (s_seen)
            {
                if (!s_seen.Add(key)) return;
            }
            // the compatibility log of wpfrun's startup hook, when there is one
            LogOnce ??= AppContext.GetData("WpfWebGpu.LogOnce") as Action<string, string>;
            if (LogOnce != null) LogOnce(key, "d3d11 shim: " + message);
            else Console.Error.WriteLine("d3d11 shim: " + message);
        }

        public static void Error(string where, Exception e) => Once("err:" + where + ":" + e.GetType().Name, $"{where} failed: {e}");
    }

    /// <summary>
    /// A vtable: one per interface, built once, never freed. Slots nobody implements answer
    /// E_NOTIMPL (or do nothing, for a void method -- the same thing at the ABI) and log once which
    /// interface method it was.
    /// </summary>
    internal sealed unsafe class Vtable
    {
        public readonly string Name;
        public readonly Guid[] Iids;
        public readonly IntPtr* Slots;
        public readonly int Count;

        public Vtable(string name, int count, params Guid[] iids)
        {
            Name = name;
            Count = count;
            Iids = iids;
            Slots = (IntPtr*)NativeMemory.AllocZeroed((nuint)(count * sizeof(IntPtr)));
            for (int i = 0; i < count; i++) Slots[i] = NotImplementedStubs.Get(i);
            Slots[0] = (IntPtr)(delegate* unmanaged<IntPtr, Guid*, IntPtr*, int>)&ComObject.QueryInterface;
            Slots[1] = (IntPtr)(delegate* unmanaged<IntPtr, uint>)&ComObject.AddRefSlot;
            Slots[2] = (IntPtr)(delegate* unmanaged<IntPtr, uint>)&ComObject.ReleaseSlot;
        }

        public void Set(int slot, void* fn) => Slots[slot] = (IntPtr)fn;
    }

    internal abstract unsafe class ComObject
    {
        private readonly Vtable[] _interfaces;
        private readonly IntPtr[] _blocks;
        private GCHandle _handle;
        private int _refs = 1;
        private int _internalRefs;

        protected ComObject(params Vtable[] interfaces)
        {
            _interfaces = interfaces;
            _blocks = new IntPtr[interfaces.Length];
            _handle = GCHandle.Alloc(this, GCHandleType.Normal);
            for (int i = 0; i < interfaces.Length; i++)
            {
                IntPtr* b = (IntPtr*)NativeMemory.AllocZeroed((nuint)(3 * sizeof(IntPtr)));
                b[0] = (IntPtr)interfaces[i].Slots;
                b[1] = GCHandle.ToIntPtr(_handle);
                b[2] = i;
                _blocks[i] = (IntPtr)b;
            }
        }

        /// <summary>The object's pointer for its first interface (the one it was created as).</summary>
        public IntPtr Pointer => _blocks[0];

        /// <summary>Its pointer for the interface whose vtable is <paramref name="vt"/>.</summary>
        public IntPtr PointerFor(Vtable vt)
        {
            for (int i = 0; i < _interfaces.Length; i++) if (ReferenceEquals(_interfaces[i], vt)) return _blocks[i];
            return IntPtr.Zero;
        }

        public string InterfaceName(int index) => (uint)index < (uint)_interfaces.Length ? _interfaces[index].Name : GetType().Name;

        public static ComObject? From(IntPtr p)
        {
            if (p == IntPtr.Zero) return null;
            IntPtr h = ((IntPtr*)p)[1];
            return h == IntPtr.Zero ? null : (ComObject?)GCHandle.FromIntPtr(h).Target;
        }

        public static T? From<T>(IntPtr p) where T : ComObject => From(p) as T;

        /// <summary>Hands out a reference: the caller now owns one more.</summary>
        public IntPtr AddRefPointer(Vtable? vt = null)
        {
            IncRef();
            return vt == null ? Pointer : PointerFor(vt);
        }

        public uint AddRef() => (uint)IncRef();

        public uint Release()
        {
            int n = Interlocked.Decrement(ref _refs);
            if (n == 0 && !OnNoReferences()) Final();
            return (uint)Math.Max(n, 0);
        }

        private int IncRef()
        {
            int n = Interlocked.Increment(ref _refs);
            if (n == 1) OnReferenced();
            return n;
        }

        /// <summary>The application's references went from none to one (an object that can be held
        /// at zero, see <see cref="OnNoReferences"/>).</summary>
        protected virtual void OnReferenced() { }

        /// <summary>The application let go of its last reference. True keeps the object (one whose
        /// lifetime is another's: a D3D9 texture's level shares its texture's count); false, the
        /// default, finalises it once the shim's own references are gone too.</summary>
        protected virtual bool OnNoReferences() => false;

        /// <summary>A reference the shim itself holds (a bound view, a view's resource): keeps the
        /// object's GPU resources alive past the app's last Release.</summary>
        public void InternalAddRef() => Interlocked.Increment(ref _internalRefs);

        public void InternalRelease()
        {
            if (Interlocked.Decrement(ref _internalRefs) == 0 && Volatile.Read(ref _refs) <= 0) Final();
        }

        private int _finalised;

        private void Final()
        {
            if (Volatile.Read(ref _internalRefs) > 0) return;
            if (Interlocked.Exchange(ref _finalised, 1) != 0) return;
            try { OnFinalRelease(); }
            catch (Exception e) { D3DLog.Error(GetType().Name + ".Release", e); }
            foreach (IntPtr b in _blocks) NativeMemory.Free((void*)b);
            if (_handle.IsAllocated) _handle.Free();
        }

        /// <summary>Releases what the object owns (GPU resources, references it holds).</summary>
        protected virtual void OnFinalRelease() { }

        /// <summary>QueryInterface beyond the object's own vtables (an object that exposes another
        /// object's interface, e.g. a device's IDXGIDevice). Null when not supported.</summary>
        protected virtual IntPtr QueryOther(in Guid iid) => IntPtr.Zero;

        protected static readonly Guid IID_IUnknown = new("00000000-0000-0000-C000-000000000046");

        private int Query(in Guid iid, IntPtr* ppv)
        {
            if (iid == IID_IUnknown) { *ppv = AddRefPointer(); return HResult.S_OK; }
            for (int i = 0; i < _interfaces.Length; i++)
            {
                foreach (Guid g in _interfaces[i].Iids)
                {
                    if (g == iid)
                    {
                        IncRef();
                        *ppv = _blocks[i];
                        return HResult.S_OK;
                    }
                }
            }
            IntPtr other = QueryOther(iid);
            if (other != IntPtr.Zero) { *ppv = other; return HResult.S_OK; }
            *ppv = IntPtr.Zero;
            if (D3DLog.Trace) D3DLog.Once("qi:" + GetType().Name + iid, $"{GetType().Name}: no interface {iid}");
            return HResult.E_NOINTERFACE;
        }

        [UnmanagedCallersOnly]
        internal static int QueryInterface(IntPtr self, Guid* iid, IntPtr* ppv)
        {
            if (ppv == null) return HResult.E_POINTER;
            ComObject? o = From(self);
            if (o == null || iid == null) { *ppv = IntPtr.Zero; return HResult.E_INVALIDARG; }
            try { return o.Query(*iid, ppv); }
            catch (Exception e) { D3DLog.Error("QueryInterface", e); *ppv = IntPtr.Zero; return HResult.E_NOINTERFACE; }
        }

        [UnmanagedCallersOnly]
        internal static uint AddRefSlot(IntPtr self) => From(self)?.AddRef() ?? 0;

        [UnmanagedCallersOnly]
        internal static uint ReleaseSlot(IntPtr self) => From(self)?.Release() ?? 0;

        /// <summary>Writes an object's pointer (with a reference) to an out parameter, or nothing when
        /// the parameter is null (the API's "validate only" form).</summary>
        public static int Out(IntPtr* pp, ComObject o, Vtable? vt = null)
        {
            if (pp == null) { o.Release(); return HResult.S_FALSE; }
            *pp = vt == null ? o.Pointer : o.PointerFor(vt);
            return HResult.S_OK;
        }
    }
}
