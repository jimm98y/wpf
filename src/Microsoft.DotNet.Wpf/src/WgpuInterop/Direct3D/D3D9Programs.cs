// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The D3D9 device's WGSL programs: the entry points of translated shaders (D3D9Shaders.cs) and the
// fixed-function pipeline, generated from the state that shapes it -- the vertex's elements,
// transforms, lights and materials, texture coordinate generation and the texture stages, fog and the
// alpha test -- as Direct3D 9 defines them.
//
// The stages meet through semantics: the pixel stage names what it reads (COLOR0, TEXCOORD3, FOG...)
// and gets locations in that order; the vertex stage writes those it has and zero for the rest.
//
// Bindings (group 0): 0 the vertex shader's constants, 1 the pixel shader's, 2 the device state both
// stages read (Misc below), 16 + 2s / 17 + 2s the pixel sampler s's texture and sampler, 48 + 2s /
// 49 + 2s the vertex sampler s's.
//

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using static Microsoft.Wpf.Interop.WebGpu.Wgpu;

namespace Microsoft.Wpf.Interop.WebGpu.Direct3D
{
    internal static class D3D9Programs
    {
        public const int MiscBinding = 2, PsTextureBase = 16, VsTextureBase = 48;
        public const int FogKey = D3D9Usage.Fog * 16, FogDepthKey = D3D9Usage.Depth * 16;
        /// <summary>The distances to user clip planes 0-3 and 4-5 (no D3D9 semantic has these keys).</summary>
        public const int ClipKeyA = 15 * 16, ClipKeyB = 15 * 16 + 1;

        // ---- the device state block (Misc) ---------------------------------------------------------

        public const int OffPx = 0, OffVp = 16, OffAlpha = 32, OffFog = 48, OffFogColor = 64, OffTFactor = 80, OffAmbient = 96,
            OffMatDiffuse = 112, OffMatAmbient = 128, OffMatSpecular = 144, OffMatEmissive = 160, OffMatPower = 176,
            OffWv = 192, OffProj = 256, OffWvp = 320, OffNrm = 384, OffTex = 448, OffStageConst = 960, OffBump = 1088, OffBumpL = 1216,
            OffLights = 1344, LightStride = 112, OffAdj = 2240, OffClip = 2256, OffPoint = 2352, OffPointScale = 2368, MiscSize = 2384;

        public const string MiscWgsl =
            "struct Light { diffuse : vec4<f32>, specular : vec4<f32>, ambient : vec4<f32>, pos : vec4<f32>, dir : vec4<f32>, att : vec4<f32>, spot : vec4<f32> };\n" +
            "struct Misc {\n" +
            "    px : vec4<f32>, vp : vec4<f32>, alpha : vec4<f32>, fog : vec4<f32>, fogColor : vec4<f32>, tfactor : vec4<f32>, ambient : vec4<f32>,\n" +
            "    matDiffuse : vec4<f32>, matAmbient : vec4<f32>, matSpecular : vec4<f32>, matEmissive : vec4<f32>, matPower : vec4<f32>,\n" +
            "    wv : mat4x4<f32>, proj : mat4x4<f32>, wvp : mat4x4<f32>, nrm : mat4x4<f32>, tex : array<mat4x4<f32>, 8>,\n" +
            "    stageConst : array<vec4<f32>, 8>, bump : array<vec4<f32>, 8>, bumpL : array<vec4<f32>, 8>, lights : array<Light, 8>,\n" +
            "    adj : vec4<f32>, clip : array<vec4<f32>, 6>, pt : vec4<f32>, ptScale : vec4<f32>,\n" +
            "};\n" +
            "@group(0) @binding(2) var<uniform> M : Misc;\n";

        public const string ConstsWgsl = "struct Consts { f : array<vec4<f32>, 256>, i : array<vec4<i32>, 16>, b : array<vec4<u32>, 4> };\n";
        public const int ConstsSize = 256 * 16 + 16 * 16 + 4 * 16;

        // ---- vertex input --------------------------------------------------------------------------

        /// <summary>A vertex element as a WebGPU attribute: its format, the WGSL type it arrives as and the
        /// expression that makes the D3D9 value (a vec4&lt;f32&gt;) of it.</summary>
        public static (WGPUVertexFormat Format, string Type, string Expr) Attribute(byte type, string v) => type switch
        {
            Direct3DVertexDeclaration9.FLOAT1 => (VtxFmt.Float32, "vec4<f32>", v),
            Direct3DVertexDeclaration9.FLOAT2 => (VtxFmt.Float32x2, "vec4<f32>", v),
            Direct3DVertexDeclaration9.FLOAT3 => (VtxFmt.Float32x3, "vec4<f32>", v),
            Direct3DVertexDeclaration9.FLOAT4 => (VtxFmt.Float32x4, "vec4<f32>", v),
            Direct3DVertexDeclaration9.D3DCOLOR => (VtxFmt.Unorm8x4, "vec4<f32>", $"{v}.zyxw"),
            Direct3DVertexDeclaration9.UBYTE4 => (VtxFmt.Uint8x4, "vec4<u32>", $"vec4<f32>({v})"),
            Direct3DVertexDeclaration9.UBYTE4N => (VtxFmt.Unorm8x4, "vec4<f32>", v),
            Direct3DVertexDeclaration9.SHORT2 => (VtxFmt.Sint16x2, "vec4<i32>", $"vec4<f32>({v})"),
            Direct3DVertexDeclaration9.SHORT4 => (VtxFmt.Sint16x4, "vec4<i32>", $"vec4<f32>({v})"),
            Direct3DVertexDeclaration9.SHORT2N => (VtxFmt.Snorm16x2, "vec4<f32>", v),
            Direct3DVertexDeclaration9.SHORT4N => (VtxFmt.Snorm16x4, "vec4<f32>", v),
            Direct3DVertexDeclaration9.USHORT2N => (VtxFmt.Unorm16x2, "vec4<f32>", v),
            Direct3DVertexDeclaration9.USHORT4N => (VtxFmt.Unorm16x4, "vec4<f32>", v),
            Direct3DVertexDeclaration9.UDEC3 => (VtxFmt.Uint32, "u32", $"vec4<f32>(f32({v} & 1023u), f32(({v} >> 10u) & 1023u), f32(({v} >> 20u) & 1023u), 1.0)"),
            Direct3DVertexDeclaration9.DEC3N => (VtxFmt.Uint32, "u32", $"vec4<f32>(max(f32((bitcast<i32>({v}) << 22u) >> 22u) / 511.0, -1.0), max(f32((bitcast<i32>({v}) << 12u) >> 22u) / 511.0, -1.0), max(f32((bitcast<i32>({v}) << 2u) >> 22u) / 511.0, -1.0), 1.0)"),
            Direct3DVertexDeclaration9.FLOAT16_2 => (VtxFmt.Float16x2, "vec4<f32>", v),
            Direct3DVertexDeclaration9.FLOAT16_4 => (VtxFmt.Float16x4, "vec4<f32>", v),
            _ => ((WGPUVertexFormat)0, "", ""),
        };

