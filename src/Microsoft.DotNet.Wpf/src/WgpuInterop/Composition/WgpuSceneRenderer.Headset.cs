// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A Viewport3D a headset shows alone, filling the eyes (AndroidXrHost's fullscreen, AndroidXrStereo3D's eye
// cameras): its scene drawn for two eyes side by side -- each from its own camera, given -- into an image of its
// own with the depth it was drawn with, which the headset's panel shows and moves to where the eyes are
// (XrExternalPanel). What a 3D engine behind a D3DImage does for its own views, WPF's 3D does here; the same
// panel, the same eyes and the same controls for either.
//
// Its window is not shown meanwhile (nothing composes it); the viewport is found in its retained scene by the
// rectangle it covers, and drawn as the window's 3D pass draws it (Render3D.cs) but for the eyes' cameras: the
// same meshes, materials, lights and pipelines. A material whose texture is live 2D content (a VisualBrush)
// is drawn untextured here: that content is rendered in the window's frame, which there is none of.
//

using System;
using System.Collections.Generic;
using System.Numerics;
using static Microsoft.Wpf.Interop.WebGpu.Wgpu;

namespace Microsoft.Wpf.Interop.WebGpu.Composition
{
    internal sealed unsafe partial class WgpuSceneRenderer
    {
        private IntPtr _headsetColor, _headsetColorView, _headsetMsaa, _headsetMsaaView, _headsetDepth, _headsetDepthView;
        private int _headsetWidth, _headsetHeight;
        private ExternalImage? _headsetImage;

        /// <summary>The image the last <see cref="RenderHeadset3D"/> drew (straight colour), or null.</summary>
        internal ExternalImage? HeadsetImage => _headsetImage;

        /// <summary>Its depth (Depth24Plus, multisampled as the 3D pass is), for the panel to move it by.</summary>
        internal (IntPtr Texture, WGPUTextureFormat Format, uint Samples) HeadsetDepth => (_headsetDepth, DepthFormat, Msaa3D);

