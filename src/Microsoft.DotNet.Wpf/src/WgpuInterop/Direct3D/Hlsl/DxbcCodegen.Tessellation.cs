// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The shim's HLSL compiler: hull and domain shaders.
//
// A hull shader is phases, each with declarations of its own, after the shader's: the control point
// phase (the entry point, once per output control point: v[N][r] in, o# out, vOutputControlPointID)
// and a fork phase (the patch constant function: the input patch as vicp[N][r], the output patch as
// vocp[N][r], the patch constants out -- the tessellation factors each a register of its own, with
// dcl_output_siv). Its signatures: ISGN the control points in, OSGN out, PCSG the patch constants.
//
// A domain shader reads the patch constants (vpc#, PCSG), the domain location (vDomain) and the
// output control points (vicp[N][r], ISGN).
//

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Wpf.Interop.WebGpu.Direct3D.Dxbc;

namespace Microsoft.Wpf.Interop.WebGpu.Direct3D.Hlsl
{
    internal sealed partial class DxbcCodegen
    {
        private string _domain = "tri";
        private int _partitioning = 1, _outputPrimitive = 3, _inputCps, _outputCps;
        private float? _maxTessFactor;
        private FunctionDecl? _patchFunction;
        /// <summary>A hull shader's phase being generated: 0 the control points', 1 the fork phase.</summary>
        private int _hsPhase;
        private readonly List<SigEntry> _patchConstants = new();
        private bool _forkReadsInputPatch, _forkReadsOutputPatch;

        private string? StringAttr(FunctionDecl f, string name)
        {
            var a = f.Attributes.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (a == null || a.Args.Count == 0) return null;
            return (a.Args[0] as LiteralExpr)?.Text;
        }

        private void TessellationAttributes(FunctionDecl f)
        {
            _domain = (StringAttr(f, "domain") ?? throw Err(f, $"a {(_stage == ShaderStage.Hull ? "hull" : "domain")} shader needs [domain(...)]", 3518)).ToLowerInvariant();
            if (_domain is not ("tri" or "quad" or "isoline")) throw Err(f, $"'{_domain}' is not a domain", 3518);
            if (_stage == ShaderStage.Domain) return;
            _partitioning = (StringAttr(f, "partitioning") ?? throw Err(f, "a hull shader needs [partitioning(...)]", 3518)).ToLowerInvariant() switch
            {
                "integer" => 1, "pow2" => 2, "fractional_odd" => 3, "fractional_even" => 4, var p => throw Err(f, $"'{p}' is not a partitioning", 3518),
            };
            _outputPrimitive = (StringAttr(f, "outputtopology") ?? throw Err(f, "a hull shader needs [outputtopology(...)]", 3518)).ToLowerInvariant() switch
            {
                "point" => 1, "line" => 2, "triangle_cw" => 3, "triangle_ccw" => 4, var p => throw Err(f, $"'{p}' is not an output topology", 3518),
            };
            var cps = f.Attributes.FirstOrDefault(x => x.Name.Equals("outputcontrolpoints", StringComparison.OrdinalIgnoreCase)) ?? throw Err(f, "a hull shader needs [outputcontrolpoints(n)]", 3518);
            _outputCps = (int)ConstInt(cps.Args[0]);
            string pcf = StringAttr(f, "patchconstantfunc") ?? throw Err(f, "a hull shader needs [patchconstantfunc(\"...\")]", 3518);
            _patchFunction = _functions.TryGetValue(pcf, out var list) ? list.FirstOrDefault(x => x.Body != null) : null;
            if (_patchFunction == null) throw Err(f, $"the patch constant function '{pcf}' is not defined", 3518);
            var max = f.Attributes.FirstOrDefault(x => x.Name.Equals("maxtessfactor", StringComparison.OrdinalIgnoreCase));
            if (max != null && max.Args[0] is LiteralExpr ml) _maxTessFactor = (float)(ml.IsFloat ? ml.FloatValue : ml.IntValue);
        }

        /// <summary>The system value of a tessellation factor (D3D_NAME), by the domain.</summary>
        private int TessFactorName(string name, int index) => (name.ToUpperInvariant(), _domain) switch
        {
            ("SV_TESSFACTOR", "tri") => 13, ("SV_INSIDETESSFACTOR", "tri") => 14,
            ("SV_TESSFACTOR", "quad") => 11, ("SV_INSIDETESSFACTOR", "quad") => 12,
            ("SV_TESSFACTOR", "isoline") => index == 0 ? 16 : 15,
            _ => 0,
        };

        private static bool IsTessFactor(int sv) => sv is >= 11 and <= 16;