        /// <summary>The location the fixed-function vertex stage reads an element at (-1: not read).</summary>
        public static int FixedLocation(int usage, int index) => usage switch
        {
            D3D9Usage.Position or D3D9Usage.PositionT when index == 0 => 0,
            D3D9Usage.Normal when index == 0 => 1,
            D3D9Usage.PSize => 2,
            D3D9Usage.Color when index < 2 => 3 + index,
            D3D9Usage.TexCoord when index < 8 => 5 + index,
            _ => -1,
        };

        // ---- texture reads -------------------------------------------------------------------------

        /// <summary>How a bound texture's texels read as D3D9 has them: a format's missing channels are 1
        /// (alpha of X8R8G8B8, G and B of R32F), luminance repeats, A8 is alpha only; 7: no texture.</summary>
        public static int FixupOf(int d3d9Format) => d3d9Format switch
        {
            D3D9Const.D3DFMT_X8R8G8B8 or D3D9Const.D3DFMT_X8B8G8R8 => 1,
            D3D9Const.D3DFMT_L8 => 2,
            D3D9Const.D3DFMT_A8 => 3,
            D3D9Const.D3DFMT_R32F or D3D9Const.D3DFMT_R16F => 4,
            D3D9Const.D3DFMT_G16R16F => 5,
            _ => 0,
        };

        private static string FixupFn(string name, int kind) => $"fn {name}(c : vec4<f32>) -> vec4<f32> {{ return " + kind switch
        {
            1 => "vec4<f32>(c.rgb, 1.0)",
            2 => "vec4<f32>(c.r, c.r, c.r, 1.0)",
            3 => "vec4<f32>(0.0, 0.0, 0.0, c.r)",
            4 => "vec4<f32>(c.r, 1.0, 1.0, 1.0)",
            5 => "vec4<f32>(c.r, c.g, 1.0, 1.0)",
            7 => "vec4<f32>(0.0, 0.0, 0.0, 1.0)",
            8 => "vec4<f32>(1.0)",
            _ => "c",
        } + "; }\n";

        private static string TextureType(int d3dType) => d3dType switch { 3 => "texture_cube<f32>", 4 => "texture_3d<f32>", _ => "texture_2d<f32>" };

        // ---- the inter-stage struct -----------------------------------------------------------------

        public sealed class Linkage
        {
            /// <summary>The semantics the pixel stage reads, in location order.</summary>
            public readonly List<int> Keys = new();
            public bool FlatColor;
            public int LocationOf(int key) => Keys.IndexOf(key);
            public string Id => (FlatColor ? "f" : "s") + string.Join(",", Keys);
        }

        private static void VaryingStruct(StringBuilder w, Linkage l)
        {
            w.Append("struct VOut {\n    @builtin(position) pos : vec4<f32>,\n");
            for (int i = 0; i < l.Keys.Count; i++)
            {
                bool color = l.Keys[i] / 16 == D3D9Usage.Color;
                w.Append("    @location(").Append(i).Append(')').Append(color && l.FlatColor ? " @interpolate(flat)" : "").Append(" l").Append(i).Append(" : vec4<f32>,\n");
            }
            w.Append("};\n");
        }

        /// <summary>The vertex entry's tail: the varyings from the provided expressions, the clip position
        /// with D3D9's half-pixel offset (pixel centres on integer coordinates).</summary>
        /// <summary>A point list drawn as squares (D3D9's point size), six vertices per point, the point's
        /// vertex an instance: 1 a square of the vertex's coordinates, 2 a sprite (POINTSPRITEENABLE: every
        /// texture coordinate runs 0-1 across it, v down).</summary>
        public struct Points
        {
            public int Mode;
            /// <summary>The point's size as the vertex gives it (an f32 expression; null: D3DRS_POINTSIZE).</summary>
            public string? Size;
            /// <summary>D3DRS_POINTSCALEENABLE applies (fixed-function untransformed vertices), from the eye distance of this.</summary>
            public string? ScaleFrom;
            public string Id => Mode == 0 ? "" : $"|pt{Mode}{(Size != null ? "s" : "")}{(ScaleFrom != null ? "z" : "")}";
        }

        /// <summary>The vertex entry's parameter the corners of a point's square come from.</summary>
        private const string CornerParam = "@builtin(vertex_index) corner : u32";

        private static void PointSquare(StringBuilder w, Linkage l, Points pts)
        {
            w.Append("    var sz = ").Append(pts.Size ?? "M.pt.x").Append(";\n");
            if (pts.ScaleFrom != null)
            {
                // D3D9: Ss = Vh * Si * sqrt(1 / (A + B * De + C * De^2)), De the distance to the eye
                w.Append("    let de = length((").Append(pts.ScaleFrom).Append(").xyz);\n");
                w.Append("    sz = M.vp.y * sz * sqrt(1.0 / max(M.ptScale.x + M.ptScale.y * de + M.ptScale.z * de * de, 1e-20));\n");
            }
            w.Append("    sz = clamp(sz, M.pt.y, M.pt.z);\n");
            w.Append("    var corners = array<vec2<f32>, 6>(vec2<f32>(0.0, 0.0), vec2<f32>(1.0, 0.0), vec2<f32>(0.0, 1.0), vec2<f32>(1.0, 0.0), vec2<f32>(1.0, 1.0), vec2<f32>(0.0, 1.0));\n");
            w.Append("    let uv = corners[corner % 6u];\n");
            w.Append("    p = vec4<f32>(p.x + (uv.x * 2.0 - 1.0) * sz / M.vp.x * p.w, p.y - (uv.y * 2.0 - 1.0) * sz / M.vp.y * p.w, p.z, p.w);\n");
            if (pts.Mode == 2)
                for (int i = 0; i < l.Keys.Count; i++)
                    if (l.Keys[i] / 16 == D3D9Usage.TexCoord) w.Append("    o.l").Append(i).Append(" = vec4<f32>(uv, 0.0, 1.0);\n");
        }

