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
                var link = DxbcTranslator.LinkageOf(ps?.Code, ds.Code);
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
            if (_gs.Shader is { } gsAfter && gsAfter.Code.Stage is not (ShaderStage.Geometry or ShaderStage.Domain))
            { D3DLog.Once("tsso", "stream output of a vertex shader with tessellation bound (D3D rejects it)"); DrawsSkipped++; return; }
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
            _storageWritten = true;
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
            var primPatch = new List<uint>();      // a geometry shader's SV_PrimitiveID: each primitive's patch
            int perPrim = h.Output switch { TessOutput.Point => 1, TessOutput.Line => 2, _ => 3 };
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
                for (int q = 0; q < idx.Length / perPrim; q++) primPatch.Add((uint)(patch % perInstance));
            }
            if (indices.Count == 0) { DrawsIssued++; return; }       // every patch culled
            int indexCount = indices.Count;
            if (_gs.Shader is { Code.Stage: ShaderStage.Geometry }) indices.AddRange(primPatch);   // read past the indices
            ulong pointBytes = (ulong)points.Count * 4, idxBytes = (ulong)indices.Count * 4;
            if (pointBytes > MaxScratch || idxBytes > MaxScratch) { D3DLog.Once("tsbig", "a tessellated draw too large for its scratch buffers was skipped"); DrawsSkipped++; return; }
            IntPtr pts = Scratch(ref _tsPoints, ref _tsPointsSize, pointBytes, 0);
            IntPtr ib = Scratch(ref _tsIdx, ref _tsIdxSize, idxBytes, WGPUBufferUsage.Index);
            Device.Gpu.WriteBuffer(pts, 0, System.Runtime.InteropServices.MemoryMarshal.AsBytes(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(points)));
            Device.Gpu.WriteBuffer(ib, 0, System.Runtime.InteropServices.MemoryMarshal.AsBytes(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(indices)));

            if (_gs.Shader is { Code.Stage: ShaderStage.Geometry } gsNext)
            {
                DrawTessellatedGeometry(t, ds, gsNext, pts, (ulong)points.Count / 4, ib, (ulong)indexCount, cpOut, cpBytes, pcOut, pcBytes);
                return;
            }
            if (_gs.Shader is { StreamOutput: { } dsSo })
            {
                // the domain shader's own stream output (CreateGeometryShaderWithStreamOutput of its
                // bytecode): as compute over the points, written by the tessellator's primitives
                DomainStreamOut(t, ds, dsSo, pts, (ulong)points.Count / 4, ib, (ulong)indexCount, cpOut, cpBytes, pcOut, pcBytes);
                if (dsSo.RasterizedStream != 0 || (_dsv == null && _rtv.All(r => r == null))) { DrawsIssued++; return; }
            }

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
            wgpuRenderPassEncoderDrawIndexed(_pass, (uint)indexCount, 1, 0, 0, 0);
            foreach (IntPtr g in groups) wgpuBindGroupRelease(g);
            DrawsIssued++;
            for (int i = 0; i < MaxTargets; i++) if (_rtv[i] != null) _rtv[i]!.Texture.Version++;
        }

        // ---- a geometry shader after tessellation ---------------------------------------------------------

        private sealed class TessellatedGeometry
        {
            public string? Error;
            public int DsRegs, GsRegs;
            public GeometryInfo Info = new();
            public ShaderBindings? DsBindings, GsBindings;
            public IntPtr DsLayout, DsPlumbing, DsPipeline, GsLayout, GsPlumbing, GsPipeline;
            public readonly Translation Draw = new();
            public GeometryDraw Pipeline = new();
            public bool SelectsLayer, SelectsViewport;
            public StreamOutputDesc? So;
            public IntPtr SoCount, SoScan, SoWrite;
        }

        private readonly Dictionary<string, TessellatedGeometry> _tessellatedGeometries = new();

        /// <summary>The domain shader as compute over the tessellator's points, the geometry shader as
        /// compute over its primitives (the tessellator's indices), and the pass-through draw.</summary>
        private void DrawTessellatedGeometry(TessellationTranslation tt, D3DShader ds, D3DShader gs, IntPtr points, ulong pointCount, IntPtr indices, ulong indexCount,
                                             IntPtr cpOut, ulong cpBytes, IntPtr pcOut, ulong pcBytes)
        {
            var ps = _ps.Shader;
            string key = $"{tt.Pipeline.Key}|{gs.Code.Hash:x16}|{gs.StreamOutput?.Key}";
            if (!_tessellatedGeometries.TryGetValue(key, out var t))
            {
                t = new TessellatedGeometry();
                _tessellatedGeometries[key] = t;
                t.Pipeline.Key = "tsgs:" + key;
                try
                {
                    t.Info = GeometryInfo.Of(gs.Code);
                    int want = tt.Info.Output switch { TessOutput.Point => 1, TessOutput.Line => 2, _ => tt.Info.Domain == 1 ? 2 : 3 };
                    if (t.Info.InputPrimitive != want) throw new NotSupportedException($"a geometry shader of input primitive {t.Info.InputPrimitive} after tessellation that makes primitive {want} (D3D rejects it)");
                    var dsc = DxbcTranslator.TranslateDomain(ds.Code, _hs.Shader!.Code, tt.CpRegs, tt.PcRegs, Array.Empty<LinkedElement>(), compute: true);
                    t.So = gs.StreamOutput;
                    var g = DxbcTranslator.TranslateGeometryCompute(gs.Code, ds.Code, dsc.OutputRegisters, new TranslationOptions { IndexedInput = true, StreamOutput = t.So });
                    int rs = Math.Max(0, t.So?.RasterizedStream ?? 0);
                    var link = DxbcTranslator.LinkageOf(ps?.Code, gs.Code, rs);
                    var pass = DxbcTranslator.PassThrough(gs.Code, link, new TranslationOptions { RasterizedStream = rs });
                    t.SelectsLayer = pass.SelectsLayer;
                    t.SelectsViewport = pass.SelectsViewport;
                    t.DsRegs = dsc.OutputRegisters;
                    t.GsRegs = g.OutputRegisters;
                    t.DsBindings = dsc.Bindings;
                    t.GsBindings = g.Bindings;
                    t.DsLayout = BindGroupLayout(dsc.Bindings, WGPUShaderStage_Compute);
                    var de = stackalloc WGPUBindGroupLayoutEntry[4];
                    for (int i = 0; i < 4; i++)
                        de[i] = new WGPUBindGroupLayoutEntry { binding = (uint)i, visibility = WGPUShaderStage_Compute, buffer = new WGPUBufferBindingLayout { type = i == DxbcTranslator.DomainOut ? WGPUBufferBindingType.Storage : WGPUBufferBindingType.ReadOnlyStorage } };
                    var dld = new WGPUBindGroupLayoutDescriptor { entryCount = 4, entries = de };
                    t.DsPlumbing = wgpuDeviceCreateBindGroupLayout(Device.Gpu.Device, &dld);
                    t.DsPipeline = ComputePipeline(dsc.Wgsl, "ds-cs", t.DsLayout, t.DsPlumbing, ds.Code, out string? err);
                    if (t.DsPipeline == IntPtr.Zero) { t.Error = "domain shader as compute: " + err; }
                    else
                    {
                        t.GsLayout = BindGroupLayout(g.Bindings, WGPUShaderStage_Compute);
                        var entries = new List<WGPUBindGroupLayoutEntry>
                        {
                            new() { binding = DxbcTranslator.PlumbingParams, visibility = WGPUShaderStage_Compute, buffer = new WGPUBufferBindingLayout { type = WGPUBufferBindingType.Uniform, minBindingSize = DxbcTranslator.ParamVec4s * 16 } },
                            new() { binding = DxbcTranslator.PlumbingVsOut, visibility = WGPUShaderStage_Compute, buffer = new WGPUBufferBindingLayout { type = WGPUBufferBindingType.ReadOnlyStorage } },
                            new() { binding = DxbcTranslator.PlumbingGsOut, visibility = WGPUShaderStage_Compute, buffer = new WGPUBufferBindingLayout { type = WGPUBufferBindingType.Storage } },
                            new() { binding = DxbcTranslator.PlumbingGsIndices, visibility = WGPUShaderStage_Compute, buffer = new WGPUBufferBindingLayout { type = WGPUBufferBindingType.Storage } },
                            new() { binding = DxbcTranslator.PlumbingInputIndices, visibility = WGPUShaderStage_Compute, buffer = new WGPUBufferBindingLayout { type = WGPUBufferBindingType.ReadOnlyStorage } },
                        };
                        if (t.So != null) AddStreamOutLayout(entries, t.So, counts: true);
                        var arr = entries.ToArray();
                        fixed (WGPUBindGroupLayoutEntry* pe = arr)
                        {
                            var ld = new WGPUBindGroupLayoutDescriptor { entryCount = (nuint)arr.Length, entries = pe };
                            t.GsPlumbing = wgpuDeviceCreateBindGroupLayout(Device.Gpu.Device, &ld);
                        }
                        t.GsPipeline = ComputePipeline(g.Wgsl, "gs", t.GsLayout, t.GsPlumbing, gs.Code, out err);
                        if (t.GsPipeline == IntPtr.Zero) t.Error = "geometry shader as compute: " + err;
                        else if (t.So != null)
                        {
                            t.SoCount = ComputePipeline(g.Wgsl, "gs", t.GsLayout, t.GsPlumbing, gs.Code, out err, "so_count");
                            t.SoScan = ComputePipeline(g.Wgsl, "gs", t.GsLayout, t.GsPlumbing, gs.Code, out err, "so_scan");
                            t.SoWrite = ComputePipeline(g.Wgsl, "gs", t.GsLayout, t.GsPlumbing, gs.Code, out err, "so_write");
                            if (t.SoCount == IntPtr.Zero || t.SoScan == IntPtr.Zero || t.SoWrite == IntPtr.Zero) t.Error = "stream output: " + err;
                        }
                    }
                    if (t.Error == null)
                    {
                        var d = t.Draw;
                        d.Vs = CreateModule(pass.Wgsl, "gs-pass", out err);
                        if (ps != null)
                        {
                            var p = DxbcTranslator.Translate(ps.Code, 1, link);
                            d.PsBindings = p.Bindings;
                            d.Ps = CreateModule(p.Wgsl, "ps", out err);
                        }
                        if (d.Vs == IntPtr.Zero || (ps != null && d.Ps == IntPtr.Zero)) t.Error = "pass-through or pixel shader: " + err;
                        var entry = stackalloc WGPUBindGroupLayoutEntry[2];
                        entry[0] = new WGPUBindGroupLayoutEntry { binding = 0, visibility = WGPUShaderStage_Vertex, buffer = new WGPUBufferBindingLayout { type = WGPUBufferBindingType.ReadOnlyStorage } };
                        entry[1] = new WGPUBindGroupLayoutEntry { binding = 1, visibility = WGPUShaderStage_Vertex, buffer = new WGPUBufferBindingLayout { type = WGPUBufferBindingType.Uniform, minBindingSize = 16 } };
                        var vld = new WGPUBindGroupLayoutDescriptor { entryCount = t.SelectsLayer || t.SelectsViewport ? 2u : 1u, entries = entry };
                        d.VsLayout = wgpuDeviceCreateBindGroupLayout(Device.Gpu.Device, &vld);
                        d.PsLayout = BindGroupLayout(d.PsBindings ?? new ShaderBindings(), WGPUShaderStage_Fragment);
                        IntPtr* layouts = stackalloc IntPtr[2] { d.VsLayout, d.PsLayout };
                        var pl = new WGPUPipelineLayoutDescriptor { bindGroupLayoutCount = 2, bindGroupLayouts = layouts };
                        d.PipelineLayout = wgpuDeviceCreatePipelineLayout(Device.Gpu.Device, &pl);
                        t.Pipeline.Topology = t.Info.OutputTopology switch { 1 => WGPUPrimitiveTopology.PointList, 3 => WGPUPrimitiveTopology.LineStrip, _ => WGPUPrimitiveTopology.TriangleStrip };
                    }
                }
                catch (NotSupportedException e) { t.Error = e.Message; D3DLog.Once("tr:" + e.Message, $"geometry shader after tessellation not translated: {e.Message}"); }
                catch (Exception e) { t.Error = e.Message; D3DLog.Error("translate tessellated geometry", e); }
            }
            if (t.Error != null) { DrawsSkipped++; return; }

            ulong prims = indexCount / (ulong)t.Info.InputVertices;
            if (prims == 0) { DrawsIssued++; return; }
            ulong gsTotal = prims * (ulong)t.Info.Instances;
            uint per = (uint)t.Info.IndicesPerInvocation, max = (uint)Math.Max(1, t.Info.MaxVertices), lanes = (uint)t.Info.Lanes(t.So);
            ulong dsBytes = pointCount * (ulong)t.DsRegs * 16, gsBytes = gsTotal * max * (ulong)t.GsRegs * 16, idxBytes = gsTotal * per * lanes * 4;
            if (dsBytes > MaxScratch || gsBytes > MaxScratch || idxBytes > MaxScratch) { D3DLog.Once("tsgsbig", "a geometry shader draw after tessellation too large for its scratch buffers was skipped"); DrawsSkipped++; return; }
            IntPtr dsOut = Scratch(ref _gsVsOut, ref _gsVsOutSize, dsBytes, 0);
            IntPtr gsOut = Scratch(ref _gsOut, ref _gsOutSize, gsBytes, 0);
            IntPtr gsIdx = Scratch(ref _gsIdx, ref _gsIdxSize, idxBytes, WGPUBufferUsage.Index);
            var p32 = new uint[DxbcTranslator.ParamVec4s * 4];
            p32[9] = (uint)prims; p32[12] = (uint)gsTotal;          // count 0: no draw instances to step over
            var soEntries = new List<WGPUBindGroupEntry>();
            if (t.So != null) p32[13] = AddStreamOutGroup(soEntries, t.So, Scratch(ref _soCounts, ref _soCountsSize, gsTotal * lanes * 4, 0), gsTotal * lanes * 4);
            var bytes = new byte[p32.Length * 4];
            Buffer.BlockCopy(p32, 0, bytes, 0, bytes.Length);
            ulong at = ArenaWrite(bytes);
            IntPtr arena = _arenas[_arenaIndex];
            var groups = new List<IntPtr>();
            try
            {
                groups.Add(BindGroup(t.DsLayout, t.DsBindings!, _ds));
                groups.Add(MakeGroup(t.DsPlumbing, new List<WGPUBindGroupEntry>
                {
                    new() { binding = DxbcTranslator.DomainPoints, buffer = points, offset = 0, size = pointCount * 16 },
                    new() { binding = DxbcTranslator.DomainCps, buffer = cpOut, offset = 0, size = Math.Max(16, cpBytes) },
                    new() { binding = DxbcTranslator.DomainPcs, buffer = pcOut, offset = 0, size = Math.Max(16, pcBytes) },
                    new() { binding = DxbcTranslator.DomainOut, buffer = dsOut, offset = 0, size = Math.Max(16, dsBytes) },
                }));
                groups.Add(BindGroup(t.GsLayout, t.GsBindings!, _gs));
                soEntries.AddRange(new WGPUBindGroupEntry[]
                {
                    new() { binding = DxbcTranslator.PlumbingParams, buffer = arena, offset = at, size = DxbcTranslator.ParamVec4s * 16 },
                    new() { binding = DxbcTranslator.PlumbingVsOut, buffer = dsOut, offset = 0, size = Math.Max(16, dsBytes) },
                    new() { binding = DxbcTranslator.PlumbingGsOut, buffer = gsOut, offset = 0, size = Math.Max(16, gsBytes) },
                    new() { binding = DxbcTranslator.PlumbingGsIndices, buffer = gsIdx, offset = 0, size = Math.Max(16, idxBytes) },
                    new() { binding = DxbcTranslator.PlumbingInputIndices, buffer = indices, offset = 0, size = Math.Max(4, (indexCount + prims) * 4) },
                });
                groups.Add(MakeGroup(t.GsPlumbing, soEntries));
                groups.Add(BindGroup(t.Draw.PsLayout, t.Draw.PsBindings ?? new ShaderBindings(), _ps));
            }
            catch (InvalidOperationException e)
            {
                foreach (IntPtr g in groups) wgpuBindGroupRelease(g);
                D3DLog.Once("tsgsbind:" + e.Message, "geometry shader draw after tessellation skipped: " + e.Message);
                DrawsSkipped++;
                return;
            }
            // stream output alone (no rasterized stream, or nothing to rasterize to) draws nothing
            bool targets = _dsv != null || _rtv.Any(r => r != null);
            bool render = (t.So == null || (t.So.RasterizedStream >= 0 && t.So.RasterizedStream < lanes)) && (targets || t.So == null);
            IntPtr pipeline = render ? Pipeline(t.Draw, ds, _ps.Shader, out _, t.Pipeline) : IntPtr.Zero;
            if (render && pipeline == IntPtr.Zero) { foreach (IntPtr g in groups) wgpuBindGroupRelease(g); DrawsSkipped++; return; }
            EndPass();
            _storageWritten = true;
            IntPtr cpass = wgpuCommandEncoderBeginComputePass(Encoder(), null);
            wgpuComputePassEncoderSetPipeline(cpass, t.DsPipeline);
            wgpuComputePassEncoderSetBindGroup(cpass, 0, groups[0], 0, null);
            wgpuComputePassEncoderSetBindGroup(cpass, 1, groups[1], 0, null);
            Dispatch1D(cpass, pointCount);
            wgpuComputePassEncoderSetPipeline(cpass, t.GsPipeline);
            wgpuComputePassEncoderSetBindGroup(cpass, 0, groups[2], 0, null);
            wgpuComputePassEncoderSetBindGroup(cpass, 1, groups[3], 0, null);
            Dispatch1D(cpass, gsTotal);
            if (t.So != null)
            {
                wgpuComputePassEncoderSetPipeline(cpass, t.SoCount);
                Dispatch1D(cpass, gsTotal);
                wgpuComputePassEncoderSetPipeline(cpass, t.SoScan);
                wgpuComputePassEncoderDispatchWorkgroups(cpass, 1, 1, 1);
                wgpuComputePassEncoderSetPipeline(cpass, t.SoWrite);
                Dispatch1D(cpass, gsTotal);
                BumpSoVersions(t.So);
            }
            wgpuComputePassEncoderEnd(cpass);
            wgpuComputePassEncoderRelease(cpass);
            ulong lane = t.So == null ? 0 : (ulong)t.So.RasterizedStream * gsTotal * per * 4;
            bool drew = !render || DrawExpanded(pipeline, t.Draw.VsLayout, groups[4], gsOut, gsBytes, gsIdx, idxBytes, (uint)(gsTotal * per), t.SelectsLayer, t.SelectsViewport, lane);
            foreach (IntPtr g in groups) wgpuBindGroupRelease(g);
            if (!drew) { DrawsSkipped++; return; }
            DrawsIssued++;
        }

        // ---- a domain shader's stream output ---------------------------------------------------------------

        private sealed class DomainStreamOutTranslation
        {
            public string? Error;
            public int DsRegs;
            public ShaderBindings? DsBindings;
            public IntPtr DsLayout, DsPlumbing, DsPipeline, SoLayout, SoScan, SoWrite;
        }

        private readonly Dictionary<string, DomainStreamOutTranslation> _domainStreamOuts = new();

        /// <summary>The domain shader as compute over the tessellator's points, and its stream output
        /// by the tessellator's primitives (points, lines or triangles, in the tessellator's order).</summary>
        private void DomainStreamOut(TessellationTranslation tt, D3DShader ds, StreamOutputDesc so, IntPtr points, ulong pointCount, IntPtr indices, ulong indexCount,
                                     IntPtr cpOut, ulong cpBytes, IntPtr pcOut, ulong pcBytes)
        {
            int vertices = tt.Info.Output switch { TessOutput.Point => 1, TessOutput.Line => 2, _ => 3 };
            string key = $"{tt.Pipeline.Key}|{so.Key}|{vertices}";
            if (!_domainStreamOuts.TryGetValue(key, out var t))
            {
                t = new DomainStreamOutTranslation();
                _domainStreamOuts[key] = t;
                try
                {
                    var dsc = DxbcTranslator.TranslateDomain(ds.Code, _hs.Shader!.Code, tt.CpRegs, tt.PcRegs, Array.Empty<LinkedElement>(), compute: true);
                    t.DsRegs = dsc.OutputRegisters;
                    t.DsBindings = dsc.Bindings;
                    t.DsLayout = BindGroupLayout(dsc.Bindings, WGPUShaderStage_Compute);
                    var de = stackalloc WGPUBindGroupLayoutEntry[4];
                    for (int i = 0; i < 4; i++)
                        de[i] = new WGPUBindGroupLayoutEntry { binding = (uint)i, visibility = WGPUShaderStage_Compute, buffer = new WGPUBufferBindingLayout { type = i == DxbcTranslator.DomainOut ? WGPUBufferBindingType.Storage : WGPUBufferBindingType.ReadOnlyStorage } };
                    var dld = new WGPUBindGroupLayoutDescriptor { entryCount = 4, entries = de };
                    t.DsPlumbing = wgpuDeviceCreateBindGroupLayout(Device.Gpu.Device, &dld);
                    t.DsPipeline = ComputePipeline(dsc.Wgsl, "ds-cs", t.DsLayout, t.DsPlumbing, ds.Code, out string? err);
                    if (t.DsPipeline == IntPtr.Zero) { t.Error = "domain shader as compute: " + err; return; }
                    string wgsl = DxbcTranslator.StreamOutFromVertex(ds.Code, t.DsRegs, so, vertices, indexed: true);
                    var entries = new List<WGPUBindGroupLayoutEntry>
                    {
                        new() { binding = DxbcTranslator.PlumbingParams, visibility = WGPUShaderStage_Compute, buffer = new WGPUBufferBindingLayout { type = WGPUBufferBindingType.Uniform, minBindingSize = DxbcTranslator.ParamVec4s * 16 } },
                        new() { binding = DxbcTranslator.PlumbingVsOut, visibility = WGPUShaderStage_Compute, buffer = new WGPUBufferBindingLayout { type = WGPUBufferBindingType.ReadOnlyStorage } },
                        new() { binding = DxbcTranslator.PlumbingInputIndices, visibility = WGPUShaderStage_Compute, buffer = new WGPUBufferBindingLayout { type = WGPUBufferBindingType.ReadOnlyStorage } },
                    };
                    AddStreamOutLayout(entries, so, counts: false);
                    var arr = entries.ToArray();
                    fixed (WGPUBindGroupLayoutEntry* p = arr)
                    {
                        var ld = new WGPUBindGroupLayoutDescriptor { entryCount = (nuint)arr.Length, entries = p };
                        t.SoLayout = wgpuDeviceCreateBindGroupLayout(Device.Gpu.Device, &ld);
                    }
                    IntPtr empty = BindGroupLayout(new ShaderBindings(), WGPUShaderStage_Compute);
                    t.SoScan = ComputePipeline(wgsl, "so", empty, t.SoLayout, ds.Code, out err, "so_scan");
                    t.SoWrite = ComputePipeline(wgsl, "so", empty, t.SoLayout, ds.Code, out err, "so_write");
                    if (t.SoScan == IntPtr.Zero || t.SoWrite == IntPtr.Zero) t.Error = "stream output: " + err;
                }
                catch (NotSupportedException e) { t.Error = e.Message; D3DLog.Once("tr:" + e.Message, $"stream output of a domain shader not translated: {e.Message}"); }
                catch (Exception e) { t.Error = e.Message; D3DLog.Error("translate domain stream output", e); }
            }
            if (t.Error != null) return;

            ulong prims = indexCount / (ulong)vertices;
            ulong dsBytes = pointCount * (ulong)t.DsRegs * 16;
            if (prims == 0) return;
            if (dsBytes > MaxScratch) { D3DLog.Once("dssobig", "stream output of a domain shader too large for its scratch buffers was skipped"); return; }
            var p32 = new uint[DxbcTranslator.ParamVec4s * 4];
            p32[9] = (uint)prims; p32[11] = (uint)vertices; p32[12] = (uint)prims;      // p[0].x 0: one "instance", indices absolute
            var soEntries = new List<WGPUBindGroupEntry>();
            p32[13] = AddStreamOutGroup(soEntries, so, IntPtr.Zero, 0);
            var bytes = new byte[p32.Length * 4];
            Buffer.BlockCopy(p32, 0, bytes, 0, bytes.Length);
            IntPtr dsOut = Scratch(ref _gsVsOut, ref _gsVsOutSize, dsBytes, 0);
            ulong at = ArenaWrite(bytes);
            IntPtr arena = _arenas[_arenaIndex];
            var groups = new List<IntPtr>();
            try
            {
                groups.Add(BindGroup(t.DsLayout, t.DsBindings!, _ds));
                groups.Add(MakeGroup(t.DsPlumbing, new List<WGPUBindGroupEntry>
                {
                    new() { binding = DxbcTranslator.DomainPoints, buffer = points, offset = 0, size = pointCount * 16 },
                    new() { binding = DxbcTranslator.DomainCps, buffer = cpOut, offset = 0, size = Math.Max(16, cpBytes) },
                    new() { binding = DxbcTranslator.DomainPcs, buffer = pcOut, offset = 0, size = Math.Max(16, pcBytes) },
                    new() { binding = DxbcTranslator.DomainOut, buffer = dsOut, offset = 0, size = Math.Max(16, dsBytes) },
                }));
                soEntries.InsertRange(0, new WGPUBindGroupEntry[]
                {
                    new() { binding = DxbcTranslator.PlumbingParams, buffer = arena, offset = at, size = DxbcTranslator.ParamVec4s * 16 },
                    new() { binding = DxbcTranslator.PlumbingVsOut, buffer = dsOut, offset = 0, size = Math.Max(16, dsBytes) },
                    new() { binding = DxbcTranslator.PlumbingInputIndices, buffer = indices, offset = 0, size = Math.Max(4, indexCount * 4) },
                });
                groups.Add(MakeGroup(t.SoLayout, soEntries));
                groups.Add(MakeGroup(BindGroupLayout(new ShaderBindings(), WGPUShaderStage_Compute), new List<WGPUBindGroupEntry>()));
            }
            catch (InvalidOperationException e)
            {
                foreach (IntPtr g in groups) wgpuBindGroupRelease(g);
                D3DLog.Once("dssobind:" + e.Message, "stream output of a domain shader skipped: " + e.Message);
                return;
            }
            EndPass();
            _storageWritten = true;
            IntPtr pass = wgpuCommandEncoderBeginComputePass(Encoder(), null);
            wgpuComputePassEncoderSetPipeline(pass, t.DsPipeline);
            wgpuComputePassEncoderSetBindGroup(pass, 0, groups[0], 0, null);
            wgpuComputePassEncoderSetBindGroup(pass, 1, groups[1], 0, null);
            Dispatch1D(pass, pointCount);
            wgpuComputePassEncoderSetPipeline(pass, t.SoScan);
            wgpuComputePassEncoderSetBindGroup(pass, 0, groups[3], 0, null);
            wgpuComputePassEncoderSetBindGroup(pass, 1, groups[2], 0, null);
            wgpuComputePassEncoderDispatchWorkgroups(pass, 1, 1, 1);
            wgpuComputePassEncoderSetPipeline(pass, t.SoWrite);
            Dispatch1D(pass, prims);
            wgpuComputePassEncoderEnd(pass);
            wgpuComputePassEncoderRelease(pass);
            foreach (IntPtr g in groups) wgpuBindGroupRelease(g);
            BumpSoVersions(so);
        }
    }
}
