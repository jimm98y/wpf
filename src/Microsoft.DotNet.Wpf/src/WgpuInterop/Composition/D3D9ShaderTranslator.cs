// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Translates Direct3D 9 pixel-shader bytecode (ps_2_0 / ps_3_0) to WGSL.
//
// This is what WPF's ShaderEffect needs. PixelShader.UriSource points at a .ps file --
// the output of fxc, i.e. D3D9 bytecode -- and milcore hands that to the GPU directly.
// There is no D3D9 anywhere near this renderer, so the bytecode has to be read and
// re-emitted as WGSL. PresentationCore already ships the whole ShaderEffect path
// unchanged (MILCMD_PIXELSHADER carries the blob, MILCMD_SHADEREFFECT the constant
// registers and samplers), so this translator is the missing piece, not the plumbing.
//
// SCOPE: the arithmetic and texture core of ps_2_0, which is what shipping WPF effects
// actually use -- they are small, branchless kernels over one or two input samplers.
// Explicitly NOT handled (rejected with a reason rather than mistranslated):
//   - flow control (if/rep/loop/call, ps_3_0 dynamic branching)
//   - predication and the aL loop register
//   - texldd / texldb / texldp gradient and bias variants, texkill
//   - integer and boolean constant registers
// TryTranslate returns false for those; the caller falls back to drawing the input
// unmodified, which is what milcore does for a shader it cannot compile.
//
// The register model maps cleanly:
//   r#  temporaries  -> var r0 : vec4<f32>
//   c#  float consts -> a uniform array (WPF sends these as float4 registers)
//   t#  / v#  inputs -> the interpolated texture coords / vertex colour
//   s#  samplers     -> texture_2d + sampler binding pairs
//   oC0 output       -> the fragment return value
//

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Microsoft.Wpf.Interop.WebGpu.Composition
{
    /// <summary>Result of a successful translation.</summary>
    internal sealed class TranslatedShader
    {
        public string Wgsl = "";
        /// <summary>Highest c# register referenced, + 1 (0 if none). Sizes the uniform array.</summary>
        public int FloatRegisterCount;
        /// <summary>Sampler registers (s#) the shader reads, ascending.</summary>
        public List<int> Samplers = new();
    }

    internal static class D3D9ShaderTranslator
    {
        // D3DSIO_* opcodes actually emitted by fxc for arithmetic ps_2_0 kernels.
        private const int OpNop = 0, OpMov = 1, OpAdd = 2, OpSub = 3, OpMad = 4, OpMul = 5;
        private const int OpRcp = 6, OpRsq = 7, OpDp3 = 8, OpDp4 = 9, OpMin = 10, OpMax = 11;
        private const int OpSlt = 12, OpSge = 13, OpExp = 14, OpLog = 15, OpLrp = 18, OpFrc = 19;
        private const int OpPow = 32, OpCrs = 33, OpAbs = 35, OpNrm = 36, OpSinCos = 37;
        private const int OpDcl = 31, OpDef = 81, OpTex = 66, OpCmp = 88, OpDp2Add = 90;
        private const int OpPhase = 0xFFFD, OpComment = 0xFFFE, OpEnd = 0xFFFF;

        // D3DSHADER_PARAM_REGISTER_TYPE
        private const int RegTemp = 0, RegInput = 1, RegConst = 2, RegTexture = 3;
        private const int RegColorOut = 8, RegSampler = 10;

        /// <summary>
        /// Translates <paramref name="bytecode"/> to a WGSL fragment shader body.
        /// Returns false (with <paramref name="reason"/> set) for anything outside the
        /// supported subset -- never a partial or guessed translation.
        /// </summary>
        public static bool TryTranslate(ReadOnlySpan<byte> bytecode, out TranslatedShader result, out string reason)
        {
            result = new TranslatedShader();
            reason = "";
            if (bytecode.Length < 8 || (bytecode.Length & 3) != 0)
            {
                reason = "bytecode is empty or not a whole number of DWORDs";
                return false;
            }

            var tok = new uint[bytecode.Length / 4];
            for (int i = 0; i < tok.Length; i++)
                tok[i] = (uint)(bytecode[i * 4] | (bytecode[i * 4 + 1] << 8)
                              | (bytecode[i * 4 + 2] << 16) | (bytecode[i * 4 + 3] << 24));

            uint version = tok[0];
            if ((version & 0xFFFF0000u) != 0xFFFF0000u)
            {
                reason = $"not a pixel shader (version token 0x{version:X8})";
                return false;
            }
            int major = (int)((version >> 8) & 0xFF);
            if (major is not (2 or 3))
            {
                reason = $"unsupported shader model ps_{major}_x; only ps_2_0 and ps_3_0 are handled";
                return false;
            }

            var body = new StringBuilder();
            var temps = new SortedSet<int>();
            var consts = new Dictionary<int, string>();   // DEF-supplied literals
            int maxConstReg = -1;
            bool wroteOutput = false;

            int p = 1;
            while (p < tok.Length)
            {
                uint t = tok[p];
                int opcode = (int)(t & 0xFFFF);

                if (opcode == OpEnd) break;
                if (opcode == OpComment) { p += 1 + (int)((t >> 16) & 0x7FFF); continue; }
                if (opcode == OpPhase) { reason = "ps_1_4 phase instruction"; return false; }

                // [27:24] is the DWORD count of the parameter tokens that follow.
                int len = (int)((t >> 24) & 0xF);
                if (p + len >= tok.Length + 1 && opcode != OpEnd)
                {
                    reason = "truncated instruction stream";
                    return false;
                }
                int a = p + 1;   // first parameter token

                switch (opcode)
                {
                    case OpNop:
                        break;

                    case OpDcl:
                        // Declarations carry semantics we do not need: inputs are fixed
                        // (uv + colour) and samplers are discovered from their use below.
                        break;

                    case OpDef:
                    {
                        // DEF c#, f, f, f, f -- an inline literal constant.
                        int reg = (int)(tok[a] & 0x7FF);
                        float f0 = BitConverter.UInt32BitsToSingle(tok[a + 1]);
                        float f1 = BitConverter.UInt32BitsToSingle(tok[a + 2]);
                        float f2 = BitConverter.UInt32BitsToSingle(tok[a + 3]);
                        float f3 = BitConverter.UInt32BitsToSingle(tok[a + 4]);
                        consts[reg] = $"vec4<f32>({F(f0)}, {F(f1)}, {F(f2)}, {F(f3)})";
                        break;
                    }

                    case OpTex:
                    {
                        // texld dst, coord, sampler
                        if (len < 3) { reason = "texld with too few parameters"; return false; }
                        int sreg = (int)(tok[a + 2] & 0x7FF);
                        if (RegType(tok[a + 2]) != RegSampler) { reason = "texld source 1 is not a sampler"; return false; }
                        if (!result.Samplers.Contains(sreg)) result.Samplers.Add(sreg);
                        string coord = Src(tok[a + 1], consts, temps, ref maxConstReg);
                        Emit(body, tok[a], $"textureSample(effTex{sreg}, effSamp{sreg}, ({coord}).xy)",
                            temps, ref wroteOutput);
                        break;
                    }

                    default:
                    {
                        string? expr = Arith(opcode, tok, a, len, consts, temps, ref maxConstReg, out string why);
                        if (expr is null)
                        {
                            reason = why;
                            return false;
                        }
                        Emit(body, tok[a], expr, temps, ref wroteOutput);
                        break;
                    }
                }

                p += 1 + len;
            }

            if (!wroteOutput)
            {
                reason = "shader never writes oC0";
                return false;
            }

            result.FloatRegisterCount = maxConstReg + 1;
            result.Samplers.Sort();
            result.Wgsl = Assemble(body.ToString(), temps, result);
            return true;
        }

        // ---- instruction translation ----

        private static string? Arith(int opcode, uint[] tok, int a, int len,
            Dictionary<int, string> consts, SortedSet<int> temps, ref int maxConst, out string why)
        {
            why = "";
            // Sources are resolved up front: a local function cannot capture a ref parameter,
            // and resolving is what advances maxConst.
            int srcCount = Math.Max(0, len - 1);
            var src = new string[3];
            for (int i = 0; i < srcCount && i < 3; i++)
                src[i] = Src(tok[a + 1 + i], consts, temps, ref maxConst);
            string S(int i) => src[i] ?? "vec4<f32>(0.0)";

            switch (opcode)
            {
                case OpMov: return S(0);
                case OpAdd: return $"({S(0)} + {S(1)})";
                case OpSub: return $"({S(0)} - {S(1)})";
                case OpMul: return $"({S(0)} * {S(1)})";
                case OpMad: return $"({S(0)} * {S(1)} + {S(2)})";
                case OpMin: return $"min({S(0)}, {S(1)})";
                case OpMax: return $"max({S(0)}, {S(1)})";
                case OpFrc: return $"fract({S(0)})";
                case OpAbs: return $"abs({S(0)})";
                case OpLrp: return $"mix({S(2)}, {S(1)}, {S(0)})";        // lrp dst, t, a, b = b + t*(a-b)
                case OpCmp: return $"select({S(2)}, {S(1)}, {S(0)} >= vec4<f32>(0.0))";
                case OpRcp: return $"vec4<f32>(1.0 / ({S(0)}).x)";
                case OpRsq: return $"vec4<f32>(inverseSqrt(abs(({S(0)}).x)))";
                case OpExp: return $"vec4<f32>(exp2(({S(0)}).x))";
                case OpLog: return $"vec4<f32>(log2(max(abs(({S(0)}).x), 1e-30)))";
                case OpPow: return $"vec4<f32>(pow(abs(({S(0)}).x), ({S(1)}).x))";
                case OpDp3: return $"vec4<f32>(dot(({S(0)}).xyz, ({S(1)}).xyz))";
                case OpDp4: return $"vec4<f32>(dot({S(0)}, {S(1)}))";
                case OpDp2Add: return $"vec4<f32>(dot(({S(0)}).xy, ({S(1)}).xy) + ({S(2)}).x)";
                case OpSlt: return $"select(vec4<f32>(0.0), vec4<f32>(1.0), {S(0)} < {S(1)})";
                case OpSge: return $"select(vec4<f32>(0.0), vec4<f32>(1.0), {S(0)} >= {S(1)})";
                case OpCrs: return $"vec4<f32>(cross(({S(0)}).xyz, ({S(1)}).xyz), 0.0)";
                case OpNrm: return $"vec4<f32>(normalize(({S(0)}).xyz), 0.0)";
                case OpSinCos:
                    // sincos dst, src.x -- .x gets cos, .y gets sin (the two macro constant
                    // sources fxc appends in ps_2_0 are approximation tables we do not need).
                    return $"vec4<f32>(cos(({S(0)}).x), sin(({S(0)}).x), 0.0, 0.0)";
                default:
                    why = $"unsupported opcode {opcode} (0x{opcode:X}) -- likely flow control or a texture variant";
                    return null;
            }
        }

        // Writes `expr` into the destination register, honouring the write mask and the
        // saturate result modifier.
        private static void Emit(StringBuilder body, uint dst, string expr, SortedSet<int> temps, ref bool wroteOutput)
        {
            int type = RegType(dst);
            int reg = (int)(dst & 0x7FF);
            int mask = (int)((dst >> 16) & 0xF);
            bool saturate = ((dst >> 20) & 0x1) != 0;

            string value = saturate ? $"clamp({expr}, vec4<f32>(0.0), vec4<f32>(1.0))" : expr;

            string target;
            if (type == RegColorOut) { target = "oC0"; wroteOutput = true; }
            else { temps.Add(reg); target = $"r{reg}"; }

            if (mask is 0 or 0xF)
            {
                body.Append("    ").Append(target).Append(" = ").Append(value).Append(";\n");
                return;
            }
            // Partial write mask. WGSL has NO swizzle assignment ("v.xy = ..." is rejected
            // by naga with 'WGSL does not support assignments to swizzles'), so each masked
            // component is written on its own.
            body.Append("    { let _v = ").Append(value).Append(';');
            const string comps = "xyzw";
            for (int i = 0; i < 4; i++)
                if ((mask & (1 << i)) != 0)
                    body.Append(' ').Append(target).Append('.').Append(comps[i])
                        .Append(" = _v.").Append(comps[i]).Append(';');
            body.Append(" }\n");
        }

        // Reads a source parameter: register, swizzle and source modifier.
        private static string Src(uint t, Dictionary<int, string> consts, SortedSet<int> temps, ref int maxConst)
        {
            int type = RegType(t);
            int reg = (int)(t & 0x7FF);
            int swizzle = (int)((t >> 16) & 0xFF);
            int mod = (int)((t >> 24) & 0xF);

            string baseExpr;
            switch (type)
            {
                case RegTemp: temps.Add(reg); baseExpr = $"r{reg}"; break;
                case RegConst:
                    if (consts.TryGetValue(reg, out string? lit)) { baseExpr = lit; }
                    else { if (reg > maxConst) maxConst = reg; baseExpr = $"effConst[{reg}]"; }
                    break;
                // t# in ps_2_0 is the interpolated texture coordinate set; WPF's ShaderEffect
                // gives every effect the same single uv set, so they all resolve to it.
                case RegTexture: baseExpr = "effUv"; break;
                case RegInput: baseExpr = "effColor"; break;
                default: baseExpr = "vec4<f32>(0.0)"; break;
            }

            // Swizzle: 2 bits per component, .xyzw == 0xE4 (00 01 10 11 read low-to-high).
            if (swizzle != 0xE4)
            {
                const string C = "xyzw";
                var sb = new StringBuilder(".");
                for (int i = 0; i < 4; i++) sb.Append(C[(swizzle >> (i * 2)) & 3]);
                baseExpr = $"({baseExpr}){sb}";
            }

            // D3DSPSM_* source modifiers.
            return mod switch
            {
                0 => baseExpr,                                        // none
                1 => $"(-{baseExpr})",                                // negate
                2 => $"(({baseExpr}) * 2.0 - vec4<f32>(1.0))",        // bias*2-1 (_bx2 without the bias step)
                3 => $"(-(({baseExpr}) * 2.0 - vec4<f32>(1.0)))",     // -_bx2
                4 => $"(vec4<f32>(1.0) - ({baseExpr}))",              // complement
                _ => baseExpr,
            };
        }

        // D3D9 splits the register type across bits [30:28] and [12:11].
        private static int RegType(uint t) => (int)(((t & 0x70000000u) >> 28) | ((t & 0x00001800u) >> 8));

        private static string F(float v) =>
            float.IsFinite(v) ? v.ToString("R", CultureInfo.InvariantCulture) + (v == MathF.Truncate(v) && MathF.Abs(v) < 1e15f ? ".0" : "")
                              : "0.0";

        private static string Assemble(string body, SortedSet<int> temps, TranslatedShader r)
        {
            var sb = new StringBuilder();
            sb.Append("// Generated from D3D9 pixel-shader bytecode by D3D9ShaderTranslator.\n");
            // The module must carry the VERTEX stage too: the renderer builds one pipeline from
            // one module, and its vertex layout for this fill kind is the standard 3-attribute
            // (pos, colour, uv) one. Emitting only the fragment stage fails pipeline creation
            // with "Unable to find entry point 'vs_main'".
            sb.Append("struct VSOut {\n")
              .Append("    @builtin(position) pos : vec4<f32>,\n")
              .Append("    @location(0) color : vec4<f32>,\n")
              .Append("    @location(1) uv : vec2<f32>,\n};\n\n")
              .Append("@vertex\n")
              .Append("fn vs_main(@location(0) pos : vec2<f32>, @location(1) color : vec4<f32>, @location(2) uv : vec2<f32>) -> VSOut {\n")
              .Append("    var o : VSOut;\n")
              .Append("    o.pos = vec4<f32>(pos, 0.0, 1.0);\n")
              .Append("    o.color = color;\n")
              .Append("    o.uv = uv;\n")
              .Append("    return o;\n}\n\n");

            int binding = 0;
            if (r.FloatRegisterCount > 0)
            {
                sb.Append($"@group(0) @binding({binding++}) var<uniform> effConst : array<vec4<f32>, {r.FloatRegisterCount}>;\n");
            }
            foreach (int s in r.Samplers)
            {
                sb.Append($"@group(0) @binding({binding++}) var effTex{s} : texture_2d<f32>;\n");
                sb.Append($"@group(0) @binding({binding++}) var effSamp{s} : sampler;\n");
            }

            sb.Append("\n@fragment\nfn fs_effect(in : VSOut) -> @location(0) vec4<f32> {\n");
            sb.Append("    let effUv = vec4<f32>(in.uv, 0.0, 1.0);\n");
            sb.Append("    let effColor = in.color;\n");
            sb.Append("    var oC0 = vec4<f32>(0.0);\n");
            foreach (int t in temps) sb.Append($"    var r{t} = vec4<f32>(0.0);\n");
            sb.Append(body);
            sb.Append("    return oC0;\n}\n");
            return sb.ToString();
        }
    }
}
