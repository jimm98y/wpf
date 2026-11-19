// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The source reader's RGB of a decoder's NV12 texture, on the GPU (MediaFoundation/Source/ReaderStream.ToRgb): one
// render pass of the shim's own into a B8G8R8A8 / B8G8R8X8 texture of the device, of the same integers as the CPU's
// conversion (ReaderStream.Nv12ToRgb: BT.601 or BT.709 in 8-bit fixed point, limited range, each chroma sample for its
// 2x2 pixels) -- the frame read back never. A planar texture is stored as its bytes lie in memory (D3DTexture2D.PlaneRows):
// the luma rows, then the interleaved chroma rows.
//

using System;
using System.Collections.Generic;
using System.Text;
using static Microsoft.Wpf.Interop.WebGpu.Wgpu;

namespace Microsoft.Wpf.Interop.WebGpu.Direct3D
{
    internal sealed unsafe partial class D3DContext
    {
        private const string Nv12Wgsl = @"
struct P { info: vec4<u32> };   // luma height, 1 of BT.709, the alpha byte, 0
@group(0) @binding(0) var tex: texture_2d<f32>;
@group(0) @binding(1) var<uniform> p: P;

@vertex
fn vs(@builtin(vertex_index) i: u32) -> @builtin(position) vec4<f32> {
    let x = f32((i << 1u) & 2u) * 2.0 - 1.0;
    let y = f32(i & 2u) * 2.0 - 1.0;
    return vec4<f32>(x, y, 0.0, 1.0);
}

fn code(x: i32, y: i32) -> i32 { return i32(round(textureLoad(tex, vec2<i32>(x, y), 0).r * 255.0)); }
fn byte(v: i32) -> f32 { return f32(clamp(v, 0, 255)) / 255.0; }

@fragment
fn fs(@builtin(position) pos: vec4<f32>) -> @location(0) vec4<f32> {
    let x = i32(pos.x); let y = i32(pos.y);
    let c = 298 * (code(x, y) - 16);
    let cx = (x / 2) * 2; let cy = i32(p.info.x) + y / 2;
    let d = code(cx, cy) - 128; let e = code(cx + 1, cy) - 128;
    var rv = 409; var gu = 100; var gv = 208; var bu = 516;
    if (p.info.y == 1u) { rv = 459; gu = 55; gv = 136; bu = 541; }
    let b = (c + bu * d + 128) >> 8u;
    let g = (c - gu * d - gv * e + 128) >> 8u;
    let r = (c + rv * e + 128) >> 8u;
    return vec4<f32>(byte(r), byte(g), byte(b), f32(p.info.z) / 255.0);
}
";

        private readonly Dictionary<WGPUTextureFormat, (IntPtr Layout, IntPtr Pipeline)> _nv12Pipelines = new();

