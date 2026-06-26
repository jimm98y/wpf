// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Resource-binding surface of the WebGPU binding: samplers, bind groups, and
// texture uploads. This is what lets fragment shaders read textures -- the
// machinery behind gradient ramps and image brushes today, and the glyph atlas
// and effect inputs later. Replaces milcore's texture/brush realization on the
// Direct3D device.
//
// Layouts/values transcribed verbatim from wgpu-native v29.0.1.1 headers.
//

using System;
using System.Runtime.InteropServices;

namespace Microsoft.Wpf.Interop.WebGpu
{
    internal static unsafe partial class Wgpu
    {
        internal enum WGPUAddressMode
        {
            Undefined = 0x00000000,
            ClampToEdge = 0x00000001,
            Repeat = 0x00000002,
            MirrorRepeat = 0x00000003,
        }

        internal enum WGPUFilterMode
        {
            Undefined = 0x00000000,
            Nearest = 0x00000001,
            Linear = 0x00000002,
        }

        internal enum WGPUMipmapFilterMode
        {
            Undefined = 0x00000000,
            Nearest = 0x00000001,
            Linear = 0x00000002,
        }

        internal enum WGPUCompareFunction
        {
            Undefined = 0x00000000,
            Less = 0x00000002,
            LessEqual = 0x00000004,
            Always = 0x00000008,
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct WGPUBindGroupEntry
        {
            public WGPUChainedStruct* nextInChain;
            public uint binding;
            public IntPtr buffer;   // WGPUBuffer
            public ulong offset;
            public ulong size;
            public IntPtr sampler;  // WGPUSampler
            public IntPtr textureView; // WGPUTextureView
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct WGPUBindGroupDescriptor
        {
            public WGPUChainedStruct* nextInChain;
            public WGPUStringView label;
            public IntPtr layout;   // WGPUBindGroupLayout
            public nuint entryCount;
            public WGPUBindGroupEntry* entries;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct WGPUSamplerDescriptor
        {
            public WGPUChainedStruct* nextInChain;
            public WGPUStringView label;
            public WGPUAddressMode addressModeU;
            public WGPUAddressMode addressModeV;
            public WGPUAddressMode addressModeW;
            public WGPUFilterMode magFilter;
            public WGPUFilterMode minFilter;
            public WGPUMipmapFilterMode mipmapFilter;
            public float lodMinClamp;
            public float lodMaxClamp;
            public WGPUCompareFunction compare;
            public ushort maxAnisotropy;
        }

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
    }
}
