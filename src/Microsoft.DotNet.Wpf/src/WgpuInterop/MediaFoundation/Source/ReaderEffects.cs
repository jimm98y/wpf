// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The source reader's transforms of the application's (IMFSourceReaderEx::AddTransformForStream;
// Documentation/media-foundation-shim.md, "The source reader"), as Windows' reader takes them (probed with an
// effect that logs what it is asked: MFReaderTransformProbe, SpyEffect):
//
//   - after the decoder (or the file's own PCM), before the reader's own conversion to the current type;
//   - the input: of video, the effect's first input type asked for, and the decoder's type set where it is of the
//     same subtype -- else the effect's first type, the decoder's output changed to it where it gives it, or the
//     reader's video processor before it; of audio, the decoder's type set, and where it is refused a float type
//     of the same rate and channels, tested, then set, the reader's converter before it;
//   - the output: the effect's types asked for until one is of the current subtype, set; else its first, and the
//     reader's converter (the video processor) after it to the current type, which stays as it was;
//   - an effect that takes nothing it can be given, or an encoder, refused (MF_E_INVALIDMEDIATYPE), and let go
//     as Windows lets it go;
//   - streaming: begun at the first read; drained at the stream's end, flushed by a seek; ended by
//     RemoveAllTransformsForStream, its types cleared.
//

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Wpf.Interop.WebGpu.Direct3D;
using Microsoft.Wpf.Interop.WebGpu.MediaFoundation.Codecs;

namespace Microsoft.Wpf.Interop.WebGpu.MediaFoundation.Source
{
    /// <summary>Pictures from one subtype to another, through NV12 (BT.601, limited range).</summary>
    internal static class VideoConvert
    {
        public static readonly Guid[] Subtypes = { MF.MFVideoFormat_NV12, MF.MFVideoFormat_YUY2, MF.MFVideoFormat_IYUV, MF.MFVideoFormat_I420, MF.MFVideoFormat_YV12, SR.MFVideoFormat_RGB32, SR.MFVideoFormat_ARGB32 };

        public static bool Converts(Guid from, Guid to) => Array.IndexOf(Subtypes, from) >= 0 && Array.IndexOf(Subtypes, to) >= 0;

        public static byte[] Convert(byte[] src, Guid from, Guid to, int w, int h)
        {
            if (from == to) return src;
            var nv12 = from == SR.MFVideoFormat_RGB32 || from == SR.MFVideoFormat_ARGB32 ? FromRgb(src, w, h) : MFH264Encoder.ToNv12(src, from, w, h);
            if (to == MF.MFVideoFormat_NV12) return nv12;
            if (to == SR.MFVideoFormat_RGB32 || to == SR.MFVideoFormat_ARGB32)
            {
                var rgb = new byte[w * h * 4];
                ReaderStream.Nv12ToRgb(nv12, w, h, rgb, w * 4, 4, to == SR.MFVideoFormat_ARGB32 ? (byte)255 : (byte)0, false);
                return rgb;
            }
            int cw = w / 2, ch = h / 2;
            if (to == MF.MFVideoFormat_YUY2)
            {
                var d = new byte[w * h * 2];
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x += 2)
                    {
                        int o = (y * w + x) * 2, c = w * h + (y / 2) * w + x;
                        d[o] = nv12[y * w + x]; d[o + 1] = nv12[c]; d[o + 2] = nv12[y * w + Math.Min(x + 1, w - 1)]; d[o + 3] = nv12[c + 1];
                    }
                return d;
            }
            var p = new byte[w * h + 2 * cw * ch];
            Array.Copy(nv12, p, w * h);
            bool vFirst = to == MF.MFVideoFormat_YV12;
            int u = w * h + (vFirst ? cw * ch : 0), v = w * h + (vFirst ? 0 : cw * ch);
            for (int k = 0; k < cw * ch; k++) { p[u + k] = nv12[w * h + 2 * k]; p[v + k] = nv12[w * h + 2 * k + 1]; }
            return p;
        }

