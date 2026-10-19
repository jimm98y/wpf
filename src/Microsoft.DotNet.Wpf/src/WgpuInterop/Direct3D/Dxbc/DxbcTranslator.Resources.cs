// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Texture instructions of the DXBC translator: every resource dimension and every sampling, load,
// gather and query instruction (Documentation/d3d11-webgpu-shim.md, section 9).
//
// THE TEXTURE MODEL. D3D's dimensions map to WGSL's as follows:
//   - 1D and 1D arrays are 2D textures (and arrays) one texel high. WGSL's texture_1d takes neither
//     a level, a gradient, a gather nor an array, and D3D's 1D textures need all of them. The device
//     creates a Texture1D as a 2D texture of height 1, so every operation is the 2D one at v = 0.5.
//   - 2D, 2D arrays, 3D, cubes and cube arrays are themselves; 2D MS is texture_multisampled_2d.
//   - A 2D MS array has no WebGPU equivalent: the shader is refused by name.
//   - A resource read with a comparison sampler is a depth texture (texture_depth_*).
// A dynamic texel offset (the _po gathers) moves the coordinate by offset / size, which is exact in
// texel space; WGSL's offsets must be constants.
//

using System;
using System.Collections.Generic;

namespace Microsoft.Wpf.Interop.WebGpu.Direct3D.Dxbc
{
    internal sealed partial class DxbcTranslator
    {
        private const int DimBuffer = 1, Dim1D = 2, Dim2D = 3, Dim2DMS = 4, Dim3D = 5, DimCube = 6, Dim1DArray = 7, Dim2DArray = 8, Dim2DMSArray = 9, DimCubeArray = 10;

        private int ResourceDim(int slot) => _resourceDim.TryGetValue(slot, out int d) ? d : Dim2D;

        private void UseResource(int slot, bool depth)
        {
            int dim = ResourceDim(slot);
            char kind = _resourceKind.TryGetValue(slot, out char k) ? k : 'f';
            if (depth) _comparisonResources.Add(slot);
            _bindings.Resources[slot] = (dim, kind, depth || _comparisonResources.Contains(slot));
        }

        private void UseSampler(int slot, bool comparison)
        {
            bool cmp = comparison || (_samplerComparison.TryGetValue(slot, out bool c) && c);
            _bindings.Samplers[slot] = cmp;
        }

        private static void RequireTextureDim(int dim, string what)
        {
            if (dim == Dim2DMSArray) throw new NotSupportedException($"{what} on a Texture2DMSArray: WebGPU has no multisampled array textures");
            if (dim is 0 or > DimCubeArray) throw new NotSupportedException($"{what} on resource dimension {dim}");
        }

        /// <summary>The sampling coordinate of a float address, and the array layer (null when the
        /// texture is not an array).</summary>
        private static string SampleCoord(int dim, string c, out string? layer)
        {
            layer = null;
            switch (dim)
            {
                case Dim1D: return $"vec2<f32>(({c}).x, 0.5f)";
                case Dim1DArray: layer = $"i32(round(({c}).y))"; return $"vec2<f32>(({c}).x, 0.5f)";
                case Dim2D: case Dim2DMS: return $"({c}).xy";
                case Dim2DArray: layer = $"i32(round(({c}).z))"; return $"({c}).xy";
                case Dim3D: case DimCube: return $"({c}).xyz";
                case DimCubeArray: layer = $"i32(round(({c}).w))"; return $"({c}).xyz";
                default: throw new NotSupportedException($"sampling resource dimension {dim}");
            }
        }

        /// <summary>The texel address of an integer address (ld): coordinates, array layer and mip
        /// level (D3D keeps the level in .w for every dimension).</summary>
        private static string LoadCoord(int dim, string c, out string? layer, out string? level)
        {
            layer = null; level = $"({c}).w";
            switch (dim)
            {
                case Dim1D: return $"vec2<i32>(({c}).x, 0)";
                case Dim1DArray: layer = $"({c}).y"; return $"vec2<i32>(({c}).x, 0)";
                case Dim2D: return $"({c}).xy";
                case Dim2DMS: level = null; return $"({c}).xy";
                case Dim2DArray: layer = $"({c}).z"; return $"({c}).xy";
                case Dim3D: return $"({c}).xyz";
                default: throw new NotSupportedException($"ld from resource dimension {dim}");
            }
        }

