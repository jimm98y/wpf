// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A source reader's stream (MFSourceReader.cs): its track's samples in order, its native and current
// types, the decoder (an MFT of the shim's, driven through its IMFTransform vtable as Windows' reader
// drives one) and the conversion to RGB after it. The native types are Windows' MPEG-4 source's.
//

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Wpf.Interop.WebGpu.Direct3D;
using Microsoft.Wpf.Interop.WebGpu.MediaFoundation.Codecs;

namespace Microsoft.Wpf.Interop.WebGpu.MediaFoundation.Source
{
    internal static class Mp4Types
    {
        /// <summary>A track's native type, as Windows' MPEG-4 source makes it; null where it makes none.</summary>
        public static MFMediaType? Native(Mp4Track t, Mp4Demuxer d)
        {
            var mt = new MFMediaType();
            var a = mt.Attributes;
            if (t.Video)
            {
                Guid? sub = t.Format switch
                {
                    "avc1" or "avc3" => MF.MFVideoFormat_H264,
                    "hvc1" or "hev1" => MF.MFVideoFormat_HEVC,
                    "vp09" => MediaFormats.VP90,
                    "av01" => MediaFormats.AV1,
                    "mp4v" => MediaFormats.MP4V,
                    "s263" or "h263" => MediaFormats.H263,
                    "mjpa" or "mjpb" or "jpeg" => SR.MFVideoFormat_MJPG,
                    _ => null,
                };
                if (sub == null) { D3DLog.Once("mf-mp4-video:" + t.Format, $"Media Foundation: the MPEG-4 file's video '{t.Format}' is not read"); mt.Release(); return null; }
                var (w, h) = Mp4Demuxer.PictureSize(t) ?? (t.Width, t.Height);
                a.Set(MF.MF_MT_MAJOR_TYPE, MF.MFMediaType_Video);
                a.Set(MF.MF_MT_SUBTYPE, sub.Value);
                if (t.AnnexB) a.Set(SR.MF_MT_AM_FORMAT_TYPE, SR.FORMAT_MPEG2Video);
                a.Set(MF.MF_MT_FRAME_SIZE, MF.Pack((uint)w, (uint)h));
                if (FrameRate(t) is { } fr) a.Set(MF.MF_MT_FRAME_RATE, MF.Pack(fr.N, fr.D));
                a.Set(MF.MF_MT_PIXEL_ASPECT_RATIO, MF.Pack(t.ParW == 0 ? 1 : t.ParW, t.ParH == 0 ? 1 : t.ParH));
                a.Set(MF.MF_MT_INTERLACE_MODE, 7u);                                  // MFVideoInterlace_MixedInterlaceOrProgressive
                if (!t.Hevc && t.AnnexB) a.Set(SR.MF_PROGRESSIVE_CODING_CONTENT, Mp4Demuxer.H264FramesOnly(t) ? 1u : 0u);
                if (t.AnnexB) a.Set(SR.MF_NALU_LENGTH_SET, 1u);
                a.Set(SR.MF_MT_AVG_BITRATE, Bitrate(t, d));
                if (t.AnnexB && Mp4Demuxer.ProfileLevel(t) is { } pl) { a.Set(SR.MF_MT_MPEG2_PROFILE, pl.Profile); a.Set(SR.MF_MT_MPEG2_LEVEL, pl.Level); }
                if (t.AnnexB && t.ParameterSets.Count > 0) a.Set(MF.MF_MT_MPEG_SEQUENCE_HEADER, MFValue.Of(Mp4Demuxer.SequenceHeader(t)));
                else if (t.Format == "mp4v" && t.Config.Length > 0) a.Set(MF.MF_MT_MPEG_SEQUENCE_HEADER, MFValue.Of(t.Config));
                else if (t.Config.Length > 0) a.Set(MF.MF_MT_USER_DATA, MFValue.Of(t.Config));
                a.Set(SR.MF_MT_MPEG4_SAMPLE_DESCRIPTION, MFValue.Of(t.SampleDescription));
                a.Set(SR.MF_MT_MPEG4_CURRENT_SAMPLE_ENTRY, 0u);
                a.Set(MF.MF_MT_SAMPLE_SIZE, 1u);
                a.Set(SR.MF_MT_VIDEO_ROTATION, (uint)t.Rotation);
                return mt;
            }
            Guid? asub = t.Format switch
            {
                "mp4a" => t.ObjectType is 0x69 or 0x6B ? SR.MFAudioFormat_MP3 : SR.MFAudioFormat_AAC,
                ".mp3" => SR.MFAudioFormat_MP3,
                "sowt" or "twos" or "lpcm" or "ipcm" or "raw " => SR.MFAudioFormat_PCM,
                "fl32" or "fl64" or "fpcm" => SR.MFAudioFormat_Float,
                "fLaC" => SR.MFAudioFormat_FLAC,
                "alac" => SR.MFAudioFormat_ALAC,
                "Opus" => SR.MFAudioFormat_Opus,
                "ac-3" => SR.MFAudioFormat_Dolby_AC3,
                "ec-3" => SR.MFAudioFormat_Dolby_DDPlus,
                _ => null,
            };
            if (asub == null) { D3DLog.Once("mf-mp4-audio:" + t.Format, $"Media Foundation: the MPEG-4 file's audio '{t.Format}' is not read"); mt.Release(); return null; }
            bool pcm = asub == SR.MFAudioFormat_PCM || asub == SR.MFAudioFormat_Float;
            int bits = pcm ? (t.Format is "fl64" ? 64 : t.Format is "fl32" ? 32 : t.SampleBits) : 16;
            int channels = Math.Max(1, t.Channels), rate = t.SampleRate > 0 ? t.SampleRate : (int)t.Timescale;
            uint bitrate = t.EsdsBitrate != 0 ? t.EsdsBitrate : Bitrate(t, d);
            a.Set(MF.MF_MT_MAJOR_TYPE, SR.MFMediaType_Audio);
            a.Set(MF.MF_MT_SUBTYPE, asub.Value);
            if (!pcm && asub != SR.MFAudioFormat_AAC && asub != SR.MFAudioFormat_MP3)
            {
                // ALAC as Windows has it (FLAC, Opus, AC-3 likewise): the bytes per second its configuration gives, no WAVEFORMATEX
                if (asub == SR.MFAudioFormat_ALAC && t.Config.Length >= 24)
                {
                    bits = t.Config[5]; channels = t.Config[9];
                    rate = (int)((uint)t.Config[20] << 24 | (uint)t.Config[21] << 16 | (uint)t.Config[22] << 8 | t.Config[23]);
                    bitrate = 8 * ((uint)t.Config[16] << 24 | (uint)t.Config[17] << 16 | (uint)t.Config[18] << 8 | t.Config[19]);
                }
                a.Set(SR.MF_MT_AUDIO_AVG_BYTES_PER_SECOND, bitrate / 8);
                a.Set(SR.MF_MT_AUDIO_BITS_PER_SAMPLE, (uint)bits);
                a.Set(SR.MF_MT_AUDIO_NUM_CHANNELS, (uint)channels);
                a.Set(SR.MF_MT_AUDIO_SAMPLES_PER_SECOND, (uint)rate);
                a.Set(SR.MF_MT_AVG_BITRATE, bitrate);
                if (t.Config.Length > 0) a.Set(MF.MF_MT_USER_DATA, MFValue.Of(t.Config));
                a.Set(SR.MF_MT_MPEG4_SAMPLE_DESCRIPTION, MFValue.Of(t.SampleDescription));
                a.Set(SR.MF_MT_MPEG4_CURRENT_SAMPLE_ENTRY, 0u);
                return mt;
            }
            a.Set(SR.MF_MT_AM_FORMAT_TYPE, SR.FORMAT_WaveFormatEx);
            a.Set(SR.MF_MT_AUDIO_NUM_CHANNELS, (uint)channels);
            a.Set(SR.MF_MT_AUDIO_SAMPLES_PER_SECOND, (uint)rate);
            a.Set(SR.MF_MT_AUDIO_BITS_PER_SAMPLE, (uint)bits);
            a.Set(SR.MF_MT_AUDIO_BLOCK_ALIGNMENT, pcm ? (uint)(channels * bits / 8) : 1u);
            a.Set(SR.MF_MT_AUDIO_AVG_BYTES_PER_SECOND, pcm ? (uint)(channels * bits / 8 * rate) : bitrate / 8);
            if (!pcm) a.Set(SR.MF_MT_AVG_BITRATE, bitrate);
            if (asub == SR.MFAudioFormat_AAC)
            {
                a.Set(SR.MF_MT_AAC_PAYLOAD_TYPE, 0u);                               // raw
                a.Set(SR.MF_MT_AAC_AUDIO_PROFILE_LEVEL_INDICATION, 0u);
                // HEAACWAVEINFO's fields after WAVEFORMATEX (payload type, profile level, struct type, reserved), then AudioSpecificConfig
                var ud = new byte[12 + t.Config.Length];
                t.Config.CopyTo(ud, 12);
                a.Set(MF.MF_MT_USER_DATA, MFValue.Of(ud));
            }
            else if (t.Config.Length > 0) a.Set(MF.MF_MT_USER_DATA, MFValue.Of(t.Config));
            a.Set(MF.MF_MT_ALL_SAMPLES_INDEPENDENT, 1u);
            a.Set(MF.MF_MT_FIXED_SIZE_SAMPLES, 1u);
            a.Set(MF.MF_MT_SAMPLE_SIZE, pcm ? (uint)(channels * bits / 8) : 1u);
            a.Set(SR.MF_MT_AUDIO_PREFER_WAVEFORMATEX, 1u);
            a.Set(SR.MF_MT_MPEG4_SAMPLE_DESCRIPTION, MFValue.Of(t.SampleDescription));
            a.Set(SR.MF_MT_MPEG4_CURRENT_SAMPLE_ENTRY, 0u);
            return mt;
        }

