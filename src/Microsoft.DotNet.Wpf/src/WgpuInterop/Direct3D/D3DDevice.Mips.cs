// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// ID3D11DeviceContext::GenerateMips: each level of a view's mip range below its most detailed drawn from the
// level above, filtered (a box filter, as the hardware's: a linear sample in the middle of each 2x2), for each
// of its array layers (a cube's faces). WebGPU has no such call; a render pass per level does it. DXEngine
// makes a texture's chain so (TextureLoader: the top level written, the rest generated); with the call not
// there, every level but the top stayed empty, and what was drawn small or at a slant sampled transparent
// black -- a textured model vanished but for its nearest faces, and hid what was behind it.
//

using System;
using System.Collections.Generic;
using System.Text;
using static Microsoft.Wpf.Interop.WebGpu.Wgpu;

namespace Microsoft.Wpf.Interop.WebGpu.Direct3D
{
    internal sealed unsafe partial class D3DDevice
    {
        private readonly Dictionary<WGPUTextureFormat, IntPtr> _mipPipelines = new();
        private IntPtr _mipModule, _mipSampler;

        private const string MipWgsl = @"
@group(0) @binding(0) var src: texture_2d<f32>;
@group(0) @binding(1) var smp: sampler;
struct V { @builtin(position) pos: vec4<f32>, @location(0) uv: vec2<f32> };
@vertex fn vs(@builtin(vertex_index) i: u32) -> V {
    let p = vec2<f32>(f32((i << 1u) & 2u), f32(i & 2u));
    var o: V;
    o.pos = vec4<f32>(p * 2.0 - 1.0, 0.0, 1.0);
    o.uv = vec2<f32>(p.x, 1.0 - p.y);
    return o;
}
@fragment fn fs(v: V) -> @location(0) vec4<f32> { return textureSampleLevel(src, smp, v.uv, 0.0); }
";

        /// <summary>The levels of <paramref name="view"/>'s range below its most detailed, each from the one above,
        /// into <paramref name="encoder"/>. A texture that is no render target (D3D has GenerateMips need
        /// RENDER_TARGET and GENERATE_MIPS) is left as it is, as D3D leaves it.</summary>
        public void GenerateMips(IntPtr encoder, D3DShaderResourceView view)
        {
            if (view.Resource is not D3DTexture2D t || t.Gpu == IntPtr.Zero || t.SampleCount > 1) return;
            if ((t.GpuUsage & WGPUTextureUsage.RenderAttachment) == 0 || (t.GpuUsage & WGPUTextureUsage.TextureBinding) == 0) return;
            var d = view.Desc;
            uint top = d.U0, mips = d.U1 == uint.MaxValue || d.U1 == 0 ? t.MipLevels - top : d.U1;
            if (t.Kind == 3) return;   // a 3D texture: not drawn level by level here
            uint layer = 0, layers = 1;
            switch (d.ViewDimension)
            {
                case 3: case 5: layer = d.U2; layers = d.U3; break;   // 1D and 2D arrays
                case 9: layers = 6; break;                             // a cube's faces
                case 10: layer = d.U2 * 6; layers = d.U3 * 6; break;   // a cube array's
            }
            if (mips < 2) return;
            var fmt = Formats.Texture(d.Format);
            if (Formats.IsDepth(fmt)) return;
            IntPtr pipeline;
            lock (Sync)
            {
                if (!_mipPipelines.TryGetValue(fmt, out pipeline))
                {
                    pipeline = CreateMipPipeline(fmt);
                    _mipPipelines[fmt] = pipeline;
                }
            }
            if (pipeline == IntPtr.Zero) return;
            IntPtr bgl = wgpuRenderPipelineGetBindGroupLayout(pipeline, 0);
            for (uint l = layer; l < layer + layers; l++)
            {
                for (uint m = top + 1; m < top + mips; m++)
                {
                    IntPtr from = D3DView.CreateTextureView(t.Gpu, fmt, WGPUTextureViewDimension._2D, m - 1, 1, l, 1);
                    IntPtr to = D3DView.CreateTextureView(t.Gpu, fmt, WGPUTextureViewDimension._2D, m, 1, l, 1);
                    var entries = stackalloc WGPUBindGroupEntry[2];
                    entries[0] = new WGPUBindGroupEntry { binding = 0, textureView = from };
                    entries[1] = new WGPUBindGroupEntry { binding = 1, sampler = _mipSampler };
                    var bgd = new WGPUBindGroupDescriptor { layout = bgl, entryCount = 2, entries = entries };
                    IntPtr bg = wgpuDeviceCreateBindGroup(Gpu.Device, &bgd);
                    var att = new WGPURenderPassColorAttachment { view = to, depthSlice = 0xFFFFFFFF, loadOp = WGPULoadOp.Clear, storeOp = WGPUStoreOp.Store };
                    var desc = new WGPURenderPassDescriptor { colorAttachmentCount = 1, colorAttachments = &att };
                    IntPtr pass = wgpuCommandEncoderBeginRenderPass(encoder, &desc);
                    wgpuRenderPassEncoderSetPipeline(pass, pipeline);
                    wgpuRenderPassEncoderSetBindGroup(pass, 0, bg, 0, null);
                    wgpuRenderPassEncoderDraw(pass, 3, 1, 0, 0);
                    wgpuRenderPassEncoderEnd(pass);
                    wgpuRenderPassEncoderRelease(pass);
                    wgpuBindGroupRelease(bg);
                    wgpuTextureViewRelease(from);
                    wgpuTextureViewRelease(to);
                }
            }
            wgpuBindGroupLayoutRelease(bgl);
            t.Version++;
        }

