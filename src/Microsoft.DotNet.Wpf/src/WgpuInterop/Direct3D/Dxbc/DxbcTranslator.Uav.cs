// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Unordered access, raw and structured buffers, group-shared memory, atomics and barriers in the
// DXBC translator (Documentation/d3d11-webgpu-shim.md, section 9).
//
// THE MEMORY MODEL.
//   - A raw or structured buffer (ByteAddressBuffer, StructuredBuffer, and their RW forms) is a
//     storage buffer of 32-bit words, addressed in bytes as D3D addresses it: raw at byte offset,
//     structured at index * stride + offset. A buffer any atomic touches is array<atomic<u32>>, and
//     every access to it goes through atomicLoad/atomicStore.
//   - A typed UAV buffer (RWBuffer<T>) is a storage buffer of the view format's elements; R32 formats
//     are one word each, the 128-bit ones four. Other formats are refused by name: the element's
//     layout is the view's, and the translation would have to be specialized on it.
//   - A typed UAV texture (RWTexture*) is a texture_storage_* of the view's format, which WGSL needs
//     in the type: the binder passes the bound views' formats in (TranslationOptions); without one it
//     is r32float/r32uint/r32sint by the declared return type -- the formats D3D11.0 itself guarantees
//     typed UAV loads for. Atomics on a typed UAV texture have no WGSL form: refused by name.
//   - A UAV's hidden counter (IncrementCounter, Append/Consume) is an atomic<u32> in a binding of its own.
//   - Group-shared memory is var<workgroup> of words (atomic<u32> when atomics touch it).
//
// BINDINGS (in the stage's group): UAV u# at 48 + #, its counter at 64 + #; raw and structured SRVs
// share t#'s binding (32 + #) as storage buffers.
//

using System;
using System.Collections.Generic;
using System.Linq;

namespace Microsoft.Wpf.Interop.WebGpu.Direct3D.Dxbc
{
    /// <summary>What a translation is specialized on, beyond the bytecode.</summary>
    internal sealed class TranslationOptions
    {
        /// <summary>The WGSL texel format of each bound typed UAV texture (u# -> "rgba8unorm", ...).</summary>
        public IReadOnlyDictionary<int, string>? UavFormats;
        /// <summary>The element size in words of each bound typed UAV buffer (1 for R32, 4 for 128-bit).</summary>
        public IReadOnlyDictionary<int, int>? UavBufferWords;
        /// <summary>The device's optional features (WGPUFeatureName / WGPUNativeFeature values): a
        /// shader takes a feature's form where the device has it. Null: the shim's shared device's.</summary>
        public IReadOnlyCollection<uint>? Features;
        /// <summary>Vertex-shader input registers whose element is B8G8R8A8: fetched as RGBA, swapped
        /// back in the shader.</summary>
        public IReadOnlyCollection<int>? BgraInputs;
        /// <summary>A geometry shader's stream-output declaration (CreateGeometryShaderWithStreamOutput).</summary>
        public StreamOutputDesc? StreamOutput;
        /// <summary>A geometry shader fed by an indexed strip: its primitives come from a table the
        /// ia_scan kernel builds, honouring strip-cut indices (all ones).</summary>
        public bool StripCut;
        /// <summary>A geometry shader after tessellation: primitive p's vertex k is dx_vin[p * V + k]
        /// (the tessellator's indices), from the domain shader's outputs.</summary>
        public bool IndexedInput;
    }

    internal sealed partial class DxbcTranslator
    {
        public const int UavBase = 48, CounterBase = 64;

        // u# -> (kind 'typed'/'raw'/'structured', dimension, return kind, stride)
        private readonly Dictionary<int, (char Kind, int Dim, char Ret, int Stride, bool Counter)> _uavs = new();
        private readonly HashSet<int> _atomicUavs = new();
        private readonly HashSet<int> _rawResources = new();
        private readonly Dictionary<int, int> _structuredResources = new();   // t# -> stride
        private readonly Dictionary<int, (int Words, int Stride, bool Atomic)> _tgsm = new();   // g# -> words, stride (0 raw)
        private readonly HashSet<int> _atomicTgsm = new();
        private readonly HashSet<int> _usedUavs = new();
        private readonly HashSet<int> _uavRead = new();
        private readonly HashSet<int> _usedCounters = new();
        private bool _usesRasterizerSamples, _usesSamplePositions;
        private int[] _threadGroup = { 1, 1, 1 };
        private TranslationOptions _options = new();

