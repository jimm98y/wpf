// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The Direct3D 9Ex half of the shim (Documentation/d3d11-webgpu-shim.md, section 7). Applications
// reach D3D9Ex for one thing: a surface WPF's D3DImage takes. They open the texture they render with
// Direct3D 11 by its shared handle (CreateTexture with *pSharedHandle set -- SharpMediaFoundationInterop,
// DXEngine), or make a shared D3D9 texture and open it from D3D11. A real D3D9 device cannot open the
// shim's handles, so with the D3D11 shim on, d3d9 is shimmed too.
//
// What is here is what that takes: the IDirect3D9Ex queries an application makes before it creates
// a device, and on the device textures, render targets and plain surfaces -- GPU ones over the
// shim's D3D11 textures (D3DTexture2D, so a shared handle names the same texture on both sides),
// system-memory ones over CPU memory -- with the copies between them (GetRenderTargetData,
// UpdateSurface, UpdateTexture, StretchRect, ColorFill) and LockRect. That is also exactly what
// PresentationCore's managed D3DImage (ManagedInteropBitmap) calls. Drawing through D3D9 itself
// (DrawPrimitive and the fixed-function pipeline) is not here: it logs once and answers E_NOTIMPL.
//
// All shim devices share one WebGPU device (D3DDevice.SharedGpu), as all of a machine's Direct3D
// devices share its GPU: a texture shared between them is one WebGPU texture.
//

using System;
using System.Runtime.InteropServices;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using static Microsoft.Wpf.Interop.WebGpu.Wgpu;

namespace Microsoft.Wpf.Interop.WebGpu.Direct3D
{
    internal static class D3D9Const
    {
        public const int D3DERR_INVALIDCALL = unchecked((int)0x8876086C);
        public const int D3DERR_NOTAVAILABLE = unchecked((int)0x8876086A);
        public const int D3DERR_WASSTILLDRAWING = unchecked((int)0x8876021C);
        public const int D3DFMT_A8R8G8B8 = 21, D3DFMT_X8R8G8B8 = 22, D3DFMT_A8B8G8R8 = 32, D3DFMT_X8B8G8R8 = 33,
            D3DFMT_A8 = 28, D3DFMT_L8 = 50, D3DFMT_D16 = 80, D3DFMT_D24S8 = 75, D3DFMT_D24X8 = 77, D3DFMT_D32 = 71,
            D3DFMT_R32F = 114, D3DFMT_A16B16G16R16F = 113, D3DFMT_A32B32G32R32F = 116, D3DFMT_A2R10G10B10 = 35, D3DFMT_A2B10G10R10 = 31,
            D3DFMT_G16R16F = 112, D3DFMT_R16F = 111;
        public const int D3DPOOL_DEFAULT = 0, D3DPOOL_MANAGED = 1, D3DPOOL_SYSTEMMEM = 2, D3DPOOL_SCRATCH = 3;
        public const uint D3DUSAGE_RENDERTARGET = 0x1, D3DUSAGE_DEPTHSTENCIL = 0x2, D3DUSAGE_DYNAMIC = 0x200, D3DUSAGE_AUTOGENMIPMAP = 0x400;
        public const int D3DRTYPE_SURFACE = 1, D3DRTYPE_TEXTURE = 3;
        public const uint D3DLOCK_READONLY = 0x10, D3DLOCK_DISCARD = 0x2000;

        /// <summary>A D3D9 format as the DXGI format of the texture behind it (0: none).</summary>
        public static DXGI_FORMAT ToDxgi(int f) => f switch
        {
            D3DFMT_A8R8G8B8 => DXGI_FORMAT.B8G8R8A8_UNORM,
            D3DFMT_X8R8G8B8 => DXGI_FORMAT.B8G8R8X8_UNORM,
            D3DFMT_A8B8G8R8 or D3DFMT_X8B8G8R8 => DXGI_FORMAT.R8G8B8A8_UNORM,
            D3DFMT_A8 => DXGI_FORMAT.A8_UNORM,
            D3DFMT_L8 => DXGI_FORMAT.R8_UNORM,
            D3DFMT_R32F => DXGI_FORMAT.R32_FLOAT,
            D3DFMT_R16F => DXGI_FORMAT.R16_FLOAT,
            D3DFMT_G16R16F => DXGI_FORMAT.R16G16_FLOAT,
            D3DFMT_A16B16G16R16F => DXGI_FORMAT.R16G16B16A16_FLOAT,
            D3DFMT_A32B32G32R32F => DXGI_FORMAT.R32G32B32A32_FLOAT,
            D3DFMT_A2B10G10R10 => DXGI_FORMAT.R10G10B10A2_UNORM,
            D3DFMT_D16 => DXGI_FORMAT.D16_UNORM,
            D3DFMT_D24S8 or D3DFMT_D24X8 => DXGI_FORMAT.D24_UNORM_S8_UINT,
            D3DFMT_D32 => DXGI_FORMAT.D32_FLOAT,
            _ => 0,
        };

