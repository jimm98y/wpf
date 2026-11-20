// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The topology loader (Documentation/media-foundation-shim.md, "The media session"): a partial topology resolved
// into a full one as Windows' loader resolves it (MFMediaSessionProbe, MediaSession.windows.txt). Each branch, of
// the partial topology's node order: its source node (MF_TOPONODE_MEDIASTART added), the nodes the application gave
// after it, its output node -- the activate made the media sink's stream sink (MF_TOPONODE_STREAMID's, 0), the
// sink's CLSID set of the system's renderers -- then the transforms the loader adds:
//
//   - nothing where the stream sink takes the stream's type;
//   - else the decoder of the stream's subtype (MFTEnumEx's first; MF_TOPONODE_DECODER and the two attributes beside
//     it, its CLSID), of its output type the sink takes, else of its first;
//   - else after it (or of an uncompressed stream) a converter: the color converter (YUV and RGB of the same size), the
//     video processor (another size), the audio resampler (Audio.Resampler); of the type the sink's handler gives,
//     completed of the frames' size, rate and aspect.
//

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Wpf.Interop.WebGpu.Direct3D;
using Microsoft.Wpf.Interop.WebGpu.MediaFoundation.Codecs;
using Microsoft.Wpf.Interop.WebGpu.MediaFoundation.Source;

namespace Microsoft.Wpf.Interop.WebGpu.MediaFoundation.Session
{
    /// <summary>The audio converter the loader adds: the resampler.</summary>
    internal static partial class Audio
    {
        private static readonly Guid MF_MT_AUDIO_NUM_CHANNELS = new("37e48bf5-645e-4c5b-89de-ada9e29b696a"), MF_MT_AUDIO_SAMPLES_PER_SECOND = new("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
        private static readonly Guid MF_MT_AUDIO_BITS_PER_SAMPLE = new("f2deb57f-40fa-4764-aa33-ed4f2d1ff669");

        /// <summary>The resampler from the input to the sink's type (of its format: the rest of the input's channels and rate where it says none).</summary>
        public static int Resampler(MFMediaType input, MFMediaType want, out IntPtr mft, out Guid clsid, out IntPtr outType, Func<IntPtr, bool> takes)
        {
            mft = IntPtr.Zero; clsid = MFResampler.Clsid; outType = IntPtr.Zero;
            bool fl = want.Subtype != MediaFormats.PCM;
            var a = want.Attributes;
            uint ch = a.U32(MF_MT_AUDIO_NUM_CHANNELS, input.Attributes.U32(MF_MT_AUDIO_NUM_CHANNELS, 2));
            uint rate = a.U32(MF_MT_AUDIO_SAMPLES_PER_SECOND, input.Attributes.U32(MF_MT_AUDIO_SAMPLES_PER_SECOND, 48000));
            uint bits = a.U32(MF_MT_AUDIO_BITS_PER_SAMPLE, fl ? 32u : 16u);
            var o = MFResampler.TypeOf(fl, bits, ch, rate);
            var r = new MFResampler();
            if (Mft.SetInputType(r.Pointer, input.Pointer) < 0 || Mft.SetOutputType(r.Pointer, o.Pointer) < 0 || !takes(o.Pointer))
            {
                r.Release(); o.Release();
                return TopoLoader.MF_E_TOPO_CANNOT_CONNECT;
            }
            mft = r.Pointer;
            outType = o.Pointer;
            return MF.S_OK;
        }
    }

    internal static unsafe class TopoLoader
    {
        public const int MF_E_TOPO_MISSING_PRESENTATION_DESCRIPTOR = unchecked((int)0xC00D5217), MF_E_TOPO_MISSING_STREAM_DESCRIPTOR = unchecked((int)0xC00D5218);
        public const int MF_E_TOPO_MISSING_SOURCE = unchecked((int)0xC00D5219), MF_E_TOPO_SINK_ACTIVATES_UNSUPPORTED = unchecked((int)0xC00D521A);
        public const int MF_E_TOPO_CODEC_NOT_FOUND = unchecked((int)0xC00D5212), MF_E_TOPO_UNSUPPORTED = unchecked((int)0xC00D5214), MF_E_TOPO_CANNOT_CONNECT = unchecked((int)0xC00D5213);
        public static readonly Guid MFT_CATEGORY_AUDIO_DECODER = new("9ea73fb4-ef7a-4559-8d5d-719d8f0426c7");
        private static readonly Guid IID_IMFTransform = new("bf94c121-5b05-4e6f-8000-ba598961414d"), IID_IMFMediaSink = SinkIds.IID_IMFMediaSink, IID_IMFStreamSink = SinkIds.IID_IMFStreamSink;
        private static readonly Guid IID_IMFActivate = new("7fee9e9a-4a89-47a6-899c-b6a53a70fb67");
        private static readonly Guid MFMediaType_Audio = new("73647561-0000-0010-8000-00aa00389b71");

