// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The shim's HLSL compiler: memory -- what lives outside registers and is read and written by
// instructions of its own. A place in memory (MemLoc) is a UAV's or a buffer's element, or
// group-shared memory (g#), addressed as fxc addresses it:
//   - typed (RWTexture*, RWBuffer): coordinates, ld_uav_typed / store_uav_typed;
//   - structured (StructuredBuffer, RWStructuredBuffer, append / consume, and group-shared memory,
//     an array's elements its structure): an element index and a byte offset in it;
//   - raw (ByteAddressBuffer, RWByteAddressBuffer): a byte address.
// A place is read when its value is needed and written when it is assigned; a struct or an array in
// memory is its members' places. The atomics, the barriers, the counters and the compute shader's
// thread ids are here too.
//

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Wpf.Interop.WebGpu.Direct3D.Dxbc;

namespace Microsoft.Wpf.Interop.WebGpu.Direct3D.Hlsl
{
    internal sealed partial class DxbcCodegen
    {
        internal sealed class MemLoc
        {
            public OperandType File;            // UnorderedAccessView, Resource, ThreadGroupSharedMemory
            public int Register;
            public char Kind;                   // t typed, s structured, r raw
            public int Stride;                  // structured: the element's bytes
            /// <summary>Typed: the coordinates; structured: the element index (null until indexed).</summary>
            public Num? Index;
            public int Offset;                  // bytes: in the element (structured), from the start (raw)
            public Num? DynOffset;              // bytes, added to Offset
            public MemLoc With(Num? index = null, int offset = 0, Num? dyn = null) => new()
            {
                File = File, Register = Register, Kind = Kind, Stride = Stride, Index = index ?? Index, Offset = Offset + offset, DynOffset = dyn ?? DynOffset,
            };
        }

        /// <summary>A value in memory: read when used, written when assigned.</summary>
        internal sealed class MemValue : Value { public MemLoc Loc = null!; }
        internal sealed class MemStore : Store { public MemLoc Loc = null!; }

        // ---- group-shared memory ----------------------------------------------------------------------

        private sealed class GroupShared { public string Name = ""; public HlslType Type = null!; public int Register = -1; public int Stride, Count; }
        private readonly Dictionary<string, GroupShared> _groupShared = new();
        private int _tgsmCount;

        private Value GroupSharedValue(GroupShared g)
        {
            if (g.Register < 0)
            {
                g.Register = _tgsmCount++;
                var elem = g.Type is ArrayType a ? a.Element : g.Type;
                g.Stride = SizeOfStructured(elem);
                g.Count = g.Type is ArrayType a2 ? Math.Max(1, a2.Length) : 1;
            }
            var loc = new MemLoc { File = OperandType.ThreadGroupSharedMemory, Register = g.Register, Kind = 's', Stride = g.Stride };
            // a single value: element 0
            if (g.Type is not ArrayType) loc.Index = ImmInt(0);
            return new MemValue { Type = g.Type, Loc = loc };
        }

        // ---- places -----------------------------------------------------------------------------------

        /// <summary>A UAV's or a buffer's element: a place, for the objects that have them.</summary>
        private MemValue? ElementOf(ObjValue o, Value index, Node at)
        {
            var t = o.Obj;
            var file = t.IsUav ? OperandType.UnorderedAccessView : OperandType.Resource;
            if (t.Name is "StructuredBuffer" or "RWStructuredBuffer" or "AppendStructuredBuffer" or "ConsumeStructuredBuffer")
            {
                var elem = t.Element ?? NumericType.Uint;
                var idx = ConvertNum(ToNum(index, at), NumericType.Int, at);
                MarkUsed(o);
                return new MemValue { Type = elem, Loc = new MemLoc { File = file, Register = o.Register, Kind = 's', Stride = SizeOfStructured(elem), Index = idx.Component(0) } };
            }
            if (t.IsUav && (t.IsTexture || t.Name == "RWBuffer"))
            {
                var idx = ToNum(index, at);
                var coords = ConvertNum(idx, idx.Type.WithScalar(ScalarKind.Int), at);
                MarkUsed(o);
                return new MemValue { Type = t.Element ?? NumericType.Vector(ScalarKind.Float, 4), Loc = new MemLoc { File = file, Register = o.Register, Kind = 't', Index = coords } };
            }
            return null;
        }

