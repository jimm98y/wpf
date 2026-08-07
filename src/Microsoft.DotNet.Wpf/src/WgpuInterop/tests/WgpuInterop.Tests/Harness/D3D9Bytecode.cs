// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A D3D9 pixel-shader bytecode assembler, for testing the ShaderEffect translator.
//
// TESTING CONSTRAINT, STATED PLAINLY -- this is the whole reason the assembler exists and the
// limit on what any test using it can claim:
//
//   There is no fxc and no .ps asset on a macOS or Linux box, so the translator cannot be fed real
//   compiler output here. This builder is written against the DOCUMENTED D3D9 token format, NOT
//   against the translator. That checks the translator against the FORMAT; it does NOT prove
//   compatibility with what fxc actually emits, and the first real .ps file is likely to find
//   something.
//
// A shared assumption between an assembler and a decoder cannot be disproved by a test that uses
// both -- and that has already happened once here: a DEF/IF opcode mix-up got through, which is why
// D3DSIO_DEF (81) and D3DSIO_IF (40) are spelled out with a comment rather than left as literals.
// ShaderTranslationTests.RealFxcShaders exists to close the gap when real shaders are available.
//
// Token layout, from the D3D9 shader bytecode spec:
//   version   0xFFFF_MMmm (pixel) / 0xFFFE_MMmm (vertex)
//   opcode    [15:0] opcode, [27:24] parameter count
//   dst       bit31 set, [10:0] register, type split across [30:28] and [12:11],
//             [19:16] write mask, bit20 saturate
//   src       bit31 set, [10:0] register, type as above, [23:16] swizzle, [27:24] modifier
//   END       0x0000FFFF
//

using System;
using System.Collections.Generic;

namespace WgpuInterop.Tests.Harness
{
    /// <summary>A destination register token.</summary>
    internal sealed class Dst
    {
        public uint Token;

        private Dst(int type, int reg)
        {
            // bit31 set; type split across [30:28] and [12:11]; default write mask .xyzw
            Token = 0x80000000u | (uint)(reg & 0x7FF)
                  | ((uint)(type & 0x7) << 28) | ((uint)((type >> 3) & 0x3) << 11)
                  | (0xFu << 16);
        }

        public static Dst Temp(int r) => new(0, r);
        public static Dst ColorOut() => new(8, 0);

        public Dst Mask(int m) { Token = (Token & ~(0xFu << 16)) | ((uint)m << 16); return this; }
        public Dst Saturated() { Token |= 1u << 20; return this; }
    }

    /// <summary>A source register token.</summary>
    internal sealed class Src
    {
        public uint Token;

        private Src(int type, int reg)
        {
            Token = 0x80000000u | (uint)(reg & 0x7FF)
                  | ((uint)(type & 0x7) << 28) | ((uint)((type >> 3) & 0x3) << 11)
                  | (0xE4u << 16);                       // .xyzw
        }

        public static Src Temp(int r) => new(0, r);
        public static Src Const(int r) => new(2, r);
        public static Src Texture(int r) => new(3, r);
        public static Src Sampler(int r) => new(10, r);

        public Src Mod(int m) { Token = (Token & ~(0xFu << 24)) | ((uint)m << 24); return this; }

        public Src Swizzle(string s)
        {
            uint sw = 0;
            for (int i = 0; i < 4; i++) sw |= (uint)("xyzw".IndexOf(s[i]) & 3) << (i * 2);
            Token = (Token & ~(0xFFu << 16)) | (sw << 16);
            return this;
        }
    }

    /// <summary>Assembles a pixel-shader token stream.</summary>
    internal sealed class Ps
    {
        // D3DSIO_DEF is 81 (0x51). 40 is D3DSIO_IF -- they are NOT the same opcode, and confusing
        // them in the assembler AND the translator at once is a mistake that has already been made
        // here. Named constants rather than literals so the two cannot drift silently.
        public const int MOV = 1, ADD = 2, MUL = 5, LRP = 18, DEF = 81, TEX = 66, IF = 40;

        private readonly List<uint> _t = new();
        private bool _versioned;

        private void EnsureVersion() { if (!_versioned) { _t.Add(0xFFFF0200u); _versioned = true; } }

        public Ps Version(int major, int minor)
        { _t.Add((uint)(0xFFFF0000u | (major << 8) | minor)); _versioned = true; return this; }

        public Ps AsVertexShader() { _t.Add(0xFFFE0200u); _versioned = true; return this; }

        public Ps Op(int opcode, Dst d, params Src[] s)
        {
            EnsureVersion();
            _t.Add((uint)(opcode | ((1 + s.Length) << 24)));
            _t.Add(d.Token);
            foreach (Src x in s) _t.Add(x.Token);
            return this;
        }

        public Ps TexLd(Dst d, Src coord, Src samp) => Op(TEX, d, coord, samp);

        public Ps Def(int reg, float a, float b, float c, float e)
        {
            EnsureVersion();
            _t.Add((uint)(DEF | (5 << 24)));
            _t.Add(Dst.Temp(reg).Token & ~(0x70000000u | 0x1800u) | (2u << 28));   // c# register
            _t.Add(BitConverter.SingleToUInt32Bits(a));
            _t.Add(BitConverter.SingleToUInt32Bits(b));
            _t.Add(BitConverter.SingleToUInt32Bits(c));
            _t.Add(BitConverter.SingleToUInt32Bits(e));
            return this;
        }

        /// <summary>An arbitrary opcode with filler parameters, for the rejection cases.</summary>
        public Ps Raw(int opcode, int paramCount, uint fill)
        {
            EnsureVersion();
            _t.Add((uint)(opcode | (paramCount << 24)));
            for (int i = 0; i < paramCount; i++) _t.Add(fill);
            return this;
        }

        public byte[] Build()
        {
            EnsureVersion();
            _t.Add(0x0000FFFFu);                       // END
            var b = new byte[_t.Count * 4];
            for (int i = 0; i < _t.Count; i++) BitConverter.GetBytes(_t[i]).CopyTo(b, i * 4);
            return b;
        }
    }
}
