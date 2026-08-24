// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Ported from WgpuInterop.ShaderEffectTest.
//
// D3D9 pixel-shader bytecode -> WGSL translation: the core of WPF's ShaderEffect.
//
// Each translation case makes THREE independent checks, because any one alone is weak:
//   1. PARSE     -- the sampler set and constant-register count the translator reports.
//   2. COMPILE   -- the emitted WGSL goes through wgpu's own frontend (naga). Text that looks
//                   right but does not compile is worthless.
//   3. SEMANTICS -- the emitted expression is compared against the WGSL a reader of the D3D9
//                   spec would write for that instruction.
//
// Unsupported constructs must be REJECTED WITH A REASON, never partially translated: a silently
// wrong effect is worse than a visibly missing one.
//
// See Harness/D3D9Bytecode.cs for the standing caveat about hand-assembled bytecode versus real
// fxc output.
//

using System;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using Microsoft.Wpf.Interop.WebGpu.Composition.Protocol;
using WgpuInterop.Tests.Harness;
using Xunit;

namespace WgpuInterop.Tests.Protocol
{
    public sealed class ShaderTranslationTests : RendererTestBase
    {
        public ShaderTranslationTests(GpuFixture gpu) : base(gpu) { }

        /// <summary>Parse + compile + semantics, the three checks every translation case makes.</summary>
        private void AssertTranslates(string name, byte[] bytecode, int[] samplers, int constCount, string[] expect)
        {
            Assert.True(D3D9ShaderTranslator.TryTranslate(bytecode, out TranslatedShader r, out string reason),
                $"{name}: translation failed -- {reason}");

            Assert.True(string.Join(",", r.Samplers) == string.Join(",", samplers),
                $"{name}: samplers [{string.Join(",", r.Samplers)}], expected [{string.Join(",", samplers)}]");
            Assert.True(r.FloatRegisterCount == constCount,
                $"{name}: float register count {r.FloatRegisterCount}, expected {constCount}");

            Assert.True(NewRenderer().CompileShaderForTest(r.Wgsl) != IntPtr.Zero,
                $"{name}: the emitted WGSL does not compile through naga");

            foreach (string e in expect)
                Assert.True(r.Wgsl.Contains(e, StringComparison.Ordinal), $"{name}: WGSL does not contain \"{e}\"");
        }

        private static void AssertRejected(string name, byte[] bytecode, string contains)
        {
            bool translated = D3D9ShaderTranslator.TryTranslate(bytecode, out _, out string reason);
            Assert.False(translated, $"{name}: was translated, but should have been refused");
            Assert.True(reason.Contains(contains, StringComparison.OrdinalIgnoreCase),
                $"{name}: refused with \"{reason}\", which does not mention \"{contains}\"");
        }

        /// <summary>texld / mul / mov -- the shape of almost every shipping WPF ShaderEffect.</summary>
        [Fact]
        public void TintShader_Translates()
        {
            byte[] code = new Ps()
                .TexLd(Dst.Temp(0), Src.Texture(0), Src.Sampler(0))
                .Op(Ps.MUL, Dst.Temp(0), Src.Temp(0), Src.Const(0))
                .Op(Ps.MOV, Dst.ColorOut(), Src.Temp(0))
                .Build();

            AssertTranslates("tint (texld/mul/mov)", code, new[] { 0 }, 1,
                new[] { "textureSample(effTex0, effSamp0", "(r0 * effConst[0])", "oC0 = r0" });
        }

        [Fact]
        public void SaturateWriteMaskAndSwizzle_Translate()
        {
            byte[] code = new Ps()
                .TexLd(Dst.Temp(0), Src.Texture(0), Src.Sampler(0))
                .Op(Ps.ADD, Dst.Temp(1).Mask(0x3).Saturated(), Src.Temp(0).Swizzle("yxzw"), Src.Const(2))
                .Op(Ps.MOV, Dst.ColorOut(), Src.Temp(1))
                .Build();

            AssertTranslates("saturate + write mask + swizzle", code, new[] { 0 }, 3,
                new[] { "clamp(", "(r0).yxzw", "r1.x = _v.x; r1.y = _v.y;" });
        }

