// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The Direct3D 11 shim, milestone 1 (Documentation/d3d11-webgpu-shim.md): a device, its immediate
// context, buffers and 2D textures, a clear and one triangle drawn with fxc-compiled SM4 shaders, read
// back through a staging texture's Map.
//
// Every call goes through vtable slots, the way SharpDX makes them, using the slot numbers of
// Vtbl.g.cs (generated from the Windows SDK headers). On Windows the same calls are made on a real
// Direct3D 11 device (WARP): that both proves the slot numbers against the real ABI and gives the
// pixels to compare with.
//

using System;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Wpf.Interop.WebGpu.Direct3D;
using WgpuInterop.Tests.Harness;
using Xunit;

namespace WgpuInterop.Tests.Direct3D
{
    public sealed unsafe class D3D11ShimTests
    {
        private const int W = 64, H = 64;

        // m1.hlsl, compiled with fxc 10.1 (vs_4_0 / ps_4_0):
        //   cbuffer Tint : register(b0) { float4 tint; float4 offset; };
        //   struct VsIn { float3 pos : POSITION; float4 color : COLOR0; };
        //   struct VsOut { float4 pos : SV_Position; float4 color : COLOR0; };
        //   VsOut vs(VsIn i) { VsOut o; o.pos = float4(i.pos + offset.xyz, 1.0); o.color = i.color; return o; }
        //   float4 ps(VsOut i) : SV_Target { return i.color * tint; }
        internal static readonly byte[] TriangleVs = Convert.FromBase64String(
            "RFhCQ+LTLY45xVqGCvTAexNLMS8BAAAA0AIAAAUAAAA0AAAAEAEAAGABAAC0AQAAVAIAAFJERUbUAAAAAQAAAEQAAAABAAAAHAAAAAAE/v8AAQAAqwAAADwAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAEAAAABAAAAVGludACrq6s8AAAAAgAAAFwAAAAgAAAAAAAAAAAAAACMAAAAAAAAABAAAAAAAAAAlAAAAAAAAACkAAAAEAAAABAAAAACAAAAlAAAAAAAAAB0aW50AKurqwEAAwABAAQAAAAAAAAAAABvZmZzZXQATWljcm9zb2Z0IChSKSBITFNMIFNoYWRlciBDb21waWxlciAxMC4xAKtJU0dOSAAAAAIAAAAIAAAAOAAAAAAAAAAAAAAAAwAAAAAAAAAHBwAAQQAAAAAAAAAAAAAAAwAAAAEAAAAPDwAAUE9TSVRJT04AQ09MT1IAq09TR05MAAAAAgAAAAgAAAA4AAAAAAAAAAEAAAADAAAAAAAAAA8AAABEAAAAAAAAAAAAAAADAAAAAQAAAA8AAABTVl9Qb3NpdGlvbgBDT0xPUgCrq1NIRFKYAAAAQAABACYAAABZAAAERo4gAAAAAAACAAAAXwAAA3IQEAAAAAAAXwAAA/IQEAABAAAAZwAABPIgEAAAAAAAAQAAAGUAAAPyIBAAAQAAAAAAAAhyIBAAAAAAAEYSEAAAAAAARoIgAAAAAAABAAAANgAABYIgEAAAAAAAAUAAAAAAgD82AAAF8iAQAAEAAABGHhAAAQAAAD4AAAFTVEFUdAAAAAQAAAAAAAAAAAAAAAQAAAABAAAAAAAAAAAAAAABAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAACAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA");
        internal static readonly byte[] TrianglePs = Convert.FromBase64String(
            "RFhCQ/b/52Wv/eGMh9yql34fL3YBAAAAcAIAAAUAAAA0AAAAEAEAAGQBAACYAQAA9AEAAFJERUbUAAAAAQAAAEQAAAABAAAAHAAAAAAE//8AAQAAqwAAADwAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAEAAAABAAAAVGludACrq6s8AAAAAgAAAFwAAAAgAAAAAAAAAAAAAACMAAAAAAAAABAAAAACAAAAlAAAAAAAAACkAAAAEAAAABAAAAAAAAAAlAAAAAAAAAB0aW50AKurqwEAAwABAAQAAAAAAAAAAABvZmZzZXQATWljcm9zb2Z0IChSKSBITFNMIFNoYWRlciBDb21waWxlciAxMC4xAKtJU0dOTAAAAAIAAAAIAAAAOAAAAAAAAAABAAAAAwAAAAAAAAAPAAAARAAAAAAAAAAAAAAAAwAAAAEAAAAPDwAAU1ZfUG9zaXRpb24AQ09MT1IAq6tPU0dOLAAAAAEAAAAIAAAAIAAAAAAAAAAAAAAAAwAAAAAAAAAPAAAAU1ZfVGFyZ2V0AKurU0hEUlQAAABAAAAAFQAAAFkAAARGjiAAAAAAAAEAAABiEAAD8hAQAAEAAABlAAAD8iAQAAAAAAA4AAAI8iAQAAAAAABGHhAAAQAAAEaOIAAAAAAAAAAAAD4AAAFTVEFUdAAAAAIAAAAAAAAAAAAAAAIAAAABAAAAAAAAAAAAAAABAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA");

