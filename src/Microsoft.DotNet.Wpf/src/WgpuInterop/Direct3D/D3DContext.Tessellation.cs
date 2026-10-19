// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A draw with hull and domain shaders bound (Documentation/d3d11-webgpu-shim.md, section 6; the
// translator's side is DxbcTranslator.Tessellation.cs). The vertex shader and the hull shader's two
// kernels run as compute; the tess factors come back to the CPU, where the fixed-function
// tessellator (D3DTessellator) makes each patch's domain points and triangles; an indexed draw of
// those with the domain shader as its vertex shader rasterizes them.
//
// The read-back makes such a draw wait for the GPU (the tessellator's output decides the draw's
// size). Patterns are cached by their factors, so a scene of like patches tessellates once.
//

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Wpf.Interop.WebGpu.Direct3D.Dxbc;
using static Microsoft.Wpf.Interop.WebGpu.Wgpu;
using static Microsoft.Wpf.Interop.WebGpu.Direct3D.WgpuCompute;

namespace Microsoft.Wpf.Interop.WebGpu.Direct3D
{
    internal sealed unsafe partial class D3DContext
    {
        private sealed class TessellationTranslation
        {
            public string? Error;
            public HullInfo Info = new();
            public int VsRegs, CpRegs, PcRegs;
            public ShaderBindings? VsBindings, HsBindings;
            public IntPtr VsLayout, VsPlumbing, VsPipeline;
            public IntPtr HsLayout, HsPlumbing, CpPipeline, PcPipeline;
            public IntPtr DomainPlumbing;
            public SortedSet<int> Slots = new();
            /// <summary>The draw: the domain shader (group 0) and pixel shader (group 1), the points (group 2).</summary>
            public readonly Translation Draw = new();
            public GeometryDraw Pipeline = new();
        }

        private readonly Dictionary<string, TessellationTranslation> _tessellations = new();
        private IntPtr _tsCp, _tsPc, _tsRead, _tsPoints, _tsIdx;
        private ulong _tsCpSize, _tsPcSize, _tsReadSize, _tsPointsSize, _tsIdxSize;
        private readonly D3DTessellator _tessellator = new();
        private readonly Dictionary<(int, int, int, uint, uint, uint, uint, uint, uint), (float[] U, float[] V, int[] I)> _patterns = new();

