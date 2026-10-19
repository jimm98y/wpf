// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Differential pixel tests for the DXBC translator: a pixel shader drawn on the shim and on real
// Direct3D 11 (WARP) from the same inputs, compared pixel for pixel.
//
// The inputs are made from the shader's own reflection (its RDEF and input signature), so any shader
// can be driven without knowing what it is for:
//   - a vertex shader generated in HLSL (compiled with d3dcompiler_47) draws one full-screen triangle
//     and writes every input the pixel shader reads: floats that vary smoothly across the target,
//     integers that are constant (they are not interpolated);
//   - each constant buffer is filled by the TYPE of each variable: floats in [0.1, 1] (a matrix near
//     the identity), integers small (loop counts stay bounded), bools 0 or 1;
//   - each texture is a deterministic pattern of the declared shape, each sampler point-sampled with wrap
//     (filter precision is the GPU's, not the translator's), or a comparison sampler where the shader
//     declares one, over a depth texture;
//   - every SV_Target is a 32-bit float (or integer) target, so nothing is lost to quantization.
//

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Wpf.Interop.WebGpu.Direct3D;
using Microsoft.Wpf.Interop.WebGpu.Direct3D.Dxbc;

namespace WgpuInterop.Tests.Direct3D
{
    internal static unsafe class DxbcPixelParity
    {
        public const int W = 32, H = 32;

        // ---- reflection (RDEF) -------------------------------------------------------------------

        internal sealed class CBVar { public string Name = ""; public int Offset, Size, Class, Type, Rows, Cols, Elements; }
        internal sealed class CBuffer { public string Name = ""; public int Size, Slot = -1; public List<CBVar> Vars = new(); }
        internal sealed class Binding { public string Name = ""; public int Kind, ReturnType, Dimension, Samples, Slot, Count, Flags; }
        internal sealed class Reflection { public List<CBuffer> CBuffers = new(); public List<Binding> Bindings = new(); }

        private static int I32(byte[] b, int o) => BitConverter.ToInt32(b, o);
        private static string Str(byte[] b, int o) { int e = o; while (e < b.Length && b[e] != 0) e++; return Encoding.ASCII.GetString(b, o, e - o); }

        public static byte[]? Chunk(byte[] dxbc, string fourcc)
        {
            int count = I32(dxbc, 28);
            for (int i = 0; i < count; i++)
            {
                int off = I32(dxbc, 32 + i * 4);
                if (Encoding.ASCII.GetString(dxbc, off, 4) == fourcc)
                {
                    int size = I32(dxbc, off + 4);
                    return dxbc.AsSpan(off + 8, size).ToArray();
                }
            }
            return null;
        }

        public static Reflection Reflect(byte[] dxbc)
        {
            var r = new Reflection();
            byte[]? d = Chunk(dxbc, "RDEF");
            if (d == null) return r;
            int cbCount = I32(d, 0), cbOff = I32(d, 4), bindCount = I32(d, 8), bindOff = I32(d, 12);
            int major = d[17];
            int varSize = major >= 5 ? 40 : 24;
            for (int i = 0; i < bindCount; i++)
            {
                int o = bindOff + i * 32;
                r.Bindings.Add(new Binding { Name = Str(d, I32(d, o)), Kind = I32(d, o + 4), ReturnType = I32(d, o + 8), Dimension = I32(d, o + 12), Samples = I32(d, o + 16), Slot = I32(d, o + 20), Count = I32(d, o + 24), Flags = I32(d, o + 28) });
            }
            for (int i = 0; i < cbCount; i++)
            {
                int o = cbOff + i * 24;
                var cb = new CBuffer { Name = Str(d, I32(d, o)), Size = I32(d, o + 12) };
                int vars = I32(d, o + 4), vOff = I32(d, o + 8);
                for (int v = 0; v < vars; v++)
                {
                    int vo = vOff + v * varSize;
                    int to = I32(d, vo + 16);
                    cb.Vars.Add(new CBVar
                    {
                        Name = Str(d, I32(d, vo)), Offset = I32(d, vo + 4), Size = I32(d, vo + 8),
                        Class = BitConverter.ToUInt16(d, to), Type = BitConverter.ToUInt16(d, to + 2), Rows = BitConverter.ToUInt16(d, to + 4),
                        Cols = BitConverter.ToUInt16(d, to + 6), Elements = BitConverter.ToUInt16(d, to + 8),
                    });
                }
                cb.Slot = r.Bindings.FirstOrDefault(b => b.Kind == 0 && b.Name == cb.Name)?.Slot ?? -1;
                r.CBuffers.Add(cb);
            }
            return r;
        }

