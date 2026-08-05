// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Surface (swap-chain) surface of the WebGPU binding: creating a presentable
// surface from a native window, configuring it, acquiring per-frame textures
// and presenting. This is the cross-platform replacement for milcore's
// HWND/DXGI swap-chain present path (CSlaveHWndRenderTarget). The HWND source
// struct is Windows-specific; the macOS (CAMetalLayer), X11/Wayland and WASM
// canvas sources are sibling chained structs added per platform.
//
// Layouts/values transcribed verbatim from wgpu-native v29.0.1.1 headers.
//

using System;
using System.Runtime.InteropServices;

namespace Microsoft.Wpf.Interop.WebGpu
{
    internal static unsafe partial class Wgpu
    {
        internal enum WGPUStatus
        {
            Success = 0x00000001,
            Error = 0x00000002,
        }

        internal enum WGPUSurfaceGetCurrentTextureStatus
        {
            SuccessOptimal = 0x00000001,
            SuccessSuboptimal = 0x00000002,
            Timeout = 0x00000003,
            Outdated = 0x00000004,
            Lost = 0x00000005,
            Error = 0x00000006,
            // wgpu-native extension (wgpu.h): the acquired texture is valid to render/present,
            // but the surface is currently occluded (e.g. the window is not front-most). We still
            // render and present so the content is up to date when it becomes visible.
            Occluded = 0x00030001,
        }

        internal enum WGPUPresentMode
        {
            Undefined = 0x00000000,
            Fifo = 0x00000001,
            FifoRelaxed = 0x00000002,
            Immediate = 0x00000003,
            Mailbox = 0x00000004,
        }

        internal enum WGPUCompositeAlphaMode
        {
            Auto = 0x00000000,
            Opaque = 0x00000001,
            Premultiplied = 0x00000002,
            Unpremultiplied = 0x00000003,
            Inherit = 0x00000004,
        }

        internal const int WGPUSType_SurfaceSourceWindowsHWND = 0x00000005;
        internal const int WGPUSType_SurfaceSourceMetalLayer = 0x00000004;
        internal const int WGPUSType_SurfaceSourceXlibWindow = 0x00000006;
        internal const int WGPUSType_SurfaceSourceWaylandSurface = 0x00000007;
        internal const int WGPUSType_SurfaceSourceAndroidNativeWindow = 0x00000008;

        [StructLayout(LayoutKind.Sequential)]
        internal struct WGPUSurfaceDescriptor
        {
            public WGPUChainedStruct* nextInChain;
            public WGPUStringView label;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct WGPUSurfaceSourceWindowsHWND
        {
            public WGPUChainedStruct chain; // chain.sType = WGPUSType_SurfaceSourceWindowsHWND
            public void* hinstance;
            public void* hwnd;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct WGPUSurfaceSourceMetalLayer
        {
            public WGPUChainedStruct chain; // chain.sType = WGPUSType_SurfaceSourceMetalLayer
            public void* layer;             // a CAMetalLayer*
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct WGPUSurfaceSourceAndroidNativeWindow
        {
            public WGPUChainedStruct chain; // chain.sType = WGPUSType_SurfaceSourceAndroidNativeWindow
            public void* window;            // an ANativeWindow*
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct WGPUSurfaceSourceXlibWindow
        {
            public WGPUChainedStruct chain; // chain.sType = WGPUSType_SurfaceSourceXlibWindow
            public void* display;           // a Display*
            public ulong window;            // an X11 Window (XID)
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct WGPUSurfaceConfiguration
        {
            public WGPUChainedStruct* nextInChain;
            public IntPtr device; // WGPUDevice
            public WGPUTextureFormat format;
            public WGPUTextureUsage usage;
            public uint width;
            public uint height;
            public nuint viewFormatCount;
            public WGPUTextureFormat* viewFormats;
            public WGPUCompositeAlphaMode alphaMode;
            public WGPUPresentMode presentMode;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct WGPUSurfaceTexture
        {
            public WGPUChainedStruct* nextInChain;
            public IntPtr texture; // WGPUTexture (owned by caller)
            public WGPUSurfaceGetCurrentTextureStatus status;
        }

        internal enum WGPUTextureViewDimension
        {
            Undefined = 0x00000000,
            _1D = 0x00000001,
            _2D = 0x00000002,
            _2DArray = 0x00000003,
            Cube = 0x00000004,
            CubeArray = 0x00000005,
            _3D = 0x00000006,
        }

        // wgpu-native v29 ABI. Lets us render into a surface texture through a view whose format
        // differs from (but is view-compatible with) the swapchain format -- specifically a plain
        // UNORM view over an sRGB swapchain, so gamma-space pre-encoded bytes store verbatim (no
        // hardware re-encode) while the swapchain stays sRGB for correct presentation on the
        // OpenGL/ANGLE backend (a plain-UNORM swapchain there is scanned out too dark).
        [StructLayout(LayoutKind.Sequential)]
        internal struct WGPUTextureViewDescriptor
        {
            public WGPUChainedStruct* nextInChain;
            public WGPUStringView label;
            public WGPUTextureFormat format;
            public WGPUTextureViewDimension dimension;
            public uint baseMipLevel;
            public uint mipLevelCount;
            public uint baseArrayLayer;
            public uint arrayLayerCount;
            public WGPUTextureAspect aspect;
            public WGPUTextureUsage usage;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct WGPUSurfaceCapabilities
        {
            public WGPUChainedStruct* nextInChain;
            public WGPUTextureUsage usages;
            public nuint formatCount;
            public WGPUTextureFormat* formats;
            public nuint presentModeCount;
            public WGPUPresentMode* presentModes;
            public nuint alphaModeCount;
            public WGPUCompositeAlphaMode* alphaModes;
        }

        // Extern entry points: see Wgpu.Native.cs / Browser/Wgpu.Browser.cs.
    }
}
