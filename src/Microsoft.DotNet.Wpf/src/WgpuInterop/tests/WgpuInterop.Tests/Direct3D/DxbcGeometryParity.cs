// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Geometry-shader execution parity (Documentation/d3d11-webgpu-shim.md, section 9): a geometry shader
// draws, on the shim and on WARP, between a generated vertex shader that feeds its input signature
// and a generated pixel shader that writes its outputs to targets. Six primitives of its input kind,
// spread over the target at different depths of w, each vertex's other inputs smooth values of its
// index; the shader's constant buffers and textures from its reflection (DxbcPixelParity.Bind).
//

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Wpf.Interop.WebGpu.Direct3D;
using Microsoft.Wpf.Interop.WebGpu.Direct3D.Dxbc;
using static WgpuInterop.Tests.Direct3D.DxbcPixelParity;

namespace WgpuInterop.Tests.Direct3D
{
    internal static unsafe class DxbcGeometryParity
    {
        public const int Primitives = 6;

        /// <summary>The input-assembler topology that feeds the geometry shader's input primitive
        /// (as a list), and the vertices of one primitive; null for patches.</summary>
        public static (uint Topology, int Vertices)? TopologyFor(GeometryInfo g) => g.InputPrimitive switch
        {
            1 => (1u, 1), 2 => (2u, 2), 3 => (4u, 3), 6 => (10u, 4), 7 => (12u, 6), _ => null,
        };

        /// <summary>HLSL for a vertex shader whose outputs are the geometry shader's inputs: the
        /// position from the vertex buffer, every other float input a smooth value of the vertex index,
        /// integers small constants.</summary>
        public static string VertexShaderFor(DxbcShader gs)
        {
            var sb = new StringBuilder("struct O {\n");
            var body = new StringBuilder();
            int k = 0;
            bool position = false;
            foreach (var e in gs.Inputs.OrderBy(e => e.Register).ThenBy(e => e.Mask & -e.Mask))
            {
                bool isPos = e.SystemValue == 1 || e.SemanticName.Equals("SV_POSITION", StringComparison.OrdinalIgnoreCase);
                // clip and cull distances come from the vertex shader too (positive: nothing clipped)
                bool distance = e.SystemValue is 2 or 3;
                if (!isPos && !distance && (e.SystemValue != 0 || e.SemanticName.StartsWith("SV_", StringComparison.OrdinalIgnoreCase))) continue;
                int n = System.Numerics.BitOperations.PopCount((uint)e.Mask);
                if (n == 0) continue;
                if (distance)
                {
                    sb.Append($"    float{(n == 1 ? "" : n.ToString())} d{k} : {e.SemanticName}{e.SemanticIndex};\n");
                    body.Append($"    o.d{k++} = 1;\n");
                    continue;
                }
                if (isPos)
                {
                    if (position) continue;
                    position = true;
                    sb.Append("    float4 pos : SV_Position;\n");
                    body.Append("    o.pos = p;\n");
                    continue;
                }
                string type = e.ComponentType switch { 1 => "uint", 2 => "int", _ => "float" };
                string decl = n == 1 ? type : type + n;
                sb.Append($"    {decl} a{k} : {e.SemanticName}{e.SemanticIndex};\n");
                string comps = "xyzw".Substring(0, n);
                if (e.ComponentType == 3)
                    body.Append($"    o.a{k} = float4(0.2 + 0.05 * (id % 7) + 0.03 * {k}, 0.3 + 0.07 * (id % 3), 0.5 + 0.04 * (id % 5), 0.9 - 0.02 * {k}).{comps};\n");
                else
                    body.Append($"    o.a{k} = {type}4(id % 2, 1, 2, id % 3 + {k}).{comps};\n");
                k++;
            }
            sb.Append("};\nO main(float4 p : POSITION, uint id : SV_VertexID) {\n    O o;\n").Append(body).Append("    return o;\n}\n");
            return sb.ToString();
        }