        private void MarkUsed(ObjValue o)
        {
            var r = _resources.FirstOrDefault(x => x.Slot == o.Register && x.Type == o.Obj) ?? _resources.FirstOrDefault(x => x.Type == o.Obj);
            if (r != null) r.Used = true;
        }

        /// <summary>A member of a struct in memory, or the element of an array in memory.</summary>
        private Value MemMember(MemValue v, string name, Node at)
        {
            if (v.Type is StructType st)
            {
                int off = 0;
                foreach (var f in st.Fields)
                {
                    if (f.Name == name) return new MemValue { Type = f.Type, Loc = v.Loc.With(offset: off) };
                    off += SizeOfStructured(f.Type);
                }
                throw Err(at, $"'{name}' is not a member of {st.Name}", 3018);
            }
            // a vector in memory: its swizzle, read
            var n = ToNum(MemRead(v, at), at);
            var pick = SwizzleOf(name, n.Type.IsScalar ? 1 : n.Count) ?? throw Err(at, $"invalid subscript '{name}'", 3018);
            return Wrap(n.Swizzle(pick, pick.Length == 1 ? n.Type.ScalarOf : NumericType.Vector(n.Type.Scalar, pick.Length)));
        }

        private Value MemIndex(MemValue v, Expr index, Node at)
        {
            if (v.Type is not ArrayType a) throw Err(at, $"a {v.Type} cannot be indexed", 3121);
            int size = SizeOfStructured(a.Element);
            bool constant = TryConstIndex(index, out long k);
            // group-shared memory's top array: its elements are the structure's
            if (v.Loc.Kind == 's' && v.Loc.Index == null)
                return new MemValue { Type = a.Element, Loc = v.Loc.With(index: constant ? ImmInt((int)k) : Registerize(ConvertNum(ToNum(EvalValue(index), index), NumericType.Int, index).Component(0))) };
            if (constant) return new MemValue { Type = a.Element, Loc = v.Loc.With(offset: (int)k * size) };
            var i = ConvertNum(ToNum(EvalValue(index), index), NumericType.Int, index).Component(0);
            var bytes = Compute(Opcode.IMul, NumericType.Int, i, ImmInt(size));
            if (v.Loc.DynOffset != null) bytes = Compute(Opcode.IAdd, NumericType.Int, bytes, v.Loc.DynOffset);
            return new MemValue { Type = a.Element, Loc = v.Loc.With(dyn: bytes) };
        }

        /// <summary>The byte offset of a structured or raw place, as an operand.</summary>
        private DxOperand OffsetOperand(MemLoc l) =>
            l.DynOffset == null ? ImmInt(l.Offset).Operand() : (l.Offset == 0 ? l.DynOffset : Compute(Opcode.IAdd, NumericType.Int, l.DynOffset, ImmInt(l.Offset))).Component(0).Operand();

        private DxOperand MemOperand(MemLoc l, int words, bool dest)
        {
            if (dest) return DxOperand.Dest(l.File, (1 << words) - 1, l.Register);
            return DxOperand.Src(l.File, new byte[] { 0, 1, 2, 3 }, l.Register);
        }

        // ---- reads and writes ---------------------------------------------------------------------------

        private Value MemRead(MemValue v, Node at) => MemRead(v.Loc, v.Type, at);

