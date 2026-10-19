// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The Direct3D 9Ex shim (Documentation/d3d11-webgpu-shim.md, section 7): what applications use D3D9Ex
// for -- a surface for WPF's D3DImage -- through vtable slots, as they call it:
//   - a render target filled and read back the way PresentationCore's managed D3DImage reads one
//     (GetDevice, CreateOffscreenPlainSurface in system memory, GetRenderTargetData, LockRect);
//   - a D3D11 texture opened in D3D9 by its shared handle (SharpMediaFoundationInterop's
//     D3D9SharedSurface), showing what D3D11 drew;
//   - a shared D3D9 texture opened from D3D11 (OpenSharedResource), showing what D3D9 filled;
//   - a texture level keeping its texture alive, as D3D9's reference counting has it.
//

using System;
using System.Runtime.InteropServices;
using Microsoft.Wpf.Interop.WebGpu.Direct3D;
using WgpuInterop.Tests.Harness;
using Xunit;

namespace WgpuInterop.Tests.Direct3D
{
    public sealed unsafe class D3D9ShimTests
    {
        private static IntPtr Fn(IntPtr com, int slot) => (*(IntPtr**)com)[slot];
        private static uint Release(IntPtr com) => com == IntPtr.Zero ? 0 : ((delegate* unmanaged<IntPtr, uint>)Fn(com, 2))(com);
        private static uint AddRef(IntPtr com) => ((delegate* unmanaged<IntPtr, uint>)Fn(com, 1))(com);
        private static void Check(int hr, string what) => Assert.True(hr >= 0, $"{what} failed: 0x{hr:x8}");

        private static void RequireGpu() => Assert.SkipWhen(Requirements.GpuUnavailable is not null, Requirements.GpuUnavailable ?? "");

        [StructLayout(LayoutKind.Sequential)]
        private struct PresentParameters
        {
            public uint BackBufferWidth, BackBufferHeight;
            public int BackBufferFormat;
            public uint BackBufferCount;
            public int MultiSampleType;
            public uint MultiSampleQuality;
            public int SwapEffect;
            public IntPtr hDeviceWindow;
            public int Windowed, EnableAutoDepthStencil, AutoDepthStencilFormat;
            public uint Flags, FullScreen_RefreshRateInHz, PresentationInterval;
        }

        private static (IntPtr D3D, IntPtr Device) Device9()
        {
            IntPtr d3d;
            Check(Exports.Create9ExPointer(32, &d3d), "Direct3DCreate9Ex");
            var pp = new PresentParameters { BackBufferWidth = 1, BackBufferHeight = 1, SwapEffect = 1, Windowed = 1, PresentationInterval = 0x80000000 };
            IntPtr dev;
            Check(((delegate* unmanaged<IntPtr, uint, int, IntPtr, uint, PresentParameters*, void*, IntPtr*, int>)Fn(d3d, Vtbl.IDirect3D9Ex.CreateDeviceEx))(
                d3d, 0, 1, IntPtr.Zero, 0x40 | 0x4 | 0x2, &pp, null, &dev), "CreateDeviceEx");
            return (d3d, dev);
        }

        /// <summary>What ManagedInteropBitmap does with a D3DImage back buffer: its pixels as
        /// A8R8G8B8 uints, through a system-memory copy.</summary>
        private static uint[] ReadLikeD3DImage(IntPtr surface)
        {
            D3DSURFACE_DESC desc;
            Check(((delegate* unmanaged<IntPtr, D3DSURFACE_DESC*, int>)Fn(surface, Vtbl.IDirect3DSurface9.GetDesc))(surface, &desc), "GetDesc");
            IntPtr device, copy;
            Check(((delegate* unmanaged<IntPtr, IntPtr*, int>)Fn(surface, Vtbl.IDirect3DSurface9.GetDevice))(surface, &device), "GetDevice");
            try
            {
                Check(((delegate* unmanaged<IntPtr, uint, uint, int, int, IntPtr*, IntPtr*, int>)Fn(device, Vtbl.IDirect3DDevice9.CreateOffscreenPlainSurface))(
                    device, desc.Width, desc.Height, desc.Format, 2 /* SYSTEMMEM */, &copy, null), "CreateOffscreenPlainSurface");
                Check(((delegate* unmanaged<IntPtr, IntPtr, IntPtr, int>)Fn(device, Vtbl.IDirect3DDevice9.GetRenderTargetData))(device, surface, copy), "GetRenderTargetData");
            }
            finally { Release(device); }
            try
            {
                D3DLOCKED_RECT locked;
                Check(((delegate* unmanaged<IntPtr, D3DLOCKED_RECT*, void*, uint, int>)Fn(copy, Vtbl.IDirect3DSurface9.LockRect))(copy, &locked, null, 0x10), "LockRect");
                var px = new uint[desc.Width * desc.Height];
                for (int y = 0; y < desc.Height; y++)
                    new ReadOnlySpan<uint>((byte*)locked.pBits + y * locked.Pitch, (int)desc.Width).CopyTo(px.AsSpan(y * (int)desc.Width));
                Check(((delegate* unmanaged<IntPtr, int>)Fn(copy, Vtbl.IDirect3DSurface9.UnlockRect))(copy), "UnlockRect");
                return px;
            }
            finally { Release(copy); }
        }

