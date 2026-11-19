// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The cameras of Android, through the NDK's Camera2 (CaptureDevices.cs): ACameraManager's cameras, each named of the
// way it faces, its formats the sizes it streams YUV_420_888 at (ACAMERA_SCALER_AVAILABLE_STREAM_CONFIGURATIONS) of
// their fastest rate (_MIN_FRAME_DURATIONS, within the auto-exposure's ranges), as NV12, largest first; a session of
// one at a format: an AImageReader's window the target of a repeating preview request at the rate, each image's
// planes copied as tight NV12 on the camera's thread. Opening one needs the application's CAMERA permission
// (ACAMERA_ERROR_PERMISSION_DENIED: E_ACCESSDENIED).
//

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Wpf.Interop.WebGpu.Direct3D;

namespace Microsoft.Wpf.Interop.WebGpu.MediaFoundation.Capture
{
    internal static unsafe class AndroidCapture
    {
        private const string Cam = "libcamera2ndk.so", Media = "libmediandk.so";

        [StructLayout(LayoutKind.Sequential)] private struct IdList { public int Count; public byte** Ids; }
        [StructLayout(LayoutKind.Sequential)] private struct ConstEntry { public uint Tag; public byte Type; public uint Count; public void* Data; }
        [StructLayout(LayoutKind.Sequential)] private struct DeviceCallbacks { public IntPtr Context; public IntPtr OnDisconnected, OnError; }
        [StructLayout(LayoutKind.Sequential)] private struct SessionCallbacks { public IntPtr Context; public IntPtr OnClosed, OnReady, OnActive; }
        [StructLayout(LayoutKind.Sequential)] private struct ImageListener { public IntPtr Context; public IntPtr OnImageAvailable; }

        [DllImport(Cam)] private static extern IntPtr ACameraManager_create();
        [DllImport(Cam)] private static extern void ACameraManager_delete(IntPtr m);
        [DllImport(Cam)] private static extern int ACameraManager_getCameraIdList(IntPtr m, IdList** list);
        [DllImport(Cam)] private static extern void ACameraManager_deleteCameraIdList(IdList* list);
        [DllImport(Cam)] private static extern int ACameraManager_getCameraCharacteristics(IntPtr m, byte* id, IntPtr* meta);
        [DllImport(Cam)] private static extern int ACameraMetadata_getConstEntry(IntPtr meta, uint tag, ConstEntry* entry);
        [DllImport(Cam)] private static extern void ACameraMetadata_free(IntPtr meta);
        [DllImport(Cam)] private static extern int ACameraManager_openCamera(IntPtr m, byte* id, DeviceCallbacks* cb, IntPtr* device);
        [DllImport(Cam)] private static extern int ACameraDevice_close(IntPtr device);
        [DllImport(Cam)] private static extern int ACameraDevice_createCaptureRequest(IntPtr device, int template, IntPtr* request);
        [DllImport(Cam)] private static extern void ACaptureRequest_free(IntPtr request);
        [DllImport(Cam)] private static extern int ACaptureRequest_addTarget(IntPtr request, IntPtr target);
        [DllImport(Cam)] private static extern int ACaptureRequest_setEntry_i32(IntPtr request, uint tag, uint count, int* data);
        [DllImport(Cam)] private static extern int ACameraOutputTarget_create(IntPtr window, IntPtr* target);
        [DllImport(Cam)] private static extern void ACameraOutputTarget_free(IntPtr target);
        [DllImport(Cam)] private static extern int ACaptureSessionOutputContainer_create(IntPtr* container);
        [DllImport(Cam)] private static extern void ACaptureSessionOutputContainer_free(IntPtr container);
        [DllImport(Cam)] private static extern int ACaptureSessionOutput_create(IntPtr window, IntPtr* output);
        [DllImport(Cam)] private static extern void ACaptureSessionOutput_free(IntPtr output);
        [DllImport(Cam)] private static extern int ACaptureSessionOutputContainer_add(IntPtr container, IntPtr output);
        [DllImport(Cam)] private static extern int ACameraDevice_createCaptureSession(IntPtr device, IntPtr container, SessionCallbacks* cb, IntPtr* session);
        [DllImport(Cam)] private static extern int ACameraCaptureSession_setRepeatingRequest(IntPtr session, IntPtr callbacks, int n, IntPtr* requests, int* sequenceId);
        [DllImport(Cam)] private static extern int ACameraCaptureSession_stopRepeating(IntPtr session);
        [DllImport(Cam)] private static extern void ACameraCaptureSession_close(IntPtr session);
        [DllImport(Media)] private static extern int AImageReader_new(int width, int height, int format, int maxImages, IntPtr* reader);
        [DllImport(Media)] private static extern void AImageReader_delete(IntPtr reader);
        [DllImport(Media)] private static extern int AImageReader_getWindow(IntPtr reader, IntPtr* window);
        [DllImport(Media)] private static extern int AImageReader_setImageListener(IntPtr reader, ImageListener* listener);
        [DllImport(Media)] private static extern int AImageReader_acquireLatestImage(IntPtr reader, IntPtr* image);
        [DllImport(Media)] private static extern void AImage_delete(IntPtr image);
        [DllImport(Media)] private static extern int AImage_getPlaneData(IntPtr image, int plane, byte** data, int* length);
        [DllImport(Media)] private static extern int AImage_getPlaneRowStride(IntPtr image, int plane, int* stride);
        [DllImport(Media)] private static extern int AImage_getPlanePixelStride(IntPtr image, int plane, int* stride);