        /// <summary>A DEF literal must INLINE, not consume a uniform slot.</summary>
        [Fact]
        public void DefLiteral_InlinesInsteadOfAllocatingAUniform()
        {
            byte[] code = new Ps()
                .Def(5, 0.25f, 0.5f, 0.75f, 1f)
                .Op(Ps.MOV, Dst.ColorOut(), Src.Const(5))
                .Build();

            AssertTranslates("DEF literal inlines", code, Array.Empty<int>(), 0,
                new[] { "vec4<f32>(0.25, 0.5, 0.75, 1.0)" });
        }

        [Fact]
        public void TwoSamplersAndLrp_Translate()
        {
            byte[] code = new Ps()
                .TexLd(Dst.Temp(0), Src.Texture(0), Src.Sampler(0))
                .TexLd(Dst.Temp(1), Src.Texture(0), Src.Sampler(1))
                .Op(Ps.LRP, Dst.ColorOut(), Src.Const(0), Src.Temp(0), Src.Temp(1))
                .Build();

            AssertTranslates("two samplers + lrp", code, new[] { 0, 1 }, 1,
                new[] { "effTex1", "mix(r1, r0, effConst[0])" });
        }

        /// <summary>
        /// Source modifiers, verified against d3d9types.h and WpfGfx/fxjit pstrans.cpp -- the same
        /// header fxc targets.
        ///
        /// Two of these were WRONG before (2 read as x*2-1, 4 as complement) and nothing caught it,
        /// because every other case in this file uses modifier 0. A modifier applies silently to a
        /// value that still type-checks, so only comparing the emitted expression finds it.
        /// </summary>
        [Theory]
        [InlineData(1, "NEG", "(-effConst[0])")]
        [InlineData(2, "BIAS", "(effConst[0]) - vec4<f32>(0.5)")]
        [InlineData(4, "SIGN/_bx2", "((effConst[0]) - vec4<f32>(0.5)) * 2.0")]
        [InlineData(6, "COMP", "vec4<f32>(1.0) - (effConst[0])")]
        [InlineData(7, "X2", "(effConst[0]) * 2.0")]
        [InlineData(11, "ABS", "abs(effConst[0])")]
        public void SourceModifier_EmitsTheSpecExpression(int mod, string name, string expect)
        {
            byte[] code = new Ps().Op(Ps.MOV, Dst.ColorOut(), Src.Const(0).Mod(mod)).Build();

            Assert.True(D3D9ShaderTranslator.TryTranslate(code, out TranslatedShader r, out string why),
                $"modifier {mod} ({name}): translation failed -- {why}");
            Assert.True(r.Wgsl.Contains(expect, StringComparison.Ordinal),
                $"modifier {mod} ({name}) should emit \"{expect}\"");
        }

        [Fact]
        public void ProjectiveDivideModifier_IsRefused()
            => AssertRejected("projective divide (DZ)",
                new Ps().Op(Ps.MOV, Dst.ColorOut(), Src.Const(0).Mod(9)).Build(),
                "unsupported source modifier");

        // Structured flow control -- if/else/endif, rep/endrep, break -- is TRANSLATED now; see
        // ShaderFlowControlTests. What remains refused is the unstructured half, which needs an
        // address register and emitted functions rather than nested blocks.

        [Fact]
        public void SubroutineCall_IsRefused()
            => AssertRejected("call",
                new Ps().Raw(25, 1, 0).Op(Ps.MOV, Dst.ColorOut(), Src.Const(0)).Build(),
                "unsupported opcode");

        [Fact]
        public void LoopWithAddressRegister_IsRefused()
            => AssertRejected("loop/aL",
                new Ps().Raw(27, 2, 0).Op(Ps.MOV, Dst.ColorOut(), Src.Const(0)).Build(),
                "unsupported opcode");

        [Fact]
        public void VertexShaderBytecode_IsRefused()
            => AssertRejected("vertex shader bytecode",
                new Ps().AsVertexShader().Op(Ps.MOV, Dst.ColorOut(), Src.Const(0)).Build(),
                "not a pixel shader");

        [Fact]
        public void ShaderThatNeverWritesColourOut_IsRefused()
            => AssertRejected("no oC0 write",
                new Ps().Op(Ps.MOV, Dst.Temp(0), Src.Const(0)).Build(),
                "never writes oC0");

