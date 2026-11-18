// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The shim's HLSL compiler: shader model 4 and 5 code. The tree is walked once, from the entry point:
// each expression typed as it is generated, each called function inlined (as fxc inlines every one),
// values living in registers -- a temp (r#), an input (v#), a constant buffer's (cb#[n]), an
// immediate, an indexable temp (x#) for a local array -- and each operation one or a few DXBC
// instructions writing a fresh temp. The declarations come last, when what the program uses is known,
// and go in front of it.
//
// Layouts are fxc's: a constant buffer's members packed into 16-byte registers (a member never
// straddles one; an array's elements, a struct and a matrix's rows or columns each start a register),
// matrices column-major unless row_major says otherwise, $Globals for uniforms outside a cbuffer, and
// registers bound in declaration order to what the program uses.
//

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Wpf.Interop.WebGpu.Direct3D.Dxbc;

namespace Microsoft.Wpf.Interop.WebGpu.Direct3D.Hlsl
{
    internal sealed partial class DxbcCodegen
    {
        // ---- values and storage -----------------------------------------------------------------------

        /// <summary>A numeric value (scalar or vector) in a register: its file and index, which register
        /// components hold its logical components, a source modifier; or an immediate.</summary>
        internal sealed class Num
        {
            public NumericType Type = NumericType.Float;
            public OperandType File;
            public long[] Index = Array.Empty<long>();
            public DxOperand?[] Relative = Array.Empty<DxOperand?>();
            public byte[] Comp = { 0 };
            public uint[]? Imm;
            public OperandModifier Mod;
            public int Count => Type.Count;

            public Num WithType(NumericType t) { var n = (Num)MemberwiseClone(); n.Type = t; return n; }

            public DxOperand Operand()
            {
                if (Type.Scalar == ScalarKind.Double) return DoubleOperand(this);
                if (Imm != null)
                {
                    var v = new uint[4];
                    for (int i = 0; i < 4; i++) v[i] = Imm[Math.Min(i, Imm.Length - 1)];
                    return Imm.Length == 1 || Imm.All(x => x == Imm[0]) ? DxOperand.Imm(Imm[0]) : DxOperand.Imm(v);
                }
                var sw = new byte[4];
                for (int i = 0; i < 4; i++) sw[i] = Comp[Math.Min(i, Comp.Length - 1)];
                var o = DxOperand.Src(File, sw, Index);
                o.Relative = (DxOperand?[])Relative.Clone();
                o.Modifier = Mod;
                return o;
            }

            /// <summary>Component i alone (a scalar).</summary>
            public Num Component(int i)
            {
                var n = (Num)MemberwiseClone();
                n.Type = Type.ScalarOf;
                if (Type.Scalar == ScalarKind.Double)
                {
                    // a double: its pair
                    int k = 2 * Math.Min(i, (Imm?.Length ?? Comp.Length) / 2 - 1);
                    if (Imm != null) n.Imm = new[] { Imm[k], Imm[k + 1] };
                    else n.Comp = new[] { Comp[k], Comp[k + 1] };
                    return n;
                }
                if (Imm != null) n.Imm = new[] { Imm[Math.Min(i, Imm.Length - 1)] };
                else n.Comp = new[] { Comp[Math.Min(i, Comp.Length - 1)] };
                return n;
            }

            /// <summary>Components picked by a swizzle of the logical components.</summary>
            public Num Swizzle(int[] pick, NumericType t)
            {
                var n = (Num)MemberwiseClone();
                n.Type = t;
                if (Type.Scalar == ScalarKind.Double)
                {
                    int last = (Imm?.Length ?? Comp.Length) / 2 - 1;
                    var words = pick.SelectMany(p => new[] { 2 * Math.Min(p, last), 2 * Math.Min(p, last) + 1 }).ToArray();
                    if (Imm != null) n.Imm = words.Select(w => Imm[w]).ToArray();
                    else n.Comp = words.Select(w => Comp[w]).ToArray();
                    return n;
                }
                if (Imm != null) n.Imm = pick.Select(p => Imm[Math.Min(p, Imm.Length - 1)]).ToArray();
                else n.Comp = pick.Select(p => Comp[Math.Min(p, Comp.Length - 1)]).ToArray();
                return n;
            }
        }

