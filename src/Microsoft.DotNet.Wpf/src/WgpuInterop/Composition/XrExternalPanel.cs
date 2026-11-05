// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A headset's fullscreen 3D view (XrStereo3D): its pictures -- a D3DImage's frames (a 3D engine's), or a WPF
// Viewport3D's drawn for the eyes (WgpuSceneRenderer.Headset.cs) -- published here, numbered, and shown in a
// surface of their own, at the resolution they are drawn at and transparent where nothing is drawn, so that the
// room shows round the view. The headset's host takes the latest as it builds a frame: an OpenXR host on its
// frame loop (AndroidXrHost, an Android Surface), a browser on the app's frame (a canvas the page's XR layer
// draws from), each moved to where the eyes are by its depth (XrExternalPanel.Reproject.cs); or, for a compositor
// that takes it from memory and moves it itself (visionOS's), read back (ReadLatest). The hosts are the app's
// code, not this assembly's, and name what they need through AppContext ("WpfWebGpu.Xr.ExternalTarget": the
// surface's window and the pictures' size; ExternalDrawLatest, ExternalReadLatest, LargestExternal). Colour is
// straight (ExternalImage.cs), drawn premultiplied, as a layer that blends by source alpha takes it.
//

using System;
using System.Collections.Generic;
using System.Text;
using static Microsoft.Wpf.Interop.WebGpu.Wgpu;

namespace Microsoft.Wpf.Interop.WebGpu.Composition
{
    internal static unsafe partial class XrExternalPanel
    {
        private static readonly object s_lock = new();
        private static readonly Dictionary<(int, int), WeakReference<ExternalImage>> s_images = new();
        private static IntPtr s_window, s_surface;
        private static int s_width, s_height;
        private static WGPUTextureFormat s_format;

        private static int s_frames;
        private static long s_framesSince;

        // The headset's frame loop draws: the compositor shows the surface's newest frame, and the views the XR
        // host submits must be that frame's. Drawn as the app published them, a frame came between the views'
        // read and the compositor's latch and was shown with the views of the one before (the scene jumped by
        // the head's move in a frame). So the app only publishes -- the latest image and its number, its views
        // kept by that number (AndroidXrStereo3D) -- and the XR host draws the latest into the panel as it
        // builds a frame and submits the views of the number it drew. One frame a headset frame, each shown:
        // the surface's buffers come back as fast as they are taken.
        private static ExternalImage? s_latest;
        private static long s_latestNumber;

        private static int s_largestWidth, s_largestHeight;
        private static long s_largestAt;
        private static bool s_failLogged;
        private static long s_whyAt;

        /// <summary>Why the headset's frame drew nothing (once a second, while it doesn't): no image yet, no
        /// target, or no views for the latest number.</summary>
        private static void Why(string what)
        {
            long now = Environment.TickCount64;
            if (now - s_whyAt < 1000) return;
            s_whyAt = now;
            Console.WriteLine($"[xr panel] nothing drawn: {what}");
        }

        /// <summary>The largest D3DImage published in the last second (width in the high 32 bits): the
        /// fullscreen view's, which is rendered the larger for it.</summary>
        private static long Largest()
        {
            lock (s_lock) return Environment.TickCount64 - s_largestAt > 1000 ? 0 : ((long)s_largestWidth << 32) | (uint)s_largestHeight;
        }

        private static IntPtr s_targetWindow;
        private static int s_targetWidth, s_targetHeight;

        /// <summary>The lock a D3DImage's copy into its texture and its publishing are made under (the sink's
        /// SendSurface): the headset's draw of the latest is never of a texture half the next frame.</summary>
        internal static object SyncRoot => s_lock;

