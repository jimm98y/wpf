// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// macOS backend for NativePlatform: the only file in the engine that talks to the
// Objective-C runtime / Cocoa. wgpu presents on macOS through a CAMetalLayer, so
// given a host's NSView* we make the view layer-backed, install a CAMetalLayer,
// and hand that layer to wgpu via WGPUSurfaceSourceMetalLayer.
//
// P/Invoke targets (libobjc, QuartzCore, libSystem) are resolved lazily at call
// time, so this file compiles and ships on every platform; the dylibs only load
// when actually running on macOS. NOTE: this path is authored against the standard
// wgpu/Cocoa idiom but has not been run on a Mac from this dev box (Windows/arm64);
// it is the structural macOS enablement, ready for on-device validation.
//

using System;
using System.Runtime.InteropServices;

namespace Microsoft.Wpf.Interop.WebGpu.Composition.Platform
{
    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    internal static unsafe class MacInterop
    {
        /// <summary>
        /// Create a wgpu Metal surface for an NSView*. Makes the view layer-backed and
        /// installs a CAMetalLayer, then wraps that layer in WGPUSurfaceSourceMetalLayer.
        /// Must be called on the main (UI) thread, like all AppKit view mutation.
        /// </summary>
        public static IntPtr CreateSurface(IntPtr instance, IntPtr nsView)
        {
            if (nsView == IntPtr.Zero) return IntPtr.Zero;

            IntPtr metalLayer = CreateMetalLayer();
            if (metalLayer == IntPtr.Zero) return IntPtr.Zero;

            // view.wantsLayer = YES; view.layer = metalLayer;  (layer-back the NSView)
            SendVoidBool(nsView, Sel("setWantsLayer:"), true);
            SendVoidPtr(nsView, Sel("setLayer:"), metalLayer);

            // Pin the layer to 1:1 device scale so its drawable size equals the view's point size.
            // WPF composes at point size (DpiScale 1.0 off-Windows), and the wgpu surface is
            // configured to that same size; on a Retina display the layer would otherwise default
            // to 2x, so wgpuSurfaceGetCurrentTexture would report Outdated every frame.
            SendVoidDouble(metalLayer, Sel("setContentsScale:"), 1.0);

            var metalSource = new Wgpu.WGPUSurfaceSourceMetalLayer
            {
                chain = new Wgpu.WGPUChainedStruct { next = null, sType = Wgpu.WGPUSType_SurfaceSourceMetalLayer },
                layer = (void*)metalLayer,
            };
            var desc = new Wgpu.WGPUSurfaceDescriptor { nextInChain = (Wgpu.WGPUChainedStruct*)&metalSource };
            return Wgpu.wgpuInstanceCreateSurface(instance, &desc);
        }

        // [[CAMetalLayer layer] retain] -- a fresh, owned CAMetalLayer. We retain it so it
        // outlives the autorelease pool; setLayer: also retains it on the view.
        private static IntPtr CreateMetalLayer()
        {
            IntPtr cls = objc_getClass("CAMetalLayer");
            if (cls == IntPtr.Zero)
            {
                // QuartzCore may not be loaded yet in a pure host; pull it in and retry.
                dlopen("/System/Library/Frameworks/QuartzCore.framework/QuartzCore", RTLD_NOW);
                cls = objc_getClass("CAMetalLayer");
                if (cls == IntPtr.Zero) return IntPtr.Zero;
            }
            IntPtr layer = Send(cls, Sel("layer"));
            if (layer != IntPtr.Zero) Send(layer, Sel("retain"));
            return layer;
        }

        // ---- Objective-C runtime -----------------------------------------------------

        private const string ObjC = "/usr/lib/libobjc.A.dylib";
        private const int RTLD_NOW = 2;

        private static IntPtr Sel(string name) => sel_registerName(name);

        [DllImport(ObjC)] private static extern IntPtr objc_getClass(string name);
        [DllImport(ObjC)] private static extern IntPtr sel_registerName(string name);

        // objc_msgSend is variadic in C; declare one typed alias per call shape we use.
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr receiver, IntPtr selector);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendVoidPtr(IntPtr receiver, IntPtr selector, IntPtr arg);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendVoidBool(IntPtr receiver, IntPtr selector, [MarshalAs(UnmanagedType.I1)] bool arg);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendVoidDouble(IntPtr receiver, IntPtr selector, double arg);

        [DllImport("/usr/lib/libSystem.dylib")] private static extern IntPtr dlopen(string path, int mode);
    }
}