        private static void VertexTail(StringBuilder w, Linkage l, Dictionary<int, string> provided, string pos, bool halfPixel, string? clipFrom = null, Points pts = default)
        {
            w.Append("    var o : VOut;\n    var p = ").Append(pos).Append(";\n");
            // user clip planes: the distance to each, from the position in the planes' space (none: 1, never clipped)
            string cp = clipFrom ?? "p";
            if (clipFrom != "") { provided[ClipKeyA] = $"vec4<f32>(dot(M.clip[0], {cp}), dot(M.clip[1], {cp}), dot(M.clip[2], {cp}), dot(M.clip[3], {cp}))"; provided[ClipKeyB] = $"vec4<f32>(dot(M.clip[4], {cp}), dot(M.clip[5], {cp}), 1.0, 1.0)"; }
            else { provided[ClipKeyA] = "vec4<f32>(1.0)"; provided[ClipKeyB] = "vec4<f32>(1.0)"; }
            for (int i = 0; i < l.Keys.Count; i++)
            {
                int key = l.Keys[i];
                string e = key == FogDepthKey ? "vec4<f32>(p.w, p.z / select(p.w, 1.0, p.w == 0.0), 0.0, 0.0)"
                    : provided.TryGetValue(key, out string? x) ? x : "vec4<f32>(0.0)";
                w.Append("    o.l").Append(i).Append(" = ").Append(e).Append(";\n");
            }
            if (pts.Mode != 0) PointSquare(w, l, pts);
            if (halfPixel) w.Append("    p.x = p.x + p.w * M.px.x;\n    p.y = p.y - p.w * M.px.y;\n");
            // D3D9's viewport into the part of it on the targets (WebGPU's viewport lies within them)
            w.Append("    p = vec4<f32>(p.x * M.adj.x + M.adj.z * p.w, p.y * M.adj.y + M.adj.w * p.w, p.z, p.w);\n");
            w.Append("    o.pos = p;\n    return o;\n}\n");
        }

        // ---- the pixel epilogue: alpha test and fog --------------------------------------------------

        public struct PixelTail
        {
            /// <summary>D3DCMPFUNC of the alpha test (8 ALWAYS: none).</summary>
            public int AlphaFunc;
            /// <summary>0 none, 1 the vertex's fog factor, 2 table EXP, 3 table EXP2, 4 table LINEAR.</summary>
            public int Fog;
            /// <summary>Table fog by the eye distance (w) rather than the depth.</summary>
            public bool WFog;
            /// <summary>D3DRS_CLIPPLANEENABLE: the user clip planes that discard.</summary>
            public int ClipMask;
            public string Id => $"a{AlphaFunc}f{Fog}{(WFog ? "w" : "")}c{ClipMask}";
        }

        private static readonly string[] AlphaCompare = { "false", "false", "<", "==", "<=", ">", "!=", ">=", "true" };

        /// <summary>The user clip planes, before the shader: a pixel on the negative side of one is not drawn.</summary>
        private static void ApplyClip(StringBuilder w, Linkage l, PixelTail t)
        {
            if (t.ClipMask == 0) return;
            var conds = new List<string>();
            for (int i = 0; i < 6; i++)
                if ((t.ClipMask & (1 << i)) != 0) conds.Add($"in.l{l.LocationOf(i < 4 ? ClipKeyA : ClipKeyB)}.{"xyzw"[i & 3]} < 0.0");
            w.Append("    if (").Append(string.Join(" || ", conds)).Append(") { discard; }\n");
        }

        private static void ApplyTail(StringBuilder w, Linkage l, PixelTail t, string c)
        {
            if (t.AlphaFunc is >= 1 and < 8)
            {
                string cmp = AlphaCompare[t.AlphaFunc];
                if (t.AlphaFunc == 1) w.Append("    discard;\n");
                else w.Append("    if (!(round(clamp(").Append(c).Append(".a, 0.0, 1.0) * 255.0) ").Append(cmp).Append(" M.alpha.x)) { discard; }\n");
            }
            if (t.Fog == 0) return;
            if (t.Fog == 1)
            {
                int at = l.LocationOf(FogKey);
                w.Append("    let fogF = clamp(").Append(at >= 0 ? $"in.l{at}.x" : "1.0").Append(", 0.0, 1.0);\n");
            }
            else
            {
                int at = l.LocationOf(FogDepthKey);
                string d = at < 0 ? "0.0" : t.WFog ? $"in.l{at}.x" : $"in.l{at}.y";
                w.Append("    let fogD = ").Append(d).Append(";\n");
                w.Append("    let fogF = clamp(").Append(t.Fog switch
                {
                    2 => "exp(-fogD * M.fog.z)",
                    3 => "exp(-(fogD * M.fog.z) * (fogD * M.fog.z))",
                    _ => "(M.fog.y - fogD) / (M.fog.y - M.fog.x)",
                }).Append(", 0.0, 1.0);\n");
            }
            w.Append("    ").Append(c).Append(" = vec4<f32>(mix(M.fogColor.rgb, ").Append(c).Append(".rgb, fogF), ").Append(c).Append(".a);\n");
        }