        // ---- typed UAV textures used atomically (WGSL has no texture atomics) ----------------------------
        //
        // Such a UAV is bound as a buffer of words, its MIRROR: a header of MirrorHeaderWords words
        // (width, height, depth or layers -- the view's, written by the device when it makes the
        // mirror), then the texels in rows MirrorPitch apart and slices one after the other, then one
        // word of scratch. The device copies the texture in before the dispatch and back after; only
        // a 32-bit format can be mirrored, which is also D3D's rule for typed UAV atomics.

        /// <summary>The words before a mirror's texels: 256 bytes, so every copy offset is aligned.</summary>
        public const uint MirrorHeaderWords = 64;

        /// <summary>The texels per row of a mirror: whole 256-byte rows, so the device copies between it
        /// and the texture with no repacking.</summary>
        public static uint MirrorPitch(uint width) => (width + 63) & ~63u;

        private bool Mirrored(int u) => _atomicUavs.Contains(u) && _uavs.TryGetValue(u, out var d) && d.Kind == 't' && d.Dim != DimBuffer;

        /// <summary>The word of a mirrored UAV texture's texel at <paramref name="c"/> (vec4&lt;u32&gt;),
        /// or 0xFFFFFFFF outside the view (D3D drops such accesses).</summary>
        private static string MirrorWord(int u, string c) => $"dx_mw{u}({c})";

        /// <summary>The mirror helpers of UAV <paramref name="u"/>: its view's size and a texel's word.</summary>
        private string MirrorHelpers(int u, int dim)
        {
            string y = dim is Dim1D or Dim1DArray ? "0u" : "c.y";
            string z = dim switch { Dim1DArray => "c.y", Dim2DArray or Dim3D => "c.z", _ => "0u" };
            return $"fn dx_msize{u}() -> vec3<u32> {{ return vec3<u32>(atomicLoad(&u{u}[0]), atomicLoad(&u{u}[1]), atomicLoad(&u{u}[2])); }}\n"
                + $"fn dx_mw{u}(c: vec4<u32>) -> u32 {{\n"
                + $"    let s = dx_msize{u}();\n"
                + $"    let p = (s.x + 63u) & ~63u;\n"
                + $"    let x = c.x; let y = {y}; let z = {z};\n"
                + $"    return select(0xFFFFFFFFu, {MirrorHeaderWords}u + x + (y + z * s.y) * p, x < s.x && y < s.y && z < s.z);\n"
                + "}\n";
        }

        /// <summary>The mirrored UAVs (their bindings are word buffers; the device copies the texture in and out).</summary>
        public IReadOnlyCollection<int> MirroredUavs => _uavs.Keys.Where(Mirrored).ToList();

        /// <summary>The bindings a translation reads and writes, for the device to build its layout.</summary>
        public IReadOnlyDictionary<int, (char Kind, int Dim, char Ret, int Stride, bool Counter)> Uavs => _uavs;
        public IReadOnlyCollection<int> UsedUavs => _usedUavs;
        public IReadOnlyCollection<int> UsedCounters => _usedCounters;
        public IReadOnlyCollection<int> RawResources => _rawResources;
        public IReadOnlyDictionary<int, int> StructuredResources => _structuredResources;
        public IReadOnlyList<int> ThreadGroup => _threadGroup;

        /// <summary>The atomics a shader uses, found before anything is emitted: an atomic makes its
        /// buffer array&lt;atomic&lt;u32&gt;&gt;, which changes how every access to it is written.</summary>
        private void FindAtomics()
        {
            foreach (var ins in _s.Instructions)
            {
                if (DxbcOpcodes.Info(ins.Op).Class != OpClass.Atomic || ins.Op is Opcode.ImmAtomicAlloc or Opcode.ImmAtomicConsume) continue;
                var target = ins.Op.ToString().StartsWith("Imm", StringComparison.Ordinal) ? ins.Operands[1] : ins.Operands[0];
                if (target.Type == OperandType.UnorderedAccessView) _atomicUavs.Add(target.Index0);
                else if (target.Type == OperandType.ThreadGroupSharedMemory) _atomicTgsm.Add(target.Index0);
            }
        }

