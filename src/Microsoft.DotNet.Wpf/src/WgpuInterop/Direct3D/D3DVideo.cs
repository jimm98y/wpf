// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// ID3D11VideoDevice and ID3D11VideoContext as a video processor only (Documentation/d3d11-webgpu-shim.md,
// section 8): the decoder is the GPU's own through Windows' Direct3D 11 where there is one, and none
// elsewhere (D3DVideoDecoder.cs); the video processor (NV12/P010 in, RGB out, colour conversion, rectangles
// and scaling) is a GPU pass of the shim's own (D3DVideoProcessor.cs).
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
            vt.Set(Vtbl.ID3D11VideoDevice.CreateVideoDecoderOutputView, (delegate* unmanaged<IntPtr, IntPtr, void*, IntPtr*, int>)&VdCreateDecoderView);
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

        // ---- no protection; the decoder is D3DVideoDecoder.cs's ----------------------------------------

        [UnmanagedCallersOnly] private static int VdNoChannel(IntPtr self, int type, IntPtr* channel) { if (channel != null) *channel = IntPtr.Zero; return HResult.E_NOTIMPL; }
        [UnmanagedCallersOnly] private static int VdNoCrypto(IntPtr self, Guid* a, Guid* b, Guid* c, IntPtr* session) { if (session != null) *session = IntPtr.Zero; return HResult.E_NOTIMPL; }

        [UnmanagedCallersOnly] private static int VdProtectionCaps(IntPtr self, Guid* crypto, Guid* profile, void* caps) => HResult.E_NOTIMPL;
        [UnmanagedCallersOnly] private static int VdKeyExchange(IntPtr self, Guid* crypto, Guid* profile, uint index, Guid* kind) => HResult.E_NOTIMPL;

        [UnmanagedCallersOnly]
        private static int VdSetPrivateData(IntPtr self, Guid* guid, uint size, void* data) => Self(self)?.Data.Set(guid, size, data) ?? HResult.E_FAIL;

        [UnmanagedCallersOnly]
        private static int VdSetPrivateDataInterface(IntPtr self, Guid* guid, IntPtr unknown) => Self(self)?.Data.SetInterface(guid, unknown) ?? HResult.E_FAIL;

        static partial void FillVideoProcessorFactory(Vtable vt);
    }
}
