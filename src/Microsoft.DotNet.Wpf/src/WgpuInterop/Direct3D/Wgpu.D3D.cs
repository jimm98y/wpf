// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The part of webgpu.h (wgpu-native v29) the Direct3D shim needs beyond what the renderer binds:
// explicit bind-group and pipeline layouts, error scopes (a validation error the shim provokes must
// come back as an HRESULT, not abort the process), non-indexed draws, buffer and texture copies.
// Enum values the renderer's enums do not list are named here as constants of those enums.
//

using System;
using System.Runtime.InteropServices;

namespace Microsoft.Wpf.Interop.WebGpu
{
    internal static unsafe partial class Wgpu
    {
        internal static class TexFmt
        {
            public const WGPUTextureFormat R8Unorm = (WGPUTextureFormat)0x01, R8Snorm = (WGPUTextureFormat)0x02, R8Uint = (WGPUTextureFormat)0x03, R8Sint = (WGPUTextureFormat)0x04;
            public const WGPUTextureFormat R16Unorm = (WGPUTextureFormat)0x05, R16Snorm = (WGPUTextureFormat)0x06, R16Uint = (WGPUTextureFormat)0x07, R16Sint = (WGPUTextureFormat)0x08, R16Float = (WGPUTextureFormat)0x09;
            public const WGPUTextureFormat RG8Unorm = (WGPUTextureFormat)0x0A, RG8Snorm = (WGPUTextureFormat)0x0B, RG8Uint = (WGPUTextureFormat)0x0C, RG8Sint = (WGPUTextureFormat)0x0D;
            public const WGPUTextureFormat R32Float = (WGPUTextureFormat)0x0E, R32Uint = (WGPUTextureFormat)0x0F, R32Sint = (WGPUTextureFormat)0x10;
            public const WGPUTextureFormat RG16Unorm = (WGPUTextureFormat)0x11, RG16Snorm = (WGPUTextureFormat)0x12, RG16Uint = (WGPUTextureFormat)0x13, RG16Sint = (WGPUTextureFormat)0x14, RG16Float = (WGPUTextureFormat)0x15;
            public const WGPUTextureFormat RGBA8Unorm = (WGPUTextureFormat)0x16, RGBA8UnormSrgb = (WGPUTextureFormat)0x17, RGBA8Snorm = (WGPUTextureFormat)0x18, RGBA8Uint = (WGPUTextureFormat)0x19, RGBA8Sint = (WGPUTextureFormat)0x1A;
            public const WGPUTextureFormat BGRA8Unorm = (WGPUTextureFormat)0x1B, BGRA8UnormSrgb = (WGPUTextureFormat)0x1C;
            public const WGPUTextureFormat RGB10A2Uint = (WGPUTextureFormat)0x1D, RGB10A2Unorm = (WGPUTextureFormat)0x1E, RG11B10Ufloat = (WGPUTextureFormat)0x1F, RGB9E5Ufloat = (WGPUTextureFormat)0x20;
            public const WGPUTextureFormat RG32Float = (WGPUTextureFormat)0x21, RG32Uint = (WGPUTextureFormat)0x22, RG32Sint = (WGPUTextureFormat)0x23;
            public const WGPUTextureFormat RGBA16Unorm = (WGPUTextureFormat)0x24, RGBA16Snorm = (WGPUTextureFormat)0x25, RGBA16Uint = (WGPUTextureFormat)0x26, RGBA16Sint = (WGPUTextureFormat)0x27, RGBA16Float = (WGPUTextureFormat)0x28;
            public const WGPUTextureFormat RGBA32Float = (WGPUTextureFormat)0x29, RGBA32Uint = (WGPUTextureFormat)0x2A, RGBA32Sint = (WGPUTextureFormat)0x2B;
            public const WGPUTextureFormat Stencil8 = (WGPUTextureFormat)0x2C, Depth16Unorm = (WGPUTextureFormat)0x2D, Depth24Plus = (WGPUTextureFormat)0x2E, Depth24PlusStencil8 = (WGPUTextureFormat)0x2F, Depth32Float = (WGPUTextureFormat)0x30, Depth32FloatStencil8 = (WGPUTextureFormat)0x31;
            public const WGPUTextureFormat BC1 = (WGPUTextureFormat)0x32, BC1Srgb = (WGPUTextureFormat)0x33, BC2 = (WGPUTextureFormat)0x34, BC2Srgb = (WGPUTextureFormat)0x35, BC3 = (WGPUTextureFormat)0x36, BC3Srgb = (WGPUTextureFormat)0x37;
            public const WGPUTextureFormat BC4 = (WGPUTextureFormat)0x38, BC4Snorm = (WGPUTextureFormat)0x39, BC5 = (WGPUTextureFormat)0x3A, BC5Snorm = (WGPUTextureFormat)0x3B, BC6HU = (WGPUTextureFormat)0x3C, BC6HS = (WGPUTextureFormat)0x3D, BC7 = (WGPUTextureFormat)0x3E, BC7Srgb = (WGPUTextureFormat)0x3F;
        }

