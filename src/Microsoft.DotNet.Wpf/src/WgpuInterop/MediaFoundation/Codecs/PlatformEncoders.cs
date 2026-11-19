// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The system's encoders, as the shim's sink writer uses them (Sink/MFSinkWriter.cs): VideoToolbox and
// AudioToolbox on Apple's systems, GStreamer on Linux, MediaCodec on Android. Video: NV12 in, Annex B access
// units out (the parameter sets before each key frame), each with its frame's time. Audio: 16-bit PCM in, raw
// AAC packets out, the AudioSpecificConfig once known.
//

using System;

namespace Microsoft.Wpf.Interop.WebGpu.MediaFoundation.Codecs
{
#pragma warning disable CA1416 // the platforms are checked
    internal sealed class PlatformVideoEncoder : IDisposable
    {
        private static bool Apple => OperatingSystem.IsMacOS() || OperatingSystem.IsIOS() || OperatingSystem.IsTvOS() || OperatingSystem.IsMacCatalyst();
        private readonly VideoToolboxEncoder? _apple;
        private readonly GStreamerVideoEncoder? _gst;
        private readonly AndroidVideoEncoder? _android;

        private PlatformVideoEncoder(VideoToolboxEncoder? a, GStreamerVideoEncoder? g, AndroidVideoEncoder? d) { _apple = a; _gst = g; _android = d; }
#if WGPU_BROWSER
        private readonly WebCodecsVideoEncoder? _web;
        private PlatformVideoEncoder(WebCodecsVideoEncoder w) { _web = w; }
#endif

        public static bool Supports(VideoCodec codec)
        {
            try
            {
#if WGPU_BROWSER
                if (OperatingSystem.IsBrowser()) return WebCodecsVideoEncoder.Supports(codec);
#endif
                if (Apple) return VideoToolboxEncoder.Supports(codec);
                if (OperatingSystem.IsAndroid()) return AndroidVideoEncoder.Supports(codec);
                if (OperatingSystem.IsLinux()) return GStreamerVideoEncoder.Supports(codec);
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or TypeInitializationException) { }
            return false;
        }

        public static PlatformVideoEncoder? Create(VideoCodec codec, VideoEncoderOptions options)
        {
            if (!Supports(codec)) return null;
            try
            {
#if WGPU_BROWSER
                if (OperatingSystem.IsBrowser()) { var w = new WebCodecsVideoEncoder(codec, options); w.Initialize(); return new PlatformVideoEncoder(w); }
#endif
                if (Apple) { var a = new VideoToolboxEncoder(codec, options); a.Initialize(); return new PlatformVideoEncoder(a, null, null); }
                if (OperatingSystem.IsAndroid()) { var d = new AndroidVideoEncoder(codec, options); d.Initialize(); return new PlatformVideoEncoder(null, null, d); }
                if (OperatingSystem.IsLinux()) { var g = new GStreamerVideoEncoder(codec, options); g.Initialize(); return new PlatformVideoEncoder(null, g, null); }
            }
            catch (Exception e) when (e is not OutOfMemoryException) { Log.Error($"no {codec} encoder", e); }
            return null;
        }

        public void Push(ReadOnlySpan<byte> nv12, long time) {
#if WGPU_BROWSER
            if (_web != null) { _web.ProcessInput(nv12, time); return; }
#endif
            if (_apple != null) _apple.ProcessInput(nv12, time); else if (_gst != null) _gst.ProcessInput(nv12, time); else _android!.ProcessInput(nv12, time); }

        public bool TryTake(ref byte[] buffer, out uint length, out long time)
        {
#if WGPU_BROWSER
            if (_web != null) return _web.ProcessOutput(ref buffer, out length, out time);
#endif
            return _apple?.ProcessOutput(ref buffer, out length, out time) ?? _gst?.ProcessOutput(ref buffer, out length, out time) ?? _android!.ProcessOutput(ref buffer, out length, out time);
        }

#if WGPU_BROWSER
        public void BeginDrain() { _web?.BeginDrain(); _apple?.BeginDrain(); _gst?.BeginDrain(); _android?.BeginDrain(); }
        public void EndDrain() { _web?.EndDrain(); _apple?.EndDrain(); _gst?.EndDrain(); _android?.EndDrain(); }
        public void Dispose() { _web?.Dispose(); _apple?.Dispose(); _gst?.Dispose(); _android?.Dispose(); }
#else
        public void BeginDrain() { _apple?.BeginDrain(); _gst?.BeginDrain(); _android?.BeginDrain(); }
        public void EndDrain() { _apple?.EndDrain(); _gst?.EndDrain(); _android?.EndDrain(); }
        public void Dispose() { _apple?.Dispose(); _gst?.Dispose(); _android?.Dispose(); }
#endif
    }

