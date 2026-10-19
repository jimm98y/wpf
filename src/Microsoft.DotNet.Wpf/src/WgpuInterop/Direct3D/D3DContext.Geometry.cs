// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A draw with a geometry shader bound (Documentation/d3d11-webgpu-shim.md, section 6; the
// translator's side is DxbcTranslator.Geometry.cs). Two compute dispatches -- the vertex shader over
// the draw's vertices, then the geometry shader over its primitives -- fill scratch buffers of
// registers and strip indices, and an indexed draw of those indices with a pass-through vertex shader
// rasterizes them. The scratch buffers are reused draw after draw: WebGPU orders the passes of an
// encoder, so a draw's compute passes and its render pass see each other's writes and nothing else's.
//

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Wpf.Interop.WebGpu.Direct3D.Dxbc;
using static Microsoft.Wpf.Interop.WebGpu.Wgpu;
using static Microsoft.Wpf.Interop.WebGpu.Direct3D.WgpuCompute;

namespace Microsoft.Wpf.Interop.WebGpu.Direct3D
{
    internal sealed unsafe partial class D3DContext
    {
        private sealed class GeometryTranslation
        {
            public string? Error;
            public GeometryInfo Info = new();
            public int VsRegs, GsRegs;
            public ShaderBindings? VsBindings, GsBindings;
            public IntPtr VsLayout, VsPlumbing, VsPipeline;
            public IntPtr GsLayout, GsPlumbing, GsPipeline;
            public SortedSet<int> Slots = new();
            /// <summary>Stream output (a GS made with CreateGeometryShaderWithStreamOutput): its three kernels.</summary>
            public StreamOutputDesc? So;
            public IntPtr SoCount, SoScan, SoWrite;
            /// <summary>The draw: the pass-through vertex shader (group 0, the GS's registers) and the pixel shader.</summary>
            public readonly Translation Draw = new();
            public GeometryDraw Pipeline = new();
        }

        private readonly Dictionary<string, GeometryTranslation> _geometries = new();
        private IntPtr _gsVsOut, _gsOut, _gsIdx, _soCounts;
        private ulong _gsVsOutSize, _gsOutSize, _gsIdxSize, _soCountsSize;
        private const ulong MaxScratch = 128UL << 20;     // WebGPU's default largest storage binding

