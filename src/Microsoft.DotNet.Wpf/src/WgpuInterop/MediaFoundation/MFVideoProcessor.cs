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
// IMFVideoProcessorControl as Windows' applies it (MFVideoProcessorControlProbe, VideoProcessorControl.windows.txt): the
// input type's rotation, then the mirror, then the source rectangle of that upright picture; letterboxed into the
// destination rectangle (Windows' rounding, worked out in the output's orientation before the rotation and mirror), the
// letterbox of the border color, the rest of the output sample left as it was.
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

        private static readonly Guid MF_MT_VIDEO_ROTATION = new("c380465d-2271-428c-9b83-ecea3b4a85c1");

        private IntPtr _held;                         // the input sample, AddRef'd, until its picture is out

        // IMFVideoProcessorControl(2)'s: the input type's rotation applied (ROTATION_NORMAL) or not (ROTATION_NONE); the
        // mirror (1 horizontal, 2 vertical: others kept, of no effect); the rectangles (none: the whole frame); the border's
        // MFARGB (opaque black); the constriction; the rotation override (taken, of no effect: Windows' software path's)
        private int _rotation = 1, _mirror;
        private Rect? _source, _destination;
        private uint _border = 0xFF000000;
        private (int W, int H)? _constriction;
        private uint _rotationOverride;

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
            // (of a subsampled output, a frame of whole chroma samples)
            var (sx, sy) = Pictures.Subsampling(t.Subtype);
            if (w % sx != 0 || h % sy != 0) return MF.MF_E_INVALIDMEDIATYPE;
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
                int w = (int)ow, h = (int)oh, size = Pictures.Size(o.Subtype, w, h);
                byte[] bytes = Bytes(_held);
                bool inUp = Pictures.BottomUp(i), outUp = Pictures.BottomUp(o);
                int rot = _rotation == 0 ? 0 : i.Attributes.U32(MF_MT_VIDEO_ROTATION) is var r && (r == 90 || r == 180 || r == 270) ? (int)r : 0;
                int pw = rot % 180 == 0 ? (int)iw : (int)ih, ph = rot % 180 == 0 ? (int)ih : (int)iw;
                bool mirrored = _mirror == 1 || _mirror == 2, unknownMirror = _mirror is not (0 or 1 or 2);

                // the source rectangle, of the upright picture (the input rotated, mirrored), where it lies in it; the
                // destination's, clipped to the frame; of none, or of none left, the whole frames
                var src = _source is { } sr && sr.L >= 0 && sr.T >= 0 && sr.L < sr.R && sr.T < sr.B && sr.R <= pw && sr.B <= ph ? sr : new Rect(0, 0, pw, ph);
                var dst = _destination is { } dr ? dr.Clip(w, h) : new Rect(0, 0, w, h);
                if (dst.Empty) dst = new Rect(0, 0, w, h);
                long time = Time(_held) ?? 0, duration = Duration(_held) ?? 0;
                var attributes = new[] { (MF.MFSampleExtension_CleanPoint, MFValue.Of(1u)), (MFSampleExtension_Interlaced, MFValue.Of(0u)) };

                ulong inPar = i.Attributes.U64(MF.MF_MT_PIXEL_ASPECT_RATIO, MF.Pack(1, 1)), outPar = o.Attributes.U64(MF.MF_MT_PIXEL_ASPECT_RATIO, MF.Pack(1, 1));
                int hr;
                if (i.Subtype == o.Subtype && iw == ow && ih == oh && inUp == outUp && inPar == outPar && rot == 0 && _mirror == 0 && src == dst)
                {
                    // nothing to change but where: Windows' copies the frame whole (of the rectangles the same, the whole frame)
                    hr = WriteInPlace(sample, size, m => bytes.AsSpan(0, Math.Min(size, bytes.Length)).CopyTo(m), time, duration, attributes);
                }
                else
                {
                    var (sx, sy) = Pictures.Subsampling(o.Subtype);
                    dst = dst.Aligned(sx, sy);
                    var picture = Pictures.Decode(bytes, i.Subtype, (int)iw, (int)ih, inUp);
                    if (rot != 0) picture = picture.Rotated(rot);
                    if (mirrored) picture = picture.Mirrored(_mirror == 1);
                    if (src != new Rect(0, 0, pw, ph)) picture = picture.Cropped(src);
                    var (inN, inD) = MF.Unpack(inPar); var (outN, outD) = MF.Unpack(outPar);
                    var content = Letterbox(dst, picture.W, picture.H, (Math.Max(inN, 1), Math.Max(inD, 1)), (Math.Max(outN, 1), Math.Max(outD, 1)), rot, mirrored ? _mirror : 0, w, h).Aligned(sx, sy);

                    // to the output's kind, then the content's size (Windows' scales an RGB output's pixels, a YUV output's
                    // planes); of a constriction, through its size first
                    byte[] inside = Array.Empty<byte>();
                    if (!content.Empty)
                    {
                        picture = Pictures.IsRgb(o.Subtype) ? picture.AsRgb() : picture.AsYuv();
                        if (_constriction is { } c && c.W > 0 && c.H > 0 && (c.W < content.W || c.H < content.H)) picture = picture.Scaled(Math.Min(c.W, content.W), Math.Min(c.H, content.H));
                        if (picture.W != content.W || picture.H != content.H) picture = picture.Scaled(content.W, content.H);
                        // (of a mirror Windows' knows not, a picture of no alpha: of RGB32 too)
                        if (unknownMirror) picture.A.AsSpan().Clear();
                        inside = Pictures.Encode(picture, unknownMirror && o.Subtype == RGB32 ? ARGB32 : o.Subtype, outUp);
                    }
                    var bands = dst.Minus(content);
                    byte[]? border = bands.Count == 0 ? null
                        : Pictures.Encode(Picture.Uniform(w, h, _border), o.Subtype == RGB32 ? ARGB32 : o.Subtype, outUp);   // (the border's alpha, of RGB32 too)
                    hr = WriteInPlace(sample, size, m =>
                    {
                        Pictures.Copy(inside, content.W, content.H, new Rect(0, 0, content.W, content.H), m, w, h, content, o.Subtype, outUp);
                        foreach (var band in bands)
                        {
                            Pictures.Copy(border!, w, h, band, m, w, h, band, o.Subtype, outUp, Pictures.Planes.Luma);
                            // (Windows' writes an NV12 border's chroma half the destination's left further right)
                            Pictures.Copy(border!, w, h, band, m, w, h, band, o.Subtype, outUp, Pictures.Planes.Chroma, o.Subtype == NV12 ? dst.L / 2 : 0);
                        }
                    }, time, duration, attributes);
                }
                if (hr < 0) return hr;
                Com.Release(_held); _held = IntPtr.Zero;
                return MF.S_OK;
            }
            finally { i.Release(); o.Release(); }
        }

        private static readonly Guid NV12 = F("NV12");

        /// <summary>
        /// The rectangle of the picture (pw x ph, upright) in the destination, of the same shape (of the pixel aspect ratios):
        /// as Windows' letterboxes it (MulDiv's rounding, the rest halved down), in the output's orientation before the
        /// rotation and mirror, then rotated and mirrored (so of 90, the odd pixel left of the picture where unrotated it is
        /// right of it).
        /// </summary>
        private static Rect Letterbox(Rect dst, int pw, int ph, (uint N, uint D) inPar, (uint N, uint D) outPar, int rot, int mirror, int w, int h)
        {
            bool quarter = rot % 180 != 0;
            int uw = quarter ? h : w, uh = quarter ? w : h;
            var u = dst.Mirrored(mirror, w, h).Unrotated(rot, w, h);
            long sw = quarter ? ph : pw, sh = quarter ? pw : ph;
            var (on, od) = quarter ? (outPar.D, outPar.N) : (outPar.N, outPar.D);
            sw *= inPar.N * od; sh *= inPar.D * on;
            int dw = u.W, dh = u.H, cw, ch;
            if (MulDiv(sw, dh, sh) <= dw) { cw = (int)Math.Min(dw, MulDiv(dh, sw, sh)); ch = dh; }
            else { ch = (int)Math.Min(dh, MulDiv(dw, sh, sw)); cw = dw; }
            int l = u.L + (dw - cw) / 2, t = u.T + (dh - ch) / 2;
            return new Rect(l, t, l + cw, t + ch).Rotated(rot, uw, uh).Mirrored(mirror, w, h);
        }

        /// <summary>Win32's MulDiv: a b / c, rounded half away from zero.</summary>
        private static long MulDiv(long a, long b, long c) => c == 0 ? -1 : (2 * a * b + c) / (2 * c);

        protected override void Flush() { Com.Release(_held); _held = IntPtr.Zero; }
        protected override void Drain() { }

        // ---- IMFVideoProcessorControl(2), IMFRealTimeClientEx, IMFGetService ------------------------------------

        private static Vtable BuildControl()
        {
            var vt = new Vtable("IMFVideoProcessorControl2", 12, IID_IMFVideoProcessorControl, IID_IMFVideoProcessorControl2);
            vt.Set(3, (delegate* unmanaged<IntPtr, uint*, int>)&SetBorderColor);
            vt.Set(4, (delegate* unmanaged<IntPtr, int*, int>)&SetSourceRectangle);
            vt.Set(5, (delegate* unmanaged<IntPtr, int*, int>)&SetDestinationRectangle);
            vt.Set(6, (delegate* unmanaged<IntPtr, int, int>)&SetMirror);
            vt.Set(7, (delegate* unmanaged<IntPtr, int, int>)&SetRotation);
            vt.Set(8, (delegate* unmanaged<IntPtr, int*, int>)&SetConstrictionSize);
            vt.Set(9, (delegate* unmanaged<IntPtr, uint, int>)&SetRotationOverride);
            vt.Set(10, (delegate* unmanaged<IntPtr, int, int>)&EnableHardwareEffects);
            vt.Set(11, (delegate* unmanaged<IntPtr, uint*, int>)&GetSupportedHardwareEffects);
            return vt;
        }

        private static MFVideoProcessor? P(IntPtr self) => From<MFVideoProcessor>(self);
        // (of a null pointer, S_OK: a rectangle or constriction of none, the border color as it was)
        [UnmanagedCallersOnly] private static int SetBorderColor(IntPtr self, uint* c) { if (P(self) is { } p && c != null) p._border = *c; return MF.S_OK; }
        [UnmanagedCallersOnly] private static int SetSourceRectangle(IntPtr self, int* r) { if (P(self) is { } p) p._source = r == null ? null : new Rect(r[0], r[1], r[2], r[3]); return MF.S_OK; }
        [UnmanagedCallersOnly] private static int SetDestinationRectangle(IntPtr self, int* r) { if (P(self) is { } p) p._destination = r == null ? null : new Rect(r[0], r[1], r[2], r[3]); return MF.S_OK; }
        [UnmanagedCallersOnly] private static int SetMirror(IntPtr self, int m) { if (P(self) is { } p) p._mirror = m; return MF.S_OK; }
        [UnmanagedCallersOnly] private static int SetRotation(IntPtr self, int r) { if (P(self) is { } p) p._rotation = r; return MF.S_OK; }
        [UnmanagedCallersOnly] private static int SetConstrictionSize(IntPtr self, int* s) { if (P(self) is { } p) p._constriction = s == null ? null : (s[0], s[1]); return MF.S_OK; }
        [UnmanagedCallersOnly]
        private static int SetRotationOverride(IntPtr self, uint r)
        {
            if (r != 0 && r != 90 && r != 180 && r != 270) return MF.E_INVALIDARG;
            if (P(self) is { } p) p._rotationOverride = r;
            return MF.S_OK;
        }
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

    /// <summary>A rectangle of pixels, right and bottom exclusive (a RECT's).</summary>
    internal readonly record struct Rect(int L, int T, int R, int B)
    {
        public int W => R - L;
        public int H => B - T;
        public bool Empty => R <= L || B <= T;

        public Rect Clip(int w, int h) => new(Math.Clamp(L, 0, w), Math.Clamp(T, 0, h), Math.Clamp(R, 0, w), Math.Clamp(B, 0, h));

        /// <summary>To whole chroma samples: the left and top up, the right and bottom down.</summary>
        public Rect Aligned(int sx, int sy) => new((L + sx - 1) / sx * sx, (T + sy - 1) / sy * sy, R / sx * sx, B / sy * sy);

        /// <summary>A rectangle of a frame of w x h turned clockwise by the degrees (into a frame of h x w of 90 or 270).</summary>
        public Rect Rotated(int deg, int w, int h) => deg switch
        {
            90 => new(h - B, L, h - T, R),
            180 => new(w - R, h - B, w - L, h - T),
            270 => new(T, w - R, B, w - L),
            _ => this,
        };

        /// <summary>A rectangle of a frame of w x h mirrored left to right (1), top to bottom (2).</summary>
        public Rect Mirrored(int mirror, int w, int h) => mirror == 1 ? new(w - R, T, w - L, B) : mirror == 2 ? new(L, h - B, R, h - T) : this;

        /// <summary>A rectangle of an output of w x h back into its frame before the rotation.</summary>
        public Rect Unrotated(int deg, int w, int h) => deg switch
        {
            90 => new(T, w - R, B, w - L),
            180 => new(w - R, h - B, w - L, h - T),
            270 => new(h - B, L, h - T, R),
            _ => this,
        };

        /// <summary>What of this the inner leaves: the bands above and below it, left and right of it.</summary>
        public List<Rect> Minus(Rect inner)
        {
            var r = new List<Rect>();
            if (inner.Empty) { if (!Empty) r.Add(this); return r; }
            foreach (var b in new[] { new Rect(L, T, R, inner.T), new Rect(L, inner.B, R, B), new Rect(L, inner.T, inner.L, inner.B), new Rect(inner.R, inner.T, R, inner.B) })
                if (!b.Empty) r.Add(b);
            return r;
        }
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

        /// <summary>A picture of one color, of an MFARGB (blue, green, red, alpha from its low byte).</summary>
        public static Picture Uniform(int w, int h, uint argb)
        {
            var p = new Picture(w, h, true);
            p.C0.AsSpan().Fill((byte)(argb >> 16)); p.C1.AsSpan().Fill((byte)(argb >> 8)); p.C2.AsSpan().Fill((byte)argb); p.A.AsSpan().Fill((byte)(argb >> 24));
            return p;
        }

        /// <summary>The picture turned clockwise by 90, 180 or 270 degrees: each plane, the chroma at its own resolution.</summary>
        public Picture Rotated(int deg)
        {
            var p = deg == 180 ? new Picture(W, H, Rgb, SubX, SubY) : new Picture(H, W, Rgb, SubY, SubX);
            Turn(C0, W, H, p.C0, deg); Turn(A, W, H, p.A, deg); Turn(C1, CW, CH, p.C1, deg); Turn(C2, CW, CH, p.C2, deg);
            return p;
        }

        private static void Turn(byte[] s, int w, int h, byte[] d, int deg)
        {
            if (deg == 180) { for (int i = 0; i < w * h; i++) d[w * h - 1 - i] = s[i]; return; }
            for (int y = 0; y < w; y++)
                for (int x = 0; x < h; x++)
                    d[y * h + x] = deg == 90 ? s[(h - 1 - x) * w + y] : s[x * w + (w - 1 - y)];
        }

        /// <summary>The picture mirrored left to right, or top to bottom.</summary>
        public Picture Mirrored(bool horizontal)
        {
            var p = new Picture(W, H, Rgb, SubX, SubY);
            Flip(C0, W, H, p.C0, horizontal); Flip(A, W, H, p.A, horizontal); Flip(C1, CW, CH, p.C1, horizontal); Flip(C2, CW, CH, p.C2, horizontal);
            return p;
        }

        private static void Flip(byte[] s, int w, int h, byte[] d, bool horizontal)
        {
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    d[y * w + x] = horizontal ? s[y * w + (w - 1 - x)] : s[(h - 1 - y) * w + x];
        }

        /// <summary>The rectangle of the picture: of one not of whole chroma samples, its chroma first to each pixel.</summary>
        public Picture Cropped(Rect r)
        {
            var from = this;
            if (r.L % SubX != 0 || r.T % SubY != 0)
            {
                from = new Picture(W, H, Rgb);
                C0.CopyTo(from.C0, 0); A.CopyTo(from.A, 0);
                for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) { var (u, v) = Chroma(x, y); from.C1[y * W + x] = (byte)u; from.C2[y * W + x] = (byte)v; }
            }
            var p = new Picture(r.W, r.H, Rgb, from.SubX, from.SubY);
            for (int y = 0; y < r.H; y++)
            {
                Array.Copy(from.C0, (r.T + y) * W + r.L, p.C0, y * r.W, r.W);
                Array.Copy(from.A, (r.T + y) * W + r.L, p.A, y * r.W, r.W);
            }
            int cl = r.L / from.SubX, ct = r.T / from.SubY;
            for (int cy = 0; cy < p.CH; cy++)
                for (int cx = 0; cx < p.CW; cx++)
                {
                    int si = Math.Min(ct + cy, from.CH - 1) * from.CW + Math.Min(cl + cx, from.CW - 1);
                    p.C1[cy * p.CW + cx] = from.C1[si]; p.C2[cy * p.CW + cx] = from.C2[si];
                }
            return p;
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

        /// <summary>
        /// The color converter's YUV to RGB: chroma repeated; each component a sum of truncated terms (its lookup tables:
        /// 1.164383 (Y - 16), 2.017232 (U - 128), 1.596027 (V - 128), 0.391762 (U - 128), 0.812968 (V - 128)).
        /// </summary>
        public Picture AsRgbConverter()
        {
            if (Rgb) return this;
            var p = new Picture(W, H, true);
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    int i = y * W + x, ci = Math.Min(y / SubY, CH - 1) * CW + Math.Min(x / SubX, CW - 1);
                    int l = (int)Math.Floor(1.164383 * (C0[i] - 16)), d = C1[ci] - 128, e = C2[ci] - 128;
                    p.C0[i] = Clamp(l + (int)Math.Floor(1.596027 * e));
                    p.C1[i] = Clamp(l - (int)Math.Floor(0.391762 * d) - (int)Math.Floor(0.812968 * e));
                    p.C2[i] = Clamp(l + (int)Math.Floor(2.017232 * d));
                    p.A[i] = 0;
                }
            return p;
        }

        /// <summary>
        /// The color converter's RGB to YUV: BT.601 in 16-bit fixed point, rounded (fitted to Windows' ramps of each channel
        /// and its pictures: the luma's coefficients are those alone; the chroma's are one of the few sets all of Windows'
        /// pictures fit); the chroma of the top-left pixel of each block.
        /// </summary>
        public Picture AsYuvConverter(int subX, int subY)
        {
            if (!Rgb) return this;
            var p = new Picture(W, H, false, subX, subY);
            for (int i = 0; i < W * H; i++) p.C0[i] = Clamp(16 + ((16842 * C0[i] + 33030 * C1[i] + 6423 * C2[i] + 32768) >> 16));
            for (int cy = 0; cy < p.CH; cy++)
                for (int cx = 0; cx < p.CW; cx++)
                {
                    int i = Math.Min(cy * subY, H - 1) * W + Math.Min(cx * subX, W - 1);
                    int r = C0[i], g = C1[i], b = C2[i];
                    p.C1[cy * p.CW + cx] = Clamp(128 + ((-9700 * r - 19071 * g + 28770 * b + 32768) >> 16));
                    p.C2[cy * p.CW + cx] = Clamp(128 + ((28770 * r - 24116 * g - 4653 * b + 32768) >> 16));
                }
            return p;
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

        /// <summary>The output's chroma subsampling, across and down (of RGB and 4:4:4, 1 and 1).</summary>
        public static (int X, int Y) Subsampling(Guid s) =>
            s == NV12 || s == NV21 || s == IYUV || s == I420 || s == YV12 || s == P010 || s == P016 ? (2, 2)
            : s == YUY2 || s == UYVY || s == YVYU || s == I422 ? (2, 1) : s == NV11 ? (4, 1) : (1, 1);

        [Flags] public enum Planes { Luma = 1, Chroma = 2, All = 3 }

        /// <summary>
        /// The bytes of a rectangle of a frame of the subtype (w x h, of whole chroma samples), as (offset, length) runs: of
        /// RGB and the packed ones, all luma; of the planar, the luma's rows, then the chroma's (shifted right by so many
        /// samples, clipped to the frame).
        /// </summary>
        public static List<(int Offset, int Length)> Spans(Guid s, int w, int h, bool bottomUp, Rect r, Planes planes = Planes.All, int shift = 0)
        {
            var o = new List<(int, int)>();
            if (r.Empty) return o;
            int cw = (w + 1) / 2, ch = (h + 1) / 2;
            bool luma = (planes & Planes.Luma) != 0, chroma = (planes & Planes.Chroma) != 0;
            void Rows(int start, int stride, int bpp, bool up = false)
            {
                for (int y = r.T; y < r.B; y++) o.Add((start + (up ? h - 1 - y : y) * stride + r.L * bpp, r.W * bpp));
            }
            if (IsRgb(s))
            {
                int bpp = s == MFVideoProcessor.RGB24 ? 3 : s == MFVideoProcessor.RGB565 || s == MFVideoProcessor.RGB555 ? 2 : s == MFVideoProcessor.A16B16G16R16F ? 8 : 4;
                if (luma) Rows(0, s == MFVideoProcessor.A16B16G16R16F ? w * 8 : RgbRow(bpp, w), bpp, bottomUp);
                return o;
            }
            if (s == MFVideoProcessor.L8) { if (luma) Rows(0, w, 1); return o; }
            if (s == MFVideoProcessor.L16 || s == MFVideoProcessor.D16) { if (luma) Rows(0, w * 2, 2); return o; }
            if (s == AYUV) { if (luma) Rows(0, w * 4, 4); return o; }
            if (s == YUY2 || s == UYVY || s == YVYU) { if (luma) Rows(0, w * 2, 2); return o; }
            bool wide = s == P010 || s == P016;
            if (luma) Rows(0, w * (wide ? 2 : 1), wide ? 2 : 1);
            if (!chroma) return o;
            if (s == NV11)
            {
                int qw = (w + 3) / 4, ql = r.L / 4 + shift, qr = Math.Min(qw, (r.R + 3) / 4 + shift);
                if (ql < qr) for (int y = r.T; y < r.B; y++) o.Add((w * h + y * qw * 2 + ql * 2, (qr - ql) * 2));
                return o;
            }
            int cl = r.L / 2 + shift, cr = Math.Min(cw, (r.R + 1) / 2 + shift);
            if (cl >= cr) return o;
            for (int pass = 0; pass < (s == NV12 || s == NV21 || wide ? 1 : 2); pass++)
                for (int cy = r.T / 2; cy < (r.B + 1) / 2; cy++)
                {
                    if (wide) o.Add((w * h * 2 + (cy * w + cl * 2) * 2, (cr - cl) * 4));
                    else if (s == NV12 || s == NV21) o.Add((w * h + cy * w + cl * 2, (cr - cl) * 2));
                    else
                    {
                        bool v = pass == 1, first = s == YV12 ? v : !v;   // YV12: V's plane, then U's
                        o.Add((w * h + (first ? 0 : cw * ch) + cy * cw + cl, cr - cl));
                    }
                }
            return o;
        }

        /// <summary>A rectangle of one frame's bytes (fw x fh) into a rectangle of the same size of another's (tw x th).</summary>
        public static void Copy(byte[] from, int fw, int fh, Rect fr, Span<byte> to, int tw, int th, Rect tr, Guid s, bool bottomUp, Planes planes = Planes.All, int shift = 0)
        {
            var a = Spans(s, fw, fh, bottomUp, fr, planes, shift); var b = Spans(s, tw, th, bottomUp, tr, planes, shift);
            for (int k = 0; k < Math.Min(a.Count, b.Count); k++)
            {
                int n = Math.Min(Math.Min(a[k].Length, b[k].Length), Math.Min(from.Length - a[k].Offset, to.Length - b[k].Offset));
                if (n > 0 && a[k].Offset >= 0 && b[k].Offset >= 0) from.AsSpan(a[k].Offset, n).CopyTo(to.Slice(b[k].Offset, n));
            }
        }

        /// <summary>RGB is bottom-up where the type has no stride, or a negative one (Windows' video processor's default).</summary>
        public static bool BottomUp(MFMediaType t, bool byDefault = true)
        {
            if (!IsRgb(t.Subtype)) return false;
            var s = t.Attributes.Get(MF_MT_DEFAULT_STRIDE);
            return s is not { } v ? byDefault : unchecked((int)(uint)v.U) < 0;
        }

        /// <summary>The subtypes the encoder writes.</summary>
        public static bool Writes(Guid s) => s == NV12 || s == NV21 || s == IYUV || s == I420 || s == YV12 || s == NV11 || s == YUY2 || s == UYVY || s == YVYU || s == AYUV
            || s == MFVideoProcessor.L8 || s == P010 || s == P016 || s == MFVideoProcessor.L16 || s == MFVideoProcessor.D16 || IsRgb(s);

        /// <summary>A row's bytes of the subtype (MF_MT_DEFAULT_STRIDE's, top-down): of the planar ones the luma's.</summary>
        public static int Stride(Guid s, int w)
        {
            if (s == MFVideoProcessor.RGB24) return RgbRow(3, w);
            if (s == MFVideoProcessor.RGB565 || s == MFVideoProcessor.RGB555 || s == YUY2 || s == UYVY || s == YVYU) return RgbRow(2, w);
            if (s == MFVideoProcessor.RGB32 || s == MFVideoProcessor.ARGB32 || s == AYUV) return w * 4;
            return w;
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
        public static byte[] Encode(Picture p, Guid s, bool bottomUp, bool converter = false)
        {
            int w = p.W, h = p.H, cw = (w + 1) / 2, ch = (h + 1) / 2;
            var o = new byte[Size(s, w, h)];
            if (IsRgb(s))
            {
                var q = converter ? p.AsRgbConverter() : p.AsRgb();
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
                        else { o[at] = bl; o[at + 1] = g; o[at + 2] = r; if (bpp == 4) o[at + 3] = s == MFVideoProcessor.RGB32 && !converter ? (byte)255 : a; }
                    }
                }
                return o;
            }
            var yv = converter ? p.AsYuvConverter(s == AYUV ? 1 : s == NV11 ? 4 : 2, s == YUY2 || s == UYVY || s == YVYU || s == AYUV || s == NV11 ? 1 : 2) : p.AsYuv();
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
            if (s == UYVY || s == YVYU)
            {
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < cw; x++)
                    {
                        int at = y * w * 2 + x * 4;
                        var (u, v) = yv.ChromaFor(x, y, 2, 1);
                        byte y0 = yv.C0[y * w + 2 * x], y1 = yv.C0[y * w + Math.Min(2 * x + 1, w - 1)];
                        if (s == UYVY) { o[at] = u; o[at + 1] = y0; o[at + 2] = v; o[at + 3] = y1; }
                        else { o[at] = y0; o[at + 1] = v; o[at + 2] = y1; o[at + 3] = u; }
                    }
                return o;
            }
            if (s == NV11)
            {
                Array.Copy(yv.C0, o, w * h);
                int qw = (w + 3) / 4;
                for (int y = 0; y < h; y++) for (int x = 0; x < qw; x++) { var (u, v) = yv.ChromaFor(x, y, 4, 1); o[w * h + y * qw * 2 + x * 2] = u; o[w * h + y * qw * 2 + x * 2 + 1] = v; }
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
