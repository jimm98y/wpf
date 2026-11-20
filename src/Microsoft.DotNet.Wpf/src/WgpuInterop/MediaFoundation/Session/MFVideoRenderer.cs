// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The enhanced video renderer (MFCreateVideoRendererActivate; Documentation/media-foundation-shim.md, "The media
// session"), as Windows' behaves (MFMediaSessionProbe's "renderer of MFMediaType_Video"): its activate of the window
// (MF_ACTIVATE_VIDEO_WINDOW), a media sink of one stream that takes the uncompressed video the mixer takes (the decoder's
// NV12 straight in), its node of the renderer's CLSID and, with the source node, of its device manager (the session's
// D3D awareness); each frame shown at its presentation time in the window's client area (Composition/VideoSurfaces.cs:
// a texture the window system draws over the window), letterboxed of the picture's shape on the border's color. Its
// display control (MR_VIDEO_RENDER_SERVICE's IMFVideoDisplayControl): the native size and aspect, the window, the
// video's place, the aspect mode (PreservePicture | PreservePixel), the border, the current image.
//

using System;
using System.Runtime.InteropServices;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using Microsoft.Wpf.Interop.WebGpu.Direct3D;
using Microsoft.Wpf.Interop.WebGpu.MediaFoundation.Sink;

namespace Microsoft.Wpf.Interop.WebGpu.MediaFoundation.Session
{
    internal sealed unsafe class MFVideoRenderer : ComObject, IStreamOwner, IRendererClsid
    {
        public static readonly Guid RendererClsid = new("d23e6477-b104-4707-81cb-e1ca19a07016");
        public static readonly Guid MF_ACTIVATE_VIDEO_WINDOW = new("9a2dbbdd-f57e-4162-82b9-6831377682d3");
        private static readonly Guid IID_IMFGetService = new("fa993888-4383-415a-a930-dd472a8cf6f7"), IID_IUnknown = new("00000000-0000-0000-c000-000000000046");
        private static readonly Guid IID_IMFVideoDisplayControl = new("a490b1e4-ab84-4d31-a1b2-181e03b1077a"), MR_VIDEO_RENDER_SERVICE = new("1092a86c-ab1a-459a-a336-831fbc4d11ff");
        private static readonly Guid MR_VIDEO_ACCELERATION_SERVICE = new("efef5175-5c7d-4ce2-bbbd-34ff8bca6554");
        private static readonly Vtable s_vt = Build(), s_clockVt = BuildClock(), s_serviceVt = BuildService(), s_displayVt = BuildDisplay();

        internal readonly MFGrabberStream Stream;
        private MFPresentationClock? _clock;
        private IntPtr _foreignClock, _manager;            // an application's clock; the device manager (AddRef'd)
        private IntPtr _hwnd;
        private bool _shutdown, _placed, _fullscreen;
        private float _srcL, _srcT, _srcR = 1, _srcB = 1;
        private int _dstL, _dstT, _dstR, _dstB;
        private uint _aspectMode = 3, _border, _prefs;
        private byte[]? _last;                              // the last frame shown (BGRA), its size and time
        private int _lastW, _lastH;
        private long _lastTime;
        private readonly object _lock = new();

        public Guid Clsid => RendererClsid;
        public bool IgnoreClock => false;
        public long Lead => 0;
        public ComObject Sink => this;
        /// <summary>The renderer's device manager (IMFDXGIDeviceManager): the D3D awareness the loader gives the branch.</summary>
        public IntPtr DeviceManager => _manager;

        private static Vtable Build()
        {
            var vt = new Vtable("IMFMediaSink", 12, SinkIds.IID_IMFMediaSink);
            vt.Set(3, (delegate* unmanaged<IntPtr, uint*, int>)&GetCharacteristics);
            vt.Set(4, (delegate* unmanaged<IntPtr, uint, IntPtr, IntPtr*, int>)&AddStreamSink);
            vt.Set(5, (delegate* unmanaged<IntPtr, uint, int>)&RemoveStreamSink);
            vt.Set(6, (delegate* unmanaged<IntPtr, uint*, int>)&GetStreamSinkCount);
            vt.Set(7, (delegate* unmanaged<IntPtr, uint, IntPtr*, int>)&GetStreamSinkByIndex);
            vt.Set(8, (delegate* unmanaged<IntPtr, uint, IntPtr*, int>)&GetStreamSinkById);
            vt.Set(9, (delegate* unmanaged<IntPtr, IntPtr, int>)&SetPresentationClock);
            vt.Set(10, (delegate* unmanaged<IntPtr, IntPtr*, int>)&GetPresentationClock);
            vt.Set(11, (delegate* unmanaged<IntPtr, int>)&ShutdownSlot);
            return vt;
        }