        // ---- calling through the vtable --------------------------------------------------------------

        private static IntPtr Fn(IntPtr com, int slot) => (*(IntPtr**)com)[slot];

        private static void Release(IntPtr com)
        {
            if (com != IntPtr.Zero) ((delegate* unmanaged<IntPtr, uint>)Fn(com, 2))(com);
        }

        private static void Check(int hr, string what) => Assert.True(hr >= 0, $"{what} failed: 0x{hr:x8}");

        [DllImport("d3d11.dll", EntryPoint = "D3D11CreateDevice")]
        private static extern int RealCreateDevice(IntPtr adapter, uint driverType, IntPtr software, uint flags, uint* levels, uint count, uint sdk, IntPtr* device, uint* level, IntPtr* context);

        private static (IntPtr Device, IntPtr Context) Ours()
        {
            IntPtr dev, ctx;
            uint level;
            Check(Exports.CreateDevicePointer(IntPtr.Zero, 1, IntPtr.Zero, 0, null, 0, 7, &dev, &level, &ctx), "D3D11CreateDevice (shim)");
            Assert.Equal((uint)0xb000, level);
            return (dev, ctx);
        }

        private static (IntPtr Device, IntPtr Context) Warp()
        {
            IntPtr dev, ctx;
            uint level;
            uint want = 0xb000;
            Check(RealCreateDevice(IntPtr.Zero, 5 /* WARP */, IntPtr.Zero, 0, &want, 1, 7, &dev, &level, &ctx), "D3D11CreateDevice (WARP)");
            return (dev, ctx);
        }