        /// <summary>The track's rate: its timescale over its first sample's duration, reduced.</summary>
        private static (uint N, uint D)? FrameRate(Mp4Track t)
        {
            uint dur = t.Samples.Count > 0 ? t.Samples[0].Duration : 0;
            if (dur == 0 || t.Timescale == 0) return null;
            uint n = t.Timescale, g = Gcd(n, dur);
            return (n / g, dur / g);
        }

        private static uint Gcd(uint a, uint b) { while (b != 0) (a, b) = (b, a % b); return a; }

        /// <summary>
        /// The bits per second of the samples known when the file is opened (in a fragmented file, the moov's and
        /// the first fragment's) over the track's duration, or the movie's where the track gives none -- as Windows has it.
        /// </summary>
        private static uint Bitrate(Mp4Track t, Mp4Demuxer d)
        {
            long bytes = 0;
            int firstFragment = t.Samples.Where(s => s.Fragment >= 0).Select(s => s.Fragment).DefaultIfEmpty(-1).Min();
            foreach (var s in t.Samples) if (s.Fragment < 0 || s.Fragment == firstFragment) bytes += s.Size;
            long hns = t.MediaDuration > 0 ? t.Hns((long)t.MediaDuration) : (long)((UInt128)(d.MovieDuration != 0 ? d.MovieDuration : d.FragmentDuration) * 10_000_000 / d.MovieTimescale);
            if (hns <= 0) return 0;
            UInt128 bps = (UInt128)bytes * 8 * 10_000_000 / (ulong)hns;
            return bps > uint.MaxValue ? uint.MaxValue : (uint)bps;
        }