        /// <summary>BGRX rows, top down, as NV12: each chroma sample of its 2x2 pixels' mean.</summary>
        private static byte[] FromRgb(byte[] s, int w, int h)
        {
            int cw = w / 2;
            var d = new byte[w * h + 2 * cw * (h / 2)];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int o = (y * w + x) * 4;
                    int b = s[o], g = s[o + 1], r = s[o + 2];
                    d[y * w + x] = (byte)(((66 * r + 129 * g + 25 * b + 128) >> 8) + 16);
                }
            for (int y = 0; y + 1 < h; y += 2)
                for (int x = 0; x + 1 < w; x += 2)
                {
                    int r = 0, g = 0, b = 0;
                    for (int dy = 0; dy < 2; dy++) for (int dx = 0; dx < 2; dx++) { int o = ((y + dy) * w + x + dx) * 4; b += s[o]; g += s[o + 1]; r += s[o + 2]; }
                    r /= 4; g /= 4; b /= 4;
                    int c = w * h + (y / 2) * w + x;
                    d[c] = (byte)Math.Clamp(((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128, 0, 255);
                    d[c + 1] = (byte)Math.Clamp(((112 * r - 94 * g - 18 * b + 128) >> 8) + 128, 0, 255);
                }
            return d;
        }
    }

    /// <summary>What GetTransformForStream gives of the reader's own converters: an IMFTransform of their types, doing nothing else.</summary>
    internal sealed unsafe class MFConverterStandIn : MFEncoder
    {
        private static readonly Vtable s_api = BuildApi();
        private static Vtable BuildApi()
        {
            var vt = new Vtable("ICodecAPI", 18, MF.IID_ICodecAPI);
            CodecApiSlots.Fill(vt);
            return vt;
        }

        public MFConverterStandIn(MFMediaType input, MFMediaType output) : base(s_api) { In = input.Clone().Pointer; Out = output.Clone().Pointer; }
        public override void Shutdown() { }
        protected override int InputAvailable(uint i, out MFMediaType? type) { type = null; return MF.MF_E_NO_MORE_TYPES; }
        protected override int OutputAvailable(uint i, out MFMediaType? type) { type = null; return MF.MF_E_NO_MORE_TYPES; }
        protected override int CheckInput(MFMediaType t, out List<(Guid, MFValue)> fill) { fill = new(); return MF.MF_E_INVALIDMEDIATYPE; }
        protected override int CheckOutput(MFMediaType t, bool test, out List<(Guid, MFValue)> fill) { fill = new(); return MF.MF_E_INVALIDMEDIATYPE; }
        protected override void TypesChanged() { }
        protected override uint OutputSize() => 0;
        protected override int Input(IntPtr sample) => MF.E_NOTIMPL;
        protected override int Output(IntPtr sample) => MF.MF_E_TRANSFORM_NEED_MORE_INPUT;
        protected override void Flush() { }
        protected override void Drain() { }
    }

    /// <summary>The transforms of the application's on a stream of the reader, in order, with the conversions about them.</summary>
    internal sealed unsafe class ReaderEffects : IDisposable
    {
        public static readonly Guid MFT_CATEGORY_VIDEO_EFFECT = new("12e17c21-532c-4a6e-8a1c-40825a736397"), MFT_CATEGORY_AUDIO_EFFECT = new("11064c48-3648-4ed0-932e-05ce8ac811b7");
        public static readonly Guid MFT_CATEGORY_VIDEO_PROCESSOR = new("302ea3fc-aa5f-47f9-9f7a-c2188bb16302");
        private static readonly Guid[] DecoderOutputs = { MF.MFVideoFormat_NV12, MF.MFVideoFormat_YV12, MF.MFVideoFormat_IYUV, MF.MFVideoFormat_I420, MF.MFVideoFormat_YUY2 };

        internal sealed class Effect
        {
            public IntPtr Mft;                 // IMFTransform, AddRef'd
            public bool Provides;
            public MFMediaType In = null!, Out = null!;
            /// <summary>What converts the stage's samples to the effect's input: the audio converter, or the subtype the pictures come in.</summary>
            public AudioConverter? PreAudio;
            public MFMediaType? PreFrom;
            public bool Streaming;
            public uint OutSize;
        }

