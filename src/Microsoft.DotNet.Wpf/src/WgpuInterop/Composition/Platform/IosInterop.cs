// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// iOS/UIKit counterpart of MacInterop. wgpu presents on Apple platforms through a CAMetalLayer, and
// that half is IDENTICAL to macOS -- WGPUSurfaceSourceMetalLayer over a CAMetalLayer, same
// Objective-C runtime, same objc_msgSend interop. What differs is only how the layer is obtained:
//
//   macOS  a plain NSView is not Metal-backed, so MacInterop must setWantsLayer:, create a
//          CAMetalLayer and add it as an AUTORESIZING SUBLAYER (a layer-HOSTED CAMetalLayer would
//          not track the view's size).
//   iOS    a UIView subclass overriding +layerClass to return CAMetalLayer IS Metal-backed, so its
//          own backing layer is the surface and UIKit keeps the geometry in sync for free. No
//          sublayer, no autoresizing mask, no zPosition ordering, and no window-opacity dance
//          (iOS has one opaque window; WPF popups fall back to swap-chain presents because
//          NativePlatform.SupportsLayeredWindows is false off Windows).
//
// The nativeWindow handle passed in is therefore a UIView* whose layer is already a CAMetalLayer.
//

using System;
using System.Runtime.InteropServices;

namespace Microsoft.Wpf.Interop.WebGpu.Composition.Platform
{
    internal static unsafe class IosInterop
    {
        /// <summary>
        /// Create a wgpu Metal surface for a UIView* whose backing layer is a CAMetalLayer
        /// (i.e. a view whose +layerClass returns CAMetalLayer). Main-thread only, like all UIKit.
        /// </summary>
        public static IntPtr CreateSurface(IntPtr instance, IntPtr uiView)
        {
            if (uiView == IntPtr.Zero) return IntPtr.Zero;

            IntPtr metalLayer = Send(uiView, Sel("layer"));
            if (metalLayer == IntPtr.Zero) return IntPtr.Zero;

            // Map the device-pixel drawable 1:1 onto the point-sized view, exactly as on macOS:
            // drawableSize(3N px) == bounds(N pt) * contentsScale(3) on a @3x phone. WPF renders its
            // DIP scene into that larger pixel target via HwndTarget._worldTransform.
            SetContentsScale(uiView, BackingScale(uiView));

            var metalSource = new Wgpu.WGPUSurfaceSourceMetalLayer
            {
                chain = new Wgpu.WGPUChainedStruct { next = null, sType = Wgpu.WGPUSType_SurfaceSourceMetalLayer },
                layer = (void*)metalLayer,
            };
            var desc = new Wgpu.WGPUSurfaceDescriptor { nextInChain = (Wgpu.WGPUChainedStruct*)&metalSource };
            return Wgpu.wgpuInstanceCreateSurface(instance, &desc);
        }

        /// <summary>Keep the view's CAMetalLayer contentsScale in sync with the screen scale.</summary>
        public static void SetContentsScale(IntPtr uiView, double scale)
        {
            if (uiView == IntPtr.Zero) return;
            IntPtr layer = Send(uiView, Sel("layer"));
            if (layer != IntPtr.Zero) SendVoidDouble(layer, Sel("setContentsScale:"), scale);
        }

        /// <summary>
        /// The view's screen scale. Prefer the view's own UIScreen (iPad external display / Stage
        /// Manager can differ from the main screen); fall back to UIScreen.mainScreen.
        /// nativeScale, not scale: it is the true pixel ratio on phones that render-and-downsample.
        /// </summary>
        internal static double BackingScale(IntPtr uiView)
        {
            IntPtr screen = IntPtr.Zero;

            if (uiView != IntPtr.Zero)
            {
                IntPtr window = Send(uiView, Sel("window"));
                if (window != IntPtr.Zero)
                    screen = Send(window, Sel("screen"));
            }

            if (screen == IntPtr.Zero)
                screen = Send(objc_getClass("UIScreen"), Sel("mainScreen"));

            if (screen == IntPtr.Zero) return 1.0;

            double scale = SendDouble(screen, Sel("nativeScale"));
            return scale > 0 ? scale : 1.0;
        }

        /// <summary>
        /// The view's top-left corner in device pixels within the app's UIWindow. Needed when a popup
        /// is drawn into its owner's surface rather than its own (NativePlatform.PopupsShareOwnerSurface):
        /// the popup's scene has to be translated to where the popup sits. Converting to a nil view
        /// gives window coordinates, which is the same space the owner's own view starts from.
        /// </summary>
        public static void GetWindowOrigin(IntPtr uiView, out int x, out int y)
        {
            x = y = 0;
            if (uiView == IntPtr.Zero) return;

            CGRect bounds = SendRect(uiView, Sel("bounds"));
            CGRect inWindow = SendRectRectPtr(uiView, Sel("convertRect:toView:"), bounds, IntPtr.Zero);
            double scale = BackingScale(uiView);
            x = (int)Math.Round(inWindow.x * scale);
            y = (int)Math.Round(inWindow.y * scale);
        }

        // ---- Objective-C runtime (same as MacInterop; UIKit and AppKit share the runtime) -------

        [StructLayout(LayoutKind.Sequential)]
        private struct CGRect { public double x, y, width, height; }

        private const string ObjC = "/usr/lib/libobjc.dylib";

        private static IntPtr Sel(string name) => sel_registerName(name);

        [DllImport(ObjC)] private static extern IntPtr objc_getClass(string name);
        [DllImport(ObjC)] private static extern IntPtr sel_registerName(string name);

        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr receiver, IntPtr selector);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendVoidDouble(IntPtr receiver, IntPtr selector, double arg);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern double SendDouble(IntPtr receiver, IntPtr selector);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern CGRect SendRect(IntPtr receiver, IntPtr selector);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern CGRect SendRectRectPtr(IntPtr receiver, IntPtr selector, CGRect r, IntPtr view);


    }
}
