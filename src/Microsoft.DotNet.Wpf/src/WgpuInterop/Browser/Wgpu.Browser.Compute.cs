// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The Direct3D shim's compute and indirect-draw entry points (Direct3D/D3DUnorderedAccess.cs declares
// them for wgpu-native) on the browser's WebGPU: the same names, through Wgpu.Browser.cs.
//

using System;

namespace Microsoft.Wpf.Interop.WebGpu.Direct3D
{
    internal static unsafe partial class WgpuCompute
    {
        internal static IntPtr wgpuDeviceCreateComputePipeline(IntPtr device, WGPUComputePipelineDescriptor* d) => Wgpu.ComputePipeline(device, d);
        internal static void wgpuComputePipelineRelease(IntPtr pipeline) => Wgpu.ComputeRelease(pipeline);
        internal static IntPtr wgpuCommandEncoderBeginComputePass(IntPtr encoder, WGPUComputePassDescriptor* d) => Wgpu.BeginComputePass(encoder);
        internal static void wgpuComputePassEncoderSetPipeline(IntPtr pass, IntPtr pipeline) => Wgpu.wgpuRenderPassEncoderSetPipeline(pass, pipeline);
        internal static void wgpuComputePassEncoderSetBindGroup(IntPtr pass, uint index, IntPtr group, nuint dynamicOffsetCount, uint* dynamicOffsets)
            => Wgpu.SetBindGroup(pass, index, group, dynamicOffsetCount, dynamicOffsets);
        internal static void wgpuComputePassEncoderDispatchWorkgroups(IntPtr pass, uint x, uint y, uint z) => Wgpu.Dispatch(pass, x, y, z);
        internal static void wgpuComputePassEncoderDispatchWorkgroupsIndirect(IntPtr pass, IntPtr buffer, ulong offset) => Wgpu.DispatchIndirect(pass, buffer, offset);
        internal static void wgpuComputePassEncoderEnd(IntPtr pass) => Wgpu.wgpuRenderPassEncoderEnd(pass);
        internal static void wgpuComputePassEncoderRelease(IntPtr pass) => Wgpu.ComputeRelease(pass);
        internal static void wgpuCommandEncoderClearBuffer(IntPtr encoder, IntPtr buffer, ulong offset, ulong size) => Wgpu.wgpuCommandEncoderClearBuffer(encoder, buffer, offset, size);
        internal static void wgpuRenderPassEncoderDrawIndirect(IntPtr pass, IntPtr buffer, ulong offset) => Wgpu.DrawIndirect(pass, buffer, offset);
        internal static void wgpuRenderPassEncoderDrawIndexedIndirect(IntPtr pass, IntPtr buffer, ulong offset) => Wgpu.DrawIndexedIndirect(pass, buffer, offset);
    }
}
