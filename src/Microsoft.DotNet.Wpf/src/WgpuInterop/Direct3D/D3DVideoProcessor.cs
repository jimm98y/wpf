// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The D3D11 video processor on WebGPU (Documentation/d3d11-webgpu-shim.md, section 8): enumerator,
// processor, input and output views, and ID3D11VideoContext's processor half -- what an app or Media
// Foundation uses to turn decoded 4:2:0 frames (NV12, P010, P016) into RGB.
//
// VideoProcessorBlt is one render pass of the shim's own: over the output's target rectangle the
// background colour, then over the stream's destination rectangle the source rectangle, rotated as the
// stream says, sampled bilinearly (luma at its texels, chroma centred between them both ways, which the
// hardware's video processor matches best) and converted by the stream's colour space (BT.601 / BT.709 matrix -- BT.2020
// through ID3D11VideoContext1's DXGI colour spaces -- limited or full range) into the output's (full or
// limited range RGB). A planar texture is stored as its bytes lie in memory (D3DTexture2D.PlaneRows):
// the luma rows, then the interleaved chroma rows.
//

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using static Microsoft.Wpf.Interop.WebGpu.Wgpu;

namespace Microsoft.Wpf.Interop.WebGpu.Direct3D
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct D3D11_VIDEO_PROCESSOR_CONTENT_DESC
    {
        public uint InputFrameFormat;
        public uint InputFrameRateNumerator, InputFrameRateDenominator;
        public uint InputWidth, InputHeight;
        public uint OutputFrameRateNumerator, OutputFrameRateDenominator;
        public uint OutputWidth, OutputHeight;
        public uint Usage;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct D3D11_VIDEO_PROCESSOR_CAPS
    {
        public uint DeviceCaps, FeatureCaps, FilterCaps, InputFormatCaps, AutoStreamCaps, StereoCaps, RateConversionCapsCount, MaxInputStreams, MaxStreamStates;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct D3D11_VIDEO_PROCESSOR_RATE_CONVERSION_CAPS
    {
        public uint PastFrames, FutureFrames, ProcessorCaps, ITelecineCaps, CustomRateCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC
    {
        public uint FourCC, ViewDimension, MipSlice, ArraySlice;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC
    {
        public uint ViewDimension, MipSlice, FirstArraySlice, ArraySize;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct D3D11_VIDEO_PROCESSOR_STREAM
    {
        public int Enable;
        public uint OutputIndex, InputFrameOrField, PastFrames, FutureFrames;
        public IntPtr* ppPastSurfaces;
        public IntPtr pInputSurface;
        public IntPtr* ppFutureSurfaces;
        public IntPtr* ppPastSurfacesRight;
        public IntPtr pInputSurfaceRight;
        public IntPtr* ppFutureSurfacesRight;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct D3D11_RECT_V { public int left, top, right, bottom; }

    /// <summary>A stream's or the output's colour: the matrix, the range, as the video processor is told.</summary>
    internal struct VideoColorSpace
    {
        /// <summary>Kr and Kb of the YCbCr matrix.</summary>
        public double Kr, Kb;
        /// <summary>YCbCr in 16-235 (else 0-255); RGB out in 16-235 (else 0-255).</summary>
        public bool LimitedYuv, LimitedRgb;

        public static readonly VideoColorSpace Default = new() { Kr = 0.299, Kb = 0.114, LimitedYuv = true };

        /// <summary>From D3D11_VIDEO_PROCESSOR_COLOR_SPACE's bits: Usage 0, RGB_Range 1, YCbCr_Matrix 2,
        /// YCbCr_xvYCC 3, Nominal_Range 4-5 (0 undefined, 1 16-235, 2 0-255).</summary>
        public static VideoColorSpace FromBits(uint bits)
        {
            bool bt709 = (bits & 4) != 0;
            uint nominal = (bits >> 4) & 3;
            return new VideoColorSpace
            {
                Kr = bt709 ? 0.2126 : 0.299, Kb = bt709 ? 0.0722 : 0.114,
                LimitedYuv = nominal != 2,
                LimitedRgb = (bits & 2) != 0,
            };
        }

        public uint ToBits() => (Kr == 0.2126 ? 4u : 0u) | (LimitedRgb ? 2u : 0u) | ((LimitedYuv ? 1u : 2u) << 4);

        /// <summary>From a DXGI_COLOR_SPACE_TYPE (ID3D11VideoContext1).</summary>
        public static VideoColorSpace FromDxgi(uint cs) => cs switch
        {
            0 => new() { Kr = 0.2126, Kb = 0.0722 },                       // RGB_FULL_G22_NONE_P709
            1 => new() { Kr = 0.2126, Kb = 0.0722 },                       // RGB_FULL_G10_NONE_P709
            2 => new() { Kr = 0.2126, Kb = 0.0722, LimitedRgb = true },    // RGB_STUDIO_G22_NONE_P709
            3 => new() { Kr = 0.2126, Kb = 0.0722, LimitedRgb = true },    // RGB_STUDIO_G22_NONE_P2020
            5 => new() { Kr = 0.299, Kb = 0.114 },                         // YCBCR_FULL_G22_NONE_P709_X601
            6 => new() { Kr = 0.299, Kb = 0.114, LimitedYuv = true },      // YCBCR_STUDIO_G22_LEFT_P601
            7 => new() { Kr = 0.299, Kb = 0.114 },                         // YCBCR_FULL_G22_LEFT_P601
            8 => new() { Kr = 0.2126, Kb = 0.0722, LimitedYuv = true },    // YCBCR_STUDIO_G22_LEFT_P709
            9 => new() { Kr = 0.2126, Kb = 0.0722 },                       // YCBCR_FULL_G22_LEFT_P709
            10 or 14 or 16 or 18 => new() { Kr = 0.2627, Kb = 0.0593, LimitedYuv = true },   // STUDIO ... P2020
            11 or 17 => new() { Kr = 0.2627, Kb = 0.0593 },                // FULL ... P2020
            12 or 13 or 15 => new() { Kr = 0.2627, Kb = 0.0593 },          // RGB 2020 (FULL_G2084, STUDIO_G2084 ...)
            _ => Default,
        };
    }

    /// <summary>ID3D11VideoProcessorEnumerator: the content it was made for.</summary>
    internal sealed unsafe class D3DVideoProcessorEnumerator : D3DChild
    {
        private static readonly Vtable s_vt = Build();
        public readonly D3D11_VIDEO_PROCESSOR_CONTENT_DESC Content;

        public D3DVideoProcessorEnumerator(D3DDevice device, D3D11_VIDEO_PROCESSOR_CONTENT_DESC content) : base(device, s_vt) { Content = content; }

        private static Vtable Build()
        {
            var vt = new Vtable("ID3D11VideoProcessorEnumerator", Vtbl.ID3D11VideoProcessorEnumerator.SlotCount, Vtbl.ID3D11VideoProcessorEnumerator.IID, Vtbl.ID3D11DeviceChild.IID);
            FillDeviceChild(vt);
            vt.Set(Vtbl.ID3D11VideoProcessorEnumerator.GetVideoProcessorContentDesc, (delegate* unmanaged<IntPtr, D3D11_VIDEO_PROCESSOR_CONTENT_DESC*, int>)&GetContentDesc);
            vt.Set(Vtbl.ID3D11VideoProcessorEnumerator.CheckVideoProcessorFormat, (delegate* unmanaged<IntPtr, DXGI_FORMAT, uint*, int>)&CheckFormat);
            vt.Set(Vtbl.ID3D11VideoProcessorEnumerator.GetVideoProcessorCaps, (delegate* unmanaged<IntPtr, D3D11_VIDEO_PROCESSOR_CAPS*, int>)&GetCaps);
            vt.Set(Vtbl.ID3D11VideoProcessorEnumerator.GetVideoProcessorRateConversionCaps, (delegate* unmanaged<IntPtr, uint, D3D11_VIDEO_PROCESSOR_RATE_CONVERSION_CAPS*, int>)&GetRateCaps);
            vt.Set(Vtbl.ID3D11VideoProcessorEnumerator.GetVideoProcessorCustomRate, (delegate* unmanaged<IntPtr, uint, uint, void*, int>)&GetCustomRate);
            vt.Set(Vtbl.ID3D11VideoProcessorEnumerator.GetVideoProcessorFilterRange, (delegate* unmanaged<IntPtr, uint, void*, int>)&GetFilterRange);
            return vt;
        }

        /// <summary>The formats the processor reads (D3D11_VIDEO_PROCESSOR_FORMAT_SUPPORT_INPUT 1) and writes (OUTPUT 2).</summary>
        public static uint Support(DXGI_FORMAT f) => f switch
        {
            Formats.NV12 or Formats.P010 or Formats.P016 => 1,
            DXGI_FORMAT.B8G8R8A8_UNORM or DXGI_FORMAT.R8G8B8A8_UNORM or DXGI_FORMAT.B8G8R8X8_UNORM or DXGI_FORMAT.R10G10B10A2_UNORM or DXGI_FORMAT.R16G16B16A16_FLOAT
                or DXGI_FORMAT.B8G8R8A8_UNORM_SRGB or DXGI_FORMAT.R8G8B8A8_UNORM_SRGB => 2,
            _ => 0,
        };

        [UnmanagedCallersOnly]
        private static int GetContentDesc(IntPtr self, D3D11_VIDEO_PROCESSOR_CONTENT_DESC* d)
        {
            var e = From<D3DVideoProcessorEnumerator>(self);
            if (e == null || d == null) return HResult.E_INVALIDARG;
            *d = e.Content;
            return HResult.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int CheckFormat(IntPtr self, DXGI_FORMAT f, uint* flags)
        {
            if (flags == null) return HResult.E_INVALIDARG;
            *flags = Support(f);
            return HResult.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int GetCaps(IntPtr self, D3D11_VIDEO_PROCESSOR_CAPS* c)
        {
            if (c == null) return HResult.E_INVALIDARG;
            // alpha fill 0x1, rotation 0x400; one stream, one rate conversion, no filters, no stereo
            *c = new D3D11_VIDEO_PROCESSOR_CAPS { FeatureCaps = 0x1 | 0x400, RateConversionCapsCount = 1, MaxInputStreams = 1, MaxStreamStates = 1 };
            return HResult.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int GetRateCaps(IntPtr self, uint index, D3D11_VIDEO_PROCESSOR_RATE_CONVERSION_CAPS* c)
        {
            if (c == null || index != 0) return HResult.E_INVALIDARG;
            *c = default;            // progressive frames only, no deinterlacing, no inverse telecine
            return HResult.S_OK;
        }

        [UnmanagedCallersOnly] private static int GetCustomRate(IntPtr self, uint a, uint b, void* rate) => HResult.E_INVALIDARG;
        [UnmanagedCallersOnly] private static int GetFilterRange(IntPtr self, uint filter, void* range) => HResult.E_INVALIDARG;
    }

    /// <summary>ID3D11VideoProcessor: its output's and its stream's state, as the video context sets them.</summary>
    internal sealed unsafe class D3DVideoProcessor : D3DChild
    {
        private static readonly Vtable s_vt = Build();
        public readonly D3D11_VIDEO_PROCESSOR_CONTENT_DESC Content;

        public bool TargetRectSet;
        public D3D11_RECT_V TargetRect;
        public bool BackgroundYCbCr;
        public float BgR, BgG, BgB, BgA = 1f;
        public VideoColorSpace OutputColor = new() { Kr = 0.299, Kb = 0.114 };
        public uint OutputColorBits;
        public uint AlphaFillMode, AlphaFillStream;

        public bool SourceRectSet, DestRectSet;
        public D3D11_RECT_V SourceRect, DestRect;
        public VideoColorSpace StreamColor = VideoColorSpace.Default;
        public uint StreamColorBits = 1u << 4;
        public uint FrameFormat, Rotation;
        public bool RotationSet;
        public float StreamAlpha = 1f;
        public bool StreamAlphaSet;

        public D3DVideoProcessor(D3DDevice device, D3D11_VIDEO_PROCESSOR_CONTENT_DESC content) : base(device, s_vt) { Content = content; }

        private static Vtable Build()
        {
            var vt = new Vtable("ID3D11VideoProcessor", Vtbl.ID3D11VideoProcessor.SlotCount, Vtbl.ID3D11VideoProcessor.IID, Vtbl.ID3D11DeviceChild.IID);
            FillDeviceChild(vt);
            vt.Set(Vtbl.ID3D11VideoProcessor.GetContentDesc, (delegate* unmanaged<IntPtr, D3D11_VIDEO_PROCESSOR_CONTENT_DESC*, void>)&GetContentDesc);
            vt.Set(Vtbl.ID3D11VideoProcessor.GetRateConversionCaps, (delegate* unmanaged<IntPtr, D3D11_VIDEO_PROCESSOR_RATE_CONVERSION_CAPS*, void>)&GetRateCaps);
            return vt;
        }

        [UnmanagedCallersOnly]
        private static void GetContentDesc(IntPtr self, D3D11_VIDEO_PROCESSOR_CONTENT_DESC* d)
        {
            var p = From<D3DVideoProcessor>(self);
            if (p != null && d != null) *d = p.Content;
        }

        [UnmanagedCallersOnly]
        private static void GetRateCaps(IntPtr self, D3D11_VIDEO_PROCESSOR_RATE_CONVERSION_CAPS* c) { if (c != null) *c = default; }
    }

    /// <summary>ID3D11VideoProcessorInputView / OutputView: a texture (a slice of it) to read or write.</summary>
    internal sealed unsafe class D3DVideoView : D3DChild
    {
        private static readonly Vtable s_in = Build(true), s_out = Build(false);
        public readonly D3DTexture2D Texture;
        public readonly bool Input;
        public readonly uint Mip, Slice;
        public readonly D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC InDesc;
        public readonly D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC OutDesc;

        public D3DVideoView(D3DDevice device, D3DTexture2D texture, D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC d) : base(device, s_in)
        {
            Texture = texture; Input = true; InDesc = d; Mip = d.MipSlice; Slice = d.ArraySlice;
            texture.InternalAddRef();
        }

        public D3DVideoView(D3DDevice device, D3DTexture2D texture, D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC d) : base(device, s_out)
        {
            Texture = texture; Input = false; OutDesc = d; Mip = d.MipSlice; Slice = d.ViewDimension == 2 ? d.FirstArraySlice : 0;
            texture.InternalAddRef();
        }

        protected override void OnFinalRelease()
        {
            Texture.InternalRelease();
            base.OnFinalRelease();
        }

        private static Vtable Build(bool input)
        {
            var vt = input
                ? new Vtable("ID3D11VideoProcessorInputView", Vtbl.ID3D11VideoProcessorInputView.SlotCount, Vtbl.ID3D11VideoProcessorInputView.IID, Vtbl.ID3D11View.IID, Vtbl.ID3D11DeviceChild.IID)
                : new Vtable("ID3D11VideoProcessorOutputView", Vtbl.ID3D11VideoProcessorOutputView.SlotCount, Vtbl.ID3D11VideoProcessorOutputView.IID, Vtbl.ID3D11View.IID, Vtbl.ID3D11DeviceChild.IID);
            FillDeviceChild(vt);
            vt.Set(7, (delegate* unmanaged<IntPtr, IntPtr*, void>)&GetResource);
            vt.Set(8, (delegate* unmanaged<IntPtr, void*, void>)&GetDesc);
            return vt;
        }

        [UnmanagedCallersOnly]
        private static void GetResource(IntPtr self, IntPtr* resource)
        {
            if (resource == null) return;
            var v = From<D3DVideoView>(self);
            *resource = v == null ? IntPtr.Zero : v.Texture.AddRefPointer();
        }

        [UnmanagedCallersOnly]
        private static void GetDesc(IntPtr self, void* desc)
        {
            var v = From<D3DVideoView>(self);
            if (v == null || desc == null) return;
            if (v.Input) *(D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC*)desc = v.InDesc;
            else *(D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC*)desc = v.OutDesc;
        }
    }

    internal sealed unsafe partial class D3DDevice
    {
        static partial void FillVideoProcessorFactory(Vtable vt)
        {
            vt.Set(Vtbl.ID3D11VideoDevice.CreateVideoProcessorEnumerator, (delegate* unmanaged<IntPtr, D3D11_VIDEO_PROCESSOR_CONTENT_DESC*, IntPtr*, int>)&VdCreateEnumerator);
            vt.Set(Vtbl.ID3D11VideoDevice.CreateVideoProcessor, (delegate* unmanaged<IntPtr, IntPtr, uint, IntPtr*, int>)&VdCreateProcessor);
            vt.Set(Vtbl.ID3D11VideoDevice.CreateVideoProcessorInputView, (delegate* unmanaged<IntPtr, IntPtr, IntPtr, D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC*, IntPtr*, int>)&VdCreateInputView);
            vt.Set(Vtbl.ID3D11VideoDevice.CreateVideoProcessorOutputView, (delegate* unmanaged<IntPtr, IntPtr, IntPtr, D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC*, IntPtr*, int>)&VdCreateOutputView);
        }

        [UnmanagedCallersOnly]
        private static int VdCreateEnumerator(IntPtr self, D3D11_VIDEO_PROCESSOR_CONTENT_DESC* desc, IntPtr* e)
        {
            var d = Self(self);
            if (d == null || desc == null) return HResult.E_INVALIDARG;
            if (desc->InputWidth == 0 || desc->InputHeight == 0 || desc->OutputWidth == 0 || desc->OutputHeight == 0) return HResult.E_INVALIDARG;
            if (desc->InputFrameFormat != 0) D3DLog.Once("vpinterlaced", $"video processor for interlaced input (frame format {desc->InputFrameFormat}): fields are not weaved, frames are shown as they are");
            return ComObject.Out(e, new D3DVideoProcessorEnumerator(d, *desc));
        }

        [UnmanagedCallersOnly]
        private static int VdCreateProcessor(IntPtr self, IntPtr enumerator, uint rateIndex, IntPtr* p)
        {
            var d = Self(self);
            var e = From<D3DVideoProcessorEnumerator>(enumerator);
            if (d == null || e == null || rateIndex != 0) return HResult.E_INVALIDARG;
            return ComObject.Out(p, new D3DVideoProcessor(d, e.Content));
        }

        [UnmanagedCallersOnly]
        private static int VdCreateInputView(IntPtr self, IntPtr resource, IntPtr enumerator, D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC* desc, IntPtr* view)
        {
            if (view != null) *view = IntPtr.Zero;
            var d = Self(self);
            var t = From<D3DTexture2D>(resource);
            if (d == null || t == null || desc == null || desc->ViewDimension != 1) return HResult.E_INVALIDARG;
            if ((D3DVideoProcessorEnumerator.Support(t.Desc.Format) & 1) == 0)
            {
                D3DLog.Once("vpin:" + t.Desc.Format, $"video processor input of format {t.Desc.Format}: the shim's video processor reads NV12, P010 and P016");
                return HResult.E_INVALIDARG;
            }
            if (desc->MipSlice >= t.MipLevels || desc->ArraySlice >= t.Desc.ArraySize) return HResult.E_INVALIDARG;
            return ComObject.Out(view, new D3DVideoView(d, t, *desc));
        }

        [UnmanagedCallersOnly]
        private static int VdCreateOutputView(IntPtr self, IntPtr resource, IntPtr enumerator, D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC* desc, IntPtr* view)
        {
            if (view != null) *view = IntPtr.Zero;
            var d = Self(self);
            var t = From<D3DTexture2D>(resource);
            if (d == null || t == null || desc == null || desc->ViewDimension is not (1 or 2)) return HResult.E_INVALIDARG;
            if ((D3DVideoProcessorEnumerator.Support(t.Desc.Format) & 2) == 0 || (t.Desc.BindFlags & D3D11_BIND.RENDER_TARGET) == 0)
            {
                D3DLog.Once("vpout:" + t.Desc.Format, $"video processor output of format {t.Desc.Format} (bind {t.Desc.BindFlags}): it writes an RGB render target");
                return HResult.E_INVALIDARG;
            }
            return ComObject.Out(view, new D3DVideoView(d, t, *desc));
        }
    }

    internal sealed unsafe partial class D3DContext
    {
        private static readonly Vtable s_videoContext = BuildVideoContext();

        private static Vtable BuildVideoContext()
        {
            var vt = new Vtable("ID3D11VideoContext", Vtbl.ID3D11VideoContext.SlotCount, Vtbl.ID3D11VideoContext.IID);
            FillDeviceChild(vt);
            vt.Set(Vtbl.ID3D11VideoContext.GetDecoderBuffer, (delegate* unmanaged<IntPtr, IntPtr, uint, uint*, void**, int>)&VcNoDecoderBuffer);
            vt.Set(Vtbl.ID3D11VideoContext.VideoProcessorSetOutputTargetRect, (delegate* unmanaged<IntPtr, IntPtr, int, D3D11_RECT_V*, void>)&VcSetOutputTargetRect);
            vt.Set(Vtbl.ID3D11VideoContext.VideoProcessorSetOutputBackgroundColor, (delegate* unmanaged<IntPtr, IntPtr, int, float*, void>)&VcSetOutputBackground);
            vt.Set(Vtbl.ID3D11VideoContext.VideoProcessorSetOutputColorSpace, (delegate* unmanaged<IntPtr, IntPtr, uint*, void>)&VcSetOutputColorSpace);
            vt.Set(Vtbl.ID3D11VideoContext.VideoProcessorSetOutputAlphaFillMode, (delegate* unmanaged<IntPtr, IntPtr, uint, uint, void>)&VcSetAlphaFill);
            vt.Set(Vtbl.ID3D11VideoContext.VideoProcessorGetOutputTargetRect, (delegate* unmanaged<IntPtr, IntPtr, int*, D3D11_RECT_V*, void>)&VcGetOutputTargetRect);
            vt.Set(Vtbl.ID3D11VideoContext.VideoProcessorGetOutputBackgroundColor, (delegate* unmanaged<IntPtr, IntPtr, int*, float*, void>)&VcGetOutputBackground);
            vt.Set(Vtbl.ID3D11VideoContext.VideoProcessorGetOutputColorSpace, (delegate* unmanaged<IntPtr, IntPtr, uint*, void>)&VcGetOutputColorSpace);
            vt.Set(Vtbl.ID3D11VideoContext.VideoProcessorGetOutputAlphaFillMode, (delegate* unmanaged<IntPtr, IntPtr, uint*, uint*, void>)&VcGetAlphaFill);
            vt.Set(Vtbl.ID3D11VideoContext.VideoProcessorSetOutputExtension, (delegate* unmanaged<IntPtr, IntPtr, Guid*, uint, void*, int>)&VcExtension);
            vt.Set(Vtbl.ID3D11VideoContext.VideoProcessorGetOutputExtension, (delegate* unmanaged<IntPtr, IntPtr, Guid*, uint, void*, int>)&VcExtension);
            vt.Set(Vtbl.ID3D11VideoContext.VideoProcessorSetStreamFrameFormat, (delegate* unmanaged<IntPtr, IntPtr, uint, uint, void>)&VcSetFrameFormat);
            vt.Set(Vtbl.ID3D11VideoContext.VideoProcessorSetStreamColorSpace, (delegate* unmanaged<IntPtr, IntPtr, uint, uint*, void>)&VcSetStreamColorSpace);
            vt.Set(Vtbl.ID3D11VideoContext.VideoProcessorSetStreamSourceRect, (delegate* unmanaged<IntPtr, IntPtr, uint, int, D3D11_RECT_V*, void>)&VcSetSourceRect);
            vt.Set(Vtbl.ID3D11VideoContext.VideoProcessorSetStreamDestRect, (delegate* unmanaged<IntPtr, IntPtr, uint, int, D3D11_RECT_V*, void>)&VcSetDestRect);
            vt.Set(Vtbl.ID3D11VideoContext.VideoProcessorSetStreamAlpha, (delegate* unmanaged<IntPtr, IntPtr, uint, int, float, void>)&VcSetStreamAlpha);
            vt.Set(Vtbl.ID3D11VideoContext.VideoProcessorSetStreamExtension, (delegate* unmanaged<IntPtr, IntPtr, uint, Guid*, uint, void*, int>)&VcStreamExtension);
            vt.Set(Vtbl.ID3D11VideoContext.VideoProcessorGetStreamExtension, (delegate* unmanaged<IntPtr, IntPtr, uint, Guid*, uint, void*, int>)&VcStreamExtension);
            vt.Set(Vtbl.ID3D11VideoContext.VideoProcessorGetStreamFrameFormat, (delegate* unmanaged<IntPtr, IntPtr, uint, uint*, void>)&VcGetFrameFormat);
            vt.Set(Vtbl.ID3D11VideoContext.VideoProcessorGetStreamColorSpace, (delegate* unmanaged<IntPtr, IntPtr, uint, uint*, void>)&VcGetStreamColorSpace);
            vt.Set(Vtbl.ID3D11VideoContext.VideoProcessorGetStreamSourceRect, (delegate* unmanaged<IntPtr, IntPtr, uint, int*, D3D11_RECT_V*, void>)&VcGetSourceRect);
            vt.Set(Vtbl.ID3D11VideoContext.VideoProcessorGetStreamDestRect, (delegate* unmanaged<IntPtr, IntPtr, uint, int*, D3D11_RECT_V*, void>)&VcGetDestRect);
            vt.Set(Vtbl.ID3D11VideoContext.VideoProcessorGetStreamAlpha, (delegate* unmanaged<IntPtr, IntPtr, uint, int*, float*, void>)&VcGetStreamAlpha);
            vt.Set(Vtbl.ID3D11VideoContext.VideoProcessorBlt, (delegate* unmanaged<IntPtr, IntPtr, IntPtr, uint, uint, D3D11_VIDEO_PROCESSOR_STREAM*, int>)&VcBlt);
            vt.Set(Vtbl.ID3D11VideoContext.VideoProcessorSetStreamRotation, (delegate* unmanaged<IntPtr, IntPtr, uint, int, uint, void>)&VcSetRotation);
            vt.Set(Vtbl.ID3D11VideoContext.VideoProcessorGetStreamRotation, (delegate* unmanaged<IntPtr, IntPtr, uint, int*, uint*, void>)&VcGetRotation);
            return vt;
        }

        private static D3DVideoProcessor? Vp(IntPtr p) => From<D3DVideoProcessor>(p);

        [UnmanagedCallersOnly]
        private static int VcNoDecoderBuffer(IntPtr self, IntPtr decoder, uint type, uint* size, void** buffer) { if (buffer != null) *buffer = null; return HResult.E_INVALIDARG; }

        [UnmanagedCallersOnly]
        private static void VcSetOutputTargetRect(IntPtr self, IntPtr p, int enable, D3D11_RECT_V* r)
        {
            if (Vp(p) is not { } v) return;
            v.TargetRectSet = enable != 0 && r != null;
            if (v.TargetRectSet) v.TargetRect = *r;
        }

        [UnmanagedCallersOnly]
        private static void VcSetOutputBackground(IntPtr self, IntPtr p, int ycbcr, float* c)
        {
            if (Vp(p) is not { } v || c == null) return;
            v.BackgroundYCbCr = ycbcr != 0;
            v.BgR = c[0]; v.BgG = c[1]; v.BgB = c[2]; v.BgA = c[3];
        }

        [UnmanagedCallersOnly]
        private static void VcSetOutputColorSpace(IntPtr self, IntPtr p, uint* cs)
        {
            if (Vp(p) is not { } v || cs == null) return;
            v.OutputColorBits = *cs;
            v.OutputColor = VideoColorSpace.FromBits(*cs);
        }

        [UnmanagedCallersOnly]
        private static void VcSetAlphaFill(IntPtr self, IntPtr p, uint mode, uint stream)
        {
            if (Vp(p) is not { } v) return;
            v.AlphaFillMode = mode; v.AlphaFillStream = stream;
        }

        [UnmanagedCallersOnly]
        private static void VcGetOutputTargetRect(IntPtr self, IntPtr p, int* enabled, D3D11_RECT_V* r)
        {
            if (Vp(p) is not { } v) return;
            if (enabled != null) *enabled = v.TargetRectSet ? 1 : 0;
            if (r != null) *r = v.TargetRect;
        }

        [UnmanagedCallersOnly]
        private static void VcGetOutputBackground(IntPtr self, IntPtr p, int* ycbcr, float* c)
        {
            if (Vp(p) is not { } v) return;
            if (ycbcr != null) *ycbcr = v.BackgroundYCbCr ? 1 : 0;
            if (c != null) { c[0] = v.BgR; c[1] = v.BgG; c[2] = v.BgB; c[3] = v.BgA; }
        }

        [UnmanagedCallersOnly]
        private static void VcGetOutputColorSpace(IntPtr self, IntPtr p, uint* cs) { if (Vp(p) is { } v && cs != null) *cs = v.OutputColorBits; }

        [UnmanagedCallersOnly]
        private static void VcGetAlphaFill(IntPtr self, IntPtr p, uint* mode, uint* stream)
        {
            if (Vp(p) is not { } v) return;
            if (mode != null) *mode = v.AlphaFillMode;
            if (stream != null) *stream = v.AlphaFillStream;
        }

        [UnmanagedCallersOnly] private static int VcExtension(IntPtr self, IntPtr p, Guid* g, uint size, void* data) => HResult.E_NOTIMPL;
        [UnmanagedCallersOnly] private static int VcStreamExtension(IntPtr self, IntPtr p, uint s, Guid* g, uint size, void* data) => HResult.E_NOTIMPL;

        [UnmanagedCallersOnly]
        private static void VcSetFrameFormat(IntPtr self, IntPtr p, uint stream, uint format)
        {
            if (Vp(p) is not { } v || stream != 0) return;
            v.FrameFormat = format;
            if (format != 0) D3DLog.Once("vpfield", $"video processor stream frame format {format}: fields are not weaved, frames are shown as they are");
        }

        [UnmanagedCallersOnly]
        private static void VcSetStreamColorSpace(IntPtr self, IntPtr p, uint stream, uint* cs)
        {
            if (Vp(p) is not { } v || stream != 0 || cs == null) return;
            v.StreamColorBits = *cs;
            v.StreamColor = VideoColorSpace.FromBits(*cs);
        }

        [UnmanagedCallersOnly]
        private static void VcSetSourceRect(IntPtr self, IntPtr p, uint stream, int enable, D3D11_RECT_V* r)
        {
            if (Vp(p) is not { } v || stream != 0) return;
            v.SourceRectSet = enable != 0 && r != null;
            if (v.SourceRectSet) v.SourceRect = *r;
        }

        [UnmanagedCallersOnly]
        private static void VcSetDestRect(IntPtr self, IntPtr p, uint stream, int enable, D3D11_RECT_V* r)
        {
            if (Vp(p) is not { } v || stream != 0) return;
            v.DestRectSet = enable != 0 && r != null;
            if (v.DestRectSet) v.DestRect = *r;
        }

        [UnmanagedCallersOnly]
        private static void VcSetStreamAlpha(IntPtr self, IntPtr p, uint stream, int enable, float alpha)
        {
            if (Vp(p) is not { } v || stream != 0) return;
            v.StreamAlphaSet = enable != 0;
            v.StreamAlpha = alpha;
        }

        [UnmanagedCallersOnly]
        private static void VcGetFrameFormat(IntPtr self, IntPtr p, uint stream, uint* f) { if (Vp(p) is { } v && f != null) *f = v.FrameFormat; }

        [UnmanagedCallersOnly]
        private static void VcGetStreamColorSpace(IntPtr self, IntPtr p, uint stream, uint* cs) { if (Vp(p) is { } v && cs != null) *cs = v.StreamColorBits; }

        [UnmanagedCallersOnly]
        private static void VcGetSourceRect(IntPtr self, IntPtr p, uint stream, int* enabled, D3D11_RECT_V* r)
        {
            if (Vp(p) is not { } v) return;
            if (enabled != null) *enabled = v.SourceRectSet ? 1 : 0;
            if (r != null) *r = v.SourceRect;
        }

        [UnmanagedCallersOnly]
        private static void VcGetDestRect(IntPtr self, IntPtr p, uint stream, int* enabled, D3D11_RECT_V* r)
        {
            if (Vp(p) is not { } v) return;
            if (enabled != null) *enabled = v.DestRectSet ? 1 : 0;
            if (r != null) *r = v.DestRect;
        }

        [UnmanagedCallersOnly]
        private static void VcGetStreamAlpha(IntPtr self, IntPtr p, uint stream, int* enabled, float* alpha)
        {
            if (Vp(p) is not { } v) return;
            if (enabled != null) *enabled = v.StreamAlphaSet ? 1 : 0;
            if (alpha != null) *alpha = v.StreamAlpha;
        }

        [UnmanagedCallersOnly]
        private static void VcSetRotation(IntPtr self, IntPtr p, uint stream, int enable, uint rotation)
        {
            if (Vp(p) is not { } v || stream != 0) return;
            v.RotationSet = enable != 0;
            v.Rotation = enable != 0 ? rotation & 3 : 0;
        }

        [UnmanagedCallersOnly]
        private static void VcGetRotation(IntPtr self, IntPtr p, uint stream, int* enabled, uint* rotation)
        {
            if (Vp(p) is not { } v) return;
            if (enabled != null) *enabled = v.RotationSet ? 1 : 0;
            if (rotation != null) *rotation = v.Rotation;
        }

        [UnmanagedCallersOnly]
        private static int VcBlt(IntPtr self, IntPtr processor, IntPtr output, uint frame, uint count, D3D11_VIDEO_PROCESSOR_STREAM* streams)
        {
            var c = From<D3DContext>(self);
            var p = Vp(processor);
            var o = From<D3DVideoView>(output);
            if (c == null || p == null || o == null || o.Input) return HResult.E_INVALIDARG;
            if (count > 1) D3DLog.Once("vpstreams", $"VideoProcessorBlt of {count} streams: the shim's video processor draws the first");
            D3DVideoView? input = null;
            if (count > 0 && streams != null && streams[0].Enable != 0)
            {
                input = From<D3DVideoView>(streams[0].pInputSurface);
                if (input == null || !input.Input) return HResult.E_INVALIDARG;
                if (streams[0].PastFrames != 0 || streams[0].FutureFrames != 0) D3DLog.Once("vpref", "VideoProcessorBlt with reference frames: no deinterlacing, the current frame alone");
            }
            int hr = HResult.S_OK;
            try { c.Run(x => hr = x.VideoBlt(p, o, input), p, o, input); }
            catch (Exception e) { D3DLog.Error("VideoProcessorBlt", e); return HResult.E_FAIL; }
            return hr;
        }

        // ---- the pass ----------------------------------------------------------------------------------

        private readonly Dictionary<(bool Uint, WGPUTextureFormat Out), (IntPtr Layout, IntPtr Pipeline)> _videoPipelines = new();

        /// <summary>The matrix of codes to RGB, rows of (Y, Cb, Cr, 1), for a stream of <paramref name="bits"/>-bit codes.</summary>
        internal static float[] VideoMatrix(VideoColorSpace stream, VideoColorSpace output, int bits)
        {
            double s = 1 << (bits - 8), max = (1 << bits) - 1, mid = 1 << (bits - 1);
            // codes to y in 0..1, cb/cr in -0.5..0.5
            double ys, yo, cs, co;
            if (stream.LimitedYuv) { ys = 1 / (219 * s); yo = -16 * s / (219 * s); cs = 1 / (224 * s); co = -128 * s / (224 * s); }
            else { ys = 1 / max; yo = 0; cs = 1 / max; co = -mid / max; }
            double kr = stream.Kr, kb = stream.Kb, kg = 1 - kr - kb;
            // R = y + 2(1-kr) cr; G = y - 2 kb (1-kb)/kg cb - 2 kr (1-kr)/kg cr; B = y + 2(1-kb) cb
            double[,] m =
            {
                { 1, 0, 2 * (1 - kr) },
                { 1, -2 * kb * (1 - kb) / kg, -2 * kr * (1 - kr) / kg },
                { 1, 2 * (1 - kb), 0 },
            };
            double outScale = output.LimitedRgb ? 219.0 / 255 : 1, outOffset = output.LimitedRgb ? 16.0 / 255 : 0;
            var r = new float[12];
            for (int row = 0; row < 3; row++)
            {
                double a = m[row, 0] * ys, b = m[row, 1] * cs, c = m[row, 2] * cs;
                double off = m[row, 0] * yo + (m[row, 1] + m[row, 2]) * co;
                r[row * 4 + 0] = (float)(a * outScale);
                r[row * 4 + 1] = (float)(b * outScale);
                r[row * 4 + 2] = (float)(c * outScale);
                r[row * 4 + 3] = (float)(off * outScale + outOffset);
            }
            return r;
        }

        private const string VideoWgsl = @"
struct P {
    src: vec4<f32>,      // the source rectangle, in luma texels: left, top, right, bottom
    dst: vec4<f32>,      // the destination rectangle, in output pixels
    m0: vec4<f32>, m1: vec4<f32>, m2: vec4<f32>,   // codes (Y, Cb, Cr, 1) to RGB
    bg: vec4<f32>,       // the background
    info: vec4<u32>,     // luma width, luma height, rotation, 1 to fill the background
    codes: vec4<f32>,    // what one unit of a texel is in codes (255 for NV12; 1/64 for P010, 1 for P016)
};
@group(0) @binding(0) var tex: texture_2d<TEXEL>;
@group(0) @binding(1) var<uniform> p: P;

@vertex
fn vs(@builtin(vertex_index) i: u32) -> @builtin(position) vec4<f32> {
    let x = f32((i << 1u) & 2u) * 2.0 - 1.0;
    let y = f32(i & 2u) * 2.0 - 1.0;
    return vec4<f32>(x, y, 0.0, 1.0);
}

fn code(x: i32, y: i32) -> f32 {
    return f32(textureLoad(tex, vec2<i32>(x, y), 0).r) * p.codes.x;
}

// the source rectangle bounds the samples (the hardware's processor reads nothing outside it)
fn lo() -> vec2<i32> { return max(vec2<i32>(p.src.xy), vec2<i32>(0, 0)); }
fn hi() -> vec2<i32> { return min(vec2<i32>(ceil(p.src.zw)), vec2<i32>(i32(p.info.x), i32(p.info.y))) - vec2<i32>(1, 1); }

fn luma(u: vec2<f32>) -> f32 {
    let a = lo(); let z = hi();
    let b = floor(u); let f = u - b;
    let x0 = clamp(i32(b.x), a.x, z.x); let x1 = clamp(i32(b.x) + 1, a.x, z.x);
    let y0 = clamp(i32(b.y), a.y, z.y); let y1 = clamp(i32(b.y) + 1, a.y, z.y);
    return mix(mix(code(x0, y0), code(x1, y0), f.x), mix(code(x0, y1), code(x1, y1), f.x), f.y);
}

fn chromaAt(i: i32, j: i32) -> vec2<f32> {
    let a = lo() / 2; let z = hi() / 2;
    let x = clamp(i, a.x, z.x) * 2; let y = i32(p.info.y) + clamp(j, a.y, z.y);
    return vec2<f32>(code(x, y), code(x + 1, y));
}

fn chroma(c: vec2<f32>) -> vec2<f32> {
    let b = floor(c); let f = c - b;
    let i = i32(b.x); let j = i32(b.y);
    return mix(mix(chromaAt(i, j), chromaAt(i + 1, j), f.x), mix(chromaAt(i, j + 1), chromaAt(i + 1, j + 1), f.x), f.y);
}

@fragment
fn fs(@builtin(position) pos: vec4<f32>) -> @location(0) vec4<f32> {
    if (p.info.w == 1u) { return p.bg; }
    var t = (pos.xy - p.dst.xy) / (p.dst.zw - p.dst.xy);
    // the stream turned clockwise as it is shown: where in the source this pixel is
    switch (p.info.z) {
        case 1u: { t = vec2<f32>(t.y, 1.0 - t.x); }
        case 2u: { t = vec2<f32>(1.0 - t.x, 1.0 - t.y); }
        case 3u: { t = vec2<f32>(1.0 - t.y, t.x); }
        default: { }
    }
    let l = p.src.xy + t * (p.src.zw - p.src.xy) - vec2<f32>(0.5, 0.5);
    let y = luma(l);
    // 4:2:0 chroma, centred between the luma texels both ways (what the hardware's video processor
    // matches best, D3DVideoProcessorTests)
    let cc = (l - vec2<f32>(0.5, 0.5)) * 0.5;
    let cbcr = chroma(cc);
    let v = vec4<f32>(y, cbcr.x, cbcr.y, 1.0);
    return vec4<f32>(clamp(vec3<f32>(dot(p.m0, v), dot(p.m1, v), dot(p.m2, v)), vec3<f32>(0.0), vec3<f32>(1.0)), 1.0);
}
";

        private (IntPtr Layout, IntPtr Pipeline) VideoPipeline(bool uintTexels, WGPUTextureFormat outFormat)
        {
            if (_videoPipelines.TryGetValue((uintTexels, outFormat), out var known)) return known;
            IntPtr module = CreateModule(VideoWgsl.Replace("TEXEL", uintTexels ? "u32" : "f32"), "video-processor", out string? err);
            if (module == IntPtr.Zero) throw new InvalidOperationException("video processor shader: " + err);
            var entries = stackalloc WGPUBindGroupLayoutEntry[2];
            entries[0] = new WGPUBindGroupLayoutEntry
            {
                binding = 0, visibility = WGPUShaderStage_Fragment,
                texture = new WGPUTextureBindingLayout { sampleType = uintTexels ? WGPUTextureSampleType.Uint : WGPUTextureSampleType.UnfilterableFloat, viewDimension = WGPUTextureViewDimension._2D },
            };
            entries[1] = new WGPUBindGroupLayoutEntry { binding = 1, visibility = WGPUShaderStage_Fragment, buffer = new WGPUBufferBindingLayout { type = WGPUBufferBindingType.Uniform, minBindingSize = 160 } };
            var ld = new WGPUBindGroupLayoutDescriptor { entryCount = 2, entries = entries };
            IntPtr layout = wgpuDeviceCreateBindGroupLayout(Device.Gpu.Device, &ld);
            var pld = new WGPUPipelineLayoutDescriptor { bindGroupLayoutCount = 1, bindGroupLayouts = &layout };
            IntPtr pl = wgpuDeviceCreatePipelineLayout(Device.Gpu.Device, &pld);
            byte[] vsName = Encoding.UTF8.GetBytes("vs"), fsName = Encoding.UTF8.GetBytes("fs");
            IntPtr pipeline;
            fixed (byte* vn = vsName)
            fixed (byte* fn = fsName)
            {
                var target = new WGPUColorTargetState { format = outFormat, writeMask = 0xF };
                var frag = new WGPUFragmentState { module = module, entryPoint = new WGPUStringView { data = fn, length = 2 }, targetCount = 1, targets = &target };
                var desc = new WGPURenderPipelineDescriptor
                {
                    layout = pl,
                    vertex = new WGPUVertexState { module = module, entryPoint = new WGPUStringView { data = vn, length = 2 } },
                    primitive = new WGPUPrimitiveState { topology = WGPUPrimitiveTopology.TriangleList, frontFace = WGPUFrontFace.CCW, cullMode = WGPUCullMode.None },
                    multisample = new WGPUMultisampleState { count = 1, mask = 0xFFFFFFFF },
                    fragment = &frag,
                };
                wgpuDevicePushErrorScope(Device.Gpu.Device, WGPUErrorFilter.Validation);
                pipeline = PopScope(Device.Gpu, wgpuDeviceCreateRenderPipeline(Device.Gpu.Device, &desc), out err);
            }
            if (pipeline == IntPtr.Zero) throw new InvalidOperationException("video processor pipeline: " + err);
            return _videoPipelines[(uintTexels, outFormat)] = (layout, pipeline);
        }

        private int VideoBlt(D3DVideoProcessor p, D3DVideoView output, D3DVideoView? input)
        {
            var ot = output.Texture;
            uint ow = Math.Max(1, ot.Desc.Width >> (int)output.Mip), oh = Math.Max(1, ot.Desc.Height >> (int)output.Mip);
            // the target rectangle: the whole output unless set
            var target = p.TargetRectSet ? p.TargetRect : new D3D11_RECT_V { right = (int)ow, bottom = (int)oh };
            target.left = Math.Clamp(target.left, 0, (int)ow); target.right = Math.Clamp(target.right, 0, (int)ow);
            target.top = Math.Clamp(target.top, 0, (int)oh); target.bottom = Math.Clamp(target.bottom, 0, (int)oh);
            if (target.right <= target.left || target.bottom <= target.top) return HResult.S_OK;

            ResolvePendingClears();
            EndPass();
            IntPtr view = D3DView.CreateTextureView(ot.Gpu, ot.Format, WGPUTextureViewDimension._2D, output.Mip, 1, output.Slice, 1);
            var att = new WGPURenderPassColorAttachment { view = view, depthSlice = 0xFFFFFFFF, loadOp = WGPULoadOp.Load, storeOp = WGPUStoreOp.Store };
            var rp = new WGPURenderPassDescriptor { colorAttachmentCount = 1, colorAttachments = &att };

            bool uintTexels = input != null && input.Texture.Format == TexFmt.R16Uint;
            var (layout, pipeline) = VideoPipeline(uintTexels, ot.Format);
            var groups = new List<IntPtr>();
            var views = new List<IntPtr> { view };
            IntPtr pass = wgpuCommandEncoderBeginRenderPass(Encoder(), &rp);
            try
            {
                wgpuRenderPassEncoderSetPipeline(pass, pipeline);
                // the background, where the stream does not cover the target (or everywhere, no stream)
                var dst = p.DestRectSet ? p.DestRect : target;
                bool covered = input != null && dst.left <= target.left && dst.top <= target.top && dst.right >= target.right && dst.bottom >= target.bottom;
                if (!covered)
                {
                    float r = p.BgR, g = p.BgG, b = p.BgB;
                    if (p.BackgroundYCbCr)
                    {
                        // a background given as YCbCr in 0..1 (Y, Cb, Cr): through the output's matrix, of full range
                        var m = VideoMatrix(new VideoColorSpace { Kr = p.OutputColor.Kr, Kb = p.OutputColor.Kb }, p.OutputColor, 8);
                        float y = p.BgR * 255, cb = p.BgG * 255, cr = p.BgB * 255;
                        r = m[0] * y + m[1] * cb + m[2] * cr + m[3]; g = m[4] * y + m[5] * cb + m[6] * cr + m[7]; b = m[8] * y + m[9] * cb + m[10] * cr + m[11];
                    }
                    IntPtr g0 = BltGroup(layout, input, ParamsFor(p, input, target, target, new[] { r, g, b, p.AlphaFillMode == 0 ? 1f : p.BgA }, fill: true), views, uintTexels);
                    groups.Add(g0);
                    Scissor(pass, target);
                    wgpuRenderPassEncoderSetBindGroup(pass, 0, g0, 0, null);
                    wgpuRenderPassEncoderDraw(pass, 3, 1, 0, 0);
                }
                if (input != null)
                {
                    var clip = new D3D11_RECT_V
                    {
                        left = Math.Max(dst.left, target.left), top = Math.Max(dst.top, target.top),
                        right = Math.Min(dst.right, target.right), bottom = Math.Min(dst.bottom, target.bottom),
                    };
                    if (clip.right > clip.left && clip.bottom > clip.top && dst.right > dst.left && dst.bottom > dst.top)
                    {
                        IntPtr g1 = BltGroup(layout, input, ParamsFor(p, input, dst, target, null, fill: false), views, uintTexels);
                        groups.Add(g1);
                        Scissor(pass, clip);
                        wgpuRenderPassEncoderSetBindGroup(pass, 0, g1, 0, null);
                        wgpuRenderPassEncoderDraw(pass, 3, 1, 0, 0);
                    }
                }
            }
            finally
            {
                wgpuRenderPassEncoderEnd(pass);
                wgpuRenderPassEncoderRelease(pass);
                foreach (IntPtr g in groups) wgpuBindGroupRelease(g);
                foreach (IntPtr v in views) wgpuTextureViewRelease(v);
            }
            ot.Version++;
            return HResult.S_OK;
        }

        private static void Scissor(IntPtr pass, D3D11_RECT_V r)
            => wgpuRenderPassEncoderSetScissorRect(pass, (uint)r.left, (uint)r.top, (uint)(r.right - r.left), (uint)(r.bottom - r.top));

        private byte[] ParamsFor(D3DVideoProcessor p, D3DVideoView? input, D3D11_RECT_V dst, D3D11_RECT_V target, float[]? bg, bool fill)
        {
            var f = new float[40];
            var u = new uint[40];
            uint w = 1, h = 1;
            const int bits = 8;
            float unit = 255;
            if (input != null)
            {
                var t = input.Texture;
                w = Math.Max(1, t.Desc.Width >> (int)input.Mip); h = Math.Max(1, t.Desc.Height >> (int)input.Mip);
                // a 16-bit texel is read as a fraction of 65535 and converted with the 8-bit codes' ranges
                // (16-235, 16-240 of 255): what the hardware's video processor does, D3DVideoProcessorTests,
                // and the same as a 10-bit range in P010's high bits to a part in 1000
                if (t.Desc.Format is Formats.P010 or Formats.P016) unit = 255f / 65535f;
            }
            var src = p.SourceRectSet ? p.SourceRect : new D3D11_RECT_V { right = (int)w, bottom = (int)h };
            f[0] = src.left; f[1] = src.top; f[2] = src.right; f[3] = src.bottom;
            f[4] = dst.left; f[5] = dst.top; f[6] = dst.right; f[7] = dst.bottom;
            Array.Copy(VideoMatrix(p.StreamColor, p.OutputColor, bits), 0, f, 8, 12);
            if (bg != null) Array.Copy(bg, 0, f, 20, 4);
            f[28] = unit;
            var bytes = new byte[160];
            Buffer.BlockCopy(f, 0, bytes, 0, 160);
            u[0] = w; u[1] = h; u[2] = p.Rotation; u[3] = fill ? 1u : 0u;
            Buffer.BlockCopy(u, 0, bytes, 96, 16);
            return bytes;
        }

        private IntPtr BltGroup(IntPtr layout, D3DVideoView? input, byte[] parms, List<IntPtr> views, bool uintTexels)
        {
            ulong at = ArenaWrite(parms);
            IntPtr texView;
            if (input != null)
            {
                var t = input.Texture;
                texView = D3DView.CreateTextureView(t.Gpu, t.Format, WGPUTextureViewDimension._2D, input.Mip, 1, input.Slice, 1);
            }
            else texView = DummyVideoView(uintTexels);
            if (input != null) views.Add(texView);
            var e = new List<WGPUBindGroupEntry>
            {
                new() { binding = 0, textureView = texView },
                new() { binding = 1, buffer = _arenas[_arenaIndex], offset = at, size = 160 },
            };
            return MakeGroup(layout, e);
        }

        private IntPtr _videoDummyF, _videoDummyU;

        private IntPtr DummyVideoView(bool uintTexels)
        {
            ref IntPtr v = ref (uintTexels ? ref _videoDummyU : ref _videoDummyF);
            if (v != IntPtr.Zero) return v;
            var fmt = uintTexels ? TexFmt.R16Uint : TexFmt.R8Unorm;
            var d = new WGPUTextureDescriptor
            {
                usage = WGPUTextureUsage.TextureBinding, dimension = WGPUTextureDimension._2D,
                size = new WGPUExtent3D { width = 2, height = 3, depthOrArrayLayers = 1 }, format = fmt, mipLevelCount = 1, sampleCount = 1,
            };
            IntPtr tex = wgpuDeviceCreateTexture(Device.Gpu.Device, &d);
            v = D3DView.CreateTextureView(tex, fmt, WGPUTextureViewDimension._2D, 0, 1, 0, 1);
            return v;
        }
    }
}