        static XrExternalPanel()
        {
            AppContext.SetData("WpfWebGpu.Xr.ExternalTarget", (Func<IntPtr, int, int, bool>)Target);
            AppContext.SetData("WpfWebGpu.Xr.LargestExternal", (Func<long>)Largest);
            AppContext.SetData("WpfWebGpu.Xr.ExternalDrawLatest", (Func<Func<long, float[]?>, long>)DrawLatest);
            AppContext.SetData("WpfWebGpu.Xr.ExternalReadLatest", (Func<Action<IntPtr, int, int, int, long>, long>)ReadLatest);
            AppContext.SetData("WpfWebGpu.Xr.ExternalReadLatestDepth", (Func<Action<IntPtr, int, int, int, IntPtr, int, int, int, long>, long>)ReadLatestDepth);
        }

        /// <summary>Its entry points offered (the static constructor's): before a host asks for them, which may be before
        /// any image is published.</summary>
        public static void Offer() { }

        /// <summary>A D3DImage's new frame (the sink's present): the latest, numbered; its views are kept by the number.</summary>
        public static void Published(ExternalImage image)
        {
            lock (s_lock)
            {
                s_images[(image.Width, image.Height)] = new WeakReference<ExternalImage>(image);
                long now = Environment.TickCount64;
                if ((long)image.Width * image.Height >= (long)s_largestWidth * s_largestHeight || now - s_largestAt > 1000)
                {
                    s_largestWidth = image.Width;
                    s_largestHeight = image.Height;
                    s_largestAt = now;
                }
                if (s_targetWindow == IntPtr.Zero || image.Width != s_targetWidth || image.Height != s_targetHeight)
                {
                    if (s_targetWindow != IntPtr.Zero) Why($"a {image.Width}x{image.Height} image published, the panel is {s_targetWidth}x{s_targetHeight}");
                    return;
                }
                s_latest = image;
                s_latestNumber++;
                (AppContext.GetData("WpfWebGpu.Xr.ExternalPublished") as Action<long>)?.Invoke(s_latestNumber);
            }
        }

        /// <summary>The surface (an ANativeWindow*, zero for none) the images of a size are drawn into; whether one
        /// of that size has been published since.</summary>
        private static bool Target(IntPtr nativeWindow, int width, int height)
        {
            lock (s_lock)
            {
                if (nativeWindow != s_targetWindow || width != s_targetWidth || height != s_targetHeight)
                {
                    s_targetWindow = nativeWindow;
                    s_targetWidth = width;
                    s_targetHeight = height;
                    s_latest = null;
                    s_failLogged = false;
                }
                return s_latest != null;
            }
        }

        /// <summary>The headset's frame: the latest published image drawn into the target, moved to where the eyes
        /// are (XrExternalPanel.Reproject.cs) by <paramref name="views"/>' answer for its number (the views it was
        /// drawn for and the frame's, null when the headset cannot show it yet) -- a frame drawn and not shown keeps
        /// a buffer the next acquire then waits for, under this lock, which the app's present needs; its number, or -1.</summary>
        private static long DrawLatest(Func<long, float[]?> views)
        {
            lock (s_lock)
            {
                float[]? moved = s_latest != null && s_targetWindow != IntPtr.Zero ? views(s_latestNumber) : null;
                if (s_latest is not { } image || s_targetWindow == IntPtr.Zero || moved == null)
                {
                    Why(s_targetWindow == IntPtr.Zero ? "no panel" : s_latest == null ? $"no {s_targetWidth}x{s_targetHeight} image since the panel was made" : $"no views for frame {s_latestNumber}");
                    return -1;
                }
                if (!Draw(image, s_targetWindow, moved))
                {
                    if (!s_failLogged) { s_failLogged = true; Console.WriteLine($"[xr panel] a {image.Width}x{image.Height} frame could not be drawn"); }
                    return -1;
                }
                s_frames++;
                long now = Environment.TickCount64;
                if (now - s_framesSince >= 1000)
                {
                    if (s_framesSince != 0) Console.WriteLine($"[xr panel] {s_frames * 1000.0 / (now - s_framesSince):0.0} frames a second, the latest {s_latestNumber}, {(s_latestHasDepth ? "moved by its depth" : "no depth: turned only")}");
                    s_frames = 0;
                    s_framesSince = now;
                }
                return s_latestNumber;
            }
        }

