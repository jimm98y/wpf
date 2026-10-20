// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A multisampled render target that shaders also read (Texture2DMS), against WARP. On wgpu's GL backend
// such a texture is drawn as a renderbuffer and read through its resolved copy (D3DDevice.MsReadsResolved),
// so a shader that averages the samples (a custom box resolve, the usual reason to read them) reads what
// WARP reads; a single sample of an edge pixel reads the pixel's resolved value instead of its own.
//

using System;
using System.Collections.Generic;
using Microsoft.Wpf.Interop.WebGpu.Direct3D;
using WgpuInterop.Tests.Harness;
using Xunit;
using static WgpuInterop.Tests.Direct3D.DxbcPixelParity;

namespace WgpuInterop.Tests.Direct3D
{
    public sealed unsafe class D3DMultisampleReadTests
    {
        [System.Runtime.InteropServices.DllImport("d3d11.dll", EntryPoint = "D3D11CreateDevice")]
        private static extern int RealCreateDevice(IntPtr adapter, uint driverType, IntPtr software, uint flags, uint* levels, uint count, uint sdk, IntPtr* device, uint* level, IntPtr* context);

        // two triangles over a 4-sample target: edges through pixels, colours whose averages are exact in half
        private const string TriVs = @"
float4 main(uint id : SV_VertexID) : SV_Position
{
    float2 p[6] = { float2(-0.9, -0.8), float2(0.1, 0.9), float2(0.7, -0.6), float2(-0.3, 0.2), float2(0.95, 0.85), float2(0.6, 0.1) };
    return float4(p[id], 0.5, 1);
}";
        private const string TriPs = "float4 main(float4 p : SV_Position, uint prim : SV_PrimitiveID) : SV_Target { return prim == 0 ? float4(1, 0.5, 0.25, 1) : float4(0, 0.75, 1, 0.5); }";

        private const string FullVs = "float4 main(uint id : SV_VertexID) : SV_Position { return float4(id == 1 ? 3 : -1, id == 2 ? 3 : -1, 0, 1); }";

        // the samples averaged in the shader, their count from GetDimensions
        private const string BoxPs = @"
Texture2DMS<float4> t : register(t0);
float4 main(float4 p : SV_Position) : SV_Target
{
    uint w, h, n;
    t.GetDimensions(w, h, n);
    float4 s = 0;
    for (uint i = 0; i < n; i++) s += t.Load(int2(p.xy), i);
    return s / n;
}";

        private static float[] Run(IntPtr dev, IntPtr ctx, byte[] triVs, byte[] triPs, byte[] fullVs, byte[] boxPs)
        {
            var made = new List<IntPtr>();
            IntPtr Keep(IntPtr p) { made.Add(p); return p; }
            try
            {
                IntPtr ms, srv, msRtv, outTex, outRtv, v1, p1, v2, p2, rs;
                var td = new D3D11_TEXTURE2D_DESC
                {
                    Width = W, Height = H, MipLevels = 1, ArraySize = 1, Format = DXGI_FORMAT.R16G16B16A16_FLOAT,
                    SampleDesc = new DXGI_SAMPLE_DESC { Count = 4 }, Usage = D3D11_USAGE.DEFAULT, BindFlags = D3D11_BIND.RENDER_TARGET | D3D11_BIND.SHADER_RESOURCE,
                };
                Check(((delegate* unmanaged<IntPtr, D3D11_TEXTURE2D_DESC*, D3D11_SUBRESOURCE_DATA*, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateTexture2D))(dev, &td, null, &ms), "CreateTexture2D ms");
                Keep(ms);
                Check(((delegate* unmanaged<IntPtr, IntPtr, void*, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateRenderTargetView))(dev, ms, null, &msRtv), "CreateRenderTargetView ms");
                Keep(msRtv);
                Check(((delegate* unmanaged<IntPtr, IntPtr, void*, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateShaderResourceView))(dev, ms, null, &srv), "CreateShaderResourceView ms");
                Keep(srv);
                var od = td; od.SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 }; od.BindFlags = D3D11_BIND.RENDER_TARGET;
                Check(((delegate* unmanaged<IntPtr, D3D11_TEXTURE2D_DESC*, D3D11_SUBRESOURCE_DATA*, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateTexture2D))(dev, &od, null, &outTex), "CreateTexture2D out");
                Keep(outTex);
                Check(((delegate* unmanaged<IntPtr, IntPtr, void*, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateRenderTargetView))(dev, outTex, null, &outRtv), "CreateRenderTargetView out");
                Keep(outRtv);
                fixed (byte* q = triVs) Check(((delegate* unmanaged<IntPtr, byte*, nuint, IntPtr, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateVertexShader))(dev, q, (nuint)triVs.Length, IntPtr.Zero, &v1), "VS");
                Keep(v1);
                fixed (byte* q = triPs) Check(((delegate* unmanaged<IntPtr, byte*, nuint, IntPtr, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreatePixelShader))(dev, q, (nuint)triPs.Length, IntPtr.Zero, &p1), "PS");
                Keep(p1);
                fixed (byte* q = fullVs) Check(((delegate* unmanaged<IntPtr, byte*, nuint, IntPtr, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateVertexShader))(dev, q, (nuint)fullVs.Length, IntPtr.Zero, &v2), "VS full");
                Keep(v2);
                fixed (byte* q = boxPs) Check(((delegate* unmanaged<IntPtr, byte*, nuint, IntPtr, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreatePixelShader))(dev, q, (nuint)boxPs.Length, IntPtr.Zero, &p2), "PS box");
                Keep(p2);
                var rd = D3D11_RASTERIZER_DESC.Default();
                rd.CullMode = 1;
                rd.MultisampleEnable = 1;
                Check(((delegate* unmanaged<IntPtr, D3D11_RASTERIZER_DESC*, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateRasterizerState))(dev, &rd, &rs), "CreateRasterizerState");
                Keep(rs);
                var vp = new D3D11_VIEWPORT { Width = W, Height = H, MaxDepth = 1 };
                ((delegate* unmanaged<IntPtr, IntPtr, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.RSSetState))(ctx, rs);
                ((delegate* unmanaged<IntPtr, uint, D3D11_VIEWPORT*, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.RSSetViewports))(ctx, 1, &vp);
                ((delegate* unmanaged<IntPtr, uint, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.IASetPrimitiveTopology))(ctx, 4);

                // 1. the triangles into the 4-sample target
                float* clear = stackalloc float[] { 0f, 0.25f, 0.5f, 1f };
                ((delegate* unmanaged<IntPtr, IntPtr, float*, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.ClearRenderTargetView))(ctx, msRtv, clear);
                ((delegate* unmanaged<IntPtr, uint, IntPtr*, IntPtr, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.OMSetRenderTargets))(ctx, 1, &msRtv, IntPtr.Zero);
                ((delegate* unmanaged<IntPtr, IntPtr, IntPtr*, uint, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.VSSetShader))(ctx, v1, null, 0);
                ((delegate* unmanaged<IntPtr, IntPtr, IntPtr*, uint, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.PSSetShader))(ctx, p1, null, 0);
                ((delegate* unmanaged<IntPtr, uint, uint, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.Draw))(ctx, 6, 0);

                // 2. its samples averaged by a shader into a plain target
                float* black = stackalloc float[] { 0f, 0f, 0f, 0f };
                ((delegate* unmanaged<IntPtr, IntPtr, float*, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.ClearRenderTargetView))(ctx, outRtv, black);
                ((delegate* unmanaged<IntPtr, uint, IntPtr*, IntPtr, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.OMSetRenderTargets))(ctx, 1, &outRtv, IntPtr.Zero);
                ((delegate* unmanaged<IntPtr, uint, uint, IntPtr*, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.PSSetShaderResources))(ctx, 0, 1, &srv);
                ((delegate* unmanaged<IntPtr, IntPtr, IntPtr*, uint, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.VSSetShader))(ctx, v2, null, 0);
                ((delegate* unmanaged<IntPtr, IntPtr, IntPtr*, uint, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.PSSetShader))(ctx, p2, null, 0);
                ((delegate* unmanaged<IntPtr, uint, uint, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.Draw))(ctx, 3, 0);
                return ReadTarget(dev, ctx, outTex, Keep);
            }
            finally
            {
                ((delegate* unmanaged<IntPtr, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.ClearState))(ctx);
                foreach (IntPtr p in made) Release(p);
            }
        }

