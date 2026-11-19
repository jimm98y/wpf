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
        public static Guid Of(VideoCodec codec) => codec switch
        {
            VideoCodec.H262 => MPEG2, VideoCodec.H263 => H263, VideoCodec.Mpeg4 => MP4V, VideoCodec.H264 => H264, VideoCodec.H265 => HEVC,
            VideoCodec.VP9 => VP90, VideoCodec.AV1 => AV1, _ => ProRes,
        };
    }
}
