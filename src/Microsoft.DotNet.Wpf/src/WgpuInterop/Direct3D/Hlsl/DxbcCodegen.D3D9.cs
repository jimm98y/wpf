// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The shim's HLSL compiler: shader models 2 and 3, the bytecode. The program generated under the
// legacy rules (DxbcCodegen.Legacy.cs) is lowered instruction by instruction to Direct3D 9 tokens
// (d3d9types.h): a value that is 0 or 1 where shader model 4 has a mask (slt/sge in a vertex shader,
// cmp in a pixel shader), the scalar instructions (rcp, rsq, exp, log, sincos) once per component,
// division as rcp and mul, floor and truncation from frc, immediates as def constants after the
// uniforms, the flow control of shader model 3 (ifc, rep with break). The registers are D3D9's: the
// uniforms' c#, v# and t# in, oPos oD# oT# (vs_2_x) or o# (vs_3_0) and oC# oDepth out, s#.
// In front: the constant table (CTAB), the comment block D3DXGetShaderConstantTable reads.
//

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Wpf.Interop.WebGpu.Direct3D.Dxbc;

namespace Microsoft.Wpf.Interop.WebGpu.Direct3D.Hlsl
{
    internal sealed partial class DxbcCodegen
    {
        // D3DSHADER_INSTRUCTION_OPCODE_TYPE
        private const int D9Mov = 1, D9Add = 2, D9Mad = 4, D9Mul = 5, D9Rcp = 6, D9Rsq = 7, D9Dp3 = 8, D9Dp4 = 9, D9Min = 10, D9Max = 11,
            D9Slt = 12, D9Sge = 13, D9Exp = 14, D9Log = 15, D9Lrp = 18, D9Frc = 19, D9Dcl = 31, D9Abs = 35, D9SinCos = 37, D9Rep = 38,
            D9EndRep = 39, D9Ifc = 41, D9Else = 42, D9EndIf = 43, D9Break = 44, D9Breakc = 45, D9Mova = 46, D9DefI = 48, D9TexKill = 65,
            D9Tex = 66, D9Def = 81, D9Cmp = 88, D9Dp2Add = 90, D9Dsx = 91, D9Dsy = 92, D9TexLdd = 93, D9TexLdl = 95;

        // D3DSHADER_PARAM_REGISTER_TYPE
        private const int D9RTemp = 0, D9RInput = 1, D9RConst = 2, D9RAddr = 3, D9RRastOut = 4, D9RAttrOut = 5, D9ROutput = 6,
            D9RConstInt = 7, D9RColorOut = 8, D9RDepthOut = 9, D9RSampler = 10, D9RMisc = 17;

        // D3DSHADER_COMPARISON
        private const int D9Eq = 2, D9Ne = 5;

        /// <summary>The lowering's state: the tokens, the constants it defines, its scratch registers.</summary>
        private sealed class D9
        {
            public readonly List<uint> Code = new();
            public readonly List<(int Reg, uint[] Words)> Defs = new();
            public readonly Dictionary<string, int> DefByValue = new();
            public int NextConst, NextTemp, LoopInt = -1;
            public bool Ps, Sm3;
            public int Major, Minor;
        }

        private D9 _d9 = null!;

        // ---- tokens -----------------------------------------------------------------------------------

        private static uint RegBits(int type, int num) => (uint)num & 0x7FF | (uint)(type & 7) << 28 | (uint)(type >> 3 & 3) << 11;

        private static uint D9Dest(int type, int num, int mask = 0xF, bool sat = false) => 0x80000000u | RegBits(type, num) | (uint)(mask & 0xF) << 16 | (sat ? 1u << 20 : 0);

        /// <summary>A source token: the swizzle (four components), the modifier (1 neg, 11 abs, 12 abs neg).</summary>
        private static uint D9Src(int type, int num, byte[] swz, int mod = 0) =>
            0x80000000u | RegBits(type, num) | (uint)(swz[0] | swz[1] << 2 | swz[2] << 4 | swz[3] << 6) << 16 | (uint)(mod & 0xF) << 24;

        private static readonly byte[] Xyzw = { 0, 1, 2, 3 };
        private static byte[] Rep(int c) => new[] { (byte)c, (byte)c, (byte)c, (byte)c };

        private void D9Ins(int op, int control, params uint[] p)
        {
            _d9.Code.Add((uint)op | (uint)(control & 0xFF) << 16 | (uint)(p.Length & 0xF) << 24);
            _d9.Code.AddRange(p);
        }

        private int D9Temp() => _d9.NextTemp++;

        /// <summary>A def constant holding these four words (one register per distinct value).</summary>
        private int D9Const(params float[] v)
        {
            var w = Enumerable.Range(0, 4).Select(i => BitConverter.SingleToUInt32Bits(v[Math.Min(i, v.Length - 1)])).ToArray();
            string key = string.Join(",", w);
            if (_d9.DefByValue.TryGetValue(key, out int r)) return r;
            r = _d9.NextConst++;
            _d9.Defs.Add((r, w));
            _d9.DefByValue[key] = r;
            return r;
        }

