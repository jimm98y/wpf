// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Draws: the shaders' translation and linkage, the pipeline for the bound state, the bind groups, the
// render pass (Documentation/d3d11-webgpu-shim.md, sections 5 and 6).
//

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Wpf.Interop.WebGpu.Direct3D.Dxbc;
using static Microsoft.Wpf.Interop.WebGpu.Wgpu;
using static Microsoft.Wpf.Interop.WebGpu.Direct3D.WgpuCompute;

namespace Microsoft.Wpf.Interop.WebGpu.Direct3D
{
    internal sealed unsafe partial class D3DContext
    {
        internal struct DrawArgs
        {
            public bool Indexed;
            public uint Count, Instances, First, FirstInstance;
            public int BaseVertex;
            /// <summary>DrawInstancedIndirect / DrawIndexedInstancedIndirect: the arguments' buffer and offset.</summary>
            public D3DBuffer? Indirect;
            public uint IndirectOffset;
        }

        public void Draw(DrawArgs a) => Run(c => c.DrawImpl(a), a.Indirect);

        /// <summary>Draws issued and draws skipped (no shader, a translation or pipeline failure), for
        /// tests and traces.</summary>
        public int DrawsIssued, DrawsSkipped;
        /// <summary>Texture bindings of draws with no view bound (reading the zero stand-in), and with a
        /// bound view that has no WebGPU view (diagnostics).</summary>
        public int UnboundTextureReads, NullTextureViews;

        // ---- translation -----------------------------------------------------------------------------

        private sealed class Translation
        {
            public IntPtr Vs, Ps;
            public IntPtr VsLayout, PsLayout, PipelineLayout;
            public ShaderBindings? VsBindings, PsBindings;
            public string? Error;
        }

        private readonly Dictionary<(ulong, ulong, string), Translation> _translations = new();

        private Translation Translate(D3DShader vs, D3DShader? ps)
        {
            // the input registers the layout feeds from B8G8R8A8 elements (swapped in the shader)
            var bgra = new SortedSet<int>();
            if (_layout != null)
                foreach (var e in _layout.Elements.Where(e => e.Format == DXGI_FORMAT.B8G8R8A8_UNORM))
                    if (vs.Code.Inputs.FirstOrDefault(i => i.SystemValue == 0 && i.SemanticName.Equals(e.Semantic, StringComparison.OrdinalIgnoreCase) && i.SemanticIndex == e.Index) is { } sig) bgra.Add(sig.Register);
            var key = (vs.Code.Hash, ps?.Code.Hash ?? 0, string.Join(",", bgra));
            if (_translations.TryGetValue(key, out var t)) return t;
            t = new Translation();
            _translations[key] = t;
            try
            {
                var link = DxbcTranslator.LinkageOf(ps?.Code, vs.Code);
                var v = DxbcTranslator.Translate(vs.Code, 0, link, bgra.Count > 0 ? new TranslationOptions { BgraInputs = bgra } : null);
                t.VsBindings = v.Bindings;
                t.Vs = CreateModule(v.Wgsl, "vs", out string? verr);
                if (t.Vs == IntPtr.Zero) { t.Error = "vertex shader: " + verr; Dump(vs.Code, v.Wgsl, t.Error); return t; }
                if (ps != null)
                {
                    var p = DxbcTranslator.Translate(ps.Code, 1, link);
                    t.PsBindings = p.Bindings;
                    t.Ps = CreateModule(p.Wgsl, "ps", out string? perr);
                    if (t.Ps == IntPtr.Zero) { t.Error = "pixel shader: " + perr; Dump(ps.Code, p.Wgsl, t.Error); return t; }
                }
                t.VsLayout = BindGroupLayout(t.VsBindings, WGPUShaderStage_Vertex);
                t.PsLayout = BindGroupLayout(t.PsBindings ?? new ShaderBindings(), WGPUShaderStage_Fragment);
                IntPtr* layouts = stackalloc IntPtr[2] { t.VsLayout, t.PsLayout };
                var pl = new WGPUPipelineLayoutDescriptor { bindGroupLayoutCount = 2, bindGroupLayouts = layouts };
                t.PipelineLayout = wgpuDeviceCreatePipelineLayout(Device.Gpu.Device, &pl);
            }
            catch (NotSupportedException e) { t.Error = e.Message; D3DLog.Once("tr:" + e.Message, $"shader not translated: {e.Message}"); }
            catch (Exception e) { t.Error = e.Message; D3DLog.Error("translate", e); }
            return t;
        }

        private static readonly string? s_dumpDir = Environment.GetEnvironmentVariable("WPF_D3D11_DUMP_SHADERS");

        private static void Dump(DxbcShader s, string wgsl, string error)
        {
            D3DLog.Once("bad:" + s.Hash, $"{s.Stage} shader {s.Hash:x16} rejected: {error}");
            if (string.IsNullOrEmpty(s_dumpDir)) return;
            try
            {
                System.IO.Directory.CreateDirectory(s_dumpDir);
                System.IO.File.WriteAllText(System.IO.Path.Combine(s_dumpDir, $"{s.Hash:x16}.wgsl"), wgsl + "\n/* " + error + " */\n");
            }
            catch { }
        }

        internal IntPtr CreateModule(string wgsl, string label, out string? error)
        {
            byte[] code = Encoding.UTF8.GetBytes(wgsl);
            fixed (byte* p = code)
            {
                var src = new WGPUShaderSourceWGSL
                {
                    chain = new WGPUChainedStruct { next = null, sType = WGPUSType_ShaderSourceWGSL },
                    code = new WGPUStringView { data = p, length = (nuint)code.Length },
                };
                var desc = new WGPUShaderModuleDescriptor { nextInChain = (WGPUChainedStruct*)&src };
                wgpuDevicePushErrorScope(Device.Gpu.Device, WGPUErrorFilter.Validation);
                IntPtr module = wgpuDeviceCreateShaderModule(Device.Gpu.Device, &desc);
                return PopScope(module, out error);
            }
        }

        // ---- error scopes: a validation error becomes a failed call, not an abort ------------------------

        private sealed class ScopeResult { public volatile bool Done; public string? Error; }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
        private static void OnScopePopped(WGPUPopErrorScopeStatus status, WGPUErrorType type, WGPUStringView message, IntPtr u1, IntPtr u2)
        {
            var r = (ScopeResult?)GCHandle.FromIntPtr(u1).Target;
            if (r == null) return;
            if (status == WGPUPopErrorScopeStatus.Success && type != WGPUErrorType.NoError) r.Error = $"{type}: {D3DDevice.Text(message)}";
            r.Done = true;
        }

