// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The fullscreen view moved to where the eyes are when the headset shows it, by its depth. A drawing is shown a
// frame or two after the eyes it was drawn for were located, and the runtime moves a projection layer to where
// the eyes are by their turn only, as if all of it were far off: a model near by (on a table, walked round)
// was off by the head's move in between and jumped back as the next drawing came. Its depth buffer (the one it
// was drawn with, D3DTexture2D.DepthWith, copied with it as it is published) takes each texel back to where it
// is in the room, from the eye it was drawn for; a grid over each eye's half is drawn from there for the eye as
// it is for the frame the headset builds, whose views the XR host then submits. Each headset frame a drawing,
// moved: the 72 a second the headset shows of the 44 the app draws.
//
// The texels with nothing drawn (the room shows through) have the far depth; a vertex takes the nearest depth
// of the cells it is a corner of, so that a model's edge moves with the model, not stretched to the far plane
// behind it. Of every texel of them: each cell's nearest is found once a drawing (the cells' own texture) -- nine
// samples round a vertex missed an edge or a railing between them, and the edges wobbled as they crossed the grid.
//

using System;
using System.Text;
using Microsoft.Wpf.Interop.WebGpu.Direct3D;
using static Microsoft.Wpf.Interop.WebGpu.Wgpu;

namespace Microsoft.Wpf.Interop.WebGpu.Composition
{
    internal static unsafe partial class XrExternalPanel
    {
        private static IntPtr s_depth, s_depthView;         // the latest image's depth (R32Float, 0 near .. 1 far)
        private static int s_depthWidth, s_depthHeight;
        private static bool s_latestHasDepth;
        private static IntPtr s_copyModule, s_copyPipeline, s_copyPipelineMs;
        private static IntPtr s_cells, s_cellsView, s_cellsPipeline, s_cellsLayout;   // each cell's nearest depth
        private static int s_cellsWidth, s_cellsHeight;
        private static IntPtr s_moveModule, s_moveLayout, s_movePipeline, s_moveSampler, s_moveUniforms;
        private static WGPUTextureFormat s_movePipelineFormat;
        private static IntPtr s_moveDepth, s_moveDepthView;   // the moved mesh folds over itself: the nearest kept
        private static int s_moveDepthWidth, s_moveDepthHeight;

        // texels a grid cell (each way): the moved picture's mesh
        private const int Cell = 8;

        /// <summary>The sink's present, under <see cref="SyncRoot"/>: the depth the image of this size was drawn with
        /// (<paramref name="depth"/>, null for none), copied for the image about to be published.</summary>
        public static void CopyDepth(WgpuContext ctx, D3DTexture2D? depth, int width, int height)
        {
            s_latestHasDepth = false;
            if (depth?.Gpu is not { } source || source == IntPtr.Zero || depth.Desc.Width != width || depth.Desc.Height != height) return;
            if ((depth.GpuUsage & WGPUTextureUsage.TextureBinding) == 0) return;
            CopyDepth(ctx, source, depth.Format, depth.SampleCount, width, height);
        }