        internal static class VtxFmt
        {
            public const WGPUVertexFormat Uint8x2 = (WGPUVertexFormat)0x02, Uint8x4 = (WGPUVertexFormat)0x03, Sint8x2 = (WGPUVertexFormat)0x05, Sint8x4 = (WGPUVertexFormat)0x06;
            public const WGPUVertexFormat Unorm8x2 = (WGPUVertexFormat)0x08, Unorm8x4 = (WGPUVertexFormat)0x09, Snorm8x2 = (WGPUVertexFormat)0x0B, Snorm8x4 = (WGPUVertexFormat)0x0C;
            public const WGPUVertexFormat Uint16x2 = (WGPUVertexFormat)0x0E, Uint16x4 = (WGPUVertexFormat)0x0F, Sint16x2 = (WGPUVertexFormat)0x11, Sint16x4 = (WGPUVertexFormat)0x12;
            public const WGPUVertexFormat Unorm16x2 = (WGPUVertexFormat)0x14, Unorm16x4 = (WGPUVertexFormat)0x15, Snorm16x2 = (WGPUVertexFormat)0x17, Snorm16x4 = (WGPUVertexFormat)0x18;
            public const WGPUVertexFormat Float16x2 = (WGPUVertexFormat)0x1A, Float16x4 = (WGPUVertexFormat)0x1B;
            public const WGPUVertexFormat Float32 = (WGPUVertexFormat)0x1C, Float32x2 = (WGPUVertexFormat)0x1D, Float32x3 = (WGPUVertexFormat)0x1E, Float32x4 = (WGPUVertexFormat)0x1F;
            public const WGPUVertexFormat Uint32 = (WGPUVertexFormat)0x20, Uint32x2 = (WGPUVertexFormat)0x21, Uint32x3 = (WGPUVertexFormat)0x22, Uint32x4 = (WGPUVertexFormat)0x23;
            public const WGPUVertexFormat Sint32 = (WGPUVertexFormat)0x24, Sint32x2 = (WGPUVertexFormat)0x25, Sint32x3 = (WGPUVertexFormat)0x26, Sint32x4 = (WGPUVertexFormat)0x27;
            public const WGPUVertexFormat Unorm10_10_10_2 = (WGPUVertexFormat)0x28, Unorm8x4BGRA = (WGPUVertexFormat)0x29;
            public const WGPUVertexFormat Uint8 = (WGPUVertexFormat)0x01, Sint8 = (WGPUVertexFormat)0x04, Unorm8 = (WGPUVertexFormat)0x07, Snorm8 = (WGPUVertexFormat)0x0A;
            public const WGPUVertexFormat Uint16 = (WGPUVertexFormat)0x0D, Sint16 = (WGPUVertexFormat)0x10, Unorm16 = (WGPUVertexFormat)0x13, Snorm16 = (WGPUVertexFormat)0x16, Float16 = (WGPUVertexFormat)0x19;
        }

        internal const ulong WGPUShaderStage_Vertex = 1, WGPUShaderStage_Fragment = 2, WGPUShaderStage_Compute = 4;