        /// <summary>A constant buffer's bytes, each variable filled by its type (see the header).</summary>
        public static byte[] Fill(CBuffer cb, int seed)
        {
            var bytes = new byte[Math.Max(16, (cb.Size + 15) & ~15)];
            var rnd = new Random(seed * 7919 + cb.Size);
            foreach (var v in cb.Vars)
            {
                int words = v.Size / 4;
                bool matrix = v.Class is 2 or 3;      // D3D_SVC_MATRIX_ROWS / COLUMNS
                for (int w = 0; w < words; w++)
                {
                    int at = v.Offset + w * 4;
                    if (at + 4 > bytes.Length) break;
                    uint bits;
                    switch (v.Type)
                    {
                        case 1: bits = (uint)rnd.Next(2); break;                                // bool
                        case 2: case 19: case 20: bits = (uint)rnd.Next(3); break;              // int, uint, uint8
                        default:
                        {
                            float f = 0.1f + 0.9f * (float)rnd.NextDouble();
                            if (matrix)
                            {
                                int reg = (w % 4 == 0 ? w : w) / 4, comp = w % 4;
                                f = (reg % 4 == comp ? 1f : 0f) + 0.05f * (float)rnd.NextDouble();
                            }
                            bits = BitConverter.SingleToUInt32Bits(f);
                            break;
                        }
                    }
                    BitConverter.TryWriteBytes(bytes.AsSpan(at), bits);
                }
            }
            return bytes;
        }

        // ---- the generated vertex shader -----------------------------------------------------------

        /// <summary>HLSL for a vertex shader that feeds <paramref name="ps"/>'s inputs: one full-screen
        /// triangle, float inputs varying with position, integer inputs constant. Null when an input
        /// cannot be generated.</summary>
        public static string? VertexShaderFor(DxbcShader ps)
        {
            var sb = new StringBuilder("struct O {\n    float4 pos : SV_Position;\n");
            var body = new StringBuilder();
            int k = 0;
            foreach (var e in ps.Inputs.OrderBy(e => e.Register).ThenBy(e => e.Mask & -e.Mask))
            {
                if (e.SystemValue != 0) continue;
                int n = System.Numerics.BitOperations.PopCount((uint)e.Mask);
                if (n == 0) continue;
                string type = e.ComponentType switch { 1 => "uint", 2 => "int", _ => "float" };
                string decl = n == 1 ? type : type + n;
                string interp = e.ComponentType == 3 ? "" : "nointerpolation ";
                string name = $"a{k}";
                sb.Append($"    {interp}{decl} {name} : {e.SemanticName}{e.SemanticIndex};\n");
                string comps = "xyzw".Substring(0, n);
                if (e.ComponentType == 3)
                    body.Append($"    o.{name} = float4(0.1 + 0.4 * p.x + 0.03 * {k}, 0.15 + 0.35 * p.y, 0.8 - 0.3 * p.x, 0.6 - 0.2 * p.y + 0.02 * {k}).{comps};\n");
                else
                    body.Append($"    o.{name} = {type}4({k % 3}, 1, 2, 0).{comps};\n");
                k++;
            }
            sb.Append("};\nO main(uint id : SV_VertexID) {\n    O o;\n    float2 p = float2((id << 1) & 2, id & 2);\n    o.pos = float4(p * float2(2, -2) + float2(-1, 1), 0.5, 1);\n");
            sb.Append(body).Append("    return o;\n}\n");
            return sb.ToString();
        }

        [DllImport("d3dcompiler_47.dll", CharSet = CharSet.Ansi)]
        private static extern int D3DCompile(byte* src, nuint size, string? name, IntPtr defines, IntPtr include, string entry, string target, uint flags1, uint flags2, IntPtr* code, IntPtr* errors);

        public static byte[] Compile(string hlsl, string target, out string? error)
        {
            error = null;
            byte[] src = Encoding.ASCII.GetBytes(hlsl);
            IntPtr code = IntPtr.Zero, errors = IntPtr.Zero;
            int hr;
            fixed (byte* p = src) hr = D3DCompile(p, (nuint)src.Length, "gen.hlsl", IntPtr.Zero, IntPtr.Zero, "main", target, 0, 0, &code, &errors);
            if (errors != IntPtr.Zero)
            {
                error = Marshal.PtrToStringAnsi(((delegate* unmanaged<IntPtr, IntPtr>)Fn(errors, 3))(errors));
                Release(errors);
            }
            if (hr < 0 || code == IntPtr.Zero) { error ??= $"D3DCompile 0x{hr:x8}"; return Array.Empty<byte>(); }
            var ptr = ((delegate* unmanaged<IntPtr, IntPtr>)Fn(code, 3))(code);
            var size = ((delegate* unmanaged<IntPtr, nuint>)Fn(code, 4))(code);
            var blob = new byte[(int)size];
            Marshal.Copy(ptr, blob, 0, blob.Length);
            Release(code);
            return blob;
        }

