// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The shim's HLSL compiler, shader model 4 and 5: the program -- globals and their constant buffers,
// resources and their registers, the entry point's signature, statements and flow control, and the
// DXBC container at the end.
//

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Wpf.Interop.WebGpu.Direct3D.Dxbc;

namespace Microsoft.Wpf.Interop.WebGpu.Direct3D.Hlsl
{
    internal sealed partial class DxbcCodegen
    {
        // ---- globals --------------------------------------------------------------------------------

        internal sealed class CBuffer
        {
            public string Name = "";
            public bool Texture;
            public int ExplicitSlot = -1, Slot = -1;
            public readonly List<CbMember> Members = new();
            public int SizeBytes;
            public bool Used, DynamicallyIndexed;
            public int DeclOrder;
        }

        internal sealed class CbMember
        {
            public string Name = "";
            public HlslType Type = null!;
            public int Offset;                   // bytes
            public int Size;
            public CBuffer Buffer = null!;
            public bool Used;
            public byte[]? Default;
        }

        internal sealed class Resource
        {
            public string Name = "";
            public ObjectType Type = null!;
            public char Kind;                    // t s u
            public int ExplicitSlot = -1, Slot = -1;
            public int ArrayLength = 1;
            public bool Used;
            public int DeclOrder;
            /// <summary>A D3D9-style sampler (sampler2D): a texture and a sampler at one register.</summary>
            public bool Legacy;
            /// <summary>A structured UAV whose counter the program uses.</summary>
            public bool Counter;
        }

        private readonly List<CBuffer> _cbuffers = new();
        private readonly Dictionary<string, CbMember> _cbMembers = new();
        private readonly List<Resource> _resources = new();
        private readonly Dictionary<string, Resource> _resourceByName = new();
        private readonly Dictionary<string, Value> _constants = new();
        private readonly Dictionary<string, VarDecl> _statics = new();
        private readonly List<uint> _icb = new();

        private void Collect()
        {
            int order = 0;
            CBuffer? globals = null;
            foreach (var d in _unit.Decls)
            {
                switch (d)
                {
                    case StructDecl s:
                    {
                        var st = new StructType(s.Name);
                        _structs[s.Name] = st;
                        foreach (var f in s.Fields)
                            st.Fields.Add(new StructField { Name = f.Name, Type = Resolve(f.Type, f.ArrayDims), Semantic = f.Semantic, Modifiers = f.Modifiers });
                        break;
                    }
                    case TypedefDecl t: _typedefs[t.Name] = (t.Type, t.ArrayDims); break;
                    case FunctionDecl f:
                        if (!_functions.TryGetValue(f.Name, out var list)) _functions[f.Name] = list = new();
                        if (f.Body != null) { list.RemoveAll(x => x.Body == null && x.Params.Count == f.Params.Count); list.Add(f); }
                        else if (!list.Any(x => x.Params.Count == f.Params.Count)) list.Add(f);
                        break;
                    case CBufferDecl c:
                    {
                        var cb = new CBuffer { Name = c.Name, Texture = c.Texture, DeclOrder = order++ };
                        var reg = c.Registers.FirstOrDefault(r => r.Kind is 'b' or 't' && r.Profile == null);
                        if (reg != null) cb.ExplicitSlot = reg.Index;
                        foreach (var m in c.Members) AddMember(cb, m);
                        Finish(cb);
                        _cbuffers.Add(cb);
                        break;
                    }
                    case VarDecl v:
                    {
                        var type = Resolve(v.Type, v.ArrayDims);
                        if (Base(type) is ObjectType ot)
                        {
                            var r = new Resource { Name = v.Name, Type = ot, DeclOrder = order++, ArrayLength = type is ArrayType at ? at.Length : 1 };
                            r.Kind = ot.IsSampler ? 's' : ot.IsUav ? 'u' : 't';
                            r.Legacy = ot.IsLegacySampler || ot.Name == "sampler";
                            var reg = v.Registers.FirstOrDefault(x => x.Profile == null || ProfileMatches(x.Profile));
                            if (reg != null) r.ExplicitSlot = reg.Index;
                            _resources.Add(r);
                            _resourceByName[v.Name] = r;
                            break;
                        }
                        if (v.Modifiers.Contains("groupshared"))
                        {
                            if (_stage != ShaderStage.Compute) break;       // other stages have none (fxc: ignored)
                            _groupShared[v.Name] = new GroupShared { Name = v.Name, Type = type };
                            break;
                        }
                        if (v.Modifiers.Contains("static"))
                        {
                            if (v.Modifiers.Contains("const") && v.Init != null && TryConstant(v, type) is { } k) _constants[v.Name] = k;
                            else _statics[v.Name] = v;
                            break;
                        }
                        // a uniform outside a cbuffer: $Globals
                        globals ??= new CBuffer { Name = "$Globals", DeclOrder = order++ };
                        AddMember(globals, v);
                        break;
                    }
                }
            }
            if (globals != null) { Finish(globals); _cbuffers.Insert(0, globals); }
        }

        private bool ProfileMatches(string p) => p.StartsWith(_stage switch
        {
            ShaderStage.Vertex => "vs", ShaderStage.Pixel => "ps", ShaderStage.Geometry => "gs", ShaderStage.Hull => "hs", ShaderStage.Domain => "ds", _ => "cs",
        }, StringComparison.Ordinal);

        private static HlslType Base(HlslType t) => t is ArrayType a ? Base(a.Element) : t;

        private void AddMember(CBuffer cb, VarDecl v)
        {
            var type = Resolve(v.Type, v.ArrayDims);
            int offset;
            if (v.PackOffset is { } po) offset = po.Index * 16 + po.Component * 4;
            else if (_legacy && v.Registers.FirstOrDefault(r => r.Kind == 'c' && (r.Profile == null || ProfileMatches(r.Profile))) is { } cr) offset = cr.Index * 16;
            else offset = _legacy ? (cb.SizeBytes + 15) & ~15 : Place(cb.SizeBytes, type);
            // the legacy models give each uniform registers of its own
            var m = new CbMember { Name = v.Name, Type = type, Offset = offset, Size = _legacy ? Math.Max(1, Registers(type)) * 16 : SizeOf(type), Buffer = cb };
            if (v.Init != null && TryConstant(v, type) is NumValue nv && nv.N.Imm != null)
            {
                m.Default = new byte[Math.Max(m.Size, nv.N.Imm.Length * 4)];
                Buffer.BlockCopy(nv.N.Imm, 0, m.Default, 0, nv.N.Imm.Length * 4);
            }
            cb.Members.Add(m);
            cb.SizeBytes = Math.Max(cb.SizeBytes, offset + m.Size);
            _cbMembers[v.Name] = m;
        }

        private static void Finish(CBuffer cb) => cb.SizeBytes = (cb.SizeBytes + 15) & ~15;