        private uint D9ConstSrc(float v) => D9Src(D9RConst, D9Const(v), Rep(0));
        private uint Zero => D9ConstSrc(0);
        private uint One => D9ConstSrc(1);

        // ---- the program ------------------------------------------------------------------------------

        /// <summary>The legacy program: generated as for shader model 4, lowered to D3D9 tokens.</summary>
        private byte[] CompileLegacy(FunctionDecl f)
        {
            s_legacyImmediates = true;
            try
            {
                Generate(f, record: true);
                AssignRegisters();
                Reset();
                Generate(f, record: false);
                var dxbc = DxbcShader.Parse(Container(f));
                return LowerToD3D9(dxbc);
            }
            finally { s_legacyImmediates = false; }
        }

        private byte[] LowerToD3D9(DxbcShader s)
        {
            _d9 = new D9 { Ps = _stage == ShaderStage.Pixel, Sm3 = _major >= 3, Major = _major, Minor = _minor };
            // the uniforms: every constant buffer's registers after the one before, in slot order
            var cbBase = new Dictionary<int, int>();
            int next = 0;
            foreach (var cb in _cbuffers.Where(c => c.Used && c.Slot >= 0).OrderBy(c => c.Slot))
            {
                cbBase[cb.Slot] = next;
                next += (cb.SizeBytes + 15) / 16;
            }
            _d9.NextConst = next;
            _d9.NextTemp = Math.Max(_temps, 0);
            var icbBase = -1;
            if (_icb.Count > 0)
            {
                icbBase = _d9.NextConst;
                for (int i = 0; i < _icb.Count; i += 4)
                {
                    var w = Enumerable.Range(0, 4).Select(k => i + k < _icb.Count ? _icb[i + k] : 0u).ToArray();
                    _d9.Defs.Add((_d9.NextConst++, w));
                }
            }

            foreach (var ins in s.Instructions)
            {
                if (ins.Op.ToString().StartsWith("Dcl", StringComparison.Ordinal)) continue;
                LowerInstruction(ins, cbBase, icbBase);
            }

            // the stream: version, the constant table, declarations, definitions, the program
            var tokens = new List<uint> { (_d9.Ps ? 0xFFFF0000u : 0xFFFE0000u) | (uint)_major << 8 | (uint)_minor };
            var ctab = ConstantTable(cbBase);
            tokens.Add(0xFFFEu | (uint)(ctab.Length / 4) << 16);
            for (int i = 0; i < ctab.Length; i += 4) tokens.Add(BitConverter.ToUInt32(ctab, i));
            foreach (var (reg, w) in _d9.Defs.OrderBy(d => d.Reg))
            {
                tokens.Add((uint)D9Def | 5u << 24);
                tokens.Add(D9Dest(D9RConst, reg));
                tokens.AddRange(w);
            }
            if (_d9.LoopInt >= 0)
            {
                tokens.Add((uint)D9DefI | 5u << 24);
                tokens.Add(D9Dest(D9RConstInt, _d9.LoopInt));
                tokens.AddRange(new uint[] { 255, 0, 1, 0 });
            }
            tokens.AddRange(D9Declarations());
            tokens.AddRange(_d9.Code);
            tokens.Add(0x0000FFFF);
            var bytes = new byte[tokens.Count * 4];
            Buffer.BlockCopy(tokens.ToArray(), 0, bytes, 0, bytes.Length);
            return bytes;
        }

        // ---- registers -----------------------------------------------------------------------------------

        private static int UsageOf(string semantic) => semantic.ToUpperInvariant() switch
        {
            "POSITION" or "SV_POSITION" or "VPOS" => 0, "BLENDWEIGHT" => 1, "BLENDINDICES" => 2, "NORMAL" => 3, "PSIZE" => 4,
            "TEXCOORD" => 5, "TANGENT" => 6, "BINORMAL" => 7, "TESSFACTOR" => 8, "POSITIONT" => 9, "COLOR" or "SV_TARGET" => 10,
            "FOG" => 11, "DEPTH" or "SV_DEPTH" => 12, "SAMPLE" => 13,
            _ => 5,
        };

        /// <summary>An input's D3D9 register: v# (by its signature register), t# or v# by its semantic
        /// (ps_2_x), vPos and vFace.</summary>
        private (int Type, int Num) D9Input(int reg)
        {
            var e = _inputs.FirstOrDefault(x => x.Register == reg) ?? throw new HlslException(default, $"an input register v{reg} without a semantic");
            if (_d9.Ps)
            {
                if (e.SystemValue == 1) return _d9.Sm3 ? (D9RMisc, 0) : throw new HlslException(default, "VPOS needs shader model 3");
                if (e.SystemValue == 9) return _d9.Sm3 ? (D9RMisc, 1) : throw new HlslException(default, "VFACE needs shader model 3");
                if (!_d9.Sm3)
                {
                    int u = UsageOf(e.Semantic);
                    if (u == 10) return (D9RInput, e.Index);
                    if (u == 5) return (D9RAddr, e.Index);
                    throw new HlslException(default, $"a ps_2_x input is a COLOR or a TEXCOORD, not {e.Semantic}{e.Index}");
                }
            }
            return (D9RInput, reg);
        }

