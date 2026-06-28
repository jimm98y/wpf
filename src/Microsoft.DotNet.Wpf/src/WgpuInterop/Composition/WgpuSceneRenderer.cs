// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Renders a SceneVisual tree to RGBA8 using real WebGPU draw calls -- the core
// of the milcore replacement. It walks the composition tree, bakes each visual's
// world transform and accumulated opacity into vertices, tessellates fills and
// applies clips as scissor rectangles, then submits a premultiplied-alpha pass.
//
// Brushes:
//   * SolidColorBrush  -> per-vertex premultiplied colour (fs_solid).
//   * LinearGradientBrush -> a sampled colour ramp texture; per-vertex UV is the
//     position projected onto the gradient axis (fs_textured).
//   * ImageBrush -> the image uploaded as a texture; per-vertex UV maps the
//     fill's local bounds to [0,1] (fs_textured).
// Gradient/image fills bind a texture + sampler (bind group 0). This is the same
// texture-sampling machinery the future glyph atlas and effects will use.
//
// Coordinate space: the root's local space is device pixels (origin top-left).
// Gradient/image coordinates are evaluated in each fill's local space (before the
// world transform), so brushes track their geometry under transforms.
//

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using static Microsoft.Wpf.Interop.WebGpu.Wgpu;

namespace Microsoft.Wpf.Interop.WebGpu.Composition
{
    internal sealed unsafe partial class WgpuSceneRenderer : IDisposable
    {
        private const int FloatsPerVertex = 8;            // pos.xy, color.rgba, uv.xy
        private const int VertexStride = FloatsPerVertex * sizeof(float);
        // Intermediate layers/masks store LINEAR values (WPF colours arrive as linear scRGB).
        private const WGPUTextureFormat ReadbackFormat = WGPUTextureFormat.RGBA8Unorm;
        // The final off-screen target is sRGB so the single linear->sRGB gamma encode happens
        // exactly once on the display write (matching the on-screen sRGB swap chain). Screenshots
        // and layered-popup bitmaps read back display-ready sRGB bytes.
        private const WGPUTextureFormat OffscreenFormat = WGPUTextureFormat.RGBA8UnormSrgb;

        /// <summary>Optional diagnostics hook (set by the sink) for one-off render tracing.</summary>
        internal static Action<string>? DebugLog;

        // True when the current frame targets an sRGB surface (on-screen / popups / screenshots):
        // colours and images are kept linear internally and gamma-encoded once on the final write.
        private bool _srgbOutput;

        // Per-frame perf counters (diagnostics): reset + read by the sink each frame.
        internal static int PerfTextures, PerfBindGroups, PerfCoverage, PerfReadbacks, PerfLayers, PerfLayerHits, PerfLayerMiss;
        internal static long PerfCollectTicks, PerfEncodeTicks, PerfSubmitTicks, PerfHashTicks;
        internal static void PerfReset() { PerfTextures = PerfBindGroups = PerfCoverage = PerfReadbacks = PerfLayers = PerfLayerHits = PerfLayerMiss = 0; PerfCollectTicks = PerfEncodeTicks = PerfSubmitTicks = PerfHashTicks = 0; }

        // Coverage-mask cache: text/solid shapes are rasterized to an R8 mask + uploaded as a
        // texture + bind group EVERY frame, which dominates cost for largely-static UI. Cache those
        // GPU resources keyed by the device-space geometry so unchanged content is reused (the colour
        // tint lives in the quad vertices, so the same glyph in any colour shares one cached mask).
        private sealed class CachedMask
        {
            public IntPtr Tex, View, BindGroup;
            public int Ox, Oy, W, H;
            public int LastFrame;
        }
        private readonly Dictionary<long, CachedMask> _maskCache = new();
        private int _frameId;

        // Static-layer cache: an effect/opacity card whose subtree is unchanged from a previous
        // frame reuses its already-rendered (+ blurred) textures and SKIPS its 3 render passes
        // (subtree + h/v blur). On the GL backend (where each pass is ~4ms) this is the biggest win
        // for largely-static UI. Keyed by a content hash of the subtree + region + effect params.
        private sealed class CachedLayer
        {
            public IntPtr SubTex, SubView;     // sharp content layer (owned)
            public IntPtr BlurTex, BlurView;   // blurred layer for blur/shadow (owned; Zero if none)
            public IntPtr MaskTex, MaskView;   // R8 mask for clip-geometry/opacity-mask (owned; Zero if none)
            public int Rx, Ry, Rw, Rh;
            public int Mode;                   // 0=opacity 1=blur 2=shadow 3=clip-geometry 4=opacity-mask
            public RgbaColor ShadowColor;
            public int OffX, OffY;
            public int LastFrame;
        }
        private readonly Dictionary<long, CachedLayer> _layerCache = new();


        /// <summary>Begin a logical composition frame (drives coverage-cache eviction).</summary>
        public void BeginFrame() => _frameId++;

        /// <summary>End the frame: return pooled layers and evict stale coverage-cache entries.</summary>
        public void EndFrame()
        {
            if (_layerCache.Count > 0)
            {
                List<long>? deadL = null;
                foreach (KeyValuePair<long, CachedLayer> kv in _layerCache)
                {
                    if (kv.Value.LastFrame >= _frameId - 2) continue;
                    (deadL ??= new List<long>()).Add(kv.Key);
                    CachedLayer c = kv.Value;
                    // Return the (same-size, reusable) RGBA layer textures to the pool; release the R8 mask.
                    ReturnLayerTexture(c.SubTex, c.SubView, c.Rw, c.Rh);
                    if (c.BlurTex != IntPtr.Zero) ReturnLayerTexture(c.BlurTex, c.BlurView, c.Rw, c.Rh);
                    if (c.MaskTex != IntPtr.Zero) { wgpuTextureViewRelease(c.MaskView); wgpuTextureRelease(c.MaskTex); }
                }
                if (deadL != null) foreach (long k in deadL) _layerCache.Remove(k);
            }

            if (_maskCache.Count == 0) return;
            List<long>? dead = null;
            foreach (KeyValuePair<long, CachedMask> kv in _maskCache)
            {
                if (kv.Value.LastFrame >= _frameId - 3) continue;
                (dead ??= new List<long>()).Add(kv.Key);
                CachedMask c = kv.Value;
                wgpuBindGroupRelease(c.BindGroup);
                wgpuTextureViewRelease(c.View);
                wgpuTextureRelease(c.Tex);
            }
            if (dead != null) foreach (long k in dead) _maskCache.Remove(k);
        }
        private const int GradientRampTexels = 256;

        private const string ShaderWgsl = @"
struct VSOut {
    @builtin(position) pos : vec4<f32>,
    @location(0) color : vec4<f32>,
    @location(1) uv : vec2<f32>,
};

@vertex
fn vs_main(@location(0) pos : vec2<f32>, @location(1) color : vec4<f32>, @location(2) uv : vec2<f32>) -> VSOut {
    var o : VSOut;
    o.pos = vec4<f32>(pos, 0.0, 1.0);
    o.color = color;
    o.uv = uv;
    return o;
}

@fragment
fn fs_solid(in : VSOut) -> @location(0) vec4<f32> {
    return in.color;          // already premultiplied
}

@group(0) @binding(0) var tex : texture_2d<f32>;
@group(0) @binding(1) var samp : sampler;

@fragment
fn fs_textured(in : VSOut) -> @location(0) vec4<f32> {
    let s = textureSample(tex, samp, in.uv);
    let a = s.a * in.color.a;          // image/ramp alpha * accumulated opacity
    return vec4<f32>(s.rgb * a, a);    // premultiply
}

@fragment
fn fs_text(in : VSOut) -> @location(0) vec4<f32> {
    let coverage = textureSample(tex, samp, in.uv).r;   // R8 glyph coverage
    let a = coverage * in.color.a;                      // coverage * brush alpha * opacity
    return vec4<f32>(in.color.rgb * a, a);              // premultiplied brush colour
}

@fragment
fn fs_layer(in : VSOut) -> @location(0) vec4<f32> {
    // The layer texture is already premultiplied; scale it by the group opacity.
    return textureSample(tex, samp, in.uv) * in.color.a;
}

// Separable Gaussian blur. The blur axis step (uv units), sigma and tap radius
// are carried in the (constant) vertex colour, so no uniform buffer is needed.
@fragment
fn fs_blur(in : VSOut) -> @location(0) vec4<f32> {
    let step = in.color.xy;
    let sigma = in.color.z;
    let radius = i32(in.color.w);
    var sum = vec4<f32>(0.0);
    var wsum = 0.0;
    for (var i = -radius; i <= radius; i = i + 1) {
        let w = exp(-f32(i * i) / (2.0 * sigma * sigma));
        sum = sum + textureSample(tex, samp, in.uv + step * f32(i)) * w;
        wsum = wsum + w;
    }
    return sum / wsum;
}

// Drop-shadow tint: use the (blurred) source alpha as coverage and paint it the
// shadow colour (in vertex colour), premultiplied by colour.a (shadow alpha).
@fragment
fn fs_shadow(in : VSOut) -> @location(0) vec4<f32> {
    let cov = textureSample(tex, samp, in.uv).a;
    let a = cov * in.color.a;
    return vec4<f32>(in.color.rgb * a, a);
}
";

        // fs_clip needs a second texture (the clip mask), so it uses its own
        // shader module with a 3-entry bind group {layer, mask, sampler}.
        private const string ClipShaderWgsl = @"
struct VSOut {
    @builtin(position) pos : vec4<f32>,
    @location(0) color : vec4<f32>,
    @location(1) uv : vec2<f32>,
};

@vertex
fn vs_main(@location(0) pos : vec2<f32>, @location(1) color : vec4<f32>, @location(2) uv : vec2<f32>) -> VSOut {
    var o : VSOut;
    o.pos = vec4<f32>(pos, 0.0, 1.0);
    o.color = color;
    o.uv = uv;
    return o;
}

@group(0) @binding(0) var layerTex : texture_2d<f32>;
@group(0) @binding(1) var maskTex : texture_2d<f32>;
@group(0) @binding(2) var clipSamp : sampler;

@fragment
fn fs_clip(in : VSOut) -> @location(0) vec4<f32> {
    let c = textureSample(layerTex, clipSamp, in.uv);   // premultiplied layer
    let m = textureSample(maskTex, clipSamp, in.uv).r;  // clip coverage
    return c * (m * in.color.a);                         // mask * group opacity
}
";

        private enum FillKind { Solid, Textured, Text, Layer, Blur, Shadow, Clip }

        private readonly WgpuContext _ctx;
        private readonly Dictionary<(WGPUTextureFormat, FillKind), IntPtr> _pipelines = new();
        private readonly Text.IFont _font;
        private readonly Text.ITextShaper _shaper;
        private readonly Text.GlyphAtlas _glyphAtlas = new();
        private readonly List<Text.ShapedGlyph> _shapeScratch = new();
        private IntPtr _shaderModule;
        private IntPtr _clipShaderModule;
        private IntPtr _linearSampler;
        private IntPtr _nearestSampler;

        // Cached glyph-atlas texture, rebuilt only when the atlas changes. The
        // per-pass bind group is created on demand (passes may differ in format).
        private IntPtr _atlasTexture, _atlasView;
        private bool _atlasValid;

        // Transient GPU objects created during the current render, released once
        // the frame is submitted (wgpu keeps them alive for in-flight work).
        private readonly List<Action> _frameReleases = new();

        /// <summary>Number of glyph-atlas texture uploads (caching diagnostic).</summary>
        public int AtlasUploads { get; private set; }

        public WgpuSceneRenderer(WgpuContext ctx, Text.IFont? font = null, Text.ITextShaper? shaper = null)
        {
            _ctx = ctx;
            _font = font ?? new Text.BuiltinBitmapFont();
            _shaper = shaper ?? new Text.SimpleTextShaper();
        }

        private void DeferRelease(Action release) => _frameReleases.Add(release);

        private void FlushFrameReleases()
        {
            foreach (Action release in _frameReleases) release();
            _frameReleases.Clear();
            // Return this frame's geometry buffers to the free pool (reused next frame). By the time
            // we get here the frame is submitted; the buffers are reused a frame later, by which point
            // the GL backend has drained them -> no per-frame CreateBuffer churn / upload stalls.
            foreach ((IntPtr Buf, ulong Cap, bool Idx) b in _inUseBufs) (b.Idx ? _freeIdx : _freeVtx).Add((b.Buf, b.Cap));
            _inUseBufs.Clear();
        }

        // Pooled vertex/index buffers (avoids per-pass-per-frame allocation, the GL stall source).
        private readonly List<(IntPtr Buf, ulong Cap)> _freeVtx = new();
        private readonly List<(IntPtr Buf, ulong Cap)> _freeIdx = new();
        private readonly List<(IntPtr Buf, ulong Cap, bool Idx)> _inUseBufs = new();

        private IntPtr RentBuffer(ulong size, bool index)
        {
            List<(IntPtr Buf, ulong Cap)> free = index ? _freeIdx : _freeVtx;
            for (int i = 0; i < free.Count; i++)
                if (free[i].Cap >= size)
                {
                    (IntPtr Buf, ulong Cap) hit = free[i];
                    free.RemoveAt(i);
                    _inUseBufs.Add((hit.Buf, hit.Cap, index));
                    return hit.Buf;
                }
            ulong cap = 256; while (cap < size) cap <<= 1;   // round up -> small bucket count
            IntPtr nb = _ctx.CreateBuffer(cap, (index ? WGPUBufferUsage.Index : WGPUBufferUsage.Vertex) | WGPUBufferUsage.CopyDst);
            _inUseBufs.Add((nb, cap, index));
            return nb;
        }

        private readonly struct Scissor
        {
            public readonly int X, Y, W, H;
            public Scissor(int x, int y, int w, int h) { X = x; Y = y; W = w; H = h; }
            public bool IsEmpty => W <= 0 || H <= 0;
        }