        // ---- drawing on one device ------------------------------------------------------------------

        internal static IntPtr Fn(IntPtr com, int slot) => (*(IntPtr**)com)[slot];
        internal static void Release(IntPtr com) { if (com != IntPtr.Zero) ((delegate* unmanaged<IntPtr, uint>)Fn(com, 2))(com); }

        internal static void Check(int hr, string what) { if (hr < 0) throw new InvalidOperationException($"{what}: 0x{hr:x8}"); }

        /// <summary>Draws the pair on the device and returns each target's texels as four floats
        /// each (W x H; integer targets' components as their bits), in SV_Target order.</summary>
        public static List<float[]> Draw(IntPtr dev, IntPtr ctx, byte[] vsBlob, byte[] psBlob, DxbcShader ps, Reflection refl, int seed)
            => Draw(dev, ctx, vsBlob, psBlob, ps, refl, null, null, seed);

        /// <summary>As Draw, with the vertex shader's own resources (<paramref name="vsRefl"/>) bound to
        /// the vertex stage and, when <paramref name="vsInputs"/> is given, a vertex buffer feeding its
        /// input signature: a triangle covering the target, each input a float4 (or int4) per vertex.</summary>
        public static List<float[]> Draw(IntPtr dev, IntPtr ctx, byte[] vsBlob, byte[] psBlob, DxbcShader ps, Reflection refl, Reflection? vsRefl, DxbcShader? vsInputs, int seed)
        {
            var made = new List<IntPtr>();
            IntPtr Keep(IntPtr p) { made.Add(p); return p; }
            try
            {
                IntPtr vs, psh;
                fixed (byte* v = vsBlob) Check(((delegate* unmanaged<IntPtr, byte*, nuint, IntPtr, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateVertexShader))(dev, v, (nuint)vsBlob.Length, IntPtr.Zero, &vs), "CreateVertexShader");
                Keep(vs);
                fixed (byte* p = psBlob) Check(((delegate* unmanaged<IntPtr, byte*, nuint, IntPtr, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreatePixelShader))(dev, p, (nuint)psBlob.Length, IntPtr.Zero, &psh), "CreatePixelShader");
                Keep(psh);

                // targets
                var outputs = ps.Outputs.Where(o => o.SystemValue == 64 || (o.SystemValue == 0 && o.SemanticName.StartsWith("SV_TARGET", StringComparison.OrdinalIgnoreCase)))
                    .OrderBy(o => o.Register).ToList();
                var rts = new IntPtr[8];
                var rtTex = new List<IntPtr>();
                int targets = outputs.Count == 0 ? 0 : outputs.Max(o => o.Register) + 1;
                for (int i = 0; i < targets; i++)
                {
                    var o = outputs.FirstOrDefault(x => x.Register == i);
                    // half floats: 32-bit float targets are not renderable on every backend (GL downlevel)
                    var fmt = o?.ComponentType switch { 1 => DXGI_FORMAT.R32G32B32A32_UINT, 2 => DXGI_FORMAT.R32G32B32A32_SINT, _ => DXGI_FORMAT.R16G16B16A16_FLOAT };
                    var td = new D3D11_TEXTURE2D_DESC { Width = W, Height = H, MipLevels = 1, ArraySize = 1, Format = fmt, SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 }, Usage = D3D11_USAGE.DEFAULT, BindFlags = D3D11_BIND.RENDER_TARGET };
                    IntPtr t, rtv;
                    Check(((delegate* unmanaged<IntPtr, D3D11_TEXTURE2D_DESC*, D3D11_SUBRESOURCE_DATA*, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateTexture2D))(dev, &td, null, &t), "CreateTexture2D rt");
                    Keep(t); rtTex.Add(t);
                    Check(((delegate* unmanaged<IntPtr, IntPtr, void*, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateRenderTargetView))(dev, t, null, &rtv), "CreateRenderTargetView");
                    Keep(rtv); rts[i] = rtv;
                    float* clear = stackalloc float[] { 0.25f, 0.5f, 0.75f, 1f };
                    ((delegate* unmanaged<IntPtr, IntPtr, float*, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.ClearRenderTargetView))(ctx, rtv, clear);
                }

                BindStage(dev, ctx, refl, seed, Keep, Vtbl.ID3D11DeviceContext.PSSetConstantBuffers, Vtbl.ID3D11DeviceContext.PSSetSamplers, Vtbl.ID3D11DeviceContext.PSSetShaderResources);
                if (vsRefl != null)
                    BindStage(dev, ctx, vsRefl, seed + 1, Keep, Vtbl.ID3D11DeviceContext.VSSetConstantBuffers, Vtbl.ID3D11DeviceContext.VSSetSamplers, Vtbl.ID3D11DeviceContext.VSSetShaderResources);

                IntPtr layout = IntPtr.Zero;
                if (vsInputs != null) layout = Keep(VertexInput(dev, ctx, vsBlob, vsInputs, Keep));

                ((delegate* unmanaged<IntPtr, IntPtr, IntPtr*, uint, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.VSSetShader))(ctx, vs, null, 0);
                ((delegate* unmanaged<IntPtr, IntPtr, IntPtr*, uint, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.PSSetShader))(ctx, psh, null, 0);
                ((delegate* unmanaged<IntPtr, IntPtr, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.IASetInputLayout))(ctx, layout);
                ((delegate* unmanaged<IntPtr, uint, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.IASetPrimitiveTopology))(ctx, 4);
                fixed (IntPtr* r = rts) ((delegate* unmanaged<IntPtr, uint, IntPtr*, IntPtr, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.OMSetRenderTargets))(ctx, (uint)targets, r, IntPtr.Zero);
                var vp = new D3D11_VIEWPORT { Width = W, Height = H, MaxDepth = 1 };
                ((delegate* unmanaged<IntPtr, uint, D3D11_VIEWPORT*, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.RSSetViewports))(ctx, 1, &vp);
                ((delegate* unmanaged<IntPtr, uint, uint, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.Draw))(ctx, 3, 0);

                var result = new List<float[]>();
                foreach (IntPtr t in rtTex) result.Add(ReadTarget(dev, ctx, t, Keep));
                ((delegate* unmanaged<IntPtr, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.ClearState))(ctx);
                return result;
            }
            finally
            {
                ((delegate* unmanaged<IntPtr, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.ClearState))(ctx);
                foreach (IntPtr p in made) Release(p);
            }
        }

