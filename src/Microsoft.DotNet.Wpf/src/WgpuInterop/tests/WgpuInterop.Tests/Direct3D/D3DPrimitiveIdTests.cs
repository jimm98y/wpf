// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// SV_PrimitiveID in a pixel shader after a geometry shader: the one the geometry shader writes (here
// its input primitive's plus 7), on every adapter -- a linked value, not WebGPU's primitive index.
//

using System;
using System.Collections.Generic;
using Microsoft.Wpf.Interop.WebGpu.Direct3D;
using WgpuInterop.Tests.Harness;
using Xunit;
using static WgpuInterop.Tests.Direct3D.DxbcPixelParity;

namespace WgpuInterop.Tests.Direct3D
{
    public sealed unsafe class D3DPrimitiveIdTests
    {
        private static byte[] Hlsl(string src, string profile)
        {
            var b = HlslCompilerTests.Compile(src, "prim.hlsl", "main", profile, Array.Empty<string>(), out string err);
            Assert.True(b != null, err);
            return b!;
        }

        [Fact]
        public void APixelShaderAfterAGeometryShader_ReadsThePrimitiveIdItWrites()
        {
            Assert.SkipWhen(Requirements.GpuUnavailable is not null, Requirements.GpuUnavailable ?? "");
            IntPtr dev, ctx;
            uint level;
            Assert.True(Exports.CreateDevicePointer(IntPtr.Zero, 1, IntPtr.Zero, 0, null, 0, 7, &dev, &level, &ctx) >= 0, "shim");
            var made = new List<IntPtr>();
            IntPtr Keep(IntPtr p) { made.Add(p); return p; }
            try
            {
                byte[] vs = Hlsl("static const float2 q[6] = { float2(-1, 1), float2(0, 1), float2(-1, -1), float2(0, 1), float2(1, 1), float2(0, -1) };\n"
                    + "float4 main(uint id : SV_VertexID) : SV_Position { return float4(q[id], 0.5, 1); }\n", "vs_5_0");
                byte[] gs = Hlsl("struct G { float4 pos : SV_Position; uint prim : SV_PrimitiveID; };\n"
                    + "[maxvertexcount(3)] void main(triangle float4 v[3] : SV_Position, uint id : SV_PrimitiveID, inout TriangleStream<G> s)\n"
                    + "{ [unroll] for (int k = 0; k < 3; k++) { G o; o.pos = v[k]; o.prim = id + 7; s.Append(o); } }\n", "gs_5_0");
                byte[] ps = Hlsl("float4 main(float4 p : SV_Position, uint prim : SV_PrimitiveID) : SV_Target { return float4(prim / 16.0, 0, 0, 1); }\n", "ps_5_0");
                IntPtr v, g, pp;
                fixed (byte* p = vs) Check(((delegate* unmanaged<IntPtr, byte*, nuint, IntPtr, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateVertexShader))(dev, p, (nuint)vs.Length, IntPtr.Zero, &v), "CreateVertexShader");
                Keep(v);
                fixed (byte* p = gs) Check(((delegate* unmanaged<IntPtr, byte*, nuint, IntPtr, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateGeometryShader))(dev, p, (nuint)gs.Length, IntPtr.Zero, &g), "CreateGeometryShader");
                Keep(g);
                fixed (byte* p = ps) Check(((delegate* unmanaged<IntPtr, byte*, nuint, IntPtr, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreatePixelShader))(dev, p, (nuint)ps.Length, IntPtr.Zero, &pp), "CreatePixelShader");
                Keep(pp);
                var rd = new D3D11_TEXTURE2D_DESC { Width = W, Height = H, MipLevels = 1, ArraySize = 1, Format = DXGI_FORMAT.R16G16B16A16_FLOAT, SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 }, Usage = D3D11_USAGE.DEFAULT, BindFlags = D3D11_BIND.RENDER_TARGET };
                IntPtr rt, rtv;
                Check(((delegate* unmanaged<IntPtr, D3D11_TEXTURE2D_DESC*, D3D11_SUBRESOURCE_DATA*, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateTexture2D))(dev, &rd, null, &rt), "CreateTexture2D rt");
                Keep(rt);
                Check(((delegate* unmanaged<IntPtr, IntPtr, void*, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateRenderTargetView))(dev, rt, null, &rtv), "CreateRenderTargetView");
                Keep(rtv);
                ((delegate* unmanaged<IntPtr, IntPtr, IntPtr*, uint, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.VSSetShader))(ctx, v, null, 0);
                ((delegate* unmanaged<IntPtr, IntPtr, IntPtr*, uint, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.GSSetShader))(ctx, g, null, 0);
                ((delegate* unmanaged<IntPtr, IntPtr, IntPtr*, uint, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.PSSetShader))(ctx, pp, null, 0);
                ((delegate* unmanaged<IntPtr, uint, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.IASetPrimitiveTopology))(ctx, 4);
                ((delegate* unmanaged<IntPtr, uint, IntPtr*, IntPtr, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.OMSetRenderTargets))(ctx, 1, &rtv, IntPtr.Zero);
                var vp = new D3D11_VIEWPORT { Width = W, Height = H, MaxDepth = 1 };
                ((delegate* unmanaged<IntPtr, uint, D3D11_VIEWPORT*, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.RSSetViewports))(ctx, 1, &vp);
                ((delegate* unmanaged<IntPtr, uint, uint, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.Draw))(ctx, 6, 0);
                var px = ReadTarget(dev, ctx, rt, Keep);
                // the left triangle (primitive 0) covers the top left, the right (1) the top right
                float At(int x, int y) => px[(y * W + x) * 4];
                Assert.True(Math.Abs(At(4, 4) - 7 / 16f) < 0.01f, $"primitive 0: {At(4, 4)}");
                Assert.True(Math.Abs(At(28, 4) - 8 / 16f) < 0.01f, $"primitive 1: {At(28, 4)}");
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
