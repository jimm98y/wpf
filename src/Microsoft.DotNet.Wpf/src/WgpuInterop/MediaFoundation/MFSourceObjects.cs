// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// What a media source is described and talks by (Documentation/media-foundation-shim.md, "Media sources"):
// IMFMediaEvent, IMFMediaEventQueue (MFCreateEventQueue), IMFStreamDescriptor and IMFMediaTypeHandler
// (MFCreateStreamDescriptor), IMFPresentationDescriptor (MFCreatePresentationDescriptor) -- for sources of the
// shim's own and of the application's alike, with Windows' semantics (probed: MFSourceObjectsProbe).
//

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Wpf.Interop.WebGpu.Direct3D;

namespace Microsoft.Wpf.Interop.WebGpu.MediaFoundation
{
    internal static class MFE
    {
        public const int MF_E_NO_EVENTS_AVAILABLE = unchecked((int)0xC00D3E80), MF_E_MULTIPLE_BEGIN = unchecked((int)0xC00D36D9);
        public const int MF_E_NOT_INITIALIZED = unchecked((int)0xC00D36B6), MF_E_INVALIDREQUEST = unchecked((int)0xC00D36B2);
        public static readonly Guid IID_IMFMediaEvent = new("df598932-f10c-4e39-bba2-c308f101daa3"), IID_IMFMediaEventQueue = new("36f846fc-2256-48b6-b58e-e2b638316581");
        public static readonly Guid IID_IMFStreamDescriptor = new("56c03d9c-9dbb-45f5-ab4b-d80f47c05938"), IID_IMFMediaTypeHandler = new("e93dcf6c-4b07-4e1e-8123-aa16ed6eadf5");
        public static readonly Guid IID_IMFPresentationDescriptor = new("03cb2711-24d7-4db6-a17f-f3a7a479a536");
    }

    /// <summary>IMFMediaEvent: a type, an extended type, a status, a value; attributes besides.</summary>
    internal sealed unsafe class MFMediaEvent : MFAttributesObject
    {
        private static readonly Vtable s_vt = Build();
        public readonly uint Type;
        public readonly Guid Extended;
        public readonly int Status;
        public readonly MFValue? Value;

        private static Vtable Build()
        {
            var vt = new Vtable("IMFMediaEvent", 37, MFE.IID_IMFMediaEvent, MF.IID_IMFAttributes);
            FillAttributes(vt);
            vt.Set(33, (delegate* unmanaged<IntPtr, uint*, int>)&GetType);
            vt.Set(34, (delegate* unmanaged<IntPtr, Guid*, int>)&GetExtendedType);
            vt.Set(35, (delegate* unmanaged<IntPtr, int*, int>)&GetStatus);
            vt.Set(36, (delegate* unmanaged<IntPtr, byte*, int>)&GetValue);
            return vt;
        }

        public MFMediaEvent(uint type, Guid extended, int status, MFValue? value) : base(s_vt)
        {
            Type = type; Extended = extended; Status = status; Value = value;
        }

        protected override void OnFinalRelease() { Value?.Release(); base.OnFinalRelease(); }

        private static MFMediaEvent? E(IntPtr self) => From<MFMediaEvent>(self);
        [UnmanagedCallersOnly] private static int GetType(IntPtr self, uint* t) { if (E(self) is not { } e || t == null) return MF.E_POINTER; *t = e.Type; return MF.S_OK; }
        [UnmanagedCallersOnly] private static int GetExtendedType(IntPtr self, Guid* g) { if (E(self) is not { } e || g == null) return MF.E_POINTER; *g = e.Extended; return MF.S_OK; }
        [UnmanagedCallersOnly] private static int GetStatus(IntPtr self, int* s) { if (E(self) is not { } e || s == null) return MF.E_POINTER; *s = e.Status; return MF.S_OK; }

        [UnmanagedCallersOnly]
        private static int GetValue(IntPtr self, byte* pv)
        {
            if (E(self) is not { } e || pv == null) return MF.E_POINTER;
            new Span<byte>(pv, MF.PropVariantSize).Clear();
            if (e.Value != null) MFAttributeStore.Write(e.Value, pv);           // an IUnknown AddRef'd by Write: the caller's
            return MF.S_OK;
        }