        private static Vtable BuildClock()
        {
            var vt = new Vtable("IMFClockStateSink", 8, MFPresentationClock.IID_IMFClockStateSink);
            vt.Set(3, (delegate* unmanaged<IntPtr, long, long, int>)&OnClockStart);
            vt.Set(4, (delegate* unmanaged<IntPtr, long, int>)&OnClockStop);
            vt.Set(5, (delegate* unmanaged<IntPtr, long, int>)&OnClockPause);
            vt.Set(6, (delegate* unmanaged<IntPtr, long, int>)&OnClockRestart);
            vt.Set(7, (delegate* unmanaged<IntPtr, long, float, int>)&OnClockSetRate);
            return vt;
        }

        public MFVideoRenderer(IntPtr hwnd) : base(s_vt, s_clockVt, s_serviceVt, s_displayVt)
        {
            _hwnd = hwnd;
            Stream = new MFGrabberStream(this, new VideoTypeHandler());
            uint token; IntPtr m;
            if (MFExports.MFCreateDXGIDeviceManager(&token, &m) >= 0) _manager = m;
        }

        protected override void OnFinalRelease() { Shutdown(); Stream.Release(); Com.Release(_manager); _manager = IntPtr.Zero; }

        public void Shutdown()
        {
            IntPtr hwnd;
            lock (_lock) { if (_shutdown) return; _shutdown = true; hwnd = _hwnd; }
            Stream.Shut();
            Detach();
            if (hwnd != IntPtr.Zero) VideoSurfaces.Remove(hwnd);
        }

        private void Detach()
        {
            IntPtr me = PointerFor(s_clockVt);
            if (_clock != null) { _clock.RemoveSink(me); _clock.Release(); _clock = null; }
            if (_foreignClock != IntPtr.Zero) { ((delegate* unmanaged<IntPtr, IntPtr, int>)Com.Fn(_foreignClock, 12))(_foreignClock, me); Com.Release(_foreignClock); _foreignClock = IntPtr.Zero; }
        }

        public long? Now
        {
            get
            {
                if (_clock != null) return _clock.Time;
                if (_foreignClock == IntPtr.Zero) return null;
                long t;
                return ((delegate* unmanaged<IntPtr, long*, int>)Com.Fn(_foreignClock, 10))(_foreignClock, &t) >= 0 ? t : null;
            }
        }

        private (int W, int H, Guid Sub, float Aspect) Format()
        {
            var t = ((VideoTypeHandler)Stream.Handler).Current;
            if (t == null) return (0, 0, Guid.Empty, 1);
            var (w, h) = t.FrameSize;
            var (pn, pd) = MF.Unpack(t.Attributes.U64(MF.MF_MT_PIXEL_ASPECT_RATIO, MF.Pack(1, 1)));
            float aspect = h == 0 ? 1 : (float)w * (pn == 0 ? 1 : pn) / (h * (pd == 0 ? 1 : pd));
            return ((int)w, (int)h, t.Subtype, aspect);
        }

