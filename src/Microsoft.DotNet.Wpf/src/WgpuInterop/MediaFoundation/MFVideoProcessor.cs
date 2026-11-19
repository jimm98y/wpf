// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Windows' video processor MFT (CLSID_VideoProcessorMFT), as an application makes it by CLSID (MFVideoProcessorProbe,
// VideoProcessor.windows.txt): IMFTransform with IMFVideoProcessorControl(2), IMFRealTimeClientEx and IMFGetService;
// the 36 input subtypes Windows' offers and the 21 outputs it offers once there is an input, each of its major type and
// subtype alone; one picture in, one out, into the application's sample, of the input's time, a clean point, not
// interlaced. Converted and scaled as Windows' converts them:
//
//   - YUV to YUV keeps the samples (the chroma averaged where the output has less of it, repeated where more);
//   - YUV to RGB by BT.601 in 8-bit fixed point (ReaderStream.Nv12ToRgb's, Windows' to the byte), RGB to YUV by its
//     inverse, the chroma of each 2x2 averaged;
//   - RGB of no MF_MT_DEFAULT_STRIDE is bottom-up (Windows' video processor's: the color converter's is top-down);
//   - another size: each plane bilinearly, at the full resolution.
//
// Of the subtypes listed, those of more than 8 bits, of floats, palettes and the rarer packings are not converted here:
// SetInputType answers MF_E_INVALIDMEDIATYPE of those (logged once).
//

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Wpf.Interop.WebGpu.Direct3D;
using Microsoft.Wpf.Interop.WebGpu.MediaFoundation.Codecs;

namespace Microsoft.Wpf.Interop.WebGpu.MediaFoundation
{
    internal sealed unsafe class MFVideoProcessor : MFEncoder
    {
        public static readonly Guid Clsid = new("88753b26-5b24-49bd-b2e7-0c445c78c982");
        private static readonly Guid IID_IMFVideoProcessorControl = new("a3f675d5-6119-4f7f-a100-1d8b280f0efb"), IID_IMFVideoProcessorControl2 = new("bde633d3-e1dc-4a7f-a693-bbae399c4a20");
        private static readonly Guid IID_IMFRealTimeClientEx = new("03910848-ab16-4611-b100-17b88ae2f248"), IID_IMFGetService = new("fa993888-4383-415a-a930-dd472a8cf6f7");
        private static readonly Guid MF_MT_DEFAULT_STRIDE = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
        private static readonly Guid MFSampleExtension_Interlaced = new("b1d5830a-deb8-40e3-90fa-389943716461");

        internal static Guid D3D(uint v) => new(v, 0, 0x10, 0x80, 0, 0, 0xaa, 0, 0x38, 0x9b, 0x71);
        private static Guid F(string s) => MediaFormats.FourCC(s);

        internal static readonly Guid L8 = D3D(0x32), L16 = D3D(0x51), D16 = D3D(0x50), ABGR32 = D3D(0x20), A2R10G10B10 = D3D(0x1f), A16B16G16R16F = D3D(0x71);
        internal static readonly Guid RGB32 = D3D(0x16), ARGB32 = D3D(0x15), RGB24 = D3D(0x14), RGB565 = D3D(0x17), RGB555 = D3D(0x18), P8 = D3D(0x29);

        /// <summary>Windows' input subtypes, in its order.</summary>
        internal static readonly Guid[] Inputs =
        {
            L8, L16, D16, F("IYUV"), F("YV12"), F("NV12"), F("NV21"), F("420O"), F("P010"), F("P016"), F("UYVY"), F("YUY2"), F("P208"), F("Y210"),
            F("Y216"), F("NV11"), F("AYUV"), F("Y410"), ARGB32, ABGR32, RGB32, A2R10G10B10, F("Y416"), A16B16G16R16F, RGB24, F("I420"), F("I422"),
            F("I444"), F("YVYU"), RGB555, RGB565, P8, F("v410"), F("Y41P"), F("Y41T"), F("Y42T"),
        };

