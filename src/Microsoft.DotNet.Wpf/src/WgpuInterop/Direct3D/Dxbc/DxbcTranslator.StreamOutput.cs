// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Stream output (Documentation/d3d11-webgpu-shim.md, section 9): the vertices of a geometry shader
// made with CreateGeometryShaderWithStreamOutput -- or of a vertex shader handed to it in a GS's
// place -- written to the bound SO buffers as D3D writes them: primitives as lists (strips cut into
// their triangles or lines), in order, each vertex the declaration's components packed at the
// buffer's stride, appended at the buffer's filled size, and no primitive written that would not fit
// in every buffer.
//
// Three kernels keep the order without atomics: so_count (per GS invocation: how many primitives its
// segment holds), so_scan (one invocation: the prefix of those counts, how many fit, and the filled
// sizes moved on), so_write (per invocation: its primitives at their places). A vertex shader's
// primitives need no count: the topology says where each is.
//
// Bindings, in group 1 next to the geometry plumbing: 7 the counts (and prefix), 9 the scan's
// results (primitives that fit; each buffer's filled size before the draw), 10 + slot the SO buffers
// (as words), 14 + slot each buffer's filled size (bytes).
//

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Microsoft.Wpf.Interop.WebGpu.Direct3D.Dxbc
{
    internal sealed class SoEntry
    {
        public int Stream;
        /// <summary>Null: a gap ("$SKIP") of <see cref="Count"/> components.</summary>
        public string? Semantic;
        public int Index, Start, Count, Slot;
    }

    /// <summary>A stream-output declaration (CreateGeometryShaderWithStreamOutput).</summary>
    internal sealed class StreamOutputDesc
    {
        public readonly List<SoEntry> Entries = new();
        public readonly uint[] Strides = new uint[4];
        /// <summary>The stream the rasterizer sees; -1 (D3D11_SO_NO_RASTERIZED_STREAM) for none.</summary>
        public int RasterizedStream;

        public IEnumerable<int> Slots => Entries.Select(e => e.Slot).Distinct().OrderBy(s => s);

        public string Key => string.Join(";", Entries.Select(e => $"{e.Stream}:{e.Semantic}{e.Index}.{e.Start}+{e.Count}@{e.Slot}")) + "|" + string.Join(",", Strides) + "|" + RasterizedStream;
    }

    internal sealed partial class DxbcTranslator
    {
        public const int PlumbingSoCounts = 7, PlumbingSoState = 9, PlumbingSoBuffer = 10, PlumbingSoFilled = 14;

        private StreamOutputDesc? StreamOut => _options.StreamOutput;

        private static void AppendStreamOutBindings(StringBuilder w, StreamOutputDesc so, bool counts)
        {
            if (counts) w.Append($"@group({PlumbingGroup}) @binding({PlumbingSoCounts}) var<storage, read_write> dx_socount: array<u32>;\n");
            w.Append($"@group({PlumbingGroup}) @binding({PlumbingSoState}) var<storage, read_write> dx_sostate: array<u32>;\n");
            foreach (int s in so.Slots)
            {
                w.Append($"@group({PlumbingGroup}) @binding({PlumbingSoBuffer + s}) var<storage, read_write> dx_so{s}: array<u32>;\n");
                w.Append($"@group({PlumbingGroup}) @binding({PlumbingSoFilled + s}) var<storage, read_write> dx_sofill{s}: array<u32>;\n");
            }
        }

        /// <summary>so_scan's tail: how many of <paramref name="total"/> primitives fit in every buffer,
        /// and each buffer's filled size before and after.</summary>
        private static void AppendStreamOutFit(StringBuilder w, StreamOutputDesc so, int vertices, string total)
        {
            w.Append($"    var fit = {total};\n");
            foreach (int s in so.Slots)
            {
                uint stride = Math.Max(4, so.Strides[s]);
                // a slot with no buffer bound (dx_p[3].y, a bit per slot) limits nothing; its writes are dropped
                w.Append($"    if ((dx_p[3].y & {1u << s}u) != 0u) {{ let cap = arrayLength(&dx_so{s}) * 4u; let used = min(dx_sofill{s}[0], cap); fit = min(fit, (cap - used) / {(uint)vertices * stride}u); }}\n");
            }
            w.Append("    dx_sostate[0] = fit;\n");
            foreach (int s in so.Slots)
                w.Append($"    dx_sostate[1u + {s}u] = dx_sofill{s}[0];\n    dx_sofill{s}[0] = dx_sofill{s}[0] + fit * {(uint)vertices * Math.Max(4, so.Strides[s])}u;\n");
        }

        /// <summary>Statements writing vertex <paramref name="vertex"/> (0-based in its primitive) of
        /// primitive <paramref name="prim"/>, its registers read by <paramref name="reg"/>(register).</summary>
        private static void AppendStreamOutVertex(StringBuilder w, StreamOutputDesc so, List<SignatureElement> outputs, int vertices, string prim, int vertex, Func<int, string> reg, string indent)
        {
            var offsets = new Dictionary<int, int>();
            foreach (var e in so.Entries.Where(e => e.Stream == 0))
            {
                int at = offsets.TryGetValue(e.Slot, out int o) ? o : 0;
                offsets[e.Slot] = at + e.Count * 4;
                if (e.Semantic == null) continue;
                var el = outputs.FirstOrDefault(x => x.Stream == 0 && x.SemanticName.Equals(e.Semantic, StringComparison.OrdinalIgnoreCase) && x.SemanticIndex == e.Index);
                if (el == null) continue;
                uint stride = Math.Max(4, so.Strides[e.Slot]);
                w.Append($"{indent}{{ let b = (dx_sostate[1u + {e.Slot}u] + ({prim} * {vertices}u + {vertex}u) * {stride}u + {at}u) / 4u; let v = bitcast<vec4<u32>>({reg(el.Register)});\n");
                for (int k = 0; k < e.Count && e.Start + k < 4; k++)
                    w.Append($"{indent}  if (b + {k}u < arrayLength(&dx_so{e.Slot})) {{ dx_so{e.Slot}[b + {k}u] = v.{"xyzw"[e.Start + k]}; }}\n");
                w.Append($"{indent}}}\n");
            }
        }

        /// <summary>A GS invocation's segment walked as list primitives: <paramref name="emit"/>(vertex
        /// index expressions) for each, the count in <c>n</c>.</summary>
        private static void AppendSegmentWalk(StringBuilder w, GeometryInfo g, Func<string[], string> emit)
        {
            int per = g.IndicesPerInvocation;
            w.Append("    var n = 0u;\n    var run = 0u;\n    var pa = 0u;\n    var pb = 0u;\n");
            w.Append($"    for (var j = 0u; j < {per}u; j = j + 1u) {{\n        let x = dx_gsidx[i * {per}u + j];\n        if (x == 0xFFFFFFFFu) {{ run = 0u; continue; }}\n");
            switch (g.OutputTopology)
            {
                case 1:
                    w.Append("        " + emit(new[] { "x" }) + "\n        n = n + 1u;\n");
                    break;
                case 3:
                    w.Append("        if (run >= 1u) { " + emit(new[] { "pa", "x" }) + " n = n + 1u; }\n        pa = x;\n");
                    break;
                default:
                    // a strip's odd triangles are written as (n, n+2, n+1), as a GS sees them (measured on WARP)
                    w.Append("        if (run >= 2u) {\n            if ((run & 1u) == 0u) { " + emit(new[] { "pa", "pb", "x" }) + " } else { " + emit(new[] { "pa", "x", "pb" }) + " }\n            n = n + 1u;\n        }\n        pa = pb;\n        pb = x;\n");
                    break;
            }
            w.Append("        run = run + 1u;\n    }\n");
        }

        /// <summary>The geometry module's three stream-output kernels.</summary>
        private void AppendGeometryStreamOut(StringBuilder w)
        {
            var so = StreamOut!;
            var g = _geometry!;
            int regs = OutputRegisters, v = g.OutputTopology == 1 ? 1 : g.OutputTopology == 3 ? 2 : 3;
            AppendStreamOutBindings(w, so, counts: true);
            var outputs = _s.Outputs;

            w.Append("\n@compute @workgroup_size(64)\nfn so_count(@builtin(global_invocation_id) gid: vec3<u32>) {\n");
            w.Append($"    let i = gid.x + gid.y * {InvocationsPerRow}u;\n    if (i >= dx_p[3].x) {{ return; }}\n");
            AppendSegmentWalk(w, g, _ => "");
            w.Append("    dx_socount[i] = n;\n}\n");

            w.Append("\n@compute @workgroup_size(1)\nfn so_scan() {\n    var total = 0u;\n");
            w.Append("    for (var i = 0u; i < dx_p[3].x; i = i + 1u) { let c = dx_socount[i]; dx_socount[i] = total; total = total + c; }\n");
            AppendStreamOutFit(w, so, v, "total");
            w.Append("}\n");

            w.Append("\n@compute @workgroup_size(64)\nfn so_write(@builtin(global_invocation_id) gid: vec3<u32>) {\n");
            w.Append($"    let i = gid.x + gid.y * {InvocationsPerRow}u;\n    if (i >= dx_p[3].x) {{ return; }}\n    let first = dx_socount[i];\n");
            int calls = 0;
            AppendSegmentWalk(w, g, idx =>
            {
                var sb = new StringBuilder($"if (first + n < dx_sostate[0]) {{ ");
                for (int k = 0; k < idx.Length; k++)
                {
                    string x = idx[k];
                    var inner = new StringBuilder();
                    AppendStreamOutVertex(inner, so, outputs, v, "(first + n)", k, r => $"dx_gsout[{x} * {regs}u + {r}u]", "");
                    sb.Append(inner.ToString().Replace("\n", " "));
                }
                calls++;
                return sb.Append("}").ToString();
            });
            w.Append("}\n");
        }

        /// <summary>Stream output of a vertex or domain shader handed to CreateGeometryShaderWithStreamOutput:
        /// its outputs (from the shader run as compute) by the input assembler's primitives, or -- with
        /// <paramref name="indexed"/>, after tessellation -- by a list of <paramref name="vertices"/>-vertex
        /// primitives whose vertices dx_vin names. Entry points so_scan and so_write over the geometry
        /// plumbing's parameters.</summary>
        public static string StreamOutFromVertex(DxbcShader vs, int vsRegs, StreamOutputDesc so, int vertices, bool indexed = false)
        {
            var w = new StringBuilder();
            w.Append($"@group({PlumbingGroup}) @binding({PlumbingParams}) var<uniform> dx_p: array<vec4<u32>, {ParamVec4s}>;\n");
            w.Append($"@group({PlumbingGroup}) @binding({PlumbingVsOut}) var<storage, read> dx_vsout: array<vec4<f32>>;\n");
            if (indexed) w.Append($"@group({PlumbingGroup}) @binding({PlumbingInputIndices}) var<storage, read> dx_vin: array<u32>;\n");
            AppendStreamOutBindings(w, so, counts: false);
            w.Append("\n@compute @workgroup_size(1)\nfn so_scan() {\n");
            AppendStreamOutFit(w, so, vertices, "dx_p[3].x");
            w.Append("}\n");
            w.Append("\n@compute @workgroup_size(64)\nfn so_write(@builtin(global_invocation_id) gid: vec3<u32>) {\n");
            w.Append($"    let i = gid.x + gid.y * {InvocationsPerRow}u;\n    if (i >= dx_sostate[0]) {{ return; }}\n");
            w.Append("    let prims = max(dx_p[2].y, 1u);\n    let prim = i % prims;\n    let first = (i / prims) * dx_p[0].x;\n");
            for (int k = 0; k < vertices; k++)
            {
                // the topology's vertices of the primitive (as the GS path assembles them)
                if (indexed) w.Append($"    {{\n        let o = dx_vin[prim * {vertices}u + {k}u];\n");
                else
                {
                    w.Append($"    {{\n        var o = prim * dx_p[2].w + {k}u;\n        if (dx_p[2].z != 0u) {{ o = prim + {k}u; }}\n");
                    if (k > 0) w.Append($"        if (dx_p[2].z == 2u && (prim & 1u) == 1u) {{ o = prim + {3 - k}u; }}\n");
                }
                AppendStreamOutVertex(w, so, vs.Outputs, vertices, "i", k, r => $"dx_vsout[(first + o) * {vsRegs}u + {r}u]", "        ");
                w.Append("    }\n");
            }
            w.Append("}\n");
            return w.ToString();
        }
    }
}