        /// <summary>A 4-sample target drawn, then its samples averaged by a pixel shader (Texture2DMS.Load
        /// over GetDimensions' count): what WARP reads, edge pixels included.</summary>
        [Fact]
        public void ASampledMultisampleTarget_ReadsItsSamplesAsWarp()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "real Direct3D 11 (WARP) and the HLSL compiler exist on Windows only");
            Assert.SkipWhen(Requirements.GpuUnavailable is not null, Requirements.GpuUnavailable ?? "");
            byte[] triVs = Compile(TriVs, "vs_5_0", out string? e1), triPs = Compile(TriPs, "ps_5_0", out string? e2), fullVs = Compile(FullVs, "vs_5_0", out string? e3), boxPs = Compile(BoxPs, "ps_5_0", out string? e4);
            Assert.True(triVs.Length > 0 && triPs.Length > 0 && fullVs.Length > 0 && boxPs.Length > 0, e1 + e2 + e3 + e4);
            IntPtr wDev, wCtx, sDev, sCtx;
            uint level, want = 0xb000;
            Assert.True(RealCreateDevice(IntPtr.Zero, 5, IntPtr.Zero, 0, &want, 1, 7, &wDev, &level, &wCtx) >= 0, "WARP");
            Assert.True(Exports.CreateDevicePointer(IntPtr.Zero, 1, IntPtr.Zero, 0, null, 0, 7, &sDev, &level, &sCtx) >= 0, "shim");
            try
            {
                var warp = new List<float[]> { Run(wDev, wCtx, triVs, triPs, fullVs, boxPs) };
                var shim = new List<float[]> { Run(sDev, sCtx, triVs, triPs, fullVs, boxPs) };
                int edges = 0;
                for (int i = 0; i < warp[0].Length; i += 4)
                {
                    float r = warp[0][i];
                    if (r != 0f && r != 1f) edges++;       // a partly covered pixel of the first triangle
                }
                Assert.True(edges > 10, $"WARP's image has {edges} partly covered pixels");
                var (count, first) = Compare(warp, shim);
                Assert.True(count == 0, $"{count} components differ in {DifferingPixels(warp, shim)} pixels (WARP covers {Covered(warp)}, the shim {Covered(shim)}); {first}");
                Assert.Equal(0, ComObject.From<D3DContext>(sCtx)!.DrawsSkipped);
            }
            finally { foreach (IntPtr p in new[] { sCtx, sDev, wCtx, wDev }) Release(p); }
        }
    }
}