        private GeometryTranslation TranslateGeometry(D3DShader vs, D3DShader gs, D3DShader? ps)
        {
            string key = $"{vs.Code.Hash:x16}|{gs.Code.Hash:x16}|{ps?.Code.Hash ?? 0:x16}|{_layout?.Id ?? 0}|{gs.StreamOutput?.Key}";
            if (_geometries.TryGetValue(key, out var t)) return t;
            t = new GeometryTranslation();
            _geometries[key] = t;
            t.Pipeline.Key = key;
            try
            {
                var fetch = (_layout?.Elements ?? Array.Empty<D3DInputLayout.Element>()).Select(e => new FetchElement
                {
                    Semantic = e.Semantic, Index = e.Index, Format = e.Format, Slot = e.Slot, Offset = e.Offset, PerInstance = e.PerInstance, StepRate = e.StepRate,
                }).ToList();
                var v = DxbcTranslator.TranslateVertexCompute(vs.Code, fetch);
                t.So = gs.StreamOutput;
                var g = DxbcTranslator.TranslateGeometryCompute(gs.Code, vs.Code, v.OutputRegisters, t.So != null ? new TranslationOptions { StreamOutput = t.So } : null);
                var link = DxbcTranslator.LinkageOf(ps?.Code);
                var pass = DxbcTranslator.PassThrough(gs.Code, link);
                foreach (string a in v.Approximations.Concat(g.Approximations).Concat(pass.Approximations)) D3DLog.Once("approx:" + a, "shader approximation: " + a);
                t.Info = GeometryInfo.Of(gs.Code);
                t.VsRegs = v.OutputRegisters;
                t.GsRegs = g.OutputRegisters;
                t.VsBindings = v.Bindings;
                t.GsBindings = g.Bindings;
                foreach (var e in fetch) t.Slots.Add(e.Slot);
                if (ps?.Code.Inputs.Any(e => e.SemanticName.Equals("SV_PrimitiveID", StringComparison.OrdinalIgnoreCase)) == true)
                    D3DLog.Once("gsprim", "SV_PrimitiveID in a pixel shader after a geometry shader: the index of the primitive in the expanded draw, not the geometry shader's");

                // 1. the vertex shader as compute
                t.VsLayout = BindGroupLayout(v.Bindings, WGPUShaderStage_Compute);
                t.VsPlumbing = PlumbingLayout(vertex: true, t.Slots);
                t.VsPipeline = ComputePipeline(v.Wgsl, "gs-vs", t.VsLayout, t.VsPlumbing, vs.Code, out string? err);
                if (t.VsPipeline == IntPtr.Zero) { t.Error = "vertex shader as compute: " + err; return t; }

                // 2. the geometry shader as compute
                t.GsLayout = BindGroupLayout(g.Bindings, WGPUShaderStage_Compute);
                t.GsPlumbing = PlumbingLayout(vertex: false, null, t.So);
                t.GsPipeline = ComputePipeline(g.Wgsl, "gs", t.GsLayout, t.GsPlumbing, gs.Code, out err);
                if (t.So != null && t.GsPipeline != IntPtr.Zero)
                {
                    t.SoCount = ComputePipeline(g.Wgsl, "gs", t.GsLayout, t.GsPlumbing, gs.Code, out err, "so_count");
                    t.SoScan = ComputePipeline(g.Wgsl, "gs", t.GsLayout, t.GsPlumbing, gs.Code, out err, "so_scan");
                    t.SoWrite = ComputePipeline(g.Wgsl, "gs", t.GsLayout, t.GsPlumbing, gs.Code, out err, "so_write");
                    if (t.SoCount == IntPtr.Zero || t.SoScan == IntPtr.Zero || t.SoWrite == IntPtr.Zero) { t.Error = "stream output: " + err; return t; }
                }
                if (t.GsPipeline == IntPtr.Zero) { t.Error = "geometry shader as compute: " + err; return t; }

                // 3. the draw: pass-through and pixel shader
                var d = t.Draw;
                d.Vs = CreateModule(pass.Wgsl, "gs-pass", out err);
                if (d.Vs == IntPtr.Zero) { t.Error = "pass-through: " + err; Dump(gs.Code, pass.Wgsl, t.Error); return t; }
                if (ps != null)
                {
                    var p = DxbcTranslator.Translate(ps.Code, 1, link);
                    d.PsBindings = p.Bindings;
                    d.Ps = CreateModule(p.Wgsl, "ps", out err);
                    if (d.Ps == IntPtr.Zero) { t.Error = "pixel shader: " + err; Dump(ps.Code, p.Wgsl, t.Error); return t; }
                }
                var entry = new WGPUBindGroupLayoutEntry { binding = 0, visibility = WGPUShaderStage_Vertex, buffer = new WGPUBufferBindingLayout { type = WGPUBufferBindingType.ReadOnlyStorage } };
                var ld = new WGPUBindGroupLayoutDescriptor { entryCount = 1, entries = &entry };
                d.VsLayout = wgpuDeviceCreateBindGroupLayout(Device.Gpu.Device, &ld);
                d.PsLayout = BindGroupLayout(d.PsBindings ?? new ShaderBindings(), WGPUShaderStage_Fragment);
                IntPtr* layouts = stackalloc IntPtr[2] { d.VsLayout, d.PsLayout };
                var pl = new WGPUPipelineLayoutDescriptor { bindGroupLayoutCount = 2, bindGroupLayouts = layouts };
                d.PipelineLayout = wgpuDeviceCreatePipelineLayout(Device.Gpu.Device, &pl);
                t.Pipeline.Topology = t.Info.OutputTopology switch
                {
                    1 => WGPUPrimitiveTopology.PointList,
                    3 => WGPUPrimitiveTopology.LineStrip,
                    _ => WGPUPrimitiveTopology.TriangleStrip,
                };
            }
            catch (NotSupportedException e) { t.Error = e.Message; D3DLog.Once("tr:" + e.Message, $"geometry draw not translated: {e.Message}"); }
            catch (Exception e) { t.Error = e.Message; D3DLog.Error("translate geometry", e); }
            return t;
        }

