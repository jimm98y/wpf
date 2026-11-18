// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The shim's HLSL compiler, shader model 4 and 5: expressions -- names, members and swizzles,
// indexing, assignment, arithmetic with HLSL's promotion rules (a scalar spreads over a vector, a
// longer vector is cut to the shorter, int with uint is uint, either with float is float), the
// conversions between scalar kinds, constructors and casts, and calls (functions inlined).
//

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Wpf.Interop.WebGpu.Direct3D.Dxbc;

namespace Microsoft.Wpf.Interop.WebGpu.Direct3D.Hlsl
{
    internal sealed partial class DxbcCodegen
    {
        private Value Eval(Expr e, HlslType? expected) => EvalValue(e, false, expected);

        private Value EvalValue(Expr e, bool discard = false, HlslType? expected = null)
        {
            switch (e)
            {
                case LiteralExpr l:
                    if (l.IsBool) return Wrap(ImmBool(l.IntValue != 0));
                    if (l.IsFloat) return Wrap(l.Suffix == 'd' ? ImmFloat((float)l.FloatValue).WithType(NumericType.Double) : ImmFloat((float)l.FloatValue));
                    if (l.Suffix == 'u') return Wrap(ImmUint((uint)l.IntValue));
                    return Wrap(ImmInt((int)l.IntValue));
                case NameExpr n: return Name(n);
                case MemberExpr m: return Member(m);
                case IndexExpr i: return Index(i);
                case UnaryExpr u: return Unary(u, discard);
                case BinaryExpr b: return Binary(b);
                case AssignExpr a: return Assignment(a);
                case ConditionalExpr c: return Ternary(c);
                case CommaExpr c:
                {
                    Value last = null!;
                    for (int i = 0; i < c.Items.Count; i++) last = EvalValue(c.Items[i], i < c.Items.Count - 1);
                    return last;
                }
                case CastExpr c:
                {
                    var to = Resolve(c.To, c.ArrayDims);
                    return Convert(EvalValue(c.Value, false, to), to, c, explicitCast: true);
                }
                case CallExpr c when c.Constructor != null: return Construct(c);
                case CallExpr c: return Call(c);
                case MethodExpr m: return Method(m);
                case InitListExpr l when expected != null: return FromInitList(l, expected);
            }
            throw Err(e, $"unsupported expression {e.GetType().Name}");
        }

        private static NumValue Wrap(Num n) => new() { Type = n.Type, N = n };

        // ---- names ----------------------------------------------------------------------------------

        private Value Name(NameExpr n)
        {
            object? local = Lookup(n.Name);
            switch (local)
            {
                case Store s: return ReadStore(s);
                case Value v: return v;
            }
            if (_constants.TryGetValue(n.Name, out var k)) return k;
            if (_cbMembers.TryGetValue(n.Name, out var m)) return CbValue(m);
            if (_resourceByName.TryGetValue(n.Name, out var r)) return ResourceValue(r);
            throw Err(n, $"undeclared identifier '{n.Name}'", 3004);
        }

        private Value ResourceValue(Resource r)
        {
            r.Used = true;
            if (r.ArrayLength > 1) throw new HlslException(default, $"an array of resources ({r.Name}) is not supported yet");
            return new ObjValue { Type = r.Type, Register = Math.Max(0, r.Slot), Sampler = r.Legacy ? Math.Max(0, r.Slot) : -1 };
        }

        private Value CbValue(CbMember m)
        {
            m.Used = true;
            m.Buffer.Used = true;
            return CbAt(m.Buffer, m.Type, m.Offset);
        }

        /// <summary>A value of a type at a byte offset in a constant buffer.</summary>
        private Value CbAt(CBuffer cb, HlslType t, int offset)
        {
            long slot = Math.Max(0, cb.Slot);
            int reg = offset / 16, comp = (offset % 16) / 4;
            switch (t)
            {
                case NumericType n when n.IsMatrix:
                {
                    int lines = n.RowMajor ? n.Rows : n.Cols, per = n.RowMajor ? n.Cols : n.Rows;
                    var ls = new Num[lines];
                    for (int i = 0; i < lines; i++)
                        ls[i] = new Num { Type = NumericType.Vector(n.Scalar, per), File = OperandType.ConstantBuffer, Index = new[] { slot, (long)reg + i }, Relative = new DxOperand?[2], Comp = Seq(per) };
                    return new MatValue { Type = n, Lines = ls, Columns = !n.RowMajor };
                }
                case NumericType n:
                {
                    var num = new Num { Type = n, File = OperandType.ConstantBuffer, Index = new[] { slot, (long)reg }, Relative = new DxOperand?[2], Comp = Enumerable.Range(comp, Math.Max(1, n.Count)).Select(i => (byte)i).ToArray() };
                    // a bool in a constant buffer is 0 or 1: as a bool register it is 0 or ~0
                    if (n.Scalar == ScalarKind.Bool) return Wrap(Compute(Opcode.INe, n, num.WithType(n.WithScalar(ScalarKind.Uint)), ImmUint(0)));
                    return Wrap(num);
                }
                case ArrayType a:
                    return new ArrayRef { Type = a, File = OperandType.ConstantBuffer, FileIndex = slot, Base = reg, Stride = (SizeOf(a.Element) + 15) / 16 == 0 ? 1 : (SizeOf(a.Element) + 15) / 16, Tag = cb };
                case StructType s:
                {
                    var parts = new List<Value>();
                    int at = offset;
                    foreach (var f in s.Fields) { at = Place(at, f.Type); parts.Add(CbAt(cb, f.Type, at)); at += SizeOf(f.Type); }
                    return new AggValue { Type = s, Parts = parts.ToArray() };
                }
            }
            throw new InvalidOperationException($"a {t} in a constant buffer");
        }

        private Value ReadStore(Store s)
        {
            switch (s)
            {
                case NumStore n: return Wrap(Read(n));
                case AggStore a when a.Type is NumericType { IsMatrix: true } mt:
                    return new MatValue { Type = mt, Lines = a.Parts.Select(p => Read((NumStore)p)).ToArray(), Columns = false };
                case AggStore a: return new AggValue { Type = a.Type, Parts = a.Parts.Select(ReadStore).ToArray() };
            }
            throw new InvalidOperationException();
        }

