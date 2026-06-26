// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Reference-count release entry points. Every WebGPU object is reference
// counted; wgpu*Release drops the caller's reference (the implementation keeps
// resources alive until in-flight GPU work that uses them completes). Without
// these the renderer leaked a texture/bind group/buffer set every frame. They
// are the cross-platform analog of releasing milcore's COM resources.
//

using System;
using System.Runtime.InteropServices;

namespace Microsoft.Wpf.Interop.WebGpu
{
    internal static unsafe partial class Wgpu
    {
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void wgpuTextureRelease(IntPtr texture);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void wgpuTextureViewRelease(IntPtr textureView);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void wgpuBufferRelease(IntPtr buffer);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void wgpuBindGroupRelease(IntPtr bindGroup);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void wgpuSamplerRelease(IntPtr sampler);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void wgpuCommandEncoderRelease(IntPtr commandEncoder);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void wgpuCommandBufferRelease(IntPtr commandBuffer);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void wgpuRenderPassEncoderRelease(IntPtr renderPassEncoder);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void wgpuRenderPipelineRelease(IntPtr renderPipeline);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void wgpuShaderModuleRelease(IntPtr shaderModule);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void wgpuQueueRelease(IntPtr queue);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void wgpuDeviceRelease(IntPtr device);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void wgpuAdapterRelease(IntPtr adapter);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void wgpuInstanceRelease(IntPtr instance);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void wgpuSurfaceRelease(IntPtr surface);
    }
}