        /// <summary>Group 1 of the two kernels: the parameters, the index buffer, the registers, the
        /// strip indices and the vertex buffers (DxbcTranslator.Plumbing*).</summary>
        private IntPtr PlumbingLayout(bool vertex, SortedSet<int>? slots, StreamOutputDesc? so = null)
        {
            var entries = new List<WGPUBindGroupLayoutEntry>
            {
                new() { binding = DxbcTranslator.PlumbingParams, visibility = WGPUShaderStage_Compute, buffer = new WGPUBufferBindingLayout { type = WGPUBufferBindingType.Uniform, minBindingSize = DxbcTranslator.ParamVec4s * 16 } },
                new() { binding = DxbcTranslator.PlumbingVsOut, visibility = WGPUShaderStage_Compute, buffer = new WGPUBufferBindingLayout { type = vertex ? WGPUBufferBindingType.Storage : WGPUBufferBindingType.ReadOnlyStorage } },
            };
            if (vertex)
            {
                entries.Add(new() { binding = DxbcTranslator.PlumbingIndices, visibility = WGPUShaderStage_Compute, buffer = new WGPUBufferBindingLayout { type = WGPUBufferBindingType.ReadOnlyStorage } });
                foreach (int s in slots!)
                    entries.Add(new() { binding = (uint)(DxbcTranslator.PlumbingVertexBuffer + s), visibility = WGPUShaderStage_Compute, buffer = new WGPUBufferBindingLayout { type = WGPUBufferBindingType.ReadOnlyStorage } });
            }
            else
            {
                entries.Add(new() { binding = DxbcTranslator.PlumbingGsOut, visibility = WGPUShaderStage_Compute, buffer = new WGPUBufferBindingLayout { type = WGPUBufferBindingType.Storage } });
                entries.Add(new() { binding = DxbcTranslator.PlumbingGsIndices, visibility = WGPUShaderStage_Compute, buffer = new WGPUBufferBindingLayout { type = WGPUBufferBindingType.Storage } });
                if (so != null) AddStreamOutLayout(entries, so, counts: true);
            }
            var arr = entries.ToArray();
            fixed (WGPUBindGroupLayoutEntry* p = arr)
            {
                var d = new WGPUBindGroupLayoutDescriptor { entryCount = (nuint)arr.Length, entries = p };
                return wgpuDeviceCreateBindGroupLayout(Device.Gpu.Device, &d);
            }
        }

        private IntPtr ComputePipeline(string wgsl, string label, IntPtr group0, IntPtr group1, DxbcShader code, out string? error, string entryPoint = "main")
        {
            IntPtr module = CreateModule(wgsl, label, out error);
            if (module == IntPtr.Zero) { Dump(code, wgsl, error ?? ""); return IntPtr.Zero; }
            IntPtr* layouts = stackalloc IntPtr[2] { group0, group1 };
            var pl = new WGPUPipelineLayoutDescriptor { bindGroupLayoutCount = 2, bindGroupLayouts = layouts };
            IntPtr layout = wgpuDeviceCreatePipelineLayout(Device.Gpu.Device, &pl);
            byte[] entry = Encoding.UTF8.GetBytes(entryPoint);
            fixed (byte* e = entry)
            {
                var d = new WGPUComputePipelineDescriptor
                {
                    layout = layout,
                    compute = new WGPUComputeState { module = module, entryPoint = new WGPUStringView { data = e, length = (nuint)entry.Length } },
                };
                wgpuDevicePushErrorScope(Device.Gpu.Device, WGPUErrorFilter.Validation);
                IntPtr pipeline = PopScope(wgpuDeviceCreateComputePipeline(Device.Gpu.Device, &d), out error);
                if (pipeline == IntPtr.Zero) Dump(code, wgsl, error ?? "");
                return pipeline;
            }
        }