        /// <summary>The same for a depth texture of the compositor's own (a WPF Viewport3D's, WgpuSceneRenderer.Headset.cs):
        /// <paramref name="format"/> and <paramref name="samples"/> its own, sampleable.</summary>
        public static void CopyDepth(WgpuContext ctx, IntPtr source, WGPUTextureFormat format, uint samples, int width, int height)
        {
            s_latestHasDepth = false;
            if (source == IntPtr.Zero || s_targetWindow == IntPtr.Zero || width != s_targetWidth || height != s_targetHeight) return;
            if (s_depth == IntPtr.Zero || s_depthWidth != width || s_depthHeight != height)
            {
                if (s_depthView != IntPtr.Zero) wgpuTextureViewRelease(s_depthView);
                if (s_depth != IntPtr.Zero) wgpuTextureRelease(s_depth);
                var td = new WGPUTextureDescriptor
                {
                    usage = WGPUTextureUsage.RenderAttachment | WGPUTextureUsage.TextureBinding, dimension = WGPUTextureDimension._2D,
                    size = new WGPUExtent3D { width = (uint)width, height = (uint)height, depthOrArrayLayers = 1 },
                    format = TexFmt.R32Float, mipLevelCount = 1, sampleCount = 1,
                };
                s_depth = wgpuDeviceCreateTexture(ctx.Device, &td);
                s_depthView = wgpuTextureCreateView(s_depth, IntPtr.Zero);
                s_depthWidth = width;
                s_depthHeight = height;
            }
            bool ms = samples > 1;
            IntPtr pipeline = CopyPipeline(ctx, ms);
            if (pipeline == IntPtr.Zero) return;
            IntPtr from = D3DView.CreateTextureView(source, format, WGPUTextureViewDimension._2D, 0, 1, 0, 1, WGPUTextureAspect.DepthOnly);
            IntPtr layout = wgpuRenderPipelineGetBindGroupLayout(pipeline, 0);
            var entry = new WGPUBindGroupEntry { binding = 0, textureView = from };
            var bgd = new WGPUBindGroupDescriptor { layout = layout, entryCount = 1, entries = &entry };
            IntPtr group = wgpuDeviceCreateBindGroup(ctx.Device, &bgd);
            IntPtr enc = wgpuDeviceCreateCommandEncoder(ctx.Device, IntPtr.Zero);
            var att = new WGPURenderPassColorAttachment { view = s_depthView, depthSlice = 0xFFFFFFFF, loadOp = WGPULoadOp.Clear, storeOp = WGPUStoreOp.Store, clearValue = new WGPUColor { r = 1 } };
            var rp = new WGPURenderPassDescriptor { colorAttachmentCount = 1, colorAttachments = &att };
            IntPtr pass = wgpuCommandEncoderBeginRenderPass(enc, &rp);
            wgpuRenderPassEncoderSetPipeline(pass, pipeline);
            wgpuRenderPassEncoderSetBindGroup(pass, 0, group, 0, null);
            wgpuRenderPassEncoderDraw(pass, 3, 1, 0, 0);
            wgpuRenderPassEncoderEnd(pass);
            wgpuRenderPassEncoderRelease(pass);
            Cells(ctx, enc, width, height);
            IntPtr cmd = wgpuCommandEncoderFinish(enc, IntPtr.Zero);
            wgpuCommandEncoderRelease(enc);
            wgpuQueueSubmit(ctx.Queue, 1, &cmd);
            wgpuCommandBufferRelease(cmd);
            wgpuBindGroupRelease(group);
            wgpuBindGroupLayoutRelease(layout);
            wgpuTextureViewRelease(from);
            s_latestHasDepth = true;
        }