        /// <summary>Where a member of this type goes after <paramref name="end"/> bytes (fxc's packing).</summary>
        private static int Place(int end, HlslType t)
        {
            int size = SizeOf(t);
            bool startsRegister = t is ArrayType or StructType || t is NumericType { IsMatrix: true };
            if (startsRegister) return (end + 15) & ~15;
            // a member does not straddle a register boundary
            if ((end & 15) + size > 16) return (end + 15) & ~15;
            return end;
        }

        /// <summary>A type's size in a constant buffer: the last register's used part counts, the rest does not.</summary>
        internal static int SizeOf(HlslType t)
        {
            switch (t)
            {
                case NumericType n when n.IsMatrix:
                {
                    int lines = n.RowMajor ? n.Rows : n.Cols, per = n.RowMajor ? n.Cols : n.Rows;
                    int comp = n.Scalar == ScalarKind.Double ? 8 : 4;
                    return (lines - 1) * 16 + per * comp;
                }
                case NumericType n: return n.Components * 4;
                case ArrayType a: return (Math.Max(a.Length, 1) - 1) * ((SizeOf(a.Element) + 15) & ~15) + SizeOf(a.Element);
                case StructType s:
                {
                    int end = 0;
                    foreach (var f in s.Fields) end = Place(end, f.Type) + SizeOf(f.Type);
                    return end;
                }
            }
            return 0;
        }

        /// <summary>Registers an element of this type takes in a register file (an indexable temp's, a cbuffer's).</summary>
        internal static int Registers(HlslType t) => t switch
        {
            NumericType n when n.IsMatrix => n.RowMajor ? n.Rows : n.Cols,
            NumericType => 1,
            ArrayType a => Math.Max(a.Length, 1) * Registers(a.Element),
            StructType s => Math.Max(1, (SizeOf(s) + 15) / 16),
            _ => 1,
        };

        // ---- constants -------------------------------------------------------------------------------

        /// <summary>A static const (or a default value) whose initializer folds to immediates.</summary>
        private Value? TryConstant(VarDecl v, HlslType type)
        {
            try
            {
                var vals = new List<uint>();
                if (!Fold(v.Init!, type, vals)) return null;
                if (type is NumericType n && !n.IsMatrix) return new NumValue { Type = n, N = new Num { Type = n, Imm = vals.ToArray() } };
                if (type is ArrayType a && a.Element is NumericType en && !en.IsMatrix)
                {
                    // a table: the immediate constant buffer, one element per register
                    int start = _icb.Count / 4;
                    for (int i = 0; i < a.Length; i++)
                        for (int c = 0; c < 4; c++) _icb.Add(c < en.Count && i * en.Count + c < vals.Count ? vals[i * en.Count + c] : 0);
                    return new ArrayRef { Type = a, File = OperandType.ImmediateConstantBuffer, Base = start, Stride = 1 };
                }
            }
            catch (HlslException) { }
            return null;
        }

        /// <summary>The bits of a constant initializer, converted to the type's scalar.</summary>
        private bool Fold(Expr e, HlslType type, List<uint> into)
        {
            if (type is ArrayType a)
            {
                if (e is not InitListExpr l) return false;
                var flat = Flatten(l).ToList();
                int per = a.Element is NumericType ne ? ne.Count : 0;
                if (per == 0) return false;
                foreach (var x in flat) if (!Fold(x, a.Element is NumericType n1 ? n1.ScalarOf : a.Element, into)) return false;
                return true;
            }
            if (type is not NumericType nt) return false;
            if (e is InitListExpr il) { foreach (var x in Flatten(il)) if (!Fold(x, nt.ScalarOf, into)) return false; return true; }
            if (e is CallExpr c && c.Constructor != null) { foreach (var x in c.Args) if (!Fold(x, nt.ScalarOf, into)) return false; return true; }
            if (!TryFoldScalar(e, out double d, out bool isFloat)) return false;
            uint bits = nt.Scalar switch
            {
                ScalarKind.Float or ScalarKind.Double => BitConverter.SingleToUInt32Bits((float)d),
                ScalarKind.Bool => d != 0 ? 0xFFFFFFFFu : 0,
                ScalarKind.Uint => (uint)(long)d,
                _ => (uint)(int)(long)d,
            };
            for (int i = 0; i < (into.Count == 0 && nt.Count > 1 && e is not InitListExpr ? nt.Count : 1); i++) into.Add(bits);
            return true;
        }

        private static IEnumerable<Expr> Flatten(InitListExpr l)
        {
            foreach (var x in l.Items)
                if (x is InitListExpr inner) foreach (var y in Flatten(inner)) yield return y;
                else yield return x;
        }

        private bool TryFoldScalar(Expr e, out double v, out bool isFloat)
        {
            v = 0; isFloat = false;
            switch (e)
            {
                case LiteralExpr l when l.Suffix != 's':
                    isFloat = l.IsFloat; v = l.IsFloat ? l.FloatValue : (double)(long)l.IntValue; return true;
                case UnaryExpr u when u.Op is "-" or "+":
                    if (!TryFoldScalar(u.Operand, out v, out isFloat)) return false;
                    if (u.Op == "-") v = -v;
                    return true;
                case BinaryExpr b when b.Op is "+" or "-" or "*" or "/":
                {
                    if (!TryFoldScalar(b.Left, out double x, out bool fx) || !TryFoldScalar(b.Right, out double y, out bool fy)) return false;
                    isFloat = fx || fy;
                    v = b.Op switch { "+" => x + y, "-" => x - y, "*" => x * y, _ => isFloat ? x / y : (y == 0 ? 0 : Math.Truncate(x / y)) };
                    return true;
                }
                case CastExpr c: return TryFoldScalar(c.Value, out v, out isFloat);
                case NameExpr n when _constants.TryGetValue(n.Name, out var k) && k is NumValue nv && nv.N.Imm is { Length: 1 } imm:
                    isFloat = nv.N.Type.IsFloatLike;
                    v = isFloat ? BitConverter.UInt32BitsToSingle(imm[0]) : nv.N.Type.Scalar == ScalarKind.Uint ? imm[0] : (int)imm[0];
                    return true;
            }
            return false;
        }

        // ---- the entry point --------------------------------------------------------------------------

        private sealed class SigEntry
        {
            public string Semantic = "";
            public int Index;
            public NumericType Type = NumericType.Float;
            public int Register = -1;
            /// <summary>The first component it takes in its register (fxc packs several into one).</summary>
            public int Start;
            public bool InArray;
            /// <summary>A geometry shader's output: its stream.</summary>
            public int Stream;
            public int Mask => ((1 << Math.Max(1, Math.Min(4, Type.Count))) - 1) << Start;
            public int SystemValue;
            public HashSet<string> Modifiers = new();
            public bool Output;
        }

        private readonly List<SigEntry> _inputs = new(), _outputs = new();
        private bool _writesDepth, _readsCoverage;
        private Store? _entryReturn;

        private static (string Name, int Index) SplitSemantic(string s)
        {
            int k = s.Length;
            while (k > 0 && char.IsDigit(s[k - 1])) k--;
            return k == s.Length ? (s, 0) : (s.Substring(0, k), int.Parse(s.AsSpan(k), System.Globalization.CultureInfo.InvariantCulture));
        }