        /// <summary>A copy of a media type, ours or the application's.</summary>
        public static unsafe MFMediaType Read(IntPtr type)
        {
            if (ComObject.From<MFMediaType>(type) is { } ours) return ours.Clone();
            var t = new MFMediaType();
            uint n;
            ((delegate* unmanaged<IntPtr, uint*, int>)Com.Fn(type, 30))(type, &n);
            var pv = stackalloc byte[24];
            for (uint i = 0; i < n; i++)
            {
                new Span<byte>(pv, 24).Clear();
                Guid k;
                if (((delegate* unmanaged<IntPtr, uint, Guid*, byte*, int>)Com.Fn(type, 31))(type, i, &k, pv) < 0) continue;
                if (MFAttributeStore.Read(pv) is { } v) t.Attributes.Set(k, v);
                Ole.PropVariantClear(pv);
            }
            return t;
        }
    }

    internal static unsafe class Ole
    {
        /// <summary>Frees what a PROPVARIANT points to (of the types attributes hold).</summary>
        public static void PropVariantClear(byte* pv)
        {
            ushort vt = *(ushort*)pv;
            if (vt is MF.VT_CLSID or MF.VT_LPWSTR) System.Runtime.InteropServices.Marshal.FreeCoTaskMem(*(IntPtr*)(pv + 8));
            else if (vt == MF.VT_BLOB) System.Runtime.InteropServices.Marshal.FreeCoTaskMem(*(IntPtr*)(pv + MF.BlobPointerOffset));
            else if (vt == MF.VT_UNKNOWN) Com.Release(*(IntPtr*)(pv + 8));
            *(ushort*)pv = 0;
        }
    }

    internal sealed unsafe class ReaderStream : IDisposable
    {
        private readonly MFSourceReader _reader;
        public readonly int Index;
        public readonly Mp4Track Track;
        public readonly MFMediaType Native;
        public MFMediaType Current;
        public bool Selected;
        private int _next;
        private bool _discontinuity = true;
        /// <summary>Every sample read (and decoded); the end told.</summary>
        public bool Ended;
        private bool _inputDone, _drainSent;

        // decoding
        public MFTransform? Decoder;
        private Guid _decoderOut;              // the decoder's subtype
        private Guid _rgb;                     // the RGB type after it, or empty
        private bool _firstOut = true;

        public ReaderStream(MFSourceReader reader, int index, Mp4Track track, MFMediaType native)
        {
            _reader = reader; Index = index; Track = track; Native = native;
            Current = native.Clone();
        }

        public void Dispose()
        {
            DropDecoder();
            Native.Release();
            Current.Release();
        }

        private void DropDecoder()
        {
            if (Decoder == null) return;
            Decoder.Shutdown();
            Decoder.Release();
            Decoder = null;
            _rgb = Guid.Empty;
            _conv = null;
        }

        /// <summary>The time of what comes next (to order the streams of an ANY read).</summary>
        public long NextTime => _next < Track.Samples.Count ? Track.Pts(Track.Samples[_next]) : long.MaxValue;

        // ---- the current type ---------------------------------------------------------------------------

        private static readonly Guid[] s_decoderOutputs = { MF.MFVideoFormat_NV12, MF.MFVideoFormat_YV12, MF.MFVideoFormat_IYUV, MF.MFVideoFormat_I420, MF.MFVideoFormat_YUY2 };
        private static readonly Guid[] s_rgb = { SR.MFVideoFormat_RGB32, SR.MFVideoFormat_ARGB32, SR.MFVideoFormat_RGB24 };

