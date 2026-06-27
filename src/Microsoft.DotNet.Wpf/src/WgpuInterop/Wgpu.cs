// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Minimal P/Invoke binding over the WebGPU C ABI (webgpu.h) as implemented by
// wgpu-native. This is the cross-platform graphics seam that will replace the
// native milcore (wpfgfx) Direct3D backend. See the plan: keep the managed
// DUCE command protocol, replace what sits below the channel with a wgpu-backed
// composition engine.
//
// ABI source of truth: wgpu-native v29.0.1.1 (webgpu.h / wgpu.h). The struct
// layouts, enum values and flag widths below are transcribed verbatim from
// those headers. Do NOT "tidy" field order or types -- they are an ABI contract.
//
// Scope: this file binds only the surface needed for a headless smoke test
// (instance -> adapter -> device/queue -> texture -> render-pass clear ->
// copy-to-buffer -> map -> readback). The binding is expanded as the engine
// grows; it is intentionally hand-written and version-pinned rather than
// generated, so the cross-platform seam stays auditable.
//

using System;
using System.Runtime.InteropServices;

namespace Microsoft.Wpf.Interop.WebGpu
{
    /// <summary>
    /// Native entry points. wgpu-native exposes the standard <c>webgpu.h</c> C ABI
    /// plus the <c>wgpu.h</c> extensions (e.g. <see cref="wgpuDevicePoll"/>). The same
    /// signatures bind to Dawn, so the managed layer is portable between impls.
    /// </summary>
    internal static unsafe partial class Wgpu
    {
        // The wgpu-native shared library. Resolved next to the managed assembly
        // (copied there by the build) or via the OS loader search path.
        internal const string Library = "wgpu_native";

        // ---- Opaque handles ---------------------------------------------------
        // Every WGPU* object in webgpu.h is `typedef struct WGPU*Impl* WGPU*`,
        // i.e. an opaque pointer. We model them all as IntPtr.

        // ---- Scalar typedefs --------------------------------------------------
        // typedef uint32_t WGPUBool;   typedef uint64_t WGPUFlags;
        internal const uint WGPU_TRUE = 1;
        internal const uint WGPU_FALSE = 0;
        internal const uint WGPU_COPY_STRIDE_UNDEFINED = 0xFFFFFFFFu;
        internal const uint WGPU_MIP_LEVEL_COUNT_UNDEFINED = 0xFFFFFFFFu;
        internal const uint WGPU_ARRAY_LAYER_COUNT_UNDEFINED = 0xFFFFFFFFu;
        internal const uint WGPU_DEPTH_SLICE_UNDEFINED = 0xFFFFFFFFu;
        internal const uint WGPU_QUERY_SET_INDEX_UNDEFINED = 0xFFFFFFFFu;

        // ---- Enums (C int, 32-bit) -------------------------------------------

        internal enum WGPUCallbackMode
        {
            WaitAnyOnly = 0x00000001,
            AllowProcessEvents = 0x00000002,
            AllowSpontaneous = 0x00000003,
        }

        internal enum WGPURequestAdapterStatus
        {
            Success = 0x00000001,
            CallbackCancelled = 0x00000002,
            Unavailable = 0x00000003,
            Error = 0x00000004,
        }

        internal enum WGPURequestDeviceStatus
        {
            Success = 0x00000001,
            CallbackCancelled = 0x00000002,
            Error = 0x00000003,
        }

        internal enum WGPUMapAsyncStatus
        {
            Success = 0x00000001,
            CallbackCancelled = 0x00000002,
            Error = 0x00000003,
            Aborted = 0x00000004,
        }

        internal enum WGPUPowerPreference
        {
            Undefined = 0x00000000,
            LowPower = 0x00000001,
            HighPerformance = 0x00000002,
        }

        internal enum WGPUFeatureLevel
        {
            Undefined = 0x00000000,
            Compatibility = 0x00000001,
            Core = 0x00000002,
        }

        internal enum WGPUBackendType
        {
            Undefined = 0x00000000,
            Null = 0x00000001,
            WebGPU = 0x00000002,
            D3D11 = 0x00000003,
            D3D12 = 0x00000004,
            Metal = 0x00000005,
            Vulkan = 0x00000006,
            OpenGL = 0x00000007,
            OpenGLES = 0x00000008,
        }

