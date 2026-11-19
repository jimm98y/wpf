// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The system's video decoder, as the shim's Media Foundation decoders use it: compressed samples in
// (NAL units in Annex B, OBUs, pictures), each with its time; decoded frames out in the order they are
// shown, each NV12 of its own size. One per system: VideoToolbox on Apple's (the GPU's decoder where
// it has one), GStreamer on Linux, MediaCodec on Android, WebCodecs in the browser (WebCodecs.cs).
//

using System;
using System.Runtime.InteropServices;
using static Microsoft.Wpf.Interop.WebGpu.MediaFoundation.Codecs.AppleNative;

namespace Microsoft.Wpf.Interop.WebGpu.MediaFoundation.Codecs
{
    internal abstract unsafe class PlatformVideoDecoder : IDisposable
    {
        private static bool Apple => OperatingSystem.IsMacOS() || OperatingSystem.IsIOS() || OperatingSystem.IsTvOS() || OperatingSystem.IsMacCatalyst();

        /// <summary>Whether this system decodes the codec.</summary>
        public static bool Supports(VideoCodec codec)
        {
            try
            {
#if WGPU_BROWSER
                if (OperatingSystem.IsBrowser()) return WebCodecs.Supports(codec);
#endif
                if (Apple) return VideoToolboxDecoder.Supports(codec);
                if (OperatingSystem.IsAndroid()) return AndroidVideoDecoder.Supports(codec);
                if (OperatingSystem.IsLinux()) return GStreamerVideoDecoder.Supports(codec);
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or TypeInitializationException) { }
            return false;
        }

        /// <summary>
        /// A decoder of the codec, or null where this system has none. The frame's size is the input type's
        /// (0 where it says none): what a decoder sized as it is made (MediaCodec's image reader) is made of.
        /// </summary>
        public static PlatformVideoDecoder? Create(VideoCodec codec, bool lowLatency, uint width = 0, uint height = 0)
        {
            if (!Supports(codec)) return null;
#if WGPU_BROWSER
            if (OperatingSystem.IsBrowser()) return new WebCodecsVideoDecoder(codec, lowLatency, width, height);
#endif
            if (Apple) return new AppleVideoDecoder(codec, lowLatency);
            if (OperatingSystem.IsAndroid()) return new AndroidDecoder(codec, lowLatency, width, height);
            if (OperatingSystem.IsLinux()) return new GStreamerDecoder(codec, lowLatency);
            return null;
        }

        public abstract void Push(ReadOnlySpan<byte> data, long time);
        /// <summary>The next frame, in display order (the caller disposes it).</summary>
        public abstract bool TryTake(out DecodedFrame? frame);
        public abstract void BeginDrain();
        public abstract void EndDrain();
        public abstract void Flush();
        public abstract void Dispose();
    }

    /// <summary>A decoded picture, NV12, of its own size.</summary>
    internal abstract unsafe class DecodedFrame : IDisposable
    {
        public int Width, Height;
        public long Time;
        /// <summary>Copies it into the top left of planes of <paramref name="width"/> by <paramref name="height"/> (cut where larger).</summary>
        public abstract void CopyTo(byte* y, int yPitch, byte* uv, int uvPitch, int width, int height);
        public abstract void Dispose();
    }

    internal sealed unsafe class AppleVideoDecoder : PlatformVideoDecoder
    {
        private readonly VideoToolboxDecoder _d;

        public AppleVideoDecoder(VideoCodec codec, bool lowLatency)
        {
            _d = new VideoToolboxDecoder(codec, new VideoDecoderOptions { Width = 2, Height = 2, LowLatency = lowLatency });
            _d.Initialize();
        }

        public override void Push(ReadOnlySpan<byte> data, long time) => _d.ProcessInput(data, time);

        public override bool TryTake(out DecodedFrame? frame)
        {
            frame = null;
            if (!_d.TryTakeFrame(out IntPtr pb, out long time)) return false;
            frame = new ApplePixelFrame(pb, time);
            return true;
        }

