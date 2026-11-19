// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The cameras of macOS and iOS, through AVFoundation (CaptureDevices.cs): AVCaptureDevice's video devices, each one's
// formats (AVCaptureDeviceFormat: its CMFormatDescription's size, its frame-rate ranges), of the bi-planar 4:2:0
// formats its formats come in, as NV12 of the full range (as Windows' frame server gives a camera's: the output asked
// for '420f', AVFoundation converting a format of the video range); a session of one (AVCaptureSession, AVCaptureDeviceInput,
// AVCaptureVideoDataOutput) at a format, each frame's CVPixelBuffer copied tightly as NV12, of the time of its
// presentation (the host clock's), on the session's own queue. The delegate is a class made at run time, its one
// method the callback below.
//

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Wpf.Interop.WebGpu.Direct3D;
using Microsoft.Wpf.Interop.WebGpu.MediaFoundation.Codecs;

namespace Microsoft.Wpf.Interop.WebGpu.MediaFoundation.Capture
{
    internal static unsafe class AvCapture
    {
        private const string ObjC = "/usr/lib/libobjc.A.dylib";
        private const string AVFoundationLib = "/System/Library/Frameworks/AVFoundation.framework/AVFoundation";
        private const string CoreMediaLib = "/System/Library/Frameworks/CoreMedia.framework/CoreMedia";
        private const string DispatchLib = "/usr/lib/system/libdispatch.dylib";

        private static readonly IntPtr s_av = NativeLibrary.Load(AVFoundationLib);
        private static readonly IntPtr AVMediaTypeVideo = *(IntPtr*)NativeLibrary.GetExport(s_av, "AVMediaTypeVideo");
        private static readonly uint F420v = AppleNative.FourCC("420v"), F420f = AppleNative.FourCC("420f");
        private static readonly Guid NV12 = new("3231564e-0000-0010-8000-00aa00389b71");