        private readonly struct DrawItem
        {
            public readonly uint FirstIndex, IndexCount;
            public readonly Scissor Clip;
            public readonly FillKind Kind;
            public readonly IntPtr BindGroup; // per-fill texture (Textured); ignored for Solid/Text
            public DrawItem(uint firstIndex, uint indexCount, Scissor clip, FillKind kind, IntPtr bindGroup)
            {
                FirstIndex = firstIndex; IndexCount = indexCount; Clip = clip; Kind = kind; BindGroup = bindGroup;
            }
        }

        private sealed class DrawData
        {
            public readonly List<float> Verts = new();
            public readonly List<uint> Indices = new();
            public readonly List<DrawItem> Draws = new();
            public bool HasText;
        }

        // A render pass: draw a DrawData into a target view. Offscreen opacity
        // layers clear to transparent; the final pass clears to the background.
        // A single mesh draw within a 3D pass (its own buffers + uniform bind group).
        private readonly struct Draw3D
        {
            public readonly IntPtr Vbuf, Ibuf, BindGroup;
            public readonly uint IndexCount;
            public Draw3D(IntPtr vbuf, IntPtr ibuf, IntPtr bindGroup, uint indexCount)
            {
                Vbuf = vbuf; Ibuf = ibuf; BindGroup = bindGroup; IndexCount = indexCount;
            }
        }

        private sealed class LayerPass
        {
            public readonly IntPtr TargetView;
            public readonly bool ClearTransparent;
            public readonly RgbaColor ClearColor;
            public readonly DrawData Data;
            public readonly WGPUTextureFormat Format;
            // When set, this is a 3D pass (depth-tested mesh draws) rather than 2D.
            public readonly List<Draw3D>? Models3D;
            public readonly IntPtr DepthView;
            // For an MSAA 3D pass: the multisampled colour target that resolves into TargetView.
            public readonly IntPtr MsaaColorView;
            // For a region-sized layer texture: its absolute device origin + size. Scissors (stored
            // in absolute coords) are rebased by this origin and clamped to the size at record time.
            public int OriginX, OriginY, TexW, TexH;

            public LayerPass(IntPtr targetView, bool clearTransparent, RgbaColor clearColor, DrawData data, WGPUTextureFormat format)
            {
                TargetView = targetView; ClearTransparent = clearTransparent; ClearColor = clearColor; Data = data; Format = format;
            }

            public LayerPass(IntPtr targetView, WGPUTextureFormat format, List<Draw3D> models3D, IntPtr depthView, IntPtr msaaColorView)
            {
                TargetView = targetView; Format = format; Models3D = models3D; DepthView = depthView; MsaaColorView = msaaColorView;
                ClearTransparent = true; ClearColor = default; Data = new DrawData();
            }
        }

        /// <summary>Render off-screen and read back RGBA8. <paramref name="srgbOutput"/> selects an
        /// sRGB target (display-ready, gamma-encoded once) for screenshots / layered popups; the
        /// default linear target is used by tests, which validate compositing independent of gamma.</summary>
        public byte[] RenderToRgba(SceneVisual root, int width, int height, RgbaColor background, bool srgbOutput = false)
        {
            try
            {
                PerfReadbacks++;
                WGPUTextureFormat outFormat = srgbOutput ? OffscreenFormat : ReadbackFormat;
                _srgbOutput = srgbOutput;
                var plan = new List<LayerPass>();
                var mainData = new DrawData();
                CollectVisual(root, Matrix3x2.Identity, 1.0, new Scissor(0, 0, width, height), mainData, plan, width, height, outFormat);

                IntPtr device = _ctx.Device;
                int bytesPerRow = AlignUp(width * 4, 256);
                ulong readbackSize = (ulong)bytesPerRow * (ulong)height;
                var texDesc = new WGPUTextureDescriptor
                {
                    usage = WGPUTextureUsage.RenderAttachment | WGPUTextureUsage.CopySrc,
                    dimension = WGPUTextureDimension._2D,
                    size = new WGPUExtent3D { width = (uint)width, height = (uint)height, depthOrArrayLayers = 1 },
                    format = outFormat,
                    mipLevelCount = 1,
                    sampleCount = 1,
                };
                IntPtr targetTex = wgpuDeviceCreateTexture(device, &texDesc);
                IntPtr targetView = wgpuTextureCreateView(targetTex, IntPtr.Zero);
                IntPtr readback = _ctx.CreateBuffer(readbackSize, WGPUBufferUsage.CopyDst | WGPUBufferUsage.MapRead);

                IntPtr atlasView = EnsureAtlasView(AnyText(mainData, plan));
                IntPtr encoder = wgpuDeviceCreateCommandEncoder(device, IntPtr.Zero);

                foreach (LayerPass lp in plan) ExecutePass(encoder, lp, atlasView);
                ExecutePass(encoder, new LayerPass(targetView, false, background, mainData, outFormat), atlasView);

                var copySrc = new WGPUTexelCopyTextureInfo { texture = targetTex, aspect = WGPUTextureAspect.All };
                var copyDst = new WGPUTexelCopyBufferInfo
                {
                    layout = new WGPUTexelCopyBufferLayout { offset = 0, bytesPerRow = (uint)bytesPerRow, rowsPerImage = (uint)height },
                    buffer = readback,
                };
                var copyExtent = new WGPUExtent3D { width = (uint)width, height = (uint)height, depthOrArrayLayers = 1 };
                wgpuCommandEncoderCopyTextureToBuffer(encoder, &copySrc, &copyDst, &copyExtent);

                IntPtr commandBuffer = wgpuCommandEncoderFinish(encoder, IntPtr.Zero);
                IntPtr* cmds = stackalloc IntPtr[1];
                cmds[0] = commandBuffer;
                wgpuQueueSubmit(_ctx.Queue, 1, cmds);

                byte[] padded = _ctx.MapRead(readback, readbackSize);
                var pixels = new byte[width * height * 4];
                for (int row = 0; row < height; row++)
                    Buffer.BlockCopy(padded, row * bytesPerRow, pixels, row * width * 4, width * 4);

                DeferRelease(() => wgpuCommandEncoderRelease(encoder));
                DeferRelease(() => wgpuCommandBufferRelease(commandBuffer));
                DeferRelease(() => wgpuBufferRelease(readback));
                DeferRelease(() => { wgpuTextureViewRelease(targetView); wgpuTextureRelease(targetTex); });
                return pixels;
            }
            finally
            {
                FlushFrameReleases();
            }
        }

        /// <summary>
        /// Renders the scene into an externally owned texture view (e.g. a
        /// swap-chain back buffer) using the given target format. No readback.
        /// </summary>
        public void RenderSceneToView(SceneVisual root, IntPtr view, WGPUTextureFormat format, int width, int height, RgbaColor background)
        {
            try
            {
                _srgbOutput = format is WGPUTextureFormat.RGBA8UnormSrgb or WGPUTextureFormat.BGRA8UnormSrgb;
                long c0 = System.Diagnostics.Stopwatch.GetTimestamp();
                var plan = new List<LayerPass>();
                var mainData = new DrawData();
                CollectVisual(root, Matrix3x2.Identity, 1.0, new Scissor(0, 0, width, height), mainData, plan, width, height, format);
                PerfCollectTicks += System.Diagnostics.Stopwatch.GetTimestamp() - c0;

                IntPtr atlasView = EnsureAtlasView(AnyText(mainData, plan));
                IntPtr encoder = wgpuDeviceCreateCommandEncoder(_ctx.Device, IntPtr.Zero);

                long e0 = System.Diagnostics.Stopwatch.GetTimestamp();
                foreach (LayerPass lp in plan) ExecutePass(encoder, lp, atlasView);
                ExecutePass(encoder, new LayerPass(view, false, background, mainData, format), atlasView);
                IntPtr commandBuffer = wgpuCommandEncoderFinish(encoder, IntPtr.Zero);
                PerfEncodeTicks += System.Diagnostics.Stopwatch.GetTimestamp() - e0;

                IntPtr* cmds = stackalloc IntPtr[1];
                cmds[0] = commandBuffer;
                long s0 = System.Diagnostics.Stopwatch.GetTimestamp();
                wgpuQueueSubmit(_ctx.Queue, 1, cmds);
                PerfSubmitTicks += System.Diagnostics.Stopwatch.GetTimestamp() - s0;

                DeferRelease(() => wgpuCommandEncoderRelease(encoder));
                DeferRelease(() => wgpuCommandBufferRelease(commandBuffer));
            }
            finally
            {
                FlushFrameReleases();
            }
        }

        // Records one pass (its own vertex/index buffers + an atlas bind group for
        // its format if it draws text).
        private void ExecutePass(IntPtr encoder, LayerPass lp, IntPtr atlasView)
        {
            if (lp.Models3D is { } models)
            {
                ExecutePass3D(encoder, lp.TargetView, lp.MsaaColorView, lp.DepthView, lp.Format, models);
                return;
            }

            BuildGeometryBuffers(lp.Data, out IntPtr vbuf, out IntPtr ibuf, out bool hasGeometry);
            RgbaColor clear = lp.ClearTransparent ? new RgbaColor(0, 0, 0, 0) : lp.ClearColor;
            IntPtr pass = BeginClearPass(encoder, lp.TargetView, clear);
            if (hasGeometry)
            {
                IntPtr atlasBindGroup = IntPtr.Zero;
                if (lp.Data.HasText && atlasView != IntPtr.Zero)
                {
                    atlasBindGroup = CreateSampledBindGroup(lp.Format, FillKind.Text, atlasView, NearestSampler());
                    IntPtr bg = atlasBindGroup;
                    DeferRelease(() => wgpuBindGroupRelease(bg));
                }
                RecordDraws(pass, lp.Format, vbuf, ibuf, lp.Data, atlasBindGroup, lp.OriginX, lp.OriginY, lp.TexW, lp.TexH);
            }
            wgpuRenderPassEncoderEnd(pass);
            IntPtr passLocal = pass;
            DeferRelease(() => wgpuRenderPassEncoderRelease(passLocal));
        }

        // ---- scene walk + opacity layering ----

        // Walks a visual. A visual whose opacity < 1 and that draws more than one
        // thing becomes an offscreen layer: its subtree is rendered at full
        // opacity into its own texture, which is then composited at the group
        // opacity. This makes overlapping children inside a translucent group
        // blend once (as a unit) instead of double-blending.
        private void CollectVisual(SceneVisual v, Matrix3x2 parentWorld, double inheritedOpacity, Scissor parentClip,
            DrawData outData, List<LayerPass> plan, int width, int height, WGPUTextureFormat outFormat)
        {
            Matrix3x2 world = v.LocalToParent * parentWorld;
            Scissor clip = parentClip;
            if (v.Clip.HasValue)
                clip = Intersect(parentClip, DeviceBounds(v.Clip.Value, world, width, height));

            double vOpacity = Math.Clamp(v.Opacity, 0.0, 1.0);
            bool needsLayer = v.Effect != null || v.ClipGeometry != null || v.OpacityMask != null || (vOpacity < 0.999 && CountDrawables(v) > 1);

            if (!needsLayer)
            {
                EmitSubtree(v, world, inheritedOpacity * vOpacity, clip, outData, plan, width, height, outFormat);
                return;
            }

            // All layer kinds are sized to just the region they touch (the big perf win): a card-sized
            // clip / opacity-mask / effect costs a card-sized offscreen + mask + composite instead of a
            // full-window (e.g. 3420x2005) one -- which on the immediate GL backend is the difference
            // between ~1ms and ~15-28ms of submit per changed card, plus a ~6.8M-pixel CPU mask raster.
            // Viewport3D still assumes full-target dimensions; and a NESTED clip/mask/3D layer keeps the
            // parent full-target (region-sizing a parent would leave those nested passes with
            // out-of-bounds scissors). The geometry-clip region is the clip path's device bounds; the
            // opacity-mask / effect regions are the content clip (+ blur spread / shadow offset).
            bool fullTarget = HasNestedFullTargetContent(v);
            // Effect/opacity-mask regions are derived from the subtree's actual content bounds, not the
            // inherited rectangular clip -- otherwise a small card with no tight clip inherits the whole
            // scroll viewport (e.g. 3420x1888) and its drop-shadow blurs ~6M pixels (and allocates a
            // ~6-27MB transient buffer -> Gen2 GC) every frame instead of ~card-sized.
            Scissor contentClip = fullTarget ? clip : Intersect(clip, ContentDeviceBounds(v, world, width, height));
            Scissor region =
                fullTarget ? new Scissor(0, 0, width, height) :
                v.ClipGeometry is { } cg ? Intersect(clip, GeoDeviceBounds(cg, world, width, height)) :
                v.OpacityMask != null ? contentClip :
                v.Effect is BlurEffect be ? EffectRegion(contentClip, be.Radius, 0, 0, width, height) :
                v.Effect is DropShadowEffect de ? EffectRegion(contentClip, de.BlurRadius, de.OffsetX, de.OffsetY, width, height) :
                contentClip;
            if (region.IsEmpty) return;
            int rx = region.X, ry = region.Y, rw = region.W, rh = region.H;
            float groupOpacity = (float)(inheritedOpacity * vOpacity);

            // ALL effect/opacity/clip/mask layers are cacheable: if this subtree (+ its clip/mask)
            // is byte-for-byte the same as a previous frame, reuse its rendered textures and SKIP
            // the render passes + the (full-target, CPU) mask rasterization entirely. This is the
            // dominant cost for static cards on the GL backend. Animated cards re-hash -> re-render.
            {
                long h0 = System.Diagnostics.Stopwatch.GetTimestamp();
                long key = LayerCacheKey(v, world, region);
                PerfHashTicks += System.Diagnostics.Stopwatch.GetTimestamp() - h0;
                if (!_layerCache.TryGetValue(key, out CachedLayer? cl))
                {
                    PerfLayerMiss++;
                    _layerCache[key] = cl = RenderLayerToCache(v, world, region, clip, plan, width, height);
                }
                else PerfLayerHits++;
                cl.LastFrame = _frameId;
                EmitCachedLayer(cl, groupOpacity, clip, outData, outFormat, width, height);
                return;
            }
        }

