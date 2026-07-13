// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// 3D render path (WPF Viewport3D). A viewport's meshes are rasterized with a
// perspective camera, directional + ambient diffuse lighting and a depth buffer
// into an offscreen RGBA texture, which is then composited into the 2D scene as
// a layer. This replaces milcore's Direct3D 3D pipeline with WebGPU.
//
// Matrices are computed with System.Numerics (row-vector convention). A
// row-major matrix uploaded to a column-major WGSL mat4x4 is transposed, so
// `mvp * vec4(pos,1)` in WGSL equals the row-vector transform pos * mvp.
//

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using static Microsoft.Wpf.Interop.WebGpu.Wgpu;

namespace Microsoft.Wpf.Interop.WebGpu.Composition
{
    internal sealed unsafe partial class WgpuSceneRenderer
    {
        private const WGPUTextureFormat DepthFormat = WGPUTextureFormat.Depth24Plus;
        private const ulong WholeSize = ulong.MaxValue;
        // 4x MSAA for the 3D pass: meshes are tessellated triangles (no analytic coverage like 2D
        // shapes/text), so their silhouette edges alias without multisampling.
        private const uint Msaa3D = 4;

        private readonly Dictionary<(WGPUTextureFormat Format, WGPUCullMode Cull), IntPtr> _pipelines3D = new();
        private IntPtr _shader3D;

        private const int MaxLights3D = 8;

        private const string Shader3DWgsl = @"
struct Light {
    colorRange : vec4<f32>,   // rgb = colour, w = range (0 = infinite)
    position   : vec4<f32>,   // xyz = world position, w = kind (1 dir, 2 point, 3 spot)
    direction  : vec4<f32>,   // xyz = travel direction (normalized), w = outer-cone cos
    atten      : vec4<f32>,   // x=const, y=linear, z=quadratic, w = inner-cone cos
};
struct U {
    mvp      : mat4x4<f32>,
    model    : mat4x4<f32>,
    camPos   : vec4<f32>,
    ambient  : vec4<f32>,     // rgb ambient light
    diffuse  : vec4<f32>,     // material diffuse rgba
    specular : vec4<f32>,     // rgb specular, w = specular power (0 = none)
    emissive : vec4<f32>,     // rgb emissive
    params   : vec4<f32>,     // x = light count, y = hasTexture, z = emissive-textured, w = flip normals (back faces)
    lights   : array<Light, 8>,
};
@group(0) @binding(0) var<uniform> u : U;
@group(0) @binding(1) var texd : texture_2d<f32>;
@group(0) @binding(2) var samp : sampler;

struct VSOut {
    @builtin(position) pos : vec4<f32>,
    @location(0) normal   : vec3<f32>,
    @location(1) worldPos : vec3<f32>,
    @location(2) uv       : vec2<f32>,
};

@vertex
fn vs_main(@location(0) pos : vec3<f32>, @location(1) normal : vec3<f32>, @location(2) uv : vec2<f32>) -> VSOut {
    var o : VSOut;
    o.pos = u.mvp * vec4<f32>(pos, 1.0);
    o.normal = (u.model * vec4<f32>(normal, 0.0)).xyz;
    o.worldPos = (u.model * vec4<f32>(pos, 1.0)).xyz;
    o.uv = uv;
    return o;
}

@fragment
fn fs_main(in : VSOut) -> @location(0) vec4<f32> {
    // Back-material draws (params.w) light the back side: flip the interpolated normal.
    let n = normalize(in.normal) * select(1.0, -1.0, u.params.w > 0.5);
    let viewDir = normalize(u.camPos.xyz - in.worldPos);
    var diffuseColor = u.diffuse.rgb;
    if (u.params.y > 0.5) {
        diffuseColor = diffuseColor * textureSampleLevel(texd, samp, in.uv, 0.0).rgb;
    }
    // Emissive is unlit; ambient modulates the diffuse albedo. An emissive-textured surface
    // (params.z) shows its diffuse texture at full brightness -- a live 2D UI reads like a screen.
    let emissive = select(u.emissive.rgb, diffuseColor * u.emissive.rgb, u.params.z > 0.5);
    var rgb = emissive + u.ambient.rgb * diffuseColor;
    let count = u32(u.params.x);
    for (var i = 0u; i < count; i = i + 1u) {
        let L = u.lights[i];
        let kind = u32(L.position.w);
        var lightDir : vec3<f32>;   // surface -> light
        var atten = 1.0;
        if (kind == 1u) {
            lightDir = normalize(-L.direction.xyz);
        } else {
            let toLight = L.position.xyz - in.worldPos;
            let dist = length(toLight);
            lightDir = toLight / max(dist, 1e-4);
            atten = 1.0 / max(L.atten.x + L.atten.y * dist + L.atten.z * dist * dist, 1e-4);
            if (L.colorRange.w > 0.0 && dist > L.colorRange.w) { atten = 0.0; }
            if (kind == 3u) {   // spot cone falloff (outer-cone cos in direction.w, inner in atten.w)
                let cosAngle = dot(normalize(L.direction.xyz), -lightDir);
                atten = atten * clamp((cosAngle - L.direction.w) / max(L.atten.w - L.direction.w, 1e-4), 0.0, 1.0);
            }
        }
        let ndotl = max(dot(n, lightDir), 0.0);
        rgb = rgb + diffuseColor * L.colorRange.rgb * (ndotl * atten);
        if (u.specular.w > 0.0 && ndotl > 0.0) {
            let halfV = normalize(lightDir + viewDir);
            let spec = pow(max(dot(n, halfV), 0.0), u.specular.w);
            rgb = rgb + u.specular.rgb * L.colorRange.rgb * (spec * atten);
        }
    }
    return vec4<f32>(rgb, 1.0);   // opaque (premultiplied, alpha 1)
}
";

