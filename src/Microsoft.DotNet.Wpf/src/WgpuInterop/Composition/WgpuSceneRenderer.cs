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
    internal sealed unsafe class WgpuSceneRenderer
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
";

        private enum FillKind { Solid, Textured, Text }

        private readonly WgpuContext _ctx;
        private readonly Dictionary<(WGPUTextureFormat, FillKind), IntPtr> _pipelines = new();
        private readonly Text.BuiltinBitmapFont _font = new();
        private readonly Text.GlyphAtlas _glyphAtlas = new();
        private IntPtr _shaderModule;
        private IntPtr _linearSampler;
        private IntPtr _nearestSampler;

        public WgpuSceneRenderer(WgpuContext ctx) => _ctx = ctx;

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

        public byte[] RenderToRgba(SceneVisual root, int width, int height, RgbaColor background)
        {
            DrawData data = BuildDrawData(root, width, height, ReadbackFormat);
            return SubmitWithReadback(width, height, background, data);
        }

        /// <summary>
        /// Renders the scene into an externally owned texture view (e.g. a
        /// swap-chain back buffer) using the given target format. No readback.
        /// </summary>
        public void RenderSceneToView(SceneVisual root, IntPtr view, WGPUTextureFormat format, int width, int height, RgbaColor background)
        {
            DrawData data = BuildDrawData(root, width, height, format);

            IntPtr device = _ctx.Device;
            BuildGeometryBuffers(data, out IntPtr vbuf, out IntPtr ibuf, out bool hasGeometry);
            IntPtr atlasBindGroup = EnsureAtlasBindGroup(data, format);

            IntPtr encoder = wgpuDeviceCreateCommandEncoder(device, IntPtr.Zero);
            IntPtr pass = BeginClearPass(encoder, view, background);
            if (hasGeometry)
                RecordDraws(pass, format, vbuf, ibuf, data, atlasBindGroup);
            wgpuRenderPassEncoderEnd(pass);

            IntPtr commandBuffer = wgpuCommandEncoderFinish(encoder, IntPtr.Zero);
            IntPtr* cmds = stackalloc IntPtr[1];
            cmds[0] = commandBuffer;
            wgpuQueueSubmit(_ctx.Queue, 1, cmds);
        }

        // ---- scene walk ----

        private DrawData BuildDrawData(SceneVisual root, int width, int height, WGPUTextureFormat format)
        {
            var data = new DrawData();
            var full = new Scissor(0, 0, width, height);
            Walk(root, Matrix3x2.Identity, 1.0, full, width, height, format, data);
            return data;
        }

        private void Walk(SceneVisual v, Matrix3x2 parentWorld, double opacityAcc, Scissor parentClip,
            int width, int height, WGPUTextureFormat format, DrawData data)
        {
            Matrix3x2 world = v.LocalToParent * parentWorld;
            double opacity = opacityAcc * Math.Clamp(v.Opacity, 0.0, 1.0);

            Scissor clip = parentClip;
            if (v.Clip.HasValue)
                clip = Intersect(parentClip, DeviceBounds(v.Clip.Value, world, width, height));

            foreach (DrawingPrimitive primitive in v.Content)
            {
                switch (primitive)
                {
                    case GeometryFill fill:
                        EmitFill(fill, world, opacity, clip, width, height, format, data);
                        break;
                    case GlyphRunDraw run:
                        EmitText(run, world, opacity, clip, width, height, data);
                        break;
                }
            }

            foreach (SceneVisual child in v.Children)
                Walk(child, world, opacity, clip, width, height, format, data);
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
                    IntPtr view = CreateTexture(BuildGradientRamp(grad.Stops), GradientRampTexels, 1);
                    bindGroup = CreateSampledBindGroup(format, FillKind.Textured, view, LinearSampler());
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
                    IntPtr view = CreateTexture(img.PixelsRgba, img.PixelWidth, img.PixelHeight);
                    bindGroup = CreateSampledBindGroup(format, FillKind.Textured, view, NearestSampler());
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

        // Fills an arbitrary path: rasterize coverage on the CPU, upload it as an
        // R8 mask, and draw a single quad sampling it (fs_text) tinted by the
        // brush. Solid brushes only for now; the AA lives entirely in the mask.
        private void EmitPath(GeometryFill fill, PathGeometry path, Matrix3x2 world, double opacity, Scissor clip,
            int width, int height, WGPUTextureFormat format, DrawData data)
        {
            if (fill.Brush is not SolidColorBrush solid) return; // gradient/image on paths: future
            CoverageMask mask = PathRasterizer.Rasterize(path);
            if (mask.IsEmpty) return;

            IntPtr view = CreateR8Texture(mask.Coverage, mask.Width, mask.Height);
            IntPtr bindGroup = CreateSampledBindGroup(format, FillKind.Text, view, NearestSampler());

            float a = (float)Math.Clamp(solid.Color.A * opacity, 0.0, 1.0);
            float x0 = mask.OriginX, y0 = mask.OriginY;
            float x1 = x0 + mask.Width, y1 = y0 + mask.Height;

            uint baseVertex = (uint)(data.Verts.Count / FloatsPerVertex);
            AddVertex(data.Verts, ToNdc(Vector2.Transform(new Vector2(x0, y0), world), width, height), solid.Color.R, solid.Color.G, solid.Color.B, a, 0f, 0f);
            AddVertex(data.Verts, ToNdc(Vector2.Transform(new Vector2(x1, y0), world), width, height), solid.Color.R, solid.Color.G, solid.Color.B, a, 1f, 0f);
            AddVertex(data.Verts, ToNdc(Vector2.Transform(new Vector2(x1, y1), world), width, height), solid.Color.R, solid.Color.G, solid.Color.B, a, 1f, 1f);
            AddVertex(data.Verts, ToNdc(Vector2.Transform(new Vector2(x0, y1), world), width, height), solid.Color.R, solid.Color.G, solid.Color.B, a, 0f, 1f);

            uint firstIndex = (uint)data.Indices.Count;
            foreach (uint li in new uint[] { 0, 1, 2, 0, 2, 3 })
                data.Indices.Add(baseVertex + li);
            data.Draws.Add(new DrawItem(firstIndex, 6, clip, FillKind.Text, bindGroup));
        }

        // Lays out a glyph run: each glyph becomes a quad sampling the atlas.
        // Glyph layout happens in local space (so transforms/opacity carry the
        // text like any other content); the shared atlas bind group is supplied
        // at record time.
        private void EmitText(GlyphRunDraw run, Matrix3x2 world, double opacity, Scissor clip, int width, int height, DrawData data)
        {
            if (clip.IsEmpty || string.IsNullOrEmpty(run.Text)) return;

            float scale = run.EmSize / _font.Ascent;
            float a = (float)Math.Clamp(run.Color.A * opacity, 0.0, 1.0);
            float penX = run.Origin.X;
            float baseline = run.Origin.Y;

            foreach (char c in run.Text)
            {
                if (!_glyphAtlas.TryGetOrAdd(_font, c, out Text.GlyphEntry e))
                    continue;

                if (e.Width > 0 && e.Height > 0)
                {
                    float x0 = penX + e.BearingX * scale;
                    float y0 = baseline - e.BearingY * scale;
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

                penX += e.Advance * scale;
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

        // ---- submission ----

        private byte[] SubmitWithReadback(int width, int height, RgbaColor background, DrawData data)
        {
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
            IntPtr texture = wgpuDeviceCreateTexture(device, &texDesc);
            IntPtr view = wgpuTextureCreateView(texture, IntPtr.Zero);
            IntPtr readback = _ctx.CreateBuffer(readbackSize, WGPUBufferUsage.CopyDst | WGPUBufferUsage.MapRead);

            BuildGeometryBuffers(data, out IntPtr vbuf, out IntPtr ibuf, out bool hasGeometry);
            IntPtr atlasBindGroup = EnsureAtlasBindGroup(data, ReadbackFormat);

            IntPtr encoder = wgpuDeviceCreateCommandEncoder(device, IntPtr.Zero);
            IntPtr pass = BeginClearPass(encoder, view, background);
            if (hasGeometry)
                RecordDraws(pass, ReadbackFormat, vbuf, ibuf, data, atlasBindGroup);
            wgpuRenderPassEncoderEnd(pass);

            var copySrc = new WGPUTexelCopyTextureInfo { texture = texture, aspect = WGPUTextureAspect.All };
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
            return pixels;
        }

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

        // Uploads the current glyph atlas as an R8 texture and returns a bind
        // group for the text pipeline, or Zero when the scene has no text.
        private IntPtr EnsureAtlasBindGroup(DrawData data, WGPUTextureFormat format)
        {
            if (!data.HasText) return IntPtr.Zero;
            IntPtr view = CreateR8Texture(_glyphAtlas.Pixels, _glyphAtlas.Width, _glyphAtlas.Height);
            _glyphAtlas.ClearDirty();
            return CreateSampledBindGroup(format, FillKind.Text, view, NearestSampler());
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
            IntPtr pipeline = CreatePipeline(_ctx.Device, GetShaderModule(), format, kind);
            _pipelines[key] = pipeline;
            return pipeline;
        }

        private static IntPtr CreatePipeline(IntPtr device, IntPtr shader, WGPUTextureFormat targetFormat, FillKind kind)
        {
            string fsName = kind switch
            {
                FillKind.Textured => "fs_textured",
                FillKind.Text => "fs_text",
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

        private IntPtr CreateTexture(byte[] rgba, int width, int height)
        {
            var texDesc = new WGPUTextureDescriptor
            {
                usage = WGPUTextureUsage.TextureBinding | WGPUTextureUsage.CopyDst,
                dimension = WGPUTextureDimension._2D,
                size = new WGPUExtent3D { width = (uint)width, height = (uint)height, depthOrArrayLayers = 1 },
                format = WGPUTextureFormat.RGBA8Unorm,
                mipLevelCount = 1,
                sampleCount = 1,
            };
            IntPtr texture = wgpuDeviceCreateTexture(_ctx.Device, &texDesc);

            var dest = new WGPUTexelCopyTextureInfo { texture = texture, aspect = WGPUTextureAspect.All };
            var layout = new WGPUTexelCopyBufferLayout { offset = 0, bytesPerRow = (uint)(width * 4), rowsPerImage = (uint)height };
            var writeSize = new WGPUExtent3D { width = (uint)width, height = (uint)height, depthOrArrayLayers = 1 };
            fixed (byte* p = rgba)
                wgpuQueueWriteTexture(_ctx.Queue, &dest, p, (nuint)rgba.Length, &layout, &writeSize);

            return wgpuTextureCreateView(texture, IntPtr.Zero);
        }

        private IntPtr CreateSampledBindGroup(WGPUTextureFormat format, FillKind kind, IntPtr view, IntPtr sampler)
        {
            IntPtr layout = wgpuRenderPipelineGetBindGroupLayout(GetPipeline(format, kind), 0);
            var entries = stackalloc WGPUBindGroupEntry[2];
            entries[0] = new WGPUBindGroupEntry { binding = 0, textureView = view };
            entries[1] = new WGPUBindGroupEntry { binding = 1, sampler = sampler };
            var desc = new WGPUBindGroupDescriptor { layout = layout, entryCount = 2, entries = entries };
            return wgpuDeviceCreateBindGroup(_ctx.Device, &desc);
        }

        private IntPtr CreateR8Texture(byte[] r8, int width, int height)
        {
            var texDesc = new WGPUTextureDescriptor
            {
                usage = WGPUTextureUsage.TextureBinding | WGPUTextureUsage.CopyDst,
                dimension = WGPUTextureDimension._2D,
                size = new WGPUExtent3D { width = (uint)width, height = (uint)height, depthOrArrayLayers = 1 },
                format = WGPUTextureFormat.R8Unorm,
                mipLevelCount = 1,
                sampleCount = 1,
            };
            IntPtr texture = wgpuDeviceCreateTexture(_ctx.Device, &texDesc);

            var dest = new WGPUTexelCopyTextureInfo { texture = texture, aspect = WGPUTextureAspect.All };
            var layout = new WGPUTexelCopyBufferLayout { offset = 0, bytesPerRow = (uint)width, rowsPerImage = (uint)height };
            var writeSize = new WGPUExtent3D { width = (uint)width, height = (uint)height, depthOrArrayLayers = 1 };
            fixed (byte* p = r8)
                wgpuQueueWriteTexture(_ctx.Queue, &dest, p, (nuint)r8.Length, &layout, &writeSize);

            return wgpuTextureCreateView(texture, IntPtr.Zero);
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
