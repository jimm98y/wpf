// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The shim's HLSL compiler, shader model 4 and 5: intrinsic functions and the objects' methods. Each
// is the instructions fxc makes of it -- most one, some a few (normalize: dp, rsq, mul), the
// transcendental functions DXBC lacks (tan, the inverse trigonometric ones) the polynomial
// approximations fxc uses -- and the texture methods the sample/ld/resinfo/gather instructions.
//

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Wpf.Interop.WebGpu.Direct3D.Dxbc;

namespace Microsoft.Wpf.Interop.WebGpu.Direct3D.Hlsl
{
    internal sealed partial class DxbcCodegen
    {
        private Num Arg(List<Expr> args, int i, Node at) => ToNum(EvalValue(args[i]), args[i]);

        /// <summary>The arguments as floats of a common shape (the widest).</summary>
        private Num[] FloatArgs(List<Expr> args, Node at, int count)
        {
            if (args.Count != count) throw Err(at, $"wrong number of arguments: {args.Count} (expected {count})", 3013);
            var vals = args.Select(a => ToNum(EvalValue(a), a)).ToArray();
            int n = vals.Max(v => v.Count);
            var t = n == 1 ? NumericType.Float : NumericType.Vector(ScalarKind.Float, n);
            return vals.Select(v => ConvertNum(v, t, at)).ToArray();
        }

        /// <summary>The arguments of a common type, keeping integer kinds (min, max, abs, clamp).</summary>
        private Num[] CommonArgs(List<Expr> args, Node at, int count, out NumericType t)
        {
            if (args.Count != count) throw Err(at, $"wrong number of arguments: {args.Count} (expected {count})", 3013);
            var vals = args.Select(a => ToNum(EvalValue(a), a)).ToArray();
            var s = vals.Select(v => v.Type.Scalar).Aggregate(Promote);
            int n = vals.Max(v => v.Count);
            t = n == 1 ? NumericType.Of(s) : NumericType.Vector(s, n);
            var tt = t;
            return vals.Select(v => ConvertNum(v, tt, at)).ToArray();
        }