        private (int Type, int Num) D9Output(int reg)
        {
            var e = _outputs.FirstOrDefault(x => x.Register == reg) ?? throw new HlslException(default, $"an output register o{reg} without a semantic");
            if (_d9.Ps)
            {
                if (e.SystemValue == 64) return (D9RColorOut, e.Index);
                if (e.SystemValue == 65) return (D9RDepthOut, 0);
                throw new HlslException(default, $"a pixel shader writes COLOR and DEPTH, not {e.Semantic}{e.Index}");
            }
            if (_d9.Sm3) return (D9ROutput, reg);
            int usage = e.SystemValue == 1 ? 0 : UsageOf(e.Semantic);
            return usage switch
            {
                0 => (D9RRastOut, 0), 11 => (D9RRastOut, 1), 4 => (D9RRastOut, 2),
                10 => (D9RAttrOut, e.Index), 5 => (D9ROutput, e.Index),
                _ => throw new HlslException(default, $"a vs_2_x output is a POSITION, COLOR, TEXCOORD, FOG or PSIZE, not {e.Semantic}{e.Index}"),
            };
        }

        private uint DestToken(Operand o, bool sat)
        {
            int mask = o.ComponentCount == 4 ? (o.IsMask ? o.Mask : 0xF) : 0x1;
            switch (o.Type)
            {
                case OperandType.Temp: return D9Dest(D9RTemp, o.Index0, mask, sat);
                case OperandType.Output: { var (t, n) = D9Output(o.Index0); return D9Dest(t, n, mask, sat); }
                case OperandType.OutputDepth: return D9Dest(D9RDepthOut, 0, 0xF, sat);
            }
            throw new HlslException(default, $"a {o.Type} destination in shader model {_major}");
        }

        /// <summary>A source operand's token, and the address register's token after it for a relative
        /// constant (a0.x, set by a mova before the instruction).</summary>
        private uint[] SrcTokens(Operand o, Dictionary<int, int> cbBase, int icbBase)
        {
            byte[] swz = o.ComponentCount == 4 ? o.Swizzle : Rep(o.Swizzle[0]);
            int mod = o.Modifier switch { OperandModifier.Neg => 1, OperandModifier.Abs => 11, OperandModifier.AbsNeg => 12, _ => 0 };
            if (mod >= 11 && !_d9.Sm3)
            {
                // shader model 2 has no abs modifier: abs into a register first
                int t = D9Temp();
                var plain = SrcTokens(WithModifier(o, OperandModifier.None), cbBase, icbBase);
                D9Ins(D9Abs, 0, new[] { D9Dest(D9RTemp, t) }.Concat(plain).ToArray());
                return new[] { D9Src(D9RTemp, t, Xyzw, mod == 12 ? 1 : 0) };
            }
            switch (o.Type)
            {
                case OperandType.Temp: return new[] { D9Src(D9RTemp, o.Index0, swz, mod) };
                case OperandType.Input: { var (t, n) = D9Input(o.Index0); return new[] { D9Src(t, n, swz, mod) }; }
                case OperandType.Immediate32:
                {
                    var v = o.Values;
                    int r = D9Const(Enumerable.Range(0, 4).Select(i => BitConverter.UInt32BitsToSingle(v[Math.Min(i, v.Length - 1)])).ToArray());
                    return new[] { D9Src(D9RConst, r, o.ComponentCount == 4 ? Xyzw : Rep(0), mod) };
                }
                case OperandType.ConstantBuffer:
                case OperandType.ImmediateConstantBuffer:
                {
                    bool icb = o.Type == OperandType.ImmediateConstantBuffer;
                    var idx = icb ? o.Indices[0] : o.Indices[1];
                    int reg = (icb ? icbBase : cbBase.GetValueOrDefault(o.Index0)) + (int)idx.Immediate;
                    if (idx.Relative == null) return new[] { D9Src(D9RConst, reg, swz, mod) };
                    if (_d9.Ps) throw new HlslException(default, "a pixel shader indexes constants by a value only in shader model 4");
                    // mova a0.x, index; then c[a0.x + reg]
                    var ri = SrcTokens(idx.Relative, cbBase, icbBase);
                    D9Ins(D9Mova, 0, new[] { D9Dest(D9RAddr, 0, 0x1) }.Concat(ri).ToArray());
                    return new[] { D9Src(D9RConst, reg, swz, mod) | 1u << 13, D9Src(D9RAddr, 0, Rep(0)) };
                }
            }
            throw new HlslException(default, $"a {o.Type} source in shader model {_major}");
        }

        private static Operand WithModifier(Operand o, OperandModifier m) => new()
        {
            Type = o.Type, ComponentCount = o.ComponentCount, Mask = o.Mask, Swizzle = o.Swizzle, IsMask = o.IsMask, Indices = o.Indices, Modifier = m, Values = o.Values,
        };