        private static string Offset(Instruction ins, int dim)
        {
            if (ins.TexelOffset[0] == 0 && ins.TexelOffset[1] == 0 && ins.TexelOffset[2] == 0) return "";
            return dim switch
            {
                Dim1D or Dim1DArray => $", vec2<i32>({ins.TexelOffset[0]}, 0)",
                Dim2D or Dim2DArray or Dim2DMS => $", vec2<i32>({ins.TexelOffset[0]}, {ins.TexelOffset[1]})",
                Dim3D => $", vec3<i32>({ins.TexelOffset[0]}, {ins.TexelOffset[1]}, {ins.TexelOffset[2]})",
                _ => "",                                   // cubes take no offset (D3D ignores it)
            };
        }

        private static string GradPart(int dim, string src) => dim switch
        {
            Dim1D or Dim1DArray => $"vec2<f32>(({src}).x, 0.0f)",
            Dim3D or DimCube or DimCubeArray => $"({src}).xyz",
            _ => $"({src}).xy",
        };

        private static string TextureType(int dim, char kind, bool depth)
        {
            if (dim == DimBuffer) return "array<u32>";
            string k = kind switch { 'i' => "i32", 'u' => "u32", _ => "f32" };
            if (depth)
                return dim switch
                {
                    DimCube => "texture_depth_cube",
                    Dim1DArray or Dim2DArray => "texture_depth_2d_array",
                    DimCubeArray => "texture_depth_cube_array",
                    Dim2DMS => "texture_depth_multisampled_2d",
                    _ => "texture_depth_2d",
                };
            return dim switch
            {
                Dim1D or Dim2D => $"texture_2d<{k}>",
                Dim2DMS => $"texture_multisampled_2d<{k}>",
                Dim3D => $"texture_3d<{k}>",
                DimCube => $"texture_cube<{k}>",
                Dim1DArray or Dim2DArray => $"texture_2d_array<{k}>",
                DimCubeArray => $"texture_cube_array<{k}>",
                _ => throw new NotSupportedException($"resource dimension {dim}" + (dim == Dim2DMSArray ? ": WebGPU has no multisampled array textures" : "")),
            };
        }

        /// <summary>A sampled result (vec4 of the texture's type, or a depth f32) as register bits.</summary>
        private string AsRegister(string call, int slot, bool depth)
        {
            if (depth) return $"vec4<f32>({call})";
            return _bindings.Resources[slot].Kind == 'f' ? call : $"bitcast<vec4<f32>>({call})";
        }

        private void EmitSample(Instruction ins)
        {
            var o = ins.Operands;
            int t = o[2].Index0, smp = o[3].Index0;
            bool cmp = ins.Op is Opcode.SampleC or Opcode.SampleCLz;
            UseResource(t, cmp);
            UseSampler(smp, cmp);
            int dim = ResourceDim(t);
            RequireTextureDim(dim, ins.Op.ToString());
            if (dim == Dim2DMS) throw new NotSupportedException("sampling a multisampled texture (D3D does not allow it either)");
            string coord = SampleCoord(dim, Source(o[1]), out string? layer);
            string tex = $"t{t}", s = $"s{smp}";
            string args = layer == null ? $"{tex}, {s}, {coord}" : $"{tex}, {s}, {coord}, {layer}";
            string off = Offset(ins, dim);
            string call = ins.Op switch
            {
                Opcode.Sample => $"textureSample({args}{off})",
                Opcode.SampleB => $"textureSampleBias({args}, ({Source(o[4])}).x{off})",
                Opcode.SampleL => $"textureSampleLevel({args}, ({Source(o[4])}).x{off})",
                Opcode.SampleD => $"textureSampleGrad({args}, {GradPart(dim, Source(o[4]))}, {GradPart(dim, Source(o[5]))}{off})",
                Opcode.SampleC => $"textureSampleCompare({args}, ({Source(o[4])}).x{off})",
                _ => $"textureSampleCompareLevel({args}, ({Source(o[4])}).x{off})",
            };
            Store(o[0], $"({AsRegister(call, t, cmp)}){Swz(o[2])}", ins.Saturate);
        }