        [Fact]
        public void UnsupportedShaderModel_IsRefused()
            => AssertRejected("ps_1_1",
                new Ps().Version(1, 1).Op(Ps.MOV, Dst.ColorOut(), Src.Const(0)).Build(),
                "unsupported shader model");

        /// <summary>
        /// End to end: a translation that COMPILES but COMPUTES the wrong thing passes every check
        /// above. Only running it catches that, so the shader is chosen to make the expected output
        /// exact arithmetic -- c0 = (1,0,0,1) applied to a white square must yield pure red.
        /// </summary>
        [Fact]
        public void TranslatedShader_ComputesTheRightPixels()
        {
            const int S = 64;
            byte[] code = new Ps()
                .TexLd(Dst.Temp(0), Src.Texture(0), Src.Sampler(0))
                .Op(Ps.MUL, Dst.ColorOut(), Src.Temp(0), Src.Const(0))
                .Build();

            Assert.True(D3D9ShaderTranslator.TryTranslate(code, out TranslatedShader tr, out string why),
                $"end-to-end: translation failed -- {why}");

            var def = new ShaderEffectDef(1, tr.Wgsl, new[] { 1f, 0f, 0f, 1f }, tr.Samplers.ToArray());
            var root = new SceneVisual();
            var child = new SceneVisual { Effect = def };
            child.Content.Add(new GeometryFill(new RectangleGeometry(new Rect(16, 16, 32, 32)),
                RgbaColor.FromBytes(255, 255, 255, 255)));
            root.Children.Add(child);

            var img = new Image(Render(root, S, S, RgbaColor.FromBytes(0, 0, 128, 255)), S, S);

            Rgb inside = img[32, 32];
            Assert.True(inside.R > 200 && inside.G < 40 && inside.B < 40,
                $"a white square through mul-by-(1,0,0,1) should be red, was {inside}");

            Rgb outside = img[4, 4];
            Assert.True(outside.R < 40 && outside.B > 90,
                $"the background outside the effected subtree should be unchanged, was {outside}");
        }

        /// <summary>
        /// s0 is WPF's ImplicitInputBrush (the content the effect is applied to) and s1 is a second
        /// image. Multi-input effects -- transitions, masks, blends -- were declined outright before
        /// the sampler payload of MILCMD_SHADEREFFECT was decoded.
        /// </summary>
        [Fact]
        public void TwoSamplerEffect_BlendsTheInputWithASecondBrush()
        {
            const int S = 64;
            byte[] code = new Ps()
                .TexLd(Dst.Temp(0), Src.Texture(0), Src.Sampler(0))
                .TexLd(Dst.Temp(1), Src.Texture(0), Src.Sampler(1))
                .Op(Ps.ADD, Dst.ColorOut(), Src.Temp(0), Src.Temp(1))
                .Build();

            Assert.True(D3D9ShaderTranslator.TryTranslate(code, out TranslatedShader tr, out string why),
                $"two samplers: translation failed -- {why}");
            Assert.True(tr.Samplers.Count == 2, $"the translator reported {tr.Samplers.Count} samplers, expected 2");

            var blue = new byte[4 * 4];
            for (int i = 0; i < 4; i++)
            { blue[i * 4] = 0; blue[i * 4 + 1] = 0; blue[i * 4 + 2] = 255; blue[i * 4 + 3] = 255; }

            var def = new ShaderEffectDef(7, tr.Wgsl, Array.Empty<float>(), tr.Samplers.ToArray(),
                new Brush?[] { null, new ImageBrush(blue, 2, 2) });

            var root = new SceneVisual();
            var child = new SceneVisual { Effect = def };
            child.Content.Add(new GeometryFill(new RectangleGeometry(new Rect(16, 16, 32, 32)),
                RgbaColor.FromBytes(255, 0, 0, 255)));
            root.Children.Add(child);

            Rgb c = new Image(Render(root, S, S, RgbaColor.FromBytes(0, 0, 0, 255)), S, S)[32, 32];
            Assert.True(c.R > 200 && c.G < 60 && c.B > 200,
                $"red input plus a blue second sampler should add to magenta, was {c}");
        }