        internal abstract class Value { public HlslType Type = null!; }
        internal sealed class NumValue : Value { public Num N = null!; }
        /// <summary>A matrix: its lines, rows or columns, each a vector Num.</summary>
        internal sealed class MatValue : Value { public Num[] Lines = Array.Empty<Num>(); public bool Columns; }
        /// <summary>A struct's fields or an array's elements.</summary>
        internal sealed class AggValue : Value { public Value[] Parts = Array.Empty<Value>(); }
        /// <summary>A texture, buffer, sampler or stream: its register, and for a D3D9-style sampler the texture too.</summary>
        internal sealed class ObjValue : Value { public int Register; public int Sampler = -1; public ObjectType Obj => (ObjectType)Type; }
        /// <summary>An array in an indexable temp or a constant buffer, indexed when it is read.</summary>
        internal sealed class ArrayRef : Value
        {
            public OperandType File;
            public long Base;
            public long FileIndex;              // x# or cb#
            public int Stride;                  // registers per element
            public bool ColumnMajor;
            /// <summary>A constant buffer's array: the buffer (a dynamic index marks it dynamically indexed).</summary>
            public object? Tag;
        }

        /// <summary>Where a value can be written: a register's components, or the parts of an aggregate.</summary>
        internal abstract class Store { public HlslType Type = null!; }
        internal sealed class NumStore : Store
        {
            public OperandType File;
            public long[] Index = Array.Empty<long>();
            public DxOperand?[] Relative = Array.Empty<DxOperand?>();
            public byte[] Comp = { 0 };
        }
        internal sealed class AggStore : Store { public Store[] Parts = Array.Empty<Store>(); }

        // ---- the compilation --------------------------------------------------------------------------

        private readonly TranslationUnit _unit;
        private readonly ShaderStage _stage;
        private readonly int _major, _minor;
        private readonly uint _flags;
        private readonly DxbcWriter _w = new();
        private readonly List<string> _warnings = new();

        private readonly Dictionary<string, StructType> _structs = new();
        private readonly Dictionary<string, (TypeSyntax Type, List<Expr?> Dims)> _typedefs = new();
        private readonly Dictionary<string, List<FunctionDecl>> _functions = new();

        public DxbcCodegen(TranslationUnit unit, ShaderStage stage, int major, int minor, uint flags)
        {
            _unit = unit; _stage = stage; _major = major; _minor = minor; _flags = flags;
            _w.Stage = stage; _w.Major = major; _w.Minor = minor;
        }

        private bool Sm5 => _major >= 5;
        private bool RowMajorDefault => (_flags & HlslCompiler.PackMatrixRowMajor) != 0;

        private static HlslException Err(Node n, string message, int code = 3000) => new(n.At, message, code);

        // ---- types ------------------------------------------------------------------------------------

        public HlslType Resolve(TypeSyntax t, List<Expr?>? dims = null)
        {
            HlslType r = ResolveBase(t);
            if (dims != null)
                for (int i = dims.Count - 1; i >= 0; i--) r = new ArrayType(r, dims[i] == null ? 0 : (int)ConstInt(dims[i]!));
            return r;
        }

        private HlslType ResolveBase(TypeSyntax t)
        {
            string n = t.Name;
            if (_typedefs.TryGetValue(n, out var td)) return Resolve(td.Type, td.Dims);
            if (_structs.TryGetValue(n, out var st)) return st;
            if (n == "void") return VoidType.Instance;
            bool rowMajor = t.Modifiers.Contains("row_major") || (!t.Modifiers.Contains("column_major") && RowMajorDefault);
            if (n == "vector")
            {
                var e = t.TypeArgs?.FirstOrDefault() is { } a ? (NumericType)Resolve(a) : NumericType.Float;
                return NumericType.Vector(e.Scalar, t.IntArgs?.FirstOrDefault() ?? 4);
            }
            if (n == "matrix")
            {
                var e = t.TypeArgs?.FirstOrDefault() is { } a ? (NumericType)Resolve(a) : NumericType.Float;
                return new NumericType(e.Scalar, t.IntArgs?.ElementAtOrDefault(0) ?? 4, t.IntArgs?.ElementAtOrDefault(1) ?? 4, matrix: true, rowMajor);
            }
            if (HlslParser.ObjectTypes.Contains(n))
            {
                HlslType? elem = t.TypeArgs?.FirstOrDefault() is { } a ? Resolve(a) : null;
                int count = t.IntArgs?.FirstOrDefault() ?? 0;
                if (elem == null && (n.StartsWith("Texture", StringComparison.Ordinal) || n.StartsWith("RWTexture", StringComparison.Ordinal) || n is "Buffer" or "RWBuffer" || n.StartsWith("texture", StringComparison.Ordinal)))
                    elem = NumericType.Vector(ScalarKind.Float, 4);
                return new ObjectType(n, elem, count);
            }
            // a scalar, floatN or floatNxM
            foreach (var (name, kind) in ScalarNames)
            {
                if (!n.StartsWith(name, StringComparison.Ordinal)) continue;
                string rest = n.Substring(name.Length);
                if (rest.Length == 0) return NumericType.Of(kind);
                if (rest.Length == 1 && rest[0] is >= '1' and <= '4') return NumericType.Vector(kind, rest[0] - '0');
                if (rest.Length == 3 && rest[1] == 'x') return new NumericType(kind, rest[0] - '0', rest[2] - '0', matrix: true, rowMajor);
            }
            throw new HlslException(t.At, $"unrecognized identifier '{n}'");
        }

