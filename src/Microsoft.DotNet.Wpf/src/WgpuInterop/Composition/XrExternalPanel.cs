// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A D3DImage's frames drawn into a surface of their own: a headset's fullscreen 3D view (AndroidXrHost).
// Drawn as each frame is published, on the thread that publishes it -- not by the headset's frame loop,
// whose wait for the surface's next buffer (taken only as the loop's own frames go to the compositor)
// held every frame for the acquire's time out: one frame a second.
// Shown as a crop of its window's image, a 3D view had the window's resolution and whatever lay behind it
// (the app's white canvas); drawn from its own texture it has the resolution the app renders it at and
// its transparency, so the room shows round what it draws. The XR host, which is the app's code and not
// this assembly's, names the target through AppContext ("WpfWebGpu.Xr.ExternalTarget": the native window
// of an Android Surface and the image's pixel size, or zero for none; it answers whether a frame has been
// drawn there). An image of that size the sinks publish (WpfCompositionSink.Publish) is drawn; its colour
// is straight (ExternalImage.cs), drawn premultiplied, as a layer that blends by source alpha takes it.
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
        }

        /// <summary>A D3DImage's new frame (the sink's present): the latest, numbered; its views are kept by the number.</summary>
        public static void Published(ExternalImage image)
        {
            if (!OperatingSystem.IsAndroid()) return;
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
                    if (s_framesSince != 0) Console.WriteLine($"[xr panel] {s_frames * 1000.0 / (now - s_framesSince):0.0} frames a second, the latest {s_latestNumber}");
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

        private static bool Surface(WgpuContext ctx, IntPtr nativeWindow, int width, int height, WGPUTextureFormat imageFormat)
        {
            if (nativeWindow != s_window)
            {
                if (s_surface != IntPtr.Zero) wgpuSurfaceRelease(s_surface);
                var source = new WGPUSurfaceSourceAndroidNativeWindow
                {
                    chain = new WGPUChainedStruct { next = null, sType = WGPUSType_SurfaceSourceAndroidNativeWindow },
                    window = (void*)nativeWindow,
                };
                var desc = new WGPUSurfaceDescriptor { nextInChain = (WGPUChainedStruct*)&source };
                s_surface = wgpuInstanceCreateSurface(ctx.Instance, &desc);
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