        /// <summary>Windows' output subtypes once there is an input, in its order (some twice, as Windows lists them).</summary>
        internal static readonly Guid[] Outputs =
        {
            F("NV12"), F("YUY2"), F("IYUV"), F("I420"), RGB24, ARGB32, RGB32, F("YV12"), F("AYUV"), F("NV21"), RGB32, ARGB32, ABGR32, A2R10G10B10,
            A16B16G16R16F, F("NV12"), F("P010"), F("P016"), L8, L16, D16,
        };

        private static readonly Vtable s_control = BuildControl(), s_realtime = BuildRealTime(), s_service = BuildService();

        private IntPtr _held;                         // the input sample, AddRef'd, until its picture is out
        private int _rotation; private int _mirror;   // IMFVideoProcessorControl's (kept; not applied)

        public MFVideoProcessor() : base(new[] { s_control, s_realtime, s_service })
        {
            Bag.Attributes.Set(new Guid("093f81b1-4f2e-4631-8168-7934032a01d3"), 1u);
            Bag.Attributes.Set(new Guid("206b4fc8-fcf9-4c51-afe3-9764369e33a0"), 1u);     // MF_SA_D3D11_AWARE
            Bag.Attributes.Set(new Guid("77f0bacb-17a8-4a50-9a7d-a5cc09d39d44"), 1u);
        }

        public override void Shutdown() { Com.Release(_held); _held = IntPtr.Zero; }
        protected override uint MaxInputStreams => 4;

        private static MFMediaType Of(Guid sub)
        {
            var t = new MFMediaType();
            t.Attributes.Set(MF.MF_MT_MAJOR_TYPE, MF.MFMediaType_Video);
            t.Attributes.Set(MF.MF_MT_SUBTYPE, sub);
            return t;
        }

        protected override int InputAvailable(uint i, out MFMediaType? type)
        {
            type = i < Inputs.Length ? Of(Inputs[i]) : null;
            return type == null ? MF.MF_E_NO_MORE_TYPES : MF.S_OK;
        }

        protected override int OutputAvailable(uint i, out MFMediaType? type)
        {
            type = In != IntPtr.Zero && i < Outputs.Length ? Of(Outputs[i]) : null;
            return type == null ? MF.MF_E_NO_MORE_TYPES : MF.S_OK;
        }

        protected override int CheckInput(MFMediaType t, out List<(Guid, MFValue)> fill)
        {
            fill = new();
            if (t.Attributes.Guid(MF.MF_MT_MAJOR_TYPE) != MF.MFMediaType_Video || Array.IndexOf(Inputs, t.Subtype) < 0) return MF.MF_E_INVALIDMEDIATYPE;
            var (w, h) = t.FrameSize;
            if (w == 0 || h == 0) return MF.MF_E_INVALIDMEDIATYPE;
            if (!Pictures.Reads(t.Subtype)) { D3DLog.Once("vproc-in:" + t.Subtype, $"Media Foundation: the video processor converts not from {t.Subtype}"); return MF.MF_E_INVALIDMEDIATYPE; }
            return MF.S_OK;
        }

        protected override int CheckOutput(MFMediaType t, bool test, out List<(Guid, MFValue)> fill)
        {
            fill = new();
            if (t.Attributes.Guid(MF.MF_MT_MAJOR_TYPE) != MF.MFMediaType_Video || Array.IndexOf(Outputs, t.Subtype) < 0) return MF.MF_E_INVALIDMEDIATYPE;
            var (w, h) = t.FrameSize;
            if (w == 0 || h == 0) return MF.MF_E_INVALIDMEDIATYPE;
            return MF.S_OK;
        }

        protected override void TypesChanged() { Com.Release(_held); _held = IntPtr.Zero; }

        protected override uint InputSize() { if (In == IntPtr.Zero) return 0; var t = Read(In); try { var (w, h) = t.FrameSize; return (uint)Pictures.Size(t.Subtype, (int)w, (int)h); } finally { t.Release(); } }
        protected override uint OutputSize() { if (Out == IntPtr.Zero) return 0; var t = Read(Out); try { var (w, h) = t.FrameSize; return (uint)Pictures.Size(t.Subtype, (int)w, (int)h); } finally { t.Release(); } }

        protected override int Input(IntPtr sample)
        {
            if (_held != IntPtr.Zero) return MF.MF_E_NOTACCEPTING;
            Com.AddRef(sample);
            _held = sample;
            return MF.S_OK;
        }

