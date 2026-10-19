// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A D3DImage's frames (Documentation/d3d11-webgpu-shim.md, section 8): one texture per D3DImage resource
// (ExternalImage.cs), kept across frames of the same size and written in place.
//
// - Zero-copy (SendSurface): the back buffer is a Direct3D shim surface on THIS compositor's device -- the
//   first UI thread's compositor adopts the shim's device, D3DDevice.SharedGpu -- so the frame is a GPU
//   copy, on the one queue, after the app's Direct3D work: a texture-to-texture copy of an opaque
//   surface, or a pass that un-premultiplies (as the copy path does, to the integer) and then a copy.
//   No readback.
// - The copy path (SendBitmapRegion): the dirty rectangle's pixels, read by D3DImage, written into the
//   same texture with queue.writeTexture -- no new texture a frame, no BGRA->RGBA pass, no image cache.
//
// The texture is the back buffer's FRONT buffer, as milcore's CInteropDeviceBitmap keeps one: it is the
// back buffer's (D3DImage's key for it), not a resource's, so a D3DImage resource that leaves a channel
// and comes back (its visual re-rendered) still has the last frame, and its next dirty rectangle is
// relative to it.
//
// Lifetimes: the texture belongs to the sink until it is replaced (a resize) or no resource shows it at
// the end of a batch; then the renderer lets go of it after the frames in flight (RetireExternal, on the
// render thread). The app's surface is only read while D3DImage holds it (the call is made under its lock);
// what the GPU still has to read of it, wgpu keeps alive.
//

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Wpf.Interop.WebGpu.Direct3D;
using static Microsoft.Wpf.Interop.WebGpu.Wgpu;

namespace Microsoft.Wpf.Interop.WebGpu.Composition.Protocol
{
    internal sealed unsafe partial class WpfCompositionSink
    {
        /// <summary>A back buffer's front buffer, and the resources showing it.</summary>
        private sealed class Front
        {
            public ExternalImage Image = null!;
            public readonly HashSet<uint> Handles = new();
        }

        private readonly Dictionary<long, Front> _external = new();      // by back buffer key
        private readonly Dictionary<uint, long> _externalKey = new();    // a resource's back buffer
        private readonly List<long> _externalOrphans = new();            // no resource left, until the batch ends
        private readonly object _externalLock = new();

        /// <summary>Frames that took the zero-copy path, and frames written from pixels (tests).</summary>
        public int ZeroCopyPresents, CopyPresents;

        /// <summary>Whether this compositor uses the Direct3D shim's device (the first UI thread's does).</summary>
        public bool SharesShimDevice { get; private set; }

        private static int s_shimDeviceClaimed;
        private static readonly bool s_noShare = Environment.GetEnvironmentVariable("WPF_D3DIMAGE_SHARE") == "0";

        /// <summary>The device for this compositor: the shim's for the first that asks, a device of its own
        /// for every other (one GPU device per UI thread otherwise).</summary>
        private WgpuContext CreateOrShareContext()
        {
            if (!s_noShare && System.Threading.Interlocked.CompareExchange(ref s_shimDeviceClaimed, 1, 0) == 0)
            {
                SharesShimDevice = true;
                return D3DDevice.SharedGpu;
            }
            return WgpuContext.Create();
        }