        /// <summary>A PROPVARIANT as an event's value (its IUnknown AddRef'd); null of VT_EMPTY.</summary>
        public static MFValue? ValueOf(byte* pv)
        {
            if (pv == null || *(ushort*)pv == 0) return null;
            if (*(ushort*)pv == MF.VT_UNKNOWN) { IntPtr u = *(IntPtr*)(pv + 8); Com.AddRef(u); return new MFValue { Type = MF.VT_UNKNOWN, Unknown = u }; }
            if (*(ushort*)pv == 20) return new MFValue { Type = 20, U = (ulong)*(long*)(pv + 8) };              // VT_I8
            return MFAttributeStore.Read(pv);
        }
    }

    /// <summary>IMFMediaEventQueue: events in order, waited for, or handed to a callback as they come.</summary>
    internal sealed unsafe class MFEventQueue : ComObject
    {
        private static readonly Vtable s_vt = Build();
        private readonly Queue<IntPtr> _events = new();             // IMFMediaEvent, AddRef'd
        private readonly object _lock = new();
        private IntPtr _callback, _state;                           // the BeginGetEvent waiting, AddRef'd
        private bool _shutdown;

        private static Vtable Build()
        {
            var vt = new Vtable("IMFMediaEventQueue", 11, MFE.IID_IMFMediaEventQueue);
            vt.Set(3, (delegate* unmanaged<IntPtr, uint, IntPtr*, int>)&GetEventSlot);
            vt.Set(4, (delegate* unmanaged<IntPtr, IntPtr, IntPtr, int>)&BeginGetEventSlot);
            vt.Set(5, (delegate* unmanaged<IntPtr, IntPtr, IntPtr*, int>)&EndGetEventSlot);
            vt.Set(6, (delegate* unmanaged<IntPtr, IntPtr, int>)&QueueEventSlot);
            vt.Set(7, (delegate* unmanaged<IntPtr, uint, Guid*, int, byte*, int>)&QueueEventParamVar);
            vt.Set(8, (delegate* unmanaged<IntPtr, uint, Guid*, int, IntPtr, int>)&QueueEventParamUnk);
            vt.Set(9, (delegate* unmanaged<IntPtr, int>)&ShutdownSlot);
            return vt;
        }

        public MFEventQueue() : base(s_vt) { }

        protected override void OnFinalRelease() => Shutdown();

        private static MFEventQueue? Q(IntPtr self) => From<MFEventQueue>(self);

        /// <summary>The next event (the caller's), waiting for one unless told not to (MF_EVENT_FLAG_NO_WAIT).</summary>
        public int GetEvent(bool noWait, out IntPtr e)
        {
            e = IntPtr.Zero;
            lock (_lock)
            {
                while (true)
                {
                    if (_shutdown) return MF.MF_E_SHUTDOWN;
                    if (_callback != IntPtr.Zero) return MFE.MF_E_MULTIPLE_BEGIN;
                    if (_events.Count > 0) { e = _events.Dequeue(); return MF.S_OK; }
                    if (noWait) return MFE.MF_E_NO_EVENTS_AVAILABLE;
                    Monitor.Wait(_lock);
                }
            }
        }

        public int BeginGetEvent(IntPtr callback, IntPtr state)
        {
            if (callback == IntPtr.Zero) return MF.E_POINTER;
            lock (_lock)
            {
                if (_shutdown) return MF.MF_E_SHUTDOWN;
                if (_callback != IntPtr.Zero) return MFE.MF_E_MULTIPLE_BEGIN;
                Com.AddRef(callback); Com.AddRef(state);
                _callback = callback; _state = state;
                Dispatch();
            }
            return MF.S_OK;
        }

        /// <summary>The waiting callback, of the next event, where there are both: invoked on the thread pool.</summary>
        private void Dispatch()
        {
            if (_callback == IntPtr.Zero || _events.Count == 0) return;
            IntPtr e = _events.Dequeue(), cb = _callback, st = _state;
            _callback = _state = IntPtr.Zero;
            var result = new Source.MFAsyncResult(e, cb, st);
            Com.Release(e); Com.Release(cb); Com.Release(st);
            result.InvokeLater();
        }

        public int EndGetEvent(IntPtr result, out IntPtr e)
        {
            e = IntPtr.Zero;
            if (result == IntPtr.Zero) return MF.E_POINTER;
            IntPtr obj;
            int hr = ((delegate* unmanaged<IntPtr, IntPtr*, int>)Com.Fn(result, 6))(result, &obj);           // IMFAsyncResult::GetObject
            if (hr < 0) return hr;
            e = obj;
            return MF.S_OK;
        }

        public int QueueEvent(IntPtr e)
        {
            if (e == IntPtr.Zero) return MF.E_POINTER;
            lock (_lock)
            {
                if (_shutdown) return MF.MF_E_SHUTDOWN;
                Com.AddRef(e);
                _events.Enqueue(e);
                Monitor.PulseAll(_lock);
                Dispatch();
            }
            return MF.S_OK;
        }

