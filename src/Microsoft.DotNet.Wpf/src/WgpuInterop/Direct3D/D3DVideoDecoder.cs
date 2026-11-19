// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The video decoder (Documentation/d3d11-webgpu-shim.md, section 8, video): ID3D11VideoDevice's decoder
// half and ID3D11VideoContext's, for Windows' own Media Foundation, whose hardware decoders (DXVA) take
// the shim's device. WebGPU has no video decode; the GPU's own decoder is reached through Windows'
// Direct3D 11 (VideoDecoderBackend): a device of the system's d3d11.dll made for it alone, on the
// default adapter, multithread-protected.
//
//   - profiles, formats and configurations are the GPU's own;
//   - a decoder is the GPU's, wrapped;
//   - an output view is of a texture of the shim's (MF's NV12 or P010 texture array): a texture of the
//     real device of the same shape mirrors it, and the view is of that;
//   - GetDecoderBuffer, ReleaseDecoderBuffer, DecoderBeginFrame, SubmitDecoderBuffers go to the GPU's
//     decoder as they are; DecoderEndFrame then copies the decoded slice -- a staging copy read back --
//     into the shim's texture, where the application's video processor, shaders and copies find it.
//
// Where there is no Windows Direct3D (off Windows: Media Foundation is the shim's own there, whose
// decoders need none of this), the device reports no decoder profiles, as before.
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Microsoft.Wpf.Interop.WebGpu.Direct3D
{
    /// <summary>Windows' Direct3D 11, for its video decoder alone.</summary>
    internal static unsafe class VideoDecoderBackend
    {
        private static readonly object s_lock = new();
        private static bool s_tried;
        public static IntPtr Device, Context, VideoDevice, VideoContext;

        public static bool Available
        {
            get
            {
                lock (s_lock)
                {
                    if (!s_tried) { s_tried = true; try { Create(); } catch (Exception e) { D3DLog.Error("video decoder backend", e); } }
                    return VideoDevice != IntPtr.Zero && VideoContext != IntPtr.Zero;
                }
            }
        }

        /// <summary>The GPU's decoder profiles: none where there is no decoder.</summary>
        public static uint Profiles => Available ? ((delegate* unmanaged<IntPtr, uint>)Fn(VideoDevice, Vtbl.ID3D11VideoDevice.GetVideoDecoderProfileCount))(VideoDevice) : 0;

        public static IntPtr Fn(IntPtr o, int slot) => (*(IntPtr**)o)[slot];
        public static void Release(IntPtr o) { if (o != IntPtr.Zero) ((delegate* unmanaged<IntPtr, uint>)Fn(o, 2))(o); }

        private static int Qi(IntPtr o, Guid iid, out IntPtr p)
        {
            IntPtr r;
            int hr = ((delegate* unmanaged<IntPtr, Guid*, IntPtr*, int>)Fn(o, 0))(o, &iid, &r);
            p = r;
            return hr;
        }

        private static void Create()
        {
            if (!OperatingSystem.IsWindows()) return;
            // the system's own d3d11.dll, by its path: the application's d3d11 is the shim
            IntPtr lib = NativeLibrary.Load(Path.Combine(Environment.SystemDirectory, "d3d11.dll"));
            var create = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, uint, uint*, uint, uint, IntPtr*, uint*, IntPtr*, int>)NativeLibrary.GetExport(lib, "D3D11CreateDevice");
            IntPtr dev, ctx;
            uint level;
            // D3D_DRIVER_TYPE_HARDWARE, D3D11_CREATE_DEVICE_VIDEO_SUPPORT | BGRA_SUPPORT
            int hr = create(IntPtr.Zero, 1, IntPtr.Zero, 0x800 | 0x20, null, 0, 7, &dev, &level, &ctx);
            if (hr < 0) { D3DLog.Once("vdec-dev", $"video decoder: no Windows Direct3D 11 device with video support (0x{hr:x8}); Media Foundation decodes on the CPU"); return; }
            if (Qi(dev, new Guid("9b7e4e00-342c-4106-a19f-4f2704f689f0"), out var mt) >= 0)
            {
                ((delegate* unmanaged<IntPtr, int, int>)Fn(mt, 5))(mt, 1);       // SetMultithreadProtected(TRUE)
                Release(mt);
            }
            if (Qi(dev, Vtbl.ID3D11VideoDevice.IID, out var vdev) < 0 || Qi(ctx, Vtbl.ID3D11VideoContext.IID, out var vctx) < 0)
            {
                D3DLog.Once("vdec-dev", "video decoder: the GPU's Direct3D 11 has no video interfaces; Media Foundation decodes on the CPU");
                Release(ctx); Release(dev);
                return;
            }
            Device = dev; Context = ctx; VideoDevice = vdev; VideoContext = vctx;
            uint n = ((delegate* unmanaged<IntPtr, uint>)Fn(vdev, Vtbl.ID3D11VideoDevice.GetVideoDecoderProfileCount))(vdev);
            D3DLog.Once("vdec-dev", $"video decoder: the GPU's, through Windows' Direct3D 11 ({n} profiles)");
        }

        /// <summary>The real texture mirroring a texture of the shim's, of the same shape, the decoder's output.</summary>
        internal sealed class Mirror
        {
            public IntPtr Texture, Staging;
            ~Mirror() { Release(Staging); Release(Texture); }
        }

        private static readonly ConditionalWeakTable<D3DTexture2D, Mirror> s_mirrors = new();

        public static Mirror? MirrorOf(D3DTexture2D t)
        {
            lock (s_lock)
            {
                if (s_mirrors.TryGetValue(t, out var m)) return m;
                var d = t.Desc;
                var rd = new D3D11_TEXTURE2D_DESC
                {
                    Width = d.Width, Height = d.Height, MipLevels = 1, ArraySize = Math.Max(1, d.ArraySize), Format = d.Format,
                    SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 }, Usage = D3D11_USAGE.DEFAULT, BindFlags = (D3D11_BIND)0x200,     // D3D11_BIND_DECODER
                };
                IntPtr tex, staging;
                int hr = ((delegate* unmanaged<IntPtr, D3D11_TEXTURE2D_DESC*, D3D11_SUBRESOURCE_DATA*, IntPtr*, int>)Fn(Device, Vtbl.ID3D11Device.CreateTexture2D))(Device, &rd, null, &tex);
                if (hr < 0) { D3DLog.Once("vdec-tex", $"video decoder: the GPU's texture of {d.Width}x{d.Height} {d.Format} was not made (0x{hr:x8})"); return null; }
                rd.ArraySize = 1; rd.Usage = D3D11_USAGE.STAGING; rd.BindFlags = 0; rd.CPUAccessFlags = D3D11_CPU_ACCESS.READ;
                hr = ((delegate* unmanaged<IntPtr, D3D11_TEXTURE2D_DESC*, D3D11_SUBRESOURCE_DATA*, IntPtr*, int>)Fn(Device, Vtbl.ID3D11Device.CreateTexture2D))(Device, &rd, null, &staging);
                if (hr < 0) { Release(tex); return null; }
                m = new Mirror { Texture = tex, Staging = staging };
                s_mirrors.Add(t, m);
                return m;
            }
        }

        /// <summary>The decoded slice of the mirror into the shim's texture.</summary>
        public static void CopyBack(D3DTexture2D t, Mirror m, uint slice)
        {
            var d = t.Desc;
            ((delegate* unmanaged<IntPtr, IntPtr, uint, uint, uint, uint, IntPtr, uint, void*, void>)Fn(Context, Vtbl.ID3D11DeviceContext.CopySubresourceRegion))(Context, m.Staging, 0, 0, 0, 0, m.Texture, slice, null);
            D3D11_MAPPED_SUBRESOURCE map;
            int hr = ((delegate* unmanaged<IntPtr, IntPtr, uint, D3D11_MAP, uint, D3D11_MAPPED_SUBRESOURCE*, int>)Fn(Context, Vtbl.ID3D11DeviceContext.Map))(Context, m.Staging, 0, D3D11_MAP.READ, 0, &map);
            if (hr < 0) { D3DLog.Once("vdec-map", $"video decoder: the decoded frame could not be read (0x{hr:x8})"); return; }
            try
            {
                // NV12 / P010 mapped: the luma rows, then the chroma rows, each RowPitch apart -- D3D's layout, which the
                // shim's UpdateSubresource of a planar texture takes
                lock (t.Device.Sync) t.Device.Immediate.UpdateSubresource(t, slice * Math.Max(1, d.MipLevels), null, map.pData, map.RowPitch, 0);
            }
            finally { ((delegate* unmanaged<IntPtr, IntPtr, uint, void>)Fn(Context, Vtbl.ID3D11DeviceContext.Unmap))(Context, m.Staging, 0); }
        }
    }

    /// <summary>ID3D11VideoDecoder: the GPU's decoder, wrapped.</summary>
    internal sealed unsafe class D3DVideoDecoder : D3DChild
    {
        private static readonly Vtable s_vt = Build();
        public readonly IntPtr Real;
        /// <summary>The view DecoderBeginFrame named: DecoderEndFrame copies it back.</summary>
        public D3DVideoDecoderOutputView? Current;

        private static Vtable Build()
        {
            var vt = new Vtable("ID3D11VideoDecoder", Vtbl.ID3D11VideoDecoder.SlotCount, Vtbl.ID3D11VideoDecoder.IID, Vtbl.ID3D11DeviceChild.IID);
            FillDeviceChild(vt);
            vt.Set(Vtbl.ID3D11VideoDecoder.GetCreationParameters, (delegate* unmanaged<IntPtr, void*, void*, int>)&GetCreationParameters);
            vt.Set(Vtbl.ID3D11VideoDecoder.GetDriverHandle, (delegate* unmanaged<IntPtr, IntPtr*, int>)&GetDriverHandle);
            return vt;
        }

        /// <summary>Decoders made so far (for the tests: the GPU's decoder was used).</summary>
        internal static int Created;

        public D3DVideoDecoder(D3DDevice device, IntPtr real) : base(device, s_vt) { Real = real; System.Threading.Interlocked.Increment(ref Created); }

        protected override void OnFinalRelease()
        {
            Current = null;
            VideoDecoderBackend.Release(Real);
            base.OnFinalRelease();
        }

        [UnmanagedCallersOnly]
        private static int GetCreationParameters(IntPtr self, void* desc, void* config)
            => From<D3DVideoDecoder>(self) is { } d ? ((delegate* unmanaged<IntPtr, void*, void*, int>)VideoDecoderBackend.Fn(d.Real, Vtbl.ID3D11VideoDecoder.GetCreationParameters))(d.Real, desc, config) : HResult.E_POINTER;

        [UnmanagedCallersOnly]
        private static int GetDriverHandle(IntPtr self, IntPtr* h)
            => From<D3DVideoDecoder>(self) is { } d ? ((delegate* unmanaged<IntPtr, IntPtr*, int>)VideoDecoderBackend.Fn(d.Real, Vtbl.ID3D11VideoDecoder.GetDriverHandle))(d.Real, h) : HResult.E_POINTER;
    }

    /// <summary>D3D11_VIDEO_DECODER_OUTPUT_VIEW_DESC.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct D3D11_VIDEO_DECODER_OUTPUT_VIEW_DESC { public Guid DecodeProfile; public uint ViewDimension; public uint ArraySlice; }

    /// <summary>ID3D11VideoDecoderOutputView: a slice of a texture of the shim's, and the GPU's view of its mirror.</summary>
    internal sealed unsafe class D3DVideoDecoderOutputView : D3DChild
    {
        private static readonly Vtable s_vt = Build();
        public readonly D3DTexture2D Texture;
        public readonly D3D11_VIDEO_DECODER_OUTPUT_VIEW_DESC Desc;
        public readonly VideoDecoderBackend.Mirror Mirror;
        public readonly IntPtr Real;

        private static Vtable Build()
        {
            var vt = new Vtable("ID3D11VideoDecoderOutputView", Vtbl.ID3D11VideoDecoderOutputView.SlotCount, Vtbl.ID3D11VideoDecoderOutputView.IID, Vtbl.ID3D11View.IID, Vtbl.ID3D11DeviceChild.IID);
            FillDeviceChild(vt);
            vt.Set(Vtbl.ID3D11VideoDecoderOutputView.GetResource, (delegate* unmanaged<IntPtr, IntPtr*, void>)&GetResource);
            vt.Set(Vtbl.ID3D11VideoDecoderOutputView.GetDesc, (delegate* unmanaged<IntPtr, D3D11_VIDEO_DECODER_OUTPUT_VIEW_DESC*, void>)&GetDesc);
            return vt;
        }

        public D3DVideoDecoderOutputView(D3DDevice device, D3DTexture2D texture, D3D11_VIDEO_DECODER_OUTPUT_VIEW_DESC desc, VideoDecoderBackend.Mirror mirror, IntPtr real) : base(device, s_vt)
        {
            Texture = texture; Desc = desc; Mirror = mirror; Real = real;
            texture.InternalAddRef();
        }

        protected override void OnFinalRelease()
        {
            VideoDecoderBackend.Release(Real);
            Texture.InternalRelease();
            base.OnFinalRelease();
        }

        [UnmanagedCallersOnly]
        private static void GetResource(IntPtr self, IntPtr* r)
        {
            if (r == null) return;
            *r = From<D3DVideoDecoderOutputView>(self) is { } v ? v.Texture.AddRefPointer() : IntPtr.Zero;
        }

        [UnmanagedCallersOnly]
        private static void GetDesc(IntPtr self, D3D11_VIDEO_DECODER_OUTPUT_VIEW_DESC* d)
        {
            if (d != null && From<D3DVideoDecoderOutputView>(self) is { } v) *d = v.Desc;
        }
    }

    internal sealed unsafe partial class D3DDevice
    {
        // ---- the decoder half of ID3D11VideoDevice ---------------------------------------------------

        private static IntPtr RealVd => VideoDecoderBackend.VideoDevice;

        [UnmanagedCallersOnly]
        private static int VdCreateVideoDecoder(IntPtr self, void* desc, void* config, IntPtr* decoder)
        {
            if (decoder != null) *decoder = IntPtr.Zero;
            if (Self(self) is not { } d || desc == null || config == null || decoder == null) return HResult.E_INVALIDARG;
            if (!VideoDecoderBackend.Available)
            {
                D3DLog.Once("vdec", "CreateVideoDecoder: no GPU decoder here (no profiles reported); the app decodes on the CPU");
                return HResult.E_INVALIDARG;
            }
            IntPtr real;
            int hr = ((delegate* unmanaged<IntPtr, void*, void*, IntPtr*, int>)VideoDecoderBackend.Fn(RealVd, Vtbl.ID3D11VideoDevice.CreateVideoDecoder))(RealVd, desc, config, &real);
            if (hr < 0) return hr;
            return ComObject.Out(decoder, new D3DVideoDecoder(d, real));
        }

        [UnmanagedCallersOnly]
        private static int VdCreateDecoderView(IntPtr self, IntPtr resource, void* desc, IntPtr* view)
        {
            if (view != null) *view = IntPtr.Zero;
            if (Self(self) is not { } d || desc == null || view == null) return HResult.E_INVALIDARG;
            if (!VideoDecoderBackend.Available) return HResult.E_INVALIDARG;
            if (From<D3DTexture2D>(resource) is not { } tex) return HResult.E_INVALIDARG;
            var vd = *(D3D11_VIDEO_DECODER_OUTPUT_VIEW_DESC*)desc;
            try
            {
                var mirror = VideoDecoderBackend.MirrorOf(tex);
                if (mirror == null) return HResult.E_INVALIDARG;
                IntPtr real;
                int hr = ((delegate* unmanaged<IntPtr, IntPtr, void*, IntPtr*, int>)VideoDecoderBackend.Fn(RealVd, Vtbl.ID3D11VideoDevice.CreateVideoDecoderOutputView))(RealVd, mirror.Texture, desc, &real);
                if (hr < 0) return hr;
                return ComObject.Out(view, new D3DVideoDecoderOutputView(d, tex, vd, mirror, real));
            }
            catch (Exception e) { D3DLog.Error("CreateVideoDecoderOutputView", e); return HResult.E_FAIL; }
        }

        [UnmanagedCallersOnly] private static uint VdProfileCount(IntPtr self) => VideoDecoderBackend.Available ? ((delegate* unmanaged<IntPtr, uint>)VideoDecoderBackend.Fn(RealVd, Vtbl.ID3D11VideoDevice.GetVideoDecoderProfileCount))(RealVd) : 0;

        [UnmanagedCallersOnly]
        private static int VdProfile(IntPtr self, uint index, Guid* profile)
            => VideoDecoderBackend.Available ? ((delegate* unmanaged<IntPtr, uint, Guid*, int>)VideoDecoderBackend.Fn(RealVd, Vtbl.ID3D11VideoDevice.GetVideoDecoderProfile))(RealVd, index, profile) : HResult.E_INVALIDARG;

        [UnmanagedCallersOnly]
        private static int VdCheckDecoderFormat(IntPtr self, Guid* profile, uint format, int* supported)
        {
            if (VideoDecoderBackend.Available) return ((delegate* unmanaged<IntPtr, Guid*, uint, int*, int>)VideoDecoderBackend.Fn(RealVd, Vtbl.ID3D11VideoDevice.CheckVideoDecoderFormat))(RealVd, profile, format, supported);
            if (supported != null) *supported = 0;
            return HResult.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int VdConfigCount(IntPtr self, void* desc, uint* count)
        {
            if (VideoDecoderBackend.Available) return ((delegate* unmanaged<IntPtr, void*, uint*, int>)VideoDecoderBackend.Fn(RealVd, Vtbl.ID3D11VideoDevice.GetVideoDecoderConfigCount))(RealVd, desc, count);
            if (count != null) *count = 0;
            return HResult.E_INVALIDARG;
        }

        [UnmanagedCallersOnly]
        private static int VdConfig(IntPtr self, void* desc, uint index, void* config)
            => VideoDecoderBackend.Available ? ((delegate* unmanaged<IntPtr, void*, uint, void*, int>)VideoDecoderBackend.Fn(RealVd, Vtbl.ID3D11VideoDevice.GetVideoDecoderConfig))(RealVd, desc, index, config) : HResult.E_INVALIDARG;
    }

    internal sealed unsafe partial class D3DContext
    {
        // ---- the decoder half of ID3D11VideoContext: the GPU's decoder, as it is ----------------------

        private static IntPtr RealVc => VideoDecoderBackend.VideoContext;

        internal static void FillVideoDecoding(Vtable vt)
        {
            vt.Set(Vtbl.ID3D11VideoContext.GetDecoderBuffer, (delegate* unmanaged<IntPtr, IntPtr, uint, uint*, void**, int>)&VcGetDecoderBuffer);
            vt.Set(Vtbl.ID3D11VideoContext.ReleaseDecoderBuffer, (delegate* unmanaged<IntPtr, IntPtr, uint, int>)&VcReleaseDecoderBuffer);
            vt.Set(Vtbl.ID3D11VideoContext.DecoderBeginFrame, (delegate* unmanaged<IntPtr, IntPtr, IntPtr, uint, void*, int>)&VcDecoderBeginFrame);
            vt.Set(Vtbl.ID3D11VideoContext.DecoderEndFrame, (delegate* unmanaged<IntPtr, IntPtr, int>)&VcDecoderEndFrame);
            vt.Set(Vtbl.ID3D11VideoContext.SubmitDecoderBuffers, (delegate* unmanaged<IntPtr, IntPtr, uint, void*, int>)&VcSubmitDecoderBuffers);
            vt.Set(Vtbl.ID3D11VideoContext.DecoderExtension, (delegate* unmanaged<IntPtr, IntPtr, void*, int>)&VcDecoderExtension);
        }

        [UnmanagedCallersOnly]
        private static int VcGetDecoderBuffer(IntPtr self, IntPtr decoder, uint type, uint* size, void** buffer)
        {
            if (buffer != null) *buffer = null;
            if (From<D3DVideoDecoder>(decoder) is not { } d) return HResult.E_INVALIDARG;
            return ((delegate* unmanaged<IntPtr, IntPtr, uint, uint*, void**, int>)VideoDecoderBackend.Fn(RealVc, Vtbl.ID3D11VideoContext.GetDecoderBuffer))(RealVc, d.Real, type, size, buffer);
        }

        [UnmanagedCallersOnly]
        private static int VcReleaseDecoderBuffer(IntPtr self, IntPtr decoder, uint type)
            => From<D3DVideoDecoder>(decoder) is { } d ? ((delegate* unmanaged<IntPtr, IntPtr, uint, int>)VideoDecoderBackend.Fn(RealVc, Vtbl.ID3D11VideoContext.ReleaseDecoderBuffer))(RealVc, d.Real, type) : HResult.E_INVALIDARG;

        [UnmanagedCallersOnly]
        private static int VcDecoderBeginFrame(IntPtr self, IntPtr decoder, IntPtr view, uint keySize, void* key)
        {
            if (From<D3DVideoDecoder>(decoder) is not { } d || From<D3DVideoDecoderOutputView>(view) is not { } v) return HResult.E_INVALIDARG;
            int hr = ((delegate* unmanaged<IntPtr, IntPtr, IntPtr, uint, void*, int>)VideoDecoderBackend.Fn(RealVc, Vtbl.ID3D11VideoContext.DecoderBeginFrame))(RealVc, d.Real, v.Real, keySize, key);
            if (hr >= 0) d.Current = v;
            return hr;
        }

        [UnmanagedCallersOnly]
        private static int VcSubmitDecoderBuffers(IntPtr self, IntPtr decoder, uint count, void* descs)
            => From<D3DVideoDecoder>(decoder) is { } d ? ((delegate* unmanaged<IntPtr, IntPtr, uint, void*, int>)VideoDecoderBackend.Fn(RealVc, Vtbl.ID3D11VideoContext.SubmitDecoderBuffers))(RealVc, d.Real, count, descs) : HResult.E_INVALIDARG;

        [UnmanagedCallersOnly]
        private static int VcDecoderEndFrame(IntPtr self, IntPtr decoder)
        {
            if (From<D3DVideoDecoder>(decoder) is not { } d) return HResult.E_INVALIDARG;
            int hr = ((delegate* unmanaged<IntPtr, IntPtr, int>)VideoDecoderBackend.Fn(RealVc, Vtbl.ID3D11VideoContext.DecoderEndFrame))(RealVc, d.Real);
            if (hr < 0 || d.Current is not { } v) return hr;
            d.Current = null;
            try { VideoDecoderBackend.CopyBack(v.Texture, v.Mirror, v.Desc.ArraySlice); }
            catch (Exception e) { D3DLog.Error("DecoderEndFrame", e); }
            return hr;
        }

        /// <summary>An extension naming no resources goes as it is; one naming the shim's would need them mirrored.</summary>
        [UnmanagedCallersOnly]
        private static int VcDecoderExtension(IntPtr self, IntPtr decoder, void* ext)
        {
            if (From<D3DVideoDecoder>(decoder) is not { } d || ext == null) return HResult.E_INVALIDARG;
            // D3D11_VIDEO_DECODER_EXTENSION: Function, pPrivateInputData, PrivateInputDataSize, pPrivateOutputData,
            // PrivateOutputDataSize, ResourceCount, ppResourceList
            uint resources = *(uint*)((byte*)ext + 4 * IntPtr.Size + 4);
            if (resources != 0) { D3DLog.Once("vdec-ext", "DecoderExtension with resources: not passed on"); return HResult.E_NOTIMPL; }
            return ((delegate* unmanaged<IntPtr, IntPtr, void*, int>)VideoDecoderBackend.Fn(RealVc, Vtbl.ID3D11VideoContext.DecoderExtension))(RealVc, d.Real, ext);
        }
    }
}