        /// <summary>The keys the epilogue reads.</summary>
        private static void TailKeys(Linkage l, PixelTail t)
        {
            if ((t.ClipMask & 0xF) != 0 && !l.Keys.Contains(ClipKeyA)) l.Keys.Add(ClipKeyA);
            if ((t.ClipMask & 0x30) != 0 && !l.Keys.Contains(ClipKeyB)) l.Keys.Add(ClipKeyB);
            if (t.Fog == 1 && !l.Keys.Contains(FogKey)) l.Keys.Add(FogKey);
            if (t.Fog >= 2 && !l.Keys.Contains(FogDepthKey)) l.Keys.Add(FogDepthKey);
        }

        // ---- translated shaders --------------------------------------------------------------------

        /// <summary>The varyings a translated pixel shader reads, with the epilogue's.</summary>
        public static Linkage LinkageOf(D3D9Shader ps, PixelTail tail, bool flat)
        {
            var l = new Linkage { FlatColor = flat };
            foreach (var io in ps.Inputs)
                if (io.Usage != D3D9Usage.Position && !l.Keys.Contains(io.Key)) l.Keys.Add(io.Key);
            TailKeys(l, tail);
            return l;
        }

        /// <summary>The vertex module of a translated vertex shader: its attributes at their input
        /// registers' locations (<paramref name="inputs"/>: register, the element's type).</summary>
        public static string VertexShaderModule(D3D9Shader vs, Linkage l, IReadOnlyDictionary<int, byte> inputs, IReadOnlyDictionary<int, int> fixups, Points pts = default)
        {
            var w = new StringBuilder();
            w.Append("diagnostic(off, derivative_uniformity);\n").Append(ConstsWgsl).Append("@group(0) @binding(0) var<uniform> K : Consts;\n").Append(MiscWgsl);
            foreach (var (s, type) in vs.Samplers)
            {
                w.Append("@group(0) @binding(").Append(VsTextureBase + 2 * s).Append(") var vtex").Append(s).Append(" : ").Append(TextureType(type)).Append(";\n");
                w.Append("@group(0) @binding(").Append(VsTextureBase + 2 * s + 1).Append(") var vsmp").Append(s).Append(" : sampler;\n");
                w.Append(FixupFn($"vfx{s}", fixups.TryGetValue(s, out int k) ? k : 7));
            }
            w.Append(vs.Code);
            VaryingStruct(w, l);
            w.Append("@vertex\nfn main(");
            bool first = true;
            foreach (var (reg, type) in inputs)
            {
                var a = Attribute(type, $"a{reg}");
                if (!first) w.Append(", ");
                first = false;
                w.Append("@location(").Append(reg).Append(") a").Append(reg).Append(" : ").Append(a.Type);
            }
            if (pts.Mode != 0) w.Append(first ? "" : ", ").Append(CornerParam);
            w.Append(") -> VOut {\n");
            foreach (var (reg, type) in inputs) w.Append("    v").Append(reg).Append(" = ").Append(Attribute(type, $"a{reg}").Expr).Append(";\n");
            w.Append("    d3dMain();\n");
            var provided = new Dictionary<int, string>();
            string pos = "vec4<f32>(0.0, 0.0, 0.0, 1.0)";
            foreach (var io in vs.Outputs)
            {
                string reg = D3D9Bytecode.IoName(vs, io, output: true);
                if (io.Usage == D3D9Usage.Position && io.Index == 0) { pos = reg; continue; }
                string e = Packed(reg, io.Mask);
                if (vs.Major < 3 && io.Usage == D3D9Usage.Color) e = $"clamp({e}, vec4<f32>(0.0), vec4<f32>(1.0))";
                if (io.Usage == D3D9Usage.Fog) e = $"vec4<f32>(({reg}).x)";
                provided[io.Key] = e;
            }
            if (pts.Mode != 0) pts.Size = provided.TryGetValue(D3D9Usage.Key(D3D9Usage.PSize, 0), out string? ps) ? $"({ps}).x" : null;
            VertexTail(w, l, provided, pos, halfPixel: true, clipFrom: "p", pts: pts);
            return w.ToString();
        }

        /// <summary>A register's components a declaration masks, as a vec4 from its first component.</summary>
        private static string Packed(string reg, int mask)
        {
            if (mask is 0 or 0xF) return reg;
            var comps = new List<string>();
            for (int i = 0; i < 4; i++) if ((mask & (1 << i)) != 0) comps.Add($"{reg}.{"xyzw"[i]}");
            while (comps.Count < 3) comps.Add("0.0");
            if (comps.Count < 4) comps.Add("1.0");
            return $"vec4<f32>({string.Join(", ", comps)})";
        }

        /// <summary>The pixel module of a translated pixel shader.</summary>
        public static string PixelShaderModule(D3D9Shader ps, Linkage l, PixelTail tail, int targets, IReadOnlyDictionary<int, int> fixups)
        {
            var w = new StringBuilder();
            w.Append("diagnostic(off, derivative_uniformity);\n").Append(ConstsWgsl).Append("@group(0) @binding(1) var<uniform> K : Consts;\n").Append(MiscWgsl);
            foreach (var (s, type) in ps.Samplers)
            {
                w.Append("@group(0) @binding(").Append(PsTextureBase + 2 * s).Append(") var tex").Append(s).Append(" : ").Append(TextureType(type)).Append(";\n");
                w.Append("@group(0) @binding(").Append(PsTextureBase + 2 * s + 1).Append(") var smp").Append(s).Append(" : sampler;\n");
                w.Append(FixupFn($"fx{s}", fixups.TryGetValue(s, out int k) ? k : 7));
            }
            w.Append(ps.Code);
            VaryingStruct(w, l);
            FragmentOut(w, targets, ps.WritesDepth);
            w.Append("@fragment\nfn main(in : VOut, @builtin(front_facing) frontFacing : bool) -> FOut {\n");
            foreach (var io in ps.Inputs)
            {
                int at = l.LocationOf(io.Key);
                string reg = D3D9Bytecode.IoName(ps, io, output: false);
                if (at < 0) continue;
                if (io.Mask is 0 or 0xF) { w.Append("    ").Append(reg).Append(" = in.l").Append(at).Append(";\n"); continue; }
                int j = 0;
                for (int i = 0; i < 4; i++)
                    if ((io.Mask & (1 << i)) != 0) w.Append("    ").Append(reg).Append('.').Append("xyzw"[i]).Append(" = in.l").Append(at).Append('.').Append("xyzw"[j++]).Append(";\n");
            }
            ApplyClip(w, l, tail);
            if (ps.UsesVPos) w.Append("    vPos = vec4<f32>(in.pos.xy - vec2<f32>(0.5), 0.0, 0.0);\n");
            if (ps.UsesVFace) w.Append("    vFace = vec4<f32>(select(-1.0, 1.0, frontFacing));\n");
            w.Append("    d3dMain();\n    var f : FOut;\n");
            w.Append("    var c0 = ").Append((ps.ColorOutputs & 1) != 0 ? "oC0" : "vec4<f32>(0.0)").Append(";\n");
            ApplyTail(w, l, ps.Major >= 3 ? tail with { Fog = 0 } : tail, "c0");
            w.Append("    f.c0 = c0;\n");
            for (int i = 1; i < targets; i++) w.Append("    f.c").Append(i).Append(" = ").Append((ps.ColorOutputs & (1 << i)) != 0 ? $"oC{i}" : "vec4<f32>(0.0)").Append(";\n");
            if (ps.WritesDepth) w.Append("    f.depth = clamp(oDepth.x, 0.0, 1.0);\n");
            w.Append("    return f;\n}\n");
            return w.ToString();
        }