        public int QueueEvent(uint type, Guid extended, int status, MFValue? value)
        {
            var e = new MFMediaEvent(type, extended, status, value);
            int hr = QueueEvent(e.Pointer);
            e.Release();
            return hr;
        }

        public void Shutdown()
        {
            lock (_lock)
            {
                if (_shutdown) return;
                _shutdown = true;
                while (_events.Count > 0) Com.Release(_events.Dequeue());
                Com.Release(_callback); Com.Release(_state);
                _callback = _state = IntPtr.Zero;
                Monitor.PulseAll(_lock);
            }
        }

        [UnmanagedCallersOnly]
        private static int GetEventSlot(IntPtr self, uint flags, IntPtr* e)
        {
            if (Q(self) is not { } q || e == null) return MF.E_POINTER;
            int hr = q.GetEvent((flags & 1) != 0, out var ev);
            *e = ev;
            return hr;
        }

        [UnmanagedCallersOnly] private static int BeginGetEventSlot(IntPtr self, IntPtr callback, IntPtr state) => Q(self)?.BeginGetEvent(callback, state) ?? MF.E_POINTER;

        [UnmanagedCallersOnly]
        private static int EndGetEventSlot(IntPtr self, IntPtr result, IntPtr* e)
        {
            if (Q(self) is not { } q || e == null) return MF.E_POINTER;
            int hr = q.EndGetEvent(result, out var ev);
            *e = ev;
            return hr;
        }

        [UnmanagedCallersOnly] private static int QueueEventSlot(IntPtr self, IntPtr e) => Q(self)?.QueueEvent(e) ?? MF.E_POINTER;
        [UnmanagedCallersOnly] private static int QueueEventParamVar(IntPtr self, uint type, Guid* ext, int status, byte* pv) => Q(self)?.QueueEvent(type, ext == null ? Guid.Empty : *ext, status, MFMediaEvent.ValueOf(pv)) ?? MF.E_POINTER;

        [UnmanagedCallersOnly]
        private static int QueueEventParamUnk(IntPtr self, uint type, Guid* ext, int status, IntPtr unk)
        {
            if (Q(self) is not { } q) return MF.E_POINTER;
            MFValue? v = null;
            if (unk != IntPtr.Zero) { Com.AddRef(unk); v = new MFValue { Type = MF.VT_UNKNOWN, Unknown = unk }; }
            return q.QueueEvent(type, ext == null ? Guid.Empty : *ext, status, v);
        }

        [UnmanagedCallersOnly] private static int ShutdownSlot(IntPtr self) { Q(self)?.Shutdown(); return MF.S_OK; }
    }

    /// <summary>IMFMediaTypeHandler: a stream's types, and the one in use.</summary>
    internal sealed unsafe class MFMediaTypeHandler : ComObject
    {
        private static readonly Vtable s_vt = Build();
        public readonly List<MFMediaType> Types = new();
        public MFMediaType? Current;
        /// <summary>Told of SetCurrentMediaType's type, after it is current (a capture device's source: the camera to it).</summary>
        public Action<MFMediaType>? CurrentChanged;
        private readonly object _lock = new();

        private static Vtable Build()
        {
            var vt = new Vtable("IMFMediaTypeHandler", 9, MFE.IID_IMFMediaTypeHandler);
            vt.Set(3, (delegate* unmanaged<IntPtr, IntPtr, IntPtr*, int>)&IsMediaTypeSupported);
            vt.Set(4, (delegate* unmanaged<IntPtr, uint*, int>)&GetMediaTypeCount);
            vt.Set(5, (delegate* unmanaged<IntPtr, uint, IntPtr*, int>)&GetMediaTypeByIndex);
            vt.Set(6, (delegate* unmanaged<IntPtr, IntPtr, int>)&SetCurrentMediaType);
            vt.Set(7, (delegate* unmanaged<IntPtr, IntPtr*, int>)&GetCurrentMediaType);
            vt.Set(8, (delegate* unmanaged<IntPtr, Guid*, int>)&GetMajorType);
            return vt;
        }

        public MFMediaTypeHandler(IEnumerable<MFMediaType> types) : base(s_vt) { Types.AddRange(types); }

        protected override void OnFinalRelease()
        {
            foreach (var t in Types) t.Release();
            Current?.Release();
        }

        private static MFMediaTypeHandler? H(IntPtr self) => From<MFMediaTypeHandler>(self);