        private int SystemValueOf(string name, bool output)
        {
            string u = name.ToUpperInvariant();
            bool compat = (_flags & HlslCompiler.EnableBackwardsCompatibility) != 0 || _legacy;
            if (_legacy && !output && _stage == ShaderStage.Pixel && name.ToUpperInvariant() is "VPOS") return 1;
            if (_legacy && !output && _stage == ShaderStage.Pixel && name.ToUpperInvariant() is "VFACE") return 9;
            return u switch
            {
                "SV_POSITION" => 1,
                "POSITION" when compat && output && _stage == ShaderStage.Vertex => 1,
                "SV_CLIPDISTANCE" => 2, "SV_CULLDISTANCE" => 3, "SV_RENDERTARGETARRAYINDEX" => 4, "SV_VIEWPORTARRAYINDEX" => 5,
                "SV_VERTEXID" => 6, "SV_PRIMITIVEID" => 7, "SV_INSTANCEID" => 8, "SV_ISFRONTFACE" => 9, "SV_SAMPLEINDEX" => 10,
                "SV_TARGET" => 64,
                "COLOR" when compat && output && _stage == ShaderStage.Pixel => 64,
                "SV_DEPTH" => 65,
                "DEPTH" when compat && output && _stage == ShaderStage.Pixel => 65,
                "SV_COVERAGE" => 66,
                "SV_DEPTHGREATEREQUAL" => 67, "SV_DEPTHLESSEQUAL" => 68,
                _ => 0,
            };
        }

        /// <summary>The signature elements of a parameter or return value (a struct's fields each one).</summary>
        private Value BindSignature(HlslType type, string? semantic, HashSet<string> mods, bool output, Node at, List<Store>? stores, bool inArray = false)
        {
            if (type is StructType st)
            {
                var parts = new List<Value>();
                var partStores = new List<Store>();
                foreach (var f in st.Fields)
                {
                    var ps = output ? new List<Store>() : null;
                    parts.Add(BindSignature(f.Type, f.Semantic ?? semantic, new HashSet<string>(f.Modifiers.Concat(mods)), output, at, ps));
                    if (ps != null) partStores.Add(ps[0]);
                }
                stores?.Add(new AggStore { Type = st, Parts = partStores.ToArray() });
                return new AggValue { Type = st, Parts = parts.ToArray() };
            }
            if (type is ArrayType arr)
            {
                // an array of semantics: TEXCOORD0..n
                var (name, idx) = SplitSemantic(semantic ?? throw Err(at, "missing semantic", 4502));
                var parts = new List<Value>();
                var partStores = new List<Store>();
                for (int i = 0; i < arr.Length; i++)
                {
                    var ps = output ? new List<Store>() : null;
                    parts.Add(BindSignature(arr.Element, name + (idx + i), mods, output, at, ps, inArray: true));
                    if (ps != null) partStores.Add(ps[0]);
                }
                stores?.Add(new AggStore { Type = arr, Parts = partStores.ToArray() });
                return new AggValue { Type = arr, Parts = parts.ToArray() };
            }
            if (type is not NumericType nt || nt.IsMatrix) throw Err(at, $"a {type} cannot be a shader input or output");
            if (semantic == null) throw Err(at, "missing semantic", 4502);
            var (sname, sindex) = SplitSemantic(semantic);
            int sv = SystemValueOf(sname, output);
            if (sv == 0 && _stage is ShaderStage.Hull or ShaderStage.Domain) sv = TessFactorName(sname, sindex);
            if (_stage == ShaderStage.Compute)
            {
                if (output) throw Err(at, "a compute shader has no outputs", 4502);
                return ComputeInput(semantic, nt, at)!;
            }
            if (!output && SpecialInput(sname, nt) is { } special) return special;
            var e = new SigEntry { Semantic = sname, Index = sindex, Type = nt, SystemValue = sv, Modifiers = mods, Output = output, InArray = inArray, Stream = output ? _currentStream : 0 };
            // a geometry shader's streams each have their registers
            var list = output ? _outputs.Where(x => x.Stream == _currentStream).ToList() : _inputs;
            if (!output && sv == 66)
            {
                // the input coverage is a register of its own (vCoverage), not a signature element
                if (_stage != ShaderStage.Pixel || !Sm5) throw Err(at, "an input SV_Coverage needs a shader model 5 pixel shader", 4502);
                _readsCoverage = true;
                var cov = new Num { Type = nt, File = OperandType.InputCoverageMask, Index = Array.Empty<long>(), Relative = Array.Empty<DxOperand?>(), Comp = new byte[] { 0 } };
                return new NumValue { Type = nt, N = cov };
            }
            if (sv == 64) e.Register = sindex;
            else if (sv > 64 && output) e.Register = -1;
            else e.Register = list.Where(x => x.Register >= 0 && x.SystemValue != 64).Select(x => x.Register + 1).DefaultIfEmpty(0).Max();
            if (output && sv < 64)
                e.Register = Math.Max(e.Register, list.Where(x => x.SystemValue == 64).Select(x => x.Register + 1).DefaultIfEmpty(0).Max());
            // fxc's packing: an element goes into the first register it fits after what is there, with
            // elements that interpolate alike (a vertex shader's inputs, each a vertex buffer element, do not)
            if (sv < 64)
            {
                foreach (var g in list.Where(x => x.Register >= 0 && x.Register < e.Register && x.SystemValue < 64).GroupBy(x => x.Register).OrderBy(g => g.Key))
                {
                    int used = g.Max(x => x.Start + Math.Max(1, x.Type.Count));
                    if (used + Math.Max(1, nt.Count) <= 4 && g.All(x => Packs(x, e))) { e.Register = g.Key; e.Start = used; break; }
                }
            }
            (output ? _outputs : _inputs).Add(e);
            var comps = Enumerable.Range(e.Start, Math.Max(1, nt.Count)).Select(i => (byte)i).ToArray();
            if (output)
            {
                if (sv is 65 or 67 or 68) _writesDepth = true;
                var s = new NumStore
                {
                    Type = nt,
                    File = sv switch { 65 => OperandType.OutputDepth, 66 => OperandType.OutputCoverageMask, 67 => OperandType.OutputDepthGreaterEqual, 68 => OperandType.OutputDepthLessEqual, _ => OperandType.Output },
                    Index = sv > 64 ? Array.Empty<long>() : new long[] { e.Register },
                    Relative = sv > 64 ? Array.Empty<DxOperand?>() : new DxOperand?[1],
                    Comp = comps,
                };
                stores?.Add(s);
                return new NumValue { Type = nt, N = Read(s) };
            }
            // an input: v#; a pixel shader's SV_IsFrontFace is a bool (0 / ~0) in the register
            var n = new Num { Type = sv == 9 && !_legacy ? NumericType.Bool : nt, File = OperandType.Input, Index = new long[] { e.Register }, Relative = new DxOperand?[1], Comp = comps };
            return new NumValue { Type = n.Type, N = n };
        }