        private Value Intrinsic(string name, List<Expr> args, Node at)
        {
            switch (name)
            {
                // ---- one instruction --------------------------------------------------------------------
                case "abs":
                {
                    var a = CommonArgs(args, at, 1, out var t);
                    if (t.IsFloatLike) { var r = a[0].WithType(t); r.Mod = OperandModifier.Abs; return Wrap(Move(r, TempStore(t))); }
                    return Wrap(IntAbs(t, a[0]));
                }
                case "saturate": { var a = FloatArgs(args, at, 1); return Wrap(Move(a[0], TempStore(a[0].Type), saturate: true)); }
                case "min":
                case "max":
                {
                    var a = CommonArgs(args, at, 2, out var t);
                    bool max = name == "max";
                    var op = t.IsFloatLike ? (max ? Opcode.Max : Opcode.Min) : t.Scalar == ScalarKind.Uint ? (max ? Opcode.UMax : Opcode.UMin) : (max ? Opcode.IMax : Opcode.IMin);
                    return Wrap(Compute(op, t, a[0], a[1]));
                }
                case "clamp":
                {
                    var a = CommonArgs(args, at, 3, out var t);
                    var (mx, mn) = t.IsFloatLike ? (Opcode.Max, Opcode.Min) : t.Scalar == ScalarKind.Uint ? (Opcode.UMax, Opcode.UMin) : (Opcode.IMax, Opcode.IMin);
                    return Wrap(Compute(mn, t, Compute(mx, t, a[0], a[1]), a[2]));
                }
                case "sqrt": return Unary(Opcode.Sqrt, args, at);
                case "rsqrt": return Unary(Opcode.Rsq, args, at);
                case "rcp":
                {
                    var a = FloatArgs(args, at, 1);
                    return Wrap(Sm5 ? Compute(Opcode.Rcp, a[0].Type, a[0]) : Compute(Opcode.Div, a[0].Type, ImmFloat(1), a[0]));
                }
                case "exp2": return Unary(Opcode.Exp, args, at);
                case "log2": return Unary(Opcode.Log, args, at);
                case "frac": return Unary(Opcode.Frc, args, at);
                case "floor": return Unary(Opcode.RoundNi, args, at);
                case "ceil": return Unary(Opcode.RoundPi, args, at);
                case "round": return Unary(Opcode.RoundNe, args, at);
                case "trunc": return Unary(Opcode.RoundZ, args, at);
                case "ddx": return Unary(Opcode.DerivRtx, args, at);
                case "ddy": return Unary(Opcode.DerivRty, args, at);
                case "ddx_coarse": return Unary(Opcode.DerivRtxCoarse, args, at);
                case "ddy_coarse": return Unary(Opcode.DerivRtyCoarse, args, at);
                case "ddx_fine": return Unary(Opcode.DerivRtxFine, args, at);
                case "ddy_fine": return Unary(Opcode.DerivRtyFine, args, at);
                case "fwidth":
                {
                    var a = FloatArgs(args, at, 1);
                    var x = Compute(Opcode.DerivRtx, a[0].Type, a[0]); x.Mod = OperandModifier.Abs;
                    var y = Compute(Opcode.DerivRty, a[0].Type, a[0]); y.Mod = OperandModifier.Abs;
                    return Wrap(Compute(Opcode.Add, a[0].Type, x, y));
                }
                case "mad":
                {
                    var a = CommonArgs(args, at, 3, out var t);
                    return Wrap(Compute(t.IsFloatLike ? Opcode.Mad : t.Scalar == ScalarKind.Uint ? Opcode.UMad : Opcode.IMad, t, a[0], a[1], a[2]));
                }

                // ---- a few instructions ----------------------------------------------------------------
                case "exp":
                {
                    var a = FloatArgs(args, at, 1);
                    return Wrap(Compute(Opcode.Exp, a[0].Type, Compute(Opcode.Mul, a[0].Type, a[0], ImmFloat(1.44269502f))));
                }
                case "log":
                case "log10":
                {
                    var a = FloatArgs(args, at, 1);
                    return Wrap(Compute(Opcode.Mul, a[0].Type, Compute(Opcode.Log, a[0].Type, a[0]), ImmFloat(name == "log" ? 0.693147182f : 0.301029992f)));
                }
                case "pow":
                {
                    var a = FloatArgs(args, at, 2);
                    var l = Compute(Opcode.Log, a[0].Type, a[0].WithAbs());
                    return Wrap(Compute(Opcode.Exp, a[0].Type, Compute(Opcode.Mul, a[0].Type, l, a[1])));
                }
                case "sin":
                case "cos":
                {
                    var a = FloatArgs(args, at, 1);
                    var d = TempStore(a[0].Type);
                    var nul = DxOperand.Bare(OperandType.Null);
                    if (name == "sin") _w.Emit(Opcode.SinCos, 0, DestOf(d), nul, Aligned(a[0], d));
                    else _w.Emit(Opcode.SinCos, 0, nul, DestOf(d), Aligned(a[0], d));
                    return Wrap(Read(d));
                }
                case "sincos":
                {
                    if (args.Count != 3) throw Err(at, "sincos takes three arguments", 3013);
                    var x = Arg(args, 0, at);
                    x = ConvertNum(x, x.Type.WithScalar(ScalarKind.Float), at);
                    var s = TempStore(x.Type); var c = TempStore(x.Type);
                    _w.Emit(Opcode.SinCos, 0, DestOf(s), DestOf(c), Aligned(x, s));
                    Assign(StoreOf(args[1]), Wrap(Read(s)), at);
                    Assign(StoreOf(args[2]), Wrap(Read(c)), at);
                    return Wrap(ImmUint(0));
                }
                case "tan":
                {
                    var a = FloatArgs(args, at, 1);
                    var s = TempStore(a[0].Type); var c = TempStore(a[0].Type);
                    _w.Emit(Opcode.SinCos, 0, DestOf(s), DestOf(c), Aligned(a[0], s));
                    return Wrap(Compute(Opcode.Div, a[0].Type, Read(s), Read(c)));
                }
                case "sinh":
                case "cosh":
                case "tanh":
                {
                    var a = FloatArgs(args, at, 1);
                    var t = a[0].Type;
                    var ep = Compute(Opcode.Exp, t, Compute(Opcode.Mul, t, a[0], ImmFloat(1.44269502f)));
                    var en = Compute(Opcode.Exp, t, Compute(Opcode.Mul, t, a[0], ImmFloat(-1.44269502f)));
                    var sum = Compute(Opcode.Add, t, ep, en);
                    var diff = Compute(Opcode.Add, t, ep, Negate(en));
                    if (name == "tanh") return Wrap(Compute(Opcode.Div, t, diff, sum));
                    return Wrap(Compute(Opcode.Mul, t, name == "sinh" ? diff : sum, ImmFloat(0.5f)));
                }
                case "asin":
                case "acos":
                {
                    // acos(x) ~ sqrt(1 - |x|) * poly(|x|), mirrored for x < 0 (fxc's approximation)
                    var a = FloatArgs(args, at, 1);
                    var t = a[0].Type;
                    var ax = a[0].WithAbs();
                    var p = Compute(Opcode.Mad, t, ax, ImmFloat(-0.0187293f), ImmFloat(0.0742610f));
                    p = Compute(Opcode.Mad, t, p, ax, ImmFloat(-0.2121144f));
                    p = Compute(Opcode.Mad, t, p, ax, ImmFloat(1.5707288f));
                    var s = Compute(Opcode.Sqrt, t, Compute(Opcode.Add, t, ImmFloat(1), Negate(ax)));
                    var r = Compute(Opcode.Mul, t, p, s);
                    var neg = Compute(Opcode.Lt, t.WithScalar(ScalarKind.Bool), a[0], ImmFloat(0));
                    var mirrored = Compute(Opcode.Add, t, ImmFloat(3.14159265f), Negate(r));
                    var acos = Compute(Opcode.Movc, t, neg, mirrored, r);
                    if (name == "acos") return Wrap(acos);
                    return Wrap(Compute(Opcode.Add, t, ImmFloat(1.57079633f), Negate(acos)));
                }
                case "atan": return Wrap(Atan2(FloatArgs(args, at, 1)[0], null, at));
                case "atan2": { var a = FloatArgs(args, at, 2); return Wrap(Atan2(a[0], a[1], at)); }
                case "sign":
                {
                    var a = CommonArgs(args, at, 1, out var t);
                    var it = t.WithScalar(ScalarKind.Int);
                    var bt = t.WithScalar(ScalarKind.Bool);
                    Num pos, neg;
                    if (t.IsFloatLike) { pos = Compute(Opcode.Lt, bt, ImmFloat(0), a[0]); neg = Compute(Opcode.Lt, bt, a[0], ImmFloat(0)); }
                    else { pos = Compute(Opcode.ILt, bt, ImmInt(0), a[0]); neg = Compute(Opcode.ILt, bt, a[0], ImmInt(0)); }
                    // true is -1: neg - pos is the sign
                    return Wrap(Compute(Opcode.IAdd, it, neg.WithType(it), Negate(pos.WithType(it))));
                }
                case "step":
                {
                    var a = FloatArgs(args, at, 2);
                    var ge = Compute(Opcode.Ge, a[0].Type.WithScalar(ScalarKind.Bool), a[1], a[0]);
                    return Wrap(Compute(Opcode.And, a[0].Type, ge, ImmFloat(1)));
                }
                case "smoothstep":
                {
                    var a = FloatArgs(args, at, 3);
                    var t = a[0].Type;
                    var x = Compute(Opcode.Add, t, a[2], Negate(a[0]));
                    var w = Compute(Opcode.Add, t, a[1], Negate(a[0]));
                    var s = TempStore(t);
                    Op(Opcode.Div, s, true, x, w);
                    var v = Read(s);
                    var k = Compute(Opcode.Mad, t, v, ImmFloat(-2), ImmFloat(3));
                    return Wrap(Compute(Opcode.Mul, t, Compute(Opcode.Mul, t, v, v), k));
                }
                case "lerp":
                {
                    var a = FloatArgs(args, at, 3);
                    var d = Compute(Opcode.Add, a[0].Type, a[1], Negate(a[0]));
                    return Wrap(Compute(Opcode.Mad, a[0].Type, a[2], d, a[0]));
                }
                case "fmod": { var a = FloatArgs(args, at, 2); return Wrap(FloatMod(a[0].Type, a[0], a[1])); }
                case "dot":
                {
                    var a = CommonArgs(args, at, 2, out var t);
                    if (t.IsFloatLike)
                    {
                        var op = t.Count switch { 1 => Opcode.Mul, 2 => Opcode.Dp2, 3 => Opcode.Dp3, _ => Opcode.Dp4 };
                        return Wrap(Compute(op, NumericType.Float, a[0], a[1]));
                    }
                    var prod = IntMul(t, a[0], a[1]);
                    var sum = prod.Component(0);
                    for (int i = 1; i < t.Count; i++) sum = Compute(Opcode.IAdd, t.ScalarOf, sum, prod.Component(i));
                    return Wrap(sum);
                }
                case "cross":
                {
                    var a = FloatArgs(args, at, 2);
                    var t3 = NumericType.Vector(ScalarKind.Float, 3);
                    var x = ConvertNum(a[0], t3, at); var y = ConvertNum(a[1], t3, at);
                    var m = Compute(Opcode.Mul, t3, x.Swizzle(new[] { 2, 0, 1 }, t3), y.Swizzle(new[] { 1, 2, 0 }, t3));
                    return Wrap(Compute(Opcode.Mad, t3, x.Swizzle(new[] { 1, 2, 0 }, t3), y.Swizzle(new[] { 2, 0, 1 }, t3), Negate(m)));
                }
                case "length":
                {
                    var a = FloatArgs(args, at, 1);
                    return Wrap(Compute(Opcode.Sqrt, NumericType.Float, Dot(a[0], a[0])));
                }
                case "distance":
                {
                    var a = FloatArgs(args, at, 2);
                    var d = Compute(Opcode.Add, a[0].Type, a[0], Negate(a[1]));
                    return Wrap(Compute(Opcode.Sqrt, NumericType.Float, Dot(d, d)));
                }
                case "normalize":
                {
                    var a = FloatArgs(args, at, 1);
                    var r = Compute(Opcode.Rsq, NumericType.Float, Dot(a[0], a[0]));
                    return Wrap(Compute(Opcode.Mul, a[0].Type, r, a[0]));
                }
                case "reflect":
                {
                    var a = FloatArgs(args, at, 2);
                    var d = Dot(a[1], a[0]);
                    var k = Compute(Opcode.Add, NumericType.Float, d, d);
                    return Wrap(Compute(Opcode.Mad, a[0].Type, Negate(k), a[1], a[0]));
                }
                case "refract":
                {
                    var a = FloatArgs(args, at, 3);
                    var i = a[0]; var n = a[1]; var eta = a[2].Component(0);
                    var t = i.Type;
                    var ndi = Dot(n, i);
                    var k = Compute(Opcode.Mad, NumericType.Float, Negate(ndi), ndi, ImmFloat(1));
                    k = Compute(Opcode.Mul, NumericType.Float, Compute(Opcode.Mul, NumericType.Float, eta, eta), k);
                    k = Compute(Opcode.Add, NumericType.Float, ImmFloat(1), Negate(k));
                    var root = Compute(Opcode.Sqrt, NumericType.Float, k);
                    var coef = Compute(Opcode.Mad, NumericType.Float, eta, ndi, root);
                    var r = Compute(Opcode.Mad, t, eta, i, Negate(Compute(Opcode.Mul, t, coef, n)));
                    var ok = Compute(Opcode.Ge, NumericType.Bool, k, ImmFloat(0));
                    return Wrap(Compute(Opcode.And, t, ok, r));
                }
                case "faceforward":
                {
                    var a = FloatArgs(args, at, 3);
                    var neg = Compute(Opcode.Lt, NumericType.Bool, Dot(a[2], a[1]), ImmFloat(0));
                    return Wrap(Compute(Opcode.Movc, a[0].Type, neg, a[0], Negate(a[0])));
                }
                case "degrees": { var a = FloatArgs(args, at, 1); return Wrap(Compute(Opcode.Mul, a[0].Type, a[0], ImmFloat(57.2957795f))); }
                case "radians": { var a = FloatArgs(args, at, 1); return Wrap(Compute(Opcode.Mul, a[0].Type, a[0], ImmFloat(0.0174532925f))); }
                case "all":
                case "any":
                {
                    var v = Arg(args, 0, at);
                    var b = ConvertNum(v, v.Type.WithScalar(ScalarKind.Bool), at);
                    var r = b.Component(0);
                    for (int i = 1; i < b.Count; i++) r = Compute(name == "all" ? Opcode.And : Opcode.Or, NumericType.Bool, r, b.Component(i));
                    return Wrap(b.Count == 1 ? Move(r, TempStore(NumericType.Bool)) : r);
                }
                case "isnan":
                {
                    var a = FloatArgs(args, at, 1);
                    return Wrap(Compute(Opcode.Ne, a[0].Type.WithScalar(ScalarKind.Bool), a[0], a[0]));
                }
                case "isinf":
                case "isfinite":
                {
                    var a = FloatArgs(args, at, 1);
                    var bt = a[0].Type.WithScalar(ScalarKind.Bool);
                    var inf = Compute(Opcode.Eq, bt, a[0].WithAbs(), ImmUint(0x7F800000).WithType(a[0].Type));
                    if (name == "isinf") return Wrap(inf);
                    var nan = Compute(Opcode.Ne, bt, a[0], a[0]);
                    return Wrap(Compute(Opcode.Not, bt, Compute(Opcode.Or, bt, inf, nan)));
                }
                case "clip":
                {
                    if (_stage != ShaderStage.Pixel) throw Err(at, "clip is only valid in a pixel shader", 4532);
                    var a = FloatArgs(args, at, 1);
                    var lt = Compute(Opcode.Lt, a[0].Type.WithScalar(ScalarKind.Bool), a[0], ImmFloat(0));
                    var any = lt.Component(0);
                    for (int i = 1; i < lt.Count; i++) any = Compute(Opcode.Or, NumericType.Bool, any, lt.Component(i));
                    _w.Emit(Opcode.Discard, 1u << 7, any.Operand());
                    return Wrap(ImmUint(0));
                }
                case "mul": return Mul(EvalValue(args[0]), EvalValue(args[1]), at);
                case "transpose":
                {
                    var v = EvalValue(args[0]);
                    if (v is not MatValue m) return v;
                    var t = (NumericType)m.Type;
                    return new MatValue { Type = new NumericType(t.Scalar, t.Cols, t.Rows, matrix: true), Lines = m.Lines, Columns = !m.Columns };
                }
                case "determinant": return Wrap(Determinant(EvalValue(args[0]) as MatValue ?? throw Err(at, "determinant needs a matrix"), at));
                case "asfloat":
                case "asint":
                case "asuint":
                {
                    var v = Arg(args, 0, at);
                    var k = name == "asfloat" ? ScalarKind.Float : name == "asint" ? ScalarKind.Int : ScalarKind.Uint;
                    return Wrap(v.WithType(v.Type.WithScalar(k)));
                }
                case "countbits": return IntUnary(Opcode.CountBits, args, at, ScalarKind.Uint);
                case "reversebits": return IntUnary(Opcode.BfRev, args, at, ScalarKind.Uint);
                case "firstbitlow": return IntUnary(Opcode.FirstBitLo, args, at, ScalarKind.Uint);
                case "firstbithigh":
                {
                    var v = Arg(args, 0, at);
                    return Wrap(Compute(v.Type.Scalar == ScalarKind.Int ? Opcode.FirstBitShi : Opcode.FirstBitHi, v.Type.WithScalar(ScalarKind.Uint), v));
                }
                case "f32tof16": { var a = FloatArgs(args, at, 1); return Wrap(Compute(Opcode.F32toF16, a[0].Type.WithScalar(ScalarKind.Uint), a[0])); }
                case "f16tof32": { var v = Arg(args, 0, at); return Wrap(Compute(Opcode.F16toF32, v.Type.WithScalar(ScalarKind.Float), ConvertNum(v, v.Type.WithScalar(ScalarKind.Uint), at))); }
                case "msad4":
                {
                    // msad4(reference, source uint2, accum uint4): each of the four byte offsets of the
                    // source's window against the reference (msad per component)
                    if (!Sm5) throw Err(at, "msad4 needs shader model 5", 3004);
                    var rf = ConvertNum(Arg(args, 0, at), NumericType.Uint, at);
                    var src = ConvertNum(Arg(args, 1, at), NumericType.Vector(ScalarKind.Uint, 2), at);
                    var acc = ConvertNum(Arg(args, 2, at), NumericType.Vector(ScalarKind.Uint, 4), at);
                    var u4 = NumericType.Vector(ScalarKind.Uint, 4);
                    var win = TempStore(u4);
                    Op(Opcode.Mov, Sub(win, 0), false, src.Component(0));
                    for (int i = 1; i < 4; i++)
                        _w.Emit(Opcode.Bfi, 0, DestOf(Sub(win, i)), ImmUint((uint)(8 * i)).Operand(), ImmUint((uint)(32 - 8 * i)).Operand(),
                            Aligned(src.Component(1), Sub(win, i)), Aligned(Compute(Opcode.UShr, NumericType.Uint, src.Component(0), ImmUint((uint)(8 * i))), Sub(win, i)));
                    return Wrap(Compute(Opcode.Msad, u4, rf, Read(win), acc));
                }
                case "EvaluateAttributeAtSample":
                case "EvaluateAttributeCentroid":
                case "EvaluateAttributeSnapped":
                {
                    if (!Sm5 || _stage != ShaderStage.Pixel) throw Err(at, $"{name} needs a shader model 5 pixel shader", 3004);
                    var v = Arg(args, 0, at);
                    if (v.File != OperandType.Input) throw Err(at, $"{name}: the attribute must be a pixel shader input (not {v.File})", 3004);
                    var d = TempStore(v.Type);
                    var ops = new List<DxOperand> { DestOf(d), Aligned(v, d) };
                    if (name == "EvaluateAttributeAtSample") ops.Add(ConvertNum(Arg(args, 1, at), NumericType.Uint, at).Component(0).Operand());
                    if (name == "EvaluateAttributeSnapped") ops.Add(Pad(ConvertNum(Arg(args, 1, at), NumericType.Vector(ScalarKind.Int, 2), at)).Operand());
                    _w.Emit(name == "EvaluateAttributeAtSample" ? Opcode.EvalSampleIndex : name == "EvaluateAttributeCentroid" ? Opcode.EvalCentroid : Opcode.EvalSnapped, 0, ops.ToArray());
                    return Wrap(Read(d));
                }
                case "noise": throw Err(at, "noise is not available in shader model 4 and later", 3012);

                // ---- D3D9-style texture sampling --------------------------------------------------------
                case "tex1D": case "tex2D": case "tex3D": case "texCUBE":
                case "tex1Dlod": case "tex2Dlod": case "tex3Dlod": case "texCUBElod":
                case "tex1Dbias": case "tex2Dbias": case "tex3Dbias": case "texCUBEbias":
                case "tex1Dproj": case "tex2Dproj": case "tex3Dproj": case "texCUBEproj":
                case "tex1Dgrad": case "tex2Dgrad": case "tex3Dgrad": case "texCUBEgrad":
                    return Wrap(LegacySample(name, args, at));
            }
            throw Err(at, $"'{name}': undeclared identifier", 3004);
        }

