// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Video shown in an application's window: what Media Foundation's video renderer (MediaFoundation/Session/
// MFVideoRenderer.cs) presents into the HWND it was given, kept here per window as a texture of the process's device
// (ExternalImage.cs: written in place, its version bumped each frame) with the rectangle of the window's client area it
// fills. The window systems draw it over the window it belongs to: WinForms' hosts through an embedded scene
// (WinFormsInterop/host/VideoSurfaceScene.cs), each frame a new version they present on; WPF's compositor over the window
// it composites (EmbeddedContent.Compose), a render posted to the window's dispatcher for each (PresentationCore's
// MediaContext). Nothing is drawn of a window none of them knows.
//

using System;
using System.Collections.Generic;
using System.Threading;
using static Microsoft.Wpf.Interop.WebGpu.Wgpu;

namespace Microsoft.Wpf.Interop.WebGpu.Composition
{
    internal static unsafe class VideoSurfaces
    {
        internal sealed class Surface
        {
            public IntPtr Hwnd;
            public ExternalImage? Image;
            /// <summary>The rectangle of the window's client area the video fills (its points): none set, the whole client
            /// area; and the border's color around it.</summary>
            public int X, Y, Width, Height;
            public bool Placed;
            /// <summary>The picture's shape kept (MFVideoARMode_PreservePicture): letterboxed in the rectangle, of its display aspect.</summary>
            public bool Preserve = true;
            public float Aspect = 1;
            public uint Border;
            public bool Visible = true;
            /// <summary>Bumped with each of its frames and changes of place.</summary>
            public int Version;
        }

        /// <summary>The window's own version (0 of none): what a compositor presents on.</summary>
        public static int VersionOf(IntPtr hwnd) { lock (s_lock) return s_surfaces.TryGetValue(hwnd, out var s) ? s.Version : 0; }

        /// <summary>The window system told of a change (WPF's: a render of the window's posted; WinForms' hosts poll <see cref="Version"/>).</summary>
        private static void Changed(IntPtr hwnd)
        {
            Interlocked.Increment(ref s_version);
            try { (AppContext.GetData("WpfWebGpu.Video.FrameArrived") as Action<IntPtr>)?.Invoke(hwnd); }
            catch (Exception) { }
        }

        private static readonly Dictionary<IntPtr, Surface> s_surfaces = new();
        private static readonly object s_lock = new();
        private static int s_version;

        /// <summary>The display targets' sRGB-ness (the renderers say it as they configure their surfaces): a texture made to match.</summary>
        internal static volatile bool SrgbDisplay = true;

        /// <summary>Bumped with every frame and every change of a surface's place: the hosts present on it.</summary>
        public static int Version => Volatile.Read(ref s_version);

        /// <summary>A frame of straight BGRA into the window's surface, where in its client area it goes.</summary>
        public static void Present(IntPtr hwnd, ReadOnlySpan<byte> bgra, int width, int height, float aspect)
        {
            if (hwnd == IntPtr.Zero || width <= 0 || height <= 0 || bgra.Length < width * height * 4) return;
            var ctx = ProcessGpu.Acquire();
            try
            {
                Surface s;
                ExternalImage? retired = null;
                lock (s_lock)
                {
                    if (!s_surfaces.TryGetValue(hwnd, out s!)) s_surfaces[hwnd] = s = new Surface { Hwnd = hwnd };
                    var format = SrgbDisplay ? WGPUTextureFormat.BGRA8UnormSrgb : WGPUTextureFormat.BGRA8Unorm;
                    if (s.Image == null || s.Image.Width != width || s.Image.Height != height || s.Image.Format != format)
                    {
                        var d = new WGPUTextureDescriptor
                        {
                            usage = WGPUTextureUsage.TextureBinding | WGPUTextureUsage.CopyDst | WGPUTextureUsage.CopySrc,
                            dimension = WGPUTextureDimension._2D,
                            size = new WGPUExtent3D { width = (uint)width, height = (uint)height, depthOrArrayLayers = 1 },
                            format = format, mipLevelCount = 1, sampleCount = 1,
                        };
                        IntPtr tex = wgpuDeviceCreateTexture(ctx.Device, &d);
                        IntPtr view = wgpuTextureCreateView(tex, IntPtr.Zero);
                        retired = s.Image;
                        s.Image = new ExternalImage(ctx.Device, tex, view, width, height, format);
                    }
                    s.Aspect = aspect > 0 ? aspect : (float)width / height;
                    ctx.ResetGlUnpack();
                    fixed (byte* p = bgra)
                    {
                        var dst = new WGPUTexelCopyTextureInfo { texture = s.Image.Texture, aspect = WGPUTextureAspect.All };
                        var layout = new WGPUTexelCopyBufferLayout { bytesPerRow = (uint)(width * 4), rowsPerImage = (uint)height };
                        var size = new WGPUExtent3D { width = (uint)width, height = (uint)height, depthOrArrayLayers = 1 };
                        wgpuQueueWriteTexture(ctx.Queue, &dst, p, (nuint)(width * height * 4), &layout, &size);
                    }
                    s.Image.Bump();
                    s.Version++;
                }
                // (a texture of another size: let go of once no frame in flight draws it -- the renderers key what they
                // made of it by its id, and keep it no longer than its last draw)
                _ = retired;
                Changed(hwnd);
            }
            finally { ProcessGpu.Release(ctx); }
        }