        private Value MemRead(MemLoc l, HlslType t, Node at)
        {
            switch (t)
            {
                case StructType st:
                {
                    var parts = new List<Value>();
                    int off = 0;
                    foreach (var f in st.Fields) { parts.Add(MemRead(l.With(offset: off), f.Type, at)); off += SizeOfStructured(f.Type); }
                    return new AggValue { Type = st, Parts = parts.ToArray() };
                }
                case ArrayType a:
                {
                    int size = SizeOfStructured(a.Element);
                    return new AggValue { Type = a, Parts = Enumerable.Range(0, Math.Max(1, a.Length)).Select(i => MemRead(l.With(offset: i * size), a.Element, at)).ToArray() };
                }
                case NumericType n when n.IsMatrix:
                {
                    // tightly packed, row by row (as a structured buffer holds a row_major matrix)
                    var lines = new Num[n.Rows];
                    for (int r = 0; r < n.Rows; r++) lines[r] = ToNum(MemRead(l.With(offset: r * n.Cols * 4), NumericType.Vector(n.Scalar, n.Cols), at), at);
                    return new MatValue { Type = n, Lines = lines, Columns = false };
                }
                case NumericType n:
                {
                    int words = Math.Max(1, n.Components);
                    var raw = TempStore(NumericType.Vector(ScalarKind.Uint, words));
                    switch (l.Kind)
                    {
                        case 't':
                        {
                            var addr = Pad(l.Index!);
                            var d4 = TempStore(NumericType.Vector(n.Scalar == ScalarKind.Bool ? ScalarKind.Uint : n.Scalar, 4));
                            _w.Emit(Opcode.LdUavTyped, 0, DestOf(d4), addr.Operand(), MemOperand(l, 4, false));
                            var r4 = Read(d4);
                            return Wrap(r4.Swizzle(Enumerable.Range(0, Math.Max(1, n.Count)).ToArray(), n));
                        }
                        case 's':
                            _w.Emit(Opcode.LdStructured, 0, DestOf(raw), l.Index!.Component(0).Operand(), OffsetOperand(l), MemOperand(l, words, false));
                            break;
                        default:
                            _w.Emit(Opcode.LdRaw, 0, DestOf(raw), OffsetOperand(l), MemOperand(l, words, false));
                            break;
                    }
                    var v = Read(raw).WithType(n.Scalar == ScalarKind.Bool ? n.WithScalar(ScalarKind.Uint) : n);
                    // a bool in memory is 0 or 1
                    if (n.Scalar == ScalarKind.Bool) return Wrap(Compute(Opcode.INe, n, v, ImmUint(0)));
                    return Wrap(v);
                }
            }
            throw Err(at, $"a {t} cannot be read from memory");
        }

        private void MemWrite(MemLoc l, HlslType t, Value v, Node at)
        {
            if (l.File == OperandType.Resource) throw Err(at, "a read-only buffer cannot be assigned to", 3025);
            switch (t)
            {
                case StructType st:
                {
                    var src = v as AggValue ?? throw Err(at, $"cannot convert from '{v.Type}' to '{st}'", 3017);
                    int off = 0;
                    for (int i = 0; i < st.Fields.Count; i++) { MemWrite(l.With(offset: off), st.Fields[i].Type, src.Parts[i], at); off += SizeOfStructured(st.Fields[i].Type); }
                    return;
                }
                case ArrayType a:
                {
                    var src = v as AggValue ?? throw Err(at, $"cannot convert from '{v.Type}' to '{a}'", 3017);
                    int size = SizeOfStructured(a.Element);
                    for (int i = 0; i < src.Parts.Length; i++) MemWrite(l.With(offset: i * size), a.Element, src.Parts[i], at);
                    return;
                }
                case NumericType n when n.IsMatrix:
                {
                    var m = v as MatValue ?? throw Err(at, $"cannot convert from '{v.Type}' to '{n}'", 3017);
                    for (int r = 0; r < n.Rows; r++) MemWrite(l.With(offset: r * n.Cols * 4), NumericType.Vector(n.Scalar, n.Cols), Wrap(Row(m, r)), at);
                    return;
                }
                case NumericType n:
                {
                    var val = Convert(v, n, at);
                    // a bool in memory is 0 or 1
                    if (n.Scalar == ScalarKind.Bool) val = Compute(Opcode.And, n.WithScalar(ScalarKind.Uint), val.WithType(n.WithScalar(ScalarKind.Uint)), ImmUint(1));
                    int words = Math.Max(1, n.Components);
                    // a double's words, stored as they are
                    if (IsDouble(n)) val = val.WithType(NumericType.Vector(ScalarKind.Uint, words));
                    switch (l.Kind)
                    {
                        case 't':
                            _w.Emit(Opcode.StoreUavTyped, 0, DxOperand.Dest(l.File, 0xF, l.Register), Pad(l.Index!).Operand(), Pad(val).Operand());
                            return;
                        case 's':
                            _w.Emit(Opcode.StoreStructured, 0, MemOperand(l, words, true), l.Index!.Component(0).Operand(), OffsetOperand(l), val.Operand());
                            return;
                        default:
                            _w.Emit(Opcode.StoreRaw, 0, MemOperand(l, words, true), OffsetOperand(l), val.Operand());
                            return;
                    }
                }
            }
            throw Err(at, $"a {t} cannot be written to memory");
        }