        private const uint ACAMERA_LENS_FACING = 0x80005, ACAMERA_SCALER_AVAILABLE_STREAM_CONFIGURATIONS = 0xD000A, ACAMERA_SCALER_AVAILABLE_MIN_FRAME_DURATIONS = 0xD000B;
        private const uint ACAMERA_CONTROL_AE_TARGET_FPS_RANGE = 0x10005, ACAMERA_CONTROL_AE_AVAILABLE_TARGET_FPS_RANGES = 0x10014;
        private const int AIMAGE_FORMAT_YUV_420_888 = 0x23, TEMPLATE_PREVIEW = 1, ACAMERA_ERROR_PERMISSION_DENIED = -10013;
        private static readonly Guid NV12 = new("3231564e-0000-0010-8000-00aa00389b71");

        private static IntPtr s_manager;
        private static readonly object s_lock = new();

        private static IntPtr Manager() { lock (s_lock) return s_manager != IntPtr.Zero ? s_manager : s_manager = ACameraManager_create(); }

        /// <summary>The cameras in the manager's order: "Back Camera", "Front Camera", "External Camera" (of a number where two face the same way).</summary>
        public static List<CaptureDevice> Devices()
        {
            var list = new List<CaptureDevice>();
            IntPtr m = Manager();
            if (m == IntPtr.Zero) return list;
            IdList* ids;
            if (ACameraManager_getCameraIdList(m, &ids) != 0) return list;
            try
            {
                var facings = new List<int>();
                for (int i = 0; i < ids->Count; i++)
                {
                    byte* id = ids->Ids[i];
                    IntPtr meta;
                    if (ACameraManager_getCameraCharacteristics(m, id, &meta) != 0) continue;
                    try
                    {
                        ConstEntry e;
                        int facing = ACameraMetadata_getConstEntry(meta, ACAMERA_LENS_FACING, &e) == 0 && e.Count > 0 ? *(byte*)e.Data : 2;
                        var dev = new CaptureDevice { Id = Marshal.PtrToStringUTF8((IntPtr)id) ?? "" };
                        Formats(meta, dev);
                        if (dev.Formats.Count == 0) continue;
                        string what = facing switch { 0 => "Front Camera", 1 => "Back Camera", _ => "External Camera" };
                        int same = facings.FindAll(f => f == facing).Count;
                        dev.Name = same == 0 ? what : $"{what} {same + 1}";
                        facings.Add(facing);
                        list.Add(dev);
                    }
                    finally { ACameraMetadata_free(meta); }
                }
            }
            finally { ACameraManager_deleteCameraIdList(ids); }
            return list;
        }