        /// <summary>Each eye's cells (<see cref="Cell"/> texels each way, from the eye's left edge) and the nearest depth
        /// of each: a texel a cell, the left eye's cells then the right's.</summary>
        private static void Cells(WgpuContext ctx, IntPtr enc, int width, int height)
        {
            int across = (width / 2 + Cell - 1) / Cell, down = (height + Cell - 1) / Cell;
            if (s_cells == IntPtr.Zero || s_cellsWidth != 2 * across || s_cellsHeight != down)
            {
                if (s_cellsView != IntPtr.Zero) wgpuTextureViewRelease(s_cellsView);
                if (s_cells != IntPtr.Zero) wgpuTextureRelease(s_cells);
                var td = new WGPUTextureDescriptor
                {
                    usage = WGPUTextureUsage.RenderAttachment | WGPUTextureUsage.TextureBinding | WGPUTextureUsage.CopySrc, dimension = WGPUTextureDimension._2D,
                    size = new WGPUExtent3D { width = (uint)(2 * across), height = (uint)down, depthOrArrayLayers = 1 },
                    format = TexFmt.R32Float, mipLevelCount = 1, sampleCount = 1,
                };
                s_cells = wgpuDeviceCreateTexture(ctx.Device, &td);
                s_cellsView = wgpuTextureCreateView(s_cells, IntPtr.Zero);
                s_cellsWidth = 2 * across;
                s_cellsHeight = down;
            }
            if (s_cellsPipeline == IntPtr.Zero)
            {
                var e = new WGPUBindGroupLayoutEntry { binding = 0, visibility = WGPUShaderStage_Fragment, texture = new WGPUTextureBindingLayout { sampleType = WGPUTextureSampleType.UnfilterableFloat, viewDimension = WGPUTextureViewDimension._2D } };
                var ld = new WGPUBindGroupLayoutDescriptor { entryCount = 1, entries = &e };
                s_cellsLayout = wgpuDeviceCreateBindGroupLayout(ctx.Device, &ld);
                IntPtr l = s_cellsLayout;
                var pld = new WGPUPipelineLayoutDescriptor { bindGroupLayoutCount = 1, bindGroupLayouts = &l };
                s_cellsPipeline = CreatePipeline(ctx, Module(ctx, CopyWgsl + CellsFs.Replace("CELL", Cell.ToString())), wgpuDeviceCreatePipelineLayout(ctx.Device, &pld), TexFmt.R32Float);
            }
            var entry = new WGPUBindGroupEntry { binding = 0, textureView = s_depthView };
            var bgd = new WGPUBindGroupDescriptor { layout = s_cellsLayout, entryCount = 1, entries = &entry };
            IntPtr group = wgpuDeviceCreateBindGroup(ctx.Device, &bgd);
            var att = new WGPURenderPassColorAttachment { view = s_cellsView, depthSlice = 0xFFFFFFFF, loadOp = WGPULoadOp.Clear, storeOp = WGPUStoreOp.Store, clearValue = new WGPUColor { r = 1 } };
            var rp = new WGPURenderPassDescriptor { colorAttachmentCount = 1, colorAttachments = &att };
            IntPtr pass = wgpuCommandEncoderBeginRenderPass(enc, &rp);
            wgpuRenderPassEncoderSetPipeline(pass, s_cellsPipeline);
            wgpuRenderPassEncoderSetBindGroup(pass, 0, group, 0, null);
            wgpuRenderPassEncoderDraw(pass, 3, 1, 0, 0);
            wgpuRenderPassEncoderEnd(pass);
            wgpuRenderPassEncoderRelease(pass);
            wgpuBindGroupRelease(group);
        }

        private const string CellsFs = @"
@group(0) @binding(0) var src: texture_2d<f32>;
@fragment fn fs(@builtin(position) pos: vec4<f32>) -> @location(0) vec4<f32> {
    let dims = vec2<i32>(textureDimensions(src));
    let half = dims.x / 2;
    let across = (half + CELL - 1) / CELL;
    let cell = vec2<i32>(pos.xy);
    let eye = cell.x / across;
    let x0 = eye * half + (cell.x - eye * across) * CELL;
    let x1 = min(x0 + CELL, (eye + 1) * half);
    let y0 = cell.y * CELL;
    let y1 = min(y0 + CELL, dims.y);
    var d = 1.0;
    for (var y = y0; y < y1; y++) { for (var x = x0; x < x1; x++) { d = min(d, textureLoad(src, vec2<i32>(x, y), 0).r); } }
    return vec4<f32>(d, 0.0, 0.0, 1.0);
}
";

        private const string CopyWgsl = @"
@vertex fn vs(@builtin(vertex_index) i: u32) -> @builtin(position) vec4<f32> {
    let xy = vec2<f32>(f32((i << 1u) & 2u), f32(i & 2u));
    return vec4<f32>(xy * 2.0 - 1.0, 0.0, 1.0);
}
";
        private const string CopyFs = @"
@group(0) @binding(0) var src: texture_depth_2d;
@fragment fn fs(@builtin(position) pos: vec4<f32>) -> @location(0) vec4<f32> { return vec4<f32>(textureLoad(src, vec2<i32>(pos.xy), 0), 0.0, 0.0, 1.0); }
";
        private const string CopyFsMs = @"
@group(0) @binding(0) var src: texture_depth_multisampled_2d;
@fragment fn fs(@builtin(position) pos: vec4<f32>) -> @location(0) vec4<f32> { return vec4<f32>(textureLoad(src, vec2<i32>(pos.xy), 0), 0.0, 0.0, 1.0); }
";