        [DllImport(ObjC)] private static extern IntPtr objc_getClass(string name);
        [DllImport(ObjC)] private static extern IntPtr sel_registerName(string name);
        [DllImport(ObjC)] private static extern IntPtr objc_allocateClassPair(IntPtr superclass, string name, nuint extraBytes);
        [DllImport(ObjC)] private static extern void objc_registerClassPair(IntPtr cls);
        [DllImport(ObjC)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool class_addMethod(IntPtr cls, IntPtr sel, IntPtr imp, string types);
        [DllImport(ObjC)] private static extern IntPtr objc_autoreleasePoolPush();
        [DllImport(ObjC)] private static extern void objc_autoreleasePoolPop(IntPtr pool);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr r, IntPtr s);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr r, IntPtr s, IntPtr a);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr r, IntPtr s, IntPtr a, IntPtr b);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendN(IntPtr r, IntPtr s, nuint a);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nuint SendCount(IntPtr r, IntPtr s);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nint SendStatus(IntPtr r, IntPtr s, IntPtr a);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern double SendDouble(IntPtr r, IntPtr s);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] [return: MarshalAs(UnmanagedType.I1)] private static extern bool SendBool(IntPtr r, IntPtr s, IntPtr a);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendTime(IntPtr r, IntPtr s, CMTime t);
        [DllImport(CoreMediaLib)] private static extern uint CMFormatDescriptionGetMediaSubType(IntPtr desc);
        [DllImport(DispatchLib)] private static extern IntPtr dispatch_queue_create(string label, IntPtr attr);
        [DllImport(DispatchLib)] private static extern void dispatch_release(IntPtr obj);

        private static IntPtr Sel(string n) => sel_registerName(n);
        private static IntPtr Cls(string n) => objc_getClass(n);

        private static string? Str(IntPtr ns) => ns == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(Send(ns, Sel("UTF8String")));

        private static IntPtr NSString(string s)
        {
            fixed (byte* p = System.Text.Encoding.UTF8.GetBytes(s + "\0"))
                return Send(Cls("NSString"), Sel("stringWithUTF8String:"), (IntPtr)p);
        }

        private static IEnumerable<IntPtr> Items(IntPtr array)
        {
            nuint n = array == IntPtr.Zero ? 0 : SendCount(array, Sel("count"));
            for (nuint i = 0; i < n; i++) yield return SendN(array, Sel("objectAtIndex:"), i);
        }

        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendArray(IntPtr r, IntPtr s, IntPtr* objects, nuint count);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendDiscovery(IntPtr r, IntPtr s, IntPtr types, IntPtr mediaType, nint position);

        /// <summary>
        /// The video devices (NSArray): of a discovery session of the built-in, external and Continuity cameras -- of
        /// the types the system has (devicesWithMediaType:, deprecated, lists none on the newer systems).
        /// </summary>
        private static IntPtr VideoDevices()
        {
            var types = new List<IntPtr>();
            foreach (var n in new[] { "AVCaptureDeviceTypeBuiltInWideAngleCamera", "AVCaptureDeviceTypeExternal", "AVCaptureDeviceTypeExternalUnknown", "AVCaptureDeviceTypeContinuityCamera" })
                if (NativeLibrary.TryGetExport(s_av, n, out var p) && *(IntPtr*)p is var t && t != IntPtr.Zero && !types.Contains(t)) types.Add(t);
            IntPtr cls = Cls("AVCaptureDeviceDiscoverySession");
            if (cls == IntPtr.Zero || types.Count == 0) return Send(Cls("AVCaptureDevice"), Sel("devicesWithMediaType:"), AVMediaTypeVideo);
            IntPtr array;
            var arr = types.ToArray();
            fixed (IntPtr* a = arr) array = SendArray(Cls("NSArray"), Sel("arrayWithObjects:count:"), a, (nuint)arr.Length);
            IntPtr session = SendDiscovery(cls, Sel("discoverySessionWithDeviceTypes:mediaType:position:"), array, AVMediaTypeVideo, 0);      // AVCaptureDevicePositionUnspecified
            return session == IntPtr.Zero ? IntPtr.Zero : Send(session, Sel("devices"));
        }

        /// <summary>The cameras: each one's name, identifier and formats (a size and its frame rates once, in the device's order).</summary>
        public static List<CaptureDevice> Devices()
        {
            var list = new List<CaptureDevice>();
            IntPtr pool = objc_autoreleasePoolPush();
            try
            {
                IntPtr devices = VideoDevices();
                foreach (var d in Items(devices))
                {
                    var dev = new CaptureDevice { Name = Str(Send(d, Sel("localizedName"))) ?? "", Id = Str(Send(d, Sel("uniqueID"))) ?? "" };
                    foreach (var f in Items(Send(d, Sel("formats"))))
                    {
                        IntPtr desc = Send(f, Sel("formatDescription"));
                        uint sub = CMFormatDescriptionGetMediaSubType(desc);
                                if (sub != F420v && sub != F420f) continue;
                        var dim = AppleNative.CMVideoFormatDescriptionGetDimensions(desc);
                        double min = double.MaxValue, max = 0;
                        foreach (var r in Items(Send(f, Sel("videoSupportedFrameRateRanges"))))
                        {
                            min = Math.Min(min, SendDouble(r, Sel("minFrameRate")));
                            max = Math.Max(max, SendDouble(r, Sel("maxFrameRate")));
                        }
                        if (max <= 0) continue;
                        // a size and rate once (of the video range and the full both)
                        if (dev.Formats.Exists(x => x.Width == dim.Width && x.Height == dim.Height && x.MaxFps == max)) continue;
                        dev.Formats.Add(new CaptureFormat { Subtype = NV12, Width = dim.Width, Height = dim.Height, MinFps = min, MaxFps = max, FullRange = true });
                    }
                    if (dev.Formats.Count > 0) list.Add(dev);
                }
            }
            finally { objc_autoreleasePoolPop(pool); }
            return list;
        }

        /// <summary>Whether the application may use the camera (AVAuthorizationStatus: not determined 0 -- the system asks when it is opened --, restricted 1, denied 2, authorized 3).</summary>
        public static bool Allowed => SendStatus(Cls("AVCaptureDevice"), Sel("authorizationStatusForMediaType:"), AVMediaTypeVideo) is 0 or 3;

        public static ICaptureSession? Open(CaptureDevice device, CaptureFormat format, CaptureFrame frames)
        {
            IntPtr pool = objc_autoreleasePoolPush();
            try
            {
                IntPtr dev = Send(Cls("AVCaptureDevice"), Sel("deviceWithUniqueID:"), NSString(device.Id));
                if (dev == IntPtr.Zero) return null;
                // the device's format of the size and rate: of the full range where it has one
                IntPtr chosen = IntPtr.Zero;
                foreach (var f in Items(Send(dev, Sel("formats"))))
                {
                    IntPtr desc = Send(f, Sel("formatDescription"));
                    var dim = AppleNative.CMVideoFormatDescriptionGetDimensions(desc);
                    uint sub = CMFormatDescriptionGetMediaSubType(desc);
                    if (dim.Width != format.Width || dim.Height != format.Height || sub != F420v && sub != F420f) continue;
                    double max = 0;
                    foreach (var r in Items(Send(f, Sel("videoSupportedFrameRateRanges")))) max = Math.Max(max, SendDouble(r, Sel("maxFrameRate")));
                    if (max != format.MaxFps) continue;
                    if (chosen == IntPtr.Zero || sub == F420f) chosen = f;
                    if (sub == F420f) break;
                }
                if (chosen == IntPtr.Zero) return null;
                IntPtr error;
                IntPtr input = Send(Cls("AVCaptureDeviceInput"), Sel("deviceInputWithDevice:error:"), dev, (IntPtr)(&error));
                if (input == IntPtr.Zero) { D3DLog.Once("avcapture-input", "Media Foundation: the camera is not opened (AVCaptureDeviceInput: " + Str(Send(error, Sel("localizedDescription"))) + ")"); return null; }
                return new AvSession(dev, chosen, input, F420f, format, frames);
            }
            finally { objc_autoreleasePoolPop(pool); }
        }

        // ---- the delegate -------------------------------------------------------------------------------------

        private static readonly Dictionary<IntPtr, AvSession> s_sessions = new();
        private static IntPtr s_delegateClass;

        internal static IntPtr NewDelegate(AvSession session)
        {
            lock (s_sessions)
            {
                if (s_delegateClass == IntPtr.Zero)
                {
                    IntPtr cls = objc_getClass("WpfWebGpuCaptureDelegate");
                    if (cls == IntPtr.Zero)
                    {
                        cls = objc_allocateClassPair(Cls("NSObject"), "WpfWebGpuCaptureDelegate", 0);
                        class_addMethod(cls, Sel("captureOutput:didOutputSampleBuffer:fromConnection:"), (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, void>)&DidOutput, "v@:@@@");
                        objc_registerClassPair(cls);
                    }
                    s_delegateClass = cls;
                }
                IntPtr d = Send(Send(s_delegateClass, Sel("alloc")), Sel("init"));
                s_sessions[d] = session;
                return d;
            }
        }

        internal static void DropDelegate(IntPtr d) { lock (s_sessions) s_sessions.Remove(d); }

        [UnmanagedCallersOnly]
        private static void DidOutput(IntPtr self, IntPtr cmd, IntPtr output, IntPtr sampleBuffer, IntPtr connection)
        {
            AvSession? s;
            lock (s_sessions) s_sessions.TryGetValue(self, out s);
            if (s == null) return;
            try { s.Frame(sampleBuffer); }
            catch (Exception e) { D3DLog.Error("AVCaptureVideoDataOutput delegate", e); }
        }

        // ---- a session ----------------------------------------------------------------------------------------

        internal sealed class AvSession : ICaptureSession
        {
            private readonly IntPtr _device, _session, _output, _delegate, _queue;      // retained
            private readonly CaptureFormat _format;
            private readonly CaptureFrame _frames;
            private int _running, _disposed;

            public AvSession(IntPtr device, IntPtr format, IntPtr input, uint pixelFormat, CaptureFormat f, CaptureFrame frames)
            {
                _format = f; _frames = frames;
                _device = Send(device, Sel("retain"));
                _session = Send(Send(Cls("AVCaptureSession"), Sel("alloc")), Sel("init"));
                Send(_session, Sel("beginConfiguration"));
                if (SendBool(_session, Sel("canAddInput:"), input)) Send(_session, Sel("addInput:"), input);
                _output = Send(Send(Cls("AVCaptureVideoDataOutput"), Sel("alloc")), Sel("init"));
                IntPtr settings = AppleNative.CreateDictionary();
                IntPtr pf = AppleNative.CreateNumber((int)pixelFormat);
                AppleNative.SetValue(settings, AppleNative.kCVPixelBufferPixelFormatTypeKey, pf);
                AppleNative.CFRelease(pf);
                Send(_output, Sel("setVideoSettings:"), settings);
                AppleNative.CFRelease(settings);
                Send(_output, Sel("setAlwaysDiscardsLateVideoFrames:"), (IntPtr)1);
                _delegate = NewDelegate(this);
                _queue = dispatch_queue_create("WpfWebGpu.Capture", IntPtr.Zero);
                Send(_output, Sel("setSampleBufferDelegate:queue:"), _delegate, _queue);
                if (SendBool(_session, Sel("canAddOutput:"), _output)) Send(_session, Sel("addOutput:"), _output);
                // the format, at its highest rate
                IntPtr error;
                if (SendBool(_device, Sel("lockForConfiguration:"), (IntPtr)(&error)))
                {
                    Send(_device, Sel("setActiveFormat:"), format);
                    var (n, d) = CaptureDevices.Rate(f.MaxFps);
                    var frame = new CMTime { Value = d, Timescale = n, Flags = 1 };
                    SendTime(_device, Sel("setActiveVideoMinFrameDuration:"), frame);
                    SendTime(_device, Sel("setActiveVideoMaxFrameDuration:"), frame);
                    Send(_device, Sel("unlockForConfiguration"));
                }
                Send(_session, Sel("commitConfiguration"));
            }

            public void Start()
            {
                if (Interlocked.Exchange(ref _running, 1) == 1 || _disposed != 0) return;
                IntPtr pool = objc_autoreleasePoolPush();
                Send(_session, Sel("startRunning"));
                objc_autoreleasePoolPop(pool);
            }

            public void Stop()
            {
                if (Interlocked.Exchange(ref _running, 0) == 0) return;
                IntPtr pool = objc_autoreleasePoolPush();
                Send(_session, Sel("stopRunning"));
                objc_autoreleasePoolPop(pool);
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
                Stop();
                IntPtr pool = objc_autoreleasePoolPush();
                Send(_output, Sel("setSampleBufferDelegate:queue:"), IntPtr.Zero, IntPtr.Zero);
                DropDelegate(_delegate);
                Send(_delegate, Sel("release"));
                Send(_output, Sel("release"));
                Send(_session, Sel("release"));
                Send(_device, Sel("release"));
                dispatch_release(_queue);
                objc_autoreleasePoolPop(pool);
            }

            /// <summary>A frame of the output: its planes copied as tight NV12, of its presentation time.</summary>
            internal void Frame(IntPtr sampleBuffer)
            {
                if (_running == 0) return;
                IntPtr px = AppleNative.CMSampleBufferGetImageBuffer(sampleBuffer);
                if (px == IntPtr.Zero) return;
                var pts = AppleNative.CMSampleBufferGetPresentationTimeStamp(sampleBuffer);
                long time = pts.Timescale > 0 ? (long)((Int128)pts.Value * 10_000_000 / pts.Timescale) : 0;
                AppleNative.CVPixelBufferLockBaseAddress(px, 1);           // kCVPixelBufferLock_ReadOnly
                byte[] bytes;
                try
                {
                    int w = (int)AppleNative.CVPixelBufferGetWidth(px), h = (int)AppleNative.CVPixelBufferGetHeight(px);
                    w = Math.Min(w, _format.Width); h = Math.Min(h, _format.Height);
                    int cw = (_format.Width + 1) / 2 * 2, ch = (_format.Height + 1) / 2;
                    bytes = new byte[_format.Width * _format.Height + cw * ch];
                    byte* y = (byte*)AppleNative.CVPixelBufferGetBaseAddressOfPlane(px, 0);
                    int ys = (int)AppleNative.CVPixelBufferGetBytesPerRowOfPlane(px, 0);
                    for (int r = 0; r < h; r++) new ReadOnlySpan<byte>(y + r * ys, w).CopyTo(bytes.AsSpan(r * _format.Width));
                    byte* uv = (byte*)AppleNative.CVPixelBufferGetBaseAddressOfPlane(px, 1);
                    int uvs = (int)AppleNative.CVPixelBufferGetBytesPerRowOfPlane(px, 1);
                    int at = _format.Width * _format.Height, rows = Math.Min(ch, (int)AppleNative.CVPixelBufferGetHeightOfPlane(px, 1));
                    int span = Math.Min(cw, (int)AppleNative.CVPixelBufferGetWidthOfPlane(px, 1) * 2);
                    for (int r = 0; r < rows; r++) new ReadOnlySpan<byte>(uv + r * uvs, span).CopyTo(bytes.AsSpan(at + r * cw));
                }
                finally { AppleNative.CVPixelBufferUnlockBaseAddress(px, 1); }
                _frames(bytes, time);
            }
        }
    }
}