        /// <summary>The full topology of the partial one, or the HRESULT the resolution failed of.</summary>
        public static int Resolve(MFTopology partial, out MFTopology? full)
        {
            full = null;
            var t = new MFTopology { Id = partial.Id };
            foreach (var (k, v) in partial.Attributes.All()) t.Attributes.Set(k, MFAttributesObject_Copy(v));
            t.Attributes.Set(TopoIds.MF_TOPOLOGY_RESOLUTION_STATUS, 0u);
            var map = new Dictionary<MFTopologyNode, MFTopologyNode>();
            try
            {
                foreach (var src in partial.Nodes.Where(n => n.NodeType == TopoIds.SOURCESTREAM))
                {
                    int hr = Branch(src, t, map);
                    if (hr < 0) { t.Release(); return hr; }
                }
                // the nodes of no branch, as they are
                foreach (var n in partial.Nodes.Where(n => !map.ContainsKey(n))) { var c = Clone(n); map[n] = c; t.Nodes.Add(c); }
            }
            catch (Exception e) { D3DLog.Error("the topology loader", e); t.Release(); return MF.E_FAIL; }
            full = t;
            return MF.S_OK;
        }

        private static MFValue MFAttributesObject_Copy(MFValue v) => new MFValue { Type = v.Type, U = v.U, D = v.D, G = v.G, S = v.S, B = v.B?.ToArray(), Unknown = AddRefOf(v.Unknown) };
        private static IntPtr AddRefOf(IntPtr p) { Com.AddRef(p); return p; }

        private static MFTopologyNode Clone(MFTopologyNode n) { var c = new MFTopologyNode(n.NodeType); c.CopyFrom(n); return c; }