        // ---- the program ------------------------------------------------------------------------------

        /// <summary>Compiles the entry point: the container's bytes.</summary>
        public byte[] Compile(string entry)
        {
            Collect();
            if (!_functions.TryGetValue(entry, out var fs) || fs.All(f => f.Body == null))
                throw new HlslException(default, $"entrypoint not found", 3501);
            var f = fs.First(x => x.Body != null);
            if (_legacy) return CompileLegacy(f);
            if (_stage == ShaderStage.Hull) return CompileHull(f);
            if (_stage == ShaderStage.Domain) TessellationAttributes(f);
            if (_stage == ShaderStage.Compute) ThreadGroup(f);
            if (_stage == ShaderStage.Geometry) GeometryAttributes(f);
            // the first pass learns what is used; the second binds it in declaration order and emits
            Generate(f, record: true);
            AssignRegisters();
            Reset();
            Generate(f, record: false);
            return Container(f);
        }

        private void Reset()
        {
            _w.Code.Clear();
            _w.InstructionCount = 0;
            _temps = 0;
            _indexables.Clear();
            _inputs.Clear();
            _outputs.Clear();
            _writesDepth = false;
            _scopes.Clear();
            _computeInputs.Clear();
            _specialInputs.Clear();
            _streams.Clear();
            _patchConstants.Clear();
            _pred = null;
            _terminated = false;
            _tgsmCount = 0;
            foreach (var g in _groupShared.Values) g.Register = -1;
        }

        private bool _recording;

        private void Generate(FunctionDecl f, bool record)
        {
            _recording = record;
            _scopes.Add(new Dictionary<string, object>());
            // statics: initialized at the start, as globals are
            foreach (var (name, v) in _statics)
            {
                var type = Resolve(v.Type, v.ArrayDims);
                var store = AllocLocal(type);
                if (v.Init != null) Assign(store, Convert(Eval(v.Init, type), type, v.Init), v);
                else ZeroInit(store);
                _scopes[0][name] = store;
            }
            var frame = new Frame { Function = f, IsEntry = true };
            // inputs and outputs
            foreach (var p in f.Params)
            {
                var type = Resolve(p.Type, p.ArrayDims);
                if (p.Modifiers.Contains("uniform")) throw Err(p, "uniform entry-point parameters are not supported");
                if (TessellationParam(p, type) is { } patch) { frame.Locals[p.Name] = patch; continue; }
                if (_stage == ShaderStage.Geometry && type is ObjectType { IsStream: true } st) { frame.Locals[p.Name] = GeometryStream(p, st); continue; }
                if (_stage == ShaderStage.Geometry && PrimitiveOf(p.Modifiers) > 0) { frame.Locals[p.Name] = GeometryInput(p, type); continue; }
                if (p.IsOut)
                {
                    var stores = new List<Store>();
                    BindSignature(type, p.Semantic, p.Modifiers, true, p, stores);
                    frame.Locals[p.Name] = stores[0];
                    if (p.Modifiers.Contains("inout")) throw Err(p, "inout entry-point parameters are not supported");
                }
                else
                {
                    var v = BindSignature(type, p.Semantic, p.Modifiers, false, p, null);
                    // a parameter the function assigns to is a copy
                    if (AssignsTo(f.Body!, p.Name))
                    {
                        var s = AllocLocal(type);
                        Assign(s, v, p);
                        frame.Locals[p.Name] = s;
                    }
                    else frame.Locals[p.Name] = v;
                }
            }
            var ret = Resolve(f.ReturnType);
            if (ret is not VoidType)
            {
                var stores = new List<Store>();
                BindSignature(ret, f.ReturnSemantic, f.ReturnType.Modifiers, true, f, stores);
                _entryReturn = stores[0];
            }
            else _entryReturn = null;
            // the legacy models: the outputs written through temps (a predicated assignment reads
            // what it keeps, and output registers cannot be read), copied out at the end
            var shadows = new List<(Store Shadow, Store Output)>();
            if (_legacy)
            {
                Store Shadow(Store o) { var sh = AllocLocal(o.Type); ZeroInit(sh); shadows.Add((sh, o)); return sh; }
                foreach (var p in f.Params.Where(p => p.IsOut)) if (frame.Locals[p.Name] is Store os) frame.Locals[p.Name] = Shadow(os);
                if (_entryReturn != null) _entryReturn = Shadow(_entryReturn);
            }
            // shader model 3: a return before the end is a branch out of a loop around the body
            bool wrap = _legacy && !_flatten && HasEarlyReturn(f.Body!);
            if (wrap)
            {
                frame.Returned = TempStore(NumericType.Bool);
                Op(Opcode.Mov, frame.Returned, false, ImmUint(0));
                _w.Emit(Opcode.Loop, 0);
                _loops.Add((f.Body!, null, null, false));
            }
            _frames.Add(frame);
            Block(f.Body!, newScope: false);
            _frames.RemoveAt(_frames.Count - 1);
            if (wrap)
            {
                _w.Emit(Opcode.Break, 0);
                _w.Emit(Opcode.EndLoop, 0);
                _loops.RemoveAt(_loops.Count - 1);
            }
            _pred = null;
            foreach (var (sh, o) in shadows) Assign(o, ReadStore(sh), f);
            _w.Emit(Opcode.Ret, 0);
        }

        private void AssignRegisters()
        {
            // constant buffers: explicit slots, then the used ones in order of declaration
            var taken = new HashSet<int>(_cbuffers.Where(c => c.ExplicitSlot >= 0).Select(c => c.ExplicitSlot));
            foreach (var cb in _cbuffers)
            {
                if (cb.ExplicitSlot >= 0) { cb.Slot = cb.ExplicitSlot; continue; }
                if (!cb.Used) continue;
                int s = 0;
                while (taken.Contains(s)) s++;
                cb.Slot = s;
                taken.Add(s);
            }
            foreach (char kind in "tsu")
            {
                var used = _resources.Where(r => (r.Kind == kind || (kind == 't' && r.Legacy)) && r.Used).ToList();
                var slots = new HashSet<int>(used.Where(r => r.ExplicitSlot >= 0).SelectMany(r => Enumerable.Range(r.ExplicitSlot, r.ArrayLength)));
                if (kind == 't') foreach (var tb in _cbuffers.Where(c => c.Texture && c.Slot >= 0)) slots.Add(tb.Slot);
                foreach (var r in used.OrderBy(r => r.DeclOrder))
                {
                    if (r.ExplicitSlot >= 0) { r.Slot = r.ExplicitSlot; continue; }
                    if (r.Slot >= 0) continue;
                    int s = 0;
                    while (Enumerable.Range(s, r.ArrayLength).Any(slots.Contains)) s++;
                    r.Slot = s;
                    foreach (int k in Enumerable.Range(s, r.ArrayLength)) slots.Add(k);
                }
            }
        }

        // ---- scopes and frames --------------------------------------------------------------------------