        private TessellationTranslation TranslateTessellation(D3DShader vs, D3DShader hs, D3DShader ds, D3DShader? ps)
        {
            string key = $"{vs.Code.Hash:x16}|{hs.Code.Hash:x16}|{ds.Code.Hash:x16}|{ps?.Code.Hash ?? 0:x16}|{_layout?.Id ?? 0}";
            if (_tessellations.TryGetValue(key, out var t)) return t;
            t = new TessellationTranslation();
            _tessellations[key] = t;
            t.Pipeline.Key = "ts:" + key;
            try
            {
                var fetch = (_layout?.Elements ?? Array.Empty<D3DInputLayout.Element>()).Select(e => new FetchElement
                {
                    Semantic = e.Semantic, Index = e.Index, Format = e.Format, Slot = e.Slot, Offset = e.Offset, PerInstance = e.PerInstance, StepRate = e.StepRate,
                }).ToList();
                var v = DxbcTranslator.TranslateVertexCompute(vs.Code, fetch);
                var h = DxbcTranslator.TranslateHull(hs.Code, vs.Code, v.OutputRegisters);
                var link = DxbcTranslator.LinkageOf(ps?.Code);
                var d = DxbcTranslator.TranslateDomain(ds.Code, hs.Code, h.ControlPointRegisters, h.PatchConstantRegisters, link);
                foreach (string a in v.Approximations.Concat(h.Approximations).Concat(d.Approximations)) D3DLog.Once("approx:" + a, "shader approximation: " + a);
                t.Info = HullInfo.Of(hs.Code);
                t.VsRegs = v.OutputRegisters;
                t.CpRegs = h.ControlPointRegisters;
                t.PcRegs = h.PatchConstantRegisters;
                t.VsBindings = v.Bindings;
                t.HsBindings = h.Bindings;
                foreach (var e in fetch) t.Slots.Add(e.Slot);

                t.VsLayout = BindGroupLayout(v.Bindings, WGPUShaderStage_Compute);
                t.VsPlumbing = PlumbingLayout(vertex: true, t.Slots);
                t.VsPipeline = ComputePipeline(v.Wgsl, "ts-vs", t.VsLayout, t.VsPlumbing, vs.Code, out string? err);
                if (t.VsPipeline == IntPtr.Zero) { t.Error = "vertex shader as compute: " + err; return t; }

                t.HsLayout = BindGroupLayout(h.Bindings, WGPUShaderStage_Compute);
                t.HsPlumbing = HullPlumbingLayout();
                t.CpPipeline = ComputePipeline(h.Wgsl, "hs", t.HsLayout, t.HsPlumbing, hs.Code, out err, "cp_main");
                if (t.CpPipeline == IntPtr.Zero) { t.Error = "hull shader (control points): " + err; return t; }
                t.PcPipeline = ComputePipeline(h.Wgsl, "hs", t.HsLayout, t.HsPlumbing, hs.Code, out err, "pc_main");
                if (t.PcPipeline == IntPtr.Zero) { t.Error = "hull shader (patch constants): " + err; return t; }

                var draw = t.Draw;
                draw.Vs = CreateModule(d.Wgsl, "ds", out err);
                if (draw.Vs == IntPtr.Zero) { t.Error = "domain shader: " + err; Dump(ds.Code, d.Wgsl, t.Error); return t; }
                draw.VsBindings = d.Bindings;
                if (ps != null)
                {
                    var p = DxbcTranslator.Translate(ps.Code, 1, link);
                    draw.PsBindings = p.Bindings;
                    draw.Ps = CreateModule(p.Wgsl, "ps", out err);
                    if (draw.Ps == IntPtr.Zero) { t.Error = "pixel shader: " + err; Dump(ps.Code, p.Wgsl, t.Error); return t; }
                }
                draw.VsLayout = BindGroupLayout(d.Bindings, WGPUShaderStage_Vertex);
                draw.PsLayout = BindGroupLayout(draw.PsBindings ?? new ShaderBindings(), WGPUShaderStage_Fragment);
                var entries = stackalloc WGPUBindGroupLayoutEntry[3];
                for (int i = 0; i < 3; i++)
                    entries[i] = new WGPUBindGroupLayoutEntry { binding = (uint)i, visibility = WGPUShaderStage_Vertex, buffer = new WGPUBufferBindingLayout { type = WGPUBufferBindingType.ReadOnlyStorage } };
                var ld = new WGPUBindGroupLayoutDescriptor { entryCount = 3, entries = entries };
                t.DomainPlumbing = wgpuDeviceCreateBindGroupLayout(Device.Gpu.Device, &ld);
                IntPtr* layouts = stackalloc IntPtr[3] { draw.VsLayout, draw.PsLayout, t.DomainPlumbing };
                var pl = new WGPUPipelineLayoutDescriptor { bindGroupLayoutCount = 3, bindGroupLayouts = layouts };
                draw.PipelineLayout = wgpuDeviceCreatePipelineLayout(Device.Gpu.Device, &pl);
                t.Pipeline.Topology = t.Info.Output switch
                {
                    TessOutput.Point => WGPUPrimitiveTopology.PointList,
                    TessOutput.Line => WGPUPrimitiveTopology.LineList,
                    _ => WGPUPrimitiveTopology.TriangleList,
                };
                if (t.Info.Domain == 1 && t.Info.Output is TessOutput.TriangleCw or TessOutput.TriangleCcw)
                    t.Pipeline.Topology = WGPUPrimitiveTopology.LineList;        // an isoline domain makes lines whatever is declared
            }
            catch (NotSupportedException e) { t.Error = e.Message; D3DLog.Once("tr:" + e.Message, $"tessellated draw not translated: {e.Message}"); }
            catch (Exception e) { t.Error = e.Message; D3DLog.Error("translate tessellation", e); }
            return t;
        }

        private IntPtr HullPlumbingLayout()
        {
            var e = stackalloc WGPUBindGroupLayoutEntry[4];
            e[0] = new() { binding = DxbcTranslator.PlumbingParams, visibility = WGPUShaderStage_Compute, buffer = new WGPUBufferBindingLayout { type = WGPUBufferBindingType.Uniform, minBindingSize = DxbcTranslator.ParamVec4s * 16 } };
            e[1] = new() { binding = DxbcTranslator.PlumbingVsOut, visibility = WGPUShaderStage_Compute, buffer = new WGPUBufferBindingLayout { type = WGPUBufferBindingType.ReadOnlyStorage } };
            e[2] = new() { binding = DxbcTranslator.PlumbingCpOut, visibility = WGPUShaderStage_Compute, buffer = new WGPUBufferBindingLayout { type = WGPUBufferBindingType.Storage } };
            e[3] = new() { binding = DxbcTranslator.PlumbingPcOut, visibility = WGPUShaderStage_Compute, buffer = new WGPUBufferBindingLayout { type = WGPUBufferBindingType.Storage } };
            var d = new WGPUBindGroupLayoutDescriptor { entryCount = 4, entries = e };
            return wgpuDeviceCreateBindGroupLayout(Device.Gpu.Device, &d);
        }