        private Value Unary(Opcode op, List<Expr> args, Node at)
        {
            var a = FloatArgs(args, at, 1);
            return Wrap(Compute(op, a[0].Type, a[0]));
        }

        private Value IntUnary(Opcode op, List<Expr> args, Node at, ScalarKind result)
        {
            var v = Arg(args, 0, at);
            return Wrap(Compute(op, v.Type.WithScalar(result), ConvertNum(v, v.Type.WithScalar(ScalarKind.Uint), at)));
        }

        private Num Dot(Num a, Num b)
        {
            var op = a.Count switch { 1 => Opcode.Mul, 2 => Opcode.Dp2, 3 => Opcode.Dp3, _ => Opcode.Dp4 };
            return Compute(op, NumericType.Float, a, b);
        }

        /// <summary>atan(y / x) by quadrant (x null: atan(y)), with fxc's polynomial of the octant.</summary>
        private Num Atan2(Num y, Num? x, Node at)
        {
            var t = y.Type;
            var bt = t.WithScalar(ScalarKind.Bool);
            Num xx = x ?? ConvertNum(ImmFloat(1), t, at);
            var ax = xx.WithAbs(); var ay = y.WithAbs();
            var mn = Compute(Opcode.Min, t, ax, ay); var mx = Compute(Opcode.Max, t, ax, ay);
            var q = Compute(Opcode.Div, t, mn, mx);
            var q2 = Compute(Opcode.Mul, t, q, q);
            var p = Compute(Opcode.Mad, t, q2, ImmFloat(0.0208350997f), ImmFloat(-0.0851330012f));
            p = Compute(Opcode.Mad, t, q2, p, ImmFloat(0.180141002f));
            p = Compute(Opcode.Mad, t, q2, p, ImmFloat(-0.330299497f));
            p = Compute(Opcode.Mad, t, q2, p, ImmFloat(0.999866009f));
            var r = Compute(Opcode.Mul, t, p, q);
            // past 45 degrees: pi/2 - r; x < 0: pi - r; y < 0: -r
            var swap = Compute(Opcode.Lt, bt, ax, ay);
            r = Compute(Opcode.Movc, t, swap, Compute(Opcode.Add, t, ImmFloat(1.57079637f), Negate(r)), r);
            var xneg = Compute(Opcode.Lt, bt, xx, ImmFloat(0));
            r = Compute(Opcode.Movc, t, xneg, Compute(Opcode.Add, t, ImmFloat(3.14159274f), Negate(r)), r);
            var yneg = Compute(Opcode.Lt, bt, y, ImmFloat(0));
            return Compute(Opcode.Movc, t, yneg, Negate(r), r);
        }