        // ---- atomics ------------------------------------------------------------------------------------

        /// <summary>The address operand of an atomic: coordinates, (index, offset) or a byte address.</summary>
        private DxOperand AtomicAddress(MemLoc l)
        {
            switch (l.Kind)
            {
                case 't': return l.Index!.Operand();
                case 's':
                {
                    var a = TempStore(NumericType.Vector(ScalarKind.Int, 2));
                    Op(Opcode.Mov, Sub(a, 0), false, l.Index!.Component(0));
                    var off = l.DynOffset == null ? ImmInt(l.Offset) : l.Offset == 0 ? l.DynOffset : Compute(Opcode.IAdd, NumericType.Int, l.DynOffset, ImmInt(l.Offset));
                    Op(Opcode.Mov, Sub(a, 1), false, off.Component(0));
                    return Read(a).Operand();
                }
                default: return OffsetOperand(l);
            }
        }

        /// <summary>Interlocked*: an atomic on a place, its old value written to <paramref name="original"/> when given.</summary>
        private void Atomic(string name, MemLoc l, HlslType type, Num value, Num? compare, Expr? original, Node at)
        {
            if (l.File == OperandType.Resource) throw Err(at, $"{name} needs a writable resource", 3025);
            var t = type as NumericType ?? throw Err(at, $"{name} of a {type}");
            if (t.Count != 1 || t.Scalar is not (ScalarKind.Int or ScalarKind.Uint)) throw Err(at, $"{name} needs an int or uint", 3017);
            bool signed = t.Scalar == ScalarKind.Int || value.Type.Scalar == ScalarKind.Int;
            var v = ConvertNum(value, t, at).Component(0);
            var target = DxOperand.Dest(l.File, 1, l.Register);
            var addr = AtomicAddress(l);
            string op = name.Substring("Interlocked".Length);
            if (op == "CompareStore")
            {
                _w.Emit(Opcode.AtomicCmpStore, 0, target, addr, ConvertNum(compare!, t, at).Component(0).Operand(), v.Operand());
                return;
            }
            if (op == "CompareExchange")
            {
                var od = TempStore(t);
                _w.Emit(Opcode.ImmAtomicCmpExch, 0, DestOf(od), target, addr, ConvertNum(compare!, t, at).Component(0).Operand(), v.Operand());
                if (original != null) Assign(StoreOf(original), Wrap(Read(od)), at);
                return;
            }
            var (plain, imm) = op switch
            {
                "Add" => (Opcode.AtomicIAdd, Opcode.ImmAtomicIAdd),
                "And" => (Opcode.AtomicAnd, Opcode.ImmAtomicAnd),
                "Or" => (Opcode.AtomicOr, Opcode.ImmAtomicOr),
                "Xor" => (Opcode.AtomicXor, Opcode.ImmAtomicXor),
                "Min" => signed ? (Opcode.AtomicIMin, Opcode.ImmAtomicIMin) : (Opcode.AtomicUMin, Opcode.ImmAtomicUMin),
                "Max" => signed ? (Opcode.AtomicIMax, Opcode.ImmAtomicIMax) : (Opcode.AtomicUMax, Opcode.ImmAtomicUMax),
                "Exchange" => (Opcode.ImmAtomicExch, Opcode.ImmAtomicExch),
                _ => throw Err(at, $"'{name}': undeclared identifier", 3004),
            };
            if (original == null && op != "Exchange") { _w.Emit(plain, 0, target, addr, v.Operand()); return; }
            var d = TempStore(t);
            _w.Emit(imm, 0, DestOf(d), target, addr, v.Operand());
            if (original != null) Assign(StoreOf(original), Wrap(Read(d)), at);
        }