        /// <summary>A source node's branch: its nodes cloned into the full topology, the transforms it needs added after them.</summary>
        private static int Branch(MFTopologyNode src, MFTopology full, Dictionary<MFTopologyNode, MFTopologyNode> map)
        {
            if (src.Attributes.Get(TopoIds.MF_TOPONODE_SOURCE) is not { Unknown: not 0 }) return MF_E_TOPO_MISSING_SOURCE;
            if (src.Attributes.Get(TopoIds.MF_TOPONODE_PRESENTATION_DESCRIPTOR) is not { Unknown: not 0 }) return MF_E_TOPO_MISSING_PRESENTATION_DESCRIPTOR;
            if (src.Attributes.Get(TopoIds.MF_TOPONODE_STREAM_DESCRIPTOR) is not { Unknown: var sd } || sd == IntPtr.Zero) return MF_E_TOPO_MISSING_STREAM_DESCRIPTOR;
            var srcC = Clone(src);
            if (srcC.Attributes.Get(TopoIds.MF_TOPONODE_MEDIASTART) == null) srcC.Attributes.Set(TopoIds.MF_TOPONODE_MEDIASTART, 0UL);
            map[src] = srcC; full.Nodes.Add(srcC);
            var added = new List<MFTopologyNode>();
            // the stream's type: its handler's current, else its first
            IntPtr type = StreamType(sd);
            if (type == IntPtr.Zero) return MF.MF_E_INVALIDMEDIATYPE;
            try
            {
                MFTopologyNode up = srcC, upP = src;
                while (upP.Down(0) is { } nextP)
                {
                    var nextC = Clone(nextP);
                    map[nextP] = nextC; full.Nodes.Add(nextC);
                    if (nextP.NodeType == TopoIds.OUTPUT)
                    {
                        int hr = Output(nextC);
                        if (hr < 0) return hr;
                        // a D3D-aware renderer's branch: its source node aware too, of the renderer's device manager
                        if (nextC.Attributes.Get(TopoIds.TOPONODE_DEVICE_MANAGER) is { Unknown: var dm } && dm != IntPtr.Zero)
                        {
                            srcC.Attributes.Set(TopoIds.MF_TOPONODE_D3DAWARE, 1u);
                            Com.AddRef(dm);
                            srcC.Attributes.Set(TopoIds.TOPONODE_DEVICE_MANAGER, new MFValue { Type = MF.VT_UNKNOWN, Unknown = dm });
                        }
                        hr = Connect(up, nextC, type, added);
                        if (hr < 0) return hr;
                        break;
                    }
                    if (nextP.NodeType == TopoIds.TRANSFORM)
                    {
                        // the application's transform: given the type (and its output the type after it)
                        IntPtr mft = TransformOf(nextC);
                        if (mft == IntPtr.Zero) return MF.E_INVALIDARG;
                        int hr = Mft.SetInputType(mft, type);
                        if (hr < 0) { Com.Release(mft); return MF_E_TOPO_CANNOT_CONNECT; }
                        IntPtr outT;
                        if (((delegate* unmanaged<IntPtr, uint, uint, IntPtr*, int>)Com.Fn(mft, 14))(mft, 0, 0, &outT) >= 0) { Mft.SetOutputType(mft, outT); Com.Release(type); type = outT; }
                        Com.Release(mft);
                        up.Connect(0, nextC, 0);
                    }
                    else up.Connect(0, nextC, 0);           // a tee: its first output followed
                    up = nextC; upP = nextP;
                }
            }
            finally { Com.Release(type); }
            foreach (var n in added) full.Nodes.Add(n);
            return MF.S_OK;
        }

        private static IntPtr StreamType(IntPtr sd)
        {
            IntPtr h, t;
            if (((delegate* unmanaged<IntPtr, IntPtr*, int>)Com.Fn(sd, 34))(sd, &h) < 0) return IntPtr.Zero;
            try
            {
                if (((delegate* unmanaged<IntPtr, IntPtr*, int>)Com.Fn(h, 7))(h, &t) >= 0) return t;
                if (((delegate* unmanaged<IntPtr, uint, IntPtr*, int>)Com.Fn(h, 5))(h, 0, &t) >= 0) return t;
                return IntPtr.Zero;
            }
            finally { Com.Release(h); }
        }

        private static IntPtr TransformOf(MFTopologyNode n)
        {
            if (n.Object == IntPtr.Zero) return IntPtr.Zero;
            if (Com.QueryInterface(n.Object, IID_IMFTransform, out var t) >= 0) return t;
            if (Com.QueryInterface(n.Object, IID_IMFActivate, out var a) >= 0)
            {
                IntPtr o; var iid = IID_IMFTransform;
                int hr = ((delegate* unmanaged<IntPtr, Guid*, IntPtr*, int>)Com.Fn(a, 33))(a, &iid, &o);
                Com.Release(a);
                if (hr >= 0) { n.SetObjectOf(o); return o; }
            }
            return IntPtr.Zero;
        }

