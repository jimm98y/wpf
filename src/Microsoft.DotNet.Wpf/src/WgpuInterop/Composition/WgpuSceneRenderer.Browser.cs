// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Browser (WebAssembly) readback support for WgpuSceneRenderer. Lives in its own
// NON-unsafe partial class part: `await` is illegal inside the main file's unsafe
// class declaration, and blocking buffer maps are impossible on the browser main
// thread — so the desktop RenderToRgba gets this async sibling instead.
//

#if WGPU_BROWSER

using System;
using System.Threading.Tasks;
using static Microsoft.Wpf.Interop.WebGpu.Wgpu;

namespace Microsoft.Wpf.Interop.WebGpu.Composition
{
    internal sealed partial class WgpuSceneRenderer
    {
        /// <summary>
        /// Browser variant of <see cref="RenderToRgba"/>: the scene render is encoded and
        /// submitted synchronously (the caller's visual tree is safe to reuse afterwards);
        /// only the GPU->CPU map awaits a Promise. Returns tightly packed RGBA8.
        /// </summary>
        public async Task<byte[]> RenderToRgbaAsync(SceneVisual root, int width, int height, RgbaColor background, bool srgbOutput = false)
        {
            PerfReadbacks++;
            // Gamma mode: colours are pre-encoded + blended in gamma space, so read back from a plain UNORM
            // target (bytes are already the sRGB pixels); an sRGB target would encode twice. See RenderToRgba.
            WGPUTextureFormat outFormat = (srgbOutput && !s_gammaComposite) ? OffscreenFormat : ReadbackFormat;
            IntPtr targetTex = CreateReadbackTargetTexture(_ctx.Device, width, height, outFormat);
            IntPtr targetView = wgpuTextureCreateView(targetTex, IntPtr.Zero);
            try
            {
                RenderSceneToView(root, targetView, outFormat, width, height, background);
                int bytesId = await Browser.WgpuBrowserJs.ReadbackTexture((int)_ctx.Device, (int)targetTex, width, height);
                return Browser.WgpuBrowserJs.TakeBytes(bytesId);
            }
            finally
            {
                wgpuTextureViewRelease(targetView);
                wgpuTextureRelease(targetTex);
            }
        }

        /// <summary>
        /// Browser GPU hit test against the last composited scene: renders the visual-id buffer
        /// once per frame (lazily, on the first query) and awaits a 1-pixel readback of the point.
        /// Returns the topmost visual's id, or 0 for no hit / out of range.
        /// </summary>
        public async Task<uint> HitTestAsync(int x, int y)
        {
            if (!EnsureIdBuffer() || x < 0 || y < 0 || x >= _idW || y >= _idH) return 0;
            int id = await Browser.WgpuBrowserJs.ReadbackTexel((int)_ctx.Device, (int)_idTex, x, y);
            return (uint)id;
        }

        /// <summary>Standalone async hit test: renders the id buffer for the given scene, then
        /// awaits the readback. Prefer <see cref="HitTestAsync(int,int)"/> after a frame render.</summary>
        public Task<uint> HitTestAsync(SceneVisual root, int x, int y, int width, int height)
        {
            _idScene = root; _idW = width; _idH = height; _idValid = false;
            return HitTestAsync(x, y);
        }

        private static unsafe IntPtr CreateReadbackTargetTexture(IntPtr device, int width, int height, WGPUTextureFormat format)
        {
            var texDesc = new WGPUTextureDescriptor
            {
                usage = WGPUTextureUsage.RenderAttachment | WGPUTextureUsage.CopySrc,
                dimension = WGPUTextureDimension._2D,
                size = new WGPUExtent3D { width = (uint)width, height = (uint)height, depthOrArrayLayers = 1 },
                format = format,
                mipLevelCount = 1,
                sampleCount = 1,
            };
            return wgpuDeviceCreateTexture(device, &texDesc);
        }
    }
}

#endif
