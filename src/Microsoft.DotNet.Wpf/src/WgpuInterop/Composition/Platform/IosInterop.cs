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

            // Anchor the drawable rather than stretching it to whatever bounds the layer currently
            // has. Same reasoning as MacInterop.CreateSurface, and for the same reason it applies
            // here: the GPU presents a drawable when it finishes, the layer's new bounds reach the
            // render server when a Core Animation transaction commits, and those are two events. For
            // one frame the layer holds a new-size drawable and the old bounds, and the default
            // kCAGravityResize closes that gap by scaling -- which on macOS was a visible stretch,
            // in the direction of the drag, for the whole gesture.
            //
            // UIKit resizes a view far less often than AppKit does, and the one case everybody sees
            // -- rotation -- is masked, because UIKit cross-fades a snapshot across it. What is NOT
            // masked is a live drag: the iPad split-view divider and Stage Manager resize the view
            // continuously, exactly like a macOS window edge.
            SetContentsGravity(uiView);

            var metalSource = new Wgpu.WGPUSurfaceSourceMetalLayer
            {
                chain = new Wgpu.WGPUChainedStruct { next = null, sType = Wgpu.WGPUSType_SurfaceSourceMetalLayer },
                layer = (void*)metalLayer,
            };
            var desc = new Wgpu.WGPUSurfaceDescriptor { nextInChain = (Wgpu.WGPUChainedStruct*)&metalSource };
            IntPtr surface = Wgpu.wgpuInstanceCreateSurface(instance, &desc);

            // Commit the view's arrival in the layer tree before anything is presented into it, for
            // the same reason MacInterop does after addSublayer: the addSubview that put this view
            // on screen has only changed our in-process layer tree so far, and a window that renders
            // one frame and then goes quiet never produces the commit that would publish it.
            FlushTransaction();
            return surface;
        }

        /// <summary>
        /// Commit the current implicit Core Animation transaction, so a just-presented drawable
        /// reaches the render server now rather than whenever UIKit next happens to commit one.
        /// </summary>
        /// <remarks>
        /// The iOS counterpart of MacInterop.FlushTransaction, and needed for the same reason: a
        /// CAMetalLayer's presented drawable is part of the layer tree, and layer-tree changes reach
        /// the render server on a transaction commit, not on present. UIKit commits one whenever it
        /// is doing anything at all, so an app that is being touched or animated never notices; one
        /// that renders and then goes quiet shows nothing.
        ///
        /// Note what this does NOT fix, so nobody credits it with more than it earns: a second window
        /// on this head still fails to appear, with both windows composing every frame (measured:
        /// 2144 and 32 drawables, two surfaces created and configured, no errors) and neither
        /// reaching the screen. Adding the flush changed nothing there. It is here because iOS was
        /// simply absent from NativePlatform.CommitPresent while needing exactly what macOS needs.
        /// </remarks>
        public static void FlushTransaction()
        {
            IntPtr cls = objc_getClass("CATransaction");
            if (cls != IntPtr.Zero) Send(cls, Sel("flush"));
        }

        /// <summary>
        /// Whether this view can be presented to at all -- i.e. whether its backing layer really is a
        /// CAMetalLayer.
        /// </summary>
        /// <remarks>
        /// The head builds two kinds of view: a Metal-backed one for a real window, and a TOUCH-ONLY
        /// one for a popup, which deliberately has no Metal layer because a popup is drawn into its
        /// owner's surface instead (UIKitWindow.Create, NativePlatform.PopupsShareOwnerSurface).
        ///
        /// The compositor decided which was which from MilTarget.IsLayered, and that is a WINDOWS
        /// notion -- it is Transparency != 0, set from WS_EX_LAYERED per-pixel opacity, and it is
        /// false for every popup on this head. So the two halves disagreed: the head made a plain
        /// CALayer and the compositor asked wgpu to build a Metal swap chain on it.
        ///
        /// wgpu checks, and the check is an assert in a function that cannot unwind, so opening any
        /// WPF popup on iOS KILLED THE PROCESS:
        ///
        ///     thread panicked at wgpu-hal-29.0.3/src/metal/surface.rs:27:9:
        ///     assertion failed: layer.isKindOfClass(CAMetalLayer::class())
        ///     panic in a function that cannot unwind
        ///
        /// Which is also why it looked like a rendering bug from outside: the screen kept showing
        /// whatever was on it, because there was no longer a process drawing to it.
        ///
        /// Asking the layer what it is settles it without a protocol flag that means something else.
        /// </remarks>
        public static bool CanPresentTo(IntPtr uiView)
        {
            if (uiView == IntPtr.Zero) return false;

            IntPtr layer = Send(uiView, Sel("layer"));
            if (layer == IntPtr.Zero) return false;

            IntPtr metalClass = objc_getClass("CAMetalLayer");
            return metalClass != IntPtr.Zero && SendBoolPtr(layer, Sel("isKindOfClass:"), metalClass);
        }

        /// <summary>Keep the view's CAMetalLayer contentsScale in sync with the screen scale.</summary>
        public static void SetContentsScale(IntPtr uiView, double scale)
        {
            if (uiView == IntPtr.Zero) return;
            IntPtr layer = Send(uiView, Sel("layer"));
            if (layer != IntPtr.Zero) SendVoidDouble(layer, Sel("setContentsScale:"), scale);
        }

        /// <summary>
        /// Anchor the layer's contents to the view's top-left corner instead of rescaling them to
        /// fill it (see CreateSurface for why).
        /// </summary>
        /// <remarks>
        /// kCAGravityTopLeft is named in the LAYER's coordinate space, and iOS puts a layer's origin
        /// at its top-left with y increasing downwards, so "top left" is the visible top left and
        /// this matches what MacInterop does. That is worth stating because the opposite is widely
        /// believed -- the inversion people run into is real, but it comes from a layer with
        /// geometryFlipped set, which a UIView's backing layer does not have.
        ///
        /// If it is nevertheless wrong, the symptom is unmistakable and so is the fix: drag the
        /// split-view divider and watch where the uncovered strip appears. Along the edge being
        /// dragged is correct; along the OPPOSITE edge, with the content sliding, means the anchor is
        /// at the bottom and this constant should be "bottomLeft".
        /// </remarks>
        private static void SetContentsGravity(IntPtr uiView)
        {
            IntPtr layer = Send(uiView, Sel("layer"));
            if (layer == IntPtr.Zero) return;

            IntPtr topLeft = NSStringFrom("topLeft");   // kCAGravityTopLeft
            if (topLeft != IntPtr.Zero) SendVoidPtr(layer, Sel("setContentsGravity:"), topLeft);
        }

        private static IntPtr NSStringFrom(string value)
        {
            IntPtr cls = objc_getClass("NSString");
            if (cls == IntPtr.Zero) return IntPtr.Zero;
            IntPtr utf8 = Marshal.StringToHGlobalAnsi(value);
            try
            {
                return SendPtrPtr(cls, Sel("stringWithUTF8String:"), utf8);
            }
            finally
            {
                Marshal.FreeHGlobal(utf8);
            }
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
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendVoidPtr(IntPtr receiver, IntPtr selector, IntPtr arg);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendPtrPtr(IntPtr receiver, IntPtr selector, IntPtr arg);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendVoidDouble(IntPtr receiver, IntPtr selector, double arg);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern double SendDouble(IntPtr receiver, IntPtr selector);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] [return: MarshalAs(UnmanagedType.I1)] private static extern bool SendBoolPtr(IntPtr receiver, IntPtr selector, IntPtr arg);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern CGRect SendRect(IntPtr receiver, IntPtr selector);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern CGRect SendRectRectPtr(IntPtr receiver, IntPtr selector, CGRect r, IntPtr view);


    }
}