        /// <summary>Pops the validation scope pushed before creating <paramref name="result"/>: the
        /// object, or zero (and the error) when WebGPU rejected it.</summary>
        private IntPtr PopScope(IntPtr result, out string? error) => PopScope(Device.Gpu, result, out error);

        /// <summary>As the instance PopScope, for any shim object made on <paramref name="gpu"/>.</summary>
        internal static IntPtr PopScope(Microsoft.Wpf.Interop.WebGpu.Composition.WgpuContext gpu, IntPtr result, out string? error)
        {
            IntPtr dev = gpu.Device;
            var r = new ScopeResult();
            var h = GCHandle.Alloc(r);
            try
            {
                var info = new WGPUPopErrorScopeCallbackInfo
                {
                    mode = WGPUCallbackMode.AllowProcessEvents,
                    callback = (IntPtr)(delegate* unmanaged[Cdecl]<WGPUPopErrorScopeStatus, WGPUErrorType, WGPUStringView, IntPtr, IntPtr, void>)&OnScopePopped,
                    userdata1 = GCHandle.ToIntPtr(h),
                };
                wgpuDevicePopErrorScope(dev, info);
                for (int i = 0; i < 10000 && !r.Done; i++)
                {
                    wgpuInstanceProcessEvents(gpu.Instance);
                    if (!r.Done) wgpuDevicePoll(dev, 0, null);
                }
            }
            finally { h.Free(); }
            error = r.Error;
            if (error != null)
            {
                D3DLog.Once("scope:" + (error.Length > 300 ? error[..300] : error), "WebGPU rejected: " + error);
                return IntPtr.Zero;
            }
            return result;
        }

        private IntPtr BindGroupLayout(ShaderBindings b, ulong stage, D3DUnorderedAccessView?[]? uavs = null)
        {
            var entries = new List<WGPUBindGroupLayoutEntry>();
            foreach (int slot in b.StorageResources)
                entries.Add(new WGPUBindGroupLayoutEntry
                {
                    binding = (uint)(DxbcTranslator.ResourceBase + slot), visibility = stage,
                    buffer = new WGPUBufferBindingLayout { type = WGPUBufferBindingType.ReadOnlyStorage },
                });
            foreach (var (slot, u) in b.Uavs)
            {
                var e = new WGPUBindGroupLayoutEntry { binding = (uint)(DxbcTranslator.UavBase + slot), visibility = stage };
                if (u.Kind == 't' && u.Dim != 1 && !b.MirroredUavs.Contains(slot))
                {
                    var view = uavs != null && slot < uavs.Length ? uavs[slot] : null;
                    e.storageTexture = new WGPUStorageTextureBindingLayout
                    {
                        access = b.UavReads.Contains(slot) ? 4 /* ReadWrite */ : 2 /* WriteOnly */,
                        format = view?.Format ?? (u.Ret switch { 'i' => TexFmt.R32Sint, 'u' => TexFmt.R32Uint, _ => TexFmt.R32Float }),
                        viewDimension = u.Dim switch { 7 or 8 => WGPUTextureViewDimension._2DArray, 5 => WGPUTextureViewDimension._3D, _ => WGPUTextureViewDimension._2D },
                    };
                }
                else e.buffer = new WGPUBufferBindingLayout { type = WGPUBufferBindingType.Storage };
                entries.Add(e);
            }
            foreach (int slot in b.Counters)
                entries.Add(new WGPUBindGroupLayoutEntry
                {
                    binding = (uint)(DxbcTranslator.CounterBase + slot), visibility = stage,
                    buffer = new WGPUBufferBindingLayout { type = WGPUBufferBindingType.Storage, minBindingSize = 4 },
                });
            if (b.DrawParams)
                entries.Add(new WGPUBindGroupLayoutEntry
                {
                    binding = DxbcTranslator.DrawParamsBinding, visibility = stage,
                    buffer = new WGPUBufferBindingLayout { type = WGPUBufferBindingType.Uniform, minBindingSize = 16 },
                });
            foreach (var (slot, size) in b.ConstantBuffers)
                entries.Add(new WGPUBindGroupLayoutEntry
                {
                    binding = (uint)slot, visibility = stage,
                    buffer = new WGPUBufferBindingLayout { type = WGPUBufferBindingType.Uniform, minBindingSize = (ulong)size * 16 },
                });
            foreach (var (slot, cmp) in b.Samplers)
                entries.Add(new WGPUBindGroupLayoutEntry
                {
                    binding = (uint)(DxbcTranslator.SamplerBase + slot), visibility = stage,
                    sampler = new WGPUSamplerBindingLayout { type = cmp ? WGPUSamplerBindingType.Comparison : WGPUSamplerBindingType.Filtering },
                });
            foreach (var (slot, r) in b.Resources)
                entries.Add(r.Dim == 1 ? new WGPUBindGroupLayoutEntry
                {
                    binding = (uint)(DxbcTranslator.ResourceBase + slot), visibility = stage,
                    buffer = new WGPUBufferBindingLayout { type = WGPUBufferBindingType.ReadOnlyStorage, minBindingSize = 0 },
                } : new WGPUBindGroupLayoutEntry
                {
                    binding = (uint)(DxbcTranslator.ResourceBase + slot), visibility = stage,
                    texture = new WGPUTextureBindingLayout
                    {
                        sampleType = r.Depth ? WGPUTextureSampleType.Depth : r.Kind switch { 'i' => WGPUTextureSampleType.Sint, 'u' => WGPUTextureSampleType.Uint, _ => WGPUTextureSampleType.Float },
                        viewDimension = r.Dim switch { 6 => WGPUTextureViewDimension.Cube, 10 => WGPUTextureViewDimension.CubeArray, 5 => WGPUTextureViewDimension._3D, 7 or 8 => WGPUTextureViewDimension._2DArray, 2 => WGPUTextureViewDimension._1D, _ => WGPUTextureViewDimension._2D },
                        multisampled = r.Dim is 4 or 9 && !(D3DDevice.MsReadsResolved && !r.Depth) ? 1u : 0u,
                    },
                });
            var arr = entries.ToArray();
            fixed (WGPUBindGroupLayoutEntry* p = arr)
            {
                var d = new WGPUBindGroupLayoutDescriptor { entryCount = (nuint)arr.Length, entries = p };
                return wgpuDeviceCreateBindGroupLayout(Device.Gpu.Device, &d);
            }
        }

