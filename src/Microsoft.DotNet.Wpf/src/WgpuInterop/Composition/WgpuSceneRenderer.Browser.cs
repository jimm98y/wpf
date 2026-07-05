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
            WGPUTextureFormat outFormat = srgbOutput ? OffscreenFormat : ReadbackFormat;
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
