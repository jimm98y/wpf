// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Flow control in a translated pixel shader.
//
// The translator handled the arithmetic and texture core of ps_2_0 and REJECTED anything with a
// branch or a loop in it, falling back to drawing the input unmodified. That is the safe answer,
// and it is also the wrong picture: a shader with a single `if` in it -- a threshold, a channel
// selector, an early-out for transparent pixels -- rendered as though the effect were not there.
//
// D3D9 flow control is already STRUCTURED. if/else/endif and rep/endrep nest properly and nothing
// jumps into a block, which is exactly the shape WGSL requires, so the translation is a matter of
// matching openers to closers rather than reconstructing control flow. That is what these assert:
// that the blocks come out nested and balanced, that the conditions mean what D3D9 says they mean,
// and -- just as important -- that a malformed or unsupported construct is still REJECTED with a
// reason instead of producing WGSL that will not compile at draw time.
//
// Same standing caveat as the rest of the translator suite: this is assembled bytecode written
// against the documented token format, not fxc output, so it checks the translator against the
// FORMAT. See the note at the top of D3D9Bytecode.cs.
//

using System;
using WgpuInterop.Tests.Harness;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using Xunit;

namespace WgpuInterop.Tests.Protocol
{
    public class ShaderFlowControlTests : RendererTestBase
    {
        public ShaderFlowControlTests(GpuFixture gpu) : base(gpu) { }

        // D3DSHADER_COMPARISON, from d3d9types.h.
        private const int GT = 1, EQ = 2, GE = 3, LT = 4, NE = 5, LE = 6;

        /// <summary>
        /// Translate, and COMPILE what comes out. Asserting on the text alone would pass for WGSL
        /// that naga refuses -- and unbalanced or misplaced braces are exactly the failure mode of
        /// emitting nested blocks, so putting the result through the real frontend is the assertion
        /// that matters most here.
        /// </summary>
        private string Translate(byte[] bytecode)
        {
            Assert.True(D3D9ShaderTranslator.TryTranslate(bytecode, out TranslatedShader shader, out string reason),
                $"translation failed: {reason}");
            Assert.True(NewRenderer().CompileShaderForTest(shader.Wgsl) != IntPtr.Zero,
                $"the emitted WGSL does not compile through naga:\n{shader.Wgsl}");
            return shader.Wgsl;
        }

        private static string Reject(byte[] bytecode)
        {
            Assert.False(D3D9ShaderTranslator.TryTranslate(bytecode, out _, out string reason),
                "the shader was translated when it should have been rejected");
            Assert.False(string.IsNullOrWhiteSpace(reason), "a rejection must say why");
            return reason;
        }

        // ---- static branching ---------------------------------------------------------------

        /// <summary>
        /// if b0 / else / endif. ps_2_0 has no way to set a boolean at run time, so the value comes
        /// from a defb and the branch is decidable here -- but it still has to be EMITTED as a
        /// branch, with both arms intact, because the arms are what differ.
        /// </summary>
        [Fact]
        public void AStaticIfElseBecomesNestedWgsl()
        {
            byte[] code = new Ps()
                .DefB(0, true)
                .If(0)
                .Op(Ps.MOV, Dst.ColorOut(), Src.Texture(0))
                .Else()
                .Op(Ps.MOV, Dst.ColorOut(), Src.Const(0))
                .EndIf()
                .Build();

            string wgsl = Translate(code);

            Assert.Contains("if (true)", wgsl);
            Assert.Contains("} else {", wgsl);
            // Balanced: every brace this opened is closed.
            Assert.Equal(CountOf(wgsl, '{'), CountOf(wgsl, '}'));
        }

        [Fact]
        public void ANegatedPredicateInvertsTheCondition()
        {
            byte[] code = new Ps()
                .DefB(3, true)
                .If(3, negated: true)
                .Op(Ps.MOV, Dst.ColorOut(), Src.Texture(0))
                .EndIf()
                .Op(Ps.MOV, Dst.ColorOut(), Src.Texture(0))
                .Build();

            Assert.Contains("if (false)", Translate(code));
        }

        /// <summary>A boolean the shader never defined cannot be evaluated, and must not be guessed.</summary>
        [Fact]
        public void AnUndefinedBooleanIsRejected()
        {
            byte[] code = new Ps()
                .If(1)
                .Op(Ps.MOV, Dst.ColorOut(), Src.Texture(0))
                .EndIf()
                .Build();

            Assert.Contains("b1", Reject(code));
        }

        // ---- dynamic branching --------------------------------------------------------------

        /// <summary>
        /// if_comp compares two scalars at run time. Each comparison mode has to map to the right
        /// operator: getting GE and LE the wrong way round produces a shader that runs and is wrong,
        /// which no amount of "it compiles" catches.
        /// </summary>
        [Theory]
        [InlineData(GT, ">")]
        [InlineData(EQ, "==")]
        [InlineData(GE, ">=")]
        [InlineData(LT, "<")]
        [InlineData(NE, "!=")]
        [InlineData(LE, "<=")]
        public void EachComparisonModeMapsToItsOperator(int comparison, string expected)
        {
            byte[] code = new Ps()
                .Def(0, 0.5f, 0f, 0f, 0f)
                .Ifc(comparison, Src.Texture(0), Src.Const(0))
                .Op(Ps.MOV, Dst.ColorOut(), Src.Texture(0))
                .EndIf()
                .Op(Ps.MOV, Dst.ColorOut(), Src.Const(0))
                .Build();

            string wgsl = Translate(code);

            // The comparison is on the scalar x component, as D3D9 defines it.
            Assert.Contains(").x " + expected + " (", wgsl);
        }

