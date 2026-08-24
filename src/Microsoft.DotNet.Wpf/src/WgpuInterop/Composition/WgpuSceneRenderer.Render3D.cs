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

        private readonly Dictionary<(WGPUTextureFormat Format, WGPUCullMode Cull, bool DepthWrite), IntPtr> _pipelines3D = new();
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
    emissive : vec4<f32>,     // rgb emissive; w = 1 -> emissive-only (unlit) material
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

// Normals transform by the INVERSE TRANSPOSE of the model matrix, not by the model matrix. The two
// agree under rotation and uniform scale -- which is why using the model matrix looked fine for a
// long time -- but under NON-uniform scale the model matrix tilts a normal toward the stretched
// axis. The fractal-tree sample scales every branch by about (0.25, 1.5, 0.25), so its normals
// leaned along the branch and each branch was shaded dark-to-light ALONG its length instead of
// across it, a lighting direction milcore does not produce.
//
// Derived here rather than uploaded: it is ~30 flops per vertex, against 64 more bytes in a uniform
// block that is read once per DRAW (thousands of times a frame) -- and measurably so, growing the
// block from 736 to 800 bytes cost this backend more than 4x the frame time.
//
// For a column-major mat3 with columns a,b,c, inverse() has ROWS cross(b,c)/det, cross(c,a)/det,
// cross(a,b)/det -- so building a matrix with those as COLUMNS is transpose(inverse(m)) directly.
fn invTranspose3(m : mat3x3<f32>) -> mat3x3<f32> {
    let a = m[0]; let b = m[1]; let c = m[2];
    let r0 = cross(b, c); let r1 = cross(c, a); let r2 = cross(a, b);
    let det = dot(a, r0);
    if (abs(det) < 1e-12) { return m; }      // singular (a zero scale): nothing better to do
    let k = 1.0 / det;
    return mat3x3<f32>(r0 * k, r1 * k, r2 * k);
}

