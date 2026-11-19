// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The audio decoders (Documentation/media-foundation-shim.md, "The audio decoders"): IMFTransforms under
// Windows' names and CLSIDs, over the system's decoder (Codecs/PlatformAudioDecoder.cs). As Windows'
// decoders do (probed: MFSourceReaderProbe's audio report):
//
//   - output types 32-bit float, then 16-bit PCM (FLAC and ALAC: then the stream's bits), of the input's
//     rate and channels;
//   - a sample per packet decoded, of its time: the first input's, then counted in frames;
//   - AAC's durations rounded, the others' rounded down; nothing of the priming cut (iTunSMPB is not read).
//

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Wpf.Interop.WebGpu.Direct3D;
using Microsoft.Wpf.Interop.WebGpu.MediaFoundation.Codecs;

namespace Microsoft.Wpf.Interop.WebGpu.MediaFoundation
{
    internal sealed record AudioDecoderInfo(string Name, Guid Clsid, AudioCodec Codec, Guid[] Inputs, bool Listed);

    internal sealed unsafe class MFAudioDecoder : MFTransform
    {
        public static readonly Guid MFMediaType_Audio = new("73647561-0000-0010-8000-00aa00389b71");
        public static readonly Guid MFT_CATEGORY_AUDIO_DECODER = new("9ea73fb4-ef7a-4559-8d5d-719d8f0426c7");
        private static readonly Guid MF_MT_AUDIO_NUM_CHANNELS = new("37e48bf5-645e-4c5b-89de-ada9e29b696a"), MF_MT_AUDIO_SAMPLES_PER_SECOND = new("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
        private static readonly Guid MF_MT_AUDIO_AVG_BYTES_PER_SECOND = new("1aab75c8-cfef-451c-ab95-ac034b8e1731"), MF_MT_AUDIO_BLOCK_ALIGNMENT = new("322de230-9eeb-43bd-ab7a-ff412251541d");
        private static readonly Guid MF_MT_AUDIO_BITS_PER_SAMPLE = new("f2deb57f-40fa-4764-aa33-ed4f2d1ff669"), MF_MT_AUDIO_PREFER_WAVEFORMATEX = new("a901aaba-e037-458a-bdf6-545be2074042");
        private static readonly Guid MF_MT_AUDIO_VALID_BITS_PER_SAMPLE = new("d9bf8d6a-9530-4b7c-9ddf-ff6fd58bbd06");

        public static readonly AudioDecoderInfo[] Registered =
        {
            new("Microsoft AAC Audio Decoder MFT", new Guid("32d186a7-218f-4c75-8876-dd77273a8999"), AudioCodec.AAC, new[] { MediaFormats.AAC, MediaFormats.Wave(0xFF), MediaFormats.Wave(0x1600) }, true),
            new("MP3 Decoder MFT", new Guid("bbeea841-0a63-4f52-a7ab-a9b3a84ed38a"), AudioCodec.Mp3, new[] { MediaFormats.MP3 }, true),
            new("Microsoft FLAC Audio Decoder MFT", new Guid("6b0b3e6b-a2c5-4514-8055-afe8a95242d9"), AudioCodec.Flac, new[] { MediaFormats.FLAC }, true),
            new("Microsoft Opus Audio Decoder MFT", new Guid("63e17c10-2d43-4c42-8fe3-8d8b63e46a6a"), AudioCodec.Opus, new[] { MediaFormats.Opus }, true),
            // Windows lists no ALAC decoder for its ALAC subtype (MFTEnumEx finds none), but its source reader decodes ALAC
            new("Microsoft ALAC Audio Decoder MFT", new Guid("c0cd7d12-31fc-4bbc-b363-7322ee3e1879"), AudioCodec.Alac, new[] { MediaFormats.ALAC }, false),
        };

        private static readonly Vtable s_vt = Build();
        private readonly AudioDecoderInfo _info;
        private readonly object _lock = new();
        private readonly MFAttributeBag _attributes = new();
        private MFMediaType? _in, _out;
        private PlatformAudioDecoder? _decoder;
        private bool _shutdown, _draining;
        private uint _channels, _rate, _bits;          // the decoder's
        private long? _t0;                             // the time of the first frame since a flush
        private long _frames;                          // the frames handed out since
        private bool _discontinuity;
        private readonly Queue<byte[]> _ready = new();
        private byte[] _scratch = Array.Empty<byte>();
        private Packetizer? _packets;
        private long _inFrames, _decodedFrames;           // MP3: the frames pushed (in samples), the samples handed out
        private long _delayLeft = 528;                     // MP3: the decoder's delay still to leave out
        private static readonly Guid MF_MT_AUDIO_CHANNEL_MASK = new("55fb5765-644a-4caf-8479-938983bb1588");

        public MFAudioDecoder(AudioDecoderInfo info) : base(s_vt) { _info = info; }

        public AudioDecoderInfo Info => _info;

        public override void Shutdown()
        {
            lock (_lock) { _shutdown = true; _decoder?.Dispose(); _decoder = null; _ready.Clear(); }
        }

        protected override void OnFinalRelease()
        {
            Shutdown();
            _attributes.Release();
            _in?.Release(); _out?.Release();
        }

        private static MFAudioDecoder? D(IntPtr self) => From<MFAudioDecoder>(self);

        private static Vtable Build()
        {
            var vt = new Vtable("IMFTransform", 26, MF.IID_IMFTransform);
            vt.Set(3, (delegate* unmanaged<IntPtr, uint*, uint*, uint*, uint*, int>)&GetStreamLimits);
            vt.Set(4, (delegate* unmanaged<IntPtr, uint*, uint*, int>)&GetStreamCount);
            vt.Set(5, (delegate* unmanaged<IntPtr, uint, uint*, uint, uint*, int>)&GetStreamIDs);
            vt.Set(6, (delegate* unmanaged<IntPtr, uint, uint*, int>)&GetInputStreamInfo);
            vt.Set(7, (delegate* unmanaged<IntPtr, uint, uint*, int>)&GetOutputStreamInfo);
            vt.Set(8, (delegate* unmanaged<IntPtr, IntPtr*, int>)&GetAttributes);
            vt.Set(9, (delegate* unmanaged<IntPtr, uint, IntPtr*, int>)&NoStreamAttributes);
            vt.Set(10, (delegate* unmanaged<IntPtr, uint, IntPtr*, int>)&NoStreamAttributes);
            vt.Set(11, (delegate* unmanaged<IntPtr, uint, int>)&NoStreams);
            vt.Set(12, (delegate* unmanaged<IntPtr, uint, uint*, int>)&NoAddStreams);
            vt.Set(13, (delegate* unmanaged<IntPtr, uint, uint, IntPtr*, int>)&GetInputAvailableType);
            vt.Set(14, (delegate* unmanaged<IntPtr, uint, uint, IntPtr*, int>)&GetOutputAvailableType);
            vt.Set(15, (delegate* unmanaged<IntPtr, uint, IntPtr, uint, int>)&SetInputType);
            vt.Set(16, (delegate* unmanaged<IntPtr, uint, IntPtr, uint, int>)&SetOutputType);
            vt.Set(17, (delegate* unmanaged<IntPtr, uint, IntPtr*, int>)&GetInputCurrentType);
            vt.Set(18, (delegate* unmanaged<IntPtr, uint, IntPtr*, int>)&GetOutputCurrentType);
            vt.Set(19, (delegate* unmanaged<IntPtr, uint, uint*, int>)&GetInputStatus);
            vt.Set(20, (delegate* unmanaged<IntPtr, uint*, int>)&GetOutputStatus);
            vt.Set(21, (delegate* unmanaged<IntPtr, long, long, int>)&SetOutputBounds);
            vt.Set(22, (delegate* unmanaged<IntPtr, uint, IntPtr, int>)&ProcessEvent);
            vt.Set(23, (delegate* unmanaged<IntPtr, uint, nuint, int>)&ProcessMessage);
            vt.Set(24, (delegate* unmanaged<IntPtr, uint, IntPtr, uint, int>)&ProcessInput);
            vt.Set(25, (delegate* unmanaged<IntPtr, uint, uint, byte*, uint*, int>)&ProcessOutput);
            return vt;
        }

        [UnmanagedCallersOnly]
        private static int GetStreamLimits(IntPtr self, uint* a, uint* b, uint* c, uint* d)
        {
            if (a == null || b == null || c == null || d == null) return MF.E_POINTER;
            *a = *b = *c = *d = 1;
            return MF.S_OK;
        }

        [UnmanagedCallersOnly] private static int GetStreamCount(IntPtr self, uint* i, uint* o) { if (i == null || o == null) return MF.E_POINTER; *i = *o = 1; return MF.S_OK; }
        [UnmanagedCallersOnly] private static int GetStreamIDs(IntPtr self, uint ni, uint* i, uint no, uint* o) => MF.E_NOTIMPL;

        /// <summary>MFT_INPUT_STREAM_INFO: whole samples, one per buffer, of a fixed size, holding none back (flags 0xF, as Windows').</summary>
        /// <summary>Of FLAC, any bytes, 64 KB at most; of MP3 (a DMO's wrapper) nothing before the output's type is set.</summary>
        [UnmanagedCallersOnly]
        private static int GetInputStreamInfo(IntPtr self, uint id, uint* info)
        {
            if (D(self) is not { } d || info == null) return MF.E_POINTER;
            if (id != 0) return MF.MF_E_INVALIDSTREAMNUMBER;
            new Span<byte>(info, 24).Clear();
            lock (d._lock)
            {
                if (d._info.Codec == AudioCodec.Mp3 && d._out == null) return MF.MF_E_TRANSFORM_TYPE_NOT_SET;
                if (d._info.Codec == AudioCodec.Flac) { info[3] = 65536; return MF.S_OK; }
                if (d._info.Codec != AudioCodec.Mp3) info[2] = 0xF;
            }
            return MF.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int GetOutputStreamInfo(IntPtr self, uint id, uint* info)
        {
            if (D(self) is not { } d || info == null) return MF.E_POINTER;
            if (id != 0) return MF.MF_E_INVALIDSTREAMNUMBER;
            lock (d._lock)
            {
                info[0] = 0x1 | 0x2 | 0x4;
                // the longest packet's frames: of AAC 2048 (HE-AAC), of MP3 1152, of Opus 5760, of FLAC and ALAC the stream's
                uint block = d._out != null ? d._out.Attributes.U32(MF_MT_AUDIO_BLOCK_ALIGNMENT) : 0;
                info[1] = block * d.MaxFrames;
                info[2] = 0;
            }
            return MF.S_OK;
        }

        private uint MaxFrames => _info.Codec switch { AudioCodec.AAC => 2048, AudioCodec.Mp3 => 1152, AudioCodec.Opus => 5760, _ => 65536 };

        [UnmanagedCallersOnly] private static int GetAttributes(IntPtr self, IntPtr* a) { if (D(self) is not { } d || a == null) return MF.E_POINTER; *a = d._attributes.AddRefPointer(); return MF.S_OK; }
        [UnmanagedCallersOnly] private static int NoStreamAttributes(IntPtr self, uint id, IntPtr* a) { if (a != null) *a = IntPtr.Zero; return MF.E_NOTIMPL; }
        [UnmanagedCallersOnly] private static int NoStreams(IntPtr self, uint id) => MF.E_NOTIMPL;
        [UnmanagedCallersOnly] private static int NoAddStreams(IntPtr self, uint n, uint* ids) => MF.E_NOTIMPL;

        [UnmanagedCallersOnly]
        private static int GetInputAvailableType(IntPtr self, uint id, uint i, IntPtr* type)
        {
            if (D(self) is not { } d || type == null) return MF.E_POINTER;
            *type = IntPtr.Zero;
            if (id != 0) return MF.MF_E_INVALIDSTREAMNUMBER;
            if (i >= d._info.Inputs.Length) return MF.MF_E_NO_MORE_TYPES;
            var t = new MFMediaType();
            t.Attributes.Set(MF.MF_MT_MAJOR_TYPE, MFMediaType_Audio);
            t.Attributes.Set(MF.MF_MT_SUBTYPE, d._info.Inputs[i]);
            *type = t.Pointer;
            return MF.S_OK;
        }

        /// <summary>The stream's rate, channels and bits, from its configuration where it has them (FLAC, ALAC), else from the type.</summary>
        private (uint Rate, uint Channels, uint Bits) Format()
        {
            var a = _in!.Attributes;
            uint rate = a.U32(MF_MT_AUDIO_SAMPLES_PER_SECOND, 44100), ch = a.U32(MF_MT_AUDIO_NUM_CHANNELS, 2), bits = 16;
            var cfg = a.Get(Source.FlacContainer.FlacMetadataAttribute)?.B ?? a.Get(MF.MF_MT_USER_DATA)?.B ?? Array.Empty<byte>();
            if (_info.Codec == AudioCodec.Alac && cfg.Length >= 24) { bits = cfg[5]; ch = cfg[9]; rate = (uint)(cfg[20] << 24 | cfg[21] << 16 | cfg[22] << 8 | cfg[23]); }
            else if (_info.Codec == AudioCodec.Flac && cfg.Length >= 4 + 18)
            {
                // STREAMINFO: after its 4-byte header, sample rate 20 bits, channels - 1 3 bits, bits - 1 5 bits
                int o = 4 + 10;
                rate = (uint)(cfg[o] << 12 | cfg[o + 1] << 4 | cfg[o + 2] >> 4);
                ch = (uint)((cfg[o + 2] >> 1) & 7) + 1;
                bits = (uint)(((cfg[o + 2] & 1) << 4 | cfg[o + 3] >> 4) + 1);
            }
            if (_info.Codec == AudioCodec.Opus) rate = 48000;
            return (rate, ch, bits);
        }

        /// <summary>An output type, as Windows' decoder of the codec makes it (of <paramref name="channels"/>, 0: the stream's).</summary>
        private MFMediaType OutputType(bool fl, uint bits, uint channels = 0)
        {
            var (rate, ch, _) = Format();
            if (channels != 0) ch = channels;
            var t = new MFMediaType();
            var a = t.Attributes;
            uint block = ch * bits / 8;
            var codec = _info.Codec;
            bool alac = codec == AudioCodec.Alac, flac = codec == AudioCodec.Flac, mp3 = codec == AudioCodec.Mp3;
            if (!alac) a.Set(MF.MF_MT_ALL_SAMPLES_INDEPENDENT, 1u);
            a.Set(MF_MT_AUDIO_AVG_BYTES_PER_SECOND, block * rate);
            a.Set(MF_MT_AUDIO_BITS_PER_SAMPLE, bits);
            a.Set(MF_MT_AUDIO_BLOCK_ALIGNMENT, block);
            // the speakers: FLAC's always, MP3's of its float types
            if (flac || mp3 && fl) a.Set(MF_MT_AUDIO_CHANNEL_MASK, Source.AudioConverter.Mask((int)ch));
            a.Set(MF_MT_AUDIO_NUM_CHANNELS, ch);
            if (!alac && !flac && !(mp3 && fl)) a.Set(MF_MT_AUDIO_PREFER_WAVEFORMATEX, 1u);
            a.Set(MF_MT_AUDIO_SAMPLES_PER_SECOND, rate);
            if ((alac || flac) && !fl) a.Set(MF_MT_AUDIO_VALID_BITS_PER_SAMPLE, bits);
            a.Set(MF.MF_MT_MAJOR_TYPE, MFMediaType_Audio);
            a.Set(MF.MF_MT_SUBTYPE, fl ? MediaFormats.Float : MediaFormats.PCM);
            return t;
        }

        /// <summary>
        /// The output types in Windows' order. AAC (and Opus): float, 16-bit PCM. MP3: float, 16 and 8-bit PCM, of the
        /// stream's channels and then of one. FLAC and ALAC: the stream's PCM alone (Windows' reader converts after them).
        /// </summary>
        private (bool Float, uint Bits, uint Channels)[] Outputs()
        {
            var (_, ch, bits) = Format();
            return _info.Codec switch
            {
                AudioCodec.Alac or AudioCodec.Flac => new[] { (false, bits, 0u) },
                AudioCodec.Mp3 when ch == 2 => new[] { (true, 32u, 0u), (false, 16u, 0u), (false, 8u, 0u), (true, 32u, 1u), (false, 16u, 1u), (false, 8u, 1u) },
                AudioCodec.Mp3 => new[] { (true, 32u, 0u), (false, 16u, 0u), (false, 8u, 0u) },
                _ => new[] { (true, 32u, 0u), (false, 16u, 0u) },
            };
        }

        [UnmanagedCallersOnly]
        private static int GetOutputAvailableType(IntPtr self, uint id, uint i, IntPtr* type)
        {
            if (D(self) is not { } d || type == null) return MF.E_POINTER;
            *type = IntPtr.Zero;
            if (id != 0) return MF.MF_E_INVALIDSTREAMNUMBER;
            lock (d._lock)
            {
                if (d._in == null) return MF.MF_E_TRANSFORM_TYPE_NOT_SET;
                var outs = d.Outputs();
                if (i >= outs.Length) return MF.MF_E_NO_MORE_TYPES;
                *type = d.OutputType(outs[i].Float, outs[i].Bits, outs[i].Channels).Pointer;
            }
            return MF.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int SetInputType(IntPtr self, uint id, IntPtr type, uint flags)
        {
            if (D(self) is not { } d) return MF.E_POINTER;
            if (id != 0) return MF.MF_E_INVALIDSTREAMNUMBER;
            bool test = (flags & 1) != 0;
            lock (d._lock)
            {
                if (d._shutdown) return MF.MF_E_SHUTDOWN;
                if (type == IntPtr.Zero) { if (!test) { d._in?.Release(); d._in = null; d._out?.Release(); d._out = null; d.Reset(); } return MF.S_OK; }
                var t = Source.Mp4Types.Read(type);
                bool ok = t.Attributes.Guid(MF.MF_MT_MAJOR_TYPE) == MFMediaType_Audio && Array.IndexOf(d._info.Inputs, t.Subtype) >= 0 && PlatformAudioDecoder.Supports(d._info.Codec);
                if (!ok || test) { t.Release(); return ok ? MF.S_OK : MF.MF_E_INVALIDMEDIATYPE; }
                d._in?.Release(); d._in = t;
                d._out?.Release(); d._out = null;
                d.Reset();
                // a byte stream (Windows' ADTS, MP3 and FLAC sources), not packets: the frames found here
                d._packets = d._info.Codec switch
                {
                    AudioCodec.AAC when t.Attributes.U32(Source.SR.MF_MT_AAC_PAYLOAD_TYPE) == 1 => new Packetizer(Packetizer.Kind.Adts),
                    AudioCodec.Mp3 => new Packetizer(Packetizer.Kind.Mpeg),
                    AudioCodec.Flac when t.Attributes.Get(Source.FlacContainer.FlacMetadataAttribute) != null => new Packetizer(Packetizer.Kind.Flac),
                    _ => null,
                };
            }
            return MF.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int SetOutputType(IntPtr self, uint id, IntPtr type, uint flags)
        {
            if (D(self) is not { } d) return MF.E_POINTER;
            if (id != 0) return MF.MF_E_INVALIDSTREAMNUMBER;
            bool test = (flags & 1) != 0;
            lock (d._lock)
            {
                if (d._in == null) return MF.MF_E_TRANSFORM_TYPE_NOT_SET;
                if (type == IntPtr.Zero) { if (!test) { d._out?.Release(); d._out = null; } return MF.S_OK; }
                var t = Source.Mp4Types.Read(type);
                bool fl = t.Subtype == MediaFormats.Float;
                uint bits = t.Attributes.U32(MF_MT_AUDIO_BITS_PER_SAMPLE, fl ? 32u : 16u);
                var (rate, ch, _) = d.Format();
                uint och = t.Attributes.U32(MF_MT_AUDIO_NUM_CHANNELS, ch);
                bool ok = t.Attributes.Guid(MF.MF_MT_MAJOR_TYPE) == MFMediaType_Audio && Array.Exists(d.Outputs(), o => o.Float == fl && o.Bits == bits && (o.Channels == 0 ? ch : o.Channels) == och)
                    && t.Attributes.U32(MF_MT_AUDIO_SAMPLES_PER_SECOND, rate) == rate;
                t.Release();
                if (!ok) return MF.MF_E_INVALIDMEDIATYPE;
                if (test) return MF.S_OK;
                d._out?.Release();
                d._out = d.OutputType(fl, bits, och);
                d._conv = null;
            }
            return MF.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int GetInputCurrentType(IntPtr self, uint id, IntPtr* type)
        {
            if (D(self) is not { } d || type == null) return MF.E_POINTER;
            *type = IntPtr.Zero;
            lock (d._lock) { if (d._in == null) return MF.MF_E_TRANSFORM_TYPE_NOT_SET; *type = d._in.Clone().Pointer; }
            return MF.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int GetOutputCurrentType(IntPtr self, uint id, IntPtr* type)
        {
            if (D(self) is not { } d || type == null) return MF.E_POINTER;
            *type = IntPtr.Zero;
            lock (d._lock) { if (d._out == null) return MF.MF_E_TRANSFORM_TYPE_NOT_SET; *type = d._out.Clone().Pointer; }
            return MF.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int GetInputStatus(IntPtr self, uint id, uint* f)
        {
            if (D(self) is not { } d || f == null) return MF.E_POINTER;
            lock (d._lock) *f = d.Pending() ? 0u : 1u;
            return MF.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int GetOutputStatus(IntPtr self, uint* f)
        {
            if (D(self) is not { } d || f == null) return MF.E_POINTER;
            lock (d._lock) *f = d.Pending() ? 1u : 0u;
            return MF.S_OK;
        }

        [UnmanagedCallersOnly] private static int SetOutputBounds(IntPtr self, long lo, long hi) => MF.E_NOTIMPL;
        [UnmanagedCallersOnly] private static int ProcessEvent(IntPtr self, uint id, IntPtr e) => MF.E_NOTIMPL;

        private void Reset()
        {
            _decoder?.Dispose(); _decoder = null;
            _ready.Clear();
            _t0 = null; _frames = 0; _draining = false;
        }

        private PlatformAudioDecoder? Decoder
        {
            get
            {
                if (_decoder != null || _in == null) return _decoder;
                var (rate, ch, _) = Format();
                var cfg = _in.Attributes.Get(Source.FlacContainer.FlacMetadataAttribute)?.B ?? _in.Attributes.Get(MF.MF_MT_USER_DATA)?.B ?? Array.Empty<byte>();
                // AAC's user data: HEAACWAVEINFO's 12 bytes, then the AudioSpecificConfig
                if (_info.Codec == AudioCodec.AAC) cfg = cfg.Length > 12 ? cfg.AsSpan(12).ToArray() : cfg;
                _decoder = PlatformAudioDecoder.Create(_info.Codec, new AudioDecoderOptions { Channels = ch, SampleRate = rate, Config = cfg.Length > 0 ? cfg : null });
                if (_decoder != null) { _channels = _decoder.Channels; _rate = _decoder.SampleRate; _bits = _decoder.BitsPerSample; }
                return _decoder;
            }
        }

        /// <summary>Whether decoded audio is waiting (taken from the decoder into the queue).</summary>
        private bool Pending()
        {
            if (_ready.Count > 0) return true;
            if (_decoder == null) return false;
            while (_decoder.TryTake(ref _scratch, out uint n))
            {
                if (n == 0) continue;
                _ready.Enqueue(_scratch.AsSpan(0, (int)n).ToArray());
            }
            return _ready.Count > 0;
        }

        [UnmanagedCallersOnly]
        private static int ProcessMessage(IntPtr self, uint message, nuint param)
        {
            if (D(self) is not { } d) return MF.E_POINTER;
            lock (d._lock)
            {
                switch (message)
                {
                    case 0:                                   // MFT_MESSAGE_COMMAND_FLUSH
                        d._decoder?.Flush();
                        d._packets?.Reset();
                        d._inFrames = d._decodedFrames = 0; d._delayLeft = 528;
                        d._ready.Clear();
                        d._t0 = null; d._frames = 0; d._draining = false;
                        return MF.S_OK;
                    case 1:                                   // MFT_MESSAGE_COMMAND_DRAIN
                        if (d._packets != null && d._decoder != null) foreach (var frame in d._packets.Push(ReadOnlySpan<byte>.Empty, end: true)) d._decoder.Push(frame, 0);
                        d._decoder?.BeginDrain();
                        d._draining = true;
                        return MF.S_OK;
                    default:
                        return MF.S_OK;
                }
            }
        }

        [UnmanagedCallersOnly]
        private static int ProcessInput(IntPtr self, uint id, IntPtr sample, uint flags)
        {
            if (D(self) is not { } d || sample == IntPtr.Zero) return MF.E_POINTER;
            if (id != 0) return MF.MF_E_INVALIDSTREAMNUMBER;
            try
            {
                lock (d._lock)
                {
                    if (d._shutdown) return MF.MF_E_SHUTDOWN;
                    if (d._in == null) return MF.MF_E_TRANSFORM_TYPE_NOT_SET;
                    if (d.Pending()) return MF.MF_E_NOTACCEPTING;
                    var dec = d.Decoder;
                    if (dec == null) return MF.E_FAIL;
                    if (d._draining) { dec.EndDrain(); d._draining = false; }
                    long time = 0;
                    byte[] bytes;
                    if (From<MFSample>(sample) is { } s)
                    {
                        bytes = s.Bytes(); time = s.Time ?? 0;
                        if (s.Attributes.U32(MF.MFSampleExtension_Discontinuity) != 0) d._discontinuity = true;
                    }
                    else
                    {
                        bytes = MFSample.ForeignSampleBytes(sample);
                        long t;
                        if (((delegate* unmanaged<IntPtr, long*, int>)Com.Fn(sample, 35))(sample, &t) >= 0) time = t;
                    }
                    d._t0 ??= time;
                    if (d._packets != null)
                        foreach (var frame in d._packets.Push(bytes))
                        {
                            if (d._info.Codec == AudioCodec.Mp3 && Source.Mp3Container.Header(frame, out _, out _, out int fch, out _, out int spf))
                            {
                                d._inFrames += spf;
                                // a Xing / Info frame (an encoder's header, no sound): Windows decodes it as a frame of silence; some
                                // decoders (GStreamer's) drop it, so it is silence here, whichever the decoder
                                if (IsXing(frame, fch))
                                {
                                    if (d._channels == 0) { d._channels = dec.Channels; d._rate = dec.SampleRate; d._bits = dec.BitsPerSample; }
                                    d._ready.Enqueue(new byte[spf * d._channels * d._bits / 8]);
                                    continue;
                                }
                            }
                            dec.Push(frame, time);
                        }
                    else dec.Push(bytes, time);
                }
                return MF.S_OK;
            }
            catch (Exception e) { D3DLog.Error("IMFTransform.ProcessInput (audio)", e); return MF.E_FAIL; }
        }

        [UnmanagedCallersOnly]
        private static int ProcessOutput(IntPtr self, uint flags, uint count, byte* buffers, uint* status)
        {
            if (D(self) is not { } d || buffers == null || status == null) return MF.E_POINTER;
            if (count != 1) return MF.E_INVALIDARG;
            *status = 0;
            try
            {
                lock (d._lock)
                {
                    if (d._in == null || d._out == null) return MF.MF_E_TRANSFORM_TYPE_NOT_SET;
                    if (!d.Pending())
                    {
                        if (d._draining) { d._decoder?.EndDrain(); d._draining = false; }
                        return MF.MF_E_TRANSFORM_NEED_MORE_INPUT;
                    }
                    IntPtr sample = *(IntPtr*)(buffers + IntPtr.Size);
                    if (sample == IntPtr.Zero) return MF.E_INVALIDARG;
                    var pcm = d._ready.Dequeue();
                    // MP3: Windows' decoder hands out 528 frames fewer than the frames hold: its delay, left out at the start and
                    // not flushed at the end (where the decoder keeps it, the first 528 are dropped here)
                    if (d._info.Codec == AudioCodec.Mp3 && d._channels > 0)
                    {
                        int fb = (int)(d._channels * d._bits / 8);
                        if (d._decoder is { DropsMp3Delay: false } && d._delayLeft > 0)
                        {
                            int cut = (int)Math.Min(d._delayLeft, pcm.Length / fb);
                            pcm = pcm.AsSpan(cut * fb).ToArray();
                            d._delayLeft -= cut;
                            if (pcm.Length == 0) return MF.MF_E_TRANSFORM_NEED_MORE_INPUT;
                        }
                        long room = Math.Max(0, d._inFrames - 528 - d._decodedFrames);
                        long have = pcm.Length / fb;
                        if (have > room) pcm = pcm.AsSpan(0, (int)(room * fb)).ToArray();
                        d._decodedFrames += Math.Min(have, room);
                        if (pcm.Length == 0) return d.Pending() ? MF.MF_E_TRANSFORM_NEED_MORE_INPUT : MF.MF_E_TRANSFORM_NEED_MORE_INPUT;
                    }
                    var bytes = d.Convert(pcm, out long frames);
                    long rate = d._rate == 0 ? 44100 : d._rate;
                    long t = d._t0 ?? 0;
                    long time = t + d._frames * 10_000_000 / rate;
                    long duration = d._info.Codec == AudioCodec.AAC ? (frames * 10_000_000 + rate / 2) / rate : frames * 10_000_000 / rate;
                    d._frames += frames;
                    int hr = Write(sample, bytes, time, duration, d._discontinuity && d._info.Codec != AudioCodec.AAC, d._info.Codec != AudioCodec.Flac);
                    d._discontinuity = false;
                    return hr;
                }
            }
            catch (Exception e) { D3DLog.Error("IMFTransform.ProcessOutput (audio)", e); return MF.E_FAIL; }
        }

        /// <summary>The decoder's PCM as the output type: its sample format and channels.</summary>
        private byte[] Convert(byte[] pcm, out long frames)
        {
            bool fromFloat = _bits == 32 && _info.Codec == AudioCodec.Opus;
            frames = pcm.Length / Math.Max(1, (int)(_channels * _bits / 8));
            bool toFloat = _out!.Subtype == MediaFormats.Float;
            uint outBits = _out.Attributes.U32(MF_MT_AUDIO_BITS_PER_SAMPLE, 16), outCh = _out.Attributes.U32(MF_MT_AUDIO_NUM_CHANNELS, _channels);
            if (toFloat == fromFloat && outBits == _bits && outCh == _channels) return pcm;
            _conv ??= new Source.AudioConverter(fromFloat, (int)_bits, (int)_channels, (int)_rate, toFloat, (int)outBits, (int)outCh, (int)_rate);
            return _conv.Convert(pcm);
        }

        private Source.AudioConverter? _conv;

        /// <summary>Whether an MPEG audio frame is a Xing or Info header: the tag after the side information.</summary>
        private static bool IsXing(byte[] f, int channels)
        {
            bool mpeg1 = ((f[1] >> 3) & 3) == 3;
            int side = mpeg1 ? (channels == 1 ? 17 : 32) : (channels == 1 ? 9 : 17);
            int at = 4 + side;
            if (at + 4 > f.Length) return false;
            var tag = f.AsSpan(at, 4);
            return tag.SequenceEqual("Xing"u8) || tag.SequenceEqual("Info"u8);
        }

        private static int Write(IntPtr sample, byte[] bytes, long time, long duration, bool discontinuity, bool cleanPoint = true)
        {
            IntPtr buf;
            int hr = ((delegate* unmanaged<IntPtr, uint, IntPtr*, int>)Com.Fn(sample, 40))(sample, 0, &buf);
            if (hr < 0) return hr;
            try
            {
                byte* p; uint max, cur;
                hr = ((delegate* unmanaged<IntPtr, byte**, uint*, uint*, int>)Com.Fn(buf, 3))(buf, &p, &max, &cur);
                if (hr < 0) return hr;
                try
                {
                    if (max < bytes.Length) return MF.MF_E_BUFFERTOOSMALL;
                    bytes.CopyTo(new Span<byte>(p, bytes.Length));
                }
                finally { ((delegate* unmanaged<IntPtr, int>)Com.Fn(buf, 4))(buf); }
                ((delegate* unmanaged<IntPtr, uint, int>)Com.Fn(buf, 6))(buf, (uint)bytes.Length);
            }
            finally { Com.Release(buf); }
            ((delegate* unmanaged<IntPtr, long, int>)Com.Fn(sample, 36))(sample, time);
            ((delegate* unmanaged<IntPtr, long, int>)Com.Fn(sample, 38))(sample, duration);
            var key = MF.MFSampleExtension_CleanPoint;
            if (cleanPoint) ((delegate* unmanaged<IntPtr, Guid*, uint, int>)Com.Fn(sample, 21))(sample, &key, 1);
            if (discontinuity) { key = MF.MFSampleExtension_Discontinuity; ((delegate* unmanaged<IntPtr, Guid*, uint, int>)Com.Fn(sample, 21))(sample, &key, 1); }
            return MF.S_OK;
        }
    }
}
