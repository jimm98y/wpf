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

        private readonly Dictionary<WGPUTextureFormat, IntPtr> _pipelines3D = new();
        private IntPtr _shader3D;

        private const string Shader3DWgsl = @"
struct U {
    mvp : mat4x4<f32>,
    model : mat4x4<f32>,
    lightDir : vec4<f32>,
    lightColor : vec4<f32>,
    material : vec4<f32>,
    ambient : vec4<f32>,
};
@group(0) @binding(0) var<uniform> u : U;

struct VSOut {
    @builtin(position) pos : vec4<f32>,
    @location(0) normal : vec3<f32>,
};

@vertex
fn vs_main(@location(0) pos : vec3<f32>, @location(1) normal : vec3<f32>) -> VSOut {
    var o : VSOut;
    o.pos = u.mvp * vec4<f32>(pos, 1.0);
    o.normal = (u.model * vec4<f32>(normal, 0.0)).xyz;
    return o;
}

@fragment
fn fs_main(in : VSOut) -> @location(0) vec4<f32> {
    let n = normalize(in.normal);
    let l = normalize(-u.lightDir.xyz);
    let diff = max(dot(n, l), 0.0);
    let rgb = u.material.rgb * (u.ambient.rgb + diff * u.lightColor.rgb);
    return vec4<f32>(rgb, 1.0); // opaque (premultiplied with alpha 1)
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

            var (_, colorView) = CreateLayerTexture(width, height);
            IntPtr depthView = CreateDepthTexture(width, height);

            float aspect = dw / dh;
            Camera3D cam = viewport.Camera;
            Matrix4x4 view = Matrix4x4.CreateLookAt(cam.Position, cam.Position + cam.LookDirection, cam.UpDirection);
            Matrix4x4 proj = Matrix4x4.CreatePerspectiveFieldOfView(cam.FieldOfView * (MathF.PI / 180f), aspect, cam.NearPlane, cam.FarPlane);
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

                byte[] vbytes = BuildMeshVertices(mesh);
                byte[] ibytes = new byte[mesh.Indices.Length * sizeof(uint)];
                Buffer.BlockCopy(mesh.Indices, 0, ibytes, 0, ibytes.Length);

                IntPtr vbuf = _ctx.CreateBuffer((ulong)vbytes.Length, WGPUBufferUsage.Vertex | WGPUBufferUsage.CopyDst);
                IntPtr ibuf = _ctx.CreateBuffer((ulong)ibytes.Length, WGPUBufferUsage.Index | WGPUBufferUsage.CopyDst);
                _ctx.WriteBuffer(vbuf, vbytes);
                _ctx.WriteBuffer(ibuf, ibytes);

                byte[] uni = BuildModelUniform(model.Transform * viewProj, model.Transform, viewport.Light, viewport.AmbientColor, model.DiffuseColor);
                IntPtr ubuf = _ctx.CreateBuffer((ulong)uni.Length, WGPUBufferUsage.Uniform | WGPUBufferUsage.CopyDst);
                _ctx.WriteBuffer(ubuf, uni);

                IntPtr bindGroup = Create3DBindGroup(ubuf, (ulong)uni.Length);
                DeferRelease(() =>
                {
                    wgpuBindGroupRelease(bindGroup);
                    wgpuBufferRelease(ubuf);
                    wgpuBufferRelease(vbuf);
                    wgpuBufferRelease(ibuf);
                });

                models.Add(new Draw3D(vbuf, ibuf, bindGroup, (uint)mesh.Indices.Length));
            }

            plan.Add(new LayerPass(colorView, ReadbackFormat, models, depthView));
            EmitFullScreenQuad(outData, outFormat, FillKind.Layer, colorView, 1f, 1f, 1f, (float)Math.Clamp(opacity, 0.0, 1.0), 0f, 0f, clip, width, height);
        }

        private void ExecutePass3D(IntPtr encoder, IntPtr colorView, IntPtr depthView, WGPUTextureFormat format, List<Draw3D> models)
        {
            var colorAttachment = new WGPURenderPassColorAttachment
            {
                view = colorView,
                depthSlice = WGPU_DEPTH_SLICE_UNDEFINED,
                loadOp = WGPULoadOp.Clear,
                storeOp = WGPUStoreOp.Store,
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

            wgpuRenderPassEncoderSetPipeline(pass, Get3DPipeline(format));
            foreach (Draw3D m in models)
            {
                wgpuRenderPassEncoderSetBindGroup(pass, 0, m.BindGroup, 0, null);
                wgpuRenderPassEncoderSetVertexBuffer(pass, 0, m.Vbuf, 0, WholeSize);
                wgpuRenderPassEncoderSetIndexBuffer(pass, m.Ibuf, WGPUIndexFormat.Uint32, 0, WholeSize);
                wgpuRenderPassEncoderDrawIndexed(pass, m.IndexCount, 1, 0, 0, 0);
            }
            wgpuRenderPassEncoderEnd(pass);
            IntPtr passLocal = pass;
            DeferRelease(() => wgpuRenderPassEncoderRelease(passLocal));
        }

        private IntPtr CreateDepthTexture(int width, int height)
        {
            var texDesc = new WGPUTextureDescriptor
            {
                usage = WGPUTextureUsage.RenderAttachment,
                dimension = WGPUTextureDimension._2D,
                size = new WGPUExtent3D { width = (uint)width, height = (uint)height, depthOrArrayLayers = 1 },
                format = DepthFormat,
                mipLevelCount = 1,
                sampleCount = 1,
            };
            IntPtr tex = wgpuDeviceCreateTexture(_ctx.Device, &texDesc);
            IntPtr view = wgpuTextureCreateView(tex, IntPtr.Zero);
            DeferRelease(() => { wgpuTextureViewRelease(view); wgpuTextureRelease(tex); });
            return view;
        }

        private IntPtr Create3DBindGroup(IntPtr uniformBuffer, ulong size)
        {
            IntPtr layout = wgpuRenderPipelineGetBindGroupLayout(Get3DPipeline(ReadbackFormat), 0);
            var entry = new WGPUBindGroupEntry { binding = 0, buffer = uniformBuffer, offset = 0, size = size };
            var desc = new WGPUBindGroupDescriptor { layout = layout, entryCount = 1, entries = &entry };
            return wgpuDeviceCreateBindGroup(_ctx.Device, &desc);
        }

        private IntPtr Get3DPipeline(WGPUTextureFormat format)
        {
            if (_pipelines3D.TryGetValue(format, out IntPtr cached)) return cached;
            IntPtr pipeline = Create3DPipeline(format);
            _pipelines3D[format] = pipeline;
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

        private IntPtr Create3DPipeline(WGPUTextureFormat targetFormat)
        {
            IntPtr shader = Get3DShaderModule();
            byte[] vsEntry = Encoding.UTF8.GetBytes("vs_main");
            byte[] fsEntry = Encoding.UTF8.GetBytes("fs_main");

            fixed (byte* pVs = vsEntry)
            fixed (byte* pFs = fsEntry)
            {
                var attributes = stackalloc WGPUVertexAttribute[2];
                attributes[0] = new WGPUVertexAttribute { format = WGPUVertexFormat.Float32x3, offset = 0, shaderLocation = 0 };
                attributes[1] = new WGPUVertexAttribute { format = WGPUVertexFormat.Float32x3, offset = 3 * sizeof(float), shaderLocation = 1 };
                var bufferLayout = new WGPUVertexBufferLayout
                {
                    stepMode = WGPUVertexStepMode.Vertex,
                    arrayStride = 6 * sizeof(float),
                    attributeCount = 2,
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
                        cullMode = WGPUCullMode.None,
                    },
                    depthStencil = (IntPtr)(&depthState),
                    multisample = new WGPUMultisampleState { count = 1, mask = 0xFFFFFFFF },
                    fragment = &fragment,
                };
                return wgpuDeviceCreateRenderPipeline(_ctx.Device, &desc);
            }
        }

        private static byte[] BuildMeshVertices(MeshGeometry3D mesh)
        {
            int n = mesh.Positions.Length;
            var floats = new float[n * 6];
            for (int i = 0; i < n; i++)
            {
                Vector3 p = mesh.Positions[i];
                Vector3 nrm = i < mesh.Normals.Length ? mesh.Normals[i] : Vector3.UnitZ;
                floats[i * 6 + 0] = p.X; floats[i * 6 + 1] = p.Y; floats[i * 6 + 2] = p.Z;
                floats[i * 6 + 3] = nrm.X; floats[i * 6 + 4] = nrm.Y; floats[i * 6 + 5] = nrm.Z;
            }
            var bytes = new byte[floats.Length * sizeof(float)];
            Buffer.BlockCopy(floats, 0, bytes, 0, bytes.Length);
            return bytes;
        }

        private static byte[] BuildModelUniform(Matrix4x4 mvp, Matrix4x4 model, DirectionalLight3D light, RgbaColor ambient, RgbaColor material)
        {
            var f = new float[48];
            int o = 0;
            WriteMatrix(f, ref o, mvp);
            WriteMatrix(f, ref o, model);
            f[o++] = light.Direction.X; f[o++] = light.Direction.Y; f[o++] = light.Direction.Z; f[o++] = 0f;
            f[o++] = light.Color.R; f[o++] = light.Color.G; f[o++] = light.Color.B; f[o++] = 1f;
            f[o++] = material.R; f[o++] = material.G; f[o++] = material.B; f[o++] = material.A;
            f[o++] = ambient.R; f[o++] = ambient.G; f[o++] = ambient.B; f[o++] = 1f;
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
        }
    }
}