        /// <summary>A tessellation factor's name in a declaration (D3D10_SB_NAME): each edge its own.</summary>
        private int TessFactorDeclName(SigEntry e) => (e.SystemValue, _domain) switch
        {
            (13, _) => 17 + e.Index, (14, _) => 20,
            (11, _) => 11 + e.Index, (12, _) => 15 + e.Index,
            (16, _) => 22, (15, _) => 21,
            _ => e.SystemValue,
        };

        /// <summary>A value's input or output registers moved to another file (the control points, the patch constants).</summary>
        private static Value Retarget(Value v, OperandType file)
        {
            switch (v)
            {
                case NumValue nv when nv.N.File is OperandType.Input or OperandType.Output:
                {
                    var n = nv.N.WithType(nv.N.Type);
                    n.File = file;
                    return new NumValue { Type = nv.Type, N = n };
                }
                case AggValue a: return new AggValue { Type = a.Type, Parts = a.Parts.Select(p => Retarget(p, file)).ToArray() };
                default: return v;
            }
        }

        /// <summary>A hull or domain shader's parameter: a patch, or the domain shader's patch constants; null for the others.</summary>
        private Value? TessellationParam(ParamDecl p, HlslType type)
        {
            if (_stage is not (ShaderStage.Hull or ShaderStage.Domain) || p.IsOut) return null;
            if (type is ObjectType { IsPatch: true } pt)
            {
                var elem = pt.Element ?? throw Err(p, "a patch needs a control point type", 3518);
                int n = pt.Count;
                bool output = pt.Name == "OutputPatch";
                OperandType file;
                // the control points the shader's ISGN describes bind as signature elements; the others
                // (the fork phase's patches) take the same registers in a list of their own
                bool signature;
                if (_stage == ShaderStage.Domain) { file = OperandType.InputControlPoint; signature = true; _inputCps = n; }
                else if (_hsPhase == 0 && !output) { file = OperandType.Input; signature = true; _inputCps = n; }
                else
                {
                    file = output ? OperandType.OutputControlPoint : OperandType.InputControlPoint;
                    signature = false;
                    if (output) _forkReadsOutputPatch = true; else _forkReadsInputPatch = true;
                }
                Value one;
                if (signature) one = BindSignature(elem, p.Semantic, p.Modifiers, false, p, null);
                else
                {
                    var saved = _inputs.ToList();
                    _inputs.Clear();
                    one = BindSignature(elem, p.Semantic, p.Modifiers, false, p, null);
                    _inputs.Clear();
                    _inputs.AddRange(saved);
                }
                return new AggValue { Type = new ArrayType(elem, n), Parts = Enumerable.Range(0, n).Select(i => Retarget(OfVertex(one, i), file)).ToArray() };
            }
            if (_stage == ShaderStage.Domain && !IsSpecialSemantic(p.Semantic))
            {
                // the patch constants: inputs of the PCSG, vpc#
                var saved = _inputs.ToList();
                _inputs.Clear();
                _inputs.AddRange(_patchConstants);
                var v = BindSignature(type, p.Semantic, p.Modifiers, false, p, null);
                _patchConstants.Clear();
                _patchConstants.AddRange(_inputs);
                _inputs.Clear();
                _inputs.AddRange(saved);
                return Retarget(v, OperandType.InputPatchConstant);
            }
            return null;
        }

        private static bool IsSpecialSemantic(string? s) => s != null && s.ToUpperInvariant() is "SV_DOMAINLOCATION" or "SV_PRIMITIVEID" or "SV_OUTPUTCONTROLPOINTID";

        // ---- the hull shader's program -------------------------------------------------------------------

        /// <summary>The hull shader: both phases generated (once to learn what is used, once to emit).</summary>
        private byte[] CompileHull(FunctionDecl f)
        {
            TessellationAttributes(f);
            GenerateHull(f, record: true);
            AssignRegisters();
            Reset();
            var (cpCode, forkCode) = GenerateHull(f, record: false);
            _w.Code.Clear();
            _w.Declare(Opcode.HsDecls, 0);
            _w.Declare(Opcode.DclInputControlPointCount, (uint)_inputCps);
            _w.Declare(Opcode.DclOutputControlPointCount, (uint)_outputCps);
            _w.Declare(Opcode.DclTessDomain, _domain switch { "isoline" => 1u, "tri" => 2u, _ => 3u });
            _w.Declare(Opcode.DclTessPartitioning, (uint)_partitioning);
            _w.Declare(Opcode.DclTessOutputPrimitive, (uint)_outputPrimitive);
            if (_maxTessFactor is float m) _w.Declare(Opcode.DclHsMaxTessFactor, 0, BitConverter.SingleToUInt32Bits(m));
            DeclareGlobals();
            _w.Code.AddRange(cpCode);
            _w.Code.AddRange(forkCode);
            var chunks = new List<(string, byte[])>
            {
                ("RDEF", Rdef()),
                ("ISGN", Signature(_inputs, false)),
                ("OSGN", Signature(_outputs, true)),
                ("PCSG", Signature(_patchConstants, true)),
                ("SHEX", _w.Program()),
                ("STAT", Stat()),
            };
            return DxbcWriter.Container(chunks);
        }

