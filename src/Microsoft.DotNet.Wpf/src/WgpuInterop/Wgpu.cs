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
        //
        // iOS cannot load a third-party dynamic library, so wgpu-native is linked STATICALLY into
        // the app executable and the imports must name "__Internal" (the main image). That also
        // gives each import a compile-time reference, without which the static linker dead-strips
        // the symbol -- see Ios/WgpuInterop.Ios.csproj.
#if WGPU_IOS
        internal const string Library = "__Internal";
#else
        internal const string Library = "wgpu_native";
#endif

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
            RG32Uint = 0x00000022,   // 2x u32 per texel — carries edge/segment data to fs_coverage/fs_stroke as a texture (GL ES 3.0 has no fragment storage buffers)
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

        // ---- Instance creation (webgpu.h + wgpu.h extension) ------------------
        //
        // WGPUInstanceDescriptor is the standard descriptor; WGPUInstanceExtras is wgpu-native's
        // chained extension, and the only field we set is `backends`. That matters on Android:
        // wgpu-core creates a native surface for EVERY backend the instance enabled, and
        // vkCreateAndroidSurfaceKHR CONNECTS the ANativeWindow to the EGL producer API and holds it.
        // With both Vulkan and GL enabled, the GL backend's eglCreateWindowSurface then fails with
        //     native_window_api_connect ... failed (already connected to another API?)  EGL_BAD_ALLOC
        // and wgpu-native turns that into a fatal Rust panic. Pinning the instance to the one backend
        // that actually has an adapter is what keeps the window free for it.
        //
        // Layouts transcribed verbatim from wgpu-native v29.0.1.1 (webgpu.h / wgpu.h). Every field is
        // present even though we only use one -- the struct is passed BY POINTER and wgpu reads it by
        // offset, so a short struct would have it reading past the end.

        internal const int WGPUSType_InstanceExtras = 0x00030004;

        // WGPUInstanceBackend is a WGPUFlags (uint64_t) bitfield; zero means "all backends".
        internal const ulong WGPUInstanceBackend_All = 0;
        internal const ulong WGPUInstanceBackend_Vulkan = 1 << 0;
        internal const ulong WGPUInstanceBackend_GL = 1 << 1;
        internal const ulong WGPUInstanceBackend_Metal = 1 << 2;
        internal const ulong WGPUInstanceBackend_DX12 = 1 << 3;
        internal const ulong WGPUInstanceBackend_BrowserWebGPU = 1 << 5;

        [StructLayout(LayoutKind.Sequential)]
        internal struct WGPUInstanceDescriptor
        {
            public WGPUChainedStruct* nextInChain;
            public nuint requiredFeatureCount;
            public int* requiredFeatures;          // WGPUInstanceFeatureName const*
            public void* requiredLimits;           // WGPUInstanceLimits const*
        }

        // WGPUNativeDisplayHandleType: which variant of the display-handle union is live.
        internal const int WGPUNativeDisplayHandleType_None = 0;
        internal const int WGPUNativeDisplayHandleType_Xlib = 1;
        internal const int WGPUNativeDisplayHandleType_Xcb = 2;
        internal const int WGPUNativeDisplayHandleType_Wayland = 3;

        [StructLayout(LayoutKind.Sequential)]
        internal struct WGPUXlibDisplayHandle { public void* display; public int screen; }

        /// <summary>The tagged union in WGPUInstanceExtras. Only the GLES backend on Wayland uses it;
        /// zero (type = None) everywhere else, but its SIZE is part of WGPUInstanceExtras' layout.
        /// The union is modelled by its largest member -- all three are pointer + optional int.</summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct WGPUNativeDisplayHandle
        {
            public int type;                       // WGPUNativeDisplayHandleType
            public WGPUXlibDisplayHandle data;     // union { xlib; xcb; wayland; }
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct WGPUInstanceExtras
        {
            public WGPUChainedStruct chain;        // chain.sType = WGPUSType_InstanceExtras
            public ulong backends;                 // WGPUInstanceBackend
            public ulong flags;                    // WGPUInstanceFlag
            public int dx12ShaderCompiler;
            public int gles3MinorVersion;
            public int glFenceBehaviour;
            public WGPUStringView dxcPath;
            public int dxcMaxShaderModel;
            public int dx12PresentationSystem;
            public byte* budgetForDeviceCreation;
            public byte* budgetForDeviceLoss;
            public WGPUNativeDisplayHandle displayHandle;
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

        internal enum WGPUAdapterType { DiscreteGPU = 1, IntegratedGPU = 2, CPU = 3, Unknown = 4 }

        internal enum WGPULogLevel { Off = 0, Error = 1, Warn = 2, Info = 3, Debug = 4, Trace = 5 }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate void WGPULogCallback(WGPULogLevel level, WGPUStringView message, IntPtr userdata);

        [StructLayout(LayoutKind.Sequential)]
        internal struct WGPUAdapterInfo
        {
            public WGPUChainedStruct* nextInChain;
            public WGPUStringView vendor;
            public WGPUStringView architecture;
            public WGPUStringView device;
            public WGPUStringView description;
            public WGPUBackendType backendType;
            public WGPUAdapterType adapterType;
            public uint vendorID;
            public uint deviceID;
            public uint subgroupMinSize;
            public uint subgroupMaxSize;
        }

        // ---- Functions -------------------------------------------------------
        // The extern entry points live in Wgpu.Native.cs (desktop DllImport into
        // wgpu-native) and Browser/Wgpu.Browser.cs (browser JS-interop bodies).
    }
}
