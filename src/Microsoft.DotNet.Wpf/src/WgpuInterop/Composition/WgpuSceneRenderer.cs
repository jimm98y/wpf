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
using System.Runtime.InteropServices;
using System.Text;
using static Microsoft.Wpf.Interop.WebGpu.Wgpu;

namespace Microsoft.Wpf.Interop.WebGpu.Composition
{
    internal sealed unsafe partial class WgpuSceneRenderer : IDisposable
    {
        private const int FloatsPerVertex = 12;           // pos.xy, color.rgba, uv.xy, shape params.xyzw (last 4 used only by fs_shape)
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

        // True when the current target is a transparent surface (Mica backdrop / layered popup). Text
        // then uses plain linear grayscale AA -- the sRGB text-gamma (cov^(1/2.2)) is tuned for blending
        // over an OPAQUE destination in the framebuffer, but on a transparent target the coverage becomes
        // the alpha that CoreAnimation later composites over the backdrop, so the gamma distorts the edges
        // (WPF likewise drops ClearType/text-gamma on layered windows).
        private static readonly bool s_runTrace =
            Environment.GetEnvironmentVariable("WPF_RUN_TRACE") == "1";
        private static int _runsTraced;

        private static readonly bool s_flagTrace =
            Environment.GetEnvironmentVariable("WPF_FLAG_TRACE") == "1";
        private static bool s_flagTraced;

        private bool _transparentTarget;

        /// <summary>Whether the run being drawn asked for symmetric smoothing (see the face's
        /// 'gasp'); it costs vertical samples, so it is per run and folded into the mask key.</summary>
        private bool _symmetricSmoothing;

        /// <summary>Vertical samples to use when the face asks for symmetric smoothing.
        /// <para>TWO, swept (WPF_SYM_ROWS): the parity suite totals 4,140,193 with the flag ignored,
        /// 4,111,685 at two samples and 4,173,135 at four, and the size that actually changes there
        /// -- 20ppem, where Segoe UI's gasp turns symmetric smoothing back on -- goes 225,559 ->
        /// 198,640 -> 209,354. Four over-softens. The grey path's four samples are for unhinted
        /// shapes and are not the right number here.</para></summary>
        private static readonly int SymmetricRows =
            int.TryParse(Environment.GetEnvironmentVariable("WPF_SYM_ROWS"), out int sr) ? sr : 2;

        // Per-frame perf counters (diagnostics): reset + read by the sink each frame.
        internal static int PerfTextures, PerfBindGroups, PerfCoverage, PerfReadbacks, PerfLayers, PerfLayerHits, PerfLayerMiss;
        /// <summary>Draws routed to the local-space (resampled) coverage cache rather than the exact device-space path.</summary>
        internal static int PerfLocalCoverage;

        /// <summary>
        /// Draws recorded this frame: <see cref="PerfDrawItems"/> counts what the scene asked for,
        /// <see cref="PerfDrawCalls"/> the drawIndexed calls that carried them. The ratio is what
        /// batching buys, and a scene where they are equal is one where nothing could be merged.
        /// </summary>
        internal static int PerfDrawItems, PerfDrawCalls;

        /// <summary>Render passes encoded this frame. A pass costs an attachment load and store,
        /// which is why several draws in one beats one draw in each.</summary>
        internal static int PerfPasses;

        /// <summary>Edge textures CREATED this frame -- the segment lists coverage masks and strokes
        /// upload. A settled scene should create none: see RentEdgeTexture.</summary>
        internal static int PerfEdgeTextures;

        /// <summary>Coverage-mask textures CREATED this frame, as opposed to taken from the pool.</summary>
        internal static int PerfMaskTextures;

        /// <summary>
        /// Native bind-group-layout acquisitions since the process started.
        /// </summary>
        /// <remarks>
        /// A LEAK counter, which is why it is a lifetime total and deliberately NOT cleared by
        /// <see cref="PerfReset"/> like the per-frame ones next to it: every acquisition used to be a
        /// leaked reference, so what matters is whether the number keeps climbing while an
        /// application renders, not what it was during one frame. In steady state it must not move.
        /// </remarks>
        internal static int PerfLayoutAcquires;
        internal static long PerfCollectTicks, PerfEncodeTicks, PerfSubmitTicks, PerfHashTicks;
        internal static long PerfCollectAlloc, PerfExecAlloc;

        internal static void PerfReset() { PerfTextures = PerfBindGroups = PerfCoverage = PerfReadbacks = PerfLayers = PerfLayerHits = PerfLayerMiss = PerfLocalCoverage = 0; PerfDrawItems = PerfDrawCalls = PerfPasses = PerfEdgeTextures = PerfMaskTextures = 0; PerfCollectTicks = PerfEncodeTicks = PerfSubmitTicks = PerfHashTicks = 0; }

        // Coverage-mask cache: text/solid shapes are rasterized to an R8 mask + uploaded as a
        // texture + bind group EVERY frame, which dominates cost for largely-static UI. Cache those
        // GPU resources keyed by the device-space geometry so unchanged content is reused (the colour
        // tint lives in the quad vertices, so the same glyph in any colour shares one cached mask).
        private sealed class CachedMask
        {
            public IntPtr Tex, View, BindGroup;
            // Subpixel text is drawn twice over one quad, and an auto-layout bind group belongs to one
            // pipeline, so the second pass needs its own. Null for an ordinary grey mask.
            public IntPtr BindGroupAdd;
            public bool Subpixel;
            // An auto-layout bind group is exclusive to ONE pipeline, and CompositingMode.SourceCopy
            // is a separate pipeline (no blend) for the same FillKind. So a cached mask needs a group
            // per blend variant; this one is built on first source-copy use and usually stays null.
            public IntPtr BindGroupCopy;
            public IntPtr Sampler;               // to rebuild BindGroupCopy with the same filtering
            public WGPUTextureFormat Format;     // ... and against the same pass format
            public int Ox, Oy, W, H;
            public int LastFrame;
            /// <summary>Whether Tex came from RentMaskTexture, and so may go back to that pool.
            /// <para>False for the masks the CPU path uploads (CreateR8Texture / CreateRgbaTexture):
            /// those carry CopyDst|TextureBinding and NOT RenderAttachment, because nothing ever draws
            /// into them. Handing one back to the pool let a later coverage pass rent it as a render
            /// target, and wgpu rejects that in a way that ABORTS the process -- it panics across the
            /// FFI boundary rather than throwing. Intermittent because the sizes have to match exactly
            /// and the entry has to have been evicted first.</para></summary>
            public bool Pooled;
        }

        // The cached mask's bind group for the compositing mode in effect at this draw.
        private IntPtr MaskBindGroup(CachedMask c)
        {
            if (!_srcCopy) return c.BindGroup;
            if (c.BindGroupCopy == IntPtr.Zero)
                c.BindGroupCopy = CreateSampledBindGroup(c.Format, FillKind.Text, c.View, c.Sampler, sourceCopy: true);
            return c.BindGroupCopy;
        }
        private readonly Dictionary<long, CachedMask> _maskCache = new();
        // Gradient ramp textures keyed by their stops: a 256x1 RGBA ramp was rebuilt + re-uploaded EVERY
        // frame per gradient fill (each an unreclaimable Metal command buffer on wgpu-native), so a
        // gradient-heavy page bursts past the 4096 limit. Cached across frames like masks; evicted when unused.
        private readonly Dictionary<long, (IntPtr Tex, IntPtr View, long LastFrame)> _rampCache = new();
        // Local-space coverage masks for gradient/image BRUSH fills (EmitGpuGradientMask/EmitGpuImageMask):
        // these were re-rasterized to a fresh R8 texture EVERY frame per fill (each an unreclaimable Metal
        // command buffer → a gradient/image-heavy page bursts past the 4096 limit). The mask is in the
        // geometry's LOCAL space (transform-independent), so it caches by geometry hash like solid masks.
        private readonly Dictionary<long, (IntPtr Tex, IntPtr View, int Ox, int Oy, int W, int H, long LastFrame)> _covCache = new();
        // Per-fill brush-param UNIFORM buffers, keyed by their 64-byte contents (were a fresh
        // mappedAtCreation buffer every frame per gradient/image fill).
        private readonly Dictionary<long, (IntPtr Buf, long LastFrame)> _uniCache = new();
        // Image-brush textures, keyed by the source pixel array's identity (were re-uploaded every frame).
        private readonly Dictionary<long, (IntPtr Tex, IntPtr View, long LastFrame)> _imgCache = new();
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
            // Whether MaskTex came from the pool, and so may go back to it. Same trap as
            // CachedMask.Pooled: the CPU fallbacks here upload a sampling-only texture.
            public bool MaskPooled;
            public int Rx, Ry, Rw, Rh;
            public int Mode;                   // 0=opacity 1=blur 2=shadow 3=clip-geometry 4=opacity-mask
            public RgbaColor ShadowColor;
            public int OffX, OffY;
            public int LastFrame;
            // A full-target layer (nested 3D/clip/mask) is rendered with region origin (0,0), so its
            // content sits at ABSOLUTE device coordinates in the texture rather than region-local. To
            // reuse it at a new scroll position we composite it shifted by how far the layer's world
            // translation has moved since it was rendered. Region-sized layers instead track position
            // via Rx/Ry (their content is already region-local). See the cache-hit path.
            public bool FullTarget;
            public float OrigTX, OrigTY;       // world translation when rendered (full-target layers)
        }
        private readonly Dictionary<long, CachedLayer> _layerCache = new();


        /// <summary>Begin a logical composition frame (drives coverage-cache eviction).</summary>
        public void BeginFrame() { _frameId++; BeginFrame3D(); }

        /// <summary>End the frame: return pooled layers and evict stale coverage-cache entries.</summary>
        public void EndFrame()
        {
            EvictStaleMeshes();

            // The animation detector holds one small entry per geometry hash ever drawn under a
            // rotation. Nothing else would ever remove them, so a long session accumulates one
            // per shape forever; anything untouched for a few frames is not animating by
            // definition and costs only 3 frames to re-detect.
            if (_linearChurn.Count > 0)
            {
                List<long>? deadC = null;
                foreach (KeyValuePair<long, (long Linear, int Frame, int Churn)> kv in _linearChurn)
                    if (kv.Value.Frame < _frameId - 4) (deadC ??= new List<long>()).Add(kv.Key);
                if (deadC != null) foreach (long k in deadC) _linearChurn.Remove(k);
            }

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
                    if (c.MaskTex != IntPtr.Zero)
                    {
                        if (c.MaskPooled) ReturnMaskTexture(c.MaskTex, c.MaskView, c.Rw, c.Rh);
                        else { wgpuTextureViewRelease(c.MaskView); wgpuTextureRelease(c.MaskTex); }
                    }
                }
                if (deadL != null) foreach (long k in deadL) _layerCache.Remove(k);
            }

            if (_rampCache.Count > 0)
            {
                List<long>? deadR = null;
                foreach (KeyValuePair<long, (IntPtr Tex, IntPtr View, long LastFrame)> kv in _rampCache)
                {
                    if (kv.Value.LastFrame >= _frameId - 60) continue;
                    (deadR ??= new List<long>()).Add(kv.Key);
                    wgpuTextureViewRelease(kv.Value.View);
                    wgpuTextureRelease(kv.Value.Tex);
                }
                if (deadR != null) foreach (long k in deadR) _rampCache.Remove(k);
            }

            if (_covCache.Count > 0)
            {
                List<long>? deadC = null;
                foreach (KeyValuePair<long, (IntPtr Tex, IntPtr View, int Ox, int Oy, int W, int H, long LastFrame)> kv in _covCache)
                {
                    if (kv.Value.LastFrame >= _frameId - 60) continue;
                    (deadC ??= new List<long>()).Add(kv.Key);
                    ReturnMaskTexture(kv.Value.Tex, kv.Value.View, kv.Value.W, kv.Value.H);
                }
                if (deadC != null) foreach (long k in deadC) _covCache.Remove(k);
            }

            if (_imgCache.Count > 0)
            {
                List<long>? deadI = null;
                foreach (KeyValuePair<long, (IntPtr Tex, IntPtr View, long LastFrame)> kv in _imgCache)
                {
                    if (kv.Value.LastFrame >= _frameId - 60) continue;
                    (deadI ??= new List<long>()).Add(kv.Key);
                    wgpuTextureViewRelease(kv.Value.View);
                    wgpuTextureRelease(kv.Value.Tex);
                }
                if (deadI != null) foreach (long k in deadI) _imgCache.Remove(k);
            }

            if (_uniCache.Count > 0)
            {
                List<long>? deadU = null;
                foreach (KeyValuePair<long, (IntPtr Buf, long LastFrame)> kv in _uniCache)
                {
                    if (kv.Value.LastFrame >= _frameId - 60) continue;
                    (deadU ??= new List<long>()).Add(kv.Key);
                    wgpuBufferRelease(kv.Value.Buf);
                }
                if (deadU != null) foreach (long k in deadU) _uniCache.Remove(k);
            }

            if (_maskCache.Count == 0) return;
            List<long>? dead = null;
            foreach (KeyValuePair<long, CachedMask> kv in _maskCache)
            {
                // Masks are small (R8, geometry-sized); keep them across a scroll's phase
                // cycling (a given half-px phase recurs every few frames, not every frame).
                if (kv.Value.LastFrame >= _frameId - 60) continue;
                (dead ??= new List<long>()).Add(kv.Key);
                CachedMask c = kv.Value;
                wgpuBindGroupRelease(c.BindGroup);
                if (c.BindGroupAdd != IntPtr.Zero) wgpuBindGroupRelease(c.BindGroupAdd);
                if (c.BindGroupCopy != IntPtr.Zero) wgpuBindGroupRelease(c.BindGroupCopy);
                // Only a POOL texture goes back to the pool. The CPU path's masks carry no
                // RenderAttachment usage, and renting one out as a coverage target aborts the process.
                if (c.Pooled) ReturnMaskTexture(c.Tex, c.View, c.W, c.H);
                else { wgpuTextureViewRelease(c.View); wgpuTextureRelease(c.Tex); }
            }
            if (dead != null) foreach (long k in dead) _maskCache.Remove(k);
        }
        private const int GradientRampTexels = 256;

        private static string ShaderWgsl => ShaderSource.Get("Shader");

        // fs_clip needs a second texture (the clip mask), so it uses its own
        // shader module with a 3-entry bind group {layer, mask, sampler}.
        private static string ClipShaderWgsl => ShaderSource.Get("ClipShader");

        // GPU path rasterization: evaluates the SAME coverage math as PathRasterizer
        // (4 vertical subsample rows, exact horizontal span coverage per pixel) in a
        // fragment shader over a storage buffer of flattened edges, writing an R8
        // coverage mask. Per pixel and subsample row: the winding at the pixel's left
        // edge is the signed count of crossings at or left of it; the in-pixel
        // crossings (rarely more than 2) are insertion-sorted and walked to measure
        // the covered fraction exactly — pixel-identical AA to the CPU rasterizer.
        // Draw encoding: uv carries mask-local pixel coords; the (flat) vertex colour
        // carries (edgeCount, flags) — no uniform buffer needed.
        private static string CoverageShaderWgsl => ShaderSource.Get("CoverageShader");

        // GPU stroking (round join + round cap, solid): the stroke coverage is a signed
        // distance field around the flattened centre-line polyline — coverage =
        // clamp(0.5 + halfWidth - distanceToPolyline). Because point-to-segment distance
        // includes the segment endpoints, shared vertices round into round joins and open
        // ends round into round caps automatically, with no CPU stroke-to-fill. Non-round
        // joins/caps and dashes are not distance-field-expressible and stay on PathStroker.
        // Buffer: 2 vec2 per segment (a, b). Vertex colour: x = segCount, y = halfWidth.
        private static string StrokeShaderWgsl => ShaderSource.Get("StrokeShader");

        // Analytic closed-form shapes (ellipse, rectangle, rounded rectangle -- filled or stroked),
        // drawn straight into the frame with per-fragment AA. NO coverage texture and NO per-frame GPU
        // resource creation, so a continuously animated shape (a growing circle, a resizing card) costs
        // nothing to re-realize -- unlike the coverage-mask path, which cache-misses on every geometry
        // change and re-bakes a texture per frame. The vertex carries uv = local position relative to the
        // shape centre and prm = (halfX, halfY, cornerR, strokeHalf), ALL in the shape's local units:
        //   cornerR <  0  -> ellipse with radii (halfX, halfY)
        //   cornerR >= 0  -> rounded rectangle of half-extents (halfX, halfY) and corner radius cornerR
        //                    (cornerR == 0 is a sharp rectangle)
        //   strokeHalf < 0 -> filled;  strokeHalf >= 0 -> stroked ring/outline of that half-thickness
        // Distance is computed in local units and the AA width comes from fwidth(), so the result is
        // correct under ANY affine world transform (rotation, non-uniform scale) with no special-casing.
        private static string ShapeShaderWgsl => ShaderSource.Get("ShapeShader");

        // The gradient counterpart of ShapeShaderWgsl: the SAME analytic rounded-rect/ellipse SDF,
        // but the colour comes from the gradient ramp evaluated per fragment instead of a flat vertex
        // colour. Without this a gradient-filled rounded rect could not take the analytic path at all
        // (fs_shape only outputs solid * coverage) and fell back to a rasterized coverage mask baked at
        // the geometry's LOCAL resolution -- which the GPU then magnified by the world transform, so
        // corners visibly pixelated under a DPI scale or a zoom while solid-filled shapes and text
        // beside them stayed crisp. Being an SDF this is resolution-independent: exact at any zoom,
        // and it bakes no mask at all.
        //
        // uv is the local position relative to the shape's CENTRE (as in fs_shape), so the gradient
        // endpoints are pre-offset by that centre on the CPU and uv feeds brushT directly.
        // Bindings mirror BrushAlphaShaderWgsl's 3-entry layout (see CreateBrushBindGroup's else).
        private static string ShapeBrushShaderWgsl => ShaderSource.Get("ShapeBrushShader");

        // Analytic stroke drawn STRAIGHT into the frame (no baked coverage texture): the round-cap/round-
        // join solid-stroke distance field (min distance to the flattened centre-line segments, minus the
        // half-width) evaluated per fragment. Segments live in a per-frame BATCHED storage buffer shared by
        // all such strokes (bound per draw at the stroke's byte offset), so an animated/resizing stroke
        // costs nothing to re-realize -- unlike fs_stroke, which bakes the same SDF into a cached texture
        // that thrashes when the geometry changes every frame. Used only for modest segment counts (the
        // per-fragment loop is O(segments)); complex strokes stay on the cached-texture path. uv carries the
        // fragment's DEVICE-pixel position; prm = (segmentCount, halfWidthPx, _, _).
        private static string StrokeDrawShaderWgsl => ShaderSource.Get("StrokeDrawShader");

        // GPU brush evaluation: gradients are computed per pixel in the fragment shader
        // (sampling the same 256-texel ramp the tessellated gradient path uses) instead
        // of the CPU per-texel bake. fs_maskbrush composites GPU-rasterized coverage
        // with the brush (masked gradient fills); fs_brushalpha writes the brush's
        // alpha into an R8 target (gradient opacity masks). Params ride in a small
        // uniform buffer; localPos = rect.xy + uv * rect.zw reproduces the CPU
        // evaluator's pixel-center sampling exactly.
        private static string BrushShaderWgsl => ShaderSource.Get("BrushShader");

        // fs_brushalpha has a different binding set (no coverage texture), so it lives
        // in its own module for a clean auto-inferred pipeline layout.
        private static string BrushAlphaShaderWgsl => ShaderSource.Get("BrushAlphaShader");

        // GPU hit testing: the scene is re-walked into a visual-id buffer where each visual's
        // content coverage writes that visual's packed id (RGBA8 = id bytes; a=1 marks a hit).
        // Painter order means the topmost covering visual wins at each pixel (no blend, and
        // uncovered fragments discard); reading back the query pixel maps a device point to its
        // visual. A shared 1x1 white coverage texture is bound for solid/bounds quads.
        private static string IdShaderWgsl => ShaderSource.Get("IdShader");

        private enum FillKind { Solid, Textured, Text, Layer, Blur, Shadow, Clip, Coverage, MaskBrush, BrushAlpha, Id, MaskImage, Stroke, Shape, ShapeBrush, StrokeDraw, ShaderEffect, TextSubpixelMultiply, TextSubpixelAdd }

        private readonly WgpuContext _ctx;
        private readonly Dictionary<(WGPUTextureFormat, FillKind, bool SourceCopy), IntPtr> _pipelines = new();
        // Custom ShaderEffects each need their OWN module and pipeline, so they cannot live in
        // the (format, kind) cache above; they are keyed by the shader's identity instead.
        private readonly Dictionary<int, IntPtr> _effectModules = new();
        private readonly Dictionary<(WGPUTextureFormat, int), IntPtr> _effectPipelines = new();
        private readonly Text.IFont _font;
        private readonly Func<int, Text.IFont?>? _styledFont;
        private readonly Dictionary<int, Text.IFont> _styledCache = new();
        // Faces resolved by family and style, e.g. "Consolas|1". A run names the family it wants;
        // without this every one of them was drawn in the single face loaded at construction.
        private readonly Dictionary<string, Text.IFont> _familyCache = new();

        // WGPU_TRACE_TEXT=1 reports every glyph run the renderer is handed and what became of it.
        // A run that Graphics recorded but that never reaches the screen is either culled here or
        // never arrives, and only the renderer can tell those two apart.
        private static readonly bool s_traceText =
            Environment.GetEnvironmentVariable("WGPU_TRACE_TEXT") == "1";
        private readonly Text.ITextShaper _shaper;
        private readonly Text.GlyphAtlas _glyphAtlas = new();
        private readonly List<Text.ShapedGlyph> _shapeScratch = new();
        private IntPtr _shaderModule;
        private IntPtr _clipShaderModule;
        private IntPtr _coverageShaderModule;
        private IntPtr _brushShaderModule;
        private IntPtr _brushAlphaShaderModule;
        private IntPtr _idShaderModule;
        private IntPtr _strokeShaderModule;
        private IntPtr _shapeShaderModule;
        private IntPtr _shapeBrushShaderModule;
        private IntPtr _strokeDrawShaderModule;
        private IntPtr _whiteTex, _whiteView;   // shared 1x1 white coverage (solid/bounds id quads)

        // GPU path rasterization is the default; WPF_WEBGPU_CPU_RASTER=1 restores the
        // CPU scanline rasterizer (A/B comparison, driver-bug escape hatch). Settable (not readonly)
        // so the sink can force the CPU path on adapters that can't run the GPU rasterizer's fragment
        // shader — notably OpenGL ES / ANGLE, where max_storage_buffers_per_shader_stage is 0 and
        // creating the coverage pipeline (a fragment storage buffer of edges) panics wgpu.
        internal static bool s_gpuRaster =
            Environment.GetEnvironmentVariable("WPF_WEBGPU_CPU_RASTER") != "1";
        internal static readonly bool s_cpuRasterExplicit =
            Environment.GetEnvironmentVariable("WPF_WEBGPU_CPU_RASTER") != null;

        // WPF_WEBGPU_LOCAL_COVERAGE_CACHE=1: rasterize solid coverage in the geometry's OWN space and
        // apply the world transform when COMPOSITING the quad - the same bargain as WPF's
        // RenderTransform / BitmapCache, where content is rasterized once and the GPU transforms it.
        // The default path bakes the world transform INTO the mask (crisper: every pixel is
        // rasterized at its final device orientation), but then the cache key must contain the
        // transform's linear part AND the sub-pixel phase, so an animating rotation/scale misses
        // EVERY mask EVERY frame (measured: ~40 glyph masks/frame at ~122us).
        // Rasterizing locally drops both from the key, so masks survive any transform change; the
        // cost is that resolution is fixed at cache time, so scaling up past it goes soft (exactly
        // WPF's BitmapCache/RenderAtScale tradeoff) and rotation is bilinear-filtered rather than
        // analytically anti-aliased.
        // See the gate in EmitFill: the analytic gradient-shape path is correct per-pass but its
        // bind group is bound to the emit-time pipeline, so multi-format scenes can abort.
        private static readonly bool s_shapeBrush =
            Environment.GetEnvironmentVariable("WPF_WEBGPU_SHAPE_BRUSH") == "1";

        // ON by default, set WPF_WEBGPU_LOCAL_COVERAGE_CACHE=0 to disable. It was opt-in while it
        // cost fidelity -- it engaged for axis-aligned content too, where the device-space path
        // already caches by integer offset + half-pixel phase and the local-space mask only added
        // bilinear blur (up to 87 levels on thin strokes). Now that EmitCoverageMask gates it to
        // rotated/skewed transforms the render baselines are pixel-exact with it on, and it is the
        // only thing that caches a rotating visual at all: the device key holds the exact linear
        // part, so a spinner misses every frame. Measured by WgpuInterop.LocalCacheProbe --
        // 60 rasterizations over 60 frames of a 1-degree-per-frame spin become 1.
        private static readonly bool s_localCoverageCache =
            Environment.GetEnvironmentVariable("WPF_WEBGPU_LOCAL_COVERAGE_CACHE") != "0";

        // Shapes whose linear transform is CHANGING frame to frame -- keyed by geometry hash,
        // holding the last linear part seen and how many consecutive frames it changed for.
        private readonly Dictionary<long, (long Linear, int Frame, int Churn)> _linearChurn = new();
        // Frames of continuous change before a shape is treated as animating. 3 keeps a static
        // rotated visual on the exact device path forever (it never changes, so Churn stays 0)
        // while a spinner pays only its first few frames at full price.
        private const int ChurnFramesToCache = 3;

        /// <summary>
        /// Is this shape's linear transform ANIMATING? The local-space cache resamples its mask
        /// through the world transform, which costs real fidelity on a rotation (measured at 59
        /// levels on the transform-rotate baseline), so it is only worth paying when the exact
        /// device-space mask would be thrown away next frame anyway. A visual sitting still at
        /// 30 degrees is rasterized once and reused forever by the device path -- there is
        /// nothing to save there, and no reason to blur it.
        /// </summary>
        private bool IsLinearAnimating(long shapeHash, Matrix3x2 world)
        {
            long lin = BitConverter.SingleToInt32Bits(world.M11);
            lin = lin * 31 + BitConverter.SingleToInt32Bits(world.M12);
            lin = lin * 31 + BitConverter.SingleToInt32Bits(world.M21);
            lin = lin * 31 + BitConverter.SingleToInt32Bits(world.M22);

            if (!_linearChurn.TryGetValue(shapeHash, out (long Linear, int Frame, int Churn) e)
                || e.Frame < _frameId - 2)          // a gap in the animation restarts the count
            {
                _linearChurn[shapeHash] = (lin, _frameId, 0);
                return false;
            }
            // Several draws of one shape within a frame must not read as churn.
            if (e.Frame == _frameId) return e.Churn >= ChurnFramesToCache;

            int churn = lin == e.Linear ? 0 : e.Churn + 1;
            _linearChurn[shapeHash] = (lin, _frameId, churn);
            return churn >= ChurnFramesToCache;
        }

        /// <summary>
        /// True when the linear part maps axis-aligned boxes to axis-aligned boxes: a scale/flip
        /// (0 or 180 degrees) or a quarter turn. Coverage for these rasterizes crisply in device
        /// space, so they do not need -- and are hurt by -- the local-space cache.
        /// </summary>
        private static bool IsAxisAligned(Matrix3x2 m)
        {
            const float Eps = 1e-4f;
            return (MathF.Abs(m.M12) < Eps && MathF.Abs(m.M21) < Eps)
                || (MathF.Abs(m.M11) < Eps && MathF.Abs(m.M22) < Eps);
        }

        /// <summary>Uniform scale magnitude of a transform's linear part (max of the two row lengths).</summary>
        private static float LinearScale(Matrix3x2 m)
            => MathF.Max(MathF.Sqrt(m.M11 * m.M11 + m.M12 * m.M12),
                         MathF.Sqrt(m.M21 * m.M21 + m.M22 * m.M22));

        /// <summary>
        /// Coverage rasterized in LOCAL space and composited through the full world transform.
        /// The key holds only the shape and the (bucketed) raster scale, so it is invariant to
        /// rotation, translation and sub-pixel phase.
        /// </summary>
        private void EmitLocalSpaceCoverage(PathGeometry coverageGeometry, RgbaColor color, Matrix3x2 world,
            double opacity, Scissor clip, int width, int height, WGPUTextureFormat format, DrawData data, bool gamma)
        {
            PerfLocalCoverage++;
            GeometryMinCached(coverageGeometry, out float gminX, out float gminY);

            // Bucket the raster scale so a smooth zoom reuses masks instead of making one per frame;
            // 1/8 steps keep the resolution error under ~6%.
            float scale = MathF.Max(LinearScale(world), 0.01f);
            float bucket = MathF.Max(MathF.Round(scale * 8f) / 8f, 0.125f);

            long key = NormalizedHashCached(coverageGeometry, -gminX, -gminY);
            key = key * 31 + BitConverter.SingleToInt32Bits(bucket);
            key = (key * 397 ^ (long)format) * 8 + (_aliasedEdges ? 4 : 0) + (gamma ? 2 : 0) + 1;   // +1 marks the local-space family

            if (!_maskCache.TryGetValue(key, out CachedMask? cm))
            {
                PerfCoverage++;
                PathGeometry normGeom = (gminX == 0f && gminY == 0f)
                    ? coverageGeometry
                    : TransformGeometry(coverageGeometry, Matrix3x2.CreateTranslation(-gminX, -gminY));
                Matrix3x2 rasterXf = Matrix3x2.CreateScale(bucket);

                IntPtr tex, view;
                int mox, moy, mw, mh;
                if (s_gpuRaster)
                {
                    if (!GpuRasterizeCoverage(TransformGeometry(normGeom, rasterXf), gamma,
                            out tex, out view, out mox, out moy, out mw, out mh))
                        return;
                }
                else
                {
                    CoverageMask m = PathRasterizer.Rasterize(TransformGeometry(normGeom, rasterXf));
                    if (m.IsEmpty) return;
                    if (_aliasedEdges) ApplyAliasedEdges(m.Coverage);
                    if (gamma) ApplyTextGamma(m.Coverage);
                    (tex, view) = CreateR8Texture(m.Coverage, m.Width, m.Height);
                    mox = (int)m.OriginX; moy = (int)m.OriginY; mw = m.Width; mh = m.Height;
                }
                // The local-space mask is blitted to a FRACTIONAL destination, so a linear sampler
                // re-introduces partial coverage at the edges -- which is precisely what
                // EdgeMode.Aliased asks us not to do. Sampling nearest keeps the hard 0/255 edge
                // that ApplyAliasedEdges (CPU) / the coverage shader's aliased flag (GPU) produced.
                IntPtr maskSampler = _aliasedEdges ? NearestSampler() : LinearSampler();
                IntPtr bg = CreateSampledBindGroup(format, FillKind.Text, view, maskSampler);
                cm = new CachedMask { Tex = tex, View = view, BindGroup = bg, Sampler = maskSampler, Format = format,
                    Ox = mox, Oy = moy, W = mw, H = mh };
                _maskCache[key] = cm;
            }
            cm.LastFrame = _frameId;

            // The mask covers this rectangle in the geometry's own space; the GPU applies the
            // world transform to its corners, so rotation/scale/translation are free.
            float inv = 1f / bucket;
            float lx0 = gminX + cm.Ox * inv, ly0 = gminY + cm.Oy * inv;
            float lx1 = lx0 + cm.W * inv,    ly1 = ly0 + cm.H * inv;

            float r = color.R, g = color.G, b = color.B, a = (float)Math.Clamp(color.A * opacity, 0.0, 1.0);
            uint baseVertex = (uint)(data.Verts.Count / FloatsPerVertex);
            AddVertex(data.Verts, ToNdc(Vector2.Transform(new Vector2(lx0, ly0), world), width, height), r, g, b, a, 0f, 0f);
            AddVertex(data.Verts, ToNdc(Vector2.Transform(new Vector2(lx1, ly0), world), width, height), r, g, b, a, 1f, 0f);
            AddVertex(data.Verts, ToNdc(Vector2.Transform(new Vector2(lx1, ly1), world), width, height), r, g, b, a, 1f, 1f);
            AddVertex(data.Verts, ToNdc(Vector2.Transform(new Vector2(lx0, ly1), world), width, height), r, g, b, a, 0f, 1f);
            uint firstIndex = (uint)data.Indices.Count;
            AddQuadIndices(data.Indices, baseVertex);
            data.Draws.Add(new DrawItem(firstIndex, 6, clip, FillKind.Text, MaskBindGroup(cm), sourceCopy: _srcCopy));
        }

        // Gamma-space compositing (the default) matches legacy WPF/GDI: colours are sRGB-encoded
        // (gamma) at their source and ALL blending/accumulation happens on those gamma values, with a
        // plain UNORM (non-sRGB) target so nothing re-encodes on store. This reproduces WPF's ADDITIVE
        // emissive look (HexSphere's honeycomb: the raw sRGB emissive brush accumulates, so overlapping
        // lattice layers shine brighter). WPF_WEBGPU_GAMMA=0 restores physically-linear compositing
        // (sRGB target + linear colours) for A/B. Read by MilcoreEngine (colour encode) too.
        // Settable (not readonly) so the sink can turn it OFF for backends that can't present a
        // gamma-space (pre-encoded, plain-UNORM) swapchain faithfully — notably OpenGL/ANGLE, which
        // scans a UNORM swapchain out too dark and lacks SURFACE_VIEW_FORMATS to view it as UNORM over
        // an sRGB swapchain. Metal keeps the default (gamma-space, matching legacy WPF). Only overridden
        // when the user did not pin it explicitly via WPF_WEBGPU_GAMMA (see s_gammaCompositeExplicit).
        internal static bool s_gammaComposite =
            Environment.GetEnvironmentVariable("WPF_WEBGPU_GAMMA") != "0";
        internal static readonly bool s_gammaCompositeExplicit =
            Environment.GetEnvironmentVariable("WPF_WEBGPU_GAMMA") != null;
        private IntPtr _linearSampler;
        private IntPtr _nearestSampler;

        // Text gamma: WPF blends glyph coverage in gamma (sRGB) space, which makes
        // text heavier than the linear-space blend this engine uses elsewhere. On the
        // display-destined sRGB path we re-map glyph coverage through this LUT so text
        // weight matches WPF. (Linear test targets are left untouched, so coverage
        // tests stay exact.) cov' = cov^(1/2.2), the standard sRGB text gamma.
        // WPF_TEXT_LOG=1 dumps how each glyph run is POSITIONED and rasterized: the device offset,
        // the snapped position, the resulting sub-pixel phase, the world scale and the TARGET SIZE.
        // Text on Linux looks worse than on macOS with the same font, rasterizer and scale, and every
        // hypothesis derived from the Linux side alone has been wrong -- so the way to make progress
        // is to run this on BOTH platforms with the same content and diff the numbers, rather than
        // theorise again. Group by target= before diffing anything: the log interleaves the window's
        // pass with supersampled brush/offscreen realizations, whose scale is their supersample
        // factor and says nothing about the display.
        private static readonly bool s_textLog =
            Environment.GetEnvironmentVariable("WPF_TEXT_LOG") == "1";

        // Grid fitting, on by default. WPF_TEXT_HINTING=0 draws the outlines as the face contains
        // them, which is what this did before there was a hinter -- kept because the two are worth
        // being able to put side by side when text looks wrong, not because either is optional.
        private static readonly bool s_hintText =
            Environment.GetEnvironmentVariable("WPF_TEXT_HINTING") != "0";

        private const float TextGamma = 2.2f;
        private static readonly byte[] s_textGammaLut = BuildTextGammaLut();
        private static byte[] BuildTextGammaLut()
        {
            var lut = new byte[256];
            for (int i = 0; i < 256; i++)
                lut[i] = (byte)Math.Clamp((int)MathF.Round(MathF.Pow(i / 255f, 1f / TextGamma) * 255f), 0, 255);
            return lut;
        }

        // Cached glyph-atlas texture, rebuilt only when the atlas changes. The
        // per-pass bind group is created on demand (passes may differ in format).
        private IntPtr _atlasTexture, _atlasView;
        private bool _atlasValid;

        // GPU glyph rasterization: when the font exposes outlines and GPU raster is on, glyph
        // coverage is rasterized by fs_coverage into a PERSISTENT atlas render target (instead of
        // the CPU rasterizing bitmaps that are re-uploaded). Set in the constructor.
        private readonly Text.IGlyphOutlineFont? _outlineFont;
        private bool _gpuGlyphs;

        // Copy a glyph outline figure (PixelsPerEm units, y-down) into run-local space at the given pen
        // origin and em scale. Used by the crisp glyph-outline text path.
        private bool _gpuAtlasCreated;   // persistent atlas texture allocated (RenderAttachment)

        // GPU hit-test id buffer, retained across frames and rendered LAZILY: RenderSceneToView
        // records the frame's scene + size and marks the buffer stale; the first HitTest after a
        // frame renders the visual-id buffer once, and every further query that frame is a pure
        // 1-pixel readback (no scene re-walk). Idle frames with no query pay nothing.
        private SceneVisual? _idScene;
        private int _idW, _idH;
        private bool _idValid;
        private IntPtr _idTex, _idView;
        private int _idTexW, _idTexH;

        // Transient GPU objects created during the current render, released once the frame is submitted
        // (wgpu keeps them alive for in-flight work). Kept as typed handle lists rather than Action
        // closures so deferring a release allocates nothing (there are ~50+ per frame).
        private readonly List<IntPtr> _relBindGroups = new();
        private readonly List<IntPtr> _relViews = new();
        private readonly List<IntPtr> _relTextures = new();
        private readonly List<IntPtr> _relBuffers = new();
        private readonly List<IntPtr> _relEncoders = new();
        private readonly List<IntPtr> _relCmdBuffers = new();
        private readonly List<IntPtr> _relPasses = new();
        private readonly List<(IntPtr Tex, IntPtr View, int W, int H)> _relPoolTex = new();

        /// <summary>Number of glyph-atlas texture uploads (caching diagnostic).</summary>
        public int AtlasUploads { get; private set; }

        public WgpuSceneRenderer(WgpuContext ctx, Text.IFont? font = null, Text.ITextShaper? shaper = null,
            Func<int, Text.IFont?>? styledFont = null)
        {
            _ctx = ctx;

            // The GPU path rasterizer mis-renders under VIRGL -- Mesa's virtio-gpu driver, which
            // forwards GL from a guest VM to the host (Parallels, QEMU, Boxes). Closed paths come out
            // with an edge MISSING, so the fill escapes to its bounding box: a clipped triangle
            // renders as a trapezoid, an ellipse loses an arc, and a stroke outline never closes and
            // fills solid.
            //
            // ROOT CAUSE, by elimination on Mesa 25.2 with tests/WgpuInterop.RenderBaselineTest:
            //
            //   Vulkan (lavapipe)            all 11 scenes pass
            //   GL on llvmpipe               all 11 scenes pass   <- LIBGL_ALWAYS_SOFTWARE=1
            //   GL on virgl                  5 scenes fail
            //
            // Same WGSL, same naga output, same Mesa GLSL front end; only the backend differs. The
            // generated GLSL was captured (MESA_SHADER_CAPTURE_PATH) and read: the straddle test,
            // the winding direction and the `continue` lowering are all correct in it. So the fault
            // is in virgl's re-translation of that GLSL for the host, not in this shader, not in
            // naga, and not in Mesa's front end. Real Linux GL hardware is unaffected -- which is
            // why this fallback keys on the ADAPTER rather than on the GL backend as a whole.
            //
            // The visible effect, for anyone re-testing: on virgl the two edges of a closed contour
            // report the SAME winding direction, so the winding never returns to zero. Summing the
            // per-segment direction across a scanline gives 0 on Vulkan and non-zero on virgl.
            //
            // WPF_WEBGPU_CPU_RASTER=0 forces the GPU rasterizer back on and reproduces it.
            if (ctx.IsVirgl && !s_cpuRasterExplicit)
            {
                s_gpuRaster = false;
            }

            _font = font ?? new Text.BuiltinBitmapFont();
            _shaper = shaper ?? new Text.SimpleTextShaper();
            _outlineFont = _font as Text.IGlyphOutlineFont;
            // Bold and italic runs need their own face. The caller supplies them, because it owns
            // where fonts come from; with no resolver every run draws in the regular face, which is
            // what happened before a run carried its style at all.
            _styledFont = styledFont;
            _gpuGlyphs = s_gpuRaster && _outlineFont != null;
        }

        private void DeferReleaseBindGroup(IntPtr bg) => _relBindGroups.Add(bg);
        private void DeferReleaseBuffer(IntPtr b) => _relBuffers.Add(b);
        private void DeferReleaseEncoder(IntPtr e) => _relEncoders.Add(e);
        private void DeferReleaseCmdBuffer(IntPtr c) => _relCmdBuffers.Add(c);
        private void DeferReleasePass(IntPtr p) => _relPasses.Add(p);
        private void DeferReturnPoolTex(IntPtr tex, IntPtr view, int w, int h) => _relPoolTex.Add((tex, view, w, h));
        private void DeferReleaseTexView(IntPtr tex, IntPtr view)
        {
            if (view != IntPtr.Zero) _relViews.Add(view);
            if (tex != IntPtr.Zero) _relTextures.Add(tex);
        }

        private void FlushFrameReleases()
        {
            foreach (IntPtr x in _relBindGroups) wgpuBindGroupRelease(x); _relBindGroups.Clear();
            foreach (IntPtr x in _relPasses) wgpuRenderPassEncoderRelease(x); _relPasses.Clear();
            foreach (IntPtr x in _relCmdBuffers) wgpuCommandBufferRelease(x); _relCmdBuffers.Clear();
            foreach (IntPtr x in _relEncoders) wgpuCommandEncoderRelease(x); _relEncoders.Clear();
            foreach (IntPtr x in _relViews) wgpuTextureViewRelease(x); _relViews.Clear();
            foreach (IntPtr x in _relTextures) wgpuTextureRelease(x); _relTextures.Clear();
            foreach (IntPtr x in _relBuffers) wgpuBufferRelease(x); _relBuffers.Clear();
            // Any staging uploads not flushed into an encoder this frame have had their buffers
            // released above; drop the now-dangling records so a later flush can't copy from them.
            _pendingTexUploads.Clear();
            foreach ((IntPtr Tex, IntPtr View, int W, int H) t in _relPoolTex) ReturnLayerTexture(t.Tex, t.View, t.W, t.H);
            _relPoolTex.Clear();
            foreach ((IntPtr Tex, IntPtr View, int H) t in _relEdgeTex) ReturnEdgeTexture(t.Tex, t.View, t.H);
            _relEdgeTex.Clear();
            // Return this frame's geometry buffers to the free pool (reused next frame). By the time
            // we get here the frame is submitted; the buffers are reused a frame later, by which point
            // the GL backend has drained them -> no per-frame CreateBuffer churn / upload stalls.
            foreach ((IntPtr Buf, ulong Cap, bool Idx) b in _inUseBufs) (b.Idx ? _freeIdx : _freeVtx).Add((b.Buf, b.Cap));
            _inUseBufs.Clear();
            // Return this frame's DrawData (and their grown Vert/Index/Draw lists) to the pool so next
            // frame reuses the backing arrays instead of allocating fresh lists per pass.
            foreach (DrawData d in _inUseDrawData) _drawDataPool.Push(d);
            _inUseDrawData.Clear();
        }

        // Pooled vertex/index buffers (avoids per-pass-per-frame allocation, the GL stall source).
        private readonly List<(IntPtr Buf, ulong Cap)> _freeVtx = new();
        private readonly List<(IntPtr Buf, ulong Cap)> _freeIdx = new();
        private readonly List<(IntPtr Buf, ulong Cap, bool Idx)> _inUseBufs = new();

        // Pooled DrawData (one per render pass). Reused across frames so the per-pass vertex/index/draw
        // lists keep their capacity instead of reallocating each frame.
        private readonly Stack<DrawData> _drawDataPool = new();
        private readonly List<DrawData> _inUseDrawData = new();
        // Shared empty DrawData for 3D passes (their content is drawn via ExecutePass3D; Data is never read).
        private static readonly DrawData s_emptyData = new();
        // Reused per-frame layer-pass plan (cleared each render; not reentrant on the single render thread).
        private readonly List<LayerPass> _plan = new();

        private DrawData RentDrawData()
        {
            DrawData d = _drawDataPool.Count > 0 ? _drawDataPool.Pop() : new DrawData();
            d.Verts.Clear(); d.Indices.Clear(); d.Draws.Clear(); d.HasText = false;
            d.VbOffset = d.IbOffset = -1;
            _inUseDrawData.Add(d);
            return d;
        }

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

        // Intersect-neutral bound (effect regions that must not be viewport-clipped).
        private static readonly Scissor UnboundedScissor = new Scissor(int.MinValue / 4, int.MinValue / 4, int.MaxValue / 2, int.MaxValue / 2);

        private readonly struct DrawItem
        {
            public readonly uint FirstIndex, IndexCount;
            public readonly Scissor Clip;
            public readonly FillKind Kind;
            public readonly IntPtr BindGroup; // per-fill texture (Textured); ignored for Solid/Text
            // Identity of the custom ShaderEffect this draw uses; -1 for every built-in kind.
            // FillKind.ShaderEffect alone is not enough to pick a pipeline -- each translated
            // shader is a different module.
            public readonly int EffectId;
            /// <summary>CompositingMode.SourceCopy: this draw replaces the target instead of blending.</summary>
            public readonly bool SourceCopy;
            public DrawItem(uint firstIndex, uint indexCount, Scissor clip, FillKind kind, IntPtr bindGroup,
                int effectId = -1, bool sourceCopy = false)
            {
                FirstIndex = firstIndex; IndexCount = indexCount; Clip = clip; Kind = kind; BindGroup = bindGroup;
                EffectId = effectId; SourceCopy = sourceCopy;
            }
        }

        private sealed class DrawData
        {
            public readonly List<float> Verts = new();
            public readonly List<uint> Indices = new();
            public readonly List<DrawItem> Draws = new();
            public bool HasText;
            // Byte offsets of this pass's vertices/indices inside the frame's shared batched geometry
            // buffers (BuildBatchedGeometry). -1 = no geometry / not yet assigned this frame.
            public int VbOffset = -1, IbOffset = -1;
        }

        // A render pass: draw a DrawData into a target view. Offscreen opacity
        // layers clear to transparent; the final pass clears to the background.
        // A single mesh draw within a 3D pass (its own buffers + uniform bind group).
        private readonly struct Draw3D
        {
            public readonly IntPtr Vbuf, Ibuf, BindGroup;
            public readonly uint IndexCount;
            /// <summary>True for a BackMaterial draw: cull front faces and flip normals.</summary>
            public readonly bool BackFace;
            /// <summary>Semi-transparent material: render with depth-write OFF after opaque geometry, and
            /// (per model) draw the far/back side before the near/front side, so front + back blend
            /// (a see-through sphere) instead of the near side alone occluding the far side.</summary>
            public readonly bool Transparent;
            public Draw3D(IntPtr vbuf, IntPtr ibuf, IntPtr bindGroup, uint indexCount, bool backFace = false, bool transparent = false)
            {
                Vbuf = vbuf; Ibuf = ibuf; BindGroup = bindGroup; IndexCount = indexCount; BackFace = backFace; Transparent = transparent;
            }
            /// <summary>The same draw once its slot's (cached) bind group is known.</summary>
            public Draw3D WithBindGroup(IntPtr bindGroup)
                => new(Vbuf, Ibuf, bindGroup, IndexCount, BackFace, Transparent);
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
            // Load (preserve) instead of clear the target -- used to rasterize a new glyph into its
            // shelf region of the persistent GPU glyph atlas without wiping the glyphs already there.
            public bool LoadPreserve;

            public LayerPass(IntPtr targetView, bool clearTransparent, RgbaColor clearColor, DrawData data, WGPUTextureFormat format)
            {
                TargetView = targetView; ClearTransparent = clearTransparent; ClearColor = clearColor; Data = data; Format = format;
            }

            public LayerPass(IntPtr targetView, WGPUTextureFormat format, List<Draw3D> models3D, IntPtr depthView, IntPtr msaaColorView)
            {
                TargetView = targetView; Format = format; Models3D = models3D; DepthView = depthView; MsaaColorView = msaaColorView;
                ClearTransparent = true; ClearColor = default; Data = s_emptyData;
            }
        }

        /// <summary>Render off-screen and read back RGBA8. <paramref name="srgbOutput"/> selects an
        /// sRGB target (display-ready, gamma-encoded once) for screenshots / layered popups; the
        /// default linear target is used by tests, which validate compositing independent of gamma.</summary>
        /// <summary>Whether glyph coverage is corrected for a gamma-space blend before it is
        /// composited. On for anything going to a screen. Off for measuring the GEOMETRY the
        /// hinting produced -- the correction is a curve on the shade, and a test that thresholds
        /// coverage to ask which pixels the ink covers would be reading the curve instead.</summary>
        internal bool TextBlendCorrection { get; set; } = true;

        /// <param name="transparentTarget">Render as a LAYERED WINDOW does: onto something that is
        /// itself see-through. It changes what the compositor may do -- subpixel text is dropped, as
        /// WPF drops ClearType there -- so without a way to ask for it from here that branch could
        /// not be tested at all. RenderSceneToView has taken the same argument all along.</param>
        public byte[] RenderToRgba(SceneVisual root, int width, int height, RgbaColor background,
                                   bool srgbOutput = false, bool transparentTarget = false)
        {
            try
            {
                PerfReadbacks++;
                // Gamma mode: colours are already sRGB-encoded and the pipeline blends in gamma space, so
                // always read back from a plain UNORM target (its bytes ARE the sRGB pixels). An sRGB target
                // here would gamma-encode a second time and decode textures inconsistently.
                bool srgb = srgbOutput && !s_gammaComposite;
                WGPUTextureFormat outFormat = srgb ? OffscreenFormat : ReadbackFormat;
                _srgbOutput = srgb;
                _transparentTarget = transparentTarget;
                List<LayerPass> plan = _plan; plan.Clear();
                _contentTexFrame.Clear();
                DrawData mainData = RentDrawData();

                // Timed on this path as well as on RenderSceneToView. The two do the same collect and
                // encode work and differ only in where the pixels end up, so leaving the counters on
                // one of them meant every headless measurement of that work read as zero.
                long c0 = System.Diagnostics.Stopwatch.GetTimestamp();
                long ca0 = GC.GetAllocatedBytesForCurrentThread();
                CollectVisual(root, Matrix3x2.Identity, 1.0, new Scissor(0, 0, width, height), mainData, plan, width, height, outFormat);
                PerfCollectTicks += System.Diagnostics.Stopwatch.GetTimestamp() - c0;
                PerfCollectAlloc += GC.GetAllocatedBytesForCurrentThread() - ca0;

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

                long e0 = System.Diagnostics.Stopwatch.GetTimestamp();
                BuildBatchedGeometry(plan, mainData);
                BuildBatchedStorage();
                FlushPendingTexUploads(encoder);
                foreach (LayerPass lp in plan) ExecutePass(encoder, lp, atlasView);
                ExecutePass(encoder, new LayerPass(targetView, false, background, mainData, outFormat), atlasView);
                PerfEncodeTicks += System.Diagnostics.Stopwatch.GetTimestamp() - e0;

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

                DeferReleaseEncoder(encoder);
                DeferReleaseCmdBuffer(commandBuffer);
                DeferReleaseBuffer(readback);
                DeferReleaseTexView(targetTex, targetView);
                return pixels;
            }
            finally
            {
                FlushFrameReleases();
            }
        }