        /// <summary>HLSL for a pixel shader that writes the geometry shader's outputs (DxbcPixelParity.
        /// PixelShaderFor) and, in one more target, the face it rasterized.</summary>
        public static string PixelShaderFor(DxbcShader gs)
        {
            string hlsl = DxbcPixelParity.PixelShaderFor(gs, out int targets);
            if (targets >= 8) return hlsl;
            return hlsl.Replace("O main(I i)", "O main(I i, bool face : SV_IsFrontFace)")
                       .Replace("struct O {\n", $"struct O {{\n    float4 front : SV_Target{targets};\n")
                       .Replace("    return o;\n", "    o.front = float4(face ? 1 : 0, i.pos.z, 0, 1);\n    return o;\n");
        }

        /// <summary>The vertex positions: six primitives of <paramref name="vertices"/> each, on a 3 x 2
        /// grid over clip space, around their centre, w from 1 to 2.</summary>
        public static float[] Positions(int vertices)
        {
            var p = new List<float>();
            for (int i = 0; i < Primitives; i++)
            {
                float cx = -0.66f + 0.66f * (i % 3), cy = i < 3 ? 0.5f : -0.5f, w = 1f + 0.2f * (i % 3);
                for (int v = 0; v < vertices; v++)
                {
                    double a = v * 2 * Math.PI / Math.Max(1, vertices) + 0.3 * i;
                    float r = vertices == 1 ? 0f : 0.25f;
                    float x = cx + r * (float)Math.Cos(a), y = cy - r * (float)Math.Sin(a);   // clockwise on screen
                    p.AddRange(new[] { x * w, y * w, (0.3f + 0.05f * i) * w, w });
                }
            }
            return p.ToArray();
        }