        private void DeclareMemory(Instruction ins)
        {
            switch (ins.Op)
            {
                case Opcode.DclUnorderedAccessViewTyped:
                {
                    int rt = (int)(ins.ReturnType & 0xF);
                    _uavs[ins.Register] = ('t', ins.ResourceDimension, rt switch { 3 => 'i', 4 => 'u', _ => 'f' }, 0, false);
                    break;
                }
                case Opcode.DclUnorderedAccessViewRaw:
                    _uavs[ins.Register] = ('r', 0, 'u', 4, false);
                    break;
                case Opcode.DclUnorderedAccessViewStructured:
                    _uavs[ins.Register] = ('s', 0, 'u', (int)ins.Stride, ((ins.Control >> 12) & 1) != 0);   // bit 23: has a counter
                    break;
                case Opcode.DclResourceRaw:
                    _rawResources.Add(ins.Register);
                    break;
                case Opcode.DclResourceStructured:
                    _structuredResources[ins.Register] = (int)ins.Stride;
                    break;
                case Opcode.DclThreadGroupSharedMemoryRaw:
                    _tgsm[ins.Register] = ((int)(ins.Stride / 4), 0, false);
                    break;
                case Opcode.DclThreadGroupSharedMemoryStructured:
                    _tgsm[ins.Register] = ((int)(ins.Stride * ins.Count / 4), (int)ins.Stride, false);
                    break;
                case Opcode.DclThreadGroup:
                    _threadGroup = new[] { (int)ins.Extra[0], (int)ins.Extra[1], (int)ins.Extra[2] };
                    break;
            }
        }

        // ---- addressing -----------------------------------------------------------------------------

        /// <summary>The word array a raw/structured/tgsm operand names, and whether it is atomic.</summary>
        private (string Name, bool Atomic) WordArray(Operand o)
        {
            switch (o.Type)
            {
                case OperandType.UnorderedAccessView:
                {
                    int u = o.Index0;
                    if (!_uavs.TryGetValue(u, out var d)) throw new NotSupportedException($"u{u} is not declared");
                    if (d.Kind == 't' && d.Dim != DimBuffer && !Mirrored(u)) throw new NotSupportedException($"word access to typed UAV texture u{u}");
                    _usedUavs.Add(u);
                    return ($"u{u}", _atomicUavs.Contains(u));
                }
                case OperandType.Resource:
                {
                    int t = o.Index0;
                    if (!_rawResources.Contains(t) && !_structuredResources.ContainsKey(t)) throw new NotSupportedException($"t{t} is not a raw or structured buffer");
                    _usedRawSrvs.Add(t);
                    return ($"t{t}", false);
                }
                case OperandType.ThreadGroupSharedMemory:
                    return ($"g{o.Index0}", _atomicTgsm.Contains(o.Index0));
                default:
                    throw new NotSupportedException($"memory operand {o.Type}");
            }
        }

        private readonly HashSet<int> _usedRawSrvs = new();

        private string ReadWord(string array, bool atomic, string wordIndex)
            => atomic ? $"atomicLoad(&{array}[{wordIndex}])" : $"{array}[{wordIndex}]";

        private void WriteWord(string array, bool atomic, string wordIndex, string value)
        {
            if (atomic) Line($"atomicStore(&{array}[{wordIndex}], {value});");
            else Line($"{array}[{wordIndex}] = {value};");
        }

        /// <summary>Bounds of a word array, for D3D's out-of-range rule (reads 0, writes dropped).
        /// Group-shared memory is never out of range in a valid shader.</summary>
        private string WordCount(Operand o, string array) => o.Type == OperandType.ThreadGroupSharedMemory
            ? $"{_tgsm[o.Index0].Words}u" : $"arrayLength(&{array})";

        // ---- raw and structured loads and stores ---------------------------------------------------------

        /// <summary>ld_raw dst, byteOffset, src / ld_structured dst, index, byteOffset, src.</summary>
        private void EmitWordLoad(Instruction ins)
        {
            var o = ins.Operands;
            bool structured = ins.Op == Opcode.LdStructured;
            var src = structured ? o[3] : o[2];
            var (array, atomic) = WordArray(src);
            int stride = StrideOf(src);
            string baseWord = structured
                ? $"(bitcast<u32>(({Source(o[1])}).x) * {stride / 4}u + bitcast<u32>(({Source(o[2])}).x) / 4u)"
                : $"(bitcast<u32>(({Source(o[1])}).x) / 4u)";
            string b = $"_w{_tmp++}";
            Line($"let {b} = {baseWord};");
            string n = WordCount(src, array);
            // the source's swizzle picks words of the 4 that follow the address
            var c = new string[4];
            for (int i = 0; i < 4; i++)
            {
                int w = src.ComponentCount == 4 ? src.Swizzle[i] : 0;
                c[i] = $"select(0u, {ReadWord(array, atomic, $"{b} + {w}u")}, {b} + {w}u < {n})";
            }
            Store(o[0], $"bitcast<vec4<f32>>(vec4<u32>({c[0]}, {c[1]}, {c[2]}, {c[3]}))", false);
        }

