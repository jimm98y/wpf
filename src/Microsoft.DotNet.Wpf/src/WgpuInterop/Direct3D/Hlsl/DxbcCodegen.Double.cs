// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The shim's HLSL compiler: doubles. A double takes two components of a register (xy or zw, the low
// word first), so a Num of doubles has two register components (and two immediate words) per logical
// component; a register holds two. The d* instructions read their double sources in that order and
// write a double destination's pairs, or one 32-bit component per double (the comparisons, dtof);
// what the float path computes with add, mul, movc..., a double computes with dadd, dmul, dmovc...
//

using System;
using System.Linq;
using Microsoft.Wpf.Interop.WebGpu.Direct3D.Dxbc;

namespace Microsoft.Wpf.Interop.WebGpu.Direct3D.Hlsl
{
    internal sealed partial class DxbcCodegen
    {
        private static bool IsDouble(NumericType t) => t.Scalar == ScalarKind.Double;

        /// <summary>An immediate of doubles: each value's low word, then its high word.</summary>
        private static Num ImmDouble(params double[] v) => new()
        {
            Type = v.Length == 1 ? NumericType.Double : NumericType.Vector(ScalarKind.Double, v.Length),
            Imm = v.SelectMany(d => { ulong b = BitConverter.DoubleToUInt64Bits(d); return new[] { (uint)b, (uint)(b >> 32) }; }).ToArray(),
        };

        private static double[] DoublesOf(Num n) => Enumerable.Range(0, n.Imm!.Length / 2).Select(i => BitConverter.UInt64BitsToDouble(n.Imm[2 * i] | (ulong)n.Imm[2 * i + 1] << 32)).ToArray();

        /// <summary>The double instruction for a float one.</summary>
        private static Opcode? DoubleOp(Opcode op) => op switch
        {
            Opcode.Mov => Opcode.DMov, Opcode.Movc => Opcode.DMovc, Opcode.Add => Opcode.DAdd, Opcode.Mul => Opcode.DMul,
            Opcode.Min => Opcode.DMin, Opcode.Max => Opcode.DMax, Opcode.Eq => Opcode.DEq, Opcode.Ne => Opcode.DNe,
            Opcode.Lt => Opcode.DLt, Opcode.Ge => Opcode.DGe, Opcode.Div => Opcode.DDiv, Opcode.Mad => Opcode.DFma, Opcode.Rcp => Opcode.DRcp,
            _ => null,
        };

        /// <summary>A double operand in its own order: one double as .xyxy (or .zwzw), two as they are.</summary>
        private static DxOperand DoubleOperand(Num v)
        {
            if (v.Imm != null)
            {
                var w = new uint[4];
                for (int i = 0; i < 4; i++) w[i] = v.Imm[i % 2 + 2 * Math.Min(i / 2, v.Imm.Length / 2 - 1)];
                return DxOperand.Imm(w);
            }
            var sw = new byte[4];
            for (int i = 0; i < 4; i++) sw[i] = v.Comp[i % 2 + 2 * Math.Min(i / 2, v.Comp.Length / 2 - 1)];
            var o = DxOperand.Src(v.File, sw, v.Index);
            o.Relative = (DxOperand?[])v.Relative.Clone();
            o.Modifier = v.Mod;
            return o;
        }

        /// <summary>A double operation, when the instruction is one (a double destination or source).</summary>
        private bool DoubleInstruction(Opcode op, NumStore dest, bool saturate, Num[] srcs)
        {
            bool destDouble = IsDouble((NumericType)dest.Type);
            if (!destDouble && !srcs.Any(s => IsDouble(s.Type))) return false;
            var dop = DoubleOp(op) ?? throw new HlslException(default, $"{op} of doubles");
            var ops = new DxOperand[srcs.Length + 1];
            ops[0] = DestOf(dest);
            for (int i = 0; i < srcs.Length; i++)
            {
                var s = srcs[i];
                // dmovc's condition is 32 bits per double
                if (IsDouble(s.Type)) ops[i + 1] = DoubleOperand(s);
                else if (dop == Opcode.DMovc && i == 0) ops[i + 1] = s.Operand();
                else ops[i + 1] = DoubleOperand(ConvertNum(s, s.Type.WithScalar(ScalarKind.Double), new ExprStmt()));
            }
            _w.Emit(dop, saturate ? 4u : 0u, ops);
            return true;
        }

        /// <summary>A conversion to or from doubles: ftod dtof itod utod dtoi dtou (and bools through them).</summary>
        private Num? DoubleConvert(Num n, NumericType t)
        {
            var from = n.Type.Scalar; var to = t.Scalar;
            if (from != ScalarKind.Double && to != ScalarKind.Double) return null;
            if (n.Imm != null)
            {
                if (from == ScalarKind.Double)
                    return new Num { Type = t, Imm = DoublesOf(n).Select(d => to switch
                    {
                        ScalarKind.Float => BitConverter.SingleToUInt32Bits((float)d),
                        ScalarKind.Bool => d != 0 ? 0xFFFFFFFFu : 0,
                        ScalarKind.Uint => d <= 0 ? 0 : (uint)Math.Min(d, uint.MaxValue),
                        _ => (uint)(int)Math.Clamp(d, int.MinValue, int.MaxValue),
                    }).ToArray() };
                var vals = n.Imm.Select(x => from switch
                {
                    ScalarKind.Float => (double)BitConverter.UInt32BitsToSingle(x),
                    ScalarKind.Uint => x, ScalarKind.Bool => x != 0 ? 1 : 0, _ => (int)x,
                }).ToArray();
                return ImmDouble(vals).WithType(t);
            }
            var d = TempStore(t);
            var src = from == ScalarKind.Double ? DoubleOperand(n) : n.Operand();
            switch (from, to)
            {
                case (ScalarKind.Double, ScalarKind.Double): return n.WithType(t);
                case (ScalarKind.Double, ScalarKind.Float): _w.Emit(Opcode.DtoF, 0, DestOf(d), src); break;
                case (ScalarKind.Double, ScalarKind.Int): _w.Emit(Opcode.DtoI, 0, DestOf(d), src); break;
                case (ScalarKind.Double, ScalarKind.Uint): _w.Emit(Opcode.DtoU, 0, DestOf(d), src); break;
                case (ScalarKind.Double, ScalarKind.Bool): _w.Emit(Opcode.DNe, 0, DestOf(d), src, DoubleOperand(ImmDouble(0))); break;
                case (ScalarKind.Float, ScalarKind.Double): _w.Emit(Opcode.FtoD, 0, DestOf(d), src); break;
                case (ScalarKind.Int, ScalarKind.Double): _w.Emit(Opcode.ItoD, 0, DestOf(d), src); break;
                case (ScalarKind.Uint, ScalarKind.Double): _w.Emit(Opcode.UtoD, 0, DestOf(d), src); break;
                case (ScalarKind.Bool, ScalarKind.Double):
                {
                    var f = Compute(Opcode.And, n.Type.WithScalar(ScalarKind.Float), n, ImmFloat(1f));
                    _w.Emit(Opcode.FtoD, 0, DestOf(d), f.Operand());
                    break;
                }
            }
            return Read(d);
        }
    }
}