        private static bool Draw(ExternalImage image, IntPtr nativeWindow, float[] moved)
        {
            int width = image.Width, height = image.Height;
            {
                WgpuContext? ctx = ProcessGpu.Peek();
                if (ctx == null || image.Device != ctx.Device) return false;
                if (!Surface(ctx, nativeWindow, width, height, image.Format)) return false;

                WGPUSurfaceTexture st;
                wgpuSurfaceGetCurrentTexture(s_surface, &st);
                if (st.texture == IntPtr.Zero) return false;
                if (st.status != WGPUSurfaceGetCurrentTextureStatus.SuccessOptimal && st.status != WGPUSurfaceGetCurrentTextureStatus.SuccessSuboptimal)
                {
                    wgpuTextureRelease(st.texture);
                    s_width = 0;        // configured again next time
                    return false;
                }
                IntPtr target = wgpuTextureCreateView(st.texture, IntPtr.Zero);
                IntPtr enc = wgpuDeviceCreateCommandEncoder(ctx.Device, IntPtr.Zero);
                DrawMoved(ctx, enc, target, image, moved);
                IntPtr cmd = wgpuCommandEncoderFinish(enc, IntPtr.Zero);
                wgpuCommandEncoderRelease(enc);
                wgpuQueueSubmit(ctx.Queue, 1, &cmd);
                wgpuCommandBufferRelease(cmd);
                wgpuTextureViewRelease(target);
                return PresentAndRelease(s_surface, st.texture) == WGPUStatus.Success;
            }
        }

        private static long s_readNumber = -1;
        private static IntPtr s_readBuffer;
        private static ulong s_readSize;

        /// <summary>For a headset whose compositor takes the picture from memory and moves it itself (visionOS's: by
        /// the head's pose it was drawn for, VisionXrHost): the latest published image, if it is new since the
        /// last, read back -- RGBA rows, straight colour -- and handed to <paramref name="use"/> while it is mapped
        /// (its memory, width, height, bytes a row, number); the number, or -1 for none new.</summary>
        private static long ReadLatest(Action<IntPtr, int, int, int, long> use)
        {
            lock (s_lock)
            {
                if (s_latest is not { } image || s_latestNumber == s_readNumber) return -1;
                WgpuContext? ctx = ProcessGpu.Peek();
                if (ctx == null || image.Device != ctx.Device) return -1;
                int row = (image.Width * 4 + 255) & ~255;
                ulong size = (ulong)row * (ulong)image.Height;
                if (s_readBuffer == IntPtr.Zero || s_readSize != size)
                {
                    if (s_readBuffer != IntPtr.Zero) wgpuBufferRelease(s_readBuffer);
                    s_readBuffer = ctx.CreateBuffer(size, WGPUBufferUsage.MapRead | WGPUBufferUsage.CopyDst);
                    s_readSize = size;
                }
                IntPtr enc = wgpuDeviceCreateCommandEncoder(ctx.Device, IntPtr.Zero);
                var from = new WGPUTexelCopyTextureInfo { texture = image.Texture, aspect = WGPUTextureAspect.All };
                var to = new WGPUTexelCopyBufferInfo { layout = new WGPUTexelCopyBufferLayout { bytesPerRow = (uint)row, rowsPerImage = (uint)image.Height }, buffer = s_readBuffer };
                var extent = new WGPUExtent3D { width = (uint)image.Width, height = (uint)image.Height, depthOrArrayLayers = 1 };
                wgpuCommandEncoderCopyTextureToBuffer(enc, &from, &to, &extent);
                IntPtr cmd = wgpuCommandEncoderFinish(enc, IntPtr.Zero);
                wgpuCommandEncoderRelease(enc);
                wgpuQueueSubmit(ctx.Queue, 1, &cmd);
                wgpuCommandBufferRelease(cmd);
                long number = s_latestNumber;
                if (!ctx.MapReadInPlace(s_readBuffer, size, p => use(p, image.Width, image.Height, row, number))) return -1;
                s_readNumber = number;
                return number;
            }
        }