        /// <summary>The back buffer <paramref name="key"/>'s front buffer at this size, made, kept or replaced
        /// (the old one retired), with the resource <paramref name="handle"/> among those showing it.</summary>
        /// <remarks><paramref name="fresh"/>: the texture is new, and holds nothing yet -- a dirty rectangle
        /// is relative to a previous frame it never had.</remarks>
        private Front? Target(uint handle, long key, int width, int height, out bool fresh)
        {
            fresh = false;
            var ctx = _ctx;
            var renderer = _renderer;
            if (ctx == null || renderer == null) return null;
            lock (_externalLock)
            {
                // the resource showed another back buffer before (SetBackBuffer): no longer
                if (_externalKey.TryGetValue(handle, out long was) && was != key) Leave(handle, was);
                _externalKey[handle] = key;
                if (!_external.TryGetValue(key, out var front)) _external[key] = front = new Front();
                front.Handles.Add(handle);
                var had = front.Image;
                if (had != null && had.Width == width && had.Height == height) return front;
                fresh = true;
                var format = renderer.SrgbOutput ? WGPUTextureFormat.BGRA8UnormSrgb : WGPUTextureFormat.BGRA8Unorm;
                var d = new WGPUTextureDescriptor
                {
                    usage = WGPUTextureUsage.TextureBinding | WGPUTextureUsage.CopyDst,
                    dimension = WGPUTextureDimension._2D,
                    size = new WGPUExtent3D { width = (uint)width, height = (uint)height, depthOrArrayLayers = 1 },
                    format = format, mipLevelCount = 1, sampleCount = 1,
                };
                IntPtr tex = wgpuDeviceCreateTexture(ctx.Device, &d);
                IntPtr view = wgpuTextureCreateView(tex, IntPtr.Zero);
                front.Image = new ExternalImage(tex, view, width, height, format);
                if (had != null) Retire(had);
                return front;
            }
        }

        /// <summary>The resource stops showing the back buffer <paramref name="key"/> (under the lock).</summary>
        private void Leave(uint handle, long key)
        {
            if (_external.TryGetValue(key, out var front) && front.Handles.Remove(handle) && front.Handles.Count == 0)
                _externalOrphans.Add(key);
        }

        private void Retire(ExternalImage image)
        {
            if (Recording) Record(() => _renderer?.RetireExternal(image));
            else _renderer?.RetireExternal(image);
        }

        /// <summary>The resource left the channel.</summary>
        private void ExternalReleased(uint handle)
        {
            lock (_externalLock)
            {
                if (_externalKey.Remove(handle, out long key)) Leave(handle, key);
            }
        }

        /// <summary>The batch ended: the front buffers no resource came back to are let go of. (A resource
        /// leaves and a new one for the same D3DImage arrives within one batch when its visual re-renders.)</summary>
        private void RetireOrphanedExternals()
        {
            List<ExternalImage>? gone = null;
            lock (_externalLock)
            {
                if (_externalOrphans.Count == 0) return;
                foreach (long key in _externalOrphans)
                    if (_external.TryGetValue(key, out var front) && front.Handles.Count == 0)
                    {
                        _external.Remove(key);
                        if (front.Image != null) (gone ??= new()).Add(front.Image);
                    }
                _externalOrphans.Clear();
            }
            if (gone != null) foreach (var image in gone) Retire(image);
        }

        /// <summary>The renderer is gone, and its textures with it.</summary>
        private void ForgetExternals()
        {
            lock (_externalLock) { _external.Clear(); _externalKey.Clear(); _externalOrphans.Clear(); }
        }

        /// <summary>A new frame in the front buffer: every resource showing it shows the new version.</summary>
        private void Publish(Front front)
        {
            var image = front.Image;
            image.Bump();
            uint[] handles;
            lock (_externalLock) { handles = new uint[front.Handles.Count]; front.Handles.CopyTo(handles); }
            foreach (uint handle in handles)
            {
                uint h = handle;
                if (Recording) Record(() => _engine.SetExternalBitmap(h, image));
                else _engine.SetExternalBitmap(h, image);
            }
        }