        private readonly bool _video;
        public readonly List<Effect> Effects = new();
        private readonly Queue<MFSample> _out = new();
        private bool _drained;

        public ReaderEffects(bool video) { _video = video; }

        public bool Any => Effects.Count > 0;
        public MFMediaType? LastOut => Effects.Count > 0 ? Effects[^1].Out : null;

        // ---- the calls of Windows' reader ----------------------------------------------------------------

        private static int GetType(IntPtr t, int slot, uint i, out IntPtr type) { IntPtr p; int hr = ((delegate* unmanaged<IntPtr, uint, uint, IntPtr*, int>)Com.Fn(t, slot))(t, 0, i, &p); type = hr >= 0 ? p : IntPtr.Zero; return hr; }
        private static int CurrentOf(IntPtr t, int slot) { IntPtr p; int hr = ((delegate* unmanaged<IntPtr, uint, IntPtr*, int>)Com.Fn(t, slot))(t, 0, &p); if (hr >= 0) Com.Release(p); return hr; }
        private static void Attrs(IntPtr t) { IntPtr a; if (((delegate* unmanaged<IntPtr, IntPtr*, int>)Com.Fn(t, 8))(t, &a) >= 0) Com.Release(a); }
        private static void StreamAttrs(IntPtr t, int slot) { IntPtr a; if (((delegate* unmanaged<IntPtr, uint, IntPtr*, int>)Com.Fn(t, slot))(t, 0, &a) >= 0) Com.Release(a); }
        private static void Counts(IntPtr t)
        {
            uint i, o;
            ((delegate* unmanaged<IntPtr, uint*, uint*, int>)Com.Fn(t, 4))(t, &i, &o);
        }
        private static void Ids(IntPtr t) { uint i = 0, o = 0; ((delegate* unmanaged<IntPtr, uint, uint*, uint, uint*, int>)Com.Fn(t, 5))(t, 1, &i, 1, &o); }
        private static int SetIn(IntPtr t, IntPtr type, uint flags = 0) => ((delegate* unmanaged<IntPtr, uint, IntPtr, uint, int>)Com.Fn(t, 15))(t, 0, type, flags);
        private static int SetOut(IntPtr t, IntPtr type, uint flags = 0) => ((delegate* unmanaged<IntPtr, uint, IntPtr, uint, int>)Com.Fn(t, 16))(t, 0, type, flags);
        private static uint OutInfo(IntPtr t, out uint size) { var info = stackalloc uint[3]; ((delegate* unmanaged<IntPtr, uint, uint*, int>)Com.Fn(t, 7))(t, 0, info); size = info[1]; return info[0]; }