        private static readonly (string, ScalarKind)[] ScalarNames =
        {
            ("min16float", ScalarKind.Float), ("min10float", ScalarKind.Float), ("min16int", ScalarKind.Int), ("min12int", ScalarKind.Int),
            ("min16uint", ScalarKind.Uint), ("float16_t", ScalarKind.Float), ("float32_t", ScalarKind.Float), ("float64_t", ScalarKind.Double),
            ("uint16_t", ScalarKind.Uint), ("uint32_t", ScalarKind.Uint), ("uint64_t", ScalarKind.Uint), ("int16_t", ScalarKind.Int),
            ("int32_t", ScalarKind.Int), ("int64_t", ScalarKind.Int),
            ("double", ScalarKind.Double), ("float", ScalarKind.Float), ("half", ScalarKind.Float), ("dword", ScalarKind.Uint),
            ("uint", ScalarKind.Uint), ("int", ScalarKind.Int), ("bool", ScalarKind.Bool),
        };

        /// <summary>A constant integer expression (an array size, a loop attribute, a case label).</summary>
        public long ConstInt(Expr e)
        {
            switch (e)
            {
                case LiteralExpr l: return l.IsFloat ? (long)l.FloatValue : (long)l.IntValue;
                case UnaryExpr u when u.Op == "-": return -ConstInt(u.Operand);
                case UnaryExpr u when u.Op == "+": return ConstInt(u.Operand);
                case UnaryExpr u when u.Op == "~": return ~ConstInt(u.Operand);
                case BinaryExpr b:
                {
                    long x = ConstInt(b.Left), y = ConstInt(b.Right);
                    return b.Op switch
                    {
                        "+" => x + y, "-" => x - y, "*" => x * y, "/" => y == 0 ? 0 : x / y, "%" => y == 0 ? 0 : x % y,
                        "<<" => x << (int)y, ">>" => x >> (int)y, "&" => x & y, "|" => x | y, "^" => x ^ y,
                        "==" => x == y ? 1 : 0, "!=" => x != y ? 1 : 0, "<" => x < y ? 1 : 0, ">" => x > y ? 1 : 0, "<=" => x <= y ? 1 : 0, ">=" => x >= y ? 1 : 0,
                        "&&" => x != 0 && y != 0 ? 1 : 0, "||" => x != 0 || y != 0 ? 1 : 0,
                        _ => throw Err(e, "an integer constant was expected"),
                    };
                }
                case CastExpr c: return ConstInt(c.Value);
                case NameExpr n when Lookup(n.Name) is NumValue lv && lv.N.Imm != null:
                    return lv.N.Type.IsFloatLike ? (long)BitConverter.UInt32BitsToSingle(lv.N.Imm[0]) : lv.N.Type.Scalar == ScalarKind.Uint ? lv.N.Imm[0] : (int)lv.N.Imm[0];
                case NameExpr n when _constants.TryGetValue(n.Name, out var v) && v is NumValue nv && nv.N.Imm != null:
                    return nv.N.Type.IsFloatLike ? (long)BitConverter.UInt32BitsToSingle(nv.N.Imm[0]) : (int)nv.N.Imm[0];
                case ConditionalExpr c: return ConstInt(c.Cond) != 0 ? ConstInt(c.Then) : ConstInt(c.Else);
            }
            throw Err(e, "an integer constant was expected", 3058);
        }

        // ---- registers ----------------------------------------------------------------------------------

        private int _temps;
        private readonly List<(int Index, int Size, int Comps)> _indexables = new();