        private static void FragmentOut(StringBuilder w, int targets, bool depth)
        {
            w.Append("struct FOut {\n");
            for (int i = 0; i < Math.Max(1, targets); i++) w.Append("    @location(").Append(i).Append(") c").Append(i).Append(" : vec4<f32>,\n");
            if (depth) w.Append("    @builtin(frag_depth) depth : f32,\n");
            w.Append("};\n");
        }

        // ---- the fixed-function vertex stage ------------------------------------------------------

        public sealed class FixedVertex
        {
            public bool Transformed, Normal, Diffuse, Specular, PSize;
            /// <summary>0: points as points, else Points.Mode.</summary>
            public int PointMode;
            public bool PointScale;
            /// <summary>Components of each texture coordinate set the vertex has (0: none).</summary>
            public readonly int[] TexDims = new int[8];
            public readonly byte[] TexTypes = new byte[8];
            public byte PosType, NormalType, DiffuseType, SpecularType;
            public bool Lighting, LocalViewer, NormalizeNormals, SpecularEnable;
            /// <summary>D3DMCS of each material colour, resolved (a vertex colour the vertex lacks is the material's).</summary>
            public int DiffuseSrc, SpecularSrc, AmbientSrc, EmissiveSrc;
            /// <summary>The enabled lights' D3DLIGHTTYPE, in the order they are packed.</summary>
            public readonly List<int> Lights = new();
            /// <summary>D3DFOGMODE of vertex fog (0: the fog factor is the specular alpha), and range fog.</summary>
            public int FogMode;
            public bool RangeFog;
            /// <summary>Per output set: D3DTSS_TEXCOORDINDEX and D3DTSS_TEXTURETRANSFORMFLAGS.</summary>
            public readonly uint[] TexIndex = new uint[8], TexTransform = new uint[8];

            public string Id
            {
                get
                {
                    var sb = new StringBuilder("ff:");
                    sb.Append(Transformed ? 'T' : 't').Append(PosType).Append(Normal ? 'N' : 'n').Append(NormalType).Append(Diffuse ? 'D' : 'd').Append(DiffuseType).Append(Specular ? 'S' : 's').Append(SpecularType).Append(PSize ? 'Z' : 'z').Append(PointMode).Append(PointScale ? 'C' : 'c');
                    for (int i = 0; i < 8; i++) sb.Append(TexDims[i]).Append(TexTypes[i]).Append('.').Append(TexIndex[i]).Append('.').Append(TexTransform[i]).Append(';');
                    sb.Append(Lighting ? 'L' : 'l').Append(LocalViewer ? 'V' : 'v').Append(NormalizeNormals ? 'Z' : 'z').Append(SpecularEnable ? 'P' : 'p');
                    sb.Append(DiffuseSrc).Append(SpecularSrc).Append(AmbientSrc).Append(EmissiveSrc).Append('|').Append(string.Join(",", Lights)).Append("|f").Append(FogMode).Append(RangeFog ? 'r' : '-');
                    return sb.ToString();
                }
            }
        }