        /// <summary>The surface's place in its window changed (SetVideoPosition), or it is shown or hidden.</summary>
        public static void Place(IntPtr hwnd, bool placed, int x, int y, int w, int h, bool preserve, uint border, bool visible)
        {
            lock (s_lock)
            {
                if (!s_surfaces.TryGetValue(hwnd, out var s)) s_surfaces[hwnd] = s = new Surface { Hwnd = hwnd };
                s.Placed = placed; s.X = x; s.Y = y; s.Width = w; s.Height = h; s.Preserve = preserve; s.Border = border; s.Visible = visible;
                s.Version++;
            }
            Changed(hwnd);
        }

        /// <summary>The window shows no video any more (its renderer shut down).</summary>
        public static void Remove(IntPtr hwnd)
        {
            lock (s_lock) s_surfaces.Remove(hwnd);
            Changed(hwnd);
        }

        /// <summary>The surfaces now, each its window, texture and place.</summary>
        public static List<Surface> Snapshot()
        {
            lock (s_lock)
            {
                var l = new List<Surface>(s_surfaces.Count);
                foreach (var s in s_surfaces.Values)
                    if (s.Image != null && s.Visible) l.Add(new Surface { Hwnd = s.Hwnd, Image = s.Image, X = s.X, Y = s.Y, Width = s.Width, Height = s.Height, Placed = s.Placed, Preserve = s.Preserve, Aspect = s.Aspect, Border = s.Border });
                return l;
            }
        }

        /// <summary>A surface as a scene in its window's client points: the border's color over the client area, the frame over its rectangle.</summary>
        public static SceneVisual SceneOf(Surface s, int clientWidth, int clientHeight)
        {
            // the rectangle: the one set, else the client area; the picture in it of its shape where that is kept
            float rx = s.Placed ? s.X : 0, ry = s.Placed ? s.Y : 0, rw = s.Placed ? s.Width : clientWidth, rh = s.Placed ? s.Height : clientHeight;
            if (s.Preserve && rw > 0 && rh > 0 && s.Aspect > 0)
            {
                float fw = rw, fh = rw / s.Aspect;
                if (fh > rh) { fh = rh; fw = rh * s.Aspect; }
                rx += (rw - fw) / 2; ry += (rh - fh) / 2; rw = fw; rh = fh;
            }
            s = new Surface { Hwnd = s.Hwnd, Image = s.Image, X = (int)MathF.Round(rx), Y = (int)MathF.Round(ry), Width = (int)MathF.Round(rw), Height = (int)MathF.Round(rh), Border = s.Border };
            var v = new SceneVisual();
            if (clientWidth > 0 && clientHeight > 0)
            {
                var c = s.Border;
                var color = new RgbaColor(((c >> 16) & 0xFF) / 255f, ((c >> 8) & 0xFF) / 255f, (c & 0xFF) / 255f, 1f);
                // the border: where the frame is not (letterboxing)
                foreach (var r in Around(s, clientWidth, clientHeight))
                    v.Content.Add(new GeometryFill(new RectangleGeometry(r), color));
            }
            var img = s.Image!;
            var brush = new ImageBrush(img.Placeholder, img.Width, img.Height, TileMode.None, s.Width, s.Height, 1f) { External = img, ExternalVersion = img.Version };
            var frame = new SceneVisual { Offset = new System.Numerics.Vector2(s.X, s.Y) };
            frame.Content.Add(new GeometryFill(new RectangleGeometry(new Rect(0, 0, s.Width, s.Height)), brush));
            v.Children.Add(frame);
            v.Clip = new Rect(0, 0, Math.Max(clientWidth, 0), Math.Max(clientHeight, 0));
            return v;
        }

        private static IEnumerable<Rect> Around(Surface s, int cw, int ch)
        {
            if (s.Y > 0) yield return new Rect(0, 0, cw, s.Y);
            if (s.Y + s.Height < ch) yield return new Rect(0, s.Y + s.Height, cw, ch - s.Y - s.Height);
            if (s.X > 0) yield return new Rect(0, s.Y, s.X, s.Height);
            if (s.X + s.Width < cw) yield return new Rect(s.X + s.Width, s.Y, cw - s.X - s.Width, s.Height);
        }
    }
}
