// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// WPF_WEBGPU_PROBE=1: storage-buffer reads a compute shader makes, checked against the CPU's answer and
// logged ("[probe] ..."), once the composition device exists. For a GPU that renders one of the stack's
// storage-buffer paths wrong (Adreno: the coverage rasterizer's bands, the geometry-shader emulation's
// lines): which way of reading a buffer it gets wrong.
//

using System;
using System.Text;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using static Microsoft.Wpf.Interop.WebGpu.Wgpu;
using static Microsoft.Wpf.Interop.WebGpu.Direct3D.WgpuCompute;

namespace Microsoft.Wpf.Interop.WebGpu.Direct3D
{
    internal static unsafe class StorageProbe
    {
        private static bool s_ran;

        public static void RunOnce(WgpuContext ctx)
        {
            if (s_ran || Environment.GetEnvironmentVariable("WPF_WEBGPU_PROBE") != "1") return;
            s_ran = true;
            try
            {
                const int N = 4096, Out = 1024;
                var data = new uint[N];
                for (int i = 0; i < N; i++) data[i] = (uint)(i * 7 + 3);
                data[N - 1] = 1000;   // the loop's bound, read from the buffer
                Check(ctx, "plain", "Out[i] = In[i];", data, 0, N, Out, i => data[i]);
                Check(ctx, "offset 4096", "Out[i] = In[i];", data, 4096, 1024, Out, i => data[1024 + i]);
                Check(ctx, "offset 256", "Out[i] = In[i];", data, 256, 2048, Out, i => data[64 + i]);
                Check(ctx, "stride 3", "Out[i] = In[i * 3u];", data, 0, N, Out, i => data[i * 3]);
                Check(ctx, "loop, bound from the buffer", "var s = 0u; let n = In[4095u]; for (var j = 0u; j < n; j = j + 1u) { s = s + In[(j + i) & 4095u]; } Out[i] = s;", data, 0, N, Out,
                    i => { uint s = 0; for (uint j = 0; j < 1000; j++) s += data[(j + (uint)i) & 4095]; return s; });
                Check(ctx, "arrayLength of an offset binding", "Out[i] = arrayLength(&In);", data, 4096, 1024, Out, i => 1024u);
                // the same reads from a fragment shader (the coverage rasterizer's stage) and a vertex shader
                // (where the geometry-shader emulation's output is read): pixel/vertex i writes its value
                const string Fs = "@vertex fn vs(@builtin(vertex_index) v: u32) -> @builtin(position) vec4<f32> {{ let p = array(vec2<f32>(-1.0, -1.0), vec2<f32>(3.0, -1.0), vec2<f32>(-1.0, 3.0)); return vec4<f32>(p[v], 0.0, 1.0); }}\n"
                    + "@fragment fn fs(@builtin(position) pos: vec4<f32>) -> @location(0) vec4<u32> {{ let i = u32(pos.y) * 64u + u32(pos.x); {0} return vec4<u32>(r, 0u, 0u, 0u); }}\n";
                Draw(ctx, "fragment plain", string.Format(Fs, "let r = In[i];"), 3, false, data, 0, N, i => data[i]);
                Draw(ctx, "fragment offset 4096", string.Format(Fs, "let r = In[i];"), 3, false, data, 4096, 1024, i => data[1024 + i]);
                Draw(ctx, "fragment loop, bound from the buffer", string.Format(Fs, "var r = 0u; let n = In[4095u]; for (var j = 0u; j < n; j = j + 1u) {{ r = r + In[(j + i) & 4095u]; }}"), 3, false, data, 0, N,
                    i => { uint t = 0; for (uint j = 0; j < 1000; j++) t += data[(j + (uint)i) & 4095]; return t; });
                const string Vs = "struct O {{ @builtin(position) p: vec4<f32>, @location(0) @interpolate(flat) r: u32 }}\n"
                    + "@vertex fn vs(@builtin(vertex_index) i: u32) -> O {{ var o: O; {0} o.r = r; o.p = vec4<f32>((f32(i % 64u) + 0.5) / 32.0 - 1.0, 1.0 - (f32(i / 64u) + 0.5) / 8.0, 0.0, 1.0); return o; }}\n"
                    + "@fragment fn fs(o: O) -> @location(0) vec4<u32> {{ return vec4<u32>(o.r, 0u, 0u, 0u); }}\n";
                Draw(ctx, "vertex plain", string.Format(Vs, "let r = In[i];"), 1024, true, data, 0, N, i => data[i]);
                Draw(ctx, "vertex offset 4096", string.Format(Vs, "let r = In[i];"), 1024, true, data, 4096, 1024, i => data[1024 + i]);
                Draw(ctx, "vertex stride 3", string.Format(Vs, "let r = In[i * 3u];"), 1024, true, data, 0, N, i => data[i * 3]);
                // a multisampled target resolved as the shim's ResolveSubresource does it (an empty pass that
                // loads it, with a resolve target), and two others to tell it from
                Resolve(ctx, "msaa resolve in an empty pass (the shim's)", 0);
                Resolve(ctx, "msaa resolve in the drawing pass", 1);
                Resolve(ctx, "msaa resolve in a pass with a degenerate draw", 2);
            }
            catch (Exception e) { Console.WriteLine("[probe] failed: " + e); }
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

        /// <summary>A 64x16 R32Uint target drawn by <paramref name="shader"/> (vs, fs; In at group 0 binding 0,
        /// visible to both stages), its 1024 texels checked against <paramref name="expect"/>.</summary>
        private static void Draw(WgpuContext ctx, string name, string shader, uint vertices, bool points, uint[] data, ulong offset, int words, Func<int, uint> expect)
        {
            const int W = 64, H = 16;
            IntPtr module = Module(ctx, "@group(0) @binding(0) var<storage, read> In: array<u32>;\n" + shader);
            var entry = new WGPUBindGroupLayoutEntry { binding = 0, visibility = WGPUShaderStage_Vertex | WGPUShaderStage_Fragment, buffer = new WGPUBufferBindingLayout { type = WGPUBufferBindingType.ReadOnlyStorage } };
            var ld = new WGPUBindGroupLayoutDescriptor { entryCount = 1, entries = &entry };
            IntPtr bgl = wgpuDeviceCreateBindGroupLayout(ctx.Device, &ld);
            var pld = new WGPUPipelineLayoutDescriptor { bindGroupLayoutCount = 1, bindGroupLayouts = &bgl };
            IntPtr pl = wgpuDeviceCreatePipelineLayout(ctx.Device, &pld);
            byte[] vsName = Encoding.UTF8.GetBytes("vs"), fsName = Encoding.UTF8.GetBytes("fs");
            IntPtr pipeline;
            fixed (byte* vn = vsName)
            fixed (byte* fn = fsName)
            {
                var target = new WGPUColorTargetState { format = TexFmt.R32Uint, writeMask = 0xF };
                var frag = new WGPUFragmentState { module = module, entryPoint = new WGPUStringView { data = fn, length = 2 }, targetCount = 1, targets = &target };
                var desc = new WGPURenderPipelineDescriptor
                {
                    layout = pl,
                    vertex = new WGPUVertexState { module = module, entryPoint = new WGPUStringView { data = vn, length = 2 } },
                    primitive = new WGPUPrimitiveState { topology = points ? WGPUPrimitiveTopology.PointList : WGPUPrimitiveTopology.TriangleList, frontFace = WGPUFrontFace.CCW, cullMode = WGPUCullMode.None },
                    multisample = new WGPUMultisampleState { count = 1, mask = 0xFFFFFFFF },
                    fragment = &frag,
                };
                pipeline = wgpuDeviceCreateRenderPipeline(ctx.Device, &desc);
            }
            var td = new WGPUTextureDescriptor
            {
                usage = WGPUTextureUsage.RenderAttachment | WGPUTextureUsage.CopySrc, dimension = WGPUTextureDimension._2D,
                size = new WGPUExtent3D { width = W, height = H, depthOrArrayLayers = 1 }, format = TexFmt.R32Uint, mipLevelCount = 1, sampleCount = 1,
            };
            IntPtr tex = wgpuDeviceCreateTexture(ctx.Device, &td);
            IntPtr view = wgpuTextureCreateView(tex, IntPtr.Zero);
            var bytes = new byte[data.Length * 4];
            Buffer.BlockCopy(data, 0, bytes, 0, bytes.Length);
            IntPtr input = ctx.CreateBufferMapped(bytes, WGPUBufferUsage.Storage);
            var be = new WGPUBindGroupEntry { binding = 0, buffer = input, offset = offset, size = (ulong)words * 4 };
            var bd = new WGPUBindGroupDescriptor { layout = bgl, entryCount = 1, entries = &be };
            IntPtr bg = wgpuDeviceCreateBindGroup(ctx.Device, &bd);
            const ulong rowBytes = W * 4, size = rowBytes * H;   // 256: already a copy's row alignment
            IntPtr read = ctx.CreateBuffer(size, WGPUBufferUsage.MapRead | WGPUBufferUsage.CopyDst);
            IntPtr enc = wgpuDeviceCreateCommandEncoder(ctx.Device, IntPtr.Zero);
            var att = new WGPURenderPassColorAttachment { view = view, depthSlice = 0xFFFFFFFF, loadOp = WGPULoadOp.Clear, storeOp = WGPUStoreOp.Store };
            var rp = new WGPURenderPassDescriptor { colorAttachmentCount = 1, colorAttachments = &att };
            IntPtr pass = wgpuCommandEncoderBeginRenderPass(enc, &rp);
            wgpuRenderPassEncoderSetPipeline(pass, pipeline);
            wgpuRenderPassEncoderSetBindGroup(pass, 0, bg, 0, null);
            wgpuRenderPassEncoderDraw(pass, vertices, 1, 0, 0);
            wgpuRenderPassEncoderEnd(pass);
            wgpuRenderPassEncoderRelease(pass);
            var from = new WGPUTexelCopyTextureInfo { texture = tex, aspect = WGPUTextureAspect.All };
            var to = new WGPUTexelCopyBufferInfo { buffer = read, layout = new WGPUTexelCopyBufferLayout { bytesPerRow = (uint)rowBytes, rowsPerImage = H } };
            var extent = new WGPUExtent3D { width = W, height = H, depthOrArrayLayers = 1 };
            wgpuCommandEncoderCopyTextureToBuffer(enc, &from, &to, &extent);
            IntPtr cmd = wgpuCommandEncoderFinish(enc, IntPtr.Zero);
            wgpuCommandEncoderRelease(enc);
            wgpuQueueSubmit(ctx.Queue, 1, &cmd);
            wgpuCommandBufferRelease(cmd);
            byte[] back = ctx.MapRead(read, size);
            var got = new uint[W * H];
            Buffer.BlockCopy(back, 0, got, 0, back.Length);
            int bad = 0, first = -1;
            for (int i = 0; i < W * H; i++)
                if (got[i] != expect(i)) { bad++; if (first < 0) first = i; }
            Console.WriteLine(bad == 0
                ? $"[probe] {name}: ok"
                : $"[probe] {name}: {bad} of {W * H} wrong, first at {first}: got {got[first]}, want {expect(first)}");
            wgpuBufferRelease(input); wgpuBufferRelease(read);
            wgpuBindGroupRelease(bg);
            wgpuTextureViewRelease(view); wgpuTextureRelease(tex);
        }

        /// <summary>A 512x512 4x target cleared blue with its upper-left half red, resolved (<paramref name="how"/>:
        /// 0 an empty load+resolve pass, 1 in the drawing pass, 2 a load+resolve pass with a degenerate draw);
        /// the resolved pixels away from the diagonal checked.</summary>
        private static void Resolve(WgpuContext ctx, string name, int how)
        {
            const int W = 512, H = 512;
            IntPtr module = Module(ctx,
                "@vertex fn vs(@builtin(vertex_index) v: u32) -> @builtin(position) vec4<f32> { let p = array(vec2<f32>(-1.0, 1.0), vec2<f32>(1.0, 1.0), vec2<f32>(-1.0, -1.0), vec2<f32>(5.0, 5.0)); return vec4<f32>(p[min(v, 3u)], 0.0, 1.0); }\n"
                + "@fragment fn fs() -> @location(0) vec4<f32> { return vec4<f32>(1.0, 0.0, 0.0, 1.0); }\n");
            var pld = new WGPUPipelineLayoutDescriptor { bindGroupLayoutCount = 0 };
            IntPtr pl = wgpuDeviceCreatePipelineLayout(ctx.Device, &pld);
            byte[] vsName = Encoding.UTF8.GetBytes("vs"), fsName = Encoding.UTF8.GetBytes("fs");
            IntPtr pipeline;
            fixed (byte* vn = vsName)
            fixed (byte* fn = fsName)
            {
                var target = new WGPUColorTargetState { format = WGPUTextureFormat.RGBA8Unorm, writeMask = 0xF };
                var frag = new WGPUFragmentState { module = module, entryPoint = new WGPUStringView { data = fn, length = 2 }, targetCount = 1, targets = &target };
                var desc = new WGPURenderPipelineDescriptor
                {
                    layout = pl,
                    vertex = new WGPUVertexState { module = module, entryPoint = new WGPUStringView { data = vn, length = 2 } },
                    primitive = new WGPUPrimitiveState { topology = WGPUPrimitiveTopology.TriangleList, frontFace = WGPUFrontFace.CCW, cullMode = WGPUCullMode.None },
                    multisample = new WGPUMultisampleState { count = 4, mask = 0xFFFFFFFF },
                    fragment = &frag,
                };
                pipeline = wgpuDeviceCreateRenderPipeline(ctx.Device, &desc);
            }
            IntPtr Tex(uint samples, WGPUTextureUsage usage)
            {
                var td = new WGPUTextureDescriptor
                {
                    usage = usage, dimension = WGPUTextureDimension._2D, size = new WGPUExtent3D { width = W, height = H, depthOrArrayLayers = 1 },
                    format = WGPUTextureFormat.RGBA8Unorm, mipLevelCount = 1, sampleCount = samples,
                };
                return wgpuDeviceCreateTexture(ctx.Device, &td);
            }
            IntPtr ms = Tex(4, WGPUTextureUsage.RenderAttachment), single = Tex(1, WGPUTextureUsage.RenderAttachment | WGPUTextureUsage.CopySrc);
            IntPtr msView = wgpuTextureCreateView(ms, IntPtr.Zero), singleView = wgpuTextureCreateView(single, IntPtr.Zero);
            const ulong rowBytes = W * 4, size = rowBytes * H;
            IntPtr read = ctx.CreateBuffer(size, WGPUBufferUsage.MapRead | WGPUBufferUsage.CopyDst);
            IntPtr enc = wgpuDeviceCreateCommandEncoder(ctx.Device, IntPtr.Zero);
            var draw = new WGPURenderPassColorAttachment
            {
                view = msView, depthSlice = 0xFFFFFFFF, resolveTarget = how == 1 ? singleView : IntPtr.Zero,
                loadOp = WGPULoadOp.Clear, storeOp = WGPUStoreOp.Store, clearValue = new WGPUColor { r = 0, g = 0, b = 1, a = 1 },
            };
            var rp = new WGPURenderPassDescriptor { colorAttachmentCount = 1, colorAttachments = &draw };
            IntPtr pass = wgpuCommandEncoderBeginRenderPass(enc, &rp);
            wgpuRenderPassEncoderSetPipeline(pass, pipeline);
            wgpuRenderPassEncoderDraw(pass, 3, 1, 0, 0);
            wgpuRenderPassEncoderEnd(pass);
            wgpuRenderPassEncoderRelease(pass);
            if (how != 1)
            {
                var res = new WGPURenderPassColorAttachment { view = msView, depthSlice = 0xFFFFFFFF, resolveTarget = singleView, loadOp = WGPULoadOp.Load, storeOp = WGPUStoreOp.Store };
                var rd = new WGPURenderPassDescriptor { colorAttachmentCount = 1, colorAttachments = &res };
                IntPtr p2 = wgpuCommandEncoderBeginRenderPass(enc, &rd);
                if (how == 2)
                {
                    wgpuRenderPassEncoderSetPipeline(p2, pipeline);
                    wgpuRenderPassEncoderDraw(p2, 3, 1, 3, 0);   // vertices 3, 4, 5: all at (5, 5), off the target
                }
                wgpuRenderPassEncoderEnd(p2);
                wgpuRenderPassEncoderRelease(p2);
            }
            var from = new WGPUTexelCopyTextureInfo { texture = single, aspect = WGPUTextureAspect.All };
            var to = new WGPUTexelCopyBufferInfo { buffer = read, layout = new WGPUTexelCopyBufferLayout { bytesPerRow = (uint)rowBytes, rowsPerImage = H } };
            var extent = new WGPUExtent3D { width = W, height = H, depthOrArrayLayers = 1 };
            wgpuCommandEncoderCopyTextureToBuffer(enc, &from, &to, &extent);
            IntPtr cmd = wgpuCommandEncoderFinish(enc, IntPtr.Zero);
            wgpuCommandEncoderRelease(enc);
            wgpuQueueSubmit(ctx.Queue, 1, &cmd);
            wgpuCommandBufferRelease(cmd);
            byte[] px = ctx.MapRead(read, size);
            int bad = 0, checkedPx = 0, firstX = -1, firstY = -1;
            uint firstGot = 0;
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    double nx = (x + 0.5) / (W / 2) - 1, ny = 1 - (y + 0.5) / (H / 2);
                    if (Math.Abs(ny - nx) < 0.02) continue;
                    bool red = ny > nx;
                    int i = (y * W + x) * 4;
                    bool ok = red ? px[i] > 240 && px[i + 2] < 15 : px[i] < 15 && px[i + 2] > 240;
                    checkedPx++;
                    if (!ok) { bad++; if (firstX < 0) { firstX = x; firstY = y; firstGot = BitConverter.ToUInt32(px, i); } }
                }
            Console.WriteLine(bad == 0
                ? $"[probe] {name}: ok"
                : $"[probe] {name}: {bad} of {checkedPx} wrong, first at ({firstX},{firstY}): got 0x{firstGot:x8}");
            wgpuBufferRelease(read);
            wgpuTextureViewRelease(msView); wgpuTextureViewRelease(singleView);
            wgpuTextureRelease(ms); wgpuTextureRelease(single);
        }