        /// <summary>A global Interlocked* (its first argument a place in memory), or null when it is no atomic.</summary>
        private Value? InterlockedIntrinsic(string name, List<Expr> args, Node at)
        {
            if (!name.StartsWith("Interlocked", StringComparison.Ordinal)) return null;
            if (!Sm5 && _stage != ShaderStage.Compute) throw Err(at, $"{name} needs shader model 5", 3004);
            var place = PlaceOf(args[0]) ?? throw Err(at, $"{name}: the destination must be a resource or group-shared memory", 3025);
            bool cmp = name is "InterlockedCompareExchange" or "InterlockedCompareStore";
            var value = Arg(args, cmp ? 2 : 1, at);
            var compare = cmp ? Arg(args, 1, at) : null;
            int origIndex = cmp ? 3 : 2;
            Atomic(name, place.Loc, place.Type, value, compare, args.Count > origIndex ? args[origIndex] : null, at);
            return Wrap(ImmUint(0));
        }

        /// <summary>The place an expression names, when it is in memory.</summary>
        private MemValue? PlaceOf(Expr e)
        {
            if (!InMemory(e)) return null;
            return EvalPlace(e) as MemValue;
        }

        /// <summary>Whether an expression's root is group-shared memory or a UAV or buffer with places.</summary>
        private bool InMemory(Expr e)
        {
            string? root = RootName(e);
            if (root == null || Lookup(root) != null) return false;
            if (_groupShared.ContainsKey(root)) return true;
            if (_resourceByName.TryGetValue(root, out var r))
                return e is IndexExpr or MemberExpr && (r.Type.IsUav || r.Type.Name == "StructuredBuffer");
            return false;
        }

        // ---- the objects' memory methods ---------------------------------------------------------------