        // Builds a 3D pass for the viewport's models and composites its offscreen
        // colour into the 2D draw data as a layer at the current opacity.
        private void Emit3DViewport(Viewport3DDraw viewport, Matrix3x2 world, double opacity, Scissor clip,
            DrawData outData, List<LayerPass> plan, int width, int height, WGPUTextureFormat outFormat)
        {
            if (clip.IsEmpty) return;

            // Project the 3D scene into the viewport's on-screen rect (the card), not the whole
            // window. The device rect = the local viewport rect transformed by the world matrix;
            // mapping NDC -> that sub-rect is affine in clip space, so it folds into the matrix
            // (no per-pixel viewport needed). An empty viewport means "fill the target" (the test).
            Rect vp = viewport.Viewport;
            float dx0, dy0, dx1, dy1;
            if (vp.Width > 0 && vp.Height > 0)
            {
                Vector2 p0 = Vector2.Transform(new Vector2(vp.X, vp.Y), world);
                Vector2 p1 = Vector2.Transform(new Vector2(vp.X + vp.Width, vp.Y + vp.Height), world);
                dx0 = MathF.Min(p0.X, p1.X); dy0 = MathF.Min(p0.Y, p1.Y);
                dx1 = MathF.Max(p0.X, p1.X); dy1 = MathF.Max(p0.Y, p1.Y);
            }
            else { dx0 = 0; dy0 = 0; dx1 = width; dy1 = height; }
            float dw = MathF.Max(1f, dx1 - dx0), dh = MathF.Max(1f, dy1 - dy0);

            // Keep the 3D inside its rect: scissor-intersect the clip with the device rect.
            clip = Intersect(clip, new Scissor((int)MathF.Floor(dx0), (int)MathF.Floor(dy0),
                (int)MathF.Ceiling(dw), (int)MathF.Ceiling(dh)));
            if (clip.IsEmpty) return;

            var (_, colorView) = CreateLayerTexture(width, height);     // single-sample resolve target
            IntPtr msaaColorView = CreateMsaaColorTexture(width, height, ReadbackFormat, Msaa3D);
            IntPtr depthView = CreateDepthTexture(width, height, Msaa3D);

            float aspect = dw / dh;
            Camera3D cam = viewport.Camera;
            Matrix4x4 view, proj;
            if (cam.HasMatrix)
            {
                // MatrixCamera: use the app-supplied matrices verbatim (WPF applies no
                // viewport-aspect correction for MatrixCamera either).
                view = cam.ViewMatrix;
                proj = cam.ProjMatrix;
            }
            else
            {
                view = Matrix4x4.CreateLookAt(cam.Position, cam.Position + cam.LookDirection, cam.UpDirection);
                // WPF's PerspectiveCamera.FieldOfView is HORIZONTAL (PerspectiveCamera.GetProjectionMatrix:
                // w = 1/tan(fov/2), h = aspectRatio/tan(fov/2)), but CreatePerspectiveFieldOfView takes a
                // VERTICAL fov. Passing the horizontal angle directly shrinks the projection by ~1/aspect,
                // so objects recede and the camera looks farther than WPF. Convert horizontal -> vertical.
                if (cam.Orthographic)
                {
                    float ow = cam.Width > 0f ? cam.Width : 2f;
                    proj = Matrix4x4.CreateOrthographic(ow, ow / aspect, cam.NearPlane, cam.FarPlane);
                }
                else
                {
                    float fovH = cam.FieldOfView * (MathF.PI / 180f);
                    float fovY = 2f * MathF.Atan(MathF.Tan(fovH / 2f) / aspect);
                    proj = Matrix4x4.CreatePerspectiveFieldOfView(fovY, aspect, cam.NearPlane, cam.FarPlane);
                }
            }
            // Map full NDC [-1,1] to the device rect's NDC sub-region (y flipped: device y grows down).
            float sx = (dx1 - dx0) / width, sy = (dy1 - dy0) / height;
            float tx = (dx0 + dx1) / width - 1f, ty = 1f - (dy0 + dy1) / height;
            var viewportMatrix = new Matrix4x4(
                sx, 0, 0, 0,
                0, sy, 0, 0,
                0, 0, 1, 0,
                tx, ty, 0, 1);
            Matrix4x4 viewProj = view * proj * viewportMatrix;

            var models = new List<Draw3D>(viewport.Models.Count);
            foreach (Model3D model in viewport.Models)
            {
                MeshGeometry3D mesh = model.Mesh;
                if (mesh.Indices.Length == 0) continue;
                if (!model.HasFrontMaterial && !model.HasBackMaterial) continue;

                byte[] vbytes = BuildMeshVertices(mesh);
                byte[] ibytes = new byte[mesh.Indices.Length * sizeof(uint)];
                Buffer.BlockCopy(mesh.Indices, 0, ibytes, 0, ibytes.Length);

                IntPtr vbuf = _ctx.CreateBuffer((ulong)vbytes.Length, WGPUBufferUsage.Vertex | WGPUBufferUsage.CopyDst);
                IntPtr ibuf = _ctx.CreateBuffer((ulong)ibytes.Length, WGPUBufferUsage.Index | WGPUBufferUsage.CopyDst);
                _ctx.WriteBuffer(vbuf, vbytes);
                _ctx.WriteBuffer(ibuf, ibytes);
                DeferReleaseBuffer(vbuf);
                DeferReleaseBuffer(ibuf);

                // WPF sidedness: Material paints front faces, BackMaterial paints back faces (with
                // normals flipped so lighting is correct); a missing side is culled away entirely.
                if (model.HasFrontMaterial) AddDraw(model.Material, backFace: false);
                if (model.HasBackMaterial) AddDraw(model.BackMaterial, backFace: true);

                void AddDraw(Material3D mat, bool backFace)
                {
                    byte[] uni = BuildModelUniform(model.Transform * viewProj, model.Transform, cam.Position,
                        viewport.Lights, viewport.AmbientColor, mat, backFace);
                    IntPtr ubuf = _ctx.CreateBuffer((ulong)uni.Length, WGPUBufferUsage.Uniform | WGPUBufferUsage.CopyDst);
                    _ctx.WriteBuffer(ubuf, uni);

                    // Diffuse texture. Three sources, in priority order:
                    //  1. LIVE 2D content (VisualBrush) -> render its subtree into a GPU texture THIS frame
                    //     (a plan pass before the 3D pass), no CPU readback -> interactive 2D-in-3D.
                    //  2. A static image -> upload its pixels.
                    //  3. Untextured -> a shared 1x1 white (the shader's hasTexture flag gates sampling).
                    IntPtr texView;
                    if (mat.TextureVisual is { } tvis)
                    {
                        texView = RenderVisualToTexture(tvis, (int)mat.TexVisualBounds.Width, (int)mat.TexVisualBounds.Height, plan);
                        // Diagnostic (WPF_DBG_3DTEX_OVERLAY=1): composite the live-2D texture straight
                        // into the 2D scene over the viewport rect, bypassing the 3D pass -- isolates
                        // texture-content bugs from 3D-sampling bugs.
                        if (Dbg3DTexOverlay)
                            EmitFullScreenQuad(outData, outFormat, FillKind.Layer, texView, 1f, 1f, 1f, 1f, 0f, 0f, clip, width, height);
                    }
                    else if (mat.Texture is { } px && mat.TexWidth > 0 && mat.TexHeight > 0)
                    {
                        (IntPtr tex, IntPtr tv) = CreateImageTexture(px, mat.TexWidth, mat.TexHeight);
                        DeferReleaseTexView(tex, tv);
                        texView = tv;
                    }
                    else
                    {
                        texView = White3DView();
                    }

                    IntPtr bindGroup = Create3DBindGroup(ubuf, (ulong)uni.Length, texView, backFace);
                    DeferReleaseBindGroup(bindGroup);
                    DeferReleaseBuffer(ubuf);

                    models.Add(new Draw3D(vbuf, ibuf, bindGroup, (uint)mesh.Indices.Length, backFace));
                }
            }

            plan.Add(new LayerPass(colorView, ReadbackFormat, models, depthView, msaaColorView));
            EmitFullScreenQuad(outData, outFormat, FillKind.Layer, colorView, 1f, 1f, 1f, (float)Math.Clamp(opacity, 0.0, 1.0), 0f, 0f, clip, width, height);
        }