        /// <summary>store_raw dst, byteOffset, value / store_structured dst, index, byteOffset, value:
        /// the destination's mask says which consecutive words are written.</summary>
        private void EmitWordStore(Instruction ins)
        {
            var o = ins.Operands;
            bool structured = ins.Op == Opcode.StoreStructured;
            var dst = o[0];
            var (array, atomic) = WordArray(dst);
            int stride = StrideOf(dst);
            string baseWord = structured
                ? $"(bitcast<u32>(({Source(o[1])}).x) * {stride / 4}u + bitcast<u32>(({Source(o[2])}).x) / 4u)"
                : $"(bitcast<u32>(({Source(o[1])}).x) / 4u)";
            string value = USource(structured ? o[3] : o[2]);
            string b = $"_w{_tmp++}", v = $"_v{_tmp++}";
            Line($"let {b} = {baseWord};");
            Line($"let {v} = {value};");
            string n = WordCount(dst, array);
            int mask = dst.IsMask ? dst.Mask : 0xF;
            for (int i = 0; i < 4; i++)
            {
                if ((mask & (1 << i)) == 0) continue;
                Line($"if ({b} + {i}u < {n}) {{");
                _indent++;
                WriteWord(array, atomic, $"{b} + {i}u", $"{v}.{"xyzw"[i]}");
                _indent--;
                Line("}");
            }
        }

        private int StrideOf(Operand o) => o.Type switch
        {
            OperandType.UnorderedAccessView => _uavs.TryGetValue(o.Index0, out var d) ? Math.Max(4, d.Stride) : 4,
            OperandType.Resource => _structuredResources.TryGetValue(o.Index0, out int s) ? s : 4,
            OperandType.ThreadGroupSharedMemory => _tgsm.TryGetValue(o.Index0, out var g) && g.Stride > 0 ? g.Stride : 4,
            _ => 4,
        };

        private string UavElementCount(int u)
        {
            if (!_uavs.TryGetValue(u, out var d)) throw new NotSupportedException($"u{u} is not declared");
            _usedUavs.Add(u);
            return d.Kind switch
            {
                'r' => $"(arrayLength(&u{u}) * 4u)",
                's' => $"(arrayLength(&u{u}) * 4u / {Math.Max(4, d.Stride)}u)",
                _ when d.Dim == DimBuffer => $"(arrayLength(&u{u}) / {UavWords(u)}u)",
                _ => throw new NotSupportedException($"bufinfo on typed UAV texture u{u}"),
            };
        }

        private string SrvElementCount(int t)
        {
            _usedRawSrvs.Add(t);
            return _structuredResources.TryGetValue(t, out int s) ? $"(arrayLength(&t{t}) * 4u / {Math.Max(4, s)}u)" : $"(arrayLength(&t{t}) * 4u)";
        }

        // ---- typed UAVs ----------------------------------------------------------------------------------

        private int UavWords(int u) => _options.UavBufferWords != null && _options.UavBufferWords.TryGetValue(u, out int w) ? w : 1;

        private string UavFormat(int u, char ret)
        {
            if (_options.UavFormats != null && _options.UavFormats.TryGetValue(u, out string? f)) return f;
            return ret switch { 'i' => "r32sint", 'u' => "r32uint", _ => "r32float" };
        }

        /// <summary>A storage texture: read_write when the shader reads it (WebGPU allows that for the R32
        /// formats, and more with the format tiers), write-only otherwise.</summary>
        private static string StorageTextureType(int dim, string format, bool read)
        {
            string access = read ? "read_write" : "write";
            return dim switch
            {
                Dim1D or Dim2D => $"texture_storage_2d<{format}, {access}>",
                Dim1DArray or Dim2DArray => $"texture_storage_2d_array<{format}, {access}>",
                Dim3D => $"texture_storage_3d<{format}, {access}>",
                _ => throw new NotSupportedException($"typed UAV of dimension {dim}"),
            };
        }