        private void EmitLoad(Instruction ins)
        {
            var o = ins.Operands;
            int t = o[2].Index0;
            if (o[2].Type == OperandType.UnorderedAccessView) { EmitUavTypedLoad(ins); return; }
            UseResource(t, false);
            int dim = ResourceDim(t);
            string c = ISource(o[1]);
            if (dim == DimBuffer) { Store(o[0], $"({BufferLoad(t, c)}){Swz(o[2])}", ins.Saturate); return; }
            RequireTextureDim(dim, "ld");
            string coord = LoadCoord(dim, c, out string? layer, out string? level);
            string off = OffsetAsAdd(ins, dim);
            if (off.Length > 0) coord = $"({coord} + {off})";
            string call;
            if (ins.Op == Opcode.LdMs)
            {
                if (dim != Dim2DMS) throw new NotSupportedException($"ld2dms on dimension {dim}");
                call = $"textureLoad(t{t}, {coord}, ({ISource(o[3])}).x)";
            }
            else call = layer == null ? $"textureLoad(t{t}, {coord}, {level})" : $"textureLoad(t{t}, {coord}, {layer}, {level})";
            Store(o[0], $"({AsRegister(call, t, false)}){Swz(o[2])}", ins.Saturate);
        }

        /// <summary>ld's immediate offset, added to the texel address (textureLoad takes none).</summary>
        private static string OffsetAsAdd(Instruction ins, int dim)
        {
            if (ins.TexelOffset[0] == 0 && ins.TexelOffset[1] == 0 && ins.TexelOffset[2] == 0) return "";
            return dim switch
            {
                Dim1D or Dim1DArray => $"vec2<i32>({ins.TexelOffset[0]}, 0)",
                Dim3D => $"vec3<i32>({ins.TexelOffset[0]}, {ins.TexelOffset[1]}, {ins.TexelOffset[2]})",
                _ => $"vec2<i32>({ins.TexelOffset[0]}, {ins.TexelOffset[1]})",
            };
        }

        /// <summary>gather4, gather4_c, gather4_po, gather4_po_c: the sampler operand's component
        /// selects the channel; the _po forms take a register offset, applied to the coordinate.</summary>
        private void EmitGather(Instruction ins)
        {
            var o = ins.Operands;
            bool po = ins.Op is Opcode.Gather4Po or Opcode.Gather4PoC;
            bool cmp = ins.Op is Opcode.Gather4C or Opcode.Gather4PoC;
            int ri = po ? 3 : 2, si = ri + 1;
            int t = o[ri].Index0, smp = o[si].Index0;
            UseResource(t, cmp);
            UseSampler(smp, cmp);
            int dim = ResourceDim(t);
            if (dim is not (Dim2D or Dim2DArray or DimCube or DimCubeArray)) throw new NotSupportedException($"gather on resource dimension {dim}");
            string coord = SampleCoord(dim, Source(o[1]), out string? layer);
            if (po)
            {
                // a register offset: in texel units, moved into the normalized coordinate
                string size = $"vec2<f32>(textureDimensions(t{t}, 0))";
                coord = $"({coord} + vec2<f32>(({ISource(o[2])}).xy) / {size})";
            }
            string off = po ? "" : Offset(ins, dim);
            int component = o[si].ComponentCount == 4 ? o[si].Swizzle[0] : 0;
            string call;
            if (cmp)
            {
                string reference = $"({Source(o[si + 1])}).x";
                call = layer == null ? $"textureGatherCompare(t{t}, s{smp}, {coord}, {reference}{off})" : $"textureGatherCompare(t{t}, s{smp}, {coord}, {layer}, {reference}{off})";
                call = $"vec4<f32>({call})";
            }
            else
            {
                call = layer == null ? $"textureGather({component}, t{t}, s{smp}, {coord}{off})" : $"textureGather({component}, t{t}, s{smp}, {coord}, {layer}{off})";
                call = AsRegister(call, t, false);
            }
            Store(o[0], $"({call}){Swz(o[ri])}", ins.Saturate);
        }