        public override void BeginDrain() => _d.BeginDrain();
        public override void EndDrain() => _d.EndDrain();
        public override void Flush() => _d.Flush();
        public override void Dispose() => _d.Dispose();

        private sealed class ApplePixelFrame : DecodedFrame
        {
            private IntPtr _pb;

            public ApplePixelFrame(IntPtr pb, long time)
            {
                _pb = pb;
                Time = time;
                Width = (int)CVPixelBufferGetWidth(pb);
                Height = (int)CVPixelBufferGetHeight(pb);
            }

            public override void CopyTo(byte* y, int yPitch, byte* uv, int uvPitch, int width, int height)
            {
                if (CVPixelBufferLockBaseAddress(_pb, kCVPixelBufferLock_ReadOnly) != 0) return;
                try
                {
                    for (nuint plane = 0; plane < 2; plane++)
                    {
                        byte* src = (byte*)CVPixelBufferGetBaseAddressOfPlane(_pb, plane);
                        int stride = (int)CVPixelBufferGetBytesPerRowOfPlane(_pb, plane);
                        int rowBytes = Math.Min((int)CVPixelBufferGetWidthOfPlane(_pb, plane) * (plane == 0 ? 1 : 2), width);
                        int rows = Math.Min((int)CVPixelBufferGetHeightOfPlane(_pb, plane), plane == 0 ? height : height / 2);
                        byte* dst = plane == 0 ? y : uv;
                        int pitch = plane == 0 ? yPitch : uvPitch;
                        for (int r = 0; r < rows; r++) Buffer.MemoryCopy(src + r * stride, dst + r * pitch, pitch, rowBytes);
                    }
                }
                finally { CVPixelBufferUnlockBaseAddress(_pb, kCVPixelBufferLock_ReadOnly); }
            }

            public override void Dispose()
            {
                if (_pb != IntPtr.Zero) CFRelease(_pb);
                _pb = IntPtr.Zero;
            }
        }
    }

    /// <summary>GStreamer's (Linux): an appsrc, decodebin -- the best decoder installed, VA-API's where there is one -- and NV12 out.</summary>
    internal sealed unsafe class GStreamerDecoder : PlatformVideoDecoder
    {
        private readonly GStreamerVideoDecoder _d;

        public GStreamerDecoder(VideoCodec codec, bool lowLatency)
        {
            _d = new GStreamerVideoDecoder(codec, new VideoDecoderOptions { Width = 2, Height = 2, LowLatency = lowLatency });
            _d.Initialize();
        }

        public override void Push(ReadOnlySpan<byte> data, long time) => _d.ProcessInput(data, time);

        public override bool TryTake(out DecodedFrame? frame)
        {
            frame = null;
            if (!_d.TryTakeSample(out IntPtr sample)) return false;
            frame = new GstFrame(sample);
            return true;
        }

        public override void BeginDrain() => _d.BeginDrain();
        public override void EndDrain() => _d.EndDrain();
        public override void Flush() => _d.Flush();
        public override void Dispose() => _d.Dispose();

        private sealed class GstFrame : DecodedFrame
        {
            private IntPtr _sample;

            public GstFrame(IntPtr sample)
            {
                _sample = sample;
                (Width, Height) = GstFrames.Size(sample);
                Time = GstFrames.Time(sample);
            }

            public override void CopyTo(byte* y, int yPitch, byte* uv, int uvPitch, int width, int height)
            {
                uint fw = (uint)(Width + 1) & ~1u, fh = (uint)(Height + 1) & ~1u;
                var tmp = new byte[fw * fh * 3 / 2];
                if (!GstFrames.CopyNV12(_sample, tmp, fw, fh, out _, out _)) return;
                int rows = Math.Min((int)fh, height), cols = Math.Min((int)fw, width);
                fixed (byte* t = tmp)
                {
                    for (int r = 0; r < rows; r++) Buffer.MemoryCopy(t + r * fw, y + r * yPitch, yPitch, cols);
                    for (int r = 0; r < rows / 2; r++) Buffer.MemoryCopy(t + fw * fh + r * fw, uv + r * uvPitch, uvPitch, cols);
                }
            }

