// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The shim's HLSL compiler: geometry shaders. The input primitive's vertices are the two-dimensional
// input registers (v[vertex][register], one signature for every vertex); each output stream
// (PointStream, LineStream, TriangleStream) is an output signature of its own, on stream m# in shader
// model 5 (OSG5); Append writes the outputs and emits the vertex, RestartStrip cuts the strip.
// SV_PrimitiveID and SV_GSInstanceID are registers of their own (vPrim, vGSInstanceID).
//
// And loop unrolling, which geometry shaders need most (a loop over the input vertices indexes the
// input registers by a constant): a [unroll] for loop with a constant trip count is its body once
// per value of its counter, the counter a constant in each.
//

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Wpf.Interop.WebGpu.Direct3D.Dxbc;

namespace Microsoft.Wpf.Interop.WebGpu.Direct3D.Hlsl
{
    internal sealed partial class DxbcCodegen
    {
        private int _gsPrimitive;              // D3D10_SB_PRIMITIVE: 1 point, 2 line, 3 triangle, 6 line_adj, 7 triangle_adj
        private int _gsVertices;
        private int _gsMaxVertices, _gsInstances = 1;
        private int _currentStream;
        private readonly List<(int Topology, Store Outputs)> _streams = new();
        /// <summary>The system values that are registers of their own (vPrim, vGSInstanceID, the compute ids).</summary>
        private readonly List<(OperandType File, int Mask)> _specialInputs = new();

        private static int PrimitiveOf(HashSet<string> mods) =>
            mods.Contains("point") ? 1 : mods.Contains("line") ? 2 : mods.Contains("triangle") ? 3 : mods.Contains("lineadj") ? 6 : mods.Contains("triangleadj") ? 7 : 0;

        private static int VerticesOf(int prim) => prim switch { 1 => 1, 2 => 2, 3 => 3, 6 => 4, 7 => 6, _ => 0 };

        private void GeometryAttributes(FunctionDecl f)
        {
            var max = f.Attributes.FirstOrDefault(a => a.Name.Equals("maxvertexcount", StringComparison.OrdinalIgnoreCase))
                ?? throw Err(f, "a geometry shader needs [maxvertexcount(n)]", 3514);
            _gsMaxVertices = (int)ConstInt(max.Args[0]);
            var inst = f.Attributes.FirstOrDefault(a => a.Name.Equals("instance", StringComparison.OrdinalIgnoreCase));
            if (inst != null)
            {
                if (!Sm5) throw Err(inst, "[instance] needs shader model 5", 3514);
                _gsInstances = (int)ConstInt(inst.Args[0]);
            }
        }

        /// <summary>The input primitive: an array of vertices, each the signature's registers indexed by the vertex.</summary>
        private Value GeometryInput(ParamDecl p, HlslType type)
        {
            int prim = PrimitiveOf(p.Modifiers);
            if (type is not ArrayType at) throw Err(p, "a geometry shader's input primitive is an array of vertices", 3514);
            int n = VerticesOf(prim);
            if (at.Length != n) throw Err(p, $"a {p.Modifiers.First(m => PrimitiveOf(new HashSet<string> { m }) > 0)} primitive has {n} vertices, not {at.Length}", 3514);
            _gsPrimitive = prim;
            _gsVertices = n;
            var one = BindSignature(at.Element, p.Semantic, p.Modifiers, false, p, null);
            return new AggValue { Type = at, Parts = Enumerable.Range(0, n).Select(i => OfVertex(one, i)).ToArray() };
        }

        /// <summary>A vertex's copy of the input value: each input register indexed by the vertex too.</summary>
        private static Value OfVertex(Value v, int vertex)
        {
            switch (v)
            {
                case NumValue nv when nv.N.File == OperandType.Input:
                {
                    var n = nv.N.WithType(nv.N.Type);
                    n.Index = new[] { (long)vertex }.Concat(nv.N.Index).ToArray();
                    n.Relative = new DxOperand?[n.Index.Length];
                    return new NumValue { Type = nv.Type, N = n };
                }
                case AggValue a: return new AggValue { Type = a.Type, Parts = a.Parts.Select(p => OfVertex(p, vertex)).ToArray() };
                default: return v;
            }
        }

        /// <summary>An output stream parameter: its element's output signature, on its own stream.</summary>
        private Value GeometryStream(ParamDecl p, ObjectType t)
        {
            int topology = t.Name switch { "PointStream" => 1, "LineStream" => 3, _ => 5 };
            int k = _streams.Count;
            if (k > 0 && !Sm5) throw Err(p, "more than one output stream needs shader model 5", 3514);
            if (k > 3) throw Err(p, "a geometry shader has at most four streams", 3514);
            _currentStream = k;
            var stores = new List<Store>();
            BindSignature(t.Element ?? throw Err(p, "a stream needs a vertex type", 3514), p.Semantic, p.Modifiers, true, p, stores);
            _currentStream = 0;
            _streams.Add((topology, stores[0]));
            return new ObjValue { Type = t, Register = k };
        }

        private Value? StreamMethod(ObjValue o, MethodExpr m)
        {
            if (!o.Obj.IsStream) return null;
            var (_, outputs) = _streams[o.Register];
            var stream = DxOperand.Bare(OperandType.Stream, o.Register);
            switch (m.Name)
            {
                case "Append":
                    Assign(outputs, Convert(EvalValue(m.Args[0]), outputs.Type, m), m);
                    if (Sm5) _w.Emit(Opcode.EmitStream, 0, stream); else _w.Emit(Opcode.Emit, 0);
                    return Wrap(ImmUint(0));
                case "RestartStrip":
                    if (Sm5) _w.Emit(Opcode.CutStream, 0, stream); else _w.Emit(Opcode.Cut, 0);
                    return Wrap(ImmUint(0));
            }
            throw Err(m, $"'{m.Name}' is not a method of {o.Obj.Name}", 3018);
        }

