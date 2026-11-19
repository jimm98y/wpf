// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The system's cameras, under MFEnumDeviceSources' video capture devices (MFCaptureSource.cs): each one's name, its
// identifier (MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_SYMBOLIC_LINK's) and its formats -- subtype, size, frame rates --
// in its own order; opened at a format, its frames handed to a callback as they come (of the system's time), until stopped.
// AVFoundation's on macOS and iOS (AvCapture.cs), Video4Linux2's on Linux (V4l2Capture.cs), Camera2's on Android
// (AndroidCapture.cs), getUserMedia's in the browser (WebCapture.cs).
//

using System;
using System.Collections.Generic;

namespace Microsoft.Wpf.Interop.WebGpu.MediaFoundation.Capture
{
    internal sealed class CaptureFormat
    {
        public Guid Subtype;                  // NV12, YUY2, UYVY, MJPG
        public int Width, Height;
        public double MinFps, MaxFps;
        public bool FullRange;
    }

    internal sealed class CaptureDevice
    {
        public string Name = "", Id = "";
        public readonly List<CaptureFormat> Formats = new();
    }

    /// <summary>A frame: its bytes in the format's layout (NV12's luma then chroma, tightly), of the system's time in 100 ns.</summary>
    internal delegate void CaptureFrame(byte[] bytes, long time);

    internal interface ICaptureSession : IDisposable
    {
        void Start();
        void Stop();
        /// <summary>The frames come in (of a session whose frames are taken, not given: the browser's), on the thread that asks.</summary>
        void Pump() { }
    }

    internal static class CaptureDevices
    {
        private static bool Apple => OperatingSystem.IsMacOS() || OperatingSystem.IsIOS() || OperatingSystem.IsMacCatalyst();

        /// <summary>The cameras, as the system lists them.</summary>
        public static List<CaptureDevice> Video()
        {
            try
            {
#if WGPU_BROWSER
                if (OperatingSystem.IsBrowser()) return WebCapture.Devices();
#endif
                if (Apple) return AvCapture.Devices();
                if (OperatingSystem.IsAndroid()) return AndroidCapture.Devices();
                if (OperatingSystem.IsLinux()) return V4l2Capture.Devices();
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or TypeInitializationException) { }
            return new List<CaptureDevice>();
        }

        /// <summary>Whether the application may use the cameras (where the system asks, it asks when one is opened).</summary>
        public static bool Allowed
        {
            get
            {
                try { return !Apple || AvCapture.Allowed; }
                catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or TypeInitializationException) { return false; }
            }
        }

        /// <summary>The system's clock the frames' times are of (the host clock: mach_absolute_time, CLOCK_MONOTONIC), in 100 ns.</summary>
        public static long Now() => (long)((Int128)System.Diagnostics.Stopwatch.GetTimestamp() * 10_000_000 / System.Diagnostics.Stopwatch.Frequency);

        /// <summary>A frame rate as Media Foundation's ratio: whole of 1, else of 1001 (29.97: 30000:1001), else of 1000.</summary>
        public static (int Num, int Den) Rate(double fps)
        {
            if (Math.Abs(fps - Math.Round(fps)) < 0.001) return ((int)Math.Round(fps), 1);
            double n = fps * 1001;
            if (Math.Abs(n - Math.Round(n)) < 0.01) return ((int)Math.Round(n), 1001);
            return ((int)Math.Round(fps * 1000), 1000);
        }

        /// <summary>A session of the camera at the format, its frames to the callback; null where it cannot be opened (the HRESULT why).</summary>
        public static ICaptureSession? Open(CaptureDevice device, CaptureFormat format, CaptureFrame frames, out int hr)
        {
            hr = MF.S_OK;
            ICaptureSession? s = null;
            try
            {
#if WGPU_BROWSER
                if (OperatingSystem.IsBrowser()) return WebCapture.Open(device, format, frames, out hr);
#endif
                if (Apple) s = AvCapture.Open(device, format, frames);
                else if (OperatingSystem.IsAndroid()) return AndroidCapture.Open(device, format, frames, out hr);
                else if (OperatingSystem.IsLinux()) s = V4l2Capture.Open(device, format, frames);
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or TypeInitializationException) { }
            if (s == null && hr >= 0) hr = CaptureIds.MF_E_NO_CAPTURE_DEVICES_AVAILABLE;
            return s;
        }
    }
}
