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

        /// <summary>Whether this system decodes the codec.</summary>
        public static bool Supports(AudioCodec codec)
        {
            try
            {
#pragma warning disable CA1416 // the platforms are checked
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
#pragma warning disable CA1416
                if (Apple) { var a = new AudioToolboxDecoder(codec, options); a.Initialize(); return new PlatformAudioDecoder(a, null, null); }
                if (OperatingSystem.IsAndroid()) { var d = new AndroidAudioDecoder(codec, options); d.Initialize(); return new PlatformAudioDecoder(null, null, d); }
                if (OperatingSystem.IsLinux()) { var g = new GStreamerAudioDecoder(codec, options); g.Initialize(); return new PlatformAudioDecoder(null, g, null); }
#pragma warning restore CA1416
            }
            catch (Exception e) when (e is not OutOfMemoryException) { Log.Error($"no {codec} decoder", e); }
            return null;
        }

#pragma warning disable CA1416
        public uint Channels => _apple?.Channels ?? _gst?.Channels ?? _android!.Channels;
        public uint SampleRate => _apple?.SampleRate ?? _gst?.SampleRate ?? _android!.SampleRate;
        public uint BitsPerSample => _apple?.BitsPerSample ?? _gst?.BitsPerSample ?? _android!.BitsPerSample;

        public void Push(ReadOnlySpan<byte> data, long time)
        {
            if (_apple != null) _apple.ProcessInput(data, time);
            else if (_gst != null) _gst.ProcessInput(data, time);
            else _android!.ProcessInput(data, time);
        }

        /// <summary>Decoded PCM, if there is some, into the buffer (grown as it needs).</summary>
        public bool TryTake(ref byte[] buffer, out uint length)
        {
            if (_apple != null) return _apple.ProcessOutput(ref buffer, out length);
            if (_gst != null) return _gst.ProcessOutput(ref buffer, out length);
            return _android!.ProcessOutput(ref buffer, out length);
        }

        public void BeginDrain() { _apple?.BeginDrain(); _gst?.BeginDrain(); _android?.BeginDrain(); }
        public void EndDrain() { _apple?.EndDrain(); _gst?.EndDrain(); _android?.EndDrain(); }
        public void Flush() { _apple?.Flush(); _gst?.Flush(); _android?.Flush(); }
        public void Dispose() { _apple?.Dispose(); _gst?.Dispose(); _android?.Dispose(); }
#pragma warning restore CA1416
    }
}