        private static void ColorFill(IntPtr dev, IntPtr surface, uint argb, D3DRECT9* rect = null)
            => Check(((delegate* unmanaged<IntPtr, IntPtr, D3DRECT9*, uint, int>)Fn(dev, Vtbl.IDirect3DDevice9.ColorFill))(dev, surface, rect, argb), "ColorFill");

        [Fact]
        public void ARenderTarget_FilledThenReadAsD3DImageReadsIt()
        {
            RequireGpu();
            var (d3d, dev) = Device9();
            IntPtr rt = IntPtr.Zero;
            try
            {
                Check(((delegate* unmanaged<IntPtr, uint, uint, int, int, uint, int, IntPtr*, IntPtr*, int>)Fn(dev, Vtbl.IDirect3DDevice9.CreateRenderTarget))(
                    dev, 64, 48, 21, 0, 0, 0, &rt, null), "CreateRenderTarget");
                ColorFill(dev, rt, 0xFF2080C0);
                var r = new D3DRECT9 { left = 10, top = 5, right = 20, bottom = 15 };
                ColorFill(dev, rt, 0x80FF0000, &r);
                uint[] px = ReadLikeD3DImage(rt);
                Assert.Equal(0xFF2080C0u, px[0]);
                Assert.Equal(0xFF2080C0u, px[47 * 64 + 63]);
                Assert.Equal(0x80FF0000u, px[5 * 64 + 10]);
                Assert.Equal(0x80FF0000u, px[14 * 64 + 19]);
                Assert.Equal(0xFF2080C0u, px[15 * 64 + 20]);
            }
            finally { Release(rt); Release(dev); Release(d3d); }
        }

        /// <summary>SharpMediaFoundationInterop's path: D3D11 renders into a shared BGRA texture, D3D9
        /// opens it by its handle (CreateTexture with *pSharedHandle set) and hands level 0 to D3DImage.</summary>
        [Fact]
        public void AD3D11Texture_OpenedInD3D9ByItsHandle_ShowsWhatD3D11Drew()
        {
            RequireGpu();
            IntPtr dev11, ctx, tex11 = IntPtr.Zero, rtv = IntPtr.Zero, dxgiRes = IntPtr.Zero;
            uint level;
            Check(Exports.CreateDevicePointer(IntPtr.Zero, 1, IntPtr.Zero, 0, null, 0, 7, &dev11, &level, &ctx), "D3D11CreateDevice");
            var (d3d, dev9) = Device9();
            IntPtr tex9 = IntPtr.Zero, surface = IntPtr.Zero;
            try
            {
                var td = new D3D11_TEXTURE2D_DESC
                {
                    Width = 32, Height = 16, MipLevels = 1, ArraySize = 1, Format = DXGI_FORMAT.B8G8R8A8_UNORM,
                    SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 }, Usage = D3D11_USAGE.DEFAULT,
                    BindFlags = D3D11_BIND.RENDER_TARGET | D3D11_BIND.SHADER_RESOURCE, MiscFlags = D3D11_RESOURCE_MISC.SHARED,
                };
                Check(((delegate* unmanaged<IntPtr, D3D11_TEXTURE2D_DESC*, D3D11_SUBRESOURCE_DATA*, IntPtr*, int>)Fn(dev11, Vtbl.ID3D11Device.CreateTexture2D))(dev11, &td, null, &tex11), "CreateTexture2D");
                Check(((delegate* unmanaged<IntPtr, IntPtr, void*, IntPtr*, int>)Fn(dev11, Vtbl.ID3D11Device.CreateRenderTargetView))(dev11, tex11, null, &rtv), "CreateRenderTargetView");
                float* green = stackalloc float[] { 0.25f, 0.75f, 0.5f, 1f };
                ((delegate* unmanaged<IntPtr, IntPtr, float*, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.ClearRenderTargetView))(ctx, rtv, green);
                ((delegate* unmanaged<IntPtr, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.Flush))(ctx);

                Guid iidRes = Vtbl.IDXGIResource.IID;
                Check(((delegate* unmanaged<IntPtr, Guid*, IntPtr*, int>)Fn(tex11, 0))(tex11, &iidRes, &dxgiRes), "QueryInterface IDXGIResource");
                IntPtr handle;
                Check(((delegate* unmanaged<IntPtr, IntPtr*, int>)Fn(dxgiRes, Vtbl.IDXGIResource.GetSharedHandle))(dxgiRes, &handle), "GetSharedHandle");
                Assert.NotEqual(IntPtr.Zero, handle);

                Check(((delegate* unmanaged<IntPtr, uint, uint, uint, uint, int, int, IntPtr*, IntPtr*, int>)Fn(dev9, Vtbl.IDirect3DDevice9.CreateTexture))(
                    dev9, 32, 16, 1, 1 /* RENDERTARGET */, 21, 0, &tex9, &handle), "CreateTexture (shared)");
                Check(((delegate* unmanaged<IntPtr, uint, IntPtr*, int>)Fn(tex9, Vtbl.IDirect3DTexture9.GetSurfaceLevel))(tex9, 0, &surface), "GetSurfaceLevel");

                uint[] px = ReadLikeD3DImage(surface);
                Assert.Equal(0xFF40BF80u, px[0]);           // B8G8R8A8 (0.25, 0.75, 0.5, 1) read as A8R8G8B8
                Assert.Equal(0xFF40BF80u, px[15 * 32 + 31]);

                // the next frame D3D11 draws shows through the same surface
                float* red = stackalloc float[] { 1f, 0f, 0f, 1f };
                ((delegate* unmanaged<IntPtr, IntPtr, float*, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.ClearRenderTargetView))(ctx, rtv, red);
                ((delegate* unmanaged<IntPtr, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.Flush))(ctx);
                Assert.Equal(0xFFFF0000u, ReadLikeD3DImage(surface)[100]);
            }
            finally
            {
                Release(surface); Release(tex9); Release(dev9); Release(d3d);
                Release(dxgiRes); Release(rtv); Release(tex11); Release(ctx); Release(dev11);
            }
        }