        private sealed class Frame
        {
            public FunctionDecl Function = null!;
            public bool IsEntry;
            public readonly Dictionary<string, object> Locals = new();
            /// <summary>An inlined function's result, and whether it returned (a return before its end).</summary>
            public Store? Result;
            public NumStore? Returned;
            /// <summary>Flattened (shader model 2): the lanes that returned, 0 or 1.</summary>
            public NumStore? RetAcc;
            /// <summary>The loops and switches open in the function (a return from inside them breaks out).</summary>
            public int Nesting;
        }

        private readonly List<Frame> _frames = new();
        private readonly List<Dictionary<string, object>> _scopes = new();
        private Frame Current => _frames[^1];

        private object? Lookup(string name)
        {
            for (int i = _scopes.Count - 1; i >= 1; i--) if (_scopes[i].TryGetValue(name, out var v)) return v;
            if (_frames.Count > 0 && Current.Locals.TryGetValue(name, out var l)) return l;
            if (_scopes.Count > 0 && _scopes[0].TryGetValue(name, out var g)) return g;
            return null;
        }

        /// <summary>Storage for a local of a type: temps for numbers and structs, an indexable temp for an array.</summary>
        private Store AllocLocal(HlslType t)
        {
            switch (t)
            {
                case NumericType n when n.IsMatrix:
                {
                    var rows = new Store[n.Rows];
                    for (int i = 0; i < n.Rows; i++) rows[i] = TempStore(n.Row);
                    return new AggStore { Type = n, Parts = rows };
                }
                case NumericType n: return TempStore(n);
                case StructType s: return new AggStore { Type = s, Parts = s.Fields.Select(f => AllocLocal(f.Type)).ToArray() };
                case ArrayType a when _legacy:
                    // no indexable temps before shader model 4: an element is temps of its own
                    return new AggStore { Type = a, Parts = Enumerable.Range(0, Math.Max(1, a.Length)).Select(_ => AllocLocal(a.Element)).ToArray() };
                case ArrayType a:
                {
                    // x#[n * per element]: an element's registers in a row
                    int per = Registers(a.Element);
                    int x = _indexables.Count;
                    int comps = Base(a.Element) is NumericType en && !en.IsMatrix ? Math.Max(1, en.Count) : 4;
                    _indexables.Add((x, Math.Max(1, a.Length) * per, comps == 3 ? 4 : comps));
                    return new AggStore { Type = a, Parts = Enumerable.Range(0, Math.Max(1, a.Length)).Select(i => IndexablePart(a.Element, x, i * per)).ToArray() };
                }
            }
            throw new InvalidOperationException($"no storage for {t}");
        }

        /// <summary>A part of an indexable temp at a register offset (an element of a local array).</summary>
        private Store IndexablePart(HlslType t, int x, long offset)
        {
            switch (t)
            {
                case NumericType n when n.IsMatrix:
                    return new AggStore { Type = n, Parts = Enumerable.Range(0, n.Rows).Select(i => (Store)new NumStore { Type = n.Row, File = OperandType.IndexableTemp, Index = new[] { (long)x, offset + i }, Relative = new DxOperand?[2], Comp = Seq(n.Cols) }).ToArray() };
                case NumericType n:
                    return new NumStore { Type = n, File = OperandType.IndexableTemp, Index = new[] { (long)x, offset }, Relative = new DxOperand?[2], Comp = Seq(n.Count) };
                case StructType s:
                {
                    var parts = new List<Store>();
                    long at = offset;
                    foreach (var f in s.Fields) { parts.Add(IndexablePart(f.Type, x, at)); at += Registers(f.Type); }
                    return new AggStore { Type = s, Parts = parts.ToArray() };
                }
                case ArrayType a:
                {
                    int per = Registers(a.Element);
                    return new AggStore { Type = a, Parts = Enumerable.Range(0, Math.Max(1, a.Length)).Select(i => IndexablePart(a.Element, x, offset + i * per)).ToArray() };
                }
            }
            throw new InvalidOperationException($"no storage for {t}");
        }

        private static byte[] Seq(int n) => Enumerable.Range(0, Math.Max(1, Math.Min(4, n))).Select(i => (byte)i).ToArray();

        private void ZeroInit(Store s)
        {
            if (s is NumStore n) Op(Opcode.Mov, n, false, ImmUint(0));
            else foreach (var p in ((AggStore)s).Parts) ZeroInit(p);
        }

        /// <summary>Whether a call can write its argument i: a function's out parameter, an intrinsic's.</summary>
        private bool WritesArgument(CallExpr c, int i)
        {
            if (c.Constructor != null) return false;
            if (_functions.TryGetValue(c.Name, out var list))
                return list.Any(f => f.Params.Count == c.Args.Count && f.Params[i].IsOut);
            return c.Name switch
            {
                "sincos" => i >= 1, "modf" or "frexp" => i == 1, "asuint" => i >= 1 && c.Args.Count == 3,
                _ when c.Name.StartsWith("Interlocked", StringComparison.Ordinal) => i == 0 || i == c.Args.Count - 1,
                _ => false,
            };
        }

        private bool AssignsTo(Stmt s, string name)
        {
            bool found = false;
            void E(Expr? e)
            {
                switch (e)
                {
                    case null: return;
                    case AssignExpr a: if (RootName(a.Target) == name) found = true; E(a.Target); E(a.Value); return;
                    case UnaryExpr u: if (u.Op is "++" or "--" && RootName(u.Operand) == name) found = true; E(u.Operand); return;
                    case BinaryExpr b: E(b.Left); E(b.Right); return;
                    case ConditionalExpr c: E(c.Cond); E(c.Then); E(c.Else); return;
                    case CommaExpr c: foreach (var x in c.Items) E(x); return;
                    case CallExpr c:
                        for (int i = 0; i < c.Args.Count; i++) { if (RootName(c.Args[i]) == name && WritesArgument(c, i)) found = true; E(c.Args[i]); }
                        return;
                    case MethodExpr m: E(m.Target); foreach (var x in m.Args) { if (RootName(x) == name) found = true; E(x); } return;
                    case MemberExpr m: E(m.Target); return;
                    case IndexExpr i: E(i.Target); E(i.Index); return;
                    case CastExpr c: E(c.Value); return;
                    case InitListExpr l: foreach (var x in l.Items) E(x); return;
                }
            }
            void S(Stmt? st)
            {
                switch (st)
                {
                    case null: return;
                    case BlockStmt b: foreach (var x in b.Body) S(x); return;
                    case DeclStmt d: foreach (var v in d.Vars) E(v.Init); return;
                    case ExprStmt e: E(e.Expr); return;
                    case IfStmt i: E(i.Cond); S(i.Then); S(i.Else); return;
                    case ForStmt f: S(f.Init); E(f.Cond); E(f.Step); S(f.Body); return;
                    case WhileStmt w: E(w.Cond); S(w.Body); return;
                    case SwitchStmt sw: E(sw.Value); foreach (var c in sw.Cases) foreach (var x in c.Body) S(x); return;
                    case JumpStmt j: E(j.Value); return;
                }
            }
            S(s);
            return found;
        }