        // ---- members, swizzles, indices -----------------------------------------------------------------

        private static int[]? SwizzleOf(string name, int count)
        {
            if (name.Length is 0 or > 4) return null;
            var r = new int[name.Length];
            string set = "xyzw".Contains(name[0]) ? "xyzw" : "rgba";
            for (int i = 0; i < name.Length; i++)
            {
                int k = set.IndexOf(name[i]);
                if (k < 0 || k >= Math.Max(count, 1)) return null;
                r[i] = k;
            }
            return r;
        }

        private Value Member(MemberExpr m)
        {
            var target = EvalValue(m.Target);
            switch (target)
            {
                case AggValue a when a.Type is StructType st:
                {
                    int i = st.Fields.FindIndex(f => f.Name == m.Name);
                    if (i < 0) throw Err(m, $"'{m.Name}' is not a member of {st.Name}", 3018);
                    return a.Parts[i];
                }
                case NumValue v:
                {
                    var pick = SwizzleOf(m.Name, v.N.Type.IsScalar ? 1 : v.N.Count) ?? throw Err(m, $"invalid subscript '{m.Name}'", 3018);
                    var t = pick.Length == 1 ? v.N.Type.ScalarOf : NumericType.Vector(v.N.Type.Scalar, pick.Length);
                    return Wrap(v.N.Swizzle(pick, t));
                }
                case MatValue mv when m.Name.StartsWith("_", StringComparison.Ordinal):
                {
                    // _m01 / _12 elements
                    var cells = MatrixSwizzle(m.Name, (NumericType)mv.Type, m);
                    var t = NumericType.Vector(((NumericType)mv.Type).Scalar, cells.Count);
                    var d = TempStore(cells.Count == 1 ? t.ScalarOf : t);
                    for (int i = 0; i < cells.Count; i++) Op(Opcode.Mov, Sub(d, i), false, Element(mv, cells[i].Row, cells[i].Col));
                    return Wrap(Read(d));
                }
            }
            throw Err(m, $"'{m.Name}' is not a member of a {target.Type}", 3018);
        }

        private static List<(int Row, int Col)> MatrixSwizzle(string s, NumericType t, Node at)
        {
            var r = new List<(int, int)>();
            int i = 0;
            while (i < s.Length)
            {
                if (s[i] != '_') throw Err(at, $"invalid subscript '{s}'", 3018);
                bool zero = i + 1 < s.Length && s[i + 1] == 'm';
                int a = zero ? 2 : 1;
                if (i + a + 1 >= s.Length + 0 && i + a + 1 > s.Length - 1 + 1) throw Err(at, $"invalid subscript '{s}'", 3018);
                int row = s[i + a] - '0' - (zero ? 0 : 1), col = s[i + a + 1] - '0' - (zero ? 0 : 1);
                if (row < 0 || row >= t.Rows || col < 0 || col >= t.Cols) throw Err(at, $"invalid subscript '{s}'", 3018);
                r.Add((row, col));
                i += a + 2;
            }
            return r;
        }

        /// <summary>Component i of a NumStore, as its own store.</summary>
        private static NumStore Sub(NumStore s, int i) => new()
        {
            Type = ((NumericType)s.Type).ScalarOf, File = s.File, Index = s.Index, Relative = s.Relative, Comp = new[] { s.Comp[Math.Min(i, s.Comp.Length - 1)] },
        };

        private Num Element(MatValue m, int row, int col) => m.Columns ? m.Lines[col].Component(row) : m.Lines[row].Component(col);

        /// <summary>Row i of a matrix, gathered into a temp when the matrix is held by columns.</summary>
        private Num Row(MatValue m, int i)
        {
            var t = (NumericType)m.Type;
            if (!m.Columns) return m.Lines[i].WithType(t.Row);
            var d = TempStore(t.Row);
            for (int j = 0; j < t.Cols; j++) Op(Opcode.Mov, Sub(d, j), false, m.Lines[j].Component(i));
            return Read(d);
        }

        private Num Column(MatValue m, int j)
        {
            var t = (NumericType)m.Type;
            var ct = NumericType.Vector(t.Scalar, t.Rows);
            if (m.Columns) return m.Lines[j].WithType(ct);
            var d = TempStore(ct);
            for (int i = 0; i < t.Rows; i++) Op(Opcode.Mov, Sub(d, i), false, m.Lines[i].Component(j));
            return Read(d);
        }

        private Value Index(IndexExpr ix)
        {
            var target = EvalValue(ix.Target);
            bool constant = TryConstIndex(ix.Index, out long k);
            switch (target)
            {
                case AggValue a when a.Type is ArrayType:
                    if (constant) return a.Parts[Math.Clamp((int)k, 0, a.Parts.Length - 1)];
                    // a local array (an indexable temp): its element's registers, relative to the index
                    return ReadStore(StoreOf(ix));
                case ArrayRef r:
                {
                    var at = (ArrayType)r.Type;
                    if (constant)
                    {
                        if (r.File == OperandType.ConstantBuffer && r.Tag is CBuffer cb) return CbAt(cb, at.Element, (int)(r.Base + k * r.Stride) * 16);
                        return ArrayElement(r, at.Element, ImmOffset(r.Base + k * r.Stride), null);
                    }
                    var idx = Convert(EvalValue(ix.Index), NumericType.Int, ix.Index).Component(0);
                    if (r.Stride != 1) idx = Compute(Opcode.IMul, NumericType.Int, idx, ImmInt(r.Stride));
                    idx = Registerize(idx);
                    if (r.Tag is CBuffer c2) c2.DynamicallyIndexed = true;
                    return ArrayElement(r, at.Element, r.Base, idx.Operand());
                }
                case NumValue v when !v.N.Type.IsScalar:
                    if (constant) return Wrap(v.N.Component((int)k));
                    return Wrap(DynamicComponent(v.N, Convert(EvalValue(ix.Index), NumericType.Int, ix.Index).Component(0)));
                case MatValue m:
                    if (constant) return Wrap(Row(m, (int)k));
                    throw Err(ix, "a dynamic index into a matrix is not supported");
                case ObjValue o:
                    return Wrap(LoadIndexed(o, EvalValue(ix.Index), ix));
            }
            throw Err(ix, $"a {target.Type} cannot be indexed", 3121);
        }

        private static long ImmOffset(long v) => v;