        /// <summary>The reprojection's device objects let go of (a lost device's: made again on the next draw).</summary>
        private static void ReleaseReproject()
        {
            foreach (IntPtr t in new[] { s_depth, s_cells, s_moveDepth }) if (t != IntPtr.Zero) wgpuTextureRelease(t);
            foreach (IntPtr v in new[] { s_depthView, s_cellsView, s_moveDepthView }) if (v != IntPtr.Zero) wgpuTextureViewRelease(v);
            foreach (IntPtr p in new[] { s_copyPipeline, s_copyPipelineMs, s_cellsPipeline, s_movePipeline }) if (p != IntPtr.Zero) wgpuRenderPipelineRelease(p);
            foreach (IntPtr m in new[] { s_copyModule, s_moveModule }) if (m != IntPtr.Zero) wgpuShaderModuleRelease(m);
            foreach (IntPtr l in new[] { s_cellsLayout, s_moveLayout }) if (l != IntPtr.Zero) wgpuBindGroupLayoutRelease(l);
            if (s_moveSampler != IntPtr.Zero) wgpuSamplerRelease(s_moveSampler);
            if (s_moveUniforms != IntPtr.Zero) wgpuBufferRelease(s_moveUniforms);
            s_depth = s_depthView = s_cells = s_cellsView = s_moveDepth = s_moveDepthView = IntPtr.Zero;
            s_copyModule = s_copyPipeline = s_copyPipelineMs = s_cellsPipeline = s_cellsLayout = IntPtr.Zero;
            s_moveModule = s_moveLayout = s_movePipeline = s_moveSampler = s_moveUniforms = IntPtr.Zero;
            s_depthWidth = s_depthHeight = s_cellsWidth = s_cellsHeight = s_moveDepthWidth = s_moveDepthHeight = 0;
        }

        private static IntPtr CopyPipeline(WgpuContext ctx, bool ms)
        {
            ref IntPtr cached = ref ms ? ref s_copyPipelineMs : ref s_copyPipeline;
            if (cached != IntPtr.Zero) return cached;
            IntPtr module = Module(ctx, CopyWgsl + (ms ? CopyFsMs : CopyFs));
            if (ms) s_copyModule = module;
            cached = CreatePipeline(ctx, module, IntPtr.Zero, TexFmt.R32Float);
            return cached;
        }