    internal sealed class PlatformAudioEncoder : IDisposable
    {
        private static bool Apple => OperatingSystem.IsMacOS() || OperatingSystem.IsIOS() || OperatingSystem.IsTvOS() || OperatingSystem.IsMacCatalyst();
        private readonly AudioToolboxEncoder? _apple;
        private readonly GStreamerAudioEncoder? _gst;
        private readonly AndroidAudioEncoder? _android;

        private PlatformAudioEncoder(AudioToolboxEncoder? a, GStreamerAudioEncoder? g, AndroidAudioEncoder? d) { _apple = a; _gst = g; _android = d; }
#if WGPU_BROWSER
        private readonly WebCodecsAudioEncoder? _web;
        private PlatformAudioEncoder(WebCodecsAudioEncoder w) { _web = w; }
#endif

        public static bool Supports(AudioCodec codec)
        {
            try
            {
#if WGPU_BROWSER
                if (OperatingSystem.IsBrowser()) return WebCodecsAudioEncoder.Supports(codec);
#endif
                if (Apple) return AudioToolboxEncoder.Supports(codec);
                if (OperatingSystem.IsAndroid()) return AndroidAudioEncoder.Supports(codec);
                if (OperatingSystem.IsLinux()) return GStreamerAudioEncoder.Supports(codec);
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or TypeInitializationException) { }
            return false;
        }

        public static PlatformAudioEncoder? Create(AudioCodec codec, AudioEncoderOptions options)
        {
            if (!Supports(codec)) return null;
            try
            {
#if WGPU_BROWSER
                if (OperatingSystem.IsBrowser()) { var w = new WebCodecsAudioEncoder(codec, options); w.Initialize(); return new PlatformAudioEncoder(w); }
#endif
                if (Apple) { var a = new AudioToolboxEncoder(codec, options); a.Initialize(); return new PlatformAudioEncoder(a, null, null); }
                if (OperatingSystem.IsAndroid()) { var d = new AndroidAudioEncoder(codec, options); d.Initialize(); return new PlatformAudioEncoder(null, null, d); }
                if (OperatingSystem.IsLinux()) { var g = new GStreamerAudioEncoder(codec, options); g.Initialize(); return new PlatformAudioEncoder(null, g, null); }
            }
            catch (Exception e) when (e is not OutOfMemoryException) { Log.Error($"no {codec} encoder", e); }
            return null;
        }

        /// <summary>AAC's AudioSpecificConfig, once the encoder has it.</summary>
#if WGPU_BROWSER
        public byte[]? Config => _web?.Config ?? _apple?.Config ?? _gst?.Config ?? _android?.Config;
#else
        public byte[]? Config => _apple?.Config ?? _gst?.Config ?? _android?.Config;
#endif

        public void Push(ReadOnlySpan<byte> pcm, long time) {
#if WGPU_BROWSER
            if (_web != null) { _web.ProcessInput(pcm, time); return; }
#endif
            if (_apple != null) _apple.ProcessInput(pcm, time); else if (_gst != null) _gst.ProcessInput(pcm, time); else _android!.ProcessInput(pcm, time); }

        public bool TryTake(ref byte[] buffer, out uint length)
        {
#if WGPU_BROWSER
            if (_web != null) return _web.ProcessOutput(ref buffer, out length);
#endif
            return _apple?.ProcessOutput(ref buffer, out length) ?? _gst?.ProcessOutput(ref buffer, out length) ?? _android!.ProcessOutput(ref buffer, out length);
        }

#if WGPU_BROWSER
        public void BeginDrain() { _web?.BeginDrain(); _apple?.BeginDrain(); _gst?.BeginDrain(); _android?.BeginDrain(); }
        public void EndDrain() { _web?.EndDrain(); _apple?.EndDrain(); _gst?.EndDrain(); _android?.EndDrain(); }
        public void Dispose() { _web?.Dispose(); _apple?.Dispose(); _gst?.Dispose(); _android?.Dispose(); }
#else
        public void BeginDrain() { _apple?.BeginDrain(); _gst?.BeginDrain(); _android?.BeginDrain(); }
        public void EndDrain() { _apple?.EndDrain(); _gst?.EndDrain(); _android?.EndDrain(); }
        public void Dispose() { _apple?.Dispose(); _gst?.Dispose(); _android?.Dispose(); }
#endif
    }
#pragma warning restore CA1416
}
