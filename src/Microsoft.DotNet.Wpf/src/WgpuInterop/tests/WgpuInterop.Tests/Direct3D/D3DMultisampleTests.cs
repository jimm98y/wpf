// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Multisampling on the Direct3D 11 shim. WebGPU has 1 and 4 samples. An app that probes 2 samples first
// and stops at the first count refused (Ab3d.DXEngine's DeviceCapabilities does) got no MSAA at all, so
// GraphWorX's 3D lines and edges were aliased where Direct3D's are smooth. Now 2 is answered as
// supported and is given 4 samples, while the texture's description keeps the 2 the app asked for.
//
// ResolveSubresource also resolves into a texture that is no render target (through one of the shim's
// own, then a copy), and a multisampled texture is sampled-capable only when the app made it a shader
// resource: on wgpu's GL backend one that is keeps nothing drawn into it.
//

using System;
using Microsoft.Wpf.Interop.WebGpu.Direct3D;
using Xunit;

namespace WgpuInterop.Tests.Direct3D
{
    public sealed unsafe class D3DMultisampleTests
    {
        private static IntPtr Fn(IntPtr com, int slot) => (*(IntPtr**)com)[slot];
        private static void Release(IntPtr com) { if (com != IntPtr.Zero) ((delegate* unmanaged<IntPtr, uint>)Fn(com, 2))(com); }
        private static void Check(int hr, string what) => Assert.True(hr >= 0, $"{what} failed: 0x{hr:x8}");

        /// <summary>The counts DXEngine probes, in its order, for its back buffer format.</summary>
        [Fact]
        public void TwoAndFourSamples_AreSupported_EightIsNot()
        {
            IntPtr d, ctx; uint level;
            Check(Exports.CreateDevicePointer(IntPtr.Zero, 1, IntPtr.Zero, 0, null, 0, 7, &d, &level, &ctx), "D3D11CreateDevice");
            IntPtr dev = d;
            try
            {
                uint Levels(uint n)
                {
                    uint q;
                    Check(((delegate* unmanaged<IntPtr, DXGI_FORMAT, uint, uint*, int>)Fn(dev, Vtbl.ID3D11Device.CheckMultisampleQualityLevels))(dev, DXGI_FORMAT.B8G8R8A8_UNORM, n, &q), "CheckMultisampleQualityLevels");
                    return q;
                }
                Assert.True(Levels(2) > 0);
                Assert.True(Levels(4) > 0);
                Assert.Equal(0u, Levels(8));
            }
            finally { Release(ctx); Release(dev); }
        }

        /// <summary>A 2-sample render target: its description says 2; cleared and resolved, it holds the
        /// colour -- resolved into a render target, and into a texture that is a shader resource only.</summary>
        [Theory]
        [InlineData(true, false)]
        [InlineData(false, false)]
        [InlineData(true, true)]
        public void ATwoSampleTarget_ClearsAndResolves(bool intoRenderTarget, bool sampled)
        {
            // wgpu's GL backend: a multisampled texture that can be sampled (a GL texture, not a
            // renderbuffer) keeps nothing drawn into it -- neither its resolve nor a shader's reads of
            // its samples see the clear
            Assert.SkipWhen(sampled && D3DDevice.SharedGpu.IsOpenGL, "a sampled multisampled texture holds nothing on wgpu's GL backend");
            IntPtr dev, ctx; uint level;
            Check(Exports.CreateDevicePointer(IntPtr.Zero, 1, IntPtr.Zero, 0, null, 0, 7, &dev, &level, &ctx), "D3D11CreateDevice");
            IntPtr ms = 0, resolved = 0, staging = 0, rtv = 0;
            try
            {
                var create = (delegate* unmanaged<IntPtr, D3D11_TEXTURE2D_DESC*, D3D11_SUBRESOURCE_DATA*, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateTexture2D);
                var td = new D3D11_TEXTURE2D_DESC
                {
                    Width = 16, Height = 16, MipLevels = 1, ArraySize = 1, Format = DXGI_FORMAT.B8G8R8A8_UNORM,
                    SampleDesc = new DXGI_SAMPLE_DESC { Count = 2 }, Usage = D3D11_USAGE.DEFAULT, BindFlags = D3D11_BIND.RENDER_TARGET | (sampled ? D3D11_BIND.SHADER_RESOURCE : 0),
                };
                Check(create(dev, &td, null, &ms), "CreateTexture2D (2 samples)");
                D3D11_TEXTURE2D_DESC got;
                ((delegate* unmanaged<IntPtr, D3D11_TEXTURE2D_DESC*, void>)Fn(ms, Vtbl.ID3D11Texture2D.GetDesc))(ms, &got);
                Assert.Equal(2u, got.SampleDesc.Count);

                var rd = td; rd.SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 }; rd.BindFlags = D3D11_BIND.SHADER_RESOURCE | (intoRenderTarget ? D3D11_BIND.RENDER_TARGET : 0);
                Check(create(dev, &rd, null, &resolved), "CreateTexture2D (resolved)");
                var sd = rd; sd.Usage = D3D11_USAGE.STAGING; sd.BindFlags = 0; sd.CPUAccessFlags = D3D11_CPU_ACCESS.READ;
                Check(create(dev, &sd, null, &staging), "CreateTexture2D (staging)");
                Check(((delegate* unmanaged<IntPtr, IntPtr, void*, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateRenderTargetView))(dev, ms, null, &rtv), "CreateRenderTargetView");

                float* clear = stackalloc float[] { 0.25f, 0.5f, 1f, 1f };
                ((delegate* unmanaged<IntPtr, IntPtr, float*, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.ClearRenderTargetView))(ctx, rtv, clear);
                ((delegate* unmanaged<IntPtr, IntPtr, uint, IntPtr, uint, DXGI_FORMAT, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.ResolveSubresource))(ctx, resolved, 0, ms, 0, td.Format);
                ((delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.CopyResource))(ctx, staging, resolved);
                D3D11_MAPPED_SUBRESOURCE m;
                Check(((delegate* unmanaged<IntPtr, IntPtr, uint, D3D11_MAP, uint, D3D11_MAPPED_SUBRESOURCE*, int>)Fn(ctx, Vtbl.ID3D11DeviceContext.Map))(ctx, staging, 0, D3D11_MAP.READ, 0, &m), "Map");
                uint px = *(uint*)((byte*)m.pData + 8 * m.RowPitch + 8 * 4);
                ((delegate* unmanaged<IntPtr, IntPtr, uint, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.Unmap))(ctx, staging, 0);
                // BGRA bytes: B = 255, G = 128, R = 64 (0.25 * 255 = 63.75)
                Assert.Equal(0xFF4080FFu, px);
            }
            finally { Release(rtv); Release(staging); Release(resolved); Release(ms); Release(ctx); Release(dev); }
        }
    }
}
