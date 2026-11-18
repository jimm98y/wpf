// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A depth surface larger than the render target, which D3D9 allows (the device's automatic depth
// surface with a smaller texture as the target is the common case) and WebGPU does not: a pass's
// attachments are one size. The pass is given a proxy of the target's size instead, the depth
// surface's top-left corner -- what D3D9 draws with -- drawn into it before the pass and back after
// it. Copies cannot do it (a depth texture is only ever copied whole), so each is a draw that writes
// frag_depth from the other texture. Stencil cannot be written that way: the proxy keeps its own
// across passes, and the large surface's is not shared with it.
//

using System;
using System.Collections.Generic;
using static Microsoft.Wpf.Interop.WebGpu.Wgpu;

namespace Microsoft.Wpf.Interop.WebGpu.Direct3D
{
    internal sealed unsafe partial class Direct3DDevice9
    {
        private sealed class DepthProxy
        {
            public IntPtr Texture, View, SampleView;
            public WGPUTextureFormat Format;
            public uint Width, Height;
        }

        private readonly Dictionary<(IntPtr Source, uint W, uint H), DepthProxy> _depthProxies = new();
        /// <summary>The pass open now draws into this proxy of that surface's corner: drawn back when it ends.</summary>
        private (DepthProxy Proxy, Direct3DSurface9 Source)? _proxyPass;
        private IntPtr _depthBlitLayout, _depthBlitPl, _depthBlitModule;
        private readonly Dictionary<WGPUTextureFormat, IntPtr> _depthBlitPipelines = new();

        /// <summary>Whether depth surface <paramref name="ds"/> can stand in for a target of this size through a proxy.</summary>
        private static bool CanProxy(Direct3DSurface9 ds, uint w, uint h)
            => ds.Width >= w && ds.Height >= h && (ds.Gpu!.GpuUsage & WGPUTextureUsage.TextureBinding) != 0;

        /// <summary>The proxy's view for a pass about to begin, its depth drawn from the surface's corner.</summary>
        private IntPtr BeginProxy(Direct3DSurface9 ds, uint w, uint h)
        {
            var key = (ds.Gpu!.Gpu, w, h);
            if (!_depthProxies.TryGetValue(key, out var p))
            {
                var fmt = ds.Gpu.Format;
                var d = new WGPUTextureDescriptor
                {
                    usage = WGPUTextureUsage.RenderAttachment | WGPUTextureUsage.TextureBinding, dimension = WGPUTextureDimension._2D,
                    size = new WGPUExtent3D { width = w, height = h, depthOrArrayLayers = 1 }, format = fmt, mipLevelCount = 1, sampleCount = 1,
                };
                p = new DepthProxy { Format = fmt, Width = w, Height = h, Texture = wgpuDeviceCreateTexture(Device11.Gpu.Device, &d) };
                p.View = D3DView.CreateTextureView(p.Texture, fmt, WGPUTextureViewDimension._2D, 0, 1, 0, 1);
                p.SampleView = D3DView.CreateTextureView(p.Texture, DepthAspectFormat(fmt), WGPUTextureViewDimension._2D, 0, 1, 0, 1, WGPUTextureAspect.DepthOnly);
                _depthProxies[key] = p;
                D3DLog.Once("d3d9dsproxy", $"a {ds.Width}x{ds.Height} depth surface with a {w}x{h} render target: drawn through a proxy of its corner (its stencil is the proxy's own)");
            }
            IntPtr from = D3DView.CreateTextureView(ds.Gpu.Gpu, DepthAspectFormat(ds.Gpu.Format), WGPUTextureViewDimension._2D, ds.Level, 1, ds.Layer, 1, WGPUTextureAspect.DepthOnly);
            BlitDepth(from, p.View, p.Format, 0, 0, w, h);
            wgpuTextureViewRelease(from);
            return p.View;
        }

        /// <summary>The pass that drew into a proxy has ended: its depth back into the surface's corner.</summary>
        private void EndProxy()
        {
            if (_proxyPass is not { } pp) return;
            _proxyPass = null;
            var (p, ds) = pp;
            IntPtr to = D3DView.CreateTextureView(ds.Gpu!.Gpu, ds.Gpu.Format, WGPUTextureViewDimension._2D, ds.Level, 1, ds.Layer, 1);
            BlitDepth(p.SampleView, to, ds.Gpu.Format, 0, 0, p.Width, p.Height);
            wgpuTextureViewRelease(to);
        }

        private static WGPUTextureFormat DepthAspectFormat(WGPUTextureFormat f)
            => f == TexFmt.Depth24PlusStencil8 ? TexFmt.Depth24Plus : f == TexFmt.Depth32FloatStencil8 ? TexFmt.Depth32Float : f;

