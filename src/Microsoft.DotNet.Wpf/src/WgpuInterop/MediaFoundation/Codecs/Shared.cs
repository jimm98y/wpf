// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// What the codecs ported from SharpMediaFoundationInterop (MIT, by the same author) take of that library:
// its codec names, its decoder options and its log (here the shim's).
//

#nullable disable
using System;
using Microsoft.Wpf.Interop.WebGpu.Direct3D;

namespace Microsoft.Wpf.Interop.WebGpu.MediaFoundation.Codecs
{
    internal enum VideoCodec { H262, H263, Mpeg4, H264, H265, VP9, AV1, ProRes }

    internal sealed class VideoDecoderOptions
    {
        public uint Width, Height;
        public uint FpsNom, FpsDenom;
        public bool LowLatency;
    }

    /// <summary>How an encoder spends its bits.</summary>
    internal enum RateControlMode
    {
        /// <summary>Whatever the encoder does when told nothing.</summary>
        Default,

        /// <summary>The <see cref="VideoEncoderOptions.Bitrate"/>, held to every second of the stream.</summary>
        ConstantBitrate,

        /// <summary>The <see cref="VideoEncoderOptions.Bitrate"/> on average, more where the picture needs it.</summary>
        VariableBitrate,

        /// <summary>A quality, <see cref="VideoEncoderOptions.Quality"/>, of 0 to 100, whatever it costs.</summary>
        Quality,

        /// <summary>
        /// One quantiser, <see cref="VideoEncoderOptions.Qp"/>, for every picture, of every type: the key frames then cost
        /// no more for their quality than the pictures between them.
        /// </summary>
        ConstantQp
    }

    /// <summary>What an encoder of <see cref="MediaCodecs.CreateVideoEncoder"/> is made for, and how it encodes.</summary>
    internal sealed class VideoEncoderOptions
    {
        /// <summary>The picture's width.</summary>
        public uint Width { get; set; }

        /// <summary>H.264's profile_idc (66 Baseline, 77 Main, 100 High), as MF_MT_MPEG2_PROFILE gives it; 0 the encoder's own.</summary>
        public uint H264Profile { get; set; }

        /// <summary>The picture's height.</summary>
        public uint Height { get; set; }

        /// <summary>The frame rate.</summary>
        public uint FpsNom { get; set; }
        public uint FpsDenom { get; set; } = 1;

        /// <summary>Bits a second, of the rate control modes that keep to one.</summary>
        public uint Bitrate { get; set; } = 8000000;

        public RateControlMode RateControl { get; set; }

        /// <summary>0 to 100, of <see cref="RateControlMode.Quality"/>.</summary>
        public uint Quality { get; set; }

        /// <summary>The quantiser, of <see cref="RateControlMode.ConstantQp"/>.</summary>
        public uint Qp { get; set; }

        /// <summary>Pictures from one key frame to the next; 0 leaves it to the encoder.</summary>
        public uint KeyFrameInterval { get; set; }

        /// <summary>
        /// Of <see cref="VideoCodec.ProRes"/>, the profile: what sets its quality and data rate, as ProRes has no rate control
        /// of its own. ProRes 422 HQ unless set.
        /// </summary>
        public ProResProfile ProResProfile { get; set; } = ProResProfile.HQ;

        /// <summary>
        /// How many threads the encoder may use; 0 leaves it to the encoder. Each keeps working buffers of its own, so
        /// this trades speed against memory.
        /// </summary>
        public uint Threads { get; set; }
    }

    /// <summary>What an encoder of <see cref="MediaCodecs.CreateAudioEncoder"/> is made for.</summary>
    internal sealed class AudioEncoderOptions
    {
        public uint Channels { get; set; } = 2;

        /// <summary>
        /// Of AAC, 44100 or 48000 alone: what Windows' AAC encoder takes; of MP3, 32000, 44100, 48000 or MPEG-2's lower rates.
        /// </summary>
        public uint SampleRate { get; set; } = 48000;

        /// <summary>The bits of the PCM in: 16, or of FLAC and ALAC 24 as well. Opus takes 32 bit float.</summary>
        public uint BitsPerSample { get; set; } = 16;

