// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Translates a decoded DXBC shader (shader model 4.0 through 5.1) to WGSL
// (Documentation/d3d11-webgpu-shim.md, sections 6 and 9). The translator walks the instruction list
// once, keeps D3D's register files as WGSL variables, and emits one statement per instruction.
//
// This file is the core: the walk, declarations, operands and stores. The other parts are
//   DxbcTranslator.Alu.cs        arithmetic, integer, bit, conversion and double instructions, flow
//   DxbcTranslator.Resources.cs  texture instructions of every dimension
//   DxbcTranslator.Uav.cs        UAVs, raw/structured buffers, group-shared memory, atomics, barriers
//   DxbcTranslator.Stages.cs     the module of each stage: VS, PS, CS (and the compute forms of GS/HS/DS)
//
// THE REGISTER MODEL. D3D registers are typeless 32-bit x 4. Every register file is a vec4<f32>; an
// integer instruction reinterprets its sources with bitcast and writes its result back as bits, so a
// value that went through integer arithmetic keeps its exact bit pattern.
//
// LINKING. D3D matches a vertex shader's outputs to a pixel shader's inputs by semantic, not by
// register, and two semantics may share a register (TEXCOORD0 in .xy, TEXCOORD1 in .zw). WGSL
// matches by location. So a pair is linked here: each pixel-shader input element (not a system
// value) is location k, in the pixel shader's own order; the vertex shader writes, for each, the
// components of its own element of that semantic, from x up; the pixel shader reads location k back
// into its own register's components.
//
// BINDINGS. A stage's resources are one bind group (group = stage slot): constant buffer b# at
// binding #, sampler s# at 16 + #, shader resource t# at 32 + #, UAV u# at 48 + #, its counter at 64 + #.
//
// What WGSL cannot express throws NotSupportedException naming it (or, where the specification
// leaves room, emits a defined approximation and logs it once); the caller logs it and the draw is
// skipped, the way a real device rejects a shader it cannot run. Nothing is translated silently wrong.
//

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Microsoft.Wpf.Interop.WebGpu.Direct3D.Dxbc
{
    internal sealed class ShaderBindings
    {
        /// <summary>Constant buffer slot -> size in vec4s.</summary>
        public readonly SortedDictionary<int, int> ConstantBuffers = new();
        /// <summary>Sampler slot -> comparison sampler.</summary>
        public readonly SortedDictionary<int, bool> Samplers = new();
        /// <summary>Resource slot -> (dimension, return kind 'f'/'i'/'u', is depth-compared).</summary>
        public readonly SortedDictionary<int, (int Dim, char Kind, bool Depth)> Resources = new();
        /// <summary>Raw and structured shader resources (read-only storage buffers at 32 + slot).</summary>
        public readonly SortedSet<int> StorageResources = new();
        /// <summary>UAV slot -> (kind 't' typed / 'r' raw / 's' structured, dimension, return kind, stride).</summary>
        public readonly SortedDictionary<int, (char Kind, int Dim, char Ret, int Stride)> Uavs = new();
        /// <summary>UAVs whose hidden counter the shader uses (binding 64 + slot).</summary>
        public readonly SortedSet<int> Counters = new();
        /// <summary>Typed UAV textures the shader reads (read_write); the others are write-only.</summary>
        public readonly SortedSet<int> UavReads = new();
        /// <summary>Typed UAV textures used atomically: bound as their R32 mirror buffer (DxbcTranslator.Uav.cs).</summary>
        public readonly SortedSet<int> MirroredUavs = new();
        /// <summary>The vertex shader reads SV_VertexID or SV_InstanceID: the draw's base vertex and
        /// start instance at binding <see cref="DxbcTranslator.DrawParamsBinding"/> (WebGPU's indices
        /// include them, D3D's do not).</summary>
        public bool DrawParams;
    }

    internal sealed class LinkedElement
    {
        public string Semantic = "";
        public int Index;
        public int Location;
        public int ComponentType = 3;
        public int ComponentCount = 4;
        /// <summary>D3D10_SB_INTERPOLATION_MODE of the pixel shader's input: 1 constant, 2 linear,
        /// 3 linear centroid, 4 noperspective, 5 noperspective centroid, 6 linear sample,
        /// 7 noperspective sample.</summary>
        public int Interpolation = 2;
    }

    internal sealed partial class DxbcTranslator
    {
        public const int SamplerBase = 16, ResourceBase = 32;
        /// <summary>A uniform vec4&lt;u32&gt; of the draw (base vertex, start instance): the binding no
        /// constant buffer takes (D3D has b0..b13).</summary>
        public const int DrawParamsBinding = 15;

        private readonly DxbcShader _s;
        private readonly int _group;
        private readonly IReadOnlyList<LinkedElement> _link;
        private StringBuilder _body = new();          // a hull shader moves it to each phase's own
        private readonly ShaderBindings _bindings = new();
        private int _indent = 1;
        private int _temps;
        private readonly Dictionary<int, (int Size, int Comps)> _indexable = new();
        private readonly HashSet<int> _outputRegs = new();
        private readonly HashSet<int> _inputRegs = new();
        private readonly Dictionary<int, int> _inputSv = new();     // register -> system value
        private readonly Dictionary<int, int> _outputSv = new();
        private readonly Dictionary<int, int> _inputInterpolation = new();
        private bool _writesDepth;
        private readonly Dictionary<int, char> _resourceKind = new();
        private readonly Dictionary<int, int> _resourceDim = new();
        private readonly HashSet<int> _comparisonResources = new();
        private readonly Dictionary<int, bool> _samplerComparison = new();
        private readonly HashSet<string> _helpers = new();
        private readonly HashSet<string> _builtins = new();          // system registers the body reads (vThreadID, vCoverage, ...)
        private int _tmp;

        public string Wgsl { get; private set; } = "";
        public ShaderBindings Bindings => _bindings;
        /// <summary>Approximations this translation made where WGSL has no exact form (logged once by the caller).</summary>
        public readonly List<string> Approximations = new();

        private DxbcTranslator(DxbcShader s, int group, IReadOnlyList<LinkedElement> link, TranslationOptions? options)
        {
            _s = s;
            _group = group;
            _link = link;
            if (options != null) _options = options;
        }

        /// <summary>The linked element that carries cull distances to the pixel shader (no semantic of
        /// D3D's can be spelled so).</summary>
        public const string CullSemantic = "\u0001CULL";

        /// <summary>WebGPU's default maxInterStageShaderVariables.</summary>
        public const int MaxInterStageVariables = 16;

        /// <summary>The pixel shader's linkage: one location per non-system-value input element (and
        /// per clip or cull distance it reads), then -- when <paramref name="rasterized"/>, the stage
        /// before the rasterizer, writes SV_CullDistance -- the elements that cull with them.</summary>
        public static List<LinkedElement> LinkageOf(DxbcShader? pixelShader, DxbcShader? rasterized = null, int stream = 0)
        {
            var list = new List<LinkedElement>();
            if (pixelShader == null) return list;
            // the interpolation of each input register, from its declaration
            var modes = new Dictionary<int, int>();
            foreach (var ins in pixelShader.Instructions)
                if (ins.Op is Opcode.DclInputPs or Opcode.DclInputPsSiv or Opcode.DclInputPsSgv && ins.Operands.Length > 0)
                    modes[ins.Operands[0].Index0] = ins.Interpolation;
            foreach (var e in pixelShader.Inputs.OrderBy(e => e.Register).ThenBy(e => e.Mask))
            {
                // a clip or cull distance the pixel shader reads is linked like any value
                if (e.SystemValue is not (0 or SvClipDistance or SvCullDistance)) continue;
                if (list.Any(l => l.Semantic == e.SemanticName.ToUpperInvariant() && l.Index == e.SemanticIndex)) continue;
                list.Add(new LinkedElement
                {
                    Semantic = e.SemanticName.ToUpperInvariant(), Index = e.SemanticIndex, Location = list.Count,
                    ComponentType = e.ComponentType, ComponentCount = 4,
                    Interpolation = modes.TryGetValue(e.Register, out int m) ? m : (e.ComponentType == 3 ? 2 : 1),
                });
            }
            // SV_CullDistance: WGSL has no cull distances. D3D culls a primitive when, for some
            // distance, every vertex's is negative. Each vertex sends, per distance, 0 when its own is
            // negative and 1 when not; interpolated over the primitive that is exactly 0 everywhere
            // when every vertex sent 0 (a sum of zeros), and above 0 inside it otherwise (centroid
            // interpolation keeps it inside), so the pixel shader discards where it is 0.
            int cull = rasterized == null ? 0 : CullComponents(rasterized, stream).Count;
            int cullElements = (cull + 3) / 4;
            if (cullElements > 0 && list.Count + cullElements <= MaxInterStageVariables)
                for (int k = 0; k < cullElements; k++)
                    list.Add(new LinkedElement { Semantic = CullSemantic, Index = k, Location = list.Count, ComponentType = 3, ComponentCount = 4, Interpolation = 5 });
            return list;
        }

        /// <summary>The stage's cull-distance components of <paramref name="stream"/>, in register order (register, component).</summary>
        internal static List<(int Register, int Component)> CullComponents(DxbcShader s, int stream = 0)
        {
            var list = new List<(int, int)>();
            foreach (var e in s.Outputs.Where(e => e.Stream == stream && e.SystemValue == SvCullDistance).OrderBy(e => e.Register).ThenBy(e => e.Mask))
                for (int c = 0; c < 4; c++) if ((e.Mask & (1 << c)) != 0) list.Add((e.Register, c));
            return list;
        }

        public static DxbcTranslator Translate(DxbcShader s, int group, IReadOnlyList<LinkedElement> link, TranslationOptions? options = null)
        {
            if (s.Stage is not (ShaderStage.Vertex or ShaderStage.Pixel or ShaderStage.Compute))
                throw new NotSupportedException($"{s.Stage} shaders are translated by the geometry/compute path");
            var t = new DxbcTranslator(s, group, link, options);
            t.Run();
            return t;
        }

        // ---- the walk ------------------------------------------------------------------------

        private void Run()
        {
            if (_s.Interfaces.Length > 0 || _s.Instructions.Any(i => i.Op is Opcode.DclInterface or Opcode.InterfaceCall))
                throw new NotSupportedException("class linkage (interfaces): each call site is specialized at bind time from the bound class instances -- not implemented yet");
            FindAtomics();
            foreach (var ins in _s.Instructions) { Declare(ins); DeclareMemory(ins); }
            foreach (var ins in _s.Instructions) Emit(ins);
            foreach (var (u, d) in _uavs) if (_usedUavs.Contains(u) || _atomicUavs.Contains(u)) _bindings.Uavs[u] = (d.Kind, d.Dim, d.Ret, d.Stride);
            foreach (int u in _usedCounters) _bindings.Counters.Add(u);
            foreach (int t in _usedRawSrvs) _bindings.StorageResources.Add(t);
            foreach (int u in _uavRead) _bindings.UavReads.Add(u);
            foreach (int u in MirroredUavs) _bindings.MirroredUavs.Add(u);
            Wgsl = Assemble();
        }

        private void Declare(Instruction ins)
        {
            if (!DeclareTessellation(ins))
            switch (ins.Op)
            {
                case Opcode.DclTemps: _temps = (int)ins.Extra[0]; break;
                case Opcode.DclIndexableTemp: _indexable[(int)ins.Extra[0]] = ((int)ins.Extra[1], (int)ins.Extra[2]); break;
                case Opcode.DclConstantBuffer:
                {
                    // a size of 0 is "any size" (the slot takes whatever buffer is bound)
                    int slot = ins.Register, size = ins.Count > 0 ? (int)ins.Count : 4096;
                    _bindings.ConstantBuffers[slot] = size;
                    break;
                }
                case Opcode.DclSampler:
                {
                    int mode = (int)((ins.Token >> 11) & 0xF);
                    _samplerComparison[ins.Register] = mode == 1;
                    break;
                }
                case Opcode.DclResource:
                {
                    int slot = ins.Register;
                    _resourceDim[slot] = ins.ResourceDimension;
                    int rt = (int)(ins.ReturnType & 0xF);
                    _resourceKind[slot] = rt switch { 3 => 'i', 4 => 'u', _ => 'f' };
                    break;
                }
                case Opcode.DclInput:
                case Opcode.DclInputPs:
                    if (_s.Stage == ShaderStage.Geometry && ins.Operands[0].Type == OperandType.Input && ins.Operands[0].Indices.Length == 2) { _gsInputRegs.Add(ins.Operands[0].Index1); break; }
                    if (IsRegisterFile(ins.Operands[0])) _inputRegs.Add(ins.Operands[0].Index0);
                    if (ins.Op == Opcode.DclInputPs) _inputInterpolation[ins.Operands[0].Index0] = ins.Interpolation;
                    break;
                case Opcode.DclInputSgv:
                case Opcode.DclInputSiv:
                case Opcode.DclInputPsSgv:
                case Opcode.DclInputPsSiv:
                    if (_s.Stage == ShaderStage.Geometry && ins.Operands[0].Indices.Length == 2) { _gsInputRegs.Add(ins.Operands[0].Index1); break; }
                    _inputRegs.Add(ins.Operands[0].Index0);
                    _inputSv[ins.Operands[0].Index0] = ins.SystemValue;
                    break;
                case Opcode.DclOutput:
                    if (ins.Operands[0].Type is OperandType.OutputDepth or OperandType.OutputDepthGreaterEqual or OperandType.OutputDepthLessEqual) _writesDepth = true;
                    else if (ins.Operands[0].Type == OperandType.OutputCoverageMask) _builtins.Add("oMask");
                    else _outputRegs.Add(ins.Operands[0].Index0);
                    break;
                case Opcode.DclOutputSgv:
                case Opcode.DclOutputSiv:
                    _outputRegs.Add(ins.Operands[0].Index0);
                    _outputSv[ins.Operands[0].Index0] = ins.SystemValue;
                    break;
                case Opcode.DclIndexRange:
                    break;                                 // the registers are declared one by one too
            }
            // Operands that reach registers the declarations did not name (fxc declares them all, but
            // be tolerant).
            foreach (var o in ins.Operands)
            {
                if (o.Type == OperandType.Temp && o.Indices.Length > 0) _temps = Math.Max(_temps, o.Index0 + 1);
                if (o.Type is OperandType.OutputDepth or OperandType.OutputDepthGreaterEqual or OperandType.OutputDepthLessEqual) _writesDepth = true;
            }
        }

        /// <summary>Inputs that are register files (v#), not system registers declared through dcl_input
        /// (vThreadID, vCoverage, vPrim, ...).</summary>
        private static bool IsRegisterFile(Operand o) => o.Type is OperandType.Input or OperandType.InputControlPoint or OperandType.InputPatchConstant;

        // ---- operand expressions -----------------------------------------------------------------

        private static string F(float v)
        {
            if (float.IsNaN(v) || float.IsInfinity(v)) return $"bitcast<f32>({BitConverter.SingleToUInt32Bits(v)}u)";
            string s = v.ToString("R", CultureInfo.InvariantCulture);
            if (!s.Contains('.') && !s.Contains('E') && !s.Contains('e')) s += ".0";
            return s + "f";
        }

        private static string Bits(uint u) => $"bitcast<f32>({u}u)";

        /// <summary>An immediate as a vec4&lt;f32&gt; of its bits.</summary>
        private static string ImmediateVec(Operand o)
        {
            var c = new string[4];
            for (int i = 0; i < 4; i++)
            {
                uint u = o.Values.Length == 1 ? o.Values[0] : o.Values[Math.Min(i, o.Values.Length - 1)];
                float f = BitConverter.UInt32BitsToSingle(u);
                // A value that reads as an ordinary float literal is written as one (readable WGSL);
                // anything else -- integers, NaN patterns, denormals -- as its bits.
                c[i] = (u == 0 || (Math.Abs(f) >= 1e-30f && Math.Abs(f) < 1e30f && !float.IsNaN(f))) && IsPlainFloat(u) ? F(f) : Bits(u);
            }
            return $"vec4<f32>({c[0]}, {c[1]}, {c[2]}, {c[3]})";
        }

        private static bool IsPlainFloat(uint u)
        {
            float f = BitConverter.UInt32BitsToSingle(u);
            return BitConverter.SingleToUInt32Bits(float.Parse(f.ToString("R", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture)) == u;
        }

        private string IndexExpr(OperandIndex ix)
        {
            if (ix.Relative == null) return $"{ix.Immediate}u";
            string rel = $"bitcast<u32>({Source(ix.Relative)}.x)";
            return ix.Immediate == 0 ? rel : $"({rel} + {ix.Immediate}u)";
        }

        /// <summary>The register an operand names, as a vec4&lt;f32&gt; lvalue/rvalue (no swizzle).</summary>
        private string Register(Operand o)
        {
            if (_s.Stage is ShaderStage.Hull or ShaderStage.Domain && TessellationRegister(o) is { } tess) return tess;
            switch (o.Type)
            {
                case OperandType.Temp: return $"r{o.Index0}";
                case OperandType.Input:
                    if (_form == StageForm.GeometryCompute) return GeometryInput(o);
                    if (o.Indices.Length == 1 && o.Indices[0].Relative == null) return $"v{o.Index0}";
                    if (o.Indices.Length == 1) { _indexedInputs = true; return $"dx_v[{IndexExpr(o.Indices[0])}]"; }
                    throw new NotSupportedException("two-dimensional input registers outside a geometry, hull or domain shader");
                case OperandType.Output:
                    if (o.Indices.Length == 1 && o.Indices[0].Relative == null) return $"o{o.Index0}";
                    _indexedOutputs = true;
                    return $"dx_o[{IndexExpr(o.Indices[0])}]";
                case OperandType.IndexableTemp: return $"x{o.Index0}[{IndexExpr(o.Indices[1])}]";
                case OperandType.ConstantBuffer:
                    if (o.Indices.Length > 2) throw new NotSupportedException("a dynamically indexed array of constant buffers (SM5.1)");
                    return $"cb{o.Index0}[{IndexExpr(o.Indices[1])}]";
                case OperandType.ImmediateConstantBuffer: return $"bitcast<vec4<f32>>(icb[{IndexExpr(o.Indices[0])}])";
                case OperandType.OutputDepth:
                case OperandType.OutputDepthGreaterEqual:
                case OperandType.OutputDepthLessEqual: return "oDepth";
                case OperandType.InputPrimitiveId: _builtins.Add("vPrim"); return "vPrim";
                case OperandType.InputThreadId: _builtins.Add("vThreadID"); return "vThreadID";
                case OperandType.InputThreadGroupId: _builtins.Add("vThreadGroupID"); return "vThreadGroupID";
                case OperandType.InputThreadIdInGroup: _builtins.Add("vThreadIDInGroup"); return "vThreadIDInGroup";
                case OperandType.InputThreadIdInGroupFlattened: _builtins.Add("vThreadIDInGroupFlattened"); return "vThreadIDInGroupFlattened";
                case OperandType.InputCoverageMask: _builtins.Add("vCoverage"); return "vCoverage";
                case OperandType.OutputCoverageMask: _builtins.Add("oMask"); return "oMask";
                case OperandType.InputGsInstanceId: _builtins.Add("vGSInstanceID"); return "vGSInstanceID";
                default: throw new NotSupportedException($"operand {o.Type}");
            }
        }

        private bool _indexedInputs, _indexedOutputs;

        private static string Swz(Operand o)
        {
            if (o.ComponentCount != 4 || o.IsMask) return o.ComponentCount == 1 ? ".xxxx" : "";
            var s = new char[4];
            for (int i = 0; i < 4; i++) s[i] = "xyzw"[o.Swizzle[i]];
            string sw = new string(s);
            return sw == "xyzw" ? "" : "." + sw;
        }

        /// <summary>A source operand as a vec4&lt;f32&gt; expression with its swizzle and modifier
        /// (the float modifier; integer instructions apply theirs through <see cref="ISource"/>).</summary>
        private string Source(Operand o)
        {
            string e;
            if (o.Type == OperandType.Immediate32) e = ImmediateVec(o);
            else if (o.Type == OperandType.Immediate64) e = ImmediateVec(new Operand { Type = OperandType.Immediate32, Values = o.Values, ComponentCount = 4 });
            else if (o.Type == OperandType.Null) e = "vec4<f32>(0.0f)";
            else if (o.ComponentCount == 1) e = Register(o) + ".xxxx";
            else e = Register(o) + Swz(o);
            return o.Modifier switch
            {
                OperandModifier.Neg => $"(-{e})",
                OperandModifier.Abs => $"abs({e})",
                OperandModifier.AbsNeg => $"(-abs({e}))",
                _ => e,
            };
        }

        /// <summary>A source operand as vec4&lt;i32&gt; (integer negation for the neg modifier).</summary>
        private string ISource(Operand o)
        {
            string e = $"bitcast<vec4<i32>>({Source(Strip(o))})";
            return o.Modifier switch
            {
                OperandModifier.Neg => $"(-{e})",
                OperandModifier.Abs => $"abs({e})",
                OperandModifier.AbsNeg => $"(-abs({e}))",
                _ => e,
            };
        }

        private string USource(Operand o) => $"bitcast<vec4<u32>>({ISource(o)})";

        private static Operand Strip(Operand o) => new() { Type = o.Type, ComponentCount = o.ComponentCount, Mask = o.Mask, Swizzle = o.Swizzle, IsMask = o.IsMask, Indices = o.Indices, Values = o.Values };

        // ---- statements ------------------------------------------------------------------------

        private void Line(string s)
        {
            _body.Append(' ', _indent * 4).Append(s).Append('\n');
        }

        /// <summary>dest = value (a vec4&lt;f32&gt; expression), honouring the write mask and _sat.</summary>
        private void Store(Operand dest, string value, bool saturate)
        {
            if (dest.Type == OperandType.Null) return;
            if (saturate) value = $"clamp({value}, vec4<f32>(0.0f), vec4<f32>(1.0f))";
            if (dest.Type is OperandType.OutputDepth or OperandType.OutputDepthGreaterEqual or OperandType.OutputDepthLessEqual) { Line($"oDepth = ({value}).x;"); return; }
            if (dest.Type == OperandType.OutputCoverageMask) { _builtins.Add("oMask"); Line($"oMask = bitcast<u32>(({value}).x);"); return; }
            string reg = Register(dest);
            int mask = dest.ComponentCount == 4 ? (dest.IsMask ? dest.Mask : 0xF) : 1;
            if (mask == 0xF) { Line($"{reg} = {value};"); return; }
            string t = $"_k{_tmp++}";
            Line($"let {t} = {value};");
            for (int c = 0; c < 4; c++)
                if ((mask & (1 << c)) != 0) Line($"{reg}.{"xyzw"[c]} = {t}.{"xyzw"[c]};");
        }

        private void StoreI(Operand dest, string ivec) => Store(dest, $"bitcast<vec4<f32>>({ivec})", false);

        // 0 or all ones per component. Not select(u32, u32, bool): naga's GLSL backend emitted that as a float mix and
        // then uintBitsToFloat(vec4), which no GLSL compiler takes (every compare failed on the GL backend).
        private static string Mask(string boolVec) => $"(vec4<u32>({boolVec}) * vec4<u32>(0xFFFFFFFFu))";

        private string Cond(Operand o, bool nonZero)
        {
            string c = $"bitcast<u32>({Source(Strip(o))}.x)";
            return nonZero ? $"({c} != 0u)" : $"({c} == 0u)";
        }

        private void Approximate(string what)
        {
            if (!Approximations.Contains(what)) Approximations.Add(what);
        }

        private void Emit(Instruction ins)
        {
            if (EmitPhase(ins)) return;
            BeforeInstruction(ins);
            var info = DxbcOpcodes.Info(ins.Op);
            if (info.Class == OpClass.Declaration || ins.Op is Opcode.Nop or Opcode.Label or Opcode.CustomData) return;
            if (EmitAlu(ins) || EmitFlow(ins)) return;
            switch (ins.Op)
            {
                case Opcode.Sample: case Opcode.SampleB: case Opcode.SampleL: case Opcode.SampleD:
                case Opcode.SampleC: case Opcode.SampleCLz:
                    EmitSample(ins); return;
                case Opcode.Ld: case Opcode.LdMs: EmitLoad(ins); return;
                case Opcode.ResInfo: EmitResInfo(ins); return;
                case Opcode.Lod: EmitLod(ins); return;
                case Opcode.Gather4: case Opcode.Gather4C: case Opcode.Gather4Po: case Opcode.Gather4PoC: EmitGather(ins); return;
                case Opcode.SampleInfo: EmitSampleInfo(ins); return;
                case Opcode.SamplePos: EmitSamplePos(ins); return;
                case Opcode.BufInfo: EmitBufInfo(ins); return;
                case Opcode.LdUavTyped: EmitUavTypedLoad(ins); return;
                case Opcode.StoreUavTyped: EmitUavTypedStore(ins); return;
                case Opcode.LdRaw: case Opcode.LdStructured: EmitWordLoad(ins); return;
                case Opcode.StoreRaw: case Opcode.StoreStructured: EmitWordStore(ins); return;
                case Opcode.Sync: EmitSync(ins); return;
                case Opcode.Emit: case Opcode.Cut: case Opcode.EmitThenCut:
                case Opcode.EmitStream: case Opcode.CutStream: case Opcode.EmitThenCutStream:
                    EmitGeometry(ins); return;
            }
            if (info.Class == OpClass.Atomic) { EmitAtomic(ins); return; }
            throw new NotSupportedException($"instruction {info.Name}");
        }

        private bool HasFeature(uint f) => (_options.Features ?? D3DDevice.Features).Contains(f);

        private readonly SortedSet<string> _enables = new();

        private string Assemble()
        {
            var body = new StringBuilder();
            AssembleBody(body);
            var w = new StringBuilder();
            foreach (string e in _enables) w.Append($"enable {e};\n");
            w.Append("diagnostic(off, derivative_uniformity);\n\n");
            w.Append(body);
            return w.ToString();
        }

        private void AssembleBody(StringBuilder w)
        {
            foreach (var (slot, size) in _bindings.ConstantBuffers)
                w.Append($"@group({_group}) @binding({slot}) var<uniform> cb{slot}: array<vec4<f32>, {size}>;\n");
            foreach (var (slot, cmp) in _bindings.Samplers)
                w.Append($"@group({_group}) @binding({SamplerBase + slot}) var s{slot}: {(cmp ? "sampler_comparison" : "sampler")};\n");
            foreach (var (slot, r) in _bindings.Resources)
                w.Append($"@group({_group}) @binding({ResourceBase + slot}) var{(r.Dim == DimBuffer ? "<storage, read>" : "")} t{slot}: {TextureType(r.Dim, r.Kind, r.Depth)};\n");
            AssembleMemory(w);
            if (_s.ImmediateConstantBuffer.Length > 0)
            {
                int n = _s.ImmediateConstantBuffer.Length / 4;
                // as bits: naga does not evaluate bitcast in a constant expression; reads bitcast them
                w.Append($"const icb = array<vec4<u32>, {n}>(");
                for (int i = 0; i < n; i++)
                {
                    if (i > 0) w.Append(", ");
                    var u = _s.ImmediateConstantBuffer;
                    w.Append($"vec4<u32>({u[i * 4]}u, {u[i * 4 + 1]}u, {u[i * 4 + 2]}u, {u[i * 4 + 3]}u)");
                }
                w.Append(");\n");
            }
            foreach (string h in _helpers) w.Append(h);
            w.Append('\n');

            // registers live at module scope (private), so helper functions (shader_out) see them
            for (int i = 0; i < _temps; i++) w.Append($"var<private> r{i}: vec4<f32>;\n");
            foreach (var (reg, (size, _)) in _indexable) w.Append($"var<private> x{reg}: array<vec4<f32>, {size}>;\n");
            int inputs = _inputRegs.Count == 0 ? 0 : _inputRegs.Max() + 1, outputs = _outputRegs.Count == 0 ? 0 : _outputRegs.Max() + 1;
            if (_indexedInputs) w.Append($"var<private> dx_v: array<vec4<f32>, {Math.Max(1, inputs)}>;\n");
            else foreach (int v in _inputRegs) w.Append($"var<private> v{v}: vec4<f32>;\n");
            if (_indexedOutputs) w.Append($"var<private> dx_o: array<vec4<f32>, {Math.Max(1, outputs)}>;\n");
            else foreach (int o in _outputRegs) w.Append($"var<private> o{o}: vec4<f32>;\n");
            if (_writesDepth) w.Append("var<private> oDepth: f32;\n");
            foreach (string b in _builtins) w.Append(b == "oMask" ? "var<private> oMask: u32 = 0xFFFFFFFFu;\n" : $"var<private> {b}: vec4<f32>;\n");
            w.Append('\n');

            switch (_s.Stage)
            {
                case ShaderStage.Vertex when _form == StageForm.VertexCompute: AssembleVertexCompute(w); break;
                case ShaderStage.Hull when _form == StageForm.HullCompute: AssembleHull(w); break;
                case ShaderStage.Domain when _form is StageForm.DomainVertex or StageForm.DomainCompute: AssembleDomain(w); break;
                case ShaderStage.Geometry when _form == StageForm.GeometryCompute: AssembleGeometryCompute(w); break;
                case ShaderStage.Vertex: AssembleVertex(w); break;
                case ShaderStage.Pixel: AssemblePixel(w); break;
                case ShaderStage.Compute: AssembleCompute(w); break;
                default: throw new NotSupportedException($"{_s.Stage} stage");
            }
        }

        /// <summary>The register an indexed input/output array keeps register k in (dx_v[k] / dx_o[k]),
        /// or the plain register.</summary>
        private string InReg(int r) => _indexedInputs ? $"dx_v[{r}]" : $"v{r}";
        private string OutReg(int r) => _indexedOutputs ? $"dx_o[{r}]" : $"o{r}";
    }
}