        private void ExecutePass3D(IntPtr encoder, IntPtr resolveView, IntPtr msaaColorView, IntPtr depthView, WGPUTextureFormat format, List<Draw3D> models)
        {
            // Render into the multisampled colour target and resolve into the single-sample view
            // (which the 2D composite then samples). storeOp=Discard: only the resolve is needed.
            var colorAttachment = new WGPURenderPassColorAttachment
            {
                view = msaaColorView,
                resolveTarget = resolveView,
                depthSlice = WGPU_DEPTH_SLICE_UNDEFINED,
                loadOp = WGPULoadOp.Clear,
                storeOp = WGPUStoreOp.Discard,
                clearValue = new WGPUColor { r = 0, g = 0, b = 0, a = 0 },
            };
            var depthAttachment = new WGPURenderPassDepthStencilAttachment
            {
                view = depthView,
                depthLoadOp = WGPULoadOp.Clear,
                depthStoreOp = WGPUStoreOp.Store,
                depthClearValue = 1f,
                stencilLoadOp = WGPULoadOp.Undefined,
                stencilStoreOp = WGPUStoreOp.Undefined,
            };
            var passDesc = new WGPURenderPassDescriptor
            {
                colorAttachmentCount = 1,
                colorAttachments = &colorAttachment,
                depthStencilAttachment = (IntPtr)(&depthAttachment),
            };
            IntPtr pass = wgpuCommandEncoderBeginRenderPass(encoder, &passDesc);

            foreach (Draw3D m in models)
            {
                // Front-material draws cull back faces; back-material draws cull front faces
                // (WPF sidedness). Same shader/layout, so bind groups are interchangeable.
                wgpuRenderPassEncoderSetPipeline(pass, Get3DPipeline(format, m.BackFace ? WGPUCullMode.Front : WGPUCullMode.Back));
                wgpuRenderPassEncoderSetBindGroup(pass, 0, m.BindGroup, 0, null);
                wgpuRenderPassEncoderSetVertexBuffer(pass, 0, m.Vbuf, 0, WholeSize);
                wgpuRenderPassEncoderSetIndexBuffer(pass, m.Ibuf, WGPUIndexFormat.Uint32, 0, WholeSize);
                wgpuRenderPassEncoderDrawIndexed(pass, m.IndexCount, 1, 0, 0, 0);
            }
            wgpuRenderPassEncoderEnd(pass);
            IntPtr passLocal = pass;
            DeferReleasePass(passLocal);
        }