        private Value? MemoryMethod(ObjValue o, MethodExpr m)
        {
            var t = o.Obj;
            var file = t.IsUav ? OperandType.UnorderedAccessView : OperandType.Resource;
            bool raw = t.Name is "ByteAddressBuffer" or "RWByteAddressBuffer";
            bool structured = t.Name is "StructuredBuffer" or "RWStructuredBuffer" or "AppendStructuredBuffer" or "ConsumeStructuredBuffer";
            if (!raw && !structured && !(t.IsUav && m.Name.StartsWith("Interlocked", StringComparison.Ordinal))) return null;
            MarkUsed(o);
            MemLoc Raw(Expr addr)
            {
                var a = ConvertNum(Arg(new List<Expr> { addr }, 0, m), NumericType.Int, addr).Component(0);
                return new MemLoc { File = file, Register = o.Register, Kind = 'r', DynOffset = a.Imm != null ? null : a, Offset = a.Imm != null ? (int)a.Imm[0] : 0 };
            }
            if (raw && m.Name is "Load" or "Load2" or "Load3" or "Load4")
            {
                int n = m.Name.Length == 4 ? 1 : m.Name[4] - '0';
                return MemRead(Raw(m.Args[0]), n == 1 ? NumericType.Uint : NumericType.Vector(ScalarKind.Uint, n), m);
            }
            if (raw && m.Name is "Store" or "Store2" or "Store3" or "Store4")
            {
                int n = m.Name.Length == 5 ? 1 : m.Name[5] - '0';
                MemWrite(Raw(m.Args[0]), n == 1 ? NumericType.Uint : NumericType.Vector(ScalarKind.Uint, n), EvalValue(m.Args[1]), m);
                return Wrap(ImmUint(0));
            }
            if (structured && m.Name == "Load") return MemRead(ElementOf(o, EvalValue(m.Args[0]), m)!, m);
            switch (m.Name)
            {
                case "GetDimensions":
                {
                    var d = TempStore(NumericType.Uint);
                    _w.Emit(Opcode.BufInfo, 0, DestOf(d), DxOperand.Src(file, new byte[] { 0, 0, 0, 0 }, o.Register));
                    Assign(StoreOf(m.Args[0]), Wrap(Read(d)), m);
                    if (structured && m.Args.Count > 1) Assign(StoreOf(m.Args[1]), Wrap(ImmUint((uint)SizeOfStructured(t.Element ?? NumericType.Uint))), m);
                    return Wrap(ImmUint(0));
                }
                case "IncrementCounter":
                case "DecrementCounter":
                {
                    SetCounter(o);
                    var d = TempStore(NumericType.Uint);
                    _w.Emit(m.Name == "IncrementCounter" ? Opcode.ImmAtomicAlloc : Opcode.ImmAtomicConsume, 0, DestOf(d), DxOperand.Bare(file, o.Register));
                    return Wrap(Read(d));
                }
                case "Append":
                {
                    SetCounter(o);
                    var d = TempStore(NumericType.Uint);
                    _w.Emit(Opcode.ImmAtomicAlloc, 0, DestOf(d), DxOperand.Bare(file, o.Register));
                    var elem = t.Element ?? NumericType.Uint;
                    var loc = new MemLoc { File = file, Register = o.Register, Kind = 's', Stride = SizeOfStructured(elem), Index = Read(d) };
                    MemWrite(loc, elem, Convert(EvalValue(m.Args[0]), elem, m), m);
                    return Wrap(ImmUint(0));
                }
                case "Consume":
                {
                    SetCounter(o);
                    var d = TempStore(NumericType.Uint);
                    _w.Emit(Opcode.ImmAtomicConsume, 0, DestOf(d), DxOperand.Bare(file, o.Register));
                    var elem = t.Element ?? NumericType.Uint;
                    var loc = new MemLoc { File = file, Register = o.Register, Kind = 's', Stride = SizeOfStructured(elem), Index = Read(d) };
                    return MemRead(loc, elem, m);
                }
            }
            if (m.Name.StartsWith("Interlocked", StringComparison.Ordinal) && raw)
            {
                // raw.InterlockedAdd(address, value[, original]); CompareExchange(address, compare, value, original)
                bool cmp = m.Name is "InterlockedCompareExchange" or "InterlockedCompareStore";
                var loc = Raw(m.Args[0]);
                var value = Arg(m.Args, cmp ? 2 : 1, m);
                var compare = cmp ? Arg(m.Args, 1, m) : null;
                int origIndex = cmp ? 3 : 2;
                Atomic(m.Name, loc, value.Type.Scalar == ScalarKind.Int ? NumericType.Int : NumericType.Uint, value, compare, m.Args.Count > origIndex ? m.Args[origIndex] : null, m);
                return Wrap(ImmUint(0));
            }
            return null;
        }

        private void SetCounter(ObjValue o)
        {
            foreach (var r in _resources.Where(x => x.Type == o.Obj)) r.Counter = true;
        }

        // ---- barriers -----------------------------------------------------------------------------------