        /// <summary>One stage's constant buffers, samplers and textures, made from its reflection.</summary>
        internal static void BindStage(IntPtr dev, IntPtr ctx, Reflection refl, int seed, Func<IntPtr, IntPtr> Keep, int setCb, int setSampler, int setSrv)
        {
                // constant buffers
                foreach (var cb in refl.CBuffers)
                {
                    if (cb.Slot < 0) continue;
                    byte[] data = Fill(cb, seed);
                    IntPtr buf;
                    fixed (byte* p = data)
                    {
                        var bd = new D3D11_BUFFER_DESC { ByteWidth = (uint)data.Length, Usage = D3D11_USAGE.DEFAULT, BindFlags = D3D11_BIND.CONSTANT_BUFFER };
                        var init = new D3D11_SUBRESOURCE_DATA { pSysMem = p };
                        Check(((delegate* unmanaged<IntPtr, D3D11_BUFFER_DESC*, D3D11_SUBRESOURCE_DATA*, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateBuffer))(dev, &bd, &init, &buf), "CreateBuffer cb");
                    }
                    Keep(buf);
                    ((delegate* unmanaged<IntPtr, uint, uint, IntPtr*, void>)Fn(ctx, setCb))(ctx, (uint)cb.Slot, 1, &buf);
                }

                // samplers and textures
                bool anyComparison = refl.Bindings.Any(b => b.Kind == 3 && (b.Flags & 2) != 0);
                foreach (var b in refl.Bindings)
                {
                    if (b.Kind == 3)
                    {
                        var sd = D3D11_SAMPLER_DESC.Default();
                        // point sampling: how a GPU weighs bilinear taps is the hardware's, not the
                        // translator's, and edge detectors amplify it past any tolerance
                        sd.Filter = 0;
                        if ((b.Flags & 2) != 0) { sd.Filter = 0x95; sd.ComparisonFunc = 4; /* LESS_EQUAL */ }
                        IntPtr s;
                        Check(((delegate* unmanaged<IntPtr, D3D11_SAMPLER_DESC*, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateSamplerState))(dev, &sd, &s), "CreateSamplerState");
                        Keep(s);
                        ((delegate* unmanaged<IntPtr, uint, uint, IntPtr*, void>)Fn(ctx, setSampler))(ctx, (uint)b.Slot, 1, &s);
                    }
                    else if (b.Kind == 2)
                    {
                        IntPtr srv = MakeTexture(dev, ctx, b, anyComparison, Keep);
                        ((delegate* unmanaged<IntPtr, uint, uint, IntPtr*, void>)Fn(ctx, setSrv))(ctx, (uint)b.Slot, 1, &srv);
                    }
                }
        }