        /// <summary>The image drawn into the surface moved to where the eyes are now (<paramref name="views"/>: each
        /// eye's drawn position, its near plane in metres, its turn, its field of view's tangents (left, right, up,
        /// down), then the eye's position now, the far plane in metres, its turn and tangents now -- 24 floats an eye).</summary>
        private static void DrawMoved(WgpuContext ctx, IntPtr enc, IntPtr target, ExternalImage image, float[] views)
        {
            IntPtr pipeline = MovePipeline(ctx, s_format);
            if (pipeline == IntPtr.Zero) return;
            int eyeWidth = image.Width / 2, height = image.Height;
            int cols = (eyeWidth + Cell - 1) / Cell + 1, rows = (height + Cell - 1) / Cell + 1;
            float* u = stackalloc float[52];
            for (int i = 0; i < 48; i++) u[i] = views[i];
            u[48] = cols; u[49] = rows; u[50] = s_latestHasDepth ? 1f : 0f; u[51] = Cell;
            if (s_moveUniforms == IntPtr.Zero)
            {
                var bd = new WGPUBufferDescriptor { usage = WGPUBufferUsage.Uniform | WGPUBufferUsage.CopyDst, size = 52 * 4 };
                s_moveUniforms = wgpuDeviceCreateBuffer(ctx.Device, &bd);
            }
            wgpuQueueWriteBuffer(ctx.Queue, s_moveUniforms, 0, u, 52 * 4);
            var entries = stackalloc WGPUBindGroupEntry[4];
            entries[0] = new WGPUBindGroupEntry { binding = 0, buffer = s_moveUniforms, size = 52 * 4 };
            entries[1] = new WGPUBindGroupEntry { binding = 1, textureView = image.View };
            entries[2] = new WGPUBindGroupEntry { binding = 2, textureView = s_latestHasDepth ? s_cellsView : image.View };
            entries[3] = new WGPUBindGroupEntry { binding = 3, sampler = s_moveSampler };
            var bgd = new WGPUBindGroupDescriptor { layout = s_moveLayout, entryCount = 4, entries = entries };
            IntPtr group = wgpuDeviceCreateBindGroup(ctx.Device, &bgd);
            if (s_moveDepth == IntPtr.Zero || s_moveDepthWidth != image.Width || s_moveDepthHeight != height)
            {
                if (s_moveDepthView != IntPtr.Zero) wgpuTextureViewRelease(s_moveDepthView);
                if (s_moveDepth != IntPtr.Zero) wgpuTextureRelease(s_moveDepth);
                var td = new WGPUTextureDescriptor
                {
                    usage = WGPUTextureUsage.RenderAttachment, dimension = WGPUTextureDimension._2D,
                    size = new WGPUExtent3D { width = (uint)image.Width, height = (uint)height, depthOrArrayLayers = 1 },
                    format = TexFmt.Depth32Float, mipLevelCount = 1, sampleCount = 1,
                };
                s_moveDepth = wgpuDeviceCreateTexture(ctx.Device, &td);
                s_moveDepthView = wgpuTextureCreateView(s_moveDepth, IntPtr.Zero);
                s_moveDepthWidth = image.Width;
                s_moveDepthHeight = height;
            }
            var att = new WGPURenderPassColorAttachment { view = target, depthSlice = 0xFFFFFFFF, loadOp = WGPULoadOp.Clear, storeOp = WGPUStoreOp.Store };
            var datt = new WGPURenderPassDepthStencilAttachment { view = s_moveDepthView, depthLoadOp = WGPULoadOp.Clear, depthStoreOp = WGPUStoreOp.Discard, depthClearValue = 1f };
            var rp = new WGPURenderPassDescriptor { colorAttachmentCount = 1, colorAttachments = &att, depthStencilAttachment = (IntPtr)(&datt) };
            IntPtr pass = wgpuCommandEncoderBeginRenderPass(enc, &rp);
            wgpuRenderPassEncoderSetPipeline(pass, pipeline);
            wgpuRenderPassEncoderSetBindGroup(pass, 0, group, 0, null);
            uint vertices = (uint)((cols - 1) * (rows - 1) * 6);
            for (uint eye = 0; eye < 2; eye++)
            {
                // each eye's half of the surface, the eye's mesh clipped to it
                wgpuRenderPassEncoderSetViewport(pass, eye * eyeWidth, 0, eyeWidth, height, 0, 1);
                wgpuRenderPassEncoderDraw(pass, vertices, 1, 0, eye);
            }
            wgpuRenderPassEncoderEnd(pass);
            wgpuRenderPassEncoderRelease(pass);
            wgpuBindGroupRelease(group);
        }

