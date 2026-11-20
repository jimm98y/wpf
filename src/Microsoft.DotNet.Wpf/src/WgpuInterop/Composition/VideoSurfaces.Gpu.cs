// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A video frame of NV12 into its window's surface on the GPU (VideoSurfaces.cs): the frame's bytes as one 8-bit texture
// (the luma rows, then the interleaved chroma rows), converted by one render pass into the surface's texture -- BT.601
// or BT.709, of the limited or the full range, in the 8-bit fixed point Windows' converters use, each chroma sample for
// its 2x2 pixels. The surface's texture is BGRA8Unorm (its bytes as converted), sampled through an sRGB view where the
// display is sRGB.
//

using System;
using System.Text;
using Microsoft.Wpf.Interop.WebGpu.Direct3D;
using static Microsoft.Wpf.Interop.WebGpu.Wgpu;

namespace Microsoft.Wpf.Interop.WebGpu.Composition
{
    internal static unsafe partial class VideoSurfaces
    {
        private const string Nv12Wgsl = @"
struct P { info: vec4<u32> };   // luma height, 1 of BT.709, 1 of the full range, 0
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
    let cx = (x / 2) * 2; let cy = i32(p.info.x) + y / 2;
    let d = code(cx, cy) - 128; let e = code(cx + 1, cy) - 128;
    var c = 298 * (code(x, y) - 16);
    var rv = 409; var gu = 100; var gv = 208; var bu = 516;
    if (p.info.y == 1u) { rv = 459; gu = 55; gv = 136; bu = 541; }
    if (p.info.z == 1u) {
        c = 256 * code(x, y);
        rv = 359; gu = 88; gv = 183; bu = 454;
        if (p.info.y == 1u) { rv = 403; gu = 48; gv = 120; bu = 475; }
    }
    let b = (c + bu * d + 128) >> 8u;
    let g = (c - gu * d - gv * e + 128) >> 8u;
    let r = (c + rv * e + 128) >> 8u;
    return vec4<f32>(byte(r), byte(g), byte(b), 1.0);
}
";

        private static IntPtr s_device, s_layout, s_pipeline;

        private static void EnsurePipeline(WgpuContext ctx)
        {
            if (s_device == ctx.Device && s_pipeline != IntPtr.Zero) return;
            byte[] code = Encoding.UTF8.GetBytes(Nv12Wgsl);
            IntPtr module;
            fixed (byte* p = code)
            {
                var src = new WGPUShaderSourceWGSL { chain = new WGPUChainedStruct { next = null, sType = WGPUSType_ShaderSourceWGSL }, code = new WGPUStringView { data = p, length = (nuint)code.Length } };
                var desc = new WGPUShaderModuleDescriptor { nextInChain = (WGPUChainedStruct*)&src };
                module = wgpuDeviceCreateShaderModule(ctx.Device, &desc);
            }
            var entries = stackalloc WGPUBindGroupLayoutEntry[2];
            entries[0] = new WGPUBindGroupLayoutEntry
            {
                binding = 0, visibility = WGPUShaderStage_Fragment,
                texture = new WGPUTextureBindingLayout { sampleType = WGPUTextureSampleType.UnfilterableFloat, viewDimension = WGPUTextureViewDimension._2D },
            };
            entries[1] = new WGPUBindGroupLayoutEntry { binding = 1, visibility = WGPUShaderStage_Fragment, buffer = new WGPUBufferBindingLayout { type = WGPUBufferBindingType.Uniform, minBindingSize = 16 } };
            var ld = new WGPUBindGroupLayoutDescriptor { entryCount = 2, entries = entries };
            IntPtr layout = wgpuDeviceCreateBindGroupLayout(ctx.Device, &ld);
            var pld = new WGPUPipelineLayoutDescriptor { bindGroupLayoutCount = 1, bindGroupLayouts = &layout };
            IntPtr pl = wgpuDeviceCreatePipelineLayout(ctx.Device, &pld);
            byte[] vsName = Encoding.UTF8.GetBytes("vs"), fsName = Encoding.UTF8.GetBytes("fs");
            fixed (byte* vn = vsName)
            fixed (byte* fn = fsName)
            {
                var target = new WGPUColorTargetState { format = WGPUTextureFormat.BGRA8Unorm, writeMask = 0xF };
                var frag = new WGPUFragmentState { module = module, entryPoint = new WGPUStringView { data = fn, length = 2 }, targetCount = 1, targets = &target };
                var desc = new WGPURenderPipelineDescriptor
                {
                    layout = pl,
                    vertex = new WGPUVertexState { module = module, entryPoint = new WGPUStringView { data = vn, length = 2 } },
                    primitive = new WGPUPrimitiveState { topology = WGPUPrimitiveTopology.TriangleList, frontFace = WGPUFrontFace.CCW, cullMode = WGPUCullMode.None },
                    multisample = new WGPUMultisampleState { count = 1, mask = 0xFFFFFFFF },
                    fragment = &frag,
                };
                s_pipeline = wgpuDeviceCreateRenderPipeline(ctx.Device, &desc);
            }
            s_layout = layout;
            s_device = ctx.Device;
        }

