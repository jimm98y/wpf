// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Arithmetic, integer, bit, conversion and double instructions, and control flow, of the DXBC
// translator (Documentation/d3d11-webgpu-shim.md, section 9). Each instruction follows the D3D11.3
// functional specification's definition, including its edge cases (division by zero, shift counts,
// bit-field widths), so a result is D3D's bit for bit, not WGSL's.
//

using System;
using System.Collections.Generic;
using System.Globalization;

namespace Microsoft.Wpf.Interop.WebGpu.Direct3D.Dxbc
{
    internal sealed partial class DxbcTranslator
    {
        /// <summary>The per-component form of a scalar WGSL function of u32/i32 arguments.</summary>
        private static string PerComponent(string fn, params string[] vecs)
        {
            var c = new string[4];
            for (int i = 0; i < 4; i++)
            {
                var args = new string[vecs.Length];
                for (int k = 0; k < vecs.Length; k++) args[k] = $"({vecs[k]}).{"xyzw"[i]}";
                c[i] = $"{fn}({string.Join(", ", args)})";
            }
            return $"vec4({c[0]}, {c[1]}, {c[2]}, {c[3]})";
        }

        private void Helper(string code) => _helpers.Add(code);

        /// <summary>select(f, t, c) on u32 vectors, written with bit operations: naga's GLSL backend
        /// emits a u32-vector select as a float mix and then uintBitsToFloat(vec4), which no GLSL
        /// compiler takes.</summary>
        private static string SelU(string f, string t, string cond) => $"({f} ^ (({f} ^ {t}) & {Mask(cond)}))";

        private const string MulHiWgsl = @"
// the high 32 bits of a 32 x 32 product (WGSL has no mulhi): four 16-bit partial products
fn dx_umulhi(a: u32, b: u32) -> u32 {
    let a0 = a & 0xFFFFu; let a1 = a >> 16u; let b0 = b & 0xFFFFu; let b1 = b >> 16u;
    let lo = a0 * b0; let m1 = a1 * b0; let m2 = a0 * b1; let hi = a1 * b1;
    let mid = (lo >> 16u) + (m1 & 0xFFFFu) + (m2 & 0xFFFFu);
    return hi + (m1 >> 16u) + (m2 >> 16u) + (mid >> 16u);
}
fn dx_imulhi(a: i32, b: i32) -> i32 {
    var h = dx_umulhi(bitcast<u32>(a), bitcast<u32>(b));
    if (a < 0) { h = h - bitcast<u32>(b); }
    if (b < 0) { h = h - bitcast<u32>(a); }
    return bitcast<i32>(h);
}
";

        private const string HalfWgsl = @"
// f32 bits to f16 bits as WARP converts: toward zero, denormals kept, overflow to infinity, NaN kept quiet
fn dx_f32tof16(f: u32) -> u32 {
    let s = (f >> 16u) & 0x8000u;
    let e = i32((f >> 23u) & 0xFFu);
    let m = f & 0x7FFFFFu;
    if (e == 255) { return s | 0x7C00u | select(0u, 0x200u | (m >> 13u), m != 0u); }
    let he = e - 127 + 15;
    if (he >= 31) { return s | 0x7C00u; }
    if (he <= 0) {
        if (he < -10) { return s; }
        let mm = m | 0x800000u;
        return s | (mm >> u32(14 - he));
    }
    return s | (u32(he) << 10u) | (m >> 13u);
}
// f16 bits (the low 16) to f32 bits, exactly; a NaN comes out quiet
fn dx_f16tof32(v: u32) -> u32 {
    let s = (v & 0x8000u) << 16u;
    let e = (v >> 10u) & 0x1Fu;
    var m = v & 0x3FFu;
    if (e == 0u) {
        if (m == 0u) { return s; }
        var x = 113u;
        loop { if ((m & 0x400u) != 0u) { break; } m = m << 1u; x = x - 1u; }
        return s | (x << 23u) | ((m & 0x3FFu) << 13u);
    }
    if (e == 31u) { return s | 0x7F800000u | select(0u, 0x400000u | (m << 13u), m != 0u); }
    return s | ((e + 112u) << 23u) | (m << 13u);
}
";