        public int SetCurrent(MFMediaType t)
        {
            try
            {
                var major = t.Attributes.Guid(MF.MF_MT_MAJOR_TYPE);
                var sub = t.Subtype;
                if (major != Native.Attributes.Guid(MF.MF_MT_MAJOR_TYPE)) return MF.MF_E_INVALIDMEDIATYPE;
                if (sub == Native.Subtype)
                {
                    DropDecoder();
                    Replace(Native.Clone());
                    return MF.S_OK;
                }
                if (Track.Audio) return SetAudio(t, sub);
                bool rgb = Array.IndexOf(s_rgb, sub) >= 0;
                if (Array.IndexOf(s_decoderOutputs, sub) < 0 && !rgb) return MF.MF_E_INVALIDMEDIATYPE;
                if (rgb && !_reader.VideoProcessing) return MF.MF_E_INVALIDMEDIATYPE;
                var info = MFVideoDecoder.Registered.FirstOrDefault(i => Array.IndexOf(i.Inputs, Native.Subtype) >= 0);
                if (info == null || !PlatformVideoDecoder.Supports(info.Codec)) return SR.MF_E_TOPO_CODEC_NOT_FOUND;
                var dec = new MFVideoDecoder(info);
                IntPtr p = dec.Pointer;
                if (_reader.Manager != IntPtr.Zero)
                {
                    int mhr = Mft.ProcessMessage(p, 2, (nuint)_reader.Manager);                // MFT_MESSAGE_SET_D3D_MANAGER
                    if (mhr < 0) { dec.Shutdown(); dec.Release(); return mhr; }
                }
                int hr = Mft.SetInputType(p, Native.Pointer);
                if (hr < 0) { dec.Shutdown(); dec.Release(); return SR.MF_E_TOPO_CODEC_NOT_FOUND; }
                Guid decSub = rgb ? MF.MFVideoFormat_NV12 : sub;
                if (_reader.Manager != IntPtr.Zero && decSub != MF.MFVideoFormat_NV12 && !rgb) { dec.Shutdown(); dec.Release(); return MF.MF_E_INVALIDMEDIATYPE; }
                hr = Mft.SetOutputOf(p, decSub);
                if (hr < 0) { dec.Shutdown(); dec.Release(); return MF.MF_E_INVALIDMEDIATYPE; }
                DropDecoder();
                Decoder = dec;
                _decoderOut = decSub;
                _rgb = rgb ? sub : Guid.Empty;
                _firstOut = true;
                Mft.ProcessMessage(p, 0x10000000, 0);                                       // MFT_MESSAGE_NOTIFY_BEGIN_STREAMING
                Mft.ProcessMessage(p, 0x10000003, 0);                                       // MFT_MESSAGE_NOTIFY_START_OF_STREAM
                var (w, h) = MF.Unpack(Native.Attributes.U64(MF.MF_MT_FRAME_SIZE));
                Replace(Decoded(sub, w, h, null));
                // reading goes on from the next sync sample
                RestartAtSync();
                return MF.S_OK;
            }
            finally { t.Release(); }
        }

        private void Replace(MFMediaType t) { Current.Release(); Current = t; }

        // ---- audio ------------------------------------------------------------------------------------

        private static readonly Guid MF_MT_AUDIO_VALID_BITS_PER_SAMPLE = new("d9bf8d6a-9530-4b7c-9ddf-ff6fd58bbd06");
        private static readonly Guid MF_MT_AUDIO_CHANNEL_MASK = new("55fb5765-644a-4caf-8479-938983bb1588");
        /// <summary>What converts the decoder's output to the type asked for (Windows' resampler between them); null where none.</summary>
        private AudioConverter? _conv;
        private long _convFrames;
        private long? _convT0;
        private bool _convDrained;