        /// <summary>
        /// The transform added after the stage (its samples of <paramref name="stage"/>), its output to become <paramref name="current"/>;
        /// <paramref name="retarget"/> changes the decoder's output to a subtype where it gives it.
        /// </summary>
        public int Add(IntPtr unknown, MFMediaType stage, MFMediaType current, Func<Guid, bool>? retarget)
        {
            if (Com.QueryInterface(unknown, MF.IID_IMFTransform, out var t) < 0)
            {
                // an activation object: the transform it makes
                if (Com.QueryInterface(unknown, MF.IID_IMFActivate, out var act) < 0) return MF.E_NOINTERFACE;
                var iid = MF.IID_IMFTransform; IntPtr made;
                int ahr = ((delegate* unmanaged<IntPtr, Guid*, IntPtr*, int>)Com.Fn(act, 33))(act, &iid, &made);
                Com.Release(act);
                if (ahr < 0) return ahr;
                t = made;
            }
            // what Windows' reader asks first
            CurrentOf(t, 18); Attrs(t); Counts(t); Counts(t); Ids(t); Attrs(t); Counts(t); Ids(t); CurrentOf(t, 18);
            var e = new Effect { Mft = t };
            MFMediaType? input = null;
            bool compressed = stage.Attributes.U32(MF.MF_MT_COMPRESSED) != 0 || stage.Attributes.U32(MF.MF_MT_ALL_SAMPLES_INDEPENDENT) == 0 && Array.IndexOf(VideoConvert.Subtypes, stage.Subtype) < 0;
            if (_video && compressed)
            {
                if (Try(t, stage, 3)) input = stage.Clone();
            }
            else if (_video)
            {
                GetType(t, 13, 0, out var a0);
                var a0t = a0 != IntPtr.Zero ? Mp4Types.Read(a0) : null;
                Com.Release(a0);
                if (a0t == null || a0t.Subtype == stage.Subtype)
                {
                    a0t?.Release();
                    if (Try(t, stage, 3)) input = stage.Clone();
                }
                else
                {
                    // another subtype: the effect's first type, of the stage's picture -- the decoder's output where it gives
                    // it, else of the video processor before the effect (its types asked for again first)
                    bool decoderGives = retarget != null && Array.IndexOf(DecoderOutputs, a0t.Subtype) >= 0;
                    if (!decoderGives)
                    {
                        StreamAttrs(t, 9);
                        for (uint i = 1; GetType(t, 13, i, out var more) >= 0; i++) Com.Release(more);
                        GetType(t, 13, 0, out var again); Com.Release(again);
                    }
                    var want = stage.Clone();
                    want.Attributes.Set(MF.MF_MT_SUBTYPE, a0t.Subtype);
                    var (w, h) = stage.FrameSize;
                    want.Attributes.Set(MF.MF_MT_DEFAULT_STRIDE, (uint)(a0t.Subtype == SR.MFVideoFormat_RGB32 || a0t.Subtype == SR.MFVideoFormat_ARGB32 ? w * 4 : a0t.Subtype == MF.MFVideoFormat_YUY2 ? w * 2 : w));
                    want.Attributes.Delete(MF.MF_MT_SAMPLE_SIZE);
                    a0t.Release();
                    if (VideoConvert.Converts(stage.Subtype, want.Subtype) && SetIn(t, want.Pointer) >= 0)
                    {
                        input = want;
                        if (retarget == null || !retarget(want.Subtype)) e.PreFrom = stage.Clone();
                    }
                    else want.Release();
                }
                if (input != null) { CurrentOf(t, 18); StreamAttrs(t, 9); CurrentOf(t, 18); CurrentOf(t, 18); }
            }
            else
            {
                if (Try(t, stage, 3)) input = stage.Clone();
                else
                {
                    // float of the stage's rate and channels: the reader's converter before the effect
                    var fl = stage.Clone();
                    uint ch = stage.Attributes.U32(SR.MF_MT_AUDIO_NUM_CHANNELS, 2), rate = stage.Attributes.U32(SR.MF_MT_AUDIO_SAMPLES_PER_SECOND, 44100);
                    fl.Attributes.Set(MF.MF_MT_SUBTYPE, MediaFormats.Float);
                    fl.Attributes.Set(SR.MF_MT_AUDIO_BITS_PER_SAMPLE, 32u);
                    fl.Attributes.Set(SR.MF_MT_AUDIO_BLOCK_ALIGNMENT, ch * 4);
                    fl.Attributes.Set(SR.MF_MT_AUDIO_AVG_BYTES_PER_SECOND, ch * 4 * rate);
                    bool pcmStage = stage.Subtype == MediaFormats.PCM || stage.Subtype == MediaFormats.Float;
                    if (pcmStage && SetIn(t, fl.Pointer, 1) >= 0 && SetIn(t, fl.Pointer) >= 0)
                    {
                        input = fl;
                        e.PreAudio = new AudioConverter(stage.Subtype == MediaFormats.Float, (int)stage.Attributes.U32(SR.MF_MT_AUDIO_BITS_PER_SAMPLE, 16), (int)ch, (int)rate, true, 32, (int)ch, (int)rate);
                    }
                    else fl.Release();
                }
                    if (input != null) { CurrentOf(t, 18); CurrentOf(t, 18); CurrentOf(t, 18); }
            }
            if (input == null) { Refuse(t); return MF.MF_E_INVALIDMEDIATYPE; }
            e.In = input;
            // the output: of the current subtype, else the effect's first (converted after it)
            var output = Negotiate(t, current, out bool converted);
            // what comes out must convert to the current type: no encoder is an effect
            if (output != null && converted && !(_video ? VideoConvert.Converts(output.Subtype, current.Subtype) : output.Subtype == MediaFormats.PCM || output.Subtype == MediaFormats.Float)) { output.Release(); output = null; }
            if (output == null) { input.Release(); e.PreFrom?.Release(); Refuse(t); return MF.MF_E_INVALIDMEDIATYPE; }
            e.Out = output;
            if (converted) StreamAttrs(t, 10);
            Effects.Add(e);
            _drained = false;
            return MF.S_OK;
        }