        // Render a cacheable region layer (subtree + optional blur) into OWNED textures.
        private CachedLayer RenderLayerToCache(SceneVisual v, Matrix3x2 world, Scissor region, Scissor clip, List<LayerPass> plan, int width, int height)
        {
            int rx = region.X, ry = region.Y, rw = region.W, rh = region.H;
            (IntPtr subTex, IntPtr subView) = CreateOwnedLayerTexture(rw, rh);
            var subData = new DrawData();
            float sOX = _devOX, sOY = _devOY;
            _devOX = rx; _devOY = ry;
            EmitSubtree(v, world, 1.0, clip, subData, plan, rw, rh, ReadbackFormat);
            _devOX = sOX; _devOY = sOY;
            plan.Add(new LayerPass(subView, true, default, subData, ReadbackFormat) { OriginX = rx, OriginY = ry, TexW = rw, TexH = rh });

            var cl = new CachedLayer { SubTex = subTex, SubView = subView, Rx = rx, Ry = ry, Rw = rw, Rh = rh };
            if (v.ClipGeometry is { } clipGeom)
            {
                // Region-sized geometry-clip mask (CPU-rasterized once into a card-sized buffer aligned
                // to the layer's device origin, then cached) -- not a full-window 6.8M-pixel raster.
                byte[] maskBytes = PathRasterizer.RasterizeInto(TransformGeometry(clipGeom, world), rw, rh, rx, ry);
                (cl.MaskTex, cl.MaskView) = CreateR8Texture(maskBytes, rw, rh);
                cl.Mode = 3;
            }
            else if (v.OpacityMask is { } opacityMask)
            {
                byte[] maskBytes = RasterizeOpacityMask(opacityMask, world, rw, rh, rx, ry);
                (cl.MaskTex, cl.MaskView) = CreateR8Texture(maskBytes, rw, rh);
                cl.Mode = 4;
            }
            else if (v.Effect is BlurEffect b)
            {
                (cl.BlurTex, cl.BlurView) = BlurLayer(subView, b.Radius, plan, region);
                cl.Mode = 1;
            }
            else if (v.Effect is DropShadowEffect ds)
            {
                (cl.BlurTex, cl.BlurView) = BlurLayer(subView, ds.BlurRadius, plan, region);
                cl.Mode = 2; cl.ShadowColor = ds.Color; cl.OffX = (int)ds.OffsetX; cl.OffY = (int)ds.OffsetY;
            }
            else
            {
                cl.Mode = 0;
            }
            return cl;
        }

        // Composite a cached region layer into the parent (no render passes needed on a hit).
        private void EmitCachedLayer(CachedLayer cl, float groupOpacity, Scissor clip, DrawData outData, WGPUTextureFormat outFormat, int width, int height)
        {
            var region = new Scissor(cl.Rx, cl.Ry, cl.Rw, cl.Rh);
            if (cl.Mode == 3 || cl.Mode == 4)
            {
                // Geometry-clip / opacity-mask: modulate the (region-sized) layer by the cached R8 mask.
                EmitClipQuad(outData, outFormat, cl.SubView, cl.MaskView, groupOpacity, cl.Rx, cl.Ry, cl.Rw, cl.Rh, clip, width, height);
            }
            else if (cl.Mode == 1)
            {
                EmitLayerQuad(outData, outFormat, FillKind.Layer, cl.BlurView, 1f, 1f, 1f, groupOpacity, cl.Rx, cl.Ry, cl.Rw, cl.Rh, region, width, height);
            }
            else if (cl.Mode == 2)
            {
                float sa = (float)Math.Clamp(cl.ShadowColor.A * groupOpacity, 0.0, 1.0);
                EmitLayerQuad(outData, outFormat, FillKind.Shadow, cl.BlurView, cl.ShadowColor.R, cl.ShadowColor.G, cl.ShadowColor.B, sa,
                    cl.Rx + cl.OffX, cl.Ry + cl.OffY, cl.Rw, cl.Rh, region, width, height);
                EmitLayerQuad(outData, outFormat, FillKind.Layer, cl.SubView, 1f, 1f, 1f, groupOpacity, cl.Rx, cl.Ry, cl.Rw, cl.Rh, clip, width, height);
            }
            else
            {
                EmitLayerQuad(outData, outFormat, FillKind.Layer, cl.SubView, 1f, 1f, 1f, groupOpacity, cl.Rx, cl.Ry, cl.Rw, cl.Rh, clip, width, height);
            }
        }

        // ---- region-layer content hash (FNV-1a over geometry/brushes/transforms) ----
        private long _hash;
        private void HV(long x) => _hash = (_hash ^ x) * 1099511628211L;
        private void HF(float f) => HV(BitConverter.SingleToInt32Bits(f));
        private void HR(Rect r) { HF(r.X); HF(r.Y); HF(r.Width); HF(r.Height); }

        private long LayerCacheKey(SceneVisual v, Matrix3x2 world, Scissor region)
        {
            _hash = unchecked((long)1469598103934665603UL);
            HV(region.X); HV(region.Y); HV(region.W); HV(region.H);
            // Clip-geometry / opacity-mask take precedence over effects (matches RenderLayerToCache).
            if (v.ClipGeometry is { } cg) { HV(103); HashGeo(cg); HF(world.M11); HF(world.M12); HF(world.M21); HF(world.M22); HF(world.M31); HF(world.M32); }
            else if (v.OpacityMask is { } om) { HV(104); HashBrush(om); HF(world.M11); HF(world.M12); HF(world.M21); HF(world.M22); HF(world.M31); HF(world.M32); }
            else switch (v.Effect)
            {
                case BlurEffect b: HV(101); HF((float)b.Radius); break;
                case DropShadowEffect d: HV(102); HF((float)d.BlurRadius); HF((float)d.OffsetX); HF((float)d.OffsetY); HF(d.Color.A); break;
                default: HV(100); break;
            }
            HashVisual(v, world);
            return _hash;
        }

        private void HashVisual(SceneVisual n, Matrix3x2 w)
        {
            HF(w.M11); HF(w.M12); HF(w.M21); HF(w.M22); HF(w.M31); HF(w.M32);
            HV(BitConverter.DoubleToInt64Bits(n.Opacity));
            if (n.Clip is { } c) HR(c); else HV(7);
            foreach (DrawingPrimitive p in n.Content) HashPrimitive(p);
            foreach (SceneVisual ch in n.Children) HashVisual(ch, ch.LocalToParent * w);
        }

        private void HashPrimitive(DrawingPrimitive p)
        {
            switch (p)
            {
                case GeometryFill f: HV(1); HashGeo(f.Geometry); HashBrush(f.Brush); break;
                case GeometryStroke s: HV(2); HashGeo(s.Geometry); HashBrush(s.Brush); HF((float)s.Style.Thickness); break;
                case GeometryDrawing d: HV(3); HashGeo(d.Geometry); break;
                case GlyphRunDraw g: HV(4); HV(g.Text.GetHashCode()); HF(g.Origin.X); HF(g.Origin.Y); HF(g.EmSize); break;
                case Viewport3DDraw v3:
                    HV(5);
                    HF(v3.Camera.Position.X); HF(v3.Camera.Position.Y); HF(v3.Camera.Position.Z);
                    HF(v3.Camera.LookDirection.X); HF(v3.Camera.LookDirection.Y); HF(v3.Camera.LookDirection.Z);
                    foreach (Model3D m in v3.Models)
                    {
                        Matrix4x4 t = m.Transform;
                        HF(t.M11); HF(t.M12); HF(t.M13); HF(t.M21); HF(t.M22); HF(t.M23);
                        HF(t.M31); HF(t.M32); HF(t.M33); HF(t.M41); HF(t.M42); HF(t.M43);
                        HF(m.DiffuseColor.R); HF(m.DiffuseColor.G); HF(m.DiffuseColor.B);
                    }
                    break;
                default: HV(9); break;
            }
        }

        private void HashGeo(Geometry g)
        {
            switch (g)
            {
                case RectangleGeometry r: HV(21); HR(r.Rect); break;
                case RoundedRectangleGeometry rr: HV(22); HR(rr.Rect); HF(rr.RadiusX); HF(rr.RadiusY); break;
                case EllipseGeometry e: HV(23); HF(e.Center.X); HF(e.Center.Y); HF(e.RadiusX); HF(e.RadiusY); break;
                case PathGeometry pg: HV(24); HV(HashGeometry(pg)); break;
                case CombinedGeometry cg: HV(25); HashGeo(cg.Geometry1); HashGeo(cg.Geometry2); break;
                default: HV(20); break;
            }
        }

        private void HashBrush(Brush b)
        {
            switch (b)
            {
                case SolidColorBrush s: HV(11); HF(s.Color.R); HF(s.Color.G); HF(s.Color.B); HF(s.Color.A); break;
                case LinearGradientBrush lg:
                    HV(12); HF(lg.Start.X); HF(lg.Start.Y); HF(lg.End.X); HF(lg.End.Y);
                    foreach (GradientStop st in lg.Stops) { HF(st.Offset); HF(st.Color.R); HF(st.Color.G); HF(st.Color.B); HF(st.Color.A); }
                    break;
                case RadialGradientBrush rg:
                    HV(13); HF(rg.Center.X); HF(rg.Center.Y); HF(rg.RadiusX); HF(rg.RadiusY);
                    foreach (GradientStop st in rg.Stops) { HF(st.Offset); HF(st.Color.R); HF(st.Color.G); HF(st.Color.B); HF(st.Color.A); }
                    break;
                case ImageBrush img:
                    HV(14); HV(img.PixelWidth); HV(img.PixelHeight); HF((float)img.TileWidth); HF((float)img.TileHeight);
                    byte[] px = img.PixelsRgba;
                    int step = Math.Max(4, (px.Length / 256) & ~3);
                    for (int i = 0; i + 3 < px.Length; i += step) HV(px[i] | (px[i + 1] << 8) | (px[i + 2] << 16) | ((long)px[i + 3] << 24));
                    break;
            }
        }

        // Two separable blur passes over the region-sized layer textures (input is region-sized).
        // The final (v) texture is OWNED by the caller (cached or defer-released); h is transient.
        private (IntPtr Tex, IntPtr View) BlurLayer(IntPtr input, double radius, List<LayerPass> plan, Scissor region)
        {
            float sigma = (float)Math.Max(0.5, radius);
            int taps = Math.Clamp((int)Math.Ceiling(radius * 3.0), 1, 48);
            int rw = region.W, rh = region.H;
            float sOX = _devOX, sOY = _devOY;

            var (_, hView) = CreateLayerTexture(rw, rh);
            var hData = new DrawData();
            _devOX = region.X; _devOY = region.Y;
            EmitBlurQuad(hData, input, 1f / rw, 0f, sigma, taps, region);
            _devOX = sOX; _devOY = sOY;
            plan.Add(new LayerPass(hView, true, default, hData, ReadbackFormat) { OriginX = region.X, OriginY = region.Y, TexW = rw, TexH = rh });

            (IntPtr vTex, IntPtr vView) = CreateOwnedLayerTexture(rw, rh);
            var vData = new DrawData();
            _devOX = region.X; _devOY = region.Y;
            EmitBlurQuad(vData, hView, 0f, 1f / rh, sigma, taps, region);
            _devOX = sOX; _devOY = sOY;
            plan.Add(new LayerPass(vView, true, default, vData, ReadbackFormat) { OriginX = region.X, OriginY = region.Y, TexW = rw, TexH = rh });

            return (vTex, vView);
        }

        // A layer texture NOT defer-released this frame (caller owns it: cached, or releases it).
        private (IntPtr Tex, IntPtr View) CreateOwnedLayerTexture(int width, int height) => RentLayerTexture(width, height);

        // Pooled layer textures (RGBA8, RenderAttachment|TextureBinding -- all layer textures share
        // ReadbackFormat). Creating + destroying GPU textures every frame for animated cards' offscreen
        // layers is very expensive on the (virtualized) GL backend; reuse same-size textures across
        // frames instead. Card layer regions are stable size (content bounds include the opaque card
        // background), so exact-size matching reuses well. Cleared on use, so stale contents are fine.
        private readonly List<(IntPtr Tex, IntPtr View, int W, int H)> _freeLayerTex = new();

        private (IntPtr Tex, IntPtr View) RentLayerTexture(int width, int height)
        {
            for (int i = 0; i < _freeLayerTex.Count; i++)
                if (_freeLayerTex[i].W == width && _freeLayerTex[i].H == height)
                {
                    (IntPtr Tex, IntPtr View, int W, int H) hit = _freeLayerTex[i];
                    _freeLayerTex.RemoveAt(i);
                    return (hit.Tex, hit.View);
                }
            PerfLayers++;
            var texDesc = new WGPUTextureDescriptor
            {
                usage = WGPUTextureUsage.RenderAttachment | WGPUTextureUsage.TextureBinding,
                dimension = WGPUTextureDimension._2D,
                size = new WGPUExtent3D { width = (uint)width, height = (uint)height, depthOrArrayLayers = 1 },
                format = ReadbackFormat,
                mipLevelCount = 1,
                sampleCount = 1,
            };
            IntPtr tex = wgpuDeviceCreateTexture(_ctx.Device, &texDesc);
            return (tex, wgpuTextureCreateView(tex, IntPtr.Zero));
        }

        private void ReturnLayerTexture(IntPtr tex, IntPtr view, int width, int height)
        {
            const int Cap = 64;   // bound the pool; release extras (e.g. after a window resize)
            if (tex == IntPtr.Zero || _freeLayerTex.Count >= Cap)
            {
                if (tex != IntPtr.Zero) { wgpuTextureViewRelease(view); wgpuTextureRelease(tex); }
                return;
            }
            _freeLayerTex.Add((tex, view, width, height));
        }