        private static void Check(WgpuContext ctx, string name, string body, uint[] data, ulong offset, int words, int outWords, Func<int, uint> expect)
        {
            string wgsl = $@"
@group(0) @binding(0) var<storage, read> In: array<u32>;
@group(0) @binding(1) var<storage, read_write> Out: array<u32>;
@compute @workgroup_size(64) fn main(@builtin(global_invocation_id) g: vec3<u32>) {{
    let i = g.x;
    if (i >= arrayLength(&Out)) {{ return; }}
    {body}
}}
";
            byte[] code = Encoding.UTF8.GetBytes(wgsl);
            IntPtr module;
            fixed (byte* c = code)
            {
                var src = new WGPUShaderSourceWGSL { chain = new WGPUChainedStruct { next = null, sType = WGPUSType_ShaderSourceWGSL }, code = new WGPUStringView { data = c, length = (nuint)code.Length } };
                var md = new WGPUShaderModuleDescriptor { nextInChain = (WGPUChainedStruct*)&src };
                module = wgpuDeviceCreateShaderModule(ctx.Device, &md);
            }
            var entries = stackalloc WGPUBindGroupLayoutEntry[2];
            entries[0] = new WGPUBindGroupLayoutEntry { binding = 0, visibility = WGPUShaderStage_Compute, buffer = new WGPUBufferBindingLayout { type = WGPUBufferBindingType.ReadOnlyStorage } };
            entries[1] = new WGPUBindGroupLayoutEntry { binding = 1, visibility = WGPUShaderStage_Compute, buffer = new WGPUBufferBindingLayout { type = WGPUBufferBindingType.Storage } };
            var ld = new WGPUBindGroupLayoutDescriptor { entryCount = 2, entries = entries };
            IntPtr bgl = wgpuDeviceCreateBindGroupLayout(ctx.Device, &ld);
            var pld = new WGPUPipelineLayoutDescriptor { bindGroupLayoutCount = 1, bindGroupLayouts = &bgl };
            IntPtr pl = wgpuDeviceCreatePipelineLayout(ctx.Device, &pld);
            byte[] entry = Encoding.UTF8.GetBytes("main");
            IntPtr pipeline;
            fixed (byte* m = entry)
            {
                var cd = new WGPUComputePipelineDescriptor { layout = pl, compute = new WGPUComputeState { module = module, entryPoint = new WGPUStringView { data = m, length = 4 } } };
                pipeline = wgpuDeviceCreateComputePipeline(ctx.Device, &cd);
            }
            var bytes = new byte[data.Length * 4];
            Buffer.BlockCopy(data, 0, bytes, 0, bytes.Length);
            IntPtr input = ctx.CreateBufferMapped(bytes, WGPUBufferUsage.Storage);
            ulong outBytes = (ulong)outWords * 4;
            IntPtr output = ctx.CreateBuffer(outBytes, WGPUBufferUsage.Storage | WGPUBufferUsage.CopySrc);
            IntPtr read = ctx.CreateBuffer(outBytes, WGPUBufferUsage.MapRead | WGPUBufferUsage.CopyDst);
            var be = stackalloc WGPUBindGroupEntry[2];
            be[0] = new WGPUBindGroupEntry { binding = 0, buffer = input, offset = offset, size = (ulong)words * 4 };
            be[1] = new WGPUBindGroupEntry { binding = 1, buffer = output, size = outBytes };
            var bd = new WGPUBindGroupDescriptor { layout = bgl, entryCount = 2, entries = be };
            IntPtr bg = wgpuDeviceCreateBindGroup(ctx.Device, &bd);
            IntPtr enc = wgpuDeviceCreateCommandEncoder(ctx.Device, IntPtr.Zero);
            IntPtr pass = wgpuCommandEncoderBeginComputePass(enc, null);
            wgpuComputePassEncoderSetPipeline(pass, pipeline);
            wgpuComputePassEncoderSetBindGroup(pass, 0, bg, 0, null);
            wgpuComputePassEncoderDispatchWorkgroups(pass, (uint)(outWords + 63) / 64, 1, 1);
            wgpuComputePassEncoderEnd(pass);
            wgpuComputePassEncoderRelease(pass);
            wgpuCommandEncoderCopyBufferToBuffer(enc, output, 0, read, 0, outBytes);
            IntPtr cmd = wgpuCommandEncoderFinish(enc, IntPtr.Zero);
            wgpuCommandEncoderRelease(enc);
            wgpuQueueSubmit(ctx.Queue, 1, &cmd);
            wgpuCommandBufferRelease(cmd);
            byte[] back = ctx.MapRead(read, outBytes);
            var got = new uint[outWords];
            Buffer.BlockCopy(back, 0, got, 0, back.Length);
            int bad = 0, first = -1;
            for (int i = 0; i < outWords; i++)
                if (got[i] != expect(i)) { bad++; if (first < 0) first = i; }
            Console.WriteLine(bad == 0
                ? $"[probe] {name}: ok"
                : $"[probe] {name}: {bad} of {outWords} wrong, first at {first}: got {got[first]}, want {expect(first)}");
            foreach (IntPtr b in new[] { input, output, read }) wgpuBufferRelease(b);
            wgpuBindGroupRelease(bg);
        }
    }
}