        private void EmitResInfo(Instruction ins)
        {
            var o = ins.Operands;
            int t = o[2].Index0;
            UseResource(t, false);
            int dim = ResourceDim(t);
            RequireTextureDim(dim, "resinfo");
            int ret = (int)((ins.Token >> 11) & 3);      // 0 float, 1 rcpfloat, 2 uint
            string mip = $"bitcast<u32>(({Source(o[1])}).x)";
            string tex = $"t{t}";
            string dims = dim switch
            {
                Dim1D => $"vec4<u32>(textureDimensions({tex}, {mip}).x, 0u, 0u, textureNumLevels({tex}))",
                Dim1DArray => $"vec4<u32>(textureDimensions({tex}, {mip}).x, textureNumLayers({tex}), 0u, textureNumLevels({tex}))",
                Dim2D or DimCube => $"vec4<u32>(textureDimensions({tex}, {mip}), 0u, textureNumLevels({tex}))",
                Dim2DMS => $"vec4<u32>(textureDimensions({tex}), 0u, 1u)",
                Dim2DArray => $"vec4<u32>(textureDimensions({tex}, {mip}), textureNumLayers({tex}), textureNumLevels({tex}))",
                DimCubeArray => $"vec4<u32>(textureDimensions({tex}, {mip}), textureNumLayers({tex}), textureNumLevels({tex}))",
                Dim3D => $"vec4<u32>(textureDimensions({tex}, {mip}), textureNumLevels({tex}))",
                _ => throw new NotSupportedException($"resinfo on dimension {dim}"),
            };
            // a level past the last: D3D returns 0 for the size and still the level count
            string v = ret switch
            {
                2 => $"bitcast<vec4<f32>>({dims})",
                1 => $"(vec4<f32>(1.0f) / vec4<f32>({dims}))",
                _ => $"vec4<f32>({dims})",
            };
            if (ret == 1) v = $"vec4<f32>(({v}).xyz, f32(({dims}).w))";   // rcpfloat leaves the level count as it is
            Store(o[0], $"({v}){Swz(o[2])}", ins.Saturate);
        }

        /// <summary>sampleinfo: the sample count of a resource (or of the rasterizer).</summary>
        private void EmitSampleInfo(Instruction ins)
        {
            var o = ins.Operands;
            bool uint_ = ((ins.Token >> 11) & 1) != 0;
            string n;
            if (o[1].Type == OperandType.Rasterizer)
            {
                _usesRasterizerSamples = true;
                n = "dx_rasterizer_samples";
            }
            else
            {
                int t = o[1].Index0;
                UseResource(t, false);
                if (ResourceDim(t) != Dim2DMS) throw new NotSupportedException("sampleinfo on a resource that is not a Texture2DMS");
                n = $"textureNumSamples(t{t})";
            }
            string v = uint_ ? $"bitcast<vec4<f32>>(vec4<u32>({n}, 0u, 0u, 0u))" : $"vec4<f32>(f32({n}), 0.0f, 0.0f, 0.0f)";
            Store(o[0], $"({v}){Swz(o[1])}", ins.Saturate);
        }

        /// <summary>samplepos: the standard sample positions (D3D11 fixes them for 1, 2, 4, 8 and 16
        /// samples), as offsets from the pixel centre, for a resource's or the rasterizer's count.</summary>
        private void EmitSamplePos(Instruction ins)
        {
            var o = ins.Operands;
            string n;
            if (o[1].Type == OperandType.Rasterizer) { _usesRasterizerSamples = true; n = "dx_rasterizer_samples"; }
            else
            {
                int t = o[1].Index0;
                UseResource(t, false);
                n = $"textureNumSamples(t{t})";
            }
            _usesSamplePositions = true;
            Store(o[0], $"(dx_sample_pos({n}, bitcast<u32>(({Source(o[2])}).x))){Swz(o[1])}", ins.Saturate);
        }

        /// <summary>lod: WGSL cannot ask a sampler which level it would take, so the level is computed
        /// as the specification defines it from the coordinate's screen derivatives: log2 of the larger
        /// derivative length in texels; .x clamped to the texture's levels, .y not.</summary>
        private void EmitLod(Instruction ins)
        {
            var o = ins.Operands;
            int t = o[2].Index0, smp = o[3].Index0;
            UseResource(t, false); UseSampler(smp, false);
            int dim = ResourceDim(t);
            RequireTextureDim(dim, "lod");
            string c = SampleCoord(dim, Source(o[1]), out _);
            string size = dim switch
            {
                Dim3D => $"vec3<f32>(textureDimensions(t{t}, 0))",
                DimCube or DimCubeArray => $"vec3<f32>(vec2<f32>(textureDimensions(t{t}, 0)), f32(textureDimensions(t{t}, 0).x))",
                _ => $"vec2<f32>(textureDimensions(t{t}, 0))",
            };
            string scaled = $"({c} * {size})";
            string tmp = $"_l{_tmp++}";
            Line($"let {tmp} = log2(max(length(dpdx({scaled})), length(dpdy({scaled}))));");
            Store(o[0], $"(vec4<f32>(clamp({tmp}, 0.0f, f32(textureNumLevels(t{t}) - 1u)), {tmp}, 0.0f, 0.0f)){Swz(o[2])}", ins.Saturate);
        }

