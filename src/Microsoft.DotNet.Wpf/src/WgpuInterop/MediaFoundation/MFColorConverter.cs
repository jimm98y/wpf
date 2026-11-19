// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Windows' color converter DSP (CLSID_CColorConvertDMO) as an IMFTransform, as an application makes it by CLSID
// (MFVideoProcessorProbe, VideoProcessor.windows.txt): its 20 input and 16 output subtypes; before an input, each type of
// its major type, subtype and fixed, independent samples; once there is one, the outputs of the input's size, rate,
// aperture and aspect, of their stride and sample size; no attributes of its own (E_NOTIMPL); the streams' information
// MF_E_TRANSFORM_TYPE_NOT_SET before the types, aligned to 1; IPropertyStore (empty) and IMFGetService beside. Its own
// arithmetic, unlike the video processor's (Picture.AsRgbConverter, AsYuvConverter): truncated lookup terms to RGB, the
// chroma repeated, alpha 0; rounded from RGB, a block's top-left chroma; RGB of no stride top-down. It does not scale:
// an output of another size is refused (Windows' crashes the process at one). Its IMediaObject (the DMO) besides
// (MFColorConverterDmo.cs). Not here: the palette's RGB8, V216, V410, the 4:1:1 packings and YVU9 (listed, refused).
//

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Wpf.Interop.WebGpu.Direct3D;
using Microsoft.Wpf.Interop.WebGpu.MediaFoundation.Codecs;

