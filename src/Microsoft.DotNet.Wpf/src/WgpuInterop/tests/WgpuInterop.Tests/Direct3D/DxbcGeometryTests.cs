// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The geometry-shader path's own plumbing, against WARP: the vertex shader run as compute fetches
// every vertex format itself (packed, normalized, half, 10:10:10:2, BGRA, unaligned elements, a
// per-instance buffer), from an indexed 16-bit triangle strip with a base vertex and instances, and
// the strips the geometry shader makes keep their winding.
//

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Wpf.Interop.WebGpu.Direct3D;
using WgpuInterop.Tests.Harness;
using Xunit;
using static WgpuInterop.Tests.Direct3D.DxbcPixelParity;

namespace WgpuInterop.Tests.Direct3D
{
    public sealed unsafe class DxbcGeometryTests
    {
        private const string Vs = @"
struct O { float4 pos : SV_Position; float4 a : TEXCOORD0; float4 b : TEXCOORD1; float4 c : TEXCOORD2; float4 d : TEXCOORD3; nointerpolation uint4 e : TEXCOORD4; };
O main(float3 p : POSITION, float4 c0 : COLOR0, float4 c1 : COLOR1, float2 h : TEXCOORD0, float2 s : TEXCOORD1, float4 t : TEXCOORD2,
       uint4 u : TEXCOORD3, int2 i : TEXCOORD4, float2 n : TEXCOORD5, float4 inst : TEXCOORD6, uint vid : SV_VertexID, uint iid : SV_InstanceID)
{
    O o;
    o.pos = float4(p.xy * 0.45 + inst.xy, p.z, 1);
    o.a = c0; o.b = c1; o.c = float4(h, s); o.d = t + float4(n, 0, 0);
    o.e = u + uint4(i.x + 8, i.y + 8, vid, iid);
    return o;
}";

        private const string Gs = @"
struct V { float4 pos : SV_Position; float4 a : TEXCOORD0; float4 b : TEXCOORD1; float4 c : TEXCOORD2; float4 d : TEXCOORD3; nointerpolation uint4 e : TEXCOORD4; };
[maxvertexcount(4)]
void main(triangle V v[3], uint prim : SV_PrimitiveID, inout TriangleStream<V> s)
{
    // the triangle, and a fourth vertex past its first edge: a strip of two, the second one odd
    [unroll] for (int k = 0; k < 3; k++) { V o = v[k]; o.e.w += prim * 16; s.Append(o); }
    V x = v[0]; x.pos.xy = v[1].pos.xy + v[2].pos.xy - v[0].pos.xy; x.e.w += prim * 16 + 100; s.Append(x);
}";

        private const string Ps = @"
struct V { float4 pos : SV_Position; float4 a : TEXCOORD0; float4 b : TEXCOORD1; float4 c : TEXCOORD2; float4 d : TEXCOORD3; nointerpolation uint4 e : TEXCOORD4; };
struct O { float4 a : SV_Target0; float4 b : SV_Target1; float4 c : SV_Target2; float4 d : SV_Target3; float4 e : SV_Target4; float4 f : SV_Target5; };
O main(V v, bool face : SV_IsFrontFace)
{
    O o;
    o.a = v.a; o.b = v.b; o.c = v.c; o.d = v.d; o.e = float4(v.e); o.f = float4(face ? 1 : 0, 0, 0, 1);
    return o;
}";

        [DllImport("d3d11.dll", EntryPoint = "D3D11CreateDevice")]
        private static extern int RealCreateDevice(IntPtr adapter, uint driverType, IntPtr software, uint flags, uint* levels, uint count, uint sdk, IntPtr* device, uint* level, IntPtr* context);

        [Fact]
        public void TheVertexShaderAsCompute_FetchesEveryFormatAsWarp() => DrawsAsWarp(geometry: true, stride: 42);

        /// <summary>The same draw with no geometry shader: the native vertex stage's formats, and D3D's
        /// SV_VertexID (the index as stored) and SV_InstanceID (from 0) though WebGPU's include the
        /// base vertex and start instance. WebGPU strides are multiples of 4.</summary>
        [Fact]
        public void TheVertexShader_SeesD3DsVertexAndInstanceIds() => DrawsAsWarp(geometry: false, stride: 44);