        // ---- pipelines -------------------------------------------------------------------------------

        private sealed class PipelineEntry { public IntPtr Pipeline; }
        private readonly Dictionary<string, PipelineEntry> _pipelines = new();

        private static WGPUBlendFactor Factor(uint b) => b switch
        {
            1 => WGPUBlendFactor.Zero, 2 => WGPUBlendFactor.One, 3 => WGPUBlendFactor.Src, 4 => WGPUBlendFactor.OneMinusSrc,
            5 => WGPUBlendFactor.SrcAlpha, 6 => WGPUBlendFactor.OneMinusSrcAlpha, 7 => WGPUBlendFactor.DstAlpha, 8 => WGPUBlendFactor.OneMinusDstAlpha,
            9 => WGPUBlendFactor.Dst, 10 => WGPUBlendFactor.OneMinusDst, 11 => WGPUBlendFactor.SrcAlphaSaturated,
            14 => (WGPUBlendFactor)0xC /*Constant*/, 15 => (WGPUBlendFactor)0xD /*OneMinusConstant*/,
            16 => (WGPUBlendFactor)0xE, 17 => (WGPUBlendFactor)0xF, 18 => (WGPUBlendFactor)0x10, 19 => (WGPUBlendFactor)0x11,
            _ => WGPUBlendFactor.One,
        };

        private static WGPUBlendFactor AlphaFactor(uint b) => b switch { 3 => WGPUBlendFactor.SrcAlpha, 4 => WGPUBlendFactor.OneMinusSrcAlpha, 9 => WGPUBlendFactor.DstAlpha, 10 => WGPUBlendFactor.OneMinusDstAlpha, _ => Factor(b) };

        private static WGPUStencilOperation StencilOp(uint op) => (WGPUStencilOperation)(op switch { 1 => 1, 2 => 2, 3 => 3, 4 => 5, 5 => 6, 6 => 4, 7 => 7, 8 => 8, _ => 1 });

        /// <summary>The draw after a geometry shader: no vertex buffers, the GS's output topology.</summary>
        private sealed class GeometryDraw { public string Key = ""; public WGPUPrimitiveTopology Topology; }