        private bool TryConstIndex(Expr e, out long k)
        {
            try { k = ConstInt(e); return true; } catch (HlslException) { k = 0; return false; }
        }

        /// <summary>An index (an int scalar) in a temp's x: what a relative operand takes.</summary>
        private Num Registerize(Num idx)
        {
            if (idx.File == OperandType.Temp && idx.Imm == null && idx.Mod == OperandModifier.None) return idx;
            return Move(idx, TempStore(NumericType.Int));
        }

        /// <summary>An element of an array in an indexable temp, a constant buffer or the immediate constant buffer.</summary>
        private Value ArrayElement(ArrayRef r, HlslType elem, long offset, DxOperand? rel)
        {
            Num Line(NumericType t, long at)
            {
                var n = new Num { Type = t, File = r.File, Comp = Seq(t.Count) };
                if (r.File == OperandType.ImmediateConstantBuffer) { n.Index = new[] { at }; n.Relative = new[] { rel }; }
                else { n.Index = new[] { r.FileIndex, at }; n.Relative = new[] { null, rel }; }
                return n;
            }
            switch (elem)
            {
                case NumericType n when n.IsMatrix:
                {
                    int lines = n.RowMajor || r.File != OperandType.ConstantBuffer ? n.Rows : n.Cols;
                    int per = n.RowMajor || r.File != OperandType.ConstantBuffer ? n.Cols : n.Rows;
                    return new MatValue { Type = n, Lines = Enumerable.Range(0, lines).Select(i => Line(NumericType.Vector(n.Scalar, per), offset + i)).ToArray(), Columns = r.File == OperandType.ConstantBuffer && !n.RowMajor };
                }
                case NumericType n:
                {
                    var v = Line(n, offset);
                    if (n.Scalar == ScalarKind.Bool && r.File == OperandType.ConstantBuffer) return Wrap(Compute(Opcode.INe, n, v.WithType(n.WithScalar(ScalarKind.Uint)), ImmUint(0)));
                    return Wrap(v);
                }
                case StructType s when r.File == OperandType.ConstantBuffer:
                {
                    var parts = new List<Value>();
                    int at = 0;
                    foreach (var f in s.Fields)
                    {
                        at = Place(at, f.Type);
                        if (rel != null && at % 16 != 0) throw new HlslException(default, "a dynamically indexed struct with a member inside a register is not supported");
                        if (f.Type is NumericType fn && !fn.IsMatrix)
                        {
                            var line = Line(fn, offset + at / 16);
                            line.Comp = Enumerable.Range((at % 16) / 4, Math.Max(1, fn.Count)).Select(i => (byte)i).ToArray();
                            parts.Add(Wrap(line));
                        }
                        else parts.Add(ArrayElement(r, f.Type, offset + at / 16, rel));
                        at += SizeOf(f.Type);
                    }
                    return new AggValue { Type = s, Parts = parts.ToArray() };
                }
            }
            throw new HlslException(default, $"an array of {elem} indexed dynamically is not supported");
        }

        /// <summary>v[i] for a vector and a dynamic i: each component selected by comparison.</summary>
        private Num DynamicComponent(Num v, Num i)
        {
            var d = TempStore(v.Type.ScalarOf);
            Op(Opcode.Mov, d, false, v.Component(0));
            for (int c = 1; c < v.Count; c++)
            {
                var eq = Compute(Opcode.IEq, NumericType.Bool, i, ImmInt(c));
                Op(Opcode.Movc, d, false, eq, v.Component(c), Read(d));
            }
            return Read(d);
        }

        // ---- l-values ----------------------------------------------------------------------------------

        private Store StoreOf(Expr e)
        {
            switch (e)
            {
                case NameExpr n:
                    if (Lookup(n.Name) is Store s) return s;
                    throw Err(e, $"'{n.Name}' cannot be assigned to", 3025);
                case MemberExpr m:
                {
                    var t = StoreOf(m.Target);
                    if (t is AggStore a && a.Type is StructType st)
                    {
                        int i = st.Fields.FindIndex(f => f.Name == m.Name);
                        if (i < 0) throw Err(m, $"'{m.Name}' is not a member of {st.Name}", 3018);
                        return a.Parts[i];
                    }
                    if (t is NumStore ns)
                    {
                        var nt = (NumericType)ns.Type;
                        var pick = SwizzleOf(m.Name, nt.IsScalar ? 1 : nt.Count) ?? throw Err(m, $"invalid subscript '{m.Name}'", 3018);
                        if (pick.Distinct().Count() != pick.Length) throw Err(m, "a swizzle with a repeated component cannot be assigned to", 3025);
                        return new NumStore { Type = pick.Length == 1 ? nt.ScalarOf : NumericType.Vector(nt.Scalar, pick.Length), File = ns.File, Index = ns.Index, Relative = ns.Relative, Comp = pick.Select(p => ns.Comp[p]).ToArray() };
                    }
                    break;
                }
                case IndexExpr ix:
                {
                    var t = StoreOf(ix.Target);
                    bool constant = TryConstIndex(ix.Index, out long k);
                    if (t is AggStore a && a.Type is ArrayType at)
                    {
                        if (constant) return a.Parts[Math.Clamp((int)k, 0, a.Parts.Length - 1)];
                        // a dynamic index: the element's registers, relative to the index times the element's size
                        if (a.Parts[0] is not NumStore and not AggStore || FirstNum(a.Parts[0]) is not { File: OperandType.IndexableTemp } first)
                            throw Err(ix, "a dynamic index into this array cannot be assigned to");
                        int per = Registers(at.Element);
                        var idx = Convert(EvalValue(ix.Index), NumericType.Int, ix.Index).Component(0);
                        if (per != 1) idx = Compute(Opcode.IMul, NumericType.Int, idx, ImmInt(per));
                        idx = Registerize(idx);
                        return Relativize(a.Parts[0], idx.Operand());
                    }
                    if (t is AggStore m && m.Type is NumericType { IsMatrix: true })
                    {
                        if (constant) return m.Parts[(int)k];
                    }
                    if (t is NumStore v && constant) return Sub(v, (int)k);
                    break;
                }
            }
            throw Err(e, "this expression cannot be assigned to", 3025);
        }

        private static NumStore? FirstNum(Store s) => s is NumStore n ? n : s is AggStore a && a.Parts.Length > 0 ? FirstNum(a.Parts[0]) : null;

