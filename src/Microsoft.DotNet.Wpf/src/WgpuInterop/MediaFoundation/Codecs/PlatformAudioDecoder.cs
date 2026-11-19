// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The system's audio decoder, as the shim's Media Foundation audio decoders use it (MFAudioTransform.cs):
// packets in, interleaved PCM out -- 16 bit of AAC and MP3, the stream's bits of FLAC and ALAC, 32-bit
// float of Opus. AudioToolbox on Apple's systems, GStreamer on Linux, MediaCodec on Android.
//

using System;

namespace Microsoft.Wpf.Interop.WebGpu.MediaFoundation.Codecs
{
    internal sealed class PlatformAudioDecoder : IDisposable
    {
        private static bool Apple => OperatingSystem.IsMacOS() || OperatingSystem.IsIOS() || OperatingSystem.IsTvOS() || OperatingSystem.IsMacCatalyst();

        private readonly AudioToolboxDecoder? _apple;
        private readonly GStreamerAudioDecoder? _gst;
        private readonly AndroidAudioDecoder? _android;
        /// <summary>Apple Lossless in managed code, where the system has no decoder of it (the browser's).</summary>
        private readonly AlacDecoder? _alac;
        private PlatformAudioDecoder(AlacDecoder a) { _alac = a; }
#if WGPU_BROWSER
        private readonly WebCodecsAudioDecoder? _web;
        private PlatformAudioDecoder(WebCodecsAudioDecoder w) { _web = w; }
#endif

        /// <summary>Whether this system decodes the codec (ALAC always: in managed code where the system does not).</summary>
        public static bool Supports(AudioCodec codec) => codec == AudioCodec.Alac || SystemSupports(codec);

        private static bool SystemSupports(AudioCodec codec)
        {
            try
            {
#pragma warning disable CA1416 // the platforms are checked
#if WGPU_BROWSER
                if (OperatingSystem.IsBrowser()) return WebCodecs.Supports(codec);
#endif
                if (Apple) return AudioToolboxDecoder.Supports(codec);
                if (OperatingSystem.IsAndroid()) return AndroidAudioDecoder.Supports(codec);
                if (OperatingSystem.IsLinux()) return GStreamerAudioDecoder.Supports(codec);
#pragma warning restore CA1416
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or TypeInitializationException) { }
            return false;
        }

        private PlatformAudioDecoder(AudioToolboxDecoder? a, GStreamerAudioDecoder? g, AndroidAudioDecoder? d) { _apple = a; _gst = g; _android = d; }

        public static PlatformAudioDecoder? Create(AudioCodec codec, AudioDecoderOptions options)
        {
            if (!Supports(codec)) return null;
            try
            {
                if (codec == AudioCodec.Alac && !SystemSupports(codec)) return options.Config is { } c ? new PlatformAudioDecoder(new AlacDecoder(c)) : null;
#pragma warning disable CA1416
#if WGPU_BROWSER
                if (OperatingSystem.IsBrowser()) { var w = new WebCodecsAudioDecoder(codec, options); w.Initialize(); return new PlatformAudioDecoder(w); }
#endif
                if (Apple) { var a = new AudioToolboxDecoder(codec, options); a.Initialize(); return new PlatformAudioDecoder(a, null, null); }
                if (OperatingSystem.IsAndroid()) { var d = new AndroidAudioDecoder(codec, options); d.Initialize(); return new PlatformAudioDecoder(null, null, d); }
                if (OperatingSystem.IsLinux()) { var g = new GStreamerAudioDecoder(codec, options); g.Initialize(); return new PlatformAudioDecoder(null, g, null); }
#pragma warning restore CA1416
            }
            catch (Exception e) when (e is not OutOfMemoryException) { Log.Error($"no {codec} decoder", e); }
            return null;
        }

        /// <summary>The frames of Opus's start the decoder leaves out itself (AudioToolbox 120, as measured), of the pre-skip to leave out.</summary>
        public static uint OpusDropsItself => OperatingSystem.IsMacOS() || OperatingSystem.IsIOS() || OperatingSystem.IsTvOS() || OperatingSystem.IsMacCatalyst() ? 120u : 0u;

        /// <summary>Whether the decoder leaves MP3's delay (529 samples) out itself, as AudioToolbox does; GStreamer's and MediaCodec's keep it.</summary>
        public bool DropsMp3Delay => _apple != null;

#pragma warning disable CA1416
#if WGPU_BROWSER
        public uint Channels => _alac?.Channels ?? _web?.Channels ?? _apple?.Channels ?? _gst?.Channels ?? _android!.Channels;
        public uint SampleRate => _alac?.SampleRate ?? _web?.SampleRate ?? _apple?.SampleRate ?? _gst?.SampleRate ?? _android!.SampleRate;
        public uint BitsPerSample => _alac?.BitsPerSample ?? _web?.BitsPerSample ?? _apple?.BitsPerSample ?? _gst?.BitsPerSample ?? _android!.BitsPerSample;
#else
        public uint Channels => _alac?.Channels ?? _apple?.Channels ?? _gst?.Channels ?? _android!.Channels;
        public uint SampleRate => _alac?.SampleRate ?? _apple?.SampleRate ?? _gst?.SampleRate ?? _android!.SampleRate;
        public uint BitsPerSample => _alac?.BitsPerSample ?? _apple?.BitsPerSample ?? _gst?.BitsPerSample ?? _android!.BitsPerSample;
#endif

        public void Push(ReadOnlySpan<byte> data, long time)
        {
            if (_alac != null) { _alac.Push(data); return; }
#if WGPU_BROWSER
            if (_web != null) { _web.ProcessInput(data, time); return; }
#endif
            if (_apple != null) _apple.ProcessInput(data, time);
            else if (_gst != null) _gst.ProcessInput(data, time);
            else _android!.ProcessInput(data, time);
        }

        /// <summary>Decoded PCM, if there is some, into the buffer (grown as it needs).</summary>
        public bool TryTake(ref byte[] buffer, out uint length)
        {
            if (_alac != null) return _alac.TryTake(ref buffer, out length);
#if WGPU_BROWSER
            if (_web != null) return _web.ProcessOutput(ref buffer, out length);
#endif
            if (_apple != null) return _apple.ProcessOutput(ref buffer, out length);
            if (_gst != null) return _gst.ProcessOutput(ref buffer, out length);
            return _android!.ProcessOutput(ref buffer, out length);
        }

#if WGPU_BROWSER
        public void BeginDrain() { _web?.BeginDrain(); _apple?.BeginDrain(); _gst?.BeginDrain(); _android?.BeginDrain(); }
        public void EndDrain() { _web?.EndDrain(); _apple?.EndDrain(); _gst?.EndDrain(); _android?.EndDrain(); }
        public void Flush() { _alac?.Flush(); _web?.Flush(); _apple?.Flush(); _gst?.Flush(); _android?.Flush(); }
        public void Dispose() { _web?.Dispose(); _apple?.Dispose(); _gst?.Dispose(); _android?.Dispose(); }
#else
        public void BeginDrain() { _apple?.BeginDrain(); _gst?.BeginDrain(); _android?.BeginDrain(); }
        public void EndDrain() { _apple?.EndDrain(); _gst?.EndDrain(); _android?.EndDrain(); }
        public void Flush() { _alac?.Flush(); _apple?.Flush(); _gst?.Flush(); _android?.Flush(); }
        public void Dispose() { _apple?.Dispose(); _gst?.Dispose(); _android?.Dispose(); }
#endif
#pragma warning restore CA1416
    }
}