namespace Microsoft.Wpf.Interop.WebGpu.MediaFoundation
{
    internal sealed unsafe partial class MFColorConverter : MFEncoder
    {
        public static readonly Guid Clsid = new("98230571-0087-4204-b020-3282538e57d3");
        private static readonly Guid IID_IPropertyStore = new("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), IID_IMFGetService = new("fa993888-4383-415a-a930-dd472a8cf6f7");
        private static readonly Guid MF_MT_DEFAULT_STRIDE = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6"), MF_MT_GEOMETRIC_APERTURE = new("66758743-7e5f-400d-980a-aa8596c85696");
        private static readonly Guid MF_MT_SAMPLE_SIZE = new("dad3ab78-1990-408b-bce2-eba673dacc10"), MF_MT_PALETTE = new("6d283f42-9846-4410-afd9-654d503b1a54");
        private static readonly Guid MF_MT_INTERLACE_MODE = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
        private static Guid F(string s) => MediaFormats.FourCC(s);
        private static readonly Guid RGB8 = new("e436eb7a-524f-11ce-9f53-0020af0ba770");
        private static readonly Guid V216 = F("V216"), V410 = F("V410");

        /// <summary>Windows' input subtypes, in its order; its outputs the first 16.</summary>
        internal static readonly Guid[] Inputs =
        {
            F("YV12"), F("YUY2"), F("UYVY"), F("AYUV"), F("NV12"), MFVideoProcessor.RGB32, MFVideoProcessor.RGB565, F("I420"), F("IYUV"), F("YVYU"),
            MFVideoProcessor.RGB24, MFVideoProcessor.RGB555, RGB8, V216, V410, F("NV11"), F("Y41P"), F("Y41T"), F("Y42T"), F("YVU9"),
        };
        private const int OutputCount = 16;

        /// <summary>Windows' halftone palette of RGB8's type (MF_MT_PALETTE, 256 RGBQUADs).</summary>
        private static readonly byte[] s_palette = System.Convert.FromHexString("00000000000080000080000000808000800000008000800080800000c0c0c000c0dcc000f0caa600010101000101330001016600010199000101cc000101fe00013301000133330001336600013399000133cc000133fe00016601000166330001666600016699000166cc000166fe00019901000199330001996600019999000199cc000199fe0001cc010001cc330001cc660001cc990001cccc0001ccfe0001fe010001fe330001fe660001fe990001fecc0001fefe00330101003301330033016600330199003301cc003301fe00333301003333330033336600333399003333cc003333fe00336601003366330033666600336699003366cc003366fe00339901003399330033996600339999003399cc003399fe0033cc010033cc330033cc660033cc990033cccc0033ccfe0033fe010033fe330033fe660033fe990033fecc0033fefe00660101006601330066016600660199006601cc006601fe00663301006633330066336600663399006633cc006633fe00666601006666330066666600666699006666cc006666fe00669901006699330066996600669999006699cc006699fe0066cc010066cc330066cc660066cc990066cccc0066ccfe0066fe010066fe330066fe660066fe990066fecc0066fefe00990101009901330099016600990199009901cc009901fe00993301009933330099336600993399009933cc009933fe00996601009966330099666600996699009966cc009966fe00999901009999330099996600999999009999cc009999fe0099cc010099cc330099cc660099cc990099cccc0099ccfe0099fe010099fe330099fe660099fe990099fecc0099fefe00cc010100cc013300cc016600cc019900cc01cc00cc01fe00cc330100cc333300cc336600cc339900cc33cc00cc33fe00cc660100cc663300cc666600cc669900cc66cc00cc66fe00cc990100cc993300cc996600cc999900cc99cc00cc99fe00cccc0100cccc3300cccc6600cccc9900cccccc00ccccfe00ccfe0100ccfe3300ccfe6600ccfe9900ccfecc00ccfefe00fe010100fe013300fe016600fe019900fe01cc00fe01fe00fe330100fe333300fe336600fe339900fe33cc00fe33fe00fe660100fe663300fe666600fe669900fe66cc00fe66fe00fe990100fe993300fe996600fe999900fe99cc00fe99fe00fecc0100fecc3300fecc6600fecc9900fecccc00feccfe00fefe0100fefe3300fefe6600fefe9900fefecc00fefefe00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000");

        private static readonly Vtable s_store = BuildStore(), s_service = BuildService(), s_dmo = BuildDmo();
        private IntPtr _held;

        public MFColorConverter() : base(new[] { s_store, s_service, s_dmo }) { }

        public override void Shutdown() { Com.Release(_held); _held = IntPtr.Zero; }
        protected override bool HasAttributes => false;
        protected override int StreamInfoWithoutTypes => MF.MF_E_TRANSFORM_TYPE_NOT_SET;
        protected override uint Alignment => 1;

        private static MFMediaType Of(Guid sub)
        {
            var t = new MFMediaType();
            t.Attributes.Set(MF.MF_MT_MAJOR_TYPE, MF.MFMediaType_Video);
            t.Attributes.Set(MF.MF_MT_SUBTYPE, sub);
            t.Attributes.Set(MF.MF_MT_ALL_SAMPLES_INDEPENDENT, 1u);
            t.Attributes.Set(MF.MF_MT_FIXED_SIZE_SAMPLES, 1u);
            return t;
        }

        protected override int InputAvailable(uint i, out MFMediaType? type)
        {
            type = i < Inputs.Length ? Of(Inputs[i]) : null;
            return type == null ? MF.MF_E_NO_MORE_TYPES : MF.S_OK;
        }

        /// <summary>An output of the input: its size, rate (as 10^7 over the time a frame), interlace and aspect; the subtype's stride and size.</summary>
        protected override int OutputAvailable(uint i, out MFMediaType? type)
        {
            type = null;
            if (i >= OutputCount) return MF.MF_E_NO_MORE_TYPES;
            var sub = Inputs[i];
            // once there is an input, RGB8 is given as Media Foundation's (D3DFMT_P8's) subtype, not DirectShow's
            if (In != IntPtr.Zero && sub == RGB8) sub = MFVideoProcessor.P8;
            var t = Of(sub);
            if (In != IntPtr.Zero)
            {
                var input = Read(In);
                try
                {
                    var (w, h) = input.FrameSize;
                    var (rn, rd) = MF.Unpack(input.Attributes.U64(MF.MF_MT_FRAME_RATE, MF.Pack(30, 1)));
                    var a = t.Attributes;
                    if (sub == MFVideoProcessor.P8) a.Set(MF_MT_PALETTE, MFValue.Of(s_palette));
                    if (sub != V216 && sub != V410) a.Set(MF_MT_DEFAULT_STRIDE, (uint)Pictures.Stride(sub, (int)w));
                    a.Set(MF.MF_MT_FRAME_RATE, MF.Pack(10_000_000, rn > 0 ? (uint)(10_000_000UL * rd / rn) : 333_333));
                    a.Set(MF.MF_MT_FRAME_SIZE, MF.Pack(w, h));
                    var area = new byte[16];
                    BitConverter.TryWriteBytes(area.AsSpan(8), (int)w); BitConverter.TryWriteBytes(area.AsSpan(12), (int)h);
                    a.Set(MF_MT_GEOMETRIC_APERTURE, MFValue.Of(area));
                    a.Set(MF_MT_INTERLACE_MODE, input.Attributes.U32(MF_MT_INTERLACE_MODE, 2));
                    a.Set(MF.MF_MT_PIXEL_ASPECT_RATIO, input.Attributes.U64(MF.MF_MT_PIXEL_ASPECT_RATIO, MF.Pack(1, 1)));
                    a.Set(MF_MT_SAMPLE_SIZE, (uint)Size(sub, (int)w, (int)h));
                }
                finally { input.Release(); }
            }
            type = t;
            return MF.S_OK;
        }

        private static int Size(Guid s, int w, int h) => s == RGB8 || s == MFVideoProcessor.P8 ? ((w + 3) & ~3) * h : s == V216 || s == V410 ? w * h * 4 : Pictures.Size(s, w, h);

        protected override int CheckInput(MFMediaType t, out List<(Guid, MFValue)> fill)
        {
            fill = new();
            if (t.Attributes.Guid(MF.MF_MT_MAJOR_TYPE) != MF.MFMediaType_Video || Array.IndexOf(Inputs, t.Subtype) < 0) return MF.MF_E_INVALIDMEDIATYPE;
            var (w, h) = t.FrameSize;
            if (w == 0 || h == 0) return MF.MF_E_INVALIDMEDIATYPE;
            if (!Pictures.Reads(t.Subtype)) { D3DLog.Once("ccnv-in:" + t.Subtype, $"Media Foundation: the color converter converts not from {t.Subtype}"); return MF.MF_E_INVALIDMEDIATYPE; }
            return MF.S_OK;
        }

        protected override int CheckOutput(MFMediaType t, bool test, out List<(Guid, MFValue)> fill)
        {
            fill = new();
            int at = Array.IndexOf(Inputs, t.Subtype);
            if (t.Attributes.Guid(MF.MF_MT_MAJOR_TYPE) != MF.MFMediaType_Video || at < 0 || at >= OutputCount) return MF.MF_E_INVALIDMEDIATYPE;
            var (w, h) = t.FrameSize;
            if (w == 0 || h == 0) return MF.MF_E_INVALIDMEDIATYPE;
            if (In != IntPtr.Zero) { var i = Read(In); var same = i.FrameSize == (w, h); i.Release(); if (!same) return MF.MF_E_INVALIDMEDIATYPE; }
            if (!Pictures.Writes(t.Subtype)) { D3DLog.Once("ccnv-out:" + t.Subtype, $"Media Foundation: the color converter converts not to {t.Subtype}"); return MF.MF_E_INVALIDMEDIATYPE; }
            return MF.S_OK;
        }

        protected override void TypesChanged() { Com.Release(_held); _held = IntPtr.Zero; }
        protected override uint InputSize() { if (In == IntPtr.Zero) return 0; var t = Read(In); try { var (w, h) = t.FrameSize; return (uint)Size(t.Subtype, (int)w, (int)h); } finally { t.Release(); } }
        protected override uint OutputSize() { if (Out == IntPtr.Zero) return 0; var t = Read(Out); try { var (w, h) = t.FrameSize; return (uint)Size(t.Subtype, (int)w, (int)h); } finally { t.Release(); } }

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
                var (w, h) = i.FrameSize;
                var picture = Pictures.Decode(Bytes(_held), i.Subtype, (int)w, (int)h, Pictures.BottomUp(i, byDefault: false));
                var bytes = Pictures.Encode(picture, o.Subtype, Pictures.BottomUp(o, byDefault: false), converter: true);
                int hr = Write(sample, bytes, Time(_held) ?? 0, Duration(_held) ?? 0, new[] { (MF.MFSampleExtension_CleanPoint, MFValue.Of(0u)) });
                if (hr < 0) return hr;
                Com.Release(_held); _held = IntPtr.Zero;
                return MF.S_OK;
            }
            finally { i.Release(); o.Release(); }
        }

