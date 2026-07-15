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

            // Keep the NSView layer-BACKED (AppKit owns/manages the root layer) and add the
            // CAMetalLayer as a SUBLAYER, rather than layer-HOSTING it via setLayer:. AppKit
            // keeps a backing layer's geometry in sync with the view, but never resizes a hosted
            // layer -- so a hosted CAMetalLayer would keep its initial frame while the view grows
            // or shrinks, and Core Animation would stretch the fixed drawable to the new bounds
            // (content over-stretched + cropped when enlarging, black margin when shrinking).
            // As an autoresizing sublayer its frame tracks the view's bounds on every resize.
            SendVoidBool(nsView, Sel("setWantsLayer:"), true);
            IntPtr rootLayer = Send(nsView, Sel("layer"));

            // Match the metal layer to the current view bounds, then let Core Animation grow/shrink
            // it with the superlayer (WidthSizable | HeightSizable, no margins => fills exactly).
            NSRect bounds = SendRect(rootLayer, Sel("bounds"));
            SendVoidRect(metalLayer, Sel("setFrame:"), bounds);
            SendVoidNUInt(metalLayer, Sel("setAutoresizingMask:"), kCALayerWidthSizable | kCALayerHeightSizable);
            SendVoidPtr(rootLayer, Sel("addSublayer:"), metalLayer);

            // Set the layer's contents scale to the backing scale so its drawable (which wgpu sizes to
            // the surface config = the view's DEVICE-PIXEL size) maps 1:1 onto the point-sized frame:
            // drawableSize(2N px) == frame(N pt) * contentsScale(2) on Retina. WPF renders its DIP scene
            // into that larger pixel target via HwndTarget._worldTransform (= the same backing scale),
            // so the result is crisp. Must match CocoaWindow.GetBackingScale / the HwndTarget DPI scale.
            SendVoidDouble(metalLayer, Sel("setContentsScale:"), BackingScale(nsView));

            var metalSource = new Wgpu.WGPUSurfaceSourceMetalLayer
            {
                chain = new Wgpu.WGPUChainedStruct { next = null, sType = Wgpu.WGPUSType_SurfaceSourceMetalLayer },
                layer = (void*)metalLayer,
            };
            var desc = new Wgpu.WGPUSurfaceDescriptor { nextInChain = (Wgpu.WGPUChainedStruct*)&metalSource };
            return Wgpu.wgpuInstanceCreateSurface(instance, &desc);
        }

        // The view's backing scale factor (2.0 on Retina). Sourced from the window's screen (falling
        // back to the main screen), mirroring CocoaWindow.GetBackingScale so the CAMetalLayer's
        // contentsScale agrees with the HwndTarget DPI scale and the device-pixel client rects.
        // WPF_MAC_FORCE_SCALE overrides it (to exercise the Retina path on a 1x display).
        internal static double BackingScale(IntPtr nsView)
        {
            string force = Environment.GetEnvironmentVariable("WPF_MAC_FORCE_SCALE");
            if (!string.IsNullOrEmpty(force) &&
                double.TryParse(force, System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out double forced) &&
                forced > 0)
            {
                return forced;
            }

            double scale = 0;
            IntPtr window = nsView != IntPtr.Zero ? Send(nsView, Sel("window")) : IntPtr.Zero;
            if (window != IntPtr.Zero)
            {
                IntPtr screen = Send(window, Sel("screen"));
                if (screen != IntPtr.Zero) scale = SendDouble(screen, Sel("backingScaleFactor"));
                if (scale <= 0) scale = SendDouble(window, Sel("backingScaleFactor"));
            }
            if (scale <= 0)
            {
                IntPtr mainScreen = Send(objc_getClass("NSScreen"), Sel("mainScreen"));
                if (mainScreen != IntPtr.Zero) scale = SendDouble(mainScreen, Sel("backingScaleFactor"));
            }
            return scale > 0 ? scale : 1.0;
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
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern double SendDouble(IntPtr receiver, IntPtr selector);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendVoidNUInt(IntPtr receiver, IntPtr selector, nuint arg);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern NSRect SendRect(IntPtr receiver, IntPtr selector);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendVoidRect(IntPtr receiver, IntPtr selector, NSRect arg);

        [DllImport("/usr/lib/libSystem.dylib")] private static extern IntPtr dlopen(string path, int mode);

        [StructLayout(LayoutKind.Sequential)]
        private struct NSRect
        {
            public double x;
            public double y;
            public double width;
            public double height;
        }

        // CALayerAutoresizingMask: sublayer stays sized to fill its superlayer's bounds.
        private const nuint kCALayerWidthSizable = 1 << 1;
        private const nuint kCALayerHeightSizable = 1 << 4;
    }
}