        internal enum WGPUTextureDimension
        {
            Undefined = 0x00000000,
            _1D = 0x00000001,
            _2D = 0x00000002,
            _3D = 0x00000003,
        }

        internal enum WGPUTextureAspect
        {
            Undefined = 0x00000000,
            All = 0x00000001,
            StencilOnly = 0x00000002,
            DepthOnly = 0x00000003,
        }

        internal enum WGPULoadOp
        {
            Undefined = 0x00000000,
            Load = 0x00000001,
            Clear = 0x00000002,
        }

        internal enum WGPUStoreOp
        {
            Undefined = 0x00000000,
            Store = 0x00000001,
            Discard = 0x00000002,
        }

        // WGPUTextureFormat is a large enum; only the value(s) we use today.
        internal enum WGPUTextureFormat
        {
            Undefined = 0x00000000,
            R8Unorm = 0x00000001,
            RGBA8Unorm = 0x00000016,
            RGBA8UnormSrgb = 0x00000017,
            BGRA8Unorm = 0x0000001B,
            BGRA8UnormSrgb = 0x0000001C,
            Depth24Plus = 0x0000002E,
        }

        // ---- Flag typedefs (WGPUFlags = uint64_t) ----------------------------

        [Flags]
        internal enum WGPUTextureUsage : ulong
        {
            None = 0,
            CopySrc = 0x1,
            CopyDst = 0x2,
            TextureBinding = 0x4,
            StorageBinding = 0x8,
            RenderAttachment = 0x10,
        }

        [Flags]
        internal enum WGPUBufferUsage : ulong
        {
            None = 0,
            MapRead = 0x1,
            MapWrite = 0x2,
            CopySrc = 0x4,
            CopyDst = 0x8,
            Index = 0x10,
            Vertex = 0x20,
            Uniform = 0x40,
            Storage = 0x80,
            Indirect = 0x100,
            QueryResolve = 0x200,
        }

        [Flags]
        internal enum WGPUMapMode : ulong
        {
            None = 0,
            Read = 0x1,
            Write = 0x2,
        }

        // ---- Structs (exact layout from webgpu.h) ----------------------------