        private static string? RootName(Expr e) => e switch
        {
            NameExpr n => n.Name, MemberExpr m => RootName(m.Target), IndexExpr i => RootName(i.Target), _ => null,
        };

        // ---- statements --------------------------------------------------------------------------------

        private void Block(BlockStmt b, bool newScope = true)
        {
            if (newScope) _scopes.Add(new Dictionary<string, object>());
            foreach (var s in b.Body)
            {
                Statement(s);
                if (_terminated) break;                // flattened: what follows a return is not reached
            }
            if (newScope) _scopes.RemoveAt(_scopes.Count - 1);
        }

        private readonly List<(Stmt Loop, Expr? Step, Expr? Cond, bool DoWhile)> _loops = new();

        private void Statement(Stmt s)
        {
            switch (s)
            {
                case BlockStmt b: Block(b); return;
                case StructStmt ss:
                {
                    var st = new StructType(ss.Struct.Name);
                    _structs[ss.Struct.Name] = st;
                    foreach (var f in ss.Struct.Fields) st.Fields.Add(new StructField { Name = f.Name, Type = Resolve(f.Type, f.ArrayDims), Semantic = f.Semantic, Modifiers = f.Modifiers });
                    return;
                }
                case DeclStmt d:
                    foreach (var v in d.Vars)
                    {
                        var type = Resolve(v.Type, v.ArrayDims);
                        if (type is ArrayType { Length: 0 } && v.Init is InitListExpr il)
                            type = new ArrayType(((ArrayType)type).Element, Flatten(il).Count() / Math.Max(1, ((ArrayType)type).Element.Components));
                        if (v.Modifiers.Contains("const") && v.Modifiers.Contains("static") && v.Init != null && TryConstant(v, type) is { } k)
                        {
                            _scopes[^1][v.Name] = k;
                            continue;
                        }
                        if (Base(type) is ObjectType)
                        {
                            // a local resource variable: an alias of the one it is initialized with
                            if (v.Init == null) throw Err(v, "a local resource must be initialized");
                            _scopes[^1][v.Name] = EvalValue(v.Init);
                            continue;
                        }
                        var store = AllocLocal(type);
                        if (v.Init != null) Assign(store, Convert(Eval(v.Init, type), type, v.Init), v);
                        _scopes[^1][v.Name] = store;
                    }
                    return;
                case ExprStmt e: if (e.Expr != null) EvalValue(e.Expr, discard: true); return;
                case IfStmt i when _flatten:
                    FlatIf(i);
                    return;
                case IfStmt i:
                {
                    var c = Condition(i.Cond);
                    _w.Emit(Opcode.If, 1u << 7, c.Operand());           // if_nz
                    Statement(i.Then);
                    if (i.Else != null) { _w.Emit(Opcode.Else, 0); Statement(i.Else); }
                    _w.Emit(Opcode.EndIf, 0);
                    return;
                }
                case ForStmt f when Unrollable(f, out string counter, out var values, out bool unsigned):
                    Unrolled(f, counter, values, unsigned);
                    return;
                case ForStmt f when _flatten:
                    throw Err(f, "a loop without a constant trip count (or with break or continue) needs shader model 3", 3511);
                case WhileStmt w when _flatten:
                    throw Err(w, "a while loop needs shader model 3", 3511);
                case SwitchStmt sw when _legacy:
                    throw Err(sw, "switch needs shader model 4", 3511);
                case ForStmt f:
                {
                    _scopes.Add(new Dictionary<string, object>());
                    if (f.Init != null) Statement(f.Init);
                    _w.Emit(Opcode.Loop, 0);
                    _loops.Add((f, f.Step, null, false));
                    if (_frames.Count > 0) Current.Nesting++;
                    if (f.Cond != null) { var c = Condition(f.Cond); _w.Emit(Opcode.Breakc, 0, c.Operand()); }      // breakc_z
                    Statement(f.Body);
                    if (f.Step != null) EvalValue(f.Step, discard: true);
                    _w.Emit(Opcode.EndLoop, 0);
                    EndNested();
                    _scopes.RemoveAt(_scopes.Count - 1);
                    return;
                }
                case WhileStmt w:
                {
                    _w.Emit(Opcode.Loop, 0);
                    _loops.Add((w, null, w.DoWhile ? w.Cond : null, w.DoWhile));
                    if (_frames.Count > 0) Current.Nesting++;
                    if (!w.DoWhile) { var c = Condition(w.Cond); _w.Emit(Opcode.Breakc, 0, c.Operand()); }
                    Statement(w.Body);
                    if (w.DoWhile) { var c = Condition(w.Cond); _w.Emit(Opcode.Breakc, 0, c.Operand()); }
                    _w.Emit(Opcode.EndLoop, 0);
                    EndNested();
                    return;
                }
                case SwitchStmt sw:
                {
                    var v = Convert(Eval(sw.Value, null), NumericType.Int, sw.Value);
                    _w.Emit(Opcode.Switch, 0, v.Component(0).Operand());
                    _loops.Add((sw, null, null, false));
                    if (_frames.Count > 0) Current.Nesting++;
                    foreach (var (labels, body) in sw.Cases)
                    {
                        foreach (var l in labels)
                        {
                            if (l == null) _w.Emit(Opcode.Default, 0);
                            else _w.Emit(Opcode.Case, 0, DxOperand.Imm((uint)ConstInt(l)));
                        }
                        _scopes.Add(new Dictionary<string, object>());
                        foreach (var st in body) Statement(st);
                        _scopes.RemoveAt(_scopes.Count - 1);
                    }
                    _w.Emit(Opcode.EndSwitch, 0);
                    EndNested();
                    return;
                }
                case JumpStmt j:
                    Jump(j);
                    return;
            }
            throw Err(s, $"unsupported statement {s.GetType().Name}");
        }

        /// <summary>A loop or switch closed: the loop list, and an inlined function's early return breaks out further.</summary>
        private void EndNested()
        {
            _loops.RemoveAt(_loops.Count - 1);
            if (_frames.Count == 0) return;
            var f = Current;
            f.Nesting--;
            if (f.Returned != null && (!f.IsEntry || _legacy) && f.Nesting >= 0 && _loops.Count > 0)
                _w.Emit(Opcode.Breakc, 1u << 7, Read(f.Returned).Operand());      // breakc_nz returned
        }