        /// <summary>A system value that is a register of its own in this stage, or null.</summary>
        private Value? SpecialInput(string semantic, NumericType nt)
        {
            var file = (semantic.ToUpperInvariant(), _stage) switch
            {
                ("SV_PRIMITIVEID", ShaderStage.Geometry or ShaderStage.Hull or ShaderStage.Domain) => OperandType.InputPrimitiveId,
                ("SV_GSINSTANCEID", ShaderStage.Geometry) => OperandType.InputGsInstanceId,
                _ => (OperandType?)null,
            };
            if (file == null) return null;
            if (!_specialInputs.Any(x => x.File == file)) _specialInputs.Add((file.Value, 1));
            var num = new Num { Type = nt.WithScalar(ScalarKind.Uint), File = file.Value, Index = Array.Empty<long>(), Relative = Array.Empty<DxOperand?>(), Comp = new byte[] { 0 } };
            return new NumValue { Type = num.Type, N = num };
        }

        private void DeclareSpecialInputs()
        {
            foreach (var (file, mask) in _specialInputs.Concat(_computeInputs))
                _w.Emit(Opcode.DclInput, 0, file is OperandType.InputPrimitiveId or OperandType.InputGsInstanceId ? DxOperand.Bare(file) : DxOperand.Dest(file, mask));
        }

        /// <summary>A geometry shader's declarations after its inputs and temps, in fxc's order.</summary>
        private void GeometryDeclarations()
        {
            if (_gsInstances > 1) _w.Declare(Opcode.DclGsInstanceCount, 0, (uint)_gsInstances);
            _w.Declare(Opcode.DclGsInputPrimitive, (uint)_gsPrimitive);
            for (int k = 0; k < Math.Max(1, _streams.Count); k++)
            {
                if (Sm5) _w.Emit(Opcode.DclStream, 0, DxOperand.Bare(OperandType.Stream, k));
                _w.Declare(Opcode.DclGsOutputTopology, (uint)(k < _streams.Count ? _streams[k].Topology : 1));
                DeclareOutputs(_outputs.Where(e => e.Stream == k));
            }
            _w.Declare(Opcode.DclMaxOutputVertexCount, 0, (uint)_gsMaxVertices);
        }

        // ---- unrolling ------------------------------------------------------------------------------

        /// <summary>A [unroll] for loop with a constant trip count and no break or continue: its counter's values.</summary>
        private bool Unrollable(ForStmt f, out string counter, out List<long> values, out bool unsigned)
        {
            counter = ""; values = new(); unsigned = false;
            if (!f.Attributes.Any(a => a.Name.Equals("unroll", StringComparison.OrdinalIgnoreCase))) return false;
            if (f.Init is not DeclStmt { Vars.Count: 1 } d || d.Vars[0].Init == null) return false;
            var v = d.Vars[0];
            counter = v.Name;
            if (Resolve(v.Type) is not NumericType { IsScalar: true, Scalar: ScalarKind.Int or ScalarKind.Uint } ct) return false;
            unsigned = ct.Scalar == ScalarKind.Uint;
            if (!TryConstIndex(v.Init!, out long start)) return false;
            if (f.Cond is not BinaryExpr { Left: NameExpr cl } c || cl.Name != counter || !TryConstIndex(c.Right, out long end)) return false;
            long step;
            switch (f.Step)
            {
                case UnaryExpr { Op: "++", Operand: NameExpr n } when n.Name == counter: step = 1; break;
                case UnaryExpr { Op: "--", Operand: NameExpr n } when n.Name == counter: step = -1; break;
                case AssignExpr { Op: "+=", Target: NameExpr n } a when n.Name == counter && TryConstIndex(a.Value, out long s1): step = s1; break;
                case AssignExpr { Op: "-=", Target: NameExpr n } a when n.Name == counter && TryConstIndex(a.Value, out long s2): step = -s2; break;
                default: return false;
            }
            if (step == 0 || AssignsTo(f.Body, counter) || HasLoopJump(f.Body)) return false;
            for (long k = start; values.Count <= 1024; k += step)
            {
                bool go = c.Op switch { "<" => k < end, "<=" => k <= end, ">" => k > end, ">=" => k >= end, "!=" => k != end, _ => false };
                if (!go) return values.Count <= 1024;
                values.Add(k);
            }
            return false;
        }

        private static bool HasLoopJump(Stmt s) => s switch
        {
            JumpStmt j => j.Kind is "break" or "continue",
            BlockStmt b => b.Body.Any(HasLoopJump),
            IfStmt i => HasLoopJump(i.Then) || (i.Else != null && HasLoopJump(i.Else)),
            SwitchStmt sw => sw.Cases.Any(c => c.Body.Any(x => x is JumpStmt { Kind: "continue" } || (x is not JumpStmt && HasLoopJump(x)))),
            _ => false,                              // a nested loop's own break and continue are its own
        };

        private void Unrolled(ForStmt f, string counter, List<long> values, bool unsigned)
        {
            foreach (long k in values)
            {
                _scopes.Add(new Dictionary<string, object> { [counter] = Wrap(unsigned ? ImmUint((uint)k) : ImmInt((int)k)) });
                Statement(f.Body);
                _scopes.RemoveAt(_scopes.Count - 1);
            }
        }
    }
}