        private static void Formats(IntPtr meta, CaptureDevice dev)
        {
            ConstEntry e;
            double aeMin = 15, aeMax = 30;
            if (ACameraMetadata_getConstEntry(meta, ACAMERA_CONTROL_AE_AVAILABLE_TARGET_FPS_RANGES, &e) == 0 && e.Count >= 2)
            {
                aeMin = double.MaxValue; aeMax = 0;
                for (uint i = 0; i + 1 < e.Count; i += 2) { aeMin = Math.Min(aeMin, ((int*)e.Data)[i]); aeMax = Math.Max(aeMax, ((int*)e.Data)[i + 1]); }
            }
            var durations = new Dictionary<(int, int), long>();
            if (ACameraMetadata_getConstEntry(meta, ACAMERA_SCALER_AVAILABLE_MIN_FRAME_DURATIONS, &e) == 0)
                for (uint i = 0; i + 3 < e.Count; i += 4)
                {
                    long* d = (long*)e.Data;
                    if (d[i] == AIMAGE_FORMAT_YUV_420_888) durations[((int)d[i + 1], (int)d[i + 2])] = d[i + 3];
                }
            if (ACameraMetadata_getConstEntry(meta, ACAMERA_SCALER_AVAILABLE_STREAM_CONFIGURATIONS, &e) != 0) return;
            var sizes = new List<(int W, int H)>();
            for (uint i = 0; i + 3 < e.Count; i += 4)
            {
                int* c = (int*)e.Data;
                if (c[i] == AIMAGE_FORMAT_YUV_420_888 && c[i + 3] == 0 && !sizes.Contains((c[i + 1], c[i + 2]))) sizes.Add((c[i + 1], c[i + 2]));
            }
            sizes.Sort((a, b) => (b.W * b.H).CompareTo(a.W * a.H));
            foreach (var (w, h) in sizes)
            {
                double max = aeMax;
                if (durations.TryGetValue((w, h), out long ns) && ns > 0) max = Math.Min(max, Math.Floor(1e9 / ns + 0.01));
                dev.Formats.Add(new CaptureFormat { Subtype = NV12, Width = w, Height = h, MinFps = Math.Min(aeMin, max), MaxFps = max, FullRange = true });
            }
        }

        public static ICaptureSession? Open(CaptureDevice device, CaptureFormat format, CaptureFrame frames, out int hr)
        {
            hr = MF.S_OK;
            var s = new Session(format, frames);
            hr = s.Open(device.Id);
            if (hr < 0) { s.Dispose(); return null; }
            return s;
        }

        // ---- the callbacks ------------------------------------------------------------------------------------

        private static readonly Dictionary<IntPtr, Session> s_sessions = new();
        private static long s_next;

        [UnmanagedCallersOnly] private static void OnDisconnected(IntPtr context, IntPtr device) { }
        [UnmanagedCallersOnly] private static void OnError(IntPtr context, IntPtr device, int error) => D3DLog.Once("camera2-error:" + error, $"Media Foundation: the camera's error {error}");
        [UnmanagedCallersOnly] private static void OnSessionState(IntPtr context, IntPtr session) { }

        [UnmanagedCallersOnly]
        private static void OnImageAvailable(IntPtr context, IntPtr reader)
        {
            Session? s;
            lock (s_sessions) s_sessions.TryGetValue(context, out s);
            try { s?.Image(reader); }
            catch (Exception e) { D3DLog.Error("AImageReader listener", e); }
        }

        private sealed class Session : ICaptureSession
        {
            private readonly CaptureFormat _format;
            private readonly CaptureFrame _frames;
            private readonly IntPtr _key = (IntPtr)Interlocked.Increment(ref s_next);
            private IntPtr _device, _reader, _window, _output, _container, _target, _request, _session;
            private readonly DeviceCallbacks* _deviceCb = (DeviceCallbacks*)NativeMemory.AllocZeroed((nuint)sizeof(DeviceCallbacks));
            private readonly SessionCallbacks* _sessionCb = (SessionCallbacks*)NativeMemory.AllocZeroed((nuint)sizeof(SessionCallbacks));
            private readonly ImageListener* _listener = (ImageListener*)NativeMemory.AllocZeroed((nuint)sizeof(ImageListener));
            private volatile bool _running;
            private int _disposed;

            public Session(CaptureFormat f, CaptureFrame frames) { _format = f; _frames = frames; lock (s_sessions) s_sessions[_key] = this; }