        private static IntPtr s_cellsReadBuffer;
        private static ulong s_cellsReadSize;

        /// <summary>As <see cref="ReadLatest"/>, with the depth the picture was drawn with, each eye's cells' nearest
        /// (<see cref="Cell"/> texels each way, 0 near .. 1 far, the left eye's cells then the right's; zero and no
        /// cells where it has none): for a compositor that moves the picture itself, by its depth (visionOS's).</summary>
        private static long ReadLatestDepth(Action<IntPtr, int, int, int, IntPtr, int, int, int, long> use)
        {
            lock (s_lock)
            {
                if (s_latest is not { } image || s_latestNumber == s_readNumber) return -1;
                WgpuContext? ctx = ProcessGpu.Peek();
                if (ctx == null || image.Device != ctx.Device) return -1;
                int row = (image.Width * 4 + 255) & ~255;
                ulong size = (ulong)row * (ulong)image.Height;
                if (s_readBuffer == IntPtr.Zero || s_readSize != size)
                {
                    if (s_readBuffer != IntPtr.Zero) wgpuBufferRelease(s_readBuffer);
                    s_readBuffer = ctx.CreateBuffer(size, WGPUBufferUsage.MapRead | WGPUBufferUsage.CopyDst);
                    s_readSize = size;
                }
                IntPtr enc = wgpuDeviceCreateCommandEncoder(ctx.Device, IntPtr.Zero);
                var from = new WGPUTexelCopyTextureInfo { texture = image.Texture, aspect = WGPUTextureAspect.All };
                var to = new WGPUTexelCopyBufferInfo { layout = new WGPUTexelCopyBufferLayout { bytesPerRow = (uint)row, rowsPerImage = (uint)image.Height }, buffer = s_readBuffer };
                var extent = new WGPUExtent3D { width = (uint)image.Width, height = (uint)image.Height, depthOrArrayLayers = 1 };
                wgpuCommandEncoderCopyTextureToBuffer(enc, &from, &to, &extent);
                bool depth = s_latestHasDepth;
                int cellsRow = 0;
                ulong cellsSize = 0;
                if (depth)
                {
                    Cells(ctx, enc, image.Width, image.Height);
                    cellsRow = (s_cellsWidth * 4 + 255) & ~255;
                    cellsSize = (ulong)cellsRow * (ulong)s_cellsHeight;
                    if (s_cellsReadBuffer == IntPtr.Zero || s_cellsReadSize != cellsSize)
                    {
                        if (s_cellsReadBuffer != IntPtr.Zero) wgpuBufferRelease(s_cellsReadBuffer);
                        s_cellsReadBuffer = ctx.CreateBuffer(cellsSize, WGPUBufferUsage.MapRead | WGPUBufferUsage.CopyDst);
                        s_cellsReadSize = cellsSize;
                    }
                    var cellsFrom = new WGPUTexelCopyTextureInfo { texture = s_cells, aspect = WGPUTextureAspect.All };
                    var cellsTo = new WGPUTexelCopyBufferInfo { layout = new WGPUTexelCopyBufferLayout { bytesPerRow = (uint)cellsRow, rowsPerImage = (uint)s_cellsHeight }, buffer = s_cellsReadBuffer };
                    var cellsExtent = new WGPUExtent3D { width = (uint)s_cellsWidth, height = (uint)s_cellsHeight, depthOrArrayLayers = 1 };
                    wgpuCommandEncoderCopyTextureToBuffer(enc, &cellsFrom, &cellsTo, &cellsExtent);
                }
                IntPtr cmd = wgpuCommandEncoderFinish(enc, IntPtr.Zero);
                wgpuCommandEncoderRelease(enc);
                wgpuQueueSubmit(ctx.Queue, 1, &cmd);
                wgpuCommandBufferRelease(cmd);
                long number = s_latestNumber;
                int cw = s_cellsWidth, ch = s_cellsHeight;
                bool read = ctx.MapReadInPlace(s_readBuffer, size, p =>
                {
                    if (!depth || !ctx.MapReadInPlace(s_cellsReadBuffer, cellsSize, c => use(p, image.Width, image.Height, row, c, cw, ch, cellsRow, number)))
                        use(p, image.Width, image.Height, row, IntPtr.Zero, 0, 0, 0, number);
                });
                if (!read) return -1;
                s_readNumber = number;
                return number;
            }
        }