        // The region a layer effect actually touches: the content's clip plus the blur spread (and,
        // for a drop shadow, the offset). Blurring/compositing only these pixels instead of the whole
        // window is the difference between ~50k and ~7M shaded pixels per effect.
        private static Scissor EffectRegion(Scissor clip, double blurRadius, double offX, double offY, int width, int height)
        {
            int m = (int)Math.Ceiling(blurRadius * 3.0) + 2;
            int x0 = (int)Math.Min(clip.X, clip.X + offX) - m;
            int y0 = (int)Math.Min(clip.Y, clip.Y + offY) - m;
            int x1 = (int)Math.Max(clip.X + clip.W, clip.X + clip.W + offX) + m;
            int y1 = (int)Math.Max(clip.Y + clip.H, clip.Y + clip.H + offY) + m;
            return Intersect(new Scissor(0, 0, width, height), new Scissor(x0, y0, x1 - x0, y1 - y0));
        }

        private void EmitBlurQuad(DrawData data, IntPtr inputView, float stepX, float stepY, float sigma, int taps, Scissor region)
        {
            IntPtr bindGroup = CreateSampledBindGroup(ReadbackFormat, FillKind.Blur, inputView, LinearSampler());
            DeferRelease(() => wgpuBindGroupRelease(bindGroup));

            // A full-coverage quad over the region texture (ToNdc + _devOX map the region to NDC).
            // Blur params ride in the (constant) vertex colour: (stepX, stepY, sigma, taps).
            int rw = region.W, rh = region.H;
            float x0 = region.X, y0 = region.Y, x1 = region.X + rw, y1 = region.Y + rh;
            uint baseVertex = (uint)(data.Verts.Count / FloatsPerVertex);
            AddVertex(data.Verts, ToNdc(new Vector2(x0, y0), rw, rh), stepX, stepY, sigma, taps, 0f, 0f);
            AddVertex(data.Verts, ToNdc(new Vector2(x1, y0), rw, rh), stepX, stepY, sigma, taps, 1f, 0f);
            AddVertex(data.Verts, ToNdc(new Vector2(x1, y1), rw, rh), stepX, stepY, sigma, taps, 1f, 1f);
            AddVertex(data.Verts, ToNdc(new Vector2(x0, y1), rw, rh), stepX, stepY, sigma, taps, 0f, 1f);

            uint firstIndex = (uint)data.Indices.Count;
            foreach (uint li in new uint[] { 0, 1, 2, 0, 2, 3 })
                data.Indices.Add(baseVertex + li);
            data.Draws.Add(new DrawItem(firstIndex, 6, region, FillKind.Blur, bindGroup));
        }

        // Composite a (region-sized) layer texture into the parent at an absolute device rect.
        private void EmitLayerQuad(DrawData data, WGPUTextureFormat format, FillKind kind, IntPtr view,
            float r, float g, float b, float a, int devX, int devY, int texW, int texH, Scissor clip, int width, int height)
        {
            if (clip.IsEmpty) return;
            IntPtr sampler = kind == FillKind.Layer ? NearestSampler() : LinearSampler();
            IntPtr bindGroup = CreateSampledBindGroup(format, kind, view, sampler);
            DeferRelease(() => wgpuBindGroupRelease(bindGroup));

            float x0 = devX, y0 = devY, x1 = devX + texW, y1 = devY + texH;
            uint baseVertex = (uint)(data.Verts.Count / FloatsPerVertex);
            AddVertex(data.Verts, ToNdc(new Vector2(x0, y0), width, height), r, g, b, a, 0f, 0f);
            AddVertex(data.Verts, ToNdc(new Vector2(x1, y0), width, height), r, g, b, a, 1f, 0f);
            AddVertex(data.Verts, ToNdc(new Vector2(x1, y1), width, height), r, g, b, a, 1f, 1f);
            AddVertex(data.Verts, ToNdc(new Vector2(x0, y1), width, height), r, g, b, a, 0f, 1f);

            uint firstIndex = (uint)data.Indices.Count;
            foreach (uint li in new uint[] { 0, 1, 2, 0, 2, 3 })
                data.Indices.Add(baseVertex + li);
            data.Draws.Add(new DrawItem(firstIndex, 6, clip, kind, bindGroup));
        }

        private void EmitSubtree(SceneVisual v, Matrix3x2 world, double accOpacity, Scissor clip,
            DrawData outData, List<LayerPass> plan, int width, int height, WGPUTextureFormat format)
        {
            foreach (DrawingPrimitive primitive in v.Content)
            {
                if (primitive is Viewport3DDraw viewport)
                    Emit3DViewport(viewport, world, accOpacity, clip, outData, plan, width, height, format);
                else
                    EmitPrimitive(primitive, world, accOpacity, clip, width, height, format, outData);
            }
            foreach (SceneVisual child in v.Children)
                CollectVisual(child, world, accOpacity, clip, outData, plan, width, height, format);
        }

        private void EmitPrimitive(DrawingPrimitive primitive, Matrix3x2 world, double opacity, Scissor clip,
            int width, int height, WGPUTextureFormat format, DrawData data)
        {
            switch (primitive)
            {
                case GeometryFill fill:
                    EmitFill(fill, world, opacity, clip, width, height, format, data);
                    break;
                case GeometryStroke stroke:
                    EmitStroke(stroke, world, opacity, clip, width, height, format, data);
                    break;
                case GeometryDrawing drawing:
                    EmitGeometryDrawing(drawing, world, opacity, clip, width, height, format, data);
                    break;
                case GlyphRunDraw run:
                    EmitText(run, world, opacity, clip, width, height, data);
                    break;
            }
        }

        private static int CountDrawables(SceneVisual v)
        {
            int n = v.Content.Count;
            foreach (SceneVisual child in v.Children) n += CountDrawables(child);
            return n;
        }

        // Draws a full-target quad sampling a texture (a finished layer, a blur
        // result, or a shadow source), optionally offset, with the given kind and
        // colour. Used to composite opacity layers, shadows and blurred layers.
        private void EmitFullScreenQuad(DrawData data, WGPUTextureFormat format, FillKind kind, IntPtr view,
            float r, float g, float b, float a, float offsetX, float offsetY, Scissor clip, int width, int height)
        {
            if (clip.IsEmpty) return;
            IntPtr sampler = kind == FillKind.Layer ? NearestSampler() : LinearSampler();
            IntPtr bindGroup = CreateSampledBindGroup(format, kind, view, sampler);
            DeferRelease(() => wgpuBindGroupRelease(bindGroup));

            float x0 = offsetX, y0 = offsetY, x1 = offsetX + width, y1 = offsetY + height;
            uint baseVertex = (uint)(data.Verts.Count / FloatsPerVertex);
            AddVertex(data.Verts, ToNdc(new Vector2(x0, y0), width, height), r, g, b, a, 0f, 0f);
            AddVertex(data.Verts, ToNdc(new Vector2(x1, y0), width, height), r, g, b, a, 1f, 0f);
            AddVertex(data.Verts, ToNdc(new Vector2(x1, y1), width, height), r, g, b, a, 1f, 1f);
            AddVertex(data.Verts, ToNdc(new Vector2(x0, y1), width, height), r, g, b, a, 0f, 1f);

            uint firstIndex = (uint)data.Indices.Count;
            foreach (uint li in new uint[] { 0, 1, 2, 0, 2, 3 })
                data.Indices.Add(baseVertex + li);
            data.Draws.Add(new DrawItem(firstIndex, 6, clip, kind, bindGroup));
        }

        // Composites a (region-sized) layer masked by a clip-coverage texture (fs_clip) at the group
        // opacity. The quad covers the layer's device rect [devX,devY .. +texW,+texH] (which equals the
        // full target when the layer is full-sized), mapping uv 0..1 onto the region-sized layer + mask.
        private void EmitClipQuad(DrawData data, WGPUTextureFormat format, IntPtr layerView, IntPtr maskView,
            float groupOpacity, int devX, int devY, int texW, int texH, Scissor clip, int width, int height)
        {
            if (clip.IsEmpty) return;
            IntPtr bindGroup = CreateClipBindGroup(format, layerView, maskView, NearestSampler());
            DeferRelease(() => wgpuBindGroupRelease(bindGroup));

            float x0 = devX, y0 = devY, x1 = devX + texW, y1 = devY + texH;
            uint baseVertex = (uint)(data.Verts.Count / FloatsPerVertex);
            AddVertex(data.Verts, ToNdc(new Vector2(x0, y0), width, height), 1f, 1f, 1f, groupOpacity, 0f, 0f);
            AddVertex(data.Verts, ToNdc(new Vector2(x1, y0), width, height), 1f, 1f, 1f, groupOpacity, 1f, 0f);
            AddVertex(data.Verts, ToNdc(new Vector2(x1, y1), width, height), 1f, 1f, 1f, groupOpacity, 1f, 1f);
            AddVertex(data.Verts, ToNdc(new Vector2(x0, y1), width, height), 1f, 1f, 1f, groupOpacity, 0f, 1f);

            uint firstIndex = (uint)data.Indices.Count;
            foreach (uint li in new uint[] { 0, 1, 2, 0, 2, 3 })
                data.Indices.Add(baseVertex + li);
            data.Draws.Add(new DrawItem(firstIndex, 6, clip, FillKind.Clip, bindGroup));
        }

        private IntPtr CreateClipBindGroup(WGPUTextureFormat format, IntPtr layerView, IntPtr maskView, IntPtr sampler)
        {
            IntPtr layout = wgpuRenderPipelineGetBindGroupLayout(GetPipeline(format, FillKind.Clip), 0);
            var entries = stackalloc WGPUBindGroupEntry[3];
            entries[0] = new WGPUBindGroupEntry { binding = 0, textureView = layerView };
            entries[1] = new WGPUBindGroupEntry { binding = 1, textureView = maskView };
            entries[2] = new WGPUBindGroupEntry { binding = 2, sampler = sampler };
            var desc = new WGPUBindGroupDescriptor { layout = layout, entryCount = 3, entries = entries };
            return wgpuDeviceCreateBindGroup(_ctx.Device, &desc);
        }

        // ---- boolean (combined) geometry, combined at the coverage level ----

        private static CoverageMask RasterizeGeometryCoverage(Geometry geometry)
            => geometry is CombinedGeometry c ? RasterizeCombined(c) : PathRasterizer.Rasterize(GeometryToPath(geometry));

        private static CoverageMask RasterizeCombined(CombinedGeometry combined)
        {
            CoverageMask m1 = RasterizeGeometryCoverage(combined.Geometry1);
            CoverageMask m2 = RasterizeGeometryCoverage(combined.Geometry2);
            if (m1.IsEmpty && m2.IsEmpty) return default;

            // Work over the union of the two masks' device rectangles.
            int x0 = Math.Min(MaskMinX(m1), MaskMinX(m2));
            int y0 = Math.Min(MaskMinY(m1), MaskMinY(m2));
            int x1 = Math.Max(MaskMaxX(m1), MaskMaxX(m2));
            int y1 = Math.Max(MaskMaxY(m1), MaskMaxY(m2));
            int width = x1 - x0, height = y1 - y0;
            if (width <= 0 || height <= 0) return default;

            var bytes = new byte[width * height];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    float a = SampleMask(m1, x0 + x, y0 + y);
                    float b = SampleMask(m2, x0 + x, y0 + y);
                    float combinedCoverage = combined.Mode switch
                    {
                        GeometryCombineMode.Intersect => a * b,
                        GeometryCombineMode.Xor => a + b - 2f * a * b,
                        GeometryCombineMode.Exclude => a * (1f - b),
                        _ => a + b - a * b, // Union
                    };
                    bytes[y * width + x] = (byte)Math.Clamp((int)MathF.Round(combinedCoverage * 255f), 0, 255);
                }
            return new CoverageMask(bytes, width, height, x0, y0);
        }

        private static int MaskMinX(CoverageMask m) => m.IsEmpty ? int.MaxValue : (int)m.OriginX;
        private static int MaskMinY(CoverageMask m) => m.IsEmpty ? int.MaxValue : (int)m.OriginY;
        private static int MaskMaxX(CoverageMask m) => m.IsEmpty ? int.MinValue : (int)m.OriginX + m.Width;
        private static int MaskMaxY(CoverageMask m) => m.IsEmpty ? int.MinValue : (int)m.OriginY + m.Height;

        private static float SampleMask(CoverageMask m, int x, int y)
        {
            if (m.IsEmpty) return 0f;
            int lx = x - (int)m.OriginX, ly = y - (int)m.OriginY;
            if (lx < 0 || ly < 0 || lx >= m.Width || ly >= m.Height) return 0f;
            return m.Coverage[ly * m.Width + lx] / 255f;
        }

        // Converts a primitive geometry to an equivalent closed PathGeometry so it
        // can go through the per-pixel coverage path.
        private static PathGeometry GeometryToPath(Geometry geometry)
        {
            var figure = new PathFigure { Closed = true };
            switch (geometry)
            {
                case RectangleGeometry rg:
                    figure.Start = new Vector2(rg.Rect.X, rg.Rect.Y);
                    figure.Segments.Add(new LineSegment(new Vector2(rg.Rect.X + rg.Rect.Width, rg.Rect.Y)));
                    figure.Segments.Add(new LineSegment(new Vector2(rg.Rect.X + rg.Rect.Width, rg.Rect.Y + rg.Rect.Height)));
                    figure.Segments.Add(new LineSegment(new Vector2(rg.Rect.X, rg.Rect.Y + rg.Rect.Height)));
                    break;
                case PolygonGeometry pg when pg.Points.Length > 0:
                    figure.Start = pg.Points[0];
                    for (int i = 1; i < pg.Points.Length; i++)
                        figure.Segments.Add(new LineSegment(pg.Points[i]));
                    break;
                case RoundedRectangleGeometry rr:
                    return RoundedRectToPath(rr);
                case EllipseGeometry el:
                    return EllipseToPath(el);
                case GeometryGroup grp:
                {
                    var merged = new List<PathFigure>();
                    foreach (Geometry child in grp.Children)
                        merged.AddRange(GeometryToPath(child).Figures);
                    return new PathGeometry(grp.FillRule, merged);
                }
            }
            return new PathGeometry(FillRule.NonZero, new List<PathFigure> { figure });
        }