        /// <summary>The tess factors of patch <paramref name="patch"/> through the tessellator: its
        /// domain points and indices (cached by the factors).</summary>
        private (float[] U, float[] V, int[] I) Tessellate(HullInfo h, float[] pc, int pcRegs, int patch)
        {
            float F(int name) => h.Factors.TryGetValue(name, out var at) ? pc[(patch * pcRegs + at.Register) * 4 + at.Component] : 0f;
            float a, b, c = 0, d = 0, e = 0, f = 0;
            switch (h.Domain)
            {
                case 3: a = F(HullInfo.QuadUeq0); b = F(HullInfo.QuadVeq0); c = F(HullInfo.QuadUeq1); d = F(HullInfo.QuadVeq1); e = F(HullInfo.QuadInsideU); f = F(HullInfo.QuadInsideV); break;
                case 2: a = F(HullInfo.TriUeq0); b = F(HullInfo.TriVeq0); c = F(HullInfo.TriWeq0); d = F(HullInfo.TriInside); break;
                default: a = F(HullInfo.LineDensity); b = F(HullInfo.LineDetail); break;
            }
            static uint B(float x) => BitConverter.SingleToUInt32Bits(x);
            var key = (h.Domain, (int)h.Partitioning, (int)h.Output, B(a), B(b), B(c), B(d), B(e), B(f));
            if (_patterns.TryGetValue(key, out var hit)) return hit;
            var output = h.Domain == 1 && h.Output is TessOutput.TriangleCw or TessOutput.TriangleCcw ? TessOutput.Line : h.Output;
            _tessellator.Init(h.Partitioning, output);
            switch (h.Domain)
            {
                case 3: _tessellator.TessellateQuadDomain(a, b, c, d, e, f); break;
                case 2: _tessellator.TessellateTriDomain(a, b, c, d); break;
                default: _tessellator.TessellateIsoLineDomain(a, b); break;
            }
            var r = (_tessellator.PointsU.AsSpan(0, _tessellator.PointCount).ToArray(), _tessellator.PointsV.AsSpan(0, _tessellator.PointCount).ToArray(),
                     _tessellator.Indices.AsSpan(0, _tessellator.IndexCount).ToArray());
            if (_patterns.Count > 4096) _patterns.Clear();
            _patterns[key] = r;
            return r;
        }