        /// <summary>The texel coordinate of a typed UAV access, with its layer.</summary>
        private static string UavCoord(int dim, string c, out string? layer)
        {
            layer = dim switch { Dim1DArray => $"({c}).y", Dim2DArray => $"({c}).z", _ => null };
            return dim switch
            {
                Dim1D => $"vec2<u32>(({c}).x, 0u)",
                Dim1DArray => $"vec2<u32>(({c}).x, 0u)",
                Dim2D => $"({c}).xy",
                Dim2DArray => $"({c}).xy",
                Dim3D => $"({c}).xyz",
                _ => throw new NotSupportedException($"typed UAV of dimension {dim}"),
            };
        }

        /// <summary>ld_uav_typed dst, address, u#.</summary>
        private void EmitUavTypedLoad(Instruction ins)
        {
            var o = ins.Operands;
            int u = o[2].Index0;
            if (!_uavs.TryGetValue(u, out var d) || d.Kind != 't') throw new NotSupportedException($"ld_uav_typed on u{u}, which is not a typed UAV");
            _usedUavs.Add(u);
            _uavRead.Add(u);
            string c = USource(o[1]);
            string v;
            if (Mirrored(u))
            {
                string i = $"_w{_tmp++}";
                Line($"let {i} = {MirrorWord(u, c)};");
                v = $"bitcast<vec4<f32>>(vec4<u32>(select(0u, atomicLoad(&u{u}[min({i}, arrayLength(&u{u}) - 1u)]), {i} < arrayLength(&u{u})), 0u, 0u, 0u))";
            }
            else if (d.Dim == DimBuffer)
            {
                int words = UavWords(u);
                string i = $"(({c}).x * {words}u)";
                var k = new string[4];
                for (int j = 0; j < 4; j++) k[j] = j < words ? $"select(0u, {ReadWord($"u{u}", _atomicUavs.Contains(u), $"{i} + {j}u")}, {i} + {j}u < arrayLength(&u{u}))" : (j == 3 && d.Ret == 'f' ? "0x3F800000u" : "0u");
                v = $"bitcast<vec4<f32>>(vec4<u32>({k[0]}, {k[1]}, {k[2]}, {k[3]}))";
            }
            else
            {
                string coord = UavCoord(d.Dim, c, out string? layer);
                string call = layer == null ? $"textureLoad(u{u}, {coord})" : $"textureLoad(u{u}, {coord}, {layer})";
                v = d.Ret == 'f' ? call : $"bitcast<vec4<f32>>({call})";
            }
            Store(o[0], $"({v}){Swz(o[2])}", false);
        }

        /// <summary>store_uav_typed u#.mask, address, value.</summary>
        private void EmitUavTypedStore(Instruction ins)
        {
            var o = ins.Operands;
            int u = o[0].Index0;
            if (!_uavs.TryGetValue(u, out var d) || d.Kind != 't') throw new NotSupportedException($"store_uav_typed on u{u}, which is not a typed UAV");
            _usedUavs.Add(u);
            string c = USource(o[1]);
            if (Mirrored(u))
            {
                string i = $"_w{_tmp++}";
                Line($"let {i} = {MirrorWord(u, c)};");
                Line($"if ({i} < arrayLength(&u{u})) {{ atomicStore(&u{u}[{i}], {USource(o[2])}.x); }}");
                return;
            }
            if (d.Dim == DimBuffer)
            {
                int words = UavWords(u);
                string i = $"_w{_tmp++}", v = $"_v{_tmp++}";
                Line($"let {i} = ({c}).x * {words}u;");
                Line($"let {v} = {USource(o[2])};");
                for (int j = 0; j < words; j++)
                {
                    Line($"if ({i} + {j}u < arrayLength(&u{u})) {{");
                    _indent++;
                    WriteWord($"u{u}", _atomicUavs.Contains(u), $"{i} + {j}u", $"{v}.{"xyzw"[j]}");
                    _indent--;
                    Line("}");
                }
                return;
            }
            string coord = UavCoord(d.Dim, c, out string? layer);
            string value = d.Ret switch { 'i' => ISource(o[2]), 'u' => USource(o[2]), _ => Source(o[2]) };
            Line(layer == null ? $"textureStore(u{u}, {coord}, {value});" : $"textureStore(u{u}, {coord}, {layer}, {value});");
        }

