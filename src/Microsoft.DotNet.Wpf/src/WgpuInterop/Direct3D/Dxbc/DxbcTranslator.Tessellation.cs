// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Hull and domain shaders (Documentation/d3d11-webgpu-shim.md, sections 6 and 9). WebGPU has no
// tessellation stages, so a draw with HS and DS bound runs as
//
//   1. the vertex shader as compute over the patches' control points (DxbcTranslator.Geometry.cs);
//   2. the hull shader as two compute kernels in one module: cp_main, one invocation per output
//      control point (the control-point phase, or a copy of the input when the shader has none),
//      and pc_main, one invocation per patch, running every fork phase (each instance in turn) and
//      then every join phase into the patch-constant registers;
//   3. the fixed-function tessellator on the CPU (D3DTessellator, the port of the D3D11 reference)
//      over the tess factors read back from (2): domain points and indices;
//   4. the domain shader as the vertex shader of an indexed draw of those points, reading its
//      control points and patch constants from (2), and handing its outputs to the pixel shader
//      by the usual linkage.
//
// The kernels' plumbing is group 1, as for the geometry path, plus
//   5 the output control points (cp_main writes them)   6 the patch constants (pc_main writes them)
// The domain shader's plumbing is group 2 of its draw: 0 the domain points, 1 the control points,
// 2 the patch constants.
//

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Microsoft.Wpf.Interop.WebGpu.Direct3D.Dxbc
{
    /// <summary>A hull shader's declarations: the patch, the tessellator's mode and where its tess
    /// factors are in the patch-constant registers.</summary>
    internal sealed class HullInfo
    {
        /// <summary>D3D11_SB_TESSELLATOR_DOMAIN: 1 isoline, 2 triangle, 3 quad.</summary>
        public int Domain;
        public TessPartitioning Partitioning;
        public TessOutput Output;
        public int InputControlPoints, OutputControlPoints;
        public bool HasControlPointPhase;
        /// <summary>D3D10_SB_NAME of each final tess factor (11..22) -> (register, component).</summary>
        public readonly Dictionary<int, (int Register, int Component)> Factors = new();

        public static HullInfo Of(DxbcShader hs)
        {
            var h = new HullInfo();
            bool forkOrJoin = false;
            foreach (var ins in hs.Instructions)
            {
                switch (ins.Op)
                {
                    case Opcode.DclInputControlPointCount: h.InputControlPoints = (int)ins.Count; break;
                    case Opcode.DclOutputControlPointCount: h.OutputControlPoints = (int)ins.Count; break;
                    case Opcode.DclTessDomain: h.Domain = (int)(ins.Control & 0x3); break;
                    case Opcode.DclTessPartitioning: h.Partitioning = (TessPartitioning)Math.Max(0, (int)(ins.Control & 0x7) - 1); break;
                    case Opcode.DclTessOutputPrimitive: h.Output = (TessOutput)Math.Max(0, (int)(ins.Control & 0x7) - 1); break;
                    case Opcode.HsControlPointPhase: h.HasControlPointPhase = true; forkOrJoin = false; break;
                    case Opcode.HsForkPhase: case Opcode.HsJoinPhase: forkOrJoin = true; break;
                    case Opcode.DclOutputSiv when forkOrJoin && ins.SystemValue is >= 11 and <= 22:
                    {
                        var o = ins.Operands[0];
                        int c = 0;
                        while (c < 3 && (o.Mask & (1 << c)) == 0) c++;
                        h.Factors[ins.SystemValue] = (o.Index0, c);
                        break;
                    }
                }
            }
            if (h.OutputControlPoints == 0) h.OutputControlPoints = h.InputControlPoints;
            return h;
        }

        // D3D10_SB_NAME of the final tess factors
        public const int QuadUeq0 = 11, QuadVeq0 = 12, QuadUeq1 = 13, QuadVeq1 = 14, QuadInsideU = 15, QuadInsideV = 16,
            TriUeq0 = 17, TriVeq0 = 18, TriWeq0 = 19, TriInside = 20, LineDetail = 21, LineDensity = 22;
    }

    internal sealed partial class DxbcTranslator
    {
        public const int PlumbingCpOut = 5, PlumbingPcOut = 6;
        public const int DomainGroup = 2, DomainPoints = 0, DomainCps = 1, DomainPcs = 2, DomainOut = 3;

        private HullInfo? _hull;
        private DxbcShader? _hullOf;                     // the domain form: the hull shader before it
        private int _cpRegs, _pcRegs;
        // the hull shader's phases: kind (0 control point, 1 fork, 2 join), body, instance count
        private readonly List<(int Kind, StringBuilder Body, int Instances)> _phases = new();
        private readonly List<int> _phaseInstances = new();
        private int _phase = -1, _declPhase = -1;
        private readonly SortedSet<int> _cpOutputRegs = new(), _pcOutputRegs = new();

        /// <summary>The hull shader's output control point registers (the stride of its control-point storage).</summary>
        public int ControlPointRegisters => _cpRegs;
        /// <summary>The patch-constant registers (the stride of the patch-constant storage).</summary>
        public int PatchConstantRegisters => _pcRegs;

        public static DxbcTranslator TranslateHull(DxbcShader hs, DxbcShader? vs, int vsRegisters, TranslationOptions? options = null)
        {
            if (hs.Stage != ShaderStage.Hull) throw new ArgumentException("not a hull shader");
            var h = HullInfo.Of(hs);
            if (h.Domain is < 1 or > 3) throw new NotSupportedException($"tessellator domain {h.Domain}");
            if (h.InputControlPoints is < 1 or > 32) throw new NotSupportedException($"{h.InputControlPoints} input control points");
            var t = new DxbcTranslator(hs, 0, Array.Empty<LinkedElement>(), options)
            {
                _form = StageForm.HullCompute, _upstream = vs, _upstreamRegisters = vsRegisters, _hull = h,
            };
            t.Run();
            return t;
        }

        /// <summary>The domain shader as the vertex shader of the tessellated draw. <paramref name="cpRegs"/>
        /// and <paramref name="pcRegs"/> are the hull shader's strides (its translation's).</summary>
        public static DxbcTranslator TranslateDomain(DxbcShader ds, DxbcShader? hs, int cpRegs, int pcRegs, IReadOnlyList<LinkedElement> link, TranslationOptions? options = null)
            => TranslateDomain(ds, hs, cpRegs, pcRegs, link, compute: false, options);

        /// <summary>The domain shader as a vertex shader (<paramref name="compute"/> false) or, for a
        /// geometry shader after it, as a compute kernel over the domain points writing its output
        /// registers (group 1: the points, control points and patch constants at 0..2, the outputs at 3).</summary>
        public static DxbcTranslator TranslateDomain(DxbcShader ds, DxbcShader? hs, int cpRegs, int pcRegs, IReadOnlyList<LinkedElement> link, bool compute, TranslationOptions? options = null)
        {
            if (ds.Stage != ShaderStage.Domain) throw new ArgumentException("not a domain shader");
            var t = new DxbcTranslator(ds, 0, link, options)
            {
                // no hull shader (a coverage run): the domain shader's own declarations, register for register
                _form = compute ? StageForm.DomainCompute : StageForm.DomainVertex, _hullOf = hs, _hull = HullInfo.Of(hs ?? ds), _cpRegs = Math.Max(1, cpRegs), _pcRegs = Math.Max(1, pcRegs),
            };
            t.Run();
            return t;
        }

        /// <summary>Declarations of a hull or domain shader: the phase they belong to, and the
        /// register files that are not v#/o#. True when handled.</summary>
        private bool DeclareTessellation(Instruction ins)
        {
            if (_s.Stage is not (ShaderStage.Hull or ShaderStage.Domain)) return false;
            switch (ins.Op)
            {
                case Opcode.HsControlPointPhase: _declPhase = 0; return true;
                case Opcode.HsForkPhase: _declPhase = 1; _phaseInstances.Add(1); return true;
                case Opcode.HsJoinPhase: _declPhase = 2; _phaseInstances.Add(1); return true;
                case Opcode.DclHsForkPhaseInstanceCount:
                case Opcode.DclHsJoinPhaseInstanceCount:
                    if (_phaseInstances.Count > 0 && ins.Extra.Length > 0) _phaseInstances[^1] = Math.Max(1, (int)ins.Extra[0]);
                    return true;
                case Opcode.DclInput: case Opcode.DclInputSgv: case Opcode.DclInputSiv:
                {
                    var o = ins.Operands[0];
                    switch (o.Type)
                    {
                        case OperandType.InputPrimitiveId: _builtins.Add("vPrim"); break;
                        case OperandType.OutputControlPointId: _builtins.Add("vOutputControlPointID"); break;
                        case OperandType.InputForkInstanceId: _builtins.Add("vForkInstanceID"); break;
                        case OperandType.InputJoinInstanceId: _builtins.Add("vJoinInstanceID"); break;
                        case OperandType.InputDomainPoint: _builtins.Add("vDomain"); break;
                    }
                    return true;                           // control points and patch constants are read through helpers
                }
                case Opcode.DclOutput: case Opcode.DclOutputSgv: case Opcode.DclOutputSiv:
                {
                    if (_s.Stage == ShaderStage.Domain) return false;   // a domain shader's outputs are a vertex shader's
                    int r = ins.Operands[0].Index0;
                    if (_declPhase == 0) { _cpOutputRegs.Add(r); _outputRegs.Add(r); }
                    else _pcOutputRegs.Add(r);
                    return true;
                }
            }
            return false;
        }

        /// <summary>A hull or domain shader's register operands (null: not one of them).</summary>
        private string? TessellationRegister(Operand o)
        {
            if (_form == StageForm.HullCompute)
            {
                switch (o.Type)
                {
                    case OperandType.Input when o.Indices.Length == 2:
                    case OperandType.InputControlPoint:
                        return $"dx_hicp({IndexExpr(o.Indices[0])}, {IndexExpr(o.Indices[1])})";
                    case OperandType.OutputControlPoint:
                        return $"dx_ocp({IndexExpr(o.Indices[0])}, {IndexExpr(o.Indices[1])})";
                    case OperandType.InputPatchConstant:
                        return $"dx_pc[{IndexExpr(o.Indices[0])}]";
                    case OperandType.Output when _phase > 0:
                        return $"dx_pc[{IndexExpr(o.Indices[0])}]";
                }
            }
            else if (_form is StageForm.DomainVertex or StageForm.DomainCompute)
            {
                switch (o.Type)
                {
                    case OperandType.InputControlPoint:
                        return $"dx_dicp({IndexExpr(o.Indices[0])}, {IndexExpr(o.Indices[1])})";
                    case OperandType.InputPatchConstant:
                        return $"dx_dpc({IndexExpr(o.Indices[0])})";
                }
            }
            switch (o.Type)
            {
                case OperandType.OutputControlPointId: _builtins.Add("vOutputControlPointID"); return "vOutputControlPointID";
                case OperandType.InputForkInstanceId: _builtins.Add("vForkInstanceID"); return "vForkInstanceID";
                case OperandType.InputJoinInstanceId: _builtins.Add("vJoinInstanceID"); return "vJoinInstanceID";
                case OperandType.InputDomainPoint: _builtins.Add("vDomain"); return "vDomain";
            }
            return null;
        }

        /// <summary>The phase markers of a hull shader: each phase's body goes to a function of its own.</summary>
        private bool EmitPhase(Instruction ins)
        {
            if (ins.Op is not (Opcode.HsDecls or Opcode.HsControlPointPhase or Opcode.HsForkPhase or Opcode.HsJoinPhase)) return false;
            if (ins.Op == Opcode.HsDecls) return true;
            int kind = ins.Op == Opcode.HsControlPointPhase ? 0 : ins.Op == Opcode.HsForkPhase ? 1 : 2;
            int instances = kind == 0 ? 1 : _phaseInstances[_phases.Count(p => p.Kind != 0)];
            var body = new StringBuilder();
            _phases.Add((kind, body, instances));
            _body = body;
            _indent = 1;
            _phase = kind;
            return true;
        }

        // ---- the hull shader's two kernels ---------------------------------------------------------------

        private void AssembleHull(StringBuilder w)
        {
            var h = _hull!;
            int vsRegs = Math.Max(1, _upstreamRegisters), inCps = h.InputControlPoints, outCps = h.OutputControlPoints;
            // with no control-point phase the output control points are the input ones, register for register
            var cpRegs = h.HasControlPointPhase ? _cpOutputRegs : new SortedSet<int>(_s.Inputs.Select(e => e.Register));
            _cpRegs = Math.Max(1, cpRegs.Count == 0 ? 0 : cpRegs.Max() + 1);
            _pcRegs = Math.Max(1, _pcOutputRegs.Count == 0 ? 0 : _pcOutputRegs.Max() + 1);
            w.Append($"@group({PlumbingGroup}) @binding({PlumbingParams}) var<uniform> dx_p: array<vec4<u32>, {ParamVec4s}>;\n");
            w.Append($"@group({PlumbingGroup}) @binding({PlumbingVsOut}) var<storage, read> dx_vsout: array<vec4<f32>>;\n");
            w.Append($"@group({PlumbingGroup}) @binding({PlumbingCpOut}) var<storage, read_write> dx_cpout: array<vec4<f32>>;\n");
            w.Append($"@group({PlumbingGroup}) @binding({PlumbingPcOut}) var<storage, read_write> dx_pcout: array<vec4<f32>>;\n");
            w.Append($"var<private> dx_pc: array<vec4<f32>, {_pcRegs}>;\nvar<private> dx_patch: u32;\nvar<private> dx_cpbase: u32;\n\n");

            // an input control point: register r of the patch's vertex c, from the vertex shader's
            // output of the same semantic (no vertex shader: register for register)
            w.Append("fn dx_hicp(c: u32, r: u32) -> vec4<f32> {\n");
            w.Append($"    let b = (dx_cpbase + min(c, {inCps - 1}u)) * {vsRegs}u;\n    switch r {{\n");
            foreach (var e in _s.Inputs.GroupBy(e => e.Register).Select(g => g.First()))
            {
                var up = _upstream == null ? e : _upstream.Outputs.FirstOrDefault(u => u.SemanticName.Equals(e.SemanticName, StringComparison.OrdinalIgnoreCase) && u.SemanticIndex == e.SemanticIndex);
                if (up != null) w.Append($"        case {e.Register}u: {{ return dx_vsout[b + {up.Register}u]; }}\n");
            }
            w.Append("        default: { return vec4<f32>(0.0f); }\n    }\n}\n");
            w.Append($"fn dx_ocp(c: u32, r: u32) -> vec4<f32> {{ return dx_cpout[(dx_patch * {outCps}u + min(c, {outCps - 1}u)) * {_cpRegs}u + min(r, {_cpRegs - 1}u)]; }}\n\n");

            int n = 0;
            foreach (var (kind, body, _) in _phases) w.Append($"fn dx_phase{n++}() {{\n").Append(body).Append("}\n\n");

            // the patch's first control point among the vertex shader's outputs: draw instance by instance
            string Start(string patch) =>
                $"    dx_patch = {patch};\n    let inst = dx_patch / max(dx_p[2].y, 1u);\n    let local = dx_patch % max(dx_p[2].y, 1u);\n" +
                $"    dx_cpbase = inst * dx_p[0].x + local * {inCps}u;\n" +
                (_builtins.Contains("vPrim") ? "    vPrim = bitcast<vec4<f32>>(vec4<u32>(local, 0u, 0u, 0u));\n" : "");

            w.Append("@compute @workgroup_size(64)\nfn cp_main(@builtin(global_invocation_id) gid: vec3<u32>) {\n");
            w.Append($"    let i = gid.x + gid.y * {InvocationsPerRow}u;\n    if (i >= dx_p[3].x) {{ return; }}\n    let cp = i % {outCps}u;\n");
            w.Append(Start($"i / {outCps}u"));
            w.Append($"    let at = (dx_patch * {outCps}u + cp) * {_cpRegs}u;\n");
            if (h.HasControlPointPhase)
            {
                if (_builtins.Contains("vOutputControlPointID")) w.Append("    vOutputControlPointID = bitcast<vec4<f32>>(vec4<u32>(cp, 0u, 0u, 0u));\n");
                int k = 0;
                foreach (var (kind, _, _) in _phases) { if (kind == 0) w.Append($"    dx_phase{k}();\n"); k++; }
                foreach (int r in _cpOutputRegs) w.Append($"    dx_cpout[at + {r}u] = {OutReg(r)};\n");
            }
            else foreach (int r in cpRegs) w.Append($"    dx_cpout[at + {r}u] = dx_hicp(cp, {r}u);\n");
            w.Append("}\n\n");

            w.Append("@compute @workgroup_size(64)\nfn pc_main(@builtin(global_invocation_id) gid: vec3<u32>) {\n");
            w.Append($"    let i = gid.x + gid.y * {InvocationsPerRow}u;\n    if (i >= dx_p[3].y) {{ return; }}\n");
            w.Append(Start("i"));
            int p = 0;
            foreach (var (kind, _, instances) in _phases)
            {
                if (kind == 1)
                    w.Append($"    for (var f = 0u; f < {instances}u; f = f + 1u) {{\n" +
                             (_builtins.Contains("vForkInstanceID") ? "        vForkInstanceID = bitcast<vec4<f32>>(vec4<u32>(f, 0u, 0u, 0u));\n" : "") +
                             $"        dx_phase{p}();\n    }}\n");
                p++;
            }
            p = 0;
            foreach (var (kind, _, instances) in _phases)
            {
                if (kind == 2)
                    w.Append($"    for (var f = 0u; f < {instances}u; f = f + 1u) {{\n" +
                             (_builtins.Contains("vJoinInstanceID") ? "        vJoinInstanceID = bitcast<vec4<f32>>(vec4<u32>(f, 0u, 0u, 0u));\n" : "") +
                             $"        dx_phase{p}();\n    }}\n");
                p++;
            }
            w.Append($"    for (var r = 0u; r < {_pcRegs}u; r = r + 1u) {{ dx_pcout[dx_patch * {_pcRegs}u + r] = dx_pc[r]; }}\n}}\n");
        }

        // ---- the domain shader as a vertex shader ------------------------------------------------------------

        private void AssembleDomain(StringBuilder w)
        {
            var h = _hull!;
            var hs = _hullOf;
            int outCps = h.OutputControlPoints;
            bool compute = _form == StageForm.DomainCompute;
            int group = compute ? PlumbingGroup : DomainGroup;
            w.Append($"@group({group}) @binding({DomainPoints}) var<storage, read> dx_dp: array<vec4<u32>>;\n");
            w.Append($"@group({group}) @binding({DomainCps}) var<storage, read> dx_cpout: array<vec4<f32>>;\n");
            w.Append($"@group({group}) @binding({DomainPcs}) var<storage, read> dx_pcout: array<vec4<f32>>;\n");
            if (compute) w.Append($"@group({group}) @binding({DomainOut}) var<storage, read_write> dx_dsout: array<vec4<f32>>;\n");
            w.Append("var<private> dx_patch: u32;\n\n");

            // a control point: the hull shader's output of the same semantic
            w.Append("fn dx_dicp(c: u32, r: u32) -> vec4<f32> {\n");
            w.Append($"    let b = (dx_patch * {outCps}u + min(c, {outCps - 1}u)) * {_cpRegs}u;\n    switch r {{\n");
            var hsCps = hs == null ? _s.Inputs : h.HasControlPointPhase ? hs.Outputs : hs.Inputs;
            foreach (var e in _s.Inputs.GroupBy(e => e.Register).Select(g => g.First()))
            {
                var up = hsCps.FirstOrDefault(u => u.SemanticName.Equals(e.SemanticName, StringComparison.OrdinalIgnoreCase) && u.SemanticIndex == e.SemanticIndex);
                if (up != null && up.Register < _cpRegs) w.Append($"        case {e.Register}u: {{ return dx_cpout[b + {up.Register}u]; }}\n");
            }
            w.Append("        default: { return vec4<f32>(0.0f); }\n    }\n}\n");
            // a patch constant: the hull shader's patch-constant output of the same semantic
            w.Append("fn dx_dpc(r: u32) -> vec4<f32> {\n");
            w.Append($"    let b = dx_patch * {_pcRegs}u;\n    switch r {{\n");
            foreach (var e in _s.PatchConstants.GroupBy(e => e.Register).Select(g => g.First()))
            {
                var up = (hs ?? _s).PatchConstants.FirstOrDefault(u => u.SemanticName.Equals(e.SemanticName, StringComparison.OrdinalIgnoreCase) && u.SemanticIndex == e.SemanticIndex);
                if (up != null && up.Register < _pcRegs) w.Append($"        case {e.Register}u: {{ return dx_pcout[b + {up.Register}u]; }}\n");
            }
            w.Append("        default: { return vec4<f32>(0.0f); }\n    }\n}\n\n");

            if (compute)
            {
                w.Append("fn dx_body() {\n").Append(_body).Append("}\n\n");
                w.Append($"@compute @workgroup_size(64)\nfn main(@builtin(global_invocation_id) gid: vec3<u32>) {{\n    let vid = gid.x + gid.y * {InvocationsPerRow}u;\n    if (vid >= arrayLength(&dx_dp)) {{ return; }}\n    let d = dx_dp[vid];\n    dx_patch = d.z;\n");
            }
            else
            {
                AppendVertexOutput(w);
                w.Append("@vertex\nfn main(@builtin(vertex_index) vid: u32) -> VsOut {\n    let d = dx_dp[vid];\n    dx_patch = d.z;\n");
            }
            if (_builtins.Contains("vDomain"))
            {
                string third = h.Domain == 2 ? "1.0f - bitcast<f32>(d.x) - bitcast<f32>(d.y)" : "0.0f";
                w.Append($"    vDomain = vec4<f32>(bitcast<f32>(d.x), bitcast<f32>(d.y), {third}, 0.0f);\n");
            }
            if (_builtins.Contains("vPrim")) w.Append("    vPrim = bitcast<vec4<f32>>(vec4<u32>(d.w, 0u, 0u, 0u));\n");
            if (compute)
            {
                w.Append("    dx_body();\n");
                int regs = OutputRegisters;
                foreach (int r in _outputRegs.OrderBy(r => r)) w.Append($"    dx_dsout[vid * {regs}u + {r}u] = {OutReg(r)};\n");
                w.Append("}\n");
                return;
            }
            w.Append(_body);
            w.Append("    return shader_out();\n}\n");
        }
    }
}