        /// <summary>The store with every register index made relative to an index operand.</summary>
        private static Store Relativize(Store s, DxOperand idx)
        {
            if (s is NumStore n)
            {
                var r = new NumStore { Type = n.Type, File = n.File, Index = n.Index, Comp = n.Comp, Relative = (DxOperand?[])n.Relative.Clone() };
                r.Relative[^1] = idx;
                return r;
            }
            var a = (AggStore)s;
            return new AggStore { Type = a.Type, Parts = a.Parts.Select(p => Relativize(p, idx)).ToArray() };
        }

        private void Assign(Store s, Value v, Node at)
        {
            switch (s)
            {
                case NumStore n:
                    Op(Opcode.Mov, n, false, Convert(v, (NumericType)n.Type, at));
                    return;
                case AggStore a when a.Type is NumericType { IsMatrix: true } mt:
                {
                    var mv = v as MatValue ?? throw Err(at, $"cannot convert from '{v.Type}' to '{mt}'", 3017);
                    for (int i = 0; i < a.Parts.Length; i++) Op(Opcode.Mov, (NumStore)a.Parts[i], false, Convert(Wrap(Row(mv, i)), ((NumericType)a.Parts[i].Type), at));
                    return;
                }
                case AggStore a:
                {
                    if (v is ArrayRef r)
                    {
                        var at2 = (ArrayType)r.Type;
                        for (int i = 0; i < a.Parts.Length; i++) Assign(a.Parts[i], ArrayElement(r, at2.Element, r.Base + i * r.Stride, null), at);
                        return;
                    }
                    var av = v as AggValue ?? throw Err(at, $"cannot convert from '{v.Type}' to '{a.Type}'", 3017);
                    for (int i = 0; i < a.Parts.Length && i < av.Parts.Length; i++) Assign(a.Parts[i], av.Parts[i], at);
                    return;
                }
            }
        }

        private Value Assignment(AssignExpr a)
        {
            var store = StoreOf(a.Target);
            Value v;
            if (a.Op == "=") v = EvalValue(a.Value, false, store.Type);
            else
            {
                string op = a.Op.Substring(0, a.Op.Length - 1);
                v = Arith(op, ReadStore(store), EvalValue(a.Value), a);
            }
            v = Convert(v, store.Type, a);
            Assign(store, v, a);
            return ReadStore(store);
        }

        // ---- operators ---------------------------------------------------------------------------------

        private Value Unary(UnaryExpr u, bool discard)
        {
            if (u.Op is "++" or "--")
            {
                var s = StoreOf(u.Operand);
                var before = Wrap(Move(Convert(ReadStore(s), (NumericType)s.Type, u), TempStore((NumericType)s.Type)));
                var t = (NumericType)s.Type;
                var one = t.IsFloatLike ? ImmFloat(1) : ImmInt(1);
                var after = Arith(u.Op == "++" ? "+" : "-", before, Wrap(one), u);
                Assign(s, after, u);
                return u.Postfix ? before : ReadStore(s);
            }
            var v = EvalValue(u.Operand);
            if (v is MatValue mv && u.Op is "-" or "+")
            {
                if (u.Op == "+") return mv;
                return new MatValue { Type = mv.Type, Columns = mv.Columns, Lines = mv.Lines.Select(l => Negate(l)).ToArray() };
            }
            var n = ToNum(v, u);
            switch (u.Op)
            {
                case "+": return Wrap(n);
                case "-": return Wrap(Negate(n));
                case "!": return Wrap(Compute(Opcode.Not, n.Type.WithScalar(ScalarKind.Bool), Convert(Wrap(n), n.Type.WithScalar(ScalarKind.Bool), u)));
                case "~":
                    if (!n.Type.IsIntLike) throw Err(u, "'~' needs an integer operand", 3082);
                    return Wrap(Compute(Opcode.Not, n.Type.Scalar == ScalarKind.Bool ? n.Type.WithScalar(ScalarKind.Int) : n.Type, n));
            }
            throw Err(u, $"unsupported operator {u.Op}");
        }

        private Num Negate(Num n)
        {
            if (n.Type.IsFloatLike)
            {
                if (n.Imm != null) return new Num { Type = n.Type, Imm = n.Imm.Select(x => x ^ 0x80000000).ToArray() };
                var r = (Num)n.WithType(n.Type);
                r.Mod = n.Mod switch { OperandModifier.None => OperandModifier.Neg, OperandModifier.Neg => OperandModifier.None, OperandModifier.Abs => OperandModifier.AbsNeg, _ => OperandModifier.Abs };
                return r;
            }
            var t = n.Type.Scalar == ScalarKind.Bool ? n.Type.WithScalar(ScalarKind.Int) : n.Type;
            if (n.Imm != null) return new Num { Type = t, Imm = n.Imm.Select(x => (uint)-(int)x).ToArray() };
            return Compute(Opcode.INeg, t, Convert(Wrap(n), t, null));
        }

        private Num ToNum(Value v, Node at) => v as NumValue is { } nv ? nv.N : throw Err(at, $"a {v.Type} is not a number here", 3020);

        private static ScalarKind Promote(ScalarKind a, ScalarKind b)
        {
            int Rank(ScalarKind s) => s switch { ScalarKind.Bool => 0, ScalarKind.Int => 1, ScalarKind.Uint => 2, ScalarKind.Float => 3, _ => 4 };
            var r = Rank(a) >= Rank(b) ? a : b;
            return r == ScalarKind.Bool ? ScalarKind.Int : r;
        }

        /// <summary>The common shape of two operands: a scalar spreads, a longer vector is cut.</summary>
        private static NumericType Common(NumericType a, NumericType b, ScalarKind s)
        {
            if (a.IsMatrix || b.IsMatrix)
            {
                var m = a.IsMatrix ? a : b;
                return new NumericType(s, m.Rows, m.Cols, matrix: true, m.RowMajor);
            }
            int n = a.IsScalar ? b.Count : b.IsScalar ? a.Count : Math.Min(a.Count, b.Count);
            return n == 1 ? NumericType.Of(s) : NumericType.Vector(s, n);
        }

