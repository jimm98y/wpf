// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Translates a decoded DXBC vertex or pixel shader (shader model 4/5) to WGSL
// (Documentation/d3d11-webgpu-shim.md, section 6). Follows D3D9ShaderTranslator's shape: walk the
// instruction list once, keep D3D's register files as WGSL variables, emit one statement per
// instruction.
//
// THE REGISTER MODEL. D3D registers are typeless 32-bit x 4. Every register file is a vec4<f32>; an
// integer instruction reinterprets its sources with bitcast and writes its result back as bits, so a
// value that went through integer arithmetic keeps its exact bit pattern.
//
// LINKING. D3D matches a vertex shader's outputs to a pixel shader's inputs by semantic, not by
// register, and two semantics may share a register (TEXCOORD0 in .xy, TEXCOORD1 in .zw). WGSL
// matches by location. So a pair is linked here: each pixel-shader input element (not a system
// value) is location k, in the pixel shader's own order; the vertex shader writes, for each, the
// components of its own element of that semantic, from x up; the pixel shader reads location k back
// into its own register's components.
//
// BINDINGS. A stage's resources are one bind group (group = stage slot): constant buffer b# at
// binding #, sampler s# at 16 + #, shader resource t# at 32 + #.
//
// Anything not translated throws NotSupportedException naming it; the caller logs it once and the
// draw is skipped, the way a real device rejects a shader it cannot run.
//

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Microsoft.Wpf.Interop.WebGpu.Direct3D.Dxbc
{
    internal sealed class ShaderBindings
    {
        /// <summary>Constant buffer slot -> size in vec4s.</summary>
        public readonly SortedDictionary<int, int> ConstantBuffers = new();
        /// <summary>Sampler slot -> comparison sampler.</summary>
        public readonly SortedDictionary<int, bool> Samplers = new();
        /// <summary>Resource slot -> (dimension, return kind 'f'/'i'/'u', is depth-compared).</summary>
        public readonly SortedDictionary<int, (int Dim, char Kind, bool Depth)> Resources = new();
    }

    internal sealed class LinkedElement
    {
        public string Semantic = "";
        public int Index;
        public int Location;
        public int ComponentType = 3;
        public int ComponentCount = 4;
    }

    internal sealed class DxbcTranslator
    {
        public const int SamplerBase = 16, ResourceBase = 32;

        private readonly DxbcShader _s;
        private readonly int _group;
        private readonly IReadOnlyList<LinkedElement> _link;
        private readonly StringBuilder _body = new();
        private readonly StringBuilder _decl = new();
        private readonly ShaderBindings _bindings = new();
        private int _indent = 1;
        private int _temps;
        private readonly Dictionary<int, (int Size, int Comps)> _indexable = new();
        private readonly HashSet<int> _outputRegs = new();
        private readonly HashSet<int> _inputRegs = new();
        private readonly Dictionary<int, int> _inputSv = new();     // register -> system value
        private readonly Dictionary<int, int> _outputSv = new();
        private bool _writesDepth;
        private readonly Dictionary<int, char> _resourceKind = new();
        private readonly Dictionary<int, int> _resourceDim = new();
        private readonly HashSet<int> _comparisonResources = new();
        private readonly Dictionary<int, bool> _samplerComparison = new();
        private int _tmp;

        public string Wgsl { get; private set; } = "";
        public ShaderBindings Bindings => _bindings;

        private DxbcTranslator(DxbcShader s, int group, IReadOnlyList<LinkedElement> link)
        {
            _s = s;
            _group = group;
            _link = link;
        }

        /// <summary>The pixel shader's linkage: one location per non-system-value input element.</summary>
        public static List<LinkedElement> LinkageOf(DxbcShader? pixelShader)
        {
            var list = new List<LinkedElement>();
            if (pixelShader == null) return list;
            foreach (var e in pixelShader.Inputs.OrderBy(e => e.Register).ThenBy(e => e.Mask))
            {
                if (e.SystemValue != 0) continue;
                if (list.Any(l => l.Semantic == e.SemanticName.ToUpperInvariant() && l.Index == e.SemanticIndex)) continue;
                list.Add(new LinkedElement
                {
                    Semantic = e.SemanticName.ToUpperInvariant(), Index = e.SemanticIndex, Location = list.Count,
                    ComponentType = e.ComponentType, ComponentCount = 4,
                });
            }
            return list;
        }

        public static DxbcTranslator Translate(DxbcShader s, int group, IReadOnlyList<LinkedElement> link)
        {
            if (s.Stage != ShaderStage.Vertex && s.Stage != ShaderStage.Pixel)
                throw new NotSupportedException($"{s.Stage} shaders are translated by the geometry/compute path");
            var t = new DxbcTranslator(s, group, link);
            t.Run();
            return t;
        }

        // ---- the walk ------------------------------------------------------------------------

        private void Run()
        {
            foreach (var ins in _s.Instructions) Declare(ins);
            foreach (var ins in _s.Instructions) Emit(ins);
            Wgsl = Assemble();
        }

        private void Declare(Instruction ins)
        {
            switch (ins.Op)
            {
                case Opcode.DclTemps: _temps = (int)ins.Extra[0]; break;
                case Opcode.DclIndexableTemp: _indexable[(int)ins.Extra[0]] = ((int)ins.Extra[1], (int)ins.Extra[2]); break;
                case Opcode.DclConstantBuffer:
                {
                    var o = ins.Operands[0];
                    int slot = o.Index0, size = o.Indices.Length > 1 ? o.Index1 : 4096;
                    _bindings.ConstantBuffers[slot] = Math.Max(1, size);
                    break;
                }
                case Opcode.DclSampler:
                {
                    int mode = (int)((ins.Token >> 11) & 0xF);
                    _samplerComparison[ins.Operands[0].Index0] = mode == 1;
                    break;
                }
                case Opcode.DclResource:
                {
                    int slot = ins.Operands[0].Index0;
                    _resourceDim[slot] = ins.ResourceDimension;
                    int rt = (int)(ins.ReturnType & 0xF);
                    _resourceKind[slot] = rt switch { 3 => 'i', 4 => 'u', _ => 'f' };
                    break;
                }
                case Opcode.DclInput:
                case Opcode.DclInputPs:
                    _inputRegs.Add(ins.Operands[0].Index0);
                    break;
                case Opcode.DclInputSgv:
                case Opcode.DclInputSiv:
                case Opcode.DclInputPsSgv:
                case Opcode.DclInputPsSiv:
                    _inputRegs.Add(ins.Operands[0].Index0);
                    _inputSv[ins.Operands[0].Index0] = ins.Extra.Length > 0 ? (int)ins.Extra[0] : 0;
                    break;
                case Opcode.DclOutput:
                    if (ins.Operands[0].Type == OperandType.OutputDepth) _writesDepth = true;
                    else _outputRegs.Add(ins.Operands[0].Index0);
                    break;
                case Opcode.DclOutputSgv:
                case Opcode.DclOutputSiv:
                    _outputRegs.Add(ins.Operands[0].Index0);
                    _outputSv[ins.Operands[0].Index0] = ins.Extra.Length > 0 ? (int)ins.Extra[0] : 0;
                    break;
            }
            // Operands that reach registers the declarations did not name (fxc declares them all, but
            // be tolerant).
            foreach (var o in ins.Operands)
            {
                if (o.Type == OperandType.Temp && o.Indices.Length > 0) _temps = Math.Max(_temps, o.Index0 + 1);
                if (o.Type == OperandType.OutputDepth) _writesDepth = true;
            }
        }

        // ---- operand expressions -----------------------------------------------------------------

        private static string F(float v)
        {
            if (float.IsNaN(v) || float.IsInfinity(v)) return $"bitcast<f32>({BitConverter.SingleToUInt32Bits(v)}u)";
            string s = v.ToString("R", CultureInfo.InvariantCulture);
            if (!s.Contains('.') && !s.Contains('E') && !s.Contains('e')) s += ".0";
            return s + "f";
        }

        private static string Bits(uint u) => $"bitcast<f32>({u}u)";

        /// <summary>An immediate as a vec4&lt;f32&gt; of its bits.</summary>
        private static string ImmediateVec(Operand o)
        {
            var c = new string[4];
            for (int i = 0; i < 4; i++)
            {
                uint u = o.Values.Length == 1 ? o.Values[0] : o.Values[Math.Min(i, o.Values.Length - 1)];
                float f = BitConverter.UInt32BitsToSingle(u);
                // A value that reads as an ordinary float literal is written as one (readable WGSL);
                // anything else -- integers, NaN patterns, denormals -- as its bits.
                c[i] = (u == 0 || (Math.Abs(f) >= 1e-30f && Math.Abs(f) < 1e30f && !float.IsNaN(f))) && IsPlainFloat(u) ? F(f) : Bits(u);
            }
            return $"vec4<f32>({c[0]}, {c[1]}, {c[2]}, {c[3]})";
        }

        private static bool IsPlainFloat(uint u)
        {
            float f = BitConverter.UInt32BitsToSingle(u);
            return BitConverter.SingleToUInt32Bits(float.Parse(f.ToString("R", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture)) == u;
        }

        private string IndexExpr(OperandIndex ix)
        {
            if (ix.Relative == null) return $"{ix.Immediate}u";
            string rel = $"bitcast<u32>({Source(ix.Relative)}.x)";
            return ix.Immediate == 0 ? rel : $"({rel} + {ix.Immediate}u)";
        }

        /// <summary>The register an operand names, as a vec4&lt;f32&gt; lvalue/rvalue (no swizzle).</summary>
        private string Register(Operand o)
        {
            switch (o.Type)
            {
                case OperandType.Temp: return $"r{o.Index0}";
                case OperandType.Input:
                    if (o.Indices.Length == 1 && o.Indices[0].Relative == null) return $"v{o.Index0}";
                    throw new NotSupportedException("indexed input registers");
                case OperandType.Output: return $"o{o.Index0}";
                case OperandType.IndexableTemp: return $"x{o.Index0}[{IndexExpr(o.Indices[1])}]";
                case OperandType.ConstantBuffer: return $"cb{o.Index0}[{IndexExpr(o.Indices[1])}]";
                case OperandType.ImmediateConstantBuffer: return $"icb[{IndexExpr(o.Indices[0])}]";
                case OperandType.OutputDepth: return "oDepth";
                case OperandType.InputPrimitiveId: return "vPrim";
                default: throw new NotSupportedException($"operand {o.Type}");
            }
        }

        private static string Swz(Operand o)
        {
            if (o.ComponentCount != 4 || o.IsMask) return o.ComponentCount == 1 ? ".xxxx" : "";
            var s = new char[4];
            for (int i = 0; i < 4; i++) s[i] = "xyzw"[o.Swizzle[i]];
            string sw = new string(s);
            return sw == "xyzw" ? "" : "." + sw;
        }

        /// <summary>A source operand as a vec4&lt;f32&gt; expression with its swizzle and modifier
        /// (the float modifier; integer instructions apply theirs through <see cref="ISource"/>).</summary>
        private string Source(Operand o)
        {
            string e;
            if (o.Type == OperandType.Immediate32) e = ImmediateVec(o);
            else if (o.Type == OperandType.Null) e = "vec4<f32>(0.0f)";
            else
            {
                e = Register(o) + Swz(o);
                if (o.ComponentCount == 1 && o.Type != OperandType.Immediate32) e = Register(o) + ".xxxx";
            }
            return o.Modifier switch
            {
                OperandModifier.Neg => $"(-{e})",
                OperandModifier.Abs => $"abs({e})",
                OperandModifier.AbsNeg => $"(-abs({e}))",
                _ => e,
            };
        }

        /// <summary>A source operand as vec4&lt;i32&gt; (integer negation for the neg modifier).</summary>
        private string ISource(Operand o)
        {
            var copy = new Operand { Type = o.Type, ComponentCount = o.ComponentCount, Mask = o.Mask, Swizzle = o.Swizzle, IsMask = o.IsMask, Indices = o.Indices, Values = o.Values };
            string e = $"bitcast<vec4<i32>>({Source(copy)})";
            return o.Modifier switch
            {
                OperandModifier.Neg => $"(-{e})",
                OperandModifier.Abs => $"abs({e})",
                OperandModifier.AbsNeg => $"(-abs({e}))",
                _ => e,
            };
        }

        private string USource(Operand o) => $"bitcast<vec4<u32>>({ISource(o)})";

        // ---- statements ------------------------------------------------------------------------

        private void Line(string s)
        {
            _body.Append(' ', _indent * 4).Append(s).Append('\n');
        }

        /// <summary>dest = value (a vec4&lt;f32&gt; expression), honouring the write mask and _sat.</summary>
        private void Store(Operand dest, string value, bool saturate)
        {
            if (dest.Type == OperandType.Null) return;
            if (saturate) value = $"clamp({value}, vec4<f32>(0.0f), vec4<f32>(1.0f))";
            string reg = Register(dest);
            int mask = dest.ComponentCount == 4 ? (dest.IsMask ? dest.Mask : 0xF) : 1;
            if (dest.Type == OperandType.OutputDepth) { Line($"oDepth = ({value}).x;"); return; }
            if (mask == 0xF) { Line($"{reg} = {value};"); return; }
            string t = $"t{_tmp++}";
            Line($"let {t} = {value};");
            for (int c = 0; c < 4; c++)
                if ((mask & (1 << c)) != 0) Line($"{reg}.{"xyzw"[c]} = {t}.{"xyzw"[c]};");
        }

        private void StoreI(Operand dest, string ivec, bool saturate = false) => Store(dest, $"bitcast<vec4<f32>>({ivec})", false);

        private static string Mask(string boolVec) => $"select(vec4<u32>(0u), vec4<u32>(0xFFFFFFFFu), {boolVec})";

        private string Cond(Operand o, bool nonZero)
        {
            string c = $"bitcast<u32>({Source(Strip(o))}.x)";
            return nonZero ? $"({c} != 0u)" : $"({c} == 0u)";
        }

        private static Operand Strip(Operand o) => new() { Type = o.Type, ComponentCount = o.ComponentCount, Mask = o.Mask, Swizzle = o.Swizzle, IsMask = o.IsMask, Indices = o.Indices, Values = o.Values };

        private sealed class SwitchState
        {
            public readonly List<string> Pending = new();
            public bool ClauseOpen, HasDefault;
        }

        private readonly Stack<SwitchState> _switches = new();

        private void Emit(Instruction ins)
        {
            if (_switches.Count > 0 && ins.Op is not (Opcode.Case or Opcode.Default or Opcode.EndSwitch) && _switches.Peek().Pending.Count > 0)
            {
                var sw = _switches.Peek();
                Line($"case {string.Join(", ", sw.Pending)}: {{");
                sw.Pending.Clear();
                sw.ClauseOpen = true;
                _indent++;
            }
            var o = ins.Operands;
            bool sat = ins.Saturate;
            switch (ins.Op)
            {
                // declarations: handled in Declare
                case >= Opcode.DclResource and <= Opcode.DclGlobalFlags:
                case Opcode.DclStream:
                case >= Opcode.DclInputControlPointCount and <= Opcode.DclResourceStructured:
                case Opcode.DclGsInstanceCount:
                case Opcode.Nop:
                case Opcode.Label:
                    return;

                case Opcode.Mov: Store(o[0], Source(o[1]), sat); return;
                case Opcode.Add: Store(o[0], $"({Source(o[1])} + {Source(o[2])})", sat); return;
                case Opcode.Mul: Store(o[0], $"({Source(o[1])} * {Source(o[2])})", sat); return;
                case Opcode.Mad: Store(o[0], $"fma({Source(o[1])}, {Source(o[2])}, {Source(o[3])})", sat); return;
                case Opcode.Div: Store(o[0], $"({Source(o[1])} / {Source(o[2])})", sat); return;
                case Opcode.Min: Store(o[0], $"min({Source(o[1])}, {Source(o[2])})", sat); return;
                case Opcode.Max: Store(o[0], $"max({Source(o[1])}, {Source(o[2])})", sat); return;
                case Opcode.Dp2: Store(o[0], $"vec4<f32>(dot(({Source(o[1])}).xy, ({Source(o[2])}).xy))", sat); return;
                case Opcode.Dp3: Store(o[0], $"vec4<f32>(dot(({Source(o[1])}).xyz, ({Source(o[2])}).xyz))", sat); return;
                case Opcode.Dp4: Store(o[0], $"vec4<f32>(dot({Source(o[1])}, {Source(o[2])}))", sat); return;
                case Opcode.Rsq: Store(o[0], $"inverseSqrt({Source(o[1])})", sat); return;
                case Opcode.Sqrt: Store(o[0], $"sqrt({Source(o[1])})", sat); return;
                case Opcode.Rcp: Store(o[0], $"(vec4<f32>(1.0f) / {Source(o[1])})", sat); return;
                case Opcode.Exp: Store(o[0], $"exp2({Source(o[1])})", sat); return;
                case Opcode.Log: Store(o[0], $"log2({Source(o[1])})", sat); return;
                case Opcode.Frc: Store(o[0], $"fract({Source(o[1])})", sat); return;
                case Opcode.RoundNe: Store(o[0], $"round({Source(o[1])})", sat); return;
                case Opcode.RoundNi: Store(o[0], $"floor({Source(o[1])})", sat); return;
                case Opcode.RoundPi: Store(o[0], $"ceil({Source(o[1])})", sat); return;
                case Opcode.RoundZ: Store(o[0], $"trunc({Source(o[1])})", sat); return;
                case Opcode.DerivRtx: case Opcode.DerivRtxCoarse: case Opcode.DerivRtxFine:
                    Store(o[0], $"dpdx({Source(o[1])})", sat); return;
                case Opcode.DerivRty: case Opcode.DerivRtyCoarse: case Opcode.DerivRtyFine:
                    Store(o[0], $"dpdy({Source(o[1])})", sat); return;
                case Opcode.SinCos:
                    if (o[0].Type != OperandType.Null) Store(o[0], $"sin({Source(o[2])})", sat);
                    if (o[1].Type != OperandType.Null) Store(o[1], $"cos({Source(o[2])})", sat);
                    return;

                case Opcode.Eq: Store(o[0], $"bitcast<vec4<f32>>({Mask($"{Source(o[1])} == {Source(o[2])}")})", false); return;
                case Opcode.Ne: Store(o[0], $"bitcast<vec4<f32>>({Mask($"{Source(o[1])} != {Source(o[2])}")})", false); return;
                case Opcode.Lt: Store(o[0], $"bitcast<vec4<f32>>({Mask($"{Source(o[1])} < {Source(o[2])}")})", false); return;
                case Opcode.Ge: Store(o[0], $"bitcast<vec4<f32>>({Mask($"{Source(o[1])} >= {Source(o[2])}")})", false); return;
                case Opcode.IEq: Store(o[0], $"bitcast<vec4<f32>>({Mask($"{ISource(o[1])} == {ISource(o[2])}")})", false); return;
                case Opcode.INe: Store(o[0], $"bitcast<vec4<f32>>({Mask($"{ISource(o[1])} != {ISource(o[2])}")})", false); return;
                case Opcode.ILt: Store(o[0], $"bitcast<vec4<f32>>({Mask($"{ISource(o[1])} < {ISource(o[2])}")})", false); return;
                case Opcode.IGe: Store(o[0], $"bitcast<vec4<f32>>({Mask($"{ISource(o[1])} >= {ISource(o[2])}")})", false); return;
                case Opcode.ULt: Store(o[0], $"bitcast<vec4<f32>>({Mask($"{USource(o[1])} < {USource(o[2])}")})", false); return;
                case Opcode.UGe: Store(o[0], $"bitcast<vec4<f32>>({Mask($"{USource(o[1])} >= {USource(o[2])}")})", false); return;

                case Opcode.Movc:
                    Store(o[0], $"select({Source(o[3])}, {Source(o[2])}, bitcast<vec4<u32>>({Source(Strip(o[1]))}) != vec4<u32>(0u))", sat); return;
                case Opcode.And: StoreI(o[0], $"({USource(o[1])} & {USource(o[2])})"); return;
                case Opcode.Or: StoreI(o[0], $"({USource(o[1])} | {USource(o[2])})"); return;
                case Opcode.Xor: StoreI(o[0], $"({USource(o[1])} ^ {USource(o[2])})"); return;
                case Opcode.Not: StoreI(o[0], $"(~{USource(o[1])})"); return;
                case Opcode.IAdd: StoreI(o[0], $"({ISource(o[1])} + {ISource(o[2])})"); return;
                case Opcode.INeg: StoreI(o[0], $"(-{ISource(o[1])})"); return;
                case Opcode.IMad: StoreI(o[0], $"({ISource(o[1])} * {ISource(o[2])} + {ISource(o[3])})"); return;
                case Opcode.UMad: StoreI(o[0], $"({USource(o[1])} * {USource(o[2])} + {USource(o[3])})"); return;
                case Opcode.IMax: StoreI(o[0], $"max({ISource(o[1])}, {ISource(o[2])})"); return;
                case Opcode.IMin: StoreI(o[0], $"min({ISource(o[1])}, {ISource(o[2])})"); return;
                case Opcode.UMax: StoreI(o[0], $"max({USource(o[1])}, {USource(o[2])})"); return;
                case Opcode.UMin: StoreI(o[0], $"min({USource(o[1])}, {USource(o[2])})"); return;
                case Opcode.IShl: StoreI(o[0], $"({ISource(o[1])} << ({USource(o[2])} & vec4<u32>(31u)))"); return;
                case Opcode.IShr: StoreI(o[0], $"({ISource(o[1])} >> ({USource(o[2])} & vec4<u32>(31u)))"); return;
                case Opcode.UShr: StoreI(o[0], $"({USource(o[1])} >> ({USource(o[2])} & vec4<u32>(31u)))"); return;
                case Opcode.IMul:   // imul dHi, dLo, a, b: only the low half is kept (the high one is rarely asked for)
                    if (o[0].Type != OperandType.Null) throw new NotSupportedException("imul high result");
                    StoreI(o[1], $"({ISource(o[2])} * {ISource(o[3])})"); return;
                case Opcode.UMul:
                    if (o[0].Type != OperandType.Null) throw new NotSupportedException("umul high result");
                    StoreI(o[1], $"({USource(o[2])} * {USource(o[3])})"); return;
                case Opcode.UDiv:
                {
                    string a = USource(o[2]), b = USource(o[3]);
                    if (o[0].Type != OperandType.Null) StoreI(o[0], $"select(vec4<u32>(0xFFFFFFFFu), {a} / max({b}, vec4<u32>(1u)), {b} != vec4<u32>(0u))");
                    if (o[1].Type != OperandType.Null) StoreI(o[1], $"select(vec4<u32>(0xFFFFFFFFu), {a} % max({b}, vec4<u32>(1u)), {b} != vec4<u32>(0u))");
                    return;
                }
                case Opcode.Itof: Store(o[0], $"vec4<f32>({ISource(o[1])})", sat); return;
                case Opcode.Utof: Store(o[0], $"vec4<f32>({USource(o[1])})", sat); return;
                case Opcode.Ftoi: StoreI(o[0], $"vec4<i32>({Source(o[1])})"); return;
                case Opcode.Ftou: StoreI(o[0], $"vec4<u32>({Source(o[1])})"); return;
                case Opcode.CountBits: StoreI(o[0], $"countOneBits({USource(o[1])})"); return;
                // D3D counts firstbit_hi from the most significant bit; WGSL from the least.
                case Opcode.FirstBitHi:
                {
                    string b = $"firstLeadingBit({USource(o[1])})";
                    StoreI(o[0], $"select(vec4<u32>(31u) - {b}, vec4<u32>(0xFFFFFFFFu), {b} == vec4<u32>(0xFFFFFFFFu))");
                    return;
                }
                case Opcode.FirstBitShi:
                {
                    string b = $"bitcast<vec4<u32>>(firstLeadingBit({ISource(o[1])}))";
                    StoreI(o[0], $"select(vec4<u32>(31u) - {b}, vec4<u32>(0xFFFFFFFFu), {b} == vec4<u32>(0xFFFFFFFFu))");
                    return;
                }
                case Opcode.FirstBitLo: StoreI(o[0], $"firstTrailingBit({USource(o[1])})"); return;
                case Opcode.BfRev: StoreI(o[0], $"reverseBits({USource(o[1])})"); return;
                case Opcode.F32toF16:
                    StoreI(o[0], $"vec4<u32>(pack2x16float(vec2<f32>(({Source(o[1])}).x, 0.0f)), pack2x16float(vec2<f32>(({Source(o[1])}).y, 0.0f)), pack2x16float(vec2<f32>(({Source(o[1])}).z, 0.0f)), pack2x16float(vec2<f32>(({Source(o[1])}).w, 0.0f)))");
                    return;
                case Opcode.F16toF32:
                {
                    string u = USource(o[1]);
                    Store(o[0], $"vec4<f32>(unpack2x16float({u}.x).x, unpack2x16float({u}.y).x, unpack2x16float({u}.z).x, unpack2x16float({u}.w).x)", sat);
                    return;
                }

                // flow control
                case Opcode.If: Line($"if {Cond(o[0], ins.TestNonZero)} {{"); _indent++; return;
                case Opcode.Else: _indent--; Line("} else {"); _indent++; return;
                case Opcode.EndIf: _indent--; Line("}"); return;
                case Opcode.Loop: Line("loop {"); _indent++; return;
                case Opcode.EndLoop: _indent--; Line("}"); return;
                case Opcode.Break: Line("break;"); return;
                case Opcode.Breakc: Line($"if {Cond(o[0], ins.TestNonZero)} {{ break; }}"); return;
                case Opcode.Continue: Line("continue;"); return;
                case Opcode.Continuec: Line($"if {Cond(o[0], ins.TestNonZero)} {{ continue; }}"); return;
                case Opcode.Discard: Line($"if {Cond(o[0], ins.TestNonZero)} {{ discard; }}"); return;
                case Opcode.Ret: if (_indent == 1) return; Line("return shader_out();"); return;
                case Opcode.Retc: Line($"if {Cond(o[0], ins.TestNonZero)} {{ return shader_out(); }}"); return;
                case Opcode.Switch:
                    Line($"switch bitcast<i32>({Source(Strip(o[0]))}.x) {{");
                    _indent++;
                    _switches.Push(new SwitchState());
                    return;
                case Opcode.Case:
                case Opcode.Default:
                {
                    var sw = _switches.Peek();
                    if (sw.ClauseOpen) { _indent--; Line("}"); sw.ClauseOpen = false; }
                    if (ins.Op == Opcode.Default) { sw.Pending.Add("default"); sw.HasDefault = true; }
                    else sw.Pending.Add(((int)o[0].Values[0]).ToString(CultureInfo.InvariantCulture));
                    return;
                }
                case Opcode.EndSwitch:
                {
                    var sw = _switches.Pop();
                    if (sw.Pending.Count > 0) { Line($"case {string.Join(", ", sw.Pending)}: {{ }}"); sw.Pending.Clear(); }
                    if (sw.ClauseOpen) { _indent--; Line("}"); }
                    if (!sw.HasDefault) Line("default: { }");
                    _indent--;
                    Line("}");
                    return;
                }

                // resources
                case Opcode.Sample: case Opcode.SampleB: case Opcode.SampleL: case Opcode.SampleD:
                case Opcode.SampleC: case Opcode.SampleCLz:
                    EmitSample(ins); return;
                case Opcode.Ld: EmitLoad(ins); return;
                case Opcode.ResInfo: EmitResInfo(ins); return;
                case Opcode.Lod:
                {
                    int t = o[2].Index0, smp = o[3].Index0;
                    UseResource(t, false); UseSampler(smp, false);
                    Line($"// lod: WGSL has no query of the level a sample would take; the level is reported as 0");
                    Store(o[0], "vec4<f32>(0.0f)", false);
                    return;
                }
                case Opcode.Emit: case Opcode.Cut: case Opcode.EmitThenCut:
                case Opcode.EmitStream: case Opcode.CutStream: case Opcode.EmitThenCutStream:
                    throw new NotSupportedException("geometry shader instruction in a vertex/pixel shader");
                default:
                    throw new NotSupportedException($"instruction {ins.Op}");
            }
        }

        private string Coord(int dim, string c, out string? arrayIndex)
        {
            arrayIndex = null;
            switch (dim)
            {
                case 2: return $"({c}).x";
                case 3: case 4: return $"({c}).xy";
                case 5: case 6: return $"({c}).xyz";
                case 7: arrayIndex = $"i32(round(({c}).y))"; return $"({c}).x";
                case 8: case 9: arrayIndex = $"i32(round(({c}).z))"; return $"({c}).xy";
                case 10: arrayIndex = $"i32(round(({c}).w))"; return $"({c}).xyz";
                default: throw new NotSupportedException($"resource dimension {dim}");
            }
        }

        private void UseResource(int slot, bool depth)
        {
            int dim = _resourceDim.TryGetValue(slot, out int d) ? d : 3;
            char kind = _resourceKind.TryGetValue(slot, out char k) ? k : 'f';
            if (depth) _comparisonResources.Add(slot);
            _bindings.Resources[slot] = (dim, kind, depth || _comparisonResources.Contains(slot));
        }

        private void UseSampler(int slot, bool comparison)
        {
            bool cmp = comparison || (_samplerComparison.TryGetValue(slot, out bool c) && c);
            _bindings.Samplers[slot] = cmp;
        }

        private string Offset(Instruction ins, int dim)
        {
            if (ins.TexelOffset[0] == 0 && ins.TexelOffset[1] == 0 && ins.TexelOffset[2] == 0) return "";
            return dim switch
            {
                3 or 8 => $", vec2<i32>({ins.TexelOffset[0]}, {ins.TexelOffset[1]})",
                5 => $", vec3<i32>({ins.TexelOffset[0]}, {ins.TexelOffset[1]}, {ins.TexelOffset[2]})",
                _ => "",
            };
        }

        private void EmitSample(Instruction ins)
        {
            var o = ins.Operands;
            int t = o[2].Index0, smp = o[3].Index0;
            bool cmp = ins.Op is Opcode.SampleC or Opcode.SampleCLz;
            UseResource(t, cmp);
            UseSampler(smp, cmp);
            int dim = _resourceDim.TryGetValue(t, out int d) ? d : 3;
            string coordSrc = Source(o[1]);
            string coord = Coord(dim, coordSrc, out string? layer);
            string tex = $"t{t}", s = $"s{smp}";
            string args = layer == null ? $"{tex}, {s}, {coord}" : $"{tex}, {s}, {coord}, {layer}";
            string off = Offset(ins, dim);
            string call = ins.Op switch
            {
                Opcode.Sample => $"textureSample({args}{off})",
                Opcode.SampleB => $"textureSampleBias({args}, ({Source(o[4])}).x{off})",
                Opcode.SampleL => $"textureSampleLevel({args}, ({Source(o[4])}).x{off})",
                Opcode.SampleD => $"textureSampleGrad({args}, {GradPart(dim, Source(o[4]))}, {GradPart(dim, Source(o[5]))}{off})",
                Opcode.SampleC => $"textureSampleCompare({args}, ({Source(o[4])}).x{off})",
                _ => $"textureSampleCompareLevel({args}, ({Source(o[4])}).x{off})",
            };
            if (cmp) call = $"vec4<f32>({call})";
            else if (_bindings.Resources[t].Kind != 'f') call = $"bitcast<vec4<f32>>({call})";
            // the resource's own swizzle selects the result components
            Store(o[0], $"({call}){Swz(o[2])}", ins.Saturate);
        }

        private static string GradPart(int dim, string src) => dim switch { 2 or 7 => $"({src}).x", 5 or 6 or 10 => $"({src}).xyz", _ => $"({src}).xy" };

        private void EmitLoad(Instruction ins)
        {
            var o = ins.Operands;
            int t = o[2].Index0;
            if (o[2].Type != OperandType.Resource) throw new NotSupportedException("ld from a UAV");
            UseResource(t, false);
            int dim = _resourceDim.TryGetValue(t, out int d) ? d : 3;
            string c = ISource(o[1]);
            string call = dim switch
            {
                1 => throw new NotSupportedException("ld from a buffer"),
                2 => $"textureLoad(t{t}, ({c}).x, ({c}).y)",
                3 => $"textureLoad(t{t}, ({c}).xy, ({c}).w)",
                5 => $"textureLoad(t{t}, ({c}).xyz, ({c}).w)",
                7 => $"textureLoad(t{t}, ({c}).x, ({c}).y, ({c}).z)",
                8 => $"textureLoad(t{t}, ({c}).xy, ({c}).z, ({c}).w)",
                _ => throw new NotSupportedException($"ld from dimension {dim}"),
            };
            if (_bindings.Resources[t].Kind != 'f') call = $"bitcast<vec4<f32>>({call})";
            Store(o[0], $"({call}){Swz(o[2])}", ins.Saturate);
        }

        private void EmitResInfo(Instruction ins)
        {
            var o = ins.Operands;
            int t = o[2].Index0;
            UseResource(t, false);
            int dim = _resourceDim.TryGetValue(t, out int d) ? d : 3;
            int ret = (int)((ins.Token >> 11) & 3);      // 0 float, 1 rcpfloat, 2 uint
            string mip = $"bitcast<u32>(({Source(o[1])}).x)";
            string dims = dim switch
            {
                2 => $"vec4<u32>(textureDimensions(t{t}, {mip}), 0u, 0u, textureNumLevels(t{t}))",
                3 or 6 => $"vec4<u32>(textureDimensions(t{t}, {mip}), 0u, textureNumLevels(t{t}))",
                8 => $"vec4<u32>(textureDimensions(t{t}, {mip}), textureNumLayers(t{t}), textureNumLevels(t{t}))",
                5 => $"vec4<u32>(textureDimensions(t{t}, {mip}), textureNumLevels(t{t}))",
                _ => throw new NotSupportedException($"resinfo on dimension {dim}"),
            };
            string v = ret switch
            {
                2 => $"bitcast<vec4<f32>>({dims})",
                1 => $"(vec4<f32>(1.0f) / vec4<f32>({dims}))",
                _ => $"vec4<f32>({dims})",
            };
            Store(o[0], $"({v}){Swz(o[2])}", ins.Saturate);
        }

        // ---- the module ----------------------------------------------------------------------------

        private static string TextureType(int dim, char kind, bool depth)
        {
            string k = kind switch { 'i' => "i32", 'u' => "u32", _ => "f32" };
            if (depth) return dim switch { 6 => "texture_depth_cube", 8 => "texture_depth_2d_array", 10 => "texture_depth_cube_array", _ => "texture_depth_2d" };
            return dim switch
            {
                2 => $"texture_1d<{k}>",
                3 => $"texture_2d<{k}>",
                4 => $"texture_multisampled_2d<{k}>",
                5 => $"texture_3d<{k}>",
                6 => $"texture_cube<{k}>",
                7 => $"texture_2d_array<{k}>",       // a 1D array is a one-row 2D array
                8 => $"texture_2d_array<{k}>",
                10 => $"texture_cube_array<{k}>",
                _ => throw new NotSupportedException($"resource dimension {dim}"),
            };
        }

        private string Assemble()
        {
            var w = new StringBuilder();
            w.Append("diagnostic(off, derivative_uniformity);\n\n");
            foreach (var (slot, size) in _bindings.ConstantBuffers)
                w.Append($"@group({_group}) @binding({slot}) var<uniform> cb{slot}: array<vec4<f32>, {size}>;\n");
            foreach (var (slot, cmp) in _bindings.Samplers)
                w.Append($"@group({_group}) @binding({SamplerBase + slot}) var s{slot}: {(cmp ? "sampler_comparison" : "sampler")};\n");
            foreach (var (slot, r) in _bindings.Resources)
                w.Append($"@group({_group}) @binding({ResourceBase + slot}) var t{slot}: {TextureType(r.Dim, r.Kind, r.Depth)};\n");
            if (_s.ImmediateConstantBuffer.Length > 0)
            {
                int n = _s.ImmediateConstantBuffer.Length / 4;
                w.Append($"const icb = array<vec4<f32>, {n}>(");
                for (int i = 0; i < n; i++)
                {
                    if (i > 0) w.Append(", ");
                    var u = _s.ImmediateConstantBuffer;
                    w.Append($"vec4<f32>({Bits(u[i * 4])}, {Bits(u[i * 4 + 1])}, {Bits(u[i * 4 + 2])}, {Bits(u[i * 4 + 3])})");
                }
                w.Append(");\n");
            }
            w.Append('\n');

            // registers live at module scope (private), so helper functions (shader_out) see them
            for (int i = 0; i < _temps; i++) w.Append($"var<private> r{i}: vec4<f32>;\n");
            foreach (var (reg, (size, _)) in _indexable) w.Append($"var<private> x{reg}: array<vec4<f32>, {size}>;\n");
            foreach (int v in _inputRegs) w.Append($"var<private> v{v}: vec4<f32>;\n");
            foreach (int o in _outputRegs) w.Append($"var<private> o{o}: vec4<f32>;\n");
            if (_writesDepth) w.Append("var<private> oDepth: f32;\n");
            w.Append('\n');

            if (_s.Stage == ShaderStage.Vertex) AssembleVertex(w);
            else AssemblePixel(w);
            return w.ToString();
        }

        private static string WgslType(int componentType) => componentType switch { 1 => "vec4<u32>", 2 => "vec4<i32>", _ => "vec4<f32>" };

        private void AssembleVertex(StringBuilder w)
        {
            // inputs: one attribute per declared input register, at location = register
            w.Append("struct VsIn {\n");
            var vertexId = -1; var instanceId = -1;
            foreach (int reg in _inputRegs.OrderBy(r => r))
            {
                if (_inputSv.TryGetValue(reg, out int sv) && sv == 6) { vertexId = reg; continue; }
                if (_inputSv.TryGetValue(reg, out sv) && sv == 8) { instanceId = reg; continue; }
                var e = _s.Inputs.FirstOrDefault(i => i.Register == reg && i.SystemValue == 0);
                var sigType = e?.ComponentType ?? 3;
                // the signature also names VertexID / InstanceID by semantic
                if (e != null && e.SemanticName.Equals("SV_VertexID", StringComparison.OrdinalIgnoreCase)) { vertexId = reg; continue; }
                if (e != null && e.SemanticName.Equals("SV_InstanceID", StringComparison.OrdinalIgnoreCase)) { instanceId = reg; continue; }
                w.Append($"    @location({reg}) a{reg}: {WgslType(sigType)},\n");
            }
            w.Append("    @builtin(vertex_index) vid: u32,\n    @builtin(instance_index) iid: u32,\n}\n\n");

            // outputs: position + the pixel shader's linkage
            int posReg = -1;
            foreach (var e in _s.Outputs)
                if (e.SystemValue == 1 || e.SemanticName.Equals("SV_Position", StringComparison.OrdinalIgnoreCase)) posReg = e.Register;
            w.Append("struct VsOut {\n    @builtin(position) pos: vec4<f32>,\n");
            foreach (var l in _link) w.Append($"    @location({l.Location}) {Interp(l)}l{l.Location}: {WgslType(l.ComponentType)},\n");
            w.Append("}\n\n");

            w.Append("fn shader_out() -> VsOut {\n    var out: VsOut;\n");
            w.Append(posReg >= 0 ? $"    out.pos = o{posReg};\n" : "    out.pos = vec4<f32>(0.0f, 0.0f, 0.0f, 1.0f);\n");
            foreach (var l in _link)
            {
                var e = _s.Outputs.FirstOrDefault(x => x.SemanticName.Equals(l.Semantic, StringComparison.OrdinalIgnoreCase) && x.SemanticIndex == l.Index);
                string v = e == null ? "vec4<f32>(0.0f)" : $"o{e.Register}{ShiftDown(e.Mask)}";
                if (e != null && !_outputRegs.Contains(e.Register)) v = "vec4<f32>(0.0f)";
                w.Append($"    out.l{l.Location} = {Cast(v, l.ComponentType)};\n");
            }
            w.Append("    return out;\n}\n\n");

            w.Append("@vertex\nfn main(input: VsIn) -> VsOut {\n");
            foreach (int reg in _inputRegs.OrderBy(r => r))
            {
                if (reg == vertexId) w.Append($"    v{reg} = bitcast<vec4<f32>>(vec4<u32>(input.vid, 0u, 0u, 0u));\n");
                else if (reg == instanceId) w.Append($"    v{reg} = bitcast<vec4<f32>>(vec4<u32>(input.iid, 0u, 0u, 0u));\n");
                else
                {
                    var e = _s.Inputs.FirstOrDefault(i => i.Register == reg);
                    int ct = e?.ComponentType ?? 3;
                    w.Append(ct == 3 ? $"    v{reg} = input.a{reg};\n" : $"    v{reg} = bitcast<vec4<f32>>(input.a{reg});\n");
                }
            }
            w.Append(_body);
            w.Append("    return shader_out();\n}\n");
        }

        private static string Interp(LinkedElement l) => l.ComponentType == 3 ? "" : "@interpolate(flat) ";

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

        private void AssemblePixel(StringBuilder w)
        {
            w.Append("struct PsIn {\n    @builtin(position) pos: vec4<f32>,\n    @builtin(front_facing) front: bool,\n");
            foreach (var l in _link) w.Append($"    @location({l.Location}) {Interp(l)}l{l.Location}: {WgslType(l.ComponentType)},\n");
            w.Append("}\n\n");

            w.Append("struct PsOut {\n");
            var targets = _s.Outputs.Where(e => e.SystemValue == 64 || e.SemanticName.Equals("SV_Target", StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var e in targets) w.Append($"    @location({e.SemanticIndex}) c{e.SemanticIndex}: {WgslType(e.ComponentType)},\n");
            if (_writesDepth) w.Append("    @builtin(frag_depth) depth: f32,\n");
            if (targets.Count == 0 && !_writesDepth) w.Append("    @location(0) unused: vec4<f32>,\n");
            w.Append("}\n\n");

            w.Append("fn shader_out() -> PsOut {\n    var out: PsOut;\n");
            foreach (var e in targets)
                w.Append($"    out.c{e.SemanticIndex} = {Cast(_outputRegs.Contains(e.Register) ? $"o{e.Register}" : "vec4<f32>(0.0f)", e.ComponentType)};\n");
            if (_writesDepth) w.Append("    out.depth = oDepth;\n");
            w.Append("    return out;\n}\n\n");

            w.Append("@fragment\nfn main(input: PsIn) -> PsOut {\n");
            foreach (int reg in _inputRegs.OrderBy(r => r))
            {
                // every element of this register, from its location or its system value
                var parts = new List<string>();
                var elements = _s.Inputs.Where(e => e.Register == reg).ToList();
                bool assigned = false;
                foreach (var e in elements)
                {
                    string? src = null;
                    if (e.SystemValue == 1 || e.SemanticName.Equals("SV_Position", StringComparison.OrdinalIgnoreCase)) src = "input.pos";
                    else if (e.SystemValue == 9 || e.SemanticName.Equals("SV_IsFrontFace", StringComparison.OrdinalIgnoreCase))
                        src = "bitcast<vec4<f32>>(vec4<u32>(select(0u, 0xFFFFFFFFu, input.front)))";
                    else
                    {
                        var l = _link.FirstOrDefault(x => x.Semantic.Equals(e.SemanticName, StringComparison.OrdinalIgnoreCase) && x.Index == e.SemanticIndex);
                        if (l != null) src = l.ComponentType == 3 ? $"input.l{l.Location}" : $"bitcast<vec4<f32>>(input.l{l.Location})";
                    }
                    if (src == null) continue;
                    if (!assigned && e.Mask == 0xF) { w.Append($"    v{reg} = {src};\n"); assigned = true; continue; }
                    // place the element's components where its mask says, from x of the source
                    int k = 0;
                    w.Append($"    {{ let e = {src};\n");
                    for (int c = 0; c < 4; c++)
                        if ((e.Mask & (1 << c)) != 0)
                            w.Append($"      v{reg}.{"xyzw"[c]} = e.{(e.SystemValue == 1 || e.SystemValue == 9 || e.SemanticName.StartsWith("SV_", StringComparison.OrdinalIgnoreCase) ? "xyzw"[c] : "xyzw"[k++])};\n");
                    w.Append("    }\n");
                    assigned = true;
                }
            }
            w.Append(_body);
            w.Append("    return shader_out();\n}\n");
        }
    }
}