        private IntPtr CreateDepthTexture(int width, int height, uint sampleCount = 1)
        {
            var texDesc = new WGPUTextureDescriptor
            {
                usage = WGPUTextureUsage.RenderAttachment,
                dimension = WGPUTextureDimension._2D,
                size = new WGPUExtent3D { width = (uint)width, height = (uint)height, depthOrArrayLayers = 1 },
                format = DepthFormat,
                mipLevelCount = 1,
                sampleCount = sampleCount,
            };
            IntPtr tex = wgpuDeviceCreateTexture(_ctx.Device, &texDesc);
            IntPtr view = wgpuTextureCreateView(tex, IntPtr.Zero);
            DeferReleaseTexView(tex, view);
            return view;
        }

        // A multisampled colour render target that resolves into a single-sample view.
        private IntPtr CreateMsaaColorTexture(int width, int height, WGPUTextureFormat format, uint sampleCount)
        {
            var texDesc = new WGPUTextureDescriptor
            {
                usage = WGPUTextureUsage.RenderAttachment,
                dimension = WGPUTextureDimension._2D,
                size = new WGPUExtent3D { width = (uint)width, height = (uint)height, depthOrArrayLayers = 1 },
                format = format,
                mipLevelCount = 1,
                sampleCount = sampleCount,
            };
            IntPtr tex = wgpuDeviceCreateTexture(_ctx.Device, &texDesc);
            IntPtr view = wgpuTextureCreateView(tex, IntPtr.Zero);
            DeferReleaseTexView(tex, view);
            return view;
        }