        /// <summary>
        /// A D3DImage's back buffer, when it is a Direct3D shim surface on this compositor's device: copied
        /// on the GPU into the resource's texture. False when it is not (another device, a real Direct3D 9
        /// surface, no GPU yet): the caller reads the pixels and sends them (<see cref="SendBitmapRegion"/>).
        /// </summary>
        public bool SendSurface(int channelId, uint handle, long key, IntPtr surface, int width, int height, bool premultiplied, int dx, int dy, int dw, int dh)
        {
            var ctx = _ctx;
            if (ctx == null || !SharesShimDevice || !ReferenceEquals(ctx, D3DDevice.SharedGpu)) return false;
            if (ComObject.From(surface) is not Direct3DSurface9 { OnCpuSide: false } s || !ReferenceEquals(s.Gpu!.Device.Gpu, ctx)) return false;
            if (s.Width != width || s.Height != height) return false;
            var front = Target(handle, key, width, height, out bool fresh);
            if (front == null) return false;
            var image = front.Image;
            if (fresh) { dx = 0; dy = 0; dw = width; dh = height; }        // all of it, the first time
            Clip(ref dx, ref dy, ref dw, ref dh, width, height);
            if (dw > 0 && dh > 0)
            {
                // the app's Direct3D work first, on the same queue
                var device = s.Gpu.Device;
                lock (device.Sync) device.Immediate.FlushToGpu(wait: false);
                try { CopyFromShim(ctx, s, image, premultiplied, dx, dy, dw, dh); }
                catch (Exception e) { Log("zero-copy D3DImage present failed, falling back to the copy path: " + e.Message); return false; }
            }
            ZeroCopyPresents++;
            Publish(front);
            return true;
        }

        /// <summary>
        /// The copy path: straight BGRA pixels of the rectangle (dx, dy, dw, dh) of a D3DImage's back buffer
        /// (<paramref name="stride"/> bytes a row) into the resource's texture. False when there is no GPU yet
        /// (the caller sends the whole bitmap as any image's).
        /// </summary>
        public bool SendBitmapRegion(int channelId, uint handle, long key, int width, int height, byte[] pixels, int stride, int dx, int dy, int dw, int dh)
        {
            var ctx = _ctx;
            if (ctx == null || pixels == null || dw <= 0 || dh <= 0 || dx < 0 || dy < 0 || dx + dw > width || dy + dh > height) return false;
            if ((long)stride * (dh - 1) + dw * 4L > pixels.Length || stride < dw * 4) return false;
            var front = Target(handle, key, width, height, out bool fresh);
            if (front == null) return false;
            var image = front.Image;
            // a new texture needs the whole frame: the caller sends it (the texture is kept for it)
            if (fresh && (dx != 0 || dy != 0 || dw != width || dh != height)) return false;
            fixed (byte* p = pixels)
            {
                var dst = new WGPUTexelCopyTextureInfo { texture = image.Texture, origin = new WGPUOrigin3D { x = (uint)dx, y = (uint)dy }, aspect = WGPUTextureAspect.All };
                var layout = new WGPUTexelCopyBufferLayout { bytesPerRow = (uint)stride, rowsPerImage = (uint)dh };
                var size = new WGPUExtent3D { width = (uint)dw, height = (uint)dh, depthOrArrayLayers = 1 };
                wgpuQueueWriteTexture(ctx.Queue, &dst, p, (nuint)((long)stride * (dh - 1) + dw * 4L), &layout, &size);
            }
            CopyPresents++;
            Publish(front);
            return true;
        }

        private static void Clip(ref int x, ref int y, ref int w, ref int h, int width, int height)
        {
            if (w <= 0 || h <= 0) { x = 0; y = 0; w = width; h = height; return; }     // no dirty rectangle: all of it
            int r = Math.Min(width, x + w), b = Math.Min(height, y + h);
            x = Math.Max(0, x); y = Math.Max(0, y);
            w = r - x; h = b - y;
        }

        // ---- the GPU copy -------------------------------------------------------------------------------

        private IntPtr _unpremultiply, _opaque, _unpremultiplyLayout;
        private IntPtr _scratch;
        private int _scratchW, _scratchH;