        /// <summary>A scratch buffer of at least <paramref name="need"/> bytes (grown by doubling; the
        /// old one is released, a recorded pass keeps it alive).</summary>
        private IntPtr Scratch(ref IntPtr buffer, ref ulong size, ulong need, WGPUBufferUsage usage)
        {
            need = Math.Max(16, (need + 15) & ~15UL);
            if (buffer != IntPtr.Zero && size >= need) return buffer;
            if (buffer != IntPtr.Zero) wgpuBufferRelease(buffer);
            size = Math.Max(need, Math.Min(MaxScratch, Math.Max(64UL << 10, size * 2)));
            buffer = Device.Gpu.CreateBuffer(size, usage | WGPUBufferUsage.Storage | WGPUBufferUsage.CopyDst);
            return buffer;
        }

        /// <summary>The primitives a draw of <paramref name="n"/> vertices makes in topology
        /// <paramref name="topo"/>: (D3D10_SB_PRIMITIVE, vertices a list primitive steps by, 0 list /
        /// 1 strip / 2 triangle strip, count); null for a topology this path does not assemble.</summary>
        private static (int Primitive, uint Step, uint Mode, uint Count)? Primitives(D3D_PRIMITIVE_TOPOLOGY topo, uint n) => topo switch
        {
            D3D_PRIMITIVE_TOPOLOGY.POINTLIST => (1, 1, 0, n),
            D3D_PRIMITIVE_TOPOLOGY.LINELIST => (2, 2, 0, n / 2),
            D3D_PRIMITIVE_TOPOLOGY.LINESTRIP => (2, 1, 1, n >= 2 ? n - 1 : 0),
            D3D_PRIMITIVE_TOPOLOGY.TRIANGLELIST => (3, 3, 0, n / 3),
            D3D_PRIMITIVE_TOPOLOGY.TRIANGLESTRIP => (3, 1, 2, n >= 3 ? n - 2 : 0),
            D3D_PRIMITIVE_TOPOLOGY.LINELIST_ADJ => (6, 4, 0, n / 4),
            D3D_PRIMITIVE_TOPOLOGY.LINESTRIP_ADJ => (6, 1, 1, n >= 4 ? n - 3 : 0),
            D3D_PRIMITIVE_TOPOLOGY.TRIANGLELIST_ADJ => (7, 6, 0, n / 6),
            _ => null,
        };

