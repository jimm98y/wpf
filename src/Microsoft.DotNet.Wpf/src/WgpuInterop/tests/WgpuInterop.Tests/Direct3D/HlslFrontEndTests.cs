// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The shim's HLSL compiler's front end: the preprocessor and the parser, over the DXBC corpus's
// sources (Corpus/hlsl, what fxc compiled into Corpus/dxbc) and the cases a shader's source takes.
//

using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Wpf.Interop.WebGpu.Direct3D.Hlsl;
using Xunit;

namespace WgpuInterop.Tests.Direct3D
{
    public sealed class HlslFrontEndTests
    {
        private static string Corpus([System.Runtime.CompilerServices.CallerFilePath] string here = "") => Path.Combine(Path.GetDirectoryName(here)!, "Corpus", "hlsl");

        private static TranslationUnit Parse(string source, params (string, string)[] defines)
        {
            var pp = new HlslPreprocessor(defines, null);
            return new HlslParser(pp.Run(source, "test.hlsl")).Parse();
        }

        [Theory]
        [InlineData("")]
        [InlineData("SM41")]
        [InlineData("SM5")]
        [InlineData("DOUBLE_EXT")]
        public void TheCorpusParses(string define)
        {
            foreach (string f in Directory.GetFiles(Corpus(), "*.hlsl"))
            {
                var defines = define.Length == 0 ? new (string, string)[0] : define == "DOUBLE_EXT" ? new[] { ("SM5", ""), ("DOUBLE_EXT", "") } : new[] { (define, "") };
                var unit = Parse(File.ReadAllText(f), defines);
                Assert.True(unit.Decls.OfType<FunctionDecl>().Any(), Path.GetFileName(f));
            }
        }

        [Fact]
        public void MacrosExpandAsC()
        {
            var tokens = new HlslPreprocessor(new[] { ("SCALE", "2.0") }, null).Run(
                "#define SQ(x) ((x) * (x))\n#define CAT(a, b) a##b\n#if defined(SCALE) && SCALE > 1\nfloat CAT(fo, o) = SQ(SCALE + 1);\n#else\nint wrong;\n#endif\n", "t");
            Assert.Equal("float foo = ( ( 2.0 + 1 ) * ( 2.0 + 1 ) ) ;", string.Join(" ", tokens.Where(t => t.Kind != TokenKind.End).Select(t => t.Text)));
        }

        [Fact]
        public void IncludesComeFromTheHandler()
        {
            var files = new Dictionary<string, string> { ["common.hlsli"] = "#define K 3\nstatic const int k = K;\n" };
            var unit = new HlslParser(new HlslPreprocessor(null, (name, system, from) => files.GetValueOrDefault(name)).Run("#include \"common.hlsli\"\nint f() { return k; }\n", "t")).Parse();
            Assert.Equal(new[] { "k", "f" }, unit.Decls.Select(d => d is VarDecl v ? v.Name : ((FunctionDecl)d).Name));
        }

        [Fact]
        public void DeclarationsCastsAndConstructorsAreTold()
        {
            var unit = Parse(@"
struct V { float4 pos : SV_Position; float2 uv : TEXCOORD0; };
cbuffer C : register(b1) { row_major float4x4 m : packoffset(c0); float4 tint; }
Texture2D<float4> t : register(t2);
SamplerState s : register(s0) { Filter = MIN_MAG_MIP_LINEAR; };
float4 main(V v, uniform bool flip = false) : SV_Target
{
    float2 uv = (float2)v.uv;
    float4 c = t.Sample(s, uv) * tint;
    [unroll] for (int i = 0; i < 4; ++i) c += float4(i, 0, 0, 1) * (float)i;
    return mul(c, m);
}
technique11 T { pass P { SetPixelShader(CompileShader(ps_5_0, main())); } }");
            var f = unit.Decls.OfType<FunctionDecl>().Single();
            Assert.Equal("SV_Target", f.ReturnSemantic);
            Assert.Equal(2, f.Params.Count);
            var cb = unit.Decls.OfType<CBufferDecl>().Single();
            Assert.Equal('b', cb.Registers[0].Kind);
            Assert.Equal(1, cb.Registers[0].Index);
            Assert.Contains("row_major", cb.Members[0].Modifiers);
            Assert.Equal(4, f.Body!.Body.Count);
            var loop = Assert.IsType<ForStmt>(f.Body.Body[2]);
            Assert.Equal("unroll", loop.Attributes.Single().Name);
            var decl = Assert.IsType<DeclStmt>(f.Body.Body[0]);
            Assert.IsType<CastExpr>(decl.Vars[0].Init);
        }
    }
}
