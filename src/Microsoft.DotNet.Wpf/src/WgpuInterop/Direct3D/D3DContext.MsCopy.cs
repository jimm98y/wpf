// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// CopyResource and CopySubresourceRegion between multisampled textures (D3D11 copies them sample for
// sample, between textures of one sample count). WebGPU copies none, so the copy is a pass over the
// destination's rectangle whose fragment shader runs per sample (sample_index) and loads the same
// sample of the source. Depth is written as frag_depth; stencil cannot be written by a shader, so a
// depth-stencil copy keeps the destination's stencil (logged once).
//

using System;
using System.Collections.Generic;
using static Microsoft.Wpf.Interop.WebGpu.Wgpu;

namespace Microsoft.Wpf.Interop.WebGpu.Direct3D
{
    internal sealed unsafe partial class D3DContext
    {
        private readonly Dictionary<(WGPUTextureFormat, uint), (IntPtr Pipeline, IntPtr Layout)> _msCopy = new();

        /// <summary>The kind of texel a format holds, as a shader reads it: f float, u uint, i sint, d depth.</summary>
        private static char TexelKind(WGPUTextureFormat f)
        {
            if (Formats.IsDepth(f)) return 'd';
            return f switch
            {
                TexFmt.R8Uint or TexFmt.R16Uint or TexFmt.R32Uint or TexFmt.RG8Uint or TexFmt.RG16Uint or TexFmt.RG32Uint or TexFmt.RGBA8Uint
                    or TexFmt.RGBA16Uint or TexFmt.RGBA32Uint or TexFmt.RGB10A2Uint => 'u',
                TexFmt.R8Sint or TexFmt.R16Sint or TexFmt.R32Sint or TexFmt.RG8Sint or TexFmt.RG16Sint or TexFmt.RG32Sint or TexFmt.RGBA8Sint
                    or TexFmt.RGBA16Sint or TexFmt.RGBA32Sint => 'i',
                _ => 'f',
            };
        }

        /// <summary>A copy of a rectangle between two multisampled textures of one sample count; false where it
        /// cannot be done (the source cannot be read by a shader, the counts differ).</summary>
        private bool CopyMultisampled(D3DTexture2D st, uint sm, uint sl, uint sx, uint sy, D3DTexture2D dt, uint dm, uint dl, uint dx, uint dy, uint w, uint h)
        {
            if (st.SampleCount != dt.SampleCount || st.Format != dt.Format) return false;
            if ((st.GpuUsage & WGPUTextureUsage.TextureBinding) == 0 || st.MsShadowed) return false;
            char kind = TexelKind(st.Format);
            if (kind == 'd' && Formats.HasStencil(st.Format)) D3DLog.Once("copy-ms-stencil", "a multisampled depth-stencil copy copies the depth: the destination keeps its stencil");
            var (pipeline, layout) = MsCopyPipeline(st.Format, st.SampleCount, kind);
            if (pipeline == IntPtr.Zero) return false;

            var aspect = kind == 'd' ? WGPUTextureAspect.DepthOnly : WGPUTextureAspect.All;
            var viewFormat = st.Format == TexFmt.Depth24PlusStencil8 ? TexFmt.Depth24Plus : st.Format == TexFmt.Depth32FloatStencil8 ? TexFmt.Depth32Float : st.Format;
            IntPtr src = D3DView.CreateTextureView(st.Gpu, viewFormat, WGPUTextureViewDimension._2D, sm, 1, sl, 1, aspect);
            IntPtr dst = D3DView.CreateTextureView(dt.Gpu, dt.Format, WGPUTextureViewDimension._2D, dm, 1, dl, 1);
            // the source texel for the destination pixel: the pixel plus (sx - dx, sy - dy)
            var offset = new int[] { (int)sx - (int)dx, (int)sy - (int)dy, 0, 0 };
            IntPtr ub = Device.Gpu.CreateBufferMapped(System.Runtime.InteropServices.MemoryMarshal.AsBytes(offset.AsSpan()), WGPUBufferUsage.Uniform);
            var entries = stackalloc WGPUBindGroupEntry[2];
            entries[0] = new WGPUBindGroupEntry { binding = 0, textureView = src };
            entries[1] = new WGPUBindGroupEntry { binding = 1, buffer = ub, size = 16 };
            var bgd = new WGPUBindGroupDescriptor { layout = layout, entryCount = 2, entries = entries };
            IntPtr group = wgpuDeviceCreateBindGroup(Device.Gpu.Device, &bgd);

            EndPass();
            var color = new WGPURenderPassColorAttachment { view = dst, depthSlice = 0xFFFFFFFF, loadOp = WGPULoadOp.Load, storeOp = WGPUStoreOp.Store };
            var ds = new WGPURenderPassDepthStencilAttachment { view = dst, depthLoadOp = WGPULoadOp.Load, depthStoreOp = WGPUStoreOp.Store };
            if (Formats.HasStencil(dt.Format)) { ds.stencilLoadOp = WGPULoadOp.Load; ds.stencilStoreOp = WGPUStoreOp.Store; }
            var desc = kind == 'd'
                ? new WGPURenderPassDescriptor { depthStencilAttachment = (IntPtr)(&ds) }
                : new WGPURenderPassDescriptor { colorAttachmentCount = 1, colorAttachments = &color };
            IntPtr pass = wgpuCommandEncoderBeginRenderPass(Encoder(), &desc);
            wgpuRenderPassEncoderSetPipeline(pass, pipeline);
            wgpuRenderPassEncoderSetBindGroup(pass, 0, group, 0, null);
            wgpuRenderPassEncoderSetViewport(pass, dx, dy, w, h, 0, 1);
            wgpuRenderPassEncoderSetScissorRect(pass, dx, dy, w, h);
            wgpuRenderPassEncoderDraw(pass, 3, 1, 0, 0);
            wgpuRenderPassEncoderEnd(pass);
            wgpuRenderPassEncoderRelease(pass);
            wgpuBindGroupRelease(group);
            wgpuBufferRelease(ub);
            wgpuTextureViewRelease(src);
            wgpuTextureViewRelease(dst);
            return true;
        }

