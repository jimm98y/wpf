// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Compute on the immediate (and deferred) context: the CS stage's state, UAV slots, Dispatch and
// DispatchIndirect, UAV clears and CopyStructureCount (Documentation/d3d11-webgpu-shim.md, section 9).
//
// A compute shader is translated per the formats of the typed UAVs bound to it (WGSL names a
// storage texture's format in its type), cached by the bytecode and those formats. A dispatch ends
// the open render pass and runs in a compute pass of its own; WebGPU orders the passes of an encoder,
// so writes before it are seen and its writes are seen after.
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
        public const int MaxUavs = 64;

        private readonly StageState _cs = new(), _hs = new(), _ds = new();
        private readonly D3DUnorderedAccessView?[] _csUav = new D3DUnorderedAccessView?[MaxUavs];
        /// <summary>The output-merger UAVs (pixel shader u#), from OMSetRenderTargetsAndUnorderedAccessViews.</summary>
        private readonly D3DUnorderedAccessView?[] _omUav = new D3DUnorderedAccessView?[MaxUavs];

        public int DispatchesIssued, DispatchesSkipped;

        public void SetUavs(bool compute, int start, D3DUnorderedAccessView?[] views, uint[]? initialCounts)
            => Run(c =>
            {
                var slots = compute ? c._csUav : c._omUav;
                for (int i = 0; i < views.Length && start + i < MaxUavs; i++)
                {
                    Bind(ref slots[start + i], views[i]);
                    // an initial count of -1 keeps the counter; any other sets it
                    if (initialCounts != null && i < initialCounts.Length && initialCounts[i] != uint.MaxValue && views[i]?.Counter is IntPtr counter && counter != IntPtr.Zero)
                        c.WriteBufferOrdered(counter, 0, BitConverter.GetBytes(initialCounts[i]));
                }
            }, views);

        private void ClearComputeState()
        {
            foreach (var s in new[] { _cs, _hs, _ds })
            {
                Bind(ref s.Shader, null);
                for (int i = 0; i < s.ConstantBuffers.Length; i++) Bind(ref s.ConstantBuffers[i], null);
                for (int i = 0; i < s.Resources.Length; i++) Bind(ref s.Resources[i], null);
                for (int i = 0; i < s.Samplers.Length; i++) Bind(ref s.Samplers[i], null);
            }
            for (int i = 0; i < MaxUavs; i++) { Bind(ref _csUav[i], null); Bind(ref _omUav[i], null); }
        }

        // ---- dispatch ------------------------------------------------------------------------------

        private sealed class ComputeTranslation
        {
            public IntPtr Module, Layout, PipelineLayout, Pipeline;
            public ShaderBindings? Bindings;
            public string? Error;
        }

        private readonly Dictionary<string, ComputeTranslation> _computes = new();

        public void Dispatch(uint x, uint y, uint z) => Run(c => c.DispatchImpl(x, y, z, null, 0));

        public void DispatchIndirect(D3DBuffer args, uint offset) => Run(c => c.DispatchImpl(0, 0, 0, args, offset), args);

        private ComputeTranslation TranslateCompute(D3DShader cs)
        {
            // specialized on the bound typed UAVs' formats
            var formats = new Dictionary<int, string>();
            var words = new Dictionary<int, int>();
            var key = new StringBuilder(cs.Code.Hash.ToString("x16"));
            for (int i = 0; i < MaxUavs; i++)
            {
                var v = _csUav[i];
                if (v == null) continue;
                if (v.Resource is D3DTexture2D) { try { formats[i] = v.WgslFormat; } catch (NotSupportedException) { } }
                else if ((v.Flags & 1) == 0 && v.Resource is D3DBuffer b && (b.Desc.MiscFlags & D3D11_RESOURCE_MISC.BUFFER_STRUCTURED) == 0) words[i] = v.BufferWords;
                key.Append('|').Append(i).Append(':').Append(formats.TryGetValue(i, out var f) ? f : "").Append(words.TryGetValue(i, out int w) ? w.ToString() : "");
            }
            string k = key.ToString();
            if (_computes.TryGetValue(k, out var t)) return t;
            t = new ComputeTranslation();
            _computes[k] = t;
            try
            {
                var tr = DxbcTranslator.Translate(cs.Code, 0, DxbcTranslator.LinkageOf(null), new TranslationOptions { UavFormats = formats, UavBufferWords = words });
                foreach (string a in tr.Approximations) D3DLog.Once("approx:" + a, "shader approximation: " + a);
                t.Bindings = tr.Bindings;
                t.Module = CreateModule(tr.Wgsl, "cs", out string? err);
                if (t.Module == IntPtr.Zero) { t.Error = "compute shader: " + err; Dump(cs.Code, tr.Wgsl, t.Error); return t; }
                t.Layout = BindGroupLayout(tr.Bindings, WGPUShaderStage_Compute, _csUav);
                IntPtr* layouts = stackalloc IntPtr[1] { t.Layout };
                var pl = new WGPUPipelineLayoutDescriptor { bindGroupLayoutCount = 1, bindGroupLayouts = layouts };
                t.PipelineLayout = wgpuDeviceCreatePipelineLayout(Device.Gpu.Device, &pl);
                byte[] entry = Encoding.UTF8.GetBytes("main");
                fixed (byte* e = entry)
                {
                    var d = new WGPUComputePipelineDescriptor
                    {
                        layout = t.PipelineLayout,
                        compute = new WGPUComputeState { module = t.Module, entryPoint = new WGPUStringView { data = e, length = (nuint)entry.Length } },
                    };
                    wgpuDevicePushErrorScope(Device.Gpu.Device, WGPUErrorFilter.Validation);
                    t.Pipeline = PopScope(wgpuDeviceCreateComputePipeline(Device.Gpu.Device, &d), out string? perr);
                    if (t.Pipeline == IntPtr.Zero) { t.Error = "compute pipeline: " + perr; Dump(cs.Code, tr.Wgsl, t.Error); }
                }
            }
            catch (NotSupportedException e) { t.Error = e.Message; D3DLog.Once("tr:" + e.Message, $"compute shader not translated: {e.Message}"); }
            catch (Exception e) { t.Error = e.Message; D3DLog.Error("translate compute", e); }
            return t;
        }

        private void DispatchImpl(uint x, uint y, uint z, D3DBuffer? indirect, uint indirectOffset)
        {
            var cs = _cs.Shader;
            if (cs == null || cs.Stage != ShaderStage.Compute) { DispatchesSkipped++; D3DLog.Once("nocs", "Dispatch with no compute shader bound"); return; }
            if (indirect == null && (x == 0 || y == 0 || z == 0)) return;
            var t = TranslateCompute(cs);
            if (t.Pipeline == IntPtr.Zero) { DispatchesSkipped++; return; }
            ResolvePendingClears();
            IntPtr group;
            try { group = BindGroup(t.Layout, t.Bindings!, _cs, _csUav); }
            catch (InvalidOperationException e) { DispatchesSkipped++; D3DLog.Once("csbind:" + e.Message, "Dispatch skipped: " + e.Message); return; }
            _storageWritten = true;
            IntPtr pass = wgpuCommandEncoderBeginComputePass(Encoder(), null);
            wgpuComputePassEncoderSetPipeline(pass, t.Pipeline);
            wgpuComputePassEncoderSetBindGroup(pass, 0, group, 0, null);
            if (indirect != null) wgpuComputePassEncoderDispatchWorkgroupsIndirect(pass, indirect.Gpu, indirectOffset);
            else wgpuComputePassEncoderDispatchWorkgroups(pass, x, y, z);
            wgpuComputePassEncoderEnd(pass);
            wgpuComputePassEncoderRelease(pass);
            wgpuBindGroupRelease(group);
            DispatchesIssued++;
            foreach (var (slot, _) in t.Bindings!.Uavs)
                if (_csUav[slot]?.Resource is D3DResource r) System.Threading.Interlocked.Increment(ref r.Version);
        }

        // ---- UAV clears and the structure count ---------------------------------------------------------

        /// <summary>ClearUnorderedAccessViewUint/Float: every element of the view's range to the value
        /// (a buffer's first component per word for raw/structured views, as D3D clears them).</summary>
        public void ClearUav(D3DUnorderedAccessView view, uint[] values, bool asFloat) => Run(c => c.ClearUavImpl(view, values, asFloat), view);

        private void ClearUavImpl(D3DUnorderedAccessView view, uint[] values, bool asFloat)
        {
            ResolvePendingClears();
            if (view.Resource is D3DBuffer b)
            {
                // a raw/structured view is cleared word by word with the first value; a typed one with
                // the format's element (R32: the first; 128-bit: all four)
                int words = (view.Flags & 1) != 0 || (b.Desc.MiscFlags & D3D11_RESOURCE_MISC.BUFFER_STRUCTURED) != 0 ? 1 : view.BufferWords;
                ulong n = view.Size / 4;
                var data = new byte[n * 4];
                for (ulong i = 0; i < n; i++)
                {
                    uint v = values[(int)(i % (ulong)words)];
                    if (asFloat && words == 1 && (view.Flags & 1) == 0) v = BitConverter.SingleToUInt32Bits(BitConverter.UInt32BitsToSingle(v));
                    BitConverter.TryWriteBytes(data.AsSpan((int)(i * 4)), v);
                }
                WriteBufferOrdered(b.Gpu, (uint)view.Offset, data);
                return;
            }
            if (view.Resource is D3DTexture2D t)
            {
                // the view's mip, filled through the queue with the value in the view's format
                uint w = Math.Max(1, t.Desc.Width >> (int)view.Mip), h = Math.Max(1, t.Desc.Height >> (int)view.Mip);
                uint layers = t.Kind == 3 ? t.DepthAt(view.Mip) : (view.Desc.ViewDimension is 3 or 5 ? view.Desc.U2 : 1);
                uint first = view.Desc.ViewDimension is 3 or 5 ? view.Desc.U1 : 0;
                byte[] texel = ClearTexel(view.Desc.Format, values, asFloat);
                if (texel.Length == 0) { D3DLog.Once("uavclear:" + view.Desc.Format, $"ClearUnorderedAccessView of format {view.Desc.Format} is not supported"); return; }
                uint row = (uint)texel.Length * w, pitch = (row + 255) & ~255u;
                var data = new byte[pitch * h * Math.Max(1, layers)];
                for (uint l = 0; l < Math.Max(1, layers); l++)
                    for (uint yy = 0; yy < h; yy++)
                        for (uint xx = 0; xx < w; xx++)
                            Array.Copy(texel, 0, data, (l * h + yy) * pitch + xx * (uint)texel.Length, texel.Length);
                FlushToGpu(wait: false);
                fixed (byte* p = data)
                    D3DTexture2D.Upload(Device.Gpu, t.Gpu, view.Mip, new WGPUOrigin3D { z = first }, p, pitch, h, row, pitch * h,
                                        new WGPUExtent3D { width = w, height = h, depthOrArrayLayers = Math.Max(1, layers) });
                System.Threading.Interlocked.Increment(ref t.Version);
            }
        }

        /// <summary>A clear value as one texel of a format (the formats a typed UAV can have).</summary>
        private static byte[] ClearTexel(DXGI_FORMAT f, uint[] v, bool asFloat)
        {
            float F(int i) => asFloat ? BitConverter.UInt32BitsToSingle(v[i]) : v[i];
            byte U8(int i) => (byte)Math.Clamp((int)MathF.Round(Math.Clamp(F(i), 0f, 1f) * 255f), 0, 255);
            switch (f)
            {
                case DXGI_FORMAT.R32_FLOAT: return BitConverter.GetBytes(asFloat ? BitConverter.UInt32BitsToSingle(v[0]) : (float)v[0]);
                case DXGI_FORMAT.R32_UINT: case DXGI_FORMAT.R32_SINT: case DXGI_FORMAT.R32_TYPELESS: return BitConverter.GetBytes(v[0]);
                case DXGI_FORMAT.R32G32B32A32_FLOAT:
                {
                    var b = new byte[16];
                    for (int i = 0; i < 4; i++) BitConverter.TryWriteBytes(b.AsSpan(i * 4), asFloat ? BitConverter.UInt32BitsToSingle(v[i]) : (float)v[i]);
                    return b;
                }
                case DXGI_FORMAT.R32G32B32A32_UINT: case DXGI_FORMAT.R32G32B32A32_SINT:
                {
                    var b = new byte[16];
                    for (int i = 0; i < 4; i++) BitConverter.TryWriteBytes(b.AsSpan(i * 4), v[i]);
                    return b;
                }
                case DXGI_FORMAT.R8G8B8A8_UNORM: return asFloat ? new[] { U8(0), U8(1), U8(2), U8(3) } : new[] { (byte)v[0], (byte)v[1], (byte)v[2], (byte)v[3] };
                case DXGI_FORMAT.B8G8R8A8_UNORM: return asFloat ? new[] { U8(2), U8(1), U8(0), U8(3) } : new[] { (byte)v[2], (byte)v[1], (byte)v[0], (byte)v[3] };
                case DXGI_FORMAT.R16G16B16A16_FLOAT:
                {
                    var b = new byte[8];
                    for (int i = 0; i < 4; i++) BitConverter.TryWriteBytes(b.AsSpan(i * 2), (Half)F(i));
                    return b;
                }
                default: return Array.Empty<byte>();
            }
        }

        /// <summary>CopyStructureCount: a UAV's hidden counter into a buffer.</summary>
        public void CopyStructureCount(D3DBuffer dst, uint offset, D3DUnorderedAccessView src) => Run(c =>
        {
            if (src.Counter == IntPtr.Zero) { D3DLog.Once("nocounter", "CopyStructureCount from a view without a counter"); return; }
            c.ResolvePendingClears();
            if (dst.Gpu != IntPtr.Zero) wgpuCommandEncoderCopyBufferToBuffer(c.Encoder(), src.Counter, 0, dst.Gpu, offset, 4);
        }, dst, src);
    }
}