        // An ellipse as four quarter-arcs (cubic Béziers with the kappa offset).
        private static PathGeometry EllipseToPath(EllipseGeometry el)
        {
            const float Kappa = 0.5522847498f;
            float cx = el.Center.X, cy = el.Center.Y;
            float rx = MathF.Abs(el.RadiusX), ry = MathF.Abs(el.RadiusY);
            float ox = rx * Kappa, oy = ry * Kappa;

            var f = new PathFigure(new Vector2(cx, cy - ry)) { Closed = true }; // top
            f.Segments.Add(new CubicBezierSegment(new Vector2(cx + ox, cy - ry), new Vector2(cx + rx, cy - oy), new Vector2(cx + rx, cy))); // -> right
            f.Segments.Add(new CubicBezierSegment(new Vector2(cx + rx, cy + oy), new Vector2(cx + ox, cy + ry), new Vector2(cx, cy + ry))); // -> bottom
            f.Segments.Add(new CubicBezierSegment(new Vector2(cx - ox, cy + ry), new Vector2(cx - rx, cy + oy), new Vector2(cx - rx, cy))); // -> left
            f.Segments.Add(new CubicBezierSegment(new Vector2(cx - rx, cy - oy), new Vector2(cx - ox, cy - ry), new Vector2(cx, cy - ry))); // -> top
            return new PathGeometry(FillRule.NonZero, new List<PathFigure> { f });
        }

        // A rounded rectangle as four edges and four quarter-ellipse corners
        // (cubic Béziers using the standard kappa control-point offset).
        private static PathGeometry RoundedRectToPath(RoundedRectangleGeometry rr)
        {
            const float Kappa = 0.5522847498f;
            float x = rr.Rect.X, y = rr.Rect.Y, w = rr.Rect.Width, h = rr.Rect.Height;
            float rx = Math.Clamp(rr.RadiusX, 0f, w * 0.5f);
            float ry = Math.Clamp(rr.RadiusY, 0f, h * 0.5f);
            float kx = rx * Kappa, ky = ry * Kappa;

            var f = new PathFigure(new Vector2(x + rx, y)) { Closed = true };
            // top edge -> top-right corner
            f.Segments.Add(new LineSegment(new Vector2(x + w - rx, y)));
            f.Segments.Add(new CubicBezierSegment(new Vector2(x + w - rx + kx, y), new Vector2(x + w, y + ry - ky), new Vector2(x + w, y + ry)));
            // right edge -> bottom-right corner
            f.Segments.Add(new LineSegment(new Vector2(x + w, y + h - ry)));
            f.Segments.Add(new CubicBezierSegment(new Vector2(x + w, y + h - ry + ky), new Vector2(x + w - rx + kx, y + h), new Vector2(x + w - rx, y + h)));
            // bottom edge -> bottom-left corner
            f.Segments.Add(new LineSegment(new Vector2(x + rx, y + h)));
            f.Segments.Add(new CubicBezierSegment(new Vector2(x + rx - kx, y + h), new Vector2(x, y + h - ry + ky), new Vector2(x, y + h - ry)));
            // left edge -> top-left corner
            f.Segments.Add(new LineSegment(new Vector2(x, y + ry)));
            f.Segments.Add(new CubicBezierSegment(new Vector2(x, y + ry - ky), new Vector2(x + rx - kx, y), new Vector2(x + rx, y)));

            return new PathGeometry(FillRule.NonZero, new List<PathFigure> { f });
        }

        // Returns a copy of the geometry with all points mapped by the transform
        // (affine, so Bézier control points map exactly).
        private static PathGeometry TransformGeometry(PathGeometry geometry, Matrix3x2 m)
        {
            var figures = new List<PathFigure>(geometry.Figures.Count);
            foreach (PathFigure f in geometry.Figures)
            {
                var nf = new PathFigure(Vector2.Transform(f.Start, m)) { Closed = f.Closed };
                foreach (PathSegment seg in f.Segments)
                {
                    switch (seg)
                    {
                        case LineSegment l:
                            nf.Segments.Add(new LineSegment(Vector2.Transform(l.Point, m)));
                            break;
                        case QuadraticBezierSegment q:
                            nf.Segments.Add(new QuadraticBezierSegment(Vector2.Transform(q.Control, m), Vector2.Transform(q.Point, m)));
                            break;
                        case CubicBezierSegment c:
                            nf.Segments.Add(new CubicBezierSegment(Vector2.Transform(c.Control1, m), Vector2.Transform(c.Control2, m), Vector2.Transform(c.Point, m)));
                            break;
                    }
                }
                figures.Add(nf);
            }
            return new PathGeometry(geometry.FillRule, figures);
        }

        // A transient layer texture (e.g. the horizontal-blur intermediate): rented from the pool and
        // returned to it after the frame is submitted (reused next frame, like the geometry buffers).
        private (IntPtr Texture, IntPtr View) CreateLayerTexture(int width, int height)
        {
            (IntPtr tex, IntPtr view) = RentLayerTexture(width, height);
            DeferRelease(() => ReturnLayerTexture(tex, view, width, height));
            return (tex, view);
        }

        private static bool HasFullTargetContent(SceneVisual v)
        {
            if (v.ClipGeometry != null || v.OpacityMask != null) return true;
            foreach (DrawingPrimitive p in v.Content) if (p is Viewport3DDraw) return true;
            foreach (SceneVisual c in v.Children) if (HasFullTargetContent(c)) return true;
            return false;
        }

        // A clip/mask/effect visual can be region-sized unless something NESTED below it still needs
        // full-target dimensions: a Viewport3D in its own content, or a descendant clip/mask/3D layer
        // (rendered into this layer's region texture, those nested full-target passes would otherwise
        // get out-of-bounds scissors). The visual's OWN clip/mask does not force full-target -- its
        // region is computed by the caller (clip-path device bounds / content clip).
        private static bool HasNestedFullTargetContent(SceneVisual v)
        {
            foreach (DrawingPrimitive p in v.Content) if (p is Viewport3DDraw) return true;
            foreach (SceneVisual c in v.Children) if (HasFullTargetContent(c)) return true;
            return false;
        }

        // Device-space bounding box (Scissor) of a clip geometry under the world transform, padded 1px
        // (matching the rasterizer) and clamped to the target. Bézier control points are included, so
        // the box conservatively encloses the curve -- it is never smaller than the actual mask.
        private Scissor GeoDeviceBounds(PathGeometry geom, Matrix3x2 world, int width, int height)
        {
            PathGeometry d = TransformGeometry(geom, world);
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            void Acc(Vector2 p) { minX = MathF.Min(minX, p.X); minY = MathF.Min(minY, p.Y); maxX = MathF.Max(maxX, p.X); maxY = MathF.Max(maxY, p.Y); }
            foreach (PathFigure f in d.Figures)
            {
                Acc(f.Start);
                foreach (PathSegment s in f.Segments)
                    switch (s)
                    {
                        case LineSegment l: Acc(l.Point); break;
                        case QuadraticBezierSegment q: Acc(q.Control); Acc(q.Point); break;
                        case CubicBezierSegment c: Acc(c.Control1); Acc(c.Control2); Acc(c.Point); break;
                    }
            }
            if (minX > maxX) return new Scissor(0, 0, 0, 0);
            int ix = (int)MathF.Floor(minX) - 1, iy = (int)MathF.Floor(minY) - 1;
            int iw = (int)MathF.Ceiling(maxX) + 1 - ix, ih = (int)MathF.Ceiling(maxY) + 1 - iy;
            return Intersect(new Scissor(0, 0, width, height), new Scissor(ix, iy, iw, ih));
        }

        // Conservative device-space bounding box of everything a subtree draws. Over-estimates text
        // (advance ~1em/char, ascent/descent ~1em) so glyphs are never clipped; the caller intersects
        // with the real clip so the result is never larger than the inherited clip. Returns empty when
        // the subtree draws nothing.
        private static Scissor ContentDeviceBounds(SceneVisual v, Matrix3x2 world, int width, int height)
        {
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            AccumulateContentBounds(v, world, ref minX, ref minY, ref maxX, ref maxY);
            if (minX > maxX) return new Scissor(0, 0, 0, 0);
            int ix = (int)MathF.Floor(minX) - 1, iy = (int)MathF.Floor(minY) - 1;
            int iw = (int)MathF.Ceiling(maxX) + 1 - ix, ih = (int)MathF.Ceiling(maxY) + 1 - iy;
            return Intersect(new Scissor(0, 0, width, height), new Scissor(ix, iy, iw, ih));
        }

        private static void AccumulateContentBounds(SceneVisual v, Matrix3x2 world,
            ref float minX, ref float minY, ref float maxX, ref float maxY)
        {
            foreach (DrawingPrimitive p in v.Content)
            {
                switch (p)
                {
                    case GeometryFill f: AccGeometry(f.Geometry, world, 0f, ref minX, ref minY, ref maxX, ref maxY); break;
                    case GeometryStroke s: AccGeometry(s.Geometry, world, (float)s.Style.Thickness * 0.5f, ref minX, ref minY, ref maxX, ref maxY); break;
                    case GeometryDrawing d: AccGeometry(d.Geometry, world, (float)d.StrokeStyle.Thickness * 0.5f, ref minX, ref minY, ref maxX, ref maxY); break;
                    case GlyphRunDraw g: AccText(g, world, ref minX, ref minY, ref maxX, ref maxY); break;
                }
            }
            foreach (SceneVisual c in v.Children)
                AccumulateContentBounds(c, c.LocalToParent * world, ref minX, ref minY, ref maxX, ref maxY);
        }

        private static void AccPoint(Vector2 p, Matrix3x2 world, float pad,
            ref float minX, ref float minY, ref float maxX, ref float maxY)
        {
            Vector2 d = Vector2.Transform(p, world);
            minX = MathF.Min(minX, d.X - pad); minY = MathF.Min(minY, d.Y - pad);
            maxX = MathF.Max(maxX, d.X + pad); maxY = MathF.Max(maxY, d.Y + pad);
        }

        private static void AccGeometry(Geometry g, Matrix3x2 world, float pad,
            ref float minX, ref float minY, ref float maxX, ref float maxY)
        {
            if (g is CombinedGeometry cg)
            {
                AccGeometry(cg.Geometry1, world, pad, ref minX, ref minY, ref maxX, ref maxY);
                AccGeometry(cg.Geometry2, world, pad, ref minX, ref minY, ref maxX, ref maxY);
                return;
            }
            PathGeometry path = g as PathGeometry ?? GeometryToPath(g);
            foreach (PathFigure f in path.Figures)
            {
                AccPoint(f.Start, world, pad, ref minX, ref minY, ref maxX, ref maxY);
                foreach (PathSegment seg in f.Segments)
                    switch (seg)
                    {
                        case LineSegment l: AccPoint(l.Point, world, pad, ref minX, ref minY, ref maxX, ref maxY); break;
                        case QuadraticBezierSegment q:
                            AccPoint(q.Control, world, pad, ref minX, ref minY, ref maxX, ref maxY);
                            AccPoint(q.Point, world, pad, ref minX, ref minY, ref maxX, ref maxY); break;
                        case CubicBezierSegment c:
                            AccPoint(c.Control1, world, pad, ref minX, ref minY, ref maxX, ref maxY);
                            AccPoint(c.Control2, world, pad, ref minX, ref minY, ref maxX, ref maxY);
                            AccPoint(c.Point, world, pad, ref minX, ref minY, ref maxX, ref maxY); break;
                    }
            }
        }

        private static void AccText(GlyphRunDraw g, Matrix3x2 world,
            ref float minX, ref float minY, ref float maxX, ref float maxY)
        {
            float w = Math.Max(1, g.Text?.Length ?? 0) * g.EmSize;
            float x0 = g.Origin.X, x1 = g.Origin.X + w;
            float y0 = g.Origin.Y - g.EmSize, y1 = g.Origin.Y + g.EmSize * 0.5f;
            AccPoint(new Vector2(x0, y0), world, 0f, ref minX, ref minY, ref maxX, ref maxY);
            AccPoint(new Vector2(x1, y0), world, 0f, ref minX, ref minY, ref maxX, ref maxY);
            AccPoint(new Vector2(x1, y1), world, 0f, ref minX, ref minY, ref maxX, ref maxY);
            AccPoint(new Vector2(x0, y1), world, 0f, ref minX, ref minY, ref maxX, ref maxY);
        }

        private static bool AnyText(DrawData main, List<LayerPass> plan)
        {
            if (main.HasText) return true;
            foreach (LayerPass lp in plan) if (lp.Data.HasText) return true;
            return false;
        }