        public static string FixedVertexModule(FixedVertex k, Linkage l)
        {
            var w = new StringBuilder();
            w.Append(MiscWgsl);
            VaryingStruct(w, l);
            w.Append("@vertex\nfn main(@location(0) aPos : vec4<f32>");
            if (k.Normal) w.Append(", @location(1) aNormal : ").Append(Attribute(k.NormalType, "").Type);
            if (k.Diffuse) w.Append(", @location(3) aDiffuse : ").Append(Attribute(k.DiffuseType, "").Type);
            if (k.Specular) w.Append(", @location(4) aSpecular : ").Append(Attribute(k.SpecularType, "").Type);
            for (int i = 0; i < 8; i++) if (k.TexDims[i] > 0) w.Append(", @location(").Append(5 + i).Append(") aTex").Append(i).Append(" : ").Append(Attribute(k.TexTypes[i], "").Type);
            if (k.PointMode != 0 && k.PSize) w.Append(", @location(2) aPSize : vec4<f32>");
            if (k.PointMode != 0) w.Append(", ").Append(CornerParam);
            w.Append(") -> VOut {\n");
            w.Append("    let pin = ").Append(Attribute(k.PosType, "aPos").Expr).Append(";\n");
            w.Append("    let diffuseIn = ").Append(k.Diffuse ? Attribute(k.DiffuseType, "aDiffuse").Expr : "vec4<f32>(1.0)").Append(";\n");
            w.Append("    let specularIn = ").Append(k.Specular ? Attribute(k.SpecularType, "aSpecular").Expr : "vec4<f32>(0.0, 0.0, 0.0, 1.0)").Append(";\n");
            string pos;
            if (k.Transformed)
            {
                // pre-transformed: render-target pixels (centres on integers), the depth, 1/w
                w.Append("    let rw = select(1.0 / pin.w, 1.0, pin.w == 0.0);\n");
                w.Append("    let ndc = vec3<f32>((pin.x + 0.5 - M.px.z) * 2.0 / M.vp.x - 1.0, 1.0 - (pin.y + 0.5 - M.px.w) * 2.0 / M.vp.y, (pin.z - M.vp.z) / max(M.vp.w - M.vp.z, 1e-20));\n");
                w.Append("    let clip = vec4<f32>(ndc * rw, rw);\n");
                w.Append("    let vpos = vec4<f32>(0.0, 0.0, rw, 1.0);\n");
                w.Append("    let n = vec3<f32>(0.0, 0.0, -1.0);\n");
                pos = "clip";
            }
            else
            {
                w.Append("    let vpos = M.wv * pin;\n");
                w.Append("    let clip = M.wvp * pin;\n");
                if (k.Normal)
                {
                    w.Append("    var n = (M.nrm * vec4<f32>(").Append(Attribute(k.NormalType, "aNormal").Expr).Append(".xyz, 0.0)).xyz;\n");
                    if (k.NormalizeNormals) w.Append("    n = normalize(n);\n");
                }
                else w.Append("    let n = vec3<f32>(0.0);\n");
                pos = "clip";
            }

            // colours: lit, or the vertex's
            if (k.Lighting && !k.Transformed)
            {
                string Src(int src, string material) => src switch { 1 when k.Diffuse => "diffuseIn", 2 when k.Specular => "specularIn", _ => material };
                w.Append("    let md = ").Append(Src(k.DiffuseSrc, "M.matDiffuse")).Append(";\n");
                w.Append("    let ma = ").Append(Src(k.AmbientSrc, "M.matAmbient")).Append(";\n");
                w.Append("    let ms = ").Append(Src(k.SpecularSrc, "M.matSpecular")).Append(";\n");
                w.Append("    let me = ").Append(Src(k.EmissiveSrc, "M.matEmissive")).Append(";\n");
                w.Append("    var ld = vec3<f32>(0.0);\n    var la = vec3<f32>(0.0);\n    var ls = vec3<f32>(0.0);\n");
                w.Append("    let toEye = ").Append(k.LocalViewer ? "normalize(-vpos.xyz)" : "vec3<f32>(0.0, 0.0, -1.0)").Append(";\n");
                for (int i = 0; i < k.Lights.Count; i++)
                {
                    int type = k.Lights[i];
                    w.Append("    {\n        let L = M.lights[").Append(i).Append("];\n");
                    if (type == 3)
                        w.Append("        let dir = -L.dir.xyz;\n        let att = 1.0;\n");
                    else
                    {
                        w.Append("        let d = L.pos.xyz - vpos.xyz;\n        let dist = length(d);\n        let dir = d / max(dist, 1e-20);\n");
                        w.Append("        var att = select(0.0, 1.0 / max(L.att.x + L.att.y * dist + L.att.z * dist * dist, 1e-20), dist <= L.pos.w);\n");
                        if (type == 2)
                        {
                            w.Append("        let rho = dot(-dir, L.dir.xyz);\n");
                            w.Append("        if (rho <= L.spot.y) { att = 0.0; } else if (rho <= L.spot.x) { att = att * pow(max((rho - L.spot.y) / max(L.spot.x - L.spot.y, 1e-20), 0.0), L.dir.w); }\n");
                        }
                    }
                    w.Append("        let ndl = dot(n, dir);\n");
                    w.Append("        ld = ld + att * L.diffuse.rgb * max(ndl, 0.0);\n");
                    w.Append("        la = la + att * L.ambient.rgb;\n");
                    if (k.SpecularEnable)
                        w.Append("        if (ndl > 0.0) { ls = ls + att * L.specular.rgb * pow(max(dot(n, normalize(dir + toEye)), 0.0), M.matPower.x); }\n");
                    w.Append("    }\n");
                }
                w.Append("    let col0 = clamp(vec4<f32>(me.rgb + ma.rgb * (M.ambient.rgb + la) + md.rgb * ld, md.a), vec4<f32>(0.0), vec4<f32>(1.0));\n");
                w.Append("    let col1 = clamp(vec4<f32>(ms.rgb * ls, ").Append(k.Specular ? "specularIn.a" : "ms.a").Append("), vec4<f32>(0.0), vec4<f32>(1.0));\n");
            }
            else
            {
                w.Append("    let col0 = diffuseIn;\n    let col1 = specularIn;\n");
            }

            var provided = new Dictionary<int, string>
            {
                [D3D9Usage.Key(D3D9Usage.Color, 0)] = "col0",
                [D3D9Usage.Key(D3D9Usage.Color, 1)] = "col1",
            };

            // fog: from the eye distance, or the specular alpha
            if (l.Keys.Contains(FogKey))
            {
                if (k.FogMode == 0 || k.Transformed) w.Append("    let fogV = col1.a;\n");
                else
                {
                    w.Append("    let fd = ").Append(k.RangeFog ? "length(vpos.xyz)" : "abs(vpos.z)").Append(";\n");
                    w.Append("    let fogV = ").Append(k.FogMode switch
                    {
                        1 => "exp(-fd * M.fog.z)",
                        2 => "exp(-(fd * M.fog.z) * (fd * M.fog.z))",
                        _ => "(M.fog.y - fd) / (M.fog.y - M.fog.x)",
                    }).Append(";\n");
                }
                provided[FogKey] = "vec4<f32>(clamp(fogV, 0.0, 1.0))";
            }

            // texture coordinates: the set (or generated coordinates) each output reads, through its transform
            for (int i = 0; i < 8; i++)
            {
                int key = D3D9Usage.Key(D3D9Usage.TexCoord, i);
                if (!l.Keys.Contains(key)) continue;
                uint tci = k.TexIndex[i];
                int set = (int)(tci & 0xFFFF);
                string b;
                switch (tci >> 16)
                {
                    case 1: b = "vec4<f32>(n, 1.0)"; break;
                    case 2: b = "vec4<f32>(vpos.xyz, 1.0)"; break;
                    case 3: b = $"vec4<f32>(reflect({(k.LocalViewer ? "normalize(vpos.xyz)" : "vec3<f32>(0.0, 0.0, 1.0)")}, n), 1.0)"; break;
                    case 4:
                        w.Append("    let sr").Append(i).Append(" = reflect(").Append(k.LocalViewer ? "normalize(vpos.xyz)" : "vec3<f32>(0.0, 0.0, 1.0)").Append(", n);\n");
                        w.Append("    let sm").Append(i).Append(" = 2.0 * sqrt(sr").Append(i).Append(".x * sr").Append(i).Append(".x + sr").Append(i).Append(".y * sr").Append(i).Append(".y + (sr").Append(i).Append(".z + 1.0) * (sr").Append(i).Append(".z + 1.0));\n");
                        b = $"vec4<f32>(sr{i}.x / sm{i} + 0.5, -sr{i}.y / sm{i} + 0.5, 0.0, 1.0)";
                        break;
                    default:
                        if (set >= 8 || k.TexDims[set] == 0) { b = "vec4<f32>(0.0)"; break; }
                        string t = Attribute(k.TexTypes[set], $"aTex{set}").Expr;
                        // D3D9 pads a set with 1 after its last component (a 2D set's translation is the matrix's third row)
                        b = k.TexDims[set] switch
                        {
                            1 => $"vec4<f32>(({t}).x, 1.0, 0.0, 0.0)",
                            2 => $"vec4<f32>(({t}).xy, 1.0, 0.0)",
                            3 => $"vec4<f32>(({t}).xyz, 1.0)",
                            _ => t,
                        };
                        break;
                }
                uint count = k.TexTransform[i] & 0xFF;
                provided[key] = count != 0 && !k.Transformed ? $"(M.tex[{i}] * {b})" : b;
            }
            var pts = new Points
            {
                Mode = k.PointMode,
                Size = k.PSize ? "aPSize.x" : null,
                ScaleFrom = k.PointScale && !k.Transformed ? "vpos" : null,
            };
            VertexTail(w, l, provided, pos, halfPixel: !k.Transformed, clipFrom: k.Transformed ? "" : "pin", pts: pts);
            return w.ToString();
        }