        private Value Binary(BinaryExpr b)
        {
            if (b.Op is "&&" or "||")
            {
                // not short-circuited, as HLSL's are not
                var l = EvalValue(b.Left); var r = EvalValue(b.Right);
                var ln = ToNum(l, b.Left); var rn = ToNum(r, b.Right);
                var t = Common(ln.Type, rn.Type, ScalarKind.Bool);
                return Wrap(Compute(b.Op == "&&" ? Opcode.And : Opcode.Or, t, Convert(l, t, b), Convert(r, t, b)));
            }
            return Arith(b.Op, EvalValue(b.Left), EvalValue(b.Right), b);
        }

        private Value Arith(string op, Value lv, Value rv, Node at)
        {
            if (lv is MatValue || rv is MatValue) return MatrixArith(op, lv, rv, at);
            var l = ToNum(lv, at); var r = ToNum(rv, at);
            bool compare = op is "==" or "!=" or "<" or ">" or "<=" or ">=";
            bool bits = op is "&" or "|" or "^" or "<<" or ">>" or "%" && !(op == "%" && (l.Type.IsFloatLike || r.Type.IsFloatLike));
            var s = Promote(l.Type.Scalar, r.Type.Scalar);
            if (op is "<<" or ">>") s = l.Type.Scalar == ScalarKind.Bool ? ScalarKind.Int : l.Type.Scalar;
            if (bits && s is ScalarKind.Float or ScalarKind.Double) throw Err(at, $"'{op}' needs integer operands", 3082);
            var t = Common(l.Type, r.Type, s);
            var a = Convert(lv, t, at); var c = Convert(rv, t, at);
            if (compare)
            {
                var bt = t.WithScalar(ScalarKind.Bool);
                bool f = s is ScalarKind.Float or ScalarKind.Double, u = s == ScalarKind.Uint;
                return op switch
                {
                    "==" => Wrap(Compute(f ? Opcode.Eq : Opcode.IEq, bt, a, c)),
                    "!=" => Wrap(Compute(f ? Opcode.Ne : Opcode.INe, bt, a, c)),
                    "<" => Wrap(Compute(f ? Opcode.Lt : u ? Opcode.ULt : Opcode.ILt, bt, a, c)),
                    ">" => Wrap(Compute(f ? Opcode.Lt : u ? Opcode.ULt : Opcode.ILt, bt, c, a)),
                    ">=" => Wrap(Compute(f ? Opcode.Ge : u ? Opcode.UGe : Opcode.IGe, bt, a, c)),
                    _ => Wrap(Compute(f ? Opcode.Ge : u ? Opcode.UGe : Opcode.IGe, bt, c, a)),
                };
            }
            bool fl = s is ScalarKind.Float or ScalarKind.Double;
            switch (op)
            {
                case "+": return Wrap(Compute(fl ? Opcode.Add : Opcode.IAdd, t, a, c));
                case "-": return Wrap(Compute(fl ? Opcode.Add : Opcode.IAdd, t, a, Negate(c)));
                case "*":
                    if (fl) return Wrap(Compute(Opcode.Mul, t, a, c));
                    return Wrap(IntMul(t, a, c));
                case "/":
                    if (fl) return Wrap(Compute(Opcode.Div, t, a, c));
                    return Wrap(IntDivide(t, a, c, remainder: false));
                case "%":
                    if (fl) return Wrap(FloatMod(t, a, c));
                    return Wrap(IntDivide(t, a, c, remainder: true));
                case "&": return Wrap(Compute(Opcode.And, t, a, c));
                case "|": return Wrap(Compute(Opcode.Or, t, a, c));
                case "^": return Wrap(Compute(Opcode.Xor, t, a, c));
                case "<<": return Wrap(Compute(Opcode.IShl, t, a, c));
                case ">>": return Wrap(Compute(s == ScalarKind.Uint ? Opcode.UShr : Opcode.IShr, t, a, c));
            }
            throw Err(at, $"unsupported operator {op}");
        }

        /// <summary>imul's low half (the high one to null).</summary>
        private Num IntMul(NumericType t, Num a, Num b)
        {
            var d = TempStore(t);
            _w.Emit(t.Scalar == ScalarKind.Uint ? Opcode.UMul : Opcode.IMul, 0, DxOperand.Bare(OperandType.Null), DestOf(d), Aligned(a, d), Aligned(b, d));
            return Read(d);
        }

        /// <summary>udiv for uints; for ints the magnitudes divided and the sign put back (no idiv in DXBC).</summary>
        private Num IntDivide(NumericType t, Num a, Num b, bool remainder)
        {
            var d = TempStore(t);
            if (t.Scalar == ScalarKind.Uint)
            {
                if (remainder) _w.Emit(Opcode.UDiv, 0, DxOperand.Bare(OperandType.Null), DestOf(d), Aligned(a, d), Aligned(b, d));
                else _w.Emit(Opcode.UDiv, 0, DestOf(d), DxOperand.Bare(OperandType.Null), Aligned(a, d), Aligned(b, d));
                return Read(d);
            }
            var ua = IntAbs(t, a); var ub = IntAbs(t, b);
            var q = TempStore(t);
            if (remainder) _w.Emit(Opcode.UDiv, 0, DxOperand.Bare(OperandType.Null), DestOf(q), Aligned(ua, q), Aligned(ub, q));
            else _w.Emit(Opcode.UDiv, 0, DestOf(q), DxOperand.Bare(OperandType.Null), Aligned(ua, q), Aligned(ub, q));
            // the sign: the dividend's for a remainder, the operands' differing for a quotient
            var neg = remainder ? Compute(Opcode.ILt, t.WithScalar(ScalarKind.Bool), a, ImmInt(0))
                                : Compute(Opcode.ILt, t.WithScalar(ScalarKind.Bool), Compute(Opcode.Xor, t, a, b), ImmInt(0));
            var nq = Compute(Opcode.INeg, t, Read(q));
            Op(Opcode.Movc, d, false, neg, nq, Read(q));
            return Read(d);
        }

        private Num IntAbs(NumericType t, Num a) => Compute(Opcode.IMax, t, a, Compute(Opcode.INeg, t, a));