        private void DrawTessellated(DrawArgs a, D3DShader vs)
        {
            var hs = _hs.Shader;
            var ds = _ds.Shader;
            if (hs == null || ds == null) { D3DLog.Once("tsmissing", "a draw with only one of the hull and domain shaders bound (D3D rejects it)"); DrawsSkipped++; return; }
            if (_gs.Shader != null) { D3DLog.Once("tsgs", "a geometry shader after tessellation is not drawn yet"); DrawsSkipped++; return; }
            var t = TranslateTessellation(vs, hs, ds, _ps.Shader);
            if (t.Error != null) { DrawsSkipped++; return; }
            var h = t.Info;
            int topo = (int)_topology;
            if (topo != 32 + h.InputControlPoints) { D3DLog.Once("tstopo:" + topo, $"topology {topo} does not match the hull shader's {h.InputControlPoints}-control-point patches (D3D rejects the draw)"); DrawsSkipped++; return; }
            if (a.Indexed && (_ib == null || _ib.Gpu == IntPtr.Zero)) { DrawsSkipped++; return; }
            uint perInstance = a.Count / (uint)h.InputControlPoints;
            if (perInstance == 0) return;
            ulong patches = (ulong)perInstance * a.Instances;
            ulong vsTotal = (ulong)a.Count * a.Instances, cpTotal = patches * (ulong)h.OutputControlPoints;
            ulong vsBytes = vsTotal * (ulong)t.VsRegs * 16, cpBytes = cpTotal * (ulong)t.CpRegs * 16, pcBytes = patches * (ulong)t.PcRegs * 16;
            if (vsBytes > MaxScratch || cpBytes > MaxScratch || pcBytes > MaxScratch) { D3DLog.Once("tsbig", "a tessellated draw too large for its scratch buffers was skipped"); DrawsSkipped++; return; }

            var p = new uint[DxbcTranslator.ParamVec4s * 4];
            p[0] = a.Count; p[1] = a.First; p[2] = (uint)a.BaseVertex; p[3] = a.FirstInstance;
            p[4] = a.Indexed ? 1u : 0u; p[5] = _ibOffset; p[6] = _ibFormat == DXGI_FORMAT.R16_UINT ? 1u : 0u; p[7] = a.Instances;
            p[8] = (uint)vsTotal; p[9] = perInstance;
            p[12] = (uint)cpTotal; p[13] = (uint)patches;
            foreach (int s in t.Slots) { p[(4 + s) * 4] = _vbOffset[s]; p[(4 + s) * 4 + 1] = _vbStride[s]; }
            var bytes = new byte[p.Length * 4];
            Buffer.BlockCopy(p, 0, bytes, 0, bytes.Length);

            IntPtr vsOut = Scratch(ref _gsVsOut, ref _gsVsOutSize, vsBytes, 0);
            IntPtr cpOut = Scratch(ref _tsCp, ref _tsCpSize, cpBytes, 0);
            IntPtr pcOut = Scratch(ref _tsPc, ref _tsPcSize, pcBytes, WGPUBufferUsage.CopySrc);
            ulong at = ArenaWrite(bytes);
            IntPtr arena = _arenas[_arenaIndex];
            var groups = new List<IntPtr>();
            try
            {
                groups.Add(BindGroup(t.VsLayout, t.VsBindings!, _vs));
                var ve = new List<WGPUBindGroupEntry>
                {
                    new() { binding = DxbcTranslator.PlumbingParams, buffer = arena, offset = at, size = DxbcTranslator.ParamVec4s * 16 },
                    new() { binding = DxbcTranslator.PlumbingVsOut, buffer = vsOut, offset = 0, size = Math.Max(16, vsBytes) },
                    a.Indexed && _ib!.Size >= 4
                        ? new() { binding = DxbcTranslator.PlumbingIndices, buffer = _ib.Gpu, offset = 0, size = (ulong)_ib.Size & ~3UL }
                        : new() { binding = DxbcTranslator.PlumbingIndices, buffer = DummyStorage(), offset = 0, size = 16 },
                };
                foreach (int s in t.Slots)
                {
                    var vb = _vb[s];
                    ve.Add(vb != null && vb.Gpu != IntPtr.Zero && vb.Size >= 4
                        ? new() { binding = (uint)(DxbcTranslator.PlumbingVertexBuffer + s), buffer = vb.Gpu, offset = 0, size = (ulong)vb.Size & ~3UL }
                        : new() { binding = (uint)(DxbcTranslator.PlumbingVertexBuffer + s), buffer = DummyStorage(), offset = 0, size = 16 });
                }
                groups.Add(MakeGroup(t.VsPlumbing, ve));
                groups.Add(BindGroup(t.HsLayout, t.HsBindings!, _hs));
                groups.Add(MakeGroup(t.HsPlumbing, new List<WGPUBindGroupEntry>
                {
                    new() { binding = DxbcTranslator.PlumbingParams, buffer = arena, offset = at, size = DxbcTranslator.ParamVec4s * 16 },
                    new() { binding = DxbcTranslator.PlumbingVsOut, buffer = vsOut, offset = 0, size = Math.Max(16, vsBytes) },
                    new() { binding = DxbcTranslator.PlumbingCpOut, buffer = cpOut, offset = 0, size = Math.Max(16, cpBytes) },
                    new() { binding = DxbcTranslator.PlumbingPcOut, buffer = pcOut, offset = 0, size = Math.Max(16, pcBytes) },
                }));
            }
            catch (InvalidOperationException e)
            {
                foreach (IntPtr g in groups) wgpuBindGroupRelease(g);
                D3DLog.Once("tsbind:" + e.Message, "tessellated draw skipped: " + e.Message);
                DrawsSkipped++;
                return;
            }

            // 1 and 2: the vertex shader and both hull kernels, in one compute pass
            EndPass();
            IntPtr pass = wgpuCommandEncoderBeginComputePass(Encoder(), null);
            wgpuComputePassEncoderSetPipeline(pass, t.VsPipeline);
            wgpuComputePassEncoderSetBindGroup(pass, 0, groups[0], 0, null);
            wgpuComputePassEncoderSetBindGroup(pass, 1, groups[1], 0, null);
            Dispatch1D(pass, vsTotal);
            wgpuComputePassEncoderSetPipeline(pass, t.CpPipeline);
            wgpuComputePassEncoderSetBindGroup(pass, 0, groups[2], 0, null);
            wgpuComputePassEncoderSetBindGroup(pass, 1, groups[3], 0, null);
            Dispatch1D(pass, cpTotal);
            wgpuComputePassEncoderSetPipeline(pass, t.PcPipeline);
            Dispatch1D(pass, patches);
            wgpuComputePassEncoderEnd(pass);
            wgpuComputePassEncoderRelease(pass);
            foreach (IntPtr g in groups) wgpuBindGroupRelease(g);
            groups.Clear();

            // 3: the tess factors back, and the tessellator over each patch
            if (_tsRead == IntPtr.Zero || _tsReadSize < pcBytes)
            {
                if (_tsRead != IntPtr.Zero) wgpuBufferRelease(_tsRead);
                _tsReadSize = Math.Max(4096, pcBytes);
                _tsRead = Device.Gpu.CreateBuffer(_tsReadSize, WGPUBufferUsage.MapRead | WGPUBufferUsage.CopyDst);
            }
            wgpuCommandEncoderCopyBufferToBuffer(Encoder(), pcOut, 0, _tsRead, 0, pcBytes);
            FlushToGpu(wait: true);
            byte[] pcData = Device.Gpu.MapRead(_tsRead, pcBytes);
            var pc = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(pcData).ToArray();
            var points = new List<uint>();
            var indices = new List<uint>();
            for (int patch = 0; patch < (int)patches; patch++)
            {
                var (u, v, idx) = Tessellate(h, pc, t.PcRegs, patch);
                uint first = (uint)(points.Count / 4);
                for (int k = 0; k < u.Length; k++)
                {
                    points.Add(BitConverter.SingleToUInt32Bits(u[k])); points.Add(BitConverter.SingleToUInt32Bits(v[k]));
                    points.Add((uint)patch); points.Add((uint)(patch % perInstance));
                }
                foreach (int i in idx) indices.Add(first + (uint)i);
            }
            if (indices.Count == 0) { DrawsIssued++; return; }       // every patch culled
            ulong pointBytes = (ulong)points.Count * 4, idxBytes = (ulong)indices.Count * 4;
            if (pointBytes > MaxScratch || idxBytes > MaxScratch) { D3DLog.Once("tsbig", "a tessellated draw too large for its scratch buffers was skipped"); DrawsSkipped++; return; }
            IntPtr pts = Scratch(ref _tsPoints, ref _tsPointsSize, pointBytes, 0);
            IntPtr ib = Scratch(ref _tsIdx, ref _tsIdxSize, idxBytes, WGPUBufferUsage.Index);
            Device.Gpu.WriteBuffer(pts, 0, System.Runtime.InteropServices.MemoryMarshal.AsBytes(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(points)));
            Device.Gpu.WriteBuffer(ib, 0, System.Runtime.InteropServices.MemoryMarshal.AsBytes(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(indices)));

            // 4: the domain shader's draw
            try
            {
                groups.Add(BindGroup(t.Draw.VsLayout, t.Draw.VsBindings!, _ds));
                groups.Add(BindGroup(t.Draw.PsLayout, t.Draw.PsBindings ?? new ShaderBindings(), _ps));
                groups.Add(MakeGroup(t.DomainPlumbing, new List<WGPUBindGroupEntry>
                {
                    new() { binding = DxbcTranslator.DomainPoints, buffer = pts, offset = 0, size = pointBytes },
                    new() { binding = DxbcTranslator.DomainCps, buffer = cpOut, offset = 0, size = Math.Max(16, cpBytes) },
                    new() { binding = DxbcTranslator.DomainPcs, buffer = pcOut, offset = 0, size = Math.Max(16, pcBytes) },
                }));
            }
            catch (InvalidOperationException e)
            {
                foreach (IntPtr g in groups) wgpuBindGroupRelease(g);
                D3DLog.Once("tsbind:" + e.Message, "tessellated draw skipped: " + e.Message);
                DrawsSkipped++;
                return;
            }
            IntPtr pipeline = Pipeline(t.Draw, vs, _ps.Shader, out _, t.Pipeline);
            if (pipeline == IntPtr.Zero || !EnsurePass() || !ApplyRasterState()) { foreach (IntPtr g in groups) wgpuBindGroupRelease(g); DrawsSkipped++; return; }
            wgpuRenderPassEncoderSetPipeline(_pass, pipeline);
            for (int i = 0; i < 3; i++) wgpuRenderPassEncoderSetBindGroup(_pass, (uint)i, groups[i], 0, null);
            wgpuRenderPassEncoderSetIndexBuffer(_pass, ib, WGPUIndexFormat.Uint32, 0, idxBytes);
            wgpuRenderPassEncoderDrawIndexed(_pass, (uint)indices.Count, 1, 0, 0, 0);
            foreach (IntPtr g in groups) wgpuBindGroupRelease(g);
            DrawsIssued++;
            for (int i = 0; i < MaxTargets; i++) if (_rtv[i] != null) _rtv[i]!.Texture.Version++;
        }
    }
}
