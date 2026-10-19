// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Tessellation against WARP (Documentation/d3d11-webgpu-shim.md, section 9).
//
// The tessellator: WARP's own, observed through stream output -- a pass-through domain shader whose
// only output is SV_DomainLocation, streamed out of a geometry shader made from the domain shader's
// bytecode -- gives each patch's primitives in order, vertex by vertex. D3DTessellator must make the
// same list, bit for bit, for every domain, partitioning, output primitive and a spread of factors
// (fractional, clamped, culled).
//
// The shim's tessellated draws: a patch through hull and domain shaders drawn on both, the domain
// shader's colour a non-linear function of the domain location (so a different diagonal shows).
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
    public sealed unsafe class DxbcTessellationTests
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct SoEntry { public uint Stream; public byte* SemanticName; public uint SemanticIndex; public byte StartComponent, ComponentCount, OutputSlot; }

        [DllImport("d3d11.dll", EntryPoint = "D3D11CreateDevice")]
        private static extern int RealCreateDevice(IntPtr adapter, uint driverType, IntPtr software, uint flags, uint* levels, uint count, uint sdk, IntPtr* device, uint* level, IntPtr* context);

        private static readonly string[] s_partitionings = { "integer", "pow2", "fractional_odd", "fractional_even" };

        private static (string Domain, int Points, int Factors, string Struct)[] Domains => new[]
        {
            ("tri", 3, 4, "float e[3] : SV_TessFactor; float i : SV_InsideTessFactor;"),
            ("quad", 4, 6, "float e[4] : SV_TessFactor; float i[2] : SV_InsideTessFactor;"),
            ("isoline", 2, 2, "float e[2] : SV_TessFactor;"),
        };

        private static readonly float[][] s_factors =
        {
            new[] { 1f, 1f, 1f, 1f, 1f, 1f },
            new[] { 2f, 3f, 4f, 5f, 3f, 2f },
            new[] { 2.5f, 3.7f, 1.2f, 6.3f, 4.4f, 1.6f },
            new[] { 7.9f, 2.1f, 5.5f, 3.3f, 9.25f, 2.75f },
            new[] { 64f, 1f, 33.3f, 12.7f, 17.5f, 63.9f },
            new[] { 100f, 0.5f, 1.01f, 2.99f, 1.0001f, 31.5f },
            new[] { 1.5f, 1.5f, 1.5f, 1.5f, 1f, 1f },
            new[] { 6f, 6f, 6f, 1f, 1f, 6f },
            new[] { 0f, 4f, 4f, 4f, 4f, 4f },
        };

        private static string Hull(string domain, int points, int factors, string kStruct, string partitioning, string topology)
        {
            var sb = new StringBuilder();
            sb.Append("cbuffer F : register(b0) { float4 f0; float4 f1; };\nstruct C { float3 p : POSITION; };\n");
            sb.Append($"struct K {{ {kStruct} }};\n");
            sb.Append($"K pc(InputPatch<C, {points}> ip) {{ K k;\n");
            float[] dummy = new float[6];
            string F(int i) => i < 4 ? $"f0[{i}]" : $"f1[{i - 4}]";
            int edges = domain == "tri" ? 3 : domain == "quad" ? 4 : 2;
            for (int i = 0; i < edges; i++) sb.Append($"    k.e[{i}] = {F(i)};\n");
            if (domain == "tri") sb.Append($"    k.i = {F(3)};\n");
            if (domain == "quad") sb.Append($"    k.i[0] = {F(4)}; k.i[1] = {F(5)};\n");
            sb.Append("    return k; }\n");
            sb.Append($"[domain(\"{domain}\")] [partitioning(\"{partitioning}\")] [outputtopology(\"{topology}\")] [outputcontrolpoints({points})] [patchconstantfunc(\"pc\")]\n");
            sb.Append($"C main(InputPatch<C, {points}> ip, uint i : SV_OutputControlPointID) {{ return ip[i]; }}\n");
            return sb.ToString();
        }

        private static string Domain(string domain, int points, string kStruct, bool position)
        {
            string loc = domain == "tri" ? "float3" : "float2";
            string pad = domain == "tri" ? "b, 0" : "b, 0, 0";
            var sb = new StringBuilder("struct C { float3 p : POSITION; };\n");
            sb.Append($"struct K {{ {kStruct} }};\n");
            if (!position)
            {
                sb.Append($"[domain(\"{domain}\")] float4 main(K k, {loc} b : SV_DomainLocation, const OutputPatch<C, {points}> c) : TEXCOORD0 {{ return float4({pad}); }}\n");
                return sb.ToString();
            }
            // a position from the control points, and a colour no plane through three vertices reproduces
            sb.Append("struct O { float4 pos : SV_Position; float4 c : COLOR0; };\n");
            sb.Append($"[domain(\"{domain}\")] O main(K k, {loc} b : SV_DomainLocation, const OutputPatch<C, {points}> c) {{\n    O o;\n");
            if (domain == "tri") sb.Append("    float3 p = c[0].p * b.x + c[1].p * b.y + c[2].p * b.z;\n    float2 uv = b.xy;\n");
            else if (domain == "quad") sb.Append("    float3 p = lerp(lerp(c[0].p, c[1].p, b.x), lerp(c[3].p, c[2].p, b.x), b.y);\n    float2 uv = b;\n");
            else sb.Append("    float3 p = lerp(c[0].p, c[1].p, b.x) + float3(0, b.y * 0.8 - 0.4, 0);\n    float2 uv = b;\n");
            sb.Append("    o.pos = float4(p, 1);\n    o.c = float4(uv.x * uv.y * 4, uv.x * uv.x, sqrt(saturate(uv.y)), 1);\n    return o;\n}\n");
            return sb.ToString();
        }

        private const string Vs = "float3 main(float3 p : POSITION) : POSITION { return p; }\n";

        /// <summary>WARP's tessellation of one patch, as the stream of SV_DomainLocation its
        /// primitives' vertices carry.</summary>
        private static List<(float U, float V)> WarpPattern(IntPtr dev, IntPtr ctx, byte[] vs, byte[] hs, byte[] ds, int points, float[] factors)
        {
            var made = new List<IntPtr>();
            IntPtr Keep(IntPtr p) { made.Add(p); return p; }
            var name = GCHandle.Alloc(Encoding.ASCII.GetBytes("TEXCOORD\0"), GCHandleType.Pinned);
            var pos = GCHandle.Alloc(Encoding.ASCII.GetBytes("POSITION\0"), GCHandleType.Pinned);
            try
            {
                IntPtr v, h, d, gs, layout, vb, cb, so, st;
                fixed (byte* p = vs) Check(((delegate* unmanaged<IntPtr, byte*, nuint, IntPtr, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateVertexShader))(dev, p, (nuint)vs.Length, IntPtr.Zero, &v), "CreateVertexShader");
                Keep(v);
                fixed (byte* p = hs) Check(((delegate* unmanaged<IntPtr, byte*, nuint, IntPtr, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateHullShader))(dev, p, (nuint)hs.Length, IntPtr.Zero, &h), "CreateHullShader");
                Keep(h);
                fixed (byte* p = ds) Check(((delegate* unmanaged<IntPtr, byte*, nuint, IntPtr, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateDomainShader))(dev, p, (nuint)ds.Length, IntPtr.Zero, &d), "CreateDomainShader");
                Keep(d);
                var entry = new SoEntry { SemanticName = (byte*)name.AddrOfPinnedObject(), ComponentCount = 4 };
                uint stride = 16;
                fixed (byte* p = ds)
                    Check(((delegate* unmanaged<IntPtr, byte*, nuint, SoEntry*, uint, uint*, uint, uint, IntPtr, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateGeometryShaderWithStreamOutput))
                        (dev, p, (nuint)ds.Length, &entry, 1, &stride, 1, 0xFFFFFFFF, IntPtr.Zero, &gs), "CreateGeometryShaderWithStreamOutput");
                Keep(gs);
                var el = new D3D11_INPUT_ELEMENT_DESC { SemanticName = (byte*)pos.AddrOfPinnedObject(), Format = DXGI_FORMAT.R32G32B32_FLOAT };
                fixed (byte* code = vs) Check(((delegate* unmanaged<IntPtr, D3D11_INPUT_ELEMENT_DESC*, uint, byte*, nuint, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateInputLayout))(dev, &el, 1, code, (nuint)vs.Length, &layout), "CreateInputLayout");
                Keep(layout);
                var verts = new float[points * 3];
                fixed (float* p = verts) vb = Buffer(dev, p, verts.Length * 4, D3D11_BIND.VERTEX_BUFFER, Keep);
                var f = new float[8];
                Array.Copy(factors, f, Math.Min(6, factors.Length));
                fixed (float* p = f) cb = Buffer(dev, p, 32, D3D11_BIND.CONSTANT_BUFFER, Keep);
                const int SoBytes = 64 * 64 * 6 * 16 + 4096;
                var sentinel = Enumerable.Repeat((byte)0xFF, SoBytes).ToArray();
                fixed (byte* p = sentinel) so = Buffer(dev, p, SoBytes, D3D11_BIND.STREAM_OUTPUT, Keep);
                uint zero = 0;
                ((delegate* unmanaged<IntPtr, uint, IntPtr*, uint*, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.SOSetTargets))(ctx, 1, &so, &zero);
                uint vstride = 12, voff = 0;
                ((delegate* unmanaged<IntPtr, uint, uint, IntPtr*, uint*, uint*, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.IASetVertexBuffers))(ctx, 0, 1, &vb, &vstride, &voff);
                ((delegate* unmanaged<IntPtr, IntPtr, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.IASetInputLayout))(ctx, layout);
                ((delegate* unmanaged<IntPtr, uint, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.IASetPrimitiveTopology))(ctx, (uint)(32 + points));
                ((delegate* unmanaged<IntPtr, IntPtr, IntPtr*, uint, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.VSSetShader))(ctx, v, null, 0);
                ((delegate* unmanaged<IntPtr, IntPtr, IntPtr*, uint, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.HSSetShader))(ctx, h, null, 0);
                ((delegate* unmanaged<IntPtr, uint, uint, IntPtr*, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.HSSetConstantBuffers))(ctx, 0, 1, &cb);
                ((delegate* unmanaged<IntPtr, IntPtr, IntPtr*, uint, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.DSSetShader))(ctx, d, null, 0);
                ((delegate* unmanaged<IntPtr, IntPtr, IntPtr*, uint, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.GSSetShader))(ctx, gs, null, 0);
                ((delegate* unmanaged<IntPtr, uint, uint, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.Draw))(ctx, (uint)points, 0);

                var bd = new D3D11_BUFFER_DESC { ByteWidth = SoBytes, Usage = D3D11_USAGE.STAGING, CPUAccessFlags = D3D11_CPU_ACCESS.READ };
                Check(((delegate* unmanaged<IntPtr, D3D11_BUFFER_DESC*, D3D11_SUBRESOURCE_DATA*, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateBuffer))(dev, &bd, null, &st), "CreateBuffer staging");
                Keep(st);
                ((delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.CopyResource))(ctx, st, so);
                D3D11_MAPPED_SUBRESOURCE m;
                Check(((delegate* unmanaged<IntPtr, IntPtr, uint, D3D11_MAP, uint, D3D11_MAPPED_SUBRESOURCE*, int>)Fn(ctx, Vtbl.ID3D11DeviceContext.Map))(ctx, st, 0, D3D11_MAP.READ, 0, &m), "Map");
                var list = new List<(float, float)>();
                uint* w = (uint*)m.pData;
                for (int i = 0; i + 4 <= SoBytes / 4 && w[i + 3] != 0xFFFFFFFF; i += 4) list.Add((BitConverter.UInt32BitsToSingle(w[i]), BitConverter.UInt32BitsToSingle(w[i + 1])));
                ((delegate* unmanaged<IntPtr, IntPtr, uint, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.Unmap))(ctx, st, 0);
                return list;
            }
            finally
            {
                ((delegate* unmanaged<IntPtr, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.ClearState))(ctx);
                foreach (IntPtr p in made) Release(p);
                name.Free();
                pos.Free();
            }
        }

        private static IntPtr Buffer(IntPtr dev, void* data, int bytes, D3D11_BIND bind, Func<IntPtr, IntPtr> keep)
        {
            IntPtr b;
            var bd = new D3D11_BUFFER_DESC { ByteWidth = (uint)bytes, Usage = D3D11_USAGE.DEFAULT, BindFlags = bind };
            var init = new D3D11_SUBRESOURCE_DATA { pSysMem = data };
            Check(((delegate* unmanaged<IntPtr, D3D11_BUFFER_DESC*, D3D11_SUBRESOURCE_DATA*, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateBuffer))(dev, &bd, &init, &b), "CreateBuffer");
            return keep(b);
        }

        private const string Ps = "struct O { float4 pos : SV_Position; float4 c : COLOR0; };\nfloat4 main(O o, bool face : SV_IsFrontFace) : SV_Target { return float4(o.c.xyz, face ? 1 : 0.5); }\n";

        /// <summary>Two patches through the shaders on the device, both windings drawn: the target's texels.</summary>
        private static List<float[]> DrawPatches(IntPtr dev, IntPtr ctx, byte[] vs, byte[] hs, byte[] ds, byte[] ps, int points, float[] factors)
        {
            var made = new List<IntPtr>();
            IntPtr Keep(IntPtr p) { made.Add(p); return p; }
            var pos = GCHandle.Alloc(Encoding.ASCII.GetBytes("POSITION\0"), GCHandleType.Pinned);
            try
            {
                IntPtr v, h, d, psh, layout, vb, cb, tex, rtv, rs;
                fixed (byte* p = vs) Check(((delegate* unmanaged<IntPtr, byte*, nuint, IntPtr, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateVertexShader))(dev, p, (nuint)vs.Length, IntPtr.Zero, &v), "CreateVertexShader");
                Keep(v);
                fixed (byte* p = hs) Check(((delegate* unmanaged<IntPtr, byte*, nuint, IntPtr, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateHullShader))(dev, p, (nuint)hs.Length, IntPtr.Zero, &h), "CreateHullShader");
                Keep(h);
                fixed (byte* p = ds) Check(((delegate* unmanaged<IntPtr, byte*, nuint, IntPtr, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateDomainShader))(dev, p, (nuint)ds.Length, IntPtr.Zero, &d), "CreateDomainShader");
                Keep(d);
                fixed (byte* p = ps) Check(((delegate* unmanaged<IntPtr, byte*, nuint, IntPtr, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreatePixelShader))(dev, p, (nuint)ps.Length, IntPtr.Zero, &psh), "CreatePixelShader");
                Keep(psh);
                var el = new D3D11_INPUT_ELEMENT_DESC { SemanticName = (byte*)pos.AddrOfPinnedObject(), Format = DXGI_FORMAT.R32G32B32_FLOAT };
                fixed (byte* code = vs) Check(((delegate* unmanaged<IntPtr, D3D11_INPUT_ELEMENT_DESC*, uint, byte*, nuint, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateInputLayout))(dev, &el, 1, code, (nuint)vs.Length, &layout), "CreateInputLayout");
                Keep(layout);
                // two patches side by side: a triangle, a quad (corners in order around it) or a line
                var verts = new List<float>();
                for (int patch = 0; patch < 2; patch++)
                {
                    float x0 = patch == 0 ? -0.95f : 0.05f, x1 = x0 + 0.9f, y0 = -0.9f + 0.1f * patch, y1 = 0.85f;
                    float[][] corners = points switch
                    {
                        3 => new[] { new[] { x0, y0 }, new[] { x0 + 0.2f, y1 }, new[] { x1, y0 + 0.3f } },
                        4 => new[] { new[] { x0, y1 }, new[] { x1, y1 - 0.2f }, new[] { x1 - 0.1f, y0 }, new[] { x0 + 0.15f, y0 } },
                        _ => new[] { new[] { x0, 0.1f }, new[] { x1, -0.1f } },
                    };
                    foreach (var c in corners) verts.AddRange(new[] { c[0], c[1], 0.5f });
                }
                var va = verts.ToArray();
                fixed (float* p = va) vb = Buffer(dev, p, va.Length * 4, D3D11_BIND.VERTEX_BUFFER, Keep);
                var f = new float[8];
                Array.Copy(factors, f, Math.Min(6, factors.Length));
                fixed (float* p = f) cb = Buffer(dev, p, 32, D3D11_BIND.CONSTANT_BUFFER, Keep);
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
                ((delegate* unmanaged<IntPtr, IntPtr, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.RSSetState))(ctx, rs);
                uint vstride = 12, voff = 0;
                ((delegate* unmanaged<IntPtr, uint, uint, IntPtr*, uint*, uint*, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.IASetVertexBuffers))(ctx, 0, 1, &vb, &vstride, &voff);
                ((delegate* unmanaged<IntPtr, IntPtr, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.IASetInputLayout))(ctx, layout);
                ((delegate* unmanaged<IntPtr, uint, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.IASetPrimitiveTopology))(ctx, (uint)(32 + points));
                ((delegate* unmanaged<IntPtr, IntPtr, IntPtr*, uint, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.VSSetShader))(ctx, v, null, 0);
                ((delegate* unmanaged<IntPtr, IntPtr, IntPtr*, uint, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.HSSetShader))(ctx, h, null, 0);
                ((delegate* unmanaged<IntPtr, uint, uint, IntPtr*, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.HSSetConstantBuffers))(ctx, 0, 1, &cb);
                ((delegate* unmanaged<IntPtr, IntPtr, IntPtr*, uint, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.DSSetShader))(ctx, d, null, 0);
                ((delegate* unmanaged<IntPtr, IntPtr, IntPtr*, uint, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.PSSetShader))(ctx, psh, null, 0);
                ((delegate* unmanaged<IntPtr, uint, IntPtr*, IntPtr, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.OMSetRenderTargets))(ctx, 1, &rtv, IntPtr.Zero);
                var vp = new D3D11_VIEWPORT { Width = W, Height = H, MaxDepth = 1 };
                ((delegate* unmanaged<IntPtr, uint, D3D11_VIEWPORT*, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.RSSetViewports))(ctx, 1, &vp);
                ((delegate* unmanaged<IntPtr, uint, uint, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.Draw))(ctx, (uint)(2 * points), 0);
                return new List<float[]> { ReadTarget(dev, ctx, tex, Keep) };
            }
            finally
            {
                ((delegate* unmanaged<IntPtr, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.ClearState))(ctx);
                foreach (IntPtr p in made) Release(p);
                pos.Free();
            }
        }

        /// <summary>Tessellated draws on the shim -- vertex shader, both hull kernels, the tessellator,
        /// the domain shader as the draw's vertex shader -- against WARP's, for every domain and
        /// partitioning and a few factors (triangles both ways round, isolines as lines).</summary>
        [Fact]
        public void TessellatedDraws_DrawAsWarpDraws()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "real Direct3D 11 (WARP) and the HLSL compiler exist on Windows only");
            Assert.SkipWhen(Requirements.GpuUnavailable is not null, Requirements.GpuUnavailable ?? "");
            IntPtr wDev, wCtx, sDev, sCtx;
            uint level, want = 0xb000;
            Assert.True(RealCreateDevice(IntPtr.Zero, 5, IntPtr.Zero, 0, &want, 1, 7, &wDev, &level, &wCtx) >= 0, "WARP");
            Assert.True(Exports.CreateDevicePointer(IntPtr.Zero, 1, IntPtr.Zero, 0, null, 0, 7, &sDev, &level, &sCtx) >= 0, "shim");
            byte[] vs = Compile(Vs, "vs_5_0", out _), ps = Compile(Ps, "ps_5_0", out string? perr);
            Assert.True(ps.Length > 0, perr);
            var failures = new List<string>();
            int same = 0;
            try
            {
                foreach (var (domain, points, nf, kStruct) in Domains)
                    foreach (string part in s_partitionings)
                    {
                        // isolines as points: WARP rasterizes the lines of an isoline patch only in part (the points,
                        // and the stream-out test above, show the same positions and line list as ours)
                        string topology = domain == "isoline" ? "point" : part == "integer" ? "triangle_ccw" : "triangle_cw";
                        byte[] hs = Compile(Hull(domain, points, nf, kStruct, part, topology), "hs_5_0", out string? herr);
                        byte[] ds = Compile(Domain(domain, points, kStruct, position: true), "ds_5_0", out string? derr);
                        if (hs.Length == 0 || ds.Length == 0) { failures.Add($"{domain}/{part}: compile: {herr}{derr}"); continue; }
                        foreach (float[] f in new[] { s_factors[1], s_factors[2], s_factors[3] })
                        {
                            var warp = DrawPatches(wDev, wCtx, vs, hs, ds, ps, points, f);
                            var shim = DrawPatches(sDev, sCtx, vs, hs, ds, ps, points, f);
                            var (count, first) = Compare(warp, shim);
                            string what = $"{domain}/{part} [{string.Join(", ", f.Take(nf))}]";
                            if (Covered(warp) == 0) failures.Add($"{what}: WARP draws nothing");
                            else if (count == 0 || OnlyCoverageDiffers(warp, shim, 4) > 0) same++;
                            else failures.Add($"{what}: {count} components differ (WARP covers {Covered(warp)}, the shim {Covered(shim)}); {first}");
                        }
                    }
                var context = Microsoft.Wpf.Interop.WebGpu.Direct3D.ComObject.From<D3DContext>(sCtx);
                if (context!.DrawsSkipped > 0) failures.Add($"the shim skipped {context.DrawsSkipped} draw(s)");
            }
            finally
            {
                foreach (IntPtr p in new[] { sCtx, sDev, wCtx, wDev }) Release(p);
            }
            Assert.True(failures.Count == 0, $"{same} draw as WARP draws; " + string.Join(Environment.NewLine, failures.Take(25)));
        }

        /// <summary>Every domain, partitioning and output primitive, over a spread of factors: the
        /// port's points and primitives are WARP's, in WARP's order, bit for bit.</summary>
        [Fact]
        public void TheTessellator_TessellatesAsWarpTessellates()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "real Direct3D 11 (WARP) and the HLSL compiler exist on Windows only");
            IntPtr dev, ctx;
            uint level, want = 0xb000;
            Assert.True(RealCreateDevice(IntPtr.Zero, 5, IntPtr.Zero, 0, &want, 1, 7, &dev, &level, &ctx) >= 0, "WARP");
            var failures = new List<string>();
            int cases = 0;
            byte[] vs = Compile(Vs, "vs_5_0", out string? verr);
            Assert.True(vs.Length > 0, verr);
            var tess = new D3DTessellator();
            try
            {
                foreach (var (domain, points, nf, kStruct) in Domains)
                    foreach (string part in s_partitionings)
                        foreach (string topology in domain == "isoline" ? new[] { "line", "point" } : new[] { "triangle_cw", "triangle_ccw", "point" })
                        {
                            byte[] hs = Compile(Hull(domain, points, nf, kStruct, part, topology), "hs_5_0", out string? herr);
                            byte[] ds = Compile(Domain(domain, points, kStruct, position: false), "ds_5_0", out string? derr);
                            if (hs.Length == 0 || ds.Length == 0) { failures.Add($"{domain}/{part}/{topology}: compile: {herr}{derr}"); continue; }
                            foreach (float[] f in s_factors)
                            {
                                cases++;
                                var warp = WarpPattern(dev, ctx, vs, hs, ds, points, f);
                                tess.Init(part switch { "integer" => TessPartitioning.Integer, "pow2" => TessPartitioning.Pow2, "fractional_odd" => TessPartitioning.FractionalOdd, _ => TessPartitioning.FractionalEven },
                                          topology switch { "point" => TessOutput.Point, "line" => TessOutput.Line, "triangle_cw" => TessOutput.TriangleCw, _ => TessOutput.TriangleCcw });
                                if (domain == "tri") tess.TessellateTriDomain(f[0], f[1], f[2], f[3]);
                                else if (domain == "quad") tess.TessellateQuadDomain(f[0], f[1], f[2], f[3], f[4], f[5]);
                                else tess.TessellateIsoLineDomain(f[0], f[1]);       // SV_TessFactor[0] is the line density, [1] the detail
                                var ours = Enumerable.Range(0, tess.IndexCount).Select(i => (tess.PointsU[tess.Indices[i]], tess.PointsV[tess.Indices[i]])).ToList();
                                string what = $"{domain}/{part}/{topology} [{string.Join(", ", f.Take(nf))}]";
                                if (ours.Count != warp.Count) { failures.Add($"{what}: {ours.Count} vertices, WARP {warp.Count}"); continue; }
                                int at = Enumerable.Range(0, ours.Count).FirstOrDefault(i => BitConverter.SingleToUInt32Bits(ours[i].Item1) != BitConverter.SingleToUInt32Bits(warp[i].U) || BitConverter.SingleToUInt32Bits(ours[i].Item2) != BitConverter.SingleToUInt32Bits(warp[i].V), -1);
                                if (at >= 0) failures.Add($"{what}: vertex {at} ({ours[at].Item1}, {ours[at].Item2}), WARP ({warp[at].U}, {warp[at].V})");
                            }
                        }
            }
            finally { Release(ctx); Release(dev); }
            Assert.True(failures.Count == 0, $"{cases - failures.Count} of {cases} as WARP; " + string.Join(Environment.NewLine, failures.Take(30)));
        }
    }
}