        private const string MsadWgsl = @"
// msad: the sum of absolute differences of the four bytes, skipping reference bytes that are 0
fn dx_msad(r: u32, s: u32, acc: u32) -> u32 {
    var sum = acc;
    for (var k = 0u; k < 32u; k = k + 8u) {
        let rb = (r >> k) & 0xFFu; let sb = (s >> k) & 0xFFu;
        if (rb != 0u) { sum = sum + select(sb - rb, rb - sb, rb > sb); }
    }
    return sum;
}
";

        private bool EmitAlu(Instruction ins)
        {
            var o = ins.Operands;
            bool sat = ins.Saturate;
            switch (ins.Op)
            {
                case Opcode.Mov: Store(o[0], Source(o[1]), sat); return true;
                case Opcode.Add: Store(o[0], $"({Source(o[1])} + {Source(o[2])})", sat); return true;
                case Opcode.Mul: Store(o[0], $"({Source(o[1])} * {Source(o[2])})", sat); return true;
                case Opcode.Mad: Store(o[0], $"fma({Source(o[1])}, {Source(o[2])}, {Source(o[3])})", sat); return true;
                case Opcode.Div: Store(o[0], $"({Source(o[1])} / {Source(o[2])})", sat); return true;
                case Opcode.Min: Store(o[0], $"min({Source(o[1])}, {Source(o[2])})", sat); return true;
                case Opcode.Max: Store(o[0], $"max({Source(o[1])}, {Source(o[2])})", sat); return true;
                case Opcode.Dp2: Store(o[0], $"vec4<f32>(dot(({Source(o[1])}).xy, ({Source(o[2])}).xy))", sat); return true;
                case Opcode.Dp3: Store(o[0], $"vec4<f32>(dot(({Source(o[1])}).xyz, ({Source(o[2])}).xyz))", sat); return true;
                case Opcode.Dp4: Store(o[0], $"vec4<f32>(dot({Source(o[1])}, {Source(o[2])}))", sat); return true;
                case Opcode.Rsq: Store(o[0], $"inverseSqrt({Source(o[1])})", sat); return true;
                case Opcode.Sqrt: Store(o[0], $"sqrt({Source(o[1])})", sat); return true;
                case Opcode.Rcp: Store(o[0], $"(vec4<f32>(1.0f) / {Source(o[1])})", sat); return true;
                case Opcode.Exp: Store(o[0], $"exp2({Source(o[1])})", sat); return true;
                case Opcode.Log: Store(o[0], $"log2({Source(o[1])})", sat); return true;
                case Opcode.Frc: Store(o[0], $"fract({Source(o[1])})", sat); return true;
                case Opcode.RoundNe: Store(o[0], $"round({Source(o[1])})", sat); return true;
                case Opcode.RoundNi: Store(o[0], $"floor({Source(o[1])})", sat); return true;
                case Opcode.RoundPi: Store(o[0], $"ceil({Source(o[1])})", sat); return true;
                case Opcode.RoundZ: Store(o[0], $"trunc({Source(o[1])})", sat); return true;
                case Opcode.DerivRtx: Store(o[0], $"dpdx({Source(o[1])})", sat); return true;
                case Opcode.DerivRty: Store(o[0], $"dpdy({Source(o[1])})", sat); return true;
                case Opcode.DerivRtxCoarse: Store(o[0], $"dpdxCoarse({Source(o[1])})", sat); return true;
                case Opcode.DerivRtyCoarse: Store(o[0], $"dpdyCoarse({Source(o[1])})", sat); return true;
                case Opcode.DerivRtxFine: Store(o[0], $"dpdxFine({Source(o[1])})", sat); return true;
                case Opcode.DerivRtyFine: Store(o[0], $"dpdyFine({Source(o[1])})", sat); return true;
                case Opcode.SinCos:
                {
                    // the source is read once: the first result may overwrite it (sincos r0, r1, r0)
                    string x = $"_c{_tmp++}";
                    Line($"let {x} = {Source(o[2])};");
                    if (o[0].Type != OperandType.Null) Store(o[0], $"sin({x})", sat);
                    if (o[1].Type != OperandType.Null) Store(o[1], $"cos({x})", sat);
                    return true;
                }

                case Opcode.Eq: Store(o[0], $"bitcast<vec4<f32>>({Mask($"{Source(o[1])} == {Source(o[2])}")})", false); return true;
                case Opcode.Ne: Store(o[0], $"bitcast<vec4<f32>>({Mask($"{Source(o[1])} != {Source(o[2])}")})", false); return true;
                case Opcode.Lt: Store(o[0], $"bitcast<vec4<f32>>({Mask($"{Source(o[1])} < {Source(o[2])}")})", false); return true;
                case Opcode.Ge: Store(o[0], $"bitcast<vec4<f32>>({Mask($"{Source(o[1])} >= {Source(o[2])}")})", false); return true;
                case Opcode.IEq: Store(o[0], $"bitcast<vec4<f32>>({Mask($"{ISource(o[1])} == {ISource(o[2])}")})", false); return true;
                case Opcode.INe: Store(o[0], $"bitcast<vec4<f32>>({Mask($"{ISource(o[1])} != {ISource(o[2])}")})", false); return true;
                case Opcode.ILt: Store(o[0], $"bitcast<vec4<f32>>({Mask($"{ISource(o[1])} < {ISource(o[2])}")})", false); return true;
                case Opcode.IGe: Store(o[0], $"bitcast<vec4<f32>>({Mask($"{ISource(o[1])} >= {ISource(o[2])}")})", false); return true;
                case Opcode.ULt: Store(o[0], $"bitcast<vec4<f32>>({Mask($"{USource(o[1])} < {USource(o[2])}")})", false); return true;
                case Opcode.UGe: Store(o[0], $"bitcast<vec4<f32>>({Mask($"{USource(o[1])} >= {USource(o[2])}")})", false); return true;

                case Opcode.Movc:
                    Store(o[0], $"select({Source(o[3])}, {Source(o[2])}, bitcast<vec4<u32>>({Source(Strip(o[1]))}) != vec4<u32>(0u))", sat); return true;
                case Opcode.Swapc:
                {
                    // swapc d0, d1, cond, a, b: d0 = cond ? b : a; d1 = cond ? a : b
                    string c = $"_c{_tmp++}", a = $"_c{_tmp++}", b = $"_c{_tmp++}";
                    Line($"let {c} = bitcast<vec4<u32>>({Source(Strip(o[2]))}) != vec4<u32>(0u);");
                    Line($"let {a} = {Source(o[3])};");
                    Line($"let {b} = {Source(o[4])};");
                    Store(o[0], $"select({a}, {b}, {c})", false);
                    Store(o[1], $"select({b}, {a}, {c})", false);
                    return true;
                }
                case Opcode.And: StoreI(o[0], $"({USource(o[1])} & {USource(o[2])})"); return true;
                case Opcode.Or: StoreI(o[0], $"({USource(o[1])} | {USource(o[2])})"); return true;
                case Opcode.Xor: StoreI(o[0], $"({USource(o[1])} ^ {USource(o[2])})"); return true;
                case Opcode.Not: StoreI(o[0], $"(~{USource(o[1])})"); return true;
                case Opcode.IAdd: StoreI(o[0], $"({ISource(o[1])} + {ISource(o[2])})"); return true;
                case Opcode.INeg: StoreI(o[0], $"(-{ISource(o[1])})"); return true;
                case Opcode.IMad: StoreI(o[0], $"({ISource(o[1])} * {ISource(o[2])} + {ISource(o[3])})"); return true;
                case Opcode.UMad: StoreI(o[0], $"({USource(o[1])} * {USource(o[2])} + {USource(o[3])})"); return true;
                case Opcode.IMax: StoreI(o[0], $"max({ISource(o[1])}, {ISource(o[2])})"); return true;
                case Opcode.IMin: StoreI(o[0], $"min({ISource(o[1])}, {ISource(o[2])})"); return true;
                case Opcode.UMax: StoreI(o[0], $"max({USource(o[1])}, {USource(o[2])})"); return true;
                case Opcode.UMin: StoreI(o[0], $"min({USource(o[1])}, {USource(o[2])})"); return true;
                // shift counts use their low 5 bits
                case Opcode.IShl: StoreI(o[0], $"({ISource(o[1])} << ({USource(o[2])} & vec4<u32>(31u)))"); return true;
                case Opcode.IShr: StoreI(o[0], $"({ISource(o[1])} >> ({USource(o[2])} & vec4<u32>(31u)))"); return true;
                case Opcode.UShr: StoreI(o[0], $"({USource(o[1])} >> ({USource(o[2])} & vec4<u32>(31u)))"); return true;
                case Opcode.IMul:
                case Opcode.UMul:
                {
                    // imul/umul dHi, dLo, a, b
                    bool signed = ins.Op == Opcode.IMul;
                    // sources are read once, before either result is written (umul r0, r1, r0, r1)
                    string a = $"_c{_tmp++}", b = $"_c{_tmp++}";
                    Line($"let {a} = {(signed ? ISource(o[2]) : USource(o[2]))};");
                    Line($"let {b} = {(signed ? ISource(o[3]) : USource(o[3]))};");
                    if (o[0].Type != OperandType.Null)
                    {
                        Helper(MulHiWgsl);
                        StoreI(o[0], PerComponent(signed ? "dx_imulhi" : "dx_umulhi", a, b));
                    }
                    StoreI(o[1], $"({a} * {b})");
                    return true;
                }
                case Opcode.UDiv:
                {
                    // division by zero gives all ones in both results
                    // sources are read once, before either result is written (udiv r0, r1, r0, r1)
                    string a = $"_c{_tmp++}", b = $"_c{_tmp++}";
                    Line($"let {a} = {USource(o[2])};");
                    Line($"let {b} = {USource(o[3])};");
                    if (o[0].Type != OperandType.Null) StoreI(o[0], SelU("vec4<u32>(0xFFFFFFFFu)", $"({a} / max({b}, vec4<u32>(1u)))", $"{b} != vec4<u32>(0u)"));
                    if (o[1].Type != OperandType.Null) StoreI(o[1], SelU("vec4<u32>(0xFFFFFFFFu)", $"({a} % max({b}, vec4<u32>(1u)))", $"{b} != vec4<u32>(0u)"));
                    return true;
                }
                case Opcode.UAddc:
                {
                    string a = $"_c{_tmp++}", s = $"_c{_tmp++}";
                    Line($"let {a} = {USource(o[2])};");
                    Line($"let {s} = {a} + {USource(o[3])};");
                    StoreI(o[0], s);
                    StoreI(o[1], $"vec4<u32>({s} < {a})");
                    return true;
                }
                case Opcode.USubb:
                {
                    string a = $"_c{_tmp++}", b = $"_c{_tmp++}";
                    Line($"let {a} = {USource(o[2])};");
                    Line($"let {b} = {USource(o[3])};");
                    StoreI(o[0], $"({a} - {b})");
                    StoreI(o[1], $"vec4<u32>({a} < {b})");
                    return true;
                }
                case Opcode.Itof: Store(o[0], $"vec4<f32>({ISource(o[1])})", sat); return true;
                case Opcode.Utof: Store(o[0], $"vec4<f32>({USource(o[1])})", sat); return true;
                // float to int saturates, NaN gives 0 (WGSL's conversion does both)
                case Opcode.Ftoi: StoreI(o[0], $"vec4<i32>({Source(o[1])})"); return true;
                case Opcode.Ftou: StoreI(o[0], $"vec4<u32>({Source(o[1])})"); return true;
                case Opcode.CountBits: StoreI(o[0], $"countOneBits({USource(o[1])})"); return true;
                // D3D counts firstbit_hi from the most significant bit; WGSL from the least.
                case Opcode.FirstBitHi:
                {
                    string b = $"firstLeadingBit({USource(o[1])})";
                    StoreI(o[0], SelU($"(vec4<u32>(31u) - {b})", "vec4<u32>(0xFFFFFFFFu)", $"{b} == vec4<u32>(0xFFFFFFFFu)"));
                    return true;
                }
                case Opcode.FirstBitShi:
                {
                    string b = $"bitcast<vec4<u32>>(firstLeadingBit({ISource(o[1])}))";
                    StoreI(o[0], SelU($"(vec4<u32>(31u) - {b})", "vec4<u32>(0xFFFFFFFFu)", $"{b} == vec4<u32>(0xFFFFFFFFu)"));
                    return true;
                }
                case Opcode.FirstBitLo: StoreI(o[0], $"firstTrailingBit({USource(o[1])})"); return true;
                case Opcode.BfRev: StoreI(o[0], $"reverseBits({USource(o[1])})"); return true;
                // ubfe/ibfe dst, width, offset, src: width and offset use their low 5 bits; WGSL's
                // extractBits clamps a field that runs past bit 31 exactly as D3D does (the shift)
                case Opcode.UBfe: StoreI(o[0], PerComponent("extractBits", USource(o[3]), $"({USource(o[2])} & vec4<u32>(31u))", $"({USource(o[1])} & vec4<u32>(31u))")); return true;
                case Opcode.IBfe: StoreI(o[0], PerComponent("extractBits", ISource(o[3]), $"({USource(o[2])} & vec4<u32>(31u))", $"({USource(o[1])} & vec4<u32>(31u))")); return true;
                // bfi dst, width, offset, insert, base
                case Opcode.Bfi: StoreI(o[0], PerComponent("insertBits", USource(o[4]), USource(o[3]), $"({USource(o[2])} & vec4<u32>(31u))", $"({USource(o[1])} & vec4<u32>(31u))")); return true;
                case Opcode.Msad:
                    Helper(MsadWgsl);
                    StoreI(o[0], PerComponent("dx_msad", USource(o[1]), USource(o[2]), USource(o[3])));
                    return true;
                // half conversions in integer arithmetic: WGSL leaves pack2x16float's rounding and
                // unpack's denormals to the implementation; WARP (the oracle) rounds toward zero and keeps them
                case Opcode.F32toF16:
                    Helper(HalfWgsl);
                    StoreI(o[0], PerComponent("dx_f32tof16", $"bitcast<vec4<u32>>({Source(o[1])})"));
                    return true;
                case Opcode.F16toF32:
                    Helper(HalfWgsl);
                    StoreI(o[0], PerComponent("dx_f16tof32", USource(o[1])));
                    return true;

                // pixel-shader attribute evaluation. WGSL interpolates an input only where its own
                // qualifier says (center, centroid or sample); evaluating it anywhere else is not
                // expressible, so the value as interpolated is used, and the translation says so.
                case Opcode.EvalCentroid:
                case Opcode.EvalSampleIndex:
                case Opcode.EvalSnapped:
                    Approximate($"{DxbcOpcodes.Info(ins.Op).Name}: the attribute as interpolated at its own location (WGSL evaluates attributes nowhere else)");
                    Store(o[0], Source(o[1]), sat);
                    return true;
            }
            if (DxbcOpcodes.Info(ins.Op).Class == OpClass.Double) { EmitDouble(ins); return true; }
            return false;
        }