        private IntPtr Pipeline(Translation t, D3DShader vs, D3DShader? ps, out string? why, GeometryDraw? gd = null)
        {
            why = null;
            var raster = _raster?.Desc ?? D3D11_RASTERIZER_DESC.Default();
            var blend = _blend?.Desc ?? D3D11_BLEND_DESC.Default();
            var depth = _depth?.Desc ?? D3D11_DEPTH_STENCIL_DESC.Default();
            var topo = _topology;
            var key = new StringBuilder(256);
            if (gd != null) key.Append("gs:").Append(gd.Key).Append('|').Append((int)gd.Topology).Append('|');
            else key.Append(vs.Code.Hash).Append('|').Append(ps?.Code.Hash ?? 0).Append('|').Append(_layout?.Id ?? 0).Append('|').Append((int)topo).Append('|');
            if (_layout != null && gd == null)
                foreach (int slot in _layout.Elements.Select(e => e.Slot).Distinct()) key.Append(slot).Append(':').Append(_vbStride[slot]).Append(',');
            if (gd == null && topo is D3D_PRIMITIVE_TOPOLOGY.LINESTRIP or D3D_PRIMITIVE_TOPOLOGY.TRIANGLESTRIP) key.Append("ib").Append((int)_ibFormat);
            key.Append('|').Append(raster.CullMode).Append(raster.FrontCounterClockwise).Append(raster.DepthBias).Append(raster.SlopeScaledDepthBias).Append(raster.DepthBiasClamp).Append(raster.DepthClipEnable);
            uint samples = 1;
            for (int i = 0; i < MaxTargets; i++)
            {
                var rt = _rtv[i];
                key.Append('|').Append(rt == null ? 0 : (int)rt.Format);
                if (rt != null)
                {
                    samples = rt.Texture.SampleCount;
                    var b = blend.Target(i);
                    key.Append(b.BlendEnable).Append(b.SrcBlend).Append(b.DestBlend).Append(b.BlendOp).Append(b.SrcBlendAlpha).Append(b.DestBlendAlpha).Append(b.BlendOpAlpha).Append(b.RenderTargetWriteMask);
                }
            }
            if (_dsv != null)
            {
                samples = _dsv.Texture.SampleCount;
                key.Append("|d").Append((int)_dsv.Texture.Format).Append(depth.DepthEnable).Append(depth.DepthWriteMask).Append(depth.DepthFunc).Append(depth.StencilEnable)
                   .Append(depth.StencilReadMask).Append(depth.StencilWriteMask)
                   .Append(depth.FrontFace.StencilFunc).Append(depth.FrontFace.StencilPassOp).Append(depth.FrontFace.StencilFailOp).Append(depth.FrontFace.StencilDepthFailOp)
                   .Append(depth.BackFace.StencilFunc).Append(depth.BackFace.StencilPassOp).Append(depth.BackFace.StencilFailOp).Append(depth.BackFace.StencilDepthFailOp);
            }
            key.Append("|s").Append(samples).Append(_sampleMask).Append(blend.AlphaToCoverageEnable);
            string k = key.ToString();
            if (_pipelines.TryGetValue(k, out var entry)) return entry.Pipeline;
            entry = new PipelineEntry();
            _pipelines[k] = entry;

            // vertex buffers: each layout element the vertex shader reads, at the location of its register
            var buffers = new List<WGPUVertexBufferLayout>();
            var attributes = new List<WGPUVertexAttribute[]>();
            var handles = new List<GCHandle>();
            try
            {
                if (_layout != null && gd == null)
                {
                    foreach (var group in _layout.Elements.GroupBy(e => e.Slot))
                    {
                        var attrs = new List<WGPUVertexAttribute>();
                        bool instance = false;
                        foreach (var e in group)
                        {
                            var sig = vs.Code.Inputs.FirstOrDefault(i => i.SystemValue == 0 && i.SemanticName.Equals(e.Semantic, StringComparison.OrdinalIgnoreCase) && i.SemanticIndex == e.Index);
                            if (sig == null) continue;
                            if (attrs.Any(a => a.shaderLocation == sig.Register)) continue;
                            attrs.Add(new WGPUVertexAttribute { format = Formats.Vertex(e.Format), offset = e.Offset, shaderLocation = (uint)sig.Register });
                            instance |= e.PerInstance;
                            if (e.PerInstance && e.StepRate > 1) D3DLog.Once("steprate", "instance data step rates above 1 are not supported");
                        }
                        if (attrs.Count == 0) continue;
                        var arr = attrs.ToArray();
                        var h = GCHandle.Alloc(arr, GCHandleType.Pinned);
                        handles.Add(h);
                        attributes.Add(arr);
                        buffers.Add(new WGPUVertexBufferLayout
                        {
                            stepMode = instance ? WGPUVertexStepMode.Instance : WGPUVertexStepMode.Vertex,
                            arrayStride = _vbStride[group.Key],
                            attributeCount = (nuint)arr.Length,
                            attributes = (WGPUVertexAttribute*)h.AddrOfPinnedObject(),
                        });
                    }
                }
                _slotOfBuffer = _layout == null ? Array.Empty<int>() : _layout.Elements.Select(e => e.Slot).Distinct()
                    .Where(s => buffers.Count > 0).ToArray();

                byte[] main = Encoding.UTF8.GetBytes("main");
                var bufArr = buffers.ToArray();
                var targets = new WGPUColorTargetState[MaxTargets];
                var blends = new WGPUBlendState[MaxTargets];
                int targetCount = 0;
                for (int i = 0; i < MaxTargets; i++) if (_rtv[i] != null) targetCount = i + 1;
                var psOutputs = ps?.Code.Outputs.Where(o => o.SystemValue == 64 || o.SemanticName.Equals("SV_Target", StringComparison.OrdinalIgnoreCase)).Select(o => o.SemanticIndex).ToHashSet() ?? new HashSet<int>();
                fixed (byte* m = main)
                fixed (WGPUVertexBufferLayout* vb = bufArr)
                fixed (WGPUColorTargetState* tg = targets)
                fixed (WGPUBlendState* bl = blends)
                {
                    for (int i = 0; i < targetCount; i++)
                    {
                        var rt = _rtv[i];
                        if (rt == null) { tg[i] = new WGPUColorTargetState { format = WGPUTextureFormat.Undefined }; continue; }
                        var b = blend.Target(i);
                        tg[i].format = rt.Format;
                        tg[i].writeMask = psOutputs.Contains(i) ? b.RenderTargetWriteMask : 0u;
                        if (b.BlendEnable != 0)
                        {
                            bl[i].color = new WGPUBlendComponent { operation = (WGPUBlendOperation)b.BlendOp, srcFactor = Factor(b.SrcBlend), dstFactor = Factor(b.DestBlend) };
                            bl[i].alpha = new WGPUBlendComponent { operation = (WGPUBlendOperation)b.BlendOpAlpha, srcFactor = AlphaFactor(b.SrcBlendAlpha), dstFactor = AlphaFactor(b.DestBlendAlpha) };
                            if (b.BlendOp is 4 or 5) { bl[i].color.srcFactor = bl[i].color.dstFactor = WGPUBlendFactor.One; }
                            if (b.BlendOpAlpha is 4 or 5) { bl[i].alpha.srcFactor = bl[i].alpha.dstFactor = WGPUBlendFactor.One; }
                            tg[i].blend = &bl[i];
                        }
                    }
                    var frag = new WGPUFragmentState { module = t.Ps, entryPoint = new WGPUStringView { data = m, length = 4 }, targetCount = (nuint)targetCount, targets = tg };
                    var ds = new WGPUDepthStencilState();
                    if (_dsv != null)
                    {
                        var f = _dsv.Texture.Format;
                        bool stencil = Formats.HasStencil(f) && depth.StencilEnable != 0;
                        ds.format = f;
                        ds.depthWriteEnabled = depth.DepthEnable != 0 && depth.DepthWriteMask != 0 && !_dsv.ReadOnlyDepth ? WGPUOptionalBool.True : WGPUOptionalBool.False;
                        ds.depthCompare = depth.DepthEnable != 0 ? (WGPUCompareFunction)Math.Clamp(depth.DepthFunc, 1u, 8u) : WGPUCompareFunction.Always;
                        ds.stencilFront = stencil ? new WGPUStencilFaceState { compare = (WGPUCompareFunction)depth.FrontFace.StencilFunc, failOp = StencilOp(depth.FrontFace.StencilFailOp), depthFailOp = StencilOp(depth.FrontFace.StencilDepthFailOp), passOp = StencilOp(depth.FrontFace.StencilPassOp) }
                                                  : new WGPUStencilFaceState { compare = WGPUCompareFunction.Always, failOp = WGPUStencilOperation.Keep, depthFailOp = WGPUStencilOperation.Keep, passOp = WGPUStencilOperation.Keep };
                        ds.stencilBack = stencil ? new WGPUStencilFaceState { compare = (WGPUCompareFunction)depth.BackFace.StencilFunc, failOp = StencilOp(depth.BackFace.StencilFailOp), depthFailOp = StencilOp(depth.BackFace.StencilDepthFailOp), passOp = StencilOp(depth.BackFace.StencilPassOp) }
                                                 : ds.stencilFront;
                        ds.stencilReadMask = stencil ? depth.StencilReadMask : 0xFFu;
                        ds.stencilWriteMask = stencil && !_dsv.ReadOnlyStencil ? depth.StencilWriteMask : 0u;
                        if (!Formats.HasStencil(f)) { ds.stencilReadMask = 0; ds.stencilWriteMask = 0; }
                        ds.depthBias = raster.DepthBias;
                        ds.depthBiasSlopeScale = raster.SlopeScaledDepthBias;
                        ds.depthBiasClamp = raster.DepthBiasClamp;
                    }
                    var desc = new WGPURenderPipelineDescriptor
                    {
                        layout = t.PipelineLayout,
                        vertex = new WGPUVertexState { module = t.Vs, entryPoint = new WGPUStringView { data = m, length = 4 }, bufferCount = (nuint)bufArr.Length, buffers = vb },
                        primitive = new WGPUPrimitiveState
                        {
                            topology = gd != null ? gd.Topology : topo switch
                            {
                                D3D_PRIMITIVE_TOPOLOGY.POINTLIST => WGPUPrimitiveTopology.PointList,
                                D3D_PRIMITIVE_TOPOLOGY.LINELIST => WGPUPrimitiveTopology.LineList,
                                D3D_PRIMITIVE_TOPOLOGY.LINESTRIP => WGPUPrimitiveTopology.LineStrip,
                                D3D_PRIMITIVE_TOPOLOGY.TRIANGLESTRIP => WGPUPrimitiveTopology.TriangleStrip,
                                _ => WGPUPrimitiveTopology.TriangleList,
                            },
                            stripIndexFormat = gd != null
                                ? (gd.Topology is WGPUPrimitiveTopology.LineStrip or WGPUPrimitiveTopology.TriangleStrip ? WGPUIndexFormat.Uint32 : WGPUIndexFormat.Undefined)
                                : topo is D3D_PRIMITIVE_TOPOLOGY.LINESTRIP or D3D_PRIMITIVE_TOPOLOGY.TRIANGLESTRIP && _ib != null
                                ? (_ibFormat == DXGI_FORMAT.R16_UINT ? WGPUIndexFormat.Uint16 : WGPUIndexFormat.Uint32) : WGPUIndexFormat.Undefined,
                            frontFace = raster.FrontCounterClockwise != 0 ? WGPUFrontFace.CCW : WGPUFrontFace.CW,
                            cullMode = raster.CullMode switch { 2 => WGPUCullMode.Front, 3 => WGPUCullMode.Back, _ => WGPUCullMode.None },
                            unclippedDepth = 0,
                        },
                        depthStencil = _dsv != null ? (IntPtr)(&ds) : IntPtr.Zero,
                        multisample = new WGPUMultisampleState { count = samples, mask = _sampleMask, alphaToCoverageEnabled = blend.AlphaToCoverageEnable != 0 ? 1u : 0u },
                        fragment = t.Ps != IntPtr.Zero ? &frag : null,
                    };
                    wgpuDevicePushErrorScope(Device.Gpu.Device, WGPUErrorFilter.Validation);
                    IntPtr created = wgpuDeviceCreateRenderPipeline(Device.Gpu.Device, &desc);
                    entry.Pipeline = PopScope(created, out why);
                }
            }
            finally
            {
                foreach (var h in handles) h.Free();
            }
            if (raster.FillMode == 2) D3DLog.Once("wire", "wireframe fill mode is drawn solid");
            return entry.Pipeline;
        }