        protected override int Output(IntPtr sample)
        {
            if (_held == IntPtr.Zero) return MF.MF_E_TRANSFORM_NEED_MORE_INPUT;
            var i = Read(In); var o = Read(Out);
            try
            {
                var (iw, ih) = i.FrameSize; var (ow, oh) = o.FrameSize;
                var picture = Pictures.Decode(Bytes(_held), i.Subtype, (int)iw, (int)ih, Pictures.BottomUp(i));
                // to the output's kind, then its size (Windows' scales an RGB output's pixels, a YUV output's planes)
                picture = Pictures.IsRgb(o.Subtype) ? picture.AsRgb() : picture.AsYuv();
                if (ow != iw || oh != ih) picture = picture.Scaled((int)ow, (int)oh);
                var bytes = Pictures.Encode(picture, o.Subtype, Pictures.BottomUp(o));
                long time = Time(_held) ?? 0, duration = Duration(_held) ?? 0;
                int hr = Write(sample, bytes, time, duration, new[] { (MF.MFSampleExtension_CleanPoint, MFValue.Of(1u)), (MFSampleExtension_Interlaced, MFValue.Of(0u)) });
                if (hr < 0) return hr;
                Com.Release(_held); _held = IntPtr.Zero;
                return MF.S_OK;
            }
            finally { i.Release(); o.Release(); }
        }

        protected override void Flush() { Com.Release(_held); _held = IntPtr.Zero; }
        protected override void Drain() { }

        // ---- IMFVideoProcessorControl(2), IMFRealTimeClientEx, IMFGetService ------------------------------------

        private static Vtable BuildControl()
        {
            var vt = new Vtable("IMFVideoProcessorControl2", 12, IID_IMFVideoProcessorControl, IID_IMFVideoProcessorControl2);
            vt.Set(3, (delegate* unmanaged<IntPtr, uint*, int>)&SetBorderColor);
            vt.Set(4, (delegate* unmanaged<IntPtr, int*, int>)&SetRectangle);
            vt.Set(5, (delegate* unmanaged<IntPtr, int*, int>)&SetRectangle);
            vt.Set(6, (delegate* unmanaged<IntPtr, int, int>)&SetMirror);
            vt.Set(7, (delegate* unmanaged<IntPtr, int, int>)&SetRotation);
            vt.Set(8, (delegate* unmanaged<IntPtr, uint*, int>)&SetConstrictionSize);
            vt.Set(9, (delegate* unmanaged<IntPtr, uint, int>)&SetRotationOverride);
            vt.Set(10, (delegate* unmanaged<IntPtr, int, int>)&EnableHardwareEffects);
            vt.Set(11, (delegate* unmanaged<IntPtr, uint*, int>)&GetSupportedHardwareEffects);
            return vt;
        }

        private static MFVideoProcessor? P(IntPtr self) => From<MFVideoProcessor>(self);
        [UnmanagedCallersOnly] private static int SetBorderColor(IntPtr self, uint* c) => MF.S_OK;
        [UnmanagedCallersOnly] private static int SetRectangle(IntPtr self, int* r) => MF.S_OK;
        [UnmanagedCallersOnly] private static int SetMirror(IntPtr self, int m) { if (P(self) is { } p) p._mirror = m; return MF.S_OK; }
        [UnmanagedCallersOnly] private static int SetRotation(IntPtr self, int r) { if (P(self) is { } p) p._rotation = r; return MF.S_OK; }
        [UnmanagedCallersOnly] private static int SetConstrictionSize(IntPtr self, uint* s) => MF.S_OK;
        [UnmanagedCallersOnly] private static int SetRotationOverride(IntPtr self, uint r) => MF.S_OK;
        [UnmanagedCallersOnly] private static int EnableHardwareEffects(IntPtr self, int e) => MF.S_OK;
        [UnmanagedCallersOnly] private static int GetSupportedHardwareEffects(IntPtr self, uint* e) { if (e == null) return MF.E_POINTER; *e = 0; return MF.S_OK; }