        internal enum WGPUBufferBindingType { BindingNotUsed = 0, Undefined = 1, Uniform = 2, Storage = 3, ReadOnlyStorage = 4 }
        internal enum WGPUSamplerBindingType { BindingNotUsed = 0, Undefined = 1, Filtering = 2, NonFiltering = 3, Comparison = 4 }
        internal enum WGPUTextureSampleType { BindingNotUsed = 0, Undefined = 1, Float = 2, UnfilterableFloat = 3, Depth = 4, Sint = 5, Uint = 6 }
        internal enum WGPUErrorFilter { Validation = 1, OutOfMemory = 2, Internal = 3 }
        internal enum WGPUPopErrorScopeStatus { Success = 1, CallbackCancelled = 2, Error = 3 }
        internal enum WGPUErrorType { NoError = 1, Validation = 2, OutOfMemory = 3, Internal = 4, Unknown = 5 }

        [StructLayout(LayoutKind.Sequential)]
        internal struct WGPUBufferBindingLayout
        {
            public WGPUChainedStruct* nextInChain;
            public WGPUBufferBindingType type;
            public uint hasDynamicOffset;
            public ulong minBindingSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct WGPUSamplerBindingLayout
        {
            public WGPUChainedStruct* nextInChain;
            public WGPUSamplerBindingType type;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct WGPUTextureBindingLayout
        {
            public WGPUChainedStruct* nextInChain;
            public WGPUTextureSampleType sampleType;
            public WGPUTextureViewDimension viewDimension;
            public uint multisampled;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct WGPUStorageTextureBindingLayout
        {
            public WGPUChainedStruct* nextInChain;
            public int access;
            public WGPUTextureFormat format;
            public WGPUTextureViewDimension viewDimension;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct WGPUBindGroupLayoutEntry
        {
            public WGPUChainedStruct* nextInChain;
            public uint binding;
            public ulong visibility;           // WGPUShaderStage
            public uint bindingArraySize;
            public WGPUBufferBindingLayout buffer;
            public WGPUSamplerBindingLayout sampler;
            public WGPUTextureBindingLayout texture;
            public WGPUStorageTextureBindingLayout storageTexture;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct WGPUBindGroupLayoutDescriptor
        {
            public WGPUChainedStruct* nextInChain;
            public WGPUStringView label;
            public nuint entryCount;
            public WGPUBindGroupLayoutEntry* entries;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct WGPUPipelineLayoutDescriptor
        {
            public WGPUChainedStruct* nextInChain;
            public WGPUStringView label;
            public nuint bindGroupLayoutCount;
            public IntPtr* bindGroupLayouts;
            public uint immediateSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct WGPUPopErrorScopeCallbackInfo
        {
            public WGPUChainedStruct* nextInChain;
            public WGPUCallbackMode mode;
            public IntPtr callback;
            public IntPtr userdata1;
            public IntPtr userdata2;
        }

#if !WGPU_BROWSER     // the browser's: Browser/Wgpu.Browser.cs
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern uint wgpuAdapterHasFeature(IntPtr adapter, uint feature);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int wgpuAdapterGetLimits(IntPtr adapter, byte* limits);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr wgpuDeviceCreateBindGroupLayout(IntPtr device, WGPUBindGroupLayoutDescriptor* descriptor);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr wgpuDeviceCreatePipelineLayout(IntPtr device, WGPUPipelineLayoutDescriptor* descriptor);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void wgpuPipelineLayoutRelease(IntPtr pipelineLayout);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void wgpuDevicePushErrorScope(IntPtr device, WGPUErrorFilter filter);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern WGPUFuture wgpuDevicePopErrorScope(IntPtr device, WGPUPopErrorScopeCallbackInfo callbackInfo);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void wgpuRenderPassEncoderDraw(IntPtr pass, uint vertexCount, uint instanceCount, uint firstVertex, uint firstInstance);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void wgpuRenderPassEncoderSetBlendConstant(IntPtr pass, WGPUColor* color);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void wgpuRenderPassEncoderSetStencilReference(IntPtr pass, uint reference);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void wgpuCommandEncoderCopyBufferToBuffer(IntPtr encoder, IntPtr source, ulong sourceOffset, IntPtr destination, ulong destinationOffset, ulong size);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void wgpuCommandEncoderCopyTextureToTexture(IntPtr encoder, WGPUTexelCopyTextureInfo* source, WGPUTexelCopyTextureInfo* destination, WGPUExtent3D* copySize);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void wgpuCommandEncoderClearBuffer(IntPtr encoder, IntPtr buffer, ulong offset, ulong size);
#endif
    }
}