        private sealed class Planes { public IntPtr Texture, View; public int W, Rows; }
        private static readonly System.Collections.Generic.Dictionary<IntPtr, Planes> s_planes = new();

        /// <summary>A frame of tight NV12 into the window's surface, converted on the GPU.</summary>
        public static void PresentNv12(IntPtr hwnd, ReadOnlySpan<byte> nv12, int width, int height, float aspect, bool bt709, bool fullRange)
        {
            int cw = (width + 1) & ~1, rows = height + (height + 1) / 2;
            if (hwnd == IntPtr.Zero || width <= 0 || height <= 0 || nv12.Length < width * height + cw * ((height + 1) / 2)) return;
            var ctx = Device();
            {
                lock (s_lock)
                {
                    EnsurePipeline(ctx);
                    if (!s_surfaces.TryGetValue(hwnd, out var s)) s_surfaces[hwnd] = s = new Surface { Hwnd = hwnd };
                    var shown = SrgbDisplay ? WGPUTextureFormat.BGRA8UnormSrgb : WGPUTextureFormat.BGRA8Unorm;
                    if (s.Image == null || s.Image.Width != width || s.Image.Height != height || s.Image.Format != shown || s.Image.Device != ctx.Device)
                    {
                        var srgb = WGPUTextureFormat.BGRA8UnormSrgb;
                        var d = new WGPUTextureDescriptor
                        {
                            usage = WGPUTextureUsage.TextureBinding | WGPUTextureUsage.CopyDst | WGPUTextureUsage.CopySrc | WGPUTextureUsage.RenderAttachment,
                            dimension = WGPUTextureDimension._2D,
                            size = new WGPUExtent3D { width = (uint)width, height = (uint)height, depthOrArrayLayers = 1 },
                            format = WGPUTextureFormat.BGRA8Unorm, mipLevelCount = 1, sampleCount = 1, viewFormatCount = 1, viewFormats = &srgb,
                        };
                        IntPtr tex = wgpuDeviceCreateTexture(ctx.Device, &d);
                        IntPtr view = D3DView.CreateTextureView(tex, shown, WGPUTextureViewDimension._2D, 0, 1, 0, 1);
                        s.Image = new ExternalImage(ctx.Device, tex, view, width, height, shown);
                    }
                    // the frame's bytes: one 8-bit texture of the luma rows, then the chroma rows
                    if (!s_planes.TryGetValue(hwnd, out var pl) || pl.W != cw || pl.Rows != rows)
                    {
                        if (pl != null) { wgpuTextureViewRelease(pl.View); wgpuTextureRelease(pl.Texture); }
                        var d = new WGPUTextureDescriptor
                        {
                            usage = WGPUTextureUsage.TextureBinding | WGPUTextureUsage.CopyDst, dimension = WGPUTextureDimension._2D,
                            size = new WGPUExtent3D { width = (uint)cw, height = (uint)rows, depthOrArrayLayers = 1 },
                            format = WGPUTextureFormat.R8Unorm, mipLevelCount = 1, sampleCount = 1,
                        };
                        IntPtr t = wgpuDeviceCreateTexture(ctx.Device, &d);
                        s_planes[hwnd] = pl = new Planes { Texture = t, View = D3DView.CreateTextureView(t, WGPUTextureFormat.R8Unorm, WGPUTextureViewDimension._2D, 0, 1, 0, 1), W = cw, Rows = rows };
                    }
                    ctx.ResetGlUnpack();
                    // (of an odd width the luma rows padded to the chroma's)
                    byte[]? padded = null;
                    if (cw != width)
                    {
                        padded = new byte[cw * rows];
                        for (int y = 0; y < height; y++) nv12.Slice(y * width, width).CopyTo(padded.AsSpan(y * cw));
                        nv12.Slice(width * height, cw * ((height + 1) / 2)).CopyTo(padded.AsSpan(cw * height));
                    }
                    ReadOnlySpan<byte> bytes = padded ?? nv12;
                    fixed (byte* p = bytes)
                    {
                        var dst = new WGPUTexelCopyTextureInfo { texture = pl.Texture, aspect = WGPUTextureAspect.All };
                        var layout = new WGPUTexelCopyBufferLayout { bytesPerRow = (uint)cw, rowsPerImage = (uint)rows };
                        var size = new WGPUExtent3D { width = (uint)cw, height = (uint)rows, depthOrArrayLayers = 1 };
                        wgpuQueueWriteTexture(ctx.Queue, &dst, p, (nuint)(cw * rows), &layout, &size);
                    }
                    var parms = new byte[16];
                    BitConverter.TryWriteBytes(parms.AsSpan(0), (uint)height);
                    BitConverter.TryWriteBytes(parms.AsSpan(4), bt709 ? 1u : 0u);
                    BitConverter.TryWriteBytes(parms.AsSpan(8), fullRange ? 1u : 0u);
                    IntPtr ubuf = ctx.CreateBufferMapped(parms, WGPUBufferUsage.Uniform);
                    var be = stackalloc WGPUBindGroupEntry[2];
                    be[0] = new WGPUBindGroupEntry { binding = 0, textureView = pl.View };
                    be[1] = new WGPUBindGroupEntry { binding = 1, buffer = ubuf, offset = 0, size = 16 };
                    var bgd = new WGPUBindGroupDescriptor { layout = s_layout, entryCount = 2, entries = be };
                    IntPtr group = wgpuDeviceCreateBindGroup(ctx.Device, &bgd);
                    IntPtr target = D3DView.CreateTextureView(s.Image.Texture, WGPUTextureFormat.BGRA8Unorm, WGPUTextureViewDimension._2D, 0, 1, 0, 1);
                    IntPtr enc = wgpuDeviceCreateCommandEncoder(ctx.Device, IntPtr.Zero);
                    var att = new WGPURenderPassColorAttachment { view = target, depthSlice = 0xFFFFFFFF, loadOp = WGPULoadOp.Clear, storeOp = WGPUStoreOp.Store };
                    var rp = new WGPURenderPassDescriptor { colorAttachmentCount = 1, colorAttachments = &att };
                    IntPtr pass = wgpuCommandEncoderBeginRenderPass(enc, &rp);
                    wgpuRenderPassEncoderSetPipeline(pass, s_pipeline);
                    wgpuRenderPassEncoderSetBindGroup(pass, 0, group, 0, null);
                    wgpuRenderPassEncoderDraw(pass, 3, 1, 0, 0);
                    wgpuRenderPassEncoderEnd(pass);
                    wgpuRenderPassEncoderRelease(pass);
                    IntPtr cmd = wgpuCommandEncoderFinish(enc, IntPtr.Zero);
                    wgpuQueueSubmit(ctx.Queue, 1, &cmd);
                    wgpuCommandBufferRelease(cmd);
                    wgpuCommandEncoderRelease(enc);
                    wgpuTextureViewRelease(target);
                    wgpuBindGroupRelease(group);
                    wgpuBufferRelease(ubuf);
                    s.Aspect = aspect > 0 ? aspect : (float)width / height;
                    s.Image.Bump();
                    s.Version++;
                }
                Changed(hwnd);
            }

        }
    }
}