        /// <summary>The Viewport3D of <paramref name="root"/> covering <paramref name="rect"/> (device pixels of the
        /// scene) drawn for two eyes into a <paramref name="width"/> x <paramref name="height"/> image, the left eye's
        /// camera (view, projection: right-handed, depth 0 near to 1 far) in its left half, the right eye's in the
        /// right. False when no viewport covers it.</summary>
        internal bool RenderHeadset3D(SceneVisual root, Rect rect, Matrix4x4 leftView, Matrix4x4 leftProjection,
            Matrix4x4 rightView, Matrix4x4 rightProjection, int width, int height)
        {
            Viewport3DDraw? viewport = FindViewport(root, Matrix3x2.Identity, rect);
            if (viewport == null || width < 2 || height < 1) return false;
            EnsureHeadsetTargets(width, height);
            BeginFrame3D();
            var eyes = new List<Draw3D>[2];
            Matrix4x4[] views = { leftView, rightView }, projections = { leftProjection, rightProjection };
            for (int e = 0; e < 2; e++)
            {
                var models = new List<Draw3D>(viewport.Models.Count);
                var slots = new List<DrawSlot>(viewport.Models.Count);
                int baseSlot = _slot3DCursor;
                Matrix4x4.Invert(views[e], out Matrix4x4 eyeWorld);
                var eyePosition = new Vector3(eyeWorld.M41, eyeWorld.M42, eyeWorld.M43);
                Matrix4x4 viewProjection = views[e] * projections[e];
                foreach (Model3D model in viewport.Models)
                {
                    MeshGeometry3D mesh = model.Mesh;
                    if (mesh.Indices.Length == 0 || (!model.HasFrontMaterial && !model.HasBackMaterial)) continue;
                    (IntPtr vbuf, IntPtr ibuf) = GetMeshBuffers(mesh);
                    bool transparent = (model.HasFrontMaterial && MaterialIsTransparent(model.Material))
                                     || (model.HasBackMaterial && MaterialIsTransparent(model.BackMaterial));
                    // as the window's pass orders them: a transparent model's far side first
                    if (transparent)
                    {
                        if (model.HasBackMaterial) Add(model.BackMaterial, true);
                        if (model.HasFrontMaterial) Add(model.Material, false);
                    }
                    else
                    {
                        if (model.HasFrontMaterial) Add(model.Material, false);
                        if (model.HasBackMaterial) Add(model.BackMaterial, true);
                    }

                    void Add(Material3D material, bool backFace)
                    {
                        IntPtr texView;
                        if (material.TextureVisual == null && material.Texture is { } px && material.TexWidth > 0 && material.TexHeight > 0)
                        {
                            (IntPtr tex, IntPtr tv) = CreateImageTexture(px, material.TexWidth, material.TexHeight);
                            DeferReleaseTexView(tex, tv);
                            texView = tv;
                        }
                        else texView = White3DView();
                        slots.Add(new DrawSlot(texView, backFace, !transparent));
                        WriteModelUniform(baseSlot + slots.Count - 1, model.Transform * viewProjection, model.Transform, eyePosition,
                            viewport.Lights, viewport.AmbientColor, material, backFace);
                        models.Add(new Draw3D(vbuf, ibuf, IntPtr.Zero, (uint)mesh.Indices.Length, backFace, transparent));
                    }
                }
                Resolve3DSlots(baseSlot, slots, models);
                _slot3DCursor = baseSlot + slots.Count;
                eyes[e] = models;
            }

            IntPtr encoder = wgpuDeviceCreateCommandEncoder(_ctx.Device, IntPtr.Zero);
            FlushPendingTexUploads(encoder);
            var color = new WGPURenderPassColorAttachment
            {
                view = _headsetMsaaView, resolveTarget = _headsetColorView, depthSlice = WGPU_DEPTH_SLICE_UNDEFINED,
                loadOp = WGPULoadOp.Clear, storeOp = WGPUStoreOp.Discard, clearValue = new WGPUColor { r = 0, g = 0, b = 0, a = 0 },
            };
            var depth = new WGPURenderPassDepthStencilAttachment
            {
                view = _headsetDepthView, depthLoadOp = WGPULoadOp.Clear, depthStoreOp = WGPUStoreOp.Store, depthClearValue = 1f,
                stencilLoadOp = WGPULoadOp.Undefined, stencilStoreOp = WGPUStoreOp.Undefined,
            };
            var desc = new WGPURenderPassDescriptor { colorAttachmentCount = 1, colorAttachments = &color, depthStencilAttachment = (IntPtr)(&depth) };
            IntPtr pass = wgpuCommandEncoderBeginRenderPass(encoder, &desc);
            int half = width / 2;
            for (int e = 0; e < 2; e++)
            {
                wgpuRenderPassEncoderSetViewport(pass, e * half, 0, half, height, 0, 1);
                foreach (bool transparent in new[] { false, true })
                    foreach (Draw3D m in eyes[e])
                    {
                        if (m.Transparent != transparent) continue;
                        wgpuRenderPassEncoderSetPipeline(pass, Get3DPipeline(ReadbackFormat, m.BackFace ? WGPUCullMode.Front : WGPUCullMode.Back, depthWrite: !transparent));
                        wgpuRenderPassEncoderSetBindGroup(pass, 0, m.BindGroup, 0, null);
                        wgpuRenderPassEncoderSetVertexBuffer(pass, 0, m.Vbuf, 0, WholeSize);
                        wgpuRenderPassEncoderSetIndexBuffer(pass, m.Ibuf, WGPUIndexFormat.Uint32, 0, WholeSize);
                        wgpuRenderPassEncoderDrawIndexed(pass, m.IndexCount, 1, 0, 0, 0);
                    }
            }
            wgpuRenderPassEncoderEnd(pass);
            wgpuRenderPassEncoderRelease(pass);
            IntPtr cmd = wgpuCommandEncoderFinish(encoder, IntPtr.Zero);
            wgpuCommandEncoderRelease(encoder);
            wgpuQueueSubmit(_ctx.Queue, 1, &cmd);
            wgpuCommandBufferRelease(cmd);
            FlushFrameReleases();
            return true;
        }