        private static bool Surface(WgpuContext ctx, IntPtr nativeWindow, int width, int height, WGPUTextureFormat imageFormat)
        {
            if (nativeWindow != s_window)
            {
                if (s_surface != IntPtr.Zero) wgpuSurfaceRelease(s_surface);
#if WGPU_BROWSER
                // the browser's: a canvas the page's XR layer is drawn from (browser-window.js, xrPanelCanvas)
                s_surface = Platform.BrowserInterop.CreateSurface(ctx.Instance, nativeWindow);
#else
                var source = new WGPUSurfaceSourceAndroidNativeWindow
                {
                    chain = new WGPUChainedStruct { next = null, sType = WGPUSType_SurfaceSourceAndroidNativeWindow },
                    window = (void*)nativeWindow,
                };
                var desc = new WGPUSurfaceDescriptor { nextInChain = (WGPUChainedStruct*)&source };
                s_surface = wgpuInstanceCreateSurface(ctx.Instance, &desc);
#endif
                s_window = nativeWindow;
                s_width = 0;
            }
            if (s_surface == IntPtr.Zero) return false;
            if (s_width == width && s_height == height) return true;
            // the image's encoding kept: an sRGB image (sampled to linear) to an sRGB surface, a plain one to a plain one
            bool srgb = imageFormat is WGPUTextureFormat.BGRA8UnormSrgb or WGPUTextureFormat.RGBA8UnormSrgb;
            s_format = Choose(ctx, srgb);
            var config = new WGPUSurfaceConfiguration
            {
                device = ctx.Device, format = s_format, usage = WGPUTextureUsage.RenderAttachment,
                width = (uint)width, height = (uint)height, alphaMode = WGPUCompositeAlphaMode.Auto, presentMode = WGPUPresentMode.Fifo,
            };
            wgpuSurfaceConfigure(s_surface, &config);
            s_width = width;
            s_height = height;
            return true;
        }

        private static WGPUTextureFormat Choose(WgpuContext ctx, bool srgb)
        {
            WGPUTextureFormat[] want = srgb
                ? new[] { WGPUTextureFormat.RGBA8UnormSrgb, WGPUTextureFormat.BGRA8UnormSrgb }
                : new[] { WGPUTextureFormat.RGBA8Unorm, WGPUTextureFormat.BGRA8Unorm };
            WGPUSurfaceCapabilities caps;
            if (wgpuSurfaceGetCapabilities(s_surface, ctx.Adapter, &caps) != WGPUStatus.Success || caps.formatCount == 0) return want[0];
            WGPUTextureFormat chosen = caps.formats[0];
            foreach (WGPUTextureFormat f in want)
            {
                bool found = false;
                for (nuint i = 0; i < caps.formatCount; i++) if (caps.formats[i] == f) { chosen = f; found = true; break; }
                if (found) break;
            }
            wgpuSurfaceCapabilitiesFreeMembers(caps);
            return chosen;
        }
    }
}