        private int[] _slotOfBuffer = Array.Empty<int>();
        /// <summary>The draw a vertex shader's bind group is made for (ShaderBindings.DrawParams).</summary>
        private int _drawBaseVertex;
        private ulong _drawParamsAt = ulong.MaxValue;
        private uint _drawFirstInstance;

        // ---- constants and bind groups ----------------------------------------------------------------

        private readonly IntPtr[] _arenas = new IntPtr[1];
        private int _arenaIndex;
        private ulong _arenaOffset;
        private const ulong ArenaSize = 4 << 20;
        private IntPtr _defaultSampler, _defaultComparisonSampler;
        private readonly Dictionary<int, IntPtr> _dummyViews = new();

        private ulong ArenaWrite(D3DBuffer? cb, int vec4s)
        {
            var tmp = new byte[(ulong)vec4s * 16];
            if (cb != null && cb.Shadow != null) new ReadOnlySpan<byte>(cb.Shadow, (int)Math.Min((ulong)tmp.Length, cb.Size)).CopyTo(tmp);
            return ArenaWrite(tmp);
        }

        /// <summary>Bytes (a multiple of 16) into the uniform arena: their offset in _arenas[_arenaIndex].</summary>
        private ulong ArenaWrite(byte[] tmp)
        {
            ulong size = (ulong)tmp.Length;
            if (_arenaOffset + size > ArenaSize)
            {
                FlushToGpu(wait: false);           // a new submission: the arena is free to reuse from the start
            }
            if (_arenas[_arenaIndex] == IntPtr.Zero)
                _arenas[_arenaIndex] = Device.Gpu.CreateBuffer(ArenaSize, WGPUBufferUsage.Uniform | WGPUBufferUsage.CopyDst);
            ulong at = _arenaOffset;
            Device.Gpu.WriteBuffer(_arenas[_arenaIndex], at, tmp);
            _arenaOffset = (at + size + 255) & ~255UL;
            return at;
        }

        private IntPtr _dummyStorage;

        /// <summary>What an unbound typed buffer reads: 16 zero bytes.</summary>
        private IntPtr DummyStorage()
        {
            // writable too: an unbound UAV reads 0 and its writes are dropped (D3D's rule; here they land in this scratch)
            if (_dummyStorage == IntPtr.Zero) _dummyStorage = Device.Gpu.CreateBufferMapped(new byte[16], WGPUBufferUsage.Storage | WGPUBufferUsage.CopyDst);
            return _dummyStorage;
        }

        private IntPtr DummyView(int dim, char kind)
        {
            int key = dim * 4 + kind;
            if (_dummyViews.TryGetValue(key, out IntPtr v)) return v;
            uint layers = dim is 6 or 10 ? 6u : 1u;
            var fmt = kind switch { 'i' => TexFmt.RGBA8Sint, 'u' => TexFmt.RGBA8Uint, _ => TexFmt.RGBA8Unorm };
            var d = new WGPUTextureDescriptor
            {
                usage = WGPUTextureUsage.TextureBinding, dimension = dim == 5 ? WGPUTextureDimension._3D : WGPUTextureDimension._2D,
                size = new WGPUExtent3D { width = 1, height = 1, depthOrArrayLayers = layers }, format = fmt, mipLevelCount = 1, sampleCount = 1,
            };
            IntPtr tex = wgpuDeviceCreateTexture(Device.Gpu.Device, &d);
            var vd = dim switch { 6 => WGPUTextureViewDimension.Cube, 10 => WGPUTextureViewDimension.CubeArray, 5 => WGPUTextureViewDimension._3D, 7 or 8 => WGPUTextureViewDimension._2DArray, _ => WGPUTextureViewDimension._2D };
            v = D3DView.CreateTextureView(tex, fmt, vd, 0, 1, 0, layers);
            _dummyViews[key] = v;
            return v;
        }