        private void DrawsAsWarp(bool geometry, int stride)
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "real Direct3D 11 (WARP) and the HLSL compiler exist on Windows only");
            Assert.SkipWhen(Requirements.GpuUnavailable is not null, Requirements.GpuUnavailable ?? "");
            byte[] vs = Compile(Vs, "vs_5_0", out string? e1), gs = Compile(Gs, "gs_5_0", out string? e2), ps = Compile(Ps, "ps_5_0", out string? e3);
            Assert.True(vs.Length > 0 && gs.Length > 0 && ps.Length > 0, e1 + e2 + e3);

            IntPtr wDev, wCtx, sDev, sCtx;
            uint level, want = 0xb000;
            Assert.True(RealCreateDevice(IntPtr.Zero, 5, IntPtr.Zero, 0, &want, 1, 7, &wDev, &level, &wCtx) >= 0, "WARP");
            Assert.True(Exports.CreateDevicePointer(IntPtr.Zero, 1, IntPtr.Zero, 0, null, 0, 7, &sDev, &level, &sCtx) >= 0, "shim");
            try
            {
                var warp = Draw(wDev, wCtx, vs, geometry ? gs : null, ps, stride);
                var shim = Draw(sDev, sCtx, vs, geometry ? gs : null, ps, stride);
                int covered = Covered(warp);
                Assert.True(covered > 30, $"WARP covers {covered} pixels");
                var (count, first) = Compare(warp, shim);
                int edge = count == 0 ? 0 : OnlyCoverageDiffers(warp, shim, 3);
                Assert.True(count == 0 || edge > 0, $"{count} components differ (WARP covers {covered}, the shim {Covered(shim)}); {first}; by target: "
                    + string.Join(", ", Enumerable.Range(0, warp.Count).Select(t => $"{t}: {Compare(new() { warp[t] }, new() { shim[t] }).Count} ({Compare(new() { warp[t] }, new() { shim[t] }).First})")));
                var context = Microsoft.Wpf.Interop.WebGpu.Direct3D.ComObject.From<D3DContext>(sCtx);
                Assert.Equal(0, context!.DrawsSkipped);
            }
            finally
            {
                foreach (IntPtr p in new[] { sCtx, sDev, wCtx, wDev }) Release(p);
            }
        }

        /// <summary>Two instances of an indexed 16-bit triangle strip of five vertices (three
        /// triangles), from a base vertex, through the pass-through geometry shader.</summary>
        private static List<float[]> Draw(IntPtr dev, IntPtr ctx, byte[] vsBlob, byte[]? gsBlob, byte[] psBlob, int Stride)
        {
            var made = new List<IntPtr>();
            IntPtr Keep(IntPtr p) { made.Add(p); return p; }
            var handles = new List<GCHandle>();
            byte* Name(string s) { var h = GCHandle.Alloc(Encoding.ASCII.GetBytes(s + "\0"), GCHandleType.Pinned); handles.Add(h); return (byte*)h.AddrOfPinnedObject(); }
            try
            {
                // slot 0: 42 bytes of elements a vertex (at a stride of 42 every other vertex starts off a word)
                var el = new D3D11_INPUT_ELEMENT_DESC[]
                {
                    new() { SemanticName = Name("POSITION"), Format = DXGI_FORMAT.R32G32B32_FLOAT, AlignedByteOffset = 0 },
                    new() { SemanticName = Name("COLOR"), SemanticIndex = 0, Format = DXGI_FORMAT.R8G8B8A8_UNORM, AlignedByteOffset = 12 },
                    new() { SemanticName = Name("COLOR"), SemanticIndex = 1, Format = DXGI_FORMAT.B8G8R8A8_UNORM, AlignedByteOffset = 16 },
                    new() { SemanticName = Name("TEXCOORD"), SemanticIndex = 0, Format = DXGI_FORMAT.R16G16_FLOAT, AlignedByteOffset = 20 },
                    new() { SemanticName = Name("TEXCOORD"), SemanticIndex = 1, Format = DXGI_FORMAT.R16G16_SNORM, AlignedByteOffset = 24 },
                    new() { SemanticName = Name("TEXCOORD"), SemanticIndex = 2, Format = DXGI_FORMAT.R10G10B10A2_UNORM, AlignedByteOffset = 28 },
                    new() { SemanticName = Name("TEXCOORD"), SemanticIndex = 3, Format = DXGI_FORMAT.R8G8B8A8_UINT, AlignedByteOffset = 32 },
                    new() { SemanticName = Name("TEXCOORD"), SemanticIndex = 4, Format = DXGI_FORMAT.R16G16_SINT, AlignedByteOffset = 36 },
                    new() { SemanticName = Name("TEXCOORD"), SemanticIndex = 5, Format = DXGI_FORMAT.R8G8_UNORM, AlignedByteOffset = 40 },
                    new() { SemanticName = Name("TEXCOORD"), SemanticIndex = 6, Format = DXGI_FORMAT.R32G32B32A32_FLOAT, InputSlot = 1, AlignedByteOffset = 0, InputSlotClass = 1, InstanceDataStepRate = 1 },
                };
                const int Vertices = 8;
                var data = new byte[Stride * Vertices];
                var rnd = new Random(11);
                rnd.NextBytes(data);
                for (int v = 0; v < Vertices; v++)
                {
                    // a fan of positions around the origin; half floats that are ordinary numbers
                    float a = v * 0.8f;
                    BitConverter.TryWriteBytes(data.AsSpan(v * Stride), MathF.Cos(a));
                    BitConverter.TryWriteBytes(data.AsSpan(v * Stride + 4), MathF.Sin(a) * (v % 2 == 0 ? 1 : -0.5f));
                    BitConverter.TryWriteBytes(data.AsSpan(v * Stride + 8), 0.25f + 0.05f * v);
                    BitConverter.TryWriteBytes(data.AsSpan(v * Stride + 20), (Half)(0.1f * v - 0.3f));
                    BitConverter.TryWriteBytes(data.AsSpan(v * Stride + 22), (Half)(1.5f + v));
                }
                float[] inst = { -0.5f, 0.4f, 0, 0, 0.5f, -0.4f, 0, 0, 0.45f, 0.45f, 0, 0 };
                ushort[] idx = { 0, 0, 1, 2, 3, 4, 5 };        // the draw starts at index 1, base vertex 2

                IntPtr vs, gs = IntPtr.Zero, psh, layout, vb0, vb1, ib;
                fixed (byte* v = vsBlob) Check(((delegate* unmanaged<IntPtr, byte*, nuint, IntPtr, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateVertexShader))(dev, v, (nuint)vsBlob.Length, IntPtr.Zero, &vs), "CreateVertexShader");
                Keep(vs);
                if (gsBlob != null) fixed (byte* g = gsBlob) Check(((delegate* unmanaged<IntPtr, byte*, nuint, IntPtr, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateGeometryShader))(dev, g, (nuint)gsBlob.Length, IntPtr.Zero, &gs), "CreateGeometryShader");
                if (gs != IntPtr.Zero) Keep(gs);
                fixed (byte* p = psBlob) Check(((delegate* unmanaged<IntPtr, byte*, nuint, IntPtr, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreatePixelShader))(dev, p, (nuint)psBlob.Length, IntPtr.Zero, &psh), "CreatePixelShader");
                Keep(psh);
                fixed (D3D11_INPUT_ELEMENT_DESC* ep = el)
                fixed (byte* code = vsBlob)
                    Check(((delegate* unmanaged<IntPtr, D3D11_INPUT_ELEMENT_DESC*, uint, byte*, nuint, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateInputLayout))(dev, ep, (uint)el.Length, code, (nuint)vsBlob.Length, &layout), "CreateInputLayout");
                Keep(layout);
                IntPtr MakeBuffer(void* p, int bytes, D3D11_BIND bind)
                {
                    IntPtr b;
                    var bd = new D3D11_BUFFER_DESC { ByteWidth = (uint)((bytes + 3) & ~3), Usage = D3D11_USAGE.DEFAULT, BindFlags = bind };
                    var padded = new byte[bd.ByteWidth];
                    new ReadOnlySpan<byte>(p, bytes).CopyTo(padded);
                    fixed (byte* q = padded)
                    {
                        var init = new D3D11_SUBRESOURCE_DATA { pSysMem = q };
                        Check(((delegate* unmanaged<IntPtr, D3D11_BUFFER_DESC*, D3D11_SUBRESOURCE_DATA*, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateBuffer))(dev, &bd, &init, &b), "CreateBuffer");
                    }
                    return Keep(b);
                }
                fixed (byte* d = data) vb0 = MakeBuffer(d, data.Length, D3D11_BIND.VERTEX_BUFFER);
                fixed (float* d = inst) vb1 = MakeBuffer(d, inst.Length * 4, D3D11_BIND.VERTEX_BUFFER);
                fixed (ushort* d = idx) ib = MakeBuffer(d, idx.Length * 2, D3D11_BIND.INDEX_BUFFER);

                var rts = new IntPtr[6];
                var tex = new List<IntPtr>();
                for (int i = 0; i < 6; i++)
                {
                    var td = new D3D11_TEXTURE2D_DESC { Width = W, Height = H, MipLevels = 1, ArraySize = 1, Format = DXGI_FORMAT.R16G16B16A16_FLOAT, SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 }, Usage = D3D11_USAGE.DEFAULT, BindFlags = D3D11_BIND.RENDER_TARGET };
                    IntPtr t, rtv;
                    Check(((delegate* unmanaged<IntPtr, D3D11_TEXTURE2D_DESC*, D3D11_SUBRESOURCE_DATA*, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateTexture2D))(dev, &td, null, &t), "CreateTexture2D");
                    Keep(t); tex.Add(t);
                    Check(((delegate* unmanaged<IntPtr, IntPtr, void*, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateRenderTargetView))(dev, t, null, &rtv), "CreateRenderTargetView");
                    rts[i] = Keep(rtv);
                    float* clear = stackalloc float[] { 0.25f, 0.5f, 0.75f, 1f };
                    ((delegate* unmanaged<IntPtr, IntPtr, float*, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.ClearRenderTargetView))(ctx, rtv, clear);
                }
                var rd = D3D11_RASTERIZER_DESC.Default();
                rd.CullMode = 1;
                IntPtr rs;
                Check(((delegate* unmanaged<IntPtr, D3D11_RASTERIZER_DESC*, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateRasterizerState))(dev, &rd, &rs), "CreateRasterizerState");
                Keep(rs);
                ((delegate* unmanaged<IntPtr, IntPtr, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.RSSetState))(ctx, rs);

                IntPtr* bufs = stackalloc IntPtr[2] { vb0, vb1 };
                uint* strides = stackalloc uint[2] { (uint)Stride, 16 };
                uint* offsets = stackalloc uint[2] { 0, 0 };
                ((delegate* unmanaged<IntPtr, uint, uint, IntPtr*, uint*, uint*, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.IASetVertexBuffers))(ctx, 0, 2, bufs, strides, offsets);
                ((delegate* unmanaged<IntPtr, IntPtr, DXGI_FORMAT, uint, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.IASetIndexBuffer))(ctx, ib, DXGI_FORMAT.R16_UINT, 0);
                ((delegate* unmanaged<IntPtr, IntPtr, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.IASetInputLayout))(ctx, layout);
                ((delegate* unmanaged<IntPtr, uint, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.IASetPrimitiveTopology))(ctx, 5);
                ((delegate* unmanaged<IntPtr, IntPtr, IntPtr*, uint, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.VSSetShader))(ctx, vs, null, 0);
                ((delegate* unmanaged<IntPtr, IntPtr, IntPtr*, uint, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.GSSetShader))(ctx, gs, null, 0);
                ((delegate* unmanaged<IntPtr, IntPtr, IntPtr*, uint, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.PSSetShader))(ctx, psh, null, 0);
                fixed (IntPtr* r = rts) ((delegate* unmanaged<IntPtr, uint, IntPtr*, IntPtr, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.OMSetRenderTargets))(ctx, 6, r, IntPtr.Zero);
                var vp = new D3D11_VIEWPORT { Width = W, Height = H, MaxDepth = 1 };
                ((delegate* unmanaged<IntPtr, uint, D3D11_VIEWPORT*, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.RSSetViewports))(ctx, 1, &vp);
                // DrawIndexedInstanced(indexCount 5, instances 2, start index 1, base vertex 2, start instance 1)
                ((delegate* unmanaged<IntPtr, uint, uint, uint, int, uint, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.DrawIndexedInstanced))(ctx, 5, 2, 1, 2, 1);
                return tex.Select(t => ReadTarget(dev, ctx, t, Keep)).ToList();
            }
            finally
            {
                ((delegate* unmanaged<IntPtr, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.ClearState))(ctx);
                foreach (IntPtr p in made) Release(p);
                foreach (var h in handles) h.Free();
            }
        }
    }
}