        /// <summary>The other direction (DXEngine's): a shared texture made by D3D9, opened from D3D11.</summary>
        [Fact]
        public void ASharedD3D9Texture_OpenedFromD3D11_HoldsWhatD3D9Filled()
        {
            RequireGpu();
            var (d3d, dev9) = Device9();
            IntPtr dev11, ctx, tex9 = IntPtr.Zero, surface = IntPtr.Zero, opened = IntPtr.Zero, staging = IntPtr.Zero;
            uint level;
            Check(Exports.CreateDevicePointer(IntPtr.Zero, 1, IntPtr.Zero, 0, null, 0, 7, &dev11, &level, &ctx), "D3D11CreateDevice");
            try
            {
                IntPtr handle = IntPtr.Zero;
                Check(((delegate* unmanaged<IntPtr, uint, uint, uint, uint, int, int, IntPtr*, IntPtr*, int>)Fn(dev9, Vtbl.IDirect3DDevice9.CreateTexture))(
                    dev9, 8, 8, 1, 1, 21, 0, &tex9, &handle), "CreateTexture (new shared)");
                Assert.NotEqual(IntPtr.Zero, handle);
                Check(((delegate* unmanaged<IntPtr, uint, IntPtr*, int>)Fn(tex9, Vtbl.IDirect3DTexture9.GetSurfaceLevel))(tex9, 0, &surface), "GetSurfaceLevel");
                ColorFill(dev9, surface, 0xFF102030);

                Guid iidTex = Vtbl.ID3D11Texture2D.IID;
                Check(((delegate* unmanaged<IntPtr, IntPtr, Guid*, IntPtr*, int>)Fn(dev11, Vtbl.ID3D11Device.OpenSharedResource))(dev11, handle, &iidTex, &opened), "OpenSharedResource");
                D3D11_TEXTURE2D_DESC desc;
                ((delegate* unmanaged<IntPtr, D3D11_TEXTURE2D_DESC*, void>)Fn(opened, Vtbl.ID3D11Texture2D.GetDesc))(opened, &desc);
                Assert.Equal(8u, desc.Width);
                Assert.Equal(DXGI_FORMAT.B8G8R8A8_UNORM, desc.Format);

                var sd = desc;
                sd.Usage = D3D11_USAGE.STAGING; sd.BindFlags = 0; sd.CPUAccessFlags = D3D11_CPU_ACCESS.READ; sd.MiscFlags = 0;
                Check(((delegate* unmanaged<IntPtr, D3D11_TEXTURE2D_DESC*, D3D11_SUBRESOURCE_DATA*, IntPtr*, int>)Fn(dev11, Vtbl.ID3D11Device.CreateTexture2D))(dev11, &sd, null, &staging), "CreateTexture2D staging");
                ((delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.CopyResource))(ctx, staging, opened);
                D3D11_MAPPED_SUBRESOURCE m;
                Check(((delegate* unmanaged<IntPtr, IntPtr, uint, D3D11_MAP, uint, D3D11_MAPPED_SUBRESOURCE*, int>)Fn(ctx, Vtbl.ID3D11DeviceContext.Map))(ctx, staging, 0, D3D11_MAP.READ, 0, &m), "Map");
                uint first = *(uint*)m.pData, last = *(uint*)((byte*)m.pData + 7 * m.RowPitch + 28);
                ((delegate* unmanaged<IntPtr, IntPtr, uint, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.Unmap))(ctx, staging, 0);
                Assert.Equal(0xFF102030u, first);
                Assert.Equal(0xFF102030u, last);
            }
            finally
            {
                Release(staging); Release(opened); Release(surface); Release(tex9);
                Release(ctx); Release(dev11); Release(dev9); Release(d3d);
            }
        }

