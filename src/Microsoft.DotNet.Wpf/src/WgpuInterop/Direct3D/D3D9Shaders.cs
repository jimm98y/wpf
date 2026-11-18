// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Direct3D 9 shader bytecode (vs_1_1 to vs_3_0, ps_2_0 to ps_3_0) as WGSL, for the D3D9 device's draws
// (D3D9.Draw.cs). Opcodes, register types and token layouts are d3d9types.h's.
//
// A shader becomes a function, d3dMain, over module-scope registers: the temporaries, the inputs and
// outputs, a0, aL and p0. Subroutines (label/call) are functions of their own over the same registers.
// The entry point that fills the inputs from the vertex attributes or the interpolated values, and
// reads the outputs, is written when a vertex shader and a pixel shader are linked (D3D9Pipelines),
// since the inter-stage locations depend on both.
//
// Constants are a uniform block (K): 256 float registers, 16 integer and 16 boolean ones, as
// SetVertexShaderConstantF/I/B and SetPixelShaderConstantF/I/B set them. A def, defi or defb in the
// bytecode overrides its register, as D3D9 has it. Flow control reads the integer and boolean
// registers at run time, so a constant change never needs a new shader.
//

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Microsoft.Wpf.Interop.WebGpu.Direct3D
{
    /// <summary>D3DDECLUSAGE.</summary>
    internal static class D3D9Usage
    {
        public const int Position = 0, BlendWeight = 1, BlendIndices = 2, Normal = 3, PSize = 4, TexCoord = 5, Tangent = 6,
            Binormal = 7, TessFactor = 8, PositionT = 9, Color = 10, Fog = 11, Depth = 12, Sample = 13;
        public static int Key(int usage, int index) => usage * 16 + index;
    }

    /// <summary>An input or output register's declaration: its semantic and the components it holds.</summary>
    internal sealed class D3D9ShaderIo
    {
        public int Register, Usage, Index, Mask;
        public int Key => D3D9Usage.Key(Usage, Index);
    }

    internal sealed class D3D9Shader
    {
        public bool Pixel;
        public int Major, Minor;
        public ulong Hash;
        public byte[] Bytecode = Array.Empty<byte>();
        public readonly List<D3D9ShaderIo> Inputs = new(), Outputs = new();
        /// <summary>s# the shader samples, and its texture type (D3DSAMPLER_TEXTURE_TYPE: 2 2D, 3 cube, 4 volume).</summary>
        public readonly SortedDictionary<int, int> Samplers = new();
        /// <summary>Module-scope declarations, the subroutines, and d3dMain.</summary>
        public string Code = "";
        public bool WritesDepth, UsesVPos, UsesVFace;
        /// <summary>oC# written (bit per target).</summary>
        public int ColorOutputs;
        public string? Error;

        public override string ToString() => $"{(Pixel ? "ps" : "vs")}_{Major}_{Minor} {Hash:x16}";
    }

    internal static class D3D9Bytecode
    {
        // D3DSHADER_INSTRUCTION_OPCODE_TYPE
        private const int Nop = 0, Mov = 1, Add = 2, Sub = 3, Mad = 4, Mul = 5, Rcp = 6, Rsq = 7, Dp3 = 8, Dp4 = 9, Min = 10, Max = 11,
            Slt = 12, Sge = 13, Exp = 14, Log = 15, Lit = 16, Dst = 17, Lrp = 18, Frc = 19, M4x4 = 20, M4x3 = 21, M3x4 = 22, M3x3 = 23,
            M3x2 = 24, Call = 25, CallNz = 26, Loop = 27, Ret = 28, EndLoop = 29, Label = 30, Dcl = 31, Pow = 32, Crs = 33, Sgn = 34,
            Abs = 35, Nrm = 36, SinCos = 37, Rep = 38, EndRep = 39, If = 40, Ifc = 41, Else = 42, EndIf = 43, Break = 44, Breakc = 45,
            Mova = 46, DefB = 47, DefI = 48, TexCoord = 64, TexKill = 65, Tex = 66, TexBem = 67, TexBemL = 68, TexReg2AR = 69, TexReg2GB = 70,
            TexM3x2Pad = 71, TexM3x2Tex = 72, TexM3x3Pad = 73, TexM3x3Tex = 74, TexM3x3Spec = 76, TexM3x3VSpec = 77, Cnd = 80,
            TexReg2RGB = 82, TexDp3Tex = 83, TexM3x2Depth = 84, TexDp3 = 85, TexM3x3 = 86, TexDepth = 87, Bem = 89, Phase = 0xFFFD, ExpP = 78, LogP = 79, Def = 81, Cmp = 88, Dp2Add = 90, Dsx = 91,
            Dsy = 92, TexLdd = 93, Setp = 94, TexLdl = 95, BreakP = 96, Comment = 0xFFFE, End = 0xFFFF;

        // D3DSHADER_PARAM_REGISTER_TYPE
        private const int RTemp = 0, RInput = 1, RConst = 2, RAddr = 3, RRastOut = 4, RAttrOut = 5, ROutput = 6, RConstInt = 7,
            RColorOut = 8, RDepthOut = 9, RSampler = 10, RConstBool = 14, RLoop = 15, RMisc = 17, RLabel = 18, RPredicate = 19;

        private static readonly string[] Comparisons = { "", ">", "==", ">=", "<", "!=", "<=", "" };

        /// <summary>Parameter tokens of the shader model 1 instructions (whose length field is reserved).</summary>
        private static int Sm1Length(int op, bool pixel, int minor) => pixel ? op switch
        {
            Nop or Phase => 0,
            Mov => 2, Add or Sub or Mul or Dp3 or Dp4 => 3, Mad or Lrp or Cnd or Cmp => 4,
            Def => 5,
            Tex or TexCoord => minor >= 4 ? 2 : 1,
            TexKill or TexDepth => 1,
            TexBem or TexBemL or TexReg2AR or TexReg2GB or TexReg2RGB or TexM3x2Pad or TexM3x2Tex or TexM3x3Pad or TexM3x3Tex or TexM3x3VSpec
                or TexDp3Tex or TexM3x2Depth or TexDp3 or TexM3x3 => 2,
            TexM3x3Spec or Bem => 3,
            _ => -1,
        } : op switch
        {
            Nop => 0, Mov or Rcp or Rsq or Exp or Log or Lit or Frc or ExpP or LogP or Mova or Abs or Nrm => 2,
            Add or Sub or Mul or Dp3 or Dp4 or Min or Max or Slt or Sge or Dst or M4x4 or M4x3 or M3x4 or M3x3 or M3x2 or Pow or Crs => 3,
            Mad or Lrp or Sgn => 4,
            Dcl => 2, Def => 5,
            _ => -1,
        };

        public static ulong HashOf(ReadOnlySpan<byte> b)
        {
            ulong h = 14695981039346656037UL;
            foreach (byte x in b) { h ^= x; h *= 1099511628211UL; }
            return h;
        }

        private sealed class Ctx
        {
            public D3D9Shader S = null!;
            public bool Vs => !S.Pixel;
            public int Major, Minor;
            /// <summary>ps_1_x: texture stages as samplers, result shifts, the 1.x value range.</summary>
            public bool Ps1;
            /// <summary>ps_1_x: the texture type bound at each stage (absent: what the instruction implies).</summary>
            public IReadOnlyDictionary<int, int>? StageTypes;
            public readonly Dictionary<int, string> Defs = new();
            public readonly Dictionary<int, int[]> Ints = new();
            public readonly Dictionary<int, bool> Bools = new();
            public readonly SortedSet<string> Regs = new(StringComparer.Ordinal);
            public bool UsesA0, UsesAL, UsesP0, RelativeConst, UsesLit;
            public readonly StringBuilder Main = new();
            public readonly Dictionary<int, StringBuilder> Labels = new();
            public StringBuilder Cur = null!;
            public bool CurClosed;
            public readonly List<string> Blocks = new();
            /// <summary>Per open loop (loop aL): the step added at its end.</summary>
            public readonly List<string> LoopSteps = new();
            public int Counter;
            public string? Reason;
            public void Reject(string why) => Reason ??= why;
            public string Ind => new string(' ', 4 + Blocks.Count * 4);
            public void Line(string s) { if (!CurClosed) Cur.Append(Ind).Append(s).Append('\n'); }
        }

        public static D3D9Shader Translate(ReadOnlySpan<byte> bytecode, bool pixel, IReadOnlyDictionary<int, int>? stageTypes = null)
        {
            var s = new D3D9Shader { Pixel = pixel, Bytecode = bytecode.ToArray(), Hash = HashOf(bytecode) };
            if (stageTypes != null) foreach (var (k, v) in stageTypes) s.Hash = (s.Hash ^ (ulong)(k * 8 + v)) * 1099511628211UL;
            try { s.Error = TranslateCore(s, stageTypes); }
            catch (Exception e) when (e is IndexOutOfRangeException or ArgumentOutOfRangeException) { s.Error = "truncated bytecode"; }
            if (s.Error != null) D3DLog.Once("d3d9sh:" + s.Error, $"D3D9 {(pixel ? "pixel" : "vertex")} shader not translated: {s.Error}");
            return s;
        }

        private static string? TranslateCore(D3D9Shader s, IReadOnlyDictionary<int, int>? stageTypes)
        {
            byte[] b = s.Bytecode;
            if (b.Length < 8 || (b.Length & 3) != 0) return "bytecode is not a whole number of tokens";
            var tok = new uint[b.Length / 4];
            for (int i = 0; i < tok.Length; i++) tok[i] = BitConverter.ToUInt32(b, i * 4);
            uint version = tok[0];
            uint kind = version >> 16;
            if (kind != (s.Pixel ? 0xFFFFu : 0xFFFEu)) return $"version token 0x{version:x8} is not a {(s.Pixel ? "pixel" : "vertex")} shader's";
            s.Major = (int)((version >> 8) & 0xFF);
            s.Minor = (int)(version & 0xFF);
            if (s.Major is not (1 or 2 or 3) || (s.Major == 1 && s.Pixel && s.Minor is < 1 or > 4))
                return $"{(s.Pixel ? "ps" : "vs")}_{s.Major}_{s.Minor} is not supported";

            var c = new Ctx { S = s, Major = s.Major, Minor = s.Minor, Ps1 = s.Pixel && s.Major == 1, StageTypes = stageTypes };
            c.Cur = c.Main;
            // ps_2_x: the colour and texture-coordinate registers' semantics are implied
            int p = 1;
            while (p < tok.Length)
            {
                uint t = tok[p];
                int op = (int)(t & 0xFFFF);
                if (op == End) break;
                if (op == Comment) { p += 1 + (int)((t >> 16) & 0x7FFF); continue; }
                if (op == Phase) { p++; continue; }     // ps_1_4: the texture phase ends; the registers carry on
                int len = s.Major >= 2 ? (int)((t >> 24) & 0xF) : Sm1Length(op, s.Pixel, s.Minor);
                if (len < 0) return $"{(s.Pixel ? "ps" : "vs")}_1_x instruction {op} is not supported";
                if (p + len >= tok.Length) return "truncated instruction stream";
                Instruction(c, t, op, new ReadOnlySpan<uint>(tok, p + 1, len));
                if (c.Reason != null) return c.Reason;
                p += 1 + len;
            }
            if (c.Blocks.Count > 0) return "unclosed flow control";
            if (s.Pixel && s.ColorOutputs == 0 && !s.WritesDepth) s.ColorOutputs = 0;    // a depth-only or kill-only shader is valid
            // ps_1_x: the colour is what r0 holds at the end
            if (c.Ps1)
            {
                c.Main.Append("    ").Append(Use(c, "oC0")).Append(" = ").Append(Use(c, "r0")).Append(";\n");
                s.ColorOutputs = 1;
            }

            // ps_2_x and vs_1/2 inputs and outputs whose semantics follow from the register
            if (s.Pixel && s.Major < 3)
                foreach (string r in c.Regs)
                {
                    if (r.StartsWith("t", StringComparison.Ordinal) && int.TryParse(r.AsSpan(1), out int tn) && !s.Inputs.Any(i => i.Usage == D3D9Usage.TexCoord && i.Index == tn))
                        s.Inputs.Add(new D3D9ShaderIo { Register = 100 + tn, Usage = D3D9Usage.TexCoord, Index = tn, Mask = 0xF });
                    if (r.StartsWith("v", StringComparison.Ordinal) && int.TryParse(r.AsSpan(1), out int vn) && !s.Inputs.Any(i => i.Usage == D3D9Usage.Color && i.Index == vn))
                        s.Inputs.Add(new D3D9ShaderIo { Register = vn, Usage = D3D9Usage.Color, Index = vn, Mask = 0xF });
                }
            if (!s.Pixel && s.Major < 3)
            {
                foreach (string r in c.Regs)
                {
                    if (r == "oPos") s.Outputs.Add(new D3D9ShaderIo { Register = -1, Usage = D3D9Usage.Position, Mask = 0xF });
                    else if (r == "oFog") s.Outputs.Add(new D3D9ShaderIo { Register = -2, Usage = D3D9Usage.Fog, Mask = 0x1 });
                    else if (r == "oPts") s.Outputs.Add(new D3D9ShaderIo { Register = -3, Usage = D3D9Usage.PSize, Mask = 0x1 });
                    else if (r.StartsWith("oD", StringComparison.Ordinal)) { int n = int.Parse(r.AsSpan(2), CultureInfo.InvariantCulture); s.Outputs.Add(new D3D9ShaderIo { Register = 200 + n, Usage = D3D9Usage.Color, Index = n, Mask = 0xF }); }
                    else if (r.StartsWith("oT", StringComparison.Ordinal)) { int n = int.Parse(r.AsSpan(2), CultureInfo.InvariantCulture); s.Outputs.Add(new D3D9ShaderIo { Register = 300 + n, Usage = D3D9Usage.TexCoord, Index = n, Mask = 0xF }); }
                }
            }
            if (!s.Pixel && !s.Outputs.Any(o => o.Usage == D3D9Usage.Position && o.Index == 0) && !s.Outputs.Any(o => o.Usage == D3D9Usage.PositionT))
                return "the vertex shader writes no position";

            var w = new StringBuilder();
            foreach (string r in c.Regs) w.Append("var<private> ").Append(r).Append(" : vec4<f32>;\n");
            if (c.UsesA0) w.Append("var<private> a0 : vec4<i32>;\n");
            if (c.UsesAL) w.Append("var<private> aL : i32;\n");
            if (c.UsesP0) w.Append("var<private> p0 : vec4<bool>;\n");
            // the float constants, for a relative read: a def wins over the uniform
            w.Append("fn kf(i : i32) -> vec4<f32> {\n");
            if (c.Defs.Count > 0)
            {
                w.Append("    switch i {\n");
                foreach (var (reg, lit) in c.Defs) w.Append("        case ").Append(reg).Append(": { return ").Append(lit).Append("; }\n");
                w.Append("        default: { }\n    }\n");
            }
            w.Append("    if (i < 0 || i > 255) { return vec4<f32>(0.0); }\n    return K.f[i];\n}\n");
            if (c.UsesLit) w.Append(LitHelper);
            w.Append("fn kb(i : u32) -> bool { return K.b[i >> 2u][i & 3u] != 0u; }\n");
            foreach (var (n, body) in c.Labels) w.Append("fn l").Append(n).Append("() {\n").Append(body).Append("}\n");
            w.Append("fn d3dMain() {\n").Append(c.Main).Append("}\n");
            s.Code = w.ToString();
            return null;
        }

        private static int RegType(uint t) => (int)(((t >> 28) & 0x7) | ((t >> 8) & 0x18));
        private static int RegNum(uint t) => (int)(t & 0x7FF);
        private static bool Relative(uint t) => (t & (1u << 13)) != 0;

        private static string F(float v)
        {
            if (float.IsNaN(v)) return "0.0";
            if (float.IsInfinity(v)) return v > 0 ? "3.4028235e38" : "-3.4028235e38";
            string r = v.ToString("R", CultureInfo.InvariantCulture);
            if (!r.Contains('.') && !r.Contains('E') && !r.Contains('e')) r += ".0";
            return r;
        }

        // ---- operands -------------------------------------------------------------------------------

        /// <summary>A parameter cursor over an instruction's tokens: sources may carry a relative-address token.</summary>
        private ref struct Params
        {
            private readonly ReadOnlySpan<uint> _t;
            private int _i;
            public Params(ReadOnlySpan<uint> t) { _t = t; _i = 0; }
            public bool More => _i < _t.Length;
            public uint Next() => _t[_i++];
            public int Index => _i;
        }

        /// <summary>A register's value as a vec4&lt;f32&gt; expression (no swizzle, no modifier).</summary>
        private static string Reg(Ctx c, int type, int n, string? rel, int offset = 0)
        {
            n += offset;
            switch (type)
            {
                case RTemp: return Use(c, $"r{n}");
                case RInput:
                    if (rel != null) { c.Reject("relative input addressing"); return "vec4<f32>(0.0)"; }
                    return Use(c, $"v{n}");
                case RConst:
                    if (rel != null) { c.RelativeConst = true; return $"kf({n} + {rel})"; }
                    if (c.Defs.TryGetValue(n, out string? lit)) return c.Ps1 ? $"clamp({lit}, vec4<f32>(-1.0), vec4<f32>(1.0))" : lit;
                    if (n > 255) return "vec4<f32>(0.0)";
                    // ps_1_x: constants are in -1..1
                    return c.Ps1 ? $"clamp(K.f[{n}], vec4<f32>(-1.0), vec4<f32>(1.0))" : $"K.f[{n}]";
                case RAddr:
                    if (c.Vs) { c.UsesA0 = true; return "vec4<f32>(a0)"; }
                    return Use(c, $"t{n}");
                case RConstInt: return $"vec4<f32>({Int(c, n)})";
                case RConstBool: return $"select(vec4<f32>(0.0), vec4<f32>(1.0), {Bool(c, n)})";
                case RLoop: c.UsesAL = true; return "vec4<f32>(f32(aL))";
                case RPredicate: c.UsesP0 = true; return "select(vec4<f32>(0.0), vec4<f32>(1.0), p0)";
                case RMisc:
                    if (n == 0) { c.S.UsesVPos = true; return Use(c, "vPos"); }
                    c.S.UsesVFace = true; return Use(c, "vFace");
                case RRastOut: return Use(c, n switch { 0 => "oPos", 1 => "oFog", _ => "oPts" });
                case RAttrOut: return Use(c, $"oD{n}");
                case ROutput: return Use(c, c.Major >= 3 ? $"o{n}" : $"oT{n}");
                case RColorOut: return Use(c, $"oC{n}");
                case RDepthOut: return Use(c, "oDepth");
                default: c.Reject($"register type {type} as a source"); return "vec4<f32>(0.0)";
            }
        }

        private static string Use(Ctx c, string name) { c.Regs.Add(name); return name; }

        private static string Int(Ctx c, int n) => c.Ints.TryGetValue(n, out int[]? v)
            ? $"vec4<i32>({v[0]}, {v[1]}, {v[2]}, {v[3]})" : $"K.i[{n}]";

        private static string Bool(Ctx c, int n) => c.Bools.TryGetValue(n, out bool v) ? (v ? "true" : "false") : $"kb({n}u)";

        /// <summary>The relative-address token's register component: a0.c or aL.</summary>
        private static string RelOf(Ctx c, uint relToken)
        {
            int type = RegType(relToken);
            int comp = (int)((relToken >> 16) & 3);
            if (type == RLoop) { c.UsesAL = true; return "aL"; }
            c.UsesA0 = true;
            return "a0." + "xyzw"[comp];
        }

        private static string Swz(int swizzle)
        {
            if (swizzle == 0xE4) return "";
            var sb = new StringBuilder(".");
            for (int i = 0; i < 4; i++) sb.Append("xyzw"[(swizzle >> (i * 2)) & 3]);
            return sb.ToString();
        }

        /// <summary>A source operand: register, relative address, swizzle and modifier.</summary>
        private static string Src(Ctx c, ref Params ps, int offset = 0)
        {
            uint t = ps.Next();
            string? rel = null;
            if (Relative(t))
            {
                if (c.Major >= 2) rel = RelOf(c, ps.Next());
                else { c.UsesA0 = true; rel = "a0.x"; }
            }
            int type = RegType(t), n = RegNum(t);
            string e = Reg(c, type, n, rel, offset);
            string swz = Swz((int)((t >> 16) & 0xFF));
            if (swz.Length > 0) e = $"({e}){swz}";
            return (int)((t >> 24) & 0xF) switch
            {
                0 => e,
                1 => $"(-{e})",
                2 => $"({e} - vec4<f32>(0.5))",
                3 => $"(vec4<f32>(0.5) - {e})",
                4 => $"(({e} - vec4<f32>(0.5)) * 2.0)",
                5 => $"((vec4<f32>(0.5) - {e}) * 2.0)",
                6 => $"(vec4<f32>(1.0) - {e})",
                7 => $"({e} * 2.0)",
                8 => $"({e} * -2.0)",
                9 => $"({e} / ({e}).z)",
                10 => $"({e} / ({e}).w)",
                11 => $"abs({e})",
                12 => $"(-abs({e}))",
                13 => $"(vec4<f32>(1.0) - {e})",    // NOT (predicate and boolean registers, as 0/1)
                int m => Fail(c, $"source modifier {m}"),
            };
        }

        private static string Fail(Ctx c, string why) { c.Reject(why); return "vec4<f32>(0.0)"; }

        /// <summary>A scalar condition source: its swizzled first component.</summary>
        private static string Scalar(string e) => $"({e}).x";

        private struct DstReg { public int Type, Num, Mask, Shift; public bool Saturate; public string Name; }

        private static DstReg Dest(Ctx c, ref Params ps)
        {
            uint t = ps.Next();
            var d = new DstReg { Type = RegType(t), Num = RegNum(t), Mask = (int)((t >> 16) & 0xF), Saturate = (t & (1u << 20)) != 0, Shift = (int)((t >> 24) & 0xF) };
            if (Relative(t))
            {
                if (c.Major >= 3) ps.Next();
                c.Reject("relative output addressing");
            }
            d.Name = d.Type switch
            {
                RTemp => Use(c, $"r{d.Num}"),
                RAddr when c.Vs => "a0",
                RAddr when c.Ps1 => Use(c, $"t{d.Num}"),
                RRastOut => Use(c, d.Num switch { 0 => "oPos", 1 => "oFog", _ => "oPts" }),
                RAttrOut => Use(c, $"oD{d.Num}"),
                ROutput => Use(c, c.Major >= 3 ? $"o{d.Num}" : $"oT{d.Num}"),
                RColorOut => Use(c, $"oC{d.Num}"),
                RDepthOut => Use(c, "oDepth"),
                RPredicate => "p0",
                _ => "",
            };
            if (d.Name.Length == 0) c.Reject($"register type {d.Type} as a destination");
            if (d.Type == RColorOut) c.S.ColorOutputs |= 1 << d.Num;
            if (d.Type == RDepthOut) c.S.WritesDepth = true;
            if (d.Type == RAddr) c.UsesA0 = true;
            if (d.Type == RPredicate) c.UsesP0 = true;
            if (d.Mask == 0) d.Mask = 0xF;
            return d;
        }

        /// <summary>Writes a vec4 expression to the destination: write mask, saturate, predicate.</summary>
        private static void Write(Ctx c, DstReg d, string value, string? predicate, bool floor = false)
        {
            // the result shift (_x2, _x4, _x8, _d2, _d4, _d8)
            if (d.Shift is >= 1 and <= 3) value = $"({value} * {1 << d.Shift}.0)";
            else if (d.Shift is >= 13 and <= 15) value = $"({value} / {1 << (16 - d.Shift)}.0)";
            if (d.Saturate) value = $"clamp({value}, vec4<f32>(0.0), vec4<f32>(1.0))";
            else if (c.Ps1 && d.Type is RTemp or RAddr) value = $"clamp({value}, vec4<f32>(-{F(Ps1MaxValue)}), vec4<f32>({F(Ps1MaxValue)}))";
            string conv(string comp) => d.Type == RAddr && c.Vs ? (floor ? $"i32(floor(_v.{comp}))" : $"i32(round(_v.{comp}))") : $"_v.{comp}";
            var sb = new StringBuilder("{ let _v = ").Append(value).Append(';');
            for (int i = 0; i < 4; i++)
            {
                if ((d.Mask & (1 << i)) == 0) continue;
                char comp = "xyzw"[i];
                string assign = $" {d.Name}.{comp} = {conv(comp.ToString())};";
                if (predicate != null) sb.Append(" if (").Append(PredComp(predicate, i)).Append(") {").Append(assign).Append(" }");
                else sb.Append(assign);
            }
            sb.Append(" }");
            c.Line(sb.ToString());
        }

        /// <summary>The predicate token (an instruction's when it is predicated): p0, its swizzle and NOT.</summary>
        private static string Predicate(Ctx c, uint t)
        {
            c.UsesP0 = true;
            int swz = (int)((t >> 16) & 0xFF);
            bool not = ((t >> 24) & 0xF) == 13;
            return (not ? "!" : "") + "|" + swz;
        }

        private static string PredComp(string predicate, int component)
        {
            bool not = predicate[0] == '!';
            int swz = int.Parse(predicate.AsSpan(predicate.IndexOf('|') + 1), CultureInfo.InvariantCulture);
            string e = "p0." + "xyzw"[(swz >> (component * 2)) & 3];
            return not ? "!" + e : e;
        }

        // ---- instructions ---------------------------------------------------------------------------

        private static void Instruction(Ctx c, uint t, int op, ReadOnlySpan<uint> args)
        {
            var ps = new Params(args);
            bool predicated = (t & (1u << 28)) != 0;
            int control = (int)((t >> 16) & 0xFF);
            switch (op)
            {
                case Nop: return;
                case Dcl: Declare(c, args); return;
                case Def:
                {
                    int reg = RegNum(args[0]);
                    c.Defs[reg] = $"vec4<f32>({F(BitConverter.UInt32BitsToSingle(args[1]))}, {F(BitConverter.UInt32BitsToSingle(args[2]))}, {F(BitConverter.UInt32BitsToSingle(args[3]))}, {F(BitConverter.UInt32BitsToSingle(args[4]))})";
                    return;
                }
                case DefI: c.Ints[RegNum(args[0])] = new[] { (int)args[1], (int)args[2], (int)args[3], (int)args[4] }; return;
                case DefB: c.Bools[RegNum(args[0])] = args[1] != 0; return;

                case Label:
                {
                    int n = RegNum(args[0]);
                    if (c.Blocks.Count > 0) { c.Reject("label inside a block"); return; }
                    c.Cur = c.Labels[n] = new StringBuilder();
                    c.CurClosed = false;
                    return;
                }
                case Ret:
                    if (c.Blocks.Count == 0) { c.CurClosed = true; return; }    // the end of the function
                    c.Line("return;");
                    return;
                case Call: c.Line($"l{RegNum(args[0])}();"); return;
                case CallNz:
                {
                    int n = RegNum(args[0]);
                    uint cond = args[1];
                    string e = RegType(cond) == RPredicate ? PredCond(c, cond) : Bool(c, RegNum(cond));
                    if (RegType(cond) != RPredicate && ((cond >> 24) & 0xF) == 13) e = "!" + e;
                    c.Line($"if ({e}) {{ l{n}(); }}");
                    return;
                }

                case If:
                {
                    uint cond = args[0];
                    string e = RegType(cond) == RPredicate ? PredCond(c, cond) : Bool(c, RegNum(cond));
                    if (RegType(cond) != RPredicate && ((cond >> 24) & 0xF) == 13) e = "!" + e;
                    c.Line($"if ({e}) {{");
                    c.Blocks.Add("if");
                    return;
                }
                case Ifc:
                {
                    string a = Src(c, ref ps), b = Src(c, ref ps);
                    string cmp = Comparisons[control & 7];
                    if (cmp.Length == 0) { c.Reject("ifc with a reserved comparison"); return; }
                    c.Line($"if ({Scalar(a)} {cmp} {Scalar(b)}) {{");
                    c.Blocks.Add("if");
                    return;
                }
                case Else:
                    if (c.Blocks.Count == 0 || c.Blocks[^1] != "if") { c.Reject("else without if"); return; }
                    c.Blocks.RemoveAt(c.Blocks.Count - 1);
                    c.Line("} else {");
                    c.Blocks.Add("if");
                    return;
                case EndIf:
                    if (c.Blocks.Count == 0 || c.Blocks[^1] != "if") { c.Reject("endif without if"); return; }
                    c.Blocks.RemoveAt(c.Blocks.Count - 1);
                    c.Line("}");
                    return;
                case Rep:
                {
                    int n = RegNum(args[0]);
                    string k = $"rc{c.Counter++}";
                    c.Line($"for (var {k} : i32 = 0; {k} < min(({Int(c, n)}).x, 255); {k} = {k} + 1) {{");
                    c.Blocks.Add("rep");
                    return;
                }
                case EndRep:
                    if (c.Blocks.Count == 0 || c.Blocks[^1] != "rep") { c.Reject("endrep without rep"); return; }
                    c.Blocks.RemoveAt(c.Blocks.Count - 1);
                    c.Line("}");
                    return;
                case Loop:
                {
                    // loop aL, i#: count i.x, start i.y, step i.z
                    c.UsesAL = true;
                    int n = RegNum(args[1]);
                    string iv = Int(c, n);
                    string k = $"lc{c.Counter}", save = $"ls{c.Counter}", ii = $"li{c.Counter}";
                    c.Counter++;
                    c.Line($"let {ii} = {iv}; let {save} = aL;");
                    // aL for the iteration from its number (start + n * step), not carried from the last one
                    c.Line($"for (var {k} : i32 = 0; {k} < min({ii}.x, 255); {k} = {k} + 1) {{");
                    c.Blocks.Add("loop");
                    c.LoopSteps.Add($"aL = {save};");
                    c.Line($"aL = {ii}.y + {k} * {ii}.z;");
                    return;
                }
                case EndLoop:
                {
                    if (c.Blocks.Count == 0 || c.Blocks[^1] != "loop") { c.Reject("endloop without loop"); return; }
                    string restore = c.LoopSteps[^1];
                    c.LoopSteps.RemoveAt(c.LoopSteps.Count - 1);
                    c.Blocks.RemoveAt(c.Blocks.Count - 1);
                    c.Line("}");
                    c.Line(restore);
                    return;
                }
                case Break:
                    if (!c.Blocks.Contains("rep") && !c.Blocks.Contains("loop")) { c.Reject("break outside a loop"); return; }
                    // a break out of a loop aL skips its step: the step is only for the next iteration
                    c.Line("break;");
                    return;
                case Breakc:
                {
                    string a = Src(c, ref ps), b = Src(c, ref ps);
                    string cmp = Comparisons[control & 7];
                    c.Line($"if ({Scalar(a)} {cmp} {Scalar(b)}) {{ break; }}");
                    return;
                }
                case BreakP:
                    c.Line($"if ({PredCond(c, args[0])}) {{ break; }}");
                    return;

                case TexKill:
                {
                    // texkill: the pixel is discarded when a component the mask selects is below zero
                    uint r = ps.Next();
                    int mask = (int)((r >> 16) & 0xF);
                    if (mask == 0) mask = 0xF;
                    string e = Reg(c, RegType(r), RegNum(r), null);
                    var conds = new List<string>();
                    for (int i = 0; i < 4; i++) if ((mask & (1 << i)) != 0) conds.Add($"({e}).{"xyzw"[i]} < 0.0");
                    c.Line($"if ({string.Join(" || ", conds)}) {{ discard; }}");
                    return;
                }
            }

            DstReg d = Dest(c, ref ps);
            string? pred = predicated ? Predicate(c, ps.Next()) : null;
            if (c.Reason != null) return;
            if (c.Ps1 && Ps1Instruction(c, op, d, ref ps)) return;

            switch (op)
            {
                case Tex:
                case TexLdl:
                case TexLdd:
                {
                    string coord = Src(c, ref ps);
                    uint st = ps.Next();
                    if (RegType(st) != RSampler) { c.Reject("texld without a sampler"); return; }
                    int sn = RegNum(st);
                    string swz = Swz((int)((st >> 16) & 0xFF));
                    if (!c.S.Samplers.TryGetValue(sn, out int type)) c.S.Samplers[sn] = type = 2;
                    // a depth texture bound to a 2D sampler: read as its depth, or as a shadow map
                    int bound = c.Vs ? 0 : BoundType(c, sn);
                    if (type == 2 && bound is 6 or 7) c.S.Samplers[sn] = type = bound;
                    int mode = op == TexLdl || c.Vs ? 3 : op == TexLdd ? 4 : (control & 3) == 1 ? 1 : (control & 3) == 2 ? 2 : 0;
                    string? dx = op == TexLdd ? Src(c, ref ps) : null, dy = op == TexLdd ? Src(c, ref ps) : null;
                    string e = SampleExpr(c, sn, type, coord, mode, dx, dy);
                    if (swz.Length > 0) e = $"({e}){swz}";
                    Write(c, d, e, pred);
                    return;
                }
                case Mova:
                    Write(c, d, Src(c, ref ps), pred);
                    return;
                case Mov:
                    Write(c, d, Src(c, ref ps), pred, floor: c.Vs && c.Major < 2 && d.Type == RAddr);
                    return;
                case Setp:
                {
                    string a = Src(c, ref ps), b = Src(c, ref ps);
                    string cmp = Comparisons[control & 7];
                    var sb = new StringBuilder("{ let _a = ").Append(a).Append("; let _b = ").Append(b).Append(';');
                    for (int i = 0; i < 4; i++)
                        if ((d.Mask & (1 << i)) != 0) sb.Append($" p0.{"xyzw"[i]} = _a.{"xyzw"[i]} {cmp} _b.{"xyzw"[i]};");
                    c.Line(sb.Append(" }").ToString());
                    return;
                }
                case M4x4: case M4x3: case M3x4: case M3x3: case M3x2:
                {
                    string v = Src(c, ref ps);
                    int at = ps.Index;
                    int rows = op switch { M4x4 or M3x4 => 4, M4x3 or M3x3 => 3, _ => 2 };
                    bool four = op is M4x4 or M4x3;
                    var parts = new List<string>();
                    for (int i = 0; i < rows; i++)
                    {
                        // the matrix: the second source's register and the ones after it, one per row
                        var rp = new Params(args.Slice(at));
                        string row = Src(c, ref rp, i);
                        parts.Add(four ? $"dot({v}, {row})" : $"dot(({v}).xyz, ({row}).xyz)");
                    }
                    while (parts.Count < 4) parts.Add("0.0");
                    Write(c, d, $"vec4<f32>({string.Join(", ", parts)})", pred);
                    return;
                }
            }

            int srcCount = op switch
            {
                Rcp or Rsq or Exp or Log or Frc or Abs or Nrm or Lit or ExpP or LogP or Dsx or Dsy => 1,
                Add or Sub or Mul or Dp3 or Dp4 or Min or Max or Slt or Sge or Dst or Pow or Crs => 2,
                Mad or Lrp or Cmp or Cnd or Dp2Add => 3,
                Sgn => 1,
                SinCos => 1,
                _ => -1,
            };
            if (srcCount < 0) { c.Reject($"opcode {op}"); return; }
            var s = new string[srcCount];
            for (int i = 0; i < srcCount; i++) s[i] = Src(c, ref ps);
            string x0 = srcCount > 0 ? Scalar(s[0]) : "";
            string expr = op switch
            {
                Add => $"({s[0]} + {s[1]})",
                Sub => $"({s[0]} - {s[1]})",
                Mul => $"({s[0]} * {s[1]})",
                Mad => $"({s[0]} * {s[1]} + {s[2]})",
                Min => $"min({s[0]}, {s[1]})",
                Max => $"max({s[0]}, {s[1]})",
                Frc => $"fract({s[0]})",
                Abs => $"abs({s[0]})",
                Sgn => $"sign({s[0]})",
                Lrp => $"mix({s[2]}, {s[1]}, {s[0]})",
                Cmp => $"select({s[2]}, {s[1]}, {s[0]} >= vec4<f32>(0.0))",
                Cnd => $"select({s[2]}, {s[1]}, {s[0]} > vec4<f32>(0.5))",
                Rcp => $"vec4<f32>(1.0 / {x0})",
                Rsq => $"vec4<f32>(inverseSqrt(abs({x0})))",
                Exp => $"vec4<f32>(exp2({x0}))",
                Log => $"vec4<f32>(select(log2(abs({x0})), -3.4028235e38, {x0} == 0.0))",
                ExpP when c.Major < 2 => $"vec4<f32>(exp2(floor({x0})), fract({x0}), exp2({x0}), 1.0)",
                ExpP => $"vec4<f32>(exp2({x0}))",
                LogP when c.Major < 2 => $"vec4<f32>(floor(log2(abs({x0}))), abs({x0}) / exp2(floor(log2(abs({x0})))), select(log2(abs({x0})), -3.4028235e38, {x0} == 0.0), 1.0)",
                LogP => $"vec4<f32>(select(log2(abs({x0})), -3.4028235e38, {x0} == 0.0))",
                Pow => $"vec4<f32>(pow(abs({x0}), {Scalar(s[1])}))",
                Dp3 => $"vec4<f32>(dot(({s[0]}).xyz, ({s[1]}).xyz))",
                Dp4 => $"vec4<f32>(dot({s[0]}, {s[1]}))",
                Dp2Add => $"vec4<f32>(dot(({s[0]}).xy, ({s[1]}).xy) + ({s[2]}).x)",
                Slt => $"select(vec4<f32>(0.0), vec4<f32>(1.0), {s[0]} < {s[1]})",
                Sge => $"select(vec4<f32>(0.0), vec4<f32>(1.0), {s[0]} >= {s[1]})",
                Crs => $"vec4<f32>(cross(({s[0]}).xyz, ({s[1]}).xyz), 0.0)",
                Nrm => $"({s[0]} * inverseSqrt(dot(({s[0]}).xyz, ({s[0]}).xyz)))",
                SinCos => $"vec4<f32>(cos({x0}), sin({x0}), 0.0, 0.0)",
                Lit => $"d3dLit({s[0]})",
                Dst => $"vec4<f32>(1.0, ({s[0]}).y * ({s[1]}).y, ({s[0]}).z, ({s[1]}).w)",
                Dsx => $"dpdx({s[0]})",
                Dsy => $"dpdy({s[0]})",
                _ => Fail(c, $"opcode {op}"),
            };
            if (op == Lit) c.UsesLit = true;
            Write(c, d, expr, pred);
        }

        /// <summary>D3DCAPS9.PixelShader1xMaxValue: the range of ps_1_x registers.</summary>
        public const float Ps1MaxValue = 8f;

        /// <summary>A ps_1_x texture stage's sample at a vec4 coordinate (its first two or three components).</summary>
        private static string Sample(Ctx c, int stage, string coord, int implied)
        {
            int bound = BoundType(c, stage);
            int type = bound != 0 ? bound : implied;
            c.S.Samplers[stage] = type;
            return SampleExpr(c, stage, type, coord, 0, null, null);
        }

        /// <summary>The type of the texture bound at a sampler (0: not known), and its address fix (D3D9Programs.AddressFix).</summary>
        private static int BoundType(Ctx c, int sn) => c.StageTypes != null && c.StageTypes.TryGetValue(sn, out int v) ? v & 0xFF : 0;
        private static int BoundAddress(Ctx c, int sn) => c.StageTypes != null && c.StageTypes.TryGetValue(sn, out int v) ? v >> 8 : 0;

        /// <summary>A sample of sampler <paramref name="sn"/> at a vec4 coordinate: mode 0 texld, 1 texldp
        /// (projected), 2 texldb (biased by w), 3 texldl (level w), 4 texldd (gradients). Depth textures:
        /// type 6 the depth, type 7 the shadow test of the coordinate's z (both in every channel).</summary>
        private static string SampleExpr(Ctx c, int sn, int type, string coord, int mode, string? dx, string? dy)
        {
            string tex = c.Vs ? $"vtex{sn}" : $"tex{sn}", smp = c.Vs ? $"vsmp{sn}" : $"smp{sn}";
            int dims = type is 3 or 4 ? 3 : 2;
            string div = mode == 1 ? $" / ({coord}).w" : "";
            string cs = dims == 2 ? $"(({coord}).xy{div})" : $"(({coord}).xyz{div})";
            int fix = c.Vs || type == 3 ? 0 : BoundAddress(c, sn);
            string at = D3D9Programs.Addressed(cs, fix, dims);
            string g = dims == 2 ? ".xy" : ".xyz";
            string e = type switch
            {
                7 => $"vec4<f32>(textureSampleCompareLevel({tex}, {smp}, {at}, {(mode == 1 ? $"({coord}).z / ({coord}).w" : $"({coord}).z")}))",
                6 when mode == 3 => $"vec4<f32>(textureSampleLevel({tex}, {smp}, {at}, i32(({coord}).w)))",
                6 => $"vec4<f32>(textureSample({tex}, {smp}, {at}))",
                _ => mode switch
                {
                    3 => $"textureSampleLevel({tex}, {smp}, {at}, ({coord}).w)",
                    4 => $"textureSampleGrad({tex}, {smp}, {at}, ({dx}){g}, ({dy}){g})",
                    2 => $"textureSampleBias({tex}, {smp}, {at}, ({coord}).w)",
                    _ => $"textureSample({tex}, {smp}, {at})",
                },
            };
            e = $"{(c.Vs ? "vfx" : "fx")}{sn}({e})";
            return D3D9Programs.Bordered(e, cs, fix, dims, sn);
        }

        /// <summary>The bump-mapped coordinate of stage <paramref name="stage"/>: (u, v) moved by the
        /// stage's D3DTSS_BUMPENVMAT and the perturbation's (r, g).</summary>
        private static string Bumped(int stage, string uv, string du) =>
            $"vec4<f32>(({uv}).x + M.bump[{stage}].x * ({du}).x + M.bump[{stage}].z * ({du}).y, ({uv}).y + M.bump[{stage}].y * ({du}).x + M.bump[{stage}].w * ({du}).y, 0.0, 0.0)";

        /// <summary>The ps_1_x texture-addressing instructions (and texld, texcrd and bem of ps_1_4).</summary>
        private static bool Ps1Instruction(Ctx c, int op, DstReg d, ref Params ps)
        {
            int n = d.Num;
            string Tn(int k) => Use(c, $"t{k}");
            // the dot product of a matrix row (the texture coordinate set of the destination register) with the source
            string Row(int k, string src) => $"dot(({Tn(k)}).xyz, ({src}).xyz)";
            switch (op)
            {
                case Tex when c.Minor >= 4:
                    Write(c, d, Sample(c, n, Src(c, ref ps), 2), null);
                    return true;
                case Tex:
                    Write(c, d, Sample(c, n, Tn(n), 2), null);
                    return true;
                case TexCoord when c.Minor >= 4:
                    Write(c, d, $"vec4<f32>(({Src(c, ref ps)}).xyz, 1.0)", null);
                    return true;
                case TexCoord:
                    Write(c, d, $"vec4<f32>(clamp(({Tn(n)}).xyz, vec3<f32>(0.0), vec3<f32>(1.0)), 1.0)", null);
                    return true;
                case TexBem:
                case TexBemL:
                {
                    string du = Src(c, ref ps);
                    string e = Sample(c, n, Bumped(n, Tn(n), du), 2);
                    if (op == TexBemL) e = $"({e} * vec4<f32>(vec3<f32>(clamp(({du}).z * M.bumpL[{n}].x + M.bumpL[{n}].y, 0.0, 1.0)), 1.0))";
                    Write(c, d, e, null);
                    return true;
                }
                case Bem:
                {
                    string a = Src(c, ref ps), du = Src(c, ref ps);
                    Write(c, d, Bumped(n, a, du), null);
                    return true;
                }
                case TexReg2AR: { string a = Src(c, ref ps); Write(c, d, Sample(c, n, $"vec4<f32>(({a}).w, ({a}).x, 0.0, 0.0)", 2), null); return true; }
                case TexReg2GB: { string a = Src(c, ref ps); Write(c, d, Sample(c, n, $"vec4<f32>(({a}).y, ({a}).z, 0.0, 0.0)", 2), null); return true; }
                case TexReg2RGB: { string a = Src(c, ref ps); Write(c, d, Sample(c, n, $"vec4<f32>(({a}).xyz, 0.0)", 3), null); return true; }
                case TexM3x2Pad:
                case TexM3x3Pad:
                {
                    // a row of the matrix: its dot product, and the eye vector's component (the set's w) for texm3x3vspec
                    string a = Src(c, ref ps);
                    c.Line($"{Use(c, $"q{n}")} = vec4<f32>({Row(n, a)}, ({Tn(n)}).w, 0.0, 0.0);");
                    return true;
                }
                case TexM3x2Tex:
                {
                    string a = Src(c, ref ps);
                    Write(c, d, Sample(c, n, $"vec4<f32>({Use(c, $"q{n - 1}")}.x, {Row(n, a)}, 0.0, 0.0)", 2), null);
                    return true;
                }
                case TexM3x2Depth:
                {
                    string a = Src(c, ref ps);
                    c.S.WritesDepth = true;
                    c.Line($"{{ let _z = {Use(c, $"q{n - 1}")}.x; let _w = {Row(n, a)}; {Use(c, "oDepth")} = vec4<f32>(select(_z / _w, 1.0, _w == 0.0)); }}");
                    return true;
                }
                case TexM3x3Tex:
                case TexM3x3:
                case TexM3x3Spec:
                case TexM3x3VSpec:
                {
                    string a = Src(c, ref ps);
                    string nv = $"vec3<f32>({Use(c, $"q{n - 2}")}.x, {Use(c, $"q{n - 1}")}.x, {Row(n, a)})";
                    if (op == TexM3x3) { Write(c, d, $"vec4<f32>({nv}, 1.0)", null); return true; }
                    if (op == TexM3x3Tex) { Write(c, d, Sample(c, n, $"vec4<f32>({nv}, 0.0)", 3), null); return true; }
                    // the reflection of the eye vector (a constant, or the sets' w) about the normal
                    string eye = op == TexM3x3Spec ? $"({Src(c, ref ps)}).xyz" : $"vec3<f32>(q{n - 2}.y, q{n - 1}.y, ({Tn(n)}).w)";
                    string r = $"(2.0 * _n * dot(_n, _e) / dot(_n, _n) - _e)";
                    string tmp = Use(c, $"q{n}");
                    c.Line($"{{ let _n = {nv}; let _e = {eye}; {tmp} = vec4<f32>({r}, 0.0); }}");
                    Write(c, d, Sample(c, n, tmp, 3), null);
                    return true;
                }
                case TexDp3Tex:
                {
                    string a = Src(c, ref ps);
                    Write(c, d, Sample(c, n, $"vec4<f32>({Row(n, a)}, 0.0, 0.0, 0.0)", 2), null);
                    return true;
                }
                case TexDp3:
                {
                    string a = Src(c, ref ps);
                    Write(c, d, $"vec4<f32>({Row(n, a)})", null);
                    return true;
                }
                case TexDepth:
                {
                    c.S.WritesDepth = true;
                    string r5 = Use(c, $"r{n}");
                    c.Line($"{Use(c, "oDepth")} = vec4<f32>(select({r5}.x / {r5}.y, 1.0, {r5}.y == 0.0));");
                    return true;
                }
            }
            return false;
        }

        private static string PredCond(Ctx c, uint t)
        {
            c.UsesP0 = true;
            string e = "p0." + "xyzw"[(int)((t >> 16) & 3)];
            return ((t >> 24) & 0xF) == 13 ? "!" + e : e;
        }

        private static void Declare(Ctx c, ReadOnlySpan<uint> args)
        {
            uint u = args[0], r = args[1];
            int type = RegType(r), n = RegNum(r), mask = (int)((r >> 16) & 0xF);
            if (mask == 0) mask = 0xF;
            if (type == RSampler) { c.S.Samplers[n] = (int)((u >> 27) & 0xF); return; }
            int usage = (int)(u & 0x1F), index = (int)((u >> 16) & 0xF);
            if (c.Vs)
            {
                if (type == RInput) { Use(c, $"v{n}"); c.S.Inputs.Add(new D3D9ShaderIo { Register = n, Usage = usage, Index = index, Mask = mask }); }
                else if (type == ROutput && c.Major >= 3) { Use(c, $"o{n}"); c.S.Outputs.Add(new D3D9ShaderIo { Register = n, Usage = usage, Index = index, Mask = mask }); }
                return;
            }
            if (c.Major >= 3)
            {
                if (type == RInput) { Use(c, $"v{n}"); c.S.Inputs.Add(new D3D9ShaderIo { Register = n, Usage = usage, Index = index, Mask = mask }); }
                else if (type == RMisc) { if (n == 0) { c.S.UsesVPos = true; Use(c, "vPos"); } else { c.S.UsesVFace = true; Use(c, "vFace"); } }
                return;
            }
            // ps_2_x: v# colours, t# texture coordinates
            if (type == RInput) { Use(c, $"v{n}"); c.S.Inputs.Add(new D3D9ShaderIo { Register = n, Usage = D3D9Usage.Color, Index = n, Mask = mask }); }
            else if (type == RAddr) { Use(c, $"t{n}"); c.S.Inputs.Add(new D3D9ShaderIo { Register = 100 + n, Usage = D3D9Usage.TexCoord, Index = n, Mask = mask }); }
        }

        /// <summary>The register a ps_2_x input or a vs_1/2 output lives in, by its synthetic number.</summary>
        public static string IoName(D3D9Shader s, D3D9ShaderIo io, bool output)
        {
            if (output)
            {
                if (s.Major >= 3) return $"o{io.Register}";
                return io.Register switch { -1 => "oPos", -2 => "oFog", -3 => "oPts", >= 300 => $"oT{io.Register - 300}", _ => $"oD{io.Register - 200}" };
            }
            if (!s.Pixel || s.Major >= 3) return $"v{io.Register}";
            return io.Register >= 100 ? $"t{io.Register - 100}" : $"v{io.Register}";
        }

        /// <summary>lit's helper (D3D9: x 1, y the diffuse, z the specular, w 1).</summary>
        public const string LitHelper =
            "fn d3dLit(s : vec4<f32>) -> vec4<f32> {\n" +
            "    let p = clamp(s.w, -128.0, 128.0);\n" +
            "    var z = 0.0;\n" +
            "    if (s.x > 0.0 && s.y > 0.0) { z = pow(s.y, p); }\n" +
            "    return vec4<f32>(1.0, max(s.x, 0.0), z, 1.0);\n}\n";
    }
}