        private static Operand ComponentOf(Operand o, int c)
        {
            var r = WithModifier(o, o.Modifier);
            int pick = o.ComponentCount == 4 ? o.Swizzle[c] : o.Swizzle[0];
            r.Swizzle = Rep(pick);
            if (o.Type == OperandType.Immediate32 && o.Values.Length == 4) { r.Values = new[] { o.Values[c] }; r.ComponentCount = 1; r.Swizzle = Rep(0); }
            return r;
        }

        private static int MaskOf(Operand d) => d.ComponentCount == 4 ? (d.IsMask ? d.Mask : 0xF) : 0x1;

        private static uint WithMask(uint dest, int mask) => dest & ~(0xFu << 16) | (uint)(mask & 0xF) << 16;

        // ---- instructions -----------------------------------------------------------------------------

        private void LowerInstruction(Instruction ins, Dictionary<int, int> cbBase, int icbBase)
        {
            var o = ins.Operands;
            uint[] S(int i) => SrcTokens(o[i], cbBase, icbBase);
            uint[] SC(int i, int c) => SrcTokens(ComponentOf(o[i], c), cbBase, icbBase);
            bool sat = ins.Saturate && (_d9.Ps || _d9.Sm3);
            uint D() => DestToken(o[0], sat);
            void Emit(int op, params uint[][] parts) => D9Ins(op, 0, parts.SelectMany(x => x).ToArray());
            // vs_2_x has no _sat: clamp after
            void Clamp()
            {
                if (!ins.Saturate || sat) return;
                uint d = DestToken(o[0], false);
                var src = new[] { D9Src((int)(d >> 28 & 7 | (d >> 11 & 3) << 3), (int)(d & 0x7FF), Xyzw) };
                D9Ins(D9Max, 0, d, src[0], Zero);
                D9Ins(D9Min, 0, d, src[0], One);
            }
            int mask = o.Length > 0 && o[0].Type != OperandType.Null ? MaskOf(o[0]) : 0xF;
            IEnumerable<int> Comps() => Enumerable.Range(0, 4).Where(c => (mask & (1 << c)) != 0);

            switch (ins.Op)
            {
                case Opcode.Ret: return;          // the end (an earlier return is a branch out of the entry's loop)
                case Opcode.Mov: Emit(D9Mov, new[] { D() }, S(1)); Clamp(); return;
                case Opcode.Add: Emit(D9Add, new[] { D() }, S(1), S(2)); Clamp(); return;
                case Opcode.Mul: Emit(D9Mul, new[] { D() }, S(1), S(2)); Clamp(); return;
                case Opcode.Mad: Emit(D9Mad, new[] { D() }, S(1), S(2), S(3)); Clamp(); return;
                case Opcode.Min: Emit(D9Min, new[] { D() }, S(1), S(2)); Clamp(); return;
                case Opcode.Max: Emit(D9Max, new[] { D() }, S(1), S(2)); Clamp(); return;
                case Opcode.Dp3: Emit(D9Dp3, new[] { D() }, S(1), S(2)); Clamp(); return;
                case Opcode.Dp4: Emit(D9Dp4, new[] { D() }, S(1), S(2)); Clamp(); return;
                case Opcode.Frc: Emit(D9Frc, new[] { D() }, S(1)); Clamp(); return;
                case Opcode.Dp2:
                    if (_d9.Ps) Emit(D9Dp2Add, new[] { D() }, S(1), S(2), new[] { Zero });
                    else
                    {
                        int t = D9Temp();
                        Emit(D9Mul, new[] { D9Dest(D9RTemp, t, 0x3) }, S(1), S(2));
                        Emit(D9Add, new[] { D() }, new[] { D9Src(D9RTemp, t, Rep(0)) }, new[] { D9Src(D9RTemp, t, Rep(1)) });
                    }
                    Clamp();
                    return;
                case Opcode.Rsq: case Opcode.Exp: case Opcode.Log:
                {
                    int op = ins.Op switch { Opcode.Rsq => D9Rsq, Opcode.Exp => D9Exp, _ => D9Log };
                    foreach (int c in Comps()) Emit(op, new[] { WithMask(D(), 1 << c) }, SC(1, c));
                    Clamp();
                    return;
                }
                case Opcode.Sqrt:
                {
                    int t = D9Temp();
                    foreach (int c in Comps())
                    {
                        Emit(D9Rsq, new[] { D9Dest(D9RTemp, t, 1 << c) }, SC(1, c));
                        Emit(D9Rcp, new[] { WithMask(D(), 1 << c) }, new[] { D9Src(D9RTemp, t, Rep(c)) });
                    }
                    Clamp();
                    return;
                }
                case Opcode.Div:
                {
                    int t = D9Temp();
                    foreach (int c in Comps()) Emit(D9Rcp, new[] { D9Dest(D9RTemp, t, 1 << c) }, SC(2, c));
                    Emit(D9Mul, new[] { D() }, S(1), new[] { D9Src(D9RTemp, t, Xyzw) });
                    Clamp();
                    return;
                }
                case Opcode.RoundNi:
                case Opcode.RoundPi:
                {
                    // floor(x) = x - frc(x); ceil(x) = x + frc(-x)
                    int t = D9Temp();
                    var x = S(1);
                    var nx = SrcTokens(WithModifier(o[1], Negated(o[1].Modifier)), cbBase, icbBase);
                    Emit(D9Frc, new[] { D9Dest(D9RTemp, t) }, ins.Op == Opcode.RoundNi ? x : nx);
                    Emit(D9Add, new[] { D() }, x, new[] { D9Src(D9RTemp, t, Xyzw, ins.Op == Opcode.RoundNi ? 1 : 0) });
                    Clamp();
                    return;
                }
                case Opcode.RoundNe:
                {
                    // floor(x + 0.5)
                    int t = D9Temp(), f = D9Temp();
                    Emit(D9Add, new[] { D9Dest(D9RTemp, t) }, S(1), new[] { D9ConstSrc(0.5f) });
                    Emit(D9Frc, new[] { D9Dest(D9RTemp, f) }, new[] { D9Src(D9RTemp, t, Xyzw) });
                    Emit(D9Add, new[] { D() }, new[] { D9Src(D9RTemp, t, Xyzw) }, new[] { D9Src(D9RTemp, f, Xyzw, 1) });
                    Clamp();
                    return;
                }
                case Opcode.RoundZ:
                {
                    // toward zero: the floor of a positive value, the ceiling of a negative one
                    int fl = D9Temp(), ce = D9Temp(), t = D9Temp();
                    var x = S(1);
                    var nx = SrcTokens(WithModifier(o[1], Negated(o[1].Modifier)), cbBase, icbBase);
                    Emit(D9Frc, new[] { D9Dest(D9RTemp, t) }, x);
                    Emit(D9Add, new[] { D9Dest(D9RTemp, fl) }, x, new[] { D9Src(D9RTemp, t, Xyzw, 1) });
                    Emit(D9Frc, new[] { D9Dest(D9RTemp, t) }, nx);
                    Emit(D9Add, new[] { D9Dest(D9RTemp, ce) }, x, new[] { D9Src(D9RTemp, t, Xyzw) });
                    if (_d9.Ps) Emit(D9Cmp, new[] { D() }, x, new[] { D9Src(D9RTemp, fl, Xyzw) }, new[] { D9Src(D9RTemp, ce, Xyzw) });
                    else
                    {
                        int n = D9Temp();
                        Emit(D9Slt, new[] { D9Dest(D9RTemp, n) }, x, new[] { Zero });
                        Emit(D9Lrp, new[] { D() }, new[] { D9Src(D9RTemp, n, Xyzw) }, new[] { D9Src(D9RTemp, ce, Xyzw) }, new[] { D9Src(D9RTemp, fl, Xyzw) });
                    }
                    Clamp();
                    return;
                }
                case Opcode.SinCos:
                {
                    // per component: the angle into [-pi, pi], then sincos (x the cosine, y the sine)
                    int t = D9Temp(), sc = D9Temp();
                    var src = o[2];
                    int m = Math.Max(o[0].Type != OperandType.Null ? MaskOf(o[0]) : 0, o[1].Type != OperandType.Null ? MaskOf(o[1]) : 0);
                    for (int c = 0; c < 4; c++)
                    {
                        if ((m & (1 << c)) == 0) continue;
                        Emit(D9Mad, new[] { D9Dest(D9RTemp, t, 0x1) }, SrcTokens(ComponentOf(src, c), cbBase, icbBase), new[] { D9ConstSrc(0.159154937f) }, new[] { D9ConstSrc(0.5f) });
                        Emit(D9Frc, new[] { D9Dest(D9RTemp, t, 0x1) }, new[] { D9Src(D9RTemp, t, Rep(0)) });
                        Emit(D9Mad, new[] { D9Dest(D9RTemp, t, 0x1) }, new[] { D9Src(D9RTemp, t, Rep(0)) }, new[] { D9ConstSrc(6.28318548f) }, new[] { D9ConstSrc(-3.14159274f) });
                        var sincos = new List<uint> { D9Dest(D9RTemp, sc, 0x3), D9Src(D9RTemp, t, Rep(0)) };
                        if (!_d9.Sm3)
                        {
                            sincos.Add(D9Src(D9RConst, D9Const(-1.55009923e-06f, -2.17013894e-05f, 0.00260416674f, 0.00026041668f), Xyzw));
                            sincos.Add(D9Src(D9RConst, D9Const(-0.020833334f, -0.125f, 1f, 0.5f), Xyzw));
                        }
                        D9Ins(D9SinCos, 0, sincos.ToArray());
                        if (o[0].Type != OperandType.Null && (MaskOf(o[0]) & (1 << c)) != 0)
                            Emit(D9Mov, new[] { WithMask(DestToken(o[0], false), 1 << c) }, new[] { D9Src(D9RTemp, sc, Rep(1)) });
                        if (o[1].Type != OperandType.Null && (MaskOf(o[1]) & (1 << c)) != 0)
                            Emit(D9Mov, new[] { WithMask(DestToken(o[1], false), 1 << c) }, new[] { D9Src(D9RTemp, sc, Rep(0)) });
                    }
                    return;
                }
                case Opcode.Lt: case Opcode.Ge: Compare(ins.Op == Opcode.Lt, D(), S(1), S(2)); return;
                case Opcode.Eq: case Opcode.Ne: Equal(ins.Op == Opcode.Eq, D(), S(1), S(2)); return;
                case Opcode.Movc:
                    // the condition is 0 or 1
                    if (_d9.Ps) Emit(D9Cmp, new[] { D() }, Negated(S(1)), S(3), S(2));
                    else Emit(D9Lrp, new[] { D() }, S(1), S(2), S(3));
                    Clamp();
                    return;
                case Opcode.And: Emit(D9Mul, new[] { D() }, S(1), S(2)); return;
                case Opcode.Or: Emit(D9Max, new[] { D() }, S(1), S(2)); return;
                case Opcode.DerivRtx: case Opcode.DerivRty: case Opcode.DerivRtxCoarse: case Opcode.DerivRtyCoarse: case Opcode.DerivRtxFine: case Opcode.DerivRtyFine:
                    if (!_d9.Ps || (_major == 2 && _minor == 0)) throw new HlslException(default, "ddx and ddy need ps_2_a or ps_3_0");
                    Emit(ins.Op is Opcode.DerivRtx or Opcode.DerivRtxCoarse or Opcode.DerivRtxFine ? D9Dsx : D9Dsy, new[] { D() }, S(1));
                    Clamp();
                    return;
                case Opcode.Sample: case Opcode.SampleB: case Opcode.SampleL: case Opcode.SampleD:
                    Sample(ins, o, S, D);
                    return;
                case Opcode.Discard:
                {
                    // texkill: discarded where a component is below zero -- the condition (0 or 1), negated
                    if (!_d9.Ps) throw new HlslException(default, "discard is only valid in a pixel shader");
                    int t = D9Temp();
                    var cond = S(0);
                    if (ins.TestNonZero) Emit(D9Mov, new[] { D9Dest(D9RTemp, t) }, Negated(cond));
                    else Emit(D9Add, new[] { D9Dest(D9RTemp, t) }, cond, new[] { D9ConstSrc(-1) });
                    D9Ins(D9TexKill, 0, D9Dest(D9RTemp, t));
                    return;
                }
                case Opcode.If:
                    RequireFlow("if");
                    D9Ins(D9Ifc, ins.TestNonZero ? D9Ne : D9Eq, S(0).Concat(new[] { Zero }).ToArray());
                    return;
                case Opcode.Else: D9Ins(D9Else, 0); return;
                case Opcode.EndIf: D9Ins(D9EndIf, 0); return;
                case Opcode.Loop:
                    RequireFlow("a loop");
                    if (_d9.LoopInt < 0) _d9.LoopInt = 0;
                    D9Ins(D9Rep, 0, D9Src(D9RConstInt, _d9.LoopInt, Xyzw));
                    return;
                case Opcode.EndLoop: D9Ins(D9EndRep, 0); return;
                case Opcode.Break: D9Ins(D9Break, 0); return;
                case Opcode.Breakc:
                    D9Ins(D9Breakc, ins.TestNonZero ? D9Ne : D9Eq, S(0).Concat(new[] { Zero }).ToArray());
                    return;
            }
            throw new HlslException(default, $"{DxbcOpcodes.Info(ins.Op).Name} is not available in shader model {_major}");
        }

