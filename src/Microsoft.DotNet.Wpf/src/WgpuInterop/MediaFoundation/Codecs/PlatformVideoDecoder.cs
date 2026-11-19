// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The system's video decoder, as the shim's Media Foundation decoders use it: compressed samples in
// (NAL units in Annex B, OBUs, pictures), each with its time; decoded frames out in the order they are
// shown, each NV12 of its own size. One per system: VideoToolbox on Apple's (the GPU's decoder where
// it has one); the others come with their heads.
//

using System;
using System.Runtime.InteropServices;
using static Microsoft.Wpf.Interop.WebGpu.MediaFoundation.Codecs.AppleNative;

namespace Microsoft.Wpf.Interop.WebGpu.MediaFoundation.Codecs
{
    internal abstract unsafe class PlatformVideoDecoder : IDisposable
    {
        /// <summary>Whether this system decodes the codec.</summary>
        public static bool Supports(VideoCodec codec)
        {
            try
            {
                if (OperatingSystem.IsMacOS() || OperatingSystem.IsIOS() || OperatingSystem.IsTvOS() || OperatingSystem.IsMacCatalyst())
                    return VideoToolboxDecoder.Supports(codec);
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { }
            return false;
        }

        /// <summary>A decoder of the codec, or null where this system has none.</summary>
        public static PlatformVideoDecoder? Create(VideoCodec codec, bool lowLatency)
        {
            if (!Supports(codec)) return null;
            if (OperatingSystem.IsMacOS() || OperatingSystem.IsIOS() || OperatingSystem.IsTvOS() || OperatingSystem.IsMacCatalyst())
                return new AppleVideoDecoder(codec, lowLatency);
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
}