        private void Jump(JumpStmt j)
        {
            switch (j.Kind)
            {
                case "break" or "continue" when _flatten: throw Err(j, $"{j.Kind} needs shader model 3", 3511);
                case "continue" when _legacy: throw Err(j, "continue needs shader model 4", 3511);
                case "break": _w.Emit(Opcode.Break, 0); return;
                case "continue":
                {
                    if (_loops.Count == 0) throw Err(j, "continue outside a loop");
                    var (loop, step, cond, doWhile) = _loops.LastOrDefault(l => l.Loop is ForStmt or WhileStmt);
                    if (step != null) EvalValue(step, discard: true);
                    if (doWhile && cond != null) { var c = Condition(cond); _w.Emit(Opcode.Breakc, 0, c.Operand()); }
                    _w.Emit(Opcode.Continue, 0);
                    return;
                }
                case "discard":
                    if (_stage != ShaderStage.Pixel) throw Err(j, "discard is only valid in a pixel shader");
                    if (_legacy) _w.Emit(Opcode.Discard, 1u << 7, (_pred?.Component(0) ?? ImmFloat(1)).Operand());
                    else _w.Emit(Opcode.Discard, 1u << 7, DxOperand.Imm(0xFFFFFFFF));
                    return;
                case "return" when _flatten:
                    FlatReturn(j);
                    return;
                case "return":
                {
                    var f = Current;
                    if (f.IsEntry)
                    {
                        if (j.Value != null && _entryReturn != null) Assign(_entryReturn, Convert(Eval(j.Value, _entryReturn.Type), _entryReturn.Type, j.Value), j);
                        if (f.Returned != null)
                        {
                            // shader model 3: out of the loop around the body (the outputs are copied after it)
                            Op(Opcode.Mov, f.Returned, false, ImmUint(0xFFFFFFFF));
                            _w.Emit(Opcode.Break, 0);
                        }
                        else _w.Emit(Opcode.Ret, 0);
                        return;
                    }
                    if (j.Value != null && f.Result != null) Assign(f.Result, Convert(Eval(j.Value, f.Result.Type), f.Result.Type, j.Value), j);
                    if (f.Returned != null)
                    {
                        Op(Opcode.Mov, f.Returned, false, ImmUint(0xFFFFFFFF));
                        _w.Emit(Opcode.Break, 0);
                    }
                    return;
                }
            }
        }

        /// <summary>A condition: a bool scalar in a register (0 or ~0).</summary>
        private Num Condition(Expr e)
        {
            var v = Eval(e, NumericType.Bool);
            var b = Convert(v, NumericType.Bool, e);
            return b.Component(0);
        }

        // ---- the container ------------------------------------------------------------------------------

        private byte[] Container(FunctionDecl entry)
        {
            // the declarations, then the program
            var body = _w.Code.ToList();
            _w.Code.Clear();
            Declarations(entry);
            _w.Code.AddRange(body);
            var chunks = new List<(string, byte[])>
            {
                ("RDEF", Rdef()),
                ("ISGN", Signature(_inputs, false)),
            };
            if (_stage == ShaderStage.Domain) chunks.Add(("PCSG", Signature(_patchConstants, false)));
            chunks.AddRange(new List<(string, byte[])>
            {
                (Sm5 && _stage == ShaderStage.Geometry ? "OSG5" : "OSGN", Signature(_outputs, true, Sm5 && _stage == ShaderStage.Geometry)),
                (Sm5 ? "SHEX" : "SHDR", _w.Program()),
                ("STAT", Stat()),
            });
            return DxbcWriter.Container(chunks);
        }

        private static byte[] Signature(List<SigEntry> list, bool output, bool withStream = false)
        {
            var els = list.Select(e => new DxbcWriter.SigElement
            {
                Name = e.Semantic, Index = e.Index,
                SystemValue = e.SystemValue >= 64 ? 0 : e.SystemValue,      // SV_Target, SV_Depth, SV_Coverage: none, in the chunk
                ComponentType = e.Type.Scalar switch { ScalarKind.Int => 2, ScalarKind.Uint or ScalarKind.Bool => 1, _ => 3 },
                Register = e.Register < 0 ? -1 : e.Register,
                Mask = e.Mask,
                ReadWriteMask = output ? 0 : e.Mask,
                Stream = e.Stream,
            }).ToList();
            return DxbcWriter.Signature(els, withStream);
        }

        private byte[] Stat()
        {
            var st = new uint[Sm5 ? 37 : 29];
            st[0] = (uint)_w.InstructionCount;
            st[1] = (uint)_temps;
            st[3] = (uint)_inputs.Count;
            st[4] = (uint)_outputs.Count;
            var b = new byte[st.Length * 4];
            Buffer.BlockCopy(st, 0, b, 0, b.Length);
            return b;
        }

        private void Declarations(FunctionDecl entry)
        {
            if (_stage == ShaderStage.Domain)
            {
                _w.Declare(Opcode.DclInputControlPointCount, (uint)_inputCps);
                _w.Declare(Opcode.DclTessDomain, _domain switch { "isoline" => 1u, "tri" => 2u, _ => 3u });
            }
            DeclareGlobals();
            if (_stage == ShaderStage.Domain) DomainDeclarations();
            else DeclareInputs();
            if (_readsCoverage) _w.Emit(Opcode.DclInput, 0, DxOperand.Bare(OperandType.InputCoverageMask));
            if (_stage != ShaderStage.Geometry) DeclareOutputs(_outputs);
            if (_stage == ShaderStage.Compute) ComputeDeclarations();
            if (_temps > 0) _w.Declare(Opcode.DclTemps, 0, (uint)_temps);
            if (_stage == ShaderStage.Geometry) GeometryDeclarations();
            foreach (var (x, size, comps) in _indexables) _w.Declare(Opcode.DclIndexableTemp, 0, (uint)x, (uint)size, (uint)comps);
        }

        /// <summary>The shader's declarations ahead of its stage's: flags, constant buffers, resources.</summary>
        private void DeclareGlobals()
        {
            _w.Declare(Opcode.DclGlobalFlags, 1);           // refactoringAllowed
            if (_icb.Count > 0) _w.ImmediateConstantBuffer(_icb.ToArray());
            foreach (var cb in _cbuffers.Where(c => c.Slot >= 0 && c.Used && !c.Texture).OrderBy(c => c.Slot))
            {
                var o = DxOperand.Src(OperandType.ConstantBuffer, new byte[] { 0, 1, 2, 3 }, cb.Slot, Math.Max(1, (cb.SizeBytes + 15) / 16));
                _w.Emit(Opcode.DclConstantBuffer, cb.DynamicallyIndexed ? 1u : 0u, o);
            }
            foreach (var r in _resources.Where(r => r.Used && r.Slot >= 0).OrderBy(r => r.Kind).ThenBy(r => r.Slot))
            {
                if (r.Kind == 's' || r.Legacy)
                {
                    uint mode = r.Type.IsComparisonSampler ? 1u : 0u;
                    _w.Emit(Opcode.DclSampler, mode, DxOperand.Bare(OperandType.Sampler, r.Slot));
                }
                if (r.Kind == 't' || r.Legacy) DeclareResource(r);
                if (r.Kind == 'u') DeclareUav(r);
            }
            // a tbuffer: a buffer of float4s at its t# (read with ld)
            foreach (var cb in _cbuffers.Where(c => c.Texture && c.Used && c.Slot >= 0))
                _w.EmitWith(Opcode.DclResource, 1u, null, new[] { DxOperand.Bare(OperandType.Resource, cb.Slot) }, new[] { 0x5555u });
        }

