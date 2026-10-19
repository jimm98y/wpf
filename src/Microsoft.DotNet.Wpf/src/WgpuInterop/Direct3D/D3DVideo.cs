// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// ID3D11VideoDevice and ID3D11VideoContext as a video processor only (Documentation/d3d11-webgpu-shim.md,
// section 8, the user's decision 2026-10-19): WebGPU has no video decode, so the device reports no decoder
// profiles -- Media Foundation then decodes on the CPU -- and the video processor (NV12/P010 in, RGB out,
// colour conversion, rectangles and scaling) is a GPU pass of the shim's own (D3DVideoProcessor.cs).
//

using System;
using System.Runtime.InteropServices;

namespace Microsoft.Wpf.Interop.WebGpu.Direct3D
{
    internal sealed unsafe partial class D3DDevice
    {
        public static readonly Guid IID_ID3D11VideoDevice = new("10EC4D5B-975A-4689-B9E4-D0AAC30FE333");

        private static readonly Vtable s_videoDevice = BuildVideoDevice();

        private static Vtable BuildVideoDevice()
        {
            var vt = new Vtable("ID3D11VideoDevice", 20, IID_ID3D11VideoDevice);
            vt.Set(Vtbl.ID3D11VideoDevice.CreateVideoDecoder, (delegate* unmanaged<IntPtr, void*, void*, IntPtr*, int>)&VdCreateVideoDecoder);
            vt.Set(Vtbl.ID3D11VideoDevice.CreateAuthenticatedChannel, (delegate* unmanaged<IntPtr, int, IntPtr*, int>)&VdNoChannel);
            vt.Set(Vtbl.ID3D11VideoDevice.CreateCryptoSession, (delegate* unmanaged<IntPtr, Guid*, Guid*, Guid*, IntPtr*, int>)&VdNoCrypto);
            vt.Set(Vtbl.ID3D11VideoDevice.CreateVideoDecoderOutputView, (delegate* unmanaged<IntPtr, IntPtr, void*, IntPtr*, int>)&VdNoDecoderView);
            vt.Set(Vtbl.ID3D11VideoDevice.GetVideoDecoderProfileCount, (delegate* unmanaged<IntPtr, uint>)&VdProfileCount);
            vt.Set(Vtbl.ID3D11VideoDevice.GetVideoDecoderProfile, (delegate* unmanaged<IntPtr, uint, Guid*, int>)&VdProfile);
            vt.Set(Vtbl.ID3D11VideoDevice.CheckVideoDecoderFormat, (delegate* unmanaged<IntPtr, Guid*, uint, int*, int>)&VdCheckDecoderFormat);
            vt.Set(Vtbl.ID3D11VideoDevice.GetVideoDecoderConfigCount, (delegate* unmanaged<IntPtr, void*, uint*, int>)&VdConfigCount);
            vt.Set(Vtbl.ID3D11VideoDevice.GetVideoDecoderConfig, (delegate* unmanaged<IntPtr, void*, uint, void*, int>)&VdConfig);
            vt.Set(Vtbl.ID3D11VideoDevice.GetContentProtectionCaps, (delegate* unmanaged<IntPtr, Guid*, Guid*, void*, int>)&VdProtectionCaps);
            vt.Set(Vtbl.ID3D11VideoDevice.CheckCryptoKeyExchange, (delegate* unmanaged<IntPtr, Guid*, Guid*, uint, Guid*, int>)&VdKeyExchange);
            vt.Set(Vtbl.ID3D11VideoDevice.SetPrivateData, (delegate* unmanaged<IntPtr, Guid*, uint, void*, int>)&VdSetPrivateData);
            vt.Set(Vtbl.ID3D11VideoDevice.SetPrivateDataInterface, (delegate* unmanaged<IntPtr, Guid*, IntPtr, int>)&VdSetPrivateDataInterface);
            FillVideoProcessorFactory(vt);
            return vt;
        }

        // ---- no decoding: no profiles, no decoders, no protection --------------------------------------

        [UnmanagedCallersOnly]
        private static int VdCreateVideoDecoder(IntPtr self, void* desc, void* config, IntPtr* decoder)
        {
            if (decoder != null) *decoder = IntPtr.Zero;
            D3DLog.Once("vdec", "CreateVideoDecoder: the shim has no video decoder (no profiles reported); the app decodes on the CPU");
            return HResult.E_INVALIDARG;
        }

        [UnmanagedCallersOnly] private static int VdNoChannel(IntPtr self, int type, IntPtr* channel) { if (channel != null) *channel = IntPtr.Zero; return HResult.E_NOTIMPL; }
        [UnmanagedCallersOnly] private static int VdNoCrypto(IntPtr self, Guid* a, Guid* b, Guid* c, IntPtr* session) { if (session != null) *session = IntPtr.Zero; return HResult.E_NOTIMPL; }
        [UnmanagedCallersOnly] private static int VdNoDecoderView(IntPtr self, IntPtr resource, void* desc, IntPtr* view) { if (view != null) *view = IntPtr.Zero; return HResult.E_INVALIDARG; }
        [UnmanagedCallersOnly] private static uint VdProfileCount(IntPtr self) => 0;
        [UnmanagedCallersOnly] private static int VdProfile(IntPtr self, uint index, Guid* profile) => HResult.E_INVALIDARG;

        [UnmanagedCallersOnly]
        private static int VdCheckDecoderFormat(IntPtr self, Guid* profile, uint format, int* supported)
        {
            if (supported != null) *supported = 0;
            return HResult.S_OK;
        }

        [UnmanagedCallersOnly] private static int VdConfigCount(IntPtr self, void* desc, uint* count) { if (count != null) *count = 0; return HResult.E_INVALIDARG; }
        [UnmanagedCallersOnly] private static int VdConfig(IntPtr self, void* desc, uint index, void* config) => HResult.E_INVALIDARG;
        [UnmanagedCallersOnly] private static int VdProtectionCaps(IntPtr self, Guid* crypto, Guid* profile, void* caps) => HResult.E_NOTIMPL;
        [UnmanagedCallersOnly] private static int VdKeyExchange(IntPtr self, Guid* crypto, Guid* profile, uint index, Guid* kind) => HResult.E_NOTIMPL;

        [UnmanagedCallersOnly]
        private static int VdSetPrivateData(IntPtr self, Guid* guid, uint size, void* data) => Self(self)?.Data.Set(guid, size, data) ?? HResult.E_FAIL;

        [UnmanagedCallersOnly]
        private static int VdSetPrivateDataInterface(IntPtr self, Guid* guid, IntPtr unknown) => Self(self)?.Data.SetInterface(guid, unknown) ?? HResult.E_FAIL;

        static partial void FillVideoProcessorFactory(Vtable vt);
    }
}