        /// <summary>fmod / % of floats: a - b * trunc(a / b), the sign of a.</summary>
        private Num FloatMod(NumericType t, Num a, Num b)
        {
            var q = Compute(Opcode.Div, t, a, b);
            var tq = Compute(Opcode.RoundZ, t, q);
            var d = TempStore(t);
            _w.Emit(Opcode.Mad, 0, DestOf(d), Aligned(Negate(tq), d), Aligned(b, d), Aligned(a, d));
            return Read(d);
        }

        private Value MatrixArith(string op, Value lv, Value rv, Node at)
        {
            var lm = lv as MatValue; var rm = rv as MatValue;
            var mt = (NumericType)(lm ?? rm)!.Type;
            var s = Promote((lv.Type as NumericType)!.Scalar, (rv.Type as NumericType)!.Scalar);
            var rows = new Num[mt.Rows];
            for (int i = 0; i < mt.Rows; i++)
            {
                Value a = lm != null ? Wrap(Row(lm, i)) : lv;
                Value b = rm != null ? Wrap(Row(rm, i)) : rv;
                rows[i] = ToNum(Arith(op, a, b, at), at);
            }
            var rt = new NumericType(op is "==" or "!=" or "<" or ">" or "<=" or ">=" ? ScalarKind.Bool : s, mt.Rows, mt.Cols, matrix: true);
            return new MatValue { Type = rt, Lines = rows, Columns = false };
        }

        private Value Ternary(ConditionalExpr c)
        {
            var cond = ToNum(EvalValue(c.Cond), c.Cond);
            var a = EvalValue(c.Then); var b = EvalValue(c.Else);
            if (a is not NumValue || b is not NumValue)
            {
                // a struct or matrix: the condition is a scalar, chosen with if
                if (!cond.Type.IsScalar) throw Err(c, "a vector condition on non-numeric operands");
                var store = AllocLocal(a.Type);
                _w.Emit(Opcode.If, 1u << 7, Convert(Wrap(cond), NumericType.Bool, c).Operand());
                Assign(store, a, c);
                _w.Emit(Opcode.Else, 0);
                Assign(store, Convert(b, a.Type, c), c);
                _w.Emit(Opcode.EndIf, 0);
                return ReadStore(store);
            }
            var an = ((NumValue)a).N; var bn = ((NumValue)b).N;
            var s = Promote(an.Type.Scalar, bn.Type.Scalar);
            if (an.Type.Scalar == ScalarKind.Bool && bn.Type.Scalar == ScalarKind.Bool) s = ScalarKind.Bool;
            var t = Common(Common(an.Type, bn.Type, s), cond.Type.IsScalar ? an.Type.IsScalar ? bn.Type : an.Type : cond.Type, s);
            var bc = Convert(Wrap(cond), t.WithScalar(ScalarKind.Bool), c);
            return Wrap(Compute(Opcode.Movc, t, bc, Convert(a, t, c), Convert(b, t, c)));
        }

        // ---- conversions --------------------------------------------------------------------------------

        private Num Convert(Value v, NumericType to, Node? at, bool explicitCast = false) => ToNum(Convert(v, (HlslType)to, at, explicitCast), at ?? new ExprStmt());

        private Value Convert(Value v, HlslType to, Node? at, bool explicitCast = false)
        {
            at ??= new ExprStmt();
            if (to is NumericType nt)
            {
                if (nt.IsMatrix)
                {
                    if (v is MatValue mv)
                    {
                        var mt = (NumericType)mv.Type;
                        if (mt.Rows < nt.Rows || mt.Cols < nt.Cols) throw Err(at, $"cannot convert from '{mt}' to '{nt}'", 3017);
                        var rows = Enumerable.Range(0, nt.Rows).Select(i => ConvertNum(Row(mv, i).Swizzle(Enumerable.Range(0, nt.Cols).ToArray(), NumericType.Vector(mt.Scalar, nt.Cols)), nt.Row, at)).ToArray();
                        return new MatValue { Type = nt, Lines = rows, Columns = false };
                    }
                    var n = ToNum(v, at);
                    if (n.Type.IsScalar)
                        return new MatValue { Type = nt, Lines = Enumerable.Range(0, nt.Rows).Select(_ => ConvertNum(n, nt.Row, at)).ToArray(), Columns = false };
                    if (n.Count == nt.Count)
                        return new MatValue { Type = nt, Lines = Enumerable.Range(0, nt.Rows).Select(i => ConvertNum(n.Swizzle(Enumerable.Range(i * nt.Cols, nt.Cols).ToArray(), NumericType.Vector(n.Type.Scalar, nt.Cols)), nt.Row, at)).ToArray(), Columns = false };
                    throw Err(at, $"cannot convert from '{n.Type}' to '{nt}'", 3017);
                }
                if (v is MatValue m2)
                {
                    // a matrix to a vector: its cells, row by row
                    var mt = (NumericType)m2.Type;
                    if (mt.Count < nt.Count && !nt.IsScalar) throw Err(at, $"cannot convert from '{mt}' to '{nt}'", 3017);
                    var d = TempStore(nt);
                    for (int i = 0; i < Math.Max(1, nt.Count); i++) Op(Opcode.Mov, Sub(d, i), false, ConvertNum(Element(m2, i / mt.Cols, i % mt.Cols), nt.ScalarOf, at));
                    return Wrap(Read(d));
                }
                if (v is AggValue av && av.Type is StructType or ArrayType)
                {
                    // a struct or array of one number to a number
                    var flat = FlatNums(av).ToList();
                    if (flat.Count == 0) throw Err(at, $"cannot convert from '{av.Type}' to '{nt}'", 3017);
                    if (flat.Count == 1) return Wrap(ConvertNum(flat[0], nt, at));
                    var d = TempStore(nt);
                    int k = 0;
                    foreach (var f in flat) for (int c = 0; c < f.Count && k < nt.Count; c++) Op(Opcode.Mov, Sub(d, k++), false, ConvertNum(f.Component(c), nt.ScalarOf, at));
                    return Wrap(Read(d));
                }
                return Wrap(ConvertNum(ToNum(v, at), nt, at));
            }
            if (to is StructType st)
            {
                if (v.Type is StructType vs && vs.Name == st.Name) return v;
                if (v is NumValue nv && nv.N.Type.IsScalar)
                    return new AggValue { Type = st, Parts = st.Fields.Select(f => Convert(v, f.Type, at)).ToArray() };
                if (v is AggValue av)
                {
                    // field by field, by position (a cast between structs of the same layout)
                    return new AggValue { Type = st, Parts = st.Fields.Select((f, i) => Convert(av.Parts[Math.Min(i, av.Parts.Length - 1)], f.Type, at)).ToArray() };
                }
                throw Err(at, $"cannot convert from '{v.Type}' to '{st}'", 3017);
            }
            if (to is ArrayType at2)
            {
                if (v is ArrayRef) return v;
                if (v is AggValue av) return new AggValue { Type = at2, Parts = av.Parts.Select(p => Convert(p, at2.Element, at)).ToArray() };
                if (v is NumValue) return new AggValue { Type = at2, Parts = Enumerable.Range(0, at2.Length).Select(_ => Convert(v, at2.Element, at)).ToArray() };
            }
            if (to is ObjectType) return v;
            throw Err(at, $"cannot convert from '{v.Type}' to '{to}'", 3017);
        }