        private static Vtable BuildRealTime()
        {
            var vt = new Vtable("IMFRealTimeClientEx", 6, IID_IMFRealTimeClientEx);
            vt.Set(3, (delegate* unmanaged<IntPtr, uint*, char*, int, int>)&RegisterThreadsEx);
            vt.Set(4, (delegate* unmanaged<IntPtr, int>)&UnregisterThreads);
            vt.Set(5, (delegate* unmanaged<IntPtr, uint, int>)&SetWorkQueueEx);
            return vt;
        }

        [UnmanagedCallersOnly] private static int RegisterThreadsEx(IntPtr self, uint* task, char* cls, int priority) => MF.S_OK;
        [UnmanagedCallersOnly] private static int UnregisterThreads(IntPtr self) => MF.S_OK;
        [UnmanagedCallersOnly] private static int SetWorkQueueEx(IntPtr self, uint queue) => MF.S_OK;

        private static Vtable BuildService()
        {
            var vt = new Vtable("IMFGetService", 4, IID_IMFGetService);
            vt.Set(3, (delegate* unmanaged<IntPtr, Guid*, Guid*, IntPtr*, int>)&GetService);
            return vt;
        }

        [UnmanagedCallersOnly] private static int GetService(IntPtr self, Guid* service, Guid* riid, IntPtr* ppv) { if (ppv != null) *ppv = IntPtr.Zero; return unchecked((int)0xC00D36BA); }   // MF_E_UNSUPPORTED_SERVICE
    }

    /// <summary>
    /// A picture: of RGB, three planes and alpha of full resolution; of YUV, luma and alpha of full resolution and chroma of
    /// the source's own subsampling (SubX, SubY: 2 of 4:2:0's, 2 and 1 of 4:2:2's, 1 of 4:4:4's), as the video processor
    /// scales and converts it.
    /// </summary>
    internal sealed class Picture
    {
        public readonly int W, H, SubX, SubY, CW, CH;
        public readonly bool Rgb;
        public readonly byte[] C0, C1, C2, A;        // Y U V (U and V of CW x CH), or R G B

        public Picture(int w, int h, bool rgb, int subX = 1, int subY = 1)
        {
            W = w; H = h; Rgb = rgb; SubX = rgb ? 1 : subX; SubY = rgb ? 1 : subY;
            CW = (w + SubX - 1) / SubX; CH = (h + SubY - 1) / SubY;
            C0 = new byte[w * h]; C1 = new byte[CW * CH]; C2 = new byte[CW * CH]; A = new byte[w * h]; A.AsSpan().Fill(255);
        }

        /// <summary>A plane of another size, bilinearly (centred, truncated: Windows' video processor's).</summary>
        private static void Plane(byte[] s, int sw, int sh, byte[] d, int dw, int dh)
        {
            for (int y = 0; y < dh; y++)
            {
                double fy = Math.Max(0, (y + 0.5) * sh / dh - 0.5);
                int y0 = Math.Min((int)fy, sh - 1), y1 = Math.Min(y0 + 1, sh - 1);
                double wy = fy - y0;
                for (int x = 0; x < dw; x++)
                {
                    double fx = Math.Max(0, (x + 0.5) * sw / dw - 0.5);
                    int x0 = Math.Min((int)fx, sw - 1), x1 = Math.Min(x0 + 1, sw - 1);
                    double wx = fx - x0;
                    double v = (1 - wy) * ((1 - wx) * s[y0 * sw + x0] + wx * s[y0 * sw + x1]) + wy * ((1 - wx) * s[y1 * sw + x0] + wx * s[y1 * sw + x1]);
                    d[y * dw + x] = (byte)Math.Clamp((int)v, 0, 255);
                }
            }
        }

        /// <summary>The picture of another size: each plane at its own resolution.</summary>
        public Picture Scaled(int w, int h)
        {
            var p = new Picture(w, h, Rgb, SubX, SubY);
            Plane(C0, W, H, p.C0, w, h); Plane(A, W, H, p.A, w, h);
            Plane(C1, CW, CH, p.C1, p.CW, p.CH); Plane(C2, CW, CH, p.C2, p.CW, p.CH);
            return p;
        }