// Browser async readback lives in WgpuSceneRenderer.Browser.cs (a non-unsafe
        // partial part: await is illegal inside this unsafe class declaration).


        /// <summary>
        /// Renders the scene into an externally owned texture view (e.g. a
        /// swap-chain back buffer) using the given target format. No readback.
        /// </summary>
        public void RenderSceneToView(SceneVisual root, IntPtr view, WGPUTextureFormat format, int width, int height, RgbaColor background, bool transparentTarget = false)
        {
            try
            {
                _srgbOutput = format is WGPUTextureFormat.RGBA8UnormSrgb or WGPUTextureFormat.BGRA8UnormSrgb;
                _transparentTarget = transparentTarget;
                long c0 = System.Diagnostics.Stopwatch.GetTimestamp();
                long ca0 = GC.GetAllocatedBytesForCurrentThread();
                List<LayerPass> plan = _plan; plan.Clear();
                _contentTexFrame.Clear();
                DrawData mainData = RentDrawData();
                CollectVisual(root, Matrix3x2.Identity, 1.0, new Scissor(0, 0, width, height), mainData, plan, width, height, format);
                PerfCollectTicks += System.Diagnostics.Stopwatch.GetTimestamp() - c0;
                long ca1 = GC.GetAllocatedBytesForCurrentThread();
                PerfCollectAlloc += ca1 - ca0;

                IntPtr atlasView = EnsureAtlasView(AnyText(mainData, plan));
                IntPtr encoder = wgpuDeviceCreateCommandEncoder(_ctx.Device, IntPtr.Zero);

                long e0 = System.Diagnostics.Stopwatch.GetTimestamp();
                BuildBatchedGeometry(plan, mainData);
                BuildBatchedStorage();
                FlushPendingTexUploads(encoder);
                foreach (LayerPass lp in plan) ExecutePass(encoder, lp, atlasView);
                ExecutePass(encoder, new LayerPass(view, false, background, mainData, format), atlasView);
                PerfExecAlloc += GC.GetAllocatedBytesForCurrentThread() - ca1;
                IntPtr commandBuffer = wgpuCommandEncoderFinish(encoder, IntPtr.Zero);
                PerfEncodeTicks += System.Diagnostics.Stopwatch.GetTimestamp() - e0;

                IntPtr* cmds = stackalloc IntPtr[1];
                cmds[0] = commandBuffer;
                long s0 = System.Diagnostics.Stopwatch.GetTimestamp();
                wgpuQueueSubmit(_ctx.Queue, 1, cmds);
                PerfSubmitTicks += System.Diagnostics.Stopwatch.GetTimestamp() - s0;

                DeferReleaseEncoder(encoder);
                DeferReleaseCmdBuffer(commandBuffer);
            }
            finally
            {
                FlushFrameReleases();
            }

            // The scene just composited is the one hit-tests should query; mark the id buffer
            // stale so the next HitTest renders it once against this frame's tree.
            _idScene = root; _idW = width; _idH = height; _idValid = false;
        }

        /// <summary>
        /// GPU hit test against the last composited scene: reads back the visual id at the given
        /// device point from the per-frame id buffer (rendered lazily on the first query each
        /// frame; subsequent queries are pure 1-pixel readbacks). Returns the topmost visual's id,
        /// or 0 if the point hits nothing.
        /// </summary>
        public uint HitTest(int x, int y)
        {
            if (!EnsureIdBuffer() || x < 0 || y < 0 || x >= _idW || y >= _idH) return 0;
            try
            {
                IntPtr readback = CopyIdTexel(x, y, out int bytesPerRow, out IntPtr encoder, out IntPtr commandBuffer);
                byte[] px = _ctx.MapRead(readback, 4);
                uint id = px[3] == 0 ? 0u : (uint)(px[0] | (px[1] << 8) | (px[2] << 16));
                DeferReleaseEncoder(encoder);
                DeferReleaseCmdBuffer(commandBuffer);
                DeferReleaseBuffer(readback);
                return id;
            }
            finally
            {
                FlushFrameReleases();
            }
        }

        /// <summary>Standalone hit test (renders the id buffer for the given scene, then reads
        /// back). Prefer <see cref="HitTest(int,int)"/> after a normal frame render.</summary>
        public uint HitTest(SceneVisual root, int x, int y, int width, int height)
        {
            _idScene = root; _idW = width; _idH = height; _idValid = false;
            return HitTest(x, y);
        }

        // Renders the visual-id buffer for _idScene into the retained _idTex (created/resized as
        // needed) if it is stale. Returns false when there is no scene to query. Shared by the
        // desktop (sync) and browser (async) readback paths.
        private bool EnsureIdBuffer()
        {
            if (_idScene is not { } root) return false;
            if (_idValid && _idTexW == _idW && _idTexH == _idH) return true;

            if (_idTexW != _idW || _idTexH != _idH)
            {
                if (_idView != IntPtr.Zero) { DeferReleaseTexView(_idTex, _idView); _idTex = _idView = IntPtr.Zero; }
                var texDesc = new WGPUTextureDescriptor
                {
                    usage = WGPUTextureUsage.RenderAttachment | WGPUTextureUsage.CopySrc | WGPUTextureUsage.TextureBinding,
                    dimension = WGPUTextureDimension._2D,
                    size = new WGPUExtent3D { width = (uint)_idW, height = (uint)_idH, depthOrArrayLayers = 1 },
                    format = ReadbackFormat,
                    mipLevelCount = 1,
                    sampleCount = 1,
                };
                _idTex = wgpuDeviceCreateTexture(_ctx.Device, &texDesc);
                _idView = wgpuTextureCreateView(_idTex, IntPtr.Zero);
                _idTexW = _idW; _idTexH = _idH;
            }

            try
            {
                List<LayerPass> plan = _plan; plan.Clear();
                _contentTexFrame.Clear();
                DrawData idData = RentDrawData();
                CollectHitIds(root, Matrix3x2.Identity, new Scissor(0, 0, _idW, _idH), idData, _idW, _idH);

                IntPtr encoder = wgpuDeviceCreateCommandEncoder(_ctx.Device, IntPtr.Zero);
                // Coverage-rasterization plan passes first, then the id pass (clear to 0 = "no visual").
                BuildBatchedGeometry(plan, idData);
                BuildBatchedStorage();
                FlushPendingTexUploads(encoder);
                foreach (LayerPass lp in plan) ExecutePass(encoder, lp, IntPtr.Zero);
                ExecutePass(encoder, new LayerPass(_idView, true, default, idData, ReadbackFormat), IntPtr.Zero);

                IntPtr commandBuffer = wgpuCommandEncoderFinish(encoder, IntPtr.Zero);
                IntPtr* cmds = stackalloc IntPtr[1];
                cmds[0] = commandBuffer;
                wgpuQueueSubmit(_ctx.Queue, 1, cmds);
                DeferReleaseEncoder(encoder);
                DeferReleaseCmdBuffer(commandBuffer);
            }
            finally
            {
                FlushFrameReleases();
            }
            _idValid = true;
            return true;
        }

        // Copies the retained id buffer's (x,y) texel into a fresh MapRead buffer. The caller maps
        // it (sync on desktop) and releases the returned buffer/encoder/commandBuffer.
        private IntPtr CopyIdTexel(int x, int y, out int bytesPerRow, out IntPtr encoder, out IntPtr commandBuffer)
        {
            bytesPerRow = AlignUp(256, 256);   // one aligned row holds a single texel
            IntPtr readback = _ctx.CreateBuffer((ulong)bytesPerRow, WGPUBufferUsage.CopyDst | WGPUBufferUsage.MapRead);
            encoder = wgpuDeviceCreateCommandEncoder(_ctx.Device, IntPtr.Zero);
            var copySrc = new WGPUTexelCopyTextureInfo
            {
                texture = _idTex,
                origin = new WGPUOrigin3D { x = (uint)x, y = (uint)y, z = 0 },
                aspect = WGPUTextureAspect.All,
            };
            var copyDst = new WGPUTexelCopyBufferInfo
            {
                layout = new WGPUTexelCopyBufferLayout { offset = 0, bytesPerRow = (uint)bytesPerRow, rowsPerImage = 1 },
                buffer = readback,
            };
            var copyExtent = new WGPUExtent3D { width = 1, height = 1, depthOrArrayLayers = 1 };
            wgpuCommandEncoderCopyTextureToBuffer(encoder, &copySrc, &copyDst, &copyExtent);
            commandBuffer = wgpuCommandEncoderFinish(encoder, IntPtr.Zero);
            IntPtr* cmds = stackalloc IntPtr[1];
            cmds[0] = commandBuffer;
            wgpuQueueSubmit(_ctx.Queue, 1, cmds);
            return readback;
        }

        // Hit-test scene walk: mirrors CollectVisual's world-transform and axis-aligned clip
        // accumulation, emitting each visual's content coverage stamped with its packed id.
        // Effects/opacity don't change WHICH visual is hit, so this skips the layer machinery;
        // clip geometry is approximated by its device bounding box (keeps hits inside the clip).
        private void CollectHitIds(SceneVisual v, Matrix3x2 parentWorld, Scissor parentClip, DrawData data, int width, int height)
        {
            Matrix3x2 world = v.LocalToParent * parentWorld;
            Scissor clip = parentClip;
            if (v.Clip.HasValue)
                clip = Intersect(clip, DeviceBounds(v.Clip.Value, world, width, height));
            if (v.ClipGeometry is { } cg)
                clip = Intersect(clip, GeoDeviceBounds(cg, world, width, height));
            if (clip.IsEmpty) return;

            // Pack the visual id into the (constant) vertex colour bytes; a = 1 marks a hit.
            uint id = v.Id;
            float pr = (id & 0xFF) / 255f, pg = ((id >> 8) & 0xFF) / 255f, pb = ((id >> 16) & 0xFF) / 255f;

            foreach (DrawingPrimitive p in v.Content)
            {
                switch (p)
                {
                    case GeometryFill f:
                        EmitIdCoverage(GeometryToPath(f.Geometry, LocalTolerance(world)), world, clip, pr, pg, pb, data, width, height);
                        break;
                    case GeometryStroke s:
                        EmitIdCoverage(PathStroker.Stroke(s.Geometry, s.Style, LocalTolerance(world)), world, clip, pr, pg, pb, data, width, height);
                        break;
                    case GeometryDrawing d2:
                        if (d2.Fill != null)
                            EmitIdCoverage(GeometryToPath(d2.Geometry, LocalTolerance(world)), world, clip, pr, pg, pb, data, width, height);
                        else if (d2.Stroke != null && d2.StrokeStyle.Thickness > 0)
                            EmitIdCoverage(PathStroker.Stroke(GeometryToPath(d2.Geometry, LocalTolerance(world)), d2.StrokeStyle, LocalTolerance(world)), world, clip, pr, pg, pb, data, width, height);
                        break;
                    case GlyphRunDraw g:
                    {
                        // Text hits its run bounds (clicking a space still hits the text), not
                        // per-glyph coverage: a solid id quad sampling the shared white coverage.
                        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
                        AccText(g, world, ref minX, ref minY, ref maxX, ref maxY);
                        if (minX <= maxX)
                            EmitIdQuad(WhiteCoverageView(), minX, minY, maxX, maxY, clip, pr, pg, pb, data, width, height);
                        break;
                    }
                }
            }
            foreach (SceneVisual child in v.Children)
                CollectHitIds(child, world, clip, data, width, height);
        }

        // GPU-rasterizes a path's coverage (device space) and stamps it with the visual id.
        private void EmitIdCoverage(PathGeometry localGeom, Matrix3x2 world, Scissor clip,
            float pr, float pg, float pb, DrawData data, int width, int height)
        {
            if (!GpuRasterizeCoverage(TransformGeometry(localGeom, world), gamma: false,
                    out IntPtr covTex, out IntPtr covView, out int ox, out int oy, out int w, out int h))
                return;
            DeferReleaseTexView(covTex, covView);
            EmitIdQuad(covView, ox, oy, ox + w, oy + h, clip, pr, pg, pb, data, width, height);
        }

        // A device-space id quad sampling the given coverage view (white for bounds quads).
        private void EmitIdQuad(IntPtr covView, float x0, float y0, float x1, float y1, Scissor clip,
            float pr, float pg, float pb, DrawData data, int width, int height)
        {
            if (clip.IsEmpty) return;
            IntPtr bg = CreateSampledBindGroup(ReadbackFormat, FillKind.Id, covView, LinearSampler());
            DeferReleaseBindGroup(bg);
            uint baseVertex = (uint)(data.Verts.Count / FloatsPerVertex);
            AddVertex(data.Verts, ToNdc(new Vector2(x0, y0), width, height), pr, pg, pb, 1f, 0f, 0f);
            AddVertex(data.Verts, ToNdc(new Vector2(x1, y0), width, height), pr, pg, pb, 1f, 1f, 0f);
            AddVertex(data.Verts, ToNdc(new Vector2(x1, y1), width, height), pr, pg, pb, 1f, 1f, 1f);
            AddVertex(data.Verts, ToNdc(new Vector2(x0, y1), width, height), pr, pg, pb, 1f, 0f, 1f);
            uint firstIndex = (uint)data.Indices.Count;
            AddQuadIndices(data.Indices, baseVertex);
            data.Draws.Add(new DrawItem(firstIndex, 6, clip, FillKind.Id, bg));
        }

        // Records one pass (its own vertex/index buffers + an atlas bind group for
        // its format if it draws text).
        private void ExecutePass(IntPtr encoder, LayerPass lp, IntPtr atlasView)
        {
            PerfPasses++;
            if (lp.Models3D is { } models)
            {
                ExecutePass3D(encoder, lp.TargetView, lp.MsaaColorView, lp.DepthView, lp.Format, models);
                return;
            }

            BuildGeometryBuffers(lp.Data, out IntPtr vbuf, out IntPtr ibuf, out bool hasGeometry);
            RgbaColor clear = lp.ClearTransparent ? new RgbaColor(0, 0, 0, 0) : lp.ClearColor;
            IntPtr pass = lp.LoadPreserve ? BeginLoadPass(encoder, lp.TargetView) : BeginClearPass(encoder, lp.TargetView, clear);
            if (hasGeometry)
            {
                IntPtr atlasBindGroup = IntPtr.Zero;
                if (lp.Data.HasText && atlasView != IntPtr.Zero)
                {
                    // Nearest, matching every other FillKind.Text sampler (the coverage-mask paths).
                    // This bind group is now reached ONLY by the built-in bitmap font: EmitText returns
                    // via the crisp outline-coverage path whenever _outlineFont != null, and _gpuGlyphs
                    // (= s_gpuRaster && _outlineFont != null) is false on the branch that draws atlas
                    // quads — so the BaseEmPixels-atlas MINIFICATION this used to sample linearly for
                    // (small WinForms runs losing thin strokes) no longer comes through here at all.
                    // What does come through is a binary bitmap font MAGNIFIED to the run's em size,
                    // where linear filtering smears its hard 0/255 coverage (a 1-texel stem at 4x read
                    // 0.875 mid-stroke instead of 1.0) instead of reproducing it exactly.
                    atlasBindGroup = CreateSampledBindGroup(lp.Format, FillKind.Text, atlasView, NearestSampler());
                    IntPtr bg = atlasBindGroup;
                    DeferReleaseBindGroup(bg);
                }
                RecordDraws(pass, lp.Format, vbuf, ibuf, lp.Data, atlasBindGroup, lp.OriginX, lp.OriginY, lp.TexW, lp.TexH);
            }
            wgpuRenderPassEncoderEnd(pass);
            IntPtr passLocal = pass;
            DeferReleasePass(passLocal);
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
            Guides guides = ResolveGuides(v, world);
            Scissor clip = parentClip;
            if (v.Clip.HasValue)
                clip = Intersect(parentClip, DeviceBounds(v.Clip.Value, world, width, height));

            double vOpacity = Math.Clamp(v.Opacity, 0.0, 1.0);
            // NOTE: v.BitmapCached (CacheMode="BitmapCache") is deliberately NOT considered here.
            // Routing such a subtree through the layer path was tried and measured; on repeat
            // frames the UNCACHED path already issues zero bind groups, because the per-shape
            // coverage-mask cache has already eliminated the work BitmapCache exists to avoid.
            // Forcing a layer then ADDS a composite bind group and costs fidelity: the render
            // round-trips through an 8-bit premultiplied texture, which moved 444 pixels by up
            // to 13/255 on a 200x200 scene. Frame time over 12/120/300 drawables was
            // 2.19/2.67/3.05 ms uncached against 2.55/2.73/2.56 ms cached -- no reliable win,
            // and all of it inside the ~2.3 ms readback floor. The command is still decoded so
            // the intent is recorded; honour it only if a case is found where it actually pays.
            bool needsLayer = v.Effect != null || v.ClipGeometry != null || v.OpacityMask != null
                || (vOpacity < 0.999 && CountDrawables(v) > 1);

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
            //
            // The region is deliberately NOT intersected with the live clip and its size is padded to a
            // multiple of 8: the cache key includes the region SIZE, and both the clip intersection (a
            // card crossing the viewport edge shrinks every frame) and the floor/ceil of fractionally
            // positioned bounds (±1px as subpixel scroll slides) would change the key every scroll frame
            // — a full card re-bake each time (measured: ~30 layer bakes + ~10MB alloc per frame → 30fps
            // touchpad scroll). Baking the complete content region keeps the key scroll-stable; the
            // composite applies the live clip. Oversized regions (> target) keep the old clipped,
            // per-position behavior.
            bool stableRegion = false;
            Scissor region;
            if (fullTarget)
            {
                region = new Scissor(0, 0, width, height);
            }
            else
            {
                Scissor cb = v.ClipGeometry is { } cg
                    ? GeoDeviceBounds(cg, world, width, height)
                    : ContentDeviceBounds(v, world, width, height);
                if (v.Effect is BlurEffect be) cb = EffectRegion(cb, be.Radius, 0, 0, UnboundedScissor);
                else if (v.Effect is DropShadowEffect de) cb = EffectRegion(cb, de.BlurRadius, de.OffsetX, de.OffsetY, UnboundedScissor);
                if (!cb.IsEmpty) cb = new Scissor(cb.X, cb.Y, (cb.W + 7) & ~7, (cb.H + 7) & ~7);
                if (!cb.IsEmpty && cb.W <= width && cb.H <= height)
                {
                    stableRegion = true;
                    region = cb;
                    if (Intersect(clip, region).IsEmpty) return;   // fully outside the live clip
                }
                else
                {
                    region = Intersect(clip, cb);
                }
            }
            if (region.IsEmpty) return;
            int rx = region.X, ry = region.Y, rw = region.W, rh = region.H;
            float groupOpacity = (float)(inheritedOpacity * vOpacity);

            // A full-target layer is rendered ONCE into a full-window texture at ABSOLUTE device coords
            // (region origin (0,0)) and reused at other scroll positions by translation shift. That single
            // render only captures the subtree where it fell inside the target; if the layer is currently
            // partly/fully off-screen, the missing part is baked blank and the scroll-invariant key means
            // it is NEVER re-rendered -- so a card first seen while partially scrolled in stays blank/clipped
            // even after it fully enters view. Guard: only allow scroll-invariant shift-reuse once the
            // layer's content fits entirely within the target. Until then, key on absolute position so it
            // re-renders fresh (and correct) at each scroll offset. Region-sized layers are unaffected
            // (their key already varies with the visible region size).
            bool shiftReusable = true;
            if (fullTarget)
            {
                // Containment vs the TARGET rect only: the bake below uses the target as its
                // clip (not the inherited scroll-viewport clip), so a card that is inside the
                // window but crossing the viewport edge still bakes COMPLETE content under the
                // scroll-invariant key — the per-frame composite applies the current viewport
                // clip. This keeps scrolling on cache hits (re-render only while crossing the
                // actual window boundary) without the baked-cropped-top bug.
                Scissor cb = ContentDeviceBounds(v, world, width, height);
                shiftReusable = cb.IsEmpty ||
                    (cb.X >= _devOX && cb.Y >= _devOY &&
                     cb.X + cb.W <= _devOX + width && cb.Y + cb.H <= _devOY + height);
            }

            // ALL effect/opacity/clip/mask layers are cacheable: if this subtree (+ its clip/mask)
            // is byte-for-byte the same as a previous frame, reuse its rendered textures and SKIP
            // the render passes + the (full-target, CPU) mask rasterization entirely. This is the
            // dominant cost for static cards on the GL backend. Animated cards re-hash -> re-render.
            {
                long h0 = System.Diagnostics.Stopwatch.GetTimestamp();
                // The scroll-invariant key is always CHECKED first: a stable bake created while
                // the layer was fully visible holds the COMPLETE card (bake clip = target), so
                // shift-reusing it while the card crosses the window edge is correct — the
                // composite scissors to the live target/clip. Containment only gates CREATING
                // the stable entry (a partial first sighting must not bake cropped content
                // under the permanent key); until then, transient frames use position keys.
                long stableKey = LayerCacheKey(v, world, region, shiftReusable: true, fullTarget: fullTarget);
                long key = stableKey;
                bool cacheHit = _layerCache.TryGetValue(key, out CachedLayer? cl);
                if (!cacheHit && !shiftReusable)
                {
                    key = LayerCacheKey(v, world, region, shiftReusable: false, fullTarget: fullTarget);
                    cacheHit = _layerCache.TryGetValue(key, out cl);
                    // Flick throttle: during fast scrolls a layer first seen partially would
                    // re-bake a full-target texture (+ CPU mask) at EVERY new offset. Reuse
                    // the most recent transient bake (shifted) for up to 2 frames instead —
                    // its leading edge lags imperceptibly, and the eviction window (3 idle
                    // frames) guarantees the reused entry's textures are still alive.
                    if (!cacheHit && fullTarget &&
                        _transientBakes.TryGetValue(stableKey, out (CachedLayer Layer, long Frame) tb) &&
                        _frameId - tb.Frame <= 2 && tb.Layer.LastFrame >= _frameId - 3)
                    {
                        cl = tb.Layer;
                        cacheHit = true;
                    }
                }
                PerfHashTicks += System.Diagnostics.Stopwatch.GetTimestamp() - h0;
                if (!cacheHit)
                {
                    PerfLayerMiss++;
                    // Full-target bakes are clipped only by the render target, and stable region
                    // bakes only by their own region, so the cached content is position-complete;
                    // the composite scissors to the live clip.
                    Scissor bakeClip = fullTarget ? new Scissor((int)_devOX, (int)_devOY, width, height)
                        : stableRegion ? region : clip;
                    _layerCache[key] = cl = RenderLayerToCache(v, world, region, bakeClip, plan, width, height);
                    cl.FullTarget = fullTarget;
                    cl.OrigTX = world.M31; cl.OrigTY = world.M32;
                    if (fullTarget)
                    {
                        if (shiftReusable) _transientBakes.Remove(stableKey);           // stable bake supersedes
                        else _transientBakes[stableKey] = (cl, _frameId);               // remember for flick reuse
                    }
                }
                else
                {
                    PerfLayerHits++;
                    // The key is scroll-invariant, so a hit's cached textures are valid at the current
                    // position -- re-composite them where the layer now sits. A region-sized layer's
                    // content is region-local, so it composites at the current region origin. A full-
                    // target layer's content is at absolute device coords, so it composites shifted by how
                    // far the layer's world translation has moved since it was rendered.
                    if (cl.FullTarget)
                    {
                        cl.Rx = (int)MathF.Round(world.M31 - cl.OrigTX);
                        cl.Ry = (int)MathF.Round(world.M32 - cl.OrigTY);
                    }
                    else
                    {
                        cl.Rx = rx; cl.Ry = ry;
                    }
                }
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
            DrawData subData = RentDrawData();
            float sOX = _devOX, sOY = _devOY;
            _devOX = rx; _devOY = ry;
            EmitSubtree(v, world, 1.0, clip, subData, plan, rw, rh, ReadbackFormat);
            _devOX = sOX; _devOY = sOY;
            plan.Add(new LayerPass(subView, true, default, subData, ReadbackFormat) { OriginX = rx, OriginY = ry, TexW = rw, TexH = rh });

            var cl = new CachedLayer { SubTex = subTex, SubView = subView, Rx = rx, Ry = ry, Rw = rw, Rh = rh };
            if (v.ClipGeometry is { } clipGeom)
            {
                // Region-sized geometry-clip mask (rasterized once, cached with the layer) --
                // not a full-window 6.8M-pixel raster. GPU-rasterized by default.
                if (s_gpuRaster)
                {
                    (cl.MaskTex, cl.MaskView) = GpuRasterizeInto(TransformGeometry(clipGeom, world), rw, rh, rx, ry);
                    cl.MaskPooled = true;
                }
                else
                {
                    byte[] maskBytes = PathRasterizer.RasterizeInto(TransformGeometry(clipGeom, world), rw, rh, rx, ry);
                    (cl.MaskTex, cl.MaskView) = CreateR8Texture(maskBytes, rw, rh);
                }
                cl.Mode = 3;
            }
            else if (v.OpacityMask is { } opacityMask)
            {
                // Gradient opacity masks are evaluated per-pixel on the GPU (fs_brushalpha);
                // solid/unsupported masks keep the trivial CPU fill.
                if (!s_gpuRaster || !GpuOpacityMask(opacityMask, world, rw, rh, rx, ry, out cl.MaskTex, out cl.MaskView))
                {
                    byte[] maskBytes = RasterizeOpacityMask(opacityMask, world, rw, rh, rx, ry);
                    (cl.MaskTex, cl.MaskView) = CreateR8Texture(maskBytes, rw, rh);
                }
                else cl.MaskPooled = true;
                cl.Mode = 4;
            }
            else if (v.Effect is BlurEffect b)
            {
                (cl.BlurTex, cl.BlurView) = BlurLayer(subView, b.Radius, b.Kernel, plan, region);
                cl.Mode = 1;
            }
            else if (v.Effect is ShaderEffectDef sfx
                     && ShaderEffectLayer(subView, sfx, plan, region, out IntPtr fxTex, out IntPtr fxView))
            {
                cl.BlurTex = fxTex; cl.BlurView = fxView;
                cl.Mode = 1;                       // composite the shaded layer like a blurred one
            }
            else if (v.Effect is DropShadowEffect ds)
            {
                (cl.BlurTex, cl.BlurView) = BlurLayer(subView, ds.BlurRadius, BlurKernelType.Gaussian, plan, region);
                cl.Mode = 2; cl.ShadowColor = ds.Color; cl.OffX = (int)ds.OffsetX; cl.OffY = (int)ds.OffsetY;
            }
            else
            {
                cl.Mode = 0;
            }
            return cl;
        }

        /// <summary>
        /// Per-axis pixel-snapping guidelines resolved to device space, with the offset each
        /// one needs to land on a pixel boundary. Mirrors milcore's CSnappingFrame
        /// (WpfGfx/core/common/guidelinecollection.cpp): a drawn point takes the offset of the
        /// NEAREST guideline on each axis.
        ///
        /// Per-point rather than one offset for the whole visual, because a uniform shift only
        /// snaps the leading edge. A 1px logical border under a 1.5x DPI scale is 1.5 device
        /// pixels; shifting it whole leaves the far edge mid-pixel and still soft. Snapping each
        /// edge to its own guideline is what lets the border resize to a whole number of pixels,
        /// which is the behaviour WPF apps are built around.
        /// </summary>
        private readonly struct Guides
        {
            public readonly float[]? X, XOff, Y, YOff;
            /// <summary>
            /// Bumped whenever the device-space guidelines are rebuilt. Consumers that memoize a
            /// result derived from these guides key on it: the arrays themselves are refilled IN
            /// PLACE, so their identity says nothing about whether the values changed.
            /// </summary>
            public readonly int Version;
            public Guides(float[]? x, float[]? xo, float[]? y, float[]? yo, int version = 0)
            { X = x; XOff = xo; Y = y; YOff = yo; Version = version; }
            public bool Active => X is not null || Y is not null;

            public float SnapX(float x) => x + Nearest(X, XOff, x);
            public float SnapY(float y) => y + Nearest(Y, YOff, y);

            // Offset of the guideline closest to z. Guidelines are sorted, so this is a scan of
            // a list that is essentially always 2-4 entries long.
            private static float Nearest(float[]? g, float[]? off, float z)
            {
                if (g is null || g.Length == 0) return 0f;
                int best = 0;
                float bestD = MathF.Abs(g[0] - z);
                for (int i = 1; i < g.Length; i++)
                {
                    float d = MathF.Abs(g[i] - z);
                    if (d < bestD) { bestD = d; best = i; }
                }
                return off![best];
            }
        }

        /// <summary>
        /// Resolves a visual's local-space guidelines into device space and precomputes each
        /// one's snapping offset (Round(device) - device). Returns an inactive set under
        /// rotation or skew, where an axis-aligned pixel grid is not meaningful.
        /// </summary>
        /// <summary>
        /// Rebuilds a primitive with its geometry snapped to the guideline grid.
        ///
        /// Snapping happens in LOCAL space (device offset / scale) so everything downstream --
        /// bounds, the mask-cache key, rasterization -- sees one consistent geometry. Only the
        /// axis-aligned rectangle shapes are snapped: those are what borders, separators,
        /// underlines and control backgrounds are made of, and they are what WPF emits
        /// guidelines for. Ellipses, arbitrary paths and glyph runs pass through unchanged, so
        /// this is never worse than ignoring guidelines entirely.
        /// </summary>
        private static DrawingPrimitive SnapPrimitive(DrawingPrimitive p, Guides g, Matrix3x2 world)
        {
            // Same primitive, same guidelines, same axis-aligned world -> the same snapped result.
            if (p.SnapGuidesVersion == g.Version &&
                p.SnapM11 == world.M11 && p.SnapM22 == world.M22 &&
                p.SnapM31 == world.M31 && p.SnapM32 == world.M32)
            {
                return p.SnapResult ?? p;
            }

            DrawingPrimitive result = p switch
            {
                GeometryFill f when SnapGeometry(f.Geometry, g, world) is { } sg => new GeometryFill(sg, f.Brush),
                GeometryDrawing d when SnapDrawing(d, g, world) is { } sd => sd,
                // A stroked arbitrary PATH cannot be reduced to edges to snap, so it passes through.
                _ => p,
            };

            p.SnapResult = ReferenceEquals(result, p) ? null : result;
            p.SnapM11 = world.M11; p.SnapM22 = world.M22;
            p.SnapM31 = world.M31; p.SnapM32 = world.M32;
            p.SnapGuidesVersion = g.Version;
            return result;
        }

        /// <summary>
        /// Snaps DrawRectangle(brush, pen, rect) -- what a Border with a uniform border emits, and
        /// therefore what every themed Button, TextBox and ComboBox is outlined with.
        ///
        /// A stroke is CENTRED on its geometry, so snapping the rectangle's edges is not enough: a
        /// 1px pen on an edge that lands exactly on a pixel boundary still spills half of itself
        /// into the pixel on either side. What has to land on the grid is the stroke's own OUTER
        /// boundary, and its width has to be a whole number of device pixels. Both are done here,
        /// which is why the geometry is re-centred inside the snapped boundary rather than moved.
        /// </summary>
        private static DrawingPrimitive? SnapDrawing(GeometryDrawing d, Guides g, Matrix3x2 world)
        {
            // No pen: the fill alone, snapped exactly as a GeometryFill is.
            if (d.Stroke is null || d.StrokeStyle.Thickness <= 0)
            {
                return SnapGeometry(d.Geometry, g, world) is { } sg
                    ? new GeometryDrawing(sg, d.Fill, d.Stroke, d.StrokeStyle)
                    : null;
            }

            float sx = MathF.Abs(world.M11), sy = MathF.Abs(world.M22);
            if (sx < 1e-6f || sy < 1e-6f) return null;

            // The pen carries ONE thickness, so a non-uniform scale has no single answer for it.
            // Leaving such a stroke alone is no worse than before.
            if (MathF.Abs(sx - sy) > 1e-3f) return null;

            Rect rect;
            float radiusX = 0, radiusY = 0;
            switch (d.Geometry)
            {
                case RectangleGeometry r: rect = r.Rect; break;
                case RoundedRectangleGeometry rr: rect = rr.Rect; radiusX = rr.RadiusX; radiusY = rr.RadiusY; break;
                default: return null;
            }

            float thickness = (float)d.StrokeStyle.Thickness;
            float half = thickness * 0.5f;

            // A whole number of device pixels, and never thinner than one: a hairline that rounded
            // to zero would disappear entirely, which is a worse artefact than a soft edge.
            float snappedThickness = MathF.Max(1f, MathF.Round(thickness * sx)) / sx;

            Rect outer = Inflate(rect, half);
            Rect snappedOuter = SnapRect(outer, g, world);
            Rect centred = Inflate(snappedOuter, -snappedThickness * 0.5f);

            // Corner radii follow the boundary that moved, so a rounded border keeps its shape
            // instead of gaining or losing curvature as the rectangle resizes.
            Geometry snapped = d.Geometry is RoundedRectangleGeometry
                ? new RoundedRectangleGeometry(centred,
                    MathF.Max(0f, radiusX + (snappedThickness - thickness) * 0.5f),
                    MathF.Max(0f, radiusY + (snappedThickness - thickness) * 0.5f))
                : new RectangleGeometry(centred);

            var style = new StrokeStyle(snappedThickness, d.StrokeStyle.Cap, d.StrokeStyle.Join,
                                        d.StrokeStyle.MiterLimit, d.StrokeStyle.DashArray, d.StrokeStyle.DashOffset);
            return new GeometryDrawing(snapped, d.Fill, d.Stroke, style);
        }

        private static Rect Inflate(Rect r, float by)
            => new Rect(r.X - by, r.Y - by, MathF.Max(0f, r.Width + by * 2f), MathF.Max(0f, r.Height + by * 2f));

        private static bool SameRect(Rect a, Rect b)
            => a.X == b.X && a.Y == b.Y && a.Width == b.Width && a.Height == b.Height;

        private static Geometry? SnapGeometry(Geometry geo, Guides g, Matrix3x2 world)
        {
            switch (geo)
            {
                case RectangleGeometry r:
                {
                    Rect snapped = SnapRect(r.Rect, g, world);

                    // Nothing moved: hand back the ORIGINAL, so the caller keeps the primitive it
                    // already had and the geometry keeps the path it already converted.
                    if (SameRect(snapped, r.Rect)) return null;

                    if (r.SnapCache is RectangleGeometry cached && SameRect(cached.Rect, snapped)) return cached;
                    var fresh = new RectangleGeometry(snapped);
                    r.SnapCache = fresh;
                    return fresh;
                }
                case RoundedRectangleGeometry rr:
                {
                    Rect snapped = SnapRect(rr.Rect, g, world);
                    if (SameRect(snapped, rr.Rect)) return null;

                    if (rr.SnapCache is RoundedRectangleGeometry cached && SameRect(cached.Rect, snapped)) return cached;
                    var fresh = new RoundedRectangleGeometry(snapped, rr.RadiusX, rr.RadiusY);
                    rr.SnapCache = fresh;
                    return fresh;
                }
                default: return null;
            }
        }

        // Snaps a local-space rect by moving each edge to its own nearest guideline. Edges move
        // INDEPENDENTLY -- that is what lets a 1.5-device-pixel border become a whole number of
        // pixels, rather than merely shifting and staying soft on the far side.
        private static Rect SnapRect(Rect r, Guides g, Matrix3x2 world)
        {
            float sx = world.M11, sy = world.M22, tx = world.M31, ty = world.M32;
            if (MathF.Abs(sx) < 1e-6f || MathF.Abs(sy) < 1e-6f) return r;

            float dx0 = r.X * sx + tx, dx1 = (r.X + r.Width) * sx + tx;
            float dy0 = r.Y * sy + ty, dy1 = (r.Y + r.Height) * sy + ty;
            float x0 = r.X + (g.SnapX(dx0) - dx0) / sx;
            float x1 = r.X + r.Width + (g.SnapX(dx1) - dx1) / sx;
            float y0 = r.Y + (g.SnapY(dy0) - dy0) / sy;
            float y1 = r.Y + r.Height + (g.SnapY(dy1) - dy1) / sy;
            return new Rect(x0, y0, MathF.Max(0f, x1 - x0), MathF.Max(0f, y1 - y0));
        }

        // Device-space guidelines for a visual, memoized ON the visual.
        //
        // A themed application resolves guidelines for most of its visuals on nearly every frame --
        // every Border, separator, underline and control background carries a GuidelineSet -- and
        // each resolve allocated four float[]. In a full-screen IDE that alone was 264KB per frame,
        // all of it collected within a few frames. The inputs are the visual's own (immutable)
        // guideline arrays and the axis-aligned part of the world matrix, so a frame that neither
        // moves nor rescales the visual can reuse the previous answer outright; and when the world
        // DOES change, the arrays are refilled in place rather than reallocated, so a scroll or a
        // resize costs nothing either. Only the first sighting of a visual allocates.
        private static Guides ResolveGuides(SceneVisual v, Matrix3x2 world)
        {
            if (v.GuidelinesX is null && v.GuidelinesY is null) return default;
            if (MathF.Abs(world.M12) > 1e-6f || MathF.Abs(world.M21) > 1e-6f) return default;

            // Reference equality on the source arrays, not contents: a new GuidelineSet arrives as a
            // new array, and re-pushing the same one is the common case by far.
            if (ReferenceEquals(v.GuidesSrcX, v.GuidelinesX) && ReferenceEquals(v.GuidesSrcY, v.GuidelinesY) &&
                v.GuidesM11 == world.M11 && v.GuidesM22 == world.M22 &&
                v.GuidesM31 == world.M31 && v.GuidesM32 == world.M32)
            {
                return new Guides(v.GuidesDevX, v.GuidesOffX, v.GuidesDevY, v.GuidesOffY, v.GuidesVersion);
            }

            static void Build(float[]? local, float scale, float translate, ref float[]? dev, ref float[]? off)
            {
                if (local is null || local.Length == 0) { dev = null; off = null; return; }
                if (dev is null || dev.Length != local.Length)
                {
                    dev = new float[local.Length];
                    off = new float[local.Length];
                }
                for (int i = 0; i < local.Length; i++)
                {
                    float d = local[i] * scale + translate;
                    dev[i] = d;
                    off[i] = float.IsFinite(d) ? MathF.Round(d) - d : 0f;
                }
            }

            Build(v.GuidelinesX, world.M11, world.M31, ref v.GuidesDevX, ref v.GuidesOffX);
            Build(v.GuidelinesY, world.M22, world.M32, ref v.GuidesDevY, ref v.GuidesOffY);

            v.GuidesSrcX = v.GuidelinesX;
            v.GuidesSrcY = v.GuidelinesY;
            v.GuidesM11 = world.M11; v.GuidesM22 = world.M22;
            v.GuidesM31 = world.M31; v.GuidesM32 = world.M32;
            v.GuidesVersion++;

            return new Guides(v.GuidesDevX, v.GuidesOffX, v.GuidesDevY, v.GuidesOffY, v.GuidesVersion);
        }

        // Composite a cached region layer into the parent (no render passes needed on a hit).        // Composite a cached region layer into the parent (no render passes needed on a hit).
        private void EmitCachedLayer(CachedLayer cl, float groupOpacity, Scissor clip, DrawData outData, WGPUTextureFormat outFormat, int width, int height)
        {
            // Clamp to the current render target's device bounds: a reused full-target layer is
            // composited shifted by its scroll delta, so (Rx,Ry) can fall partly/fully off-target and an
            // unclamped scissor origin makes wgpu abort. The target origin is (_devOX,_devOY) -- NOT (0,0)
            // -- because when compositing a nested layer into a parent region texture the scissor coords
            // are absolute device space; clamping against (0,0) there would empty a valid nested layer
            // (e.g. a nested blur at absolute x~800 vs a 224-wide card region -> nothing drawn).
            var region = Intersect(new Scissor(cl.Rx, cl.Ry, cl.Rw, cl.Rh),
                                   new Scissor((int)_devOX, (int)_devOY, width, height));
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

        // Latest transient (partially-visible) full-target bake per stable key, for
        // short-window reuse during fast scrolling. See the flick throttle above.
        private readonly Dictionary<long, (CachedLayer Layer, long Frame)> _transientBakes = new();

        // ---- region-layer content hash (FNV-1a over geometry/brushes/transforms) ----
        private long _hash;
        private void HV(long x) => _hash = (_hash ^ x) * 1099511628211L;
        private void HF(float f) => HV(BitConverter.SingleToInt32Bits(f));
        private void HR(Rect r) { HF(r.X); HF(r.Y); HF(r.Width); HF(r.Height); }

        private long LayerCacheKey(SceneVisual v, Matrix3x2 world, Scissor region, bool shiftReusable = true, bool fullTarget = false)
        {
            _hash = unchecked((long)1469598103934665603UL);
            // Key on region SIZE, not origin, and on translation RELATIVE to the region origin. A cached
            // layer's textures are rendered in layer-local space (content offset = world translation minus
            // region origin), and scrolling shifts the world translation and the region origin by the SAME
            // delta -- so that relative offset (and thus the rendered content) is invariant under scroll.
            // Keying on the absolute translation/origin made every card miss the cache each scroll frame
            // (hits=0, ~600 re-rasterizations/frame -> ~20fps); keying relative lets fully-visible cards
            // hit and just be re-composited at their new position. (region.X/Y and M31/M32 must move
            // together; sub-pixel scroll still changes the relative offset -> re-render, staying correct.)
            HV(region.W); HV(region.H);
            // A full-target layer that is not yet fully on-screen must NOT be shift-reused (its off-target
            // content is baked blank); mixing in the absolute position makes each scroll offset a distinct
            // key so it re-renders fresh at the current position until it is fully in view. See CollectVisual.
            if (!shiftReusable) { HV(0x5C0117); HV(region.X); HV(region.Y); HF(world.M31); HF(world.M32); }
            // A layer that does NOT cover the whole target is re-composited at its REGION origin, so its
            // cached texture encodes where the content sits WITHIN that region. Scrolling moves the region
            // origin and the world translation together, so that relative offset -- and the texture -- stay
            // valid, which is what makes the scroll-invariant key sound. A RenderTransform that translates
            // the layer inside a STATIONARY region breaks the assumption: the offset changes but the key
            // would not, so the stale texture is re-composited at the old position and the layer looks
            // frozen. That is the Fluent ComboBox dropdown, whose DropShadowEffect makes it a cached layer:
            // its entrance animation baked once and then stopped part-way open, flipping between the stale
            // bake and fresh ones on hover. Hashing the relative offset keeps scrolling on the fast path and
            // forces a re-render only when the layer genuinely moves within its region. Full-target layers
            // are excluded: their region never moves, so hashing it would defeat shift-reuse, and they are
            // already corrected at composite time via OrigTX/OrigTY.
            else if (!fullTarget) { HF(world.M31 - region.X); HF(world.M32 - region.Y); }
            float bx = world.M31, by = world.M32;
            // Clip-geometry / opacity-mask take precedence over effects (matches RenderLayerToCache).
            if (v.ClipGeometry is { } cg) { HV(103); HashGeo(cg); HF(world.M11); HF(world.M12); HF(world.M21); HF(world.M22); }
            else if (v.OpacityMask is { } om) { HV(104); HashBrush(om); HF(world.M11); HF(world.M12); HF(world.M21); HF(world.M22); }
            else switch (v.Effect)
            {
                case BlurEffect b: HV(101); HF((float)b.Radius); HV((long)b.Kernel); break;
                case ShaderEffectDef sx:
                    HV(104); HV(sx.ShaderId);
                    foreach (float fc in sx.FloatConstants) HF(fc);
                    foreach (Brush? sb in sx.SamplerBrushes) { if (sb is null) HV(0); else HashBrush(sb); }
                    break;
                case DropShadowEffect d: HV(102); HF((float)d.BlurRadius); HF((float)d.OffsetX); HF((float)d.OffsetY); HF(d.Color.A); break;
                default: HV(100); break;
            }
            HashVisual(v, world, bx, by);
            return _hash;
        }

        // bx/by: the layer's top-node world translation. Each node's translation is hashed relative to it
        // so the key is scroll-invariant: (world - topWorld) is a node's fixed local offset within the
        // card, cancelling both the scroll delta and the card's absolute position EXACTLY (no rounding ->
        // no sub-pixel boundary flips). The top node hashes to 0. This lets a fully-visible card keep a
        // stable key while scrolling and hit the cache instead of re-rasterizing ~600 masks/frame (the
        // ~20fps scroll stall). The cached texture is re-composited at the current region origin; the only
        // approximation is a <=1px whole-pixel snap of the cached content, standard for a scroll cache.
        // Scale/rotation/skew (M11..M22) are hashed exactly, so rotating/scaling cards re-render.
        private void HashVisual(SceneVisual n, Matrix3x2 w, float bx, float by)
        {
            HF(w.M11); HF(w.M12); HF(w.M21); HF(w.M22); HF(w.M31 - bx); HF(w.M32 - by);
            HV(BitConverter.DoubleToInt64Bits(n.Opacity));
            if (n.Clip is { } c) HR(c); else HV(7);
            if (n.OpacityMask is { } nm) HashBrush(nm);
            if (n.ClipGeometry is { } ncg) HashGeo(ncg);
            foreach (DrawingPrimitive p in n.Content) HashPrimitive(p);
            foreach (SceneVisual ch in n.Children) HashVisual(ch, ch.LocalToParent * w, bx, by);
        }

        // Everything a primitive renders FROM has to be in here. Anything that is not is invisible to
        // the layer cache: the key matches, the bake is reused, and the layer keeps showing the old
        // picture -- which does not look like a caching bug, it looks like the content has frozen or
        // is animating at some fraction of the frame rate.
        private void HashPrimitive(DrawingPrimitive p)
        {
            // SourceCopy replaces the target instead of blending, so the same geometry and brush
            // produce different pixels with and without it.
            if (p.SourceCopy) HV(41);
            switch (p)
            {
                // IsGlyph selects gamma-corrected coverage (text weight), so it changes the pixels
                // for identical geometry and brush.
                case GeometryFill f: HV(1); HashGeo(f.Geometry); HashBrush(f.Brush); if (f.IsGlyph) HV(42); break;
                // Hash THROUGH a nested visual: its content is the scope's real content, and a
                // change inside it must move the layer-cache key or the group renders stale.
                case NestedVisualDraw nv:
                    HV(9);
                    if (nv.Visual.OpacityMask is { } nvm) HashBrush(nvm);
                    foreach (DrawingPrimitive np in nv.Visual.Content) HashPrimitive(np);
                    break;
                case GeometryStroke s: HV(2); HashGeo(s.Geometry); HashBrush(s.Brush); HashStroke(s.Style); break;
                // Must hash the fill/stroke brushes too -- a brush-only change (e.g. a menu item's
                // hover highlight: transparent -> blue with the geometry unchanged) would otherwise
                // leave the layer-cache key unchanged and the card would render the stale (un-hovered) state.
                case GeometryDrawing d:
                    HV(3); HashGeo(d.Geometry);
                    if (d.Fill != null) { HV(31); HashBrush(d.Fill); }
                    if (d.Stroke != null) { HV(32); HashBrush(d.Stroke); HashStroke(d.StrokeStyle); }
                    break;
                // The COLOUR was missing here, so a run whose brush animated -- a value that turns red
                // as it crosses a threshold, a label fading in -- kept rendering its first colour for
                // as long as its text, position and size stayed put.
                case GlyphRunDraw g:
                    HV(4); HV(g.Text.GetHashCode()); HF(g.Origin.X); HF(g.Origin.Y); HF(g.EmSize);
                    HF(g.Color.R); HF(g.Color.G); HF(g.Color.B); HF(g.Color.A);
                    break;
                case Viewport3DDraw v3:
                    HV(5);
                    HF(v3.Camera.Position.X); HF(v3.Camera.Position.Y); HF(v3.Camera.Position.Z);
                    HF(v3.Camera.LookDirection.X); HF(v3.Camera.LookDirection.Y); HF(v3.Camera.LookDirection.Z);
                    // The viewport rect drives both the projection and the size of the region the pass
                    // renders into (Emit3DViewport), so a resized 3D panel is a different picture.
                    HR(v3.Viewport);
                    // Lighting: a scene lit by a moving light animates with nothing else changing.
                    HF(v3.AmbientColor.R); HF(v3.AmbientColor.G); HF(v3.AmbientColor.B);
                    foreach (Light3D l in v3.Lights)
                    {
                        HV((long)l.Kind);
                        HF(l.Direction.X); HF(l.Direction.Y); HF(l.Direction.Z);
                        HF(l.Position.X); HF(l.Position.Y); HF(l.Position.Z);
                        HF(l.Color.R); HF(l.Color.G); HF(l.Color.B); HF(l.Color.A);
                    }
                    foreach (Model3D m in v3.Models)
                    {
                        Matrix4x4 t = m.Transform;
                        HF(t.M11); HF(t.M12); HF(t.M13); HF(t.M21); HF(t.M22); HF(t.M23);
                        HF(t.M31); HF(t.M32); HF(t.M33); HF(t.M41); HF(t.M42); HF(t.M43);
                        HF(m.DiffuseColor.R); HF(m.DiffuseColor.G); HF(m.DiffuseColor.B);
                    }
                    break;
                // Unreachable while every DrawingPrimitive above is handled, and it must STAY that
                // way: a new primitive kind that landed here would hash to a constant, so a layer
                // containing one would never notice it changing.
                default: HV(9); break;
            }
        }

        private void HashStroke(in StrokeStyle s)
        {
            // Thickness alone was hashed. Everything else about a pen changes the pixels too, and
            // DashOffset in particular is what "marching ants" and indeterminate progress indicators
            // animate -- with nothing else about the drawing moving at all.
            HF((float)s.Thickness);
            HV((long)s.Cap); HV((long)s.Join); HF((float)s.MiterLimit); HF((float)s.DashOffset);
            if (s.DashArray is { } dashes) { HV(43); foreach (double d in dashes) HF((float)d); }
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
                // PolygonGeometry must hash its points: a rotated/skewed rect bakes into a polygon, and
                // omitting the points left an animated transform with a constant layer-cache key (stuck).
                case PolygonGeometry pgon: HV(26); foreach (Vector2 pt in pgon.Points) { HF(pt.X); HF(pt.Y); } break;
                case GeometryGroup grp: HV(27); foreach (Geometry child in grp.Children) HashGeo(child); break;
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
                case ImageBrush img when img.SourceVisual is not null:
                    // GPU-live source: no pixels to hash; the source can animate, so key on the frame id so a
                    // cached layer containing it never goes stale (re-renders every frame).
                    HV(15); HV(img.SourceId); HF(img.U0); HF(img.V0); HF(img.U1); HF(img.V1); HV(_frameId);
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
        private (IntPtr Tex, IntPtr View) BlurLayer(IntPtr input, double radius, BlurKernelType kernel, List<LayerPass> plan, Scissor region)
        {
            // Match WPF: standard deviation is 1/3rd the radius, and the Gaussian kernel
            // half-extent is the radius itself (kernel runs -radius..+radius). See
            // CMilBlurEffectDuce::CalculateSamplingWeights ("sd = radius / 3.0"). Treating
            // the radius directly as sigma (and extending taps to 3*radius) over-blurs ~3x.
            // A NEGATIVE sigma is the shader's sentinel for a box kernel (uniform weights);
            // a Gaussian sigma is always positive, so no extra vertex channel is needed.
            float sigma = kernel == BlurKernelType.Box ? -1f : (float)Math.Max(0.5, radius / 3.0);
            int taps = Math.Clamp((int)Math.Ceiling(radius), 1, 48);
            int rw = region.W, rh = region.H;
            float sOX = _devOX, sOY = _devOY;

            var (_, hView) = CreateLayerTexture(rw, rh);
            DrawData hData = RentDrawData();
            _devOX = region.X; _devOY = region.Y;
            EmitBlurQuad(hData, input, 1f / rw, 0f, sigma, taps, region);
            _devOX = sOX; _devOY = sOY;
            plan.Add(new LayerPass(hView, true, default, hData, ReadbackFormat) { OriginX = region.X, OriginY = region.Y, TexW = rw, TexH = rh });

            (IntPtr vTex, IntPtr vView) = CreateOwnedLayerTexture(rw, rh);
            DrawData vData = RentDrawData();
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

        // Pooled coverage-mask textures (R8, RenderAttachment|TextureBinding). Same bargain as the
        // layer pool above, for the masks a path that misses the coverage cache renders into.
        //
        // EXACT size, no bucketing, and the measurement is why: a shape that animates keeps its own
        // mask size across frames even while its outline changes -- eight wiggling 200-segment paths
        // produced eighty masks in ten frames but only EIGHT distinct sizes, one per path, and twenty
        // animating strokes produced four. Rounding sizes up into buckets would buy nothing over that
        // and would cost real pixels: a mask is a render attachment, so an oversized one clears an
        // oversized area, and it is sampled with normalized uv, so it would need every uv rescaled.
        private readonly List<(IntPtr Tex, IntPtr View, int W, int H)> _freeMaskTex = new();

        private (IntPtr Tex, IntPtr View) RentMaskTexture(int width, int height)
        {
            for (int i = 0; i < _freeMaskTex.Count; i++)
            {
                if (_freeMaskTex[i].W != width || _freeMaskTex[i].H != height) continue;
                (IntPtr Tex, IntPtr View, int W, int H) hit = _freeMaskTex[i];
                _freeMaskTex.RemoveAt(i);
                return (hit.Tex, hit.View);
            }

            PerfTextures++;
            PerfMaskTextures++;
            var texDesc = new WGPUTextureDescriptor
            {
                usage = WGPUTextureUsage.RenderAttachment | WGPUTextureUsage.TextureBinding,
                dimension = WGPUTextureDimension._2D,
                size = new WGPUExtent3D { width = (uint)width, height = (uint)height, depthOrArrayLayers = 1 },
                format = WGPUTextureFormat.R8Unorm,
                mipLevelCount = 1,
                sampleCount = 1,
            };
            IntPtr tex = wgpuDeviceCreateTexture(_ctx.Device, &texDesc);
            return (tex, wgpuTextureCreateView(tex, IntPtr.Zero));
        }

        private void ReturnMaskTexture(IntPtr tex, IntPtr view, int width, int height)
        {
            const int Cap = 64;
            if (tex == IntPtr.Zero || _freeMaskTex.Count >= Cap)
            {
                if (tex != IntPtr.Zero) { wgpuTextureViewRelease(view); wgpuTextureRelease(tex); }
                return;
            }
            _freeMaskTex.Add((tex, view, width, height));
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

        // The region a layer effect actually touches: the content bounds plus the blur spread (and, for
        // a drop shadow, the offset). Blurring/compositing only these pixels instead of the whole window
        // is the difference between ~50k and ~7M shaded pixels per effect. Clamped to <paramref
        // name="bound"/> (the absolute-device clip) so the halo stays within the visible area -- NOT to
        // (0,0,width,height), which for a nested layer is the region size, not the window (the absolute
        // content coords would then fall outside it and the region would wrongly come out empty).
        private static Scissor EffectRegion(Scissor content, double blurRadius, double offX, double offY, Scissor bound)
        {
            // The blur kernel reaches blurRadius pixels (half-extent == radius; see BlurLayer),
            // so the touched halo is blurRadius beyond the content edge.
            int m = (int)Math.Ceiling(blurRadius) + 2;
            int x0 = (int)Math.Min(content.X, content.X + offX) - m;
            int y0 = (int)Math.Min(content.Y, content.Y + offY) - m;
            int x1 = (int)Math.Max(content.X + content.W, content.X + content.W + offX) + m;
            int y1 = (int)Math.Max(content.Y + content.H, content.Y + content.H + offY) + m;
            return Intersect(bound, new Scissor(x0, y0, x1 - x0, y1 - y0));
        }

        private void EmitBlurQuad(DrawData data, IntPtr inputView, float stepX, float stepY, float sigma, int taps, Scissor region)
        {
            IntPtr bindGroup = CreateSampledBindGroup(ReadbackFormat, FillKind.Blur, inputView, LinearSampler());
            DeferReleaseBindGroup(bindGroup);

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
            AddQuadIndices(data.Indices, baseVertex);
            data.Draws.Add(new DrawItem(firstIndex, 6, region, FillKind.Blur, bindGroup));
        }

        // Composite a (region-sized) layer texture into the parent at an absolute device rect.
        private void EmitLayerQuad(DrawData data, WGPUTextureFormat format, FillKind kind, IntPtr view,
            float r, float g, float b, float a, int devX, int devY, int texW, int texH, Scissor clip, int width, int height)
        {
            if (clip.IsEmpty) return;
            IntPtr sampler = kind == FillKind.Layer ? NearestSampler() : LinearSampler();
            IntPtr bindGroup = CreateSampledBindGroup(format, kind, view, sampler);
            DeferReleaseBindGroup(bindGroup);

            float x0 = devX, y0 = devY, x1 = devX + texW, y1 = devY + texH;
            uint baseVertex = (uint)(data.Verts.Count / FloatsPerVertex);
            AddVertex(data.Verts, ToNdc(new Vector2(x0, y0), width, height), r, g, b, a, 0f, 0f);
            AddVertex(data.Verts, ToNdc(new Vector2(x1, y0), width, height), r, g, b, a, 1f, 0f);
            AddVertex(data.Verts, ToNdc(new Vector2(x1, y1), width, height), r, g, b, a, 1f, 1f);
            AddVertex(data.Verts, ToNdc(new Vector2(x0, y1), width, height), r, g, b, a, 0f, 1f);

            uint firstIndex = (uint)data.Indices.Count;
            AddQuadIndices(data.Indices, baseVertex);
            data.Draws.Add(new DrawItem(firstIndex, 6, clip, kind, bindGroup));
        }

        // Per-render cache: a content brush's live source (VisualBrush/DrawingBrush) rendered to a GPU
        // texture, keyed by source id so all tiles of one chopped source render it ONCE. Cleared each render.
        private readonly Dictionary<uint, IntPtr> _contentTexFrame = new();

        private static ImageBrush? GpuSourceBrushOf(DrawingPrimitive p) => p switch
        {
            GeometryFill f when f.Brush is ImageBrush { SourceVisual: not null } ib => ib,
            GeometryDrawing d when d.Fill is ImageBrush { SourceVisual: not null } ib => ib,
            _ => null,
        };

        // Render a content brush's live source to a GPU texture ONCE per render (deduped by source id),
        // as a plan LayerPass so it renders before the main pass samples it (same encoder, WebGPU-ordered).
        private IntPtr EnsureContentSourceTexture(ImageBrush img, List<LayerPass> plan)
        {
            if (img.SourceVisual is null) return IntPtr.Zero;
            if (_contentTexFrame.TryGetValue(img.SourceId, out IntPtr view)) return view;
            view = RenderVisualToTexture(img.SourceVisual, img.SourceTexW, img.SourceTexH, plan);
            _contentTexFrame[img.SourceId] = view;
            return view;
        }

        private void EmitSubtree(SceneVisual v, Matrix3x2 world, double accOpacity, Scissor clip,
            DrawData outData, List<LayerPass> plan, int width, int height, WGPUTextureFormat format)
        {
            bool savedAliased = _aliasedEdges, savedNearest = _nearestScaling;
            _aliasedEdges |= v.AliasedEdges;
            _nearestScaling |= v.NearestBitmapScaling;

            Guides guides = ResolveGuides(v, world);
            foreach (DrawingPrimitive rawPrimitive in v.Content)
            {
                // Pixel-snap the primitive's geometry against this visual's GuidelineSet before
                // anything measures or rasterizes it, so bounds, mask keys and coverage agree.
                DrawingPrimitive primitive = guides.Active ? SnapPrimitive(rawPrimitive, guides, world) : rawPrimitive;
                if (primitive is Viewport3DDraw viewport)
                    Emit3DViewport(viewport, world, accOpacity, clip, outData, plan, width, height, format);
                // A scoped group (PushOpacityMask): collect it right here so it draws in content
                // order. CollectVisual gives it a layer of its own, because the mask it carries
                // is exactly what needsLayer keys off.
                else if (primitive is NestedVisualDraw nested)
                    CollectVisual(nested.Visual, world, accOpacity, clip, outData, plan, width, height, format);
                else
                {
                    // Pre-render any GPU-live content-brush source (deduped) before the fill samples it.
                    if (GpuSourceBrushOf(primitive) is { } cbImg) EnsureContentSourceTexture(cbImg, plan);
                    EmitPrimitive(primitive, world, accOpacity, clip, width, height, format, outData);
                }
            }
            foreach (SceneVisual child in v.Children)
                CollectVisual(child, world, accOpacity, clip, outData, plan, width, height, format);

            _aliasedEdges = savedAliased;
            _nearestScaling = savedNearest;
        }

        private void EmitPrimitive(DrawingPrimitive primitive, Matrix3x2 world, double opacity, Scissor clip,
            int width, int height, WGPUTextureFormat format, DrawData data)
        {
            _srcCopy = primitive.SourceCopy;
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
                    EmitText(run, world, opacity, clip, width, height, format, data);
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
            DeferReleaseBindGroup(bindGroup);

            float x0 = offsetX, y0 = offsetY, x1 = offsetX + width, y1 = offsetY + height;
            uint baseVertex = (uint)(data.Verts.Count / FloatsPerVertex);
            AddVertex(data.Verts, ToNdc(new Vector2(x0, y0), width, height), r, g, b, a, 0f, 0f);
            AddVertex(data.Verts, ToNdc(new Vector2(x1, y0), width, height), r, g, b, a, 1f, 0f);
            AddVertex(data.Verts, ToNdc(new Vector2(x1, y1), width, height), r, g, b, a, 1f, 1f);
            AddVertex(data.Verts, ToNdc(new Vector2(x0, y1), width, height), r, g, b, a, 0f, 1f);

            uint firstIndex = (uint)data.Indices.Count;
            AddQuadIndices(data.Indices, baseVertex);
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
            DeferReleaseBindGroup(bindGroup);

            float x0 = devX, y0 = devY, x1 = devX + texW, y1 = devY + texH;
            uint baseVertex = (uint)(data.Verts.Count / FloatsPerVertex);
            AddVertex(data.Verts, ToNdc(new Vector2(x0, y0), width, height), 1f, 1f, 1f, groupOpacity, 0f, 0f);
            AddVertex(data.Verts, ToNdc(new Vector2(x1, y0), width, height), 1f, 1f, 1f, groupOpacity, 1f, 0f);
            AddVertex(data.Verts, ToNdc(new Vector2(x1, y1), width, height), 1f, 1f, 1f, groupOpacity, 1f, 1f);
            AddVertex(data.Verts, ToNdc(new Vector2(x0, y1), width, height), 1f, 1f, 1f, groupOpacity, 0f, 1f);

            uint firstIndex = (uint)data.Indices.Count;
            AddQuadIndices(data.Indices, baseVertex);
            data.Draws.Add(new DrawItem(firstIndex, 6, clip, FillKind.Clip, bindGroup));
        }

        private IntPtr CreateClipBindGroup(WGPUTextureFormat format, IntPtr layerView, IntPtr maskView, IntPtr sampler)
        {

            IntPtr layout = GetBindGroupLayout(format, FillKind.Clip);
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
        /// <summary>
        /// Converts a shape to a fillable path, memoized on the source geometry. The conversion
        /// allocates a whole new PathGeometry, and it ran for every shape on every frame; the fresh
        /// instance also kept PathGeometry's Min/hash memos permanently cold, so this cache is what
        /// makes those effective.
        /// </summary>
        private static PathGeometry GeometryToPath(Geometry geometry)
            => GeometryToPath(geometry, CurveFlattener.DefaultTolerance);

        private static PathGeometry GeometryToPath(Geometry geometry, float tolerance)
        {
            if (geometry.PathCache is not null && geometry.PathCacheTolerance <= tolerance)
                return geometry.PathCache;
            geometry.PathCache = GeometryToPathCore(geometry, tolerance);
            geometry.PathCacheTolerance = tolerance;
            return geometry.PathCache;
        }

        private static PathGeometry GeometryToPathCore(Geometry geometry, float tolerance)
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
                    return RoundedRectToPath(rr, tolerance);
                case EllipseGeometry el:
                    return EllipseToPath(el, tolerance);
                case GeometryGroup grp:
                {
                    var merged = new List<PathFigure>();
                    foreach (Geometry child in grp.Children)
                        merged.AddRange(GeometryToPath(child, tolerance).Figures);
                    return new PathGeometry(grp.FillRule, merged);
                }
            }
            return new PathGeometry(FillRule.NonZero, new List<PathFigure> { figure });
        }

        /// <summary>Test hook: the ellipse-to-path conversion in isolation.</summary>
        internal static PathGeometry EllipseToPathForTest(Vector2 centre, float rx, float ry, float tolerance)
            => EllipseToPath(new EllipseGeometry(centre, rx, ry), tolerance);

        // An ellipse as cubic-Bézier arcs. The arc COUNT follows the radius: four quarter-arcs
        // (the classic kappa construction) carry ~2.7e-4·r of radial error, which is invisible
        // at r=20 but is 0.16px at r=600 -- past the flattening tolerance, and measurably the
        // dominant geometric error on a large circle even after the subdivision itself became
        // adaptive. The error falls as the sixth power of the arc angle, so one extra split
        // buys a factor of 64 and the count grows very slowly.
        private static PathGeometry EllipseToPath(EllipseGeometry el, float tolerance)
        {
            float cx = el.Center.X, cy = el.Center.Y;
            float rx = MathF.Abs(el.RadiusX), ry = MathF.Abs(el.RadiusY);
            int arcs = CurveFlattener.ArcCount(MathF.Max(rx, ry), MathF.PI * 2f, tolerance);

            var f = new PathFigure(new Vector2(cx, cy - ry)) { Closed = true };   // start at the top
            float step = MathF.PI * 2f / arcs;
            for (int i = 0; i < arcs; i++)
            {
                float a0 = -MathF.PI / 2f + i * step;
                AppendEllipseArc(f, cx, cy, rx, ry, a0, a0 + step);
            }
            return new PathGeometry(FillRule.NonZero, new List<PathFigure> { f });
        }

        // One elliptical arc [a0, a1] as a cubic, using the generalized kappa
        // alpha = (4/3)·tan((a1-a0)/4) applied to the parametric derivative.
        private static void AppendEllipseArc(PathFigure f, float cx, float cy, float rx, float ry, float a0, float a1)
        {
            float alpha = 4f / 3f * MathF.Tan((a1 - a0) * 0.25f);
            float c0 = MathF.Cos(a0), s0 = MathF.Sin(a0);
            float c1 = MathF.Cos(a1), s1 = MathF.Sin(a1);
            var p0 = new Vector2(cx + rx * c0, cy + ry * s0);
            var p1 = new Vector2(cx + rx * c1, cy + ry * s1);
            var d0 = new Vector2(-rx * s0, ry * c0);
            var d1 = new Vector2(-rx * s1, ry * c1);
            f.Segments.Add(new CubicBezierSegment(p0 + alpha * d0, p1 - alpha * d1, p1));
        }

        // A rounded rectangle as four edges and four quarter-ellipse corners
        // (cubic Béziers using the standard kappa control-point offset).
        private static PathGeometry RoundedRectToPath(RoundedRectangleGeometry rr, float tolerance)
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
            DeferReturnPoolTex(tex, view, width, height);
            return (tex, view);
        }

        // A Viewport3D with a real viewport rect no longer appears here. Its pass is sized to that
        // rect (Emit3DViewport), so it neither needs full-target dimensions itself nor forces them on
        // the clip/mask/effect layers above it -- which is what made a 3D panel bake window-sized
        // textures all the way up the tree, on every frame the 3D animated.
        //
        // An EMPTY viewport rect still does, because it means "fill the whole render target": there is
        // no rect to size a region from, and no bounds for it to contribute to an enclosing layer
        // (AccumulateContentBounds), so a region-sized layer would crop it away entirely.
        private static bool HasFullTargetContent(SceneVisual v)
        {
            if (v.ClipGeometry != null || v.OpacityMask != null) return true;
            if (FillsTargetWith3D(v)) return true;
            foreach (SceneVisual c in v.Children) if (HasFullTargetContent(c)) return true;
            return false;
        }

        private static bool FillsTargetWith3D(SceneVisual v)
        {
            foreach (DrawingPrimitive p in v.Content)
                if (p is Viewport3DDraw { Viewport.Width: <= 0 } or Viewport3DDraw { Viewport.Height: <= 0 })
                    return true;
            return false;
        }

        // A clip/mask/effect visual can be region-sized unless something NESTED below it still needs
        // full-target dimensions: a descendant clip/mask layer (rendered into this layer's region
        // texture, those nested full-target passes would otherwise get out-of-bounds scissors). The
        // visual's OWN clip/mask does not force full-target -- its region is computed by the caller
        // (clip-path device bounds / content clip). A Viewport3D in its own content does not force it
        // either any more; see HasFullTargetContent.
        private static bool HasNestedFullTargetContent(SceneVisual v)
        {
            if (FillsTargetWith3D(v)) return true;
            foreach (SceneVisual c in v.Children) if (HasFullTargetContent(c)) return true;
            return false;
        }

        // Absolute device-space bounding box (Scissor) of a clip geometry under the world transform,
        // padded 1px (matching the rasterizer). Bézier control points are included, so the box
        // conservatively encloses the curve. The caller intersects with the (absolute) clip; this does
        // NOT clamp to (0,0,width,height) -- for a nested layer those are the region size, not the window.
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
            return new Scissor(ix, iy, iw, ih);
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
            // Absolute device-space bbox; the caller intersects with the (absolute) clip. Do NOT clamp to
            // (0,0,width,height) -- for a nested layer those are the region's size, not the window, and
            // the absolute bbox would fall outside them and be wrongly discarded.
            return new Scissor(ix, iy, iw, ih);
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
                    // A Viewport3D occupies its viewport rect. This used to be unnecessary because a
                    // Viewport3D forced its enclosing layer full-target; now that the layer is sized
                    // to its content, leaving 3D out of the bounds gave a region that excluded it and
                    // the 3D was cropped away entirely. An EMPTY rect means "fill the target" and
                    // still forces full-target (see HasFullTargetContent), so it never reaches here.
                    case Viewport3DDraw v3 when v3.Viewport.Width > 0 && v3.Viewport.Height > 0:
                        AccRect(v3.Viewport.X, v3.Viewport.Y, v3.Viewport.Width, v3.Viewport.Height, world, 0f,
                            ref minX, ref minY, ref maxX, ref maxY);
                        break;
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

        // Accumulates a geometry's device bounds directly per type -- deliberately does NOT call
        // GeometryToPath (which allocates a path graph per rounded-rect/ellipse/group); this runs for
        // every layer's whole subtree each frame, so allocating here would dominate the GC churn.
        private static void AccGeometry(Geometry g, Matrix3x2 world, float pad,
            ref float minX, ref float minY, ref float maxX, ref float maxY)
        {
            switch (g)
            {
                case RectangleGeometry r:
                    AccRect(r.Rect.X, r.Rect.Y, r.Rect.Width, r.Rect.Height, world, pad, ref minX, ref minY, ref maxX, ref maxY);
                    break;
                case RoundedRectangleGeometry rr:
                    AccRect(rr.Rect.X, rr.Rect.Y, rr.Rect.Width, rr.Rect.Height, world, pad, ref minX, ref minY, ref maxX, ref maxY);
                    break;
                case EllipseGeometry e:
                    AccRect(e.Center.X - e.RadiusX, e.Center.Y - e.RadiusY, 2f * e.RadiusX, 2f * e.RadiusY, world, pad, ref minX, ref minY, ref maxX, ref maxY);
                    break;
                case PolygonGeometry pg:
                    foreach (Vector2 p in pg.Points) AccPoint(p, world, pad, ref minX, ref minY, ref maxX, ref maxY);
                    break;
                case GeometryGroup grp:
                    foreach (Geometry child in grp.Children) AccGeometry(child, world, pad, ref minX, ref minY, ref maxX, ref maxY);
                    break;
                case CombinedGeometry cg:
                    AccGeometry(cg.Geometry1, world, pad, ref minX, ref minY, ref maxX, ref maxY);
                    AccGeometry(cg.Geometry2, world, pad, ref minX, ref minY, ref maxX, ref maxY);
                    break;
                case PathGeometry path:
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
                    break;
            }
        }

        // Accumulates the 4 (world-transformed) corners of an axis-aligned rect -- correct under rotation/skew.
        private static void AccRect(float x, float y, float w, float h, Matrix3x2 world, float pad,
            ref float minX, ref float minY, ref float maxX, ref float maxY)
        {
            AccPoint(new Vector2(x, y), world, pad, ref minX, ref minY, ref maxX, ref maxY);
            AccPoint(new Vector2(x + w, y), world, pad, ref minX, ref minY, ref maxX, ref maxY);
            AccPoint(new Vector2(x + w, y + h), world, pad, ref minX, ref minY, ref maxX, ref maxY);
            AccPoint(new Vector2(x, y + h), world, pad, ref minX, ref minY, ref maxX, ref maxY);
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

        /// <summary>Whether any of a polygon's edges is neither horizontal nor vertical -- i.e.
        /// whether it has an edge that needs antialiasing to look like anything but a staircase.</summary>
        private static bool HasSlopedEdge(PolygonGeometry poly)
        {
            Vector2[] pts = poly.Points;
            for (int i = 0; i < pts.Length; i++)
            {
                Vector2 a = pts[i], b = pts[(i + 1) % pts.Length];
                if (MathF.Abs(a.X - b.X) > 1e-4f && MathF.Abs(a.Y - b.Y) > 1e-4f) return true;
            }
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

            // A polygon is flat-tessellated further down, which gives its edges no antialiasing at
            // all. That is right for one that is really a rectangle and wrong for everything else a
            // theme draws with FillPolygon -- arrows, chevrons, sort marks -- and it is why a
            // DIAGONAL LINE came out as a dotted row of fully opaque pixels: a line is widened into
            // a thin quad, and a quad half a pixel wide has no pixel it covers completely, so flat
            // tessellation either took a pixel whole or not at all. Anything with a sloped edge goes
            // through the same analytic-AA coverage path a path fill takes.
            // (A GPU-live content brush is the exception the mesh path below documents: it has no
            // pixels for the CPU rasterizer to sample.)
            if (fill.Geometry is PolygonGeometry poly && HasSlopedEdge(poly)
                && !(fill.Brush is ImageBrush polyIb && polyIb.SourceVisual != null))
            {
                EmitCoverageMask(GeometryToPath(fill.Geometry, LocalTolerance(world)), fill.Brush,
                                 world, opacity, clip, width, height, format, data);
                return;
            }

            // Boolean combinations are combined at the coverage level (per-pixel),
            // so they can't be flattened to a single path.
            if (fill.Geometry is CombinedGeometry combined)
            {
                if (!clip.IsEmpty)
                    EmitMask(RasterizeCombined(combined), fill.Brush, world, opacity, clip, width, height, format, data, fill.IsGlyph);
                return;
            }

            // A solid-filled closed-form shape (ellipse, rounded rect, or plain rectangle) is drawn
            // analytically (per-fragment SDF, no coverage texture), so animated/resizing shapes don't
            // re-bake a mask every frame -- and even a rotated rect (which used to hit the coverage path)
            // gets a crisp analytic edge. The SDF stays pixel-crisp on axis-aligned edges (full/zero
            // coverage right at the pixel boundary) and only anti-aliases fractional edges, so it also
            // improves the mesh path's hard-aliased rects. Non-solid brushes fall through to the coverage
            // path (the SDF only outputs solid * coverage), as do rounded rects with elliptical corners
            // (TryShapeParams returns false).
            // EdgeMode.Aliased opts out of the analytic SDF shape path: fs_shape anti-aliases
            // through fwidth and has no flag channel left to disable it, whereas the coverage
            // path already thresholds on the aliased flag. Aliased rendering is opt-in and rare,
            // so paying for a coverage mask there is a fair trade for CPU/GPU agreement.
            if (!_aliasedEdges && s_gpuRaster && fill.Brush is SolidColorBrush shapeFillBrush && !fill.IsGlyph
                && fill.Geometry is EllipseGeometry or RoundedRectangleGeometry or RectangleGeometry
                && TryShapeParams(fill.Geometry, out Vector2 sc, out float shx, out float shy, out float scr))
            {
                EmitShape(sc, shx, shy, scr, -1f, shapeFillBrush, world, opacity, clip, width, height, format, data);
                return;
            }

            // Same analytic path for a GRADIENT-filled closed-form shape (fs_shapebrush evaluates the
            // ramp per fragment). Without this these drop to a coverage mask baked at local
            // resolution, so their corners pixelate under a DPI scale/zoom -- see ShapeBrushShaderWgsl.
            // Image brushes stay on the coverage path (brushT only models gradients).
            //
            // OFF BY DEFAULT (WPF_WEBGPU_SHAPE_BRUSH=1 to enable): unlike FillKind.Shape, this kind
            // needs a bind group, and the group is built from GetPipeline(format, ShapeBrush)'s
            // auto-layout at EMIT time. Where a draw is later recorded into a pass with a different
            // format, the layout belongs to another pipeline instance and wgpu rejects the draw:
            //     Exclusive pipelines don't match ... in wgpuCommandEncoderFinish
            // which aborts the process (the gallery hits it via its layered/effect content). The
            // shader and emit path are correct in the single-format case; what is missing is
            // resolving the bind group at RECORD time, against the pass actually being encoded.
            if (s_shapeBrush && s_gpuRaster && fill.Brush is LinearGradientBrush or RadialGradientBrush && !fill.IsGlyph
                && fill.Geometry is EllipseGeometry or RoundedRectangleGeometry or RectangleGeometry
                && TryShapeParams(fill.Geometry, out Vector2 gc, out float ghx, out float ghy, out float gcr))
            {
                EmitShapeBrush(gc, ghx, ghy, gcr, -1f, fill.Brush, world, opacity, clip, width, height, format, data);
                return;
            }

            // Curved/composite geometries (rounded rect, ellipse, group) go through
            // the analytic-AA coverage path rather than flat tessellation.
            if (fill.Geometry is RoundedRectangleGeometry or EllipseGeometry or GeometryGroup)
            {
                EmitCoverageMask(GeometryToPath(fill.Geometry, LocalTolerance(world)), fill.Brush, world, opacity, clip, width, height, format, data);
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
                EmitCoverageMask(GeometryToPath(fill.Geometry, LocalTolerance(world)), fill.Brush, world, opacity, clip, width, height, format, data);
                return;
            }

            // A rotated/skewed rectangle is a non-axis-aligned quad whose edges alias under flat
            // tessellation (no MSAA on the 2D path). Route it through the analytic-AA coverage path
            // instead (same as paths/ellipses); keep the fast mesh path for axis-aligned rects, which
            // don't alias. M12/M21 are the off-diagonal (rotation/shear) terms of the world matrix.
            // EXCEPTION: a GPU-live content brush (SourceVisual) can't be sampled by the CPU coverage
            // path (it has no pixels, readback was skipped), so keep it on the mesh path — a rotated
            // textured quad is still correct, just with aliased edges.
            if ((Math.Abs(world.M12) > 1e-6f || Math.Abs(world.M21) > 1e-6f)
                && !(fill.Brush is ImageBrush srcIb && srcIb.SourceVisual != null))
            {
                EmitCoverageMask(GeometryToPath(fill.Geometry, LocalTolerance(world)), fill.Brush, world, opacity, clip, width, height, format, data);
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
                    IntPtr view = GetOrCreateRampView(grad.Stops);
                    bindGroup = CreateSampledBindGroup(format, FillKind.Textured, view, LinearSampler());
                    DeferReleaseBindGroup(bindGroup);
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
                    // A GPU-live content brush samples the source texture rendered this frame (no readback),
                    // mapping the geometry across the Viewbox UV sub-rect [U0,U1]x[V0,V1]; a plain image
                    // brush uploads its pixels and maps the whole image [0,1].
                    float u0 = 0f, v0 = 0f, uSpan = 1f, vSpan = 1f;
                    IntPtr view;
                    if (img.SourceVisual is not null)
                    {
                        if (!_contentTexFrame.TryGetValue(img.SourceId, out view) || view == IntPtr.Zero)
                            return;   // source texture not rendered (unexpected) -> skip rather than sample garbage
                        u0 = img.U0; v0 = img.V0; uSpan = img.U1 - img.U0; vSpan = img.V1 - img.V0;
                    }
                    else
                    {
                        view = GetOrCreateImageView(img.PixelsRgba, img.PixelWidth, img.PixelHeight);
                    }
                    // RenderOptions.BitmapScalingMode=NearestNeighbor -> no filtering.
                    bindGroup = CreateSampledBindGroup(format, FillKind.Textured, view,
                        _nearestScaling ? NearestSampler() : LinearSampler());
                    DeferReleaseBindGroup(bindGroup);
                    kind = FillKind.Textured;

                    Bounds(mesh.Positions, out Vector2 min, out Vector2 size);
                    float imgAlpha = opacityF * img.Opacity;
                    foreach (Vector2 p in mesh.Positions)
                    {
                        float u = size.X > 0f ? (p.X - min.X) / size.X : 0f;
                        float vv = size.Y > 0f ? (p.Y - min.Y) / size.Y : 0f;
                        AddVertex(data.Verts, ToNdc(Vector2.Transform(p, world), width, height), 1f, 1f, 1f, imgAlpha, u0 + u * uSpan, v0 + vv * vSpan);
                    }
                    break;
                }
                default:
                    return;
            }

            uint firstIndex = (uint)data.Indices.Count;
            foreach (uint li in mesh.Indices)
                data.Indices.Add(baseVertex + li);
            data.Draws.Add(new DrawItem(firstIndex, (uint)mesh.Indices.Length, clip, kind, bindGroup, sourceCopy: _srcCopy));
        }

        // Fills an arbitrary path with any brush; AA is in the coverage mask.
        private void EmitPath(GeometryFill fill, PathGeometry path, Matrix3x2 world, double opacity, Scissor clip,
            int width, int height, WGPUTextureFormat format, DrawData data)
            => EmitCoverageMask(path, fill.Brush, world, opacity, clip, width, height, format, data, fill.IsGlyph,
                                fill.BaselineAnchor, fill.PixelAligned);

        // Fills a geometry and/or strokes its outline in one primitive (the fill
        // first, then the stroke on top), reusing the fill and stroke paths.
        private void EmitGeometryDrawing(GeometryDrawing drawing, Matrix3x2 world, double opacity, Scissor clip,
            int width, int height, WGPUTextureFormat format, DrawData data)
        {
            if (drawing.Fill is { } fillBrush)
                EmitFill(drawing.FillPrimitive ??= new GeometryFill(drawing.Geometry, fillBrush),
                    world, opacity, clip, width, height, format, data);

            if (drawing.Stroke is { } strokeBrush && drawing.StrokeStyle.Thickness > 0)
            {
                // A solid-stroked closed-form shape (ellipse, rounded rect, rectangle) becomes an analytic
                // outline (per-fragment SDF, no coverage texture) instead of CPU stroke-to-outline + a
                // re-baked mask every frame. These shapes have no meaningful caps, and rounded/elliptical
                // corners already round the joins, so cap/join style is irrelevant. Distance is computed
                // in local units with fwidth AA, so any affine transform is fine; no dashes.
                if (s_gpuRaster && strokeBrush is SolidColorBrush solidRing
                    && (drawing.StrokeStyle.DashArray is null || drawing.StrokeStyle.DashArray.Length == 0)
                    && TryShapeParams(drawing.Geometry, out Vector2 rc, out float rhx, out float rhy, out float rcr))
                {
                    EmitShape(rc, rhx, rhy, rcr, (float)(drawing.StrokeStyle.Thickness / 2.0), solidRing, world, opacity, clip, width, height, format, data);
                    return;
                }
                PathGeometry path = drawing.Geometry as PathGeometry ?? GeometryToPath(drawing.Geometry, LocalTolerance(world));
                if (!ReferenceEquals(drawing.StrokePrimitivePath, path))
                {
                    drawing.StrokePrimitivePath = path;
                    drawing.StrokePrimitive = new GeometryStroke(path, strokeBrush, drawing.StrokeStyle);
                }
                EmitStroke(drawing.StrokePrimitive, world, opacity, clip, width, height, format, data);
            }
        }

        // Strokes a path. Round-join + round-cap solid strokes (the fork's default, and the
        // exactly distance-field-expressible case) are rasterized directly from the centre-line
        // by fs_stroke — no CPU stroke-to-fill. Everything else (miter/bevel/square joins/caps,
        // dashes, non-solid brushes, non-uniform scale) builds the outline on the CPU
        // (PathStroker) and composites it through the GPU coverage path like any filled path.
        private void EmitStroke(GeometryStroke stroke, Matrix3x2 world, double opacity, Scissor clip,
            int width, int height, WGPUTextureFormat format, DrawData data)
        {
            if (clip.IsEmpty || stroke.Style.Thickness <= 0) return;
            StrokeStyle style = stroke.Style;
            if (!_aliasedEdges && s_gpuRaster && stroke.Brush is SolidColorBrush solidStroke
                && style.Cap == LineCap.Round && style.Join == LineJoin.Round
                && (style.DashArray is null || style.DashArray.Length == 0)
                && IsUniformScale(world))
            {
                float half = (float)(style.Thickness / 2.0);
                // Prefer drawing the stroke SDF straight into the frame (no baked texture); fall back to
                // the cached-texture path for strokes too complex for the per-fragment segment loop.
                if (!EmitStrokeDrawDirect(stroke.Geometry, half, solidStroke, world, opacity, clip, width, height, format, data))
                    EmitGpuStroke(stroke.Geometry, half, solidStroke, world, opacity, clip, width, height, format, data);
                return;
            }
            PathGeometry outline = StrokeOutline(stroke.Geometry, style, LocalTolerance(world));
            EmitCoverageMask(outline, stroke.Brush, world, opacity, clip, width, height, format, data);
        }

        // The stroked outline of a path, memoized on the path (see Geometry.StrokeCache).
        //
        // Keyed by the style and the flattening tolerance as well as the geometry, because the same
        // shape can legitimately be stroked with two different pens, and because the tolerance
        // tightens with the world scale exactly as it does for PathCache. One entry, not a
        // dictionary: a given geometry is drawn with one pen in every case worth optimising, and a
        // geometry that really is stroked two ways just misses and allocates as it did before.
        private static PathGeometry StrokeOutline(PathGeometry geometry, StrokeStyle style, float tolerance)
        {
            if (geometry.StrokeCache is { } cached &&
                geometry.StrokeCacheTolerance == tolerance &&
                SameStrokeStyle(geometry.StrokeCacheStyle, style))
            {
                return cached;
            }

            PathGeometry outline = PathStroker.Stroke(geometry, style, tolerance);
            geometry.StrokeCache = outline;
            geometry.StrokeCacheStyle = style;
            geometry.StrokeCacheTolerance = tolerance;
            return outline;
        }

        // Reference equality on DashArray: a pen keeps its array, and two pens that happen to hold
        // equal dash patterns in different arrays only cost a miss.
        private static bool SameStrokeStyle(in StrokeStyle a, in StrokeStyle b)
            => a.Thickness == b.Thickness && a.Cap == b.Cap && a.Join == b.Join &&
               a.MiterLimit == b.MiterLimit && a.DashOffset == b.DashOffset &&
               ReferenceEquals(a.DashArray, b.DashArray);

        // The SDF stroke uses one device-space half-width, so it is exact only when the world
        // scale is (near-)uniform (rotation is fine; non-uniform scale would need an elliptical
        // pen and stays on PathStroker).
        // Flattening tolerance for geometry that is subdivided in its LOCAL space and only
        // afterwards scaled to device pixels by `world`. Passing the device-space default
        // here would let the world scale magnify the chord error along with the shape --
        // the reason a zoomed-in rounded rect or a thick round-joined stroke used to show
        // visible facets while everything drawn analytically beside it stayed smooth.
        private static float LocalTolerance(Matrix3x2 world)
            => CurveFlattener.ToleranceForScale(CurveFlattener.ScaleOf(world));

        private static bool IsUniformScale(Matrix3x2 m)
        {
            float sx = MathF.Sqrt(m.M11 * m.M11 + m.M12 * m.M12);
            float sy = MathF.Sqrt(m.M21 * m.M21 + m.M22 * m.M22);
            return MathF.Abs(sx - sy) <= 0.01f * MathF.Max(sx, sy);
        }

        // Max centre-line segments drawn analytically per stroke: fs_strokedraw loops over every segment
        // per fragment, so past this a cached-texture bake (fs_stroke) is cheaper for a static stroke.
        private const int MaxStrokeDrawSegments = 256;

        // Draws a round-cap/round-join solid stroke analytically STRAIGHT into the frame (no baked coverage
        // texture): flattens the centre-line to DEVICE-space segments, stashes them in the frame-shared
        // storage arena (one buffer for all such strokes), and emits a bounding-box quad shaded by
        // fs_strokedraw (min segment distance − half-width). Zero per-frame GPU resource creation, so an
        // animated/resizing stroke doesn't re-bake. Returns false (declining to the cached-texture path) for
        // strokes with too many segments, whose O(segments) per-fragment loop would be costly.
        private bool EmitStrokeDrawDirect(PathGeometry centerline, float localHalf, SolidColorBrush solid, Matrix3x2 world,
            double opacity, Scissor clip, int width, int height, WGPUTextureFormat format, DrawData data)
        {
            if (clip.IsEmpty) return false;
            float scale = MathF.Sqrt(world.M11 * world.M11 + world.M12 * world.M12);
            float deviceHalf = localHalf * scale;
            if (deviceHalf <= 0f) return false;

            _edgeScratch.Clear();
            int segCount = PathRasterizer.FlattenCenterlineSegments(TransformGeometry(centerline, world), _edgeScratch,
                out float minX, out float minY, out float maxX, out float maxY);
            if (segCount == 0 || minX > maxX) return false;
            if (segCount > MaxStrokeDrawSegments) return false;   // complex stroke: use the cached-texture path

            // Segments stay in ABSOLUTE device coords (so does the quad's uv), so the fragment's sample
            // point matches them whether or not this draw sits inside a region-offset layer bake.
            Span<float> es = CollectionsMarshal.AsSpan(_edgeScratch);
            IntPtr edgeBg = EdgeBindGroup(MemoryMarshal.AsBytes(es), format, FillKind.StrokeDraw, _srcCopy);

            float pad = deviceHalf + 2f;                          // half-width + ~2px AA/margin
            float x0 = minX - pad, y0 = minY - pad, x1 = maxX + pad, y1 = maxY + pad;

            float a = (float)Math.Clamp(solid.Color.A * opacity, 0.0, 1.0);
            float pr = solid.Color.R * a, pg = solid.Color.G * a, pb = solid.Color.B * a;   // premultiplied
            float sc = segCount;

            uint baseVertex = (uint)(data.Verts.Count / FloatsPerVertex);
            AddVertex(data.Verts, ToNdc(new Vector2(x0, y0), width, height), pr, pg, pb, a, x0, y0, sc, deviceHalf);
            AddVertex(data.Verts, ToNdc(new Vector2(x1, y0), width, height), pr, pg, pb, a, x1, y0, sc, deviceHalf);
            AddVertex(data.Verts, ToNdc(new Vector2(x1, y1), width, height), pr, pg, pb, a, x1, y1, sc, deviceHalf);
            AddVertex(data.Verts, ToNdc(new Vector2(x0, y1), width, height), pr, pg, pb, a, x0, y1, sc, deviceHalf);
            uint firstIndex = (uint)data.Indices.Count;
            AddQuadIndices(data.Indices, baseVertex);
            data.Draws.Add(new DrawItem(firstIndex, 6, clip, FillKind.StrokeDraw, edgeBg, sourceCopy: _srcCopy));
            return true;
        }

        // GPU signed-distance stroke (round join + cap, solid): translation-invariant mask cache
        // mirroring the solid-fill path, rasterized by fs_stroke from the device-space centre-line.
        private void EmitGpuStroke(PathGeometry centerline, float localHalf, SolidColorBrush solid, Matrix3x2 world,
            double opacity, Scissor clip, int width, int height, WGPUTextureFormat format, DrawData data)
        {
            float deviceHalf = localHalf * MathF.Sqrt(world.M11 * world.M11 + world.M12 * world.M12);
            if (deviceHalf <= 0f) return;

            float qx = MathF.Round(world.M31 * 2f) * 0.5f;
            float qy = MathF.Round(world.M32 * 2f) * 0.5f;
            float ox = MathF.Floor(qx), oy = MathF.Floor(qy);
            int phase = (int)((qx - ox) * 2f) * 2 + (int)((qy - oy) * 2f);
            long key = HashGeometry(centerline);
            key = key * 31 + BitConverter.SingleToInt32Bits(world.M11);
            key = key * 31 + BitConverter.SingleToInt32Bits(world.M12);
            key = key * 31 + BitConverter.SingleToInt32Bits(world.M21);
            key = key * 31 + BitConverter.SingleToInt32Bits(world.M22);
            key = key * 31 + phase;
            key = key * 31 + BitConverter.SingleToInt32Bits(deviceHalf);
            key = (key * 397 ^ (long)format) * 2 + 1;   // +1 marker distinguishes stroke from fill keys
            if (!_maskCache.TryGetValue(key, out CachedMask? cm))
            {
                PerfCoverage++;
                Matrix3x2 phased = world;
                phased.M31 = qx - ox;
                phased.M32 = qy - oy;
                if (!GpuStrokeRasterize(TransformGeometry(centerline, phased), deviceHalf,
                        out IntPtr tex, out IntPtr view, out int mox, out int moy, out int mw, out int mh))
                    return;
                IntPtr bg = CreateSampledBindGroup(format, FillKind.Text, view, NearestSampler());
                cm = new CachedMask { Tex = tex, View = view, BindGroup = bg, Sampler = NearestSampler(), Format = format,
                    Ox = mox, Oy = moy, W = mw, H = mh };
                _maskCache[key] = cm;
            }
            cm.LastFrame = _frameId;
            EmitCachedSolidMask(cm, solid.Color, opacity, clip, width, height, data, ox, oy);
        }

        // Extracts the analytic-shape parameters of a closed-form geometry (ellipse / rounded rect /
        // rectangle). Returns false for anything else -- or a rounded rect with unequal corner radii,
        // which the single-radius SDF can't represent (it keeps the coverage-mask path). centreLocal and
        // the half-extents/cornerR are in the geometry's local units; cornerR < 0 marks an ellipse.
        private static bool TryShapeParams(Geometry geom, out Vector2 centreLocal, out float halfX, out float halfY, out float cornerR)
        {
            centreLocal = default; halfX = halfY = cornerR = 0f;
            switch (geom)
            {
                case EllipseGeometry e:
                    if (e.RadiusX <= 0f || e.RadiusY <= 0f) return false;
                    centreLocal = e.Center; halfX = e.RadiusX; halfY = e.RadiusY; cornerR = -1f;   // ellipse
                    return true;
                case RoundedRectangleGeometry rr:
                    if (MathF.Abs(rr.RadiusX - rr.RadiusY) > 0.01f) return false;                  // elliptical corners: not this SDF
                    halfX = (float)rr.Rect.Width * 0.5f; halfY = (float)rr.Rect.Height * 0.5f;
                    if (halfX <= 0f || halfY <= 0f) return false;
                    centreLocal = new Vector2((float)(rr.Rect.X + rr.Rect.Width * 0.5), (float)(rr.Rect.Y + rr.Rect.Height * 0.5));
                    cornerR = Math.Clamp(rr.RadiusX, 0f, MathF.Min(halfX, halfY));
                    return true;
                case RectangleGeometry r:
                    halfX = (float)r.Rect.Width * 0.5f; halfY = (float)r.Rect.Height * 0.5f;
                    if (halfX <= 0f || halfY <= 0f) return false;
                    centreLocal = new Vector2((float)(r.Rect.X + r.Rect.Width * 0.5), (float)(r.Rect.Y + r.Rect.Height * 0.5));
                    cornerR = 0f;                                                                   // sharp rectangle
                    return true;
                default:
                    return false;
            }
        }

        // Draws an analytic closed-form shape -- filled (halfLocal < 0) or stroked outline of half-width
        // halfLocal -- as a single device-space quad shaded by fs_shape. No coverage texture and no
        // per-frame GPU resource creation, so a resizing shape costs nothing to re-realize each frame.
        // All shape params are in local units; the shader does the AA (fwidth), so any affine world works.
        private void EmitShape(Vector2 centreLocal, float halfX, float halfY, float cornerR, float halfLocal,
            SolidColorBrush brush, Matrix3x2 world, double opacity, Scissor clip, int width, int height, WGPUTextureFormat format, DrawData data)
        {
            if (clip.IsEmpty || halfX <= 0f || halfY <= 0f) return;

            float scale = MathF.Sqrt(world.M11 * world.M11 + world.M12 * world.M12);
            float hwLocal = MathF.Max(0f, halfLocal);
            float padLocal = scale > 1e-6f ? 2f / scale : 2f;          // ~2px of AA margin, in local units
            float extentX = halfX + hwLocal + padLocal;
            float extentY = halfY + hwLocal + padLocal;
            float sh = halfLocal < 0f ? -1f : hwLocal;                 // <0 selects a filled shape in the shader

            float a = (float)Math.Clamp(brush.Color.A * opacity, 0.0, 1.0);
            float pr = brush.Color.R * a, pg = brush.Color.G * a, pb = brush.Color.B * a;   // premultiplied

            // Four corners of the local bounding box; uv = local position relative to the shape centre.
            Span<Vector2> corner = stackalloc Vector2[4]
            {
                new(-extentX, -extentY), new(extentX, -extentY), new(extentX, extentY), new(-extentX, extentY),
            };
            uint baseVertex = (uint)(data.Verts.Count / FloatsPerVertex);
            foreach (Vector2 lc in corner)
            {
                Vector2 dev = Vector2.Transform(centreLocal + lc, world);
                AddVertex(data.Verts, ToNdc(dev, width, height), pr, pg, pb, a, lc.X, lc.Y, halfX, halfY, cornerR, sh);
            }
            uint firstIndex = (uint)data.Indices.Count;
            data.Indices.Add(baseVertex + 0); data.Indices.Add(baseVertex + 1); data.Indices.Add(baseVertex + 2);
            data.Indices.Add(baseVertex + 0); data.Indices.Add(baseVertex + 2); data.Indices.Add(baseVertex + 3);
            data.Draws.Add(new DrawItem(firstIndex, 6, clip, FillKind.Shape, IntPtr.Zero, sourceCopy: _srcCopy));
        }

        // Gradient-filled closed-form shape, drawn analytically (fs_shapebrush): the same SDF quad as
        // EmitShape, with the gradient evaluated per fragment instead of a flat colour. Keeps a
        // gradient rounded rect/ellipse on the resolution-independent path rather than dropping it to a
        // local-resolution coverage mask that the world transform then magnifies.
        private void EmitShapeBrush(Vector2 centreLocal, float halfX, float halfY, float cornerR, float halfLocal,
            Brush gradient, Matrix3x2 world, double opacity, Scissor clip, int width, int height,
            WGPUTextureFormat format, DrawData data)
        {
            if (clip.IsEmpty || halfX <= 0f || halfY <= 0f) return;

            float scale = MathF.Sqrt(world.M11 * world.M11 + world.M12 * world.M12);
            float hwLocal = MathF.Max(0f, halfLocal);
            float padLocal = scale > 1e-6f ? 2f / scale : 2f;
            float extentX = halfX + hwLocal + padLocal;
            float extentY = halfY + hwLocal + padLocal;
            float sh = halfLocal < 0f ? -1f : hwLocal;

            // The shader's `local` is uv, i.e. relative to the shape centre, so shift the gradient's
            // geometry into that same frame. A radial brush's g1 is (radiusX, radiusY), not a point,
            // so only the centre/start moves.
            (Vector2 g0, Vector2 g1) = gradient is LinearGradientBrush lgb
                ? (lgb.Start - centreLocal, lgb.End - centreLocal)
                : (((RadialGradientBrush)gradient).Center - centreLocal,
                   new Vector2(((RadialGradientBrush)gradient).RadiusX, ((RadialGradientBrush)gradient).RadiusY));

            byte[] uni = BuildBrushParams(gradient, g0, g1, 0f, 0f, 0f, 0f, (float)Math.Clamp(opacity, 0.0, 1.0));
            IntPtr ubuf = GetOrCreateUniform(uni);
            IntPtr rampView = GetOrCreateRampView(BrushStops(gradient));
            IntPtr bg = CreateBrushBindGroup(format, FillKind.ShapeBrush, IntPtr.Zero, rampView, ubuf, uni.Length, _srcCopy);
            DeferReleaseBindGroup(bg);

            Span<Vector2> corner = stackalloc Vector2[4]
            {
                new(-extentX, -extentY), new(extentX, -extentY), new(extentX, extentY), new(-extentX, extentY),
            };
            uint baseVertex = (uint)(data.Verts.Count / FloatsPerVertex);
            foreach (Vector2 lc in corner)
            {
                Vector2 dev = Vector2.Transform(centreLocal + lc, world);
                AddVertex(data.Verts, ToNdc(dev, width, height), 1f, 1f, 1f, 1f, lc.X, lc.Y, halfX, halfY, cornerR, sh);
            }
            uint firstIndex = (uint)data.Indices.Count;
            data.Indices.Add(baseVertex + 0); data.Indices.Add(baseVertex + 1); data.Indices.Add(baseVertex + 2);
            data.Indices.Add(baseVertex + 0); data.Indices.Add(baseVertex + 2); data.Indices.Add(baseVertex + 3);
            data.Draws.Add(new DrawItem(firstIndex, 6, clip, FillKind.ShapeBrush, bg, sourceCopy: _srcCopy));
        }

        private void EmitCoverageMask(PathGeometry coverageGeometry, Brush brush, Matrix3x2 world, double opacity,
            Scissor clip, int width, int height, WGPUTextureFormat format, DrawData data, bool isGlyph = false,
            Vector2? baselineAnchor = null, bool pixelAligned = false)
        {
            if (clip.IsEmpty) return;

            // Apply WPF's text-gamma weighting only where this pass blends in LINEAR space, which is
            // exactly what _srgbOutput means: an sRGB target makes the GPU decode to linear on read and
            // re-encode on write, so coverage blends linearly and needs cov^(1/2.2) to land where WPF's
            // gamma-space blend does. It is NOT needed in gamma-space mode (the default: colours
            // pre-encoded at their source, blended on those encoded values, plain-UNORM target) --
            // there the blend is ALREADY the thing the LUT emulates, and applying both counts the
            // correction twice.
            //
            // It used to read `(s_gammaComposite || _srgbOutput)`, i.e. it fired in gamma mode too, on
            // the theory that gating it on _srgbOutput alone left text "too light to read". MEASURED,
            // and that is backwards -- the double correction made every glyph ~23% too heavy. Total
            // coverage over one 13px word, normalised against the run's own ink colour (#222833 ->
            // grayscale 39; measuring against black instead is what made this look fine for so long):
            //
            //     LUT on  : 263.9      <- 23% more ink than the outlines contain
            //     LUT off : 215.6      <- within 1.6% of the geometry
            //     FreeType rasterizing the same outlines at the same size: 219.2
            //
            // The excess lands on partially-covered EDGE pixels (cov 0.25 -> 0.53), so it reads as a
            // halo of over-dark alpha around every glyph -- which is what "the text looks aliased on
            // Linux" turned out to be. See Documentation/linux-head.md.
            //
            // Still dropped on transparent targets (layered windows), matching WPF dropping ClearType
            // there. Note _srgbOutput can only be true when s_gammaComposite is false (see the two
            // assignment sites), so this single term expresses "this pass blends linearly".
            // Glyph runs fitted to the pixel grid are WinForms text, and WinForms text is drawn
            // by GDI, which composites in LINEAR LIGHT. On an sRGB target ours already does -- the
            // GPU decodes on read and encodes on write -- so the right correction is none at all.
            //
            // The LUT below is not for that. It exists to make text land where WPF's own GAMMA-SPACE
            // blend puts it, and applied to WinForms text it is a correction toward the wrong
            // answer: it takes a pixel of 0.47 coverage to 0.42 displayed darkness where GDI shows
            // 0.23, and every glyph edge is such a pixel, so the page reads as bold. Measured
            // against ClearType, dropping it puts that pixel at 0.246.
            bool hintedText = TextBlendCorrection && isGlyph && pixelAligned;

            // ONE LINE PER PROCESS, so the two harnesses can be held against each other. The parity
            // suite and the live window disagree about whether keeping the x fitting helps, and they
            // are supposed to be rendering the same way; this prints what each actually renders
            // under so the difference can be seen rather than guessed at.
            if (isGlyph && s_flagTrace && !s_flagTraced)
            {
                s_flagTraced = true;
                Console.Error.WriteLine($"[flags] pixelAligned={pixelAligned} hintedText={hintedText}"
                    + $" clearType={_clearType} srgbOutput={_srgbOutput}"
                    + $" gammaComposite={s_gammaComposite} transparent={_transparentTarget}"
                    + $" textBlend={TextBlendCorrection} aliasedEdges={_aliasedEdges}"
                    + $" axisAligned={IsAxisAligned(world)} scale={LinearScale(world):0.000}");
            }
            bool gamma = isGlyph && !_transparentTarget && _srgbOutput && !hintedText;

            // Glyph runs that were fitted to the pixel grid are composited the way Windows
            // composites text: in LINEAR LIGHT. Our pipeline blends in gamma space, where half
            // coverage over white gives a pixel half way between black and white in encoded units
            // -- much darker than half the light. Every glyph edge is a partly covered pixel, so
            // the whole page comes out heavier than the same text beside it, which is what "the
            // normal font looks like bold" was. See the coverage shader for the measurement.
            // And where the compositor blends in GAMMA space instead, coverage has to be squared
            // to land in the same place -- half coverage over white is half the LIGHT, not a value
            // half way between the two encoded colours.
            bool textBlend = hintedText && !_transparentTarget && !_srgbOutput;

            // Coverage stays EXACT AREA, including for text that has been through the face's own
            // hinting -- and it is worth saying why, because matching Windows exactly argues the
            // other way and is wrong here.
            //
            // GDI's greyscale does not measure area: it counts sixteen point samples and resolves
            // them through a ladder of greys that is far from linear (eight of sixteen comes out at
            // 101, not 128). Doing the same makes our text match GDI's ANTIALIASED_QUALITY output
            // almost pixel for pixel -- the average disagreement over the alphabet drops from 36/255
            // to 12/255. It also makes it EIGHTEEN PER CENT LIGHTER than the application beside it,
            // because what is on the screen beside it is not ANTIALIASED_QUALITY, it is ClearType,
            // which lays ink on all three subpixels and is heavier than either. Measured on one
            // label: stock 78.8 units of ink, exact area 86.7, GDI's ladder 64.9. The ladder wins
            // the comparison it was measured against and loses the one anybody is looking at.
            //
            // So the ladder is not used. The parity test measures GEOMETRY -- which pixels the ink
            // covers -- and expects the shades to differ.

            // Solid coverage (text, icons, rounded rects, ellipses, strokes) is rasterized in
            // DEVICE space so the mask isn't upscaled by the world transform -- this keeps text
            // and edges crisp under the DPI/scale/rotation transform instead of bilinear-blurry.
            // (Non-solid brushes bake per-texel in the geometry's local space, so they stay local.)
            if (brush is SolidColorBrush solid)
            {
                // ... but only where it actually buys something, because it is NOT free: it
                // resamples a local-space mask through the world transform, and that bilinear
                // blit costs fidelity.
                //
                // Two conditions have to hold before it is worth paying.
                //
                // Axis-aligned content fails the first: the device-space path below is ALREADY
                // translation-invariant (integer offset on the quad + <=4 half-pixel phase
                // variants baked into the mask), so the local cache has the same hit rate and
                // adds nothing but blur -- up to 87 levels on thin strokes (strokes-joins,
                // strokes-dashed, mil-arc-large), where the feature is a pixel or two wide.
                //
                // Static rotated content fails the second: the device key holds the exact linear
                // part, so a visual sitting at a fixed angle is rasterized once and reused
                // forever. Caching it locally would trade 59 levels on transform-rotate for a
                // saving of zero.
                //
                // What is left is the case the cache exists for: an ANIMATING rotation, where the
                // device path misses every single frame (measured at 60 rasterizations over 60
                // frames of a 1-degree spin, versus 1 with the cache). There the mask would be
                // thrown away next frame regardless, so the resample buys a 60x cut for a blur
                // that is on a moving object for one frame at a time.
                if (s_localCoverageCache && !pixelAligned && !IsAxisAligned(world)
                    && IsLinearAnimating(NormalizedHashCached(coverageGeometry, 0f, 0f), world))
                {
                    EmitLocalSpaceCoverage(coverageGeometry, solid.Color, world, opacity, clip, width, height, format, data, gamma);
                    return;
                }

                // Translation-invariant cache: scrolling is pure translation, so the key must
                // not contain the device-space offset or every scroll position re-rasterizes
                // every visible path on the CPU. Split the translation into an integer pixel
                // offset (applied to the emitted quad) and a quarter-pixel subpixel phase
                // (baked into the mask + key); rasterization then only happens when geometry,
                // the linear transform, or the phase changes.
                // Half-pixel phase (4 variants): fewer variants than quarter-px means a fast
                // fractional scroll cycles through cached phases instead of missing on most
                // frames (16 phases + a short eviction window re-rasterized nearly every frame).
                // Normalize the geometry's OWN translation (e.g. a glyph's layout position) into the same
                // integer-offset + half-pixel-phase split we already apply to the world translation, and
                // hash the shape at its local origin — so the same glyph shape at any position shares ONE
                // mask (≤4 phase variants) instead of one mask per instance.
                GeometryMinCached(coverageGeometry, out float gminX, out float gminY);

                // Geometry that is already ON the grid is re-seated by a WHOLE number of units, so
                // its edges stay where they were put. Taking its own bounding box as the origin
                // instead shifts everything by whatever fraction of a pixel that box begins at -- and
                // a stem narrowed to exactly one pixel and then moved a third of one is fainter than
                // the unfitted outline it replaced. That is what made fitted text look thin.
                if (pixelAligned)
                {
                    gminX = MathF.Floor(gminX);
                    gminY = MathF.Floor(gminY);
                }

                float dx = world.M11 * gminX + world.M21 * gminY + world.M31;
                float dy = world.M12 * gminX + world.M22 * gminY + world.M32;
                float qx = MathF.Round(dx * 2f) * 0.5f;
                float ox = MathF.Floor(qx);
                float phaseX = qx - ox;

                // Vertical snap. For glyphs, snap the RUN's shared baseline (baselineAnchor)
                // to the half-pixel grid and shift every glyph by that same amount, so all
                // glyphs land on one snapped baseline. Snapping each glyph by its own ink-box
                // top (dy) instead rounds glyphs with different ascents in different directions,
                // scattering baselines by up to half a pixel — the ~1px "sunken letters" bug.
                // Non-glyph fills keep the per-geometry snap. In both cases the vertical phase
                // stays shape-stable (≤2 values across a scroll), so the mask cache still dedups.
                //
                // Whole-pixel baseline snapping (MathF.Round(bdy)) was MEASURED and is a no-op on a
                // 1x display: layout puts baselines on integer DIPs, so the two grids round to the
                // same place and only 2.5k of 741k pixels in the gallery change. The continuous
                // phaseY in WPF_TEXT_LOG is each glyph's fractional INK-BOX TOP, not a fractional
                // baseline, so it is not evidence of a snapping fault. Don't re-try this as a text
                // sharpness fix -- see Documentation/linux-head.md.
                float oy, phaseY;
                if (baselineAnchor is Vector2 anchor)
                {
                    float bdy = world.M12 * anchor.X + world.M22 * anchor.Y + world.M32;
                    float shift = MathF.Round(bdy * 2f) * 0.5f - bdy; // baseline snap, shared by the run
                    float ty = dy + shift;                            // this glyph's top, shifted with the run
                    oy = MathF.Floor(ty);
                    phaseY = ty - oy;
                }
                else
                {
                    float qy = MathF.Round(dy * 2f) * 0.5f;
                    oy = MathF.Floor(qy);
                    phaseY = qy - oy;
                }
                // Key must distinguish the (now finer) vertical phase; X stays half-pixel (2 variants).
                int phase = (int)(phaseX * 2f) * 512 + (int)MathF.Round(phaseY * 255f);
                if (s_textLog && baselineAnchor is not null)
                {
                    // target=WxH is not decoration: it says WHICH PASS the placement belongs to, and
                    // without it this log is actively misleading. Brush and offscreen realizations
                    // rasterize their content SUPERSAMPLED into a small texture -- x3 for a
                    // DrawingBrush motif, x2 for a VisualBrush (see MilcoreEngine RealizeTileBrush /
                    // RealizeVisualBrush) -- so their glyphs legitimately report scale=3.000 and
                    // scale=2.000 while the window itself renders at 1.000. Reading those lines as
                    // the window's DPI cost one whole investigation (Documentation/linux-head.md):
                    // they are the FIRST lines in the log, because brushes realize before the first
                    // present. Filter on the window's target size before drawing any conclusion.
                    DebugLog?.Invoke($"glyphpos dx={dx:F3} dy={dy:F3} -> ox={ox} oy={oy} " +
                                     $"phaseX={phaseX:F3} phaseY={phaseY:F3} scale={LinearScale(world):F3} " +
                                     $"target={width}x{height}");
                }
                // The normalized geometry is only MATERIALIZED on a cache miss (below); the key is
                // hashed straight off the original with the normalizing translation folded in, and
                // memoized on the geometry (the walk is pure and the instances are stable).
                long key = NormalizedHashCached(coverageGeometry, -gminX, -gminY);
                key = key * 31 + BitConverter.SingleToInt32Bits(world.M11);
                key = key * 31 + BitConverter.SingleToInt32Bits(world.M12);
                key = key * 31 + BitConverter.SingleToInt32Bits(world.M21);
                key = key * 31 + BitConverter.SingleToInt32Bits(world.M22);
                key = key * 31 + phase;
                // Subpixel text is a DIFFERENT MASK of the same shape -- three channels instead of
                // one -- so it needs its own key, or a run drawn once with ClearType and once without
                // (a rotated copy, a layered window) would be handed the other one's texture.
                bool subpixel = isGlyph && ClearType && !_transparentTarget && IsAxisAligned(world);
                key = (key * 397 ^ (long)format) * 32 + (_symmetricSmoothing && subpixel ? 16 : 0)
                      + (_aliasedEdges ? 8 : 0) + (gamma ? 4 : 0)
                      + (textBlend ? 2 : 0) + (subpixel ? 1 : 0);
                // A subpixel mask now carries the contrast curve for the INK it will be drawn in --
                // the gamma blend GDI does depends on the foreground, and only black makes it drop
                // out -- so two colours cannot share one mask. Keyed only for subpixel text, and
                // only on the three channels the curve reads, so a UI's handful of text colours
                // costs a handful of extra entries and everything else caches exactly as before.
                if (subpixel)
                    key = key * 397 ^ ((long) ToByte(solid.Color.R) << 16
                                     | (long) ToByte(solid.Color.G) << 8
                                     | ToByte(solid.Color.B));
                if (!_maskCache.TryGetValue(key, out CachedMask? cm))
                {
                    PerfCoverage++;
                    PathGeometry normGeom = (gminX == 0f && gminY == 0f)
                        ? coverageGeometry
                        : TransformGeometry(coverageGeometry, Matrix3x2.CreateTranslation(-gminX, -gminY));
                    // normGeom is at local origin; transform by the world LINEAR part + the sub-pixel phase.
                    Matrix3x2 phased = world;
                    phased.M31 = phaseX;
                    phased.M32 = phaseY;
                    IntPtr tex, view;
                    int mox, moy, mw, mh;
                    if (subpixel)
                    {
                        // Rasterized on the CPU even where the GPU rasterizer is on: the subpixel
                        // mask is three times as wide before it is filtered down, and the coverage
                        // shader has no notion of that. Glyph masks are cached by shape, so this is
                        // paid once per glyph per size, not once per frame.
                        PathRasterizer.SubpixelRowsForRun = _symmetricSmoothing ? SymmetricRows : 0;
                        PathRasterizer.SubpixelMask sm;
                        try { sm = PathRasterizer.RasterizeSubpixel(TransformGeometry(normGeom, phased)); }
                        finally { PathRasterizer.SubpixelRowsForRun = 0; }
                        if (sm.IsEmpty) return;
                        // Corrected AFTER the filter, and it was worth checking which way round:
                        // correcting the raw lamps first is the tidier story (a linear filter then
                        // carries corrected ink about unchanged) and measures slightly WORSE on every
                        // count -- 1307 disagreeing pixels against 1196. It also does not straighten
                        // the size tilt, which is how we know the tilt is not about this order.
                        ApplySubpixelWeight(sm.Rgba, gamma, textBlend, solid.Color);
                        (tex, view) = CreateRgbaTexture(sm.Rgba, sm.Width, sm.Height);
                        mox = (int)sm.OriginX; moy = (int)sm.OriginY; mw = sm.Width; mh = sm.Height;
                        cm = new CachedMask
                        {
                            Tex = tex, View = view,
                            BindGroup = CreateSampledBindGroup(format, FillKind.TextSubpixelMultiply, view, NearestSampler()),
                            BindGroupAdd = CreateSampledBindGroup(format, FillKind.TextSubpixelAdd, view, NearestSampler()),
                            Subpixel = true,
                            Sampler = NearestSampler(), Format = format,
                            Ox = mox, Oy = moy, W = mw, H = mh,
                        };
                        _maskCache[key] = cm;
                        cm.LastFrame = _frameId;
                        EmitCachedSolidMask(cm, solid.Color, opacity, clip, width, height, data, ox, oy);
                        return;
                    }
                    // Only the GPU path's target comes from the mask POOL; the CPU path uploads a
                    // sampling-only texture. See CachedMask.Pooled.
                    bool pooled = s_gpuRaster;
                    if (s_gpuRaster)
                    {
                        if (!GpuRasterizeCoverage(TransformGeometry(normGeom, phased), gamma,
                                out tex, out view, out mox, out moy, out mw, out mh, textBlend))
                            return;
                    }
                    else
                    {
                        CoverageMask m = PathRasterizer.Rasterize(TransformGeometry(normGeom, phased));
                        if (textBlend) ApplyTextBlend(m.Coverage);
                        if (m.IsEmpty) return;
                        if (_aliasedEdges) ApplyAliasedEdges(m.Coverage);
                        if (gamma) ApplyTextGamma(m.Coverage);
                        (tex, view) = CreateR8Texture(m.Coverage, m.Width, m.Height);
                        mox = (int)m.OriginX; moy = (int)m.OriginY; mw = m.Width; mh = m.Height;
                    }
                    IntPtr bg = CreateSampledBindGroup(format, FillKind.Text, view, NearestSampler());
                    cm = new CachedMask { Tex = tex, View = view, BindGroup = bg, Sampler = NearestSampler(), Format = format,
                        Ox = mox, Oy = moy, W = mw, H = mh, Pooled = pooled };
                    _maskCache[key] = cm;   // cache owns these (NOT defer-released); evicted in EndFrame
                }
                cm.LastFrame = _frameId;
                EmitCachedSolidMask(cm, solid.Color, opacity, clip, width, height, data, ox, oy);
                return;
            }
            PerfCoverage++;
            // Non-solid fills: rasterize coverage + evaluate the brush entirely on the GPU
            // (fs_maskbrush for gradients, fs_maskimage for image/tile brushes) — no CPU
            // per-texel bake. Only unsupported brushes fall back to the CPU EmitMask bake.
            if (s_gpuRaster)
            {
                if (IsGpuGradient(brush) &&
                    EmitGpuGradientMask(coverageGeometry, brush, world, opacity, clip, width, height, format, data))
                    return;
                if (brush is ImageBrush imgBrush &&
                    EmitGpuImageMask(coverageGeometry, imgBrush, world, opacity, clip, width, height, format, data))
                    return;
            }
            if (isGlyph && brush is SolidColorBrush subpixelSolid
                && TryEmitSubpixelGlyph(coverageGeometry, subpixelSolid, world, opacity, clip,
                                        width, height, format, data))
                return;

            EmitMask(PathRasterizer.Rasterize(coverageGeometry), brush, world, opacity, clip, width, height, format, data, isGlyph);
        }

        /// <summary>Whether text is resolved onto the three lamps of each pixel rather than to grey.
        /// <para>On by default wherever it can be done, because it is what the platform does and what
        /// the application beside ours looks like.</para></summary>
        internal bool ClearType
        {
            get => _clearType;
            set
            {
                _clearType = value;
                // Subpixel rendering and vertical-only hinting are two halves of one decision, and
                // the outline has to be fitted the right way before it ever reaches the rasterizer --
                // so this is set here, where the mode is chosen, not down in the draw where the mask
                // is made. The glyph caches are keyed by it, so the two modes cannot cross.
                Text.TrueTypeFont.SubpixelFitting = value;
                // And which MODE is being drawn, which is not the same question. The hinting rules
                // ask a face whether ClearType is on (GETINFO) and round on a sixteenth of a pixel
                // when it is; with ClearType off, GDI's rasterizer answers no and rounds on whole
                // pixels. Kept apart from SubpixelFitting deliberately -- the stage tests toggle
                // that to compare the two fits and must not change what the face is told.
                Text.TrueTypeFont.ClearTypeRendering = value;
            }
        }
        private bool _clearType = InitClearType();

        /// <summary>Whether the user has ClearType on at all, and whether they want it.
        /// <para>This answered "yes, always". Windows has TWO settings ahead of the ones we already
        /// read: font smoothing can be OFF, and when it is on it can be STANDARD rather than
        /// ClearType, in which case GDI draws grey. Answering yes regardless puts coloured fringes
        /// on every glyph on a desktop where every other application has just gone grey -- the same
        /// shape of gap as having the panel's lamp order written in, and more visible.</para>
        /// <para>Off Windows this is ClearType, as it was: every other head emulates the Windows
        /// text stack and has no such setting to read.</para></summary>
        private static bool InitClearType()
        {
            // WPF_CLEARTYPE forces either answer, so the other mode can be exercised on a machine
            // that is not set to it -- and so that "should we follow the setting at all?" is a
            // measurement rather than an assumption.
            string? forced = Environment.GetEnvironmentVariable("WPF_CLEARTYPE");
            bool on = forced == "1" || forced == "0"
                ? forced == "1"
                : Platform.Win32Interop.FontSmoothingKind() == 2;
            Text.TrueTypeFont.SubpixelFitting = on;
            // Both, and for the same reason the property setter sets both: this runs as a FIELD
            // INITIALIZER and never goes through that setter, so anything set only there is missed
            // for the whole life of the renderer.
            Text.TrueTypeFont.ClearTypeRendering = on;
            return on;
        }

        /// <summary>Draw a glyph run the way ClearType does, or say that this one cannot be.</summary>
        /// <remarks>
        ///  Three things have to hold, and each of them is a reason Windows itself turns ClearType off:
        ///  <list type="bullet">
        ///  <item>The target must be OPAQUE. Subpixel coverage is a statement about a lamp behind a
        ///  known colour; composited into something transparent it is a lie, and the fringe surfaces
        ///  later against whatever the layer lands on. WPF drops ClearType on layered windows for the
        ///  same reason.</item>
        ///  <item>The transform must be AXIS-ALIGNED. The three lamps are laid out along the screen's
        ///  x axis, so a rotated glyph's subpixels are not along its own.</item>
        ///  <item>The glyph must be on the PIXEL GRID, which for us means it went through the face's
        ///  hinting. Text placed at a fraction of a pixel gains nothing from three times the
        ///  horizontal resolution and picks up a fringe that moves as it scrolls.</item>
        ///  </list>
        /// </remarks>
        private bool TryEmitSubpixelGlyph(PathGeometry geometry, SolidColorBrush solid, Matrix3x2 world,
                                          double opacity, Scissor clip, int width, int height,
                                          WGPUTextureFormat format, DrawData data)
        {
            if (!ClearType || _transparentTarget || !IsAxisAligned(world)) return false;

            PathRasterizer.SubpixelMask mask = PathRasterizer.RasterizeSubpixel(geometry);
            if (mask.IsEmpty) return false;

            (IntPtr tex, IntPtr view) = CreateRgbaTexture(mask.Rgba, mask.Width, mask.Height);
            IntPtr multiplyBind = CreateSampledBindGroup(format, FillKind.TextSubpixelMultiply, view, NearestSampler());
            IntPtr addBind = CreateSampledBindGroup(format, FillKind.TextSubpixelAdd, view, NearestSampler());
            DeferReleaseSampled(tex, view, multiplyBind);
            DeferReleaseBindGroup(addBind);

            float r = solid.Color.R, g = solid.Color.G, b = solid.Color.B;
            float a = (float)Math.Clamp(solid.Color.A * opacity, 0.0, 1.0);

            float x0 = mask.OriginX, y0 = mask.OriginY;
            float x1 = x0 + mask.Width, y1 = y0 + mask.Height;

            uint baseVertex = (uint)(data.Verts.Count / FloatsPerVertex);
            AddVertex(data.Verts, ToNdc(Vector2.Transform(new Vector2(x0, y0), world), width, height), r, g, b, a, 0f, 0f);
            AddVertex(data.Verts, ToNdc(Vector2.Transform(new Vector2(x1, y0), world), width, height), r, g, b, a, 1f, 0f);
            AddVertex(data.Verts, ToNdc(Vector2.Transform(new Vector2(x1, y1), world), width, height), r, g, b, a, 1f, 1f);
            AddVertex(data.Verts, ToNdc(Vector2.Transform(new Vector2(x0, y1), world), width, height), r, g, b, a, 0f, 1f);

            uint firstIndex = (uint)data.Indices.Count;
            AddQuadIndices(data.Indices, baseVertex);

            // The SAME quad twice, in this order: take the destination down by the coverage, then add
            // the ink. Swapping them adds ink to a destination that is then dimmed, which is a
            // different (and wrong) picture.
            data.Draws.Add(new DrawItem(firstIndex, 6, clip, FillKind.TextSubpixelMultiply, multiplyBind));
            data.Draws.Add(new DrawItem(firstIndex, 6, clip, FillKind.TextSubpixelAdd, addBind));
            return true;
        }

        // EdgeMode.Aliased on the CPU rasterizer: same half-covered threshold the coverage
        // shader applies, so the two rasterizers agree on what "aliased" looks like.
        private static void ApplyAliasedEdges(byte[] coverage)
        {
            for (int i = 0; i < coverage.Length; i++)
                coverage[i] = coverage[i] >= 128 ? (byte)255 : (byte)0;
        }

        // Re-map glyph coverage through the text-gamma LUT (in place) so text blends
        // with WPF-matching weight on the display-destined sRGB path.
        /// <summary>Coverage as it has to be for a gamma-space blend to land where a linear-light
        /// one would. The same correction the coverage shader applies; this is the CPU rasterizer's
        /// copy of it.</summary>
        private static void ApplyTextBlend(byte[] coverage)
        {
            for (int i = 0; i < coverage.Length; i++)
                coverage[i] = s_textBlendLut[coverage[i]];
        }

        private static readonly byte[] s_textBlendLut = BuildTextBlendLut();

        /// <summary>The curve subpixel coverage goes through on its way to the screen, which is NOT
        /// the one grey text uses.
        /// <para>Grey text is corrected by squaring, which is a gamma of 2 and is what makes its
        /// weight land on Windows'. Subpixel text through the same curve comes out far too light,
        /// because ClearType is not a lighter grey -- it lays ink on three lamps and Windows drives
        /// them much harder, reaching FULL coverage on subpixels where our grey mask is at three
        /// quarters.</para>
        /// <para>1.1 is measured, not derived: swept against GDI's own ClearType. Squaring instead
        /// (gamma 2, what grey text uses) costs 766 more pixels of disagreement. The curve is a
        /// stand-in for the contrast enhancement Windows applies, which is a tunable of the machine
        /// and not a constant we can read off anything.</para>
        ///
        /// <para>IT IS CHOSEN FOR THE SIZES PEOPLE READ, and it is worth knowing why it cannot simply
        /// be chosen for all of them. Our weight against Windows' is not off by a constant, it is
        /// TILTED: at eleven pixels an em we lay down 0.91 of its ink and at nineteen we lay down
        /// 1.08. A gamma slides that whole line up and down and cannot rotate it, so no single value
        /// is right everywhere -- 1.1 puts eleven to fourteen pixels an em, which is where interface
        /// text lives, closest.</para>
        ///
        /// <para>The tilt is NOT a curve problem, and four attempts to make it one all failed: a
        /// contrast curve applied before the filter instead of after; the face's control values
        /// allowed to set stem widths, rounded to a whole pixel; the same rounded to a third; and the
        /// same not rounded at all. Every one of them was measured and every one was worse. Stem
        /// POSITION was claimed to reproduce GDI exactly, citing a TrueTypeInterpreter.RoundToThirds
        /// that NO LONGER EXISTS -- see TrueTypeFont.SubpixelFitting, where x is kept at its plain
        /// scaled value and lands on arbitrary fractions (mean 0.286 px from a pixel boundary). The
        /// claim was stale. Rounding x was swept in 2026-08-28 and every setting made the rendered
        /// coverage worse, so leaving it alone is still right -- but "the stems are where GDI puts
        /// them" is not an argument that can be leaned on.</para>
        ///
        /// <para>What is left unexplained is stem WIDTH: ours ramps smoothly with the size where
        /// Windows' holds and steps. Whatever Windows does about that is not any of the four things
        /// above.</para>
        ///
        /// <para>The exponent itself is not the tilt either, and now there is a number for that too.
        /// TheWholeRepertoire_CarriesAsMuchInkAsWindows weighs our ink against GDI's over the whole
        /// repertoire at every size and face; sweeping this constant across 1.0, 1.05, 1.1, 1.15 and
        /// 1.2 gives mean disagreements of 3.52%, 2.13%, 1.23%, 1.91% and 2.92%. 1.1 is the floor, so
        /// there is nothing left to win by moving it.</para>
        ///
        /// <para>What that measurement DOES say is where the tilt lives: bold, italic and bold-italic
        /// land within about 1% of GDI at every size from 10 to 20, and the REGULAR face alone misses
        /// -- 8% light at 11, 3.6% at 12, crossing over to 6% heavy by 19. One face, not the curve,
        /// and the thinnest stems of the four.</para>
        ///
        /// <para>The sweep figures above were taken over the GREEN lamp alone, before that test was
        /// corrected to weigh all three (a subpixel filter moves ink sideways between lamps, so one
        /// lamp is not a conserved quantity). The ordering is what matters and it did not change.</para>
        ///
        /// <para>RE-SWEPT after PathRasterizer stopped antialiasing text vertically, which changed
        /// the coverage distribution this curve is applied to and moved the optimum to 1.15. Mean
        /// ink disagreement across 0.9/1.0/1.05/1.1/1.15/1.2/1.3 is now
        /// 6.53%/3.83%/2.62%/1.53%/0.86%/1.44%/3.43%, and the live WinForms window agrees
        /// (2,555,219 -> 2,551,427 -> 2,564,657 for 1.1/1.15/1.2). A curve tuned against one
        /// coverage model does not stay tuned when the model changes -- re-sweep it after any
        /// rasteriser change, not just after a change to the curve.</para>
        /// </summary>
        /// <summary>Whether the contrast is applied by BLENDING in gamma space rather than as a
        /// power on the coverage. ON.
        /// <para>"The contrast value used in the ClearType algorithm" is a blend parameter, and for
        /// black ink on white paper a gamma-space blend reduces to 1-(1-c)^(1/g) where ours was
        /// c^g. The two are the same curve only at g = 1.</para>
        /// <para>The evidence that this is GDI's shape is not that it measures better, though it
        /// does -- 681,433 against 759,520. It is that it makes the exponent COME OUT RIGHT. With
        /// c^g the best exponent was 0.75 * contrast + 0.25, three quarters of the way from 1 to the
        /// user's setting, which is the shape of an approximation. With the blend it is the setting
        /// EXACTLY, at every contrast measured: 1.00 at 1000, 1.20 at 1200, 1.40 at 1400, each a
        /// sharp minimum with both neighbours 60,000 or more worse.</para>
        /// <para>And the error stops depending on the setting. With c^g it ran 688k / 759k / 827k
        /// across those three contrasts, because the fit was only good near where it was tuned; with
        /// the blend it is 688k / 681k / 683k. A model that is right does not get worse when the
        /// user changes a preference.</para>
        /// <para>WPF_SUBPIXEL_CURVE=power restores the old shape.</para></summary>
        private static readonly bool SubpixelCurveIsBlend =
            Environment.GetEnvironmentVariable("WPF_SUBPIXEL_CURVE") != "power";

        private static readonly float SubpixelGamma =
            float.TryParse(Environment.GetEnvironmentVariable("WPF_SUBPIXEL_GAMMA"),
                           System.Globalization.NumberStyles.Float,
                           System.Globalization.CultureInfo.InvariantCulture, out float g)
                ? g : GammaForSystemContrast();

        /// <summary>The exponent that matches the ClearType CONTRAST the user has set.
        /// <para>1.15 was measured against Windows on a machine set to 1200 and then written down as
        /// a constant, which is only right on that machine -- and the Windows DEFAULT is 1400, where
        /// a constant 1.15 costs about six percent (879,159 against 827,148 on the text specimen).
        /// </para>
        /// <para>It is the setting itself, once the contrast is applied the way GDI applies it --
        /// see SubpixelCurveIsBlend. Measured at 1000, 1200 and 1400 by setting the system contrast
        /// and re-sweeping against Windows' own rendering: the optimum is 1.00, 1.20 and 1.40, each
        /// a sharp minimum. The 0.75 * contrast + 0.25 that the old power curve needed was the
        /// approximation showing through.</para>
        /// <para>Off Windows, and if the setting cannot be read, 1.2 -- which is not a Windows
        /// default (that is 1400) but the value that leaves those platforms looking as they did:
        /// blending at 1.2 and the old power curve at 1.15 agree to within a percent through the
        /// midtones. There is no ClearType contrast to honour there and no reference to measure
        /// against, so this is not the place to change how they look.</para></summary>
        /// <summary>The text gamma, exposed so a test can UNDO the contrast curve and compare
        /// coverage rather than luminance.</summary>
        internal static float TextGammaForTest => SubpixelGamma;

        private static float GammaForSystemContrast()
        {
            int contrast = Platform.Win32Interop.FontSmoothingContrast();
            if (contrast < 1000 || contrast > 2200) return 1.2f;
            return contrast / 1000f;
        }

        /// <summary>How hard the curve pushes coverage AWAY from the middle, after the gamma.
        /// <para>A power curve can only slide the whole line up or down, and what is left disagreeing
        /// with GDI is not a slide. With no correction at all our ink runs 1.02-1.05 of GDI's for
        /// bold, italic and bold-italic at every size, and for the regular face at 13 pixels an em
        /// and up -- but 0.97-1.03 for the REGULAR face at 10, 11 and 12, where its stems are about
        /// one pixel wide. So GDI keeps more ink than the pattern predicts exactly where a stem is
        /// thin and its lamps are half covered, which is what a CONTRAST curve does and a gamma
        /// cannot: it darkens what is already more than half covered and leaves the rest.</para>
        /// <para>WPF_SUBPIXEL_CONTRAST sweeps it; 0 is the plain power curve, and 0 is what it stays.
        /// MEASURED: k of 0/0.3/0.6 moves the regular face at 11 pixels an em 0.9204/0.9186/0.9158 --
        /// the wrong way and barely at all, because after the gamma a thin stem's lamps sit BELOW a
        /// half, where a curve that pushes away from the middle lightens them. Structural does not
        /// move at all (274 throughout). This is the second curve family to fail, and it fails for the
        /// reason the first one did: the disagreement is SIZE-dependent and a curve is not. Keep the
        /// knob, do not keep looking for a shape.</para></summary>
        private static readonly float SubpixelContrast =
            float.TryParse(Environment.GetEnvironmentVariable("WPF_SUBPIXEL_CONTRAST"),
                           System.Globalization.NumberStyles.Float,
                           System.Globalization.CultureInfo.InvariantCulture, out float k) ? k : 0f;

        /// <summary>Whether the contrast curve goes through the lamps BEFORE the filter (which is
        /// the order GDI's pipeline is described in) or after it (which is what measured better).
        /// <para>Re-testable rather than re-arguable: WPF_SUBPIXEL_CORRECT=before switches it. The
        /// order was settled once under the OLD coverage model and the raw lamps have since become
        /// three-valued, so the arithmetic it was settled on no longer exists.</para>
        /// <para>RE-MEASURED, and AFTER still wins. Corrected before the filter the curve needs a much
        /// steeper exponent to do the same work (its best is gamma 1.8, where mean ink disagreement is
        /// 0.88% against 0.89% -- a tie) but it costs structural accuracy heavily: 684 pixels against
        /// 274.</para>
        /// <para>Re-measured on the text specimen, which is repeatable to 41 and does not depend on
        /// the structural metric that reading came from: 841,250 against 759,520. The conclusion
        /// stands and THE REASON GIVEN FOR IT WAS WRONG. It said correcting three-valued lamps
        /// quantizes the curve to three points, so the filter spreads a coarse result -- but the
        /// number does not move when the lamps are given more levels, or none at all: 841,244 at
        /// thirteen levels and 841,212 unquantized, against 841,250 at seven. The cost is inherent
        /// to correcting before the filter rather than after it, and has nothing to do with how many
        /// levels a lamp has.</para>
        /// <para>The first attempt at this measurement was WRONG and said "before" was inert. This
        /// field was declared BELOW s_subpixelLut, and a static field initializer runs in declaration
        /// order, so BuildSubpixelLut read it as false and never handed the table over: the run was
        /// measuring no correction at all. A knob that is read too early is indistinguishable from a
        /// parameter that does nothing -- if a sweep comes back perfectly flat, check the ORDER before
        /// believing it.</para></summary>
        private static readonly bool s_correctBeforeFilter =
            Environment.GetEnvironmentVariable("WPF_SUBPIXEL_CORRECT") == "before";

        private static readonly byte[] s_subpixelLut = BuildSubpixelLut();

        private static byte[] BuildSubpixelLut()
        {
            var lut = new byte[256];
            for (int i = 0; i < 256; i++)
            {
                // TWO READINGS OF THE CONTRAST VALUE. Ours darkens the coverage itself, c^g. The
                // other is what "the contrast used in the ClearType algorithm" most naturally means
                // -- blend in gamma space -- which for black ink on white paper reduces to
                // 1-(1-c)^(1/g). If that is the shape GDI uses, the exponent that fits it should be
                // the system contrast EXACTLY rather than three quarters of the way to it, which is
                // what the c^g form needs.
                float c = SubpixelCurveIsBlend
                    ? 1f - MathF.Pow(1f - i / 255f, 1f / SubpixelGamma)
                    : MathF.Pow(i / 255f, SubpixelGamma);
                // Symmetric about a half, so it cannot change which side of the middle a value is on
                // and cannot invert an edge; k = 1 is the full cubic.
                if (SubpixelContrast != 0f)
                    c += SubpixelContrast * c * (1f - c) * (2f * c - 1f);
                lut[i] = (byte)MathF.Round(Math.Clamp(c, 0f, 1f) * 255f);
            }
            if (s_correctBeforeFilter) PathRasterizer.PreFilterLut = lut;
            return lut;
        }

        private static byte[] BuildTextBlendLut()
        {
            var lut = new byte[256];
            for (int i = 0; i < 256; i++)
            {
                float c = i / 255f;
                lut[i] = (byte)MathF.Round(c * c * 255f);
            }
            return lut;
        }

        private static void ApplyTextGamma(byte[] coverage)
        {
            for (int i = 0; i < coverage.Length; i++)
                coverage[i] = s_textGammaLut[coverage[i]];
        }

        /// <summary>Put a subpixel mask's three lamps through the same weight correction a grey mask
        /// gets, and recompute the alpha from what comes out.
        /// <para>Alpha is recomputed rather than corrected: it is the MEAN of the three, and the mean
        /// of three corrected values is not the correction of their mean. Correcting it separately
        /// left the destination dimmed by a different amount from the ink that replaced it, which
        /// shows as a pale halo around every glyph.</para></summary>
        /// <summary>The contrast curve for ink of a given channel value, against WHITE paper.
        /// <para>Our composite is a linear blend, `dst*(1-c') + fg*c'`, with the contrast folded into
        /// c'. GDI's is a blend in gamma space, `((1-cov)*dst^g + cov*fg^g)^(1/g)`. The two agree
        /// EXACTLY when the ink is black -- the fg^g term vanishes -- which is why black text on any
        /// background is right and why nothing here mattered while the specimen was black on white.
        /// They part company as the ink lightens: measured against Windows, error per unit of ink
        /// runs 0.1005 / 0.1201 / 0.1283 / 0.1300 for black / #404040 / #808080 / #A0A0A0, and our
        /// ink goes from 3.5% light to 8% light over the same range.</para>
        /// <para>Solving the linear blend for the gamma blend's answer on WHITE paper gives
        /// c' = (1 - T)/(1 - fg) with T = ((1-cov) + cov*fg^g)^(1/g). A two-pass linear blend cannot
        /// be exact for an arbitrary foreground AND an arbitrary destination -- (A+B)^(1/g) is not
        /// A^(1/g) + B^(1/g) -- so white paper is the achievable target, and it is the common one.
        /// At fg = 0 this reduces to the curve that was there before.</para></summary>
        private static byte[] SubpixelLutForInk(byte fg)
        {
            if (fg == 0) return s_subpixelLut;
            byte[]? cached = s_inkLuts[fg];
            if (cached is not null) return cached;

            float g = SubpixelGamma, f = fg / 255f;
            var lut = new byte[256];
            float fgPow = MathF.Pow(f, g);
            for (int i = 0; i < 256; i++)
            {
                float cov = i / 255f;
                float t = MathF.Pow((1f - cov) + cov * fgPow, 1f / g);
                // 1 - fg is the most ink this channel can carry; as it goes to zero there is no ink
                // to place and the curve is meaningless, so fall back rather than divide by it.
                float c = f >= 0.999f ? cov : (1f - t) / (1f - f);
                lut[i] = (byte) MathF.Round(Math.Clamp(c, 0f, 1f) * 255f);
            }
            return s_inkLuts[fg] = lut;
        }

        private static readonly byte[]?[] s_inkLuts = new byte[]?[256];

        private static void ApplySubpixelWeight(byte[] rgba, bool gamma, bool textBlend, RgbaColor ink)
        {
            // Applied whichever correction the grey path would have wanted, because the reason is the
            // same one -- coverage is not brightness -- and only the curve differs.
            if (!gamma && !textBlend) return;
            if (s_correctBeforeFilter)
            {
                // Already put through the curve on the way in (PreFilterLut, handed over when the
                // table was built -- it has to be in place before the FIRST glyph is rasterized, not
                // when the first one is corrected); only alpha is still owed.
                for (int j = 0; j < rgba.Length; j += 4)
                    rgba[j + 3] = (byte)((rgba[j] + rgba[j + 1] + rgba[j + 2]) / 3);
                return;
            }
            // One curve per LAMP, because each lamp lights a different channel of the ink.
            byte[] lutR = SubpixelLutForInk(ToByte(ink.R));
            byte[] lutG = SubpixelLutForInk(ToByte(ink.G));
            byte[] lutB = SubpixelLutForInk(ToByte(ink.B));
            for (int i = 0; i < rgba.Length; i += 4)
            {
                int total = 0;
                for (int c = 0; c < 3; c++)
                {
                    byte v = (c == 0 ? lutR : c == 1 ? lutG : lutB)[rgba[i + c]];
                    rgba[i + c] = v;
                    total += v;
                }
                rgba[i + 3] = (byte)(total / 3);
            }
        }

        // Emit a quad sampling a cached coverage mask, tinted by the solid colour. The mask is
        // rasterized translation-free; ox/oy re-apply the integer device-pixel offset.
        private void EmitCachedSolidMask(CachedMask c, RgbaColor color, double opacity, Scissor clip, int width, int height, DrawData data, float ox = 0, float oy = 0)
        {
            if (clip.IsEmpty) return;
            float r = color.R, g = color.G, b = color.B, a = (float)Math.Clamp(color.A * opacity, 0.0, 1.0);
            float x0 = c.Ox + ox, y0 = c.Oy + oy, x1 = x0 + c.W, y1 = y0 + c.H;
            uint baseVertex = (uint)(data.Verts.Count / FloatsPerVertex);
            AddVertex(data.Verts, ToNdc(new Vector2(x0, y0), width, height), r, g, b, a, 0f, 0f);
            AddVertex(data.Verts, ToNdc(new Vector2(x1, y0), width, height), r, g, b, a, 1f, 0f);
            AddVertex(data.Verts, ToNdc(new Vector2(x1, y1), width, height), r, g, b, a, 1f, 1f);
            AddVertex(data.Verts, ToNdc(new Vector2(x0, y1), width, height), r, g, b, a, 0f, 1f);
            uint firstIndex = (uint)data.Indices.Count;
            AddQuadIndices(data.Indices, baseVertex);
            if (c.Subpixel)
            {
                // Dim the destination by the coverage, then add the ink: source-over written out one
                // channel at a time, which is the whole reason there are two of them.
                data.Draws.Add(new DrawItem(firstIndex, 6, clip, FillKind.TextSubpixelMultiply, c.BindGroup));
                data.Draws.Add(new DrawItem(firstIndex, 6, clip, FillKind.TextSubpixelAdd, c.BindGroupAdd));
                return;
            }
            data.Draws.Add(new DrawItem(firstIndex, 6, clip, FillKind.Text, MaskBindGroup(c), sourceCopy: _srcCopy));
        }

        // Local bounding-box minimum of a geometry (over all figure start/segment points). Used to make
        // the coverage-mask cache TRANSLATION-INVARIANT: WPF sends each glyph as an outline fill positioned
        // in local space, so without normalizing by this origin every glyph instance hashes uniquely and
        // never dedups (a text-heavy page then rasterizes thousands of masks — the "What's New" crash).
        /// <summary>Memoized <see cref="GeometryMin"/> - see the fields on PathGeometry.</summary>
        private static void GeometryMinCached(PathGeometry g, out float minX, out float minY)
        {
            if (!g.MinValid)
            {
                GeometryMin(g, out g.MinX, out g.MinY);
                g.MinValid = true;
            }
            minX = g.MinX;
            minY = g.MinY;
        }

        /// <summary>
        /// Memoized hash of the geometry normalized to its own minimum. (dx, dy) is always
        /// -(MinX, MinY), i.e. itself derived from the geometry, so the result is a pure function of
        /// the instance and safe to cache on it.
        /// </summary>
        private static long NormalizedHashCached(PathGeometry g, float dx, float dy)
        {
            if (!g.NormHashValid)
            {
                g.NormHash = HashGeometry(g, dx, dy);
                g.NormHashValid = true;
            }
            return g.NormHash;
        }

        private static void GeometryMin(PathGeometry g, out float minX, out float minY)
        {
            minX = float.MaxValue; minY = float.MaxValue;
            static void Acc(Vector2 v, ref float mnX, ref float mnY) { if (v.X < mnX) mnX = v.X; if (v.Y < mnY) mnY = v.Y; }
            foreach (PathFigure f in g.Figures)
            {
                Acc(f.Start, ref minX, ref minY);
                foreach (PathSegment s in f.Segments)
                    switch (s)
                    {
                        case LineSegment l: Acc(l.Point, ref minX, ref minY); break;
                        case QuadraticBezierSegment q: Acc(q.Control, ref minX, ref minY); Acc(q.Point, ref minX, ref minY); break;
                        case CubicBezierSegment c: Acc(c.Control1, ref minX, ref minY); Acc(c.Control2, ref minX, ref minY); Acc(c.Point, ref minX, ref minY); break;
                    }
            }
            if (minX == float.MaxValue) { minX = 0; minY = 0; }
        }

        private static long HashGeometry(PathGeometry g) => HashGeometry(g, 0f, 0f);

        /// <summary>
        /// Hash of <paramref name="g"/> as if every point had been translated by (dx, dy).
        /// Hashing the translation in rather than materializing a translated copy matters: the
        /// mask-cache key is computed for EVERY fill on EVERY frame, so building that copy first
        /// allocated a full geometry per glyph per frame even when the lookup then HIT the cache
        /// (measured at ~830KB/frame for 240 short text runs). The copy is now only built on a
        /// miss, where it is genuinely needed for rasterization.
        /// </summary>
        private static long HashGeometry(PathGeometry g, float dx, float dy)
        {
            long h = 17 * 31 + (int)g.FillRule;
            long HashV(Vector2 v) => ((long)BitConverter.SingleToInt32Bits(v.X + dx) << 32) ^ (uint)BitConverter.SingleToInt32Bits(v.Y + dy);
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
            Scissor clip, int width, int height, WGPUTextureFormat format, DrawData data, bool isGlyph = false)
        {
            if (clip.IsEmpty || mask.IsEmpty) return;

            // Same gate as EmitCoverageMask: linear-blending passes only. Applying this in gamma-space
            // mode double-counts the correction and puts ~23% excess alpha on glyph edges.
            if (isGlyph && !_transparentTarget && _srgbOutput) ApplyTextGamma(mask.Coverage);

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
                byte[] rgba = BakeBrushMask(mask, brush, _srgbOutput);
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
            AddQuadIndices(data.Indices, baseVertex);
            data.Draws.Add(new DrawItem(firstIndex, 6, clip, kind, bindGroup));
        }

        // Evaluates a non-solid brush at each coverage texel, producing a straight
        // RGBA mask (rgb = brush colour, a = coverage * brush alpha) that the
        // premultiplying fs_textured pipeline composites correctly.
        // <paramref name="decodeSrgb"/> mirrors CreateImageTexture: when the final target is sRGB,
        // image samples are sRGB-encoded bytes that must be decoded to linear (the direct image path
        // gets this from the hardware sRGB texture format) before they enter the linear bake buffer,
        // which is uploaded as RGBA8Unorm. Without it tiled/DrawingBrush images come out too light.
        private static byte[] BakeBrushMask(CoverageMask mask, Brush brush, bool decodeSrgb)
        {
            var rgba = new byte[mask.Width * mask.Height * 4];
            for (int y = 0; y < mask.Height; y++)
            {
                for (int x = 0; x < mask.Width; x++)
                {
                    int i = (y * mask.Width + x);
                    float cov = mask.Coverage[i] / 255f;
                    var localPos = new Vector2(mask.OriginX + x + 0.5f, mask.OriginY + y + 0.5f);
                    RgbaColor c = EvaluateBrush(brush, localPos, mask, decodeSrgb);
                    rgba[i * 4 + 0] = ToByte(c.R);
                    rgba[i * 4 + 1] = ToByte(c.G);
                    rgba[i * 4 + 2] = ToByte(c.B);
                    rgba[i * 4 + 3] = ToByte(cov * c.A);
                }
            }
            return rgba;
        }

        private static RgbaColor EvaluateBrush(Brush brush, Vector2 localPos, CoverageMask mask, bool decodeSrgb)
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
                    return SampleBilinear(img, u, v, decodeSrgb);
                }
                case SolidColorBrush solid:
                    return solid.Color;
                default:
                    return new RgbaColor(0, 0, 0, 0);
            }
        }

        // Bilinear sample of a straight-RGBA image at uv in [0,1] (edge-clamped). Interpolates in
        // PREMULTIPLIED space so transparent texels don't bleed dark fringes into opaque edges, then
        // returns straight RGBA (matching WPF's smooth tile/image sampling instead of blocky nearest).
        // When <paramref name="decodeSrgb"/>, each texel's RGB is decoded sRGB->linear first (alpha
        // stays linear), reproducing the hardware sRGB texture path so baked tiles match direct images.
        private static RgbaColor SampleBilinear(ImageBrush img, float u, float v, bool decodeSrgb)
        {
            int w = img.PixelWidth, h = img.PixelHeight;
            float fx = u * w - 0.5f, fy = v * h - 0.5f;
            int x0 = (int)MathF.Floor(fx), y0 = (int)MathF.Floor(fy);
            float tx = fx - x0, ty = fy - y0;
            int x1 = Math.Clamp(x0 + 1, 0, w - 1), y1 = Math.Clamp(y0 + 1, 0, h - 1);
            x0 = Math.Clamp(x0, 0, w - 1); y0 = Math.Clamp(y0, 0, h - 1);
            byte[] px = img.PixelsRgba;
            (float r, float g, float b, float a) Pm(int x, int y)
            {
                int p = (y * w + x) * 4;
                float a = px[p + 3] / 255f;
                float r = px[p] / 255f, g = px[p + 1] / 255f, b = px[p + 2] / 255f;
                if (decodeSrgb) { r = SrgbToLinear(r); g = SrgbToLinear(g); b = SrgbToLinear(b); }
                return (r * a, g * a, b * a, a);
            }
            var c00 = Pm(x0, y0); var c10 = Pm(x1, y0); var c01 = Pm(x0, y1); var c11 = Pm(x1, y1);
            float L(float a, float b, float t) => a + (b - a) * t;
            float pr = L(L(c00.r, c10.r, tx), L(c01.r, c11.r, tx), ty);
            float pg = L(L(c00.g, c10.g, tx), L(c01.g, c11.g, tx), ty);
            float pb = L(L(c00.b, c10.b, tx), L(c01.b, c11.b, tx), ty);
            float pa = L(L(c00.a, c10.a, tx), L(c01.a, c11.a, tx), ty);
            return pa > 1e-6f ? new RgbaColor(pr / pa, pg / pa, pb / pa, pa) : new RgbaColor(0, 0, 0, 0);
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
                case ImageBrush img when img.PixelsRgba.Length > 0 && img.PixelWidth > 0 && img.PixelHeight > 0:
                {
                    // A VisualBrush or DrawingBrush, already rasterized to pixels by
                    // RealizeContentBrushes. WPF masks with these routinely -- GroupBox's default
                    // template gaps its border around the header with exactly this, a VisualBrush
                    // from BorderGapMaskConverter -- and falling through to "fully opaque" below
                    // drew the border straight through the header text.
                    if (!Matrix3x2.Invert(world, out Matrix3x2 inv))
                    {
                        Array.Fill(bytes, (byte)255);
                        break;
                    }

                    // The brush paints one tile across TileWidth x TileHeight of its own space,
                    // which the bitmap covers; go from device space back to that.
                    float tw = img.TileWidth > 0f ? img.TileWidth : img.PixelWidth;
                    float th = img.TileHeight > 0f ? img.TileHeight : img.PixelHeight;

                    for (int y = 0; y < height; y++)
                        for (int x = 0; x < width; x++)
                        {
                            Vector2 p = Vector2.Transform(new Vector2(originX + x + 0.5f, originY + y + 0.5f), inv);
                            float u = tw > 0f ? p.X / tw : 0f;
                            float v = th > 0f ? p.Y / th : 0f;

                            float a;
                            if (img.TileMode == TileMode.None && (u < 0f || u >= 1f || v < 0f || v >= 1f))
                            {
                                a = 0f;      // outside the brush: nothing painted there, so nothing shows
                            }
                            else
                            {
                                u -= MathF.Floor(u);
                                v -= MathF.Floor(v);
                                int sx = Math.Clamp((int)(u * img.PixelWidth), 0, img.PixelWidth - 1);
                                int sy = Math.Clamp((int)(v * img.PixelHeight), 0, img.PixelHeight - 1);
                                a = img.PixelsRgba[(sy * img.PixelWidth + sx) * 4 + 3] / 255f;
                            }

                            bytes[y * width + x] = ToByte(a * img.Opacity);
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
        // Reused across the glyphs of a run: GlyphRunPainter appends into it, and a run is drawn one
        // glyph at a time, so a single list serves the whole run.
        private readonly List<Text.GlyphFill> _glyphFills = new();

        /// <summary>The face a run's style asks for, built once and kept. Falls back to the regular
        /// face when nothing can supply the styled one, which is what happened for every run before
        /// style was carried at all.</summary>
        private Text.IFont FontFor(int simulations)
        {
            int key = simulations & 3;
            if (key == 0 || _styledFont == null) return _font;

            if (_styledCache.TryGetValue(key, out Text.IFont? cached)) return cached;

            Text.IFont resolved = _styledFont(key) ?? _font;
            _styledCache[key] = resolved;
            return resolved;
        }

        /// <summary>The face a run asked for. A named family is loaded from the file the platform
        /// keeps it in; anything unnamed, or a name this machine does not have, falls back to the
        /// default face in the requested style, which is what happened to every run before.</summary>
        private Text.IFont FontFor(int simulations, string? family)
        {
            if (string.IsNullOrEmpty(family)) return FontFor(simulations);

            int style = simulations & 3;
            string key = family + "|" + style;
            if (_familyCache.TryGetValue(key, out Text.IFont? cached)) return cached;

            Text.IFont resolved = LoadFamily(family!, style) ?? FontFor(simulations);
            _familyCache[key] = resolved;
            return resolved;
        }

        private static Text.IFont? LoadFamily(string family, int style)
        {
            bool bold = (style & 1) != 0, italic = (style & 2) != 0;
            string? path = Text.FontFiles.Find(family, bold, italic);
            if (path == null) return null;
            try
            {
                // A family that ships a real bold or italic file gets that file as it is; one that
                // does not has the style synthesized from its regular face.
                bool styled = Text.FontFiles.HasStyledFile(family, bold, italic);
                return new Text.TrueTypeFont(System.IO.File.ReadAllBytes(path),
                                             bold && !styled, italic && !styled);
            }
            catch (Exception)
            {
                return null;      // an unreadable or unsupported font file is not worth a crash
            }
        }

        private void EmitText(GlyphRunDraw run, Matrix3x2 world, double opacity, Scissor clip, int width, int height, WGPUTextureFormat format, DrawData data)
        {
            if (s_traceText)
                Console.Error.WriteLine($"[emit] '{run.Text}' family={run.FontFamily ?? "-"} sims={run.Simulations} "
                    + $"em={run.EmSize} origin={run.Origin} colorA={run.Color.A} clipEmpty={clip.IsEmpty} "
                    + $"opacity={opacity} world=({world.M31},{world.M32}) target={width}x{height}");
            if (clip.IsEmpty || string.IsNullOrEmpty(run.Text)) return;

            // What the APP asks for, as opposed to what the parity suite asks for. The suite passes
            // an integer pen and an integer em; the app passes whatever its layout computed, and a
            // fractional em would mean the face is fitted at a size nobody measured.
            if (s_runTrace && _runsTraced < 8)
            {
                _runsTraced++;
                Console.Error.WriteLine($"[run] em={run.EmSize:0.0000} origin=({run.Origin.X:0.0000},"
                    + $"{run.Origin.Y:0.0000}) world=({world.M31:0.0000},{world.M32:0.0000})"
                    + $" scale={LinearScale(world):0.0000} '{(run.Text.Length > 12 ? run.Text.Substring(0, 12) : run.Text)}'");
            }

            // The face this run asked for. Style is per-run, so it cannot be resolved once at
            // construction the way the regular face is.
            Text.IFont font = FontFor(run.Simulations, run.FontFamily);
            Text.IGlyphOutlineFont? outline = ReferenceEquals(font, _font)
                ? _outlineFont
                : font as Text.IGlyphOutlineFont;

            // Whether this face, at this size, asks to be smoothed in both directions. The 'gasp'
            // table says so per size and the answer changes with it -- Segoe UI wants it at 8ppem
            // and below and again above 19, and not in between -- so it is decided per RUN, not
            // once for the renderer.
            _symmetricSmoothing = font is Text.IHintedGlyphFont hf
                                  && hf.WantsSymmetricSmoothing(run.EmSize * LinearScale(world));

            // Shape the run into positioned glyphs (glyph ids + advances/offsets),
            // then lay them out. Advances come from the shaper (so kerning etc.
            // are honoured); the atlas provides each glyph's bitmap and bearings.
            _shaper.Shape(font, run.Text, _shapeScratch);

            float scale = run.EmSize / font.PixelsPerEm;

            // Grid fitting: how big this run will be ON THE SCREEN, which is the only size at which
            // fitting an outline to whole pixels means anything. Skipped where it would be a lie --
            // a rotated or sheared transform has no pixel columns to fit stems to -- and at sizes
            // where there is nothing to gain: above a hundred pixels an em, a stem is dozens of
            // pixels wide and moving its edge by a fraction of one changes nothing anybody can see.
            float deviceScale = LinearScale(world);
            bool upright = MathF.Abs(world.M12) < 1e-6f && MathF.Abs(world.M21) < 1e-6f
                           && MathF.Abs(MathF.Abs(world.M11) - MathF.Abs(world.M22)) < 1e-4f;
            float hintPpem = s_hintText && upright && deviceScale > 1e-6f
                             ? run.EmSize * deviceScale : 0f;
            if (hintPpem < 6f || hintPpem > 100f)
                hintPpem = 0f;
            var hinted = hintPpem > 0f ? font as Text.IHintedGlyphFont : null;

            // The fitted outline arrives in DEVICE pixels; the run is drawn in the caller's space, so
            // it is scaled back by the same factor the world transform will scale it up by.
            float hintScale = hintPpem > 0f ? 1f / deviceScale : scale;

            // And the run has to LAND on the grid it was fitted to. Fitting puts a stem on one whole
            // column and, in doing so, makes it a little narrower than the outline drew it; if the run
            // is then placed half a pixel over, that narrower stem is smeared across two columns and
            // comes out LIGHTER than the unfitted one would have -- which is exactly what it looked
            // like. So a fitted run starts on a whole device pixel and steps by whole device pixels.
            float originX = run.Origin.X, originY = run.Origin.Y;
            if (hintPpem > 0f)
            {
                Vector2 dev = Vector2.Transform(run.Origin, world);
                originX += (MathF.Round(dev.X) - dev.X) / deviceScale;
                originY += (MathF.Round(dev.Y) - dev.Y) / deviceScale;
            }

            // Prefer CRISP outline coverage (the same analytic-AA path WPF's glyph fills take) over the
            // fixed-size glyph atlas: the atlas rasterizes at BaseEmPixels (48) and MINIFIES to the run's
            // em size, so small runs (e.g. embedded WinForms at ~13px) alias/pixelate. Rasterizing each
            // glyph's outline at the exact display size matches WPF's quality. (WPF text arrives as
            // FillPath glyph outlines already; only string runs like WinForms reach here.)
            if (outline != null)
            {
                // Monochrome glyphs accumulate into ONE fill: they share the run's colour, and a
                // single path keeps the coverage cache and the text-gamma pass working per run
                // rather than per glyph. Colour layers cannot join that batch -- they carry their own
                // colours -- so the batch is flushed first and they are drawn in order on top.
                var batch = new List<PathFigure>();
                var colorFont = font as Text.IColorGlyphFont;
                // The pen starts on a whole pixel and moves by whole pixels, which is how Windows
                // lays a string out: each glyph's advance is rounded to a pixel before the next glyph
                // is placed. Carrying the fractions along instead left every run a shade narrower than
                // the same run in Windows, and put its glyphs on fractional positions, which is what
                // softens them. (WPF's own text does not come through here -- it arrives as glyph
                // outlines already positioned -- so this is the string runs, which is WinForms.)
                float pen = hintPpem > 0f ? originX : MathF.Round(originX);

                void FlushBatch()
                {
                    if (batch.Count == 0) return;
                    EmitFill(new GeometryFill(new PathGeometry(FillRule.NonZero, batch),
                                              new SolidColorBrush(run.Color), isGlyph: true)
                             { PixelAligned = hintPpem > 0f },
                             world, opacity, clip, width, height, format, data);
                    batch = new List<PathFigure>();
                }

                foreach (Text.ShapedGlyph g in _shapeScratch)
                {
                    float gx = pen + g.XOffset * scale;
                    float gy = originY + g.YOffset * scale;

                    _glyphFills.Clear();
                    Text.GlyphRunPainter.Paint(outline, colorFont, font as Text.IBitmapGlyphFont,
                                               g.GlyphId, hintPpem > 0f ? hintScale : scale, gx, gy,
                                               _glyphFills, hintPpem);

                    foreach (Text.GlyphFill gf in _glyphFills)
                    {
                        if (!gf.IsColorLayer)
                        {
                            batch.AddRange(gf.Figures);
                            continue;
                        }

                        // Whatever is already batched sits UNDER this glyph, so it has to be drawn
                        // before the layers are.
                        FlushBatch();
                        EmitFill(new GeometryFill(new PathGeometry(FillRule.NonZero, gf.Figures),
                                                  gf.Brush ?? new SolidColorBrush(gf.Color ?? run.Color), isGlyph: false),
                                 world, opacity, clip, width, height, format, data);
                    }

                    // The advance is rounded on the DEVICE grid when the glyphs were fitted to it,
                    // so the next glyph starts on a whole pixel too; in world units otherwise, which
                    // is how Windows lays a string out when nothing has been fitted.
                    //
                    // Where the face ships its own device widths, they are the answer: Windows uses
                    // them, and rounding the scaled advance instead disagrees with the face by a
                    // pixel on letters whose true advance sits near a half -- "Shapes" came out two
                    // pixels narrower than the same word beside it, which reads as squeezed.
                    float step;
                    if (hintPpem > 0f)
                    {
                        step = hinted is not null
                               && hinted.TryGetDeviceAdvance(g.GlyphId, hintPpem, out float device)
                               ? device / deviceScale
                               : MathF.Round(g.Advance * scale * deviceScale) / deviceScale;
                        // The pair adjustment, on the same whole-device-pixel grid as the advance.
                        // It has to be added HERE and not folded into the advance, because the
                        // branch above throws the shaped advance away for the face's own.
                        if (g.Kern != 0f)
                            step += MathF.Round(g.Kern * scale * deviceScale) / deviceScale;
                    }
                    else
                    {
                        step = MathF.Round((g.Advance + g.Kern) * scale);
                    }
                    pen += step;
                }

                FlushBatch();
                return;
            }

            float a = (float)Math.Clamp(run.Color.A * opacity, 0.0, 1.0);
            float penX = MathF.Round(run.Origin.X);
            float baseline = run.Origin.Y;

            foreach (Text.ShapedGlyph sg in _shapeScratch)
            {
                bool haveGlyph = _gpuGlyphs
                    ? TryGetOrAddGpuGlyph(sg.GlyphId, out Text.GlyphEntry e)
                    : _glyphAtlas.TryGetOrAdd(_font, sg.GlyphId, out e);
                if (haveGlyph && e.Width > 0 && e.Height > 0)
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
                    AddQuadIndices(data.Indices, baseVertex);
                    data.Draws.Add(new DrawItem(firstIndex, 6, clip, FillKind.Text, IntPtr.Zero));
                    data.HasText = true;
                }

                // The pair adjustment travels with the advance here too -- this is the atlas path,
                // taken when the font has no outlines to fit.
                penX += MathF.Round((sg.Advance + sg.Kern) * scale);
            }
        }

        // Appends two triangles (0,1,2, 0,2,3) for a quad starting at baseVertex -- avoids allocating
        // a 6-element index array per quad (which, with one quad per glyph, was hundreds of allocs/frame).
        private static void AddQuadIndices(List<uint> indices, uint baseVertex)
        {
            indices.Add(baseVertex); indices.Add(baseVertex + 1); indices.Add(baseVertex + 2);
            indices.Add(baseVertex); indices.Add(baseVertex + 2); indices.Add(baseVertex + 3);
        }

        private static void AddVertex(List<float> verts, Vector2 ndc, float r, float g, float b, float a, float u, float v,
            float p0 = 0f, float p1 = 0f, float p2 = 0f, float p3 = 0f)
        {
            verts.Add(ndc.X); verts.Add(ndc.Y);
            verts.Add(r); verts.Add(g); verts.Add(b); verts.Add(a);
            verts.Add(u); verts.Add(v);
            verts.Add(p0); verts.Add(p1); verts.Add(p2); verts.Add(p3);   // shape params; used only by fs_shape (0 elsewhere)
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

        // Begins a pass that PRESERVES the target's existing contents (loadOp=Load). Used to
        // rasterize new glyphs into the persistent glyph atlas without clearing prior glyphs.
        private IntPtr BeginLoadPass(IntPtr encoder, IntPtr view)
        {
            var colorAttachment = new WGPURenderPassColorAttachment
            {
                view = view,
                depthSlice = WGPU_DEPTH_SLICE_UNDEFINED,
                loadOp = WGPULoadOp.Load,
                storeOp = WGPUStoreOp.Store,
            };
            var passDesc = new WGPURenderPassDescriptor { colorAttachmentCount = 1, colorAttachments = &colorAttachment };
            return wgpuCommandEncoderBeginRenderPass(encoder, &passDesc);
        }

        // Frame-wide shared geometry: ALL 2D passes' vertices/indices are concatenated into ONE vertex +
        // ONE index buffer per frame (BuildBatchedGeometry), instead of a mappedAtCreation buffer PER pass.
        // wgpu-native's Metal backend commits a command buffer per mapped-buffer upload that a normal poll
        // never reclaims; a busy scene has hundreds of passes, so per-pass buffers pile up to Metal's 4096
        // in-flight limit → device lost. Batching turns "hundreds per frame" into two.
        private readonly List<float> _batchVerts = new();
        private readonly List<uint> _batchIndices = new();
        private IntPtr _frameVbuf, _frameIbuf;

        // Frame-wide shared STORAGE buffer for coverage/stroke/glyph masks: instead of a mappedAtCreation
        // storage buffer PER mask (the dominant per-frame command-buffer source when a page first rasterizes
        // its masks — a mask-heavy page like "What's New" bursts past Metal's 4096 limit), all masks' edge
        // data lives in ONE buffer and each mask's bind group references it at a 256-aligned offset. Because
        // the shared buffer doesn't exist until every mask is collected, the coverage passes record a PENDING
        // bind (their DrawItem starts with a null bind group) and BuildBatchedStorage patches them in after
        // sealing the buffer. Only populated on cache-MISS frames (new masks); cached masks skip all of this.
        // Ambient RenderOptions for the subtree being walked. WPF's RenderOptions are set on a
        // visual and apply to its descendants, so these are pushed/popped around the recursion
        // rather than threaded through every emit signature (same idiom as _devOX/_devOY).
        // CompositingMode.SourceCopy for the primitive currently being emitted. Ambient for the
        // same reason as the render options: it would otherwise have to be threaded through
        // every emit signature down to each DrawItem.
        private bool _srcCopy;
        private bool _aliasedEdges;
        private bool _nearestScaling;

        private readonly List<float> _bandScratch = new();

        // Rows per scanline band, and the cap on how many bands one mask may have. 32 rows keeps
        // the band table small while still cutting the per-fragment segment loop hard; the cap
        // bounds the worst-case duplication below (a segment is copied into every band its
        // y-range touches).
        private const int BandRows = 32;
        private const int MaxBands = 64;

        /// <summary>
        /// Repacks flat cubic segments into per-scanline-band lists so fs_coverage only loops
        /// over segments that can actually cross the fragment's scanline.
        ///
        /// This is EXACT, not an approximation: the shader already skipped any segment whose
        /// endpoints do not straddle the scanline, and a segment can only straddle it if its
        /// y-range contains it. Banding just moves that rejection off the per-fragment path.
        /// Measured, coverage cost is linear in the segments examined (587-599 us per segment
        /// over a 1000x1000 target across a 4x range), so the win is the reduction in list
        /// length -- roughly the ratio of a path's height to a band's.
        ///
        /// Layout, all in 8-byte vec2 slots so it rides in the existing single storage binding:
        ///   slot 0            header: (bandCount as u32 bits, rows per band as f32, integral)
        ///   slots 1..bandCount per band: (first segment's slot index, segment count) as u32 bits
        ///   rest              each band's segments, contiguous, 4 slots each
        /// A segment overlapping several bands is copied into each; bands are capped so that
        /// duplication stays bounded.
        /// </summary>
        private static void BuildScanlineBands(ReadOnlySpan<float> segs, int height, List<float> outBuf)
        {
            int segCount = segs.Length / 8;
            // Bands must be a WHOLE number of pixel rows. A fractional band height lets one
            // pixel row straddle a boundary, and since the shader looks the band up once from
            // the pixel row while sampling four subsample rows inside it, the rows past the
            // boundary would consult the wrong segment list and drop crossings -- holes in the
            // fill. Growing the row count (rather than the band count) honours the cap.
            int bandRows = Math.Max(BandRows, (height + MaxBands - 1) / MaxBands);
            int bandCount = Math.Max(1, (height + bandRows - 1) / bandRows);

            // Which bands each segment touches. The control polygon contains the curve, so
            // taking min/max over the four control points is conservative and never drops a
            // crossing (dropping one would punch a hole in the fill).
            Span<int> firstBand = segCount <= 256 ? stackalloc int[segCount] : new int[segCount];
            Span<int> lastBand = segCount <= 256 ? stackalloc int[segCount] : new int[segCount];
            var counts = new int[bandCount];
            for (int i = 0; i < segCount; i++)
            {
                float lo = float.MaxValue, hi = float.MinValue;
                for (int k = 0; k < 4; k++)
                {
                    float y = segs[i * 8 + k * 2 + 1];
                    lo = MathF.Min(lo, y); hi = MathF.Max(hi, y);
                }
                int b0 = Math.Clamp((int)MathF.Floor(lo) / bandRows, 0, bandCount - 1);
                int b1 = Math.Clamp((int)MathF.Floor(hi) / bandRows, 0, bandCount - 1);
                firstBand[i] = b0; lastBand[i] = b1;
                for (int b = b0; b <= b1; b++) counts[b]++;
            }

            int headerSlots = 1 + bandCount;
            var bandStart = new int[bandCount];
            int slot = headerSlots;
            for (int b = 0; b < bandCount; b++) { bandStart[b] = slot; slot += counts[b] * 4; }

            outBuf.Clear();
            for (int i = 0; i < slot * 2; i++) outBuf.Add(0f);      // 2 floats per vec2 slot

            outBuf[0] = BitConverter.Int32BitsToSingle(bandCount);
            outBuf[1] = bandRows;
            for (int b = 0; b < bandCount; b++)
            {
                outBuf[(1 + b) * 2] = BitConverter.Int32BitsToSingle(bandStart[b]);
                outBuf[(1 + b) * 2 + 1] = BitConverter.Int32BitsToSingle(counts[b]);
            }

            var cursor = new int[bandCount];
            for (int b = 0; b < bandCount; b++) cursor[b] = bandStart[b];
            for (int i = 0; i < segCount; i++)
                for (int b = firstBand[i]; b <= lastBand[i]; b++)
                {
                    int dst = cursor[b] * 2;
                    for (int k = 0; k < 8; k++) outBuf[dst + k] = segs[i * 8 + k];
                    cursor[b] += 4;
                }
        }

        private readonly List<byte> _batchStorage = new();
        private readonly List<(DrawData Data, int DrawIndex, int Offset, int Size, WGPUTextureFormat Format)> _pendingStorageBinds = new();
        private IntPtr _frameStorageBuf;

        // Reserves a 256-aligned region in the shared storage arena for a mask's edge data (256 =
        // minStorageBufferOffsetAlignment). Returns its byte offset; the region is exactly maxLen bytes.
        private int AllocStorage(ReadOnlySpan<byte> data, int maxLen)
        {
            int off = (_batchStorage.Count + 255) & ~255;
            while (_batchStorage.Count < off) _batchStorage.Add(0);
            _batchStorage.AddRange(data);
            for (int pad = data.Length; pad < maxLen; pad++) _batchStorage.Add(0);
            return off;
        }

        // Seals the shared storage arena into one buffer and creates+patches the deferred coverage-mask bind
        // groups. Call after collection, before the ExecutePass loop (alongside BuildBatchedGeometry).
        private void BuildBatchedStorage()
        {
            _frameStorageBuf = IntPtr.Zero;
            if (_pendingStorageBinds.Count == 0) { _batchStorage.Clear(); return; }

            _frameStorageBuf = _ctx.CreateBufferMapped(CollectionsMarshal.AsSpan(_batchStorage), WGPUBufferUsage.Storage);
            DeferReleaseBuffer(_frameStorageBuf);

            foreach ((DrawData d, int idx, int off, int size, WGPUTextureFormat layoutFmt) in _pendingStorageBinds)
            {
                DrawItem di = d.Draws[idx];
                // The layout MUST come from the pipeline of the pass this draw is actually recorded in:
                // an auto-layout bind group is exclusive to its pipeline, and pipelines are per (format,
                // kind). This used to assume StrokeDraw always lands in the main colour pass and used the
                // main format - but a stroke inside an opacity/effect layer is recorded into that LAYER's
                // pass (ReadbackFormat), so the group belonged to a different pipeline and wgpu aborted
                // the process at wgpuCommandEncoderFinish with "Exclusive pipelines don't match". Each
                // pending bind now carries the format of its own pass (R8Unorm for the mask passes).
                IntPtr layout = GetBindGroupLayout(layoutFmt, di.Kind, di.SourceCopy);
                var entry = new WGPUBindGroupEntry { binding = 0, buffer = _frameStorageBuf, offset = (ulong)off, size = (ulong)size };
                var bgDesc = new WGPUBindGroupDescriptor { layout = layout, entryCount = 1, entries = &entry };
                IntPtr bg = wgpuDeviceCreateBindGroup(_ctx.Device, &bgDesc);
                DeferReleaseBindGroup(bg);
                d.Draws[idx] = new DrawItem(di.FirstIndex, di.IndexCount, di.Clip, di.Kind, bg, di.EffectId, di.SourceCopy);
            }
            _pendingStorageBinds.Clear();
            _batchStorage.Clear();
        }

        // Concatenates every 2D pass's geometry (the plan's LayerPasses + the main pass) into the two
        // shared buffers and records each DrawData's byte offset. Each AddVertex appends a fixed
        // VertexStride (8 floats), so a pass's vertex block is naturally VertexStride-aligned when
        // concatenated (a valid SetVertexBuffer offset); indices are uint (4-byte, a valid index offset).
        // Draw indices stay pass-local (baseVertex 0 / firstIndex relative), resolved by binding the
        // shared buffers at the pass's offset. Call once, after collection, before the ExecutePass loop.
        private void BuildBatchedGeometry(List<LayerPass> plan, DrawData? mainData)
        {
            _batchVerts.Clear();
            _batchIndices.Clear();
            _frameVbuf = _frameIbuf = IntPtr.Zero;

            void Assign(DrawData d)
            {
                if (d.Indices.Count == 0) { d.VbOffset = d.IbOffset = -1; return; }
                d.VbOffset = _batchVerts.Count * sizeof(float);
                d.IbOffset = _batchIndices.Count * sizeof(uint);
                _batchVerts.AddRange(d.Verts);
                _batchIndices.AddRange(d.Indices);
            }

            foreach (LayerPass lp in plan)
                if (lp.Models3D == null) Assign(lp.Data);   // 3D passes carry their own per-mesh buffers
            if (mainData != null) Assign(mainData);

            if (_batchIndices.Count == 0) return;
            _frameVbuf = _ctx.CreateBufferMapped(MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(_batchVerts)), WGPUBufferUsage.Vertex);
            _frameIbuf = _ctx.CreateBufferMapped(MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(_batchIndices)), WGPUBufferUsage.Index);
            DeferReleaseBuffer(_frameVbuf);
            DeferReleaseBuffer(_frameIbuf);
        }

        private void BuildGeometryBuffers(DrawData data, out IntPtr vbuf, out IntPtr ibuf, out bool hasGeometry)
        {
            // Geometry was uploaded once for the whole frame by BuildBatchedGeometry; just hand back the
            // shared buffers. RecordDraws binds them at this pass's VbOffset/IbOffset.
            hasGeometry = data.Indices.Count > 0 && data.VbOffset >= 0;
            vbuf = _frameVbuf;
            ibuf = _frameIbuf;
        }

        private void RecordDraws(IntPtr pass, WGPUTextureFormat format, IntPtr vbuf, IntPtr ibuf, DrawData data, IntPtr atlasBindGroup,
            int originX = 0, int originY = 0, int texW = 0, int texH = 0)
        {
            wgpuRenderPassEncoderSetVertexBuffer(pass, 0, vbuf, (ulong)data.VbOffset, (ulong)(data.Verts.Count * sizeof(float)));
            wgpuRenderPassEncoderSetIndexBuffer(pass, ibuf, WGPUIndexFormat.Uint32, (ulong)data.IbOffset, (ulong)(data.Indices.Count * sizeof(uint)));

            // Nothing bound yet in this pass: a render pass starts with no pipeline, no bind group
            // and a scissor covering the whole attachment, so the first draw always sets all three.
            IntPtr boundPipeline = IntPtr.Zero, boundBindGroup = IntPtr.Zero;
            bool haveScissor = false;
            int bx = 0, by = 0, bw = 0, bh = 0;

            List<DrawItem> draws = data.Draws;
            for (int i = 0; i < draws.Count; i++)
            {
                DrawItem d = draws[i];
                if (d.Clip.IsEmpty) continue;

                IntPtr pipeline = ResolvePipeline(format, d.Kind, d.EffectId, d.SourceCopy);
                IntPtr bindGroup = BindGroupFor(d, atlasBindGroup);

                // Absorb every following item that would be drawn with exactly this state out of the
                // very next indices. Nearly all of them are one quad, and a run of them -- the glyphs
                // of a line of text, the cells of a grid -- differs only in vertex data, which is
                // already sitting contiguously in the shared buffer. Merging preserves order, because
                // one drawIndexed processes its indices in order just as the separate calls did.
                uint firstIndex = d.FirstIndex, indexCount = d.IndexCount;
                int merged = 1;
                while (i + merged < draws.Count)
                {
                    DrawItem n = draws[i + merged];
                    if (n.Clip.IsEmpty) break;                                  // a gap in the indices
                    if (n.FirstIndex != firstIndex + indexCount) break;         // not contiguous
                    if (n.Kind != d.Kind || n.EffectId != d.EffectId || n.SourceCopy != d.SourceCopy) break;
                    if (n.Clip.X != d.Clip.X || n.Clip.Y != d.Clip.Y ||
                        n.Clip.W != d.Clip.W || n.Clip.H != d.Clip.H) break;
                    if (BindGroupFor(n, atlasBindGroup) != bindGroup) break;

                    indexCount += n.IndexCount;
                    merged++;
                }
                i += merged - 1;

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

                if (pipeline != boundPipeline)
                {
                    wgpuRenderPassEncoderSetPipeline(pass, pipeline);
                    boundPipeline = pipeline;
                }

                // Zero means this kind binds nothing at group 0 (a plain Shape fill is driven
                // entirely by its vertex attributes), and leaving whatever was bound before in place
                // is what the unbatched version did too.
                if (bindGroup != IntPtr.Zero && bindGroup != boundBindGroup)
                {
                    wgpuRenderPassEncoderSetBindGroup(pass, 0, bindGroup, 0, null);
                    boundBindGroup = bindGroup;
                }

                if (!haveScissor || sx != bx || sy != by || sw != bw || sh != bh)
                {
                    wgpuRenderPassEncoderSetScissorRect(pass, (uint)sx, (uint)sy, (uint)sw, (uint)sh);
                    haveScissor = true;
                    bx = sx; by = sy; bw = sw; bh = sh;
                }

                PerfDrawItems += merged;
                PerfDrawCalls++;
                wgpuRenderPassEncoderDrawIndexed(pass, indexCount, 1, firstIndex, 0, 0);
            }
        }

        /// <summary>
        /// The group-0 bind group a draw needs, or Zero for the kinds that bind nothing there.
        /// </summary>
        private static IntPtr BindGroupFor(DrawItem d, IntPtr atlasBindGroup) => d.Kind switch
        {
            FillKind.Textured or FillKind.Layer or FillKind.Blur or FillKind.Shadow or
            FillKind.Clip or FillKind.Coverage or FillKind.MaskBrush or FillKind.MaskImage or
            FillKind.ShapeBrush or        // group 0 = gradient ramp + sampler + brush params
            FillKind.BrushAlpha or FillKind.Id or FillKind.Stroke or
            FillKind.StrokeDraw or
            FillKind.ShaderEffect or      // group 0 = constants uniform (optional) + input texture + sampler
            FillKind.TextSubpixelMultiply or FillKind.TextSubpixelAdd
                => d.BindGroup,

            // Glyph runs share the atlas bind group; path masks carry their own.
            FillKind.Text => d.BindGroup != IntPtr.Zero ? d.BindGroup : atlasBindGroup,

            _ => IntPtr.Zero,
        };

        private void DeferReleaseSampled(IntPtr texture, IntPtr view, IntPtr bindGroup)
        {
            DeferReleaseBindGroup(bindGroup);
            DeferReleaseTexView(texture, view);
        }

        // Returns the glyph-atlas texture view, rebuilding it only when new glyphs
        // were added. The atlas is long-lived (re-uploading it every frame was a
        // leak); per-pass bind groups are created from this view on demand.
        private IntPtr EnsureAtlasView(bool anyText)
        {
            if (!anyText) return IntPtr.Zero;

            // GPU glyphs: the persistent atlas render target was already created and had this
            // frame's new glyphs rasterized into it (load-preserve passes appended during
            // collection). Just return its view -- no CPU pixel upload.
            if (_gpuGlyphs)
            {
                _glyphAtlas.ClearDirty();
                return _atlasView;   // Zero only if no glyph with a rect was ever packed
            }

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
            _gpuAtlasCreated = false;
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
            foreach ((IntPtr Tex, IntPtr View, int _) in _freeEdgeTex) { wgpuTextureViewRelease(View); wgpuTextureRelease(Tex); }
            _freeEdgeTex.Clear();
            foreach ((IntPtr Tex, IntPtr View, int _, int _) in _freeMaskTex) { wgpuTextureViewRelease(View); wgpuTextureRelease(Tex); }
            _freeMaskTex.Clear();
            if (_idView != IntPtr.Zero) { wgpuTextureViewRelease(_idView); wgpuTextureRelease(_idTex); _idView = _idTex = IntPtr.Zero; }
            ReleaseAtlas();
            ReleaseResources3D();
            // Before the pipelines they came from: a layout holds a reference to its pipeline.
            foreach (IntPtr layout in _bindGroupLayouts.Values) wgpuBindGroupLayoutRelease(layout);
            _bindGroupLayouts.Clear();
            foreach (IntPtr pipeline in _pipelines.Values) wgpuRenderPipelineRelease(pipeline);
            _pipelines.Clear();
            foreach (IntPtr pipeline in _effectPipelines.Values) wgpuRenderPipelineRelease(pipeline);
            _effectPipelines.Clear();
            foreach (IntPtr module in _effectModules.Values) wgpuShaderModuleRelease(module);
            _effectModules.Clear();
            if (_shaderModule != IntPtr.Zero) wgpuShaderModuleRelease(_shaderModule);
            if (_clipShaderModule != IntPtr.Zero) wgpuShaderModuleRelease(_clipShaderModule);
            if (_coverageShaderModule != IntPtr.Zero) wgpuShaderModuleRelease(_coverageShaderModule);
            if (_brushShaderModule != IntPtr.Zero) wgpuShaderModuleRelease(_brushShaderModule);
            if (_brushAlphaShaderModule != IntPtr.Zero) wgpuShaderModuleRelease(_brushAlphaShaderModule);
            if (_idShaderModule != IntPtr.Zero) wgpuShaderModuleRelease(_idShaderModule);
            if (_strokeShaderModule != IntPtr.Zero) wgpuShaderModuleRelease(_strokeShaderModule);
            if (_shapeShaderModule != IntPtr.Zero) wgpuShaderModuleRelease(_shapeShaderModule);
            if (_shapeBrushShaderModule != IntPtr.Zero) wgpuShaderModuleRelease(_shapeBrushShaderModule);
            if (_strokeDrawShaderModule != IntPtr.Zero) wgpuShaderModuleRelease(_strokeDrawShaderModule);
            if (_whiteView != IntPtr.Zero) { wgpuTextureViewRelease(_whiteView); wgpuTextureRelease(_whiteTex); }
            if (_linearSampler != IntPtr.Zero) wgpuSamplerRelease(_linearSampler);
            if (_nearestSampler != IntPtr.Zero) wgpuSamplerRelease(_nearestSampler);
            _shaderModule = _clipShaderModule = _coverageShaderModule = IntPtr.Zero;
            _brushShaderModule = _brushAlphaShaderModule = _idShaderModule = _strokeShaderModule = _shapeShaderModule = _shapeBrushShaderModule = _strokeDrawShaderModule = IntPtr.Zero;
            _whiteTex = _whiteView = _linearSampler = _nearestSampler = IntPtr.Zero;
        }

        // ---- GPU resource creation ----

        /// <summary>
        /// Test hook: compiles one WGSL source through the runtime's own frontend and returns
        /// the module (IntPtr.Zero on a compile error). wgpu-native embeds naga, so this is the
        /// exact validator that would reject the shader at runtime -- a build-time check against
        /// a separately-installed tool could disagree with it.
        /// </summary>
        internal IntPtr CompileShaderForTest(string wgsl)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(wgsl);
            fixed (byte* p = bytes)
            {
                var src = new WGPUShaderSourceWGSL
                {
                    chain = new WGPUChainedStruct { next = null, sType = WGPUSType_ShaderSourceWGSL },
                    code = new WGPUStringView { data = p, length = (nuint)bytes.Length },
                };
                var desc = new WGPUShaderModuleDescriptor { nextInChain = (WGPUChainedStruct*)&src };
                return wgpuDeviceCreateShaderModule(_ctx.Device, &desc);
            }
        }

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

        /// <summary>
        /// Pipeline for a draw. Built-in kinds are one pipeline per (format, kind); a custom
        /// ShaderEffect is one per (format, shader) because every translated shader is its own
        /// module, so the kind alone cannot identify it.
        /// </summary>
        private IntPtr ResolvePipeline(WGPUTextureFormat format, FillKind kind, int effectId, bool sourceCopy = false)
        {
            if (kind != FillKind.ShaderEffect || effectId < 0) return GetPipeline(format, kind, sourceCopy);
            var key = (format, effectId);
            if (_effectPipelines.TryGetValue(key, out IntPtr cached)) return cached;
            IntPtr pipeline = CreatePipeline(_ctx.Device, _effectModules[effectId], format, FillKind.ShaderEffect);
            _effectPipelines[key] = pipeline;
            return pipeline;
        }

        /// <summary>
        /// Renders the subtree layer through a custom pixel shader into a new layer.
        /// Returns false if the shader cannot be realized, in which case the caller leaves the
        /// subtree unmodified -- the same thing milcore does with a shader it cannot compile,
        /// and far better than dropping the content.
        /// </summary>
        private bool ShaderEffectLayer(IntPtr input, ShaderEffectDef def, List<LayerPass> plan, Scissor region,
            out IntPtr outTex, out IntPtr outView)
        {
            outTex = IntPtr.Zero; outView = IntPtr.Zero;
            // Every sampler must resolve to something bindable: the subtree's own layer (WPF's
            // ImplicitInputBrush) or an image brush we can hand to the GPU. A gradient or visual
            // brush in a sampler slot is declined rather than bound to the wrong texture.
            foreach (Brush? b in def.SamplerBrushes)
                if (b is not null and not ImageBrush) return false;

            if (!_effectModules.TryGetValue(def.ShaderId, out IntPtr module))
            {
                module = CompileWgsl(def.Wgsl);
                if (module == IntPtr.Zero) return false;
                _effectModules[def.ShaderId] = module;
            }

            int rw = region.W, rh = region.H;
            if (rw <= 0 || rh <= 0) return false;
            (outTex, outView) = CreateOwnedLayerTexture(rw, rh);

            IntPtr bindGroup = CreateEffectBindGroup(def, input);
            if (bindGroup == IntPtr.Zero) return false;

            float sOX = _devOX, sOY = _devOY;
            DrawData d = RentDrawData();
            _devOX = region.X; _devOY = region.Y;
            float x0 = region.X, y0 = region.Y, x1 = region.X + rw, y1 = region.Y + rh;
            uint baseVertex = (uint)(d.Verts.Count / FloatsPerVertex);
            AddVertex(d.Verts, ToNdc(new Vector2(x0, y0), rw, rh), 1f, 1f, 1f, 1f, 0f, 0f);
            AddVertex(d.Verts, ToNdc(new Vector2(x1, y0), rw, rh), 1f, 1f, 1f, 1f, 1f, 0f);
            AddVertex(d.Verts, ToNdc(new Vector2(x1, y1), rw, rh), 1f, 1f, 1f, 1f, 1f, 1f);
            AddVertex(d.Verts, ToNdc(new Vector2(x0, y1), rw, rh), 1f, 1f, 1f, 1f, 0f, 1f);
            AddQuadIndices(d.Indices, baseVertex);
            d.Draws.Add(new DrawItem(0, 6, region, FillKind.ShaderEffect, bindGroup, def.ShaderId));
            _devOX = sOX; _devOY = sOY;

            plan.Add(new LayerPass(outView, true, default, d, ReadbackFormat)
            { OriginX = region.X, OriginY = region.Y, TexW = rw, TexH = rh });
            return true;
        }

        // Bindings follow D3D9ShaderTranslator's emission order: the constant uniform first
        // (only when the shader reads any c# register), then each sampler's texture + sampler.
        private IntPtr CreateEffectBindGroup(ShaderEffectDef def, IntPtr inputView)
        {
            PerfBindGroups++;
            IntPtr layout = GetBindGroupLayout(ReadbackFormat, FillKind.ShaderEffect, effectId: def.ShaderId);

            bool hasConsts = def.FloatConstants.Length > 0;
            // 1 uniform + 2 entries per sampler.
            int maxEntries = 1 + def.Samplers.Length * 2;
            var entries = stackalloc WGPUBindGroupEntry[maxEntries];
            uint n = 0;
            IntPtr cbuf = IntPtr.Zero;
            if (hasConsts)
            {
                // std140-ish: the shader declares array<vec4<f32>, N>, so the buffer is 16 bytes
                // per register and must be at least one register long.
                int bytes = Math.Max(16, def.FloatConstants.Length * sizeof(float));
                var bufBytes = new byte[bytes];
                Buffer.BlockCopy(def.FloatConstants, 0, bufBytes, 0, def.FloatConstants.Length * sizeof(float));
                cbuf = _ctx.CreateBufferMapped(bufBytes, WGPUBufferUsage.Uniform, (ulong)bytes);
                DeferReleaseBuffer(cbuf);
                entries[n++] = new WGPUBindGroupEntry { binding = n - 1, buffer = cbuf, offset = 0, size = (ulong)bytes };
            }
            // One texture+sampler pair per sampler register, in the order the translator
            // emitted the bindings. A null brush is the implicit input: the subtree's layer.
            for (int i = 0; i < def.Samplers.Length; i++)
            {
                IntPtr view = inputView;
                if (def.SamplerBrushes[i] is ImageBrush ib)
                    view = GetOrCreateImageView(ib.PixelsRgba, ib.PixelWidth, ib.PixelHeight);
                entries[n] = new WGPUBindGroupEntry { binding = n, textureView = view }; n++;
                entries[n] = new WGPUBindGroupEntry { binding = n, sampler = LinearSampler() }; n++;
            }

            var desc = new WGPUBindGroupDescriptor { layout = layout, entryCount = (nuint)n, entries = entries };
            return wgpuDeviceCreateBindGroup(_ctx.Device, &desc);
        }

        /// <summary>
        /// The group-0 bind group layout for a pipeline, cached.
        /// </summary>
        /// <remarks>
        /// wgpuRenderPipelineGetBindGroupLayout hands back a NEW reference on every call, so asking
        /// for one per bind group -- which is per DRAW, per FRAME -- leaked a layout every time. The
        /// 3D path had already learnt this (see Get3DBindGroupLayout) and the 2D one had not, so the
        /// leak grew with frames rendered times draws per frame, for as long as an app was on screen.
        /// There is nothing per-draw about a layout: it is a property of the pipeline, and the
        /// pipelines are themselves cached, so one lookup keyed the same way serves every draw that
        /// will ever use it.
        /// </remarks>
        private readonly Dictionary<(WGPUTextureFormat Format, FillKind Kind, bool SourceCopy, int EffectId), IntPtr> _bindGroupLayouts = new();

        private IntPtr GetBindGroupLayout(WGPUTextureFormat format, FillKind kind, bool sourceCopy = false, int effectId = -1)
        {
            var key = (format, kind, sourceCopy, effectId);
            if (_bindGroupLayouts.TryGetValue(key, out IntPtr cached)) return cached;

            PerfLayoutAcquires++;
            IntPtr layout = wgpuRenderPipelineGetBindGroupLayout(ResolvePipeline(format, kind, effectId, sourceCopy), 0);
            _bindGroupLayouts[key] = layout;
            return layout;
        }

        private IntPtr GetPipeline(WGPUTextureFormat format, FillKind kind) => GetPipeline(format, kind, false);

        private IntPtr GetPipeline(WGPUTextureFormat format, FillKind kind, bool sourceCopy)
        {
            var key = (format, kind, sourceCopy);
            if (_pipelines.TryGetValue(key, out IntPtr cached))
                return cached;
            IntPtr shader = kind switch
            {
                FillKind.Clip => GetClipShaderModule(),
                FillKind.Coverage => GetCoverageShaderModule(),
                FillKind.MaskBrush or FillKind.MaskImage => GetBrushShaderModule(),
                FillKind.BrushAlpha => GetBrushAlphaShaderModule(),
                FillKind.Id => GetIdShaderModule(),
                FillKind.Stroke => GetStrokeShaderModule(),
                FillKind.Shape => GetShapeShaderModule(),
                FillKind.ShapeBrush => GetShapeBrushShaderModule(),
                FillKind.StrokeDraw => GetStrokeDrawShaderModule(),
                _ => GetShaderModule(),
            };
            IntPtr pipeline = CreatePipeline(_ctx.Device, shader, format, kind, sourceCopy);
            _pipelines[key] = pipeline;
            return pipeline;
        }

        private IntPtr GetCoverageShaderModule()
        {
            if (_coverageShaderModule != IntPtr.Zero) return _coverageShaderModule;
            _coverageShaderModule = CompileWgsl(CoverageShaderWgsl);
            return _coverageShaderModule;
        }

        private IntPtr GetShapeShaderModule()
        {
            if (_shapeShaderModule != IntPtr.Zero) return _shapeShaderModule;
            _shapeShaderModule = CompileWgsl(ShapeShaderWgsl);
            return _shapeShaderModule;
        }

        private IntPtr GetShapeBrushShaderModule()
        {
            if (_shapeBrushShaderModule != IntPtr.Zero) return _shapeBrushShaderModule;
            _shapeBrushShaderModule = CompileWgsl(ShapeBrushShaderWgsl);
            return _shapeBrushShaderModule;
        }

        private IntPtr GetStrokeDrawShaderModule()
        {
            if (_strokeDrawShaderModule != IntPtr.Zero) return _strokeDrawShaderModule;
            _strokeDrawShaderModule = CompileWgsl(StrokeDrawShaderWgsl);
            return _strokeDrawShaderModule;
        }

        private IntPtr GetBrushShaderModule()
        {
            if (_brushShaderModule != IntPtr.Zero) return _brushShaderModule;
            _brushShaderModule = CompileWgsl(BrushShaderWgsl);
            return _brushShaderModule;
        }

        private IntPtr GetBrushAlphaShaderModule()
        {
            if (_brushAlphaShaderModule != IntPtr.Zero) return _brushAlphaShaderModule;
            _brushAlphaShaderModule = CompileWgsl(BrushAlphaShaderWgsl);
            return _brushAlphaShaderModule;
        }

        private IntPtr GetIdShaderModule()
        {
            if (_idShaderModule != IntPtr.Zero) return _idShaderModule;
            _idShaderModule = CompileWgsl(IdShaderWgsl);
            return _idShaderModule;
        }

        private IntPtr GetStrokeShaderModule()
        {
            if (_strokeShaderModule != IntPtr.Zero) return _strokeShaderModule;
            _strokeShaderModule = CompileWgsl(StrokeShaderWgsl);
            return _strokeShaderModule;
        }

        private IntPtr WhiteCoverageView()
        {
            if (_whiteView != IntPtr.Zero) return _whiteView;
            (_whiteTex, _whiteView) = CreateR8Texture(new byte[] { 255 }, 1, 1);
            return _whiteView;
        }

        private IntPtr CompileWgsl(string wgslSource)
        {
            byte[] wgsl = Encoding.UTF8.GetBytes(wgslSource);
            fixed (byte* pWgsl = wgsl)
            {
                var src = new WGPUShaderSourceWGSL
                {
                    chain = new WGPUChainedStruct { next = null, sType = WGPUSType_ShaderSourceWGSL },
                    code = new WGPUStringView { data = pWgsl, length = (nuint)wgsl.Length },
                };
                var desc = new WGPUShaderModuleDescriptor { nextInChain = (WGPUChainedStruct*)&src };
                return wgpuDeviceCreateShaderModule(_ctx.Device, &desc);
            }
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

        private static IntPtr CreatePipeline(IntPtr device, IntPtr shader, WGPUTextureFormat targetFormat, FillKind kind, bool sourceCopy = false)
        {
            string fsName = kind switch
            {
                FillKind.Textured => "fs_textured",
                FillKind.Text => "fs_text",
                FillKind.TextSubpixelMultiply => "fs_text_subpixel_multiply",
                FillKind.TextSubpixelAdd => "fs_text_subpixel_add",
                FillKind.Layer => "fs_layer",
                FillKind.Blur => "fs_blur",
                FillKind.Shadow => "fs_shadow",
                FillKind.Clip => "fs_clip",
                FillKind.Coverage => "fs_coverage",
                FillKind.MaskBrush => "fs_maskbrush",
                FillKind.MaskImage => "fs_maskimage",
                FillKind.BrushAlpha => "fs_brushalpha",
                FillKind.Id => "fs_id",
                FillKind.Stroke => "fs_stroke",
                FillKind.Shape => "fs_shape",
                FillKind.ShapeBrush => "fs_shapebrush",
                FillKind.StrokeDraw => "fs_strokedraw",
                FillKind.ShaderEffect => "fs_effect",
                _ => "fs_solid",
            };
            byte[] vsEntry = Encoding.UTF8.GetBytes("vs_main");
            byte[] fsEntry = Encoding.UTF8.GetBytes(fsName);

            fixed (byte* pVs = vsEntry)
            fixed (byte* pFs = fsEntry)
            {
                // All pipelines share the 12-float vertex stride. The shape and stroke-draw pipelines
                // additionally read the last 4 floats as location 3 (shape params / stroke params); every
                // other pipeline reads only locations 0-2 and ignores the trailing floats.
                bool usesParams = kind is FillKind.Shape or FillKind.ShapeBrush or FillKind.StrokeDraw;
                var attributes = stackalloc WGPUVertexAttribute[4];
                attributes[0] = new WGPUVertexAttribute { format = WGPUVertexFormat.Float32x2, offset = 0, shaderLocation = 0 };
                attributes[1] = new WGPUVertexAttribute { format = WGPUVertexFormat.Float32x4, offset = 2 * sizeof(float), shaderLocation = 1 };
                attributes[2] = new WGPUVertexAttribute { format = WGPUVertexFormat.Float32x2, offset = 6 * sizeof(float), shaderLocation = 2 };
                attributes[3] = new WGPUVertexAttribute { format = WGPUVertexFormat.Float32x4, offset = 8 * sizeof(float), shaderLocation = 3 };

                var bufferLayout = new WGPUVertexBufferLayout
                {
                    stepMode = WGPUVertexStepMode.Vertex,
                    arrayStride = VertexStride,
                    attributeCount = usesParams ? 4u : 3u,
                    attributes = attributes,
                };

                var blend = new WGPUBlendState
                {
                    color = new WGPUBlendComponent { operation = WGPUBlendOperation.Add, srcFactor = WGPUBlendFactor.One, dstFactor = WGPUBlendFactor.OneMinusSrcAlpha },
                    alpha = new WGPUBlendComponent { operation = WGPUBlendOperation.Add, srcFactor = WGPUBlendFactor.One, dstFactor = WGPUBlendFactor.OneMinusSrcAlpha },
                };

                // The two halves of subpixel text. Together they are the ordinary source-over blend
                // written out per channel, which is the one thing a single blend state cannot say:
                // its destination factor is one number and here each lamp needs its own.
                if (kind == FillKind.TextSubpixelMultiply)
                {
                    // dst *= (1 - coverage), per channel.
                    blend.color = new WGPUBlendComponent { operation = WGPUBlendOperation.Add, srcFactor = WGPUBlendFactor.Zero, dstFactor = WGPUBlendFactor.OneMinusSrc };
                    blend.alpha = new WGPUBlendComponent { operation = WGPUBlendOperation.Add, srcFactor = WGPUBlendFactor.Zero, dstFactor = WGPUBlendFactor.OneMinusSrcAlpha };
                }
                else if (kind == FillKind.TextSubpixelAdd)
                {
                    // dst += colour * coverage, per channel.
                    blend.color = new WGPUBlendComponent { operation = WGPUBlendOperation.Add, srcFactor = WGPUBlendFactor.One, dstFactor = WGPUBlendFactor.One };
                    blend.alpha = new WGPUBlendComponent { operation = WGPUBlendOperation.Add, srcFactor = WGPUBlendFactor.One, dstFactor = WGPUBlendFactor.One };
                }
                // Coverage / brush-alpha passes write the mask value directly (single opaque
                // quad into a cleared R8 target) — no blending. All others blend premultiplied.
                var colorTarget = new WGPUColorTargetState { format = targetFormat, blend = &blend, writeMask = WGPUColorWriteMask_All };
                // Coverage / stroke / brush-alpha write mask values directly; the id pass writes
                // packed ids with topmost-wins-by-paint-order (overwrite, no blend).
                if (kind is FillKind.Coverage or FillKind.Stroke or FillKind.BrushAlpha or FillKind.Id) colorTarget.blend = null;
                // CompositingMode.SourceCopy: write the premultiplied source straight out,
                // replacing the destination colour AND alpha. Disabling blending is exactly
                // that -- no separate blend factors needed.
                if (sourceCopy) colorTarget.blend = null;

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

        // Texture uploads staged during scene collection (CreateTexture) and flushed as
        // CopyBufferToTexture into the frame's single command encoder (FlushPendingTexUploads),
        // so they add ZERO command buffers — see the note on CreateTexture.
        private struct PendingTexUpload { public int StagingOffset; public IntPtr Texture; public int Width; public int Height; public int BytesPerRow; }
        private readonly List<PendingTexUpload> _pendingTexUploads = new();

        // Frame-shared texture-upload staging: all this frame's texture pixels (gradient ramps, images,
        // CPU masks) live in ONE CopySrc buffer instead of one mappedAtCreation buffer PER texture. Each
        // upload's CopyBufferToTexture reads from it at a 256-aligned offset. An image/gradient-heavy page
        // otherwise commits a per-texture command buffer each and bursts past Metal's 4096 limit.
        private readonly List<byte> _batchTexStaging = new();

        // Reserves a 256-aligned region in the texture-staging arena and copies the rows in; returns its
        // byte offset (256 satisfies the CopyBufferToTexture bufferOffset + bytesPerRow alignment rules).
        private int AllocTexStaging(byte[] data, int length)
        {
            int off = (_batchTexStaging.Count + 255) & ~255;
            while (_batchTexStaging.Count < off) _batchTexStaging.Add(0);
            _batchTexStaging.AddRange(data.AsSpan(0, length));
            return off;
        }

        // Records the staged texture uploads collected this frame into the frame's command encoder,
        // BEFORE any render pass that samples them. Called right after the encoder is created and
        // before the plan/main passes. The staging buffers are freed by FlushFrameReleases (after
        // submit); wgpu keeps them alive until the submission completes.
        private void FlushPendingTexUploads(IntPtr encoder)
        {
            if (_pendingTexUploads.Count == 0) { _batchTexStaging.Clear(); return; }
            IntPtr staging = _ctx.CreateBufferMapped(CollectionsMarshal.AsSpan(_batchTexStaging), WGPUBufferUsage.CopySrc);
            DeferReleaseBuffer(staging);
            foreach (PendingTexUpload u in _pendingTexUploads)
            {
                var src = new WGPUTexelCopyBufferInfo
                {
                    layout = new WGPUTexelCopyBufferLayout { offset = (ulong)u.StagingOffset, bytesPerRow = (uint)u.BytesPerRow, rowsPerImage = (uint)u.Height },
                    buffer = staging,
                };
                var dst = new WGPUTexelCopyTextureInfo { texture = u.Texture, aspect = WGPUTextureAspect.All };
                var size = new WGPUExtent3D { width = (uint)u.Width, height = (uint)u.Height, depthOrArrayLayers = 1 };
                wgpuCommandEncoderCopyBufferToTexture(encoder, &src, &dst, &size);
            }
            _pendingTexUploads.Clear();
            _batchTexStaging.Clear();
        }

        private (IntPtr Texture, IntPtr View) CreateTexture(byte[] pixels, int width, int height, WGPUTextureFormat format, int bytesPerPixel)
        {
            IntPtr texture = NewSampledTexture(width, height, format);
            UploadTexture(texture, pixels, width, height, bytesPerPixel);
            return (texture, wgpuTextureCreateView(texture, IntPtr.Zero));
        }

        /// <summary>An empty sampled texture. Split from the upload so a pooled one can be refilled.</summary>
        private IntPtr NewSampledTexture(int width, int height, WGPUTextureFormat format)
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
            return wgpuDeviceCreateTexture(_ctx.Device, &texDesc);
        }

        private void UploadTexture(IntPtr texture, byte[] pixels, int width, int height, int bytesPerPixel)
        {
#if WGPU_BROWSER
            // The browser's JS WebGPU backend has no Metal command-buffer accounting problem;
            // queue.writeTexture is simplest and mapped ranges can't be marshaled to JS.
            var dest = new WGPUTexelCopyTextureInfo { texture = texture, aspect = WGPUTextureAspect.All };
            var layout = new WGPUTexelCopyBufferLayout { offset = 0, bytesPerRow = (uint)(width * bytesPerPixel), rowsPerImage = (uint)height };
            var writeSize = new WGPUExtent3D { width = (uint)width, height = (uint)height, depthOrArrayLayers = 1 };
            fixed (byte* p = pixels)
                wgpuQueueWriteTexture(_ctx.Queue, &dest, p, (nuint)pixels.Length, &layout, &writeSize);
#else
            // OpenGL/GLES: upload directly, like the browser. The staged CopyBufferToTexture below
            // exists ONLY to avoid Metal's command-buffer accounting, and it has to PAD every row up
            // to a 256-byte multiple -- which the GLES backend gets wrong: a texture whose natural
            // stride is not already 256-aligned (a 48x48 icon is 192 bytes/row) came out as vertical
            // stripe noise, while an unpadded one (512x512 = 2048) was pixel-perfect. Same asset, same
            // code path, correct on Metal. Sidestep the padding entirely where it buys us nothing.
            if (_ctx.IsOpenGL)
            {
                var glDest = new WGPUTexelCopyTextureInfo { texture = texture, aspect = WGPUTextureAspect.All };
                var glLayout = new WGPUTexelCopyBufferLayout { offset = 0, bytesPerRow = (uint)(width * bytesPerPixel), rowsPerImage = (uint)height };
                var glSize = new WGPUExtent3D { width = (uint)width, height = (uint)height, depthOrArrayLayers = 1 };
                fixed (byte* p = pixels)
                    wgpuQueueWriteTexture(_ctx.Queue, &glDest, p, (nuint)pixels.Length, &glLayout, &glSize);
                return;
            }

            // Desktop (Metal/…): stage into a mappedAtCreation buffer (a pure CPU copy, no command
            // buffer) and record a CopyBufferToTexture into the frame's single command encoder later
            // (FlushPendingTexUploads). wgpu-native's Metal backend commits an UNRECLAIMED command
            // buffer for every queue.write_texture; per-frame uploads pile up to Metal's hard 4096
            // in-flight limit ("outstanding command buffers exceeds the limit" → device lost → fatal).
            // CopyBufferToTexture requires bytesPerRow be a multiple of 256, so pad rows if needed.
            int unpaddedBpr = width * bytesPerPixel;
            int alignedBpr = AlignUp(unpaddedBpr, 256);
            byte[] staging;
            if (alignedBpr == unpaddedBpr)
            {
                staging = pixels;
            }
            else
            {
                staging = new byte[alignedBpr * height];
                for (int r = 0; r < height; r++)
                    Buffer.BlockCopy(pixels, r * unpaddedBpr, staging, r * alignedBpr, unpaddedBpr);
            }
            int soff = AllocTexStaging(staging, alignedBpr * height);
            _pendingTexUploads.Add(new PendingTexUpload { StagingOffset = soff, Texture = texture, Width = width, Height = height, BytesPerRow = alignedBpr });
#endif
        }

        private (IntPtr Texture, IntPtr View) CreateRgbaTexture(byte[] rgba, int width, int height)
            => CreateTexture(rgba, width, height, WGPUTextureFormat.RGBA8Unorm, 4);

        // Bitmap pixels are sRGB-encoded. For an sRGB (display) target, use an sRGB texture so the
        // hardware decodes them to linear on sample, keeping one consistent linear space before the
        // single gamma encode on the final write. For the linear (test) target, pass them through.
        private (IntPtr Texture, IntPtr View) CreateImageTexture(byte[] rgba, int width, int height)
            => CreateTexture(rgba, width, height, _srgbOutput ? WGPUTextureFormat.RGBA8UnormSrgb : WGPUTextureFormat.RGBA8Unorm, 4);

        // Cached image texture: the engine stores each bitmap's pixel array once, so its identity keys a
        // texture reused across frames (was re-created + re-uploaded every frame per image fill). Includes
        // _srgbOutput in the key since the format depends on it. Caller must NOT release the returned view.
        private IntPtr GetOrCreateImageView(byte[] rgba, int width, int height)
        {
            long key = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(rgba) * 2 + (_srgbOutput ? 1 : 0);
            if (_imgCache.TryGetValue(key, out (IntPtr Tex, IntPtr View, long LastFrame) e))
            {
                _imgCache[key] = (e.Tex, e.View, _frameId);
                return e.View;
            }
            var (tex, view) = CreateImageTexture(rgba, width, height);
            _imgCache[key] = (tex, view, _frameId);
            return view;
        }

        // Cached brush-param uniform buffer, keyed by its bytes (small, 64B). Caller must NOT release it.
        private IntPtr GetOrCreateUniform(byte[] uni)
        {
            var h = new HashCode();
            h.AddBytes(uni);
            long key = h.ToHashCode();
            if (_uniCache.TryGetValue(key, out (IntPtr Buf, long LastFrame) e))
            {
                _uniCache[key] = (e.Buf, _frameId);
                return e.Buf;
            }
            IntPtr buf = _ctx.CreateBufferMapped(uni, WGPUBufferUsage.Uniform);
            _uniCache[key] = (buf, _frameId);
            return buf;
        }

        private (IntPtr Texture, IntPtr View) CreateR8Texture(byte[] r8, int width, int height)
            => CreateTexture(r8, width, height, WGPUTextureFormat.R8Unorm, 1);

        // Edge-texture width MUST equal EDGE_TW in _EdgeTexture.wgsl (the shader maps slot i to
        // texel (i % EdgeTexWidth, i / EdgeTexWidth)); 256 => a 256-aligned bytesPerRow (256*8).
        private const int EdgeTexWidth = 256;

        // Packs vec2<f32> edge/segment slots into an RG32Uint texture so fs_coverage/fs_stroke can read
        // them with textureLoad — the GL-ES-3.0/ANGLE stand-in for a fragment storage buffer (which that
        // backend can't bind). The f32 bit patterns ride verbatim in the u32 channels. Each mask/stroke
        // gets its own texture, so shader indices are relative (slot 0 = this object's header).
        private (IntPtr Tex, IntPtr View, int Height) CreateEdgeTexture(ReadOnlySpan<byte> data)
        {
            int slots = Math.Max(1, (data.Length + 7) / 8);
            int rows = (slots + EdgeTexWidth - 1) / EdgeTexWidth;

            // Rounded up to a power of two so that a path which grows by a segment between frames --
            // which is what an animating path does -- lands in the same bucket and reuses the same
            // texture. Extra rows are never indexed: the shader maps slot i to (i % EdgeTexWidth,
            // i / EdgeTexWidth), which does not depend on the texture's height at all.
            int height = BucketRows(rows);

            byte[] padded = new byte[EdgeTexWidth * height * 8];
            data.CopyTo(padded);   // trailing texels stay zero (never indexed)

            (IntPtr tex, IntPtr view) = RentEdgeTexture(height);
            UploadTexture(tex, padded, EdgeTexWidth, height, 8);
            return (tex, view, height);
        }

        private static int BucketRows(int rows)
        {
            int bucket = 1;
            while (bucket < rows) bucket <<= 1;
            return bucket;
        }

        // Pooled edge textures (RG32Uint, TextureBinding|CopyDst, always EdgeTexWidth wide).
        //
        // Every coverage mask and every stroke uploads its segment list as one of these, and until
        // now each was a texture CREATED and destroyed inside a single frame. Measured on twenty
        // animating 200-segment paths that was twenty texture creations a frame; on the static and
        // simply-animated scenes it is zero, because the coverage cache answers those before any of
        // this runs. Creating and destroying textures per frame is the cost the layer pool next door
        // was already written to avoid, on the same reasoning and for the same backends.
        private readonly List<(IntPtr Tex, IntPtr View, int H)> _freeEdgeTex = new();
        private readonly List<(IntPtr Tex, IntPtr View, int H)> _relEdgeTex = new();

        private (IntPtr Tex, IntPtr View) RentEdgeTexture(int height)
        {
            for (int i = 0; i < _freeEdgeTex.Count; i++)
            {
                if (_freeEdgeTex[i].H != height) continue;
                (IntPtr Tex, IntPtr View, int H) hit = _freeEdgeTex[i];
                _freeEdgeTex.RemoveAt(i);
                return (hit.Tex, hit.View);
            }

            PerfEdgeTextures++;
            IntPtr tex = NewSampledTexture(EdgeTexWidth, height, WGPUTextureFormat.RG32Uint);
            return (tex, wgpuTextureCreateView(tex, IntPtr.Zero));
        }

        private void ReturnEdgeTexture(IntPtr tex, IntPtr view, int height)
        {
            const int Cap = 64;   // bound the pool; a scene that briefly needed many gives them back
            if (tex == IntPtr.Zero || _freeEdgeTex.Count >= Cap)
            {
                if (tex != IntPtr.Zero) { wgpuTextureViewRelease(view); wgpuTextureRelease(tex); }
                return;
            }
            _freeEdgeTex.Add((tex, view, height));
        }

        // Builds an edge texture and an auto-layout bind group (binding 0) for the given coverage/stroke
        // pipeline, in one step. Replaces the old storage-buffer + BuildBatchedStorage bind.
        private IntPtr EdgeBindGroup(ReadOnlySpan<byte> data, WGPUTextureFormat passFormat, FillKind kind, bool sourceCopy = false)
        {
            var (tex, view, height) = CreateEdgeTexture(data);
            _relEdgeTex.Add((tex, view, height));   // back to the pool at the end of the frame
            PerfBindGroups++;
            IntPtr layout = GetBindGroupLayout(passFormat, kind, sourceCopy);
            var entry = new WGPUBindGroupEntry { binding = 0, textureView = view };
            var bgDesc = new WGPUBindGroupDescriptor { layout = layout, entryCount = 1, entries = &entry };
            IntPtr bg = wgpuDeviceCreateBindGroup(_ctx.Device, &bgDesc);
            DeferReleaseBindGroup(bg);
            return bg;
        }

        // ---- GPU path rasterization (fs_coverage) ------------------------------

        // Scratch quadratic-segment list for GPU coverage (single render thread; cleared per use).
        // Holds 6 floats per segment (p0, control, p1); fs_coverage flattens curves analytically.
        private readonly List<float> _edgeScratch = new();

        /// <summary>
        /// Rasterizes path coverage into a new R8 mask texture ON THE GPU: emits quadratic
        /// segments on the CPU (no curve flattening — cheap, cached upstream), uploads them to a
        /// storage buffer, and appends an fs_coverage pass to the frame plan (plan passes execute
        /// before any pass that samples the mask). Bounds are the curves' tight extents. Returns
        /// false for an empty path. The caller owns tex/view.
        /// </summary>
        // Cached local-space coverage for brush fills: rasterizes once per unique local geometry +
        // raster scale and keeps the R8 mask across frames (see _covCache). The caller must NOT
        // release the returned view.
        //
        // A non-solid brush is evaluated per-fragment in the geometry's LOCAL space -- fs_maskbrush /
        // fs_maskimage map uv through params.rect -- so unlike the solid path this mask cannot be
        // rasterized in device space. Its RESOLUTION, though, is independent of that: rasterizing at
        // the local DIP size and letting the GPU magnify it by the world transform is what made
        // gradient-filled rounded corners and curved edges pixelate under a DPI scale (3x on a phone)
        // or a zoom, while solid-filled text beside them stayed crisp. Rasterize at the transform's
        // linear scale and report the SAME local-space rect, so every brush kind (linear, radial,
        // image) keeps its existing coordinate math and only the coverage gets denser.
        private bool GetOrRasterizeLocalCoverage(PathGeometry localGeom, Matrix3x2 world,
            out IntPtr view, out float rx, out float ry, out float rw, out float rh)
        {
            view = IntPtr.Zero;
            rx = ry = rw = rh = 0f;

            // Round the raster scale UP to a 1/4 step: rounding down would under-sample, which is the
            // blur being fixed, while bucketing still keeps a smooth zoom from minting a mask per
            // frame. Never below 1 -- that is the old local-DIP resolution, the floor, not a target.
            float bucket = MathF.Ceiling(MathF.Max(LinearScale(world), 1f) * 4f) / 4f;
            float inv = 1f / bucket;

            long key = HashGeometry(localGeom) * 31 + BitConverter.SingleToInt32Bits(bucket);
            if (_covCache.TryGetValue(key, out (IntPtr Tex, IntPtr View, int Ox, int Oy, int W, int H, long LastFrame) e))
            {
                _covCache[key] = (e.Tex, e.View, e.Ox, e.Oy, e.W, e.H, _frameId);
                view = e.View;
                rx = e.Ox * inv; ry = e.Oy * inv; rw = e.W * inv; rh = e.H * inv;
                return true;
            }

            PathGeometry rasterGeom = bucket == 1f
                ? localGeom
                : TransformGeometry(localGeom, Matrix3x2.CreateScale(bucket));
            if (!GpuRasterizeCoverage(rasterGeom, gamma: false, out IntPtr tex, out view,
                    out int ox, out int oy, out int w, out int h))
                return false;

            _covCache[key] = (tex, view, ox, oy, w, h, _frameId);
            rx = ox * inv; ry = oy * inv; rw = w * inv; rh = h * inv;
            return true;
        }

        private bool GpuRasterizeCoverage(PathGeometry path, bool gamma,
            out IntPtr tex, out IntPtr view, out int ox, out int oy, out int w, out int h,
            bool textBlend = false)
        {
            tex = IntPtr.Zero; view = IntPtr.Zero; ox = oy = w = h = 0;
            _edgeScratch.Clear();
            int segCount = PathRasterizer.SegmentsToCubics(path, _edgeScratch, out float minX, out float minY, out float maxX, out float maxY);
            if (segCount == 0 || minX > maxX) return false;
            ox = (int)MathF.Floor(minX) - 1;
            oy = (int)MathF.Floor(minY) - 1;
            w = (int)MathF.Ceiling(maxX) + 1 - ox;
            h = (int)MathF.Ceiling(maxY) + 1 - oy;
            if (w <= 0 || h <= 0) return false;
            (tex, view) = GpuCoveragePass(segCount, ox, oy, w, h, path.FillRule, gamma, textBlend);
            return true;
        }

        // ---- GPU glyph rasterization (outline -> fs_coverage -> persistent atlas) ----

        // Ensures the persistent glyph-atlas render target exists (R8, RenderAttachment so
        // fs_coverage can draw glyphs into it, TextureBinding so text draws sample it). New
        // textures read as zero (transparent) until written, so no explicit clear is needed.
        private void EnsureGpuAtlas()
        {
            if (_gpuAtlasCreated) return;
            var texDesc = new WGPUTextureDescriptor
            {
                usage = WGPUTextureUsage.RenderAttachment | WGPUTextureUsage.TextureBinding,
                dimension = WGPUTextureDimension._2D,
                size = new WGPUExtent3D { width = (uint)_glyphAtlas.Width, height = (uint)_glyphAtlas.Height, depthOrArrayLayers = 1 },
                format = WGPUTextureFormat.R8Unorm,
                mipLevelCount = 1,
                sampleCount = 1,
            };
            _atlasTexture = wgpuDeviceCreateTexture(_ctx.Device, &texDesc);
            _atlasView = wgpuTextureCreateView(_atlasTexture, IntPtr.Zero);
            _atlasValid = true;
            _gpuAtlasCreated = true;
        }

        // Looks up (or first-time GPU-rasterizes) a glyph in the persistent atlas. New glyphs:
        // fetch the outline, flatten for bounds+edges (matching PathRasterizer/TrueTypeFont
        // metrics exactly), shelf-pack the rectangle, and append a load-preserve fs_coverage
        // pass that rasterizes the outline into that rectangle. Returns false if unmappable.
        private bool TryGetOrAddGpuGlyph(int glyphId, out Text.GlyphEntry entry)
        {
            if (_glyphAtlas.TryGet(glyphId, out entry))
                return true;

            if (_outlineFont == null || !_outlineFont.TryGetGlyphOutline(glyphId, out System.Collections.Generic.List<PathFigure> figures))
            {
                // Blank/missing glyph (e.g. space): metrics-only entry, no atlas rect.
                return _glyphAtlas.AddPacked(glyphId, 0, 0, 0, 0, _outlineFont?.PixelsPerEm ?? 0, out entry, out _, out _, out _, out _);
            }

            _edgeScratch.Clear();
            var outline = new PathGeometry(FillRule.NonZero, figures);
            int segCount = PathRasterizer.SegmentsToCubics(outline, _edgeScratch, out float minX, out float minY, out float maxX, out float maxY);
            if (segCount == 0 || minX > maxX)
                return _glyphAtlas.AddPacked(glyphId, 0, 0, 0, 0, _outlineFont.PixelsPerEm, out entry, out _, out _, out _, out _);

            // Same bounds/bearing math as PathRasterizer.Rasterize / TrueTypeFont.TryGetGlyph.
            int originX = (int)MathF.Floor(minX) - 1;
            int originY = (int)MathF.Floor(minY) - 1;
            int gw = (int)MathF.Ceiling(maxX) + 1 - originX;
            int gh = (int)MathF.Ceiling(maxY) + 1 - originY;
            if (gw <= 0 || gh <= 0)
                return _glyphAtlas.AddPacked(glyphId, 0, 0, 0, 0, _outlineFont.PixelsPerEm, out entry, out _, out _, out _, out _);

            if (!_glyphAtlas.AddPacked(glyphId, gw, gh, 0, originX, -originY,
                    out entry, out int rx, out int ry, out int rw, out int rh))
                return false;   // atlas full -> skip this glyph

            EnsureGpuAtlas();
            AppendGlyphCoveragePass(rx, ry, rw, rh, originX, originY, segCount, outline.FillRule);
            return true;
        }

        // Appends a load-preserve fs_coverage pass that rasterizes the current _edgeScratch outline
        // (quadratic segments rebased to glyph-local) into the atlas rectangle (rx,ry,rw,rh). Runs
        // before any text pass samples the atlas (added to _plan during collection).
        private void AppendGlyphCoveragePass(int rx, int ry, int rw, int rh, int originX, int originY, int segCount, FillRule rule)
        {
            Span<float> es = CollectionsMarshal.AsSpan(_edgeScratch);
            for (int i = 0; i + 1 < es.Length; i += 2) { es[i] -= originX; es[i + 1] -= originY; }

            IntPtr bg = EdgeBindGroup(MemoryMarshal.AsBytes(es), WGPUTextureFormat.R8Unorm, FillKind.Coverage);

            // Quad over the atlas rectangle in atlas NDC; uv = glyph-local pixel coords for fs_coverage.
            float aw = _glyphAtlas.Width, ah = _glyphAtlas.Height;
            float x0 = rx / aw * 2f - 1f, x1 = (rx + rw) / aw * 2f - 1f;
            float y0 = 1f - ry / ah * 2f, y1 = 1f - (ry + rh) / ah * 2f;
            float flags = rule == FillRule.EvenOdd ? 1f : 0f;   // glyphs: no text gamma (matches CPU atlas)
            DrawData d = RentDrawData();
            AddVertex(d.Verts, new Vector2(x0, y0), segCount, flags, 0f, 0f, 0f, 0f);
            AddVertex(d.Verts, new Vector2(x1, y0), segCount, flags, 0f, 0f, rw, 0f);
            AddVertex(d.Verts, new Vector2(x1, y1), segCount, flags, 0f, 0f, rw, rh);
            AddVertex(d.Verts, new Vector2(x0, y1), segCount, flags, 0f, 0f, 0f, rh);
            AddQuadIndices(d.Indices, 0);
            d.Draws.Add(new DrawItem(0, 6, new Scissor(rx, ry, rw, rh), FillKind.Coverage, bg));
            _plan.Add(new LayerPass(_atlasView, false, default, d, WGPUTextureFormat.R8Unorm)
            { LoadPreserve = true, TexW = (int)aw, TexH = (int)ah });
        }

        // Rasterizes a round stroke's coverage into a new R8 mask ON THE GPU (fs_stroke SDF):
        // flattens the centre-line, pads the bounds by the half-width, and appends a stroke pass.
        // Returns false for an empty centre-line. The caller owns tex/view.
        private bool GpuStrokeRasterize(PathGeometry centerline, float half,
            out IntPtr tex, out IntPtr view, out int ox, out int oy, out int w, out int h)
        {
            tex = IntPtr.Zero; view = IntPtr.Zero; ox = oy = w = h = 0;
            _edgeScratch.Clear();
            int segCount = PathRasterizer.FlattenCenterlineSegments(centerline, _edgeScratch, out float minX, out float minY, out float maxX, out float maxY);
            if (segCount == 0 || minX > maxX) return false;
            ox = (int)MathF.Floor(minX - half) - 1;
            oy = (int)MathF.Floor(minY - half) - 1;
            w = (int)MathF.Ceiling(maxX + half) + 1 - ox;
            h = (int)MathF.Ceiling(maxY + half) + 1 - oy;
            if (w <= 0 || h <= 0) return false;

            Span<float> es = CollectionsMarshal.AsSpan(_edgeScratch);
            for (int i = 0; i + 1 < es.Length; i += 2) { es[i] -= ox; es[i + 1] -= oy; }

            IntPtr bg = EdgeBindGroup(MemoryMarshal.AsBytes(es), WGPUTextureFormat.R8Unorm, FillKind.Stroke);

            (tex, view) = RentMaskTexture(w, h);

            // Full-target quad in the mask's own NDC; uv = mask-local pixel coords; the flat vertex
            // colour carries (segCount, halfWidth) — matching fs_stroke's decoding.
            DrawData d = RentDrawData();
            AddVertex(d.Verts, new Vector2(-1f, 1f), segCount, half, 0f, 0f, 0f, 0f);
            AddVertex(d.Verts, new Vector2(1f, 1f), segCount, half, 0f, 0f, w, 0f);
            AddVertex(d.Verts, new Vector2(1f, -1f), segCount, half, 0f, 0f, w, h);
            AddVertex(d.Verts, new Vector2(-1f, -1f), segCount, half, 0f, 0f, 0f, h);
            AddQuadIndices(d.Indices, 0);
            d.Draws.Add(new DrawItem(0, 6, new Scissor(0, 0, w, h), FillKind.Stroke, bg));
            _plan.Add(new LayerPass(view, true, default, d, WGPUTextureFormat.R8Unorm) { TexW = w, TexH = h });
            return true;
        }

        /// <summary>GPU equivalent of PathRasterizer.RasterizeInto: coverage into a
        /// fixed-size mask whose pixel (0,0) maps to device (originX, originY).</summary>
        private (IntPtr Tex, IntPtr View) GpuRasterizeInto(PathGeometry path, int width, int height, int originX, int originY)
        {
            _edgeScratch.Clear();
            int segCount = PathRasterizer.SegmentsToCubics(path, _edgeScratch, out _, out _, out _, out _);
            return GpuCoveragePass(segCount, originX, originY, width, height, path.FillRule, gamma: false);
        }

        private (IntPtr Tex, IntPtr View) GpuCoveragePass(int segCount, int ox, int oy, int w, int h,
            FillRule rule, bool gamma, bool textBlend = false)
        {
            // Rebase segment control points to mask-local coordinates (all x/y pairs; the shader's
            // pixel coords are mask-local via uv).
            Span<float> es = CollectionsMarshal.AsSpan(_edgeScratch);
            for (int i = 0; i + 1 < es.Length; i += 2) { es[i] -= ox; es[i + 1] -= oy; }

            _bandScratch.Clear();
            BuildScanlineBands(es, h, _bandScratch);
            Span<float> es2 = CollectionsMarshal.AsSpan(_bandScratch);

            // This mask's edges go in their own RG32Uint texture that fs_coverage reads via textureLoad
            // (GL ES 3.0 / ANGLE can't bind a fragment storage buffer).
            IntPtr edgeBg = EdgeBindGroup(MemoryMarshal.AsBytes(es2), WGPUTextureFormat.R8Unorm, FillKind.Coverage);

            (IntPtr tex, IntPtr view) = RentMaskTexture(w, h);

            PerfBindGroups++;
            // One full-target quad in the mask texture's own NDC — deliberately NOT ToNdc,
            // which offsets by the ambient layer-bake origin (_devOX/_devOY): a mask created
            // during a region-sized card bake would render shifted off its own target (and
            // the blank result would be cached). uv = mask-local pixel coords; the flat
            // vertex colour carries (segCount, flags) — matching fs_coverage's decoding.
            float flags = (rule == FillRule.EvenOdd ? 1f : 0f) + (gamma ? 2f : 0f)
                          + (_aliasedEdges ? 4f : 0f) + (textBlend ? 8f : 0f);
            DrawData d = RentDrawData();
            AddVertex(d.Verts, new Vector2(-1f, 1f), segCount, flags, 0f, 0f, 0f, 0f);
            AddVertex(d.Verts, new Vector2(1f, 1f), segCount, flags, 0f, 0f, w, 0f);
            AddVertex(d.Verts, new Vector2(1f, -1f), segCount, flags, 0f, 0f, w, h);
            AddVertex(d.Verts, new Vector2(-1f, -1f), segCount, flags, 0f, 0f, 0f, h);
            AddQuadIndices(d.Indices, 0);
            d.Draws.Add(new DrawItem(0, 6, new Scissor(0, 0, w, h), FillKind.Coverage, edgeBg));
            _plan.Add(new LayerPass(view, true, default, d, WGPUTextureFormat.R8Unorm) { TexW = w, TexH = h });
            return (tex, view);
        }

        // ---- GPU brush evaluation (fs_maskbrush / fs_brushalpha) -----------------

        private static bool IsGpuGradient(Brush b) => b is LinearGradientBrush or RadialGradientBrush;

        // Fills the 64-byte BrushParams uniform for a gradient. localOrigin/localSize is the
        // brush-space rect the quad's uv 0..1 maps across; gradient geometry (start/axis or
        // centre/radius) is already in that same space (local for masked fills, device for
        // opacity masks — the caller pre-transforms it). Mirrors EvaluateBrush's math.
        private static byte[] BuildBrushParams(Brush brush, Vector2 g0, Vector2 g1,
            float rectX, float rectY, float rectW, float rectH, float opacity)
        {
            var buf = new byte[64];
            Span<uint> u = MemoryMarshal.Cast<byte, uint>((Span<byte>)buf);
            Span<float> f = MemoryMarshal.Cast<byte, float>((Span<byte>)buf);
            if (brush is LinearGradientBrush lg)
            {
                u[0] = 1; u[1] = SpreadCode(lg.SpreadMethod);
                Vector2 axis = g1 - g0;
                f[4] = g0.X; f[5] = g0.Y; f[6] = axis.X; f[7] = axis.Y;
                float len2 = axis.LengthSquared();
                f[13] = len2 > 0f ? 1f / len2 : 0f;   // misc.y = 1/|axis|^2
            }
            else if (brush is RadialGradientBrush)
            {
                u[0] = 2; u[1] = SpreadCode(((RadialGradientBrush)brush).SpreadMethod);
                f[4] = g0.X; f[5] = g0.Y; f[6] = g1.X; f[7] = g1.Y;  // g1 = (radiusX, radiusY)
            }
            f[8] = rectX; f[9] = rectY; f[10] = rectW; f[11] = rectH; // rect
            f[12] = opacity;                                          // misc.x
            return buf;
        }

        private static uint SpreadCode(GradientSpreadMethod s) => s switch
        {
            GradientSpreadMethod.Reflect => 1u,
            GradientSpreadMethod.Repeat => 2u,
            _ => 0u,
        };

        private static GradientStop[] BrushStops(Brush b) => b switch
        {
            LinearGradientBrush lg => lg.Stops,
            RadialGradientBrush rg => rg.Stops,
            _ => Array.Empty<GradientStop>(),
        };

        // format must be the pipeline format of the pass the returned bind group is drawn in:
        // auto-layout bind groups are exclusive to the exact pipeline their layout came from.
        private IntPtr CreateBrushBindGroup(WGPUTextureFormat format, FillKind kind, IntPtr covView, IntPtr rampView, IntPtr ubuf, int uniSize,
            bool sourceCopy = false)
        {

            PerfBindGroups++;
            IntPtr layout = GetBindGroupLayout(format, kind, sourceCopy);
            if (kind is FillKind.MaskBrush or FillKind.MaskImage)
            {
                // binding(1) = ramp (gradient) or image (fs_maskimage); identical layout.
                var e = stackalloc WGPUBindGroupEntry[5];
                e[0] = new WGPUBindGroupEntry { binding = 0, textureView = covView };
                e[1] = new WGPUBindGroupEntry { binding = 1, textureView = rampView };
                e[2] = new WGPUBindGroupEntry { binding = 2, sampler = LinearSampler() };
                e[3] = new WGPUBindGroupEntry { binding = 3, sampler = LinearSampler() };
                e[4] = new WGPUBindGroupEntry { binding = 4, buffer = ubuf, offset = 0, size = (ulong)uniSize };
                var desc = new WGPUBindGroupDescriptor { layout = layout, entryCount = 5, entries = e };
                return wgpuDeviceCreateBindGroup(_ctx.Device, &desc);
            }
            else
            {
                var e = stackalloc WGPUBindGroupEntry[3];
                e[0] = new WGPUBindGroupEntry { binding = 0, textureView = rampView };
                e[1] = new WGPUBindGroupEntry { binding = 1, sampler = LinearSampler() };
                e[2] = new WGPUBindGroupEntry { binding = 2, buffer = ubuf, offset = 0, size = (ulong)uniSize };
                var desc = new WGPUBindGroupDescriptor { layout = layout, entryCount = 3, entries = e };
                return wgpuDeviceCreateBindGroup(_ctx.Device, &desc);
            }
        }

        // Masked gradient fill on the GPU: rasterize local coverage, then composite it with
        // the gradient evaluated per-fragment (fs_maskbrush) — no CPU per-texel bake. The
        // brush geometry is in the same LOCAL space as the coverage; the quad carries the
        // world transform. Returns false if the coverage is empty.
        private bool EmitGpuGradientMask(PathGeometry localGeom, Brush gradient, Matrix3x2 world, double opacity,
            Scissor clip, int width, int height, WGPUTextureFormat format, DrawData data)
        {
            if (!GetOrRasterizeLocalCoverage(localGeom, world, out IntPtr covView, out float ox, out float oy, out float w, out float h))
                return false;

            IntPtr rampView = GetOrCreateRampView(BrushStops(gradient));

            (Vector2 g0, Vector2 g1) = gradient is LinearGradientBrush lg
                ? (lg.Start, lg.End)
                : (((RadialGradientBrush)gradient).Center,
                   new Vector2(((RadialGradientBrush)gradient).RadiusX, ((RadialGradientBrush)gradient).RadiusY));
            byte[] uni = BuildBrushParams(gradient, g0, g1, ox, oy, w, h, (float)Math.Clamp(opacity, 0.0, 1.0));
            IntPtr ubuf = GetOrCreateUniform(uni);

            IntPtr bg = CreateBrushBindGroup(format, FillKind.MaskBrush, covView, rampView, ubuf, uni.Length, _srcCopy);
            DeferReleaseBindGroup(bg);

            float x0 = ox, y0 = oy, x1 = ox + w, y1 = oy + h;
            uint baseVertex = (uint)(data.Verts.Count / FloatsPerVertex);
            AddVertex(data.Verts, ToNdc(Vector2.Transform(new Vector2(x0, y0), world), width, height), 1f, 1f, 1f, 1f, 0f, 0f);
            AddVertex(data.Verts, ToNdc(Vector2.Transform(new Vector2(x1, y0), world), width, height), 1f, 1f, 1f, 1f, 1f, 0f);
            AddVertex(data.Verts, ToNdc(Vector2.Transform(new Vector2(x1, y1), world), width, height), 1f, 1f, 1f, 1f, 1f, 1f);
            AddVertex(data.Verts, ToNdc(Vector2.Transform(new Vector2(x0, y1), world), width, height), 1f, 1f, 1f, 1f, 0f, 1f);
            uint firstIndex = (uint)data.Indices.Count;
            AddQuadIndices(data.Indices, baseVertex);
            data.Draws.Add(new DrawItem(firstIndex, 6, clip, FillKind.MaskBrush, bg, sourceCopy: _srcCopy));
            return true;
        }

        // Masked image/tile fill on the GPU: rasterize local coverage, upload the image (an
        // sRGB texture on the display path so filtering happens in linear space), and let
        // fs_maskimage do the tile/flip UV + bilinear sample per-fragment — replacing the CPU
        // BakeBrushMask/SampleBilinear per-texel loop. Returns false if empty.
        private bool EmitGpuImageMask(PathGeometry localGeom, ImageBrush img, Matrix3x2 world, double opacity,
            Scissor clip, int width, int height, WGPUTextureFormat format, DrawData data)
        {
            if (img.PixelWidth <= 0 || img.PixelHeight <= 0)
                return false;
            if (!GetOrRasterizeLocalCoverage(localGeom, world, out IntPtr covView, out float ox, out float oy, out float w, out float h))
                return false;

            IntPtr imgView = GetOrCreateImageView(img.PixelsRgba, img.PixelWidth, img.PixelHeight);

            byte[] uni = BuildImageBrushParams(img, ox, oy, w, h, (float)Math.Clamp(opacity * img.Opacity, 0.0, 1.0));
            IntPtr ubuf = GetOrCreateUniform(uni);

            IntPtr bg = CreateBrushBindGroup(format, FillKind.MaskImage, covView, imgView, ubuf, uni.Length, _srcCopy);
            DeferReleaseBindGroup(bg);

            float x0 = ox, y0 = oy, x1 = ox + w, y1 = oy + h;
            uint baseVertex = (uint)(data.Verts.Count / FloatsPerVertex);
            AddVertex(data.Verts, ToNdc(Vector2.Transform(new Vector2(x0, y0), world), width, height), 1f, 1f, 1f, 1f, 0f, 0f);
            AddVertex(data.Verts, ToNdc(Vector2.Transform(new Vector2(x1, y0), world), width, height), 1f, 1f, 1f, 1f, 1f, 0f);
            AddVertex(data.Verts, ToNdc(Vector2.Transform(new Vector2(x1, y1), world), width, height), 1f, 1f, 1f, 1f, 1f, 1f);
            AddVertex(data.Verts, ToNdc(Vector2.Transform(new Vector2(x0, y1), world), width, height), 1f, 1f, 1f, 1f, 0f, 1f);
            uint firstIndex = (uint)data.Indices.Count;
            AddQuadIndices(data.Indices, baseVertex);
            data.Draws.Add(new DrawItem(firstIndex, 6, clip, FillKind.MaskImage, bg, sourceCopy: _srcCopy));
            return true;
        }

        // Fills the BrushParams uniform for an image/tile brush: kindSpread.y = TileMode,
        // g0.xy = (tileWidth, tileHeight), rect = coverage bounds, misc.x = opacity.
        private static byte[] BuildImageBrushParams(ImageBrush img, float ox, float oy, float w, float h, float opacity)
        {
            var buf = new byte[64];
            Span<uint> u = MemoryMarshal.Cast<byte, uint>((Span<byte>)buf);
            Span<float> f = MemoryMarshal.Cast<byte, float>((Span<byte>)buf);
            u[0] = 3;                        // image marker
            u[1] = (uint)img.TileMode;
            f[4] = img.TileWidth; f[5] = img.TileHeight;
            f[8] = ox; f[9] = oy; f[10] = w; f[11] = h;
            f[12] = opacity;
            return buf;
        }

        // Gradient opacity mask on the GPU: an R8 alpha target evaluated per-device-pixel
        // (fs_brushalpha), replacing RasterizeOpacityMask's CPU loop. Endpoints are
        // transformed to device space (matching the CPU path). Returns false for
        // non-gradient brushes (the caller keeps the trivial CPU fill).
        private bool GpuOpacityMask(Brush brush, Matrix3x2 world, int width, int height, int originX, int originY,
            out IntPtr tex, out IntPtr view)
        {
            tex = IntPtr.Zero; view = IntPtr.Zero;
            if (!IsGpuGradient(brush)) return false;

            Vector2 g0, g1;
            if (brush is LinearGradientBrush lg)
            {
                g0 = Vector2.Transform(lg.Start, world);
                g1 = Vector2.Transform(lg.End, world);
            }
            else
            {
                var rg = (RadialGradientBrush)brush;
                g0 = Vector2.Transform(rg.Center, world);
                g1 = new Vector2(rg.RadiusX * new Vector2(world.M11, world.M12).Length(),
                                 rg.RadiusY * new Vector2(world.M21, world.M22).Length());
            }
            byte[] uni = BuildBrushParams(brush, g0, g1, originX, originY, width, height, 1f);
            IntPtr ubuf = GetOrCreateUniform(uni);

            IntPtr rampView = GetOrCreateRampView(BrushStops(brush));

            (tex, view) = RentMaskTexture(width, height);

            IntPtr bg = CreateBrushBindGroup(WGPUTextureFormat.R8Unorm, FillKind.BrushAlpha, IntPtr.Zero, rampView, ubuf, uni.Length);
            DeferReleaseBindGroup(bg);

            DrawData d = RentDrawData();
            AddVertex(d.Verts, new Vector2(-1f, 1f), 1f, 1f, 1f, 1f, 0f, 0f);
            AddVertex(d.Verts, new Vector2(1f, 1f), 1f, 1f, 1f, 1f, 1f, 0f);
            AddVertex(d.Verts, new Vector2(1f, -1f), 1f, 1f, 1f, 1f, 1f, 1f);
            AddVertex(d.Verts, new Vector2(-1f, -1f), 1f, 1f, 1f, 1f, 0f, 1f);
            AddQuadIndices(d.Indices, 0);
            d.Draws.Add(new DrawItem(0, 6, new Scissor(0, 0, width, height), FillKind.BrushAlpha, bg));
            _plan.Add(new LayerPass(view, true, default, d, WGPUTextureFormat.R8Unorm) { TexW = width, TexH = height });
            return true;
        }

        private IntPtr CreateSampledBindGroup(WGPUTextureFormat format, FillKind kind, IntPtr view, IntPtr sampler,
            bool sourceCopy = false)
        {

            PerfBindGroups++;
            IntPtr layout = GetBindGroupLayout(format, kind, sourceCopy);
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

        // Returns a cached (across frames) gradient ramp texture VIEW for these stops, creating+uploading
        // it only on a miss. The cache owns the texture (do NOT defer-release it); callers just bind the view.
        private IntPtr GetOrCreateRampView(GradientStop[] stops)
        {
            var h = new HashCode();
            foreach (GradientStop s in stops) { h.Add(s.Offset); h.Add(s.Color.R); h.Add(s.Color.G); h.Add(s.Color.B); h.Add(s.Color.A); }
            long key = h.ToHashCode();
            if (_rampCache.TryGetValue(key, out (IntPtr Tex, IntPtr View, long LastFrame) e))
            {
                _rampCache[key] = (e.Tex, e.View, _frameId);
                return e.View;
            }
            var (tex, view) = CreateRgbaTexture(BuildGradientRamp(stops), GradientRampTexels, 1);
            _rampCache[key] = (tex, view, _frameId);
            return view;
        }

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

        // Interpolate gradient stops in sRGB (gamma) space, matching WPF's default
        // GradientBrush.ColorInterpolationMode = SRgbLinearInterpolation. Our stop colours are scRGB
        // (linear, as they arrive in the MIL stream), so convert each endpoint linear->sRGB, lerp,
        // then convert back to linear for the linear compositing pipeline. Alpha stays linear.
        // (A plain linear lerp shifts midtones -- e.g. red->orange midpoint reads too green/blue.)
        private static RgbaColor Lerp(RgbaColor a, RgbaColor b, float f)
        {
            // Gamma mode: stops are already sRGB-encoded, so a plain lerp IS the sRGB-space interpolation
            // (no linear<->sRGB round-trip). Linear mode: convert each endpoint to sRGB, lerp, back to linear.
            if (s_gammaComposite)
                return new(a.R + (b.R - a.R) * f, a.G + (b.G - a.G) * f, a.B + (b.B - a.B) * f, a.A + (b.A - a.A) * f);
            float Ch(float la, float lb) => SrgbToLinear(LinearToSrgb(la) + (LinearToSrgb(lb) - LinearToSrgb(la)) * f);
            return new(Ch(a.R, b.R), Ch(a.G, b.G), Ch(a.B, b.B), a.A + (b.A - a.A) * f);
        }

        private static float LinearToSrgb(float c)
        {
            c = Math.Clamp(c, 0f, 1f);
            return c <= 0.0031308f ? c * 12.92f : 1.055f * MathF.Pow(c, 1f / 2.4f) - 0.055f;
        }

        private static float SrgbToLinear(float c)
        {
            c = Math.Clamp(c, 0f, 1f);
            return c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
        }

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