        private void RequireFlow(string what)
        {
            if (!_d9.Sm3) throw new HlslException(default, $"{what} that a value decides needs shader model 3");
        }

        private static OperandModifier Negated(OperandModifier m) => m switch
        {
            OperandModifier.None => OperandModifier.Neg, OperandModifier.Neg => OperandModifier.None, OperandModifier.Abs => OperandModifier.AbsNeg, _ => OperandModifier.Abs,
        };

        /// <summary>A source token negated (its modifier bit 24 flipped; abs to abs-neg).</summary>
        private static uint[] Negated(uint[] src)
        {
            var r = (uint[])src.Clone();
            uint mod = r[0] >> 24 & 0xF;
            uint nm = mod switch { 0 => 1, 1 => 0, 11 => 12, 12 => 11, _ => throw new HlslException(default, "a negated source with a modifier") };
            r[0] = r[0] & ~(0xFu << 24) | nm << 24;
            return r;
        }

        /// <summary>a &lt; b (or a &gt;= b) as 0 or 1: slt / sge in a vertex shader, cmp of the difference in a pixel shader.</summary>
        private void Compare(bool less, uint dest, uint[] a, uint[] b)
        {
            if (!_d9.Ps) { D9Ins(less ? D9Slt : D9Sge, 0, new[] { dest }.Concat(a).Concat(b).ToArray()); return; }
            int t = D9Temp();
            D9Ins(D9Add, 0, new[] { D9Dest(D9RTemp, t) }.Concat(a).Concat(Negated(b)).ToArray());
            var tt = D9Src(D9RTemp, t, Xyzw);
            D9Ins(D9Cmp, 0, dest, tt, less ? Zero : One, less ? One : Zero);
        }