        private void DrawGeometry(DrawArgs a, D3DShader vs, D3DShader gs)
        {
            var t = TranslateGeometry(vs, gs, _ps.Shader);
            if (t.Error != null) { DrawsSkipped++; return; }
            var prims = Primitives(_topology, a.Count);
            if (prims == null) { D3DLog.Once("gstopo:" + _topology, $"a geometry shader draw in topology {_topology} is not assembled"); DrawsSkipped++; return; }
            var (primitive, step, mode, count) = prims.Value;
            if (primitive != t.Info.InputPrimitive) { D3DLog.Once("gsmismatch", $"topology {_topology} does not match the geometry shader's input primitive {t.Info.InputPrimitive} (D3D rejects the draw)"); DrawsSkipped++; return; }
            if (count == 0) return;
            if (a.Indexed && (_ib == null || _ib.Gpu == IntPtr.Zero)) { DrawsSkipped++; return; }
            if (a.Indexed && mode != 0) D3DLog.Once("gsrestart", "an indexed strip into a geometry shader: strip-cut indices are not honoured");

            ulong vsTotal = (ulong)a.Count * a.Instances;
            ulong gsTotal = (ulong)count * a.Instances * (ulong)t.Info.Instances;
            uint per = (uint)t.Info.IndicesPerInvocation, max = (uint)Math.Max(1, t.Info.MaxVertices);
            ulong vsBytes = vsTotal * (ulong)t.VsRegs * 16, gsBytes = gsTotal * max * (ulong)t.GsRegs * 16, idxBytes = gsTotal * per * 4;
            if (vsBytes > MaxScratch || gsBytes > MaxScratch || idxBytes > MaxScratch || gsTotal * per > uint.MaxValue - 1)
            {
                D3DLog.Once("gsbig", "a geometry shader draw too large for its scratch buffers was skipped");
                DrawsSkipped++;
                return;
            }

            // the draw's parameters (DxbcTranslator.Geometry.cs)
            var p = new uint[DxbcTranslator.ParamVec4s * 4];
            p[0] = a.Count; p[1] = a.First; p[2] = (uint)a.BaseVertex; p[3] = a.FirstInstance;
            p[4] = a.Indexed ? 1u : 0u; p[5] = _ibOffset; p[6] = _ibFormat == DXGI_FORMAT.R16_UINT ? 1u : 0u; p[7] = a.Instances;
            p[8] = (uint)vsTotal; p[9] = count; p[10] = mode; p[11] = step;
            p[12] = (uint)gsTotal;
            var soEntries = new List<WGPUBindGroupEntry>();
            if (t.So != null) p[13] = AddStreamOutGroup(soEntries, t.So, Scratch(ref _soCounts, ref _soCountsSize, gsTotal * 4, 0), gsTotal * 4);
            foreach (int s in t.Slots) { p[(4 + s) * 4] = _vbOffset[s]; p[(4 + s) * 4 + 1] = _vbStride[s]; }
            var bytes = new byte[p.Length * 4];
            Buffer.BlockCopy(p, 0, bytes, 0, bytes.Length);

            IntPtr vsOut = Scratch(ref _gsVsOut, ref _gsVsOutSize, vsBytes, 0);
            IntPtr gsOut = Scratch(ref _gsOut, ref _gsOutSize, gsBytes, 0);
            IntPtr gsIdx = Scratch(ref _gsIdx, ref _gsIdxSize, idxBytes, WGPUBufferUsage.Index);

            // bind groups (the arena writes are queue writes: before the passes)
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
                groups.Add(BindGroup(t.GsLayout, t.GsBindings!, _gs));
                soEntries.AddRange(new WGPUBindGroupEntry[]
                {
                    new() { binding = DxbcTranslator.PlumbingParams, buffer = arena, offset = at, size = DxbcTranslator.ParamVec4s * 16 },
                    new() { binding = DxbcTranslator.PlumbingVsOut, buffer = vsOut, offset = 0, size = Math.Max(16, vsBytes) },
                    new() { binding = DxbcTranslator.PlumbingGsOut, buffer = gsOut, offset = 0, size = Math.Max(16, gsBytes) },
                    new() { binding = DxbcTranslator.PlumbingGsIndices, buffer = gsIdx, offset = 0, size = Math.Max(16, idxBytes) },
                });
                groups.Add(MakeGroup(t.GsPlumbing, soEntries));
                groups.Add(MakeGroup(t.Draw.VsLayout, new List<WGPUBindGroupEntry> { new() { binding = 0, buffer = gsOut, offset = 0, size = Math.Max(16, gsBytes) } }));
                groups.Add(BindGroup(t.Draw.PsLayout, t.Draw.PsBindings ?? new ShaderBindings(), _ps));
            }
            catch (InvalidOperationException e)
            {
                foreach (IntPtr g in groups) wgpuBindGroupRelease(g);
                D3DLog.Once("gsbind:" + e.Message, "geometry shader draw skipped: " + e.Message);
                DrawsSkipped++;
                return;
            }
            // stream output alone (no rasterized stream, or nothing to rasterize to) draws nothing
            bool targets = _dsv != null || _rtv.Any(r => r != null);
            bool render = (t.So == null || t.So.RasterizedStream == 0) && (targets || t.So == null);
            IntPtr pipeline = render ? Pipeline(t.Draw, vs, _ps.Shader, out _, t.Pipeline) : IntPtr.Zero;
            if (render && pipeline == IntPtr.Zero) { foreach (IntPtr g in groups) wgpuBindGroupRelease(g); DrawsSkipped++; return; }