        /// <summary>The chroma at a pixel: of 4:2:x, an odd column's the rounded mean of the samples beside it (Windows'), rows repeated.</summary>
        public (int U, int V) Chroma(int x, int y)
        {
            int cy = Math.Min(y / SubY, CH - 1);
            if (SubX == 1) return (C1[cy * CW + Math.Min(x, CW - 1)], C2[cy * CW + Math.Min(x, CW - 1)]);
            int cx = Math.Min(x / 2, CW - 1);
            if ((x & 1) == 0) return (C1[cy * CW + cx], C2[cy * CW + cx]);
            int nx = Math.Min(cx + 1, CW - 1);
            return ((C1[cy * CW + cx] + C1[cy * CW + nx] + 1) >> 1, (C2[cy * CW + cx] + C2[cy * CW + nx] + 1) >> 1);
        }

        public Picture AsRgb()
        {
            if (Rgb) return this;
            var p = new Picture(W, H, true);
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    int i = y * W + x;
                    var (u, v) = Chroma(x, y);
                    int c = 298 * (C0[i] - 16), d = u - 128, e = v - 128;
                    p.C0[i] = Clamp((c + 409 * e + 128) >> 8);
                    p.C1[i] = Clamp((c - 100 * d - 208 * e + 128) >> 8);
                    p.C2[i] = Clamp((c + 516 * d + 128) >> 8);
                    p.A[i] = A[i];
                }
            return p;
        }

