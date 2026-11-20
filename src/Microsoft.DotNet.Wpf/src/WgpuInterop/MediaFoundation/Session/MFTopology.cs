// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Topologies (Documentation/media-foundation-shim.md, "The media session"): IMFTopology, its nodes (IMFTopologyNode:
// a source stream, a transform, an output, a tee), the connections between them and their attributes; IMFCollection
// of the source and output nodes. A node's identifier is the process's next one, kept by a clone (CloneFrom), as
// Windows' are; the full topology the loader makes holds the partial one's nodes, cloned, of their identifiers.
//

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Wpf.Interop.WebGpu.Direct3D;

namespace Microsoft.Wpf.Interop.WebGpu.MediaFoundation.Session
{
    internal static class TopoIds
    {
        public static readonly Guid IID_IMFTopology = new("83cf873a-f6da-4bc8-823f-bacfd55dc433"), IID_IMFTopologyNode = new("83cf873a-f6da-4bc8-823f-bacfd55dc430");
        public static readonly Guid IID_IMFCollection = new("5bc8a76b-869a-46a3-9b03-fa218a66aebe");
        public static readonly Guid MF_TOPONODE_SOURCE = new("835c58ec-e075-4bc7-bcba-4de000df9ae6"), MF_TOPONODE_PRESENTATION_DESCRIPTOR = new("835c58ed-e075-4bc7-bcba-4de000df9ae6");
        public static readonly Guid MF_TOPONODE_STREAM_DESCRIPTOR = new("835c58ee-e075-4bc7-bcba-4de000df9ae6"), MF_TOPONODE_MEDIASTART = new("835c58ea-e075-4bc7-bcba-4de000df9ae6");
        public static readonly Guid MF_TOPONODE_STREAMID = new("14932f9b-9087-4bb4-8412-5167145cbe04"), MF_TOPONODE_TRANSFORM_OBJECTID = new("88dcc0c9-293e-4e8b-9aeb-0ad64cc016b0");
        // a decoder's: MF_TOPONODE_DECODER and its two neighbours, as Windows' loader sets them
        public static readonly Guid TOPONODE_494bbd00 = new("494bbd00-b031-4e38-97c4-d5422dd618dc"), TOPONODE_494bbd01 = new("494bbd01-b031-4e38-97c4-d5422dd618dc");
        public static readonly Guid MF_TOPONODE_DECODER = new("494bbd02-b031-4e38-97c4-d5422dd618dc"), MF_TOPONODE_D3DAWARE = new("494bbced-b031-4e38-97c4-d5422dd618dc");
        public static readonly Guid MF_TOPOLOGY_RESOLUTION_STATUS = new("494bbcde-b031-4e38-97c4-d5422dd618dc");
        public const uint OUTPUT = 0, SOURCESTREAM = 1, TRANSFORM = 2, TEE = 3;

        private static long s_next = 0x10000;
        public static ulong Next() => (ulong)Interlocked.Increment(ref s_next);
    }

    /// <summary>IMFTopologyNode.</summary>
    internal sealed unsafe class MFTopologyNode : MFAttributesObject
    {
        private static readonly Vtable s_vt = Build();
        public readonly uint NodeType;
        public ulong Id = TopoIds.Next();
        public IntPtr Object;                                       // AddRef'd
        public readonly List<(MFTopologyNode? Node, uint Input)> Outputs = new();      // downstream: AddRef'd
        public readonly List<(MFTopologyNode? Node, uint Output)> Inputs = new();      // upstream: not AddRef'd (the topology holds them)
        public readonly Dictionary<uint, MFMediaType> OutputPref = new(), InputPref = new();
        private readonly object _lock = new();