        private IntPtr CreateMipPipeline(WGPUTextureFormat format)
        {
            if (_mipModule == IntPtr.Zero)
            {
                byte[] code = Encoding.UTF8.GetBytes(MipWgsl);
                fixed (byte* c = code)
                {
                    var src = new WGPUShaderSourceWGSL { chain = new WGPUChainedStruct { next = null, sType = WGPUSType_ShaderSourceWGSL }, code = new WGPUStringView { data = c, length = (nuint)code.Length } };
                    var md = new WGPUShaderModuleDescriptor { nextInChain = (WGPUChainedStruct*)&src };
                    _mipModule = wgpuDeviceCreateShaderModule(Gpu.Device, &md);
                }
                var sd = new WGPUSamplerDescriptor
                {
                    addressModeU = WGPUAddressMode.ClampToEdge, addressModeV = WGPUAddressMode.ClampToEdge, addressModeW = WGPUAddressMode.ClampToEdge,
                    magFilter = WGPUFilterMode.Linear, minFilter = WGPUFilterMode.Linear, mipmapFilter = WGPUMipmapFilterMode.Nearest,
                    lodMinClamp = 0f, lodMaxClamp = 32f, compare = WGPUCompareFunction.Undefined, maxAnisotropy = 1,
                };
                _mipSampler = wgpuDeviceCreateSampler(Gpu.Device, &sd);
            }
            byte[] vsName = Encoding.UTF8.GetBytes("vs"), fsName = Encoding.UTF8.GetBytes("fs");
            fixed (byte* vn = vsName)
            fixed (byte* fn = fsName)
            {
                var target = new WGPUColorTargetState { format = format, writeMask = WGPUColorWriteMask_All };
                var frag = new WGPUFragmentState { module = _mipModule, entryPoint = new WGPUStringView { data = fn, length = 2 }, targetCount = 1, targets = &target };
                var desc = new WGPURenderPipelineDescriptor
                {
                    layout = IntPtr.Zero,   // the shader's own (auto)
                    vertex = new WGPUVertexState { module = _mipModule, entryPoint = new WGPUStringView { data = vn, length = 2 } },
                    primitive = new WGPUPrimitiveState { topology = WGPUPrimitiveTopology.TriangleList, frontFace = WGPUFrontFace.CCW, cullMode = WGPUCullMode.None },
                    multisample = new WGPUMultisampleState { count = 1, mask = 0xFFFFFFFF },
                    fragment = &frag,
                };
                return wgpuDeviceCreateRenderPipeline(Gpu.Device, &desc);
            }
        }
    }
}
