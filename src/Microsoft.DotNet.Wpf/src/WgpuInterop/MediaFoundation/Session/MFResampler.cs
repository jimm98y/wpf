// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The audio resampler (CResamplerMediaObject: what the topology loader puts before an audio renderer whose device
// takes another format), an IMFTransform over the reader's converter (Source/AudioConverter.cs: the sample format,
// the channels, the rate): PCM or float in, PCM or float out of any rate and channels; its outputs before a type is
// set the input's, as float then PCM. Each input's frames out as they are made, of the input's time (then counted in
// output frames); the filter's tail out at the drain.
//

using System;
using System.Collections.Generic;
using Microsoft.Wpf.Interop.WebGpu.Direct3D;
using Microsoft.Wpf.Interop.WebGpu.MediaFoundation.Codecs;
using Microsoft.Wpf.Interop.WebGpu.MediaFoundation.Source;

namespace Microsoft.Wpf.Interop.WebGpu.MediaFoundation.Session
{
    internal sealed unsafe class MFResampler : MFEncoder
    {
        public static readonly Guid Clsid = new("f447b69e-1884-4a7e-8055-346f74d6edb3");
        private static readonly Guid MFMediaType_Audio = new("73647561-0000-0010-8000-00aa00389b71");
        private static readonly Guid MF_MT_AUDIO_NUM_CHANNELS = new("37e48bf5-645e-4c5b-89de-ada9e29b696a"), MF_MT_AUDIO_SAMPLES_PER_SECOND = new("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
        private static readonly Guid MF_MT_AUDIO_AVG_BYTES_PER_SECOND = new("1aab75c8-cfef-451c-ab95-ac034b8e1731"), MF_MT_AUDIO_BLOCK_ALIGNMENT = new("322de230-9eeb-43bd-ab7a-ff412251541d");
        private static readonly Guid MF_MT_AUDIO_BITS_PER_SAMPLE = new("f2deb57f-40fa-4764-aa33-ed4f2d1ff669"), MF_MT_AUDIO_CHANNEL_MASK = new("55fb5765-644a-4caf-8479-938983bb1588");

        private AudioConverter? _conv;
        private readonly Queue<byte[]> _out = new();
        private long? _time;                    // the next output's time
        private long _frames;                   // output frames since it
        private bool _draining;

        public MFResampler() : base(Array.Empty<Vtable>()) { }

        public override void Shutdown() { _out.Clear(); _conv = null; }

        private static (bool Float, uint Bits, uint Channels, uint Rate) Format(MFMediaType t)
        {
            bool fl = t.Subtype == MediaFormats.Float;
            var a = t.Attributes;
            return (fl, a.U32(MF_MT_AUDIO_BITS_PER_SAMPLE, fl ? 32u : 16u), a.U32(MF_MT_AUDIO_NUM_CHANNELS, 2), a.U32(MF_MT_AUDIO_SAMPLES_PER_SECOND, 44100));
        }

        /// <summary>An audio type of the format, as the resampler gives it.</summary>
        internal static MFMediaType TypeOf(bool fl, uint bits, uint ch, uint rate)
        {
            var t = new MFMediaType();
            var a = t.Attributes;
            uint block = ch * bits / 8;
            a.Set(MF.MF_MT_MAJOR_TYPE, MFMediaType_Audio);
            a.Set(MF.MF_MT_SUBTYPE, fl ? MediaFormats.Float : MediaFormats.PCM);
            a.Set(MF_MT_AUDIO_NUM_CHANNELS, ch);
            a.Set(MF_MT_AUDIO_SAMPLES_PER_SECOND, rate);
            a.Set(MF_MT_AUDIO_BLOCK_ALIGNMENT, block);
            a.Set(MF_MT_AUDIO_AVG_BYTES_PER_SECOND, block * rate);
            a.Set(MF_MT_AUDIO_BITS_PER_SAMPLE, bits);
            a.Set(MF_MT_AUDIO_CHANNEL_MASK, AudioConverter.Mask((int)ch));
            a.Set(MF.MF_MT_ALL_SAMPLES_INDEPENDENT, 1u);
            return t;
        }

        protected override int InputAvailable(uint i, out MFMediaType? type)
        {
            type = i switch { 0 => TypeOf(true, 32, 2, 48000), 1 => TypeOf(false, 16, 2, 48000), _ => null };
            if (type != null) { type.Attributes.Delete(MF_MT_AUDIO_NUM_CHANNELS); type.Attributes.Delete(MF_MT_AUDIO_SAMPLES_PER_SECOND); type.Attributes.Delete(MF_MT_AUDIO_BLOCK_ALIGNMENT); type.Attributes.Delete(MF_MT_AUDIO_AVG_BYTES_PER_SECOND); type.Attributes.Delete(MF_MT_AUDIO_CHANNEL_MASK); }
            return type == null ? MF.MF_E_NO_MORE_TYPES : MF.S_OK;
        }

        protected override int OutputAvailable(uint i, out MFMediaType? type)
        {
            type = null;
            if (i > 1) return MF.MF_E_NO_MORE_TYPES;
            if (In == IntPtr.Zero) return MF.MF_E_TRANSFORM_TYPE_NOT_SET;
            var input = Read(In);
            var (_, _, ch, rate) = Format(input);
            input.Release();
            type = i == 0 ? TypeOf(true, 32, ch, rate) : TypeOf(false, 16, ch, rate);
            return MF.S_OK;
        }

        private static int Check(MFMediaType t)
        {
            if (t.Attributes.Guid(MF.MF_MT_MAJOR_TYPE) != MFMediaType_Audio || t.Subtype != MediaFormats.Float && t.Subtype != MediaFormats.PCM) return MF.MF_E_INVALIDMEDIATYPE;
            var (fl, bits, ch, rate) = Format(t);
            if (rate is < 1000 or > 384000 || ch is < 1 or > 8 || fl && bits != 32 || !fl && bits is not (8 or 16 or 24 or 32)) return MF.MF_E_INVALIDMEDIATYPE;
            return MF.S_OK;
        }

        protected override int CheckInput(MFMediaType t, out List<(Guid, MFValue)> fill) { fill = new(); return Check(t); }
        protected override int CheckOutput(MFMediaType t, bool test, out List<(Guid, MFValue)> fill) { fill = new(); return Check(t); }

        protected override void TypesChanged()
        {
            _out.Clear(); _time = null; _frames = 0; _draining = false;
            _conv = null;
            if (In == IntPtr.Zero || Out == IntPtr.Zero) return;
            var i = Read(In); var o = Read(Out);
            var (ifl, ib, ich, ir) = Format(i);
            var (ofl, ob, och, or) = Format(o);
            i.Release(); o.Release();
            _conv = new AudioConverter(ifl, (int)ib, (int)ich, (int)ir, ofl, (int)ob, (int)och, (int)or);
        }

        private int OutBlock { get { var o = Read(Out); var (fl, b, ch, _) = Format(o); o.Release(); return (int)(ch * (fl ? 32 : b) / 8); } }
        private uint OutRate { get { var o = Read(Out); var r = Format(o).Rate; o.Release(); return r; } }

        /// <summary>A second of output at most a sample (an input's frames, resampled).</summary>
        protected override uint OutputSize() => Out == IntPtr.Zero ? 0 : (uint)(OutBlock * OutRate);

        protected override int Input(IntPtr sample)
        {
            if (_conv == null) return MF.MF_E_TRANSFORM_TYPE_NOT_SET;
            if (_out.Count > 0) return MF.MF_E_NOTACCEPTING;
            if (_time == null && Time(sample) is long t) { _time = t; _frames = 0; }
            var bytes = _conv.Convert(Bytes(sample));
            if (bytes.Length > 0) _out.Enqueue(bytes);
            return MF.S_OK;
        }

        protected override int Output(IntPtr sample)
        {
            if (_out.Count == 0)
            {
                if (!_draining || _conv == null) return MF.MF_E_TRANSFORM_NEED_MORE_INPUT;
                _draining = false;
                var tail = _conv.Drain();
                if (tail.Length == 0) return MF.MF_E_TRANSFORM_NEED_MORE_INPUT;
                _out.Enqueue(tail);
            }
            var bytes = _out.Dequeue();
            int block = OutBlock; uint rate = OutRate;
            long frames = bytes.Length / block;
            long t0 = (_time ?? 0) + _frames * 10_000_000 / rate;
            _frames += frames;
            long t1 = (_time ?? 0) + _frames * 10_000_000 / rate;
            return Write(sample, bytes, t0, t1 - t0, new[] { (MF.MFSampleExtension_CleanPoint, MFValue.Of(1u)) });
        }

        protected override void Flush() { _out.Clear(); _time = null; _frames = 0; _draining = false; _conv?.Reset(); }
        protected override void Drain() { _draining = true; }
    }
}