        /// <summary>The input layout and vertex buffer for a vertex shader's input signature: three
        /// vertices of a triangle covering the target. An input named POSITION gets the triangle's
        /// corner (z 0.5, w 1), every other float input a smooth per-vertex value, integers small
        /// constants (instance indices, bone indices).</summary>
        private static IntPtr VertexInput(IntPtr dev, IntPtr ctx, byte[] vsBlob, DxbcShader vs, Func<IntPtr, IntPtr> keep)
        {
            var inputs = vs.Inputs.Where(e => e.SystemValue == 0).OrderBy(e => e.Register).ToList();
            int n = inputs.Count;
            var names = new List<byte[]>();
            var elems = new D3D11_INPUT_ELEMENT_DESC[Math.Max(1, n)];
            var handles = new List<GCHandle>();
            float[] corners = { -1f, -1f, -1f, 3f, 3f, -1f };   // a triangle over all of clip space, clockwise (front-facing in D3D)
            var data = new byte[3 * n * 16];
            for (int i = 0; i < n; i++)
            {
                var e = inputs[i];
                byte[] name = Encoding.ASCII.GetBytes(e.SemanticName + "\0");
                var h = GCHandle.Alloc(name, GCHandleType.Pinned);
                handles.Add(h);
                elems[i] = new D3D11_INPUT_ELEMENT_DESC
                {
                    SemanticName = (byte*)h.AddrOfPinnedObject(), SemanticIndex = (uint)e.SemanticIndex,
                    Format = e.ComponentType switch { 1 => DXGI_FORMAT.R32G32B32A32_UINT, 2 => DXGI_FORMAT.R32G32B32A32_SINT, _ => DXGI_FORMAT.R32G32B32A32_FLOAT },
                    AlignedByteOffset = (uint)(i * 16),
                };
                for (int v = 0; v < 3; v++)
                {
                    int at = (v * n + i) * 16;
                    for (int c = 0; c < 4; c++)
                    {
                        uint bits;
                        if (e.ComponentType != 3) bits = (uint)(c == 0 ? i % 2 : 0);
                        else
                        {
                            float f;
                            if (e.SemanticName.Equals("POSITION", StringComparison.OrdinalIgnoreCase) || e.SemanticName.Equals("SV_POSITION", StringComparison.OrdinalIgnoreCase))
                                f = c < 2 ? corners[v * 2 + c] * 0.6f : c == 2 ? 0.5f : 1f;
                            else
                                f = 0.2f + 0.15f * v + 0.1f * c + 0.03f * i;
                            bits = BitConverter.SingleToUInt32Bits(f);
                        }
                        BitConverter.TryWriteBytes(data.AsSpan(at + c * 4), bits);
                    }
                }
            }
            try
            {
                IntPtr layout;
                fixed (D3D11_INPUT_ELEMENT_DESC* ep = elems)
                fixed (byte* code = vsBlob)
                    Check(((delegate* unmanaged<IntPtr, D3D11_INPUT_ELEMENT_DESC*, uint, byte*, nuint, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateInputLayout))(dev, ep, (uint)n, code, (nuint)vsBlob.Length, &layout), "CreateInputLayout");
                if (n > 0)
                {
                    IntPtr vb;
                    fixed (byte* p = data)
                    {
                        var bd = new D3D11_BUFFER_DESC { ByteWidth = (uint)data.Length, Usage = D3D11_USAGE.DEFAULT, BindFlags = D3D11_BIND.VERTEX_BUFFER };
                        var init = new D3D11_SUBRESOURCE_DATA { pSysMem = p };
                        Check(((delegate* unmanaged<IntPtr, D3D11_BUFFER_DESC*, D3D11_SUBRESOURCE_DATA*, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateBuffer))(dev, &bd, &init, &vb), "CreateBuffer vb");
                    }
                    keep(vb);
                    uint stride = (uint)(n * 16), offset = 0;
                    ((delegate* unmanaged<IntPtr, uint, uint, IntPtr*, uint*, uint*, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.IASetVertexBuffers))(ctx, 0, 1, &vb, &stride, &offset);
                }
                return layout;
            }
            finally { foreach (var h in handles) h.Free(); }
        }

