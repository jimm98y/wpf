// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The P/Invoke entry points of the WebGPU binding, extracted into one file so a
// platform can swap the *implementation* without touching the types: desktop
// builds compile this file (DllImport into wgpu-native); the browser/WebAssembly
// build (Browser/WgpuInterop.Browser.csproj) excludes it and supplies managed
// bodies with identical signatures that route to the browser's own WebGPU via
// JS interop (Browser/Wgpu.Browser.cs). Types/enums/structs stay in Wgpu*.cs.
//
// ABI source of truth: wgpu-native v29.0.1.1 (webgpu.h / wgpu.h) — see Wgpu.cs.
//

using System;
using System.Runtime.InteropServices;

namespace Microsoft.Wpf.Interop.WebGpu
{
    internal static unsafe partial class Wgpu
    {
        // ---- Diagnostics (wgpu.h extensions) ----------------------------------

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void wgpuSetLogCallback(IntPtr callback, IntPtr userdata);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void wgpuSetLogLevel(WGPULogLevel level);

        // ---- Instance / adapter / device --------------------------------------

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern WGPUStatus wgpuAdapterGetInfo(IntPtr adapter, WGPUAdapterInfo* info);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr wgpuCreateInstance(WGPUChainedStruct* descriptor);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern WGPUFuture wgpuInstanceRequestAdapter(
            IntPtr instance, WGPURequestAdapterOptions* options, WGPURequestAdapterCallbackInfo callbackInfo);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void wgpuInstanceProcessEvents(IntPtr instance);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern WGPUFuture wgpuAdapterRequestDevice(
            IntPtr adapter, IntPtr descriptor, WGPURequestDeviceCallbackInfo callbackInfo);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr wgpuDeviceGetQueue(IntPtr device);

        // ---- Resource creation -------------------------------------------------

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr wgpuDeviceCreateTexture(IntPtr device, WGPUTextureDescriptor* descriptor);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr wgpuDeviceCreateBuffer(IntPtr device, WGPUBufferDescriptor* descriptor);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr wgpuDeviceCreateCommandEncoder(IntPtr device, IntPtr descriptor);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr wgpuTextureCreateView(IntPtr texture, IntPtr descriptor);

        // ---- Render pass / command encoding ------------------------------------

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr wgpuCommandEncoderBeginRenderPass(IntPtr commandEncoder, WGPURenderPassDescriptor* descriptor);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void wgpuRenderPassEncoderEnd(IntPtr renderPassEncoder);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void wgpuCommandEncoderCopyTextureToBuffer(
            IntPtr commandEncoder, WGPUTexelCopyTextureInfo* source, WGPUTexelCopyBufferInfo* destination, WGPUExtent3D* copySize);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void wgpuCommandEncoderCopyBufferToTexture(
            IntPtr commandEncoder, WGPUTexelCopyBufferInfo* source, WGPUTexelCopyTextureInfo* destination, WGPUExtent3D* copySize);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void wgpuCommandEncoderCopyTextureToTexture(
            IntPtr commandEncoder, WGPUTexelCopyTextureInfo* source, WGPUTexelCopyTextureInfo* destination, WGPUExtent3D* copySize);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr wgpuCommandEncoderFinish(IntPtr commandEncoder, IntPtr descriptor);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void wgpuQueueSubmit(IntPtr queue, nuint commandCount, IntPtr* commands);

        // ---- Buffer readback ----------------------------------------------------

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern WGPUFuture wgpuBufferMapAsync(
            IntPtr buffer, WGPUMapMode mode, nuint offset, nuint size, WGPUBufferMapCallbackInfo callbackInfo);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void* wgpuBufferGetConstMappedRange(IntPtr buffer, nuint offset, nuint size);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void* wgpuBufferGetMappedRange(IntPtr buffer, nuint offset, nuint size);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void wgpuBufferUnmap(IntPtr buffer);

        // Drives the device's internal event/callback queue. wait=true blocks
        // until the optional submission index has completed. (wgpu.h extension)
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern uint wgpuDevicePoll(IntPtr device, uint wait, ulong* submissionIndex);

        // ---- Binding: samplers / bind groups / texture upload -------------------

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr wgpuDeviceCreateBindGroup(IntPtr device, WGPUBindGroupDescriptor* descriptor);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr wgpuDeviceCreateSampler(IntPtr device, WGPUSamplerDescriptor* descriptor);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr wgpuRenderPipelineGetBindGroupLayout(IntPtr renderPipeline, uint groupIndex);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void wgpuQueueWriteTexture(
            IntPtr queue, WGPUTexelCopyTextureInfo* destination, void* data, nuint dataSize,
            WGPUTexelCopyBufferLayout* dataLayout, WGPUExtent3D* writeSize);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void wgpuRenderPassEncoderSetBindGroup(
            IntPtr renderPassEncoder, uint groupIndex, IntPtr group, nuint dynamicOffsetCount, uint* dynamicOffsets);

        // ---- Pipeline: shaders / pipelines / draw -------------------------------

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr wgpuDeviceCreateShaderModule(IntPtr device, WGPUShaderModuleDescriptor* descriptor);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr wgpuDeviceCreateRenderPipeline(IntPtr device, WGPURenderPipelineDescriptor* descriptor);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void wgpuQueueWriteBuffer(IntPtr queue, IntPtr buffer, ulong bufferOffset, void* data, nuint size);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void wgpuRenderPassEncoderSetPipeline(IntPtr renderPassEncoder, IntPtr pipeline);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void wgpuRenderPassEncoderSetVertexBuffer(IntPtr renderPassEncoder, uint slot, IntPtr buffer, ulong offset, ulong size);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void wgpuRenderPassEncoderSetIndexBuffer(IntPtr renderPassEncoder, IntPtr buffer, WGPUIndexFormat format, ulong offset, ulong size);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void wgpuRenderPassEncoderDrawIndexed(IntPtr renderPassEncoder, uint indexCount, uint instanceCount, uint firstIndex, int baseVertex, uint firstInstance);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void wgpuRenderPassEncoderSetScissorRect(IntPtr renderPassEncoder, uint x, uint y, uint width, uint height);

        // ---- Surface (swap chain) -----------------------------------------------

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr wgpuInstanceCreateSurface(IntPtr instance, WGPUSurfaceDescriptor* descriptor);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void wgpuSurfaceConfigure(IntPtr surface, WGPUSurfaceConfiguration* config);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern WGPUStatus wgpuSurfaceGetCapabilities(IntPtr surface, IntPtr adapter, WGPUSurfaceCapabilities* capabilities);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void wgpuSurfaceCapabilitiesFreeMembers(WGPUSurfaceCapabilities capabilities);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void wgpuSurfaceGetCurrentTexture(IntPtr surface, WGPUSurfaceTexture* surfaceTexture);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern WGPUStatus wgpuSurfacePresent(IntPtr surface);
    }
}
