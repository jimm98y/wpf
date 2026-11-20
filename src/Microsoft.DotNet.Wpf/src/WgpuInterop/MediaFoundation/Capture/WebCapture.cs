// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The browser's cameras (CaptureDevices.cs), through Browser/browser-codecs.js on the page's thread: the page's video
// inputs (enumerateDevices, the page asking once for the use of one where it may not yet -- their names and
// capabilities come only then), each one's formats the usual sizes up to its largest, of its fastest rate, as NV12;
// a session of one at a format (getUserMedia of the size), each frame copied as NV12 on the page and taken by the
// managed side when it polls -- the shim's code on a worker, each JS call a round trip to the page (WebCodecs.cs):
// the frames come in as the capture source is asked for its events (Pump).
//

#if WGPU_BROWSER
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices.JavaScript;
using System.Text.Json;
using Microsoft.Wpf.Interop.WebGpu.MediaFoundation.Codecs;

namespace Microsoft.Wpf.Interop.WebGpu.MediaFoundation.Capture
{
    internal static partial class WebCaptureJs
    {
        private const string M = "wpfBrowserCodecs";
        [JSImport("cameras", M)] internal static partial string Cameras();
        [JSImport("openCamera", M)] internal static partial int OpenCamera(string id, int width, int height, double fps);
        [JSImport("cameraState", M)] internal static partial int CameraState(int handle);
        [JSImport("takeCameraFrame", M)] internal static partial int TakeCameraFrame(int handle, [JSMarshalAs<JSType.MemoryView>] Span<byte> buffer);
        [JSImport("closeCamera", M)] internal static partial void CloseCamera(int handle);
        [JSImport("microphones", M)] internal static partial string Microphones();
        [JSImport("openMicrophone", M)] internal static partial int OpenMicrophone(string id, int rate, int channels);
        [JSImport("takeMicrophoneBlock", M)] internal static partial int TakeMicrophoneBlock(int handle, [JSMarshalAs<JSType.MemoryView>] Span<byte> buffer);
    }

    internal static class WebCapture
    {
        private static readonly Guid NV12 = new("3231564e-0000-0010-8000-00aa00389b71");
        private static readonly (int W, int H)[] s_sizes = { (3840, 2160), (2560, 1440), (1920, 1080), (1280, 720), (960, 540), (640, 480), (640, 360), (320, 240) };

        /// <summary>The page's cameras, waited for (up to 10 s: the page may ask the user) the first time.</summary>
        public static List<CaptureDevice> Devices()
        {
            var list = new List<CaptureDevice>();
            string json;
            try
            {
                var sw = Stopwatch.StartNew();
                while ((json = WebCaptureJs.Cameras()).Length == 0 && sw.ElapsedMilliseconds < 10_000) WebCodecs.Pause();
            }
            catch (Exception e) when (e is JSException or InvalidOperationException) { return list; }
            if (json.Length == 0) return list;
            using var doc = JsonDocument.Parse(json);
            foreach (var d in doc.RootElement.EnumerateArray())
            {
                var dev = new CaptureDevice { Id = d.GetProperty("id").GetString() ?? "", Name = d.GetProperty("name").GetString() ?? "" };
                int w = d.GetProperty("w").GetInt32(), h = d.GetProperty("h").GetInt32();
                double min = d.GetProperty("minFps").GetDouble(), max = Math.Floor(d.GetProperty("maxFps").GetDouble() + 0.01);
                // its largest size, then the usual ones within it
                dev.Formats.Add(new CaptureFormat { Subtype = NV12, Width = w, Height = h, MinFps = Math.Min(min, max), MaxFps = max, FullRange = true });
                foreach (var (sw, sh) in s_sizes)
                    if (sw <= w && sh <= h && !(sw == w && sh == h)) dev.Formats.Add(new CaptureFormat { Subtype = NV12, Width = sw, Height = sh, MinFps = Math.Min(min, max), MaxFps = max, FullRange = true });
                list.Add(dev);
            }
            return list;
        }

        public static ICaptureSession? Open(CaptureDevice device, CaptureFormat format, CaptureFrame frames, out int hr)
        {
            hr = MF.S_OK;
            int handle;
            try { handle = WebCaptureJs.OpenCamera(device.Id, format.Width, format.Height, format.MaxFps); }
            catch (Exception e) when (e is JSException or InvalidOperationException) { hr = CaptureIds.MF_E_NO_CAPTURE_DEVICES_AVAILABLE; return null; }
            // opened (or refused) before the source is started
            var sw = Stopwatch.StartNew();
            int state;
            while ((state = WebCaptureJs.CameraState(handle)) == 0 && sw.ElapsedMilliseconds < 10_000) WebCodecs.Pause();
            if (state != 1)
            {
                WebCaptureJs.CloseCamera(handle);
                hr = state == 3 ? CaptureIds.E_ACCESSDENIED : CaptureIds.MF_E_NO_CAPTURE_DEVICES_AVAILABLE;
                return null;
            }
            return new Session(handle, format, frames);
        }