        protected override void Flush() { Com.Release(_held); _held = IntPtr.Zero; }
        protected override void Drain() { }

        // ---- IPropertyStore (of no properties), IMFGetService ------------------------------------------------

        private static Vtable BuildStore()
        {
            var vt = new Vtable("IPropertyStore", 8, IID_IPropertyStore);
            vt.Set(3, (delegate* unmanaged<IntPtr, uint*, int>)&GetCount);
            vt.Set(4, (delegate* unmanaged<IntPtr, uint, byte*, int>)&GetAt);
            vt.Set(5, (delegate* unmanaged<IntPtr, byte*, byte*, int>)&GetValue);
            vt.Set(6, (delegate* unmanaged<IntPtr, byte*, byte*, int>)&SetValue);
            vt.Set(7, (delegate* unmanaged<IntPtr, int>)&Commit);
            return vt;
        }

        [UnmanagedCallersOnly] private static int GetCount(IntPtr self, uint* n) { if (n == null) return MF.E_POINTER; *n = 0; return MF.S_OK; }
        [UnmanagedCallersOnly] private static int GetAt(IntPtr self, uint i, byte* key) => MF.E_INVALIDARG;
        [UnmanagedCallersOnly] private static int GetValue(IntPtr self, byte* key, byte* pv) { if (pv == null) return MF.E_POINTER; new Span<byte>(pv, 16).Clear(); return MF.S_OK; }
        [UnmanagedCallersOnly] private static int SetValue(IntPtr self, byte* key, byte* pv) => MF.S_OK;
        [UnmanagedCallersOnly] private static int Commit(IntPtr self) => MF.S_OK;

        private static Vtable BuildService()
        {
            var vt = new Vtable("IMFGetService", 4, IID_IMFGetService);
            vt.Set(3, (delegate* unmanaged<IntPtr, Guid*, Guid*, IntPtr*, int>)&GetService);
            return vt;
        }

        [UnmanagedCallersOnly] private static int GetService(IntPtr self, Guid* service, Guid* riid, IntPtr* ppv) { if (ppv != null) *ppv = IntPtr.Zero; return unchecked((int)0xC00D36BA); }   // MF_E_UNSUPPORTED_SERVICE
    }
}
