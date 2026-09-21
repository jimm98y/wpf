// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Depth-stencil surface of the WebGPU binding: the depth-test state for the 3D
// render pipeline and the depth attachment for the 3D render pass. This is what
// makes WPF's 3D (Viewport3D) hidden-surface removal work -- nearer fragments
// occlude farther ones -- the cross-platform replacement for milcore's
// Direct3D depth buffer.
//
// Layouts/values transcribed verbatim from wgpu-native v29.0.1.1 headers.
//

using System;
using System.Runtime.InteropServices;

namespace Microsoft.Wpf.Interop.WebGpu
{
    internal static unsafe partial class Wgpu
    {
        internal enum WGPUOptionalBool
        {
            False = 0x00000000,
            True = 0x00000001,
            Undefined = 0x00000002,
        }

        internal enum WGPUStencilOperation
        {
            Undefined = 0x00000000,
            Keep = 0x00000001,
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct WGPUStencilFaceState
        {
            public WGPUCompareFunction compare;
            public WGPUStencilOperation failOp;
            public WGPUStencilOperation depthFailOp;
            public WGPUStencilOperation passOp;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct WGPUDepthStencilState
        {
            public WGPUChainedStruct* nextInChain;
            public WGPUTextureFormat format;
            public WGPUOptionalBool depthWriteEnabled;
            public WGPUCompareFunction depthCompare;
            public WGPUStencilFaceState stencilFront;
            public WGPUStencilFaceState stencilBack;
            public uint stencilReadMask;
            public uint stencilWriteMask;
            public int depthBias;
            public float depthBiasSlopeScale;
            public float depthBiasClamp;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct WGPURenderPassDepthStencilAttachment
        {
            public WGPUChainedStruct* nextInChain;
            public IntPtr view; // WGPUTextureView
            public WGPULoadOp depthLoadOp;
            public WGPUStoreOp depthStoreOp;
            public float depthClearValue;
            public uint depthReadOnly; // WGPUBool
            public WGPULoadOp stencilLoadOp;
            public WGPUStoreOp stencilStoreOp;
            public uint stencilClearValue;
            public uint stencilReadOnly; // WGPUBool
        }
    }
}