        private void EnsureHeadsetTargets(int width, int height)
        {
            if (_headsetColor != IntPtr.Zero && _headsetWidth == width && _headsetHeight == height) return;
            ReleaseHeadsetTargets();
            var size = new WGPUExtent3D { width = (uint)width, height = (uint)height, depthOrArrayLayers = 1 };
            var cd = new WGPUTextureDescriptor
            {
                usage = WGPUTextureUsage.RenderAttachment | WGPUTextureUsage.TextureBinding | WGPUTextureUsage.CopySrc,
                dimension = WGPUTextureDimension._2D, size = size, format = ReadbackFormat, mipLevelCount = 1, sampleCount = 1,
            };
            _headsetColor = wgpuDeviceCreateTexture(_ctx.Device, &cd);
            _headsetColorView = wgpuTextureCreateView(_headsetColor, IntPtr.Zero);
            var md = cd;
            md.usage = WGPUTextureUsage.RenderAttachment;
            md.sampleCount = Msaa3D;
            _headsetMsaa = wgpuDeviceCreateTexture(_ctx.Device, &md);
            _headsetMsaaView = wgpuTextureCreateView(_headsetMsaa, IntPtr.Zero);
            // read for the picture it was drawn with (XrExternalPanel.CopyDepth)
            var dd = md;
            dd.usage = WGPUTextureUsage.RenderAttachment | WGPUTextureUsage.TextureBinding;
            dd.format = DepthFormat;
            _headsetDepth = wgpuDeviceCreateTexture(_ctx.Device, &dd);
            _headsetDepthView = wgpuTextureCreateView(_headsetDepth, IntPtr.Zero);
            _headsetWidth = width;
            _headsetHeight = height;
            _headsetImage = new ExternalImage(_ctx.Device, _headsetColor, _headsetColorView, width, height, ReadbackFormat);
        }

        private void ReleaseHeadsetTargets()
        {
            if (_headsetColorView != IntPtr.Zero) wgpuTextureViewRelease(_headsetColorView);
            if (_headsetColor != IntPtr.Zero) wgpuTextureRelease(_headsetColor);
            if (_headsetMsaaView != IntPtr.Zero) wgpuTextureViewRelease(_headsetMsaaView);
            if (_headsetMsaa != IntPtr.Zero) wgpuTextureRelease(_headsetMsaa);
            if (_headsetDepthView != IntPtr.Zero) wgpuTextureViewRelease(_headsetDepthView);
            if (_headsetDepth != IntPtr.Zero) wgpuTextureRelease(_headsetDepth);
            _headsetColor = _headsetColorView = _headsetMsaa = _headsetMsaaView = _headsetDepth = _headsetDepthView = IntPtr.Zero;
            _headsetImage = null;
        }

        /// <summary>The Viewport3D whose device rectangle is <paramref name="rect"/> (within a couple of pixels), or the
        /// one covering most of it.</summary>
        private static Viewport3DDraw? FindViewport(SceneVisual v, Matrix3x2 parentWorld, Rect rect)
        {
            Viewport3DDraw? best = null;
            double bestArea = 0;
            Walk(v, parentWorld);
            return best;

            void Walk(SceneVisual visual, Matrix3x2 parent)
            {
                Matrix3x2 world = visual.LocalToParent * parent;
                foreach (DrawingPrimitive p in visual.Content)
                {
                    if (p is Viewport3DDraw vp && vp.Viewport.Width > 0 && vp.Viewport.Height > 0)
                    {
                        Vector2 a = Vector2.Transform(new Vector2(vp.Viewport.X, vp.Viewport.Y), world);
                        Vector2 b = Vector2.Transform(new Vector2(vp.Viewport.X + vp.Viewport.Width, vp.Viewport.Y + vp.Viewport.Height), world);
                        float w = MathF.Min(MathF.Max(a.X, b.X), rect.X + rect.Width) - MathF.Max(MathF.Min(a.X, b.X), rect.X);
                        float h = MathF.Min(MathF.Max(a.Y, b.Y), rect.Y + rect.Height) - MathF.Max(MathF.Min(a.Y, b.Y), rect.Y);
                        double area = w > 0 && h > 0 ? (double)w * h : 0;
                        if (area > bestArea) { bestArea = area; best = vp; }
                    }
                    else if (p is NestedVisualDraw nested) Walk(nested.Visual, world);
                }
                foreach (SceneVisual child in visual.Children) Walk(child, world);
            }
        }
    }
}