        // ---- the fixed-function pixel stage: the texture stages ------------------------------------

        public sealed class FixedPixel
        {
            public struct Stage
            {
                public uint ColorOp, ColorArg0, ColorArg1, ColorArg2, AlphaOp, AlphaArg0, AlphaArg1, AlphaArg2, ResultArg;
                /// <summary>Texture fixup (FixupOf; 8: no texture, reads 1), texture type, D3DTSS_TEXTURETRANSFORMFLAGS.</summary>
                public int Fixup, TexType;
                public uint Transform;
                public string Id => $"{ColorOp}.{ColorArg0}.{ColorArg1}.{ColorArg2}.{AlphaOp}.{AlphaArg0}.{AlphaArg1}.{AlphaArg2}.{ResultArg}.{Fixup}.{TexType}.{Transform}";
            }

            public readonly List<Stage> Stages = new();
            public bool SpecularEnable;
            public string Id => "ffp:" + string.Join("/", Stages.Select(s => s.Id)) + (SpecularEnable ? "S" : "s");

            public static bool ReadsTexture(Stage s)
            {
                bool Arg(uint a) => (a & 0xF) == 2;
                if (s.ColorOp is 13 or 15 or 22 or 23) return true;    // BLENDTEXTUREALPHA(PM), BUMPENVMAP(LUMINANCE)
                if (s.AlphaOp is 13 or 15) return true;
                bool three = s.ColorOp is 25 or 26, threeA = s.AlphaOp is 25 or 26;
                return Arg(s.ColorArg1) || (s.ColorOp != 2 && Arg(s.ColorArg2)) || (three && Arg(s.ColorArg0))
                    || (s.AlphaOp != 1 && (Arg(s.AlphaArg1) || (s.AlphaOp != 2 && Arg(s.AlphaArg2)) || (threeA && Arg(s.AlphaArg0))));
            }
        }

        public static Linkage FixedLinkage(FixedPixel k, PixelTail tail, bool flat)
        {
            var l = new Linkage { FlatColor = flat };
            l.Keys.Add(D3D9Usage.Key(D3D9Usage.Color, 0));
            l.Keys.Add(D3D9Usage.Key(D3D9Usage.Color, 1));
            for (int i = 0; i < k.Stages.Count; i++)
                if (FixedPixel.ReadsTexture(k.Stages[i]) && k.Stages[i].Fixup != 8) l.Keys.Add(D3D9Usage.Key(D3D9Usage.TexCoord, i));
            TailKeys(l, tail);
            return l;
        }