        private int NewTemp() => _temps++;

        /// <summary>A fresh temp for a value of n components (.x, .xy, .xyz, .xyzw).</summary>
        private NumStore TempStore(NumericType t)
        {
            int n = Math.Max(1, Math.Min(4, t.Count * (t.Scalar == ScalarKind.Double ? 2 : 1)));
            if (n > 4 || (t.Scalar == ScalarKind.Double && t.Count > 2)) throw new HlslException(default, $"a {t} takes more than a register");
            return new NumStore { Type = t, File = OperandType.Temp, Index = new long[] { NewTemp() }, Relative = new DxOperand?[1], Comp = Enumerable.Range(0, n).Select(i => (byte)i).ToArray() };
        }

        private static Num Read(NumStore s) => new()
        {
            Type = (NumericType)s.Type, File = s.File, Index = s.Index, Relative = s.Relative, Comp = s.Comp,
        };

        private static DxOperand DestOf(NumStore s)
        {
            int mask = 0;
            foreach (byte c in s.Comp) mask |= 1 << c;
            var o = DxOperand.Dest(s.File, mask, s.Index);
            // oDepth and oMask are one component, with no mask
            if (s.File is OperandType.OutputDepth or OperandType.OutputDepthGreaterEqual or OperandType.OutputDepthLessEqual or OperandType.OutputCoverageMask) o.Components = 1;
            o.Relative = (DxOperand?[])s.Relative.Clone();
            return o;
        }

        /// <summary>A source whose logical components line up with a destination's components.</summary>
        private static DxOperand Aligned(Num v, NumStore dest)
        {
            if (v.Imm != null)
            {
                var vals = new uint[4];
                for (int i = 0; i < dest.Comp.Length; i++) vals[dest.Comp[i]] = v.Imm[Math.Min(i, v.Imm.Length - 1)];
                return DxOperand.Imm(vals);
            }
            var sw = new byte[4];
            for (int i = 0; i < 4; i++) sw[i] = v.Comp[0];
            for (int i = 0; i < dest.Comp.Length; i++) sw[dest.Comp[i]] = v.Comp[Math.Min(i, v.Comp.Length - 1)];
            var o = DxOperand.Src(v.File, sw, v.Index);
            o.Relative = (DxOperand?[])v.Relative.Clone();
            o.Modifier = v.Mod;
            return o;
        }

        /// <summary>op dest, sources..., each source aligned to the destination's components.</summary>
        private void Op(Opcode op, NumStore dest, bool saturate, params Num[] srcs)
        {
            if (DoubleInstruction(op, dest, saturate, srcs)) return;
            var ops = new DxOperand[srcs.Length + 1];
            ops[0] = DestOf(dest);
            // a dot product reads its sources' own components, whatever it writes
            bool reduction = op is Opcode.Dp2 or Opcode.Dp3 or Opcode.Dp4;
            for (int i = 0; i < srcs.Length; i++) ops[i + 1] = reduction ? srcs[i].Operand() : Aligned(srcs[i], dest);
            _w.Emit(op, saturate ? 4u : 0u, ops);
        }

        /// <summary>The value of op over sources, in a fresh temp of type t.</summary>
        private Num Compute(Opcode op, NumericType t, params Num[] srcs)
        {
            var d = TempStore(t);
            Op(op, d, false, srcs);
            return Read(d);
        }

        private Num Move(Num v, NumStore dest, bool saturate = false)
        {
            Op(Opcode.Mov, dest, saturate, v);
            return Read(dest);
        }

        // ---- immediates ---------------------------------------------------------------------------------

        private static Num ImmFloat(params float[] v) => new() { Type = v.Length == 1 ? NumericType.Float : NumericType.Vector(ScalarKind.Float, v.Length), Imm = v.Select(BitConverter.SingleToUInt32Bits).ToArray() };
        private static Num ImmInt(params int[] v) => new() { Type = v.Length == 1 ? NumericType.Int : NumericType.Vector(ScalarKind.Int, v.Length), Imm = v.Select(x => (uint)x).ToArray() };
        private static Num ImmUint(params uint[] v) => new() { Type = v.Length == 1 ? NumericType.Uint : NumericType.Vector(ScalarKind.Uint, v.Length), Imm = v };
        private static Num ImmBool(bool b) => new() { Type = NumericType.Bool, Imm = new[] { b ? 0xFFFFFFFFu : 0u } };
    }
}