        /// <summary>An output node's object made its stream sink: of an activate (the media sink's), of a media sink, as it is.</summary>
        private static int Output(MFTopologyNode n)
        {
            if (n.Object == IntPtr.Zero) return MF.E_INVALIDARG;
            uint id = n.Attributes.Get(TopoIds.MF_TOPONODE_STREAMID) is { } v ? (uint)v.U : 0;
            if (Com.QueryInterface(n.Object, IID_IMFStreamSink, out var ss) >= 0) { Com.Release(ss); n.Attributes.Set(TopoIds.MF_TOPONODE_STREAMID, id); return MF.S_OK; }
            IntPtr sink = IntPtr.Zero;
            if (Com.QueryInterface(n.Object, IID_IMFActivate, out var a) >= 0)
            {
                var iid = IID_IMFMediaSink;
                int hr = ((delegate* unmanaged<IntPtr, Guid*, IntPtr*, int>)Com.Fn(a, 33))(a, &iid, &sink);
                Com.Release(a);
                if (hr < 0) return hr;
            }
            else if (Com.QueryInterface(n.Object, IID_IMFMediaSink, out var ms) >= 0) sink = ms;
            else return MF.E_INVALIDARG;
            // a system renderer's node: of its CLSID
            if (ComObject.From(sink) is IRendererClsid r) n.Attributes.Set(TopoIds.MF_TOPONODE_TRANSFORM_OBJECTID, MFValue.Of(r.Clsid));
            if (ComObject.From(sink) is MFVideoRenderer vr && vr.DeviceManager != IntPtr.Zero)
            {
                Com.AddRef(vr.DeviceManager);
                n.Attributes.Set(TopoIds.TOPONODE_DEVICE_MANAGER, new MFValue { Type = MF.VT_UNKNOWN, Unknown = vr.DeviceManager });
            }
            try
            {
                IntPtr stream;
                int hr = ((delegate* unmanaged<IntPtr, uint, IntPtr*, int>)Com.Fn(sink, 8))(sink, id, &stream);       // GetStreamSinkById
                if (hr < 0) hr = ((delegate* unmanaged<IntPtr, uint, IntPtr*, int>)Com.Fn(sink, 7))(sink, 0, &stream);
                if (hr < 0) return hr;
                n.SetObjectOf(stream);
                Com.Release(stream);
                n.Attributes.Set(TopoIds.MF_TOPONODE_STREAMID, id);
                return MF.S_OK;
            }
            finally { Com.Release(sink); }
        }

        private static IntPtr HandlerOf(MFTopologyNode output)
        {
            IntPtr h;
            return ((delegate* unmanaged<IntPtr, IntPtr*, int>)Com.Fn(output.Object, 9))(output.Object, &h) >= 0 ? h : IntPtr.Zero;
        }

        private static bool Takes(IntPtr handler, IntPtr type)
        {
            IntPtr closest = IntPtr.Zero;
            int hr = ((delegate* unmanaged<IntPtr, IntPtr, IntPtr*, int>)Com.Fn(handler, 3))(handler, type, &closest);
            Com.Release(closest);
            return hr >= 0;
        }

        private static int SetCurrent(IntPtr handler, IntPtr type) => ((delegate* unmanaged<IntPtr, IntPtr, int>)Com.Fn(handler, 6))(handler, type);

        /// <summary>The upstream node to the output node: directly, else through a decoder, a converter or both.</summary>
        private static int Connect(MFTopologyNode up, MFTopologyNode output, IntPtr type, List<MFTopologyNode> added)
        {
            IntPtr handler = HandlerOf(output);
            if (handler == IntPtr.Zero) return MF_E_TOPO_UNSUPPORTED;
            try
            {
                if (Takes(handler, type)) { SetCurrent(handler, type); up.Connect(0, output, 0); return MF.S_OK; }
                var t = Mp4Types.Read(type);
                bool audio = t.Attributes.Guid(MF.MF_MT_MAJOR_TYPE) == MFMediaType_Audio;
                bool compressed = !IsUncompressed(t);
                t.Release();
                MFTopologyNode last = up;
                IntPtr now = type;
                Com.AddRef(now);
                try
                {
                    if (compressed)
                    {
                        // the decoder
                        if (Decoder(now, audio, out var dec, out var clsid) is var dh && dh < 0) return dh;
                        var node = new MFTopologyNode(TopoIds.TRANSFORM);
                        node.SetObjectOf(dec);
                        node.Attributes.Set(TopoIds.TOPONODE_494bbd00, 1u);
                        node.Attributes.Set(TopoIds.TOPONODE_494bbd01, 1u);
                        node.Attributes.Set(TopoIds.MF_TOPONODE_DECODER, 1u);
                        if (clsid != Guid.Empty) node.Attributes.Set(TopoIds.MF_TOPONODE_TRANSFORM_OBJECTID, MFValue.Of(clsid));
                        last.Connect(0, node, 0);
                        added.Add(node);
                        last = node;
                        // of its outputs, one the sink takes; else its first
                        IntPtr chosen = IntPtr.Zero, first = IntPtr.Zero;
                        for (uint i = 0; ; i++)
                        {
                            IntPtr o;
                            if (((delegate* unmanaged<IntPtr, uint, uint, IntPtr*, int>)Com.Fn(dec, 14))(dec, 0, i, &o) < 0) break;
                            if (first == IntPtr.Zero) { first = o; Com.AddRef(o); }
                            if (Takes(handler, o)) { chosen = o; break; }
                            Com.Release(o);
                        }
                        IntPtr outType = chosen != IntPtr.Zero ? chosen : first;
                        if (outType == IntPtr.Zero) { Com.Release(dec); return MF_E_TOPO_CODEC_NOT_FOUND; }
                        int sh = Mft.SetOutputType(dec, outType);
                        if (chosen != IntPtr.Zero && first != IntPtr.Zero) Com.Release(first);
                        Com.Release(dec);
                        if (sh < 0) { Com.Release(outType); return MF_E_TOPO_CODEC_NOT_FOUND; }
                        Com.Release(now); now = outType;
                        if (chosen != IntPtr.Zero) { SetCurrent(handler, now); last.Connect(0, output, 0); return MF.S_OK; }
                    }
                    // the converter
                    int ch = Converter(now, handler, audio, out var conv, out var convClsid, out var convOut);
                    if (ch < 0) return ch;
                    var cnode = new MFTopologyNode(TopoIds.TRANSFORM);
                    cnode.SetObjectOf(conv);
                    cnode.Attributes.Set(TopoIds.MF_TOPONODE_TRANSFORM_OBJECTID, MFValue.Of(convClsid));
                    Com.Release(conv);
                    last.Connect(0, cnode, 0);
                    added.Add(cnode);
                    cnode.Connect(0, output, 0);
                    SetCurrent(handler, convOut);
                    Com.Release(convOut);
                    return MF.S_OK;
                }
                finally { Com.Release(now); }
            }
            finally { Com.Release(handler); }
        }