        private const string UnpremultiplyWgsl = @"
@group(0) @binding(0) var src: texture_2d<f32>;
@vertex fn vs(@builtin(vertex_index) i: u32) -> @builtin(position) vec4<f32> {
    return vec4<f32>(f32((i << 1u) & 2u) * 2.0 - 1.0, f32(i & 2u) * 2.0 - 1.0, 0.0, 1.0);
}
// D3DImage's content is premultiplied, the compositor's bitmaps straight: as the copy path's integer
// arithmetic (ManagedInteropBitmap.ReadPixels) does it
@fragment fn fs(@builtin(position) pos: vec4<f32>) -> @location(0) vec4<f32> {
    var c = vec4<u32>(round(textureLoad(src, vec2<i32>(pos.xy), 0) * 255.0));
    if (c.a == 0u) { c = vec4<u32>(0u); }
    else if (c.a != 255u) { c = vec4<u32>(min(vec3<u32>(255u), c.rgb * 255u / c.a), c.a); }
    return vec4<f32>(c) / 255.0;
}
// X8R8G8B8: its fourth byte is no alpha
@fragment fn fs_opaque(@builtin(position) pos: vec4<f32>) -> @location(0) vec4<f32> {
    return vec4<f32>(textureLoad(src, vec2<i32>(pos.xy), 0).rgb, 1.0);
}
";

        private void CopyFromShim(WgpuContext ctx, Direct3DSurface9 s, ExternalImage image, bool premultiplied, int dx, int dy, int dw, int dh)
        {
            D3DTexture2D src = s.Gpu!;
            IntPtr enc = wgpuDeviceCreateCommandEncoder(ctx.Device, IntPtr.Zero);
            var size = new WGPUExtent3D { width = (uint)dw, height = (uint)dh, depthOrArrayLayers = 1 };
            var to = new WGPUTexelCopyTextureInfo { texture = image.Texture, origin = new WGPUOrigin3D { x = (uint)dx, y = (uint)dy }, aspect = WGPUTextureAspect.All };
            IntPtr srcView = IntPtr.Zero;
            {
                // un-premultiplied (or, X8R8G8B8, made opaque) into a plain BGRA scratch, then copied into
                // the (possibly sRGB) texture, which keeps the bytes
                IntPtr scratch = Scratch(ctx, image.Width, image.Height);
                IntPtr scratchView = wgpuTextureCreateView(scratch, IntPtr.Zero);
                srcView = D3DView.CreateTextureView(src.Gpu, src.Format, WGPUTextureViewDimension._2D, s.Level, 1, 0, 1);
                IntPtr pipeline = Unpremultiply(ctx, opaque: !premultiplied);
                var ge = new WGPUBindGroupEntry { binding = 0, textureView = srcView };
                var gd = new WGPUBindGroupDescriptor { layout = _unpremultiplyLayout, entryCount = 1, entries = &ge };
                IntPtr group = wgpuDeviceCreateBindGroup(ctx.Device, &gd);
                var att = new WGPURenderPassColorAttachment { view = scratchView, depthSlice = 0xFFFFFFFF, loadOp = WGPULoadOp.Load, storeOp = WGPUStoreOp.Store };
                var rp = new WGPURenderPassDescriptor { colorAttachmentCount = 1, colorAttachments = &att };
                IntPtr pass = wgpuCommandEncoderBeginRenderPass(enc, &rp);
                wgpuRenderPassEncoderSetPipeline(pass, pipeline);
                wgpuRenderPassEncoderSetBindGroup(pass, 0, group, 0, null);
                wgpuRenderPassEncoderSetScissorRect(pass, (uint)dx, (uint)dy, (uint)dw, (uint)dh);
                wgpuRenderPassEncoderDraw(pass, 3, 1, 0, 0);
                wgpuRenderPassEncoderEnd(pass);
                wgpuRenderPassEncoderRelease(pass);
                wgpuBindGroupRelease(group);
                wgpuTextureViewRelease(scratchView);
                var from = new WGPUTexelCopyTextureInfo { texture = scratch, origin = new WGPUOrigin3D { x = (uint)dx, y = (uint)dy }, aspect = WGPUTextureAspect.All };
                wgpuCommandEncoderCopyTextureToTexture(enc, &from, &to, &size);
            }
            IntPtr cmd = wgpuCommandEncoderFinish(enc, IntPtr.Zero);
            wgpuCommandEncoderRelease(enc);
            wgpuQueueSubmit(ctx.Queue, 1, &cmd);
            wgpuCommandBufferRelease(cmd);
            if (srcView != IntPtr.Zero) wgpuTextureViewRelease(srcView);
        }