        /// <summary>Each phase's instructions with its declarations in front.</summary>
        private (List<uint> ControlPoints, List<uint> Fork) GenerateHull(FunctionDecl f, bool record)
        {
            // the control point phase: the entry point
            _hsPhase = 0;
            _w.Code.Clear();
            Generate(f, record);
            var body = _w.Code.ToList();
            _w.Code.Clear();
            _w.Declare(Opcode.HsControlPointPhase, 0);
            DeclareSpecialInputs();
            foreach (var e in _inputs) _w.Emit(Opcode.DclInput, 0, DxOperand.Dest(OperandType.Input, e.Mask, _inputCps, e.Register));
            DeclareOutputs(_outputs);
            if (_temps > 0) _w.Declare(Opcode.DclTemps, 0, (uint)_temps);
            foreach (var (x, size, comps) in _indexables) _w.Declare(Opcode.DclIndexableTemp, 0, (uint)x, (uint)size, (uint)comps);
            var cp = _w.Code.Concat(body).ToList();

            // the fork phase: the patch constant function, its outputs the patch constants
            ResetPhase();
            _hsPhase = 1;
            _forkReadsInputPatch = _forkReadsOutputPatch = false;
            var outputs = _outputs.ToList();
            _outputs.Clear();
            _patchConstants.Clear();
            Generate(_patchFunction!, record);
            _patchConstants.AddRange(_outputs);
            _outputs.Clear();
            _outputs.AddRange(outputs);
            body = _w.Code.ToList();
            _w.Code.Clear();
            _w.Declare(Opcode.HsForkPhase, 0);
            DeclareSpecialInputs();
            if (_forkReadsInputPatch)
                foreach (var e in _inputs) _w.Emit(Opcode.DclInput, 0, DxOperand.Dest(OperandType.InputControlPoint, e.Mask, _inputCps, e.Register));
            if (_forkReadsOutputPatch)
                foreach (var e in _outputs) _w.Emit(Opcode.DclInput, 0, DxOperand.Dest(OperandType.OutputControlPoint, e.Mask, _outputCps, e.Register));
            foreach (var e in _patchConstants)
            {
                var o = DxOperand.Dest(OperandType.Output, e.Mask, e.Register);
                if (IsTessFactor(e.SystemValue)) _w.EmitWith(Opcode.DclOutputSiv, 0, null, new[] { o }, new[] { (uint)TessFactorDeclName(e) });
                else _w.Emit(Opcode.DclOutput, 0, o);
            }
            if (_temps > 0) _w.Declare(Opcode.DclTemps, 0, (uint)_temps);
            foreach (var (x, size, comps) in _indexables) _w.Declare(Opcode.DclIndexableTemp, 0, (uint)x, (uint)size, (uint)comps);
            var fork = _w.Code.Concat(body).ToList();
            _hsPhase = 0;
            return (cp, fork);
        }

        /// <summary>What one phase's generation leaves behind, cleared for the next.</summary>
        private void ResetPhase()
        {
            _w.Code.Clear();
            _temps = 0;
            _indexables.Clear();
            _specialInputs.Clear();
            _scopes.Clear();
        }

        /// <summary>A domain shader's declarations before the globals, and its inputs.</summary>
        private void DomainDeclarations()
        {
            foreach (var e in _patchConstants)
            {
                var o = DxOperand.Dest(OperandType.InputPatchConstant, e.Mask, e.Register);
                if (IsTessFactor(e.SystemValue)) _w.EmitWith(Opcode.DclInputSiv, 0, null, new[] { o }, new[] { (uint)TessFactorDeclName(e) });
                else _w.Emit(Opcode.DclInput, 0, o);
            }
            DeclareSpecialInputs();
            foreach (var e in _inputs) _w.Emit(Opcode.DclInput, 0, DxOperand.Dest(OperandType.InputControlPoint, e.Mask, _inputCps, e.Register));
        }
    }
}