        private void EmitFill(GeometryFill fill, Matrix3x2 world, double opacity, Scissor clip,
            int width, int height, WGPUTextureFormat format, DrawData data)
        {
            if (clip.IsEmpty) return;

            // Arbitrary paths are rasterized to an analytic-AA coverage mask and
            // composited like glyphs (coverage * brush colour).
            if (fill.Geometry is PathGeometry pathGeom)
            {
                EmitPath(fill, pathGeom, world, opacity, clip, width, height, format, data);
                return;
            }

            // Boolean combinations are combined at the coverage level (per-pixel),
            // so they can't be flattened to a single path.
            if (fill.Geometry is CombinedGeometry combined)
            {
                if (!clip.IsEmpty)
                    EmitMask(RasterizeCombined(combined), fill.Brush, world, opacity, clip, width, height, format, data);
                return;
            }

            // Curved/composite geometries (rounded rect, ellipse, group) go through
            // the analytic-AA coverage path rather than flat tessellation.
            if (fill.Geometry is RoundedRectangleGeometry or EllipseGeometry or GeometryGroup)
            {
                EmitCoverageMask(GeometryToPath(fill.Geometry), fill.Brush, world, opacity, clip, width, height, format, data);
                return;
            }

            // The mesh path (per-vertex interpolation + a clamped ramp) can only do
            // a Pad-spread linear gradient. Radial gradients and Reflect/Repeat
            // spreads need per-pixel evaluation, so route them through coverage.
            bool needsPerPixelBrush = fill.Brush is RadialGradientBrush
                || (fill.Brush is LinearGradientBrush lg && lg.SpreadMethod != GradientSpreadMethod.Pad)
                || (fill.Brush is ImageBrush ib && ib.TileMode != TileMode.None);
            if (needsPerPixelBrush)
            {
                EmitCoverageMask(GeometryToPath(fill.Geometry), fill.Brush, world, opacity, clip, width, height, format, data);
                return;
            }

            // A rotated/skewed rectangle is a non-axis-aligned quad whose edges alias under flat
            // tessellation (no MSAA on the 2D path). Route it through the analytic-AA coverage path
            // instead (same as paths/ellipses); keep the fast mesh path for axis-aligned rects, which
            // don't alias. M12/M21 are the off-diagonal (rotation/shear) terms of the world matrix.
            if (Math.Abs(world.M12) > 1e-6f || Math.Abs(world.M21) > 1e-6f)
            {
                EmitCoverageMask(GeometryToPath(fill.Geometry), fill.Brush, world, opacity, clip, width, height, format, data);
                return;
            }

            Mesh mesh = Tessellator.Tessellate(fill.Geometry);
            if (mesh.Indices.Length == 0) return;

            float opacityF = (float)Math.Clamp(opacity, 0.0, 1.0);
            uint baseVertex = (uint)(data.Verts.Count / FloatsPerVertex);

            FillKind kind = FillKind.Solid;
            IntPtr bindGroup = IntPtr.Zero;

            switch (fill.Brush)
            {
                case SolidColorBrush solid:
                {
                    float a = (float)Math.Clamp(solid.Color.A * opacity, 0.0, 1.0);
                    float r = solid.Color.R * a, g = solid.Color.G * a, b = solid.Color.B * a;
                    foreach (Vector2 p in mesh.Positions)
                        AddVertex(data.Verts, ToNdc(Vector2.Transform(p, world), width, height), r, g, b, a, 0f, 0f);
                    break;
                }
                case LinearGradientBrush grad:
                {
                    var (tex, view) = CreateRgbaTexture(BuildGradientRamp(grad.Stops), GradientRampTexels, 1);
                    bindGroup = CreateSampledBindGroup(format, FillKind.Textured, view, LinearSampler());
                    DeferReleaseSampled(tex, view, bindGroup);
                    kind = FillKind.Textured;

                    Vector2 axis = grad.End - grad.Start;
                    float len2 = axis.LengthSquared();
                    foreach (Vector2 p in mesh.Positions)
                    {
                        float t = len2 > 0f ? Vector2.Dot(p - grad.Start, axis) / len2 : 0f;
                        AddVertex(data.Verts, ToNdc(Vector2.Transform(p, world), width, height), 1f, 1f, 1f, opacityF, t, 0.5f);
                    }
                    break;
                }
                case ImageBrush img:
                {
                    var (tex, view) = CreateImageTexture(img.PixelsRgba, img.PixelWidth, img.PixelHeight);
                    bindGroup = CreateSampledBindGroup(format, FillKind.Textured, view, NearestSampler());
                    DeferReleaseSampled(tex, view, bindGroup);
                    kind = FillKind.Textured;

                    Bounds(mesh.Positions, out Vector2 min, out Vector2 size);
                    foreach (Vector2 p in mesh.Positions)
                    {
                        float u = size.X > 0f ? (p.X - min.X) / size.X : 0f;
                        float vv = size.Y > 0f ? (p.Y - min.Y) / size.Y : 0f;
                        AddVertex(data.Verts, ToNdc(Vector2.Transform(p, world), width, height), 1f, 1f, 1f, opacityF, u, vv);
                    }
                    break;
                }
                default:
                    return;
            }

            uint firstIndex = (uint)data.Indices.Count;
            foreach (uint li in mesh.Indices)
                data.Indices.Add(baseVertex + li);
            data.Draws.Add(new DrawItem(firstIndex, (uint)mesh.Indices.Length, clip, kind, bindGroup));
        }

        // Fills an arbitrary path with any brush; AA is in the coverage mask.
        private void EmitPath(GeometryFill fill, PathGeometry path, Matrix3x2 world, double opacity, Scissor clip,
            int width, int height, WGPUTextureFormat format, DrawData data)
            => EmitCoverageMask(path, fill.Brush, world, opacity, clip, width, height, format, data);

        // Fills a geometry and/or strokes its outline in one primitive (the fill
        // first, then the stroke on top), reusing the fill and stroke paths.
        private void EmitGeometryDrawing(GeometryDrawing drawing, Matrix3x2 world, double opacity, Scissor clip,
            int width, int height, WGPUTextureFormat format, DrawData data)
        {
            if (drawing.Fill is { } fillBrush)
                EmitFill(new GeometryFill(drawing.Geometry, fillBrush), world, opacity, clip, width, height, format, data);

            if (drawing.Stroke is { } strokeBrush && drawing.StrokeStyle.Thickness > 0)
            {
                PathGeometry path = drawing.Geometry as PathGeometry ?? GeometryToPath(drawing.Geometry);
                EmitStroke(new GeometryStroke(path, strokeBrush, drawing.StrokeStyle), world, opacity, clip, width, height, format, data);
            }
        }

        // Strokes a path: build the stroke outline (stroke-to-fill) and composite
        // it through the same coverage path as a filled path (any brush).
        private void EmitStroke(GeometryStroke stroke, Matrix3x2 world, double opacity, Scissor clip,
            int width, int height, WGPUTextureFormat format, DrawData data)
        {
            if (clip.IsEmpty || stroke.Style.Thickness <= 0) return;
            PathGeometry outline = PathStroker.Stroke(stroke.Geometry, stroke.Style);
            EmitCoverageMask(outline, stroke.Brush, world, opacity, clip, width, height, format, data);
        }

        private void EmitCoverageMask(PathGeometry coverageGeometry, Brush brush, Matrix3x2 world, double opacity,
            Scissor clip, int width, int height, WGPUTextureFormat format, DrawData data)
        {
            if (clip.IsEmpty) return;

            // Solid coverage (text, icons, rounded rects, ellipses, strokes) is rasterized in
            // DEVICE space so the mask isn't upscaled by the world transform -- this keeps text
            // and edges crisp under the DPI/scale/rotation transform instead of bilinear-blurry.
            // (Non-solid brushes bake per-texel in the geometry's local space, so they stay local.)
            if (brush is SolidColorBrush solid)
            {
                PathGeometry deviceGeom = TransformGeometry(coverageGeometry, world);
                long key = HashGeometry(deviceGeom) * 397 ^ (long)format;
                if (!_maskCache.TryGetValue(key, out CachedMask? cm))
                {
                    PerfCoverage++;
                    CoverageMask m = PathRasterizer.Rasterize(deviceGeom);
                    if (m.IsEmpty) return;
                    (IntPtr tex, IntPtr view) = CreateR8Texture(m.Coverage, m.Width, m.Height);
                    IntPtr bg = CreateSampledBindGroup(format, FillKind.Text, view, NearestSampler());
                    cm = new CachedMask { Tex = tex, View = view, BindGroup = bg, Ox = (int)m.OriginX, Oy = (int)m.OriginY, W = m.Width, H = m.Height };
                    _maskCache[key] = cm;   // cache owns these (NOT defer-released); evicted in EndFrame
                }
                cm.LastFrame = _frameId;
                EmitCachedSolidMask(cm, solid.Color, opacity, clip, width, height, data);
                return;
            }
            PerfCoverage++;
            EmitMask(PathRasterizer.Rasterize(coverageGeometry), brush, world, opacity, clip, width, height, format, data);
        }

        // Emit a quad sampling a cached device-space coverage mask, tinted by the solid colour.
        private void EmitCachedSolidMask(CachedMask c, RgbaColor color, double opacity, Scissor clip, int width, int height, DrawData data)
        {
            if (clip.IsEmpty) return;
            float r = color.R, g = color.G, b = color.B, a = (float)Math.Clamp(color.A * opacity, 0.0, 1.0);
            float x0 = c.Ox, y0 = c.Oy, x1 = c.Ox + c.W, y1 = c.Oy + c.H;
            uint baseVertex = (uint)(data.Verts.Count / FloatsPerVertex);
            AddVertex(data.Verts, ToNdc(new Vector2(x0, y0), width, height), r, g, b, a, 0f, 0f);
            AddVertex(data.Verts, ToNdc(new Vector2(x1, y0), width, height), r, g, b, a, 1f, 0f);
            AddVertex(data.Verts, ToNdc(new Vector2(x1, y1), width, height), r, g, b, a, 1f, 1f);
            AddVertex(data.Verts, ToNdc(new Vector2(x0, y1), width, height), r, g, b, a, 0f, 1f);
            uint firstIndex = (uint)data.Indices.Count;
            foreach (uint li in new uint[] { 0, 1, 2, 0, 2, 3 }) data.Indices.Add(baseVertex + li);
            data.Draws.Add(new DrawItem(firstIndex, 6, clip, FillKind.Text, c.BindGroup));
        }

        private static long HashGeometry(PathGeometry g)
        {
            long h = 17 * 31 + (int)g.FillRule;
            static long HashV(Vector2 v) => ((long)BitConverter.SingleToInt32Bits(v.X) << 32) ^ (uint)BitConverter.SingleToInt32Bits(v.Y);
            foreach (PathFigure f in g.Figures)
            {
                h = h * 31 + HashV(f.Start);
                foreach (PathSegment s in f.Segments)
                {
                    h = s switch
                    {
                        LineSegment l => h * 31 + HashV(l.Point),
                        QuadraticBezierSegment q => (h * 31 + HashV(q.Control)) * 31 + HashV(q.Point),
                        CubicBezierSegment c => ((h * 31 + HashV(c.Control1)) * 31 + HashV(c.Control2)) * 31 + HashV(c.Point),
                        _ => h * 31,
                    };
                }
            }
            return h;
        }

        // Composites a coverage mask with a brush. Solid brushes sample the R8
        // coverage directly (fs_text); other brushes are baked per-texel into an
        // RGBA mask (brush colour, alpha = coverage * brush alpha) for fs_textured.
        private void EmitMask(CoverageMask mask, Brush brush, Matrix3x2 world, double opacity,
            Scissor clip, int width, int height, WGPUTextureFormat format, DrawData data)
        {
            if (clip.IsEmpty || mask.IsEmpty) return;

            IntPtr view, bindGroup;
            FillKind kind;
            float r, g, b, a;
            if (brush is SolidColorBrush solid)
            {
                (IntPtr tex, view) = CreateR8Texture(mask.Coverage, mask.Width, mask.Height);
                bindGroup = CreateSampledBindGroup(format, FillKind.Text, view, NearestSampler());
                DeferReleaseSampled(tex, view, bindGroup);
                kind = FillKind.Text;
                r = solid.Color.R; g = solid.Color.G; b = solid.Color.B;
                a = (float)Math.Clamp(solid.Color.A * opacity, 0.0, 1.0);
            }
            else
            {
                byte[] rgba = BakeBrushMask(mask, brush);
                (IntPtr tex, view) = CreateRgbaTexture(rgba, mask.Width, mask.Height);
                bindGroup = CreateSampledBindGroup(format, FillKind.Textured, view, NearestSampler());
                DeferReleaseSampled(tex, view, bindGroup);
                kind = FillKind.Textured;
                r = 1f; g = 1f; b = 1f;
                a = (float)Math.Clamp(opacity, 0.0, 1.0);
            }

            float x0 = mask.OriginX, y0 = mask.OriginY;
            float x1 = x0 + mask.Width, y1 = y0 + mask.Height;

            uint baseVertex = (uint)(data.Verts.Count / FloatsPerVertex);
            AddVertex(data.Verts, ToNdc(Vector2.Transform(new Vector2(x0, y0), world), width, height), r, g, b, a, 0f, 0f);
            AddVertex(data.Verts, ToNdc(Vector2.Transform(new Vector2(x1, y0), world), width, height), r, g, b, a, 1f, 0f);
            AddVertex(data.Verts, ToNdc(Vector2.Transform(new Vector2(x1, y1), world), width, height), r, g, b, a, 1f, 1f);
            AddVertex(data.Verts, ToNdc(Vector2.Transform(new Vector2(x0, y1), world), width, height), r, g, b, a, 0f, 1f);

            uint firstIndex = (uint)data.Indices.Count;
            foreach (uint li in new uint[] { 0, 1, 2, 0, 2, 3 })
                data.Indices.Add(baseVertex + li);
            data.Draws.Add(new DrawItem(firstIndex, 6, clip, kind, bindGroup));
        }

        // Evaluates a non-solid brush at each coverage texel, producing a straight
        // RGBA mask (rgb = brush colour, a = coverage * brush alpha) that the
        // premultiplying fs_textured pipeline composites correctly.
        private static byte[] BakeBrushMask(CoverageMask mask, Brush brush)
        {
            var rgba = new byte[mask.Width * mask.Height * 4];
            for (int y = 0; y < mask.Height; y++)
            {
                for (int x = 0; x < mask.Width; x++)
                {
                    int i = (y * mask.Width + x);
                    float cov = mask.Coverage[i] / 255f;
                    var localPos = new Vector2(mask.OriginX + x + 0.5f, mask.OriginY + y + 0.5f);
                    RgbaColor c = EvaluateBrush(brush, localPos, mask);
                    rgba[i * 4 + 0] = ToByte(c.R);
                    rgba[i * 4 + 1] = ToByte(c.G);
                    rgba[i * 4 + 2] = ToByte(c.B);
                    rgba[i * 4 + 3] = ToByte(cov * c.A);
                }
            }
            return rgba;
        }