        /// <summary>Of RGB, YUV 4:4:4 (each pixel converted); of YUV, itself.</summary>
        public Picture AsYuv()
        {
            if (!Rgb) return this;
            var p = new Picture(W, H, false);
            for (int i = 0; i < W * H; i++)
            {
                int r = C0[i], g = C1[i], b = C2[i];
                p.C0[i] = Clamp(((66 * r + 129 * g + 25 * b + 128) >> 8) + 16);
                p.C1[i] = Clamp(((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128);
                p.C2[i] = Clamp(((112 * r - 94 * g - 18 * b + 128) >> 8) + 128);
                p.A[i] = A[i];
            }
            return p;
        }

        /// <summary>
        /// The chroma for an output of the subsampling: the same kept; less of it, the samples it covers averaged (rounded);
        /// more of it, repeated.
        /// </summary>
        public (byte U, byte V) ChromaFor(int ox, int oy, int sx, int sy)
        {
            if (sx == SubX && sy == SubY) return (C1[Math.Min(oy, CH - 1) * CW + Math.Min(ox, CW - 1)], C2[Math.Min(oy, CH - 1) * CW + Math.Min(ox, CW - 1)]);
            // the full-resolution pixels the output's sample covers, each to the source's sample
            int su = 0, sv = 0, n = 0;
            for (int dy = 0; dy < sy; dy++)
                for (int dx = 0; dx < sx; dx++)
                {
                    int x = Math.Min(ox * sx + dx, W - 1), y = Math.Min(oy * sy + dy, H - 1);
                    int ci = Math.Min(y / SubY, CH - 1) * CW + Math.Min(x / SubX, CW - 1);
                    su += C1[ci]; sv += C2[ci]; n++;
                }
            return ((byte)((su + n / 2) / n), (byte)((sv + n / 2) / n));
        }

        internal static byte Clamp(int v) => v <= 0 ? (byte)0 : v >= 255 ? (byte)255 : (byte)v;
    }

    /// <summary>The layouts of the video processor's subtypes: decoded to a picture, encoded from one.</summary>
    internal static class Pictures
    {
        private static Guid F(string s) => MediaFormats.FourCC(s);
        private static readonly Guid NV12 = F("NV12"), NV21 = F("NV21"), IYUV = F("IYUV"), I420 = F("I420"), YV12 = F("YV12"), NV11 = F("NV11"), I422 = F("I422"), I444 = F("I444");
        private static readonly Guid YUY2 = F("YUY2"), UYVY = F("UYVY"), YVYU = F("YVYU"), AYUV = F("AYUV"), P010 = F("P010"), P016 = F("P016");
        private static readonly Guid MF_MT_DEFAULT_STRIDE = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");

        public static bool IsRgb(Guid s) => s == MFVideoProcessor.RGB32 || s == MFVideoProcessor.ARGB32 || s == MFVideoProcessor.RGB24 || s == MFVideoProcessor.RGB565
            || s == MFVideoProcessor.RGB555 || s == MFVideoProcessor.ABGR32 || s == MFVideoProcessor.A2R10G10B10 || s == MFVideoProcessor.A16B16G16R16F;

        public static bool Reads(Guid s) => s == NV12 || s == NV21 || s == IYUV || s == I420 || s == YV12 || s == NV11 || s == I422 || s == I444 || s == YUY2 || s == UYVY
            || s == YVYU || s == AYUV || s == MFVideoProcessor.L8 || s == MFVideoProcessor.RGB32 || s == MFVideoProcessor.ARGB32 || s == MFVideoProcessor.RGB24
            || s == MFVideoProcessor.RGB565 || s == MFVideoProcessor.RGB555 || s == MFVideoProcessor.ABGR32 || s == P010 || s == P016;

        /// <summary>RGB is bottom-up where the type has no stride, or a negative one (Windows' video processor's default).</summary>
        public static bool BottomUp(MFMediaType t)
        {
            if (!IsRgb(t.Subtype)) return false;
            var s = t.Attributes.Get(MF_MT_DEFAULT_STRIDE);
            return s is not { } v || unchecked((int)(uint)v.U) < 0;
        }

        private static int RgbRow(int bytesPerPixel, int w) => (w * bytesPerPixel + 3) & ~3;

        public static int Size(Guid s, int w, int h)
        {
            if (s == NV12 || s == NV21 || s == IYUV || s == I420 || s == YV12 || s == NV11) return w * h * 3 / 2;
            if (s == P010 || s == P016) return w * h * 3;
            if (s == I422 || s == YUY2 || s == UYVY || s == YVYU || s == MFVideoProcessor.L16 || s == MFVideoProcessor.D16) return w * h * 2;
            if (s == I444) return w * h * 3;
            if (s == MFVideoProcessor.L8) return w * h;
            if (s == MFVideoProcessor.RGB24) return RgbRow(3, w) * h;
            if (s == MFVideoProcessor.RGB565 || s == MFVideoProcessor.RGB555) return RgbRow(2, w) * h;
            if (s == MFVideoProcessor.A16B16G16R16F) return w * h * 8;
            return w * h * 4;                                    // AYUV, RGB32, ARGB32, ABGR32, A2R10G10B10, and the 4-byte ones
        }

        public static Picture Decode(byte[] b, Guid s, int w, int h, bool bottomUp)
        {
            byte At(int i) => i < b.Length ? b[i] : (byte)0;
            int cw = (w + 1) / 2, ch = (h + 1) / 2;
            if (s == NV12 || s == NV21 || s == IYUV || s == I420 || s == YV12 || s == P010 || s == P016)
            {
                var p = new Picture(w, h, false, 2, 2);
                bool wide = s == P010 || s == P016;
                for (int i = 0; i < w * h; i++) p.C0[i] = wide ? At(i * 2 + 1) : At(i);
                int ys = w * h * (wide ? 2 : 1);
                for (int cy = 0; cy < p.CH; cy++)
                    for (int cx = 0; cx < p.CW; cx++)
                    {
                        byte u, v;
                        if (wide) { int o = ys + (cy * w + cx * 2) * 2; u = At(o + 1); v = At(o + 3); }
                        else if (s == NV12 || s == NV21) { int o = ys + cy * w + cx * 2; u = At(o); v = At(o + 1); if (s == NV21) (u, v) = (v, u); }
                        else { int uo = ys + (s == YV12 ? cw * ch : 0), vo = ys + (s == YV12 ? 0 : cw * ch); u = At(uo + cy * cw + cx); v = At(vo + cy * cw + cx); }
                        p.C1[cy * p.CW + cx] = u; p.C2[cy * p.CW + cx] = v;
                    }
                return p;
            }
            if (s == NV11)
            {
                var p = new Picture(w, h, false, 4, 1);
                for (int i = 0; i < w * h; i++) p.C0[i] = At(i);
                for (int y = 0; y < h; y++) for (int cx = 0; cx < p.CW; cx++) { int o = w * h + y * p.CW * 2 + cx * 2; p.C1[y * p.CW + cx] = At(o); p.C2[y * p.CW + cx] = At(o + 1); }
                return p;
            }
            if (s == I422 || s == I444)
            {
                var p = new Picture(w, h, false, s == I444 ? 1 : 2, 1);
                for (int i = 0; i < w * h; i++) p.C0[i] = At(i);
                for (int i = 0; i < p.CW * p.CH; i++) { p.C1[i] = At(w * h + i); p.C2[i] = At(w * h + p.CW * p.CH + i); }
                return p;
            }
            if (s == YUY2 || s == UYVY || s == YVYU)
            {
                var p = new Picture(w, h, false, 2, 1);
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        int o = y * w * 2 + (x / 2) * 4;
                        p.C0[y * w + x] = s == UYVY ? At(o + 1 + (x & 1) * 2) : At(o + (x & 1) * 2);
                        if ((x & 1) != 0) continue;
                        int ci = y * p.CW + x / 2;
                        if (s == UYVY) { p.C1[ci] = At(o); p.C2[ci] = At(o + 2); }
                        else { p.C1[ci] = At(o + (s == YVYU ? 3 : 1)); p.C2[ci] = At(o + (s == YVYU ? 1 : 3)); }
                    }
                return p;
            }
            if (s == AYUV)
            {
                var p = new Picture(w, h, false);
                for (int i = 0; i < w * h; i++) { p.C2[i] = At(i * 4); p.C1[i] = At(i * 4 + 1); p.C0[i] = At(i * 4 + 2); p.A[i] = At(i * 4 + 3); }
                return p;
            }
            if (s == MFVideoProcessor.L8)
            {
                var p = new Picture(w, h, false);
                for (int i = 0; i < w * h; i++) { p.C0[i] = At(i); p.C1[i] = 128; p.C2[i] = 128; }
                return p;
            }
            // RGB: rows top-down, or bottom-up
            var r = new Picture(w, h, true);
            int bpp = s == MFVideoProcessor.RGB24 ? 3 : s == MFVideoProcessor.RGB565 || s == MFVideoProcessor.RGB555 ? 2 : 4, row = RgbRow(bpp, w);
            for (int y = 0; y < h; y++)
            {
                int ry = bottomUp ? h - 1 - y : y;
                for (int x = 0; x < w; x++)
                {
                    int o = ry * row + x * bpp, i = y * w + x;
                    if (bpp == 2)
                    {
                        int v = At(o) | At(o + 1) << 8;
                        if (s == MFVideoProcessor.RGB565) { r.C0[i] = (byte)((v >> 11 & 31) * 255 / 31); r.C1[i] = (byte)((v >> 5 & 63) * 255 / 63); r.C2[i] = (byte)((v & 31) * 255 / 31); }
                        else { r.C0[i] = (byte)((v >> 10 & 31) * 255 / 31); r.C1[i] = (byte)((v >> 5 & 31) * 255 / 31); r.C2[i] = (byte)((v & 31) * 255 / 31); }
                    }
                    else if (s == MFVideoProcessor.ABGR32) { r.C0[i] = At(o); r.C1[i] = At(o + 1); r.C2[i] = At(o + 2); r.A[i] = At(o + 3); }
                    else { r.C2[i] = At(o); r.C1[i] = At(o + 1); r.C0[i] = At(o + 2); if (s == MFVideoProcessor.ARGB32) r.A[i] = At(o + 3); }
                }
            }
            return r;
        }

        /// <summary>The picture into the output's layout (converted to its kind first: the caller scales after an RGB output's conversion).</summary>
        public static byte[] Encode(Picture p, Guid s, bool bottomUp)
        {
            int w = p.W, h = p.H, cw = (w + 1) / 2, ch = (h + 1) / 2;
            var o = new byte[Size(s, w, h)];
            if (IsRgb(s))
            {
                var q = p.AsRgb();
                if (s == MFVideoProcessor.A16B16G16R16F)
                {
                    for (int y = 0; y < h; y++)
                        for (int x = 0; x < w; x++)
                        {
                            int i = y * w + x, at = ((bottomUp ? h - 1 - y : y) * w + x) * 8;
                            BitConverter.TryWriteBytes(o.AsSpan(at), (Half)(q.C0[i] / 255f)); BitConverter.TryWriteBytes(o.AsSpan(at + 2), (Half)(q.C1[i] / 255f));
                            BitConverter.TryWriteBytes(o.AsSpan(at + 4), (Half)(q.C2[i] / 255f)); BitConverter.TryWriteBytes(o.AsSpan(at + 6), (Half)(q.A[i] / 255f));
                        }
                    return o;
                }
                int bpp = s == MFVideoProcessor.RGB24 ? 3 : s == MFVideoProcessor.RGB565 || s == MFVideoProcessor.RGB555 ? 2 : 4, row = RgbRow(bpp, w);
                for (int y = 0; y < h; y++)
                {
                    int ry = bottomUp ? h - 1 - y : y;
                    for (int x = 0; x < w; x++)
                    {
                        int i = y * w + x, at = ry * row + x * bpp;
                        byte r = q.C0[i], g = q.C1[i], bl = q.C2[i], a = q.A[i];
                        if (bpp == 2)
                        {
                            int v = s == MFVideoProcessor.RGB565 ? (r >> 3) << 11 | (g >> 2) << 5 | bl >> 3 : (r >> 3) << 10 | (g >> 3) << 5 | bl >> 3;
                            o[at] = (byte)v; o[at + 1] = (byte)(v >> 8);
                        }
                        else if (s == MFVideoProcessor.ABGR32) { o[at] = r; o[at + 1] = g; o[at + 2] = bl; o[at + 3] = a; }
                        else if (s == MFVideoProcessor.A2R10G10B10)
                        {
                            uint v = (uint)(a >> 6) << 30 | (uint)(r << 2 | r >> 6) << 20 | (uint)(g << 2 | g >> 6) << 10 | (uint)(bl << 2 | bl >> 6);
                            BitConverter.TryWriteBytes(o.AsSpan(at), v);
                        }
                        else { o[at] = bl; o[at + 1] = g; o[at + 2] = r; if (bpp == 4) o[at + 3] = s == MFVideoProcessor.RGB32 ? (byte)255 : a; }
                    }
                }
                return o;
            }
            var yv = p.AsYuv();
            if (s == NV12 || s == NV21 || s == IYUV || s == I420 || s == YV12)
            {
                Array.Copy(yv.C0, o, w * h);
                for (int y = 0; y < ch; y++)
                    for (int x = 0; x < cw; x++)
                    {
                        var (u, v) = yv.ChromaFor(x, y, 2, 2);
                        if (s == NV12 || s == NV21) { int at = w * h + y * w + x * 2; o[at] = s == NV12 ? u : v; o[at + 1] = s == NV12 ? v : u; }
                        else { int uo = w * h + (s == YV12 ? cw * ch : 0), vo = w * h + (s == YV12 ? 0 : cw * ch); o[uo + y * cw + x] = u; o[vo + y * cw + x] = v; }
                    }
                return o;
            }
            if (s == P010 || s == P016 || s == MFVideoProcessor.L16 || s == MFVideoProcessor.D16)
            {
                for (int i = 0; i < w * h; i++) { o[i * 2 + 1] = yv.C0[i]; o[i * 2] = s == P010 ? (byte)0 : yv.C0[i]; }
                if (s == P010 || s == P016)
                    for (int y = 0; y < ch; y++)
                        for (int x = 0; x < cw; x++)
                        {
                            int at = w * h * 2 + (y * w + x * 2) * 2;
                            var (u, v) = yv.ChromaFor(x, y, 2, 2);
                            o[at + 1] = u; o[at + 3] = v;
                            if (s == P016) { o[at] = u; o[at + 2] = v; }
                        }
                return o;
            }
            if (s == YUY2)
            {
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < cw; x++)
                    {
                        int at = y * w * 2 + x * 4;
                        var (u, v) = yv.ChromaFor(x, y, 2, 1);
                        o[at] = yv.C0[y * w + 2 * x]; o[at + 1] = u; o[at + 2] = yv.C0[y * w + Math.Min(2 * x + 1, w - 1)]; o[at + 3] = v;
                    }
                return o;
            }
            if (s == AYUV)
            {
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        int i = y * w + x;
                        var (u, v) = yv.ChromaFor(x, y, 1, 1);
                        o[i * 4] = v; o[i * 4 + 1] = u; o[i * 4 + 2] = yv.C0[i]; o[i * 4 + 3] = yv.A[i];
                    }
                return o;
            }
            if (s == MFVideoProcessor.L8) Array.Copy(yv.C0, o, w * h);
            return o;
        }
    }
}