        /// <summary>The D3D9 format a shared D3D11 texture is seen as (CreateTexture's check).</summary>
        public static bool SameTexels(int d3d9, DXGI_FORMAT dxgi) => ToDxgi(d3d9) == dxgi
            || (d3d9 is D3DFMT_A8R8G8B8 or D3DFMT_X8R8G8B8 && dxgi is DXGI_FORMAT.B8G8R8A8_UNORM or DXGI_FORMAT.B8G8R8X8_UNORM or DXGI_FORMAT.B8G8R8A8_TYPELESS or DXGI_FORMAT.B8G8R8A8_UNORM_SRGB)
            || (d3d9 is D3DFMT_A8B8G8R8 or D3DFMT_X8B8G8R8 && dxgi is DXGI_FORMAT.R8G8B8A8_UNORM or DXGI_FORMAT.R8G8B8A8_TYPELESS or DXGI_FORMAT.R8G8B8A8_UNORM_SRGB);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct D3DSURFACE_DESC { public int Format, Type; public uint Usage; public int Pool, MultiSampleType; public uint MultiSampleQuality, Width, Height; }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct D3DLOCKED_RECT { public int Pitch; public void* pBits; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct D3DRECT9 { public int left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct D3DDISPLAYMODE { public uint Width, Height, RefreshRate; public int Format; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct D3DDISPLAYMODEEX { public uint Size, Width, Height, RefreshRate; public int Format, ScanLineOrdering; }

    // ---- IDirect3D9Ex ---------------------------------------------------------------------------

    internal sealed unsafe class Direct3D9 : ComObject
    {
        private static readonly Vtable s_vt = Build();
        public readonly bool Ex;

        private Direct3D9(bool ex) : base(s_vt) { Ex = ex; }

        public static Direct3D9 Create(bool ex) => new(ex);

        private static Vtable Build()
        {
            var vt = new Vtable("IDirect3D9Ex", Vtbl.IDirect3D9Ex.SlotCount, Vtbl.IDirect3D9Ex.IID, Vtbl.IDirect3D9.IID);
            vt.Set(Vtbl.IDirect3D9Ex.RegisterSoftwareDevice, (delegate* unmanaged<IntPtr, void*, int>)&RegisterSoftwareDevice);
            vt.Set(Vtbl.IDirect3D9Ex.GetAdapterCount, (delegate* unmanaged<IntPtr, uint>)&GetAdapterCount);
            vt.Set(Vtbl.IDirect3D9Ex.GetAdapterIdentifier, (delegate* unmanaged<IntPtr, uint, uint, byte*, int>)&GetAdapterIdentifier);
            vt.Set(Vtbl.IDirect3D9Ex.GetAdapterModeCount, (delegate* unmanaged<IntPtr, uint, int, uint>)&GetAdapterModeCount);
            vt.Set(Vtbl.IDirect3D9Ex.EnumAdapterModes, (delegate* unmanaged<IntPtr, uint, int, uint, D3DDISPLAYMODE*, int>)&EnumAdapterModes);
            vt.Set(Vtbl.IDirect3D9Ex.GetAdapterDisplayMode, (delegate* unmanaged<IntPtr, uint, D3DDISPLAYMODE*, int>)&GetAdapterDisplayMode);
            vt.Set(Vtbl.IDirect3D9Ex.CheckDeviceType, (delegate* unmanaged<IntPtr, uint, int, int, int, int, int>)&CheckDeviceType);
            vt.Set(Vtbl.IDirect3D9Ex.CheckDeviceFormat, (delegate* unmanaged<IntPtr, uint, int, int, uint, int, int, int>)&CheckDeviceFormat);
            vt.Set(Vtbl.IDirect3D9Ex.CheckDeviceMultiSampleType, (delegate* unmanaged<IntPtr, uint, int, int, int, int, uint*, int>)&CheckDeviceMultiSampleType);
            vt.Set(Vtbl.IDirect3D9Ex.CheckDepthStencilMatch, (delegate* unmanaged<IntPtr, uint, int, int, int, int, int>)&CheckDepthStencilMatch);
            vt.Set(Vtbl.IDirect3D9Ex.CheckDeviceFormatConversion, (delegate* unmanaged<IntPtr, uint, int, int, int, int>)&CheckDeviceFormatConversion);
            vt.Set(Vtbl.IDirect3D9Ex.GetDeviceCaps, (delegate* unmanaged<IntPtr, uint, int, uint*, int>)&GetDeviceCaps);
            vt.Set(Vtbl.IDirect3D9Ex.GetAdapterMonitor, (delegate* unmanaged<IntPtr, uint, IntPtr>)&GetAdapterMonitor);
            vt.Set(Vtbl.IDirect3D9Ex.CreateDevice, (delegate* unmanaged<IntPtr, uint, int, IntPtr, uint, void*, IntPtr*, int>)&CreateDevice);
            vt.Set(Vtbl.IDirect3D9Ex.GetAdapterModeCountEx, (delegate* unmanaged<IntPtr, uint, void*, uint>)&GetAdapterModeCountEx);
            vt.Set(Vtbl.IDirect3D9Ex.EnumAdapterModesEx, (delegate* unmanaged<IntPtr, uint, void*, uint, D3DDISPLAYMODEEX*, int>)&EnumAdapterModesEx);
            vt.Set(Vtbl.IDirect3D9Ex.GetAdapterDisplayModeEx, (delegate* unmanaged<IntPtr, uint, D3DDISPLAYMODEEX*, int*, int>)&GetAdapterDisplayModeEx);
            vt.Set(Vtbl.IDirect3D9Ex.CreateDeviceEx, (delegate* unmanaged<IntPtr, uint, int, IntPtr, uint, void*, void*, IntPtr*, int>)&CreateDeviceEx);
            vt.Set(Vtbl.IDirect3D9Ex.GetAdapterLUID, (delegate* unmanaged<IntPtr, uint, long*, int>)&GetAdapterLUID);
            return vt;
        }

        protected override IntPtr QueryOther(in Guid iid) => IntPtr.Zero;

        [UnmanagedCallersOnly] private static int RegisterSoftwareDevice(IntPtr self, void* init) => HResult.E_NOTIMPL;
        [UnmanagedCallersOnly] private static uint GetAdapterCount(IntPtr self) => 1;

        [UnmanagedCallersOnly]
        private static int GetAdapterIdentifier(IntPtr self, uint adapter, uint flags, byte* id)
        {
            if (adapter != 0 || id == null) return D3D9Const.D3DERR_INVALIDCALL;
            new Span<byte>(id, 1100).Clear();
            WriteAnsi(id, 512, "wgpu");
            WriteAnsi(id + 512, 512, "WebGPU (Direct3D 9Ex shim) " + (D3DDevice.SharedGpu.AdapterDescription ?? ""));
            WriteAnsi(id + 1024, 32, @"\\.\DISPLAY1");
            *(long*)(id + 1056) = 0x000A_0000_0000_0001L;      // DriverVersion
            ((uint*)(id + 1064))[0] = 0x1414;                    // VendorId: Microsoft's, as WARP reports
            ((uint*)(id + 1064))[1] = 0x008C;                    // DeviceId
            *(Guid*)(id + 1080) = new Guid("7d3d9e5c-1b5a-4d0f-9a7e-5c3e2b1f0d11");
            *(uint*)(id + 1096) = 1;                             // WHQLLevel
            return HResult.S_OK;
        }

        private static void WriteAnsi(byte* dst, int cap, string s)
        {
            int n = Math.Min(cap - 1, s.Length);
            for (int i = 0; i < n; i++) dst[i] = (byte)(s[i] < 128 ? s[i] : '?');
            dst[n] = 0;
        }

        internal static D3DDISPLAYMODE DisplayMode() => new() { Width = 1920, Height = 1080, RefreshRate = 60, Format = D3D9Const.D3DFMT_X8R8G8B8 };

        [UnmanagedCallersOnly] private static uint GetAdapterModeCount(IntPtr self, uint adapter, int format) => format == D3D9Const.D3DFMT_X8R8G8B8 ? 1u : 0u;

        [UnmanagedCallersOnly]
        private static int EnumAdapterModes(IntPtr self, uint adapter, int format, uint mode, D3DDISPLAYMODE* m)
        {
            if (m == null || mode != 0 || format != D3D9Const.D3DFMT_X8R8G8B8) return D3D9Const.D3DERR_INVALIDCALL;
            *m = DisplayMode();
            return HResult.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int GetAdapterDisplayMode(IntPtr self, uint adapter, D3DDISPLAYMODE* m)
        {
            if (m == null || adapter != 0) return D3D9Const.D3DERR_INVALIDCALL;
            *m = DisplayMode();
            return HResult.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int CheckDeviceType(IntPtr self, uint adapter, int type, int displayFormat, int backBufferFormat, int windowed)
            => adapter == 0 ? HResult.S_OK : D3D9Const.D3DERR_INVALIDCALL;

        [UnmanagedCallersOnly]
        private static int CheckDeviceFormat(IntPtr self, uint adapter, int type, int adapterFormat, uint usage, int resourceType, int format)
        {
            if (adapter != 0) return D3D9Const.D3DERR_INVALIDCALL;
            DXGI_FORMAT f = D3D9Const.ToDxgi(format);
            if (f == 0) return D3D9Const.D3DERR_NOTAVAILABLE;
            bool depth = f is DXGI_FORMAT.D16_UNORM or DXGI_FORMAT.D24_UNORM_S8_UINT or DXGI_FORMAT.D32_FLOAT;
            if ((usage & D3D9Const.D3DUSAGE_DEPTHSTENCIL) != 0 && !depth) return D3D9Const.D3DERR_NOTAVAILABLE;
            if ((usage & D3D9Const.D3DUSAGE_RENDERTARGET) != 0 && depth) return D3D9Const.D3DERR_NOTAVAILABLE;
            return HResult.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int CheckDeviceMultiSampleType(IntPtr self, uint adapter, int type, int format, int windowed, int multiSample, uint* levels)
        {
            if (levels != null) *levels = multiSample == 0 ? 1u : 0u;
            return multiSample == 0 ? HResult.S_OK : D3D9Const.D3DERR_NOTAVAILABLE;
        }

        [UnmanagedCallersOnly] private static int CheckDepthStencilMatch(IntPtr self, uint adapter, int type, int af, int rt, int ds) => HResult.S_OK;
        [UnmanagedCallersOnly] private static int CheckDeviceFormatConversion(IntPtr self, uint adapter, int type, int src, int dst) => HResult.S_OK;

        [UnmanagedCallersOnly]
        private static int GetDeviceCaps(IntPtr self, uint adapter, int type, uint* caps) => FillCaps(caps, type);

        /// <summary>D3DCAPS9 (304 bytes) as a hardware device that shares resources and runs
        /// shader model 3 reports it.</summary>
        internal static int FillCaps(uint* c, int type)
        {
            if (c == null) return D3D9Const.D3DERR_INVALIDCALL;
            new Span<byte>(c, 304).Clear();
            c[0] = (uint)(type == 0 ? 1 : type);    // DeviceType
            c[3] = 0x80000000u | 0x20000000u | 0x40000000u;   // Caps2: CANSHARERESOURCE | DYNAMICTEXTURES | CANAUTOGENMIPMAP
            c[5] = 0x80000000u | 0x1u;               // PresentationIntervals: IMMEDIATE | ONE
            c[7] = 0x10000u | 0x80000u | 0x2000u;    // DevCaps: HWTRANSFORMANDLIGHT | HWRASTERIZATION | DRAWPRIMITIVES2EX
            c[22] = 8192; c[23] = 8192;               // MaxTextureWidth, MaxTextureHeight
            c[24] = 2048;                             // MaxVolumeExtent
            c[25] = 8192; c[26] = 8192; c[27] = 16;   // MaxTextureRepeat, AspectRatio, Anisotropy
            c[37] = 8; c[38] = 8;                     // MaxTextureBlendStages, MaxSimultaneousTextures
            c[45] = 0xFFFFFF; c[46] = 0xFFFFFF;       // MaxPrimitiveCount, MaxVertexIndex
            c[47] = 16; c[48] = 255;                  // MaxStreams, MaxStreamStride
            c[49] = 0xFFFE0300;                       // VertexShaderVersion 3.0
            c[50] = 256;                              // MaxVertexShaderConst
            c[51] = 0xFFFF0300;                       // PixelShaderVersion 3.0
            c[56] = 0; c[57] = 0; c[58] = 1;          // master ordinal, ordinal in group, adapters in group
            c[60] = 4;                                // NumSimultaneousRTs
            return HResult.S_OK;
        }

        [UnmanagedCallersOnly] private static IntPtr GetAdapterMonitor(IntPtr self, uint adapter) => IntPtr.Zero;

        [UnmanagedCallersOnly]
        private static int CreateDevice(IntPtr self, uint adapter, int type, IntPtr focus, uint flags, void* pp, IntPtr* device)
            => CreateDeviceCore(self, adapter, device);

        [UnmanagedCallersOnly] private static uint GetAdapterModeCountEx(IntPtr self, uint adapter, void* filter) => 1;

        [UnmanagedCallersOnly]
        private static int EnumAdapterModesEx(IntPtr self, uint adapter, void* filter, uint mode, D3DDISPLAYMODEEX* m)
        {
            if (m == null || mode != 0) return D3D9Const.D3DERR_INVALIDCALL;
            FillModeEx(m);
            return HResult.S_OK;
        }

        internal static void FillModeEx(D3DDISPLAYMODEEX* m)
        {
            D3DDISPLAYMODE d = DisplayMode();
            m->Size = (uint)sizeof(D3DDISPLAYMODEEX); m->Width = d.Width; m->Height = d.Height; m->RefreshRate = d.RefreshRate; m->Format = d.Format; m->ScanLineOrdering = 1;
        }

        [UnmanagedCallersOnly]
        private static int GetAdapterDisplayModeEx(IntPtr self, uint adapter, D3DDISPLAYMODEEX* m, int* rotation)
        {
            if (adapter != 0) return D3D9Const.D3DERR_INVALIDCALL;
            if (m != null) FillModeEx(m);
            if (rotation != null) *rotation = 1;    // D3DDISPLAYROTATION_IDENTITY
            return HResult.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int CreateDeviceEx(IntPtr self, uint adapter, int type, IntPtr focus, uint flags, void* pp, void* mode, IntPtr* device)
            => CreateDeviceCore(self, adapter, device);

        private static int CreateDeviceCore(IntPtr self, uint adapter, IntPtr* device)
        {
            if (device == null) return D3D9Const.D3DERR_INVALIDCALL;
            *device = IntPtr.Zero;
            var d3d = From<Direct3D9>(self);
            if (d3d == null || adapter != 0) return D3D9Const.D3DERR_INVALIDCALL;
            try
            {
                var dev = new Direct3DDevice9(d3d);
                D3DLog.Once("d3d9created", "Direct3D 9Ex device over the shared WebGPU device");
                *device = dev.Pointer;
                return HResult.S_OK;
            }
            catch (Exception e) { D3DLog.Error("CreateDeviceEx", e); return D3D9Const.D3DERR_NOTAVAILABLE; }
        }

        [UnmanagedCallersOnly]
        private static int GetAdapterLUID(IntPtr self, uint adapter, long* luid)
        {
            if (luid == null || adapter != 0) return D3D9Const.D3DERR_INVALIDCALL;
            *luid = 0x5747_5055;      // "WGPU": the same LUID the shim's DXGI adapter reports
            return HResult.S_OK;
        }
    }

    // ---- IDirect3DDevice9Ex ---------------------------------------------------------------------

    internal sealed unsafe class Direct3DDevice9 : ComObject
    {
        private static readonly Vtable s_vt = Build();
        public readonly Direct3D9 Parent;
        /// <summary>The D3D11 device whose textures this device makes (and whose WebGPU device,
        /// shared by every shim device, it copies with).</summary>
        public readonly D3DDevice Device11;
        public Direct3DSurface9? RenderTarget;

        public Direct3DDevice9(Direct3D9 parent) : base(s_vt)
        {
            Parent = parent;
            parent.AddRef();
            Device11 = new D3DDevice(Composition.ProcessGpu.Acquire(), ownsGpu: true, D3D_FEATURE_LEVEL.L11_0, 0, null);
        }

        protected override void OnFinalRelease()
        {
            RenderTarget?.InternalRelease();
            RenderTarget = null;
            Device11.Release();
            Parent.Release();
        }

        private static Direct3DDevice9? Self(IntPtr p) => From<Direct3DDevice9>(p);

        private static Vtable Build()
        {
            var vt = new Vtable("IDirect3DDevice9Ex", Vtbl.IDirect3DDevice9Ex.SlotCount, Vtbl.IDirect3DDevice9Ex.IID, Vtbl.IDirect3DDevice9.IID);
            vt.Set(Vtbl.IDirect3DDevice9Ex.TestCooperativeLevel, (delegate* unmanaged<IntPtr, int>)&Ok0);
            vt.Set(Vtbl.IDirect3DDevice9Ex.GetAvailableTextureMem, (delegate* unmanaged<IntPtr, uint>)&GetAvailableTextureMem);
            vt.Set(Vtbl.IDirect3DDevice9Ex.EvictManagedResources, (delegate* unmanaged<IntPtr, int>)&Ok0);
            vt.Set(Vtbl.IDirect3DDevice9Ex.GetDirect3D, (delegate* unmanaged<IntPtr, IntPtr*, int>)&GetDirect3D);
            vt.Set(Vtbl.IDirect3DDevice9Ex.GetDeviceCaps, (delegate* unmanaged<IntPtr, uint*, int>)&GetDeviceCaps);
            vt.Set(Vtbl.IDirect3DDevice9Ex.GetDisplayMode, (delegate* unmanaged<IntPtr, uint, D3DDISPLAYMODE*, int>)&GetDisplayMode);
            vt.Set(Vtbl.IDirect3DDevice9Ex.Present, (delegate* unmanaged<IntPtr, void*, void*, IntPtr, void*, int>)&Present);
            vt.Set(Vtbl.IDirect3DDevice9Ex.CreateTexture, NativeTwins.Register((IntPtr)(delegate* unmanaged<IntPtr, uint, uint, uint, uint, int, int, IntPtr*, IntPtr*, int>)&CreateTexture, (Func<IntPtr, uint, uint, uint, uint, int, int, IntPtr, IntPtr, int>)((p0, p1, p2, p3, p4, p5, p6, p7, p8) => CreateTextureImpl(p0, p1, p2, p3, p4, p5, p6, (IntPtr*)p7, (IntPtr*)p8))));
            vt.Set(Vtbl.IDirect3DDevice9Ex.CreateRenderTarget, NativeTwins.Register((IntPtr)(delegate* unmanaged<IntPtr, uint, uint, int, int, uint, int, IntPtr*, IntPtr*, int>)&CreateRenderTarget, (Func<IntPtr, uint, uint, int, int, uint, int, IntPtr, IntPtr, int>)((p0, p1, p2, p3, p4, p5, p6, p7, p8) => CreateRenderTargetImpl(p0, p1, p2, p3, p4, p5, p6, (IntPtr*)p7, (IntPtr*)p8))));
            vt.Set(Vtbl.IDirect3DDevice9Ex.CreateDepthStencilSurface, NativeTwins.Register((IntPtr)(delegate* unmanaged<IntPtr, uint, uint, int, int, uint, int, IntPtr*, IntPtr*, int>)&CreateDepthStencilSurface, (Func<IntPtr, uint, uint, int, int, uint, int, IntPtr, IntPtr, int>)((p0, p1, p2, p3, p4, p5, p6, p7, p8) => CreateDepthStencilSurfaceImpl(p0, p1, p2, p3, p4, p5, p6, (IntPtr*)p7, (IntPtr*)p8))));
            vt.Set(Vtbl.IDirect3DDevice9Ex.UpdateSurface, (delegate* unmanaged<IntPtr, IntPtr, D3DRECT9*, IntPtr, int*, int>)&UpdateSurface);
            vt.Set(Vtbl.IDirect3DDevice9Ex.UpdateTexture, (delegate* unmanaged<IntPtr, IntPtr, IntPtr, int>)&UpdateTexture);
            vt.Set(Vtbl.IDirect3DDevice9Ex.GetRenderTargetData, (delegate* unmanaged<IntPtr, IntPtr, IntPtr, int>)&GetRenderTargetData);
            vt.Set(Vtbl.IDirect3DDevice9Ex.StretchRect, (delegate* unmanaged<IntPtr, IntPtr, D3DRECT9*, IntPtr, D3DRECT9*, int, int>)&StretchRect);
            vt.Set(Vtbl.IDirect3DDevice9Ex.ColorFill, (delegate* unmanaged<IntPtr, IntPtr, D3DRECT9*, uint, int>)&ColorFill);
            vt.Set(Vtbl.IDirect3DDevice9Ex.CreateOffscreenPlainSurface, (delegate* unmanaged<IntPtr, uint, uint, int, int, IntPtr*, IntPtr*, int>)&CreateOffscreenPlainSurface);
            vt.Set(Vtbl.IDirect3DDevice9Ex.SetRenderTarget, (delegate* unmanaged<IntPtr, uint, IntPtr, int>)&SetRenderTarget);
            vt.Set(Vtbl.IDirect3DDevice9Ex.GetRenderTarget, (delegate* unmanaged<IntPtr, uint, IntPtr*, int>)&GetRenderTarget);
            vt.Set(Vtbl.IDirect3DDevice9Ex.BeginScene, (delegate* unmanaged<IntPtr, int>)&Ok0);
            vt.Set(Vtbl.IDirect3DDevice9Ex.EndScene, (delegate* unmanaged<IntPtr, int>)&Ok0);
            vt.Set(Vtbl.IDirect3DDevice9Ex.Clear, (delegate* unmanaged<IntPtr, uint, D3DRECT9*, uint, uint, float, uint, int>)&Clear);
            vt.Set(Vtbl.IDirect3DDevice9Ex.CheckDeviceState, (delegate* unmanaged<IntPtr, IntPtr, int>)&CheckDeviceState);
            vt.Set(Vtbl.IDirect3DDevice9Ex.PresentEx, (delegate* unmanaged<IntPtr, void*, void*, IntPtr, void*, uint, int>)&PresentEx);
            vt.Set(Vtbl.IDirect3DDevice9Ex.SetGPUThreadPriority, (delegate* unmanaged<IntPtr, int, int>)&Ok1);
            vt.Set(Vtbl.IDirect3DDevice9Ex.GetGPUThreadPriority, (delegate* unmanaged<IntPtr, int*, int>)&GetGPUThreadPriority);
            vt.Set(Vtbl.IDirect3DDevice9Ex.SetMaximumFrameLatency, (delegate* unmanaged<IntPtr, uint, int>)&Ok1u);
            vt.Set(Vtbl.IDirect3DDevice9Ex.GetMaximumFrameLatency, (delegate* unmanaged<IntPtr, uint*, int>)&GetMaximumFrameLatency);
            vt.Set(Vtbl.IDirect3DDevice9Ex.WaitForVBlank, (delegate* unmanaged<IntPtr, uint, int>)&Ok1u);
            vt.Set(Vtbl.IDirect3DDevice9Ex.CreateRenderTargetEx, NativeTwins.Register((IntPtr)(delegate* unmanaged<IntPtr, uint, uint, int, int, uint, int, IntPtr*, IntPtr*, uint, int>)&CreateRenderTargetEx, (Func<IntPtr, uint, uint, int, int, uint, int, IntPtr, IntPtr, uint, int>)((p0, p1, p2, p3, p4, p5, p6, p7, p8, p9) => CreateRenderTargetExImpl(p0, p1, p2, p3, p4, p5, p6, (IntPtr*)p7, (IntPtr*)p8, p9))));
            vt.Set(Vtbl.IDirect3DDevice9Ex.CreateOffscreenPlainSurfaceEx, (delegate* unmanaged<IntPtr, uint, uint, int, int, IntPtr*, IntPtr*, uint, int>)&CreateOffscreenPlainSurfaceEx);
            vt.Set(Vtbl.IDirect3DDevice9Ex.CreateDepthStencilSurfaceEx, NativeTwins.Register((IntPtr)(delegate* unmanaged<IntPtr, uint, uint, int, int, uint, int, IntPtr*, IntPtr*, uint, int>)&CreateDepthStencilSurfaceEx, (Func<IntPtr, uint, uint, int, int, uint, int, IntPtr, IntPtr, uint, int>)((p0, p1, p2, p3, p4, p5, p6, p7, p8, p9) => CreateDepthStencilSurfaceExImpl(p0, p1, p2, p3, p4, p5, p6, (IntPtr*)p7, (IntPtr*)p8, p9))));
            vt.Set(Vtbl.IDirect3DDevice9Ex.GetDisplayModeEx, (delegate* unmanaged<IntPtr, uint, D3DDISPLAYMODEEX*, int*, int>)&GetDisplayModeEx);
            return vt;
        }

        private static int Guard(string where, Func<int> body)
        {
            try { return body(); }
            catch (NotSupportedException e) { D3DLog.Once("ns9:" + where + e.Message, $"{where}: {e.Message}"); return D3D9Const.D3DERR_NOTAVAILABLE; }
            catch (Exception e) { D3DLog.Error(where, e); return D3D9Const.D3DERR_INVALIDCALL; }
        }

        [UnmanagedCallersOnly] private static int Ok0(IntPtr self) => HResult.S_OK;
        [UnmanagedCallersOnly] private static int Ok1(IntPtr self, int a) => HResult.S_OK;
        [UnmanagedCallersOnly] private static int Ok1u(IntPtr self, uint a) => HResult.S_OK;
        [UnmanagedCallersOnly] private static uint GetAvailableTextureMem(IntPtr self) => 512u << 20;
        [UnmanagedCallersOnly] private static int GetGPUThreadPriority(IntPtr self, int* p) { if (p != null) *p = 0; return HResult.S_OK; }
        [UnmanagedCallersOnly] private static int GetMaximumFrameLatency(IntPtr self, uint* p) { if (p != null) *p = 3; return HResult.S_OK; }
        [UnmanagedCallersOnly] private static int Present(IntPtr self, void* src, void* dst, IntPtr wnd, void* dirty) => HResult.S_OK;
        [UnmanagedCallersOnly] private static int PresentEx(IntPtr self, void* src, void* dst, IntPtr wnd, void* dirty, uint flags) => HResult.S_OK;
        [UnmanagedCallersOnly] private static int CheckDeviceState(IntPtr self, IntPtr wnd) => HResult.S_OK;

        [UnmanagedCallersOnly]
        private static int GetDirect3D(IntPtr self, IntPtr* d3d)
        {
            var d = Self(self);
            if (d == null || d3d == null) return D3D9Const.D3DERR_INVALIDCALL;
            *d3d = d.Parent.AddRefPointer();
            return HResult.S_OK;
        }

        [UnmanagedCallersOnly] private static int GetDeviceCaps(IntPtr self, uint* caps) => Direct3D9.FillCaps(caps, 1);

        [UnmanagedCallersOnly]
        private static int GetDisplayMode(IntPtr self, uint swapChain, D3DDISPLAYMODE* m)
        {
            if (m == null) return D3D9Const.D3DERR_INVALIDCALL;
            *m = Direct3D9.DisplayMode();
            return HResult.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int GetDisplayModeEx(IntPtr self, uint swapChain, D3DDISPLAYMODEEX* m, int* rotation)
        {
            if (m != null) Direct3D9.FillModeEx(m);
            if (rotation != null) *rotation = 1;
            return HResult.S_OK;
        }

        /// <summary>A GPU texture of this device, or the shared one <paramref name="shared"/> names.</summary>
        private D3DTexture2D TextureFor(uint w, uint h, uint levels, uint usage, int format, IntPtr* shared, bool renderTarget)
        {
            if (shared != null && *shared != IntPtr.Zero)
            {
                D3DTexture2D? t = SharedHandles.Find(*shared) ?? throw new ArgumentException($"shared handle 0x{(long)*shared:x} is not one of the shim's");
                if (!ReferenceEquals(t.Device.Gpu, Device11.Gpu))
                    throw new ArgumentException("the shared texture was made on another thread's WebGPU device (devices made on two UI threads share no resources)");
                if (t.Desc.Width != w || t.Desc.Height != h || !D3D9Const.SameTexels(format, t.Desc.Format))
                    throw new ArgumentException($"shared texture is {t.Desc.Width}x{t.Desc.Height} {t.Desc.Format}, opened as {w}x{h} format {format}");
                t.AddRef();
                return t;
            }
            DXGI_FORMAT f = D3D9Const.ToDxgi(format);
            if (f == 0) throw new NotSupportedException($"D3D9 format {format}");
            bool depth = (usage & D3D9Const.D3DUSAGE_DEPTHSTENCIL) != 0;
            var desc = new D3D11_TEXTURE2D_DESC
            {
                Width = w, Height = h, MipLevels = levels, ArraySize = 1, Format = f,
                SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 },
                Usage = D3D11_USAGE.DEFAULT,
                BindFlags = depth ? D3D11_BIND.DEPTH_STENCIL : D3D11_BIND.SHADER_RESOURCE | (renderTarget ? D3D11_BIND.RENDER_TARGET : 0),
                MiscFlags = (shared != null ? D3D11_RESOURCE_MISC.SHARED : 0) | ((usage & D3D9Const.D3DUSAGE_AUTOGENMIPMAP) != 0 ? D3D11_RESOURCE_MISC.GENERATE_MIPS : 0),
            };
            var tex = new D3DTexture2D(Device11, desc, null);
            if (shared != null) *shared = tex.SharedHandle = SharedHandles.Add(tex);
            return tex;
        }

        [UnmanagedCallersOnly]

        private static int CreateTexture(IntPtr self, uint w, uint h, uint levels, uint usage, int format, int pool, IntPtr* texture, IntPtr* shared) => CreateTextureImpl(self, w, h, levels, usage, format, pool, texture, shared);


        // CreateTexture's body, and its managed twin's (NativeTwins.cs)

        private static int CreateTextureImpl(IntPtr self, uint w, uint h, uint levels, uint usage, int format, int pool, IntPtr* texture, IntPtr* shared)
        {
            var d = Self(self);
            if (d == null || texture == null || w == 0 || h == 0) return D3D9Const.D3DERR_INVALIDCALL;
            *texture = IntPtr.Zero;
            return Guard("CreateTexture", () =>
            {
                Direct3DTexture9 t;
                if (pool is D3D9Const.D3DPOOL_SYSTEMMEM or D3D9Const.D3DPOOL_SCRATCH)
                {
                    if (shared != null && *shared != IntPtr.Zero) return D3D9Const.D3DERR_INVALIDCALL;
                    t = Direct3DTexture9.OnCpu(d, w, h, levels, format, pool, usage);
                }
                else
                {
                    D3DTexture2D tex = d.TextureFor(w, h, levels, usage, format, shared, (usage & D3D9Const.D3DUSAGE_RENDERTARGET) != 0);
                    t = new Direct3DTexture9(d, tex, format, pool, usage);
                    tex.Release();      // the texture object holds it
                }
                *texture = t.Pointer;
                return HResult.S_OK;
            });
        }

        private int MakeSurface(uint w, uint h, int format, int pool, uint usage, bool renderTarget, IntPtr* surface, IntPtr* shared)
        {
            if (surface == null || w == 0 || h == 0) return D3D9Const.D3DERR_INVALIDCALL;
            *surface = IntPtr.Zero;
            Direct3DSurface9 s;
            if (pool is D3D9Const.D3DPOOL_SYSTEMMEM or D3D9Const.D3DPOOL_SCRATCH)
            {
                if (shared != null && *shared != IntPtr.Zero) return D3D9Const.D3DERR_INVALIDCALL;
                s = Direct3DSurface9.OnCpu(this, null, w, h, format, pool, usage);
            }
            else
            {
                D3DTexture2D tex = TextureFor(w, h, 1, usage, format, shared, renderTarget);
                s = new Direct3DSurface9(this, null, tex, 0, format, pool, usage);
                tex.Release();
            }
            *surface = s.Pointer;
            return HResult.S_OK;
        }

        [UnmanagedCallersOnly]

        private static int CreateRenderTarget(IntPtr self, uint w, uint h, int format, int ms, uint msq, int lockable, IntPtr* surface, IntPtr* shared) => CreateRenderTargetImpl(self, w, h, format, ms, msq, lockable, surface, shared);


        // CreateRenderTarget's body, and its managed twin's (NativeTwins.cs)

        private static int CreateRenderTargetImpl(IntPtr self, uint w, uint h, int format, int ms, uint msq, int lockable, IntPtr* surface, IntPtr* shared)
        {
            var d = Self(self);
            return d == null ? D3D9Const.D3DERR_INVALIDCALL : Guard("CreateRenderTarget", () => d.MakeSurface(w, h, format, D3D9Const.D3DPOOL_DEFAULT, D3D9Const.D3DUSAGE_RENDERTARGET, true, surface, shared));
        }

        [UnmanagedCallersOnly]

        private static int CreateRenderTargetEx(IntPtr self, uint w, uint h, int format, int ms, uint msq, int lockable, IntPtr* surface, IntPtr* shared, uint usage) => CreateRenderTargetExImpl(self, w, h, format, ms, msq, lockable, surface, shared, usage);


        // CreateRenderTargetEx's body, and its managed twin's (NativeTwins.cs)

        private static int CreateRenderTargetExImpl(IntPtr self, uint w, uint h, int format, int ms, uint msq, int lockable, IntPtr* surface, IntPtr* shared, uint usage)
        {
            var d = Self(self);
            return d == null ? D3D9Const.D3DERR_INVALIDCALL : Guard("CreateRenderTargetEx", () => d.MakeSurface(w, h, format, D3D9Const.D3DPOOL_DEFAULT, usage | D3D9Const.D3DUSAGE_RENDERTARGET, true, surface, shared));
        }

        [UnmanagedCallersOnly]

        private static int CreateDepthStencilSurface(IntPtr self, uint w, uint h, int format, int ms, uint msq, int discard, IntPtr* surface, IntPtr* shared) => CreateDepthStencilSurfaceImpl(self, w, h, format, ms, msq, discard, surface, shared);


        // CreateDepthStencilSurface's body, and its managed twin's (NativeTwins.cs)

        private static int CreateDepthStencilSurfaceImpl(IntPtr self, uint w, uint h, int format, int ms, uint msq, int discard, IntPtr* surface, IntPtr* shared)
        {
            var d = Self(self);
            return d == null ? D3D9Const.D3DERR_INVALIDCALL : Guard("CreateDepthStencilSurface", () => d.MakeSurface(w, h, format, D3D9Const.D3DPOOL_DEFAULT, D3D9Const.D3DUSAGE_DEPTHSTENCIL, false, surface, shared));
        }

        [UnmanagedCallersOnly]

        private static int CreateDepthStencilSurfaceEx(IntPtr self, uint w, uint h, int format, int ms, uint msq, int discard, IntPtr* surface, IntPtr* shared, uint usage) => CreateDepthStencilSurfaceExImpl(self, w, h, format, ms, msq, discard, surface, shared, usage);


        // CreateDepthStencilSurfaceEx's body, and its managed twin's (NativeTwins.cs)

        private static int CreateDepthStencilSurfaceExImpl(IntPtr self, uint w, uint h, int format, int ms, uint msq, int discard, IntPtr* surface, IntPtr* shared, uint usage)
        {
            var d = Self(self);
            return d == null ? D3D9Const.D3DERR_INVALIDCALL : Guard("CreateDepthStencilSurfaceEx", () => d.MakeSurface(w, h, format, D3D9Const.D3DPOOL_DEFAULT, usage | D3D9Const.D3DUSAGE_DEPTHSTENCIL, false, surface, shared));
        }

        [UnmanagedCallersOnly]
        private static int CreateOffscreenPlainSurface(IntPtr self, uint w, uint h, int format, int pool, IntPtr* surface, IntPtr* shared)
        {
            var d = Self(self);
            return d == null ? D3D9Const.D3DERR_INVALIDCALL : Guard("CreateOffscreenPlainSurface", () => d.MakeSurface(w, h, format, pool, 0, false, surface, shared));
        }

        [UnmanagedCallersOnly]
        private static int CreateOffscreenPlainSurfaceEx(IntPtr self, uint w, uint h, int format, int pool, IntPtr* surface, IntPtr* shared, uint usage)
        {
            var d = Self(self);
            return d == null ? D3D9Const.D3DERR_INVALIDCALL : Guard("CreateOffscreenPlainSurfaceEx", () => d.MakeSurface(w, h, format, pool, usage, false, surface, shared));
        }

        [UnmanagedCallersOnly]
        private static int UpdateSurface(IntPtr self, IntPtr src, D3DRECT9* srcRect, IntPtr dst, int* dstPoint)
        {
            var s = From<Direct3DSurface9>(src);
            var t = From<Direct3DSurface9>(dst);
            if (s == null || t == null || !s.OnCpuSide || t.OnCpuSide) return D3D9Const.D3DERR_INVALIDCALL;
            D3DRECT9 r = srcRect != null ? *srcRect : new D3DRECT9 { right = (int)s.Width, bottom = (int)s.Height };
            int dx = dstPoint != null ? dstPoint[0] : 0, dy = dstPoint != null ? dstPoint[1] : 0;
            return Guard("UpdateSurface", () => t.WriteFrom(s, r, dx, dy));
        }

        [UnmanagedCallersOnly]
        private static int UpdateTexture(IntPtr self, IntPtr src, IntPtr dst)
        {
            var s = From<Direct3DTexture9>(src);
            var t = From<Direct3DTexture9>(dst);
            if (s == null || t == null) return D3D9Const.D3DERR_INVALIDCALL;
            return Guard("UpdateTexture", () =>
            {
                int n = (int)Math.Min(s.Levels, t.Levels);
                for (int i = 0; i < n; i++)
                {
                    Direct3DSurface9 a = s.Level(i), b = t.Level(i);
                    int hr = b.WriteFrom(a, new D3DRECT9 { right = (int)a.Width, bottom = (int)a.Height }, 0, 0);
                    if (hr < 0) return hr;
                }
                return HResult.S_OK;
            });
        }

        [UnmanagedCallersOnly]
        private static int GetRenderTargetData(IntPtr self, IntPtr rt, IntPtr dst)
        {
            var s = From<Direct3DSurface9>(rt);
            var t = From<Direct3DSurface9>(dst);
            if (s == null || t == null || s.OnCpuSide || !t.OnCpuSide || s.Width != t.Width || s.Height != t.Height) return D3D9Const.D3DERR_INVALIDCALL;
            return Guard("GetRenderTargetData", () => t.ReadFrom(s));
        }

        [UnmanagedCallersOnly]
        private static int StretchRect(IntPtr self, IntPtr src, D3DRECT9* srcRect, IntPtr dst, D3DRECT9* dstRect, int filter)
        {
            var s = From<Direct3DSurface9>(src);
            var t = From<Direct3DSurface9>(dst);
            if (s == null || t == null || t.OnCpuSide) return D3D9Const.D3DERR_INVALIDCALL;
            D3DRECT9 a = srcRect != null ? *srcRect : new D3DRECT9 { right = (int)s.Width, bottom = (int)s.Height };
            D3DRECT9 b = dstRect != null ? *dstRect : new D3DRECT9 { right = (int)t.Width, bottom = (int)t.Height };
            return Guard("StretchRect", () => t.Stretch(s, a, b, filter));
        }

        [UnmanagedCallersOnly]
        private static int ColorFill(IntPtr self, IntPtr surface, D3DRECT9* rect, uint color)
        {
            var s = From<Direct3DSurface9>(surface);
            if (s == null) return D3D9Const.D3DERR_INVALIDCALL;
            D3DRECT9 r = rect != null ? *rect : new D3DRECT9 { right = (int)s.Width, bottom = (int)s.Height };
            return Guard("ColorFill", () => s.Fill(r, color));
        }

        [UnmanagedCallersOnly]
        private static int SetRenderTarget(IntPtr self, uint index, IntPtr surface)
        {
            var d = Self(self);
            if (d == null || index != 0) return index < 4 ? HResult.S_OK : D3D9Const.D3DERR_INVALIDCALL;
            var s = From<Direct3DSurface9>(surface);
            s?.InternalAddRef();
            d.RenderTarget?.InternalRelease();
            d.RenderTarget = s;
            return HResult.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int GetRenderTarget(IntPtr self, uint index, IntPtr* surface)
        {
            var d = Self(self);
            if (d == null || surface == null) return D3D9Const.D3DERR_INVALIDCALL;
            *surface = IntPtr.Zero;
            if (index != 0 || d.RenderTarget == null) return D3D9Const.D3DERR_NOTAVAILABLE;
            *surface = d.RenderTarget.AddRefPointer();
            return HResult.S_OK;
        }

        /// <summary>Clear: D3DCLEAR_TARGET (1) fills the render target; depth and stencil have
        /// nothing here to clear.</summary>
        [UnmanagedCallersOnly]
        private static int Clear(IntPtr self, uint count, D3DRECT9* rects, uint flags, uint color, float z, uint stencil)
        {
            var d = Self(self);
            if (d == null) return D3D9Const.D3DERR_INVALIDCALL;
            if ((flags & 1) == 0 || d.RenderTarget == null) return HResult.S_OK;
            Direct3DSurface9 rt = d.RenderTarget;
            return Guard("Clear", () =>
            {
                if (count == 0 || rects == null) return rt.Fill(new D3DRECT9 { right = (int)rt.Width, bottom = (int)rt.Height }, color);
                for (uint i = 0; i < count; i++) { int hr = rt.Fill(rects[i], color); if (hr < 0) return hr; }
                return HResult.S_OK;
            });
        }
    }

    // ---- IDirect3DTexture9 ----------------------------------------------------------------------

    internal sealed unsafe class Direct3DTexture9 : ComObject
    {
        private static readonly Vtable s_vt = Build();
        public readonly Direct3DDevice9 Device;
        public readonly D3DTexture2D? Gpu;
        public readonly int Format, Pool;
        public readonly uint Usage, Width, Height, Levels;
        private readonly Direct3DSurface9[] _levels;
        public readonly PrivateData Data = new();
        private uint _priority, _lod;

        public Direct3DTexture9(Direct3DDevice9 device, D3DTexture2D tex, int format, int pool, uint usage) : base(s_vt)
        {
            Device = device; device.InternalAddRef();
            Gpu = tex; tex.AddRef();
            Format = format; Pool = pool; Usage = usage;
            Width = tex.Desc.Width; Height = tex.Desc.Height; Levels = tex.MipLevels;
            _levels = new Direct3DSurface9[Levels];
            for (int i = 0; i < Levels; i++)
            {
                _levels[i] = new Direct3DSurface9(device, this, tex, (uint)i, format, pool, usage);
                _levels[i].MakeInternal();
            }
        }

        private Direct3DTexture9(Direct3DDevice9 device, uint w, uint h, uint levels, int format, int pool, uint usage) : base(s_vt)
        {
            Device = device; device.InternalAddRef();
            Format = format; Pool = pool; Usage = usage; Width = w; Height = h;
            uint max = 1 + (uint)Math.Floor(Math.Log2(Math.Max(w, h)));
            Levels = levels == 0 ? max : Math.Min(levels, max);
            _levels = new Direct3DSurface9[Levels];
            for (int i = 0; i < Levels; i++)
            {
                _levels[i] = Direct3DSurface9.OnCpu(device, this, Math.Max(1, w >> i), Math.Max(1, h >> i), format, pool, usage);
                _levels[i].MakeInternal();
            }
        }

        public static Direct3DTexture9 OnCpu(Direct3DDevice9 device, uint w, uint h, uint levels, int format, int pool, uint usage)
            => new(device, w, h, levels, format, pool, usage);

        public Direct3DSurface9 Level(int i) => _levels[i];

        protected override void OnFinalRelease()
        {
            foreach (Direct3DSurface9 s in _levels) s.ReleaseInternal();
            Gpu?.Release();
            Data.ReleaseAll();
            Device.InternalRelease();
        }

        private static Direct3DTexture9? Self(IntPtr p) => From<Direct3DTexture9>(p);

        private static Vtable Build()
        {
            var vt = new Vtable("IDirect3DTexture9", Vtbl.IDirect3DTexture9.SlotCount, Vtbl.IDirect3DTexture9.IID, Vtbl.IDirect3DBaseTexture9.IID, Vtbl.IDirect3DResource9.IID);
            vt.Set(Vtbl.IDirect3DTexture9.GetDevice, (delegate* unmanaged<IntPtr, IntPtr*, int>)&GetDevice);
            vt.Set(Vtbl.IDirect3DTexture9.SetPrivateData, (delegate* unmanaged<IntPtr, Guid*, void*, uint, uint, int>)&SetPrivateData);
            vt.Set(Vtbl.IDirect3DTexture9.GetPrivateData, (delegate* unmanaged<IntPtr, Guid*, void*, uint*, int>)&GetPrivateData);
            vt.Set(Vtbl.IDirect3DTexture9.FreePrivateData, (delegate* unmanaged<IntPtr, Guid*, int>)&FreePrivateData);
            vt.Set(Vtbl.IDirect3DTexture9.SetPriority, (delegate* unmanaged<IntPtr, uint, uint>)&SetPriority);
            vt.Set(Vtbl.IDirect3DTexture9.GetPriority, (delegate* unmanaged<IntPtr, uint>)&GetPriority);
            vt.Set(Vtbl.IDirect3DTexture9.PreLoad, (delegate* unmanaged<IntPtr, void>)&PreLoad);
            vt.Set(Vtbl.IDirect3DTexture9.GetType, (delegate* unmanaged<IntPtr, int>)&GetType);
            vt.Set(Vtbl.IDirect3DTexture9.SetLOD, (delegate* unmanaged<IntPtr, uint, uint>)&SetLOD);
            vt.Set(Vtbl.IDirect3DTexture9.GetLOD, (delegate* unmanaged<IntPtr, uint>)&GetLOD);
            vt.Set(Vtbl.IDirect3DTexture9.GetLevelCount, (delegate* unmanaged<IntPtr, uint>)&GetLevelCount);
            vt.Set(Vtbl.IDirect3DTexture9.SetAutoGenFilterType, (delegate* unmanaged<IntPtr, int, int>)&SetAutoGenFilterType);
            vt.Set(Vtbl.IDirect3DTexture9.GetAutoGenFilterType, (delegate* unmanaged<IntPtr, int>)&GetAutoGenFilterType);
            vt.Set(Vtbl.IDirect3DTexture9.GenerateMipSubLevels, (delegate* unmanaged<IntPtr, void>)&PreLoad);
            vt.Set(Vtbl.IDirect3DTexture9.GetLevelDesc, (delegate* unmanaged<IntPtr, uint, D3DSURFACE_DESC*, int>)&GetLevelDesc);
            vt.Set(Vtbl.IDirect3DTexture9.GetSurfaceLevel, (delegate* unmanaged<IntPtr, uint, IntPtr*, int>)&GetSurfaceLevel);
            vt.Set(Vtbl.IDirect3DTexture9.LockRect, (delegate* unmanaged<IntPtr, uint, D3DLOCKED_RECT*, D3DRECT9*, uint, int>)&LockRect);
            vt.Set(Vtbl.IDirect3DTexture9.UnlockRect, (delegate* unmanaged<IntPtr, uint, int>)&UnlockRect);
            vt.Set(Vtbl.IDirect3DTexture9.AddDirtyRect, (delegate* unmanaged<IntPtr, D3DRECT9*, int>)&AddDirtyRect);
            return vt;
        }

        [UnmanagedCallersOnly]
        private static int GetDevice(IntPtr self, IntPtr* device)
        {
            var t = Self(self);
            if (t == null || device == null) return D3D9Const.D3DERR_INVALIDCALL;
            *device = t.Device.AddRefPointer();
            return HResult.S_OK;
        }

        [UnmanagedCallersOnly] private static int SetPrivateData(IntPtr self, Guid* g, void* d, uint n, uint flags) => Self(self)?.Data.Set(g, n, d) ?? D3D9Const.D3DERR_INVALIDCALL;
        [UnmanagedCallersOnly] private static int GetPrivateData(IntPtr self, Guid* g, void* d, uint* n) => Self(self)?.Data.Get(g, n, d) ?? D3D9Const.D3DERR_INVALIDCALL;
        [UnmanagedCallersOnly] private static int FreePrivateData(IntPtr self, Guid* g) => Self(self)?.Data.Set(g, 0, null) ?? D3D9Const.D3DERR_INVALIDCALL;
        [UnmanagedCallersOnly] private static uint SetPriority(IntPtr self, uint p) { var t = Self(self); if (t == null) return 0; uint o = t._priority; t._priority = p; return o; }
        [UnmanagedCallersOnly] private static uint GetPriority(IntPtr self) => Self(self)?._priority ?? 0;
        [UnmanagedCallersOnly] private static void PreLoad(IntPtr self) { }
        [UnmanagedCallersOnly] private static int GetType(IntPtr self) => D3D9Const.D3DRTYPE_TEXTURE;
        [UnmanagedCallersOnly] private static uint SetLOD(IntPtr self, uint lod) { var t = Self(self); if (t == null) return 0; uint o = t._lod; t._lod = lod; return o; }
        [UnmanagedCallersOnly] private static uint GetLOD(IntPtr self) => Self(self)?._lod ?? 0;
        [UnmanagedCallersOnly] private static uint GetLevelCount(IntPtr self) => Self(self)?.Levels ?? 0;
        [UnmanagedCallersOnly] private static int SetAutoGenFilterType(IntPtr self, int f) => HResult.S_OK;
        [UnmanagedCallersOnly] private static int GetAutoGenFilterType(IntPtr self) => 2;   // D3DTEXF_LINEAR

        [UnmanagedCallersOnly]
        private static int GetLevelDesc(IntPtr self, uint level, D3DSURFACE_DESC* desc)
        {
            var t = Self(self);
            if (t == null || desc == null || level >= t.Levels) return D3D9Const.D3DERR_INVALIDCALL;
            *desc = t._levels[level].Desc;
            return HResult.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int GetSurfaceLevel(IntPtr self, uint level, IntPtr* surface)
        {
            var t = Self(self);
            if (t == null || surface == null || level >= t.Levels) return D3D9Const.D3DERR_INVALIDCALL;
            *surface = t._levels[level].AddRefPointer();
            return HResult.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int LockRect(IntPtr self, uint level, D3DLOCKED_RECT* locked, D3DRECT9* rect, uint flags)
        {
            var t = Self(self);
            if (t == null || level >= t.Levels) return D3D9Const.D3DERR_INVALIDCALL;
            return t._levels[level].Lock(locked, rect, flags);
        }

        [UnmanagedCallersOnly]
        private static int UnlockRect(IntPtr self, uint level)
        {
            var t = Self(self);
            if (t == null || level >= t.Levels) return D3D9Const.D3DERR_INVALIDCALL;
            return t._levels[level].Unlock();
        }

        [UnmanagedCallersOnly] private static int AddDirtyRect(IntPtr self, D3DRECT9* rect) => HResult.S_OK;
    }

    // ---- IDirect3DSurface9 ----------------------------------------------------------------------

    /// <summary>
    /// A surface: a level of a GPU texture (D3DTexture2D), or CPU memory (system-memory and scratch
    /// pools). A GPU surface locks through a CPU copy: read back on LockRect, written back on
    /// UnlockRect unless the lock was read-only.
    /// </summary>
    internal sealed unsafe class Direct3DSurface9 : ComObject
    {
        private static readonly Vtable s_vt = Build();
        public readonly Direct3DDevice9 Device;
        public readonly Direct3DTexture9? Container;
        public readonly D3DTexture2D? Gpu;
        public readonly uint Level;
        public readonly D3DSURFACE_DESC Desc;
        public readonly uint Bpp;
        private byte* _cpu;                 // CPU pools: the surface; GPU: the lock's copy
        private int _pitch;
        private bool _locked, _lockReadOnly;
        private D3DRECT9 _lockRect;
        public readonly PrivateData Data = new();
        private uint _priority;
        private bool _internal;

        public uint Width => Desc.Width;
        public uint Height => Desc.Height;
        public bool OnCpuSide => Gpu == null;

        public Direct3DSurface9(Direct3DDevice9 device, Direct3DTexture9? container, D3DTexture2D tex, uint level, int format, int pool, uint usage) : base(s_vt)
        {
            Device = device; device.InternalAddRef();
            Container = container;
            Gpu = tex; tex.AddRef();
            Level = level;
            Desc = new D3DSURFACE_DESC
            {
                Format = format, Type = D3D9Const.D3DRTYPE_SURFACE, Usage = usage, Pool = pool,
                Width = Math.Max(1, tex.Desc.Width >> (int)level), Height = Math.Max(1, tex.Desc.Height >> (int)level),
            };
            Bpp = Formats.TexelBytes(tex.Desc.Format);
        }

        private Direct3DSurface9(Direct3DDevice9 device, Direct3DTexture9? container, uint w, uint h, int format, int pool, uint usage) : base(s_vt)
        {
            Device = device; device.InternalAddRef();
            Container = container;
            DXGI_FORMAT f = D3D9Const.ToDxgi(format);
            if (f == 0) throw new NotSupportedException($"D3D9 format {format}");
            Bpp = Formats.TexelBytes(f);
            Desc = new D3DSURFACE_DESC { Format = format, Type = D3D9Const.D3DRTYPE_SURFACE, Usage = usage, Pool = pool, Width = w, Height = h };
            _pitch = (int)((w * Bpp + 3) & ~3u);
            _cpu = (byte*)NativeMemory.AllocZeroed((nuint)(_pitch * h));
        }

        public static Direct3DSurface9 OnCpu(Direct3DDevice9 device, Direct3DTexture9? container, uint w, uint h, int format, int pool, uint usage)
            => new(device, container, w, h, format, pool, usage);

        /// <summary>A texture's level: owned by its texture, and while the application holds the
        /// level, the level holds the texture (D3D9's "a surface level's count is its texture's").</summary>
        public void MakeInternal() { _internal = true; InternalAddRef(); Release(); }

        public void ReleaseInternal() => InternalRelease();

        private bool _holdsContainer;

        protected override void OnReferenced()
        {
            if (_internal && Container != null && !_holdsContainer) { Container.AddRef(); _holdsContainer = true; }
        }

        protected override bool OnNoReferences()
        {
            if (!_internal) return false;
            if (_holdsContainer) { _holdsContainer = false; Container?.Release(); }
            return false;      // finalised only when the texture lets go too (its internal reference)
        }

        protected override void OnFinalRelease()
        {
            if (_cpu != null) NativeMemory.Free(_cpu);
            _cpu = null;
            Gpu?.Release();
            Data.ReleaseAll();
            Device.InternalRelease();
        }

        private static Direct3DSurface9? Self(IntPtr p) => From<Direct3DSurface9>(p);

        private static Vtable Build()
        {
            var vt = new Vtable("IDirect3DSurface9", Vtbl.IDirect3DSurface9.SlotCount, Vtbl.IDirect3DSurface9.IID, Vtbl.IDirect3DResource9.IID);
            vt.Set(Vtbl.IDirect3DSurface9.GetDevice, (delegate* unmanaged<IntPtr, IntPtr*, int>)&GetDevice);
            vt.Set(Vtbl.IDirect3DSurface9.SetPrivateData, (delegate* unmanaged<IntPtr, Guid*, void*, uint, uint, int>)&SetPrivateData);
            vt.Set(Vtbl.IDirect3DSurface9.GetPrivateData, (delegate* unmanaged<IntPtr, Guid*, void*, uint*, int>)&GetPrivateData);
            vt.Set(Vtbl.IDirect3DSurface9.FreePrivateData, (delegate* unmanaged<IntPtr, Guid*, int>)&FreePrivateData);
            vt.Set(Vtbl.IDirect3DSurface9.SetPriority, (delegate* unmanaged<IntPtr, uint, uint>)&SetPriority);
            vt.Set(Vtbl.IDirect3DSurface9.GetPriority, (delegate* unmanaged<IntPtr, uint>)&GetPriority);
            vt.Set(Vtbl.IDirect3DSurface9.PreLoad, (delegate* unmanaged<IntPtr, void>)&PreLoad);
            vt.Set(Vtbl.IDirect3DSurface9.GetType, (delegate* unmanaged<IntPtr, int>)&GetType);
            vt.Set(Vtbl.IDirect3DSurface9.GetContainer, (delegate* unmanaged<IntPtr, Guid*, IntPtr*, int>)&GetContainer);
            vt.Set(Vtbl.IDirect3DSurface9.GetDesc, (delegate* unmanaged<IntPtr, D3DSURFACE_DESC*, int>)&GetDesc);
            vt.Set(Vtbl.IDirect3DSurface9.LockRect, (delegate* unmanaged<IntPtr, D3DLOCKED_RECT*, D3DRECT9*, uint, int>)&LockRect);
            vt.Set(Vtbl.IDirect3DSurface9.UnlockRect, (delegate* unmanaged<IntPtr, int>)&UnlockRect);
            return vt;
        }

        [UnmanagedCallersOnly]
        private static int GetDevice(IntPtr self, IntPtr* device)
        {
            var s = Self(self);
            if (s == null || device == null) return D3D9Const.D3DERR_INVALIDCALL;
            *device = s.Device.AddRefPointer();
            return HResult.S_OK;
        }

        [UnmanagedCallersOnly] private static int SetPrivateData(IntPtr self, Guid* g, void* d, uint n, uint flags) => Self(self)?.Data.Set(g, n, d) ?? D3D9Const.D3DERR_INVALIDCALL;
        [UnmanagedCallersOnly] private static int GetPrivateData(IntPtr self, Guid* g, void* d, uint* n) => Self(self)?.Data.Get(g, n, d) ?? D3D9Const.D3DERR_INVALIDCALL;
        [UnmanagedCallersOnly] private static int FreePrivateData(IntPtr self, Guid* g) => Self(self)?.Data.Set(g, 0, null) ?? D3D9Const.D3DERR_INVALIDCALL;
        [UnmanagedCallersOnly] private static uint SetPriority(IntPtr self, uint p) { var s = Self(self); if (s == null) return 0; uint o = s._priority; s._priority = p; return o; }
        [UnmanagedCallersOnly] private static uint GetPriority(IntPtr self) => Self(self)?._priority ?? 0;
        [UnmanagedCallersOnly] private static void PreLoad(IntPtr self) { }
        [UnmanagedCallersOnly] private static int GetType(IntPtr self) => D3D9Const.D3DRTYPE_SURFACE;

        [UnmanagedCallersOnly]
        private static int GetContainer(IntPtr self, Guid* iid, IntPtr* container)
        {
            var s = Self(self);
            if (s == null || iid == null || container == null) return D3D9Const.D3DERR_INVALIDCALL;
            ComObject owner = (ComObject?)s.Container ?? s.Device;
            return D3DDevice.QueryInterfaceOf(owner, *iid, container);
        }

        [UnmanagedCallersOnly]
        private static int GetDesc(IntPtr self, D3DSURFACE_DESC* desc)
        {
            var s = Self(self);
            if (s == null || desc == null) return D3D9Const.D3DERR_INVALIDCALL;
            *desc = s.Desc;
            return HResult.S_OK;
        }

        [UnmanagedCallersOnly]
        private static int LockRect(IntPtr self, D3DLOCKED_RECT* locked, D3DRECT9* rect, uint flags) => Self(self)?.Lock(locked, rect, flags) ?? D3D9Const.D3DERR_INVALIDCALL;

        [UnmanagedCallersOnly]
        private static int UnlockRect(IntPtr self) => Self(self)?.Unlock() ?? D3D9Const.D3DERR_INVALIDCALL;

        // ---- the operations ------------------------------------------------------------------------

        private D3DRECT9 Whole => new() { right = (int)Width, bottom = (int)Height };

        private static bool Inside(D3DRECT9 r, uint w, uint h) => r.left >= 0 && r.top >= 0 && r.right <= w && r.bottom <= h && r.left < r.right && r.top < r.bottom;

        public int Lock(D3DLOCKED_RECT* locked, D3DRECT9* rect, uint flags)
        {
            if (locked == null) return D3D9Const.D3DERR_INVALIDCALL;
            lock (this)
            {
                if (_locked) return D3D9Const.D3DERR_INVALIDCALL;
                D3DRECT9 r = rect != null ? *rect : Whole;
                if (!Inside(r, Width, Height)) return D3D9Const.D3DERR_INVALIDCALL;
                if (Gpu != null)
                {
                    // the lock's copy: what the texture holds now (not for a discarding write)
                    _pitch = (int)((Width * Bpp + 3) & ~3u);
                    _cpu = (byte*)NativeMemory.AllocZeroed((nuint)(_pitch * Height));
                    if ((flags & D3D9Const.D3DLOCK_DISCARD) == 0)
                    {
                        try { ReadGpu(_cpu, _pitch); }
                        catch (Exception e) { D3DLog.Error("LockRect", e); NativeMemory.Free(_cpu); _cpu = null; return D3D9Const.D3DERR_INVALIDCALL; }
                    }
                }
                _locked = true;
                _lockReadOnly = (flags & D3D9Const.D3DLOCK_READONLY) != 0;
                _lockRect = r;
                locked->Pitch = _pitch;
                locked->pBits = _cpu + (long)r.top * _pitch + r.left * Bpp;
                return HResult.S_OK;
            }
        }

        public int Unlock()
        {
            lock (this)
            {
                if (!_locked) return D3D9Const.D3DERR_INVALIDCALL;
                _locked = false;
                if (Gpu != null)
                {
                    try { if (!_lockReadOnly) WriteGpu(_cpu, _pitch, _lockRect, _lockRect.left, _lockRect.top); }
                    finally { NativeMemory.Free(_cpu); _cpu = null; }
                }
                return HResult.S_OK;
            }
        }

        /// <summary>The GPU level's texels into CPU memory with the given pitch (a queue copy into a
        /// mappable buffer, waited for: GetRenderTargetData is synchronous in D3D9).</summary>
        private void ReadGpu(byte* dst, int pitch)
        {
            WgpuContext gpu = Gpu!.Device.Gpu;
            uint rowBytes = Width * Bpp;
            uint aligned = (rowBytes + 255) & ~255u;
            ulong size = (ulong)aligned * Height;
            IntPtr buf = gpu.CreateBuffer(size, WGPUBufferUsage.MapRead | WGPUBufferUsage.CopyDst);
            try
            {
                IntPtr enc = wgpuDeviceCreateCommandEncoder(gpu.Device, IntPtr.Zero);
                var from = new WGPUTexelCopyTextureInfo { texture = Gpu.Gpu, mipLevel = Level, aspect = WGPUTextureAspect.All };
                var to = new WGPUTexelCopyBufferInfo { buffer = buf, layout = new WGPUTexelCopyBufferLayout { bytesPerRow = aligned, rowsPerImage = Height } };
                var extent = new WGPUExtent3D { width = Width, height = Height, depthOrArrayLayers = 1 };
                wgpuCommandEncoderCopyTextureToBuffer(enc, &from, &to, &extent);
                IntPtr cmd = wgpuCommandEncoderFinish(enc, IntPtr.Zero);
                wgpuCommandEncoderRelease(enc);
                wgpuQueueSubmit(gpu.Queue, 1, &cmd);
                wgpuCommandBufferRelease(cmd);
                byte[] px = gpu.MapRead(buf, size);
                fixed (byte* src = px)
                    for (uint y = 0; y < Height; y++)
                        Buffer.MemoryCopy(src + (long)y * aligned, dst + (long)y * pitch, rowBytes, rowBytes);
            }
            finally { wgpuBufferRelease(buf); }
        }

        /// <summary>CPU texels (rectangle <paramref name="src"/> of memory laid out with
        /// <paramref name="pitch"/>, whole-surface coordinates) into the GPU level at (dx, dy).</summary>
        private void WriteGpu(byte* mem, int pitch, D3DRECT9 src, int dx, int dy)
        {
            WgpuContext gpu = Gpu!.Device.Gpu;
            uint w = (uint)(src.right - src.left), h = (uint)(src.bottom - src.top);
            byte* first = mem + (long)src.top * pitch + src.left * Bpp;
            D3DTexture2D.Upload(gpu, Gpu.Gpu, Level, new WGPUOrigin3D { x = (uint)dx, y = (uint)dy }, first, (uint)pitch, h, w * (uint)Bpp, 0,
                                new WGPUExtent3D { width = w, height = h, depthOrArrayLayers = 1 });
            System.Threading.Interlocked.Increment(ref Gpu.Version);
        }

        /// <summary>GetRenderTargetData: this CPU surface := the GPU surface <paramref name="rt"/>.</summary>
        public int ReadFrom(Direct3DSurface9 rt)
        {
            if (Bpp != rt.Bpp) return D3D9Const.D3DERR_INVALIDCALL;
            lock (this) rt.ReadGpu(_cpu, _pitch);
            return HResult.S_OK;
        }

        /// <summary>UpdateSurface / UpdateTexture: a CPU surface's rectangle into this GPU surface.</summary>
        public int WriteFrom(Direct3DSurface9 cpu, D3DRECT9 r, int dx, int dy)
        {
            if (Bpp != cpu.Bpp || !Inside(r, cpu.Width, cpu.Height)) return D3D9Const.D3DERR_INVALIDCALL;
            if (dx < 0 || dy < 0 || dx + (r.right - r.left) > Width || dy + (r.bottom - r.top) > Height) return D3D9Const.D3DERR_INVALIDCALL;
            if (Gpu == null)
            {
                for (int y = 0; y < r.bottom - r.top; y++)
                    Buffer.MemoryCopy(cpu._cpu + (long)(r.top + y) * cpu._pitch + r.left * Bpp, _cpu + (long)(dy + y) * _pitch + dx * Bpp, (r.right - r.left) * Bpp, (r.right - r.left) * Bpp);
                return HResult.S_OK;
            }
            WriteGpu(cpu._cpu, cpu._pitch, r, dx, dy);
            return HResult.S_OK;
        }

        /// <summary>ColorFill: an A8R8G8B8 colour, converted to the surface's format (the 8-bit
        /// RGBA/BGRA formats, as D3D9 drivers fill; others are refused).</summary>
        public int Fill(D3DRECT9 r, uint argb)
        {
            if (!Inside(r, Width, Height)) return D3D9Const.D3DERR_INVALIDCALL;
            DXGI_FORMAT f = D3D9Const.ToDxgi(Desc.Format);
            uint texel;
            if (f is DXGI_FORMAT.B8G8R8A8_UNORM or DXGI_FORMAT.B8G8R8X8_UNORM) texel = argb;
            else if (f == DXGI_FORMAT.R8G8B8A8_UNORM) texel = (argb & 0xFF00FF00) | ((argb >> 16) & 0xFF) | ((argb & 0xFF) << 16);
            else throw new NotSupportedException($"ColorFill of D3D9 format {Desc.Format}");
            int w = r.right - r.left, h = r.bottom - r.top;
            if (Gpu == null)
            {
                lock (this)
                    for (int y = 0; y < h; y++)
                        new Span<uint>(_cpu + (long)(r.top + y) * _pitch + r.left * 4, w).Fill(texel);
                return HResult.S_OK;
            }
            int pitch = w * 4;
            var rows = (uint*)NativeMemory.Alloc((nuint)(pitch * h));
            try
            {
                new Span<uint>(rows, w * h).Fill(texel);
                WriteGpu((byte*)rows, pitch, new D3DRECT9 { left = 0, top = 0, right = w, bottom = h }, r.left, r.top);
            }
            finally { NativeMemory.Free(rows); }
            return HResult.S_OK;
        }

        /// <summary>StretchRect: a GPU-to-GPU copy when the rectangles are the same size (the texture
        /// copy), else through the CPU, point- or bilinear-sampled as asked.</summary>
        public int Stretch(Direct3DSurface9 src, D3DRECT9 a, D3DRECT9 b, int filter)
        {
            if (!Inside(a, src.Width, src.Height) || !Inside(b, Width, Height) || src.Bpp != Bpp) return D3D9Const.D3DERR_INVALIDCALL;
            int sw = a.right - a.left, sh = a.bottom - a.top, dw = b.right - b.left, dh = b.bottom - b.top;
            if (src.Gpu != null && Gpu != null && sw == dw && sh == dh)
            {
                WgpuContext gpu = Gpu.Device.Gpu;
                IntPtr enc = wgpuDeviceCreateCommandEncoder(gpu.Device, IntPtr.Zero);
                var from = new WGPUTexelCopyTextureInfo { texture = src.Gpu.Gpu, mipLevel = src.Level, origin = new WGPUOrigin3D { x = (uint)a.left, y = (uint)a.top }, aspect = WGPUTextureAspect.All };
                var to = new WGPUTexelCopyTextureInfo { texture = Gpu.Gpu, mipLevel = Level, origin = new WGPUOrigin3D { x = (uint)b.left, y = (uint)b.top }, aspect = WGPUTextureAspect.All };
                var extent = new WGPUExtent3D { width = (uint)sw, height = (uint)sh, depthOrArrayLayers = 1 };
                wgpuCommandEncoderCopyTextureToTexture(enc, &from, &to, &extent);
                IntPtr cmd = wgpuCommandEncoderFinish(enc, IntPtr.Zero);
                wgpuCommandEncoderRelease(enc);
                wgpuQueueSubmit(gpu.Queue, 1, &cmd);
                wgpuCommandBufferRelease(cmd);
                System.Threading.Interlocked.Increment(ref Gpu.Version);
                return HResult.S_OK;
            }
            if (Bpp != 4) throw new NotSupportedException("a scaling StretchRect of a format other than 32-bit");
            // through the CPU
            int spitch = (int)(src.Width * 4);
            var s = (byte*)NativeMemory.Alloc((nuint)(spitch * src.Height));
            var d = (uint*)NativeMemory.Alloc((nuint)(dw * dh * 4));
            try
            {
                if (src.Gpu != null) src.ReadGpu(s, spitch);
                else for (uint y = 0; y < src.Height; y++) Buffer.MemoryCopy(src._cpu + (long)y * src._pitch, s + (long)y * spitch, spitch, spitch);
                bool linear = filter == 2;   // D3DTEXF_LINEAR
                for (int y = 0; y < dh; y++)
                    for (int x = 0; x < dw; x++)
                    {
                        float u = a.left + (x + 0.5f) * sw / dw - 0.5f, v = a.top + (y + 0.5f) * sh / dh - 0.5f;
                        d[y * dw + x] = linear ? Bilinear(s, spitch, a, u, v) : *(uint*)(s + (long)Math.Clamp((int)MathF.Round(v), a.top, a.bottom - 1) * spitch + Math.Clamp((int)MathF.Round(u), a.left, a.right - 1) * 4);
                    }
                if (Gpu != null) WriteGpu((byte*)d, dw * 4, new D3DRECT9 { right = dw, bottom = dh }, b.left, b.top);
                else
                    for (int y = 0; y < dh; y++) Buffer.MemoryCopy(d + y * dw, _cpu + (long)(b.top + y) * _pitch + b.left * 4, dw * 4, dw * 4);
            }
            finally { NativeMemory.Free(s); NativeMemory.Free(d); }
            return HResult.S_OK;
        }

        private static uint Bilinear(byte* s, int pitch, D3DRECT9 a, float u, float v)
        {
            int x0 = (int)MathF.Floor(u), y0 = (int)MathF.Floor(v);
            float fx = u - x0, fy = v - y0;
            uint T(int x, int y) => *(uint*)(s + (long)Math.Clamp(y, a.top, a.bottom - 1) * pitch + Math.Clamp(x, a.left, a.right - 1) * 4);
            uint c00 = T(x0, y0), c10 = T(x0 + 1, y0), c01 = T(x0, y0 + 1), c11 = T(x0 + 1, y0 + 1);
            uint r = 0;
            for (int sh = 0; sh < 32; sh += 8)
            {
                float top = ((c00 >> sh) & 0xFF) * (1 - fx) + ((c10 >> sh) & 0xFF) * fx;
                float bot = ((c01 >> sh) & 0xFF) * (1 - fx) + ((c11 >> sh) & 0xFF) * fx;
                r |= (uint)Math.Clamp((int)MathF.Round(top * (1 - fy) + bot * fy), 0, 255) << sh;
            }
            return r;
        }
    }
}