        /// <summary>Doubles. WGSL has f64 only behind the shader-f64 extension, which naga's WGSL front
        /// end does not take yet: each double instruction is refused by name (the dmov family, which
        /// only moves bits, is translated).</summary>
        private void EmitDouble(Instruction ins)
        {
            var o = ins.Operands;
            switch (ins.Op)
            {
                case Opcode.DMov: Store(o[0], Source(o[1]), false); return;
                case Opcode.DMovc:
                    // a double occupies two components; the condition's components select pairs
                    Store(o[0], $"select({Source(o[3])}, {Source(o[2])}, bitcast<vec4<u32>>({Source(Strip(o[1]))}).xxyy != vec4<u32>(0u))", false);
                    return;
            }
            throw new NotSupportedException($"double-precision {DxbcOpcodes.Info(ins.Op).Name}: shader-f64 is not available to WGSL here");
        }

        // ---- control flow ------------------------------------------------------------------------

        private sealed class SwitchState
        {
            public readonly List<string> Pending = new();
            public bool ClauseOpen, HasDefault;
        }

        private readonly Stack<SwitchState> _switches = new();

        /// <summary>A switch's pending case labels open their clause at the first instruction that
        /// is not another label.</summary>
        private void BeforeInstruction(Instruction ins)
        {
            if (_switches.Count > 0 && ins.Op is not (Opcode.Case or Opcode.Default or Opcode.EndSwitch) && _switches.Peek().Pending.Count > 0)
            {
                var sw = _switches.Peek();
                Line($"case {string.Join(", ", sw.Pending)}: {{");
                sw.Pending.Clear();
                sw.ClauseOpen = true;
                _indent++;
            }
        }