        // Renders a 2D SceneVisual's subtree into a fresh RGBA GPU texture via a plan pass (which
        // executes before the 3D pass samples it). Entirely on the GPU -- the live 2D content is
        // rendered by the 2D shaders and sampled by the 3D shader, no CPU readback. Returns the view.
        private static readonly bool Dbg3DTex = Environment.GetEnvironmentVariable("WPF_DBG_3DTEX") == "1";
        private static readonly bool Dbg3DTexOverlay = Environment.GetEnvironmentVariable("WPF_DBG_3DTEX_OVERLAY") == "1";
        private static int _dbg3DTexOnce;

        private IntPtr RenderVisualToTexture(SceneVisual v, int w, int h, List<LayerPass> plan)
        {
            w = Math.Clamp(w, 1, 1024);
            h = Math.Clamp(h, 1, 1024);
            (IntPtr _, IntPtr view) = CreateLayerTexture(w, h);   // pooled; returned after the frame
            DrawData d = RentDrawData();
            float sOX = _devOX, sOY = _devOY;
            _devOX = 0; _devOY = 0;
            // CollectVisual (not EmitSubtree) so the wrapper's OWN transform (content-bounds ->
            // texture-size mapping) is applied. Same-encoder write->sample across passes is ordered
            // by WebGPU, so the 3D pass can sample this texture in the same submit (verified on
            // wgpu-native/Metal and Dawn).
            CollectVisual(v, System.Numerics.Matrix3x2.Identity, 1.0, new Scissor(0, 0, w, h), d, plan, w, h, ReadbackFormat);
            _devOX = sOX; _devOY = sOY;
            if (Dbg3DTex && _dbg3DTexOnce++ == 30)
                Console.WriteLine($"3D-TEXRND {w}x{h} draws={d.Draws.Count} planIdx={plan.Count}");
            plan.Add(new LayerPass(view, true, default, d, ReadbackFormat) { OriginX = 0, OriginY = 0, TexW = w, TexH = h });
            return view;
        }

        private IntPtr _white3DTex, _white3DView;

        // Shared 1x1 opaque-white RGBA texture for untextured 3D models (so the shader always has a
        // diffuse texture bound; the hasTexture flag gates whether it's actually sampled).
        private IntPtr White3DView()
        {
            if (_white3DView != IntPtr.Zero) return _white3DView;
            (_white3DTex, _white3DView) = CreateRgbaTexture(new byte[] { 255, 255, 255, 255 }, 1, 1);
            return _white3DView;
        }

        private IntPtr Create3DBindGroup(IntPtr uniformBuffer, ulong size, IntPtr textureView, bool backFace)
        {
            // Auto pipeline layouts are only compatible with the pipeline they came from, so the
            // bind group must be created against the SAME cull variant it will be drawn with.
            IntPtr layout = wgpuRenderPipelineGetBindGroupLayout(
                Get3DPipeline(ReadbackFormat, backFace ? WGPUCullMode.Front : WGPUCullMode.Back), 0);
            var entries = stackalloc WGPUBindGroupEntry[3];
            entries[0] = new WGPUBindGroupEntry { binding = 0, buffer = uniformBuffer, offset = 0, size = size };
            entries[1] = new WGPUBindGroupEntry { binding = 1, textureView = textureView };
            entries[2] = new WGPUBindGroupEntry { binding = 2, sampler = LinearSampler() };
            var desc = new WGPUBindGroupDescriptor { layout = layout, entryCount = 3, entries = entries };
            return wgpuDeviceCreateBindGroup(_ctx.Device, &desc);
        }