        private Value? Barrier(string name, Node at)
        {
            // D3D11_SB_SYNC flags: 1 threads in group, 2 group-shared memory, 4 UAVs of the group, 8 UAVs globally
            uint? flags = name switch
            {
                "GroupMemoryBarrier" => 2u, "GroupMemoryBarrierWithGroupSync" => 3u,
                "DeviceMemoryBarrier" => 8u, "DeviceMemoryBarrierWithGroupSync" => 9u,
                "AllMemoryBarrier" => 10u, "AllMemoryBarrierWithGroupSync" => 11u,
                _ => null,
            };
            if (flags == null) return null;
            uint f = flags.Value;
            // outside a compute shader there is no group: the UAV fences alone
            if (_stage != ShaderStage.Compute)
            {
                if ((f & 1) != 0) throw Err(at, $"{name} is only valid in a compute shader", 4532);
                f &= ~2u;
                if (f == 0) return Wrap(ImmUint(0));
            }
            _w.Emit(Opcode.Sync, f);
            return Wrap(ImmUint(0));
        }

        // ---- the compute shader -----------------------------------------------------------------------

        private readonly List<(OperandType File, int Mask)> _computeInputs = new();
        private (uint X, uint Y, uint Z) _threads = (1, 1, 1);

        /// <summary>A compute shader's system value: the thread ids, registers of their own.</summary>
        private Value? ComputeInput(string semantic, NumericType nt, Node at)
        {
            var file = semantic.ToUpperInvariant() switch
            {
                "SV_DISPATCHTHREADID" => OperandType.InputThreadId,
                "SV_GROUPID" => OperandType.InputThreadGroupId,
                "SV_GROUPTHREADID" => OperandType.InputThreadIdInGroup,
                "SV_GROUPINDEX" => OperandType.InputThreadIdInGroupFlattened,
                _ => (OperandType?)null,
            };
            if (file == null) throw Err(at, $"'{semantic}' is not a compute shader input", 4502);
            int n = file == OperandType.InputThreadIdInGroupFlattened ? 1 : Math.Min(3, Math.Max(1, nt.Count));
            int i = _computeInputs.FindIndex(x => x.File == file);
            int mask = file == OperandType.InputThreadIdInGroupFlattened ? 1 : 7;
            if (i < 0) _computeInputs.Add((file.Value, mask));
            var num = new Num { Type = nt.WithScalar(nt.Scalar == ScalarKind.Int ? ScalarKind.Int : ScalarKind.Uint), File = file.Value, Index = Array.Empty<long>(), Relative = Array.Empty<DxOperand?>(), Comp = Enumerable.Range(0, n).Select(c => (byte)c).ToArray() };
            return new NumValue { Type = num.Type, N = num };
        }

        private void ThreadGroup(FunctionDecl f)
        {
            var a = f.Attributes.FirstOrDefault(x => x.Name.Equals("numthreads", StringComparison.OrdinalIgnoreCase))
                ?? throw Err(f, "a compute shader needs [numthreads(x, y, z)]", 3087);
            if (a.Args.Count != 3) throw Err(a, "numthreads takes three arguments", 3087);
            _threads = ((uint)ConstInt(a.Args[0]), (uint)ConstInt(a.Args[1]), (uint)ConstInt(a.Args[2]));
            if (_threads.X * _threads.Y * _threads.Z is 0 or > 1024) throw Err(a, "a thread group has 1 to 1024 threads", 3087);
        }

        private void ComputeDeclarations()
        {
            foreach (var (file, mask) in _computeInputs) _w.Emit(Opcode.DclInput, 0, DxOperand.Dest(file, mask));
            foreach (var g in _groupShared.Values.Where(g => g.Register >= 0).OrderBy(g => g.Register))
                _w.EmitWith(Opcode.DclThreadGroupSharedMemoryStructured, 0, null, new[] { DxOperand.Bare(OperandType.ThreadGroupSharedMemory, g.Register) }, new[] { (uint)g.Stride, (uint)g.Count });
            _w.Declare(Opcode.DclThreadGroup, 0, _threads.X, _threads.Y, _threads.Z);
        }
    }
}