        /// <summary>GL: a multisampled texture read by a shader is read through its resolved copy, resolved
        /// again when it was drawn or cleared since (D3DDevice.MsReadsResolved). Ends the pass.</summary>
        private void RefreshMsShadow(D3DTexture2D t)
        {
            if (t.MsShadowVersion == t.Version || t.MsShadow == IntPtr.Zero) return;
            ResolvePendingClears();
            var fmt = t.Format;
            IntPtr sv = D3DView.CreateTextureView(t.Gpu, fmt, WGPUTextureViewDimension._2D, 0, 1, 0, 1);
            IntPtr dv = D3DView.CreateTextureView(t.MsShadow, fmt, WGPUTextureViewDimension._2D, 0, 1, 0, 1);
            var att = new WGPURenderPassColorAttachment { view = sv, depthSlice = 0xFFFFFFFF, resolveTarget = dv, loadOp = WGPULoadOp.Load, storeOp = WGPUStoreOp.Store };
            var desc = new WGPURenderPassDescriptor { colorAttachmentCount = 1, colorAttachments = &att };
            IntPtr p = wgpuCommandEncoderBeginRenderPass(Encoder(), &desc);
            wgpuRenderPassEncoderEnd(p);
            wgpuRenderPassEncoderRelease(p);
            wgpuTextureViewRelease(sv);
            wgpuTextureViewRelease(dv);
            t.MsShadowVersion = t.Version;
            D3DLog.Once("msshadow", "GL: a multisampled texture read by a shader reads its resolved copy (every sample the pixel's resolved value)");
        }

        private IntPtr BindGroup(IntPtr layout, ShaderBindings b, StageState s, D3DUnorderedAccessView?[]? uavs = null)
        {
            var entries = new List<WGPUBindGroupEntry>();
            foreach (int slot in b.StorageResources)
            {
                // a raw or structured buffer view: its byte range (BUFFEREX: first element, count, flags)
                var srv = s.Resources[slot];
                if (srv?.Resource is D3DBuffer buf && buf.Gpu != IntPtr.Zero)
                {
                    bool raw = (srv.Desc.U2 & 1) != 0 || (buf.Desc.MiscFlags & D3D11_RESOURCE_MISC.BUFFER_STRUCTURED) == 0;
                    ulong element = raw ? 4UL : Math.Max(4UL, buf.Desc.StructureByteStride);
                    ulong first = srv.Desc.U0 * element, size = srv.Desc.U1 * element;
                    ulong whole = (ulong)buf.Size & ~3UL;
                    if (first >= whole) { first = 0; size = 0; }
                    if (size == 0 || first + size > whole) size = whole - first;
                    entries.Add(new WGPUBindGroupEntry { binding = (uint)(DxbcTranslator.ResourceBase + slot), buffer = buf.Gpu, offset = first, size = Math.Max(4UL, size & ~3UL) });
                }
                else entries.Add(new WGPUBindGroupEntry { binding = (uint)(DxbcTranslator.ResourceBase + slot), buffer = DummyStorage(), offset = 0, size = 16 });
            }
            if (b.Uavs.Count > 0) _storageWritten = true;
            foreach (var (slot, u) in b.Uavs)
            {
                var view = uavs != null && slot < uavs.Length ? uavs[slot] : null;
                uint binding = (uint)(DxbcTranslator.UavBase + slot);
                if (b.MirroredUavs.Contains(slot))
                {
                    if (view?.Resource is not D3DTexture2D) throw new InvalidOperationException($"u{slot}: no texture UAV bound to mirror");
                    entries.Add(new WGPUBindGroupEntry { binding = binding, buffer = view.MirrorBuffer(), offset = 0, size = view.MirrorBytes });
                    continue;
                }
                if (u.Kind == 't' && u.Dim != 1)
                {
                    if (view?.View is not IntPtr tv || tv == IntPtr.Zero) throw new InvalidOperationException($"u{slot}: no texture UAV bound (an unbound typed UAV texture has no WebGPU stand-in)");
                    entries.Add(new WGPUBindGroupEntry { binding = binding, textureView = tv });
                }
                else if (view?.Resource is D3DBuffer vb && vb.Gpu != IntPtr.Zero)
                    entries.Add(new WGPUBindGroupEntry { binding = binding, buffer = vb.Gpu, offset = view.Offset, size = Math.Max(4UL, view.Size & ~3UL) });
                else entries.Add(new WGPUBindGroupEntry { binding = binding, buffer = DummyStorage(), offset = 0, size = 16 });
            }
            foreach (int slot in b.Counters)
            {
                var view = uavs != null && slot < uavs.Length ? uavs[slot] : null;
                entries.Add(new WGPUBindGroupEntry { binding = (uint)(DxbcTranslator.CounterBase + slot), buffer = view?.Counter is IntPtr c && c != IntPtr.Zero ? c : DummyStorage(), offset = 0, size = 4 });
            }
            if (b.DrawParams)
            {
                var dp = new byte[16];
                BitConverter.TryWriteBytes(dp.AsSpan(0), _drawBaseVertex);
                BitConverter.TryWriteBytes(dp.AsSpan(4), _drawFirstInstance);
                ulong at = ArenaWrite(dp);
                _drawParamsAt = at;
                entries.Add(new WGPUBindGroupEntry { binding = DxbcTranslator.DrawParamsBinding, buffer = _arenas[_arenaIndex], offset = at, size = 16 });
            }
            foreach (var (slot, size) in b.ConstantBuffers)
            {
                ulong at = ArenaWrite(s.ConstantBuffers[slot], size);
                entries.Add(new WGPUBindGroupEntry { binding = (uint)slot, buffer = _arenas[_arenaIndex], offset = at, size = (ulong)size * 16 });
            }
            foreach (var (slot, cmp) in b.Samplers)
            {
                var smp = s.Samplers[slot];
                IntPtr sampler;
                if (smp != null) sampler = smp.Sampler(cmp);
                else
                {
                    var def = D3D11_SAMPLER_DESC.Default();
                    if (cmp) def.Filter |= 0x80;
                    sampler = cmp ? D3DSamplerState.SamplerFor(Device, def, true, ref _defaultComparisonSampler) : D3DSamplerState.SamplerFor(Device, def, false, ref _defaultSampler);
                }
                entries.Add(new WGPUBindGroupEntry { binding = (uint)(DxbcTranslator.SamplerBase + slot), sampler = sampler });
            }
            foreach (var (slot, r) in b.Resources)
            {
                var srv = s.Resources[slot];
                if (r.Dim == 1)
                {
                    // a typed buffer's view: its elements as words (see DxbcTranslator.BufferLoad)
                    if (srv?.Resource is D3DBuffer buf && buf.Gpu != IntPtr.Zero)
                    {
                        if (Formats.TexelBytes(srv.Desc.Format) != 16) D3DLog.Once("bufsrv:" + srv.Desc.Format, $"buffer shader resource view of {srv.Desc.Format}: read as 128-bit elements");
                        ulong first = (ulong)srv.Desc.U0 * 16, size = Math.Min((ulong)srv.Desc.U1 * 16, ((ulong)buf.Size & ~3UL) - Math.Min(first, (ulong)buf.Size & ~3UL));
                        if (size == 0) size = ((ulong)buf.Size & ~3UL) - first;
                        entries.Add(new WGPUBindGroupEntry { binding = (uint)(DxbcTranslator.ResourceBase + slot), buffer = buf.Gpu, offset = first, size = size });
                    }
                    else entries.Add(new WGPUBindGroupEntry { binding = (uint)(DxbcTranslator.ResourceBase + slot), buffer = DummyStorage(), offset = 0, size = 16 });
                    continue;
                }
                if (srv?.Resource is D3DTexture2D { MsShadowed: true } ms) RefreshMsShadow(ms);
                IntPtr view = srv != null && srv.Resource is D3DTexture2D ? srv.ViewFor(r.Dim) : DummyView(r.Dim, r.Kind);
                if (srv == null || srv.Resource is not D3DTexture2D) UnboundTextureReads++;
                else if (view == IntPtr.Zero) NullTextureViews++;
                entries.Add(new WGPUBindGroupEntry { binding = (uint)(DxbcTranslator.ResourceBase + slot), textureView = view });
            }
            var arr = entries.ToArray();
            fixed (WGPUBindGroupEntry* p = arr)
            {
                var d = new WGPUBindGroupDescriptor { layout = layout, entryCount = (nuint)arr.Length, entries = p };
                return wgpuDeviceCreateBindGroup(Device.Gpu.Device, &d);
            }
        }