        /// <summary>The frame as BGRA, shown in the window (the source rectangle's part of it).</summary>
        public void Deliver(IntPtr sample, Guid major, long time, long duration)
        {
            var (w, h, sub, aspect) = Format();
            if (w <= 0 || h <= 0) return;
            byte[] bytes = MFSample.ForeignSampleBytes(sample);
            var picture = Pictures.Decode(bytes, sub, w, h, Pictures.BottomUp(((VideoTypeHandler)Stream.Handler).Current!, byDefault: false));
            byte[] bgra = Pictures.Encode(picture, MFVideoProcessor.RGB32, false, converter: true);
            for (int i = 3; i < bgra.Length; i += 4) bgra[i] = 255;
            // the source rectangle's part
            float l, t, r, b;
            lock (_lock) { l = _srcL; t = _srcT; r = _srcR; b = _srcB; }
            int x0 = (int)(l * w), y0 = (int)(t * h), x1 = Math.Max(x0 + 1, (int)(r * w)), y1 = Math.Max(y0 + 1, (int)(b * h));
            if (x0 != 0 || y0 != 0 || x1 != w || y1 != h)
            {
                int cw = x1 - x0, ch = y1 - y0;
                var crop = new byte[cw * ch * 4];
                for (int y = 0; y < ch; y++) Array.Copy(bgra, ((y0 + y) * w + x0) * 4, crop, y * cw * 4, cw * 4);
                bgra = crop; aspect *= (float)cw / w * h / ch; w = cw; h = ch;
            }
            IntPtr hwnd;
            lock (_lock) { _last = bgra; _lastW = w; _lastH = h; _lastTime = time; hwnd = _hwnd; }
            if (hwnd != IntPtr.Zero) VideoSurfaces.Present(hwnd, bgra, w, h, aspect);
        }

        private void Place()
        {
            IntPtr hwnd; bool placed, preserve; int x, y, w, h; uint border;
            lock (_lock) { hwnd = _hwnd; placed = _placed; x = _dstL; y = _dstT; w = _dstR - _dstL; h = _dstB - _dstT; preserve = (_aspectMode & 1) != 0; border = _border; }
            if (hwnd != IntPtr.Zero) VideoSurfaces.Place(hwnd, placed, x, y, w, h, preserve, border, true);
        }

        private static MFVideoRenderer? S(IntPtr self) => From<MFVideoRenderer>(self);

        [UnmanagedCallersOnly] private static int GetCharacteristics(IntPtr self, uint* c) { if (S(self) is not { } s || c == null) return MF.E_POINTER; if (s._shutdown) return MF.MF_E_SHUTDOWN; *c = SinkIds.MEDIASINK_FIXED_STREAMS; return MF.S_OK; }
        [UnmanagedCallersOnly] private static int AddStreamSink(IntPtr self, uint id, IntPtr type, IntPtr* stream) { if (stream != null) *stream = IntPtr.Zero; return unchecked((int)0xC00D4A3A); }
        [UnmanagedCallersOnly] private static int RemoveStreamSink(IntPtr self, uint id) => unchecked((int)0xC00D4A3A);
        [UnmanagedCallersOnly] private static int GetStreamSinkCount(IntPtr self, uint* n) { if (S(self) is not { } s || n == null) return MF.E_POINTER; if (s._shutdown) return MF.MF_E_SHUTDOWN; *n = 1; return MF.S_OK; }