        /// <summary>A pass that writes the depth of <paramref name="source"/> (a depth-only view) at each pixel of
        /// the rectangle into <paramref name="target"/>, keeping the rest of it and its stencil.</summary>
        private void BlitDepth(IntPtr source, IntPtr target, WGPUTextureFormat format, uint x, uint y, uint w, uint h)
        {
            IntPtr pipeline = DepthBlitPipeline(format);
            var e = new WGPUBindGroupEntry { binding = 0, textureView = source };
            var bd = new WGPUBindGroupDescriptor { layout = _depthBlitLayout, entryCount = 1, entries = &e };
            IntPtr group = wgpuDeviceCreateBindGroup(Device11.Gpu.Device, &bd);
            var ds = new WGPURenderPassDepthStencilAttachment
            {
                view = target, depthLoadOp = WGPULoadOp.Load, depthStoreOp = WGPUStoreOp.Store,
            };
            if (Formats.HasStencil(format)) { ds.stencilLoadOp = WGPULoadOp.Load; ds.stencilStoreOp = WGPUStoreOp.Store; }
            var desc = new WGPURenderPassDescriptor { depthStencilAttachment = (IntPtr)(&ds) };
            IntPtr pass = wgpuCommandEncoderBeginRenderPass(Encoder(), &desc);
            wgpuRenderPassEncoderSetPipeline(pass, pipeline);
            wgpuRenderPassEncoderSetBindGroup(pass, 0, group, 0, null);
            wgpuRenderPassEncoderSetViewport(pass, x, y, w, h, 0, 1);
            wgpuRenderPassEncoderSetScissorRect(pass, x, y, w, h);
            wgpuRenderPassEncoderDraw(pass, 3, 1, 0, 0);
            wgpuRenderPassEncoderEnd(pass);
            wgpuRenderPassEncoderRelease(pass);
            wgpuBindGroupRelease(group);
        }

        private IntPtr DepthBlitPipeline(WGPUTextureFormat format)
        {
            if (_depthBlitPipelines.TryGetValue(format, out IntPtr p)) return p;
            if (_depthBlitLayout == IntPtr.Zero)
            {
                var entry = new WGPUBindGroupLayoutEntry
                {
                    binding = 0, visibility = WGPUShaderStage_Fragment,
                    texture = new WGPUTextureBindingLayout { sampleType = WGPUTextureSampleType.Depth, viewDimension = WGPUTextureViewDimension._2D },
                };
                var ld = new WGPUBindGroupLayoutDescriptor { entryCount = 1, entries = &entry };
                _depthBlitLayout = wgpuDeviceCreateBindGroupLayout(Device11.Gpu.Device, &ld);
                IntPtr bgl = _depthBlitLayout;
                var pld = new WGPUPipelineLayoutDescriptor { bindGroupLayoutCount = 1, bindGroupLayouts = &bgl };
                _depthBlitPl = wgpuDeviceCreatePipelineLayout(Device11.Gpu.Device, &pld);
                // a triangle over the viewport; each pixel's depth loaded from the source at the same pixel
                _depthBlitModule = Module("d3d9:depthblit",
                    "@group(0) @binding(0) var src : texture_depth_2d;\n" +
                    "@vertex fn vs(@builtin(vertex_index) i : u32) -> @builtin(position) vec4<f32> {\n" +
                    "    var p = array<vec2<f32>, 3>(vec2<f32>(-1.0, -1.0), vec2<f32>(3.0, -1.0), vec2<f32>(-1.0, 3.0));\n" +
                    "    return vec4<f32>(p[i], 0.0, 1.0);\n}\n" +
                    "@fragment fn fs(@builtin(position) at : vec4<f32>) -> @builtin(frag_depth) f32 {\n" +
                    "    return textureLoad(src, vec2<i32>(at.xy), 0);\n}\n");
            }
            byte* vsName = stackalloc byte[2] { (byte)'v', (byte)'s' };
            byte* fsName = stackalloc byte[2] { (byte)'f', (byte)'s' };
            var frag = new WGPUFragmentState { module = _depthBlitModule, entryPoint = new WGPUStringView { data = fsName, length = 2 } };
            var keep = new WGPUStencilFaceState { compare = WGPUCompareFunction.Always, failOp = WGPUStencilOperation.Keep, depthFailOp = WGPUStencilOperation.Keep, passOp = WGPUStencilOperation.Keep };
            var ds = new WGPUDepthStencilState
            {
                format = format, depthWriteEnabled = WGPUOptionalBool.True, depthCompare = WGPUCompareFunction.Always,
                stencilFront = keep, stencilBack = keep, stencilReadMask = 0, stencilWriteMask = 0,
            };
            var desc = new WGPURenderPipelineDescriptor
            {
                layout = _depthBlitPl,
                vertex = new WGPUVertexState { module = _depthBlitModule, entryPoint = new WGPUStringView { data = vsName, length = 2 } },
                primitive = new WGPUPrimitiveState { topology = WGPUPrimitiveTopology.TriangleList, frontFace = WGPUFrontFace.CW, cullMode = WGPUCullMode.None },
                depthStencil = (IntPtr)(&ds),
                multisample = new WGPUMultisampleState { count = 1, mask = 0xFFFFFFFF },
                fragment = &frag,
            };
            p = CreatePipeline(&desc);
            _depthBlitPipelines[format] = p;
            return p;
        }

        private void ReleaseDepthProxies()
        {
            foreach (var p in _depthProxies.Values)
            {
                wgpuTextureViewRelease(p.View);
                wgpuTextureViewRelease(p.SampleView);
                wgpuTextureRelease(p.Texture);
            }
            _depthProxies.Clear();
            _proxyPass = null;
        }
    }
}