        /// <summary>Draws vertex, geometry and pixel shader on the device; each target's texels as in
        /// DxbcPixelParity.Draw.</summary>
        public static List<float[]> Draw(IntPtr dev, IntPtr ctx, byte[] vsBlob, byte[] gsBlob, byte[] psBlob, DxbcShader ps, Reflection gsRefl, uint topology, float[] positions, int seed)
        {
            var made = new List<IntPtr>();
            IntPtr Keep(IntPtr p) { made.Add(p); return p; }
            var name = GCHandle.Alloc(Encoding.ASCII.GetBytes("POSITION\0"), GCHandleType.Pinned);
            try
            {
                IntPtr vs, gs, psh, layout, vb;
                fixed (byte* v = vsBlob) Check(((delegate* unmanaged<IntPtr, byte*, nuint, IntPtr, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateVertexShader))(dev, v, (nuint)vsBlob.Length, IntPtr.Zero, &vs), "CreateVertexShader");
                Keep(vs);
                fixed (byte* g = gsBlob) Check(((delegate* unmanaged<IntPtr, byte*, nuint, IntPtr, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateGeometryShader))(dev, g, (nuint)gsBlob.Length, IntPtr.Zero, &gs), "CreateGeometryShader");
                Keep(gs);
                fixed (byte* p = psBlob) Check(((delegate* unmanaged<IntPtr, byte*, nuint, IntPtr, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreatePixelShader))(dev, p, (nuint)psBlob.Length, IntPtr.Zero, &psh), "CreatePixelShader");
                Keep(psh);

                var elem = new D3D11_INPUT_ELEMENT_DESC { SemanticName = (byte*)name.AddrOfPinnedObject(), Format = DXGI_FORMAT.R32G32B32A32_FLOAT };
                fixed (byte* code = vsBlob)
                    Check(((delegate* unmanaged<IntPtr, D3D11_INPUT_ELEMENT_DESC*, uint, byte*, nuint, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateInputLayout))(dev, &elem, 1, code, (nuint)vsBlob.Length, &layout), "CreateInputLayout");
                Keep(layout);
                fixed (float* data = positions)
                {
                    var bd = new D3D11_BUFFER_DESC { ByteWidth = (uint)positions.Length * 4, Usage = D3D11_USAGE.DEFAULT, BindFlags = D3D11_BIND.VERTEX_BUFFER };
                    var init = new D3D11_SUBRESOURCE_DATA { pSysMem = data };
                    Check(((delegate* unmanaged<IntPtr, D3D11_BUFFER_DESC*, D3D11_SUBRESOURCE_DATA*, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateBuffer))(dev, &bd, &init, &vb), "CreateBuffer vb");
                }
                Keep(vb);

                var outputs = ps.Outputs.Where(o => o.SystemValue == 64 || o.SemanticName.StartsWith("SV_TARGET", StringComparison.OrdinalIgnoreCase)).OrderBy(o => o.Register).ToList();
                int targets = outputs.Count == 0 ? 0 : outputs.Max(o => o.Register) + 1;
                var rts = new IntPtr[8];
                var rtTex = new List<IntPtr>();
                for (int i = 0; i < targets; i++)
                {
                    var o = outputs.FirstOrDefault(x => x.Register == i);
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

                // both windings drawn (the pixel shader writes which face it saw: strip parity shows)
                var rd = D3D11_RASTERIZER_DESC.Default();
                rd.CullMode = 1;
                IntPtr rs;
                Check(((delegate* unmanaged<IntPtr, D3D11_RASTERIZER_DESC*, IntPtr*, int>)Fn(dev, Vtbl.ID3D11Device.CreateRasterizerState))(dev, &rd, &rs), "CreateRasterizerState");
                Keep(rs);
                ((delegate* unmanaged<IntPtr, IntPtr, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.RSSetState))(ctx, rs);

                BindStage(dev, ctx, gsRefl, seed, Keep,Vtbl.ID3D11DeviceContext.GSSetConstantBuffers, Vtbl.ID3D11DeviceContext.GSSetSamplers, Vtbl.ID3D11DeviceContext.GSSetShaderResources);
                uint stride = 16, offset = 0;
                ((delegate* unmanaged<IntPtr, uint, uint, IntPtr*, uint*, uint*, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.IASetVertexBuffers))(ctx, 0, 1, &vb, &stride, &offset);
                ((delegate* unmanaged<IntPtr, IntPtr, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.IASetInputLayout))(ctx, layout);
                ((delegate* unmanaged<IntPtr, uint, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.IASetPrimitiveTopology))(ctx, topology);
                ((delegate* unmanaged<IntPtr, IntPtr, IntPtr*, uint, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.VSSetShader))(ctx, vs, null, 0);
                ((delegate* unmanaged<IntPtr, IntPtr, IntPtr*, uint, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.GSSetShader))(ctx, gs, null, 0);
                ((delegate* unmanaged<IntPtr, IntPtr, IntPtr*, uint, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.PSSetShader))(ctx, psh, null, 0);
                fixed (IntPtr* r = rts) ((delegate* unmanaged<IntPtr, uint, IntPtr*, IntPtr, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.OMSetRenderTargets))(ctx, (uint)targets, r, IntPtr.Zero);
                var vp = new D3D11_VIEWPORT { Width = W, Height = H, MaxDepth = 1 };
                ((delegate* unmanaged<IntPtr, uint, D3D11_VIEWPORT*, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.RSSetViewports))(ctx, 1, &vp);
                ((delegate* unmanaged<IntPtr, uint, uint, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.Draw))(ctx, (uint)(positions.Length / 4), 0);

                var result = new List<float[]>();
                foreach (IntPtr t in rtTex) result.Add(ReadTarget(dev, ctx, t, Keep));
                return result;
            }
            finally
            {
                ((delegate* unmanaged<IntPtr, void>)Fn(ctx, Vtbl.ID3D11DeviceContext.ClearState))(ctx);
                foreach (IntPtr p in made) Release(p);
                name.Free();
            }
        }
    }
}