        [UnmanagedCallersOnly]
        private static int GetStreamSinkByIndex(IntPtr self, uint i, IntPtr* stream)
        {
            if (S(self) is not { } s || stream == null) return MF.E_POINTER;
            *stream = IntPtr.Zero;
            if (s._shutdown) return MF.MF_E_SHUTDOWN;
            if (i != 0) return MF.E_INVALIDARG;
            *stream = s.Stream.AddRefPointer();
            return MF.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int GetStreamSinkById(IntPtr self, uint id, IntPtr* stream)
        {
            if (S(self) is not { } s || stream == null) return MF.E_POINTER;
            *stream = IntPtr.Zero;
            if (s._shutdown) return MF.MF_E_SHUTDOWN;
            if (id != 0) return unchecked((int)0xC00D4A3B);
            *stream = s.Stream.AddRefPointer();
            return MF.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int SetPresentationClock(IntPtr self, IntPtr clock)
        {
            if (S(self) is not { } s) return MF.E_POINTER;
            if (s._shutdown) return MF.MF_E_SHUTDOWN;
            s.Detach();
            IntPtr me = s.PointerFor(s_clockVt);
            if (From<MFPresentationClock>(clock) is { } ours) { ours.AddRef(); s._clock = ours; ours.AddSink(me); }
            else if (clock != IntPtr.Zero) { Com.AddRef(clock); s._foreignClock = clock; ((delegate* unmanaged<IntPtr, IntPtr, int>)Com.Fn(clock, 11))(clock, me); }
            return MF.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int GetPresentationClock(IntPtr self, IntPtr* clock)
        {
            if (S(self) is not { } s || clock == null) return MF.E_POINTER;
            *clock = IntPtr.Zero;
            IntPtr c = s._clock?.Pointer ?? s._foreignClock;
            if (c == IntPtr.Zero) return SinkIds.MF_E_NO_CLOCK;
            Com.AddRef(c); *clock = c;
            return MF.S_OK;
        }

        [UnmanagedCallersOnly] private static int ShutdownSlot(IntPtr self) { if (S(self) is not { } s) return MF.E_POINTER; if (s._shutdown) return MF.MF_E_SHUTDOWN; s.Shutdown(); return MF.S_OK; }

        [UnmanagedCallersOnly] private static int OnClockStart(IntPtr self, long system, long offset) { if (S(self) is not { } s) return MF.E_POINTER; s.Place(); s.Stream.ClockStarted(offset); return MF.S_OK; }
        [UnmanagedCallersOnly] private static int OnClockStop(IntPtr self, long system) { if (S(self) is not { } s) return MF.E_POINTER; s.Stream.ClockStopped(); return MF.S_OK; }
        [UnmanagedCallersOnly] private static int OnClockPause(IntPtr self, long system) { if (S(self) is not { } s) return MF.E_POINTER; s.Stream.ClockPaused(); return MF.S_OK; }
        [UnmanagedCallersOnly] private static int OnClockRestart(IntPtr self, long system) { if (S(self) is not { } s) return MF.E_POINTER; s.Stream.ClockRestarted(); return MF.S_OK; }
        [UnmanagedCallersOnly] private static int OnClockSetRate(IntPtr self, long system, float rate) => MF.S_OK;

        // ---- IMFGetService ----

        private static Vtable BuildService()
        {
            var vt = new Vtable("IMFGetService", 4, IID_IMFGetService);
            vt.Set(3, (delegate* unmanaged<IntPtr, Guid*, Guid*, IntPtr*, int>)&GetService);
            return vt;
        }

        [UnmanagedCallersOnly]
        private static int GetService(IntPtr self, Guid* service, Guid* iid, IntPtr* ppv)
        {
            if (S(self) is not { } s || service == null || iid == null || ppv == null) return MF.E_POINTER;
            *ppv = IntPtr.Zero;
            if (*service == MR_VIDEO_RENDER_SERVICE)
            {
                if (*iid == IID_IMFVideoDisplayControl || *iid == IID_IUnknown) { *ppv = s.AddRefPointer(s_displayVt); return MF.S_OK; }
                return MF.E_NOINTERFACE;
            }
            if (*service == MR_VIDEO_ACCELERATION_SERVICE && s._manager != IntPtr.Zero) { int hr = Com.QueryInterface(s._manager, *iid, out var p); *ppv = p; return hr; }
            return unchecked((int)0xC00D36BA);                  // MF_E_UNSUPPORTED_SERVICE
        }

        // ---- IMFVideoDisplayControl ----

        private static Vtable BuildDisplay()
        {
            var vt = new Vtable("IMFVideoDisplayControl", 19, IID_IMFVideoDisplayControl);
            vt.Set(3, (delegate* unmanaged<IntPtr, int*, int*, int>)&GetNativeVideoSize);
            vt.Set(4, (delegate* unmanaged<IntPtr, int*, int*, int>)&GetIdealVideoSize);
            vt.Set(5, (delegate* unmanaged<IntPtr, float*, int*, int>)&SetVideoPosition);
            vt.Set(6, (delegate* unmanaged<IntPtr, float*, int*, int>)&GetVideoPosition);
            vt.Set(7, (delegate* unmanaged<IntPtr, uint, int>)&SetAspectRatioMode);
            vt.Set(8, (delegate* unmanaged<IntPtr, uint*, int>)&GetAspectRatioMode);
            vt.Set(9, (delegate* unmanaged<IntPtr, IntPtr, int>)&SetVideoWindow);
            vt.Set(10, (delegate* unmanaged<IntPtr, IntPtr*, int>)&GetVideoWindow);
            vt.Set(11, (delegate* unmanaged<IntPtr, int>)&RepaintVideo);
            vt.Set(12, (delegate* unmanaged<IntPtr, byte*, byte**, uint*, long*, int>)&GetCurrentImage);
            vt.Set(13, (delegate* unmanaged<IntPtr, uint, int>)&SetBorderColor);
            vt.Set(14, (delegate* unmanaged<IntPtr, uint*, int>)&GetBorderColor);
            vt.Set(15, (delegate* unmanaged<IntPtr, uint, int>)&SetRenderingPrefs);
            vt.Set(16, (delegate* unmanaged<IntPtr, uint*, int>)&GetRenderingPrefs);
            vt.Set(17, (delegate* unmanaged<IntPtr, int, int>)&SetFullscreen);
            vt.Set(18, (delegate* unmanaged<IntPtr, int*, int>)&GetFullscreen);
            return vt;
        }

        /// <summary>The picture's size (SIZE), and its display aspect reduced (4x3 of a 64x48 of square pixels).</summary>
        [UnmanagedCallersOnly]
        private static int GetNativeVideoSize(IntPtr self, int* native, int* aspect)
        {
            if (S(self) is not { } s) return MF.E_POINTER;
            if (native == null && aspect == null) return MF.E_POINTER;
            var t = ((VideoTypeHandler)s.Stream.Handler).Current;
            int w = 0, h = 0, aw = 0, ah = 0;
            if (t != null)
            {
                var (fw, fh) = t.FrameSize; w = (int)fw; h = (int)fh;
                var (pn, pd) = MF.Unpack(t.Attributes.U64(MF.MF_MT_PIXEL_ASPECT_RATIO, MF.Pack(1, 1)));
                long x = (long)w * Math.Max(1u, pn), y = (long)h * Math.Max(1u, pd);
                long g = Gcd(x, y);
                if (g > 0) { aw = (int)(x / g); ah = (int)(y / g); }
            }
            if (native != null) { native[0] = w; native[1] = h; }
            if (aspect != null) { aspect[0] = aw; aspect[1] = ah; }
            return MF.S_OK;
        }

        private static long Gcd(long a, long b) { while (b != 0) (a, b) = (b, a % b); return a; }

        [UnmanagedCallersOnly]
        private static int GetIdealVideoSize(IntPtr self, int* min, int* max)
        {
            if (S(self) is not { } s) return MF.E_POINTER;
            var t = ((VideoTypeHandler)s.Stream.Handler).Current;
            int w = 0, h = 0;
            if (t != null) { var (fw, fh) = t.FrameSize; w = (int)fw; h = (int)fh; }
            if (min != null) { min[0] = 1; min[1] = 1; }
            if (max != null) { max[0] = Math.Max(w, 1) * 8; max[1] = Math.Max(h, 1) * 8; }
            return MF.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int SetVideoPosition(IntPtr self, float* src, int* dst)
        {
            if (S(self) is not { } s) return MF.E_POINTER;
            if (src == null && dst == null) return MF.E_POINTER;
            if (src != null && (src[0] < 0 || src[1] < 0 || src[2] > 1 || src[3] > 1 || src[0] > src[2] || src[1] > src[3])) return MF.E_INVALIDARG;
            if (dst != null && (dst[0] > dst[2] || dst[1] > dst[3])) return MF.E_INVALIDARG;
            lock (s._lock)
            {
                if (src != null) { s._srcL = src[0]; s._srcT = src[1]; s._srcR = src[2]; s._srcB = src[3]; }
                if (dst != null) { s._dstL = dst[0]; s._dstT = dst[1]; s._dstR = dst[2]; s._dstB = dst[3]; s._placed = true; }
            }
            s.Place();
            return MF.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int GetVideoPosition(IntPtr self, float* src, int* dst)
        {
            if (S(self) is not { } s || src == null || dst == null) return MF.E_POINTER;
            lock (s._lock)
            {
                src[0] = s._srcL; src[1] = s._srcT; src[2] = s._srcR; src[3] = s._srcB;
                dst[0] = s._dstL; dst[1] = s._dstT; dst[2] = s._dstR; dst[3] = s._dstB;
            }
            return MF.S_OK;
        }

        [UnmanagedCallersOnly] private static int SetAspectRatioMode(IntPtr self, uint mode) { if (S(self) is not { } s) return MF.E_POINTER; if ((mode & ~7u) != 0) return MF.E_INVALIDARG; lock (s._lock) s._aspectMode = mode; s.Place(); return MF.S_OK; }
        [UnmanagedCallersOnly] private static int GetAspectRatioMode(IntPtr self, uint* mode) { if (S(self) is not { } s || mode == null) return MF.E_POINTER; lock (s._lock) *mode = s._aspectMode; return MF.S_OK; }

        [UnmanagedCallersOnly]
        private static int SetVideoWindow(IntPtr self, IntPtr hwnd)
        {
            if (S(self) is not { } s) return MF.E_POINTER;
            IntPtr old;
            lock (s._lock) { old = s._hwnd; s._hwnd = hwnd; }
            if (old != IntPtr.Zero && old != hwnd) VideoSurfaces.Remove(old);
            s.Place();
            return MF.S_OK;
        }

        [UnmanagedCallersOnly] private static int GetVideoWindow(IntPtr self, IntPtr* hwnd) { if (S(self) is not { } s || hwnd == null) return MF.E_POINTER; lock (s._lock) *hwnd = s._hwnd; return MF.S_OK; }

        /// <summary>The last frame shown again (an application's WM_PAINT while paused).</summary>
        [UnmanagedCallersOnly]
        private static int RepaintVideo(IntPtr self)
        {
            if (S(self) is not { } s) return MF.E_POINTER;
            byte[]? last; int w, h; IntPtr hwnd;
            lock (s._lock) { last = s._last; w = s._lastW; h = s._lastH; hwnd = s._hwnd; }
            if (last != null && hwnd != IntPtr.Zero) VideoSurfaces.Present(hwnd, last, w, h, s.Format().Aspect);
            return MF.S_OK;
        }

        /// <summary>The frame shown: a BITMAPINFOHEADER (32-bit, bottom-up) and its bits (CoTaskMemAlloc'd), and its time.</summary>
        [UnmanagedCallersOnly]
        private static int GetCurrentImage(IntPtr self, byte* header, byte** dib, uint* size, long* time)
        {
            if (S(self) is not { } s || header == null || dib == null || size == null) return MF.E_POINTER;
            if (*(uint*)header != 40) return MF.E_INVALIDARG;
            byte[]? last; int w, h; long t;
            lock (s._lock) { last = s._last; w = s._lastW; h = s._lastH; t = s._lastTime; }
            if (last == null) return MF.E_FAIL;
            *(int*)(header + 4) = w; *(int*)(header + 8) = h; *(ushort*)(header + 12) = 1; *(ushort*)(header + 14) = 32;
            *(uint*)(header + 16) = 0; *(uint*)(header + 20) = (uint)(w * h * 4);
            byte* bits = (byte*)Marshal.AllocCoTaskMem(w * h * 4);
            for (int y = 0; y < h; y++) new ReadOnlySpan<byte>(last, (h - 1 - y) * w * 4, w * 4).CopyTo(new Span<byte>(bits + y * w * 4, w * 4));
            *dib = bits; *size = (uint)(w * h * 4);
            if (time != null) *time = t;
            return MF.S_OK;
        }

        [UnmanagedCallersOnly] private static int SetBorderColor(IntPtr self, uint c) { if (S(self) is not { } s) return MF.E_POINTER; lock (s._lock) s._border = c & 0xFFFFFF; s.Place(); return MF.S_OK; }
        [UnmanagedCallersOnly] private static int GetBorderColor(IntPtr self, uint* c) { if (S(self) is not { } s || c == null) return MF.E_POINTER; lock (s._lock) *c = s._border; return MF.S_OK; }
        [UnmanagedCallersOnly] private static int SetRenderingPrefs(IntPtr self, uint p) { if (S(self) is not { } s) return MF.E_POINTER; lock (s._lock) s._prefs = p; return MF.S_OK; }
        [UnmanagedCallersOnly] private static int GetRenderingPrefs(IntPtr self, uint* p) { if (S(self) is not { } s || p == null) return MF.E_POINTER; lock (s._lock) *p = s._prefs; return MF.S_OK; }
        [UnmanagedCallersOnly] private static int SetFullscreen(IntPtr self, int f) { if (S(self) is not { } s) return MF.E_POINTER; lock (s._lock) s._fullscreen = f != 0; return MF.S_OK; }
        [UnmanagedCallersOnly] private static int GetFullscreen(IntPtr self, int* f) { if (S(self) is not { } s || f == null) return MF.E_POINTER; lock (s._lock) *f = s._fullscreen ? 1 : 0; return MF.S_OK; }
    }

    /// <summary>The video renderer's handler: the uncompressed video its mixer takes, of any size.</summary>
    internal sealed unsafe class VideoTypeHandler : ComObject
    {
        private static readonly Vtable s_vt = Build();
        private static Guid F(string s) => Codecs.MediaFormats.FourCC(s);
        private static readonly Guid[] s_takes = { F("NV12"), F("YUY2"), F("UYVY"), F("YV12"), F("IYUV"), F("I420"), F("AYUV"), MFVideoProcessor.RGB32, MFVideoProcessor.ARGB32 };
        public MFMediaType? Current;

        private static Vtable Build()
        {
            var vt = new Vtable("IMFMediaTypeHandler", 9, new Guid("e93dcf6c-4b07-4e1e-8123-aa16ed6eadf5"));
            vt.Set(3, (delegate* unmanaged<IntPtr, IntPtr, IntPtr*, int>)&IsMediaTypeSupported);
            vt.Set(4, (delegate* unmanaged<IntPtr, uint*, int>)&GetMediaTypeCount);
            vt.Set(5, (delegate* unmanaged<IntPtr, uint, IntPtr*, int>)&GetMediaTypeByIndex);
            vt.Set(6, (delegate* unmanaged<IntPtr, IntPtr, int>)&SetCurrentMediaType);
            vt.Set(7, (delegate* unmanaged<IntPtr, IntPtr*, int>)&GetCurrentMediaType);
            vt.Set(8, (delegate* unmanaged<IntPtr, Guid*, int>)&GetMajorType);
            return vt;
        }

        public VideoTypeHandler() : base(s_vt) { }

        protected override void OnFinalRelease() { Current?.Release(); }

        private static bool Takes(IntPtr type)
        {
            var t = Source.Mp4Types.Read(type);
            try
            {
                var (w, h) = t.FrameSize;
                return t.Attributes.Guid(MF.MF_MT_MAJOR_TYPE) == MF.MFMediaType_Video && Array.IndexOf(s_takes, t.Subtype) >= 0 && w > 0 && h > 0;
            }
            finally { t.Release(); }
        }

        private static VideoTypeHandler? H(IntPtr self) => From<VideoTypeHandler>(self);

        [UnmanagedCallersOnly]
        private static int IsMediaTypeSupported(IntPtr self, IntPtr type, IntPtr* closest)
        {
            if (H(self) is null || type == IntPtr.Zero) return MF.E_POINTER;
            if (closest != null) *closest = IntPtr.Zero;
            return Takes(type) ? MF.S_OK : MF.MF_E_INVALIDMEDIATYPE;
        }

        [UnmanagedCallersOnly] private static int GetMediaTypeCount(IntPtr self, uint* n) { if (n == null) return MF.E_POINTER; *n = (uint)s_takes.Length; return MF.S_OK; }

        [UnmanagedCallersOnly]
        private static int GetMediaTypeByIndex(IntPtr self, uint i, IntPtr* type)
        {
            if (type == null) return MF.E_POINTER;
            *type = IntPtr.Zero;
            if (i >= s_takes.Length) return MF.MF_E_NO_MORE_TYPES;
            var t = new MFMediaType();
            t.Attributes.Set(MF.MF_MT_MAJOR_TYPE, MF.MFMediaType_Video);
            t.Attributes.Set(MF.MF_MT_SUBTYPE, s_takes[i]);
            *type = t.Pointer;
            return MF.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int SetCurrentMediaType(IntPtr self, IntPtr type)
        {
            if (H(self) is not { } h || type == IntPtr.Zero) return MF.E_POINTER;
            if (!Takes(type)) return MF.MF_E_INVALIDMEDIATYPE;
            h.Current?.Release();
            h.Current = Source.Mp4Types.Read(type);
            return MF.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int GetCurrentMediaType(IntPtr self, IntPtr* type)
        {
            if (H(self) is not { } h || type == null) return MF.E_POINTER;
            *type = IntPtr.Zero;
            if (h.Current == null) return MFE.MF_E_NOT_INITIALIZED;
            *type = h.Current.AddRefPointer();
            return MF.S_OK;
        }

        [UnmanagedCallersOnly] private static int GetMajorType(IntPtr self, Guid* major) { if (major == null) return MF.E_POINTER; *major = MF.MFMediaType_Video; return MF.S_OK; }
    }
}