        // ---- mul -------------------------------------------------------------------------------------

        private Value Mul(Value a, Value b, Node at)
        {
            if (a is NumValue an && b is NumValue bn)
            {
                // scalar or vector times vector: a component-wise product (scalar), or a dot (vectors)
                if (an.N.Type.IsScalar || bn.N.Type.IsScalar) return Arith("*", a, b, at);
                return Wrap(Dot(ConvertNum(an.N, an.N.Type.WithScalar(ScalarKind.Float), at), ConvertNum(bn.N, bn.N.Type.WithScalar(ScalarKind.Float), at)));
            }
            if (a is NumValue s && s.N.Type.IsScalar) return Arith("*", a, b, at);
            if (b is NumValue s2 && s2.N.Type.IsScalar) return Arith("*", a, b, at);
            if (a is NumValue v && b is MatValue m)
            {
                // a row vector times a matrix: result[j] = dot(v, column j)
                var mt = (NumericType)m.Type;
                var vf = ConvertNum(v.N, NumericType.Vector(ScalarKind.Float, mt.Rows), at);
                var rt = NumericType.Vector(ScalarKind.Float, mt.Cols);
                if (m.Columns)
                {
                    var d = TempStore(rt);
                    for (int j = 0; j < mt.Cols; j++) Op(DotOp(mt.Rows), Sub(d, j), false, vf, m.Lines[j]);
                    return Wrap(Read(d));
                }
                // by rows: v.x * row0 + v.y * row1 + ...
                Num acc = Compute(Opcode.Mul, rt, vf.Component(0), m.Lines[0].WithType(rt));
                for (int i = 1; i < mt.Rows; i++) acc = Compute(Opcode.Mad, rt, vf.Component(i), m.Lines[i].WithType(rt), acc);
                return Wrap(acc);
            }
            if (a is MatValue ma && b is NumValue vb)
            {
                // a matrix times a column vector: result[i] = dot(row i, v)
                var mt = (NumericType)ma.Type;
                var vf = ConvertNum(vb.N, NumericType.Vector(ScalarKind.Float, mt.Cols), at);
                var rt = NumericType.Vector(ScalarKind.Float, mt.Rows);
                if (!ma.Columns)
                {
                    var d = TempStore(rt);
                    for (int i = 0; i < mt.Rows; i++) Op(DotOp(mt.Cols), Sub(d, i), false, ma.Lines[i], vf);
                    return Wrap(Read(d));
                }
                Num acc = Compute(Opcode.Mul, rt, vf.Component(0), ma.Lines[0].WithType(rt));
                for (int j = 1; j < mt.Cols; j++) acc = Compute(Opcode.Mad, rt, vf.Component(j), ma.Lines[j].WithType(rt), acc);
                return Wrap(acc);
            }
            if (a is MatValue x && b is MatValue y)
            {
                var xt = (NumericType)x.Type; var yt = (NumericType)y.Type;
                var rows = new Num[xt.Rows];
                for (int i = 0; i < xt.Rows; i++) rows[i] = ToNum(Mul(Wrap(Row(x, i)), y, at), at);
                return new MatValue { Type = new NumericType(ScalarKind.Float, xt.Rows, yt.Cols, matrix: true), Lines = rows, Columns = false };
            }
            throw Err(at, $"mul of a {a.Type} and a {b.Type}");
        }

