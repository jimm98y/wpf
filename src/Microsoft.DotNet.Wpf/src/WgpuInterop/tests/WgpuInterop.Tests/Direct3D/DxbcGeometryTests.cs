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

        // SV_CullDistance (two of them): x culls the triangles wholly left of x = 0.45 -- one that
        // straddles it is drawn whole, as a clip distance would not -- and y the second instance
        private const string CullVs = @"
struct O { float4 pos : SV_Position; float4 a : TEXCOORD0; float4 b : TEXCOORD1; float4 c : TEXCOORD2; float4 d : TEXCOORD3; nointerpolation uint4 e : TEXCOORD4; float2 cull : SV_CullDistance0; };
O main(float3 p : POSITION, float4 c0 : COLOR0, float4 c1 : COLOR1, float2 h : TEXCOORD0, float2 s : TEXCOORD1, float4 t : TEXCOORD2,
       uint4 u : TEXCOORD3, int2 i : TEXCOORD4, float2 n : TEXCOORD5, float4 inst : TEXCOORD6, uint vid : SV_VertexID, uint iid : SV_InstanceID)
{
    O o;
    o.pos = float4(p.xy * 0.45 + inst.xy, p.z, 1);
    o.a = c0; o.b = c1; o.c = float4(h, s); o.d = t + float4(n, 0, 0);
    o.e = u + uint4(i.x + 8, i.y + 8, vid, iid);
    o.cull = float2(o.pos.x - 0.45, iid == 1 ? -1 : 1);
    return o;
}";

        private const string CullGs = @"
struct V { float4 pos : SV_Position; float4 a : TEXCOORD0; float4 b : TEXCOORD1; float4 c : TEXCOORD2; float4 d : TEXCOORD3; nointerpolation uint4 e : TEXCOORD4; float2 cull : SV_CullDistance0; };
[maxvertexcount(3)]
void main(triangle V v[3], inout TriangleStream<V> s)
{
    [unroll] for (int k = 0; k < 3; k++) s.Append(v[k]);
}";

        // SV_CullDistance with every inter-stage variable WebGPU has taken by the pixel shader's sixteen inputs
        private const string CullFullVs = @"
struct O { float4 pos : SV_Position; float4 t0 : TEXCOORD0; float4 t1 : TEXCOORD1; float4 t2 : TEXCOORD2; float4 t3 : TEXCOORD3; float4 t4 : TEXCOORD4; float4 t5 : TEXCOORD5; float4 t6 : TEXCOORD6; float4 t7 : TEXCOORD7; float4 t8 : TEXCOORD8; float4 t9 : TEXCOORD9; float4 t10 : TEXCOORD10; float4 t11 : TEXCOORD11; float4 t12 : TEXCOORD12; float4 t13 : TEXCOORD13; float4 t14 : TEXCOORD14; float4 t15 : TEXCOORD15; float2 cull : SV_CullDistance0; };
O main(float3 p : POSITION, float4 inst : TEXCOORD6, uint iid : SV_InstanceID)
{
    O o;
    o.pos = float4(p.xy * 0.45 + inst.xy, p.z, 1);
    o.t0 = o.pos.xyxy * 1 * 0.1 + float4(0, iid, 0.5, 1) * 0.01; o.t1 = o.pos.xyxy * 2 * 0.1 + float4(1, iid, 0.5, 1) * 0.01; o.t2 = o.pos.xyxy * 3 * 0.1 + float4(2, iid, 0.5, 1) * 0.01; o.t3 = o.pos.xyxy * 4 * 0.1 + float4(3, iid, 0.5, 1) * 0.01; o.t4 = o.pos.xyxy * 5 * 0.1 + float4(4, iid, 0.5, 1) * 0.01; o.t5 = o.pos.xyxy * 6 * 0.1 + float4(5, iid, 0.5, 1) * 0.01; o.t6 = o.pos.xyxy * 7 * 0.1 + float4(6, iid, 0.5, 1) * 0.01; o.t7 = o.pos.xyxy * 8 * 0.1 + float4(7, iid, 0.5, 1) * 0.01; o.t8 = o.pos.xyxy * 9 * 0.1 + float4(8, iid, 0.5, 1) * 0.01; o.t9 = o.pos.xyxy * 10 * 0.1 + float4(9, iid, 0.5, 1) * 0.01; o.t10 = o.pos.xyxy * 11 * 0.1 + float4(10, iid, 0.5, 1) * 0.01; o.t11 = o.pos.xyxy * 12 * 0.1 + float4(11, iid, 0.5, 1) * 0.01; o.t12 = o.pos.xyxy * 13 * 0.1 + float4(12, iid, 0.5, 1) * 0.01; o.t13 = o.pos.xyxy * 14 * 0.1 + float4(13, iid, 0.5, 1) * 0.01; o.t14 = o.pos.xyxy * 15 * 0.1 + float4(14, iid, 0.5, 1) * 0.01; o.t15 = o.pos.xyxy * 16 * 0.1 + float4(15, iid, 0.5, 1) * 0.01;
    o.cull = float2(o.pos.x - 0.45, iid == 1 ? -1 : 1);
    return o;
}";

        private const string CullFullPs = @"