        /// <summary>A decoder of the stream's codec, of PCM or float; the bits it does not give converted after it.</summary>
        private int SetAudio(MFMediaType t, Guid sub)
        {
            bool fl = sub == MediaFormats.Float;
            if (sub != MediaFormats.PCM && !fl) return MF.MF_E_INVALIDMEDIATYPE;
            var info = MFAudioDecoder.Registered.FirstOrDefault(i => Array.IndexOf(i.Inputs, Native.Subtype) >= 0);
            if (info == null || !PlatformAudioDecoder.Supports(info.Codec)) return SR.MF_E_TOPO_CODEC_NOT_FOUND;
            var dec = new MFAudioDecoder(info);
            IntPtr p = dec.Pointer;
            if (Mft.SetInputType(p, Native.Pointer) < 0) { dec.Shutdown(); dec.Release(); return SR.MF_E_TOPO_CODEC_NOT_FOUND; }
            // the decoder's types: the one asked for, or the first of the kind (the bits converted after)
            var types = new List<MFMediaType>();
            for (uint i = 0; ; i++)
            {
                IntPtr ot;
                if (((delegate* unmanaged<IntPtr, uint, uint, IntPtr*, int>)Com.Fn(p, 14))(p, 0, i, &ot) < 0) break;
                if (ComObject.From<MFMediaType>(ot) is { } m) types.Add(m); else Com.Release(ot);
            }
            try
            {
                uint rate = Native.Attributes.U32(SR.MF_MT_AUDIO_SAMPLES_PER_SECOND), ch = Native.Attributes.U32(SR.MF_MT_AUDIO_NUM_CHANNELS);
                // of the kind asked for, or (to convert from) the decoder's first
                var first = types.FirstOrDefault(x => x.Subtype == sub) ?? types.FirstOrDefault();
                if (first == null) { dec.Shutdown(); dec.Release(); return MF.MF_E_INVALIDMEDIATYPE; }
                uint bits = t.Attributes.U32(SR.MF_MT_AUDIO_BITS_PER_SAMPLE, fl ? 32u : first.Subtype == sub ? first.Attributes.U32(SR.MF_MT_AUDIO_BITS_PER_SAMPLE) : 16u);
                uint wantRate = t.Attributes.U32(SR.MF_MT_AUDIO_SAMPLES_PER_SECOND, first.Attributes.U32(SR.MF_MT_AUDIO_SAMPLES_PER_SECOND));
                uint wantCh = t.Attributes.U32(SR.MF_MT_AUDIO_NUM_CHANNELS, first.Attributes.U32(SR.MF_MT_AUDIO_NUM_CHANNELS));
                if (wantRate is < 1000 or > 384000 || wantCh is < 1 or > 8) { dec.Shutdown(); dec.Release(); return MF.MF_E_INVALIDMEDIATYPE; }
                uint decRate = first.Attributes.U32(SR.MF_MT_AUDIO_SAMPLES_PER_SECOND), decCh = first.Attributes.U32(SR.MF_MT_AUDIO_NUM_CHANNELS);
                if (fl ? bits != 32 : bits is not (8 or 16 or 24 or 32)) { dec.Shutdown(); dec.Release(); return MF.MF_E_INVALIDMEDIATYPE; }
                var exact = types.FirstOrDefault(x => x.Subtype == sub && x.Attributes.U32(SR.MF_MT_AUDIO_BITS_PER_SAMPLE) == bits);
                var use = exact ?? first;
                if (Mft.SetOutputType(p, use.Pointer) < 0) { dec.Shutdown(); dec.Release(); return MF.MF_E_INVALIDMEDIATYPE; }
                DropDecoder();
                Decoder = dec;
                bool convert = exact == null || wantRate != decRate || wantCh != decCh;
                _conv = convert ? new AudioConverter(use.Subtype == MediaFormats.Float, (int)use.Attributes.U32(SR.MF_MT_AUDIO_BITS_PER_SAMPLE, 16), (int)decCh, (int)decRate, fl, (int)bits, (int)wantCh, (int)wantRate) : null;
                _convFrames = 0; _convT0 = null; _convDrained = false;
                var cur = use.Clone();
                if (convert)
                {
                    cur.Attributes.Set(MF.MF_MT_SUBTYPE, sub);
                    uint block = wantCh * bits / 8;
                    cur.Attributes.Set(SR.MF_MT_AUDIO_BITS_PER_SAMPLE, bits);
                    cur.Attributes.Set(SR.MF_MT_AUDIO_BLOCK_ALIGNMENT, block);
                    cur.Attributes.Set(SR.MF_MT_AUDIO_AVG_BYTES_PER_SECOND, block * wantRate);
                    cur.Attributes.Set(SR.MF_MT_AUDIO_NUM_CHANNELS, wantCh);
                    cur.Attributes.Set(SR.MF_MT_AUDIO_SAMPLES_PER_SECOND, wantRate);
                    if (exact == null || fl) cur.Attributes.Delete(MF_MT_AUDIO_VALID_BITS_PER_SAMPLE);
                    // the speakers, where the count of them changes
                    if (wantCh != decCh) cur.Attributes.Set(MF_MT_AUDIO_CHANNEL_MASK, AudioConverter.Mask((int)wantCh));
                }
                Replace(cur);
                RestartAtSync();
                return MF.S_OK;
            }
            finally { foreach (var x in types) x.Release(); }
        }

        /// <summary>The decoder's PCM as the type asked for: its time the decoder's, or (resampled) counted in frames from the first.</summary>
        private MFSample? ConvertAudio(MFSample? s, byte[]? drained = null)
        {
            try
            {
                var o = drained ?? _conv!.Convert(s!.Bytes());
                if (o.Length == 0) return null;
                uint rate = Current.Attributes.U32(SR.MF_MT_AUDIO_SAMPLES_PER_SECOND, 44100), ch = Math.Max(1, Current.Attributes.U32(SR.MF_MT_AUDIO_NUM_CHANNELS, 1));
                uint block = Math.Max(1, Current.Attributes.U32(SR.MF_MT_AUDIO_BLOCK_ALIGNMENT, ch * 2));
                long frames = o.Length / block;
                var r = new MFSample();
                var buf = new MFMemoryBuffer((uint)o.Length);
                byte* d = buf.LockBytes(true);
                o.CopyTo(new Span<byte>(d, o.Length));
                buf.UnlockBytes();
                buf.CurrentLength = (uint)o.Length;
                r.SetBuffer(buf.Pointer);
                buf.Release();
                _convT0 ??= s?.Time ?? 0;
                r.Time = _conv!.Resamples ? _convT0 + _convFrames * 10_000_000 / rate : s?.Time ?? _convT0 + _convFrames * 10_000_000 / rate;
                // Windows' resampler: the duration of the frames, rounded
                r.Duration = (frames * 10_000_000 + rate / 2) / rate;
                _convFrames += frames;
                if (s != null) foreach (var (k, v) in s.Attributes.All()) r.Attributes.Set(k, MFAttributesObject.Copy(v));
                else r.Attributes.Set(MF.MFSampleExtension_CleanPoint, 1u);
                return r;
            }
            finally { s?.Release(); }
        }

