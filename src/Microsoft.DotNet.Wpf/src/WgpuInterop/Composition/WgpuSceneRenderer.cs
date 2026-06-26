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
        private const WGPUTextureFormat ReadbackFormat = WGPUTextureFormat.RGBA8Unorm;
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

            public LayerPass(IntPtr targetView, bool clearTransparent, RgbaColor clearColor, DrawData data, WGPUTextureFormat format)
            {
                TargetView = targetView; ClearTransparent = clearTransparent; ClearColor = clearColor; Data = data; Format = format;
            }

            public LayerPass(IntPtr targetView, WGPUTextureFormat format, List<Draw3D> models3D, IntPtr depthView)
            {
                TargetView = targetView; Format = format; Models3D = models3D; DepthView = depthView;
                ClearTransparent = true; ClearColor = default; Data = new DrawData();
            }
        }

        public byte[] RenderToRgba(SceneVisual root, int width, int height, RgbaColor background)
        {
            try
            {
                var plan = new List<LayerPass>();
                var mainData = new DrawData();
                CollectVisual(root, Matrix3x2.Identity, 1.0, new Scissor(0, 0, width, height), mainData, plan, width, height, ReadbackFormat);

                IntPtr device = _ctx.Device;
                int bytesPerRow = AlignUp(width * 4, 256);
                ulong readbackSize = (ulong)bytesPerRow * (ulong)height;
                var texDesc = new WGPUTextureDescriptor
                {
                    usage = WGPUTextureUsage.RenderAttachment | WGPUTextureUsage.CopySrc,
                    dimension = WGPUTextureDimension._2D,
                    size = new WGPUExtent3D { width = (uint)width, height = (uint)height, depthOrArrayLayers = 1 },
                    format = ReadbackFormat,
                    mipLevelCount = 1,
                    sampleCount = 1,
                };
                IntPtr targetTex = wgpuDeviceCreateTexture(device, &texDesc);
                IntPtr targetView = wgpuTextureCreateView(targetTex, IntPtr.Zero);
                IntPtr readback = _ctx.CreateBuffer(readbackSize, WGPUBufferUsage.CopyDst | WGPUBufferUsage.MapRead);

                IntPtr atlasView = EnsureAtlasView(AnyText(mainData, plan));
                IntPtr encoder = wgpuDeviceCreateCommandEncoder(device, IntPtr.Zero);

                foreach (LayerPass lp in plan) ExecutePass(encoder, lp, atlasView);
                ExecutePass(encoder, new LayerPass(targetView, false, background, mainData, ReadbackFormat), atlasView);

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
                var plan = new List<LayerPass>();
                var mainData = new DrawData();
                CollectVisual(root, Matrix3x2.Identity, 1.0, new Scissor(0, 0, width, height), mainData, plan, width, height, format);

                IntPtr atlasView = EnsureAtlasView(AnyText(mainData, plan));
                IntPtr encoder = wgpuDeviceCreateCommandEncoder(_ctx.Device, IntPtr.Zero);

                foreach (LayerPass lp in plan) ExecutePass(encoder, lp, atlasView);
                ExecutePass(encoder, new LayerPass(view, false, background, mainData, format), atlasView);

                IntPtr commandBuffer = wgpuCommandEncoderFinish(encoder, IntPtr.Zero);
                IntPtr* cmds = stackalloc IntPtr[1];
                cmds[0] = commandBuffer;
                wgpuQueueSubmit(_ctx.Queue, 1, cmds);

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
                ExecutePass3D(encoder, lp.TargetView, lp.DepthView, lp.Format, models);
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
                RecordDraws(pass, lp.Format, vbuf, ibuf, lp.Data, atlasBindGroup);
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

            // Render the subtree at full opacity into its own texture.
            var (_, layerView) = CreateLayerTexture(width, height);
            var subData = new DrawData();
            EmitSubtree(v, world, 1.0, clip, subData, plan, width, height, ReadbackFormat);
            plan.Add(new LayerPass(layerView, true, default, subData, ReadbackFormat));

            float groupOpacity = (float)(inheritedOpacity * vOpacity);

            // Arbitrary clip geometry masks the layer (takes precedence over effects).
            if (v.ClipGeometry is { } clipGeom)
            {
                PathGeometry deviceClip = TransformGeometry(clipGeom, world);
                byte[] maskBytes = PathRasterizer.RasterizeInto(deviceClip, width, height);
                var (maskTex, maskView) = CreateR8Texture(maskBytes, width, height);
                DeferRelease(() => { wgpuTextureViewRelease(maskView); wgpuTextureRelease(maskTex); });
                EmitClipQuad(outData, outFormat, layerView, maskView, groupOpacity, clip, width, height);
                return;
            }

            // Opacity mask: a brush's alpha modulates the layer per pixel (same
            // masked composite as a geometry clip, but the mask is brush alpha).
            if (v.OpacityMask is { } opacityMask)
            {
                byte[] maskBytes = RasterizeOpacityMask(opacityMask, world, width, height);
                var (maskTex, maskView) = CreateR8Texture(maskBytes, width, height);
                DeferRelease(() => { wgpuTextureViewRelease(maskView); wgpuTextureRelease(maskTex); });
                EmitClipQuad(outData, outFormat, layerView, maskView, groupOpacity, clip, width, height);
                return;
            }

            switch (v.Effect)
            {
                case BlurEffect blur:
                {
                    IntPtr blurred = BlurLayer(layerView, blur.Radius, plan, width, height);
                    EmitFullScreenQuad(outData, outFormat, FillKind.Layer, blurred, 1f, 1f, 1f, groupOpacity, 0f, 0f, clip, width, height);
                    break;
                }
                case DropShadowEffect ds:
                {
                    IntPtr shadowBlur = BlurLayer(layerView, ds.BlurRadius, plan, width, height);
                    float shadowAlpha = (float)Math.Clamp(ds.Color.A * groupOpacity, 0.0, 1.0);
                    EmitFullScreenQuad(outData, outFormat, FillKind.Shadow, shadowBlur,
                        ds.Color.R, ds.Color.G, ds.Color.B, shadowAlpha, (float)ds.OffsetX, (float)ds.OffsetY, clip, width, height);
                    EmitFullScreenQuad(outData, outFormat, FillKind.Layer, layerView, 1f, 1f, 1f, groupOpacity, 0f, 0f, clip, width, height);
                    break;
                }
                default: // opacity-only layer
                    EmitFullScreenQuad(outData, outFormat, FillKind.Layer, layerView, 1f, 1f, 1f, groupOpacity, 0f, 0f, clip, width, height);
                    break;
            }
        }

        // Two-pass separable Gaussian blur of a layer texture; returns the
        // blurred texture's view. Each pass is a full-target draw added to the plan.
        private IntPtr BlurLayer(IntPtr input, double radius, List<LayerPass> plan, int width, int height)
        {
            float sigma = (float)Math.Max(0.5, radius);
            int taps = Math.Clamp((int)Math.Ceiling(radius * 3.0), 1, 48);

            var (_, hView) = CreateLayerTexture(width, height);
            var hData = new DrawData();
            EmitBlurQuad(hData, input, 1f / width, 0f, sigma, taps, width, height);
            plan.Add(new LayerPass(hView, true, default, hData, ReadbackFormat));

            var (_, vView) = CreateLayerTexture(width, height);
            var vData = new DrawData();
            EmitBlurQuad(vData, hView, 0f, 1f / height, sigma, taps, width, height);
            plan.Add(new LayerPass(vView, true, default, vData, ReadbackFormat));

            return vView;
        }

        private void EmitBlurQuad(DrawData data, IntPtr inputView, float stepX, float stepY, float sigma, int taps, int width, int height)
        {
            IntPtr bindGroup = CreateSampledBindGroup(ReadbackFormat, FillKind.Blur, inputView, LinearSampler());
            DeferRelease(() => wgpuBindGroupRelease(bindGroup));

            // Blur params ride in the (constant) vertex colour: (stepX, stepY, sigma, taps).
            var clip = new Scissor(0, 0, width, height);
            uint baseVertex = (uint)(data.Verts.Count / FloatsPerVertex);
            AddVertex(data.Verts, ToNdc(new Vector2(0, 0), width, height), stepX, stepY, sigma, taps, 0f, 0f);
            AddVertex(data.Verts, ToNdc(new Vector2(width, 0), width, height), stepX, stepY, sigma, taps, 1f, 0f);
            AddVertex(data.Verts, ToNdc(new Vector2(width, height), width, height), stepX, stepY, sigma, taps, 1f, 1f);
            AddVertex(data.Verts, ToNdc(new Vector2(0, height), width, height), stepX, stepY, sigma, taps, 0f, 1f);

            uint firstIndex = (uint)data.Indices.Count;
            foreach (uint li in new uint[] { 0, 1, 2, 0, 2, 3 })
                data.Indices.Add(baseVertex + li);
            data.Draws.Add(new DrawItem(firstIndex, 6, clip, FillKind.Blur, bindGroup));
        }

        private void EmitSubtree(SceneVisual v, Matrix3x2 world, double accOpacity, Scissor clip,
            DrawData outData, List<LayerPass> plan, int width, int height, WGPUTextureFormat format)
        {
            foreach (DrawingPrimitive primitive in v.Content)
            {
                if (primitive is Viewport3DDraw viewport)
                    Emit3DViewport(viewport, accOpacity, clip, outData, plan, width, height, format);
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

        // Composites a layer masked by a clip-coverage texture (fs_clip), as a
        // full-target quad, at the group opacity.
        private void EmitClipQuad(DrawData data, WGPUTextureFormat format, IntPtr layerView, IntPtr maskView,
            float groupOpacity, Scissor clip, int width, int height)
        {
            if (clip.IsEmpty) return;
            IntPtr bindGroup = CreateClipBindGroup(format, layerView, maskView, NearestSampler());
            DeferRelease(() => wgpuBindGroupRelease(bindGroup));

            uint baseVertex = (uint)(data.Verts.Count / FloatsPerVertex);
            AddVertex(data.Verts, ToNdc(new Vector2(0, 0), width, height), 1f, 1f, 1f, groupOpacity, 0f, 0f);
            AddVertex(data.Verts, ToNdc(new Vector2(width, 0), width, height), 1f, 1f, 1f, groupOpacity, 1f, 0f);
            AddVertex(data.Verts, ToNdc(new Vector2(width, height), width, height), 1f, 1f, 1f, groupOpacity, 1f, 1f);
            AddVertex(data.Verts, ToNdc(new Vector2(0, height), width, height), 1f, 1f, 1f, groupOpacity, 0f, 1f);

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

        private (IntPtr Texture, IntPtr View) CreateLayerTexture(int width, int height)
        {
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
            IntPtr view = wgpuTextureCreateView(tex, IntPtr.Zero);
            DeferRelease(() => { wgpuTextureViewRelease(view); wgpuTextureRelease(tex); });
            return (tex, view);
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
                    var (tex, view) = CreateRgbaTexture(img.PixelsRgba, img.PixelWidth, img.PixelHeight);
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
            EmitMask(PathRasterizer.Rasterize(coverageGeometry), brush, world, opacity, clip, width, height, format, data);
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
        private static byte[] RasterizeOpacityMask(Brush brush, Matrix3x2 world, int width, int height)
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
                            var p = new Vector2(x + 0.5f, y + 0.5f);
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
                            float dx = rx > 0f ? (x + 0.5f - c.X) / rx : 0f;
                            float dy = ry > 0f ? (y + 0.5f - c.Y) / ry : 0f;
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

        private static Vector2 ToNdc(Vector2 devicePoint, int width, int height)
            => new(devicePoint.X / width * 2f - 1f, 1f - devicePoint.Y / height * 2f);

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

            vbuf = _ctx.CreateBuffer((ulong)vbytes.Length, WGPUBufferUsage.Vertex | WGPUBufferUsage.CopyDst);
            ibuf = _ctx.CreateBuffer((ulong)ibytes.Length, WGPUBufferUsage.Index | WGPUBufferUsage.CopyDst);
            _ctx.WriteBuffer(vbuf, vbytes);
            _ctx.WriteBuffer(ibuf, ibytes);

            IntPtr vbufLocal = vbuf, ibufLocal = ibuf;
            DeferRelease(() => wgpuBufferRelease(vbufLocal));
            DeferRelease(() => wgpuBufferRelease(ibufLocal));
        }

        private void RecordDraws(IntPtr pass, WGPUTextureFormat format, IntPtr vbuf, IntPtr ibuf, DrawData data, IntPtr atlasBindGroup)
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
                wgpuRenderPassEncoderSetScissorRect(pass, (uint)d.Clip.X, (uint)d.Clip.Y, (uint)d.Clip.W, (uint)d.Clip.H);
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

        private (IntPtr Texture, IntPtr View) CreateR8Texture(byte[] r8, int width, int height)
            => CreateTexture(r8, width, height, WGPUTextureFormat.R8Unorm, 1);

        private IntPtr CreateSampledBindGroup(WGPUTextureFormat format, FillKind kind, IntPtr view, IntPtr sampler)
        {
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

        private static Scissor DeviceBounds(Rect clip, Matrix3x2 world, int width, int height)
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
            return Intersect(new Scissor(0, 0, width, height), new Scissor(ix, iy, iw, ih));
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