            // 1 and 2: the two kernels, in one compute pass (WebGPU orders its dispatches' storage writes)
            EndPass();
            IntPtr pass = wgpuCommandEncoderBeginComputePass(Encoder(), null);
            wgpuComputePassEncoderSetPipeline(pass, t.VsPipeline);
            wgpuComputePassEncoderSetBindGroup(pass, 0, groups[0], 0, null);
            wgpuComputePassEncoderSetBindGroup(pass, 1, groups[1], 0, null);
            Dispatch1D(pass, vsTotal);
            wgpuComputePassEncoderSetPipeline(pass, t.GsPipeline);
            wgpuComputePassEncoderSetBindGroup(pass, 0, groups[2], 0, null);
            wgpuComputePassEncoderSetBindGroup(pass, 1, groups[3], 0, null);
            Dispatch1D(pass, gsTotal);
            if (t.So != null)
            {
                // stream output: count each invocation's primitives, place them, write them
                wgpuComputePassEncoderSetPipeline(pass, t.SoCount);
                Dispatch1D(pass, gsTotal);
                wgpuComputePassEncoderSetPipeline(pass, t.SoScan);
                wgpuComputePassEncoderDispatchWorkgroups(pass, 1, 1, 1);
                wgpuComputePassEncoderSetPipeline(pass, t.SoWrite);
                Dispatch1D(pass, gsTotal);
                BumpSoVersions(t.So);
            }
            wgpuComputePassEncoderEnd(pass);
            wgpuComputePassEncoderRelease(pass);
            if (!render) { foreach (IntPtr g in groups) wgpuBindGroupRelease(g); DrawsIssued++; return; }

            // 3: the indexed draw
            if (!EnsurePass() || !ApplyRasterState()) { foreach (IntPtr g in groups) wgpuBindGroupRelease(g); DrawsSkipped++; return; }
            wgpuRenderPassEncoderSetPipeline(_pass, pipeline);
            wgpuRenderPassEncoderSetBindGroup(_pass, 0, groups[4], 0, null);
            wgpuRenderPassEncoderSetBindGroup(_pass, 1, groups[5], 0, null);
            wgpuRenderPassEncoderSetIndexBuffer(_pass, gsIdx, WGPUIndexFormat.Uint32, 0, Math.Max(16, idxBytes));
            wgpuRenderPassEncoderDrawIndexed(_pass, (uint)(gsTotal * per), 1, 0, 0, 0);
            foreach (IntPtr g in groups) wgpuBindGroupRelease(g);
            DrawsIssued++;
            for (int i = 0; i < MaxTargets; i++) if (_rtv[i] != null) _rtv[i]!.Texture.Version++;
        }

        private IntPtr MakeGroup(IntPtr layout, List<WGPUBindGroupEntry> entries)
        {
            var arr = entries.ToArray();
            fixed (WGPUBindGroupEntry* p = arr)
            {
                var d = new WGPUBindGroupDescriptor { layout = layout, entryCount = (nuint)arr.Length, entries = p };
                return wgpuDeviceCreateBindGroup(Device.Gpu.Device, &d);
            }
        }

        /// <summary>Workgroups of 64 for <paramref name="invocations"/>, in rows of 65535 groups
        /// (DxbcTranslator.InvocationsPerRow).</summary>
        private static void Dispatch1D(IntPtr pass, ulong invocations)
        {
            ulong groups = (invocations + 63) / 64;
            if (groups == 0) return;
            uint x = (uint)Math.Min(groups, 65535UL), y = (uint)((groups + 65534) / 65535);
            wgpuComputePassEncoderDispatchWorkgroups(pass, x, y, 1);
        }
    }
}