        private static Vtable Build()
        {
            var vt = new Vtable("IMFTopologyNode", 49, TopoIds.IID_IMFTopologyNode, MF.IID_IMFAttributes);
            FillAttributes(vt);
            vt.Set(33, (delegate* unmanaged<IntPtr, IntPtr, int>)&SetObject);
            vt.Set(34, (delegate* unmanaged<IntPtr, IntPtr*, int>)&GetObject);
            vt.Set(35, (delegate* unmanaged<IntPtr, uint*, int>)&GetNodeType);
            vt.Set(36, (delegate* unmanaged<IntPtr, ulong*, int>)&GetTopoNodeID);
            vt.Set(37, (delegate* unmanaged<IntPtr, ulong, int>)&SetTopoNodeID);
            vt.Set(38, (delegate* unmanaged<IntPtr, uint*, int>)&GetInputCount);
            vt.Set(39, (delegate* unmanaged<IntPtr, uint*, int>)&GetOutputCount);
            vt.Set(40, (delegate* unmanaged<IntPtr, uint, IntPtr, uint, int>)&ConnectOutput);
            vt.Set(41, (delegate* unmanaged<IntPtr, uint, int>)&DisconnectOutput);
            vt.Set(42, (delegate* unmanaged<IntPtr, uint, IntPtr*, uint*, int>)&GetInput);
            vt.Set(43, (delegate* unmanaged<IntPtr, uint, IntPtr*, uint*, int>)&GetOutput);
            vt.Set(44, (delegate* unmanaged<IntPtr, uint, IntPtr, int>)&SetOutputPrefType);
            vt.Set(45, (delegate* unmanaged<IntPtr, uint, IntPtr*, int>)&GetOutputPrefType);
            vt.Set(46, (delegate* unmanaged<IntPtr, uint, IntPtr, int>)&SetInputPrefType);
            vt.Set(47, (delegate* unmanaged<IntPtr, uint, IntPtr*, int>)&GetInputPrefType);
            vt.Set(48, (delegate* unmanaged<IntPtr, IntPtr, int>)&CloneFromSlot);
            return vt;
        }

        public MFTopologyNode(uint type) : base(s_vt) { NodeType = type; }

        protected override void OnFinalRelease()
        {
            Com.Release(Object); Object = IntPtr.Zero;
            foreach (var (n, _) in Outputs) n?.Release();
            Outputs.Clear(); Inputs.Clear();
            foreach (var t in OutputPref.Values) t.Release();
            foreach (var t in InputPref.Values) t.Release();
            OutputPref.Clear(); InputPref.Clear();
            base.OnFinalRelease();
        }

        public void SetObjectOf(IntPtr o) { Com.AddRef(o); Com.Release(Object); Object = o; }

        public MFTopologyNode? Down(uint output) => output < Outputs.Count ? Outputs[(int)output].Node : null;

        /// <summary>The node's connection from its output to the downstream node's input (both sides told).</summary>
        public void Connect(uint output, MFTopologyNode down, uint input)
        {
            lock (_lock)
            {
                while (Outputs.Count <= output) Outputs.Add((null, 0));
                Outputs[(int)output].Node?.Release();
                down.AddRef();
                Outputs[(int)output] = (down, input);
            }
            while (down.Inputs.Count <= input) down.Inputs.Add((null, 0));
            down.Inputs[(int)input] = (this, output);
        }

        /// <summary>A copy of the node's object, attributes, identifier and types; not its connections.</summary>
        public void CopyFrom(MFTopologyNode n)
        {
            SetObjectOf(n.Object);
            Id = n.Id;
            Attributes.Clear();
            foreach (var (k, v) in n.Attributes.All()) Attributes.Set(k, Copy(v));
            foreach (var (i, t) in n.OutputPref) { t.AddRef(); OutputPref[i] = t; }
            foreach (var (i, t) in n.InputPref) { t.AddRef(); InputPref[i] = t; }
        }

        private static MFTopologyNode? N(IntPtr self) => From<MFTopologyNode>(self);

        [UnmanagedCallersOnly] private static int SetObject(IntPtr self, IntPtr o) { if (N(self) is not { } n) return MF.E_POINTER; n.SetObjectOf(o); return MF.S_OK; }

