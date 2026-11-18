// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A depth buffer read as a texture without comparison -- what SSAO, depth of field and deferred
// lighting do: drawn through a D32_FLOAT view of an R32_TYPELESS texture, read through an R32_FLOAT
// view with a linear sampler (the blend of the texels around the coordinate), a point sampler (the
// nearest) and Load, each the depth in red as D3D gives it: (d, 0, 0, 1).
//

using System;
using System.Collections.Generic;
using Microsoft.Wpf.Interop.WebGpu.Direct3D;
using WgpuInterop.Tests.Harness;
using Xunit;
using static WgpuInterop.Tests.Direct3D.DxbcPixelParity;

namespace WgpuInterop.Tests.Direct3D
{
    public sealed unsafe class D3DDepthReadTests
    {
        private static byte[] Hlsl(string src, string profile)
        {
            var b = HlslCompilerTests.Compile(src, "depth.hlsl", "main", profile, Array.Empty<string>(), out string err);
            Assert.True(b != null, err);
            return b!;
        }

        [Fact]
        public void ADepthBuffer_ReadsAsItsDepths_ThroughLinearAndPointSamplersAndLoad()
        {
            Assert.SkipWhen(Requirements.GpuUnavailable is not null, Requirements.GpuUnavailable ?? "");
            IntPtr dev, ctx;
            uint level;
            Assert.True(Exports.CreateDevicePointer(IntPtr.Zero, 1, IntPtr.Zero, 0, null, 0, 7, &dev, &level, &ctx) >= 0, "shim");
            var made = new List<IntPtr>();
            IntPtr Keep(IntPtr p) { made.Add(p); return p; }
            try
            {
                // a 4x4 depth buffer: the left half drawn at 0.25, the right half cleared to 1
                var td = new D3D11_TEXTURE2D_DESC { Width = 4, Height = 4, MipLevels = 1, ArraySize = 1, Format = DXGI_FORMAT.R32_TYPELESS, SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 }, Usage = D3D11_USAGE.DEFAULT, BindFlags = D3D11_BIND.DEPTH_STENCIL | D3D11_BIND.SHADER_RESOURCE };
                IntPtr depth, dsv, srv;
                Check(((delegate* unmanaged<IntPtr, D3D11_TEXTURE2D_DESC*, D3D11_SUBRESOURCE_DATA*, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateTexture2D))(dev, &td, null, &depth), "CreateTexture2D depth");
                Keep(depth);
                var dd = new D3D11_DEPTH_STENCIL_VIEW_DESC { Format = DXGI_FORMAT.D32_FLOAT, ViewDimension = 3 };
                Check(((delegate* unmanaged<IntPtr, IntPtr, D3D11_DEPTH_STENCIL_VIEW_DESC*, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateDepthStencilView))(dev, depth, &dd, &dsv), "CreateDepthStencilView");
                Keep(dsv);
                ((delegate* unmanaged<IntPtr, IntPtr, uint, float, byte, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.ClearDepthStencilView))(ctx, dsv, 1, 1f, 0);
                byte[] fill = Hlsl("static const float2 q[6] = { float2(-1, 1), float2(0, 1), float2(-1, -1), float2(0, 1), float2(0, -1), float2(-1, -1) };\n"
                    + "float4 main(uint id : SV_VertexID) : SV_Position { return float4(q[id], 0.25, 1); }\n", "vs_5_0");
                IntPtr fvs;
                fixed (byte* p = fill) Check(((delegate* unmanaged<IntPtr, byte*, nuint, IntPtr, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateVertexShader))(dev, p, (nuint)fill.Length, IntPtr.Zero, &fvs), "CreateVertexShader fill");
                Keep(fvs);
                ((delegate* unmanaged<IntPtr, IntPtr, IntPtr*, uint, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.VSSetShader))(ctx, fvs, null, 0);
                ((delegate* unmanaged<IntPtr, IntPtr, IntPtr*, uint, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.PSSetShader))(ctx, IntPtr.Zero, null, 0);
                ((delegate* unmanaged<IntPtr, uint, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.IASetPrimitiveTopology))(ctx, 4);
                ((delegate* unmanaged<IntPtr, uint, IntPtr*, IntPtr, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.OMSetRenderTargets))(ctx, 0, null, dsv);
                var vp = new D3D11_VIEWPORT { Width = 4, Height = 4, MaxDepth = 1 };
                ((delegate* unmanaged<IntPtr, uint, D3D11_VIEWPORT*, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.RSSetViewports))(ctx, 1, &vp);
                ((delegate* unmanaged<IntPtr, uint, uint, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.Draw))(ctx, 6, 0);
                ((delegate* unmanaged<IntPtr, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.ClearState))(ctx);

                var sv = new D3D11_VIEW_DESC { Format = DXGI_FORMAT.R32_FLOAT, ViewDimension = 4, U0 = 0, U1 = 1 };
                Check(((delegate* unmanaged<IntPtr, IntPtr, D3D11_VIEW_DESC*, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateShaderResourceView))(dev, depth, &sv, &srv), "CreateShaderResourceView depth");
                Keep(srv);

                // the reads: x < 10 linear between texel columns 1 and 2 (u 0.5), x < 20 point at u 0.55
                // (column 2), else Load of texel (1, 0)
                byte[] vs = Hlsl("float4 main(uint id : SV_VertexID) : SV_Position { float2 p = float2((id << 1) & 2, id & 2); return float4(p * float2(2, -2) + float2(-1, 1), 0.5, 1); }\n", "vs_5_0");
                byte[] ps = Hlsl("Texture2D<float> depth : register(t0); SamplerState linearS : register(s0); SamplerState pointS : register(s1);\n"
                    + "float4 main(float4 pos : SV_Position) : SV_Target {\n"
                    + "  if (pos.x < 10) return depth.Sample(linearS, float2(0.5, 0.375)).xxxx * float4(1, 0, 0, 0) + float4(0, 0, 0, 1);\n"
                    + "  if (pos.x < 20) return float4(depth.Sample(pointS, float2(0.55, 0.375)), 0, 0, 1);\n"
                    + "  return float4(depth.Load(int3(1, 0, 0)), 0, 0, 1); }\n", "ps_5_0");
                IntPtr v, pp;
                fixed (byte* p = vs) Check(((delegate* unmanaged<IntPtr, byte*, nuint, IntPtr, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateVertexShader))(dev, p, (nuint)vs.Length, IntPtr.Zero, &v), "CreateVertexShader");
                Keep(v);
                fixed (byte* p = ps) Check(((delegate* unmanaged<IntPtr, byte*, nuint, IntPtr, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreatePixelShader))(dev, p, (nuint)ps.Length, IntPtr.Zero, &pp), "CreatePixelShader");
                Keep(pp);
                var samplers = new IntPtr[2];
                for (int i = 0; i < 2; i++)
                {
                    var sd = D3D11_SAMPLER_DESC.Default();
                    sd.Filter = i == 0 ? 0x15u : 0u;            // MIN_MAG_MIP_LINEAR, MIN_MAG_MIP_POINT
                    IntPtr s;
                    Check(((delegate* unmanaged<IntPtr, D3D11_SAMPLER_DESC*, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateSamplerState))(dev, &sd, &s), "CreateSamplerState");
                    samplers[i] = Keep(s);
                }
                var rd = new D3D11_TEXTURE2D_DESC { Width = W, Height = H, MipLevels = 1, ArraySize = 1, Format = DXGI_FORMAT.R16G16B16A16_FLOAT, SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 }, Usage = D3D11_USAGE.DEFAULT, BindFlags = D3D11_BIND.RENDER_TARGET };
                IntPtr rt, rtv;
                Check(((delegate* unmanaged<IntPtr, D3D11_TEXTURE2D_DESC*, D3D11_SUBRESOURCE_DATA*, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateTexture2D))(dev, &rd, null, &rt), "CreateTexture2D rt");
                Keep(rt);
                Check(((delegate* unmanaged<IntPtr, IntPtr, void*, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateRenderTargetView))(dev, rt, null, &rtv), "CreateRenderTargetView");
                Keep(rtv);
                ((delegate* unmanaged<IntPtr, IntPtr, IntPtr*, uint, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.VSSetShader))(ctx, v, null, 0);
                ((delegate* unmanaged<IntPtr, IntPtr, IntPtr*, uint, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.PSSetShader))(ctx, pp, null, 0);
                ((delegate* unmanaged<IntPtr, uint, uint, IntPtr*, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.PSSetShaderResources))(ctx, 0, 1, &srv);
                fixed (IntPtr* sp = samplers) ((delegate* unmanaged<IntPtr, uint, uint, IntPtr*, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.PSSetSamplers))(ctx, 0, 2, sp);
                ((delegate* unmanaged<IntPtr, uint, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.IASetPrimitiveTopology))(ctx, 4);
                ((delegate* unmanaged<IntPtr, uint, IntPtr*, IntPtr, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.OMSetRenderTargets))(ctx, 1, &rtv, IntPtr.Zero);
                var vp2 = new D3D11_VIEWPORT { Width = W, Height = H, MaxDepth = 1 };
                ((delegate* unmanaged<IntPtr, uint, D3D11_VIEWPORT*, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.RSSetViewports))(ctx, 1, &vp2);
                ((delegate* unmanaged<IntPtr, uint, uint, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.Draw))(ctx, 3, 0);
                var px = ReadTarget(dev, ctx, rt, Keep);
                float[] At(int x) => new[] { px[(5 * W + x) * 4], px[(5 * W + x) * 4 + 1], px[(5 * W + x) * 4 + 2], px[(5 * W + x) * 4 + 3] };
                void Near(float[] want, float[] got, string what)
                {
                    for (int i = 0; i < 4; i++) Assert.True(Math.Abs(want[i] - got[i]) < 0.01f, $"{what}: ({string.Join(", ", got)}), expected ({string.Join(", ", want)})");
                }
                Near(new[] { 0.625f, 0, 0, 1 }, At(5), "linear, between 0.25 and 1");
                Near(new[] { 1f, 0, 0, 1 }, At(15), "point, the right half");
                Near(new[] { 0.25f, 0, 0, 1 }, At(25), "Load, the left half");
            }
            finally
            {
                ((delegate* unmanaged<IntPtr, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.ClearState))(ctx);
                foreach (IntPtr p in made) Release(p);
                Release(ctx); Release(dev);
            }
        }
    }
}