        private IntPtr Get3DPipeline(WGPUTextureFormat format, WGPUCullMode cull = WGPUCullMode.Back)
        {
            if (_pipelines3D.TryGetValue((format, cull), out IntPtr cached)) return cached;
            IntPtr pipeline = Create3DPipeline(format, cull);
            _pipelines3D[(format, cull)] = pipeline;
            return pipeline;
        }

        private IntPtr Get3DShaderModule()
        {
            if (_shader3D != IntPtr.Zero) return _shader3D;
            byte[] wgsl = Encoding.UTF8.GetBytes(Shader3DWgsl);
            fixed (byte* p = wgsl)
            {
                var src = new WGPUShaderSourceWGSL
                {
                    chain = new WGPUChainedStruct { next = null, sType = WGPUSType_ShaderSourceWGSL },
                    code = new WGPUStringView { data = p, length = (nuint)wgsl.Length },
                };
                var desc = new WGPUShaderModuleDescriptor { nextInChain = (WGPUChainedStruct*)&src };
                _shader3D = wgpuDeviceCreateShaderModule(_ctx.Device, &desc);
            }
            return _shader3D;
        }

        private IntPtr Create3DPipeline(WGPUTextureFormat targetFormat, WGPUCullMode cull)
        {
            IntPtr shader = Get3DShaderModule();
            byte[] vsEntry = Encoding.UTF8.GetBytes("vs_main");
            byte[] fsEntry = Encoding.UTF8.GetBytes("fs_main");

            fixed (byte* pVs = vsEntry)
            fixed (byte* pFs = fsEntry)
            {
                var attributes = stackalloc WGPUVertexAttribute[3];
                attributes[0] = new WGPUVertexAttribute { format = WGPUVertexFormat.Float32x3, offset = 0, shaderLocation = 0 };
                attributes[1] = new WGPUVertexAttribute { format = WGPUVertexFormat.Float32x3, offset = 3 * sizeof(float), shaderLocation = 1 };
                attributes[2] = new WGPUVertexAttribute { format = WGPUVertexFormat.Float32x2, offset = 6 * sizeof(float), shaderLocation = 2 };
                var bufferLayout = new WGPUVertexBufferLayout
                {
                    stepMode = WGPUVertexStepMode.Vertex,
                    arrayStride = 8 * sizeof(float),
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

                var stencil = new WGPUStencilFaceState
                {
                    compare = WGPUCompareFunction.Always,
                    failOp = WGPUStencilOperation.Keep,
                    depthFailOp = WGPUStencilOperation.Keep,
                    passOp = WGPUStencilOperation.Keep,
                };
                var depthState = new WGPUDepthStencilState
                {
                    format = DepthFormat,
                    depthWriteEnabled = WGPUOptionalBool.True,
                    depthCompare = WGPUCompareFunction.Less,
                    stencilFront = stencil,
                    stencilBack = stencil,
                    stencilReadMask = 0xFFFFFFFF,
                    stencilWriteMask = 0xFFFFFFFF,
                };

                var desc = new WGPURenderPipelineDescriptor
                {
                    layout = IntPtr.Zero,
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
                        cullMode = cull,
                    },
                    depthStencil = (IntPtr)(&depthState),
                    multisample = new WGPUMultisampleState { count = Msaa3D, mask = 0xFFFFFFFF },
                    fragment = &fragment,
                };
                return wgpuDeviceCreateRenderPipeline(_ctx.Device, &desc);
            }
        }

        private static byte[] BuildMeshVertices(MeshGeometry3D mesh)
        {
            int n = mesh.Positions.Length;
            var floats = new float[n * 8];
            for (int i = 0; i < n; i++)
            {
                Vector3 p = mesh.Positions[i];
                Vector3 nrm = i < mesh.Normals.Length ? mesh.Normals[i] : Vector3.UnitZ;
                Vector2 uv = i < mesh.TexCoords.Length ? mesh.TexCoords[i] : Vector2.Zero;
                floats[i * 8 + 0] = p.X; floats[i * 8 + 1] = p.Y; floats[i * 8 + 2] = p.Z;
                floats[i * 8 + 3] = nrm.X; floats[i * 8 + 4] = nrm.Y; floats[i * 8 + 5] = nrm.Z;
                floats[i * 8 + 6] = uv.X; floats[i * 8 + 7] = uv.Y;
            }
            var bytes = new byte[floats.Length * sizeof(float)];
            Buffer.BlockCopy(floats, 0, bytes, 0, bytes.Length);
            return bytes;
        }