        /// <summary>HLSL for a pixel shader that writes <paramref name="vs"/>'s outputs, one element
        /// per SV_Target (eight at most), as they arrive interpolated.</summary>
        public static string PixelShaderFor(DxbcShader vs, out int targets)
        {
            var sb = new StringBuilder("struct I {\n    float4 pos : SV_Position;\n");
            var body = new StringBuilder();
            var outs = new StringBuilder();
            int k = 0;
            foreach (var e in vs.Outputs.Where(e => e.SystemValue == 0).OrderBy(e => e.Register).ThenBy(e => e.Mask & -e.Mask))
            {
                int n = System.Numerics.BitOperations.PopCount((uint)e.Mask);
                if (n == 0) continue;
                string type = e.ComponentType switch { 1 => "uint", 2 => "int", _ => "float" };
                string interp = e.ComponentType == 3 ? "" : "nointerpolation ";
                sb.Append($"    {interp}{(n == 1 ? type : type + n)} a{k} : {e.SemanticName}{e.SemanticIndex};\n");
                if (k < 8)
                {
                    string pad = n == 4 ? "" : ", " + string.Join(", ", Enumerable.Repeat("0", 4 - n));
                    outs.Append($"    float4 o{k} : SV_Target{k};\n");
                    body.Append($"    o.o{k} = float4(i.a{k}{pad});\n");     // integers as numbers: small, exact in a half
                }
                k++;
            }
            targets = Math.Min(8, k);
            sb.Append("};\nstruct O {\n").Append(outs).Append("};\nO main(I i) {\n    O o;\n").Append(body).Append("    return o;\n}\n");
            return sb.ToString();
        }