        /// <summary>bufinfo: a buffer's element count (typed: 16-byte elements; raw: bytes;
        /// structured: elements of its stride).</summary>
        private void EmitBufInfo(Instruction ins)
        {
            var o = ins.Operands;
            string n;
            if (o[1].Type == OperandType.UnorderedAccessView) n = UavElementCount(o[1].Index0);
            else
            {
                int t = o[1].Index0;
                if (_rawResources.Contains(t) || _structuredResources.ContainsKey(t)) n = SrvElementCount(t);
                else { UseResource(t, false); n = $"(arrayLength(&t{t}) / 4u)"; }
            }
            Store(o[0], $"(bitcast<vec4<f32>>(vec4<u32>({n}))){Swz(o[1])}", false);
        }

        /// <summary>ld from a typed buffer (Buffer&lt;T&gt;): WebGPU has no texel buffers, so the view is a
        /// read-only storage buffer of 32-bit words and an element is four of them (the 128-bit
        /// formats; D3DContext logs any other view format). Out of range reads 0, as D3D11's do.</summary>
        private string BufferLoad(int t, string c)
        {
            string i = $"bitcast<u32>(({c}).x)";
            string w(int k) => $"select(0u, t{t}[{i} * 4u + {k}u], {i} * 4u + {k}u < arrayLength(&t{t}))";
            return $"bitcast<vec4<f32>>(vec4<u32>({w(0)}, {w(1)}, {w(2)}, {w(3)}))";   // registers hold bits
        }

        private const string SamplePositionsWgsl = @"
// D3D11's standard sample positions in 1/16 pixel units, from the pixel centre (1, 2, 4, 8 and 16 samples)
const dx_pos1 = array<vec2<i32>, 1>(vec2<i32>(0, 0));
const dx_pos2 = array<vec2<i32>, 2>(vec2<i32>(4, 4), vec2<i32>(-4, -4));
const dx_pos4 = array<vec2<i32>, 4>(vec2<i32>(-2, -6), vec2<i32>(6, -2), vec2<i32>(-6, 2), vec2<i32>(2, 6));
const dx_pos8 = array<vec2<i32>, 8>(vec2<i32>(1, -3), vec2<i32>(-1, 3), vec2<i32>(5, 1), vec2<i32>(-3, -5), vec2<i32>(-5, 5), vec2<i32>(-7, -1), vec2<i32>(3, 7), vec2<i32>(7, -7));
const dx_pos16 = array<vec2<i32>, 16>(vec2<i32>(1, 1), vec2<i32>(-1, -3), vec2<i32>(-3, 2), vec2<i32>(4, -1), vec2<i32>(-5, -2), vec2<i32>(2, 5), vec2<i32>(5, 3), vec2<i32>(3, -5),
    vec2<i32>(-2, 6), vec2<i32>(0, -7), vec2<i32>(-4, -6), vec2<i32>(-6, 4), vec2<i32>(-8, 0), vec2<i32>(7, -4), vec2<i32>(6, 7), vec2<i32>(-7, -8));
fn dx_sample_pos(count: u32, index: u32) -> vec4<f32> {
    var p = vec2<i32>(0, 0);
    if (count == 2u && index < 2u) { p = dx_pos2[index]; }
    else if (count == 4u && index < 4u) { p = dx_pos4[index]; }
    else if (count == 8u && index < 8u) { p = dx_pos8[index]; }
    else if (count == 16u && index < 16u) { p = dx_pos16[index]; }
    return vec4<f32>(vec2<f32>(p) / 16.0f, 0.0f, 0.0f);
}
";
    }
}