        private string ReturnStatement => _s.Stage == ShaderStage.Compute || _form != StageForm.Native ? "return;" : "return shader_out();";

        private bool EmitFlow(Instruction ins)
        {
            var o = ins.Operands;
            switch (ins.Op)
            {
                case Opcode.If: Line($"if {Cond(o[0], ins.TestNonZero)} {{"); _indent++; return true;
                case Opcode.Else: _indent--; Line("} else {"); _indent++; return true;
                case Opcode.EndIf: _indent--; Line("}"); return true;
                case Opcode.Loop: Line("loop {"); _indent++; return true;
                case Opcode.EndLoop: _indent--; Line("}"); return true;
                case Opcode.Break: Line("break;"); return true;
                case Opcode.Breakc: Line($"if {Cond(o[0], ins.TestNonZero)} {{ break; }}"); return true;
                case Opcode.Continue: Line("continue;"); return true;
                case Opcode.Continuec: Line($"if {Cond(o[0], ins.TestNonZero)} {{ continue; }}"); return true;
                case Opcode.Discard: Line($"if {Cond(o[0], ins.TestNonZero)} {{ discard; }}"); return true;
                case Opcode.Ret:
                    if (_indent == 1 && _switches.Count == 0) { _ended = true; return true; }
                    Line(ReturnStatement);
                    return true;
                case Opcode.Retc: Line($"if {Cond(o[0], ins.TestNonZero)} {{ {ReturnStatement} }}"); return true;
                case Opcode.Call:
                case Opcode.Callc:
                case Opcode.Label:
                    throw new NotSupportedException("subroutines (call/callc/label): fxc inlines all but [call]-marked code -- not implemented yet");
                case Opcode.Switch:
                    Line($"switch bitcast<i32>({Source(Strip(o[0]))}.x) {{");
                    _indent++;
                    _switches.Push(new SwitchState());
                    return true;
                case Opcode.Case:
                case Opcode.Default:
                {
                    var sw = _switches.Peek();
                    if (sw.ClauseOpen) { _indent--; Line("}"); sw.ClauseOpen = false; }
                    if (ins.Op == Opcode.Default) { sw.Pending.Add("default"); sw.HasDefault = true; }
                    else sw.Pending.Add(((int)o[0].Values[0]).ToString(CultureInfo.InvariantCulture));
                    return true;
                }
                case Opcode.EndSwitch:
                {
                    var sw = _switches.Pop();
                    if (sw.Pending.Count > 0) { Line($"case {string.Join(", ", sw.Pending)}: {{ }}"); sw.Pending.Clear(); }
                    if (sw.ClauseOpen) { _indent--; Line("}"); }
                    if (!sw.HasDefault) Line("default: { }");
                    _indent--;
                    Line("}");
                    return true;
                }
                case Opcode.Abort:
                    Approximate("abort: ignored (a debugging instruction)");
                    return true;
                case Opcode.DebugBreak:
                    return true;
            }
            return false;
        }

        private bool _ended;
    }
}