            public int Open(string id)
            {
                _deviceCb->Context = _key;
                _deviceCb->OnDisconnected = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, void>)&OnDisconnected;
                _deviceCb->OnError = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, int, void>)&OnError;
                byte[] idz = System.Text.Encoding.UTF8.GetBytes(id + "\0");
                int r;
                IntPtr dev;
                fixed (byte* p = idz) r = ACameraManager_openCamera(Manager(), p, _deviceCb, &dev);
                if (r == ACAMERA_ERROR_PERMISSION_DENIED) return CaptureIds.E_ACCESSDENIED;
                if (r != 0) { D3DLog.Once("camera2-open:" + r, $"Media Foundation: the camera is not opened ({r})"); return CaptureIds.MF_E_NO_CAPTURE_DEVICES_AVAILABLE; }
                _device = dev;
                IntPtr rd, win, outp, cont, tgt, req;
                if (AImageReader_new(_format.Width, _format.Height, AIMAGE_FORMAT_YUV_420_888, 4, &rd) != 0) return MF.E_FAIL;
                _reader = rd;
                _listener->Context = _key;
                _listener->OnImageAvailable = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, void>)&OnImageAvailable;
                AImageReader_setImageListener(_reader, _listener);
                AImageReader_getWindow(_reader, &win); _window = win;
                ACaptureSessionOutputContainer_create(&cont); _container = cont;
                ACaptureSessionOutput_create(_window, &outp); _output = outp;
                ACaptureSessionOutputContainer_add(_container, _output);
                ACameraOutputTarget_create(_window, &tgt); _target = tgt;
                if (ACameraDevice_createCaptureRequest(_device, TEMPLATE_PREVIEW, &req) != 0) return MF.E_FAIL;
                _request = req;
                ACaptureRequest_addTarget(_request, _target);
                var (n, d) = CaptureDevices.Rate(_format.MaxFps);
                int fps = Math.Max(1, n / d);
                int* range = stackalloc int[2] { fps, fps };
                ACaptureRequest_setEntry_i32(_request, ACAMERA_CONTROL_AE_TARGET_FPS_RANGE, 2, range);
                return MF.S_OK;
            }

            public void Start()
            {
                if (_running || _disposed != 0 || _device == IntPtr.Zero) return;
                _sessionCb->Context = _key;
                _sessionCb->OnClosed = _sessionCb->OnReady = _sessionCb->OnActive = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, void>)&OnSessionState;
                IntPtr ses;
                if (ACameraDevice_createCaptureSession(_device, _container, _sessionCb, &ses) != 0) return;
                _session = ses;
                IntPtr req = _request;
                _running = true;
                ACameraCaptureSession_setRepeatingRequest(_session, IntPtr.Zero, 1, &req, null);
            }

            public void Stop()
            {
                if (!_running) return;
                _running = false;
                ACameraCaptureSession_stopRepeating(_session);
                ACameraCaptureSession_close(_session);
                _session = IntPtr.Zero;
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
                Stop();
                if (_device != IntPtr.Zero) ACameraDevice_close(_device);
                if (_request != IntPtr.Zero) ACaptureRequest_free(_request);
                if (_target != IntPtr.Zero) ACameraOutputTarget_free(_target);
                if (_output != IntPtr.Zero) ACaptureSessionOutput_free(_output);
                if (_container != IntPtr.Zero) ACaptureSessionOutputContainer_free(_container);
                lock (s_sessions) s_sessions.Remove(_key);
                if (_reader != IntPtr.Zero) AImageReader_delete(_reader);
                NativeMemory.Free(_deviceCb); NativeMemory.Free(_sessionCb); NativeMemory.Free(_listener);
            }

            /// <summary>The latest image as tight NV12, of the time it came.</summary>
            internal void Image(IntPtr reader)
            {
                IntPtr img;
                if (AImageReader_acquireLatestImage(reader, &img) != 0) return;
                long time = CaptureDevices.Now();
                byte[] o;
                try
                {
                    int w = _format.Width, h = _format.Height, cw = (w + 1) / 2, ch = (h + 1) / 2;
                    o = new byte[w * h + cw * 2 * ch];
                    byte* y, u, v; int ly, lu, lv, ys, us, vs, up, vp;
                    AImage_getPlaneData(img, 0, &y, &ly); AImage_getPlaneRowStride(img, 0, &ys);
                    AImage_getPlaneData(img, 1, &u, &lu); AImage_getPlaneRowStride(img, 1, &us); AImage_getPlanePixelStride(img, 1, &up);
                    AImage_getPlaneData(img, 2, &v, &lv); AImage_getPlaneRowStride(img, 2, &vs); AImage_getPlanePixelStride(img, 2, &vp);
                    for (int r = 0; r < h; r++) if ((long)r * ys + w <= ly) new ReadOnlySpan<byte>(y + r * ys, w).CopyTo(o.AsSpan(r * w));
                    for (int r = 0; r < ch; r++)
                        for (int x = 0; x < cw; x++)
                        {
                            int iu = r * us + x * up, iv = r * vs + x * vp, at = w * h + r * cw * 2 + x * 2;
                            if (iu < lu) o[at] = u[iu];
                            if (iv < lv) o[at + 1] = v[iv];
                        }
                }
                finally { AImage_delete(img); }
                if (_running) _frames(o, time);
            }
        }
    }
}