            public override void Dispose()
            {
                if (_sample != IntPtr.Zero) Gst.gst_mini_object_unref(_sample);
                _sample = IntPtr.Zero;
            }
        }
    }

    /// <summary>
    /// MediaCodec's (Android): the NDK's, frames through an image reader of the input type's size -- or, where the type
    /// names none, made at the stream's first SPS of its coded size (H.264), 1920x1088 of other codecs. Each frame is its
    /// image's crop rectangle, the picture.
    /// </summary>
    internal sealed unsafe class AndroidDecoder : PlatformVideoDecoder
    {
        private readonly VideoCodec _codec;
        private readonly bool _lowLatency;
        private readonly uint _width, _height;
        private AndroidVideoDecoder? _d;
        private byte[] _buffer = Array.Empty<byte>();

        public AndroidDecoder(VideoCodec codec, bool lowLatency, uint width, uint height)
        {
            _codec = codec; _lowLatency = lowLatency; _width = width; _height = height;
            if (width > 0 && height > 0) Make(width, height);
        }

        private void Make(uint w, uint h)
        {
            _d = new AndroidVideoDecoder(_codec, new VideoDecoderOptions { Width = w, Height = h, LowLatency = _lowLatency });
            _d.Initialize();
            _buffer = new byte[_d.OutputSize];
        }

        public override void Push(ReadOnlySpan<byte> data, long time)
        {
            if (_d == null)
            {
                if (_codec == VideoCodec.H264) { if (H264Sps.CodedSize(data) is not { } s) return; Make((uint)s.W, (uint)s.H); }
                else Make(1920, 1088);
            }
            _d!.ProcessInput(data, time);
        }

        public override bool TryTake(out DecodedFrame? frame)
        {
            frame = null;
            if (_d == null || !_d.ProcessOutput(ref _buffer, out _, out long time)) return false;
            int w = (int)_d.Width, h = (int)_d.Height, pw = Math.Min(_d.PictureWidth, w), ph = Math.Min(_d.PictureHeight, h);
            if (pw <= 0 || ph <= 0 || (pw == w && ph == h)) { frame = new BytesFrame(_buffer.AsSpan(0, (int)_d.OutputSize).ToArray(), w, h, time); return true; }
            // the picture alone: its rows of luma, then of chroma, each of its width
            var pic = new byte[pw * ph + pw * ((ph + 1) / 2)];
            for (int r = 0; r < ph; r++) _buffer.AsSpan(r * w, pw).CopyTo(pic.AsSpan(r * pw));
            for (int r = 0; r < (ph + 1) / 2; r++) _buffer.AsSpan(w * h + r * w, pw).CopyTo(pic.AsSpan(pw * ph + r * pw));
            frame = new BytesFrame(pic, pw, ph, time);
            return true;
        }

        public override void BeginDrain() => _d?.BeginDrain();
        public override void EndDrain() => _d?.EndDrain();
        public override void Flush() => _d?.Flush();
        public override void Dispose() => _d?.Dispose();
    }

    /// <summary>A frame held as contiguous NV12 of its own size.</summary>
    internal sealed unsafe class BytesFrame : DecodedFrame
    {
        private readonly byte[] _nv12;

        public BytesFrame(byte[] nv12, int width, int height, long time) { _nv12 = nv12; Width = width; Height = height; Time = time; }

        public override void CopyTo(byte* y, int yPitch, byte* uv, int uvPitch, int width, int height)
        {
            int rows = Math.Min(Height, height), cols = Math.Min(Width, width);
            fixed (byte* s = _nv12)
            {
                for (int r = 0; r < rows; r++) Buffer.MemoryCopy(s + r * Width, y + r * yPitch, yPitch, cols);
                for (int r = 0; r < rows / 2; r++) Buffer.MemoryCopy(s + Width * Height + r * Width, uv + r * uvPitch, uvPitch, cols);
            }
        }

        public override void Dispose() { }
    }
}