        /// <summary>a == b (or a != b) as 0 or 1.</summary>
        private void Equal(bool eq, uint dest, uint[] a, uint[] b)
        {
            int t = D9Temp();
            if (!_d9.Ps)
            {
                int u = D9Temp();
                D9Ins(eq ? D9Sge : D9Slt, 0, new[] { D9Dest(D9RTemp, t) }.Concat(a).Concat(b).ToArray());
                D9Ins(eq ? D9Sge : D9Slt, 0, new[] { D9Dest(D9RTemp, u) }.Concat(b).Concat(a).ToArray());
                D9Ins(eq ? D9Mul : D9Add, 0, dest, D9Src(D9RTemp, t, Xyzw), D9Src(D9RTemp, u, Xyzw));
                return;
            }
            // -(a - b)^2 >= 0 only where they are equal
            D9Ins(D9Add, 0, new[] { D9Dest(D9RTemp, t) }.Concat(a).Concat(Negated(b)).ToArray());
            D9Ins(D9Mul, 0, D9Dest(D9RTemp, t), D9Src(D9RTemp, t, Xyzw), D9Src(D9RTemp, t, Xyzw));
            D9Ins(D9Cmp, 0, dest, D9Src(D9RTemp, t, Xyzw, 1), eq ? One : Zero, eq ? Zero : One);
        }