        private static RgbaColor EvaluateBrush(Brush brush, Vector2 localPos, CoverageMask mask)
        {
            switch (brush)
            {
                case LinearGradientBrush grad:
                {
                    Vector2 axis = grad.End - grad.Start;
                    float len2 = axis.LengthSquared();
                    float t = len2 > 0f ? Vector2.Dot(localPos - grad.Start, axis) / len2 : 0f;
                    return SampleStops(grad.Stops, ApplySpread(t, grad.SpreadMethod));
                }
                case RadialGradientBrush radial:
                {
                    float dx = radial.RadiusX > 0f ? (localPos.X - radial.Center.X) / radial.RadiusX : 0f;
                    float dy = radial.RadiusY > 0f ? (localPos.Y - radial.Center.Y) / radial.RadiusY : 0f;
                    float t = MathF.Sqrt(dx * dx + dy * dy);
                    return SampleStops(radial.Stops, ApplySpread(t, radial.SpreadMethod));
                }
                case ImageBrush img when img.PixelWidth > 0 && img.PixelHeight > 0:
                {
                    float u, v;
                    if (img.TileMode == TileMode.None || img.TileWidth <= 0f || img.TileHeight <= 0f)
                    {
                        // Map once across the geometry bounds.
                        u = mask.Width > 0 ? (localPos.X - mask.OriginX) / mask.Width : 0f;
                        v = mask.Height > 0 ? (localPos.Y - mask.OriginY) / mask.Height : 0f;
                    }
                    else
                    {
                        // Repeat every (TileWidth, TileHeight); flip alternate cells.
                        float tu = localPos.X / img.TileWidth;
                        float tv = localPos.Y / img.TileHeight;
                        int cellX = (int)MathF.Floor(tu);
                        int cellY = (int)MathF.Floor(tv);
                        u = tu - cellX;
                        v = tv - cellY;
                        if ((img.TileMode == TileMode.FlipX || img.TileMode == TileMode.FlipXY) && (cellX & 1) != 0) u = 1f - u;
                        if ((img.TileMode == TileMode.FlipY || img.TileMode == TileMode.FlipXY) && (cellY & 1) != 0) v = 1f - v;
                    }
                    int px = Math.Clamp((int)(u * img.PixelWidth), 0, img.PixelWidth - 1);
                    int py = Math.Clamp((int)(v * img.PixelHeight), 0, img.PixelHeight - 1);
                    int p = (py * img.PixelWidth + px) * 4;
                    return RgbaColor.FromBytes(img.PixelsRgba[p], img.PixelsRgba[p + 1], img.PixelsRgba[p + 2], img.PixelsRgba[p + 3]);
                }
                case SolidColorBrush solid:
                    return solid.Color;
                default:
                    return new RgbaColor(0, 0, 0, 0);
            }
        }

        // Bakes a brush's alpha into a full-target R8 mask (device space). Used as
        // an opacity mask; gradient endpoints are transformed by the world matrix.
        private static byte[] RasterizeOpacityMask(Brush brush, Matrix3x2 world, int width, int height, int originX = 0, int originY = 0)
        {
            var bytes = new byte[width * height];
            switch (brush)
            {
                case SolidColorBrush solid:
                {
                    byte a = ToByte(solid.Color.A);
                    Array.Fill(bytes, a);
                    break;
                }
                case LinearGradientBrush g:
                {
                    Vector2 start = Vector2.Transform(g.Start, world);
                    Vector2 axis = Vector2.Transform(g.End, world) - start;
                    float len2 = axis.LengthSquared();
                    for (int y = 0; y < height; y++)
                        for (int x = 0; x < width; x++)
                        {
                            var p = new Vector2(originX + x + 0.5f, originY + y + 0.5f);
                            float t = len2 > 0f ? ApplySpread(Vector2.Dot(p - start, axis) / len2, g.SpreadMethod) : 0f;
                            bytes[y * width + x] = ToByte(SampleStops(g.Stops, t).A);
                        }
                    break;
                }
                case RadialGradientBrush rad:
                {
                    Vector2 c = Vector2.Transform(rad.Center, world);
                    float rx = rad.RadiusX * new Vector2(world.M11, world.M12).Length();
                    float ry = rad.RadiusY * new Vector2(world.M21, world.M22).Length();
                    for (int y = 0; y < height; y++)
                        for (int x = 0; x < width; x++)
                        {
                            float dx = rx > 0f ? (originX + x + 0.5f - c.X) / rx : 0f;
                            float dy = ry > 0f ? (originY + y + 0.5f - c.Y) / ry : 0f;
                            float t = ApplySpread(MathF.Sqrt(dx * dx + dy * dy), rad.SpreadMethod);
                            bytes[y * width + x] = ToByte(SampleStops(rad.Stops, t).A);
                        }
                    break;
                }
                default:
                    Array.Fill(bytes, (byte)255); // unsupported mask brush: fully opaque
                    break;
            }
            return bytes;
        }

        // Maps a gradient parameter t (which may fall outside [0,1]) back into the
        // [0,1] stop range per the spread method.
        private static float ApplySpread(float t, GradientSpreadMethod spread)
        {
            switch (spread)
            {
                case GradientSpreadMethod.Repeat:
                    return t - MathF.Floor(t);
                case GradientSpreadMethod.Reflect:
                {
                    float f = t - 2f * MathF.Floor(t / 2f); // [0,2)
                    return f > 1f ? 2f - f : f;
                }
                default:
                    return Math.Clamp(t, 0f, 1f);
            }
        }

        // Lays out a glyph run: each glyph becomes a quad sampling the atlas.
        // Glyph layout happens in local space (so transforms/opacity carry the
        // text like any other content); the shared atlas bind group is supplied
        // at record time.
        private void EmitText(GlyphRunDraw run, Matrix3x2 world, double opacity, Scissor clip, int width, int height, DrawData data)
        {
            if (clip.IsEmpty || string.IsNullOrEmpty(run.Text)) return;

            // Shape the run into positioned glyphs (glyph ids + advances/offsets),
            // then lay them out. Advances come from the shaper (so kerning etc.
            // are honoured); the atlas provides each glyph's bitmap and bearings.
            _shaper.Shape(_font, run.Text, _shapeScratch);

            float scale = run.EmSize / _font.PixelsPerEm;
            float a = (float)Math.Clamp(run.Color.A * opacity, 0.0, 1.0);
            float penX = run.Origin.X;
            float baseline = run.Origin.Y;

            foreach (Text.ShapedGlyph sg in _shapeScratch)
            {
                if (_glyphAtlas.TryGetOrAdd(_font, sg.GlyphId, out Text.GlyphEntry e) && e.Width > 0 && e.Height > 0)
                {
                    float x0 = penX + (e.BearingX + sg.XOffset) * scale;
                    float y0 = baseline - (e.BearingY + sg.YOffset) * scale;
                    float x1 = x0 + e.Width * scale;
                    float y1 = y0 + e.Height * scale;

                    uint baseVertex = (uint)(data.Verts.Count / FloatsPerVertex);
                    AddVertex(data.Verts, ToNdc(Vector2.Transform(new Vector2(x0, y0), world), width, height), run.Color.R, run.Color.G, run.Color.B, a, e.U0, e.V0);
                    AddVertex(data.Verts, ToNdc(Vector2.Transform(new Vector2(x1, y0), world), width, height), run.Color.R, run.Color.G, run.Color.B, a, e.U1, e.V0);
                    AddVertex(data.Verts, ToNdc(Vector2.Transform(new Vector2(x1, y1), world), width, height), run.Color.R, run.Color.G, run.Color.B, a, e.U1, e.V1);
                    AddVertex(data.Verts, ToNdc(Vector2.Transform(new Vector2(x0, y1), world), width, height), run.Color.R, run.Color.G, run.Color.B, a, e.U0, e.V1);

                    uint firstIndex = (uint)data.Indices.Count;
                    foreach (uint li in new uint[] { 0, 1, 2, 0, 2, 3 })
                        data.Indices.Add(baseVertex + li);
                    data.Draws.Add(new DrawItem(firstIndex, 6, clip, FillKind.Text, IntPtr.Zero));
                    data.HasText = true;
                }

                penX += sg.Advance * scale;
            }
        }

        private static void AddVertex(List<float> verts, Vector2 ndc, float r, float g, float b, float a, float u, float v)
        {
            verts.Add(ndc.X); verts.Add(ndc.Y);
            verts.Add(r); verts.Add(g); verts.Add(b); verts.Add(a);
            verts.Add(u); verts.Add(v);
        }

        // The current render target's absolute device origin (non-zero when rendering into a
        // region-sized layer texture, so a card's effect uses a ~card-sized target instead of the
        // whole window). ToNdc maps absolute device coords into the current target's NDC.
        private float _devOX, _devOY;

        private Vector2 ToNdc(Vector2 devicePoint, int width, int height)
            => new((devicePoint.X - _devOX) / width * 2f - 1f, 1f - (devicePoint.Y - _devOY) / height * 2f);

        private IntPtr BeginClearPass(IntPtr encoder, IntPtr view, RgbaColor background)
        {
            var colorAttachment = new WGPURenderPassColorAttachment
            {
                view = view,
                depthSlice = WGPU_DEPTH_SLICE_UNDEFINED,
                loadOp = WGPULoadOp.Clear,
                storeOp = WGPUStoreOp.Store,
                clearValue = new WGPUColor { r = background.R, g = background.G, b = background.B, a = background.A },
            };
            var passDesc = new WGPURenderPassDescriptor { colorAttachmentCount = 1, colorAttachments = &colorAttachment };
            return wgpuCommandEncoderBeginRenderPass(encoder, &passDesc);
        }

        private void BuildGeometryBuffers(DrawData data, out IntPtr vbuf, out IntPtr ibuf, out bool hasGeometry)
        {
            vbuf = IntPtr.Zero;
            ibuf = IntPtr.Zero;
            hasGeometry = data.Indices.Count > 0;
            if (!hasGeometry) return;

            byte[] vbytes = new byte[data.Verts.Count * sizeof(float)];
            Buffer.BlockCopy(data.Verts.ToArray(), 0, vbytes, 0, vbytes.Length);
            byte[] ibytes = new byte[data.Indices.Count * sizeof(uint)];
            Buffer.BlockCopy(data.Indices.ToArray(), 0, ibytes, 0, ibytes.Length);

            vbuf = RentBuffer((ulong)vbytes.Length, index: false);
            ibuf = RentBuffer((ulong)ibytes.Length, index: true);
            _ctx.WriteBuffer(vbuf, vbytes);
            _ctx.WriteBuffer(ibuf, ibytes);
            // Buffers are recycled (not released) in FlushFrameReleases.
        }

        private void RecordDraws(IntPtr pass, WGPUTextureFormat format, IntPtr vbuf, IntPtr ibuf, DrawData data, IntPtr atlasBindGroup,
            int originX = 0, int originY = 0, int texW = 0, int texH = 0)
        {
            wgpuRenderPassEncoderSetVertexBuffer(pass, 0, vbuf, 0, (ulong)(data.Verts.Count * sizeof(float)));
            wgpuRenderPassEncoderSetIndexBuffer(pass, ibuf, WGPUIndexFormat.Uint32, 0, (ulong)(data.Indices.Count * sizeof(uint)));

            foreach (DrawItem d in data.Draws)
            {
                if (d.Clip.IsEmpty) continue;
                wgpuRenderPassEncoderSetPipeline(pass, GetPipeline(format, d.Kind));
                switch (d.Kind)
                {
                    case FillKind.Textured:
                    case FillKind.Layer:
                    case FillKind.Blur:
                    case FillKind.Shadow:
                    case FillKind.Clip:
                        wgpuRenderPassEncoderSetBindGroup(pass, 0, d.BindGroup, 0, null);
                        break;
                    case FillKind.Text:
                        // Glyph runs share the atlas bind group; path masks carry their own.
                        wgpuRenderPassEncoderSetBindGroup(pass, 0, d.BindGroup != IntPtr.Zero ? d.BindGroup : atlasBindGroup, 0, null);
                        break;
                }
                // Rebase the absolute scissor into the (possibly region-sized) target + clamp.
                int sx = d.Clip.X - originX, sy = d.Clip.Y - originY, sw = d.Clip.W, sh = d.Clip.H;
                if (texW > 0)
                {
                    if (sx < 0) { sw += sx; sx = 0; }
                    if (sy < 0) { sh += sy; sy = 0; }
                    if (sx + sw > texW) sw = texW - sx;
                    if (sy + sh > texH) sh = texH - sy;
                    if (sw <= 0 || sh <= 0) continue;
                }
                wgpuRenderPassEncoderSetScissorRect(pass, (uint)sx, (uint)sy, (uint)sw, (uint)sh);
                wgpuRenderPassEncoderDrawIndexed(pass, d.IndexCount, 1, d.FirstIndex, 0, 0);
            }
        }

        private void DeferReleaseSampled(IntPtr texture, IntPtr view, IntPtr bindGroup)
        {
            DeferRelease(() =>
            {
                wgpuBindGroupRelease(bindGroup);
                wgpuTextureViewRelease(view);
                wgpuTextureRelease(texture);
            });
        }

        // Returns the glyph-atlas texture view, rebuilding it only when new glyphs
        // were added. The atlas is long-lived (re-uploading it every frame was a
        // leak); per-pass bind groups are created from this view on demand.
        private IntPtr EnsureAtlasView(bool anyText)
        {
            if (!anyText) return IntPtr.Zero;
            if (_atlasValid && !_glyphAtlas.Dirty) return _atlasView;

            ReleaseAtlas();
            (_atlasTexture, _atlasView) = CreateR8Texture(_glyphAtlas.Pixels, _glyphAtlas.Width, _glyphAtlas.Height);
            _atlasValid = true;
            _glyphAtlas.ClearDirty();
            AtlasUploads++;
            return _atlasView;
        }

        private void ReleaseAtlas()
        {
            if (!_atlasValid) return;
            wgpuTextureViewRelease(_atlasView);
            wgpuTextureRelease(_atlasTexture);
            _atlasView = _atlasTexture = IntPtr.Zero;
            _atlasValid = false;
        }