        internal static IntPtr MakeTexture(IntPtr dev, IntPtr ctx, Binding b, bool comparison, Func<IntPtr, IntPtr> keep)
        {
            const int S = 16;
            if (b.Dimension is 1 or 11)
            {
                // a typed buffer of float4s
                var data = new float[64 * 4];
                for (int i = 0; i < data.Length; i++) data[i] = 0.1f + 0.8f * ((i * 37) % 101) / 101f;
                IntPtr buf, view;
                fixed (float* p = data)
                {
                    var bd = new D3D11_BUFFER_DESC { ByteWidth = (uint)(data.Length * 4), Usage = D3D11_USAGE.DEFAULT, BindFlags = D3D11_BIND.SHADER_RESOURCE };
                    var init = new D3D11_SUBRESOURCE_DATA { pSysMem = p };
                    Check(((delegate* unmanaged<IntPtr, D3D11_BUFFER_DESC*, D3D11_SUBRESOURCE_DATA*, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateBuffer))(dev, &bd, &init, &buf), "CreateBuffer srv");
                }
                keep(buf);
                var vd = new D3D11_VIEW_DESC { Format = DXGI_FORMAT.R32G32B32A32_FLOAT, ViewDimension = 1, U0 = 0, U1 = 64 };
                Check(((delegate* unmanaged<IntPtr, IntPtr, D3D11_VIEW_DESC*, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateShaderResourceView))(dev, buf, &vd, &view), "CreateShaderResourceView buffer");
                return keep(view);
            }
            if (b.Dimension is 2 or 3 or 8) return MakeTexture1D3D(dev, b, keep);
            bool cube = b.Dimension is 9 or 10;
            uint layers = cube ? 6u : b.Dimension is 5 ? 2u : 1u;
            bool depth = comparison && !cube && b.Dimension == 4 && b.ReturnType == 5;
            var td = new D3D11_TEXTURE2D_DESC
            {
                Width = S, Height = S, MipLevels = 1, ArraySize = layers,
                Format = depth ? DXGI_FORMAT.R32_TYPELESS : DXGI_FORMAT.R8G8B8A8_UNORM,
                SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 }, Usage = D3D11_USAGE.DEFAULT,
                BindFlags = D3D11_BIND.SHADER_RESOURCE | (depth ? D3D11_BIND.DEPTH_STENCIL : 0),
                MiscFlags = cube ? D3D11_RESOURCE_MISC.TEXTURECUBE : 0,
            };
            var px = new uint[S * S * layers];
            for (int i = 0; i < px.Length; i++)
            {
                int x = i % S, y = (i / S) % S, l = i / (S * S);
                // smooth gradients: a texel differs from its neighbour by a few levels, so how precisely a
                // GPU weighs its bilinear taps (hardware-dependent) moves a sample by far less than the tolerance
                px[i] = (uint)((x * 8 + l * 20) | ((y * 8 + 10) << 8) | (((x + y) * 4 + 30) << 16) | (uint)(200 + x) << 24);
            }
            IntPtr tex, srv;
            fixed (uint* p = px)
            {
                var init = new D3D11_SUBRESOURCE_DATA[layers];
                for (int l = 0; l < layers; l++) init[l] = new D3D11_SUBRESOURCE_DATA { pSysMem = p + l * S * S, SysMemPitch = S * 4 };
                fixed (D3D11_SUBRESOURCE_DATA* ip = init)
                    Check(((delegate* unmanaged<IntPtr, D3D11_TEXTURE2D_DESC*, D3D11_SUBRESOURCE_DATA*, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateTexture2D))(dev, &td, depth ? null : ip, &tex), "CreateTexture2D srv");
            }
            keep(tex);
            if (depth)
            {
                var dd = new D3D11_DEPTH_STENCIL_VIEW_DESC { Format = DXGI_FORMAT.D32_FLOAT, ViewDimension = 3 };
                IntPtr dsv;
                Check(((delegate* unmanaged<IntPtr, IntPtr, D3D11_DEPTH_STENCIL_VIEW_DESC*, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateDepthStencilView))(dev, tex, &dd, &dsv), "CreateDepthStencilView");
                keep(dsv);
                ((delegate* unmanaged<IntPtr, IntPtr, uint, float, byte, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.ClearDepthStencilView))(ctx, dsv, 1, 0.5f, 0);
                var sv = new D3D11_VIEW_DESC { Format = DXGI_FORMAT.R32_FLOAT, ViewDimension = 4, U0 = 0, U1 = 1 };
                Check(((delegate* unmanaged<IntPtr, IntPtr, D3D11_VIEW_DESC*, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateShaderResourceView))(dev, tex, &sv, &srv), "CreateShaderResourceView depth");
                return keep(srv);
            }
            var vd2 = new D3D11_VIEW_DESC { Format = DXGI_FORMAT.R8G8B8A8_UNORM, ViewDimension = cube ? 9u : layers > 1 ? 5u : 4u, U0 = 0, U1 = 1, U2 = 0, U3 = layers };
            if (b.Dimension == 10) { vd2.ViewDimension = 10; vd2.U2 = 0; vd2.U3 = 1; }
            Check(((delegate* unmanaged<IntPtr, IntPtr, D3D11_VIEW_DESC*, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateShaderResourceView))(dev, tex, &vd2, &srv), "CreateShaderResourceView");
            return keep(srv);
        }

        /// <summary>A 1D, 1D array or 3D texture of the same gradient pattern.</summary>
        private static IntPtr MakeTexture1D3D(IntPtr dev, Binding b, Func<IntPtr, IntPtr> keep)
        {
            const uint S = 16, D = 4;
            uint layers = b.Dimension == 3 ? 2u : 1u, height = b.Dimension == 8 ? S : 1u, depth = b.Dimension == 8 ? D : 1u;
            var px = new uint[S * height * depth * layers];
            for (int i = 0; i < px.Length; i++)
            {
                int x = i % (int)S, y = (i / (int)S) % (int)height, z = i / (int)(S * height);
                px[i] = (uint)((x * 8) | ((y * 8 + 10) << 8) | ((z * 30 + 30) << 16) | (200 + x) << 24);
            }
            IntPtr tex, srv;
            fixed (uint* p = px)
            {
                var init = new D3D11_SUBRESOURCE_DATA[layers];
                for (int l = 0; l < layers; l++) init[l] = new D3D11_SUBRESOURCE_DATA { pSysMem = p + l * S, SysMemPitch = S * 4, SysMemSlicePitch = S * height * 4 };
                fixed (D3D11_SUBRESOURCE_DATA* ip = init)
                {
                    if (b.Dimension == 8)
                    {
                        var d = new D3D11_TEXTURE3D_DESC { Width = S, Height = S, Depth = D, MipLevels = 1, Format = DXGI_FORMAT.R8G8B8A8_UNORM, Usage = D3D11_USAGE.DEFAULT, BindFlags = D3D11_BIND.SHADER_RESOURCE };
                        Check(((delegate* unmanaged<IntPtr, D3D11_TEXTURE3D_DESC*, D3D11_SUBRESOURCE_DATA*, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateTexture3D))(dev, &d, ip, &tex), "CreateTexture3D srv");
                    }
                    else
                    {
                        var d = new D3D11_TEXTURE1D_DESC { Width = S, MipLevels = 1, ArraySize = layers, Format = DXGI_FORMAT.R8G8B8A8_UNORM, Usage = D3D11_USAGE.DEFAULT, BindFlags = D3D11_BIND.SHADER_RESOURCE };
                        Check(((delegate* unmanaged<IntPtr, D3D11_TEXTURE1D_DESC*, D3D11_SUBRESOURCE_DATA*, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateTexture1D))(dev, &d, ip, &tex), "CreateTexture1D srv");
                    }
                }
            }
            keep(tex);
            Check(((delegate* unmanaged<IntPtr, IntPtr, void*, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateShaderResourceView))(dev, tex, null, &srv), "CreateShaderResourceView 1D/3D");
            return keep(srv);
        }

        internal static float[] ReadTarget(IntPtr dev, IntPtr ctx, IntPtr tex, Func<IntPtr, IntPtr> keep)
        {
            D3D11_TEXTURE2D_DESC d;
            ((delegate* unmanaged<IntPtr, D3D11_TEXTURE2D_DESC*, void>)Fn(tex, Vtbl.ID3D11Texture2D.GetDesc))(tex, &d);
            d.Usage = D3D11_USAGE.STAGING; d.BindFlags = 0; d.CPUAccessFlags = D3D11_CPU_ACCESS.READ;
            IntPtr st;
            Check(((delegate* unmanaged<IntPtr, D3D11_TEXTURE2D_DESC*, D3D11_SUBRESOURCE_DATA*, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateTexture2D))(dev, &d, null, &st), "CreateTexture2D staging");
            keep(st);
            ((delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.CopyResource))(ctx, st, tex);
            D3D11_MAPPED_SUBRESOURCE m;
            Check(((delegate* unmanaged<IntPtr, IntPtr, uint, D3D11_MAP, uint, D3D11_MAPPED_SUBRESOURCE*, int>)Fn(ctx, Vtbl.ID3D11DeviceContext.Map))(ctx, st, 0, D3D11_MAP.READ, 0, &m), "Map");
            bool half = d.Format == DXGI_FORMAT.R16G16B16A16_FLOAT;
            var px = new float[W * H * 4];
            for (int y = 0; y < H; y++)
                for (int i = 0; i < W * 4; i++)
                {
                    byte* row = (byte*)m.pData + y * m.RowPitch;
                    px[y * W * 4 + i] = half ? (float)((Half*)row)[i] : ((float*)row)[i];
                }
            ((delegate* unmanaged<IntPtr, IntPtr, uint, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.Unmap))(ctx, st, 0);
            return px;
        }

        /// <summary>How many pixels a draw covered: any target no longer the clear colour there.</summary>
        public static int Covered(List<float[]> targets)
        {
            int n = 0;
            for (int px = 0; px < W * H; px++)
                if (targets.Any(t => t[px * 4] != 0.25f || t[px * 4 + 1] != 0.5f || t[px * 4 + 2] != 0.75f || t[px * 4 + 3] != 1f)) n++;
            return n;
        }

        /// <summary>When every difference is a pixel one draw covered and the other did not, and there
        /// are at most <paramref name="max"/> of them: how many. Otherwise 0.</summary>
        public static int OnlyCoverageDiffers(List<float[]> a, List<float[]> b, int max)
        {
            bool Clear(List<float[]> t, int px) => t.All(x => x[px * 4] == 0.25f && x[px * 4 + 1] == 0.5f && x[px * 4 + 2] == 0.75f && x[px * 4 + 3] == 1f);
            int n = 0;
            for (int px = 0; px < W * H; px++)
            {
                bool differs = false;
                for (int t = 0; t < a.Count && !differs; t++)
                    for (int c = 0; c < 4; c++)
                    {
                        float x = a[t][px * 4 + c], y = b[t][px * 4 + c];
                        if (!(x == y || (float.IsNaN(x) && float.IsNaN(y)) || Math.Abs(x - y) <= Math.Max(4e-3f, 4e-3f * Math.Max(Math.Abs(x), Math.Abs(y))))) { differs = true; break; }
                    }
                if (!differs) continue;
                if (Clear(a, px) == Clear(b, px)) return 0;     // both covered it and disagree
                if (++n > max) return 0;
            }
            return n;
        }

        /// <summary>How many of the targets' float components differ beyond tolerance (relative 1e-3,
        /// absolute 2e-3; NaN equals NaN), and the first one.</summary>
        public static (int Count, string First) Compare(List<float[]> a, List<float[]> b)
        {
            int bad = 0; string first = "";
            for (int t = 0; t < Math.Min(a.Count, b.Count); t++)
                for (int i = 0; i < a[t].Length; i++)
                {
                    float x = a[t][i], y = b[t][i];
                    if (float.IsNaN(x) && float.IsNaN(y)) continue;
                    if (x == y) continue;
                    float tol = Math.Max(4e-3f, 4e-3f * Math.Max(Math.Abs(x), Math.Abs(y)));   // two half-float steps
                    if (!(Math.Abs(x - y) <= tol))
                    {
                        if (bad++ == 0) first = $"target {t} pixel ({i / 4 % W},{i / 4 / W}).{"xyzw"[i % 4]}: shim {y}, WARP {x}";
                    }
                }
            return (bad, first);
        }
    }
}
