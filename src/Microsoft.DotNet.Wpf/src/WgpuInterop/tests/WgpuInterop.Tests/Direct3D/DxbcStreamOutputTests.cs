// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Stream output, DrawAuto and indirect draws against WARP (Documentation/d3d11-webgpu-shim.md,
// section 9): what lands in the SO buffers word for word (a geometry shader's strips cut into lists,
// a vertex shader's primitives, gaps and partial components, two slots, appending, overflow), and
// what DrawAuto and the indirect draws draw.
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
    public sealed unsafe class DxbcStreamOutputTests
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct SoEntry { public uint Stream; public byte* SemanticName; public uint SemanticIndex; public byte StartComponent, ComponentCount, OutputSlot; }

        [DllImport("d3d11.dll", EntryPoint = "D3D11CreateDevice")]
        private static extern int RealCreateDevice(IntPtr adapter, uint driverType, IntPtr software, uint flags, uint* levels, uint count, uint sdk, IntPtr* device, uint* level, IntPtr* context);

        private const string Vs = @"
struct O { float4 pos : SV_Position; float4 c : COLOR0; };
O main(float3 p : POSITION, uint id : SV_VertexID, uint iid : SV_InstanceID) { O o; o.pos = float4(p.xy + iid * 0.1, p.z, 1); o.c = float4(id, id * 2 + iid, p.x, 1); return o; }";

        private const string Gs = @"
struct O { float4 pos : SV_Position; float4 c : COLOR0; };
[maxvertexcount(7)]
void main(triangle O v[3], uint prim : SV_PrimitiveID, inout TriangleStream<O> s)
{
    // a strip of four (its second triangle odd), a cut, then the triangle the other way round
    [unroll] for (int k = 0; k < 3; k++) { O o = v[k]; o.c.w = prim; s.Append(o); }
    O x = v[0]; x.pos.xy = v[1].pos.xy + v[2].pos.xy - v[0].pos.xy; x.c.w = prim + 100; s.Append(x);
    s.RestartStrip();
    [unroll] for (int j = 0; j < 3; j++) { O o = v[2 - j]; o.c.w = prim + 200; s.Append(o); }
}";

        private const string GsLines = @"