        /// <summary>A decoded type as Windows' reader makes it, of the size; the picture's apertures from the decoder where it gives them.</summary>
        private MFMediaType Decoded(Guid sub, uint w, uint h, MFMediaType? fromDecoder)
        {
            var t = new MFMediaType();
            var a = t.Attributes;
            bool rgb = Array.IndexOf(s_rgb, sub) >= 0;
            a.Set(SR.MF_MT_AVG_BIT_ERROR_RATE, 0u);
            a.Set(MF.MF_MT_ALL_SAMPLES_INDEPENDENT, 1u);
            a.Set(SR.MF_MT_AVG_BITRATE, Native.Attributes.U32(SR.MF_MT_AVG_BITRATE));
            a.Set(MF.MF_MT_COMPRESSED, 0u);
            a.Set(MF.MF_MT_DEFAULT_STRIDE, (uint)Stride(sub, w));
            a.Set(MF.MF_MT_FIXED_SIZE_SAMPLES, 1u);
            if (Native.Attributes.Get(MF.MF_MT_FRAME_RATE) is { } rate) a.Set(MF.MF_MT_FRAME_RATE, MFAttributesObject.Copy(rate));
            a.Set(MF.MF_MT_FRAME_SIZE, MF.Pack(w, h));
            a.Set(MF.MF_MT_INTERLACE_MODE, rgb ? 2u : Native.Attributes.U32(MF.MF_MT_INTERLACE_MODE, 2));
            a.Set(MF.MF_MT_MAJOR_TYPE, MF.MFMediaType_Video);
            if (Native.Attributes.Get(MF.MF_MT_PIXEL_ASPECT_RATIO) is { } par) a.Set(MF.MF_MT_PIXEL_ASPECT_RATIO, MFAttributesObject.Copy(par));
            a.Set(MF.MF_MT_SAMPLE_SIZE, ImageSize(sub, w, h));
            a.Set(MF.MF_MT_SUBTYPE, sub);
            a.Set(SR.MF_MT_VIDEO_ROTATION, Native.Attributes.U32(SR.MF_MT_VIDEO_ROTATION));
            if (fromDecoder?.Attributes.Get(MF.MF_MT_MINIMUM_DISPLAY_APERTURE) is { } ap)
            {
                a.Set(MF.MF_MT_GEOMETRIC_APERTURE, MFAttributesObject.Copy(ap));
                a.Set(MF.MF_MT_MINIMUM_DISPLAY_APERTURE, MFAttributesObject.Copy(ap));
                a.Set(SR.MF_MT_PAN_SCAN_APERTURE, MFAttributesObject.Copy(ap));
                a.Set(MF.MF_MT_VIDEO_NOMINAL_RANGE, 2u);                                     // MFNominalRange_16_235
            }
            return t;
        }

        private static int Stride(Guid sub, uint w) => sub == SR.MFVideoFormat_RGB32 || sub == SR.MFVideoFormat_ARGB32 ? (int)w * 4 : sub == SR.MFVideoFormat_RGB24 ? (int)((w * 3 + 3) & ~3u) : MFVideoDecoder.Stride(sub, w);

        private static uint ImageSize(Guid sub, uint w, uint h) => Array.IndexOf(s_rgb, sub) >= 0 ? (uint)Stride(sub, w) * h : MFVideoDecoder.ImageSize(sub, w, h);

        // ---- position -----------------------------------------------------------------------------------

        public void Seek(long hns)
        {
            var s = Track.Samples;
            int at = 0;
            for (int i = 0; i < s.Count; i++)
            {
                if (Track.Pts(s[i]) > hns) break;
                if (s[i].Sync) at = i;
            }
            // a fragmented file: from its fragment's first sync sample
            if (s.Count > 0 && s[at].Fragment >= 0)
            {
                int f = s[at].Fragment;
                int first = s.FindIndex(x => x.Fragment == f && x.Sync);
                if (first >= 0) at = first;
            }
            _next = at;
            Restart();
        }

        public void Flush() => Restart();

        private void Restart()
        {
            _discontinuity = true;
            Ended = false;
            _inputDone = false;
            _drainSent = false;
            if (Decoder != null) Mft.ProcessMessage(Decoder.Pointer, 0, 0);               // MFT_MESSAGE_COMMAND_FLUSH
            _conv?.Reset(); _convFrames = 0; _convT0 = null; _convDrained = false;
        }

        private void RestartAtSync()
        {
            while (_next > 0 && !Track.Samples[Math.Min(_next, Track.Samples.Count - 1)].Sync) _next--;
            Restart();
        }

        // ---- samples ------------------------------------------------------------------------------------