        [StructLayout(LayoutKind.Sequential)]
        internal struct WGPUStringView
        {
            public byte* data;   // char const*
            public nuint length; // size_t
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct WGPUFuture
        {
            public ulong id;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct WGPUExtent3D
        {
            public uint width;
            public uint height;
            public uint depthOrArrayLayers;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct WGPUOrigin3D
        {
            public uint x;
            public uint y;
            public uint z;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct WGPUColor
        {
            public double r;
            public double g;
            public double b;
            public double a;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct WGPURequestAdapterOptions
        {
            public WGPUChainedStruct* nextInChain;
            public WGPUFeatureLevel featureLevel;
            public WGPUPowerPreference powerPreference;
            public uint forceFallbackAdapter; // WGPUBool
            public WGPUBackendType backendType;
            public IntPtr compatibleSurface; // WGPUSurface
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct WGPUChainedStruct
        {
            public WGPUChainedStruct* next;
            public int sType; // WGPUSType
        }

        // Callback-info structs are passed BY VALUE to the request/map functions.
        // The `callback` field is a C function pointer; we keep the managed
        // delegate alive separately and pass its function pointer here.
        [StructLayout(LayoutKind.Sequential)]
        internal struct WGPURequestAdapterCallbackInfo
        {
            public WGPUChainedStruct* nextInChain;
            public WGPUCallbackMode mode;
            public IntPtr callback;  // WGPURequestAdapterCallback
            public IntPtr userdata1;
            public IntPtr userdata2;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct WGPURequestDeviceCallbackInfo
        {
            public WGPUChainedStruct* nextInChain;
            public WGPUCallbackMode mode;
            public IntPtr callback;  // WGPURequestDeviceCallback
            public IntPtr userdata1;
            public IntPtr userdata2;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct WGPUBufferMapCallbackInfo
        {
            public WGPUChainedStruct* nextInChain;
            public WGPUCallbackMode mode;
            public IntPtr callback;  // WGPUBufferMapCallback
            public IntPtr userdata1;
            public IntPtr userdata2;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct WGPUTextureDescriptor
        {
            public WGPUChainedStruct* nextInChain;
            public WGPUStringView label;
            public WGPUTextureUsage usage;
            public WGPUTextureDimension dimension;
            public WGPUExtent3D size;
            public WGPUTextureFormat format;
            public uint mipLevelCount;
            public uint sampleCount;
            public nuint viewFormatCount;
            public WGPUTextureFormat* viewFormats;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct WGPUBufferDescriptor
        {
            public WGPUChainedStruct* nextInChain;
            public WGPUStringView label;
            public WGPUBufferUsage usage;
            public ulong size;
            public uint mappedAtCreation; // WGPUBool
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct WGPURenderPassColorAttachment
        {
            public WGPUChainedStruct* nextInChain;
            public IntPtr view;          // WGPUTextureView
            public uint depthSlice;
            public IntPtr resolveTarget;  // WGPUTextureView
            public WGPULoadOp loadOp;
            public WGPUStoreOp storeOp;
            public WGPUColor clearValue;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct WGPURenderPassDescriptor
        {
            public WGPUChainedStruct* nextInChain;
            public WGPUStringView label;
            public nuint colorAttachmentCount;
            public WGPURenderPassColorAttachment* colorAttachments;
            public IntPtr depthStencilAttachment;
            public IntPtr occlusionQuerySet;
            public IntPtr timestampWrites;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct WGPUTexelCopyBufferLayout
        {
            public ulong offset;
            public uint bytesPerRow;
            public uint rowsPerImage;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct WGPUTexelCopyTextureInfo
        {
            public IntPtr texture; // WGPUTexture
            public uint mipLevel;
            public WGPUOrigin3D origin;
            public WGPUTextureAspect aspect;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct WGPUTexelCopyBufferInfo
        {
            public WGPUTexelCopyBufferLayout layout;
            public IntPtr buffer; // WGPUBuffer
        }

        // ---- Callback delegate types -----------------------------------------
        // The WebGPU C ABI uses the platform default C calling convention.

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate void WGPURequestAdapterCallback(
            WGPURequestAdapterStatus status, IntPtr adapter, WGPUStringView message, IntPtr userdata1, IntPtr userdata2);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate void WGPURequestDeviceCallback(
            WGPURequestDeviceStatus status, IntPtr device, WGPUStringView message, IntPtr userdata1, IntPtr userdata2);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate void WGPUBufferMapCallback(
            WGPUMapAsyncStatus status, WGPUStringView message, IntPtr userdata1, IntPtr userdata2);

        // ---- Functions: webgpu.h ---------------------------------------------

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

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr wgpuDeviceCreateTexture(IntPtr device, WGPUTextureDescriptor* descriptor);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr wgpuDeviceCreateBuffer(IntPtr device, WGPUBufferDescriptor* descriptor);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr wgpuDeviceCreateCommandEncoder(IntPtr device, IntPtr descriptor);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr wgpuTextureCreateView(IntPtr texture, IntPtr descriptor);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr wgpuCommandEncoderBeginRenderPass(IntPtr commandEncoder, WGPURenderPassDescriptor* descriptor);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void wgpuRenderPassEncoderEnd(IntPtr renderPassEncoder);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void wgpuCommandEncoderCopyTextureToBuffer(
            IntPtr commandEncoder, WGPUTexelCopyTextureInfo* source, WGPUTexelCopyBufferInfo* destination, WGPUExtent3D* copySize);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr wgpuCommandEncoderFinish(IntPtr commandEncoder, IntPtr descriptor);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void wgpuQueueSubmit(IntPtr queue, nuint commandCount, IntPtr* commands);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern WGPUFuture wgpuBufferMapAsync(
            IntPtr buffer, WGPUMapMode mode, nuint offset, nuint size, WGPUBufferMapCallbackInfo callbackInfo);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void* wgpuBufferGetConstMappedRange(IntPtr buffer, nuint offset, nuint size);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void wgpuBufferUnmap(IntPtr buffer);

        // ---- Functions: wgpu.h extensions ------------------------------------

        // Drives the device's internal event/callback queue. wait=true blocks
        // until the optional submission index has completed.
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern uint wgpuDevicePoll(IntPtr device, uint wait, ulong* submissionIndex);
    }
}