        // ---- the pass ---------------------------------------------------------------------------------

        /// <summary>The slice of array render targets the pass draws to (a geometry shader's
        /// SV_RenderTargetArrayIndex: one pass per slice).</summary>
        private uint _passLayer;

        private bool EnsurePass()
        {
            bool same = _pass != IntPtr.Zero && (_dsv != null ? _dsv.ViewForLayer(_passLayer) : IntPtr.Zero) == _passDepth;
            for (int i = 0; same && i < MaxTargets; i++) same = (_rtv[i] != null ? _rtv[i]!.ViewForLayer(_passLayer) : IntPtr.Zero) == _passColor[i];
            if (same) return true;
            EndPass();
            int count = 0;
            for (int i = 0; i < MaxTargets; i++) if (_rtv[i] != null) count = i + 1;
            if (count == 0 && _dsv == null) return false;
            var atts = stackalloc WGPURenderPassColorAttachment[MaxTargets];
            for (int i = 0; i < count; i++)
            {
                var rt = _rtv[i];
                atts[i] = new WGPURenderPassColorAttachment { depthSlice = 0xFFFFFFFF };
                if (rt == null) continue;
                atts[i].view = rt.ViewForLayer(_passLayer);
                atts[i].storeOp = WGPUStoreOp.Store;
                if (_pendingColorClears.Remove(rt.ViewForLayer(_passLayer), out var color)) { atts[i].loadOp = WGPULoadOp.Clear; atts[i].clearValue = color; }
                else atts[i].loadOp = WGPULoadOp.Load;
                _passColor[i] = rt.ViewForLayer(_passLayer);
            }
            WGPURenderPassDepthStencilAttachment ds = default;
            if (_dsv != null)
            {
                _pendingDepthClears.Remove(_dsv.ViewForLayer(_passLayer), out var clear);
                clear.HasStencil = Formats.HasStencil(_dsv.Texture.Format);
                ds = DepthAttachment(_dsv.ViewForLayer(_passLayer), clear, readOnly: false);
                _passDepth = _dsv.ViewForLayer(_passLayer);
            }
            var desc = new WGPURenderPassDescriptor
            {
                colorAttachmentCount = (nuint)count, colorAttachments = atts,
                depthStencilAttachment = _dsv != null ? (IntPtr)(&ds) : IntPtr.Zero,
            };
            _pass = wgpuCommandEncoderBeginRenderPass(Encoder(), &desc);
            return _pass != IntPtr.Zero;
        }

        private (uint W, uint H) TargetSize()
        {
            for (int i = 0; i < MaxTargets; i++)
                if (_rtv[i] is { } rt) return (Math.Max(1, rt.Texture.Desc.Width >> (int)rt.Mip), Math.Max(1, rt.Texture.Desc.Height >> (int)rt.Mip));
            if (_dsv != null) return (Math.Max(1, _dsv.Texture.Desc.Width >> (int)_dsv.Mip), Math.Max(1, _dsv.Texture.Desc.Height >> (int)_dsv.Mip));
            return (1, 1);
        }