        private IEnumerable<Num> FlatNums(Value v)
        {
            switch (v)
            {
                case NumValue n: yield return n.N; break;
                case MatValue m:
                    for (int i = 0; i < ((NumericType)m.Type).Rows; i++) yield return Row(m, i);
                    break;
                case AggValue a: foreach (var p in a.Parts) foreach (var x in FlatNums(p)) yield return x; break;
            }
        }

        /// <summary>A number to another shape and scalar kind.</summary>
        private Num ConvertNum(Num n, NumericType to, Node at)
        {
            // the shape: a scalar spreads, a vector is cut
            if (n.Count != to.Count && !to.IsMatrix)
            {
                if (n.Type.IsScalar) n = n.Swizzle(Enumerable.Repeat(0, Math.Max(1, to.Count)).ToArray(), n.Type.IsScalar && to.IsScalar ? n.Type : NumericType.Vector(n.Type.Scalar, to.Count));
                else if (n.Count > to.Count) n = n.Swizzle(Enumerable.Range(0, to.Count).ToArray(), to.Count == 1 ? n.Type.ScalarOf : NumericType.Vector(n.Type.Scalar, to.Count));
                else throw Err(at, $"cannot implicitly convert from '{n.Type}' to '{to}'", 3017);
            }
            var shaped = to.Count == 1 ? n.Type.ScalarOf : NumericType.Vector(n.Type.Scalar, to.Count);
            n = n.WithType(shaped);
            var from = n.Type.Scalar;
            if (from == to.Scalar || (from is ScalarKind.Float or ScalarKind.Double && to.Scalar is ScalarKind.Float or ScalarKind.Double)) return n.WithType(to.IsMatrix ? to.Row : to);
            var t = to.IsMatrix ? to.Row : to;
            if (n.Imm != null) return new Num { Type = t, Imm = n.Imm.Select(x => ConvertBits(x, from, to.Scalar)).ToArray() };
            return (from, to.Scalar) switch
            {
                (ScalarKind.Int, ScalarKind.Float) or (ScalarKind.Int, ScalarKind.Double) => Compute(Opcode.Itof, t, n),
                (ScalarKind.Uint, ScalarKind.Float) or (ScalarKind.Uint, ScalarKind.Double) => Compute(Opcode.Utof, t, n),
                (ScalarKind.Float, ScalarKind.Int) or (ScalarKind.Double, ScalarKind.Int) => Compute(Opcode.Ftoi, t, n),
                (ScalarKind.Float, ScalarKind.Uint) or (ScalarKind.Double, ScalarKind.Uint) => Compute(Opcode.Ftou, t, n),
                (ScalarKind.Bool, ScalarKind.Float) or (ScalarKind.Bool, ScalarKind.Double) => Compute(Opcode.And, t, n, ImmFloat(1f)),
                (ScalarKind.Bool, _) => Compute(Opcode.And, t, n, ImmUint(1)),
                (ScalarKind.Float, ScalarKind.Bool) or (ScalarKind.Double, ScalarKind.Bool) => Compute(Opcode.Ne, t, n, ImmFloat(0)),
                (_, ScalarKind.Bool) => Compute(Opcode.INe, t, n, ImmUint(0)),
                _ => n.WithType(t),                     // int <-> uint: the same bits
            };
        }

        private static uint ConvertBits(uint x, ScalarKind from, ScalarKind to)
        {
            double v = from switch
            {
                ScalarKind.Float or ScalarKind.Double => BitConverter.UInt32BitsToSingle(x),
                ScalarKind.Uint => x,
                ScalarKind.Bool => x != 0 ? 1 : 0,
                _ => (int)x,
            };
            return to switch
            {
                ScalarKind.Float or ScalarKind.Double => BitConverter.SingleToUInt32Bits((float)v),
                ScalarKind.Bool => v != 0 ? 0xFFFFFFFFu : 0,
                ScalarKind.Uint => v < 0 ? (uint)(int)v : (uint)Math.Min(v, uint.MaxValue),
                _ => (uint)(int)Math.Clamp(v, int.MinValue, int.MaxValue),
            };
        }

        // ---- constructors and initializer lists --------------------------------------------------------

        private Value Construct(CallExpr c)
        {
            var t = Resolve(c.Constructor!);
            if (t is not NumericType nt) throw Err(c, $"'{t}' cannot be constructed");
            var parts = new List<Num>();
            foreach (var a in c.Args)
            {
                var v = EvalValue(a);
                parts.AddRange(FlatNums(v).SelectMany(n => Enumerable.Range(0, Math.Max(1, n.Count)).Select(i => n.Component(i))));
            }
            return Gather(parts, nt, c);
        }

        /// <summary>A number of a type from scalars, in order (a constructor, an initializer list).</summary>
        private Value Gather(List<Num> scalars, NumericType t, Node at)
        {
            if (scalars.Count == 1 && t.Count > 1 && !t.IsMatrix) return Wrap(ConvertNum(scalars[0], t, at));
            if (scalars.Count != t.Count) throw Err(at, $"incorrect number of arguments to numeric-type constructor", 3014);
            if (t.IsMatrix)
            {
                var rows = new Num[t.Rows];
                for (int i = 0; i < t.Rows; i++) rows[i] = ToNum(Gather(scalars.GetRange(i * t.Cols, t.Cols), t.Row, at), at);
                return new MatValue { Type = t, Lines = rows, Columns = false };
            }
            if (scalars.All(s => s.Imm != null))
                return Wrap(new Num { Type = t, Imm = scalars.Select(s => ConvertNum(s, t.ScalarOf, at).Imm![0]).ToArray() });
            var d = TempStore(t);
            for (int i = 0; i < t.Count; i++) Op(Opcode.Mov, Sub(d, i), false, ConvertNum(scalars[i], t.ScalarOf, at));
            return Wrap(Read(d));
        }

