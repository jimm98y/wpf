// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Stream output on the context (Documentation/d3d11-webgpu-shim.md, section 9; the kernels are in
// DxbcTranslator.StreamOutput.cs): the SO targets, each buffer's filled size (a small GPU counter of
// its own, which SOSetTargets' offsets set and the kernels move on), DrawAuto, and the indirect draws.
// DrawAuto and an indirect draw through the compute paths read their counts back, so they wait for
// the GPU; an indirect draw on the native path stays on the GPU.
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
        internal readonly D3DBuffer?[] _so = new D3DBuffer?[4];
        private IntPtr _soState;

        public void SetSoTargets(D3DBuffer?[] buffers, uint[] offsets) => State(c =>
        {
            for (int i = 0; i < 4; i++)
            {
                var b = i < buffers.Length ? buffers[i] : null;
                Bind(ref c._so[i], b);
                // an offset of -1 appends to what the buffer holds; any other is where writing starts
                if (b != null && i < offsets.Length && offsets[i] != 0xFFFFFFFF)
                    c.WriteBufferOrdered(b.SoFilledBuffer(), 0, BitConverter.GetBytes(offsets[i]));
            }
        }, buffers);

        /// <summary>Words of a GPU buffer, read back (waits for the GPU).</summary>
        private uint[] ReadWords(IntPtr buffer, ulong offset, int words)
        {
            ulong size = (ulong)Math.Max(4, words * 4);
            IntPtr read = Device.Gpu.CreateBuffer((size + 3) & ~3UL, WGPUBufferUsage.MapRead | WGPUBufferUsage.CopyDst);
            try
            {
                EndPass();
                wgpuCommandEncoderCopyBufferToBuffer(Encoder(), buffer, offset, read, 0, size);
                FlushToGpu(wait: true);
                byte[] bytes = Device.Gpu.MapRead(read, size);
                var r = new uint[words];
                Buffer.BlockCopy(bytes, 0, r, 0, words * 4);
                return r;
            }
            finally { wgpuBufferRelease(read); }
        }

        /// <summary>An indirect draw's arguments, read back into an ordinary draw.</summary>
        private DrawArgs ReadIndirect(DrawArgs a)
        {
            var buf = a.Indirect!;
            if (buf.Gpu == IntPtr.Zero || a.IndirectOffset + (a.Indexed ? 20u : 16u) > buf.Size) return a;
            uint[] w = ReadWords(buf.Gpu, a.IndirectOffset, a.Indexed ? 5 : 4);
            return a.Indexed
                ? new DrawArgs { Indexed = true, Count = w[0], Instances = w[1], First = w[2], BaseVertex = (int)w[3], FirstInstance = w[4] }
                : new DrawArgs { Count = w[0], Instances = w[1], First = w[2], FirstInstance = w[3] };
        }

        public void DrawAuto() => Run(c =>
        {
            var vb = c._vb[0];
            if (vb == null || vb.Gpu == IntPtr.Zero || c._vbStride[0] == 0) { c.DrawsSkipped++; return; }
            uint filled = c.ReadWords(vb.SoFilledBuffer(), 0, 1)[0];
            uint bytes = filled > c._vbOffset[0] ? filled - c._vbOffset[0] : 0;
            c.DrawImpl(new DrawArgs { Count = bytes / c._vbStride[0], Instances = 1 });
        });

        private IntPtr SoState() => _soState != IntPtr.Zero ? _soState : _soState = Device.Gpu.CreateBuffer(64, WGPUBufferUsage.Storage | WGPUBufferUsage.CopyDst);

        /// <summary>The SO bindings of group 1 (layout entries) for the slots <paramref name="so"/> declares.</summary>
        private static void AddStreamOutLayout(List<WGPUBindGroupLayoutEntry> entries, StreamOutputDesc so, bool counts)
        {
            WGPUBindGroupLayoutEntry Rw(int binding) => new() { binding = (uint)binding, visibility = WGPUShaderStage_Compute, buffer = new WGPUBufferBindingLayout { type = WGPUBufferBindingType.Storage } };
            if (counts) entries.Add(Rw(DxbcTranslator.PlumbingSoCounts));
            entries.Add(Rw(DxbcTranslator.PlumbingSoState));
            foreach (int s in so.Slots) { entries.Add(Rw(DxbcTranslator.PlumbingSoBuffer + s)); entries.Add(Rw(DxbcTranslator.PlumbingSoFilled + s)); }
        }

        /// <summary>The SO bindings' entries, and the bound-slot mask the kernels take.</summary>
        private uint AddStreamOutGroup(List<WGPUBindGroupEntry> entries, StreamOutputDesc so, IntPtr counts, ulong countBytes)
        {
            if (counts != IntPtr.Zero) entries.Add(new() { binding = DxbcTranslator.PlumbingSoCounts, buffer = counts, offset = 0, size = Math.Max(16, countBytes) });
            entries.Add(new() { binding = DxbcTranslator.PlumbingSoState, buffer = SoState(), offset = 0, size = 64 });
            uint mask = 0;
            foreach (int s in so.Slots)
            {
                var b = _so[s];
                if (b != null && b.Gpu != IntPtr.Zero && b.Size >= 4)
                {
                    mask |= 1u << s;
                    entries.Add(new() { binding = (uint)(DxbcTranslator.PlumbingSoBuffer + s), buffer = b.Gpu, offset = 0, size = (ulong)b.Size & ~3UL });
                    entries.Add(new() { binding = (uint)(DxbcTranslator.PlumbingSoFilled + s), buffer = b.SoFilledBuffer(), offset = 0, size = 16 });
                }
                else
                {
                    entries.Add(new() { binding = (uint)(DxbcTranslator.PlumbingSoBuffer + s), buffer = DummyStorage(), offset = 0, size = 16 });
                    entries.Add(new() { binding = (uint)(DxbcTranslator.PlumbingSoFilled + s), buffer = DummyStorage(), offset = 0, size = 16 });
                }
            }
            return mask;
        }

        private void BumpSoVersions(StreamOutputDesc so)
        {
            foreach (int s in so.Slots) if (_so[s] is { } b) System.Threading.Interlocked.Increment(ref b.Version);
        }

        // ---- a vertex shader's stream output -------------------------------------------------------------

        private sealed class VertexStreamOutTranslation
        {
            public string? Error;
            public int VsRegs, Vertices;
            public ShaderBindings? VsBindings;
            public IntPtr VsLayout, VsPlumbing, VsPipeline, SoLayout, SoScan, SoWrite;
            public SortedSet<int> Slots = new();
        }

        private readonly Dictionary<string, VertexStreamOutTranslation> _vertexStreamOuts = new();

        private void VertexStreamOut(DrawArgs a, D3DShader vs, StreamOutputDesc so)
        {
            var prims = Primitives(_topology, a.Count);
            if (prims == null || prims.Value.Primitive > 3) { D3DLog.Once("sotopo:" + _topology, $"stream output of a vertex shader in topology {_topology} is not written"); return; }
            var (primitive, step, mode, count) = prims.Value;
            if (count == 0) return;
            int vertices = primitive;
            string key = $"{vs.Code.Hash:x16}|{_layout?.Id ?? 0}|{so.Key}|{vertices}";
            if (!_vertexStreamOuts.TryGetValue(key, out var t))
            {
                t = new VertexStreamOutTranslation { Vertices = vertices };
                _vertexStreamOuts[key] = t;
                try
                {
                    var fetch = (_layout?.Elements ?? Array.Empty<D3DInputLayout.Element>()).Select(e => new FetchElement
                    {
                        Semantic = e.Semantic, Index = e.Index, Format = e.Format, Slot = e.Slot, Offset = e.Offset, PerInstance = e.PerInstance, StepRate = e.StepRate,
                    }).ToList();
                    var v = DxbcTranslator.TranslateVertexCompute(vs.Code, fetch);
                    t.VsRegs = v.OutputRegisters;
                    t.VsBindings = v.Bindings;
                    foreach (var e in fetch) t.Slots.Add(e.Slot);
                    t.VsLayout = BindGroupLayout(v.Bindings, WGPUShaderStage_Compute);
                    t.VsPlumbing = PlumbingLayout(vertex: true, t.Slots);
                    t.VsPipeline = ComputePipeline(v.Wgsl, "so-vs", t.VsLayout, t.VsPlumbing, vs.Code, out string? err);
                    if (t.VsPipeline == IntPtr.Zero) { t.Error = err; return; }
                    string wgsl = DxbcTranslator.StreamOutFromVertex(vs.Code, t.VsRegs, so, vertices);
                    var entries = new List<WGPUBindGroupLayoutEntry>
                    {
                        new() { binding = DxbcTranslator.PlumbingParams, visibility = WGPUShaderStage_Compute, buffer = new WGPUBufferBindingLayout { type = WGPUBufferBindingType.Uniform, minBindingSize = DxbcTranslator.ParamVec4s * 16 } },
                        new() { binding = DxbcTranslator.PlumbingVsOut, visibility = WGPUShaderStage_Compute, buffer = new WGPUBufferBindingLayout { type = WGPUBufferBindingType.ReadOnlyStorage } },
                    };
                    AddStreamOutLayout(entries, so, counts: false);
                    var arr = entries.ToArray();
                    fixed (WGPUBindGroupLayoutEntry* p = arr)
                    {
                        var d = new WGPUBindGroupLayoutDescriptor { entryCount = (nuint)arr.Length, entries = p };
                        t.SoLayout = wgpuDeviceCreateBindGroupLayout(Device.Gpu.Device, &d);
                    }
                    IntPtr empty = BindGroupLayout(new ShaderBindings(), WGPUShaderStage_Compute);
                    t.SoScan = ComputePipeline(wgsl, "so", empty, t.SoLayout, vs.Code, out err, "so_scan");
                    t.SoWrite = ComputePipeline(wgsl, "so", empty, t.SoLayout, vs.Code, out err, "so_write");
                    if (t.SoScan == IntPtr.Zero || t.SoWrite == IntPtr.Zero) t.Error = err;
                }
                catch (NotSupportedException e) { t.Error = e.Message; D3DLog.Once("tr:" + e.Message, $"stream output not translated: {e.Message}"); }
            }
            if (t.Error != null) return;

            ulong vsTotal = (ulong)a.Count * a.Instances, total = (ulong)count * a.Instances;
            ulong vsBytes = vsTotal * (ulong)t.VsRegs * 16;
            if (vsBytes > MaxScratch) { D3DLog.Once("sobig", "stream output too large for its scratch buffers was skipped"); return; }
            var p32 = new uint[DxbcTranslator.ParamVec4s * 4];
            p32[0] = a.Count; p32[1] = a.First; p32[2] = (uint)a.BaseVertex; p32[3] = a.FirstInstance;
            p32[4] = a.Indexed ? 1u : 0u; p32[5] = _ibOffset; p32[6] = _ibFormat == DXGI_FORMAT.R16_UINT ? 1u : 0u; p32[7] = a.Instances;
            p32[8] = (uint)vsTotal; p32[9] = count; p32[10] = mode; p32[11] = step;
            p32[12] = (uint)total;
            foreach (int s in t.Slots) { p32[(4 + s) * 4] = _vbOffset[s]; p32[(4 + s) * 4 + 1] = _vbStride[s]; }
            IntPtr vsOut = Scratch(ref _gsVsOut, ref _gsVsOutSize, vsBytes, 0);
            var soEntries = new List<WGPUBindGroupEntry>();
            p32[13] = AddStreamOutGroup(soEntries, so, IntPtr.Zero, 0);
            var bytes = new byte[p32.Length * 4];
            Buffer.BlockCopy(p32, 0, bytes, 0, bytes.Length);
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
                    a.Indexed && _ib != null && _ib.Size >= 4
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
                soEntries.Insert(0, new() { binding = DxbcTranslator.PlumbingVsOut, buffer = vsOut, offset = 0, size = Math.Max(16, vsBytes) });
                soEntries.Insert(0, new() { binding = DxbcTranslator.PlumbingParams, buffer = arena, offset = at, size = DxbcTranslator.ParamVec4s * 16 });
                groups.Add(MakeGroup(t.SoLayout, soEntries));
                groups.Add(MakeGroup(BindGroupLayout(new ShaderBindings(), WGPUShaderStage_Compute), new List<WGPUBindGroupEntry>()));
            }
            catch (InvalidOperationException e)
            {
                foreach (IntPtr g in groups) wgpuBindGroupRelease(g);
                D3DLog.Once("sobind:" + e.Message, "stream output skipped: " + e.Message);
                return;
            }
            EndPass();
            _storageWritten = true;
            IntPtr pass = wgpuCommandEncoderBeginComputePass(Encoder(), null);
            wgpuComputePassEncoderSetPipeline(pass, t.VsPipeline);
            wgpuComputePassEncoderSetBindGroup(pass, 0, groups[0], 0, null);
            wgpuComputePassEncoderSetBindGroup(pass, 1, groups[1], 0, null);
            Dispatch1D(pass, vsTotal);
            wgpuComputePassEncoderSetPipeline(pass, t.SoScan);
            wgpuComputePassEncoderSetBindGroup(pass, 0, groups[3], 0, null);
            wgpuComputePassEncoderSetBindGroup(pass, 1, groups[2], 0, null);
            wgpuComputePassEncoderDispatchWorkgroups(pass, 1, 1, 1);
            wgpuComputePassEncoderSetPipeline(pass, t.SoWrite);
            Dispatch1D(pass, total);
            wgpuComputePassEncoderEnd(pass);
            wgpuComputePassEncoderRelease(pass);
            foreach (IntPtr g in groups) wgpuBindGroupRelease(g);
            BumpSoVersions(so);
        }
    }
}