        /// <summary>Clears a 64x64 BGRA target to a dark blue, draws one triangle (vertex colour times a
        /// constant-buffer tint, moved by a constant-buffer offset), and reads the target back through a
        /// staging texture: BGRA rows, tightly packed.</summary>
        internal static byte[] RenderTriangle(IntPtr dev, IntPtr ctx)
        {
            var created = new System.Collections.Generic.List<IntPtr>();
            IntPtr Keep(IntPtr p) { created.Add(p); return p; }
            try
            {
                // target and staging copy
                var td = new D3D11_TEXTURE2D_DESC
                {
                    Width = W, Height = H, MipLevels = 1, ArraySize = 1, Format = DXGI_FORMAT.B8G8R8A8_UNORM,
                    SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 }, Usage = D3D11_USAGE.DEFAULT,
                    BindFlags = D3D11_BIND.RENDER_TARGET | D3D11_BIND.SHADER_RESOURCE,
                };
                IntPtr rt, staging, rtv;
                Check(((delegate* unmanaged<IntPtr, D3D11_TEXTURE2D_DESC*, D3D11_SUBRESOURCE_DATA*, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateTexture2D))(dev, &td, null, &rt), "CreateTexture2D");
                Keep(rt);
                var sd = td;
                sd.Usage = D3D11_USAGE.STAGING; sd.BindFlags = 0; sd.CPUAccessFlags = D3D11_CPU_ACCESS.READ;
                Check(((delegate* unmanaged<IntPtr, D3D11_TEXTURE2D_DESC*, D3D11_SUBRESOURCE_DATA*, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateTexture2D))(dev, &sd, null, &staging), "CreateTexture2D staging");
                Keep(staging);
                Check(((delegate* unmanaged<IntPtr, IntPtr, void*, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateRenderTargetView))(dev, rt, null, &rtv), "CreateRenderTargetView");
                Keep(rtv);

                // shaders and input layout
                IntPtr vs, ps, layout;
                fixed (byte* v = TriangleVs)
                fixed (byte* p = TrianglePs)
                {
                    Check(((delegate* unmanaged<IntPtr, byte*, nuint, IntPtr, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateVertexShader))(dev, v, (nuint)TriangleVs.Length, IntPtr.Zero, &vs), "CreateVertexShader");
                    Keep(vs);
                    Check(((delegate* unmanaged<IntPtr, byte*, nuint, IntPtr, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreatePixelShader))(dev, p, (nuint)TrianglePs.Length, IntPtr.Zero, &ps), "CreatePixelShader");
                    Keep(ps);
                    byte[] pos = Encoding.ASCII.GetBytes("POSITION\0"), col = Encoding.ASCII.GetBytes("COLOR\0");
                    fixed (byte* pn = pos)
                    fixed (byte* cn = col)
                    {
                        var elements = stackalloc D3D11_INPUT_ELEMENT_DESC[2];
                        elements[0] = new D3D11_INPUT_ELEMENT_DESC { SemanticName = pn, Format = DXGI_FORMAT.R32G32B32_FLOAT, AlignedByteOffset = 0 };
                        elements[1] = new D3D11_INPUT_ELEMENT_DESC { SemanticName = cn, Format = DXGI_FORMAT.R32G32B32A32_FLOAT, AlignedByteOffset = 12 };
                        Check(((delegate* unmanaged<IntPtr, D3D11_INPUT_ELEMENT_DESC*, uint, byte*, nuint, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateInputLayout))(dev, elements, 2, v, (nuint)TriangleVs.Length, &layout), "CreateInputLayout");
                        Keep(layout);
                    }
                }

                // a triangle in the middle, one colour, and the constants
                float* verts = stackalloc float[]
                {
                    -0.5f, -0.5f, 0f,   1f, 0.5f, 0.25f, 1f,
                     0.0f,  0.6f, 0f,   1f, 0.5f, 0.25f, 1f,
                     0.5f, -0.5f, 0f,   1f, 0.5f, 0.25f, 1f,
                };
                float* consts = stackalloc float[] { 0.5f, 1f, 1f, 1f, 0.1f, 0f, 0f, 0f };
                IntPtr vb, cb;
                var bd = new D3D11_BUFFER_DESC { ByteWidth = 3 * 7 * 4, Usage = D3D11_USAGE.DEFAULT, BindFlags = D3D11_BIND.VERTEX_BUFFER };
                var init = new D3D11_SUBRESOURCE_DATA { pSysMem = verts };
                Check(((delegate* unmanaged<IntPtr, D3D11_BUFFER_DESC*, D3D11_SUBRESOURCE_DATA*, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateBuffer))(dev, &bd, &init, &vb), "CreateBuffer vb");
                Keep(vb);
                var cd = new D3D11_BUFFER_DESC { ByteWidth = 32, Usage = D3D11_USAGE.DEFAULT, BindFlags = D3D11_BIND.CONSTANT_BUFFER };
                var cinit = new D3D11_SUBRESOURCE_DATA { pSysMem = consts };
                Check(((delegate* unmanaged<IntPtr, D3D11_BUFFER_DESC*, D3D11_SUBRESOURCE_DATA*, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateBuffer))(dev, &cd, &cinit, &cb), "CreateBuffer cb");
                Keep(cb);

                // the context
                uint stride = 28, offset = 0;
                ((delegate* unmanaged<IntPtr, IntPtr, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.IASetInputLayout))(ctx, layout);
                ((delegate* unmanaged<IntPtr, uint, uint, IntPtr*, uint*, uint*, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.IASetVertexBuffers))(ctx, 0, 1, &vb, &stride, &offset);
                ((delegate* unmanaged<IntPtr, uint, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.IASetPrimitiveTopology))(ctx, 4);
                ((delegate* unmanaged<IntPtr, IntPtr, IntPtr*, uint, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.VSSetShader))(ctx, vs, null, 0);
                ((delegate* unmanaged<IntPtr, uint, uint, IntPtr*, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.VSSetConstantBuffers))(ctx, 0, 1, &cb);
                ((delegate* unmanaged<IntPtr, IntPtr, IntPtr*, uint, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.PSSetShader))(ctx, ps, null, 0);
                ((delegate* unmanaged<IntPtr, uint, uint, IntPtr*, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.PSSetConstantBuffers))(ctx, 0, 1, &cb);
                ((delegate* unmanaged<IntPtr, uint, IntPtr*, IntPtr, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.OMSetRenderTargets))(ctx, 1, &rtv, IntPtr.Zero);
                var vp = new D3D11_VIEWPORT { Width = W, Height = H, MaxDepth = 1 };
                ((delegate* unmanaged<IntPtr, uint, D3D11_VIEWPORT*, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.RSSetViewports))(ctx, 1, &vp);
                float* clear = stackalloc float[] { 0.0f, 0.125f, 0.5f, 1f };
                ((delegate* unmanaged<IntPtr, IntPtr, float*, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.ClearRenderTargetView))(ctx, rtv, clear);
                ((delegate* unmanaged<IntPtr, uint, uint, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.Draw))(ctx, 3, 0);
                ((delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.CopyResource))(ctx, staging, rt);

                D3D11_MAPPED_SUBRESOURCE m;
                Check(((delegate* unmanaged<IntPtr, IntPtr, uint, D3D11_MAP, uint, D3D11_MAPPED_SUBRESOURCE*, int>)Fn(ctx, Vtbl.ID3D11DeviceContext.Map))(ctx, staging, 0, D3D11_MAP.READ, 0, &m), "Map");
                var pixels = new byte[W * H * 4];
                for (int y = 0; y < H; y++) Marshal.Copy((IntPtr)((byte*)m.pData + y * m.RowPitch), pixels, y * W * 4, W * 4);
                ((delegate* unmanaged<IntPtr, IntPtr, uint, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.Unmap))(ctx, staging, 0);
                ((delegate* unmanaged<IntPtr, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.ClearState))(ctx);
                return pixels;
            }
            finally
            {
                foreach (IntPtr p in created) Release(p);
            }
        }