struct O { float4 pos : SV_Position; float4 c : COLOR0; };
[maxvertexcount(4)]
void main(point O v[1], uint prim : SV_PrimitiveID, inout LineStream<O> s)
{
    [unroll] for (int k = 0; k < 4; k++) { O o = v[0]; o.pos.x += k * 0.1; o.c.y = k; o.c.w = prim; s.Append(o); }
}";

        private const string Ps = "struct O { float4 pos : SV_Position; float4 c : COLOR0; };\nfloat4 main(O o) : SV_Target { return o.c * 0.05; }\n";

        private static IntPtr Fresh(IntPtr dev, int bytes, D3D11_BIND bind, Func<IntPtr, IntPtr> keep, byte fill = 0xFF, uint misc = 0)
        {
            var data = Enumerable.Repeat(fill, bytes).ToArray();
            IntPtr b;
            fixed (byte* p = data)
            {
                var bd = new D3D11_BUFFER_DESC { ByteWidth = (uint)bytes, Usage = D3D11_USAGE.DEFAULT, BindFlags = bind, MiscFlags = (D3D11_RESOURCE_MISC)misc };
                var init = new D3D11_SUBRESOURCE_DATA { pSysMem = p };
                Check(((delegate* unmanaged<IntPtr, D3D11_BUFFER_DESC*, D3D11_SUBRESOURCE_DATA*, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateBuffer))(dev, &bd, &init, &b), "CreateBuffer");
            }
            return keep(b);
        }

        private static IntPtr Data<T>(IntPtr dev, T[] data, D3D11_BIND bind, Func<IntPtr, IntPtr> keep, uint misc = 0) where T : unmanaged
        {
            IntPtr b;
            fixed (T* p = data)
            {
                var bd = new D3D11_BUFFER_DESC { ByteWidth = (uint)((data.Length * sizeof(T) + 3) & ~3), Usage = D3D11_USAGE.DEFAULT, BindFlags = bind, MiscFlags = (D3D11_RESOURCE_MISC)misc };
                var padded = new byte[bd.ByteWidth];
                new ReadOnlySpan<byte>(p, data.Length * sizeof(T)).CopyTo(padded);
                fixed (byte* q = padded)
                {
                    var init = new D3D11_SUBRESOURCE_DATA { pSysMem = q };
                    Check(((delegate* unmanaged<IntPtr, D3D11_BUFFER_DESC*, D3D11_SUBRESOURCE_DATA*, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateBuffer))(dev, &bd, &init, &b), "CreateBuffer");
                }
            }
            return keep(b);
        }

        private static uint[] Words(IntPtr dev, IntPtr ctx, IntPtr buffer, int bytes, Func<IntPtr, IntPtr> keep)
        {
            IntPtr st;
            var bd = new D3D11_BUFFER_DESC { ByteWidth = (uint)bytes, Usage = D3D11_USAGE.STAGING, CPUAccessFlags = D3D11_CPU_ACCESS.READ };
            Check(((delegate* unmanaged<IntPtr, D3D11_BUFFER_DESC*, D3D11_SUBRESOURCE_DATA*, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateBuffer))(dev, &bd, null, &st), "CreateBuffer staging");
            keep(st);
            ((delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.CopyResource))(ctx, st, buffer);
            D3D11_MAPPED_SUBRESOURCE m;
            Check(((delegate* unmanaged<IntPtr, IntPtr, uint, D3D11_MAP, uint, D3D11_MAPPED_SUBRESOURCE*, int>)Fn(ctx, Vtbl.ID3D11DeviceContext.Map))(ctx, st, 0, D3D11_MAP.READ, 0, &m), "Map");
            var r = new uint[bytes / 4];
            new ReadOnlySpan<uint>(m.pData, r.Length).CopyTo(r);
            ((delegate* unmanaged<IntPtr, IntPtr, uint, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.Unmap))(ctx, st, 0);
            return r;
        }

        private sealed class Case
        {
            public string Name = "";
            public string? Gs;
            public uint Topology = 4;
            public uint Vertices = 6, Instances = 1;
            public bool Indexed;
            public int[] SoBytes = { 4096 };
            public bool Append;                  // a second draw appending to the first
            public (string? Semantic, int Start, int Count, int Slot)[] Decl = { ("SV_POSITION", 0, 4, 0), ("COLOR", 0, 4, 0) };
        }

        /// <summary>The case drawn on the device with SO targets: every SO buffer's words.</summary>
        private static List<uint[]> Run(IntPtr dev, IntPtr ctx, Case c, byte[] vs, byte[]? gsCode)
        {
            var made = new List<IntPtr>();
            IntPtr Keep(IntPtr p) { made.Add(p); return p; }
            var handles = new List<GCHandle>();
            byte* Name(string s) { var h = GCHandle.Alloc(Encoding.ASCII.GetBytes(s + "\0"), GCHandleType.Pinned); handles.Add(h); return (byte*)h.AddrOfPinnedObject(); }
            try
            {
                IntPtr v, gs, layout;
                fixed (byte* p = vs) Check(((delegate* unmanaged<IntPtr, byte*, nuint, IntPtr, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateVertexShader))(dev, p, (nuint)vs.Length, IntPtr.Zero, &v), "CreateVertexShader");
                Keep(v);
                var decl = c.Decl.Select(d => new SoEntry { SemanticName = d.Semantic == null ? null : Name(d.Semantic), StartComponent = (byte)d.Start, ComponentCount = (byte)d.Count, OutputSlot = (byte)d.Slot }).ToArray();
                byte[] code = gsCode ?? vs;
                fixed (byte* p = code)
                fixed (SoEntry* e = decl)
                    Check(((delegate* unmanaged<IntPtr, byte*, nuint, SoEntry*, uint, uint*, uint, uint, IntPtr, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateGeometryShaderWithStreamOutput))
                        (dev, p, (nuint)code.Length, e, (uint)decl.Length, null, 0, 0xFFFFFFFF, IntPtr.Zero, &gs), "CreateGeometryShaderWithStreamOutput");
                Keep(gs);
                var el = new D3D11_INPUT_ELEMENT_DESC { SemanticName = Name("POSITION"), Format = DXGI_FORMAT.R32G32B32_FLOAT };
                fixed (byte* p = vs) Check(((delegate* unmanaged<IntPtr, D3D11_INPUT_ELEMENT_DESC*, uint, byte*, nuint, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateInputLayout))(dev, &el, 1, p, (nuint)vs.Length, &layout), "CreateInputLayout");
                Keep(layout);
                var pos = new float[12 * 3];
                for (int i = 0; i < 12; i++) { pos[i * 3] = -0.9f + 0.15f * i; pos[i * 3 + 1] = (i % 3) * 0.3f - 0.4f; pos[i * 3 + 2] = 0.1f * (i % 4); }
                IntPtr vb = Data(dev, pos, D3D11_BIND.VERTEX_BUFFER, Keep);
                IntPtr ib = Data(dev, new ushort[] { 3, 1, 4, 0, 2, 5, 7, 6, 8 }, D3D11_BIND.INDEX_BUFFER, Keep);
                var sos = c.SoBytes.Select(n => Fresh(dev, n, D3D11_BIND.STREAM_OUTPUT, Keep)).ToArray();
                uint* offs = stackalloc uint[4] { 0, 0, 0, 0 };
                fixed (IntPtr* sp = sos) ((delegate* unmanaged<IntPtr, uint, IntPtr*, uint*, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.SOSetTargets))(ctx, (uint)sos.Length, sp, offs);
                uint stride = 12, off = 0;
                ((delegate* unmanaged<IntPtr, uint, uint, IntPtr*, uint*, uint*, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.IASetVertexBuffers))(ctx, 0, 1, &vb, &stride, &off);
                ((delegate* unmanaged<IntPtr, IntPtr, DXGI_FORMAT, uint, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.IASetIndexBuffer))(ctx, ib, DXGI_FORMAT.R16_UINT, 0);
                ((delegate* unmanaged<IntPtr, IntPtr, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.IASetInputLayout))(ctx, layout);
                ((delegate* unmanaged<IntPtr, uint, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.IASetPrimitiveTopology))(ctx, c.Topology);
                ((delegate* unmanaged<IntPtr, IntPtr, IntPtr*, uint, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.VSSetShader))(ctx, v, null, 0);
                ((delegate* unmanaged<IntPtr, IntPtr, IntPtr*, uint, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.GSSetShader))(ctx, gs, null, 0);
                for (int pass = 0; pass < (c.Append ? 2 : 1); pass++)
                {
                    if (pass == 1)
                    {
                        uint* append = stackalloc uint[4] { 0xFFFFFFFF, 0xFFFFFFFF, 0xFFFFFFFF, 0xFFFFFFFF };
                        fixed (IntPtr* sp = sos) ((delegate* unmanaged<IntPtr, uint, IntPtr*, uint*, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.SOSetTargets))(ctx, (uint)sos.Length, sp, append);
                    }
                    if (c.Indexed) ((delegate* unmanaged<IntPtr, uint, uint, uint, int, uint, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.DrawIndexedInstanced))(ctx, c.Vertices, c.Instances, 0, 1, 0);
                    else ((delegate* unmanaged<IntPtr, uint, uint, uint, uint, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.DrawInstanced))(ctx, c.Vertices, c.Instances, 0, 0);
                }
                IntPtr none = IntPtr.Zero;
                ((delegate* unmanaged<IntPtr, uint, IntPtr*, uint*, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.SOSetTargets))(ctx, 1, &none, offs);
                return sos.Select((b, i) => Words(dev, ctx, b, c.SoBytes[i], Keep)).ToList();
            }
            finally
            {
                ((delegate* unmanaged<IntPtr, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.ClearState))(ctx);
                foreach (IntPtr p in made) Release(p);
                foreach (var h in handles) h.Free();
            }
        }

        private static Case[] Cases => new[]
        {
            new Case { Name = "gs strips cut into lists", Gs = Gs },
            new Case { Name = "gs from a strip, indexed, instanced", Gs = Gs, Topology = 5, Vertices = 7, Indexed = true, Instances = 2 },
            new Case { Name = "gs line strips", Gs = GsLines, Topology = 1, Vertices = 5 },
            new Case { Name = "gs two slots, a gap, partial components", Gs = Gs, SoBytes = new[] { 4096, 2048 },
                       Decl = new (string?, int, int, int)[] { ("SV_POSITION", 1, 2, 0), (null, 0, 1, 0), ("COLOR", 3, 1, 0), ("COLOR", 0, 2, 1) } },
            new Case { Name = "gs overflow: whole primitives only", Gs = Gs, SoBytes = new[] { 32 * 11 } },
            new Case { Name = "gs appending", Gs = Gs, Append = true },
            new Case { Name = "vs triangle list", Topology = 4 },
            new Case { Name = "vs triangle strip, instanced", Topology = 5, Vertices = 7, Instances = 2 },
            new Case { Name = "vs line strip, indexed", Topology = 3, Vertices = 6, Indexed = true },
            new Case { Name = "vs points, appending, overflow", Topology = 1, Vertices = 9, Append = true, SoBytes = new[] { 32 * 13 } },
        };

        private static bool Same(uint a, uint b)
        {
            if (a == b) return true;
            float x = BitConverter.UInt32BitsToSingle(a), y = BitConverter.UInt32BitsToSingle(b);
            return float.IsFinite(x) && float.IsFinite(y) && Math.Sign(x) == Math.Sign(y) && Math.Abs((long)a - b) <= 4;
        }

        [Fact]
        public void StreamOutput_WritesWhatWarpWrites()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "real Direct3D 11 (WARP) and the HLSL compiler exist on Windows only");
            Assert.SkipWhen(Requirements.GpuUnavailable is not null, Requirements.GpuUnavailable ?? "");
            IntPtr wDev, wCtx, sDev, sCtx;
            uint level, want = 0xb000;
            Assert.True(RealCreateDevice(IntPtr.Zero, 5, IntPtr.Zero, 0, &want, 1, 7, &wDev, &level, &wCtx) >= 0, "WARP");
            Assert.True(Exports.CreateDevicePointer(IntPtr.Zero, 1, IntPtr.Zero, 0, null, 0, 7, &sDev, &level, &sCtx) >= 0, "shim");
            byte[] vs = Compile(Vs, "vs_5_0", out string? verr);
            Assert.True(vs.Length > 0, verr);
            var failures = new List<string>();
            try
            {
                foreach (var c in Cases)
                {
                    byte[]? gs = c.Gs == null ? null : Compile(c.Gs, "gs_5_0", out string? gerr);
                    if (gs != null && gs.Length == 0) { failures.Add($"{c.Name}: compile"); continue; }
                    var warp = Run(wDev, wCtx, c, vs, gs);
                    var shim = Run(sDev, sCtx, c, vs, gs);
                    for (int b = 0; b < warp.Count; b++)
                    {
                        // a float may differ by an ulp or two: this GL driver (over Metal) reassociates float adds
                        int at = Enumerable.Range(0, warp[b].Length).FirstOrDefault(i => !Same(warp[b][i], shim[b][i]), -1);
                        int written = warp[b].Count(w => w != 0xFFFFFFFF);
                        if (written == 0) failures.Add($"{c.Name}: WARP wrote nothing to slot {b}");
                        if (at >= 0) failures.Add($"{c.Name}: slot {b} word {at}: shim 0x{shim[b][at]:x8} ({BitConverter.UInt32BitsToSingle(shim[b][at])}), WARP 0x{warp[b][at]:x8} ({BitConverter.UInt32BitsToSingle(warp[b][at])}); WARP wrote {written} words, the shim {shim[b].Count(w => w != 0xFFFFFFFF)}");
                    }
                }
            }
            finally { foreach (IntPtr p in new[] { sCtx, sDev, wCtx, wDev }) Release(p); }
            Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
        }

        /// <summary>DrawAuto from a buffer stream output filled, and both indirect draws (their
        /// vertex and instance ids too): the pixels WARP draws.</summary>
        [Fact]
        public void DrawAutoAndIndirectDraws_DrawAsWarpDraws()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "real Direct3D 11 (WARP) and the HLSL compiler exist on Windows only");
            Assert.SkipWhen(Requirements.GpuUnavailable is not null, Requirements.GpuUnavailable ?? "");
            IntPtr wDev, wCtx, sDev, sCtx;
            uint level, want = 0xb000;
            Assert.True(RealCreateDevice(IntPtr.Zero, 5, IntPtr.Zero, 0, &want, 1, 7, &wDev, &level, &wCtx) >= 0, "WARP");
            Assert.True(Exports.CreateDevicePointer(IntPtr.Zero, 1, IntPtr.Zero, 0, null, 0, 7, &sDev, &level, &sCtx) >= 0, "shim");
            byte[] vs = Compile(Vs, "vs_5_0", out _), ps = Compile(Ps, "ps_5_0", out _), gs = Compile(Gs, "gs_5_0", out _);
            byte[] vsAuto = Compile("struct O { float4 pos : SV_Position; float4 c : COLOR0; };\nO main(float4 p : POSITION, float4 c : COLOR) { O o; o.pos = p; o.c = c; return o; }", "vs_5_0", out string? aerr);
            Assert.True(vsAuto.Length > 0, aerr);
            var failures = new List<string>();
            try
            {
                foreach (string mode in new[] { "auto", "direct", "indirect", "indexed-indirect", "indirect-gs" })
                {
                    var warp = DrawMode(wDev, wCtx, mode, vs, ps, gs, vsAuto);
                    var shim = DrawMode(sDev, sCtx, mode, vs, ps, gs, vsAuto);
                    var (count, first) = Compare(warp, shim);
                    if (Covered(warp) == 0) failures.Add($"{mode}: WARP draws nothing");
                    // a pixel on an edge shared by two primitives can change owner when a position is an ulp
                    // off (see Same): at most four such pixels
                    else if (count > 0 && OnlyCoverageDiffers(warp, shim, 3) == 0 && DifferingPixels(warp, shim) > 4) failures.Add($"{mode}: {count} components differ in {DifferingPixels(warp, shim)} pixels (WARP covers {Covered(warp)}, the shim {Covered(shim)}); {first}");
                }
                var context = Microsoft.Wpf.Interop.WebGpu.Direct3D.ComObject.From<D3DContext>(sCtx);
                if (context!.DrawsSkipped > 0) failures.Add($"the shim skipped {context.DrawsSkipped} draw(s)");
            }
            finally { foreach (IntPtr p in new[] { sCtx, sDev, wCtx, wDev }) Release(p); }
            Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
        }

        private static List<float[]> DrawMode(IntPtr dev, IntPtr ctx, string mode, byte[] vs, byte[] ps, byte[] gsCode, byte[] vsAuto)
        {
            var made = new List<IntPtr>();
            IntPtr Keep(IntPtr p) { made.Add(p); return p; }
            var handles = new List<GCHandle>();
            byte* Name(string s) { var h = GCHandle.Alloc(Encoding.ASCII.GetBytes(s + "\0"), GCHandleType.Pinned); handles.Add(h); return (byte*)h.AddrOfPinnedObject(); }
            try
            {
                IntPtr v, p, layout, tex, rtv, rs;
                fixed (byte* q = vs) Check(((delegate* unmanaged<IntPtr, byte*, nuint, IntPtr, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateVertexShader))(dev, q, (nuint)vs.Length, IntPtr.Zero, &v), "CreateVertexShader");
                Keep(v);
                fixed (byte* q = ps) Check(((delegate* unmanaged<IntPtr, byte*, nuint, IntPtr, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreatePixelShader))(dev, q, (nuint)ps.Length, IntPtr.Zero, &p), "CreatePixelShader");
                Keep(p);
                var el = new D3D11_INPUT_ELEMENT_DESC { SemanticName = Name("POSITION"), Format = DXGI_FORMAT.R32G32B32_FLOAT };
                fixed (byte* q = vs) Check(((delegate* unmanaged<IntPtr, D3D11_INPUT_ELEMENT_DESC*, uint, byte*, nuint, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateInputLayout))(dev, &el, 1, q, (nuint)vs.Length, &layout), "CreateInputLayout");
                Keep(layout);
                var pos = new float[] { -0.9f, -0.9f, 0.2f, -0.7f, 0.6f, 0.3f, 0.1f, -0.5f, 0.4f, 0.5f, 0.7f, 0.5f, 0.8f, -0.8f, 0.6f, 0.2f, 0.1f, 0.1f };
                IntPtr vb = Data(dev, pos, D3D11_BIND.VERTEX_BUFFER, Keep);
                IntPtr ib = Data(dev, new ushort[] { 0, 0, 1, 2, 3, 4, 5, 1 }, D3D11_BIND.INDEX_BUFFER, Keep);
                var td = new D3D11_TEXTURE2D_DESC { Width = W, Height = H, MipLevels = 1, ArraySize = 1, Format = DXGI_FORMAT.R16G16B16A16_FLOAT, SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 }, Usage = D3D11_USAGE.DEFAULT, BindFlags = D3D11_BIND.RENDER_TARGET };
                Check(((delegate* unmanaged<IntPtr, D3D11_TEXTURE2D_DESC*, D3D11_SUBRESOURCE_DATA*, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateTexture2D))(dev, &td, null, &tex), "CreateTexture2D");
                Keep(tex);
                Check(((delegate* unmanaged<IntPtr, IntPtr, void*, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateRenderTargetView))(dev, tex, null, &rtv), "CreateRenderTargetView");
                Keep(rtv);
                float* clear = stackalloc float[] { 0.25f, 0.5f, 0.75f, 1f };
                ((delegate* unmanaged<IntPtr, IntPtr, float*, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.ClearRenderTargetView))(ctx, rtv, clear);
                var rd = D3D11_RASTERIZER_DESC.Default();
                rd.CullMode = 1;
                Check(((delegate* unmanaged<IntPtr, D3D11_RASTERIZER_DESC*, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateRasterizerState))(dev, &rd, &rs), "CreateRasterizerState");
                Keep(rs);
                uint stride = 12, off = 0;
                ((delegate* unmanaged<IntPtr, uint, uint, IntPtr*, uint*, uint*, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.IASetVertexBuffers))(ctx, 0, 1, &vb, &stride, &off);
                ((delegate* unmanaged<IntPtr, IntPtr, DXGI_FORMAT, uint, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.IASetIndexBuffer))(ctx, ib, DXGI_FORMAT.R16_UINT, 0);
                ((delegate* unmanaged<IntPtr, IntPtr, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.IASetInputLayout))(ctx, layout);
                ((delegate* unmanaged<IntPtr, uint, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.IASetPrimitiveTopology))(ctx, 4);
                ((delegate* unmanaged<IntPtr, IntPtr, IntPtr*, uint, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.VSSetShader))(ctx, v, null, 0);
                var vp = new D3D11_VIEWPORT { Width = W, Height = H, MaxDepth = 1 };

                if (mode == "auto")
                {
                    // fill a buffer through stream output (no rasterizer), then draw it with DrawAuto
                    IntPtr gs, so, layout2, v2;
                    var decl = new[] { new SoEntry { SemanticName = Name("SV_POSITION"), ComponentCount = 4 }, new SoEntry { SemanticName = Name("COLOR"), ComponentCount = 4 } };
                    fixed (byte* q = gsCode)
                    fixed (SoEntry* e = decl)
                        Check(((delegate* unmanaged<IntPtr, byte*, nuint, SoEntry*, uint, uint*, uint, uint, IntPtr, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateGeometryShaderWithStreamOutput))
                            (dev, q, (nuint)gsCode.Length, e, 2, null, 0, 0xFFFFFFFF, IntPtr.Zero, &gs), "CreateGeometryShaderWithStreamOutput");
                    Keep(gs);
                    so = Fresh(dev, 4096, D3D11_BIND.STREAM_OUTPUT | D3D11_BIND.VERTEX_BUFFER, Keep, 0);
                    uint zero = 0;
                    ((delegate* unmanaged<IntPtr, uint, IntPtr*, uint*, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.SOSetTargets))(ctx, 1, &so, &zero);
                    ((delegate* unmanaged<IntPtr, IntPtr, IntPtr*, uint, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.GSSetShader))(ctx, gs, null, 0);
                    ((delegate* unmanaged<IntPtr, uint, uint, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.Draw))(ctx, 6, 0);
                    IntPtr none = IntPtr.Zero;
                    ((delegate* unmanaged<IntPtr, uint, IntPtr*, uint*, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.SOSetTargets))(ctx, 1, &none, &zero);
                    ((delegate* unmanaged<IntPtr, IntPtr, IntPtr*, uint, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.GSSetShader))(ctx, IntPtr.Zero, null, 0);
                    fixed (byte* q = vsAuto) Check(((delegate* unmanaged<IntPtr, byte*, nuint, IntPtr, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateVertexShader))(dev, q, (nuint)vsAuto.Length, IntPtr.Zero, &v2), "CreateVertexShader auto");
                    Keep(v2);
                    var els = new[]
                    {
                        new D3D11_INPUT_ELEMENT_DESC { SemanticName = Name("POSITION"), Format = DXGI_FORMAT.R32G32B32A32_FLOAT },
                        new D3D11_INPUT_ELEMENT_DESC { SemanticName = Name("COLOR"), Format = DXGI_FORMAT.R32G32B32A32_FLOAT, AlignedByteOffset = 16 },
                    };
                    fixed (D3D11_INPUT_ELEMENT_DESC* ep = els)
                    fixed (byte* q = vsAuto) Check(((delegate* unmanaged<IntPtr, D3D11_INPUT_ELEMENT_DESC*, uint, byte*, nuint, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateInputLayout))(dev, ep, 2, q, (nuint)vsAuto.Length, &layout2), "CreateInputLayout auto");
                    Keep(layout2);
                    uint s32 = 32;
                    ((delegate* unmanaged<IntPtr, uint, uint, IntPtr*, uint*, uint*, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.IASetVertexBuffers))(ctx, 0, 1, &so, &s32, &zero);
                    ((delegate* unmanaged<IntPtr, IntPtr, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.IASetInputLayout))(ctx, layout2);
                    ((delegate* unmanaged<IntPtr, IntPtr, IntPtr*, uint, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.VSSetShader))(ctx, v2, null, 0);
                }

                ((delegate* unmanaged<IntPtr, IntPtr, IntPtr*, uint, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.PSSetShader))(ctx, p, null, 0);
                ((delegate* unmanaged<IntPtr, IntPtr, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.RSSetState))(ctx, rs);
                ((delegate* unmanaged<IntPtr, uint, IntPtr*, IntPtr, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.OMSetRenderTargets))(ctx, 1, &rtv, IntPtr.Zero);
                ((delegate* unmanaged<IntPtr, uint, D3D11_VIEWPORT*, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.RSSetViewports))(ctx, 1, &vp);
                const uint DrawIndirectArgs = 0x10;
                switch (mode)
                {
                    case "auto":
                        ((delegate* unmanaged<IntPtr, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.DrawAuto))(ctx);
                        break;
                    case "direct":
                        ((delegate* unmanaged<IntPtr, uint, uint, uint, uint, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.DrawInstanced))(ctx, 3, 2, 3, 1);
                        break;
                    case "indirect":
                    {
                        IntPtr args = Data(dev, new uint[] { 3, 2, 3, 1 }, 0, Keep, DrawIndirectArgs);
                        ((delegate* unmanaged<IntPtr, IntPtr, uint, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.DrawInstancedIndirect))(ctx, args, 0);
                        break;
                    }
                    case "indexed-indirect":
                    {
                        IntPtr args = Data(dev, new uint[] { 99, 6, 2, 1, 0, 1, 0 }, 0, Keep, DrawIndirectArgs);
                        ((delegate* unmanaged<IntPtr, IntPtr, uint, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.DrawIndexedInstancedIndirect))(ctx, args, 4);
                        break;
                    }
                    default:
                    {
                        IntPtr g;
                        fixed (byte* q = gsCode) Check(((delegate* unmanaged<IntPtr, byte*, nuint, IntPtr, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateGeometryShader))(dev, q, (nuint)gsCode.Length, IntPtr.Zero, &g), "CreateGeometryShader");
                        Keep(g);
                        ((delegate* unmanaged<IntPtr, IntPtr, IntPtr*, uint, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.GSSetShader))(ctx, g, null, 0);
                        IntPtr args = Data(dev, new uint[] { 6, 2, 0, 0 }, 0, Keep, DrawIndirectArgs);
                        ((delegate* unmanaged<IntPtr, IntPtr, uint, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.DrawInstancedIndirect))(ctx, args, 0);
                        break;
                    }
                }
                return new List<float[]> { ReadTarget(dev, ctx, tex, Keep) };
            }
            finally
            {
                ((delegate* unmanaged<IntPtr, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.ClearState))(ctx);
                foreach (IntPtr q in made) Release(q);
                foreach (var h in handles) h.Free();
            }
        }
    }
}
