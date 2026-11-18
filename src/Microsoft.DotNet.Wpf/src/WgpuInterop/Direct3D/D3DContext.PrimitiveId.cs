// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// SV_PrimitiveID in a pixel shader with no geometry shader, on an adapter without WebGPU's
// primitive-index feature (Metal): the draw goes through the geometry shader path with a pass-through
// geometry shader of its own -- made here, in HLSL, by the shim's compiler -- that copies each vertex
// and writes the input primitive's ID, which that path counts as the input assembler does (lists,
// strips, adjacency, strip cuts, instances); the pixel shader reads it linked (LinkageOf).
//

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Wpf.Interop.WebGpu.Direct3D.Dxbc;
using Microsoft.Wpf.Interop.WebGpu.Direct3D.Hlsl;

namespace Microsoft.Wpf.Interop.WebGpu.Direct3D
{
    internal sealed unsafe partial class D3DContext
    {
        private readonly Dictionary<(ulong, uint), D3DShader?> _primitiveIdGs = new();

        private static bool ReadsPrimitiveId(DxbcShader ps) =>
            ps.Inputs.Any(e => e.SystemValue == 7 || e.SemanticName.Equals("SV_PrimitiveID", StringComparison.OrdinalIgnoreCase));

        /// <summary>The draw needs the pass-through: a pixel shader reading SV_PrimitiveID, no geometry
        /// shader, and no primitive index on the adapter.</summary>
        private bool NeedsPrimitiveIdPass() =>
            _gs.Shader == null && _ps.Shader is { } ps && ReadsPrimitiveId(ps.Code) && !D3DDevice.HasFeature(D3DDevice.FeaturePrimitiveIndex);

        /// <summary>The pass-through geometry shader for the vertex shader's outputs and the topology (null: none fits).</summary>
        private D3DShader? PrimitiveIdGs(D3DShader vs, uint topology)
        {
            if (_primitiveIdGs.TryGetValue((vs.Code.Hash, topology), out var cached)) return cached;
            D3DShader? gs = null;
            try
            {
                string? src = PrimitiveIdGsSource(vs.Code, topology);
                if (src != null)
                {
                    var r = HlslCompiler.Compile(src, "primitive-id.hlsl", null, null, "main", "gs_5_0", 0);
                    if (r.Succeeded) gs = new D3DShader(Device, DxbcShader.Parse(r.Code!));
                    else D3DLog.Once("primgs", $"SV_PrimitiveID's pass-through geometry shader did not compile: {r.Messages.Trim()}");
                }
                else D3DLog.Once("primgs:" + topology, $"SV_PrimitiveID in topology {topology} without a geometry shader: no pass-through for it");
            }
            catch (Exception e) { D3DLog.Error("primitive-id gs", e); }
            _primitiveIdGs[(vs.Code.Hash, topology)] = gs;
            return gs;
        }

        internal static string? PrimitiveIdGsSource(DxbcShader vs, uint topology)
        {
            // the input primitive, its vertices, the ones it passes on, the stream
            (string Prim, int N, int[] Pass, string Stream)? p = topology switch
            {
                1 => ("point", 1, new[] { 0 }, "PointStream"),
                2 or 3 => ("line", 2, new[] { 0, 1 }, "LineStream"),
                4 or 5 => ("triangle", 3, new[] { 0, 1, 2 }, "TriangleStream"),
                10 or 11 => ("lineadj", 4, new[] { 1, 2 }, "LineStream"),
                12 or 13 => ("triangleadj", 6, new[] { 0, 2, 4 }, "TriangleStream"),
                _ => null,
            };
            if (p == null) return null;
            var fields = new StringBuilder();
            int k = 0;
            foreach (var e in vs.Outputs.Where(e => e.Stream == 0).OrderBy(e => e.Register).ThenBy(e => e.Mask))
            {
                if (e.SystemValue == 7 || e.SemanticName.Equals("SV_PrimitiveID", StringComparison.OrdinalIgnoreCase)) continue;
                int n = System.Numerics.BitOperations.PopCount((uint)e.Mask);
                string type = (e.ComponentType switch { 1 => "uint", 2 => "int", _ => "float" }) + (n > 1 ? n.ToString(System.Globalization.CultureInfo.InvariantCulture) : "");
                string sem = e.SemanticName + e.SemanticIndex;
                fields.Append($"    {type} f{k++} : {sem};\n");
            }
            var sb = new StringBuilder();
            sb.Append("struct V {\n").Append(fields).Append("};\n");
            sb.Append("struct G {\n    V v;\n    uint dx_prim : SV_PrimitiveID;\n};\n");
            sb.Append($"[maxvertexcount({p.Value.Pass.Length})]\nvoid main({p.Value.Prim} V v[{p.Value.N}], uint id : SV_PrimitiveID, inout {p.Value.Stream}<G> s)\n{{\n");
            foreach (int i in p.Value.Pass) sb.Append($"    {{ G o; o.v = v[{i}]; o.dx_prim = id; s.Append(o); }}\n");
            sb.Append("}\n");
            return sb.ToString();
        }
    }
}