        /// <summary>The next sample, or null with the end of the stream in the flags.</summary>
        public MFSample? Next(bool drain, out uint flags)
        {
            flags = 0;
            if (Ended) { flags = SR.ENDOFSTREAM; return null; }
            if (Decoder == null)
            {
                if (_next >= Track.Samples.Count || drain) { if (!drain) Ended = true; flags = SR.ENDOFSTREAM; return null; }
                return Compressed(Track.Samples[_next++]);
            }
            var p = Decoder.Pointer;
            while (true)
            {
                int hr = Mft.Output(p, out var got, out bool changed, this);
                if (changed)
                {
                    Mft.SetOutputOf(p, _decoderOut);
                    var dt = Mft.OutputType(p);
                    var (w, h) = dt?.FrameSize ?? (0, 0);
                    var cur = Current.FrameSize;
                    bool apChange = dt?.Attributes.Get(MF.MF_MT_MINIMUM_DISPLAY_APERTURE) != null;
                    if (w > 0 && ((w, h) != cur || apChange && Current.Attributes.Get(MF.MF_MT_MINIMUM_DISPLAY_APERTURE) == null))
                    {
                        Replace(Decoded(_rgb != Guid.Empty ? _rgb : _decoderOut, w, h, dt));
                        flags |= SR.CURRENTMEDIATYPECHANGED;
                    }
                    dt?.Release();
                    continue;
                }
                if (hr >= 0 && got != null)
                {
                    if (Track.Audio)
                    {
                        if (_conv == null) return got;
                        var converted = ConvertAudio(got);
                        if (converted == null) continue;                 // the resampler's look-ahead filling
                        return converted;
                    }
                    var outSample = _rgb != Guid.Empty ? ToRgb(got) : got;
                    if (_firstOut || _discontinuity) { outSample.Attributes.Set(MF.MFSampleExtension_Discontinuity, 1u); _firstOut = false; _discontinuity = false; }
                    return outSample;
                }
                if (hr != MF.MF_E_TRANSFORM_NEED_MORE_INPUT) throw new InvalidOperationException($"the decoder's ProcessOutput: 0x{hr:x8}");
                if (_inputDone || drain || _next >= Track.Samples.Count)
                {
                    if (!_drainSent) { Mft.ProcessMessage(p, 1, 0); _drainSent = true; continue; }    // MFT_MESSAGE_COMMAND_DRAIN
                    if (drain && !_inputDone && _next < Track.Samples.Count) { _drainSent = false; flags |= SR.ENDOFSTREAM; return null; }
                    // what the resampler still holds
                    if (_conv != null && !_convDrained) { _convDrained = true; if (ConvertAudio(null, _conv.Drain()) is { } tail) return tail; }
                    Ended = true;
                    flags |= SR.ENDOFSTREAM;
                    return null;
                }
                var c = Compressed(Track.Samples[_next++]);
                try
                {
                    hr = Mft.ProcessInput(p, c.Pointer);
                    if (hr == MF.MF_E_NOTACCEPTING) { _next--; continue; }
                    if (hr < 0) throw new InvalidOperationException($"the decoder's ProcessInput: 0x{hr:x8}");
                }
                finally { c.Release(); }
                if (_next >= Track.Samples.Count) _inputDone = true;
            }
        }

        /// <summary>A sample as the source hands it out.</summary>
        private MFSample Compressed(in Mp4Sample m)
        {
            var bytes = _reader.Demuxer.Read(Track, m, out var lengths);
            if (Track.Format == "twos") Swap16(bytes);
            var buf = new MFMemoryBuffer((uint)Math.Max(1, bytes.Length));
            byte* d = buf.LockBytes(true);
            bytes.CopyTo(new Span<byte>(d, bytes.Length));
            buf.UnlockBytes();
            buf.CurrentLength = (uint)bytes.Length;
            var s = new MFSample();
            s.SetBuffer(buf.Pointer);
            buf.Release();
            s.Time = Track.Pts(m);
            s.Duration = Track.Hns(m.Duration);
            if (lengths != null)
            {
                var blob = new byte[lengths.Length * 4];
                for (int i = 0; i < lengths.Length; i++) BitConverter.GetBytes(lengths[i]).CopyTo(blob, i * 4);
                s.Attributes.Set(SR.MFSampleExtension_NALULengthInfo, MFValue.Of(blob));
            }
            if (m.Sync) s.Attributes.Set(MF.MFSampleExtension_CleanPoint, 1u);
            if (_discontinuity) { s.Attributes.Set(MF.MFSampleExtension_Discontinuity, 1u); if (Decoder == null || Track.Audio) _discontinuity = false; }
            return s;
        }

        private static void Swap16(byte[] b) { for (int i = 0; i + 1 < b.Length; i += 2) (b[i], b[i + 1]) = (b[i + 1], b[i]); }

        // ---- RGB ----------------------------------------------------------------------------------------

        /// <summary>
        /// The decoder's NV12 frame as the RGB type, of the same size: BT.601 below 720 lines and BT.709 from there
        /// (or the stream's MF_MT_YUV_MATRIX), limited range, each chroma sample for its 2x2 pixels -- as Windows'
        /// video processor converts it. RGB32's fourth byte 0, ARGB32's 255. Of a texture, a texture of the device.
        /// </summary>
        private MFSample ToRgb(MFSample nv12)
        {
            try
            {
                var (w, h) = Current.FrameSize;
                var src = nv12.Bytes();
                int stride = Stride(_rgb, w);
                var rgb = new byte[stride * h];
                uint matrix = Native.Attributes.U32(SR.MF_MT_YUV_MATRIX, h >= 720 ? 1u : 2u);   // MFVideoTransferMatrix_BT709 1, BT601 2
                Nv12ToRgb(src, (int)w, (int)h, rgb, stride, _rgb == SR.MFVideoFormat_RGB24 ? 3 : 4, _rgb == SR.MFVideoFormat_ARGB32 ? (byte)255 : (byte)0, matrix == 1);
                var s = new MFSample();
                if (nv12.Buffers.Count > 0 && From<MFDxgiBuffer>(nv12.Buffers[0]) is { } tex && _rgb != SR.MFVideoFormat_RGB24)
                {
                    IntPtr b = D3DTextureIo.CreateBgraBuffer(tex, (int)w, (int)h, rgb, _rgb == SR.MFVideoFormat_ARGB32);
                    s.SetBuffer(b);
                    Com.Release(b);
                }
                else
                {
                    var buf = new MFMemoryBuffer((uint)rgb.Length);
                    byte* d = buf.LockBytes(true);
                    rgb.CopyTo(new Span<byte>(d, rgb.Length));
                    buf.UnlockBytes();
                    buf.CurrentLength = (uint)rgb.Length;
                    s.SetBuffer(buf.Pointer);
                    buf.Release();
                }
                s.Time = nv12.Time; s.Duration = nv12.Duration;
                foreach (var (k, v) in nv12.Attributes.All()) s.Attributes.Set(k, MFAttributesObject.Copy(v));
                return s;
            }
            finally { nv12.Release(); }
        }