        /// <summary>SetInputType of the type, as many times as Windows' reader tries it.</summary>
        private static bool Try(IntPtr t, MFMediaType type, int times)
        {
            for (int i = 0; i < times; i++) if (SetIn(t, type.Pointer) >= 0) return true;
            return false;
        }

        /// <summary>The effect's output types until one of the current subtype (set); else its first, set.</summary>
        private static MFMediaType? Negotiate(IntPtr t, MFMediaType current, out bool converted)
        {
            converted = false;
            for (uint i = 0; ; i++)
            {
                if (GetType(t, 14, i, out var o) < 0) break;
                var ot = Mp4Types.Read(o);
                if (ot.Subtype == current.Subtype)
                {
                    int hr = SetOut(t, o);
                    Com.Release(o);
                    if (hr >= 0) return ot;
                    ot.Release();
                    return null;
                }
                ot.Release();
                Com.Release(o);
            }
            if (GetType(t, 14, 0, out var first) < 0) return null;
            var ft = Mp4Types.Read(first);
            int fhr = SetOut(t, first);
            Com.Release(first);
            if (fhr < 0) { ft.Release(); return null; }
            converted = true;
            return ft;
        }

        /// <summary>A transform taken no further: let go as Windows' reader lets one go.</summary>
        private static void Refuse(IntPtr t)
        {
            StreamAttrs(t, 9); StreamAttrs(t, 10);
            Mft.ProcessMessage(t, 0, 0);                                   // FLUSH
            Mft.ProcessMessage(t, 0x10000002, 0);                          // NOTIFY_END_OF_STREAM
            Mft.ProcessMessage(t, 0x10000001, 0);                          // NOTIFY_END_STREAMING
            SetOut(t, IntPtr.Zero); SetIn(t, IntPtr.Zero);
            Com.Release(t);
        }

        /// <summary>The current type changed: the last effect's output asked again of the new subtype.</summary>
        /// <summary>
        /// The current type changed (<paramref name="before"/> the old one): of the effect's output's subtype, set as it is;
        /// else its types asked for one of it -- and where none is and none was converted after it, its first set again (the
        /// converter after it added).
        /// </summary>
        public void Retarget(MFMediaType current, MFMediaType before)
        {
            if (Effects.Count == 0) return;
            var e = Effects[^1];
            bool wasConverted = e.Out.Subtype != before.Subtype;
            if (e.Out.Subtype == current.Subtype)
            {
                if (SetOut(e.Mft, current.Pointer) < 0) return;
                OutInfo(e.Mft, out _);
                if (!e.Provides) CurrentOf(e.Mft, 18);
                if (wasConverted) StreamAttrs(e.Mft, 10);
                e.Out.Release();
                e.Out = current.Clone();
                return;
            }
            for (uint i = 0; ; i++)
            {
                if (GetType(e.Mft, 14, i, out var o) < 0) break;
                var ot = Mp4Types.Read(o);
                if (ot.Subtype == current.Subtype && SetOut(e.Mft, o) >= 0)
                {
                    Com.Release(o);
                    OutInfo(e.Mft, out _);
                    if (!e.Provides) CurrentOf(e.Mft, 18);
                    e.Out.Release();
                    e.Out = ot;
                    return;
                }
                ot.Release();
                Com.Release(o);
            }
            if (wasConverted || GetType(e.Mft, 14, 0, out var first) < 0) return;
            SetOut(e.Mft, first);
            Com.Release(first);
            OutInfo(e.Mft, out _);
            StreamAttrs(e.Mft, 10);
        }