        public static string FixedPixelModule(FixedPixel k, Linkage l, PixelTail tail, int targets)
        {
            var w = new StringBuilder();
            w.Append("diagnostic(off, derivative_uniformity);\n").Append(MiscWgsl);
            for (int i = 0; i < k.Stages.Count; i++)
            {
                var s = k.Stages[i];
                if (!FixedPixel.ReadsTexture(s) || s.Fixup == 8) continue;
                w.Append("@group(0) @binding(").Append(PsTextureBase + 2 * i).Append(") var tex").Append(i).Append(" : ").Append(TextureType(s.TexType)).Append(";\n");
                w.Append("@group(0) @binding(").Append(PsTextureBase + 2 * i + 1).Append(") var smp").Append(i).Append(" : sampler;\n");
                w.Append(FixupFn($"fx{i}", s.Fixup));
            }
            VaryingStruct(w, l);
            FragmentOut(w, targets, false);
            w.Append("@fragment\nfn main(in : VOut) -> FOut {\n");
            ApplyClip(w, l, tail);
            w.Append("    let diffuse = in.l0;\n    let specular = in.l1;\n");
            w.Append("    var cur = diffuse;\n    var temp = vec4<f32>(0.0);\n    var bumpUv = vec2<f32>(0.0);\n    var bumpL = 1.0;\n");
            for (int i = 0; i < k.Stages.Count; i++)
            {
                var s = k.Stages[i];
                w.Append("    // stage ").Append(i).Append('\n');
                bool reads = FixedPixel.ReadsTexture(s);
                if (reads && s.Fixup != 8)
                {
                    int at = l.LocationOf(D3D9Usage.Key(D3D9Usage.TexCoord, i));
                    string tc = $"in.l{at}";
                    uint count = s.Transform & 0xFF;
                    bool projected = (s.Transform & 256) != 0 && count > 1;
                    string div = projected ? $" / {tc}.{"xyzw"[(int)count - 1]}" : "";
                    string uv = s.TexType == 2 || s.TexType == 0 ? $"({tc}.xy{div} + bumpUv)" : $"({tc}.xyz{div})";
                    w.Append("    let t").Append(i).Append(" = fx").Append(i).Append("(textureSample(tex").Append(i).Append(", smp").Append(i).Append(", ").Append(uv).Append(")) * vec4<f32>(vec3<f32>(bumpL), 1.0);\n");
                }
                else w.Append("    let t").Append(i).Append(" = vec4<f32>(1.0);\n");
                w.Append("    bumpUv = vec2<f32>(0.0);\n    bumpL = 1.0;\n");
                if (s.ColorOp is 22 or 23)
                {
                    // BUMPENVMAP(LUMINANCE): this stage's texel perturbs the next stage's coordinates; the colour passes
                    w.Append("    bumpUv = vec2<f32>(t").Append(i).Append(".r * M.bump[").Append(i).Append("].x + t").Append(i).Append(".g * M.bump[").Append(i).Append("].z, t").Append(i).Append(".r * M.bump[").Append(i).Append("].y + t").Append(i).Append(".g * M.bump[").Append(i).Append("].w);\n");
                    if (s.ColorOp == 23) w.Append("    bumpL = clamp(t").Append(i).Append(".b * M.bumpL[").Append(i).Append("].x + M.bumpL[").Append(i).Append("].y, 0.0, 1.0);\n");
                    continue;
                }
                string A(uint a) => Arg(a, i);
                string rgb = Op(s.ColorOp, A(s.ColorArg0), A(s.ColorArg1), A(s.ColorArg2), i, alpha: false);
                string alpha = s.AlphaOp == 1 ? "cur.a" : Op(s.AlphaOp, A(s.AlphaArg0), A(s.AlphaArg1), A(s.AlphaArg2), i, alpha: true);
                string target = (s.ResultArg & 0xF) == 5 ? "temp" : "cur";
                if (s.ColorOp == 24)
                    w.Append("    ").Append(target).Append(" = clamp(").Append(rgb).Append(", vec4<f32>(0.0), vec4<f32>(1.0));\n");      // DOTPRODUCT3 fills alpha too
                else
                    w.Append("    ").Append(target).Append(" = clamp(vec4<f32>((").Append(rgb).Append(").rgb, (").Append(alpha).Append(")), vec4<f32>(0.0), vec4<f32>(1.0));\n");
            }
            if (k.SpecularEnable) w.Append("    cur = vec4<f32>(clamp(cur.rgb + specular.rgb, vec3<f32>(0.0), vec3<f32>(1.0)), cur.a);\n");
            w.Append("    var c0 = cur;\n");
            ApplyTail(w, l, tail, "c0");
            w.Append("    var f : FOut;\n    f.c0 = c0;\n");
            for (int i = 1; i < targets; i++) w.Append("    f.c").Append(i).Append(" = vec4<f32>(0.0);\n");
            w.Append("    return f;\n}\n");
            return w.ToString();
        }

        /// <summary>A texture stage argument (D3DTA_*, with COMPLEMENT and ALPHAREPLICATE).</summary>
        private static string Arg(uint a, int stage)
        {
            string e = (a & 0xF) switch
            {
                0 => "diffuse", 1 => "cur", 2 => $"t{stage}", 3 => "M.tfactor", 4 => "specular", 5 => "temp", 6 => $"M.stageConst[{stage}]",
                _ => "cur",
            };
            if ((a & 0x10) != 0) e = $"(vec4<f32>(1.0) - {e})";
            if ((a & 0x20) != 0) e = $"vec4<f32>(({e}).a)";
            return e;
        }

        /// <summary>A D3DTEXTUREOP over the arguments (as vec4; the caller takes rgb or a).</summary>
        private static string Op(uint op, string a0, string a1, string a2, int stage, bool alpha)
        {
            string e = op switch
            {
                2 => a1,
                3 => a2,
                4 => $"({a1} * {a2})",
                5 => $"({a1} * {a2} * 2.0)",
                6 => $"({a1} * {a2} * 4.0)",
                7 => $"({a1} + {a2})",
                8 => $"({a1} + {a2} - vec4<f32>(0.5))",
                9 => $"(({a1} + {a2} - vec4<f32>(0.5)) * 2.0)",
                10 => $"({a1} - {a2})",
                11 => $"({a1} + {a2} - {a1} * {a2})",
                12 => $"mix({a2}, {a1}, diffuse.a)",
                13 => $"mix({a2}, {a1}, t{stage}.a)",
                14 => $"mix({a2}, {a1}, M.tfactor.a)",
                15 => $"({a1} + {a2} * (1.0 - t{stage}.a))",
                16 => $"mix({a2}, {a1}, cur.a)",
                17 => a1,
                18 => alpha ? a1 : $"({a1} + vec4<f32>(({a1}).a) * {a2})",
                19 => alpha ? a1 : $"({a1} * {a2} + vec4<f32>(({a1}).a))",
                20 => alpha ? a1 : $"((1.0 - ({a1}).a) * {a2} + {a1})",
                21 => alpha ? a1 : $"((vec4<f32>(1.0) - {a1}) * {a2} + vec4<f32>(({a1}).a))",
                24 => $"vec4<f32>(4.0 * dot(({a1}).rgb - vec3<f32>(0.5), ({a2}).rgb - vec3<f32>(0.5)))",
                25 => $"({a0} + {a1} * {a2})",
                26 => $"mix({a2}, {a1}, {a0})",
                _ => "cur",
            };
            return alpha ? $"({e}).a" : e;
        }
    }
}