        [Fact]
        public void AReservedComparisonModeIsRejected()
        {
            byte[] code = new Ps()
                .Def(0, 0.5f, 0f, 0f, 0f)
                .Ifc(0, Src.Texture(0), Src.Const(0))      // D3DSPC_RESERVED0
                .Op(Ps.MOV, Dst.ColorOut(), Src.Texture(0))
                .EndIf()
                .Build();

            Assert.Contains("comparison", Reject(code));
        }

        // ---- loops ---------------------------------------------------------------------------

        /// <summary>
        /// rep i0 / endrep becomes a counted loop. The count lives in a defi, so it is known here and
        /// the bound is a literal -- which is what lets the shader compiler unroll or bound it.
        /// </summary>
        [Fact]
        public void ARepBecomesACountedLoop()
        {
            byte[] code = new Ps()
                .DefI(0, 4, 0, 0, 0)
                .Def(0, 0.25f, 0f, 0f, 0f)
                .Rep(0)
                .Op(Ps.ADD, Dst.Temp(0), Src.Temp(0), Src.Const(0))
                .EndRep()
                .Op(Ps.MOV, Dst.ColorOut(), Src.Temp(0))
                .Build();

            string wgsl = Translate(code);

            Assert.Contains("< 4;", wgsl);
            Assert.Equal(CountOf(wgsl, '{'), CountOf(wgsl, '}'));
        }

        [Fact]
        public void ABreakInsideALoopIsTranslated()
        {
            byte[] code = new Ps()
                .DefI(0, 8, 0, 0, 0)
                .Def(0, 1f, 0f, 0f, 0f)
                .Rep(0)
                .BreakC(GE, Src.Temp(0), Src.Const(0))
                .Op(Ps.ADD, Dst.Temp(0), Src.Temp(0), Src.Const(0))
                .EndRep()
                .Op(Ps.MOV, Dst.ColorOut(), Src.Temp(0))
                .Build();

            string wgsl = Translate(code);

            Assert.Contains("break;", wgsl);
            Assert.Contains(">=", wgsl);
        }

        /// <summary>A break with no loop around it has nowhere to go and is not silently dropped.</summary>
        [Fact]
        public void ABreakOutsideALoopIsRejected()
        {
            byte[] code = new Ps()
                .Break()
                .Op(Ps.MOV, Dst.ColorOut(), Src.Texture(0))
                .Build();

            Assert.Contains("break", Reject(code).ToLowerInvariant());
        }

        [Fact]
        public void ARepWithNoMatchingDefiIsRejected()
        {
            byte[] code = new Ps()
                .Rep(2)
                .Op(Ps.MOV, Dst.ColorOut(), Src.Texture(0))
                .EndRep()
                .Build();

            Assert.Contains("i2", Reject(code));
        }

        // ---- structure -------------------------------------------------------------------------

        /// <summary>
        /// Nesting has to survive: an if inside a rep inside an if is ordinary in a real effect, and
        /// each closer must match its own opener rather than the nearest one.
        /// </summary>
        [Fact]
        public void BlocksNestToAnyDepth()
        {
            byte[] code = new Ps()
                .DefB(0, true)
                .DefI(0, 3, 0, 0, 0)
                .Def(0, 0.5f, 0f, 0f, 0f)
                .If(0)
                .Rep(0)
                .Ifc(LT, Src.Temp(0), Src.Const(0))
                .Op(Ps.ADD, Dst.Temp(0), Src.Temp(0), Src.Const(0))
                .EndIf()
                .EndRep()
                .EndIf()
                .Op(Ps.MOV, Dst.ColorOut(), Src.Temp(0))
                .Build();

            string wgsl = Translate(code);

            Assert.Equal(CountOf(wgsl, '{'), CountOf(wgsl, '}'));
            Assert.Contains("if (true)", wgsl);
            Assert.Contains("< 3;", wgsl);
        }

        /// <summary>
        /// An unclosed block would emit WGSL that does not parse. Failing here, with a reason, is far
        /// easier to trace than a shader-compile error at draw time.
        /// </summary>
        [Fact]
        public void AnUnclosedBlockIsRejected()
        {
            byte[] code = new Ps()
                .DefB(0, true)
                .If(0)
                .Op(Ps.MOV, Dst.ColorOut(), Src.Texture(0))
                .Build();                                  // no endif

            Assert.Contains("unclosed", Reject(code).ToLowerInvariant());
        }

        [Fact]
        public void AnElseWithNoIfIsRejected()
        {
            byte[] code = new Ps()
                .Op(Ps.MOV, Dst.ColorOut(), Src.Texture(0))
                .Else()
                .Build();

            Assert.Contains("else", Reject(code).ToLowerInvariant());
        }

        [Fact]
        public void AnEndRepClosingAnIfIsRejected()
        {
            byte[] code = new Ps()
                .DefB(0, true)
                .If(0)
                .Op(Ps.MOV, Dst.ColorOut(), Src.Texture(0))
                .EndRep()                                  // closes the wrong kind of block
                .Build();

            Assert.Contains("endrep", Reject(code).ToLowerInvariant());
        }

        private static int CountOf(string text, char c)
        {
            int n = 0;
            foreach (char ch in text) if (ch == c) n++;
            return n;
        }
    }
}