        [UnmanagedCallersOnly]
        private static int GetObject(IntPtr self, IntPtr* o)
        {
            if (N(self) is not { } n || o == null) return MF.E_POINTER;
            *o = IntPtr.Zero;
            if (n.Object == IntPtr.Zero) return MF.E_FAIL;
            Com.AddRef(n.Object);
            *o = n.Object;
            return MF.S_OK;
        }

        [UnmanagedCallersOnly] private static int GetNodeType(IntPtr self, uint* t) { if (N(self) is not { } n || t == null) return MF.E_POINTER; *t = n.NodeType; return MF.S_OK; }
        [UnmanagedCallersOnly] private static int GetTopoNodeID(IntPtr self, ulong* id) { if (N(self) is not { } n || id == null) return MF.E_POINTER; *id = n.Id; return MF.S_OK; }
        [UnmanagedCallersOnly] private static int SetTopoNodeID(IntPtr self, ulong id) { if (N(self) is not { } n) return MF.E_POINTER; n.Id = id; return MF.S_OK; }
        [UnmanagedCallersOnly] private static int GetInputCount(IntPtr self, uint* c) { if (N(self) is not { } n || c == null) return MF.E_POINTER; *c = (uint)n.Inputs.Count; return MF.S_OK; }
        [UnmanagedCallersOnly] private static int GetOutputCount(IntPtr self, uint* c) { if (N(self) is not { } n || c == null) return MF.E_POINTER; *c = (uint)n.Outputs.Count; return MF.S_OK; }

