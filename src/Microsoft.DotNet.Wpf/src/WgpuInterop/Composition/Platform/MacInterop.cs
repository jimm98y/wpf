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
        private static readonly System.Collections.Generic.Dictionary<IntPtr, IntPtr> s_metalLayers = new();

        /// <summary>
        /// Commit the current implicit Core Animation transaction so a just-presented CAMetalLayer
        /// drawable reaches the window server NOW. The CAMetalLayer is an autoresizing SUBLAYER, whose
        /// contents/layout updates ride an implicit CATransaction that is normally committed at the end of
        /// a run-loop iteration. A window that renders a SINGLE frame and then goes idle (no animation,
        /// caret, or input — e.g. a small static tool window) posts no further run-loop work, so its one
        /// present never commits and the window shows blank until an unrelated relayout (a resize) forces a
        /// transaction. Flushing right after present makes the frame appear immediately regardless.
        /// </summary>
        public static void FlushTransaction()
        {
            Send(objc_getClass("CATransaction"), Sel("flush"));
            if (s_traceLayer) TraceLayer();
        }

        // ---- diagnostic: WPF_MAC_PRESLAYER=1 -------------------------------------------------
        //
        // Samples the PRESENTATION layer (what the window server is compositing right now) beside the
        // model layer (the value we assigned) after every present. The earlier geometry trace read
        // only the model layer, which by construction always agrees with what was just assigned.

        private static readonly bool s_traceLayer =
            Environment.GetEnvironmentVariable("WPF_MAC_PRESLAYER") == "1";

        private static void TraceLayer()
        {
            foreach (var pair in s_metalLayers)
            {
                IntPtr view = pair.Key, layer = pair.Value;
                if (view == IntPtr.Zero || layer == IntPtr.Zero) continue;

                NSRect model = SendRect(layer, Sel("bounds"));
                IntPtr presentation = Send(layer, Sel("presentationLayer"));
                NSRect shown = presentation == IntPtr.Zero ? model : SendRect(presentation, Sel("bounds"));
                NSRect viewBounds = SendRect(view, Sel("bounds"));

                IntPtr window = Send(view, Sel("window"));
                bool live = window != IntPtr.Zero && SendBool(window, Sel("inLiveResize"));
                IntPtr anims = Send(layer, Sel("animationKeys"));
                nuint animCount = anims == IntPtr.Zero ? 0 : SendNUInt(anims, Sel("count"));

                Console.WriteLine(
                    $"PRESLAYER view={viewBounds.width}x{viewBounds.height} " +
                    $"model={model.width}x{model.height} shown={shown.width}x{shown.height} " +
                    $"live={(live ? 1 : 0)} anims={animCount}");
            }
        }

        /// <summary>Keep the view's CAMetalLayer contentsScale in sync with the (runtime-detected)
        /// backing scale so the drawable maps 1:1 to the display — e.g. when the window moves to a
        /// different-DPI screen. Call together with resizing the wgpu surface to the new device size.</summary>
        public static void SetContentsScale(IntPtr nsView, double scale)
        {
            if (nsView != IntPtr.Zero && s_metalLayers.TryGetValue(nsView, out IntPtr metalLayer) && metalLayer != IntPtr.Zero)
                SendVoidDouble(metalLayer, Sel("setContentsScale:"), scale);
        }

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

            // Pin the metal content ABOVE any sibling sublayers (e.g. a Mica NSVisualEffectView added as a
            // sibling behind the content). Sublayer array order alone is fragile: when the system appearance
            // toggles (Dark/Light), AppKit relayouts the view and can reorder the effect view above the metal
            // layer, which would occlude the whole WPF scene (window goes "blank"). A higher zPosition keeps
            // the drawable in front regardless of sublayer order.
            SendVoidDouble(metalLayer, Sel("setZPosition:"), 1.0);

            // Set the layer's contents scale to the backing scale so its drawable (which wgpu sizes to
            // the surface config = the view's DEVICE-PIXEL size) maps 1:1 onto the point-sized frame:
            // drawableSize(2N px) == frame(N pt) * contentsScale(2) on Retina. WPF renders its DIP scene
            // into that larger pixel target via HwndTarget._worldTransform (= the same backing scale),
            // so the result is crisp. Must match CocoaWindow.GetBackingScale / the HwndTarget DPI scale.
            SendVoidDouble(metalLayer, Sel("setContentsScale:"), BackingScale(nsView));
            s_metalLayers[nsView] = metalLayer;   // remembered so contentsScale can track DPI at runtime

            // Anchor the drawable to the top-left instead of stretching it to whatever the layer's
            // bounds currently are. This is the resize drift, and it is a mismatch that cannot be
            // designed away: a drawable is presented by the GPU when it finishes, the layer's bounds
            // reach the window server when a Core Animation transaction commits, and the two are not
            // the same event. Measured during a live drag -- the presentation layer, which is what is
            // actually being composited, trails the bounds we assigned by one drag step every single
            // frame (a 2702-wide drawable shown in a 2547-wide layer). With the default kCAGravityResize
            // the server rescales the drawable to close that gap, so the content stretches by the
            // amount of the last mouse movement and snaps back when the transaction lands. Anchored,
            // the same gap costs an uncovered strip along the edge being dragged for one frame, which
            // is what every native Cocoa application does during a live resize.
            //
            // NOTE this was tried once before and reverted because it rendered the app into the
            // top-left quadrant. That was not this setting: contentsScale was 2 while the drawable was
            // sized 1x (see PhysicalDisplayScale), so the drawable really did cover a quarter of the
            // layer -- with resize gravity it was being scaled up to fit and merely looked blurry, and
            // anchoring it only stopped hiding the other bug. That one is fixed; this one needs it.
            IntPtr topLeft = NSStringFrom("topLeft");   // kCAGravityTopLeft
            if (topLeft != IntPtr.Zero) SendVoidPtr(metalLayer, Sel("setContentsGravity:"), topLeft);

            // Match the CAMetalLayer's opacity to the hosting window's. Popup windows (menus, ComboBox,
            // ToolTip) are created non-opaque (CocoaWindow) so their drop shadow / rounded corners can
            // composite over the content behind them; the layer must also be non-opaque or Core Animation
            // fills the untouched (alpha-0) pixels black/white. Normal windows stay opaque (faster).
            IntPtr hostWindow = Send(nsView, Sel("window"));
            bool windowOpaque = hostWindow == IntPtr.Zero || SendBool(hostWindow, Sel("isOpaque"));
            SendVoidBool(metalLayer, Sel("setOpaque:"), windowOpaque);

            // Commit the layer-tree change (addSublayer) to the render server NOW, synchronously. addSublayer:
            // only mutates our in-process layer tree; the structural change reaches the window server on the next
            // main-thread CA transaction commit. If the window is static (renders one frame then parks the
            // dispatcher), no such commit fires before the first present -- so wgpu's presentDrawable: targets a
            // layer the server has never instantiated and nothing appears until an unrelated relayout (a resize)
            // finally commits it. That's the intermittent "blank until you resize" on static windows. Flushing
            // here guarantees the server knows the CAMetalLayer before any drawable is presented into it.
            Send(objc_getClass("CATransaction"), Sel("flush"));

            var metalSource = new Wgpu.WGPUSurfaceSourceMetalLayer
            {
                chain = new Wgpu.WGPUChainedStruct { next = null, sType = Wgpu.WGPUSType_SurfaceSourceMetalLayer },
                layer = (void*)metalLayer,
            };
            var desc = new Wgpu.WGPUSurfaceDescriptor { nextInChain = (Wgpu.WGPUChainedStruct*)&metalSource };
            return Wgpu.wgpuInstanceCreateSurface(instance, &desc);
        }

        /// <summary>
        /// Whether the NSView's host window is opaque. A window turned non-opaque for a translucent
        /// backdrop (Mica substitute / layered popup) needs its swap-chain surface configured with an
        /// alpha mode and a transparent clear so the material behind shows through. Treats a view with
        /// no window as opaque.
        /// </summary>
        public static bool IsWindowOpaque(IntPtr nsView)
        {
            if (nsView == IntPtr.Zero) return true;
            IntPtr window = Send(nsView, Sel("window"));
            return window == IntPtr.Zero || SendBool(window, Sel("isOpaque"));
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
                // NSWindow.backingScaleFactor is the canonical, reliable value for a shown window;
                // window.screen.backingScaleFactor can be transiently wrong (0/stale) while the
                // window's screen association settles, which would render 1x on a Retina display and
                // upscale to a pixelated result. Prefer the window, fall back to its screen.
                scale = SendDouble(window, Sel("backingScaleFactor"));
                if (scale <= 0)
                {
                    IntPtr screen = Send(window, Sel("screen"));
                    if (screen != IntPtr.Zero) scale = SendDouble(screen, Sel("backingScaleFactor"));
                }
            }
            if (scale <= 0)
            {
                IntPtr mainScreen = Send(objc_getClass("NSScreen"), Sel("mainScreen"));
                if (mainScreen != IntPtr.Zero) scale = SendDouble(mainScreen, Sel("backingScaleFactor"));
            }
            if (scale <= 0) scale = 1.0;

            // A non-bundled binary (dotnet X.dll) isn't always registered as HiDPI-capable, so AppKit
            // reports backingScaleFactor 1 even on a Retina panel — which would render 1x and upscale to
            // thin, blurry text. CAMetalLayer renders at native resolution regardless of the window's
            // backing, so when AppKit claims 1x, cross-check the physical panel via CoreGraphics (works
            // independent of HiDPI-awareness) and prefer 2x on an actually-Retina display.
            if (scale < 1.5 && PhysicalDisplayScale(window) >= 1.5)
                scale = 2.0;
            return scale;
        }

        // Native-pixels / point ("looks like") ratio of the display a WINDOW is on: ~2 on a Retina
        // panel (including the scaled "More Space" modes, where it is >1.5), 1 on anything else.
        //
        // Per window, NOT the best of every active display, which is what this used to take. On a
        // Retina laptop driving a 1x external monitor that forced 2x on every window, including the
        // ones on the 1x screen: the layer was told contentsScale 2 while the drawable was sized from
        // AppKit's honest 1x, so Core Animation had a half-size image to fit a double-size backing
        // store and resampled it on every frame. Measured directly, layer 1571x494pt @2 wanting
        // 3142x988px against a 1571x494px drawable.
        //
        // Falls back to scanning every display when the window has no screen yet (not placed, or
        // off-screen), where guessing high is the safer error: 1x on a Retina panel is visibly
        // blurry, while 2x on a 1x panel only costs fill rate.
        private static double PhysicalDisplayScale(IntPtr window)
        {
            uint displayId = window == IntPtr.Zero ? 0 : DisplayIdOfWindow(window);
            if (displayId != 0)
            {
                return ScaleOfDisplay(displayId);
            }

            var ids = new uint[16];
            if (CGGetActiveDisplayList((uint)ids.Length, ids, out uint count) != 0 || count == 0)
                return 1.0;
            double best = 1.0;
            for (uint i = 0; i < count && i < ids.Length; i++)
            {
                double scale = ScaleOfDisplay(ids[i]);
                if (scale > best) best = scale;
            }
            return best;
        }

        private static double ScaleOfDisplay(uint displayId)
        {
            IntPtr mode = CGDisplayCopyDisplayMode(displayId);
            if (mode == IntPtr.Zero) return 1.0;
            double px = CGDisplayModeGetPixelWidth(mode);
            double pt = CGDisplayModeGetWidth(mode);
            CGDisplayModeRelease(mode);
            return pt > 0 ? px / pt : 1.0;
        }

        /// <summary>
        /// The CGDirectDisplayID of the screen a window is on, from
        /// NSScreen.deviceDescription[@"NSScreenNumber"]; 0 when it has no screen yet.
        /// </summary>
        private static uint DisplayIdOfWindow(IntPtr window)
        {
            IntPtr screen = Send(window, Sel("screen"));
            if (screen == IntPtr.Zero) return 0;

            IntPtr description = Send(screen, Sel("deviceDescription"));
            if (description == IntPtr.Zero) return 0;

            IntPtr key = NSStringFrom("NSScreenNumber");
            if (key == IntPtr.Zero) return 0;

            IntPtr number = SendPtrPtr(description, Sel("objectForKey:"), key);
            return number == IntPtr.Zero ? 0 : (uint)SendNUInt(number, Sel("unsignedIntValue"));
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

        private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
        [DllImport(CoreGraphics)] private static extern int CGGetActiveDisplayList(uint maxDisplays, [Out] uint[] activeDisplays, out uint displayCount);
        [DllImport(CoreGraphics)] private static extern IntPtr CGDisplayCopyDisplayMode(uint display);
        [DllImport(CoreGraphics)] private static extern nuint CGDisplayModeGetPixelWidth(IntPtr mode);
        [DllImport(CoreGraphics)] private static extern nuint CGDisplayModeGetWidth(IntPtr mode);
        [DllImport(CoreGraphics)] private static extern void CGDisplayModeRelease(IntPtr mode);

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
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] [return: MarshalAs(UnmanagedType.I1)] private static extern bool SendBool(IntPtr receiver, IntPtr selector);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendVoidPtr(IntPtr receiver, IntPtr selector, IntPtr arg);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendPtrPtr(IntPtr receiver, IntPtr selector, IntPtr arg);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendVoidPtrPtr(IntPtr receiver, IntPtr selector, IntPtr a, IntPtr b);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nuint SendNUInt(IntPtr receiver, IntPtr selector);
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