        private (IntPtr Layout, IntPtr Pipeline) Nv12Pipeline(WGPUTextureFormat outFormat)
        {
            if (_nv12Pipelines.TryGetValue(outFormat, out var known)) return known;
            IntPtr module = CreateModule(Nv12Wgsl, "nv12-to-rgb", out string? err);
            if (module == IntPtr.Zero) throw new InvalidOperationException("NV12 to RGB shader: " + err);
            var entries = stackalloc WGPUBindGroupLayoutEntry[2];
            entries[0] = new WGPUBindGroupLayoutEntry
            {
                binding = 0, visibility = WGPUShaderStage_Fragment,
                texture = new WGPUTextureBindingLayout { sampleType = WGPUTextureSampleType.UnfilterableFloat, viewDimension = WGPUTextureViewDimension._2D },
            };
            entries[1] = new WGPUBindGroupLayoutEntry { binding = 1, visibility = WGPUShaderStage_Fragment, buffer = new WGPUBufferBindingLayout { type = WGPUBufferBindingType.Uniform, minBindingSize = 16 } };
            var ld = new WGPUBindGroupLayoutDescriptor { entryCount = 2, entries = entries };
            IntPtr layout = wgpuDeviceCreateBindGroupLayout(Device.Gpu.Device, &ld);
            var pld = new WGPUPipelineLayoutDescriptor { bindGroupLayoutCount = 1, bindGroupLayouts = &layout };
            IntPtr pl = wgpuDeviceCreatePipelineLayout(Device.Gpu.Device, &pld);
            byte[] vsName = Encoding.UTF8.GetBytes("vs"), fsName = Encoding.UTF8.GetBytes("fs");
            IntPtr pipeline;
            fixed (byte* vn = vsName)
            fixed (byte* fn = fsName)
            {
                var target = new WGPUColorTargetState { format = outFormat, writeMask = 0xF };
                var frag = new WGPUFragmentState { module = module, entryPoint = new WGPUStringView { data = fn, length = 2 }, targetCount = 1, targets = &target };
                var desc = new WGPURenderPipelineDescriptor
                {
                    layout = pl,
                    vertex = new WGPUVertexState { module = module, entryPoint = new WGPUStringView { data = vn, length = 2 } },
                    primitive = new WGPUPrimitiveState { topology = WGPUPrimitiveTopology.TriangleList, frontFace = WGPUFrontFace.CCW, cullMode = WGPUCullMode.None },
                    multisample = new WGPUMultisampleState { count = 1, mask = 0xFFFFFFFF },
                    fragment = &frag,
                };
                wgpuDevicePushErrorScope(Device.Gpu.Device, WGPUErrorFilter.Validation);
                pipeline = PopScope(Device.Gpu, wgpuDeviceCreateRenderPipeline(Device.Gpu.Device, &desc), out err);
            }
            if (pipeline == IntPtr.Zero) throw new InvalidOperationException("NV12 to RGB pipeline: " + err);
            return _nv12Pipelines[outFormat] = (layout, pipeline);
        }

        /// <summary>
        /// The NV12 slice of <paramref name="src"/> (8-bit, its luma <paramref name="h"/> rows high) into the whole of
        /// <paramref name="dst"/> (BGRA of the same size): the reader's conversion, its alpha byte <paramref name="alpha"/>.
        /// </summary>
        internal void Nv12ToBgra(D3DTexture2D src, uint slice, D3DTexture2D dst, uint h, bool bt709, byte alpha)
        {
            ResolvePendingClears();
            EndPass();
            var (layout, pipeline) = Nv12Pipeline(dst.Format);
            IntPtr view = D3DView.CreateTextureView(dst.Gpu, dst.Format, WGPUTextureViewDimension._2D, 0, 1, 0, 1);
            IntPtr srcView = D3DView.CreateTextureView(src.Gpu, src.Format, WGPUTextureViewDimension._2D, 0, 1, slice, 1);
            var parms = new byte[16];
            BitConverter.TryWriteBytes(parms.AsSpan(0), h);
            BitConverter.TryWriteBytes(parms.AsSpan(4), bt709 ? 1u : 0u);
            BitConverter.TryWriteBytes(parms.AsSpan(8), (uint)alpha);
            ulong at = ArenaWrite(parms);
            IntPtr group = MakeGroup(layout, new List<WGPUBindGroupEntry>
            {
                new() { binding = 0, textureView = srcView },
                new() { binding = 1, buffer = _arenas[_arenaIndex], offset = at, size = 16 },
            });
            var att = new WGPURenderPassColorAttachment { view = view, depthSlice = 0xFFFFFFFF, loadOp = WGPULoadOp.Clear, storeOp = WGPUStoreOp.Store };
            var rp = new WGPURenderPassDescriptor { colorAttachmentCount = 1, colorAttachments = &att };
            IntPtr pass = wgpuCommandEncoderBeginRenderPass(Encoder(), &rp);
            try
            {
                wgpuRenderPassEncoderSetPipeline(pass, pipeline);
                wgpuRenderPassEncoderSetBindGroup(pass, 0, group, 0, null);
                wgpuRenderPassEncoderDraw(pass, 3, 1, 0, 0);
            }
            finally
            {
                wgpuRenderPassEncoderEnd(pass);
                wgpuRenderPassEncoderRelease(pass);
                wgpuBindGroupRelease(group);
                wgpuTextureViewRelease(srcView);
                wgpuTextureViewRelease(view);
            }
            dst.Version++;
        }
    }
}