        public void Dispose()
        {
            FlushFrameReleases();
            foreach ((IntPtr Buf, ulong _) in _freeVtx) wgpuBufferRelease(Buf);
            foreach ((IntPtr Buf, ulong _) in _freeIdx) wgpuBufferRelease(Buf);
            foreach ((IntPtr Buf, ulong _, bool _) in _inUseBufs) wgpuBufferRelease(Buf);
            _freeVtx.Clear(); _freeIdx.Clear(); _inUseBufs.Clear();
            foreach ((IntPtr Tex, IntPtr View, int _, int _) in _freeLayerTex) { wgpuTextureViewRelease(View); wgpuTextureRelease(Tex); }
            _freeLayerTex.Clear();
            ReleaseAtlas();
            ReleaseResources3D();
            foreach (IntPtr pipeline in _pipelines.Values) wgpuRenderPipelineRelease(pipeline);
            _pipelines.Clear();
            if (_shaderModule != IntPtr.Zero) wgpuShaderModuleRelease(_shaderModule);
            if (_clipShaderModule != IntPtr.Zero) wgpuShaderModuleRelease(_clipShaderModule);
            if (_linearSampler != IntPtr.Zero) wgpuSamplerRelease(_linearSampler);
            if (_nearestSampler != IntPtr.Zero) wgpuSamplerRelease(_nearestSampler);
            _shaderModule = _clipShaderModule = _linearSampler = _nearestSampler = IntPtr.Zero;
        }

        // ---- GPU resource creation ----

        private IntPtr GetShaderModule()
        {
            if (_shaderModule != IntPtr.Zero) return _shaderModule;

            byte[] wgsl = Encoding.UTF8.GetBytes(ShaderWgsl);
            fixed (byte* pWgsl = wgsl)
            {
                var wgslSource = new WGPUShaderSourceWGSL
                {
                    chain = new WGPUChainedStruct { next = null, sType = WGPUSType_ShaderSourceWGSL },
                    code = new WGPUStringView { data = pWgsl, length = (nuint)wgsl.Length },
                };
                var shaderDesc = new WGPUShaderModuleDescriptor { nextInChain = (WGPUChainedStruct*)&wgslSource };
                _shaderModule = wgpuDeviceCreateShaderModule(_ctx.Device, &shaderDesc);
            }
            return _shaderModule;
        }

        private IntPtr GetPipeline(WGPUTextureFormat format, FillKind kind)
        {
            var key = (format, kind);
            if (_pipelines.TryGetValue(key, out IntPtr cached))
                return cached;
            IntPtr shader = kind == FillKind.Clip ? GetClipShaderModule() : GetShaderModule();
            IntPtr pipeline = CreatePipeline(_ctx.Device, shader, format, kind);
            _pipelines[key] = pipeline;
            return pipeline;
        }

        private IntPtr GetClipShaderModule()
        {
            if (_clipShaderModule != IntPtr.Zero) return _clipShaderModule;
            byte[] wgsl = Encoding.UTF8.GetBytes(ClipShaderWgsl);
            fixed (byte* pWgsl = wgsl)
            {
                var src = new WGPUShaderSourceWGSL
                {
                    chain = new WGPUChainedStruct { next = null, sType = WGPUSType_ShaderSourceWGSL },
                    code = new WGPUStringView { data = pWgsl, length = (nuint)wgsl.Length },
                };
                var desc = new WGPUShaderModuleDescriptor { nextInChain = (WGPUChainedStruct*)&src };
                _clipShaderModule = wgpuDeviceCreateShaderModule(_ctx.Device, &desc);
            }
            return _clipShaderModule;
        }

        private static IntPtr CreatePipeline(IntPtr device, IntPtr shader, WGPUTextureFormat targetFormat, FillKind kind)
        {
            string fsName = kind switch
            {
                FillKind.Textured => "fs_textured",
                FillKind.Text => "fs_text",
                FillKind.Layer => "fs_layer",
                FillKind.Blur => "fs_blur",
                FillKind.Shadow => "fs_shadow",
                FillKind.Clip => "fs_clip",
                _ => "fs_solid",
            };
            byte[] vsEntry = Encoding.UTF8.GetBytes("vs_main");
            byte[] fsEntry = Encoding.UTF8.GetBytes(fsName);

            fixed (byte* pVs = vsEntry)
            fixed (byte* pFs = fsEntry)
            {
                var attributes = stackalloc WGPUVertexAttribute[3];
                attributes[0] = new WGPUVertexAttribute { format = WGPUVertexFormat.Float32x2, offset = 0, shaderLocation = 0 };
                attributes[1] = new WGPUVertexAttribute { format = WGPUVertexFormat.Float32x4, offset = 2 * sizeof(float), shaderLocation = 1 };
                attributes[2] = new WGPUVertexAttribute { format = WGPUVertexFormat.Float32x2, offset = 6 * sizeof(float), shaderLocation = 2 };

                var bufferLayout = new WGPUVertexBufferLayout
                {
                    stepMode = WGPUVertexStepMode.Vertex,
                    arrayStride = VertexStride,
                    attributeCount = 3,
                    attributes = attributes,
                };

                var blend = new WGPUBlendState
                {
                    color = new WGPUBlendComponent { operation = WGPUBlendOperation.Add, srcFactor = WGPUBlendFactor.One, dstFactor = WGPUBlendFactor.OneMinusSrcAlpha },
                    alpha = new WGPUBlendComponent { operation = WGPUBlendOperation.Add, srcFactor = WGPUBlendFactor.One, dstFactor = WGPUBlendFactor.OneMinusSrcAlpha },
                };
                var colorTarget = new WGPUColorTargetState { format = targetFormat, blend = &blend, writeMask = WGPUColorWriteMask_All };

                var fragment = new WGPUFragmentState
                {
                    module = shader,
                    entryPoint = new WGPUStringView { data = pFs, length = (nuint)fsEntry.Length },
                    targetCount = 1,
                    targets = &colorTarget,
                };

                var desc = new WGPURenderPipelineDescriptor
                {
                    layout = IntPtr.Zero, // auto layout
                    vertex = new WGPUVertexState
                    {
                        module = shader,
                        entryPoint = new WGPUStringView { data = pVs, length = (nuint)vsEntry.Length },
                        bufferCount = 1,
                        buffers = &bufferLayout,
                    },
                    primitive = new WGPUPrimitiveState
                    {
                        topology = WGPUPrimitiveTopology.TriangleList,
                        frontFace = WGPUFrontFace.CCW,
                        cullMode = WGPUCullMode.None,
                    },
                    multisample = new WGPUMultisampleState { count = 1, mask = 0xFFFFFFFF },
                    fragment = &fragment,
                };
                return wgpuDeviceCreateRenderPipeline(device, &desc);
            }
        }

        private (IntPtr Texture, IntPtr View) CreateTexture(byte[] pixels, int width, int height, WGPUTextureFormat format, int bytesPerPixel)
        {
            PerfTextures++;
            var texDesc = new WGPUTextureDescriptor
            {
                usage = WGPUTextureUsage.TextureBinding | WGPUTextureUsage.CopyDst,
                dimension = WGPUTextureDimension._2D,
                size = new WGPUExtent3D { width = (uint)width, height = (uint)height, depthOrArrayLayers = 1 },
                format = format,
                mipLevelCount = 1,
                sampleCount = 1,
            };
            IntPtr texture = wgpuDeviceCreateTexture(_ctx.Device, &texDesc);

            var dest = new WGPUTexelCopyTextureInfo { texture = texture, aspect = WGPUTextureAspect.All };
            var layout = new WGPUTexelCopyBufferLayout { offset = 0, bytesPerRow = (uint)(width * bytesPerPixel), rowsPerImage = (uint)height };
            var writeSize = new WGPUExtent3D { width = (uint)width, height = (uint)height, depthOrArrayLayers = 1 };
            fixed (byte* p = pixels)
                wgpuQueueWriteTexture(_ctx.Queue, &dest, p, (nuint)pixels.Length, &layout, &writeSize);

            return (texture, wgpuTextureCreateView(texture, IntPtr.Zero));
        }

        private (IntPtr Texture, IntPtr View) CreateRgbaTexture(byte[] rgba, int width, int height)
            => CreateTexture(rgba, width, height, WGPUTextureFormat.RGBA8Unorm, 4);

        // Bitmap pixels are sRGB-encoded. For an sRGB (display) target, use an sRGB texture so the
        // hardware decodes them to linear on sample, keeping one consistent linear space before the
        // single gamma encode on the final write. For the linear (test) target, pass them through.
        private (IntPtr Texture, IntPtr View) CreateImageTexture(byte[] rgba, int width, int height)
            => CreateTexture(rgba, width, height, _srgbOutput ? WGPUTextureFormat.RGBA8UnormSrgb : WGPUTextureFormat.RGBA8Unorm, 4);

        private (IntPtr Texture, IntPtr View) CreateR8Texture(byte[] r8, int width, int height)
            => CreateTexture(r8, width, height, WGPUTextureFormat.R8Unorm, 1);

        private IntPtr CreateSampledBindGroup(WGPUTextureFormat format, FillKind kind, IntPtr view, IntPtr sampler)
        {
            PerfBindGroups++;
            IntPtr layout = wgpuRenderPipelineGetBindGroupLayout(GetPipeline(format, kind), 0);
            var entries = stackalloc WGPUBindGroupEntry[2];
            entries[0] = new WGPUBindGroupEntry { binding = 0, textureView = view };
            entries[1] = new WGPUBindGroupEntry { binding = 1, sampler = sampler };
            var desc = new WGPUBindGroupDescriptor { layout = layout, entryCount = 2, entries = entries };
            return wgpuDeviceCreateBindGroup(_ctx.Device, &desc);
        }

        private IntPtr LinearSampler() => _linearSampler != IntPtr.Zero ? _linearSampler : (_linearSampler = CreateSampler(WGPUFilterMode.Linear));
        private IntPtr NearestSampler() => _nearestSampler != IntPtr.Zero ? _nearestSampler : (_nearestSampler = CreateSampler(WGPUFilterMode.Nearest));

        private IntPtr CreateSampler(WGPUFilterMode filter)
        {
            var desc = new WGPUSamplerDescriptor
            {
                addressModeU = WGPUAddressMode.ClampToEdge,
                addressModeV = WGPUAddressMode.ClampToEdge,
                addressModeW = WGPUAddressMode.ClampToEdge,
                magFilter = filter,
                minFilter = filter,
                mipmapFilter = WGPUMipmapFilterMode.Nearest,
                lodMinClamp = 0f,
                lodMaxClamp = 32f,
                compare = WGPUCompareFunction.Undefined,
                maxAnisotropy = 1,
            };
            return wgpuDeviceCreateSampler(_ctx.Device, &desc);
        }

        // ---- gradient ramp ----

        private static byte[] BuildGradientRamp(GradientStop[] stops)
        {
            var ramp = new byte[GradientRampTexels * 4];
            if (stops.Length == 0) return ramp;

            for (int i = 0; i < GradientRampTexels; i++)
            {
                float t = i / (float)(GradientRampTexels - 1);
                RgbaColor c = SampleStops(stops, t);
                ramp[i * 4 + 0] = ToByte(c.R);
                ramp[i * 4 + 1] = ToByte(c.G);
                ramp[i * 4 + 2] = ToByte(c.B);
                ramp[i * 4 + 3] = ToByte(c.A);
            }
            return ramp;
        }

        private static RgbaColor SampleStops(GradientStop[] stops, float t)
        {
            if (t <= stops[0].Offset) return stops[0].Color;
            if (t >= stops[^1].Offset) return stops[^1].Color;
            for (int i = 1; i < stops.Length; i++)
            {
                if (t <= stops[i].Offset)
                {
                    GradientStop a = stops[i - 1], b = stops[i];
                    float span = b.Offset - a.Offset;
                    float f = span > 0f ? (t - a.Offset) / span : 0f;
                    return Lerp(a.Color, b.Color, f);
                }
            }
            return stops[^1].Color;
        }

        private static RgbaColor Lerp(RgbaColor a, RgbaColor b, float f)
            => new(a.R + (b.R - a.R) * f, a.G + (b.G - a.G) * f, a.B + (b.B - a.B) * f, a.A + (b.A - a.A) * f);

        private static byte ToByte(float v) => (byte)Math.Clamp((int)MathF.Round(v * 255f), 0, 255);

        // ---- helpers ----

        private static void Bounds(Vector2[] points, out Vector2 min, out Vector2 size)
        {
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            foreach (Vector2 p in points)
            {
                minX = MathF.Min(minX, p.X); minY = MathF.Min(minY, p.Y);
                maxX = MathF.Max(maxX, p.X); maxY = MathF.Max(maxY, p.Y);
            }
            min = new Vector2(minX, minY);
            size = new Vector2(maxX - minX, maxY - minY);
        }

        private Scissor DeviceBounds(Rect clip, Matrix3x2 world, int width, int height)
        {
            Vector2 p0 = Vector2.Transform(new Vector2(clip.X, clip.Y), world);
            Vector2 p1 = Vector2.Transform(new Vector2(clip.X + clip.Width, clip.Y), world);
            Vector2 p2 = Vector2.Transform(new Vector2(clip.X + clip.Width, clip.Y + clip.Height), world);
            Vector2 p3 = Vector2.Transform(new Vector2(clip.X, clip.Y + clip.Height), world);

            float minX = MathF.Min(MathF.Min(p0.X, p1.X), MathF.Min(p2.X, p3.X));
            float minY = MathF.Min(MathF.Min(p0.Y, p1.Y), MathF.Min(p2.Y, p3.Y));
            float maxX = MathF.Max(MathF.Max(p0.X, p1.X), MathF.Max(p2.X, p3.X));
            float maxY = MathF.Max(MathF.Max(p0.Y, p1.Y), MathF.Max(p2.Y, p3.Y));

            int ix = (int)MathF.Floor(minX);
            int iy = (int)MathF.Floor(minY);
            int iw = (int)MathF.Ceiling(maxX) - ix;
            int ih = (int)MathF.Ceiling(maxY) - iy;
            // Scissors are absolute device coords, clamped to the current target's absolute extent.
            return Intersect(new Scissor((int)_devOX, (int)_devOY, width, height), new Scissor(ix, iy, iw, ih));
        }

        private static Scissor Intersect(Scissor a, Scissor b)
        {
            int x = Math.Max(a.X, b.X);
            int y = Math.Max(a.Y, b.Y);
            int r = Math.Min(a.X + a.W, b.X + b.W);
            int bot = Math.Min(a.Y + a.H, b.Y + b.H);
            return new Scissor(x, y, Math.Max(0, r - x), Math.Max(0, bot - y));
        }

        private static int AlignUp(int value, int alignment) => (value + alignment - 1) / alignment * alignment;
    }
}