        private void DrawImpl(DrawArgs a)
        {
            // an indirect draw through the compute paths needs its counts here: read back (it waits)
            // and so does one with a start instance when the adapter lacks indirect-first-instance (WebGPU
            // would drop the draw)
            if (a.Indirect != null && (_gs.Shader != null || _hs.Shader != null || _ds.Shader != null || !D3DDevice.HasFeature(D3DDevice.FeatureIndirectFirstInstance)))
            {
                a = ReadIndirect(a);
                if (a.Indirect != null) { DrawsSkipped++; return; }
            }
            if (a.Indirect == null && (a.Count == 0 || a.Instances == 0)) return;
            var vs = _vs.Shader;
            if (vs == null) { DrawsSkipped++; return; }
            if (_hs.Shader != null || _ds.Shader != null) { DrawTessellated(a, vs); return; }
            if (_gs.Shader is { } gs)
            {
                if (gs.StreamOutput == null || gs.Code.Stage == ShaderStage.Geometry) { DrawGeometry(a, vs, gs); return; }
                // a vertex shader's stream output (a pass-through GS); rasterized as without a GS
                if (gs.Code.Stage == ShaderStage.Vertex) VertexStreamOut(a, vs, gs.StreamOutput);
                else D3DLog.Once("sods", "stream output of a domain shader is not written");
                if (gs.StreamOutput.RasterizedStream != 0 || (_dsv == null && _rtv.All(r => r == null))) { DrawsIssued++; return; }
            }
            var t = Translate(vs, _ps.Shader);
            if (t.Error != null) { DrawsSkipped++; return; }
            IntPtr pipeline = Pipeline(t, vs, _ps.Shader, out string? why);
            if (pipeline == IntPtr.Zero) { DrawsSkipped++; return; }

            // bind groups before the pass: the arena writes are queue writes
            // what WebGPU adds to its indices and D3D does not: indexed, the base vertex; not, the start vertex (WARP)
            _drawBaseVertex = a.Indexed ? a.BaseVertex : (int)a.First;
            _drawFirstInstance = a.FirstInstance;
            _drawParamsAt = ulong.MaxValue;
            IntPtr g0 = BindGroup(t.VsLayout, t.VsBindings!, _vs);
            IntPtr g1 = BindGroup(t.PsLayout, t.PsBindings ?? new ShaderBindings(), _ps);
            if (a.Indirect != null && _drawParamsAt != ulong.MaxValue)
            {
                // the vertex shader's base vertex and start instance are in the arguments: copied there
                EndPass();
                IntPtr arena = _arenas[_arenaIndex];
                if (a.Indexed) wgpuCommandEncoderCopyBufferToBuffer(Encoder(), a.Indirect.Gpu, a.IndirectOffset + 12, arena, _drawParamsAt, 8);
                else wgpuCommandEncoderCopyBufferToBuffer(Encoder(), a.Indirect.Gpu, a.IndirectOffset + 8, arena, _drawParamsAt, 8);
            }
            if (!EnsurePass()) { wgpuBindGroupRelease(g0); wgpuBindGroupRelease(g1); DrawsSkipped++; return; }

            wgpuRenderPassEncoderSetPipeline(_pass, pipeline);
            wgpuRenderPassEncoderSetBindGroup(_pass, 0, g0, 0, null);
            wgpuRenderPassEncoderSetBindGroup(_pass, 1, g1, 0, null);
            wgpuBindGroupRelease(g0);
            wgpuBindGroupRelease(g1);

            // vertex buffers in the order the pipeline lists them (by slot)
            if (_layout != null)
            {
                uint index = 0;
                foreach (var group in _layout.Elements.GroupBy(e => e.Slot))
                {
                    bool used = group.Any(e => vs.Code.Inputs.Any(i => i.SystemValue == 0 && i.SemanticName.Equals(e.Semantic, StringComparison.OrdinalIgnoreCase) && i.SemanticIndex == e.Index));
                    if (!used) continue;
                    var vb = _vb[group.Key];
                    if (vb == null || vb.Gpu == IntPtr.Zero) { D3DLog.Once("novb", $"draw without a vertex buffer in slot {group.Key}"); DrawsSkipped++; return; }
                    ulong off = _vbOffset[group.Key];
                    wgpuRenderPassEncoderSetVertexBuffer(_pass, index++, vb.Gpu, off, vb.Size > off ? vb.Size - off : 0);
                }
            }

            if (!ApplyRasterState()) { DrawsSkipped++; return; }

            if (a.Indexed)
            {
                if (_ib == null || _ib.Gpu == IntPtr.Zero) { DrawsSkipped++; return; }
                var fmt = _ibFormat == DXGI_FORMAT.R16_UINT ? WGPUIndexFormat.Uint16 : WGPUIndexFormat.Uint32;
                wgpuRenderPassEncoderSetIndexBuffer(_pass, _ib.Gpu, fmt, _ibOffset, _ib.Size > _ibOffset ? _ib.Size - _ibOffset : 0);
                if (a.Indirect != null) wgpuRenderPassEncoderDrawIndexedIndirect(_pass, a.Indirect.Gpu, a.IndirectOffset);
                else wgpuRenderPassEncoderDrawIndexed(_pass, a.Count, a.Instances, a.First, a.BaseVertex, a.FirstInstance);
            }
            else if (a.Indirect != null) wgpuRenderPassEncoderDrawIndirect(_pass, a.Indirect.Gpu, a.IndirectOffset);
            else wgpuRenderPassEncoderDraw(_pass, a.Count, a.Instances, a.First, a.FirstInstance);
            DrawsIssued++;
            for (int i = 0; i < MaxTargets; i++) if (_rtv[i] != null) _rtv[i]!.Texture.Version++;
        }

        /// <summary>The open pass's viewport, scissor, blend constant and stencil reference; false when
        /// the viewport has no area on the targets.</summary>
        private bool ApplyRasterState(int viewport = 0)
        {
            var (tw, th) = TargetSize();
            var vp = _viewportCount > viewport ? _viewports[viewport] : new D3D11_VIEWPORT { Width = tw, Height = th, MaxDepth = 1 };
            float x = Math.Max(0, vp.TopLeftX), y = Math.Max(0, vp.TopLeftY);
            float w = Math.Min(vp.Width, tw - x), h = Math.Min(vp.Height, th - y);
            if (w <= 0 || h <= 0) return false;
            if (x != vp.TopLeftX || y != vp.TopLeftY || w != vp.Width || h != vp.Height) D3DLog.Once("vpclip", "a viewport past its target was cut to it");
            wgpuRenderPassEncoderSetViewport(_pass, x, y, w, h, Math.Clamp(vp.MinDepth, 0, 1), Math.Clamp(vp.MaxDepth, 0, 1));
            var raster = _raster?.Desc ?? D3D11_RASTERIZER_DESC.Default();
            if (raster.ScissorEnable != 0 && _scissorCount > viewport)
            {
                var r = _scissors[viewport];
                int l = Math.Clamp(r.left, 0, (int)tw), tp = Math.Clamp(r.top, 0, (int)th), rr = Math.Clamp(r.right, l, (int)tw), b = Math.Clamp(r.bottom, tp, (int)th);
                wgpuRenderPassEncoderSetScissorRect(_pass, (uint)l, (uint)tp, (uint)(rr - l), (uint)(b - tp));
            }
            else wgpuRenderPassEncoderSetScissorRect(_pass, 0, 0, tw, th);
            var bf = _blendFactor;
            wgpuRenderPassEncoderSetBlendConstant(_pass, &bf);
            wgpuRenderPassEncoderSetStencilReference(_pass, _stencilRef);
            return true;
        }
    }
}