        private const string MoveWgsl = @"
struct Eye { dpos: vec4<f32>, dturn: vec4<f32>, dtan: vec4<f32>, cpos: vec4<f32>, cturn: vec4<f32>, ctan: vec4<f32> };
struct P { eyes: array<Eye, 2>, grid: vec4<f32> };
@group(0) @binding(0) var<uniform> p: P;
@group(0) @binding(1) var color: texture_2d<f32>;
@group(0) @binding(2) var depth: texture_2d<f32>;
@group(0) @binding(3) var smp: sampler;
fn rot(q: vec4<f32>, v: vec3<f32>) -> vec3<f32> { let t = 2.0 * cross(q.xyz, v); return v + q.w * t + cross(q.xyz, t); }
struct V { @builtin(position) pos: vec4<f32>, @location(0) uv: vec2<f32> };
@vertex fn vs(@builtin(vertex_index) vi: u32, @builtin(instance_index) eye: u32) -> V {
    let cols = u32(p.grid.x);
    let rows = u32(p.grid.y);
    let cell = vi / 6u;
    let k = vi % 6u;
    var corner = array<vec2<u32>, 6>(vec2<u32>(0u, 0u), vec2<u32>(1u, 0u), vec2<u32>(0u, 1u), vec2<u32>(1u, 0u), vec2<u32>(1u, 1u), vec2<u32>(0u, 1u));
    let g = vec2<i32>(vec2<u32>(cell % (cols - 1u), cell / (cols - 1u)) + corner[k]);
    let dims = vec2<i32>(textureDimensions(color));
    let half = dims.x / 2;
    // a cell's corner: at its texels' edge, the eye's half from 0 to 1
    let size = i32(p.grid.w);
    let uv = vec2<f32>(min(g * size, vec2<i32>(half, dims.y))) / vec2<f32>(f32(half), f32(dims.y));
    // the nearest depth of the cells it is a corner of: a model's edge moves with it
    var d = 1.0;
    if (p.grid.z > 0.5) {
        let across = i32(cols) - 1;
        let downs = i32(rows) - 1;
        for (var j = -1; j <= 0; j++) {
            for (var i = -1; i <= 0; i++) {
                let q = clamp(g + vec2<i32>(i, j), vec2<i32>(0, 0), vec2<i32>(across - 1, downs - 1));
                d = min(d, textureLoad(depth, vec2<i32>(i32(eye) * across + q.x, q.y), 0).r);
            }
        }
    }
    let e = p.eyes[eye];
    let n = e.dpos.w;
    let f = e.cpos.w;
    // metres from the eye it was drawn for; nothing drawn there: far off, turned only
    var z = 1.0e4;
    if (d < 1.0) { z = n * f / (f - d * (f - n)); }
    let tx = mix(e.dtan.x, e.dtan.y, uv.x);
    let ty = mix(e.dtan.z, e.dtan.w, uv.y);
    let room = e.dpos.xyz + rot(e.dturn, vec3<f32>(tx * z, ty * z, -z));
    let local = rot(vec4<f32>(-e.cturn.xyz, e.cturn.w), room - e.cpos.xyz);
    var o: V;
    o.uv = vec2<f32>((uv.x + f32(eye)) * 0.5, uv.y);
    if (local.z > -1.0e-4) { o.pos = vec4<f32>(0.0, 0.0, 2.0, 1.0); return o; }    // behind the eye
    let w = -local.z;
    let ox = (local.x / w - e.ctan.x) / (e.ctan.y - e.ctan.x);
    let oy = (e.ctan.z - local.y / w) / (e.ctan.z - e.ctan.w);
    o.pos = vec4<f32>(ox * 2.0 - 1.0, 1.0 - oy * 2.0, w / (w + 1.0), 1.0);
    return o;
}
@fragment fn fs(v: V) -> @location(0) vec4<f32> {
    let c = textureSampleLevel(color, smp, v.uv, 0.0);
    return vec4<f32>(c.rgb * c.a, c.a);
}
";

        private static IntPtr MovePipeline(WgpuContext ctx, WGPUTextureFormat format)
        {
            if (s_movePipeline != IntPtr.Zero && s_movePipelineFormat == format) return s_movePipeline;
            if (s_moveModule == IntPtr.Zero)
            {
                s_moveModule = Module(ctx, MoveWgsl);
                var e = stackalloc WGPUBindGroupLayoutEntry[4];
                e[0] = new WGPUBindGroupLayoutEntry { binding = 0, visibility = WGPUShaderStage_Vertex, buffer = new WGPUBufferBindingLayout { type = WGPUBufferBindingType.Uniform, minBindingSize = 52 * 4 } };
                e[1] = new WGPUBindGroupLayoutEntry { binding = 1, visibility = WGPUShaderStage_Vertex | WGPUShaderStage_Fragment, texture = new WGPUTextureBindingLayout { sampleType = WGPUTextureSampleType.Float, viewDimension = WGPUTextureViewDimension._2D } };
                e[2] = new WGPUBindGroupLayoutEntry { binding = 2, visibility = WGPUShaderStage_Vertex, texture = new WGPUTextureBindingLayout { sampleType = WGPUTextureSampleType.UnfilterableFloat, viewDimension = WGPUTextureViewDimension._2D } };
                e[3] = new WGPUBindGroupLayoutEntry { binding = 3, visibility = WGPUShaderStage_Fragment, sampler = new WGPUSamplerBindingLayout { type = WGPUSamplerBindingType.Filtering } };
                var ld = new WGPUBindGroupLayoutDescriptor { entryCount = 4, entries = e };
                s_moveLayout = wgpuDeviceCreateBindGroupLayout(ctx.Device, &ld);
                var sd = new WGPUSamplerDescriptor
                {
                    addressModeU = WGPUAddressMode.ClampToEdge, addressModeV = WGPUAddressMode.ClampToEdge, addressModeW = WGPUAddressMode.ClampToEdge,
                    magFilter = WGPUFilterMode.Linear, minFilter = WGPUFilterMode.Linear, mipmapFilter = WGPUMipmapFilterMode.Nearest,
                    lodMinClamp = 0f, lodMaxClamp = 32f, compare = WGPUCompareFunction.Undefined, maxAnisotropy = 1,
                };
                s_moveSampler = wgpuDeviceCreateSampler(ctx.Device, &sd);
            }
            IntPtr layout = s_moveLayout;
            var pld = new WGPUPipelineLayoutDescriptor { bindGroupLayoutCount = 1, bindGroupLayouts = &layout };
            IntPtr pl = wgpuDeviceCreatePipelineLayout(ctx.Device, &pld);
            if (s_movePipeline != IntPtr.Zero) wgpuRenderPipelineRelease(s_movePipeline);
            s_movePipeline = CreatePipeline(ctx, s_moveModule, pl, format, depthTested: true);
            s_movePipelineFormat = format;
            return s_movePipeline;
        }