        private static bool IsUncompressed(MFMediaType t)
        {
            var s = t.Subtype;
            if (t.Attributes.Guid(MF.MF_MT_MAJOR_TYPE) == MFMediaType_Audio) return s == MediaFormats.PCM || s == MediaFormats.Float;
            return Pictures.Reads(s);
        }

        /// <summary>The first decoder MFTEnumEx lists of the type's subtype, its input type set.</summary>
        private static int Decoder(IntPtr type, bool audio, out IntPtr mft, out Guid clsid)
        {
            mft = IntPtr.Zero; clsid = Guid.Empty;
            var t = Mp4Types.Read(type);
            var info = new MFT_REGISTER_TYPE_INFO { guidMajorType = t.Attributes.Guid(MF.MF_MT_MAJOR_TYPE), guidSubtype = t.Subtype };
            t.Release();
            IntPtr* acts; uint n;
            int hr = MFExports.MFTEnumEx(audio ? MFT_CATEGORY_AUDIO_DECODER : MF.MFT_CATEGORY_VIDEO_DECODER, 0x1 | 0x8 | 0x40, &info, null, &acts, &n);   // SYNCMFT, LOCALMFT, SORTANDFILTER
            if (hr < 0 || n == 0) return MF_E_TOPO_CODEC_NOT_FOUND;
            try
            {
                for (uint i = 0; i < n && mft == IntPtr.Zero; i++)
                {
                    IntPtr a = acts[i], o; var iid = IID_IMFTransform;
                    if (((delegate* unmanaged<IntPtr, Guid*, IntPtr*, int>)Com.Fn(a, 33))(a, &iid, &o) < 0) continue;
                    if (Mft.SetInputType(o, type) < 0) { Com.Release(o); continue; }
                    var k = MF.MFT_TRANSFORM_CLSID_Attribute; Guid g;
                    if (((delegate* unmanaged<IntPtr, Guid*, Guid*, int>)Com.Fn(a, 10))(a, &k, &g) >= 0) clsid = g;
                    mft = o;
                }
            }
            finally
            {
                for (uint i = 0; i < n; i++) Com.Release(acts[i]);
                Marshal.FreeCoTaskMem((IntPtr)acts);
            }
            return mft != IntPtr.Zero ? MF.S_OK : MF_E_TOPO_CODEC_NOT_FOUND;
        }