struct V { float4 pos : SV_Position; float4 t0 : TEXCOORD0; float4 t1 : TEXCOORD1; float4 t2 : TEXCOORD2; float4 t3 : TEXCOORD3; float4 t4 : TEXCOORD4; float4 t5 : TEXCOORD5; float4 t6 : TEXCOORD6; float4 t7 : TEXCOORD7; float4 t8 : TEXCOORD8; float4 t9 : TEXCOORD9; float4 t10 : TEXCOORD10; float4 t11 : TEXCOORD11; float4 t12 : TEXCOORD12; float4 t13 : TEXCOORD13; float4 t14 : TEXCOORD14; float4 t15 : TEXCOORD15; };
float4 main(V v) : SV_Target0 { return (v.t0 + v.t1 + v.t2 + v.t3 + v.t4 + v.t5 + v.t6 + v.t7 + v.t8 + v.t9 + v.t10 + v.t11 + v.t12 + v.t13 + v.t14 + v.t15) * 0.02; }";

        /// <summary>SV_CullDistance when the pixel shader's inputs leave no inter-stage variable to carry
        /// it: culled through clip distances instead.</summary>
        [Fact]
        public void CullDistances_WithNoInterStageVariableLeft_CullAsWarp() => DrawsAsWarp(geometry: false, stride: 44, CullFullVs, null, culls: true, psSource: CullFullPs);

        /// <summary>SV_CullDistance with no pixel shader: a depth-only pass with the cull distances, then the
        /// plain draw where its depth is equal -- what the depth pass wrote, as WARP wrote it.</summary>
        [Fact]
        public void CullDistances_WithoutAPixelShader_CullAsWarp() => DrawsAsWarp(geometry: false, stride: 44, Vs, null, culls: true, depthOnlyVs: CullVs);

        /// <summary>The same, the cull distances from a geometry shader with no pixel shader.</summary>
        [Fact]
        public void CullDistances_FromAGeometryShader_WithoutAPixelShader_CullAsWarp() => DrawsAsWarp(geometry: false, stride: 44, Vs, null, culls: true, depthOnlyVs: CullVs, depthOnlyGs: CullGs);

        /// <summary>SV_CullDistance from the vertex shader: whole primitives culled as WARP culls them.</summary>
        [Fact]
        public void CullDistances_CullWholePrimitivesAsWarp() => DrawsAsWarp(geometry: false, stride: 44, CullVs, null, culls: true);

        /// <summary>SV_CullDistance from a geometry shader (the pass-through vertex stage carries it).</summary>
        [Fact]
        public void CullDistances_FromAGeometryShader_CullAsWarp() => DrawsAsWarp(geometry: true, stride: 44, CullVs, CullGs, culls: true);

        private void DrawsAsWarp(bool geometry, int stride, string? vsSource = null, string? gsSource = null, bool culls = false, string? psSource = null,
                                 string? depthOnlyVs = null, string? depthOnlyGs = null)
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "real Direct3D 11 (WARP) and the HLSL compiler exist on Windows only");
            Assert.SkipWhen(Requirements.GpuUnavailable is not null, Requirements.GpuUnavailable ?? "");
            byte[] vs = Compile(vsSource ?? Vs, "vs_5_0", out string? e1), gs = Compile(gsSource ?? Gs, "gs_5_0", out string? e2), ps = Compile(psSource ?? Ps, "ps_5_0", out string? e3);
            Assert.True(vs.Length > 0 && gs.Length > 0 && ps.Length > 0, e1 + e2 + e3);
            DepthOnly? depthOnly = null;
            if (depthOnlyVs != null)
            {
                depthOnly = new DepthOnly { Vs = Compile(depthOnlyVs, "vs_5_0", out string? e4), Gs = depthOnlyGs == null ? null : Compile(depthOnlyGs, "gs_5_0", out string? e5) };
                Assert.True(depthOnly.Vs.Length > 0, "depth-only vertex shader");
            }

            IntPtr wDev, wCtx, sDev, sCtx;
            uint level, want = 0xb000;
            Assert.True(RealCreateDevice(IntPtr.Zero, 5, IntPtr.Zero, 0, &want, 1, 7, &wDev, &level, &wCtx) >= 0, "WARP");
            Assert.True(Exports.CreateDevicePointer(IntPtr.Zero, 1, IntPtr.Zero, 0, null, 0, 7, &sDev, &level, &sCtx) >= 0, "shim");
            try
            {
                var warp = Draw(wDev, wCtx, vs, geometry ? gs : null, ps, stride, depthOnly);
                var shim = Draw(sDev, sCtx, vs, geometry ? gs : null, ps, stride, depthOnly);
                int covered = Covered(warp);
                Assert.True(covered > (culls ? 15 : 30), $"WARP covers {covered} pixels");
                if (culls)
                {
                    // the cull distances cull something, and not everything
                    byte[] plain = Compile(Vs, "vs_5_0", out _);
                    int all = Covered(Draw(wDev, wCtx, plain, null, ps, stride, depthOnly == null ? null : new DepthOnly { Vs = plain }));
                    Assert.True(covered < all * 3 / 4, $"WARP covers {covered} of {all} pixels with the cull distances");
                }
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

        private const string LayeredGs = @"
struct V { float4 pos : SV_Position; float4 a : TEXCOORD0; float4 b : TEXCOORD1; float4 c : TEXCOORD2; float4 d : TEXCOORD3; nointerpolation uint4 e : TEXCOORD4; };
struct L { float4 pos : SV_Position; float4 a : TEXCOORD0; uint layer : SV_RenderTargetArrayIndex; uint vp : SV_ViewportArrayIndex; };
[maxvertexcount(3)]
void main(triangle V v[3], uint prim : SV_PrimitiveID, inout TriangleStream<L> s)
{
    // a slice of three (and one past them: slice 0) and a viewport of two, by primitive
    [unroll] for (int k = 0; k < 3; k++) { L o; o.pos = v[k].pos; o.a = v[k].a; o.layer = prim % 4; o.vp = prim % 3; s.Append(o); }
}";

        private const string LayeredPs = "struct L { float4 pos : SV_Position; float4 a : TEXCOORD0; };\nfloat4 main(L l) : SV_Target { return l.a; }\n";

        /// <summary>SV_RenderTargetArrayIndex and SV_ViewportArrayIndex from a geometry shader into a
        /// three-slice array target and two viewports (a slice past them is slice 0, a viewport past them
        /// draws nothing): every slice as WARP
        /// draws it.</summary>
        [Fact]
        public void LayeredAndViewportArrays_DrawAsWarpDraws()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "real Direct3D 11 (WARP) and the HLSL compiler exist on Windows only");
            Assert.SkipWhen(Requirements.GpuUnavailable is not null, Requirements.GpuUnavailable ?? "");
            byte[] vs = Compile(Vs, "vs_5_0", out _), gs = Compile(LayeredGs, "gs_5_0", out string? gerr), ps = Compile(LayeredPs, "ps_5_0", out string? perr);
            Assert.True(gs.Length > 0 && ps.Length > 0, gerr + perr);
            IntPtr wDev, wCtx, sDev, sCtx;
            uint level, want = 0xb000;
            Assert.True(RealCreateDevice(IntPtr.Zero, 5, IntPtr.Zero, 0, &want, 1, 7, &wDev, &level, &wCtx) >= 0, "WARP");
            Assert.True(Exports.CreateDevicePointer(IntPtr.Zero, 1, IntPtr.Zero, 0, null, 0, 7, &sDev, &level, &sCtx) >= 0, "shim");
            try
            {
                var warp = DrawLayered(wDev, wCtx, vs, gs, ps);
                var shim = DrawLayered(sDev, sCtx, vs, gs, ps);
                for (int l = 0; l < 3; l++)
                {
                    var w = new List<float[]> { warp[l] };
                    var s = new List<float[]> { shim[l] };
                    var (count, first) = Compare(w, s);
                    Assert.True(Covered(w) > 0, $"slice {l}: WARP draws nothing");
                    Assert.True(count == 0 || OnlyCoverageDiffers(w, s, 3) > 0, $"slice {l}: {count} components differ (WARP covers {Covered(w)}, the shim {Covered(s)}); {first}");
                }
                Assert.Equal(0, Microsoft.Wpf.Interop.WebGpu.Direct3D.ComObject.From<D3DContext>(sCtx)!.DrawsSkipped);
            }
            finally { foreach (IntPtr p in new[] { sCtx, sDev, wCtx, wDev }) Release(p); }
        }

        private static List<float[]> DrawLayered(IntPtr dev, IntPtr ctx, byte[] vsBlob, byte[] gsBlob, byte[] psBlob)
        {
            var made = new List<IntPtr>();
            IntPtr Keep(IntPtr p) { made.Add(p); return p; }
            var handles = new List<GCHandle>();
            byte* Name(string s) { var h = GCHandle.Alloc(Encoding.ASCII.GetBytes(s + "\0"), GCHandleType.Pinned); handles.Add(h); return (byte*)h.AddrOfPinnedObject(); }
            try
            {
                IntPtr vs, gs, psh, layout, vb0, vb1, tex, rtv, rs;
                fixed (byte* v = vsBlob) Check(((delegate* unmanaged<IntPtr, byte*, nuint, IntPtr, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateVertexShader))(dev, v, (nuint)vsBlob.Length, IntPtr.Zero, &vs), "vs"); Keep(vs);
                fixed (byte* g = gsBlob) Check(((delegate* unmanaged<IntPtr, byte*, nuint, IntPtr, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateGeometryShader))(dev, g, (nuint)gsBlob.Length, IntPtr.Zero, &gs), "gs"); Keep(gs);
                fixed (byte* p = psBlob) Check(((delegate* unmanaged<IntPtr, byte*, nuint, IntPtr, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreatePixelShader))(dev, p, (nuint)psBlob.Length, IntPtr.Zero, &psh), "ps"); Keep(psh);
                var el = new D3D11_INPUT_ELEMENT_DESC[]
                {
                    new() { SemanticName = Name("POSITION"), Format = DXGI_FORMAT.R32G32B32_FLOAT },
                    new() { SemanticName = Name("COLOR"), SemanticIndex = 0, Format = DXGI_FORMAT.R8G8B8A8_UNORM, AlignedByteOffset = 12 },
                    new() { SemanticName = Name("COLOR"), SemanticIndex = 1, Format = DXGI_FORMAT.B8G8R8A8_UNORM, AlignedByteOffset = 16 },
                    new() { SemanticName = Name("TEXCOORD"), SemanticIndex = 0, Format = DXGI_FORMAT.R16G16_FLOAT, AlignedByteOffset = 20 },
                    new() { SemanticName = Name("TEXCOORD"), SemanticIndex = 1, Format = DXGI_FORMAT.R16G16_SNORM, AlignedByteOffset = 24 },
                    new() { SemanticName = Name("TEXCOORD"), SemanticIndex = 2, Format = DXGI_FORMAT.R10G10B10A2_UNORM, AlignedByteOffset = 28 },
                    new() { SemanticName = Name("TEXCOORD"), SemanticIndex = 3, Format = DXGI_FORMAT.R8G8B8A8_UINT, AlignedByteOffset = 32 },
                    new() { SemanticName = Name("TEXCOORD"), SemanticIndex = 4, Format = DXGI_FORMAT.R16G16_SINT, AlignedByteOffset = 36 },
                    new() { SemanticName = Name("TEXCOORD"), SemanticIndex = 5, Format = DXGI_FORMAT.R8G8_UNORM, AlignedByteOffset = 40 },
                    new() { SemanticName = Name("TEXCOORD"), SemanticIndex = 6, Format = DXGI_FORMAT.R32G32B32A32_FLOAT, InputSlot = 1, InputSlotClass = 1, InstanceDataStepRate = 1 },
                };
                const int Stride = 44, Vertices = 24;
                var data = new byte[Stride * Vertices];
                new Random(5).NextBytes(data);
                for (int v = 0; v < Vertices; v++)
                {
                    float cx = (v / 3 % 4) * 0.4f - 0.6f, cy = (v / 12) * 0.8f - 0.4f;
                    float[] off = { -0.5f, -0.5f, 0.0f, 0.6f, 0.5f, -0.4f };
                    BitConverter.TryWriteBytes(data.AsSpan(v * Stride), cx + off[v % 3 * 2]);
                    BitConverter.TryWriteBytes(data.AsSpan(v * Stride + 4), cy + off[v % 3 * 2 + 1]);
                    BitConverter.TryWriteBytes(data.AsSpan(v * Stride + 8), 0.5f);
                    BitConverter.TryWriteBytes(data.AsSpan(v * Stride + 20), (Half)0.5f);
                    BitConverter.TryWriteBytes(data.AsSpan(v * Stride + 22), (Half)0.25f);
                }
                fixed (D3D11_INPUT_ELEMENT_DESC* ep = el) fixed (byte* code = vsBlob)
                    Check(((delegate* unmanaged<IntPtr, D3D11_INPUT_ELEMENT_DESC*, uint, byte*, nuint, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateInputLayout))(dev, ep, (uint)el.Length, code, (nuint)vsBlob.Length, &layout), "il");
                Keep(layout);
                IntPtr Buf(byte[] bytes)
                {
                    IntPtr b;
                    fixed (byte* p = bytes)
                    {
                        var bd = new D3D11_BUFFER_DESC { ByteWidth = (uint)bytes.Length, Usage = D3D11_USAGE.DEFAULT, BindFlags = D3D11_BIND.VERTEX_BUFFER };
                        var init = new D3D11_SUBRESOURCE_DATA { pSysMem = p };
                        Check(((delegate* unmanaged<IntPtr, D3D11_BUFFER_DESC*, D3D11_SUBRESOURCE_DATA*, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateBuffer))(dev, &bd, &init, &b), "vb");
                    }
                    return Keep(b);
                }
                vb0 = Buf(data);
                vb1 = Buf(new byte[16]);
                var td = new D3D11_TEXTURE2D_DESC { Width = W, Height = H, MipLevels = 1, ArraySize = 3, Format = DXGI_FORMAT.R16G16B16A16_FLOAT, SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 }, Usage = D3D11_USAGE.DEFAULT, BindFlags = D3D11_BIND.RENDER_TARGET };
                Check(((delegate* unmanaged<IntPtr, D3D11_TEXTURE2D_DESC*, D3D11_SUBRESOURCE_DATA*, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateTexture2D))(dev, &td, null, &tex), "tex"); Keep(tex);
                Check(((delegate* unmanaged<IntPtr, IntPtr, void*, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateRenderTargetView))(dev, tex, null, &rtv), "rtv"); Keep(rtv);
                float* clear = stackalloc float[] { 0.25f, 0.5f, 0.75f, 1f };
                ((delegate* unmanaged<IntPtr, IntPtr, float*, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.ClearRenderTargetView))(ctx, rtv, clear);
                var rd = D3D11_RASTERIZER_DESC.Default(); rd.CullMode = 1;
                Check(((delegate* unmanaged<IntPtr, D3D11_RASTERIZER_DESC*, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateRasterizerState))(dev, &rd, &rs), "rs"); Keep(rs);
                ((delegate* unmanaged<IntPtr, IntPtr, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.RSSetState))(ctx, rs);
                IntPtr* bufs = stackalloc IntPtr[2] { vb0, vb1 };
                uint* strides = stackalloc uint[2] { Stride, 16 };
                uint* offsets = stackalloc uint[2] { 0, 0 };
                ((delegate* unmanaged<IntPtr, uint, uint, IntPtr*, uint*, uint*, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.IASetVertexBuffers))(ctx, 0, 2, bufs, strides, offsets);
                ((delegate* unmanaged<IntPtr, IntPtr, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.IASetInputLayout))(ctx, layout);
                ((delegate* unmanaged<IntPtr, uint, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.IASetPrimitiveTopology))(ctx, 4);
                ((delegate* unmanaged<IntPtr, IntPtr, IntPtr*, uint, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.VSSetShader))(ctx, vs, null, 0);
                ((delegate* unmanaged<IntPtr, IntPtr, IntPtr*, uint, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.GSSetShader))(ctx, gs, null, 0);
                ((delegate* unmanaged<IntPtr, IntPtr, IntPtr*, uint, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.PSSetShader))(ctx, psh, null, 0);
                ((delegate* unmanaged<IntPtr, uint, IntPtr*, IntPtr, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.OMSetRenderTargets))(ctx, 1, &rtv, IntPtr.Zero);
                var vp = stackalloc D3D11_VIEWPORT[2];
                vp[0] = new D3D11_VIEWPORT { Width = W, Height = H, MaxDepth = 1 };
                vp[1] = new D3D11_VIEWPORT { TopLeftX = 8, TopLeftY = 4, Width = 16, Height = 20, MaxDepth = 1 };
                ((delegate* unmanaged<IntPtr, uint, D3D11_VIEWPORT*, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.RSSetViewports))(ctx, 2, vp);
                ((delegate* unmanaged<IntPtr, uint, uint, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.Draw))(ctx, Vertices, 0);
                var slices = new List<float[]>();
                for (uint l = 0; l < 3; l++) slices.Add(ReadTarget(dev, ctx, tex, Keep, l));
                return slices;
            }
            finally
            {
                ((delegate* unmanaged<IntPtr, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.ClearState))(ctx);
                foreach (IntPtr p in made) Release(p);
                foreach (var h in handles) h.Free();
            }
        }

        /// <summary>Two instances of an indexed 16-bit triangle strip of five vertices (three
        /// triangles), from a base vertex, through the pass-through geometry shader.</summary>
        /// <summary>A depth-only pass (no pixel shader) before the draw, which then draws where the depth is equal.</summary>
        private sealed class DepthOnly { public byte[] Vs = Array.Empty<byte>(); public byte[]? Gs; }

        private static List<float[]> Draw(IntPtr dev, IntPtr ctx, byte[] vsBlob, byte[]? gsBlob, byte[] psBlob, int Stride, DepthOnly? depthOnly = null)
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
                if (depthOnly != null)
                {
                    // the depth pass: no pixel shader, depth written where the draw is not culled
                    var td = new D3D11_TEXTURE2D_DESC { Width = W, Height = H, MipLevels = 1, ArraySize = 1, Format = DXGI_FORMAT.D32_FLOAT, SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 }, Usage = D3D11_USAGE.DEFAULT, BindFlags = D3D11_BIND.DEPTH_STENCIL };
                    IntPtr dt, dsv, dvs, dgs = IntPtr.Zero, write, equal;
                    Check(((delegate* unmanaged<IntPtr, D3D11_TEXTURE2D_DESC*, D3D11_SUBRESOURCE_DATA*, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateTexture2D))(dev, &td, null, &dt), "CreateTexture2D depth");
                    Keep(dt);
                    Check(((delegate* unmanaged<IntPtr, IntPtr, void*, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateDepthStencilView))(dev, dt, null, &dsv), "CreateDepthStencilView");
                    Keep(dsv);
                    ((delegate* unmanaged<IntPtr, IntPtr, uint, float, byte, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.ClearDepthStencilView))(ctx, dsv, 1, 1f, 0);
                    var dd = D3D11_DEPTH_STENCIL_DESC.Default();
                    Check(((delegate* unmanaged<IntPtr, D3D11_DEPTH_STENCIL_DESC*, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateDepthStencilState))(dev, &dd, &write), "CreateDepthStencilState");
                    Keep(write);
                    dd.DepthWriteMask = 0; dd.DepthFunc = 3;        // EQUAL
                    Check(((delegate* unmanaged<IntPtr, D3D11_DEPTH_STENCIL_DESC*, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateDepthStencilState))(dev, &dd, &equal), "CreateDepthStencilState");
                    Keep(equal);
                    fixed (byte* v = depthOnly.Vs) Check(((delegate* unmanaged<IntPtr, byte*, nuint, IntPtr, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateVertexShader))(dev, v, (nuint)depthOnly.Vs.Length, IntPtr.Zero, &dvs), "CreateVertexShader depth");
                    Keep(dvs);
                    if (depthOnly.Gs != null) { fixed (byte* g = depthOnly.Gs) Check(((delegate* unmanaged<IntPtr, byte*, nuint, IntPtr, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateGeometryShader))(dev, g, (nuint)depthOnly.Gs.Length, IntPtr.Zero, &dgs), "CreateGeometryShader depth"); Keep(dgs); }
                    ((delegate* unmanaged<IntPtr, uint, IntPtr*, IntPtr, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.OMSetRenderTargets))(ctx, 0, null, dsv);
                    ((delegate* unmanaged<IntPtr, IntPtr, uint, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.OMSetDepthStencilState))(ctx, write, 0);
                    ((delegate* unmanaged<IntPtr, IntPtr, IntPtr*, uint, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.VSSetShader))(ctx, dvs, null, 0);
                    ((delegate* unmanaged<IntPtr, IntPtr, IntPtr*, uint, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.GSSetShader))(ctx, dgs, null, 0);
                    ((delegate* unmanaged<IntPtr, IntPtr, IntPtr*, uint, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.PSSetShader))(ctx, IntPtr.Zero, null, 0);
                    ((delegate* unmanaged<IntPtr, uint, uint, uint, int, uint, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.DrawIndexedInstanced))(ctx, 5, 2, 1, 2, 1);
                    // then the draw itself, where the depth pass wrote
                    fixed (IntPtr* r = rts) ((delegate* unmanaged<IntPtr, uint, IntPtr*, IntPtr, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.OMSetRenderTargets))(ctx, 6, r, dsv);
                    ((delegate* unmanaged<IntPtr, IntPtr, uint, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.OMSetDepthStencilState))(ctx, equal, 0);
                    ((delegate* unmanaged<IntPtr, IntPtr, IntPtr*, uint, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.VSSetShader))(ctx, vs, null, 0);
                    ((delegate* unmanaged<IntPtr, IntPtr, IntPtr*, uint, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.GSSetShader))(ctx, gs, null, 0);
                    ((delegate* unmanaged<IntPtr, IntPtr, IntPtr*, uint, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.PSSetShader))(ctx, psh, null, 0);
                }
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