        /// <summary>Of MP3, the bit rate in bits a second: the nearest the encoder offers not above it.</summary>
        public uint Bitrate { get; set; } = 192000;
    }

    /// <summary>The ProRes an encoder makes: each of its own fixed quality, and of its own data rate, lowest first.</summary>
    internal enum ProResProfile
    {
        /// <summary>ProRes 422 Proxy, 'apco': for offline editing.</summary>
        Proxy,

        /// <summary>ProRes 422 LT, 'apcs'.</summary>
        LT,

        /// <summary>ProRes 422, 'apcn'.</summary>
        Standard,

        /// <summary>ProRes 422 HQ, 'apch': what an iPhone records.</summary>
        HQ,

        /// <summary>ProRes 4444, 'ap4h': of 4:4:4 chroma - of NV12 in, its chroma made full.</summary>
        P4444,

        /// <summary>ProRes 4444 XQ, 'ap4x': 4444 of a higher data rate still. Not every encoder has it.</summary>
        P4444XQ
    }

    internal static class Log
    {
        public static bool WarnEnabled => true;
        public static bool ErrorEnabled => true;
        public static bool TraceEnabled => false;
        public static bool DebugEnabled => false;
        public static void Warn(string message, Exception ex = null) => D3DLog.Once("mf:" + message, "Media Foundation: " + message + (ex != null ? ": " + ex.Message : ""));
        public static void Error(string message, Exception ex = null) => D3DLog.Once("mf:" + message, "Media Foundation: " + message + (ex != null ? ": " + ex.Message : ""));
        public static void Trace(string message, Exception ex = null) { }
        public static void Debug(string message, Exception ex = null) { }
    }

    internal static class MediaFormats
    {
        public static Guid FourCC(string s) => new Guid((uint)(s[0] | s[1] << 8 | s[2] << 16 | s[3] << 24), 0x0000, 0x0010, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71);
        public static readonly Guid NV12 = FourCC("NV12");
        public static readonly Guid H264 = FourCC("H264"), HEVC = FourCC("HEVC"), H263 = FourCC("H263"), MP4V = FourCC("MP4V"), VP90 = FourCC("VP90"), AV1 = FourCC("AV01");
        public static readonly Guid MPEG2 = new("e06d8026-db46-11cf-b4d1-00805f6cbbea");
        public static readonly Guid ProRes = FourCC("apch");
        public static Guid Wave(ushort tag) => new Guid(tag, 0x0000, 0x0010, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71);
        public static readonly Guid PCM = Wave(1), Float = Wave(3), AAC = Wave(0x1610), MP3 = Wave(0x55), Opus = Wave(0x704F), FLAC = Wave(0xF1AC);
        /// <summary>Windows' ALAC subtype: of no WAVE format tag.</summary>
        public static readonly Guid ALAC = new("616c6163-767a-494d-b478-f29d25dc9037");
        public static Guid Of(AudioCodec codec) => codec switch
        {
            AudioCodec.AAC => AAC, AudioCodec.Mp3 => MP3, AudioCodec.Opus => Opus, AudioCodec.Flac => FLAC, _ => ALAC,
        };
        public static Guid Of(ProResProfile profile) => FourCC(ProResFourCC(profile));

        /// <summary>The FOURCC a ProRes profile is told by: of its sample entries, and of VideoToolbox's codec types.</summary>
        public static string ProResFourCC(ProResProfile profile) => profile switch
        {
            ProResProfile.Proxy => "apco", ProResProfile.LT => "apcs", ProResProfile.Standard => "apcn", ProResProfile.HQ => "apch",
            ProResProfile.P4444 => "ap4h", ProResProfile.P4444XQ => "ap4x",
            _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, "unknown ProRes profile"),
        };

        public static Guid Of(VideoCodec codec) => codec switch
        {
            VideoCodec.H262 => MPEG2, VideoCodec.H263 => H263, VideoCodec.Mpeg4 => MP4V, VideoCodec.H264 => H264, VideoCodec.H265 => HEVC,
            VideoCodec.VP9 => VP90, VideoCodec.AV1 => AV1, _ => ProRes,
        };
    }
}