        private static IntPtr Module(WgpuContext ctx, string wgsl)
        {
            byte[] code = Encoding.UTF8.GetBytes(wgsl);
            fixed (byte* c = code)
            {
                var src = new WGPUShaderSourceWGSL { chain = new WGPUChainedStruct { next = null, sType = WGPUSType_ShaderSourceWGSL }, code = new WGPUStringView { data = c, length = (nuint)code.Length } };
                var md = new WGPUShaderModuleDescriptor { nextInChain = (WGPUChainedStruct*)&src };
                return wgpuDeviceCreateShaderModule(ctx.Device, &md);
            }
        }

        /// <summary>A pipeline of a module's vs and fs, drawing triangles into one target; <paramref name="layout"/>
        /// zero for the shader's own.</summary>
        private static IntPtr CreatePipeline(WgpuContext ctx, IntPtr module, IntPtr layout, WGPUTextureFormat format, bool depthTested = false)
        {
            var keep = new WGPUStencilFaceState { compare = WGPUCompareFunction.Always, failOp = WGPUStencilOperation.Keep, depthFailOp = WGPUStencilOperation.Keep, passOp = WGPUStencilOperation.Keep };
            var depthState = new WGPUDepthStencilState
            {
                format = TexFmt.Depth32Float, depthWriteEnabled = WGPUOptionalBool.True, depthCompare = WGPUCompareFunction.Less,
                stencilFront = keep, stencilBack = keep, stencilReadMask = 0xFFFFFFFF, stencilWriteMask = 0xFFFFFFFF,
            };
            byte[] vsName = Encoding.UTF8.GetBytes("vs"), fsName = Encoding.UTF8.GetBytes("fs");
            fixed (byte* vn = vsName)
            fixed (byte* fn = fsName)
            {
                var target = new WGPUColorTargetState { format = format, writeMask = 0xF };
                var frag = new WGPUFragmentState { module = module, entryPoint = new WGPUStringView { data = fn, length = 2 }, targetCount = 1, targets = &target };
                var desc = new WGPURenderPipelineDescriptor
                {
                    layout = layout,
                    vertex = new WGPUVertexState { module = module, entryPoint = new WGPUStringView { data = vn, length = 2 } },
                    primitive = new WGPUPrimitiveState { topology = WGPUPrimitiveTopology.TriangleList, frontFace = WGPUFrontFace.CCW, cullMode = WGPUCullMode.None },
                    multisample = new WGPUMultisampleState { count = 1, mask = 0xFFFFFFFF },
                    fragment = &frag,
                    depthStencil = depthTested ? (IntPtr)(&depthState) : IntPtr.Zero,
                };
                return wgpuDeviceCreateRenderPipeline(ctx.Device, &desc);
            }
        }
    }
}