        // ---- atomics ------------------------------------------------------------------------------------

        /// <summary>atomic_* dst, address, value(s) and imm_atomic_* old, dst, address, value(s).</summary>
        private void EmitAtomic(Instruction ins)
        {
            var o = ins.Operands;
            if (ins.Op is Opcode.ImmAtomicAlloc or Opcode.ImmAtomicConsume) { EmitCounter(ins); return; }
            bool imm = ins.Op.ToString().StartsWith("Imm", StringComparison.Ordinal);
            int k = imm ? 1 : 0;
            var target = o[k];
            var (array, _) = WordArray(target);
            if (target.Type == OperandType.UnorderedAccessView) _atomicUavs.Add(target.Index0);
            string address = AtomicWord(target, o[k + 1]);
            if (target.Type == OperandType.UnorderedAccessView && Mirrored(target.Index0))
            {
                // outside the view the access is dropped (an immediate one returns 0): aim it at a
                // word of scratch instead -- the mirror's last word is padding past every row
                address = $"select(arrayLength(&{array}) - 1u, {address}, {address} < arrayLength(&{array}) - 1u)";
            }
            string p = $"&{array}[{address}]";
            string a = USource(o[k + 2]) + ".x";
            string call = ins.Op switch
            {
                Opcode.AtomicAnd or Opcode.ImmAtomicAnd => $"atomicAnd({p}, {a})",
                Opcode.AtomicOr or Opcode.ImmAtomicOr => $"atomicOr({p}, {a})",
                Opcode.AtomicXor or Opcode.ImmAtomicXor => $"atomicXor({p}, {a})",
                Opcode.AtomicIAdd or Opcode.ImmAtomicIAdd => $"atomicAdd({p}, {a})",
                Opcode.AtomicUMax or Opcode.ImmAtomicUMax => $"atomicMax({p}, {a})",
                Opcode.AtomicUMin or Opcode.ImmAtomicUMin => $"atomicMin({p}, {a})",
                // signed min/max on u32 storage: a compare-exchange loop on the signed view
                Opcode.AtomicIMax or Opcode.ImmAtomicIMax or Opcode.AtomicIMin or Opcode.ImmAtomicIMin => "",
                Opcode.ImmAtomicExch => $"atomicExchange({p}, {a})",
                Opcode.AtomicCmpStore or Opcode.ImmAtomicCmpExch => $"atomicCompareExchangeWeak({p}, {a}, {USource(o[k + 3])}.x).old_value",
                _ => throw new NotSupportedException($"atomic {ins.Op}"),
            };
            // signed min/max on u32 storage: a compare-exchange loop on the signed view, inline (WGSL
            // takes no storage pointer as a function argument)
            if (ins.Op is Opcode.AtomicIMax or Opcode.ImmAtomicIMax or Opcode.AtomicIMin or Opcode.ImmAtomicIMin)
            {
                string fn = ins.Op is Opcode.AtomicIMax or Opcode.ImmAtomicIMax ? "max" : "min";
                string old = $"_a{_tmp++}", v = $"_a{_tmp++}";
                Line($"let {v} = {a};");
                Line($"var {old} = atomicLoad({p});");
                Line($"loop {{ let r = atomicCompareExchangeWeak({p}, {old}, bitcast<u32>({fn}(bitcast<i32>({old}), bitcast<i32>({v})))); if (r.exchanged) {{ break; }} {old} = r.old_value; }}");
                if (imm) StoreI(o[0], $"vec4<u32>({old})");
                return;
            }
            // compare-exchange is weak in WGSL: a spurious failure is retried until the value differs
            if (ins.Op is Opcode.AtomicCmpStore or Opcode.ImmAtomicCmpExch)
            {
                string r = $"_a{_tmp++}";
                Line($"var {r} = atomicCompareExchangeWeak({p}, {a}, {USource(o[k + 3])}.x);");
                Line($"loop {{ if ({r}.exchanged || {r}.old_value != {a}) {{ break; }} {r} = atomicCompareExchangeWeak({p}, {a}, {USource(o[k + 3])}.x); }}");
                if (imm) StoreI(o[0], $"vec4<u32>({r}.old_value)");
                return;
            }
            if (imm) StoreI(o[0], $"vec4<u32>({call})");
            else Line($"{call};");
        }