        private static (byte B, byte G, byte R, byte A) At(byte[] px, int x, int y)
        {
            int i = (y * W + x) * 4;
            return (px[i], px[i + 1], px[i + 2], px[i + 3]);
        }

        [Fact]
        public void AClearAndATriangle_ReadBackThroughAStagingTexture()
        {
            Assert.SkipWhen(Requirements.GpuUnavailable is not null, Requirements.GpuUnavailable ?? "");
            var (dev, ctx) = Ours();
            try
            {
                byte[] px = RenderTriangle(dev, ctx);
                // the clear colour in a corner: (0, 0.125, 0.5) -> B 128, G 32, R 0
                var corner = At(px, 1, 1);
                Assert.Equal((128, 32, 0, 255), ((int)corner.B, (int)corner.G, (int)corner.R, (int)corner.A));
                // the triangle in the middle (offset 0.1 to the right): (1, 0.5, 0.25) * (0.5, 1, 1) = (0.5, 0.5, 0.25)
                var centre = At(px, W / 2 + 3, H / 2 + 4);
                Assert.Equal((64, 128, 128, 255), ((int)centre.B, (int)centre.G, (int)centre.R, (int)centre.A));
                // and the offset moved it right: the left tip's column is clear
                var left = At(px, 17, H / 2 + 14);
                Assert.Equal((128, 32, 0), ((int)left.B, (int)left.G, (int)left.R));
            }
            finally
            {
                Release(ctx);
                Release(dev);
            }
        }

        [Fact]
        public void TheSameCallsOnRealDirect3D11_GiveTheSamePixels()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "real Direct3D 11 (WARP) exists on Windows only");
            Assert.SkipWhen(Requirements.GpuUnavailable is not null, Requirements.GpuUnavailable ?? "");
            byte[] warp, ours;
            var (wd, wc) = Warp();
            try { warp = RenderTriangle(wd, wc); }
            finally { Release(wc); Release(wd); }
            var (od, oc) = Ours();
            try { ours = RenderTriangle(od, oc); }
            finally { Release(oc); Release(od); }

            int differing = 0, worst = 0;
            for (int i = 0; i < warp.Length; i += 4)
            {
                int d = 0;
                for (int c = 0; c < 4; c++) d = Math.Max(d, Math.Abs(warp[i + c] - ours[i + c]));
                if (d > 0) differing++;
                worst = Math.Max(worst, d);
            }
            Assert.True(differing == 0, $"{differing} of {W * H} pixels differ from WARP (worst by {worst})");
        }
    }
}