        /// <summary>D3D9's counting: a surface level shares its texture's count, so holding only the
        /// level keeps the texture (and its pixels) alive, and the level's reference goes back.</summary>
        [Fact]
        public void ATextureLevel_KeepsItsTextureAlive()
        {
            RequireGpu();
            var (d3d, dev) = Device9();
            IntPtr tex = IntPtr.Zero, surface = IntPtr.Zero;
            try
            {
                Check(((delegate* unmanaged<IntPtr, uint, uint, uint, uint, int, int, IntPtr*, IntPtr*, int>)Fn(dev, Vtbl.IDirect3DDevice9.CreateTexture))(
                    dev, 16, 16, 1, 1, 21, 0, &tex, null), "CreateTexture");
                Check(((delegate* unmanaged<IntPtr, uint, IntPtr*, int>)Fn(tex, Vtbl.IDirect3DTexture9.GetSurfaceLevel))(tex, 0, &surface), "GetSurfaceLevel");
                Assert.Equal(1u, Release(tex));         // the application's texture reference is gone; the level holds it...
                tex = IntPtr.Zero;
                ColorFill(dev, surface, 0xFF00FF00);    // ...and the level still works
                Assert.Equal(0xFF00FF00u, ReadLikeD3DImage(surface)[17]);

                // GetContainer finds the texture again, alive
                Guid iid = Vtbl.IDirect3DTexture9.IID;
                IntPtr container;
                Check(((delegate* unmanaged<IntPtr, Guid*, IntPtr*, int>)Fn(surface, Vtbl.IDirect3DSurface9.GetContainer))(surface, &iid, &container), "GetContainer");
                Assert.Equal(16u, ((delegate* unmanaged<IntPtr, uint>)Fn(container, Vtbl.IDirect3DTexture9.GetLevelCount))(container) * 16);
                Release(container);
            }
            finally { Release(surface); Release(tex); Release(dev); Release(d3d); }
        }

        /// <summary>The queries an application makes before it creates its device.</summary>
        [Fact]
        public void TheAdapter_AnswersTheQueriesBeforeCreateDevice()
        {
            RequireGpu();
            IntPtr d3d;
            Check(Exports.Create9ExPointer(32, &d3d), "Direct3DCreate9Ex");
            try
            {
                Assert.Equal(1u, ((delegate* unmanaged<IntPtr, uint>)Fn(d3d, Vtbl.IDirect3D9Ex.GetAdapterCount))(d3d));
                Check(((delegate* unmanaged<IntPtr, uint, int, int, uint, int, int, int>)Fn(d3d, Vtbl.IDirect3D9Ex.CheckDeviceFormat))(d3d, 0, 1, 22, 1, 3, 21), "CheckDeviceFormat A8R8G8B8 render target texture");
                var caps = new uint[76];
                fixed (uint* c = caps) Check(((delegate* unmanaged<IntPtr, uint, int, uint*, int>)Fn(d3d, Vtbl.IDirect3D9Ex.GetDeviceCaps))(d3d, 0, 1, c), "GetDeviceCaps");
                Assert.Equal(0xFFFF0300u, caps[51]);                // PixelShaderVersion 3.0
                Assert.True((caps[3] & 0x80000000u) != 0, "D3DCAPS2_CANSHARERESOURCE");
                Guid iid9 = Vtbl.IDirect3D9.IID;
                IntPtr plain;
                Check(((delegate* unmanaged<IntPtr, Guid*, IntPtr*, int>)Fn(d3d, 0))(d3d, &iid9, &plain), "QueryInterface IDirect3D9");
                Release(plain);
            }
            finally { Release(d3d); }
        }
    }
}