        // ---- samples ------------------------------------------------------------------------------------

        private void Begin(Effect e)
        {
            if (e.Streaming) return;
            e.Streaming = true;
            Attrs(e.Mft); CurrentOf(e.Mft, 17); CurrentOf(e.Mft, 18); StreamAttrs(e.Mft, 10);
            Mft.ProcessMessage(e.Mft, 0x10000000, 0);                      // BEGIN_STREAMING
            e.Provides = (OutInfo(e.Mft, out e.OutSize) & 0x100) != 0;
            if (!e.Provides) CurrentOf(e.Mft, 18);
            Mft.ProcessMessage(e.Mft, 0x10000003, 0);                      // START_OF_STREAM
            if (!e.Provides && !_video) { CurrentOf(e.Mft, 18); OutInfo(e.Mft, out e.OutSize); }
        }

        /// <summary>A sample of the stage through the effects; what comes out queued (the caller's reference given up).</summary>
        public void Push(MFSample s) => Push(0, s);

        private void Push(int k, MFSample s)
        {
            if (k >= Effects.Count) { _out.Enqueue(s); return; }
            var e = Effects[k];
            Begin(e);
            var input = Pre(e, s);
            if (input == null) return;
            try
            {
                while (true)
                {
                    int hr = Mft.ProcessInput(e.Mft, input.Pointer);
                    if (hr == MF.MF_E_NOTACCEPTING) { Pull(k); continue; }
                    if (hr < 0) throw new InvalidOperationException($"the transform's ProcessInput: 0x{hr:x8}");
                    break;
                }
            }
            finally { input.Release(); }
            Pull(k);
        }

        /// <summary>The stage's sample as the effect's input: as it is, or converted.</summary>
        private MFSample? Pre(Effect e, MFSample s)
        {
            if (e.PreAudio != null)
            {
                var o = e.PreAudio.Convert(s.Bytes());
                if (o.Length == 0) { s.Release(); return null; }
                return Replace(s, o);
            }
            if (e.PreFrom != null)
            {
                var (w, h) = e.In.FrameSize;
                return Replace(s, VideoConvert.Convert(s.Bytes(), e.PreFrom.Subtype, e.In.Subtype, (int)w, (int)h));
            }
            return s;
        }

        /// <summary>A sample of the bytes, of the times and attributes of the one given (let go).</summary>
        internal static MFSample Replace(MFSample s, byte[] bytes)
        {
            try
            {
                var r = new MFSample();
                var buf = new MFMemoryBuffer((uint)Math.Max(1, bytes.Length));
                byte* d = buf.LockBytes(true);
                bytes.CopyTo(new Span<byte>(d, bytes.Length));
                buf.UnlockBytes();
                buf.CurrentLength = (uint)bytes.Length;
                r.Buffers.Add(buf.Pointer);
                r.Time = s.Time; r.Duration = s.Duration;
                foreach (var (k, v) in s.Attributes.All()) r.Attributes.Set(k, MFAttributesObject.Copy(v));
                return r;
            }
            finally { s.Release(); }
        }