        private (IntPtr Pipeline, IntPtr Layout) MsCopyPipeline(WGPUTextureFormat format, uint samples, char kind)
        {
            if (_msCopy.TryGetValue((format, samples), out var cached)) return cached;
            string scalar = kind switch { 'u' => "u32", 'i' => "i32", _ => "f32" };
            string texture = kind == 'd' ? "texture_depth_multisampled_2d" : $"texture_multisampled_2d<{scalar}>";
            string output = kind == 'd' ? "@builtin(frag_depth) f32" : $"@location(0) vec4<{scalar}>";
            string wgsl =
                $"@group(0) @binding(0) var src : {texture};\n" +
                "@group(0) @binding(1) var<uniform> off : vec4<i32>;\n" +
                "@vertex fn vs(@builtin(vertex_index) i : u32) -> @builtin(position) vec4<f32> {\n" +
                "    var p = array<vec2<f32>, 3>(vec2<f32>(-1.0, -1.0), vec2<f32>(3.0, -1.0), vec2<f32>(-1.0, 3.0));\n" +
                "    return vec4<f32>(p[i], 0.0, 1.0);\n}\n" +
                $"@fragment fn fs(@builtin(position) at : vec4<f32>, @builtin(sample_index) s : u32) -> {output} {{\n" +
                "    return textureLoad(src, vec2<i32>(at.xy) + off.xy, i32(s));\n}\n";
            IntPtr module = CreateModule(wgsl, "mscopy", out string? error);
            if (module == IntPtr.Zero) { D3DLog.Once("copy-ms-module", $"the multisampled copy shader: {error}"); return default; }

            var entries = stackalloc WGPUBindGroupLayoutEntry[2];
            entries[0] = new WGPUBindGroupLayoutEntry
            {
                binding = 0, visibility = WGPUShaderStage_Fragment,
                texture = new WGPUTextureBindingLayout
                {
                    sampleType = kind switch { 'd' => WGPUTextureSampleType.Depth, 'u' => WGPUTextureSampleType.Uint, 'i' => WGPUTextureSampleType.Sint, _ => WGPUTextureSampleType.UnfilterableFloat },
                    viewDimension = WGPUTextureViewDimension._2D, multisampled = 1,
                },
            };
            entries[1] = new WGPUBindGroupLayoutEntry { binding = 1, visibility = WGPUShaderStage_Fragment, buffer = new WGPUBufferBindingLayout { type = WGPUBufferBindingType.Uniform, minBindingSize = 16 } };
            var ld = new WGPUBindGroupLayoutDescriptor { entryCount = 2, entries = entries };
            IntPtr bgl = wgpuDeviceCreateBindGroupLayout(Device.Gpu.Device, &ld);
            var pld = new WGPUPipelineLayoutDescriptor { bindGroupLayoutCount = 1, bindGroupLayouts = &bgl };
            IntPtr pl = wgpuDeviceCreatePipelineLayout(Device.Gpu.Device, &pld);

            byte* vsName = stackalloc byte[2] { (byte)'v', (byte)'s' };
            byte* fsName = stackalloc byte[2] { (byte)'f', (byte)'s' };
            var target = new WGPUColorTargetState { format = format, writeMask = 0xF };
            var frag = new WGPUFragmentState { module = module, entryPoint = new WGPUStringView { data = fsName, length = 2 }, targetCount = kind == 'd' ? 0u : 1u, targets = kind == 'd' ? null : &target };
            var keep = new WGPUStencilFaceState { compare = WGPUCompareFunction.Always, failOp = WGPUStencilOperation.Keep, depthFailOp = WGPUStencilOperation.Keep, passOp = WGPUStencilOperation.Keep };
            var dss = new WGPUDepthStencilState { format = format, depthWriteEnabled = WGPUOptionalBool.True, depthCompare = WGPUCompareFunction.Always, stencilFront = keep, stencilBack = keep };
            var desc = new WGPURenderPipelineDescriptor
            {
                layout = pl,
                vertex = new WGPUVertexState { module = module, entryPoint = new WGPUStringView { data = vsName, length = 2 } },
                primitive = new WGPUPrimitiveState { topology = WGPUPrimitiveTopology.TriangleList, frontFace = WGPUFrontFace.CW, cullMode = WGPUCullMode.None },
                depthStencil = kind == 'd' ? (IntPtr)(&dss) : IntPtr.Zero,
                multisample = new WGPUMultisampleState { count = samples, mask = 0xFFFFFFFF },
                fragment = &frag,
            };
            wgpuDevicePushErrorScope(Device.Gpu.Device, WGPUErrorFilter.Validation);
            IntPtr pipeline = PopScope(wgpuDeviceCreateRenderPipeline(Device.Gpu.Device, &desc), out string? why);
            if (pipeline == IntPtr.Zero) D3DLog.Once("copy-ms-pipeline", $"the multisampled copy pipeline: {why}");
            wgpuShaderModuleRelease(module);
            var r = (pipeline, bgl);
            _msCopy[(format, samples)] = r;
            return r;
        }
    }
}