        /// <summary>The converter from the type to one the sink takes: its output type, of the sink's type completed of the input's.</summary>
        private static int Converter(IntPtr type, IntPtr handler, bool audio, out IntPtr mft, out Guid clsid, out IntPtr outType)
        {
            mft = IntPtr.Zero; outType = IntPtr.Zero; clsid = Guid.Empty;
            // the sink's wanted type: its current, else its first
            IntPtr want;
            if (((delegate* unmanaged<IntPtr, IntPtr*, int>)Com.Fn(handler, 7))(handler, &want) < 0 && ((delegate* unmanaged<IntPtr, uint, IntPtr*, int>)Com.Fn(handler, 5))(handler, 0, &want) < 0)
                return MF_E_TOPO_CANNOT_CONNECT;
            var w = Mp4Types.Read(want);
            Com.Release(want);
            var input = Mp4Types.Read(type);
            try
            {
                // completed of what the input has and the wanted type does not say
                if (audio) return Audio.Resampler(input, w, out mft, out clsid, out outType, h => Takes(handler, h));
                foreach (var (k, v) in input.Attributes.All())
                {
                    if (k == MF.MF_MT_SUBTYPE || k == MF.MF_MT_DEFAULT_STRIDE || k == MF.MF_MT_SAMPLE_SIZE || w.Attributes.Get(k) != null) continue;
                    w.Attributes.Set(k, MFAttributesObject_Copy(v));
                }
                Complete(w);
                ComObject conv;
                bool sameSize = w.Attributes.U64(MF.MF_MT_FRAME_SIZE) == input.Attributes.U64(MF.MF_MT_FRAME_SIZE);
                if (sameSize) { conv = new MFColorConverter(); clsid = MFColorConverter.Clsid; }
                else { conv = new MFVideoProcessor(); clsid = MFVideoProcessor.Clsid; }
                IntPtr c = conv.Pointer;
                if (Mft.SetInputType(c, input.Pointer) < 0 || Mft.SetOutputType(c, w.Pointer) < 0 || !Takes(handler, w.Pointer))
                {
                    conv.Release();
                    if (!audio && clsid == MFColorConverter.Clsid)
                    {
                        // a conversion the color converter does not make: the video processor
                        var vp = new MFVideoProcessor();
                        if (Mft.SetInputType(vp.Pointer, input.Pointer) >= 0 && Mft.SetOutputType(vp.Pointer, w.Pointer) >= 0 && Takes(handler, w.Pointer))
                        { mft = vp.Pointer; clsid = MFVideoProcessor.Clsid; outType = w.AddRefPointer(); return MF.S_OK; }
                        vp.Release();
                    }
                    return MF_E_TOPO_CANNOT_CONNECT;
                }
                mft = c;
                outType = w.AddRefPointer();
                return MF.S_OK;
            }
            finally { w.Release(); input.Release(); }
        }

        /// <summary>A video type's stride and sample size, of its subtype and size.</summary>
        private static void Complete(MFMediaType t)
        {
            ulong size = t.Attributes.U64(MF.MF_MT_FRAME_SIZE);
            uint w = (uint)(size >> 32), h = (uint)size;
            if (w == 0 || h == 0) return;
            var sub = t.Subtype;
            int stride = Pictures.Stride(sub, (int)w);
            uint bytes = (uint)Pictures.Size(sub, (int)w, (int)h);
            if (t.Attributes.Get(MF.MF_MT_DEFAULT_STRIDE) == null) t.Attributes.Set(MF.MF_MT_DEFAULT_STRIDE, (uint)stride);
            if (t.Attributes.Get(MF.MF_MT_SAMPLE_SIZE) == null) t.Attributes.Set(MF.MF_MT_SAMPLE_SIZE, bytes);
            if (t.Attributes.Get(MF.MF_MT_FIXED_SIZE_SAMPLES) == null) t.Attributes.Set(MF.MF_MT_FIXED_SIZE_SAMPLES, 1u);
            if (t.Attributes.Get(MF.MF_MT_ALL_SAMPLES_INDEPENDENT) == null) t.Attributes.Set(MF.MF_MT_ALL_SAMPLES_INDEPENDENT, 1u);
        }
    }
}