@vertex
fn vs_main(@location(0) pos : vec3<f32>, @location(1) normal : vec3<f32>, @location(2) uv : vec2<f32>) -> VSOut {
    var o : VSOut;
    o.pos = u.mvp * vec4<f32>(pos, 1.0);
    o.normal = invTranspose3(mat3x3<f32>(u.model[0].xyz, u.model[1].xyz, u.model[2].xyz)) * normal;
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
    var alpha = 1.0;                    // solid materials stay opaque (unchanged)
    if (u.params.y > 0.5) {
        let tex = textureSampleLevel(texd, samp, in.uv, 0.0);
        diffuseColor = diffuseColor * tex.rgb;
        alpha = tex.a;                 // a textured material's transparency comes from its texture (e.g. an
                                       // EmissiveMaterial ImageBrush with transparent regions -> see-through)
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
    // Fully-transparent texels are discarded so they write no depth and the geometry behind shows
    // through (matching WPF's transparent 3D faces); the rest blends premultiplied.
    if (alpha < 0.004) { discard; }
    // WPF EmissiveMaterial with no diffuse material (emissive.w) is UNLIT + ADDITIVE: it EMITS light,
    // adding to whatever is behind with ZERO coverage (does NOT occlude -- coverage != 0 turns it into
    // a dead, opaque alpha-over web). The light it adds is the brush colour PREMULTIPLIED by its
    // coverage: emissive.rgb * alpha -- exactly WPF's additive emissive. Front+back faces both add, so
    // overlapping lattice layers accumulate brighter over the (correctly gamma-composited) fire.
    if (u.emissive.w > 0.5) { return vec4<f32>(emissive * alpha, 0.0); }
    return vec4<f32>(rgb * alpha, alpha);   // premultiplied
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
            // An empty viewport means "fill the target" -- in ABSOLUTE device coords, because the
            // target of a nested layer starts at (_devOX,_devOY) rather than at the origin.
            else { dx0 = _devOX; dy0 = _devOY; dx1 = _devOX + width; dy1 = _devOY + height; }
            float dw = MathF.Max(1f, dx1 - dx0), dh = MathF.Max(1f, dy1 - dy0);

            // Keep the 3D inside its rect: scissor-intersect the clip with the device rect.
            clip = Intersect(clip, new Scissor((int)MathF.Floor(dx0), (int)MathF.Floor(dy0),
                (int)MathF.Ceiling(dw), (int)MathF.Ceiling(dh)));
            if (clip.IsEmpty) return;

            // The 3D pass is sized to the VIEWPORT's device rect, not to the window.
            //
            // A Viewport3D is almost always a panel inside a larger window, and its three attachments
            // -- the resolve target, the multisampled colour and the depth buffer -- used to be
            // allocated at full target size whatever that panel's size was. On a maximized window that
            // is 3840x1077 of MSAA colour plus depth, cleared and rasterized every frame, to draw a
            // cube occupying a fraction of it; and because a Viewport3D forced its enclosing
            // clip/mask/effect layers full-target too (HasFullTargetContent), every one of those baked
            // a window-sized texture as well, re-baked on each frame the 3D animated.
            //
            // Rounded OUT to whole pixels so the rect is covered completely, and capped at the target
            // size so a degenerate or enormous viewport can never ask for more than the old behaviour.
            int rx3 = (int)MathF.Floor(dx0), ry3 = (int)MathF.Floor(dy0);
            int rw3 = Math.Min(width, Math.Max(1, (int)MathF.Ceiling(dx1) - rx3));
            int rh3 = Math.Min(height, Math.Max(1, (int)MathF.Ceiling(dy1) - ry3));

            var (_, colorView) = CreateLayerTexture(rw3, rh3);          // single-sample resolve target
            IntPtr msaaColorView = CreateMsaaColorTexture(rw3, rh3, ReadbackFormat, Msaa3D);
            IntPtr depthView = CreateDepthTexture(rw3, rh3, Msaa3D);

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
            // Map full NDC [-1,1] onto the device rect AS IT SITS INSIDE THE REGION TEXTURE (y flipped:
            // device y grows down). Since the region is that same rect rounded out, this is within a
            // pixel of the identity -- but it must be computed rather than assumed, both to absorb that
            // rounding and because the rect's absolute position has to cancel against the region origin.
            float sx = (dx1 - dx0) / rw3, sy = (dy1 - dy0) / rh3;
            float tx = ((dx0 - rx3) + (dx1 - rx3)) / rw3 - 1f;
            float ty = 1f - ((dy0 - ry3) + (dy1 - ry3)) / rh3;
            var viewportMatrix = new Matrix4x4(
                sx, 0, 0, 0,
                0, sy, 0, 0,
                0, 0, 1, 0,
                tx, ty, 0, 1);
            Matrix4x4 viewProj = view * proj * viewportMatrix;

            var models = new List<Draw3D>(viewport.Models.Count);
            var slots = new List<DrawSlot>(viewport.Models.Count);
            int baseSlot = _slot3DCursor;
            foreach (Model3D model in viewport.Models)
            {
                MeshGeometry3D mesh = model.Mesh;
                if (mesh.Indices.Length == 0) continue;
                if (!model.HasFrontMaterial && !model.HasBackMaterial) continue;

                (IntPtr vbuf, IntPtr ibuf) = GetMeshBuffers(mesh);

                // A model is semi-transparent if either side's material has a sub-1 colour alpha or a
                // texture that contains any translucent texel. Such models render with depth-write off,
                // after opaque geometry, far side first -- so a sphere's front and back hemispheres blend
                // (see-through) rather than the near hemisphere occluding the far one (opaque with holes).
                bool transparent = (model.HasFrontMaterial && MaterialIsTransparent(model.Material))
                                 || (model.HasBackMaterial && MaterialIsTransparent(model.BackMaterial));

                // WPF sidedness: Material paints front faces, BackMaterial paints back faces (with
                // normals flipped so lighting is correct); a missing side is culled away entirely.
                if (transparent)
                {
                    // Back-to-front for a convex mesh: far (back) side before near (front) side.
                    if (model.HasBackMaterial) AddDraw(model.BackMaterial, backFace: true, transparent);
                    if (model.HasFrontMaterial) AddDraw(model.Material, backFace: false, transparent);
                }
                else
                {
                    if (model.HasFrontMaterial) AddDraw(model.Material, backFace: false, transparent);
                    if (model.HasBackMaterial) AddDraw(model.BackMaterial, backFace: true, transparent);
                }

                void AddDraw(Material3D mat, bool backFace, bool isTransparent)
                {
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

                    // The uniform block goes into one shared buffer at this draw's slot; the bind group
                    // that names that slot is built once and reused across frames (Resolve3DSlots).
                    slots.Add(new DrawSlot(texView, backFace, !isTransparent));
                    WriteModelUniform(baseSlot + slots.Count - 1,
                        model.Transform * viewProj, model.Transform, cam.Position,
                        viewport.Lights, viewport.AmbientColor, mat, backFace);

                    models.Add(new Draw3D(vbuf, ibuf, IntPtr.Zero, (uint)mesh.Indices.Length, backFace, isTransparent));
                }
            }

            Resolve3DSlots(baseSlot, slots, models);
            _slot3DCursor = baseSlot + slots.Count;

            plan.Add(new LayerPass(colorView, ReadbackFormat, models, depthView, msaaColorView));
            // Composite the region texture at its device position rather than as a full-screen quad.
            EmitLayerQuad(outData, outFormat, FillKind.Layer, colorView, 1f, 1f, 1f,
                (float)Math.Clamp(opacity, 0.0, 1.0), rx3, ry3, rw3, rh3, clip, width, height);
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

            // Two ordered passes over the draw list: opaque first (writes depth so it occludes correctly),
            // then transparent with depth-write OFF (tests against opaque depth but doesn't self-occlude, so
            // a model's far and near sides both survive and blend). The list already has transparent models
            // ordered far-side-before-near-side.
            void DrawSubset(bool transparent)
            {
                foreach (Draw3D m in models)
                {
                    if (m.Transparent != transparent) continue;
                    // Front-material draws cull back faces; back-material draws cull front faces
                    // (WPF sidedness). Same shader/layout, so bind groups are interchangeable.
                    wgpuRenderPassEncoderSetPipeline(pass, Get3DPipeline(format,
                        m.BackFace ? WGPUCullMode.Front : WGPUCullMode.Back, depthWrite: !transparent));
                    wgpuRenderPassEncoderSetBindGroup(pass, 0, m.BindGroup, 0, null);
                    wgpuRenderPassEncoderSetVertexBuffer(pass, 0, m.Vbuf, 0, WholeSize);
                    wgpuRenderPassEncoderSetIndexBuffer(pass, m.Ibuf, WGPUIndexFormat.Uint32, 0, WholeSize);
                    wgpuRenderPassEncoderDrawIndexed(pass, m.IndexCount, 1, 0, 0, 0);
                }
            }
            DrawSubset(false);
            DrawSubset(true);
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

        // ---- per-draw uniforms + bind groups ------------------------------------------------
        //
        // Everything below exists because a 3D scene is made of MANY draws of FEW distinct things,
        // and the cost that matters is per-DRAW resource creation, not per-draw GPU work. Creating a
        // uniform buffer and a bind group for every draw, every frame, put the fractal-tree sample
        // (~4000 branches) at 1.3fps against milcore's 44 on the same machine and the same scene.
        //
        // Instead: all of a viewport's uniform blocks go into ONE buffer (one upload per frame) and
        // each draw's bind group -- which names its slot's byte range plus its texture -- is built
        // once and reused for as long as the scene keeps that slot pointing at the same texture and
        // pipeline variant. A frame of an unchanged scene then creates nothing at all.

        /// <summary>What a draw's bind group has to describe; a slot is rebuilt only when this changes.</summary>
        private readonly struct DrawSlot
        {
            public readonly IntPtr TexView;
            public readonly bool BackFace, DepthWrite;
            public DrawSlot(IntPtr texView, bool backFace, bool depthWrite)
            { TexView = texView; BackFace = backFace; DepthWrite = depthWrite; }
            public bool Matches(in DrawSlot o) => TexView == o.TexView && BackFace == o.BackFace && DepthWrite == o.DepthWrite;
        }

        // The uniform block is 184 floats; bind-group entry offsets must be a multiple of the
        // device's minUniformBufferOffsetAlignment, which is 256 everywhere this runs.
        private const int Uniform3DFloats = 32 + 24 + MaxLights3D * 16;   // 2 mat4 + 6 vec4 + lights
        private const int Uniform3DStride = ((Uniform3DFloats * 4) + 255) & ~255;

        private byte[] _uniform3DStaging = new byte[Uniform3DStride * 64];
        private IntPtr _uniform3DBuffer;
        private int _uniform3DCapacity;                       // slots the buffer can hold
        private int _slot3DCursor;                            // next free slot THIS frame

        // Slot resources replaced during a frame, released at the START of the next one.
        //
        // NOT DeferReleaseBuffer/DeferReleaseBindGroup: those are flushed by FlushFrameReleases,
        // which a NESTED render (RenderToRgba for an offscreen readback, run in the middle of a
        // frame) calls in its finally -- so a nested render that grew the slot buffer freed the
        // bind groups the outer, still-unexecuted pass was holding, and wgpu aborted the process
        // with "invalid bind group". By the next BeginFrame the frame that used them has been
        // submitted, and wgpu keeps a released resource alive until its in-flight work completes.
        private readonly List<IntPtr> _retiredBindGroups3D = new();
        private readonly List<IntPtr> _retiredBuffers3D = new();
        private readonly List<DrawSlot> _slotDesc = new();    // what each cached bind group was built for
        private readonly List<IntPtr> _slotBindGroups = new();

        /// <summary>Reset the frame's slot allocation and free last frame's retired slots.</summary>
        private void BeginFrame3D()
        {
            _slot3DCursor = 0;
            if (_retiredBindGroups3D.Count > 0)
            {
                foreach (IntPtr bg in _retiredBindGroups3D) wgpuBindGroupRelease(bg);
                _retiredBindGroups3D.Clear();
            }
            if (_retiredBuffers3D.Count > 0)
            {
                foreach (IntPtr b in _retiredBuffers3D) wgpuBufferRelease(b);
                _retiredBuffers3D.Clear();
            }
        }

        /// <summary>
        /// Uploads this viewport's uniform blocks in one write and gives every draw its bind group,
        /// creating only the ones whose slot changed since last frame.
        /// </summary>
        /// <param name="baseSlot">
        /// Where this viewport's slots start. Slots are numbered across the WHOLE frame, not per
        /// viewport: a second Viewport3D reusing slot 0 would overwrite the first one's uniforms
        /// before either pass has executed.
        /// </param>
        private void Resolve3DSlots(int baseSlot, List<DrawSlot> slots, List<Draw3D> models)
        {
            if (slots.Count == 0) return;
            int end = baseSlot + slots.Count;

            // Grow the shared buffer in steps, and only ever grow: a reallocation invalidates every
            // bind group that names it, so doing it per frame would defeat the whole cache. The old
            // buffer and bind groups are released at END of frame -- an earlier viewport's pass is
            // still holding them, and its uniforms are still correct in the old buffer.
            if (_uniform3DBuffer == IntPtr.Zero || end > _uniform3DCapacity)
            {
                int capacity = Math.Max(64, _uniform3DCapacity);
                while (capacity < end) capacity *= 2;
                if (_uniform3DBuffer != IntPtr.Zero) _retiredBuffers3D.Add(_uniform3DBuffer);
                _uniform3DBuffer = _ctx.CreateBuffer((ulong)((long)capacity * Uniform3DStride),
                    WGPUBufferUsage.Uniform | WGPUBufferUsage.CopyDst);
                _uniform3DCapacity = capacity;
                InvalidateSlotBindGroups();
            }

            _ctx.WriteBuffer(_uniform3DBuffer, (ulong)((long)baseSlot * Uniform3DStride),
                _uniform3DStaging.AsSpan(baseSlot * Uniform3DStride, slots.Count * Uniform3DStride));

            for (int i = 0; i < slots.Count; i++)
            {
                int slot = baseSlot + i;
                DrawSlot want = slots[i];
                if (slot < _slotBindGroups.Count)
                {
                    if (_slotDesc[slot].Matches(want))
                    {
                        models[i] = models[i].WithBindGroup(_slotBindGroups[slot]);
                        continue;
                    }
                    if (_slotBindGroups[slot] != IntPtr.Zero) _retiredBindGroups3D.Add(_slotBindGroups[slot]);
                }

                IntPtr bg = Create3DBindGroup(_uniform3DBuffer, (ulong)((long)slot * Uniform3DStride),
                    (ulong)(Uniform3DFloats * 4), want.TexView, want.BackFace, want.DepthWrite);

                if (slot < _slotBindGroups.Count) { _slotBindGroups[slot] = bg; _slotDesc[slot] = want; }
                else
                {
                    // Slots are assigned in order, so this only ever appends at the end.
                    while (_slotBindGroups.Count < slot) { _slotBindGroups.Add(IntPtr.Zero); _slotDesc.Add(default); }
                    _slotBindGroups.Add(bg); _slotDesc.Add(want);
                }
                models[i] = models[i].WithBindGroup(bg);
            }
        }

        /// <summary>Drops every cached slot bind group (retired: a pass this frame may still hold one).</summary>
        private void InvalidateSlotBindGroups()
        {
            foreach (IntPtr bg in _slotBindGroups) if (bg != IntPtr.Zero) _retiredBindGroups3D.Add(bg);
            _slotBindGroups.Clear();
            _slotDesc.Clear();
        }

        // A mesh's vertex/index buffers, uploaded once and reused for as long as the scene keeps
        // referencing that mesh.
        //
        // These used to be rebuilt and re-uploaded PER MODEL, PER FRAME -- which is catastrophic
        // exactly where 3D gets interesting, because a scene with many instances of one shape shares
        // a single MeshGeometry3D. The fractal-tree sample draws ~4000 branches off ONE cylinder:
        // that was ~8000 buffer creations and ~4000 redundant vertex re-packs every frame, and it ran
        // at 1.3fps against milcore's 44.
        //
        // Keyed on the mesh INSTANCE, which is safe because MeshGeometry3D is immutable and the
        // decoder replaces the instance (rather than mutating it) when a mesh resource is updated.
        private sealed class GpuMesh
        {
            public IntPtr Vbuf, Ibuf;
            public int LastUsedFrame;
        }

        private readonly Dictionary<MeshGeometry3D, GpuMesh> _meshCache =
            new(System.Collections.Generic.ReferenceEqualityComparer.Instance as IEqualityComparer<MeshGeometry3D>);

        // Frames a mesh may go unused before its buffers are released. Generous: re-uploading is far
        // more expensive than holding a few hundred KB, and scenes routinely hide/show geometry.
        private const int MeshCacheIdleFrames = 240;

        private (IntPtr Vbuf, IntPtr Ibuf) GetMeshBuffers(MeshGeometry3D mesh)
        {
            if (!_meshCache.TryGetValue(mesh, out GpuMesh? gm))
            {
                byte[] vbytes = BuildMeshVertices(mesh);
                byte[] ibytes = new byte[mesh.Indices.Length * sizeof(uint)];
                Buffer.BlockCopy(mesh.Indices, 0, ibytes, 0, ibytes.Length);
                gm = new GpuMesh
                {
                    Vbuf = _ctx.CreateBufferMapped(vbytes, WGPUBufferUsage.Vertex),
                    Ibuf = _ctx.CreateBufferMapped(ibytes, WGPUBufferUsage.Index),
                };
                _meshCache[mesh] = gm;
            }
            gm.LastUsedFrame = _frameId;
            return (gm.Vbuf, gm.Ibuf);
        }

        /// <summary>Releases mesh buffers untouched for a while. Called once per frame.</summary>
        private void EvictStaleMeshes()
        {
            if (_meshCache.Count == 0) return;
            List<MeshGeometry3D>? dead = null;
            foreach (KeyValuePair<MeshGeometry3D, GpuMesh> kv in _meshCache)
                if (_frameId - kv.Value.LastUsedFrame > MeshCacheIdleFrames)
                    (dead ??= new List<MeshGeometry3D>()).Add(kv.Key);
            if (dead is null) return;
            foreach (MeshGeometry3D m in dead)
            {
                GpuMesh gm = _meshCache[m];
                wgpuBufferRelease(gm.Vbuf);
                wgpuBufferRelease(gm.Ibuf);
                _meshCache.Remove(m);
            }
        }

        // wgpuRenderPipelineGetBindGroupLayout hands back a NEW reference per call, so asking for it
        // once per model per frame both cost a call and leaked one layout per draw.
        private readonly Dictionary<(WGPUCullMode Cull, bool DepthWrite), IntPtr> _bindGroupLayouts3D = new();

        private IntPtr Get3DBindGroupLayout(WGPUCullMode cull, bool depthWrite)
        {
            if (_bindGroupLayouts3D.TryGetValue((cull, depthWrite), out IntPtr cached)) return cached;
            IntPtr layout = wgpuRenderPipelineGetBindGroupLayout(Get3DPipeline(ReadbackFormat, cull, depthWrite), 0);
            _bindGroupLayouts3D[(cull, depthWrite)] = layout;
            return layout;
        }

        private IntPtr Create3DBindGroup(IntPtr uniformBuffer, ulong offset, ulong size, IntPtr textureView, bool backFace, bool depthWrite)
        {
            // Auto pipeline layouts are only compatible with the pipeline they came from, so the bind
            // group must be created against the SAME cull AND depth-write variant it will be drawn with.
            IntPtr layout = Get3DBindGroupLayout(backFace ? WGPUCullMode.Front : WGPUCullMode.Back, depthWrite);
            var entries = stackalloc WGPUBindGroupEntry[3];
            entries[0] = new WGPUBindGroupEntry { binding = 0, buffer = uniformBuffer, offset = offset, size = size };
            entries[1] = new WGPUBindGroupEntry { binding = 1, textureView = textureView };
            entries[2] = new WGPUBindGroupEntry { binding = 2, sampler = LinearSampler() };
            var desc = new WGPUBindGroupDescriptor { layout = layout, entryCount = 3, entries = entries };
            return wgpuDeviceCreateBindGroup(_ctx.Device, &desc);
        }

        // Cache of "does this diffuse-texture byte[] contain any translucent texel" (keyed by the array
        // instance, which is stable per decoded image), so the per-frame transparency test is O(1).
        private readonly System.Runtime.CompilerServices.ConditionalWeakTable<byte[], object> _texHasAlpha = new();

        private bool MaterialIsTransparent(Material3D mat)
        {
            // An emissive-only material is ADDITIVE (it adds light, does not occlude), so it's drawn after
            // opaque geometry with depth-write off (front+back both survive). That is the ONLY lobe-based
            // reason to treat a material as non-occluding: Emissive/Specular are additive contributions
            // whose colour ALPHA is NOT surface coverage. A plain DiffuseMaterial leaves them at alpha 0,
            // so testing Emissive.A/Specular.A here wrongly flagged every diffuse material transparent ->
            // depth-write was disabled -> far geometry painted over near (broke depth occlusion, e.g.
            // PhotoFlipper's flip and the depth test). Transparency comes from the DIFFUSE alpha / texture.
            if (mat.EmissiveOnly) return true;
            if (mat.Diffuse.A < 0.996f) return true;
            // A static diffuse texture with any translucent texel (e.g. a PNG with an alpha channel).
            if (mat.Texture is { } px && mat.TexWidth > 0 && mat.TexHeight > 0)
            {
                if (_texHasAlpha.TryGetValue(px, out object? cached)) return (bool)cached;
                bool hasAlpha = false;
                for (int i = 3; i < px.Length; i += 4) { if (px[i] < 250) { hasAlpha = true; break; } }
                _texHasAlpha.Add(px, hasAlpha);
                return hasAlpha;
            }
            // Live 2D-in-3D content is treated as opaque (UI screens); no cheap alpha test available.
            return false;
        }

        private IntPtr Get3DPipeline(WGPUTextureFormat format, WGPUCullMode cull = WGPUCullMode.Back, bool depthWrite = true)
        {
            if (_pipelines3D.TryGetValue((format, cull, depthWrite), out IntPtr cached)) return cached;
            IntPtr pipeline = Create3DPipeline(format, cull, depthWrite);
            _pipelines3D[(format, cull, depthWrite)] = pipeline;
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

        private IntPtr Create3DPipeline(WGPUTextureFormat targetFormat, WGPUCullMode cull, bool depthWrite = true)
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
                    depthWriteEnabled = depthWrite ? WGPUOptionalBool.True : WGPUOptionalBool.False,
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
        //
        // Written straight into the shared staging array at the slot's stride offset: with thousands
        // of draws a per-model float[]+byte[] pair was megabytes of garbage every frame.
        private void WriteModelUniform(int slot, Matrix4x4 mvp, Matrix4x4 model, Vector3 camPos,
            IReadOnlyList<Light3D> lights, RgbaColor ambient, Material3D mat, bool flipNormals)
        {
            int need = (slot + 1) * Uniform3DStride;
            if (_uniform3DStaging.Length < need)
            {
                int size = _uniform3DStaging.Length;
                while (size < need) size *= 2;
                Array.Resize(ref _uniform3DStaging, size);
            }

            Span<float> f = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(
                _uniform3DStaging.AsSpan(slot * Uniform3DStride, Uniform3DFloats * 4));
            int o = 0;
            WriteMatrix(f, ref o, mvp);
            WriteMatrix(f, ref o, model);   // the shader derives the normal matrix from this
            f[o++] = camPos.X; f[o++] = camPos.Y; f[o++] = camPos.Z; f[o++] = 0f;
            f[o++] = ambient.R; f[o++] = ambient.G; f[o++] = ambient.B; f[o++] = 1f;
            f[o++] = mat.Diffuse.R; f[o++] = mat.Diffuse.G; f[o++] = mat.Diffuse.B; f[o++] = mat.Diffuse.A;
            f[o++] = mat.Specular.R; f[o++] = mat.Specular.G; f[o++] = mat.Specular.B; f[o++] = mat.SpecularPower;
            f[o++] = mat.Emissive.R; f[o++] = mat.Emissive.G; f[o++] = mat.Emissive.B; f[o++] = mat.EmissiveOnly ? 1f : 0f;
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
            // A slot re-used by a draw with FEWER lights must not keep the old ones: the light count
            // gates the shader loop, but zeroing the tail keeps a slot's bytes a function of its draw.
            for (; o < Uniform3DFloats; o++) f[o] = 0f;
        }

        private static void WriteMatrix(Span<float> dst, ref int o, Matrix4x4 m)
        {
            dst[o++] = m.M11; dst[o++] = m.M12; dst[o++] = m.M13; dst[o++] = m.M14;
            dst[o++] = m.M21; dst[o++] = m.M22; dst[o++] = m.M23; dst[o++] = m.M24;
            dst[o++] = m.M31; dst[o++] = m.M32; dst[o++] = m.M33; dst[o++] = m.M34;
            dst[o++] = m.M41; dst[o++] = m.M42; dst[o++] = m.M43; dst[o++] = m.M44;
        }

        private void ReleaseResources3D()
        {
            foreach (GpuMesh gm in _meshCache.Values) { wgpuBufferRelease(gm.Vbuf); wgpuBufferRelease(gm.Ibuf); }
            _meshCache.Clear();
            InvalidateSlotBindGroups();
            foreach (IntPtr bg in _retiredBindGroups3D) wgpuBindGroupRelease(bg);
            _retiredBindGroups3D.Clear();
            foreach (IntPtr b in _retiredBuffers3D) wgpuBufferRelease(b);
            _retiredBuffers3D.Clear();
            if (_uniform3DBuffer != IntPtr.Zero) { wgpuBufferRelease(_uniform3DBuffer); _uniform3DBuffer = IntPtr.Zero; _uniform3DCapacity = 0; }
            foreach (IntPtr layout in _bindGroupLayouts3D.Values) wgpuBindGroupLayoutRelease(layout);
            _bindGroupLayouts3D.Clear();
            foreach (IntPtr pipeline in _pipelines3D.Values) wgpuRenderPipelineRelease(pipeline);
            _pipelines3D.Clear();
            if (_shader3D != IntPtr.Zero) { wgpuShaderModuleRelease(_shader3D); _shader3D = IntPtr.Zero; }
            if (_white3DView != IntPtr.Zero) { wgpuTextureViewRelease(_white3DView); wgpuTextureRelease(_white3DTex); _white3DView = _white3DTex = IntPtr.Zero; }
        }
    }
}
