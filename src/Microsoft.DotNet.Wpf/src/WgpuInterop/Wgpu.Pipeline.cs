// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Render-pipeline surface of the WebGPU binding: shader modules, render
// pipelines (vertex layout + blend state), vertex/index buffers, and the
// render-pass draw/scissor commands. This is what turns a tessellated WPF
// scene into actual GPU draw calls -- the replacement for milcore's D3D
// primitive submission.
//
// As with Wgpu.cs, every layout/value is transcribed verbatim from the pinned
// wgpu-native v29.0.1.1 headers. See that file for ABI conventions.
//

using System;
using System.Runtime.InteropServices;

namespace Microsoft.Wpf.Interop.WebGpu
{
    internal static unsafe partial class Wgpu
    {
        // ---- Enums -----------------------------------------------------------

        internal enum WGPUVertexFormat
        {
            Unorm8x4 = 0x00000009,
            Float32x2 = 0x0000001D,
            Float32x4 = 0x0000001F,
        }

        internal enum WGPUVertexStepMode
        {
            Undefined = 0x00000000,
            Vertex = 0x00000001,
            Instance = 0x00000002,
        }

        internal enum WGPUPrimitiveTopology
        {
            Undefined = 0x00000000,
            PointList = 0x00000001,
            LineList = 0x00000002,
            LineStrip = 0x00000003,
            TriangleList = 0x00000004,
            TriangleStrip = 0x00000005,
        }

        internal enum WGPUIndexFormat
        {
            Undefined = 0x00000000,
            Uint16 = 0x00000001,
            Uint32 = 0x00000002,
        }

        internal enum WGPUFrontFace
        {
            Undefined = 0x00000000,
            CCW = 0x00000001,
            CW = 0x00000002,
        }

        internal enum WGPUCullMode
        {
            Undefined = 0x00000000,
            None = 0x00000001,
            Front = 0x00000002,
            Back = 0x00000003,
        }

        internal enum WGPUBlendOperation
        {
            Undefined = 0x00000000,
            Add = 0x00000001,
            Subtract = 0x00000002,
            ReverseSubtract = 0x00000003,
            Min = 0x00000004,
            Max = 0x00000005,
        }

        internal enum WGPUBlendFactor
        {
            Undefined = 0x00000000,
            Zero = 0x00000001,
            One = 0x00000002,
            Src = 0x00000003,
            OneMinusSrc = 0x00000004,
            SrcAlpha = 0x00000005,
            OneMinusSrcAlpha = 0x00000006,
            Dst = 0x00000007,
            OneMinusDst = 0x00000008,
            DstAlpha = 0x00000009,
            OneMinusDstAlpha = 0x0000000A,
            SrcAlphaSaturated = 0x0000000B,
        }

        // WGPUColorWriteMask is a WGPUFlags (uint64).
        internal const ulong WGPUColorWriteMask_All = 0xF;

        // WGPUSType value for chaining WGSL shader source onto a shader module.
        internal const int WGPUSType_ShaderSourceWGSL = 0x00000002;

        // ---- Structs ---------------------------------------------------------

        [StructLayout(LayoutKind.Sequential)]
        internal struct WGPUShaderModuleDescriptor
        {
            public WGPUChainedStruct* nextInChain;
            public WGPUStringView label;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct WGPUShaderSourceWGSL
        {
            public WGPUChainedStruct chain; // chain.sType = WGPUSType_ShaderSourceWGSL
            public WGPUStringView code;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct WGPUVertexAttribute
        {
            public WGPUChainedStruct* nextInChain;
            public WGPUVertexFormat format;
            public ulong offset;
            public uint shaderLocation;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct WGPUVertexBufferLayout
        {
            public WGPUChainedStruct* nextInChain;
            public WGPUVertexStepMode stepMode;
            public ulong arrayStride;
            public nuint attributeCount;
            public WGPUVertexAttribute* attributes;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct WGPUVertexState
        {
            public WGPUChainedStruct* nextInChain;
            public IntPtr module; // WGPUShaderModule
            public WGPUStringView entryPoint;
            public nuint constantCount;
            public IntPtr constants;
            public nuint bufferCount;
            public WGPUVertexBufferLayout* buffers;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct WGPUPrimitiveState
        {
            public WGPUChainedStruct* nextInChain;
            public WGPUPrimitiveTopology topology;
            public WGPUIndexFormat stripIndexFormat;
            public WGPUFrontFace frontFace;
            public WGPUCullMode cullMode;
            public uint unclippedDepth; // WGPUBool
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct WGPUMultisampleState
        {
            public WGPUChainedStruct* nextInChain;
            public uint count;
            public uint mask;
            public uint alphaToCoverageEnabled; // WGPUBool
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct WGPUBlendComponent
        {
            public WGPUBlendOperation operation;
            public WGPUBlendFactor srcFactor;
            public WGPUBlendFactor dstFactor;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct WGPUBlendState
        {
            public WGPUBlendComponent color;
            public WGPUBlendComponent alpha;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct WGPUColorTargetState
        {
            public WGPUChainedStruct* nextInChain;
            public WGPUTextureFormat format;
            public WGPUBlendState* blend;
            public ulong writeMask; // WGPUColorWriteMask
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct WGPUFragmentState
        {
            public WGPUChainedStruct* nextInChain;
            public IntPtr module; // WGPUShaderModule
            public WGPUStringView entryPoint;
            public nuint constantCount;
            public IntPtr constants;
            public nuint targetCount;
            public WGPUColorTargetState* targets;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct WGPURenderPipelineDescriptor
        {
            public WGPUChainedStruct* nextInChain;
            public WGPUStringView label;
            public IntPtr layout;             // WGPUPipelineLayout (NULL = auto)
            public WGPUVertexState vertex;     // by value
            public WGPUPrimitiveState primitive; // by value
            public IntPtr depthStencil;        // WGPUDepthStencilState const*
            public WGPUMultisampleState multisample; // by value
            public WGPUFragmentState* fragment;
        }

        // ---- Functions -------------------------------------------------------

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
    }
}