        private void Sample(Instruction ins, Operand[] o, Func<int, uint[]> S, Func<uint> D)
        {
            // sample dest, coord, t#, s# [, lod/bias | ddx, ddy]: texld* dest, coord, s#
            int s = o[3].Index0;
            var sampler = D9Src(D9RSampler, s, Xyzw);
            switch (ins.Op)
            {
                case Opcode.Sample when _d9.Ps:
                    D9Ins(D9Tex, 0, new[] { D() }.Concat(S(1)).Concat(new[] { sampler }).ToArray());
                    return;
                case Opcode.Sample:
                case Opcode.SampleL:
                case Opcode.SampleB:
                {
                    // the level (or bias) in the coordinate's w
                    if (ins.Op != Opcode.SampleB && !_d9.Sm3) throw new HlslException(default, "a sample at a level needs shader model 3");
                    int t = D9Temp();
                    D9Ins(D9Mov, 0, new[] { D9Dest(D9RTemp, t, 0x7) }.Concat(S(1)).ToArray());
                    D9Ins(D9Mov, 0, new[] { D9Dest(D9RTemp, t, 0x8) }.Concat(ins.Op == Opcode.Sample ? new[] { Zero } : S(4)).ToArray());
                    var coord = D9Src(D9RTemp, t, Xyzw);
                    if (ins.Op == Opcode.SampleB) D9Ins(D9Tex, 2, D(), coord, sampler);
                    else D9Ins(D9TexLdl, 0, D(), coord, sampler);
                    return;
                }
                case Opcode.SampleD:
                    if (!_d9.Ps || (_major == 2 && _minor == 0)) throw new HlslException(default, "a sample with gradients needs ps_2_a or ps_3_0");
                    D9Ins(D9TexLdd, 0, new[] { D() }.Concat(S(1)).Concat(new[] { sampler }).Concat(S(4)).Concat(S(5)).ToArray());
                    return;
            }
        }

        // ---- declarations --------------------------------------------------------------------------------

        private IEnumerable<uint> D9Declarations()
        {
            var r = new List<uint>();
            void Dcl(uint usage, uint dest) { r.Add((uint)D9Dcl | 2u << 24); r.Add(usage); r.Add(dest); }
            foreach (var e in _inputs)
            {
                var (type, num) = D9Input(e.Register);
                int mask = e.Mask;
                if (type == D9RMisc) { Dcl(0x80000000u, D9Dest(D9RMisc, num, num == 0 ? 0x3 : 0x1)); continue; }
                uint usage = _d9.Ps && !_d9.Sm3 ? 0x80000000u : 0x80000000u | (uint)UsageOf(e.Semantic) | (uint)e.Index << 16;
                uint dest = D9Dest(type, num, mask);
                // a pixel shader's centroid input
                if (_d9.Ps && e.Modifiers.Contains("centroid")) dest |= 4u << 20;
                Dcl(usage, dest);
            }
            if (!_d9.Ps && _d9.Sm3)
                foreach (var e in _outputs)
                {
                    int usage = e.SystemValue == 1 ? 0 : UsageOf(e.Semantic);
                    Dcl(0x80000000u | (uint)usage | (uint)e.Index << 16, D9Dest(D9ROutput, e.Register, e.Mask));
                }
            foreach (var res in _resources.Where(x => x.Used && x.Slot >= 0 && (x.Kind == 's' || x.Legacy)).OrderBy(x => x.Slot))
            {
                uint type = res.Type.Name switch { "samplerCUBE" or "textureCUBE" or "TextureCube" => 3u, "sampler3D" or "texture3D" or "Texture3D" => 4u, _ => 2u };
                if (_d9.Ps || _d9.Sm3) Dcl(0x80000000u | type << 27, D9Dest(D9RSampler, res.Slot));
            }
            return r;
        }

