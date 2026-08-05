// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Android backend of the windowing seam. wgpu presents on Android through an ANativeWindow --
// the NDK handle behind a Java android.view.Surface -- wrapped as WGPUSurfaceSourceAndroidNativeWindow.
// wgpu picks Vulkan where the device/emulator has it and falls back to GLES otherwise; both consume
// the same ANativeWindow, so nothing here is backend-specific.
//
// Two things differ from every other platform and shape this file:
//
//   1. The native window ARRIVES LATE and is REPLACED. A Surface is only valid between
//      surfaceCreated and surfaceDestroyed, and Android destroys it whenever the activity stops
//      (home button, screen off, task switch) -- handing back a DIFFERENT ANativeWindow* on resume.
//      A WPF window's handle, however, must be stable for its whole life. So the handle WPF holds is
//      a synthetic one minted by the windowing backend (MS.Internal.Interop.AndroidWindow), and this
//      file resolves it to the CURRENT ANativeWindow* through Resolver, which that backend installs.
//      Callers must therefore be prepared for CreateSurface to return Zero (no surface yet) and for
//      GetNativeWindow to change value -- WpfCompositionSink re-creates its wgpu surface when it does.
//
//   2. There is no contents-scale to keep in sync. An ANativeWindow is sized in DEVICE PIXELS
//      already (unlike a CAMetalLayer, which maps a pixel drawable onto a point-sized view), so
//      NativePlatform.UpdateContentsScale is a no-op here and the display density only ever reaches
//      WPF as its DPI scale.
//
// Note this file P/Invokes ONLY the NDK's libandroid.so, never JNI: everything that needs a JNIEnv*
// (turning a Java Surface into an ANativeWindow, creating views, reading DisplayMetrics) is done by
// the app head, which is a .NET-for-Android assembly and has the Java bindings for free.
//

using System;
using System.Runtime.InteropServices;

namespace Microsoft.Wpf.Interop.WebGpu.Composition.Platform
{
    internal static unsafe class AndroidInterop
    {
        /// <summary>
        /// Maps a WPF window handle to the ANativeWindow* currently backing it, or Zero when the
        /// window has no live Surface (not yet created, or destroyed while the activity is stopped).
        /// Installed by the windowing backend; unset outside a WPF app (the spikes drive the engine
        /// with a real ANativeWindow* as the handle, which the fallback below passes straight through).
        /// </summary>
        public static Func<IntPtr, IntPtr>? Resolver { get; set; }

        /// <summary>
        /// Where a window sits inside the activity. Needed only when popups are composited into their
        /// owner's surface (see NativePlatform.PopupsShareOwnerSurface): the popup's scene has to be
        /// translated to the popup's position, because it is being drawn into someone else's surface.
        /// Installed by the windowing backend; null leaves every window at the origin.
        /// </summary>
        public static AndroidPlatform.WindowOriginCallback? OriginQuery { get; set; }

        /// <summary>See <see cref="OriginQuery"/>.</summary>
        public static void GetWindowOrigin(IntPtr handle, out int x, out int y)
        {
            x = y = 0;
            OriginQuery?.Invoke(handle, out x, out y);
        }

        /// <summary>
        /// Whether the window behind a WPF handle is OPAQUE. A WPF popup on Android is its own
        /// translucent view, so its surface has to be configured with premultiplied alpha and cleared
        /// transparent -- otherwise the popup's drop shadow and rounded corners composite against
        /// black instead of the content behind them. Installed by the windowing backend alongside
        /// <see cref="Resolver"/>; null (no backend) means "assume opaque", which is right for a
        /// spike driving the engine with a single fullscreen window.
        /// </summary>
        public static Func<IntPtr, bool>? OpaqueQuery { get; set; }

        /// <summary>See <see cref="OpaqueQuery"/>.</summary>
        public static bool IsWindowOpaque(IntPtr handle)
        {
            Func<IntPtr, bool>? query = OpaqueQuery;
            return query is null || query(handle);
        }

        /// <summary>The live ANativeWindow* behind a WPF window handle (see <see cref="Resolver"/>).</summary>
        public static IntPtr GetNativeWindow(IntPtr handle)
        {
            Func<IntPtr, IntPtr>? resolver = Resolver;
            return resolver is null ? handle : resolver(handle);
        }

        /// <summary>
        /// Create a wgpu surface for a WPF window handle. Returns Zero when the window has no live
        /// Surface yet -- which is the NORMAL state for the first frame or two after a window is
        /// created, because Android delivers surfaceCreated asynchronously.
        /// </summary>
        public static IntPtr CreateSurface(IntPtr instance, IntPtr handle)
        {
            IntPtr nativeWindow = GetNativeWindow(handle);
            if (nativeWindow == IntPtr.Zero) return IntPtr.Zero;

            var androidSource = new Wgpu.WGPUSurfaceSourceAndroidNativeWindow
            {
                chain = new Wgpu.WGPUChainedStruct { next = null, sType = Wgpu.WGPUSType_SurfaceSourceAndroidNativeWindow },
                window = (void*)nativeWindow,
            };
            var desc = new Wgpu.WGPUSurfaceDescriptor { nextInChain = (Wgpu.WGPUChainedStruct*)&androidSource };
            return Wgpu.wgpuInstanceCreateSurface(instance, &desc);
        }

        /// <summary>The native window's size in device pixels, or 0x0 when it has no live Surface.</summary>
        public static void GetSize(IntPtr handle, out int width, out int height)
        {
            width = height = 0;
            IntPtr nativeWindow = GetNativeWindow(handle);
            if (nativeWindow == IntPtr.Zero) return;

            width = ANativeWindow_getWidth(nativeWindow);
            height = ANativeWindow_getHeight(nativeWindow);
            if (width < 0) width = 0;
            if (height < 0) height = 0;
        }

        // ---- NDK (libandroid.so) --------------------------------------------------
        //
        // Refcounting an ANativeWindow is what keeps it alive across the window-handle indirection
        // above: the backend acquires the one Android hands it and releases it on surfaceDestroyed,
        // so a frame that is already in flight cannot be rendering into freed memory.



        private const string Lib = "android";

        [DllImport(Lib)] internal static extern void ANativeWindow_acquire(IntPtr window);
        [DllImport(Lib)] internal static extern void ANativeWindow_release(IntPtr window);
        [DllImport(Lib)] internal static extern int ANativeWindow_getWidth(IntPtr window);
        [DllImport(Lib)] internal static extern int ANativeWindow_getHeight(IntPtr window);
    }
}