        // Uniform layout matches the WGSL struct U: 2 mat4 + 6 vec4 header, then MaxLights3D Light
        // (4 vec4 each). std140 alignment is satisfied (all vec4-aligned).
        private static byte[] BuildModelUniform(Matrix4x4 mvp, Matrix4x4 model, Vector3 camPos,
            IReadOnlyList<Light3D> lights, RgbaColor ambient, Material3D mat, bool flipNormals = false)
        {
            const int headerFloats = 32 + 24;                 // 2 mat4 (32) + 6 vec4 (24)
            var f = new float[headerFloats + MaxLights3D * 16];
            int o = 0;
            WriteMatrix(f, ref o, mvp);
            WriteMatrix(f, ref o, model);
            f[o++] = camPos.X; f[o++] = camPos.Y; f[o++] = camPos.Z; f[o++] = 0f;
            f[o++] = ambient.R; f[o++] = ambient.G; f[o++] = ambient.B; f[o++] = 1f;
            f[o++] = mat.Diffuse.R; f[o++] = mat.Diffuse.G; f[o++] = mat.Diffuse.B; f[o++] = mat.Diffuse.A;
            f[o++] = mat.Specular.R; f[o++] = mat.Specular.G; f[o++] = mat.Specular.B; f[o++] = mat.SpecularPower;
            f[o++] = mat.Emissive.R; f[o++] = mat.Emissive.G; f[o++] = mat.Emissive.B; f[o++] = 1f;
            int count = Math.Min(lights.Count, MaxLights3D);
            f[o++] = count; f[o++] = mat.HasTexture ? 1f : 0f; f[o++] = mat.EmissiveTextured ? 1f : 0f; f[o++] = flipNormals ? 1f : 0f;
            for (int i = 0; i < count; i++)
            {
                Light3D l = lights[i];
                f[o++] = l.Color.R; f[o++] = l.Color.G; f[o++] = l.Color.B; f[o++] = l.Range;
                f[o++] = l.Position.X; f[o++] = l.Position.Y; f[o++] = l.Position.Z; f[o++] = (float)l.Kind;
                Vector3 d = l.Direction.LengthSquared() > 1e-8f ? Vector3.Normalize(l.Direction) : new Vector3(0, 0, -1);
                f[o++] = d.X; f[o++] = d.Y; f[o++] = d.Z; f[o++] = l.OuterConeCos;
                f[o++] = l.ConstantAtten; f[o++] = l.LinearAtten; f[o++] = l.QuadraticAtten; f[o++] = l.InnerConeCos;
            }
            var bytes = new byte[f.Length * sizeof(float)];
            Buffer.BlockCopy(f, 0, bytes, 0, bytes.Length);
            return bytes;
        }

        private static void WriteMatrix(float[] dst, ref int o, Matrix4x4 m)
        {
            dst[o++] = m.M11; dst[o++] = m.M12; dst[o++] = m.M13; dst[o++] = m.M14;
            dst[o++] = m.M21; dst[o++] = m.M22; dst[o++] = m.M23; dst[o++] = m.M24;
            dst[o++] = m.M31; dst[o++] = m.M32; dst[o++] = m.M33; dst[o++] = m.M34;
            dst[o++] = m.M41; dst[o++] = m.M42; dst[o++] = m.M43; dst[o++] = m.M44;
        }

        private void ReleaseResources3D()
        {
            foreach (IntPtr pipeline in _pipelines3D.Values) wgpuRenderPipelineRelease(pipeline);
            _pipelines3D.Clear();
            if (_shader3D != IntPtr.Zero) { wgpuShaderModuleRelease(_shader3D); _shader3D = IntPtr.Zero; }
            if (_white3DView != IntPtr.Zero) { wgpuTextureViewRelease(_white3DView); wgpuTextureRelease(_white3DTex); _white3DView = _white3DTex = IntPtr.Zero; }
        }
    }
}