        /// <summary>A type of the list: the one of the same major type, subtype and format data (IsEqual's first three).</summary>
        private MFMediaType? Match(IntPtr type)
        {
            var t = Source.Mp4Types.Read(type);
            try
            {
                foreach (var c in Types)
                {
                    if (c.Attributes.Guid(MF.MF_MT_MAJOR_TYPE) != t.Attributes.Guid(MF.MF_MT_MAJOR_TYPE) || c.Subtype != t.Subtype) continue;
                    bool same = true;
                    foreach (var (k, v) in t.Attributes.All())
                        if (k != MF.MF_MT_USER_DATA && !(c.Attributes.Get(k) is { } w && w.SameAs(v))) { same = false; break; }
                    if (same) return c;
                }
                return null;
            }
            finally { t.Release(); }
        }

        [UnmanagedCallersOnly]
        private static int IsMediaTypeSupported(IntPtr self, IntPtr type, IntPtr* closest)
        {
            if (H(self) is not { } h || type == IntPtr.Zero) return MF.E_POINTER;
            if (closest != null) *closest = IntPtr.Zero;
            lock (h._lock) return h.Match(type) != null ? MF.S_OK : MF.MF_E_INVALIDMEDIATYPE;
        }

        [UnmanagedCallersOnly] private static int GetMediaTypeCount(IntPtr self, uint* n) { if (H(self) is not { } h || n == null) return MF.E_POINTER; lock (h._lock) *n = (uint)h.Types.Count; return MF.S_OK; }