        /// <summary>The effect's outputs, each on to the next.</summary>
        private void Pull(int k)
        {
            var e = Effects[k];
            while (true)
            {
                // the size asked again before each output of audio (Windows' reader allocates video's once)
                uint size = e.OutSize;
                if (!e.Provides && !_video) OutInfo(e.Mft, out size);
                bool provides = e.Provides;
                MFSample? mine = null;
                if (!provides)
                {
                    mine = new MFSample();
                    var buf = new MFMemoryBuffer(Math.Max(size, 1u << 16));
                    mine.Buffers.Add(buf.Pointer);
                }
                var odb = stackalloc byte[32];
                new Span<byte>(odb, 32).Clear();
                *(IntPtr*)(odb + IntPtr.Size) = mine?.Pointer ?? IntPtr.Zero;
                uint status;
                int hr = ((delegate* unmanaged<IntPtr, uint, uint, byte*, uint*, int>)Com.Fn(e.Mft, 25))(e.Mft, 0, 1, odb, &status);
                IntPtr got = *(IntPtr*)(odb + IntPtr.Size);
                if (hr == MF.MF_E_TRANSFORM_NEED_MORE_INPUT || hr == MF.MF_E_TRANSFORM_STREAM_CHANGE) { mine?.Release(); if (provides) Com.Release(got); return; }
                if (hr < 0) { mine?.Release(); throw new InvalidOperationException($"the transform's ProcessOutput: 0x{hr:x8}"); }
                MFSample next;
                if (ComObject.From<MFSample>(got) is { } ours && ours.Buffers.Count <= 1) { next = ours; if (!provides) { /* the reader's own sample, filled */ } }
                else { next = Import(got); if (provides) Com.Release(got); else mine?.Release(); }
                Push(k + 1, next);
            }
        }

        private static MFSample Import(IntPtr p)
        {
            var s = new MFSample();
            long t, d;
            if (((delegate* unmanaged<IntPtr, long*, int>)Com.Fn(p, 35))(p, &t) >= 0) s.Time = t;
            if (((delegate* unmanaged<IntPtr, long*, int>)Com.Fn(p, 37))(p, &d) >= 0) s.Duration = d;
            var t2 = Mp4Types.Read(p);                     // the sample's attributes, through IMFAttributes
            foreach (var (k, v) in t2.Attributes.All()) s.Attributes.Set(k, MFAttributesObject.Copy(v));
            t2.Release();
            var bytes = MFSample.ForeignSampleBytes(p);
            var buf = new MFMemoryBuffer((uint)Math.Max(1, bytes.Length));
            byte* q = buf.LockBytes(true);
            bytes.CopyTo(new Span<byte>(q, bytes.Length));
            buf.UnlockBytes();
            buf.CurrentLength = (uint)bytes.Length;
            s.Buffers.Add(buf.Pointer);
            return s;
        }

        public bool TryTake(out MFSample? s) { s = _out.Count > 0 ? _out.Dequeue() : null; return s != null; }

        /// <summary>The stage has ended: each effect drained in order, what it held on through the rest.</summary>
        public void Drain()
        {
            if (_drained) return;
            _drained = true;
            for (int k = 0; k < Effects.Count; k++)
            {
                if (!Effects[k].Streaming) continue;
                Mft.ProcessMessage(Effects[k].Mft, 1, 0);                  // DRAIN
                Pull(k);
            }
        }

        public void Flush()
        {
            while (_out.Count > 0) _out.Dequeue().Release();
            _drained = false;
            foreach (var e in Effects) { if (e.Streaming) Mft.ProcessMessage(e.Mft, 0, 0); e.PreAudio?.Reset(); }
        }

        /// <summary>Every effect ended and let go, as RemoveAllTransformsForStream does.</summary>
        public void RemoveAll()
        {
            while (_out.Count > 0) _out.Dequeue().Release();
            foreach (var e in Effects)
            {
                var t = e.Mft;
                Mft.ProcessMessage(t, 0x10000002, 0); Mft.ProcessMessage(t, 0x10000001, 0);
                StreamAttrs(t, 10);
                Mft.ProcessMessage(t, 0, 0);
                Mft.ProcessMessage(t, 0x10000002, 0); Mft.ProcessMessage(t, 0x10000001, 0);
                SetOut(t, IntPtr.Zero); SetIn(t, IntPtr.Zero);
                Com.Release(t);
                e.In.Release(); e.Out.Release(); e.PreFrom?.Release();
            }
            Effects.Clear();
            _drained = false;
        }

        public void Dispose()
        {
            while (_out.Count > 0) _out.Dequeue().Release();
            foreach (var e in Effects) { Com.Release(e.Mft); e.In.Release(); e.Out.Release(); e.PreFrom?.Release(); }
            Effects.Clear();
        }
    }
}