        /// <summary>
        /// The SAME shader through the actual milcore command stream a WPF app produces, rather than
        /// by constructing ShaderEffectDef directly. This is the part that decides whether a real
        /// ShaderEffect renders at all: the payload layout of MILCMD_SHADEREFFECT is easy to get
        /// subtly wrong, and a misparse silently yields NO EFFECT rather than an error.
        /// </summary>
        [Fact]
        public void ShaderEffect_DecodesFromTheMilcoreCommandStream()
        {
            const int S = 80;
            byte[] code = new Ps()
                .TexLd(Dst.Temp(0), Src.Texture(0), Src.Sampler(0))
                .Op(Ps.MUL, Dst.ColorOut(), Src.Temp(0), Src.Const(0))
                .Build();

            var engine = new MilcoreEngine();
            engine.CreateOrAddRef(2, MilResourceTypeId.Visual);
            engine.SubmitCommand(MilCmd.SolidColorBrush(3, 1f, 1f, 1f, 1f));

            SceneVisual _ = Realize(engine, MilCmd.DrawRectangleRecord(3, 0, 20, 20, 32, 32),
                                    hVisual: 2, hContent: 5);

            engine.SubmitCommand(MilCmd.PixelShader(11, code));
            engine.SubmitCommand(MilCmd.ShaderEffect(12, 11, register: 0, r: 0f, g: 1f, b2: 0f, a: 1f));
            engine.SubmitCommand(MilCmd.VisualSetEffect(2, 12));
            engine.Realize();

            SceneVisual? v = engine.VisualByHandle(2);
            Assert.True(v is not null, "no visual after the shader-effect commands");
            Assert.True(v!.Effect is ShaderEffectDef, "MILCMD_SHADEREFFECT did not decode to a ShaderEffectDef");

            var def = (ShaderEffectDef)v.Effect!;
            Assert.True(def.FloatConstants.Length >= 4 && def.FloatConstants[0] == 0f && def.FloatConstants[1] == 1f,
                $"c0 should unpack to (0,1,0,1), got ({string.Join(",", def.FloatConstants)})");

            Rgb c = new Image(NewRenderer().RenderToRgba(v, S, S, RgbaColor.FromBytes(0, 0, 128, 255)), S, S)[36, 36];
            Assert.True(c.R < 40 && c.G > 200 && c.B < 40,
                $"a white square through mul-by-(0,1,0,1) should be green, was {c}");
        }

        /// <summary>
        /// REAL fxc output, when any is available. This is the only case that can catch an assumption
        /// shared by the hand-assembler and the translator -- see Harness/D3D9Bytecode.cs.
        ///
        /// WPF's own effect shaders are compiled into milcore's native wpfgfx_cor3.dll;
        /// WPF_REAL_SHADER_DIR points at a directory of them extracted by magic bytes. Every shader
        /// must either translate to WGSL that COMPILES or be refused with a reason. A crash, or
        /// output naga rejects, is a translator bug.
        /// </summary>
        [Fact]
        public void RealFxcShaders_TranslateOrAreRefusedWithAReason()
        {
            string? dir = Environment.GetEnvironmentVariable("WPF_REAL_SHADER_DIR");
            Assert.SkipWhen(string.IsNullOrEmpty(dir) || !System.IO.Directory.Exists(dir),
                "set WPF_REAL_SHADER_DIR to a directory of fxc-compiled .ps files to run this");

            string[] files = System.IO.Directory.GetFiles(dir!, "*.ps");
            Array.Sort(files, StringComparer.Ordinal);
            Assert.SkipWhen(files.Length == 0, $"no .ps files in {dir}");

            WgpuSceneRenderer renderer = NewRenderer();
            var broken = new System.Collections.Generic.List<string>();
            foreach (string f in files)
            {
                byte[] code = System.IO.File.ReadAllBytes(f);
                string name = System.IO.Path.GetFileName(f);
                if (!D3D9ShaderTranslator.TryTranslate(code, out TranslatedShader tr, out _))
                    continue;                                   // refused with a reason: acceptable
                if (renderer.CompileShaderForTest(tr.Wgsl) == IntPtr.Zero)
                    broken.Add(name);                           // translated to WGSL naga rejects: a bug
            }

            Assert.True(broken.Count == 0,
                $"{broken.Count} of {files.Length} real shaders translated to WGSL that does not compile: " +
                string.Join(", ", broken));
        }
    }
}