        private static T? From<T>(IntPtr p) where T : ComObject => ComObject.From<T>(p);

        /// <summary>
        /// Windows' conversion, in 8-bit fixed point: C = Y - 16, D = U - 128, E = V - 128, each channel (298 C + k D + k' E + 128) >> 8.
        /// </summary>
        internal static void Nv12ToRgb(byte[] src, int w, int h, byte[] dst, int stride, int bpp, byte alpha, bool bt709)
        {
            int rv = bt709 ? 459 : 409, gu = bt709 ? 55 : 100, gv = bt709 ? 136 : 208, bu = bt709 ? 541 : 516;
            for (int y = 0; y < h; y++)
            {
                int crow = w * h + (y / 2) * w;
                for (int x = 0; x < w; x++)
                {
                    int c = 298 * (src[y * w + x] - 16);
                    int ci = crow + (x / 2) * 2;
                    int d = src[ci] - 128, e = src[ci + 1] - 128;
                    int o = y * stride + x * bpp;
                    dst[o] = Clamp((c + bu * d + 128) >> 8);
                    dst[o + 1] = Clamp((c - gu * d - gv * e + 128) >> 8);
                    dst[o + 2] = Clamp((c + rv * e + 128) >> 8);
                    if (bpp == 4) dst[o + 3] = alpha;
                }
            }
        }

        private static byte Clamp(int v) => v <= 0 ? (byte)0 : v >= 255 ? (byte)255 : (byte)v;
    }

    /// <summary>An IMFTransform, through its vtable.</summary>
    internal static unsafe class Mft
    {
        public static int ProcessMessage(IntPtr t, uint message, nuint param) => ((delegate* unmanaged<IntPtr, uint, nuint, int>)Com.Fn(t, 23))(t, message, param);
        public static int SetInputType(IntPtr t, IntPtr type) => ((delegate* unmanaged<IntPtr, uint, IntPtr, uint, int>)Com.Fn(t, 15))(t, 0, type, 0);
        public static int SetOutputType(IntPtr t, IntPtr type) => ((delegate* unmanaged<IntPtr, uint, IntPtr, uint, int>)Com.Fn(t, 16))(t, 0, type, 0);
        public static int ProcessInput(IntPtr t, IntPtr sample) => ((delegate* unmanaged<IntPtr, uint, IntPtr, uint, int>)Com.Fn(t, 24))(t, 0, sample, 0);

        /// <summary>The decoder's output type of the subtype, set.</summary>
        public static int SetOutputOf(IntPtr t, Guid sub)
        {
            for (uint i = 0; ; i++)
            {
                IntPtr type;
                int hr = ((delegate* unmanaged<IntPtr, uint, uint, IntPtr*, int>)Com.Fn(t, 14))(t, 0, i, &type);
                if (hr < 0) return hr;
                try
                {
                    if (ComObject.From<MFMediaType>(type)?.Subtype == sub)
                        return ((delegate* unmanaged<IntPtr, uint, IntPtr, uint, int>)Com.Fn(t, 16))(t, 0, type, 0);
                }
                finally { Com.Release(type); }
            }
        }

        public static MFMediaType? OutputType(IntPtr t)
        {
            IntPtr type;
            if (((delegate* unmanaged<IntPtr, uint, IntPtr*, int>)Com.Fn(t, 18))(t, 0, &type) < 0) return null;
            var m = ComObject.From<MFMediaType>(type);
            if (m == null) Com.Release(type);
            return m;
        }

        /// <summary>ProcessOutput: a sample (the decoder's own, or one made here of its size), or a change of format.</summary>
        public static int Output(IntPtr t, out MFSample? sample, out bool changed, ReaderStream _)
        {
            sample = null; changed = false;
            var info = stackalloc uint[3];
            int hr = ((delegate* unmanaged<IntPtr, uint, uint*, int>)Com.Fn(t, 7))(t, 0, info);
            if (hr < 0) return hr;
            bool provides = (info[0] & 0x100) != 0;
            MFSample? mine = null;
            if (!provides && info[1] > 0)
            {
                mine = new MFSample();
                var buf = new MFMemoryBuffer(info[1]);
                mine.SetBuffer(buf.Pointer);
                buf.Release();
            }
            int size = 4 * IntPtr.Size;
            var odb = stackalloc byte[size];
            new Span<byte>(odb, size).Clear();
            *(IntPtr*)(odb + IntPtr.Size) = mine?.Pointer ?? IntPtr.Zero;
            uint status;
            hr = ((delegate* unmanaged<IntPtr, uint, uint, byte*, uint*, int>)Com.Fn(t, 25))(t, 0, 1, odb, &status);
            if (hr == MF.MF_E_TRANSFORM_STREAM_CHANGE) { mine?.Release(); changed = true; return hr; }
            if (hr < 0) { mine?.Release(); return hr; }
            IntPtr got = *(IntPtr*)(odb + IntPtr.Size);
            if (provides)
            {
                sample = ComObject.From<MFSample>(got);
                if (sample == null) Com.Release(got);
            }
            else sample = mine;
            return hr;
        }
    }
}