        private readonly HashSet<string> _usesSignedAtomics = new();

        private static string AtomicSpace(Operand target) => target.Type == OperandType.ThreadGroupSharedMemory ? "workgroup" : "storage";

        /// <summary>The word an atomic's address names: a raw byte offset, a structured (index,
        /// byte offset), or a typed buffer element.</summary>
        private string AtomicWord(Operand target, Operand address)
        {
            string c = USource(address);
            if (target.Type == OperandType.UnorderedAccessView && _uavs.TryGetValue(target.Index0, out var d))
            {
                if (d.Kind == 's') return $"(({c}).x * {Math.Max(4, d.Stride) / 4}u + ({c}).y / 4u)";
                if (d.Kind == 't' && d.Dim != DimBuffer) return $"min({MirrorWord(target.Index0, c)}, arrayLength(&u{target.Index0}))";
                if (d.Kind == 't') return $"(({c}).x * {UavWords(target.Index0)}u)";
                return $"(({c}).x / 4u)";
            }
            if (target.Type == OperandType.ThreadGroupSharedMemory && _tgsm.TryGetValue(target.Index0, out var g) && g.Stride > 0)
                return $"(({c}).x * {g.Stride / 4}u + ({c}).y / 4u)";
            return $"(({c}).x / 4u)";
        }

        /// <summary>imm_atomic_alloc / imm_atomic_consume: a structured UAV's hidden counter.</summary>
        private void EmitCounter(Instruction ins)
        {
            var o = ins.Operands;
            int u = o[1].Index0;
            _usedCounters.Add(u);
            _usedUavs.Add(u);
            string call = ins.Op == Opcode.ImmAtomicAlloc ? $"atomicAdd(&uc{u}, 1u)" : $"(atomicSub(&uc{u}, 1u) - 1u)";
            StoreI(o[0], $"vec4<u32>({call})");
        }

        /// <summary>sync: the thread barrier, group-shared and UAV fences, in WGSL's barriers.</summary>
        private void EmitSync(Instruction ins)
        {
            uint flags = (ins.Token >> 11) & 0xF;      // 1 threads in group, 2 tgsm, 4 uav group, 8 uav global
            bool threads = (flags & 1) != 0, tgsm = (flags & 2) != 0, uav = (flags & 0xC) != 0;
            if (uav) Line("storageBarrier();");
            if (tgsm || threads) Line("workgroupBarrier();");
            if (threads && !tgsm && !uav) { }           // the barrier above is the thread sync
        }

        // ---- declarations in the module -------------------------------------------------------------------

        private void AssembleMemory(System.Text.StringBuilder w)
        {
            foreach (var (u, d) in _uavs.OrderBy(k => k.Key))
            {
                if (!_usedUavs.Contains(u) && !_atomicUavs.Contains(u)) continue;
                string binding = $"@group({_group}) @binding({UavBase + u})";
                if (d.Kind == 't' && d.Dim != DimBuffer && !Mirrored(u))
                    w.Append($"{binding} var u{u}: {StorageTextureType(d.Dim, UavFormat(u, d.Ret), _uavRead.Contains(u))};\n");
                else
                    w.Append($"{binding} var<storage, read_write> u{u}: array<{(_atomicUavs.Contains(u) ? "atomic<u32>" : "u32")}>;\n");
                if (Mirrored(u)) w.Append(MirrorHelpers(u, d.Dim));
            }
            foreach (int u in _usedCounters.OrderBy(x => x))
                w.Append($"@group({_group}) @binding({CounterBase + u}) var<storage, read_write> uc{u}: atomic<u32>;\n");
            foreach (int t in _usedRawSrvs.OrderBy(x => x))
                w.Append($"@group({_group}) @binding({ResourceBase + t}) var<storage, read> t{t}: array<u32>;\n");
            foreach (var (g, d) in _tgsm.OrderBy(k => k.Key))
                w.Append($"var<workgroup> g{g}: array<{(_atomicTgsm.Contains(g) ? "atomic<u32>" : "u32")}, {Math.Max(1, d.Words)}>;\n");
            if (_usesRasterizerSamples) w.Append("override dx_rasterizer_samples: u32 = 1u;\n");
            if (_usesSamplePositions) w.Append(SamplePositionsWgsl);
        }
    }
}