        private static Opcode DotOp(int n) => n switch { 1 => Opcode.Mul, 2 => Opcode.Dp2, 3 => Opcode.Dp3, _ => Opcode.Dp4 };

        private Num Determinant(MatValue m, Node at)
        {
            var t = (NumericType)m.Type;
            if (t.Rows != t.Cols) throw Err(at, "determinant of a matrix that is not square");
            Num E(int r, int c) => Element(m, r, c);
            Num M(Num a, Num b) => Compute(Opcode.Mul, NumericType.Float, a, b);
            Num S(Num a, Num b) => Compute(Opcode.Add, NumericType.Float, a, Negate(b));
            Num A(Num a, Num b) => Compute(Opcode.Add, NumericType.Float, a, b);
            switch (t.Rows)
            {
                case 1: return E(0, 0);
                case 2: return S(M(E(0, 0), E(1, 1)), M(E(0, 1), E(1, 0)));
                case 3:
                    return A(S(M(E(0, 0), S(M(E(1, 1), E(2, 2)), M(E(1, 2), E(2, 1)))), M(E(0, 1), S(M(E(1, 0), E(2, 2)), M(E(1, 2), E(2, 0))))),
                             M(E(0, 2), S(M(E(1, 0), E(2, 1)), M(E(1, 1), E(2, 0)))));
                default:
                {
                    // expansion along the first row of 3x3 minors
                    Num total = null!;
                    for (int c = 0; c < 4; c++)
                    {
                        var cols = Enumerable.Range(0, 4).Where(k => k != c).ToArray();
                        Num Q(int r, int k) => E(r, cols[k]);
                        var minor = A(S(M(Q(1, 0), S(M(Q(2, 1), Q(3, 2)), M(Q(2, 2), Q(3, 1)))), M(Q(1, 1), S(M(Q(2, 0), Q(3, 2)), M(Q(2, 2), Q(3, 0))))),
                                      M(Q(1, 2), S(M(Q(2, 0), Q(3, 1)), M(Q(2, 1), Q(3, 0)))));
                        var term = M(E(0, c), minor);
                        total = c == 0 ? term : (c % 2 == 1 ? S(total, term) : A(total, term));
                    }
                    return total;
                }
            }
        }

        // ---- textures --------------------------------------------------------------------------------