        private void DeclareInputs()
        {
            foreach (var e in _inputs)
            {
                int mask = e.Mask;
                var o = _stage == ShaderStage.Geometry ? DxOperand.Dest(OperandType.Input, mask, _gsVertices, e.Register) : DxOperand.Dest(OperandType.Input, mask, e.Register);
                if (_stage == ShaderStage.Pixel)
                {
                    uint interp = Interpolation(e);
                    if (e.SystemValue == 0) _w.Emit(Opcode.DclInputPs, interp, o);
                    else if (e.SystemValue is 9 or 7 or 10) _w.EmitWith(Opcode.DclInputPsSgv, interp, null, new[] { o }, new[] { (uint)e.SystemValue });
                    else _w.EmitWith(Opcode.DclInputPsSiv, interp, null, new[] { o }, new[] { (uint)e.SystemValue });
                }
                else if (e.SystemValue is 6 or 8 or 7) _w.EmitWith(Opcode.DclInputSgv, 0, null, new[] { o }, new[] { (uint)e.SystemValue });
                else if (e.SystemValue > 0) _w.EmitWith(Opcode.DclInputSiv, 0, null, new[] { o }, new[] { (uint)e.SystemValue });
                else _w.Emit(Opcode.DclInput, 0, o);
            }
            DeclareSpecialInputs();
        }

        private void DeclareOutputs(IEnumerable<SigEntry> outputs)
        {
            foreach (var e in outputs)
            {
                int mask = e.Mask;
                if (e.SystemValue > 64)
                {
                    var file = e.SystemValue switch { 65 => OperandType.OutputDepth, 66 => OperandType.OutputCoverageMask, 67 => OperandType.OutputDepthGreaterEqual, _ => OperandType.OutputDepthLessEqual };
                    _w.Emit(Opcode.DclOutput, 0, DxOperand.Bare(file));
                    continue;
                }
                var o = DxOperand.Dest(OperandType.Output, mask, e.Register);
                if (e.SystemValue is > 0 and < 64) _w.EmitWith(Opcode.DclOutputSiv, 0, null, new[] { o }, new[] { (uint)e.SystemValue });
                else _w.Emit(Opcode.DclOutput, 0, o);
            }
        }

        private bool Packs(SigEntry a, SigEntry b)
        {
            if (_stage == ShaderStage.Vertex && !b.Output) return false;
            if (_legacy) return false;               // never packed before shader model 4
            if (a.InArray || b.InArray) return false;
            // a tessellation factor takes a register of its own (what follows may share it)
            if (IsTessFactor(b.SystemValue)) return false;
            if (a.SystemValue is 1 or 9 or 10 || b.SystemValue is 1 or 9 or 10) return false;
            bool distA = a.SystemValue is 2 or 3, distB = b.SystemValue is 2 or 3;
            if (distA != distB) return false;
            if ((a.SystemValue is 4 or 5) || (b.SystemValue is 4 or 5)) return false;
            if (b.Output && _stage == ShaderStage.Pixel) return false;
            return Interpolation(a) == Interpolation(b);
        }

        private static uint Interpolation(SigEntry e)
        {
            if (e.SystemValue == 1) return 4;                      // linear noperspective
            if (e.Type.IsIntLike || e.Modifiers.Contains("nointerpolation")) return 1;
            bool noPersp = e.Modifiers.Contains("noperspective");
            if (e.Modifiers.Contains("sample")) return noPersp ? 7u : 6u;
            if (e.Modifiers.Contains("centroid")) return noPersp ? 5u : 3u;
            return noPersp ? 4u : 2u;
        }

        private static int Dimension(ObjectType t) => t.Name switch
        {
            "Buffer" or "RWBuffer" => 1, "Texture1D" or "RWTexture1D" or "sampler1D" or "texture1D" => 2,
            "Texture2D" or "RWTexture2D" or "sampler2D" or "sampler" or "texture" or "texture2D" => 3,
            "Texture2DMS" => 4, "Texture3D" or "RWTexture3D" or "sampler3D" or "texture3D" => 5,
            "TextureCube" or "samplerCUBE" or "textureCUBE" => 6, "Texture1DArray" or "RWTexture1DArray" => 7,
            "Texture2DArray" or "RWTexture2DArray" => 8, "Texture2DMSArray" => 9, "TextureCubeArray" => 10,
            _ => 3,
        };

        /// <summary>D3D10_SB_RESOURCE_RETURN_TYPE, four per component: 5 float, 3 sint, 4 uint.</summary>
        private static uint ReturnType(ObjectType t)
        {
            uint k = (t.Element as NumericType)?.Scalar switch { ScalarKind.Int => 3u, ScalarKind.Uint or ScalarKind.Bool => 4u, _ => 5u };
            return k | k << 4 | k << 8 | k << 12;
        }

        private void DeclareResource(Resource r)
        {
            var t = r.Type;
            if (t.Name is "StructuredBuffer")
            {
                _w.EmitWith(Opcode.DclResourceStructured, 0, null, new[] { DxOperand.Bare(OperandType.Resource, r.Slot) }, new[] { (uint)(t.Element == null ? 4 : SizeOfStructured(t.Element)) });
                return;
            }
            if (t.Name is "ByteAddressBuffer")
            {
                _w.Emit(Opcode.DclResourceRaw, 0, DxOperand.Bare(OperandType.Resource, r.Slot));
                return;
            }
            uint control = (uint)Dimension(t) | (t.Name.Contains("MS") ? (uint)t.Count << 5 : 0);
            _w.EmitWith(Opcode.DclResource, control, null, new[] { DxOperand.Bare(OperandType.Resource, r.Slot) }, new[] { ReturnType(t) });
        }

        private void DeclareUav(Resource r)
        {
            var t = r.Type;
            if (t.Name is "RWStructuredBuffer" or "AppendStructuredBuffer" or "ConsumeStructuredBuffer")
            {
                uint counter = t.Name == "RWStructuredBuffer" && !r.Counter ? 0u : 1u << 12;
                _w.EmitWith(Opcode.DclUnorderedAccessViewStructured, counter, null, new[] { DxOperand.Bare(OperandType.UnorderedAccessView, r.Slot) }, new[] { (uint)(t.Element == null ? 4 : SizeOfStructured(t.Element)) });
                return;
            }
            if (t.Name == "RWByteAddressBuffer")
            {
                _w.Emit(Opcode.DclUnorderedAccessViewRaw, 0, DxOperand.Bare(OperandType.UnorderedAccessView, r.Slot));
                return;
            }
            _w.EmitWith(Opcode.DclUnorderedAccessViewTyped, (uint)Dimension(t), null, new[] { DxOperand.Bare(OperandType.UnorderedAccessView, r.Slot) }, new[] { ReturnType(t) });
        }

        /// <summary>A structured buffer element's size: tightly packed, as structured buffers are.</summary>
        internal static int SizeOfStructured(HlslType t) => t switch
        {
            NumericType n => n.Components * 4,
            ArrayType a => Math.Max(1, a.Length) * SizeOfStructured(a.Element),
            StructType s => s.Fields.Sum(f => SizeOfStructured(f.Type)),
            _ => 4,
        };
    }
}
