// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The WGSL module of each stage (Documentation/d3d11-webgpu-shim.md, sections 6 and 9): its entry
// point, how D3D's input and output registers and system values reach WGSL's, and the link between
// a vertex and a pixel shader.
//

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Microsoft.Wpf.Interop.WebGpu.Direct3D.Dxbc
{
    internal sealed partial class DxbcTranslator
    {
        // D3D10_SB_NAME system values
        private const int SvPosition = 1, SvClipDistance = 2, SvCullDistance = 3, SvRenderTargetArrayIndex = 4, SvViewportArrayIndex = 5,
            SvVertexId = 6, SvPrimitiveId = 7, SvInstanceId = 8, SvIsFrontFace = 9, SvSampleIndex = 10;

        private static string WgslType(int componentType) => componentType switch { 1 => "vec4<u32>", 2 => "vec4<i32>", _ => "vec4<f32>" };

        /// <summary>The WGSL interpolation attribute of a linked element (the pixel shader's mode).</summary>
        private static string Interp(LinkedElement l)
        {
            if (l.ComponentType != 3) return "@interpolate(flat) ";
            return l.Interpolation switch
            {
                1 => "@interpolate(flat) ",
                3 => "@interpolate(perspective, centroid) ",
                4 => "@interpolate(linear) ",
                5 => "@interpolate(linear, centroid) ",
                6 => "@interpolate(perspective, sample) ",
                7 => "@interpolate(linear, sample) ",
                _ => "",
            };
        }

        private static string Cast(string vec4f, int componentType) => componentType == 3 ? vec4f : $"bitcast<{WgslType(componentType)}>({vec4f})";

        /// <summary>The components of a mask moved down to start at x (an element in .zw travels in
        /// .xy of its location).</summary>
        private static string ShiftDown(int mask)
        {
            int first = 0;
            while (first < 4 && (mask & (1 << first)) == 0) first++;
            if (first == 0 || first == 4) return "";
            var s = new char[4];
            for (int i = 0; i < 4; i++) s[i] = "xyzw"[Math.Min(3, first + i)];
            return "." + new string(s);
        }

        private void AssembleVertex(StringBuilder w)
        {
            // inputs: one attribute per declared input register, at location = register
            w.Append("struct VsIn {\n");
            var vertexId = -1; var instanceId = -1;
            foreach (int reg in _inputRegs.OrderBy(r => r))
            {
                if (_inputSv.TryGetValue(reg, out int sv) && sv == SvVertexId) { vertexId = reg; continue; }
                if (_inputSv.TryGetValue(reg, out sv) && sv == SvInstanceId) { instanceId = reg; continue; }
                var e = _s.Inputs.FirstOrDefault(i => i.Register == reg && i.SystemValue == 0);
                var sigType = e?.ComponentType ?? 3;
                // the signature also names VertexID / InstanceID by semantic
                if (e != null && e.SemanticName.Equals("SV_VertexID", StringComparison.OrdinalIgnoreCase)) { vertexId = reg; continue; }
                if (e != null && e.SemanticName.Equals("SV_InstanceID", StringComparison.OrdinalIgnoreCase)) { instanceId = reg; continue; }
                w.Append($"    @location({reg}) a{reg}: {WgslType(sigType)},\n");
            }
            w.Append("    @builtin(vertex_index) vid: u32,\n    @builtin(instance_index) iid: u32,\n}\n\n");
            if (vertexId >= 0 || instanceId >= 0)
            {
                _bindings.DrawParams = true;
                w.Append($"@group({_group}) @binding({DrawParamsBinding}) var<uniform> dx_draw: vec4<u32>;\n\n");
            }

            AppendVertexOutput(w);

            w.Append("@vertex\nfn main(input: VsIn) -> VsOut {\n");
            foreach (int reg in _inputRegs.OrderBy(r => r))
            {
                // D3D's SV_VertexID is the index as stored (no base vertex), or counts from 0 (no start vertex); its SV_InstanceID counts from 0
                if (reg == vertexId) w.Append($"    {InReg(reg)} = bitcast<vec4<f32>>(vec4<u32>(bitcast<u32>(bitcast<i32>(input.vid) - bitcast<i32>(dx_draw.x)), 0u, 0u, 0u));\n");
                else if (reg == instanceId) w.Append($"    {InReg(reg)} = bitcast<vec4<f32>>(vec4<u32>(input.iid - dx_draw.y, 0u, 0u, 0u));\n");
                else
                {
                    var e = _s.Inputs.FirstOrDefault(i => i.Register == reg);
                    int ct = e?.ComponentType ?? 3;
                    string swap = _options.BgraInputs?.Contains(reg) == true ? ".zyxw" : "";
                    w.Append(ct == 3 ? $"    {InReg(reg)} = input.a{reg}{swap};\n" : $"    {InReg(reg)} = bitcast<vec4<f32>>(input.a{reg});\n");
                }
            }
            w.Append(_body);
            w.Append("    return shader_out();\n}\n");
        }

        /// <summary>The vertex stage's outputs -- position, clip distances and the pixel shader's
        /// linkage -- as struct VsOut and shader_out() over the o# registers. Shared by the vertex
        /// shader and the pass-through after a geometry shader (its stream-0 outputs).</summary>
        private void AppendVertexOutput(StringBuilder w)
        {
            var outputs = _s.Outputs.Where(e => e.Stream == _options.RasterizedStream).ToList();
            int posReg = -1;
            var clip = new List<string>();             // each clip-distance component, in register order
            foreach (var e in outputs.OrderBy(e => e.Register).ThenBy(e => e.Mask))
            {
                if (e.SystemValue == SvPosition || e.SemanticName.Equals("SV_Position", StringComparison.OrdinalIgnoreCase)) posReg = e.Register;
                if (e.SystemValue == SvClipDistance && _outputRegs.Contains(e.Register))
                    for (int c = 0; c < 4; c++) if ((e.Mask & (1 << c)) != 0) clip.Add($"{OutReg(e.Register)}.{"xyzw"[c]}");
            }
            var cull = CullComponents(_s, _options.RasterizedStream);
            if (cull.Count > 0 && !_link.Any(l => l.Semantic == CullSemantic))
            {
                // no pixel shader to discard, or no inter-stage variable left to tell it: each distance becomes
                // a clip distance of -1 at a vertex whose cull distance is negative and +1e30 at any other. A
                // primitive whose vertices are all negative is clipped away whole; any other keeps all of
                // itself but a sliver within 1e-30 of its negative vertices -- in float, none (the clipped
                // edge's new vertex is the old one)
                if (HasFeature(D3DDevice.FeatureClipDistances) && clip.Count + cull.Count <= 8)
                    foreach (var (reg, c) in cull)
                        clip.Add(_outputRegs.Contains(reg) ? $"select(1e30f, -1.0f, {OutReg(reg)}.{"xyzw"[c]} < 0.0f)" : "1e30f");
                else
                    Approximate(!HasFeature(D3DDevice.FeatureClipDistances)
                        ? "SV_CullDistance: not applied (no pixel shader or inter-stage variable to carry it, and no clip-distances feature); primitives it would cull are drawn"
                        : "SV_CullDistance: not applied (more than 8 clip and cull distances together); primitives it would cull are drawn");
            }
            if (clip.Count > 0)
            {
                if (!HasFeature(D3DDevice.FeatureClipDistances)) throw new NotSupportedException("SV_ClipDistance: this adapter has no clip-distances feature");
                if (clip.Count > 8) throw new NotSupportedException($"{clip.Count} clip distances (WebGPU takes 8)");
                _enables.Add("clip_distances");
            }
            w.Append("struct VsOut {\n    @builtin(position) pos: vec4<f32>,\n");
            if (clip.Count > 0) w.Append($"    @builtin(clip_distances) clip: array<f32, {clip.Count}>,\n");
            foreach (var l in _link) w.Append($"    @location({l.Location}) {Interp(l)}l{l.Location}: {WgslType(l.ComponentType)},\n");
            w.Append("}\n\n");

            w.Append("fn shader_out() -> VsOut {\n    var out: VsOut;\n");
            w.Append(posReg >= 0 ? $"    out.pos = {OutReg(posReg)};\n" : "    out.pos = vec4<f32>(0.0f, 0.0f, 0.0f, 1.0f);\n");
            for (int i = 0; i < clip.Count; i++) w.Append($"    out.clip[{i}] = {clip[i]};\n");
            foreach (var l in _link)
            {
                if (l.Semantic == CullSemantic)
                {
                    // per distance: 0 when this vertex's is negative, else 1 (see LinkageOf)
                    var parts = new string[4];
                    for (int c = 0; c < 4; c++)
                    {
                        int i = l.Index * 4 + c;
                        parts[c] = i < cull.Count && _outputRegs.Contains(cull[i].Register)
                            ? $"select(1.0f, 0.0f, {OutReg(cull[i].Register)}.{"xyzw"[cull[i].Component]} < 0.0f)" : "1.0f";
                    }
                    w.Append($"    out.l{l.Location} = vec4<f32>({string.Join(", ", parts)});\n");
                    continue;
                }
                var e = outputs.FirstOrDefault(x => x.SemanticName.Equals(l.Semantic, StringComparison.OrdinalIgnoreCase) && x.SemanticIndex == l.Index);
                string v = e == null || !_outputRegs.Contains(e.Register) ? "vec4<f32>(0.0f)" : $"{OutReg(e.Register)}{ShiftDown(e.Mask)}";
                w.Append($"    out.l{l.Location} = {Cast(v, l.ComponentType)};\n");
            }
            w.Append("    return out;\n}\n\n");
        }

        private void AssemblePixel(StringBuilder w)
        {
            bool sampleIndex = _s.Inputs.Any(e => e.SystemValue == SvSampleIndex || e.SemanticName.Equals("SV_SampleIndex", StringComparison.OrdinalIgnoreCase));
            bool primitive = _s.Inputs.Any(e => (e.SystemValue == SvPrimitiveId || e.SemanticName.Equals("SV_PrimitiveID", StringComparison.OrdinalIgnoreCase)) && _inputRegs.Contains(e.Register)) || _builtins.Contains("vPrim");
            if (primitive)
            {
                if (!HasFeature(D3DDevice.FeaturePrimitiveIndex))
                    throw new NotSupportedException("SV_PrimitiveID in a pixel shader: this adapter has no primitive-index feature");
                _enables.Add("primitive_index");
            }
            // front_facing only when read: WebGPU counts it against the inter-stage variables
            bool front = _s.Inputs.Any(e => e.SystemValue == SvIsFrontFace || e.SemanticName.Equals("SV_IsFrontFace", StringComparison.OrdinalIgnoreCase));
            w.Append("struct PsIn {\n    @builtin(position) pos: vec4<f32>,\n");
            if (front) w.Append("    @builtin(front_facing) front: bool,\n");
            if (primitive) w.Append("    @builtin(primitive_index) prim: u32,\n");
            if (sampleIndex) w.Append("    @builtin(sample_index) sample: u32,\n");
            if (_builtins.Contains("vCoverage")) w.Append("    @builtin(sample_mask) coverage: u32,\n");
            foreach (var l in _link) w.Append($"    @location({l.Location}) {Interp(l)}l{l.Location}: {WgslType(l.ComponentType)},\n");
            w.Append("}\n\n");

            w.Append("struct PsOut {\n");
            var targets = _s.Outputs.Where(e => e.SystemValue == 64 || e.SemanticName.Equals("SV_Target", StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var e in targets) w.Append($"    @location({e.SemanticIndex}) c{e.SemanticIndex}: {WgslType(e.ComponentType)},\n");
            if (_writesDepth) w.Append("    @builtin(frag_depth) depth: f32,\n");
            if (_builtins.Contains("oMask")) w.Append("    @builtin(sample_mask) mask: u32,\n");
            if (targets.Count == 0 && !_writesDepth && !_builtins.Contains("oMask")) w.Append("    @location(0) unused: vec4<f32>,\n");
            w.Append("}\n\n");

            w.Append("fn shader_out() -> PsOut {\n    var out: PsOut;\n");
            foreach (var e in targets)
                w.Append($"    out.c{e.SemanticIndex} = {Cast(_outputRegs.Contains(e.Register) ? OutReg(e.Register) : "vec4<f32>(0.0f)", e.ComponentType)};\n");
            if (_writesDepth) w.Append("    out.depth = oDepth;\n");
            if (_builtins.Contains("oMask")) w.Append("    out.mask = oMask;\n");
            w.Append("    return out;\n}\n\n");

            w.Append("@fragment\nfn main(input: PsIn) -> PsOut {\n");
            if (_builtins.Contains("vPrim")) w.Append("    vPrim = bitcast<vec4<f32>>(vec4<u32>(input.prim, 0u, 0u, 0u));\n");
            if (_builtins.Contains("vCoverage")) w.Append("    vCoverage = bitcast<vec4<f32>>(vec4<u32>(input.coverage, 0u, 0u, 0u));\n");
            foreach (int reg in _inputRegs.OrderBy(r => r))
            {
                // every element of this register, from its location or its system value
                var elements = _s.Inputs.Where(e => e.Register == reg).ToList();
                bool assigned = false;
                foreach (var e in elements)
                {
                    string? src = null;
                    bool system = true;
                    if (e.SystemValue == SvPosition || e.SemanticName.Equals("SV_Position", StringComparison.OrdinalIgnoreCase)) src = "input.pos";
                    else if (e.SystemValue == SvIsFrontFace || e.SemanticName.Equals("SV_IsFrontFace", StringComparison.OrdinalIgnoreCase))
                        src = "bitcast<vec4<f32>>(vec4<u32>(select(0u, 0xFFFFFFFFu, input.front)))";
                    else if (e.SystemValue == SvSampleIndex || e.SemanticName.Equals("SV_SampleIndex", StringComparison.OrdinalIgnoreCase))
                        src = "bitcast<vec4<f32>>(vec4<u32>(input.sample))";
                    else if (e.SystemValue == SvPrimitiveId || e.SemanticName.Equals("SV_PrimitiveID", StringComparison.OrdinalIgnoreCase))
                        src = "bitcast<vec4<f32>>(vec4<u32>(input.prim))";
                    else
                    {
                        system = false;
                        var l = _link.FirstOrDefault(x => x.Semantic.Equals(e.SemanticName, StringComparison.OrdinalIgnoreCase) && x.Index == e.SemanticIndex);
                        if (l != null) src = l.ComponentType == 3 ? $"input.l{l.Location}" : $"bitcast<vec4<f32>>(input.l{l.Location})";
                    }
                    if (src == null) continue;
                    if (!assigned && e.Mask == 0xF) { w.Append($"    {InReg(reg)} = {src};\n"); assigned = true; continue; }
                    // place the element's components where its mask says, from x of the source (a system
                    // value keeps its own components)
                    int k = 0;
                    w.Append($"    {{ let e = {src};\n");
                    for (int c = 0; c < 4; c++)
                        if ((e.Mask & (1 << c)) != 0)
                            w.Append($"      {InReg(reg)}.{"xyzw"[c]} = e.{(system ? "xyzw"[c] : "xyzw"[k++])};\n");
                    w.Append("    }\n");
                    assigned = true;
                }
            }
            // a primitive every vertex of which has a negative cull distance is culled (see LinkageOf)
            foreach (var l in _link.Where(l => l.Semantic == CullSemantic))
                w.Append($"    if (any(input.l{l.Location} <= vec4<f32>(0.0f))) {{ discard; }}\n");
            w.Append(_body);
            w.Append("    return shader_out();\n}\n");
        }

        private void AssembleCompute(StringBuilder w)
        {
            w.Append($"@compute @workgroup_size({_threadGroup[0]}, {_threadGroup[1]}, {_threadGroup[2]})\n");
            w.Append("fn main(@builtin(global_invocation_id) gid: vec3<u32>, @builtin(local_invocation_id) lid: vec3<u32>, @builtin(workgroup_id) wid: vec3<u32>, @builtin(local_invocation_index) lidx: u32) {\n");
            if (_builtins.Contains("vThreadID")) w.Append("    vThreadID = bitcast<vec4<f32>>(vec4<u32>(gid, 0u));\n");
            if (_builtins.Contains("vThreadGroupID")) w.Append("    vThreadGroupID = bitcast<vec4<f32>>(vec4<u32>(wid, 0u));\n");
            if (_builtins.Contains("vThreadIDInGroup")) w.Append("    vThreadIDInGroup = bitcast<vec4<f32>>(vec4<u32>(lid, 0u));\n");
            if (_builtins.Contains("vThreadIDInGroupFlattened")) w.Append("    vThreadIDInGroupFlattened = bitcast<vec4<f32>>(vec4<u32>(lidx, 0u, 0u, 0u));\n");
            w.Append(_body);
            w.Append("}\n");
        }
    }
}