        [UnmanagedCallersOnly]
        private static int GetMediaTypeByIndex(IntPtr self, uint i, IntPtr* type)
        {
            if (H(self) is not { } h || type == null) return MF.E_POINTER;
            *type = IntPtr.Zero;
            lock (h._lock)
            {
                if (i >= h.Types.Count) return MF.MF_E_NO_MORE_TYPES;
                *type = h.Types[(int)i].AddRefPointer();
            }
            return MF.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int SetCurrentMediaType(IntPtr self, IntPtr type)
        {
            if (H(self) is not { } h || type == IntPtr.Zero) return MF.E_POINTER;
            lock (h._lock)
            {
                // one of the stream's types; kept itself where it is the shim's (GetCurrentMediaType hands the same back)
                if (h.Match(type) == null) return MF.MF_E_INVALIDMEDIATYPE;
                MFMediaType t;
                if (From<MFMediaType>(type) is { } ours) { ours.AddRef(); t = ours; }
                else t = Source.Mp4Types.Read(type);
                h.Current?.Release();
                h.Current = t;
            }
            h.CurrentChanged?.Invoke(h.Current!);
            return MF.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int GetCurrentMediaType(IntPtr self, IntPtr* type)
        {
            if (H(self) is not { } h || type == null) return MF.E_POINTER;
            *type = IntPtr.Zero;
            lock (h._lock)
            {
                if (h.Current == null) return MFE.MF_E_NOT_INITIALIZED;
                *type = h.Current.AddRefPointer();
            }
            return MF.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int GetMajorType(IntPtr self, Guid* major)
        {
            if (H(self) is not { } h || major == null) return MF.E_POINTER;
            lock (h._lock)
            {
                var t = h.Current ?? (h.Types.Count > 0 ? h.Types[0] : null);
                if (t == null) return MFE.MF_E_NOT_INITIALIZED;
                *major = t.Attributes.Guid(MF.MF_MT_MAJOR_TYPE);
            }
            return MF.S_OK;
        }
    }

    /// <summary>IMFStreamDescriptor: a stream's identifier and its type handler; attributes besides.</summary>
    internal sealed unsafe class MFStreamDescriptor : MFAttributesObject
    {
        private static readonly Vtable s_vt = Build();
        public readonly uint Id;
        public readonly MFMediaTypeHandler Handler;

        private static Vtable Build()
        {
            var vt = new Vtable("IMFStreamDescriptor", 35, MFE.IID_IMFStreamDescriptor, MF.IID_IMFAttributes);
            FillAttributes(vt);
            vt.Set(33, (delegate* unmanaged<IntPtr, uint*, int>)&GetStreamIdentifier);
            vt.Set(34, (delegate* unmanaged<IntPtr, IntPtr*, int>)&GetMediaTypeHandler);
            return vt;
        }

        public MFStreamDescriptor(uint id, IEnumerable<MFMediaType> types) : base(s_vt) { Id = id; Handler = new MFMediaTypeHandler(types); }

        protected override void OnFinalRelease() { Handler.Release(); base.OnFinalRelease(); }

        [UnmanagedCallersOnly] private static int GetStreamIdentifier(IntPtr self, uint* id) { if (From<MFStreamDescriptor>(self) is not { } d || id == null) return MF.E_POINTER; *id = d.Id; return MF.S_OK; }

        [UnmanagedCallersOnly]
        private static int GetMediaTypeHandler(IntPtr self, IntPtr* h)
        {
            if (From<MFStreamDescriptor>(self) is not { } d || h == null) return MF.E_POINTER;
            *h = d.Handler.AddRefPointer();
            return MF.S_OK;
        }
    }

    /// <summary>IMFPresentationDescriptor: the streams (each its descriptor, selected or not); attributes besides.</summary>
    internal sealed unsafe class MFPresentationDescriptor : MFAttributesObject
    {
        private static readonly Vtable s_vt = Build();
        public readonly List<(IntPtr Descriptor, bool Selected)> Streams = new();     // IMFStreamDescriptor, AddRef'd
        private readonly object _lock = new();

        private static Vtable Build()
        {
            var vt = new Vtable("IMFPresentationDescriptor", 38, MFE.IID_IMFPresentationDescriptor, MF.IID_IMFAttributes);
            FillAttributes(vt);
            vt.Set(33, (delegate* unmanaged<IntPtr, uint*, int>)&GetStreamDescriptorCount);
            vt.Set(34, (delegate* unmanaged<IntPtr, uint, int*, IntPtr*, int>)&GetStreamDescriptorByIndex);
            vt.Set(35, (delegate* unmanaged<IntPtr, uint, int>)&SelectStream);
            vt.Set(36, (delegate* unmanaged<IntPtr, uint, int>)&DeselectStream);
            vt.Set(37, (delegate* unmanaged<IntPtr, IntPtr*, int>)&Clone);
            return vt;
        }

        public MFPresentationDescriptor() : base(s_vt) { }

        /// <summary>A stream descriptor of its (AddRef'd here), deselected.</summary>
        public void Add(IntPtr sd, bool selected = false) { Com.AddRef(sd); lock (_lock) Streams.Add((sd, selected)); }

        protected override void OnFinalRelease()
        {
            foreach (var (d, _) in Streams) Com.Release(d);
            Streams.Clear();
            base.OnFinalRelease();
        }

        private static MFPresentationDescriptor? P(IntPtr self) => From<MFPresentationDescriptor>(self);

        [UnmanagedCallersOnly] private static int GetStreamDescriptorCount(IntPtr self, uint* n) { if (P(self) is not { } p || n == null) return MF.E_POINTER; lock (p._lock) *n = (uint)p.Streams.Count; return MF.S_OK; }

        [UnmanagedCallersOnly]
        private static int GetStreamDescriptorByIndex(IntPtr self, uint i, int* selected, IntPtr* sd)
        {
            if (P(self) is not { } p || selected == null || sd == null) return MF.E_POINTER;
            lock (p._lock)
            {
                if (i >= p.Streams.Count) return MF.E_INVALIDARG;
                *selected = p.Streams[(int)i].Selected ? 1 : 0;
                *sd = p.Streams[(int)i].Descriptor;
                Com.AddRef(*sd);
            }
            return MF.S_OK;
        }

        private int Select(uint i, bool on)
        {
            lock (_lock)
            {
                if (i >= Streams.Count) return MF.E_INVALIDARG;
                Streams[(int)i] = (Streams[(int)i].Descriptor, on);
            }
            return MF.S_OK;
        }

        [UnmanagedCallersOnly] private static int SelectStream(IntPtr self, uint i) => P(self)?.Select(i, true) ?? MF.E_POINTER;
        [UnmanagedCallersOnly] private static int DeselectStream(IntPtr self, uint i) => P(self)?.Select(i, false) ?? MF.E_POINTER;

        /// <summary>A copy: its own selection and attributes, the same stream descriptors.</summary>
        public MFPresentationDescriptor CloneOf()
        {
            var c = new MFPresentationDescriptor();
            lock (_lock) foreach (var (d, s) in Streams) c.Add(d, s);
            foreach (var (k, v) in Attributes.All()) c.Attributes.Set(k, Copy(v));
            return c;
        }

        [UnmanagedCallersOnly]
        private static int Clone(IntPtr self, IntPtr* pd)
        {
            if (P(self) is not { } p || pd == null) return MF.E_POINTER;
            *pd = p.CloneOf().Pointer;
            return MF.S_OK;
        }
    }
}