        private IntPtr Scratch(WgpuContext ctx, int w, int h)
        {
            if (_scratch != IntPtr.Zero && _scratchW >= w && _scratchH >= h) return _scratch;
            if (_scratch != IntPtr.Zero) wgpuTextureRelease(_scratch);      // the encoder keeps a copy in flight alive
            var d = new WGPUTextureDescriptor
            {
                usage = WGPUTextureUsage.RenderAttachment | WGPUTextureUsage.CopySrc, dimension = WGPUTextureDimension._2D,
                size = new WGPUExtent3D { width = (uint)w, height = (uint)h, depthOrArrayLayers = 1 },
                format = WGPUTextureFormat.BGRA8Unorm, mipLevelCount = 1, sampleCount = 1,
            };
            _scratch = wgpuDeviceCreateTexture(ctx.Device, &d);
            _scratchW = w; _scratchH = h;
            return _scratch;
        }

        private IntPtr Unpremultiply(WgpuContext ctx, bool opaque)
        {
            if (_unpremultiply != IntPtr.Zero) return opaque ? _opaque : _unpremultiply;
            byte[] code = Encoding.UTF8.GetBytes(UnpremultiplyWgsl);
            IntPtr module;
            fixed (byte* c = code)
            {
                var srcDesc = new WGPUShaderSourceWGSL { chain = new WGPUChainedStruct { next = null, sType = WGPUSType_ShaderSourceWGSL }, code = new WGPUStringView { data = c, length = (nuint)code.Length } };
                var md = new WGPUShaderModuleDescriptor { nextInChain = (WGPUChainedStruct*)&srcDesc };
                module = wgpuDeviceCreateShaderModule(ctx.Device, &md);
            }
            var entry = new WGPUBindGroupLayoutEntry
            {
                binding = 0, visibility = WGPUShaderStage_Fragment,
                texture = new WGPUTextureBindingLayout { sampleType = WGPUTextureSampleType.UnfilterableFloat, viewDimension = WGPUTextureViewDimension._2D },
            };
            var ld = new WGPUBindGroupLayoutDescriptor { entryCount = 1, entries = &entry };
            _unpremultiplyLayout = wgpuDeviceCreateBindGroupLayout(ctx.Device, &ld);
            IntPtr layout = _unpremultiplyLayout;
            var pld = new WGPUPipelineLayoutDescriptor { bindGroupLayoutCount = 1, bindGroupLayouts = &layout };
            IntPtr pl = wgpuDeviceCreatePipelineLayout(ctx.Device, &pld);
            _opaque = PassPipeline(ctx, module, pl, "fs_opaque");
            _unpremultiply = PassPipeline(ctx, module, pl, "fs");
            return opaque ? _opaque : _unpremultiply;
        }

        private static IntPtr PassPipeline(WgpuContext ctx, IntPtr module, IntPtr pl, string entry)
        {
            byte[] vsName = Encoding.UTF8.GetBytes("vs"), fsName = Encoding.UTF8.GetBytes(entry);
            fixed (byte* vn = vsName)
            fixed (byte* fn = fsName)
            {
                var target = new WGPUColorTargetState { format = WGPUTextureFormat.BGRA8Unorm, writeMask = 0xF };
                var frag = new WGPUFragmentState { module = module, entryPoint = new WGPUStringView { data = fn, length = (nuint)fsName.Length }, targetCount = 1, targets = &target };
                var desc = new WGPURenderPipelineDescriptor
                {
                    layout = pl,
                    vertex = new WGPUVertexState { module = module, entryPoint = new WGPUStringView { data = vn, length = 2 } },
                    primitive = new WGPUPrimitiveState { topology = WGPUPrimitiveTopology.TriangleList, frontFace = WGPUFrontFace.CCW, cullMode = WGPUCullMode.None },
                    multisample = new WGPUMultisampleState { count = 1, mask = 0xFFFFFFFF },
                    fragment = &frag,
                };
                return wgpuDeviceCreateRenderPipeline(ctx.Device, &desc);
            }
        }
    }
}