        /// <summary>A texture's coordinates for sample: its dimension's components (the array slice last).</summary>
        private static int CoordComponents(ObjectType t) => t.Name switch
        {
            "Texture1D" or "sampler1D" or "texture1D" => 1,
            "Texture1DArray" or "Texture2D" or "sampler2D" or "sampler" or "texture" or "texture2D" => 2,
            "Texture2DArray" or "Texture3D" or "TextureCube" or "sampler3D" or "samplerCUBE" or "texture3D" or "textureCUBE" => 3,
            "TextureCubeArray" => 4,
            _ => 2,
        };

        private static int OffsetComponents(ObjectType t) => t.Name switch
        {
            "Texture1D" or "Texture1DArray" => 1, "Texture3D" => 3, "TextureCube" or "TextureCubeArray" => 0, _ => 2,
        };

        private Num ResultOf(ObjectType t, NumStore d) => Read(d);

        private NumericType TexelType(ObjectType t) => t.Element as NumericType ?? NumericType.Vector(ScalarKind.Float, 4);

        private DxOperand ResourceOperand(ObjValue o) => DxOperand.Src(OperandType.Resource, new byte[] { 0, 1, 2, 3 }, o.Register);
        private DxOperand SamplerOperand(ObjValue s) => DxOperand.Bare(OperandType.Sampler, s.Sampler >= 0 ? s.Sampler : s.Register);

        /// <summary>sample-family instructions over a texture and a sampler, the texel's type's components.</summary>
        private Num Sample(Opcode op, ObjValue tex, ObjValue smp, Num coord, Num? extra1, Num? extra2, Num? offset, Node at, bool scalar = false)
        {
            var tt = TexelType(tex.Obj);
            var rt = scalar ? NumericType.Float : NumericType.Vector(tt.Scalar, 4);
            var d = TempStore(rt);
            var ops = new List<DxOperand> { DestOf(d), Pad(coord).Operand(), ResourceOperand(tex), SamplerOperand(smp) };
            if (scalar) ops[0] = DestOf(Sub(d, 0));
            if (extra1 != null) ops.Add(extra1.Component(0).Operand());
            if (extra2 != null) ops.Add(Pad(extra2).Operand());
            if (op == Opcode.SampleD) { ops[^2] = Pad(extra1!).Operand(); }
            uint[]? ext = null;
            if (offset != null)
            {
                if (offset.Imm == null) throw Err(at, "a texel offset must be a literal", 4000);
                int Comp(int i) => i < offset.Count ? (int)offset.Imm[Math.Min(i, offset.Imm.Length - 1)] & 0xF : 0;
                ext = new[] { 1u | (uint)Comp(0) << 9 | (uint)Comp(1) << 13 | (uint)Comp(2) << 17 };
            }
            if (ext != null) _w.EmitExtended(op, 0, ext, ops.ToArray());
            else _w.Emit(op, 0, ops.ToArray());
            var r = Read(d);
            if (scalar) return r.Component(0);
            return r.Swizzle(Enumerable.Range(0, Math.Max(1, tt.Count)).ToArray(), tt);
        }

        /// <summary>A vector padded to 4 components (an instruction's address operand reads xyzw).</summary>
        private static Num Pad(Num n) => n.Imm != null ? new Num { Type = n.Type, Imm = Enumerable.Range(0, 4).Select(i => i < n.Imm.Length ? n.Imm[i] : 0u).ToArray() } : n;

        private Value Method(MethodExpr m)
        {
            var target = EvalValue(m.Target);
            if (target is not ObjValue o) throw Err(m, $"'{m.Name}' is not a method of a {target.Type}", 3018);
            var t = o.Obj;
            ObjValue SamplerArg(int i) => EvalValue(m.Args[i]) as ObjValue ?? throw Err(m.Args[i], "a sampler was expected", 3017);
            Num Coord(int i, int n) => ConvertNum(Arg(m.Args, i, m), NumericType.Vector(ScalarKind.Float, n), m);
            Num? Offset(int i) => m.Args.Count > i ? ConvertNum(Arg(m.Args, i, m), NumericType.Vector(ScalarKind.Int, Math.Max(1, OffsetComponents(t))), m) : null;
            int cc = CoordComponents(t);
            switch (m.Name)
            {
                case "Sample": return Wrap(Sample(Opcode.Sample, o, SamplerArg(0), Coord(1, cc), null, null, Offset(2), m));
                case "SampleLevel": return Wrap(Sample(Opcode.SampleL, o, SamplerArg(0), Coord(1, cc), ToF(Arg(m.Args, 2, m), m), null, Offset(3), m));
                case "SampleBias": return Wrap(Sample(Opcode.SampleB, o, SamplerArg(0), Coord(1, cc), ToF(Arg(m.Args, 2, m), m), null, Offset(3), m));
                case "SampleGrad":
                {
                    var c = Coord(1, cc);
                    int g = t.Name.Contains("Array") ? cc - 1 : cc;
                    var dx = ConvertNum(Arg(m.Args, 2, m), NumericType.Vector(ScalarKind.Float, g), m);
                    var dy = ConvertNum(Arg(m.Args, 3, m), NumericType.Vector(ScalarKind.Float, g), m);
                    return Wrap(Sample(Opcode.SampleD, o, SamplerArg(0), c, dx, dy, Offset(4), m));
                }
                case "SampleCmp": return Wrap(Sample(Opcode.SampleC, o, SamplerArg(0), Coord(1, cc), ToF(Arg(m.Args, 2, m), m), null, Offset(3), m, scalar: true));
                case "SampleCmpLevelZero": return Wrap(Sample(Opcode.SampleCLz, o, SamplerArg(0), Coord(1, cc), ToF(Arg(m.Args, 2, m), m), null, Offset(3), m, scalar: true));
                case "Gather":
                case "GatherRed": case "GatherGreen": case "GatherBlue": case "GatherAlpha":
                {
                    if (_major == 4 && _minor < 1) throw Err(m, "Gather needs shader model 4.1", 3004);
                    var s = SamplerArg(0);
                    var c = Coord(1, cc);
                    int channel = m.Name switch { "GatherGreen" => 1, "GatherBlue" => 2, "GatherAlpha" => 3, _ => 0 };
                    return Wrap(GatherOp(o, s, c, channel, Offset(2), null, m));
                }
                case "GatherCmp":
                case "GatherCmpRed": case "GatherCmpGreen": case "GatherCmpBlue": case "GatherCmpAlpha":
                {
                    if (!Sm5) throw Err(m, "GatherCmp needs shader model 5", 3004);
                    var s = SamplerArg(0);
                    var c = Coord(1, cc);
                    var cmp = ToF(Arg(m.Args, 2, m), m);
                    int channel = m.Name switch { "GatherCmpGreen" => 1, "GatherCmpBlue" => 2, "GatherCmpAlpha" => 3, _ => 0 };
                    return Wrap(GatherOp(o, s, c, channel, Offset(3), cmp, m));
                }
                case "CalculateLevelOfDetail":
                case "CalculateLevelOfDetailUnclamped":
                {
                    if (_major == 4 && _minor < 1) throw Err(m, "CalculateLevelOfDetail needs shader model 4.1", 3004);
                    var s = SamplerArg(0);
                    var c = Coord(1, t.Name.Contains("Array") ? cc - 1 : cc);
                    var d = TempStore(NumericType.Vector(ScalarKind.Float, 4));
                    _w.Emit(Opcode.Lod, 0, DestOf(d), Pad(c).Operand(), ResourceOperand(o), SamplerOperand(s));
                    return Wrap(Read(d).Component(m.Name == "CalculateLevelOfDetail" ? 0 : 1));
                }
                case "GetSamplePosition":
                {
                    if (_major == 4 && _minor < 1) throw Err(m, "GetSamplePosition needs shader model 4.1", 3004);
                    var idx = ConvertNum(Arg(m.Args, 0, m), NumericType.Uint, m);
                    var d = TempStore(NumericType.Vector(ScalarKind.Float, 4));
                    _w.Emit(Opcode.SamplePos, 0, DestOf(d), ResourceOperand(o), idx.Component(0).Operand());
                    return Wrap(Read(d).Swizzle(new[] { 0, 1 }, NumericType.Vector(ScalarKind.Float, 2)));
                }
                case "Load": return Wrap(Load(o, Arg(m.Args, 0, m), m.Args.Count > 1 && t.Name.Contains("MS") ? Arg(m.Args, 1, m) : null, m.Args.Count > (t.Name.Contains("MS") ? 2 : 1) ? Offset(t.Name.Contains("MS") ? 2 : 1) : null, m));
                case "GetDimensions": GetDimensions(o, m.Args, m); return Wrap(ImmUint(0));
            }
            throw Err(m, $"'{m.Name}' is not a method of {t.Name}", 3018);
        }