        private Value FromInitList(InitListExpr l, HlslType t)
        {
            var scalars = new List<Num>();
            foreach (var x in Flatten(l)) scalars.AddRange(FlatNums(EvalValue(x)).SelectMany(n => Enumerable.Range(0, Math.Max(1, n.Count)).Select(i => n.Component(i))));
            int k = 0;
            Value Build(HlslType ty)
            {
                switch (ty)
                {
                    case NumericType n:
                    {
                        var take = scalars.Skip(k).Take(Math.Max(1, n.Count)).ToList();
                        k += take.Count;
                        return Gather(take, n, l);
                    }
                    case ArrayType a: return new AggValue { Type = a, Parts = Enumerable.Range(0, a.Length).Select(_ => Build(a.Element)).ToArray() };
                    case StructType s: return new AggValue { Type = s, Parts = s.Fields.Select(f => Build(f.Type)).ToArray() };
                }
                throw Err(l, $"an initializer list for a {ty}");
            }
            return Build(t);
        }

        // ---- calls -------------------------------------------------------------------------------------

        private Value Call(CallExpr c)
        {
            if (_functions.TryGetValue(c.Name, out var overloads)) return CallFunction(c, overloads);
            return Intrinsic(c.Name, c.Args, c);
        }

        private Value CallFunction(CallExpr c, List<FunctionDecl> overloads)
        {
            // the overload whose parameter count fits, preferring exact numeric shapes
            var args = c.Args.Select(a => (Expr: a, Value: (Value?)null)).ToList();
            var candidates = overloads.Where(f => f.Body != null && f.Params.Count >= c.Args.Count && f.Params.Skip(c.Args.Count).All(p => p.Default != null)).ToList();
            if (candidates.Count == 0) throw Err(c, $"'{c.Name}': no matching {c.Args.Count} parameter function", 3013);
            var values = new List<Value?>();
            for (int i = 0; i < c.Args.Count; i++)
                values.Add(candidates.Any(f => f.Params[i].IsOut && !f.Params[i].IsIn) ? null : EvalValue(c.Args[i]));
            FunctionDecl f = candidates.Count == 1 ? candidates[0] : candidates.OrderBy(fd => Mismatch(fd, values)).First();
            if (_frames.Any(fr => fr.Function == f)) throw Err(c, $"recursive call to '{f.Name}'", 3500);

            var frame = new Frame { Function = f };
            var copyBack = new List<(Store From, Store To)>();
            for (int i = 0; i < f.Params.Count; i++)
            {
                var p = f.Params[i];
                var pt = Resolve(p.Type, p.ArrayDims);
                if (Base(pt) is ObjectType)
                {
                    frame.Locals[p.Name] = i < values.Count && values[i] != null ? values[i]! : EvalValue(c.Args[i]);
                    continue;
                }
                var store = AllocLocal(pt);
                if (p.IsIn)
                {
                    Value v = i < c.Args.Count ? values[i] ?? EvalValue(c.Args[i]) : EvalValue(p.Default!);
                    Assign(store, Convert(v, pt, p), p);
                }
                if (p.IsOut) copyBack.Add((store, StoreOf(c.Args[i])));
                frame.Locals[p.Name] = store;
            }
            var rt = Resolve(f.ReturnType);
            if (rt is not VoidType) frame.Result = AllocLocal(rt);
            // a return before the end: the body in a loop it breaks out of
            bool early = HasEarlyReturn(f.Body!);
            if (early)
            {
                frame.Returned = TempStore(NumericType.Bool);
                Op(Opcode.Mov, frame.Returned, false, ImmUint(0));
                _w.Emit(Opcode.Loop, 0);
                _loops.Add((f.Body!, null, null, false));
            }
            var savedScopes = _scopes.GetRange(1, _scopes.Count - 1);
            _scopes.RemoveRange(1, _scopes.Count - 1);
            _frames.Add(frame);
            Block(f.Body!);
            _frames.RemoveAt(_frames.Count - 1);
            _scopes.AddRange(savedScopes);
            if (early)
            {
                _w.Emit(Opcode.Break, 0);
                _w.Emit(Opcode.EndLoop, 0);
                _loops.RemoveAt(_loops.Count - 1);
            }
            foreach (var (from, to) in copyBack) Assign(to, Convert(ReadStore(from), to.Type, c), c);
            return frame.Result != null ? ReadStore(frame.Result) : new NumValue { Type = VoidType.Instance, N = ImmUint(0) };
        }

        private int Mismatch(FunctionDecl f, List<Value?> values)
        {
            int score = 0;
            for (int i = 0; i < values.Count; i++)
            {
                var pt = Resolve(f.Params[i].Type, f.Params[i].ArrayDims);
                var v = values[i];
                if (v == null) continue;
                if (pt is NumericType pn && v.Type is NumericType vn)
                {
                    if (pn.Count != vn.Count) score += 10;
                    if (pn.Scalar != vn.Scalar) score += 1;
                }
                else if (pt.ToString() != v.Type.ToString()) score += 100;
            }
            return score;
        }

        private static bool HasEarlyReturn(BlockStmt body)
        {
            bool found = false;
            void S(Stmt s, bool last)
            {
                switch (s)
                {
                    case JumpStmt { Kind: "return" } when !last: found = true; return;
                    case BlockStmt b: for (int i = 0; i < b.Body.Count; i++) S(b.Body[i], last && i == b.Body.Count - 1); return;
                    case IfStmt i: S(i.Then, false); if (i.Else != null) S(i.Else, false); return;
                    case ForStmt f: S(f.Body, false); return;
                    case WhileStmt w: S(w.Body, false); return;
                    case SwitchStmt sw: foreach (var c in sw.Cases) foreach (var x in c.Body) S(x, false); return;
                }
            }
            for (int i = 0; i < body.Body.Count; i++) S(body.Body[i], i == body.Body.Count - 1);
            return found;
        }
    }
}