        // ---- the constant table --------------------------------------------------------------------------

        /// <summary>CTAB (D3DXSHADER_CONSTANTTABLE): the uniforms' registers and types, the samplers'.</summary>
        private byte[] ConstantTable(Dictionary<int, int> cbBase)
        {
            var b = new RdefBuilder { Base = 0 };
            var entries = new List<(string Name, int Set, int Index, int Count, HlslType Type, byte[]? Default)>();
            foreach (var cb in _cbuffers.Where(c => c.Used && c.Slot >= 0))
                foreach (var m in cb.Members.Where(m => m.Used))
                    entries.Add((m.Name, 2, cbBase[cb.Slot] + m.Offset / 16, Math.Max(1, Registers(m.Type)), m.Type, m.Default));
            foreach (var res in _resources.Where(x => x.Used && x.Slot >= 0 && (x.Kind == 's' || x.Legacy)))
                entries.Add((res.Name, 3, res.Slot, 1, res.Type, null));
            entries = entries.OrderBy(e => e.Name, StringComparer.Ordinal).ToList();

            // the header (28 bytes) and the entries (20 each) first; types, names and defaults after
            int infoOff = 28, tail = infoOff + entries.Count * 20;
            b.Base = tail + 4;                          // offsets count from after the 'CTAB' fourcc
            var typeOffs = new List<int>();
            var written = new Dictionary<string, int>();
            foreach (var e in entries) typeOffs.Add(CtabType(b, e.Type, written));
            var defaults = new List<int>();
            foreach (var e in entries)
            {
                if (e.Default == null) { defaults.Add(0); continue; }
                b.Align4();
                defaults.Add(b.Here - 4);
                // a default as it is in the registers: each element or line its own
                b.Tail.AddRange(e.Default);
                while ((b.Here - 4 - defaults[^1]) < e.Count * 16) b.Tail.Add(0);
            }
            var names = entries.Select(e => b.Str(e.Name) - 4).ToList();
            int creator = b.Str(Creator) - 4;
            int target = b.Str($"{(_stage == ShaderStage.Pixel ? "ps" : "vs")}_{_major}_{_minor}") - 4;
            b.Align4();

            var head = new List<byte>();
            void W(int v) => head.AddRange(BitConverter.GetBytes(v));
            void H(int v) => head.AddRange(BitConverter.GetBytes((ushort)v));
            head.AddRange(Encoding.ASCII.GetBytes("CTAB"));
            W(28); W(creator); W((_stage == ShaderStage.Pixel ? unchecked((int)0xFFFF0000) : unchecked((int)0xFFFE0000)) | _major << 8 | _minor);
            W(entries.Count); W(entries.Count > 0 ? infoOff : 0); W((int)(_flags & 0xFFFF) | 0x100); W(target);
            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                W(names[i]); H(e.Set); H(e.Index); H(e.Count); H(0); W(typeOffs[i] - 4); W(defaults[i]);
            }
            head.AddRange(b.Tail);
            while (head.Count % 4 != 0) head.Add(0);
            return head.ToArray();
        }

        /// <summary>A D3DXSHADER_TYPEINFO (16 bytes; offsets counted from after the fourcc, written here +4).</summary>
        private int CtabType(RdefBuilder b, HlslType type, Dictionary<string, int> written)
        {
            int elements = 1;
            var t = type;
            if (t is ArrayType at) { elements = Math.Max(1, at.Length); t = at.Element; }
            string key = $"{t}[{elements}]";
            if (written.TryGetValue(key, out int known)) return known;
            int cls, kind, rows, cols, members = 0, memberOff = 0;
            switch (t)
            {
                case NumericType n:
                    cls = n.IsMatrix ? (n.RowMajor ? 2 : 3) : n.Cols == 1 ? 0 : 1;
                    kind = n.Scalar switch { ScalarKind.Bool => 1, ScalarKind.Int or ScalarKind.Uint => 2, _ => 3 };
                    rows = n.Rows; cols = n.Cols;
                    break;
                case StructType s:
                {
                    cls = 5; kind = 0; rows = 1; cols = s.Components;
                    var mt = s.Fields.Select(f => CtabType(b, f.Type, written)).ToList();
                    var mn = s.Fields.Select(f => b.Str(f.Name)).ToList();
                    b.Align4();
                    memberOff = b.Here - 4;
                    for (int i = 0; i < s.Fields.Count; i++) { b.U32(mn[i] - 4); b.U32(mt[i] - 4); }
                    members = s.Fields.Count;
                    break;
                }
                case ObjectType o:
                    cls = 4; rows = 1; cols = 1;
                    kind = o.Name switch { "sampler1D" => 11, "sampler2D" => 12, "sampler3D" => 13, "samplerCUBE" => 14, _ => 10 };
                    break;
                default: cls = 0; kind = 3; rows = cols = 1; break;
            }
            b.Align4();
            int at0 = b.Here;
            b.U16(cls); b.U16(kind); b.U16(rows); b.U16(cols); b.U16(elements); b.U16(members); b.U32(memberOff);
            written[key] = at0;
            return at0;
        }
    }
}