        /// <summary>gather4 and its forms: _c with a reference, _po with an offset in a register.</summary>
        private Num GatherOp(ObjValue tex, ObjValue smp, Num coord, int channel, Num? offset, Num? cmp, Node at)
        {
            var tt = TexelType(tex.Obj);
            var d = TempStore(NumericType.Vector(tt.Scalar, 4));
            var sampler = DxOperand.Scalar(OperandType.Sampler, channel, smp.Sampler >= 0 ? smp.Sampler : smp.Register);
            sampler.Components = 4;
            bool literal = offset == null || offset.Imm != null;
            if (!literal && !Sm5) throw Err(at, "a gather offset that is not a literal needs shader model 5", 4000);
            var ops = new List<DxOperand> { DestOf(d), Pad(coord).Operand() };
            if (!literal) ops.Add(Pad(offset!).Operand());
            ops.Add(ResourceOperand(tex));
            ops.Add(sampler);
            if (cmp != null) ops.Add(cmp.Component(0).Operand());
            var op = cmp == null ? (literal ? Opcode.Gather4 : Opcode.Gather4Po) : (literal ? Opcode.Gather4C : Opcode.Gather4PoC);
            if (literal && offset != null)
                _w.EmitExtended(op, 0, new[] { 1u | (offset.Imm![0] & 0xF) << 9 | (offset.Imm[Math.Min(1, offset.Imm.Length - 1)] & 0xF) << 13 }, ops.ToArray());
            else _w.Emit(op, 0, ops.ToArray());
            return Read(d);
        }

        private Num ToF(Num n, Node at) => ConvertNum(n.Component(0), NumericType.Float, at);

        /// <summary>Texture.Load: the coordinates with the mip level in w (ld), or a 2DMS's sample index (ld2dms).</summary>
        private Num Load(ObjValue o, Num location, Num? sample, Num? offset, Node at)
        {
            var t = o.Obj;
            var tt = TexelType(t);
            var it = ConvertNum(location, location.Type.WithScalar(ScalarKind.Int), at);
            int coords = t.Name switch { "Texture1D" => 1, "Texture1DArray" or "Texture2D" or "Texture2DMS" => 2, _ => 3 };
            bool ms = t.Name.Contains("MS");
            bool buffer = t.Name is "Buffer" or "RWBuffer";
            var addr = TempStore(NumericType.Vector(ScalarKind.Int, 4));
            if (buffer)
            {
                Op(Opcode.Mov, addr, false, it.Component(0));
            }
            else
            {
                for (int i = 0; i < coords; i++) Op(Opcode.Mov, Sub(addr, i), false, it.Component(i));
                for (int i = coords; i < 3; i++) Op(Opcode.Mov, Sub(addr, i), false, ImmInt(0));
                Op(Opcode.Mov, Sub(addr, 3), false, ms ? ImmInt(0) : it.Count > coords ? it.Component(coords) : ImmInt(0));
            }
            var d = TempStore(NumericType.Vector(tt.Scalar, 4));
            var ops = new List<DxOperand> { DestOf(d), Read(addr).Operand(), ResourceOperand(o) };
            if (ms) ops.Add(ConvertNum(sample ?? ImmInt(0), NumericType.Int, at).Operand());
            uint[]? ext = null;
            if (offset?.Imm != null) ext = new[] { 1u | (offset.Imm[0] & 0xF) << 9 | (offset.Imm[Math.Min(1, offset.Imm.Length - 1)] & 0xF) << 13 };
            var op = ms ? Opcode.LdMs : t.IsUav ? Opcode.LdUavTyped : Opcode.Ld;
            if (ext != null) _w.EmitExtended(op, 0, ext, ops.ToArray());
            else _w.Emit(op, 0, ops.ToArray());
            return Read(d).Swizzle(Enumerable.Range(0, Math.Max(1, tt.Count)).ToArray(), tt);
        }