        [UnmanagedCallersOnly]
        private static int ConnectOutput(IntPtr self, uint output, IntPtr down, uint input)
        {
            if (N(self) is not { } n || From<MFTopologyNode>(down) is not { } d) return MF.E_POINTER;
            if (n.NodeType == TopoIds.OUTPUT || d.NodeType == TopoIds.SOURCESTREAM) return MF.E_INVALIDARG;
            n.Connect(output, d, input);
            return MF.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int DisconnectOutput(IntPtr self, uint output)
        {
            if (N(self) is not { } n) return MF.E_POINTER;
            if (output >= n.Outputs.Count || n.Outputs[(int)output].Node is not { } d) return MF.E_INVALIDARG;
            uint input = n.Outputs[(int)output].Input;
            if (input < d.Inputs.Count) d.Inputs[(int)input] = (null, 0);
            n.Outputs[(int)output] = (null, 0);
            d.Release();
            return MF.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int GetInput(IntPtr self, uint input, IntPtr* up, uint* output)
        {
            if (N(self) is not { } n || up == null || output == null) return MF.E_POINTER;
            *up = IntPtr.Zero;
            if (input >= n.Inputs.Count || n.Inputs[(int)input].Node is not { } u) return MF.E_INVALIDARG;
            *up = u.AddRefPointer(); *output = n.Inputs[(int)input].Output;
            return MF.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int GetOutput(IntPtr self, uint output, IntPtr* down, uint* input)
        {
            if (N(self) is not { } n || down == null || input == null) return MF.E_POINTER;
            *down = IntPtr.Zero;
            if (output >= n.Outputs.Count || n.Outputs[(int)output].Node is not { } d) return MF.E_INVALIDARG;
            *down = d.AddRefPointer(); *input = n.Outputs[(int)output].Input;
            return MF.S_OK;
        }

        private static int SetPref(Dictionary<uint, MFMediaType> d, uint i, IntPtr type)
        {
            if (d.Remove(i, out var old)) old.Release();
            if (type != IntPtr.Zero) d[i] = Source.Mp4Types.Read(type);
            return MF.S_OK;
        }

        private static int GetPref(Dictionary<uint, MFMediaType> d, uint i, IntPtr* type)
        {
            if (type == null) return MF.E_POINTER;
            *type = IntPtr.Zero;
            if (!d.TryGetValue(i, out var t)) return MF.E_FAIL;
            *type = t.AddRefPointer();
            return MF.S_OK;
        }

        [UnmanagedCallersOnly] private static int SetOutputPrefType(IntPtr self, uint i, IntPtr t) => N(self) is { } n ? SetPref(n.OutputPref, i, t) : MF.E_POINTER;
        [UnmanagedCallersOnly] private static int GetOutputPrefType(IntPtr self, uint i, IntPtr* t) => N(self) is { } n ? GetPref(n.OutputPref, i, t) : MF.E_POINTER;
        [UnmanagedCallersOnly] private static int SetInputPrefType(IntPtr self, uint i, IntPtr t) => N(self) is { } n ? SetPref(n.InputPref, i, t) : MF.E_POINTER;
        [UnmanagedCallersOnly] private static int GetInputPrefType(IntPtr self, uint i, IntPtr* t) => N(self) is { } n ? GetPref(n.InputPref, i, t) : MF.E_POINTER;

        [UnmanagedCallersOnly]
        private static int CloneFromSlot(IntPtr self, IntPtr other)
        {
            if (N(self) is not { } n || From<MFTopologyNode>(other) is not { } o) return MF.E_POINTER;
            if (o.NodeType != n.NodeType) return MF.E_INVALIDARG;
            n.CopyFrom(o);
            return MF.S_OK;
        }
    }

    /// <summary>IMFTopology.</summary>
    internal sealed unsafe class MFTopology : MFAttributesObject
    {
        private static readonly Vtable s_vt = Build();
        public ulong Id = TopoIds.Next();
        public readonly List<MFTopologyNode> Nodes = new();         // AddRef'd

        private static Vtable Build()
        {
            var vt = new Vtable("IMFTopology", 43, TopoIds.IID_IMFTopology, MF.IID_IMFAttributes);
            FillAttributes(vt);
            vt.Set(33, (delegate* unmanaged<IntPtr, ulong*, int>)&GetTopologyID);
            vt.Set(34, (delegate* unmanaged<IntPtr, IntPtr, int>)&AddNode);
            vt.Set(35, (delegate* unmanaged<IntPtr, IntPtr, int>)&RemoveNode);
            vt.Set(36, (delegate* unmanaged<IntPtr, ushort*, int>)&GetNodeCount);
            vt.Set(37, (delegate* unmanaged<IntPtr, ushort, IntPtr*, int>)&GetNode);
            vt.Set(38, (delegate* unmanaged<IntPtr, int>)&Clear);
            vt.Set(39, (delegate* unmanaged<IntPtr, IntPtr, int>)&CloneFrom);
            vt.Set(40, (delegate* unmanaged<IntPtr, ulong, IntPtr*, int>)&GetNodeByID);
            vt.Set(41, (delegate* unmanaged<IntPtr, IntPtr*, int>)&GetSourceNodeCollection);
            vt.Set(42, (delegate* unmanaged<IntPtr, IntPtr*, int>)&GetOutputNodeCollection);
            return vt;
        }

        public MFTopology() : base(s_vt) { }

        protected override void OnFinalRelease() { ClearNodes(); base.OnFinalRelease(); }

        public void Add(MFTopologyNode n) { if (Nodes.Contains(n)) return; n.AddRef(); Nodes.Add(n); }

        private void ClearNodes()
        {
            // the connections broken first: a node's downstream reference would keep it
            foreach (var n in Nodes) { foreach (var (d, _) in n.Outputs) d?.Release(); n.Outputs.Clear(); n.Inputs.Clear(); }
            foreach (var n in Nodes) n.Release();
            Nodes.Clear();
        }

        /// <summary>A copy: its nodes cloned (their identifiers kept), connected as these are.</summary>
        public MFTopology Copy()
        {
            var t = new MFTopology { Id = Id };
            foreach (var (k, v) in Attributes.All()) t.Attributes.Set(k, Copy(v));
            var map = new Dictionary<MFTopologyNode, MFTopologyNode>();
            foreach (var n in Nodes) { var c = new MFTopologyNode(n.NodeType); c.CopyFrom(n); map[n] = c; t.Nodes.Add(c); }
            foreach (var n in Nodes)
                for (int o = 0; o < n.Outputs.Count; o++)
                    if (n.Outputs[o].Node is { } d && map.TryGetValue(d, out var dc)) map[n].Connect((uint)o, dc, n.Outputs[o].Input);
            return t;
        }

        private static MFTopology? T(IntPtr self) => From<MFTopology>(self);

        [UnmanagedCallersOnly] private static int GetTopologyID(IntPtr self, ulong* id) { if (T(self) is not { } t || id == null) return MF.E_POINTER; *id = t.Id; return MF.S_OK; }

        [UnmanagedCallersOnly]
        private static int AddNode(IntPtr self, IntPtr node)
        {
            if (T(self) is not { } t || From<MFTopologyNode>(node) is not { } n) return MF.E_POINTER;
            if (t.Nodes.Contains(n)) return MF.E_INVALIDARG;
            if (t.Nodes.Exists(x => x.Id == n.Id)) return unchecked((int)0xC00D521C);        // MF_E_TOPO_DUPLICATE_NODE? (an identifier there already)
            t.Add(n);
            return MF.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int RemoveNode(IntPtr self, IntPtr node)
        {
            if (T(self) is not { } t || From<MFTopologyNode>(node) is not { } n) return MF.E_POINTER;
            if (!t.Nodes.Remove(n)) return MF.E_INVALIDARG;
            n.Release();
            return MF.S_OK;
        }

        [UnmanagedCallersOnly] private static int GetNodeCount(IntPtr self, ushort* c) { if (T(self) is not { } t || c == null) return MF.E_POINTER; *c = (ushort)t.Nodes.Count; return MF.S_OK; }

        [UnmanagedCallersOnly]
        private static int GetNode(IntPtr self, ushort i, IntPtr* node)
        {
            if (T(self) is not { } t || node == null) return MF.E_POINTER;
            *node = IntPtr.Zero;
            if (i >= t.Nodes.Count) return MF.E_INVALIDARG;
            *node = t.Nodes[i].AddRefPointer();
            return MF.S_OK;
        }

        [UnmanagedCallersOnly] private static int Clear(IntPtr self) { if (T(self) is not { } t) return MF.E_POINTER; t.ClearNodes(); return MF.S_OK; }

        [UnmanagedCallersOnly]
        private static int CloneFrom(IntPtr self, IntPtr other)
        {
            if (T(self) is not { } t || From<MFTopology>(other) is not { } o) return MF.E_POINTER;
            var c = o.Copy();
            t.ClearNodes();
            foreach (var n in c.Nodes) { n.AddRef(); t.Nodes.Add(n); }
            t.Id = o.Id;
            t.Attributes.Clear();
            foreach (var (k, v) in o.Attributes.All()) t.Attributes.Set(k, Copy(v));
            c.Nodes.Clear();            // (its references moved to this one)
            foreach (var n in t.Nodes) n.Release();
            c.Release();
            return MF.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int GetNodeByID(IntPtr self, ulong id, IntPtr* node)
        {
            if (T(self) is not { } t || node == null) return MF.E_POINTER;
            *node = IntPtr.Zero;
            var n = t.Nodes.Find(x => x.Id == id);
            if (n == null) return unchecked((int)0xC00D36E5);        // MF_E_NOT_FOUND
            *node = n.AddRefPointer();
            return MF.S_OK;
        }

        private static int Collection(MFTopology t, uint type, IntPtr* c)
        {
            if (c == null) return MF.E_POINTER;
            var col = new MFCollection();
            foreach (var n in t.Nodes.Where(x => x.NodeType == type)) col.Add(n.Pointer);
            *c = col.Pointer;
            return MF.S_OK;
        }

        [UnmanagedCallersOnly] private static int GetSourceNodeCollection(IntPtr self, IntPtr* c) => T(self) is { } t ? Collection(t, TopoIds.SOURCESTREAM, c) : MF.E_POINTER;
        [UnmanagedCallersOnly] private static int GetOutputNodeCollection(IntPtr self, IntPtr* c) => T(self) is { } t ? Collection(t, TopoIds.OUTPUT, c) : MF.E_POINTER;
    }

    /// <summary>IMFCollection: IUnknowns in order (AddRef'd).</summary>
    internal sealed unsafe class MFCollection : ComObject
    {
        private static readonly Vtable s_vt = Build();
        private readonly List<IntPtr> _items = new();
        private readonly object _lock = new();

        private static Vtable Build()
        {
            var vt = new Vtable("IMFCollection", 9, TopoIds.IID_IMFCollection);
            vt.Set(3, (delegate* unmanaged<IntPtr, uint*, int>)&GetElementCount);
            vt.Set(4, (delegate* unmanaged<IntPtr, uint, IntPtr*, int>)&GetElement);
            vt.Set(5, (delegate* unmanaged<IntPtr, IntPtr, int>)&AddElement);
            vt.Set(6, (delegate* unmanaged<IntPtr, uint, IntPtr*, int>)&RemoveElement);
            vt.Set(7, (delegate* unmanaged<IntPtr, uint, IntPtr, int>)&InsertElementAt);
            vt.Set(8, (delegate* unmanaged<IntPtr, int>)&RemoveAllElements);
            return vt;
        }

        public MFCollection() : base(s_vt) { }

        public void Add(IntPtr p) { Com.AddRef(p); lock (_lock) _items.Add(p); }

        protected override void OnFinalRelease() { foreach (var p in _items) Com.Release(p); _items.Clear(); }

        private static MFCollection? C(IntPtr self) => From<MFCollection>(self);

        [UnmanagedCallersOnly] private static int GetElementCount(IntPtr self, uint* n) { if (C(self) is not { } c || n == null) return MF.E_POINTER; lock (c._lock) *n = (uint)c._items.Count; return MF.S_OK; }

        [UnmanagedCallersOnly]
        private static int GetElement(IntPtr self, uint i, IntPtr* e)
        {
            if (C(self) is not { } c || e == null) return MF.E_POINTER;
            *e = IntPtr.Zero;
            lock (c._lock)
            {
                if (i >= c._items.Count) return MF.E_INVALIDARG;
                Com.AddRef(c._items[(int)i]);
                *e = c._items[(int)i];
            }
            return MF.S_OK;
        }

        [UnmanagedCallersOnly] private static int AddElement(IntPtr self, IntPtr e) { if (C(self) is not { } c) return MF.E_POINTER; c.Add(e); return MF.S_OK; }

        [UnmanagedCallersOnly]
        private static int RemoveElement(IntPtr self, uint i, IntPtr* e)
        {
            if (C(self) is not { } c || e == null) return MF.E_POINTER;
            lock (c._lock)
            {
                if (i >= c._items.Count) return MF.E_INVALIDARG;
                *e = c._items[(int)i];                  // the caller's reference now
                c._items.RemoveAt((int)i);
            }
            return MF.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int InsertElementAt(IntPtr self, uint i, IntPtr e)
        {
            if (C(self) is not { } c) return MF.E_POINTER;
            lock (c._lock)
            {
                if (i > c._items.Count) return MF.E_INVALIDARG;
                Com.AddRef(e);
                c._items.Insert((int)i, e);
            }
            return MF.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int RemoveAllElements(IntPtr self)
        {
            if (C(self) is not { } c) return MF.E_POINTER;
            lock (c._lock) { foreach (var p in c._items) Com.Release(p); c._items.Clear(); }
            return MF.S_OK;
        }
    }
}