        /// <summary>The page's microphones, waited for (up to 10 s) the first time; each of the shared format (float, 48 kHz, stereo).</summary>
        public static List<AudioCaptureDevice> Microphones()
        {
            var list = new List<AudioCaptureDevice>();
            string json;
            try
            {
                var sw = Stopwatch.StartNew();
                while ((json = WebCaptureJs.Microphones()).Length == 0 && sw.ElapsedMilliseconds < 10_000) WebCodecs.Pause();
            }
            catch (Exception e) when (e is JSException or InvalidOperationException) { return list; }
            if (json.Length == 0) return list;
            using var doc = JsonDocument.Parse(json);
            foreach (var d in doc.RootElement.EnumerateArray())
                list.Add(new AudioCaptureDevice { Id = d.GetProperty("id").GetString() ?? "", Name = d.GetProperty("name").GetString() ?? "", Rate = 48000, Channels = 2 });
            return list;
        }

        public static ICaptureSession? OpenMicrophone(AudioCaptureDevice device, AudioBlock blocks, out int hr)
        {
            hr = MF.S_OK;
            int handle;
            try { handle = WebCaptureJs.OpenMicrophone(device.Id, device.Rate, device.Channels); }
            catch (Exception e) when (e is JSException or InvalidOperationException) { hr = CaptureIds.MF_E_NO_CAPTURE_DEVICES_AVAILABLE; return null; }
            var sw = Stopwatch.StartNew();
            int state;
            while ((state = WebCaptureJs.CameraState(handle)) == 0 && sw.ElapsedMilliseconds < 10_000) WebCodecs.Pause();
            if (state != 1)
            {
                WebCaptureJs.CloseCamera(handle);
                hr = state == 3 ? CaptureIds.E_ACCESSDENIED : CaptureIds.MF_E_NO_CAPTURE_DEVICES_AVAILABLE;
                return null;
            }
            return new MicSession(handle, device, blocks);
        }

        private sealed class MicSession : ICaptureSession
        {
            private readonly int _handle, _channels;
            private readonly AudioBlock _blocks;
            private readonly byte[] _buffer = new byte[48000 * 8];
            private bool _running, _closed;

            public MicSession(int handle, AudioCaptureDevice d, AudioBlock blocks) { _handle = handle; _channels = d.Channels; _blocks = blocks; }

            public void Start() => _running = true;
            public void Stop() => _running = false;

            public void Pump()
            {
                if (!_running || _closed) return;
                int n;
                while ((n = WebCaptureJs.TakeMicrophoneBlock(_handle, _buffer)) > 0)
                {
                    var f = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(_buffer.AsSpan(0, n)).ToArray();
                    _blocks(f, CaptureDevices.Now() - (long)f.Length / _channels * 10_000_000 / 48000);
                }
            }

            public void Dispose()
            {
                if (_closed) return;
                _closed = true; _running = false;
                try { WebCaptureJs.CloseCamera(_handle); }
                catch (Exception e) when (e is JSException or InvalidOperationException) { }
            }
        }

        private sealed class Session : ICaptureSession
        {
            private readonly int _handle;
            private readonly CaptureFrame _frames;
            private readonly byte[] _buffer;
            private bool _running, _closed;

            public Session(int handle, CaptureFormat f, CaptureFrame frames)
            {
                _handle = handle; _frames = frames;
                _buffer = new byte[f.Width * f.Height + (f.Width + 1) / 2 * 2 * ((f.Height + 1) / 2)];
            }

            public void Start() => _running = true;
            public void Stop() => _running = false;

            /// <summary>The frames the page has, to the callback (of the time they are taken).</summary>
            public void Pump()
            {
                if (!_running || _closed) return;
                int n;
                while ((n = WebCaptureJs.TakeCameraFrame(_handle, _buffer)) > 0)
                    _frames(_buffer.AsSpan(0, n).ToArray(), CaptureDevices.Now());
            }

            public void Dispose()
            {
                if (_closed) return;
                _closed = true;
                _running = false;
                try { WebCaptureJs.CloseCamera(_handle); }
                catch (Exception e) when (e is JSException or InvalidOperationException) { }
            }
        }
    }
}
#endif