        /// <summary>texture[location]: Load with mip 0 (a texture), or the element (a buffer).</summary>
        private Num LoadIndexed(ObjValue o, Value index, Node at)
        {
            var idx = ToNum(index, at);
            var it = ConvertNum(idx, idx.Type.WithScalar(ScalarKind.Int), at);
            if (o.Obj.Name is "StructuredBuffer" or "RWStructuredBuffer") throw Err(at, "structured buffers are not supported yet");
            int coords = CoordComponents(o.Obj);
            var with0 = it.Count >= 4 ? it : Gather(Enumerable.Range(0, Math.Max(1, it.Count)).Select(i => it.Component(i)).Append(ImmInt(0)).ToList(), NumericType.Vector(ScalarKind.Int, it.Count + 1), at) is NumValue nv ? nv.N : it;
            return Load(o, o.Obj.Name is "Buffer" or "RWBuffer" ? it : with0, null, null, at);
        }

        private void GetDimensions(ObjValue o, List<Expr> args, Node at)
        {
            var t = o.Obj;
            bool hasMip = args.Count >= 1 && !(t.Name is "Buffer" or "RWBuffer" or "Texture2DMS" or "Texture2DMSArray" || t.IsUav) && args.Count > OutsOf(t);
            int first = hasMip ? 1 : 0;
            var mip = hasMip ? ConvertNum(Arg(args, 0, at), NumericType.Uint, at) : ImmUint(0);
            var outs = args.Skip(first).ToList();
            var d = TempStore(NumericType.Vector(ScalarKind.Uint, 4));
            bool floats = outs.Count > 0 && StoreOf(outs[0]).Type is NumericType { IsFloatLike: true };
            if (t.Name is "Buffer" or "RWBuffer")
                _w.Emit(Opcode.BufInfo, 0, DestOf(Sub(d, 0)), ResourceOperand(o));
            else
                _w.Emit(Opcode.ResInfo, floats ? 0u : 2u, DestOf(d), mip.Operand(), ResourceOperand(o));
            var r = Read(d);
            // (width, height or elements, depth or elements, levels), the outputs in that order for the dimension
            int[] order = t.Name switch
            {
                "Texture1D" => new[] { 0, 3 }, "Texture1DArray" => new[] { 0, 1, 3 },
                "Texture2D" or "TextureCube" => new[] { 0, 1, 3 }, "Texture2DArray" or "TextureCubeArray" or "Texture3D" => new[] { 0, 1, 2, 3 },
                // a multisampled texture's sample count is sampleinfo's (-1)
                "Texture2DMS" => new[] { 0, 1, -1 }, "Texture2DMSArray" => new[] { 0, 1, 2, -1 }, "Buffer" or "RWBuffer" => new[] { 0 },
                "RWTexture1D" => new[] { 0 }, "RWTexture2D" => new[] { 0, 1 }, "RWTexture2DArray" or "RWTexture3D" => new[] { 0, 1, 2 },
                _ => new[] { 0, 1, 3 },
            };
            for (int i = 0; i < outs.Count && i < order.Length; i++)
            {
                var s = StoreOf(outs[i]);
                Num v;
                if (order[i] < 0)
                {
                    var si = TempStore(NumericType.Uint);
                    _w.Emit(Opcode.SampleInfo, floats ? 0u : 1u, DestOf(si), ResourceOperand(o));
                    v = Read(si);
                }
                else v = r.Component(order[i]);
                if (floats) v = v.WithType(NumericType.Float);
                Assign(s, Wrap(v), at);
            }
        }

        private static int OutsOf(ObjectType t) => t.Name switch
        {
            "Texture1D" => 1, "Texture1DArray" => 2, "Texture2D" or "TextureCube" => 2, "Texture2DArray" or "TextureCubeArray" or "Texture3D" => 3, _ => 2,
        };

        /// <summary>tex2D and its family over a D3D9-style sampler: a texture and a sampler at one register.</summary>
        private Num LegacySample(string name, List<Expr> args, Node at)
        {
            var s = EvalValue(args[0]) as ObjValue ?? throw Err(at, "a sampler was expected", 3017);
            int cc = name.Contains("1D") ? 1 : name.Contains("2D") ? 2 : 3;
            var c = Arg(args, 1, at);
            var tex = new ObjValue { Type = new ObjectType(name.Contains("CUBE") ? "TextureCube" : name.Contains("3D") ? "Texture3D" : name.Contains("1D") ? "Texture1D" : "Texture2D", NumericType.Vector(ScalarKind.Float, 4)), Register = s.Register, Sampler = s.Register };
            if (name.EndsWith("lod", StringComparison.Ordinal))
            {
                var c4 = ConvertNum(c, NumericType.Vector(ScalarKind.Float, 4), at);
                return Sample(Opcode.SampleL, tex, s, c4.Swizzle(Enumerable.Range(0, cc).ToArray(), NumericType.Vector(ScalarKind.Float, cc)), c4.Component(3), null, null, at);
            }
            if (name.EndsWith("bias", StringComparison.Ordinal))
            {
                var c4 = ConvertNum(c, NumericType.Vector(ScalarKind.Float, 4), at);
                return Sample(Opcode.SampleB, tex, s, c4.Swizzle(Enumerable.Range(0, cc).ToArray(), NumericType.Vector(ScalarKind.Float, cc)), c4.Component(3), null, null, at);
            }
            if (name.EndsWith("proj", StringComparison.Ordinal))
            {
                var c4 = ConvertNum(c, NumericType.Vector(ScalarKind.Float, 4), at);
                var div = Compute(Opcode.Div, NumericType.Vector(ScalarKind.Float, cc), c4.Swizzle(Enumerable.Range(0, cc).ToArray(), NumericType.Vector(ScalarKind.Float, cc)), c4.Component(3));
                return Sample(Opcode.Sample, tex, s, div, null, null, null, at);
            }
            if (name.EndsWith("grad", StringComparison.Ordinal))
            {
                var dx = ConvertNum(Arg(args, 2, at), NumericType.Vector(ScalarKind.Float, cc), at);
                var dy = ConvertNum(Arg(args, 3, at), NumericType.Vector(ScalarKind.Float, cc), at);
                return Sample(Opcode.SampleD, tex, s, ConvertNum(c, NumericType.Vector(ScalarKind.Float, cc), at), dx, dy, null, at);
            }
            return Sample(Opcode.Sample, tex, s, ConvertNum(c, NumericType.Vector(ScalarKind.Float, cc), at), null, null, null, at);
        }
    }

    internal static class NumExtensions
    {
        public static DxbcCodegen.Num WithAbs(this DxbcCodegen.Num n)
        {
            var r = n.WithType(n.Type);
            if (r.Imm != null) { r.Imm = r.Imm.Select(x => x & 0x7FFFFFFF).ToArray(); return r; }
            r.Mod = OperandModifier.Abs;
            return r;
        }
    }
}
