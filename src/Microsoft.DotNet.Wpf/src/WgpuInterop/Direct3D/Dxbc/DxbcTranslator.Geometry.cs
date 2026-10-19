// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The geometry-shader path (Documentation/d3d11-webgpu-shim.md, sections 6 and 9). WebGPU has no
// geometry stage, so a draw with a GS bound runs as
//
//   1. the vertex shader as a compute kernel, one invocation per vertex the draw reads (count x
//      instances), fetching its own inputs from the vertex buffers (bound as storage) by the input
//      layout's formats, and writing its output registers to a storage buffer;
//   2. the geometry shader as a compute kernel, one invocation per input primitive (x GS instances x
//      draw instances), reading its primitive's vertices from (1). Each invocation owns a fixed slot of
//      dcl_maxout vertices and an index segment: emit writes the output registers and appends the
//      vertex's index, cut appends a strip restart, the rest of the segment is restarts. So the
//      primitives keep D3D's order (invocation by invocation) without any atomics;
//   3. a plain indexed draw of those indices with a generated pass-through vertex shader that reads
//      the registers back and hands them to the pixel shader by the usual linkage. Strips stay strips
//      (primitive restart), so their winding is the rasterizer's own.
//
// Plumbing bindings live in group 1 of both kernels (group 0 is the stage's own resources):
//   0 the draw's parameters (uniform)   1 the index buffer   2 the vertex shader's outputs
//   3 the geometry shader's outputs     4 its index segments  8 + slot  vertex buffer `slot`
//

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Microsoft.Wpf.Interop.WebGpu.Direct3D.Dxbc
{
    internal enum StageForm { Native, VertexCompute, GeometryCompute, GeometryPassThrough, HullCompute, DomainVertex }

    /// <summary>One input-layout element, as the compute form of a vertex shader fetches it.</summary>
    internal sealed class FetchElement
    {
        public string Semantic = "";
        public int Index;
        public DXGI_FORMAT Format;
        public int Slot;
        public uint Offset;
        public bool PerInstance;
        public uint StepRate;
    }

    /// <summary>A geometry shader's declarations: what it takes and what it makes.</summary>
    internal sealed class GeometryInfo
    {
        /// <summary>D3D10_SB_PRIMITIVE: 1 point, 2 line, 3 triangle, 6 line with adjacency, 7 triangle with adjacency.</summary>
        public int InputPrimitive;
        public int InputVertices;
        /// <summary>D3D10_SB_PRIMITIVE_TOPOLOGY: 1 point list, 3 line strip, 5 triangle strip.</summary>
        public int OutputTopology = 5;
        public int MaxVertices;
        public int Instances = 1;

        public bool Strip => OutputTopology != 1;
        /// <summary>Indices one invocation owns: each emit and at most one restart after it, and a final restart.</summary>
        public int IndicesPerInvocation => Strip ? 2 * MaxVertices + 1 : Math.Max(1, MaxVertices);

        public static GeometryInfo Of(DxbcShader gs)
        {
            var g = new GeometryInfo();
            foreach (var ins in gs.Instructions)
            {
                switch (ins.Op)
                {
                    case Opcode.DclGsInputPrimitive: g.InputPrimitive = (int)(ins.Control & 0x3F); break;
                    case Opcode.DclGsOutputTopology: g.OutputTopology = (int)(ins.Control & 0x7F); break;
                    case Opcode.DclMaxOutputVertexCount: g.MaxVertices = ins.Extra.Length > 0 ? (int)ins.Extra[0] : 0; break;
                    case Opcode.DclGsInstanceCount: g.Instances = ins.Extra.Length > 0 ? Math.Max(1, (int)ins.Extra[0]) : 1; break;
                }
            }
            g.InputVertices = g.InputPrimitive switch
            {
                1 => 1, 2 => 2, 3 => 3, 6 => 4, 7 => 6,
                >= 8 and <= 39 => g.InputPrimitive - 7,         // control-point patches 1..32
                _ => 0,
            };
            return g;
        }
    }

    internal sealed partial class DxbcTranslator
    {
        public const int PlumbingGroup = 1, PlumbingParams = 0, PlumbingIndices = 1, PlumbingVsOut = 2, PlumbingGsOut = 3, PlumbingGsIndices = 4, PlumbingVertexBuffer = 8;
        /// <summary>vec4&lt;u32&gt;s of the draw's parameters: four of the draw, then one per vertex-buffer slot.</summary>
        public const int ParamVec4s = 4 + 32;
        /// <summary>Invocations per row of a dispatch: 65535 workgroups of 64.</summary>
        public const uint InvocationsPerRow = 65535u * 64u;

        private StageForm _form = StageForm.Native;
        private IReadOnlyList<FetchElement> _fetch = Array.Empty<FetchElement>();
        private DxbcShader? _upstream;            // the GS form: the vertex shader whose outputs it reads
        private int _upstreamRegisters;
        private GeometryInfo? _geometry;
        private readonly SortedSet<int> _gsInputRegs = new();

        /// <summary>Output registers the stage writes (o0 .. o[n-1]): the stride of its storage output.</summary>
        public int OutputRegisters => Math.Max(1, _outputRegs.Count == 0 ? 0 : _outputRegs.Max() + 1);

        public static DxbcTranslator TranslateVertexCompute(DxbcShader vs, IReadOnlyList<FetchElement> fetch, TranslationOptions? options = null)
        {
            if (vs.Stage != ShaderStage.Vertex) throw new ArgumentException("not a vertex shader");
            var t = new DxbcTranslator(vs, 0, Array.Empty<LinkedElement>(), options) { _form = StageForm.VertexCompute, _fetch = fetch };
            t.Run();
            return t;
        }

        public static DxbcTranslator TranslateGeometryCompute(DxbcShader gs, DxbcShader? vs, int vsRegisters, TranslationOptions? options = null)
        {
            if (gs.Stage != ShaderStage.Geometry) throw new ArgumentException("not a geometry shader");
            var g = GeometryInfo.Of(gs);
            if (g.InputVertices == 0 || g.InputVertices > 6) throw new NotSupportedException($"geometry shader input primitive {g.InputPrimitive}");
            if (g.OutputTopology is not (1 or 3 or 5)) throw new NotSupportedException($"geometry shader output topology {g.OutputTopology}");
            var t = new DxbcTranslator(gs, 0, Array.Empty<LinkedElement>(), options)
            {
                _form = StageForm.GeometryCompute, _upstream = vs, _upstreamRegisters = vsRegisters, _geometry = g,
            };
            t.Run();
            return t;
        }

        /// <summary>The vertex shader of the draw after a geometry shader: reads its output registers back
        /// and hands them to the pixel shader by <paramref name="link"/>.</summary>
        public static DxbcTranslator PassThrough(DxbcShader gs, IReadOnlyList<LinkedElement> link, TranslationOptions? options = null)
        {
            var t = new DxbcTranslator(gs, 0, link, options) { _form = StageForm.GeometryPassThrough, _geometry = GeometryInfo.Of(gs) };
            foreach (var ins in gs.Instructions) t.Declare(ins);
            var w = new StringBuilder();
            t.AssemblePassThrough(w);
            var head = new StringBuilder();
            foreach (string e in t._enables) head.Append($"enable {e};\n");
            t.Wgsl = head.Append(w).ToString();
            return t;
        }

        /// <summary>The GS form's input register: v[vertex][register].</summary>
        private string GeometryInput(Operand o)
        {
            if (o.Indices.Length != 2) throw new NotSupportedException("a geometry shader input without a vertex index");
            return $"dx_gv[{IndexExpr(o.Indices[0])}][{IndexExpr(o.Indices[1])}]";
        }

        private void EmitGeometry(Instruction ins)
        {
            if (_form != StageForm.GeometryCompute) throw new NotSupportedException("geometry shader instruction outside a geometry shader");
            int stream = ins.Op is Opcode.EmitStream or Opcode.CutStream or Opcode.EmitThenCutStream && ins.Operands.Length > 0 ? ins.Operands[0].Index0 : 0;
            if (stream != 0) Approximate("geometry shader streams above 0: rasterized with stream 0 (as WARP draws a GS made without stream output); stream output is not written");
            switch (ins.Op)
            {
                case Opcode.Emit: case Opcode.EmitStream: Line("dx_emit();"); break;
                case Opcode.Cut: case Opcode.CutStream: Line("dx_cut();"); break;
                default: Line("dx_emit();"); Line("dx_cut();"); break;
            }
        }

        // ---- the vertex shader as a compute kernel ---------------------------------------------------

        private void AssembleVertexCompute(StringBuilder w)
        {
            w.Append($"@group({PlumbingGroup}) @binding({PlumbingParams}) var<uniform> dx_p: array<vec4<u32>, {ParamVec4s}>;\n");
            w.Append($"@group({PlumbingGroup}) @binding({PlumbingIndices}) var<storage, read> dx_ib: array<u32>;\n");
            w.Append($"@group({PlumbingGroup}) @binding({PlumbingVsOut}) var<storage, read_write> dx_vsout: array<vec4<f32>>;\n");
            var slots = new SortedSet<int>();
            var used = new List<(FetchElement F, SignatureElement S)>();
            foreach (var sig in _s.Inputs.Where(i => i.SystemValue == 0 && _inputRegs.Contains(i.Register)))
            {
                var f = _fetch.FirstOrDefault(e => e.Semantic.Equals(sig.SemanticName, StringComparison.OrdinalIgnoreCase) && e.Index == sig.SemanticIndex);
                if (f == null) continue;
                used.Add((f, sig));
                slots.Add(f.Slot);
            }
            // half elements convert exactly (the body's helpers are already written: add it here if they lack it)
            if (used.Any(u => u.F.Format is DXGI_FORMAT.R16G16B16A16_FLOAT or DXGI_FORMAT.R16G16_FLOAT or DXGI_FORMAT.R16_FLOAT) && !_helpers.Contains(HalfWgsl)) w.Append(HalfWgsl);
            foreach (int s in slots)
            {
                w.Append($"@group({PlumbingGroup}) @binding({PlumbingVertexBuffer + s}) var<storage, read> dx_vb{s}: array<u32>;\n");
                // a word at any byte address (8- and 16-bit elements need not be word aligned); past the end, 0
                w.Append($"fn dx_vbw{s}(a: u32) -> u32 {{\n    let i = a >> 2u;\n    let n = arrayLength(&dx_vb{s});\n    let lo = select(0u, dx_vb{s}[min(i, n - 1u)], i < n);\n");
                w.Append($"    let sh = (a & 3u) * 8u;\n    if (sh == 0u) {{ return lo; }}\n    let hi = select(0u, dx_vb{s}[min(i + 1u, n - 1u)], i + 1u < n);\n    return (lo >> sh) | (hi << (32u - sh));\n}}\n");
            }
            w.Append("\nfn dx_body() {\n").Append(_body).Append("}\n\n");

            w.Append("@compute @workgroup_size(64)\nfn main(@builtin(global_invocation_id) gid: vec3<u32>) {\n");
            w.Append($"    let idx = gid.x + gid.y * {InvocationsPerRow}u;\n    if (idx >= dx_p[2].x) {{ return; }}\n");
            w.Append("    let count = max(dx_p[0].x, 1u);\n    let inst = idx / count;\n    let ord = idx % count;\n    var vid = dx_p[0].y + ord;\n    var fetch = vid;\n");
            w.Append("    if (dx_p[1].x != 0u) {\n");
            w.Append("        var at = dx_p[1].y + (dx_p[0].y + ord) * 4u;\n        if (dx_p[1].z != 0u) { at = dx_p[1].y + (dx_p[0].y + ord) * 2u; }\n");
            w.Append("        let n = arrayLength(&dx_ib);\n        var index = select(0u, dx_ib[min(at >> 2u, n - 1u)], (at >> 2u) < n);\n");
            w.Append("        if (dx_p[1].z != 0u) { index = (index >> ((at & 2u) * 8u)) & 0xFFFFu; }\n");
            // D3D's SV_VertexID is the index as stored; the base vertex moves only the fetch
            w.Append("        vid = index;\n        fetch = bitcast<u32>(bitcast<i32>(index) + bitcast<i32>(dx_p[0].z));\n    }\n");
            // and SV_InstanceID counts from 0 whatever the start instance (which moves only the fetch)
            w.Append("    let iid = inst;\n");
            foreach (int reg in _inputRegs.OrderBy(r => r))
            {
                var e = _s.Inputs.FirstOrDefault(i => i.Register == reg);
                bool isVid = (_inputSv.TryGetValue(reg, out int sv) && sv == SvVertexId) || (e != null && e.SemanticName.Equals("SV_VertexID", StringComparison.OrdinalIgnoreCase));
                bool isIid = (_inputSv.TryGetValue(reg, out sv) && sv == SvInstanceId) || (e != null && e.SemanticName.Equals("SV_InstanceID", StringComparison.OrdinalIgnoreCase));
                if (isVid) w.Append($"    {InReg(reg)} = bitcast<vec4<f32>>(vec4<u32>(vid, 0u, 0u, 0u));\n");
                else if (isIid) w.Append($"    {InReg(reg)} = bitcast<vec4<f32>>(vec4<u32>(iid, 0u, 0u, 0u));\n");
            }
            foreach (var (f, sig) in used)
            {
                string element = f.PerInstance ? (f.StepRate == 0 ? "dx_p[0].w" : $"(dx_p[0].w + inst / {f.StepRate}u)") : "fetch";
                w.Append($"    {{\n        let a = dx_p[{4 + f.Slot}].x + {element} * dx_p[{4 + f.Slot}].y + {f.Offset}u;\n");
                w.Append($"        let e = {Fetch(f.Format, $"dx_vbw{f.Slot}", "a")};\n");
                int k = 0;
                for (int c = 0; c < 4; c++)
                    if ((sig.Mask & (1 << c)) != 0) w.Append($"        {InReg(sig.Register)}.{"xyzw"[c]} = e.{"xyzw"[k++]};\n");
                w.Append("    }\n");
            }
            w.Append("    dx_body();\n");
            int regs = OutputRegisters;
            foreach (int r in _outputRegs.OrderBy(r => r)) w.Append($"    dx_vsout[idx * {regs}u + {r}u] = {OutReg(r)};\n");
            w.Append("}\n");
        }

        /// <summary>A vertex element of <paramref name="f"/> at byte address <paramref name="a"/> as the
        /// bits of a vec4&lt;f32&gt; register: missing components 0, and 1 in w, as D3D fills them.</summary>
        private string Fetch(DXGI_FORMAT f, string word, string a)
        {
            // per component: bit offset, bit width; the kind: f float32, h half, n unorm, s snorm, u uint, i sint
            (int Off, int Bits)[] comps;
            char kind;
            bool bgra = false, forceW = false;
            static (int, int)[] Even(int bits, int n) => Enumerable.Range(0, n).Select(i => (i * bits, bits)).ToArray();
            switch (f)
            {
                case DXGI_FORMAT.R32G32B32A32_FLOAT: comps = Even(32, 4); kind = 'f'; break;
                case DXGI_FORMAT.R32G32B32A32_UINT: comps = Even(32, 4); kind = 'u'; break;
                case DXGI_FORMAT.R32G32B32A32_SINT: comps = Even(32, 4); kind = 'i'; break;
                case DXGI_FORMAT.R32G32B32_FLOAT: comps = Even(32, 3); kind = 'f'; break;
                case DXGI_FORMAT.R32G32B32_UINT: comps = Even(32, 3); kind = 'u'; break;
                case DXGI_FORMAT.R32G32B32_SINT: comps = Even(32, 3); kind = 'i'; break;
                case DXGI_FORMAT.R32G32_FLOAT: comps = Even(32, 2); kind = 'f'; break;
                case DXGI_FORMAT.R32G32_UINT: comps = Even(32, 2); kind = 'u'; break;
                case DXGI_FORMAT.R32G32_SINT: comps = Even(32, 2); kind = 'i'; break;
                case DXGI_FORMAT.R32_FLOAT: comps = Even(32, 1); kind = 'f'; break;
                case DXGI_FORMAT.R32_UINT: comps = Even(32, 1); kind = 'u'; break;
                case DXGI_FORMAT.R32_SINT: comps = Even(32, 1); kind = 'i'; break;
                case DXGI_FORMAT.R16G16B16A16_FLOAT: comps = Even(16, 4); kind = 'h'; break;
                case DXGI_FORMAT.R16G16B16A16_UNORM: comps = Even(16, 4); kind = 'n'; break;
                case DXGI_FORMAT.R16G16B16A16_UINT: comps = Even(16, 4); kind = 'u'; break;
                case DXGI_FORMAT.R16G16B16A16_SNORM: comps = Even(16, 4); kind = 's'; break;
                case DXGI_FORMAT.R16G16B16A16_SINT: comps = Even(16, 4); kind = 'i'; break;
                case DXGI_FORMAT.R16G16_FLOAT: comps = Even(16, 2); kind = 'h'; break;
                case DXGI_FORMAT.R16G16_UNORM: comps = Even(16, 2); kind = 'n'; break;
                case DXGI_FORMAT.R16G16_UINT: comps = Even(16, 2); kind = 'u'; break;
                case DXGI_FORMAT.R16G16_SNORM: comps = Even(16, 2); kind = 's'; break;
                case DXGI_FORMAT.R16G16_SINT: comps = Even(16, 2); kind = 'i'; break;
                case DXGI_FORMAT.R16_FLOAT: comps = Even(16, 1); kind = 'h'; break;
                case DXGI_FORMAT.R16_UNORM: comps = Even(16, 1); kind = 'n'; break;
                case DXGI_FORMAT.R16_UINT: comps = Even(16, 1); kind = 'u'; break;
                case DXGI_FORMAT.R16_SNORM: comps = Even(16, 1); kind = 's'; break;
                case DXGI_FORMAT.R16_SINT: comps = Even(16, 1); kind = 'i'; break;
                case DXGI_FORMAT.R8G8B8A8_UNORM: case DXGI_FORMAT.R8G8B8A8_UNORM_SRGB: comps = Even(8, 4); kind = 'n'; break;
                case DXGI_FORMAT.R8G8B8A8_UINT: comps = Even(8, 4); kind = 'u'; break;
                case DXGI_FORMAT.R8G8B8A8_SNORM: comps = Even(8, 4); kind = 's'; break;
                case DXGI_FORMAT.R8G8B8A8_SINT: comps = Even(8, 4); kind = 'i'; break;
                case DXGI_FORMAT.R8G8_UNORM: comps = Even(8, 2); kind = 'n'; break;
                case DXGI_FORMAT.R8G8_UINT: comps = Even(8, 2); kind = 'u'; break;
                case DXGI_FORMAT.R8G8_SNORM: comps = Even(8, 2); kind = 's'; break;
                case DXGI_FORMAT.R8G8_SINT: comps = Even(8, 2); kind = 'i'; break;
                case DXGI_FORMAT.R8_UNORM: comps = Even(8, 1); kind = 'n'; break;
                case DXGI_FORMAT.R8_UINT: comps = Even(8, 1); kind = 'u'; break;
                case DXGI_FORMAT.R8_SNORM: comps = Even(8, 1); kind = 's'; break;
                case DXGI_FORMAT.R8_SINT: comps = Even(8, 1); kind = 'i'; break;
                case DXGI_FORMAT.B8G8R8A8_UNORM: comps = Even(8, 4); kind = 'n'; bgra = true; break;
                case DXGI_FORMAT.B8G8R8X8_UNORM: comps = Even(8, 4); kind = 'n'; bgra = true; forceW = true; break;
                case DXGI_FORMAT.R10G10B10A2_UNORM: comps = new[] { (0, 10), (10, 10), (20, 10), (30, 2) }; kind = 'n'; break;
                case DXGI_FORMAT.R10G10B10A2_UINT: comps = new[] { (0, 10), (10, 10), (20, 10), (30, 2) }; kind = 'u'; break;
                default: throw new NotSupportedException($"vertex element format {f} in a vertex shader run as compute");
            }
            bool integer = kind is 'u' or 'i';
            var c = new string[4];
            for (int i = 0; i < 4; i++)
            {
                int src = bgra && i < 3 ? 2 - i : i;
                if (src >= comps.Length || (forceW && i == 3)) { c[i] = integer ? (i == 3 ? "bitcast<f32>(1u)" : "bitcast<f32>(0u)") : (i == 3 ? "1.0f" : "0.0f"); continue; }
                var (off, bits) = comps[src];
                string raw = bits == 32 ? $"{word}({a} + {off / 32 * 4}u)" : $"(({word}({a} + {off / 32 * 4}u) >> {off % 32}u) & {(1u << bits) - 1}u)";
                string signed = bits == 32 ? $"bitcast<i32>({raw})" : $"(bitcast<i32>({raw} << {32 - bits}u) >> {32 - bits}u)";
                c[i] = kind switch
                {
                    'f' => $"bitcast<f32>({raw})",
                    'h' => $"bitcast<f32>(dx_f16tof32({raw}))",
                    'n' => $"(f32({raw}) / {(1u << bits) - 1}.0f)",
                    's' => $"max(f32({signed}) / {(1u << (bits - 1)) - 1}.0f, -1.0f)",
                    'u' => $"bitcast<f32>({raw})",
                    _ => $"bitcast<f32>({signed})",
                };
            }
            return $"vec4<f32>({c[0]}, {c[1]}, {c[2]}, {c[3]})";
        }

        // ---- the geometry shader as a compute kernel ---------------------------------------------------

        private void AssembleGeometryCompute(StringBuilder w)
        {
            var g = _geometry!;
            int regs = OutputRegisters, vsRegs = Math.Max(1, _upstreamRegisters), per = g.IndicesPerInvocation, max = Math.Max(1, g.MaxVertices);
            int inRegs = _gsInputRegs.Count == 0 ? 1 : _gsInputRegs.Max() + 1;
            w.Append($"@group({PlumbingGroup}) @binding({PlumbingParams}) var<uniform> dx_p: array<vec4<u32>, {ParamVec4s}>;\n");
            w.Append($"@group({PlumbingGroup}) @binding({PlumbingVsOut}) var<storage, read> dx_vsout: array<vec4<f32>>;\n");
            w.Append($"@group({PlumbingGroup}) @binding({PlumbingGsOut}) var<storage, read_write> dx_gsout: array<vec4<f32>>;\n");
            w.Append($"@group({PlumbingGroup}) @binding({PlumbingGsIndices}) var<storage, read_write> dx_gsidx: array<u32>;\n");
            w.Append($"var<private> dx_gv: array<array<vec4<f32>, {inRegs}>, {g.InputVertices}>;\n");
            w.Append("var<private> dx_n: u32;\nvar<private> dx_k: u32;\nvar<private> dx_vbase: u32;\nvar<private> dx_ibase: u32;\nvar<private> dx_open: bool;\n\n");

            // emit: the output registers into the invocation's next vertex, its index into the segment
            w.Append($"fn dx_emit() {{\n    if (dx_n >= {max}u) {{ return; }}\n    let b = (dx_vbase + dx_n) * {regs}u;\n");
            foreach (int r in _outputRegs.OrderBy(r => r)) w.Append($"    dx_gsout[b + {r}u] = {OutReg(r)};\n");
            w.Append("    dx_gsidx[dx_ibase + dx_k] = dx_vbase + dx_n;\n    dx_k = dx_k + 1u;\n    dx_n = dx_n + 1u;\n    dx_open = true;\n}\n");
            w.Append(g.Strip
                ? "fn dx_cut() {\n    if (dx_open) { dx_gsidx[dx_ibase + dx_k] = 0xFFFFFFFFu; dx_k = dx_k + 1u; dx_open = false; }\n}\n"
                : "fn dx_cut() { }\n");
            w.Append("\nfn dx_body() {\n").Append(_body).Append("}\n\n");

            w.Append("@compute @workgroup_size(64)\nfn main(@builtin(global_invocation_id) gid: vec3<u32>) {\n");
            w.Append($"    let i = gid.x + gid.y * {InvocationsPerRow}u;\n    if (i >= dx_p[3].x) {{ return; }}\n");
            w.Append($"    let gsi = i % {g.Instances}u;\n    let rest = i / {g.Instances}u;\n    let prims = max(dx_p[2].y, 1u);\n    let prim = rest % prims;\n    let inst = rest / prims;\n");
            w.Append($"    dx_vbase = i * {max}u;\n    dx_ibase = i * {per}u;\n    let first = inst * dx_p[0].x;\n");
            // the primitive's vertices: lists step by the primitive's size, strips by one, a triangle
            // strip's odd triangles come as (n, n+2, n+1) (D3D's order, measured on WARP)
            for (int k = 0; k < g.InputVertices; k++)
            {
                w.Append($"    {{\n        var o = prim * dx_p[2].w + {k}u;\n");
                w.Append($"        if (dx_p[2].z != 0u) {{ o = prim + {k}u; }}\n");
                if (k > 0) w.Append($"        if (dx_p[2].z == 2u && (prim & 1u) == 1u) {{ o = prim + {3 - k}u; }}\n");
                w.Append($"        let b = (first + o) * {vsRegs}u;\n");
                foreach (var e in _s.Inputs.Where(e => _gsInputRegs.Contains(e.Register)))
                {
                    if (e.SystemValue == SvPrimitiveId || e.SemanticName.Equals("SV_PrimitiveID", StringComparison.OrdinalIgnoreCase)) continue;
                    // by semantic from the vertex shader's outputs (no vertex shader: register for register)
                    var up = _upstream == null ? e : _upstream.Outputs.FirstOrDefault(u => u.SemanticName.Equals(e.SemanticName, StringComparison.OrdinalIgnoreCase) && u.SemanticIndex == e.SemanticIndex);
                    if (up == null) continue;                      // D3D leaves an unwritten input undefined; it reads 0 here
                    var from = Enumerable.Range(0, 4).Where(c => (up.Mask & (1 << c)) != 0).ToList();
                    int j = 0;
                    for (int c = 0; c < 4; c++)
                        if ((e.Mask & (1 << c)) != 0 && j < from.Count)
                            w.Append($"        dx_gv[{k}][{e.Register}].{"xyzw"[c]} = dx_vsout[b + {up.Register}u].{"xyzw"[from[j++]]};\n");
                }
                w.Append("    }\n");
            }
            if (_builtins.Contains("vPrim")) w.Append("    vPrim = bitcast<vec4<f32>>(vec4<u32>(prim, 0u, 0u, 0u));\n");
            if (_builtins.Contains("vGSInstanceID")) w.Append("    vGSInstanceID = bitcast<vec4<f32>>(vec4<u32>(gsi, 0u, 0u, 0u));\n");
            w.Append("    dx_body();\n");
            w.Append($"    for (var j = dx_k; j < {per}u; j = j + 1u) {{ dx_gsidx[dx_ibase + j] = 0xFFFFFFFFu; }}\n}}\n");
        }

        // ---- the pass-through vertex shader of the draw ---------------------------------------------------

        private void AssemblePassThrough(StringBuilder w)
        {
            int regs = OutputRegisters;
            w.Append("@group(0) @binding(0) var<storage, read> dx_gsout: array<vec4<f32>>;\n\n");
            foreach (int o in _outputRegs) w.Append($"var<private> o{o}: vec4<f32>;\n");
            w.Append('\n');
            AppendVertexOutput(w);
            w.Append("@vertex\nfn main(@builtin(vertex_index) vid: u32) -> VsOut {\n");
            // an unused index of a point list: a vertex outside the clip volume
            w.Append("    if (vid == 0xFFFFFFFFu) { var dead: VsOut; dead.pos = vec4<f32>(2.0f, 2.0f, 2.0f, 1.0f); return dead; }\n");
            foreach (int r in _outputRegs.OrderBy(r => r)) w.Append($"    o{r} = dx_gsout[vid * {regs}u + {r}u];\n");
            w.Append("    return shader_out();\n}\n");
        }
    }
}
