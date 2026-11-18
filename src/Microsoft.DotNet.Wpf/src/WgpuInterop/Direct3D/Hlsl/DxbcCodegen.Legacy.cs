// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The shim's HLSL compiler: shader models 2 and 3, the code side. The program is generated as for
// shader model 4 -- the same front end, the same values -- under the rules of the older models, then
// lowered to Direct3D 9 bytecode (DxbcCodegen.D3D9.cs):
//   - there are no integers: an int or a bool is a float (a bool 0 or 1), a conversion to int
//     truncates, and an immediate of an integer type is its value as a float;
//   - a comparison's result is 0 or 1 (slt, sge, cmp), so && is a product, || a maximum, ! is 1 - x
//     and a selection is a lerp or a cmp;
//   - uniforms take a register each (an array's elements, a matrix's rows or columns each one);
//   - inputs and outputs are never packed together;
//   - shader model 2 has no flow control a value decides: an if is both branches, each assignment
//     under a predicate (the branch's condition, 0 or 1: the new value where it is 1, the old where it
//     is 0), a return the result's assignment under it and the lanes it covers done; loops are
//     unrolled. Shader model 3 branches and loops (if, rep with break).
//

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Wpf.Interop.WebGpu.Direct3D.Dxbc;

namespace Microsoft.Wpf.Interop.WebGpu.Direct3D.Hlsl
{
    internal sealed partial class DxbcCodegen
    {
        /// <summary>Shader model 2 or 3.</summary>
        private bool _legacy;
        /// <summary>Shader model 2: no flow control, every branch predicated.</summary>
        private bool _flatten;
        /// <summary>Flattened code: the predicate assignments are under (null: all lanes).</summary>
        private Num? _pred;

        /// <summary>While shader model 2 or 3 code is generated: immediates of integer types are emitted as
        /// floats of their value (Num.Operand, Aligned).</summary>
        [ThreadStatic] private static bool s_legacyImmediates;

        /// <summary>An immediate word as the legacy models hold it: an integer as its float value, a bool 0 or 1.</summary>
        internal static uint LegacyWord(uint bits, ScalarKind kind) => kind switch
        {
            ScalarKind.Int => BitConverter.SingleToUInt32Bits((int)bits),
            ScalarKind.Uint => BitConverter.SingleToUInt32Bits(bits),
            ScalarKind.Bool => bits != 0 ? BitConverter.SingleToUInt32Bits(1f) : 0u,
            _ => bits,
        };

        private static uint[] ImmWords(Num v) => s_legacyImmediates ? v.Imm!.Select(x => LegacyWord(x, v.Type.Scalar)).ToArray() : v.Imm!;

        /// <summary>A legacy conversion: only to an integer (truncation) or to a bool (x != 0) does anything.</summary>
        private Num LegacyConvert(Num n, NumericType t)
        {
            var from = n.Type.Scalar; var to = t.Scalar;
            if (n.Imm != null) return new Num { Type = t, Imm = n.Imm.Select(x => ConvertBits(x, from, to)).ToArray() };
            if (to is ScalarKind.Int or ScalarKind.Uint && from is ScalarKind.Float) return Compute(Opcode.RoundZ, t, n.WithType(t.WithScalar(ScalarKind.Float))).WithType(t);
            if (to == ScalarKind.Bool && from != ScalarKind.Bool) return Compute(Opcode.Ne, t, n.WithType(n.Type.WithScalar(ScalarKind.Float)), ImmFloat(0));
            return n.WithType(t);
        }

        // ---- predication (shader model 2) ---------------------------------------------------------------

        /// <summary>The value an assignment writes under the predicate: the new one where it holds, the old elsewhere.</summary>
        private Num Predicated(Num value, NumStore store)
        {
            if (_pred == null) return value;
            var t = (NumericType)store.Type;
            return Compute(Opcode.Movc, t, _pred.Component(0), value, Read(store));
        }

        private Num And01(Num a, Num b) => Compute(Opcode.Mul, NumericType.Float, LegacyConvert(a.Component(0), NumericType.Float), LegacyConvert(b.Component(0), NumericType.Float));
        private Num Not01(Num a) => Compute(Opcode.Add, NumericType.Float, ImmFloat(1), Negate(LegacyConvert(a.Component(0), NumericType.Float)));

        /// <summary>An if, flattened: both branches, each under its predicate; the lanes a branch
        /// returned from are not active after it.</summary>
        private void FlatIf(IfStmt i)
        {
            var c = LegacyConvert(Condition(i.Cond), NumericType.Float);
            var saved = _pred;
            var frame = Current;
            var returnedBefore = frame.RetAcc != null ? Move(Read(frame.RetAcc), TempStore(NumericType.Float)) : null;
            _pred = saved == null ? Move(c, TempStore(NumericType.Float)) : And01(saved, c);
            FlatBranch(i.Then);
            if (i.Else != null)
            {
                var notC = Not01(c);
                _pred = saved == null ? notC : And01(saved, notC);
                FlatBranch(i.Else);
            }
            _pred = saved;
            // the lanes that returned in either branch are done
            if (frame.RetAcc != null)
            {
                var now = Read(frame.RetAcc);
                var fresh = returnedBefore == null ? now : Compute(Opcode.Add, NumericType.Float, now, Negate(returnedBefore));
                var alive = Not01(fresh);
                _pred = _pred == null ? alive : And01(_pred, alive);
            }
        }

        private void FlatBranch(Stmt s)
        {
            bool terminated = _terminated;
            Statement(s);
            _terminated = terminated;          // a return ends its branch only
        }

        /// <summary>The rest of a flattened block is not generated after a return (its lanes are done).</summary>
        private bool _terminated;

        /// <summary>return, flattened: the result assigned under the predicate, its lanes marked done.</summary>
        private void FlatReturn(JumpStmt j)
        {
            var f = Current;
            var result = f.IsEntry ? _entryReturn : f.Result;
            if (j.Value != null && result != null) Assign(result, Convert(Eval(j.Value, result.Type), result.Type, j.Value), j);
            if (f.RetAcc == null)
            {
                f.RetAcc = TempStore(NumericType.Float);
                Op(Opcode.Mov, f.RetAcc, false, ImmFloat(0));
            }
            // the lanes under the predicate (all, without one) are done
            Op(Opcode.Max, f.RetAcc, false, Read(f.RetAcc), _pred?.Component(0) ?? ImmFloat(1));
            _terminated = true;
        }
    }
}
