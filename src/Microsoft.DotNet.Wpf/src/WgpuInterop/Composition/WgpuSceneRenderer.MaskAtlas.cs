// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// ONE TEXTURE FOR THE CLEARTYPE MASKS.
//
// Every ClearType run -- a GDI+ caption, a WPF TextBlock -- was a texture of its own, with two bind
// groups of its own (the multiply pass and the add pass), so every run was two draw calls that could
// merge with nothing: about 300 of the 640 calls of a partial frame of a busy form, at ~44 us each on
// a virtualised GL. The masks now share one atlas texture and one pair of bind groups per pass format,
// so runs drawn next to each other merge into a few calls.
//
// Allocation is shelves: a mask goes on the first shelf of its height (rounded up to 8) with room,
// else a new shelf is opened. Nothing is freed one mask at a time. When the atlas cannot take a mask
// it is drawn from a texture of its own as before, and at the end of that frame the whole atlas is
// emptied -- every mask in it leaves the cache, and the ones still in use come back on their next
// draw. Emptying only between frames keeps a frame from overwriting texels it has already drawn from.
// The masks are sampled nearest at texel centres, so a mask's neighbours never bleed into it.
//

using System;
using System.Collections.Generic;
using static Microsoft.Wpf.Interop.WebGpu.Wgpu;

namespace Microsoft.Wpf.Interop.WebGpu.Composition
{
    internal sealed unsafe partial class WgpuSceneRenderer
    {
        /// <summary>WGPU_MASK_ATLAS=0: every ClearType mask is a texture of its own, as before. Settable
        /// for tests, which compare the two.</summary>
        internal static bool MaskAtlasEnabled { get; set; } = Environment.GetEnvironmentVariable("WGPU_MASK_ATLAS") != "0";

        private const int MaskAtlasSize = 2048;
        private const int MaskAtlasMaxSide = 512;      // larger masks keep a texture of their own

        private sealed class MaskAtlas
        {
            public IntPtr Tex, View;
            public readonly List<(int Y, int H, int X)> Shelves = new();
            public int NextY;
            public bool Full;                           // a mask did not fit this frame: empty it at the end of it
            public readonly Dictionary<WGPUTextureFormat, (IntPtr Multiply, IntPtr Add)> BindGroups = new();
        }

        private MaskAtlas? _maskAtlas;

        /// <summary>Masks placed in the atlas / drawn from a texture of their own because it was full (diagnostics).</summary>
        internal static long PerfAtlasMasks, PerfAtlasFallbacks, PerfAtlasResets;

        /// <summary>A ClearType (subpixel) mask of <paramref name="rgba"/>: in the shared atlas when it
        /// fits, else a texture of its own.</summary>
        private CachedMask NewSubpixelMask(byte[] rgba, int w, int h, WGPUTextureFormat format, int ox, int oy)
        {
            if (MaskAtlasEnabled && w <= MaskAtlasMaxSide && h <= MaskAtlasMaxSide && w > 0 && h > 0 && AtlasPlace(w, h, out int ax, out int ay))
            {
                MaskAtlas atlas = _maskAtlas!;
                UploadTextureRegion(atlas.Tex, rgba, w, h, 4, ax, ay);
                if (!atlas.BindGroups.TryGetValue(format, out var groups))
                {
                    groups = (CreateSampledBindGroup(format, FillKind.TextSubpixelMultiply, atlas.View, NearestSampler()),
                              CreateSampledBindGroup(format, FillKind.TextSubpixelAdd, atlas.View, NearestSampler()));
                    atlas.BindGroups[format] = groups;
                }
                PerfAtlasMasks++;
                return new CachedMask
                {
                    Tex = atlas.Tex, View = atlas.View, BindGroup = groups.Multiply, BindGroupAdd = groups.Add,
                    Subpixel = true, Sampler = NearestSampler(), Format = format,
                    Ox = ox, Oy = oy, W = w, H = h,
                    InAtlas = true,
                    U0 = ax / (float)MaskAtlasSize, V0 = ay / (float)MaskAtlasSize,
                    U1 = (ax + w) / (float)MaskAtlasSize, V1 = (ay + h) / (float)MaskAtlasSize,
                };
            }
            if (MaskAtlasEnabled && _maskAtlas is { } a && w <= MaskAtlasMaxSide && h <= MaskAtlasMaxSide) { a.Full = true; PerfAtlasFallbacks++; }
            (IntPtr t, IntPtr view) = CreateRgbaTexture(rgba, w, h);
            return new CachedMask
            {
                Tex = t, View = view,
                BindGroup = CreateSampledBindGroup(format, FillKind.TextSubpixelMultiply, view, NearestSampler()),
                BindGroupAdd = CreateSampledBindGroup(format, FillKind.TextSubpixelAdd, view, NearestSampler()),
                Subpixel = true, Sampler = NearestSampler(), Format = format,
                Ox = ox, Oy = oy, W = w, H = h,
            };
        }

        private bool AtlasPlace(int w, int h, out int x, out int y)
        {
            x = y = 0;
            MaskAtlas atlas = _maskAtlas ??= NewMaskAtlas();
            if (atlas.Full) return false;               // emptied at the end of the frame, not during it
            int sh = (h + 7) & ~7;
            for (int i = 0; i < atlas.Shelves.Count; i++)
            {
                var s = atlas.Shelves[i];
                if (s.H == sh && s.X + w <= MaskAtlasSize)
                {
                    x = s.X; y = s.Y;
                    atlas.Shelves[i] = (s.Y, s.H, s.X + w);
                    return true;
                }
            }
            if (atlas.NextY + sh > MaskAtlasSize) return false;
            x = 0; y = atlas.NextY;
            atlas.Shelves.Add((atlas.NextY, sh, w));
            atlas.NextY += sh;
            return true;
        }

        private MaskAtlas NewMaskAtlas()
        {
            var atlas = new MaskAtlas();
            atlas.Tex = NewSampledTexture(MaskAtlasSize, MaskAtlasSize, WGPUTextureFormat.RGBA8Unorm);
            atlas.View = wgpuTextureCreateView(atlas.Tex, IntPtr.Zero);
            return atlas;
        }

        /// <summary>End of a frame in which the atlas ran out: every mask in it leaves the cache and its
        /// shelves start again (see the file header).</summary>
        private void EmptyMaskAtlasIfFull()
        {
            if (_maskAtlas is not { Full: true } atlas) return;
            List<long>? gone = null;
            foreach (KeyValuePair<long, CachedMask> kv in _maskCache)
                if (kv.Value.InAtlas) (gone ??= new List<long>()).Add(kv.Key);
            if (gone != null) foreach (long k in gone) _maskCache.Remove(k);
            atlas.Shelves.Clear();
            atlas.NextY = 0;
            atlas.Full = false;
            PerfAtlasResets++;
        }

        private void ReleaseMaskAtlas()
        {
            if (_maskAtlas is not { } atlas) return;
            foreach ((IntPtr m, IntPtr a) in atlas.BindGroups.Values) { wgpuBindGroupRelease(m); wgpuBindGroupRelease(a); }
            atlas.BindGroups.Clear();
            wgpuTextureViewRelease(atlas.View);
            wgpuTextureRelease(atlas.Tex);
            _maskAtlas = null;
        }

        /// <summary>Uploads <paramref name="pixels"/> into a rectangle of <paramref name="texture"/> at
        /// (<paramref name="ox"/>, <paramref name="oy"/>) -- the same two paths as UploadTexture.</summary>
        private void UploadTextureRegion(IntPtr texture, byte[] pixels, int width, int height, int bytesPerPixel, int ox, int oy)
        {
#if WGPU_BROWSER
            var dest = new WGPUTexelCopyTextureInfo { texture = texture, aspect = WGPUTextureAspect.All, origin = new WGPUOrigin3D { x = (uint)ox, y = (uint)oy } };
            var layout = new WGPUTexelCopyBufferLayout { offset = 0, bytesPerRow = (uint)(width * bytesPerPixel), rowsPerImage = (uint)height };
            var writeSize = new WGPUExtent3D { width = (uint)width, height = (uint)height, depthOrArrayLayers = 1 };
            fixed (byte* p = pixels)
                wgpuQueueWriteTexture(_ctx.Queue, &dest, p, (nuint)pixels.Length, &layout, &writeSize);
#else
            if (_ctx.IsOpenGL)
            {
                var glDest = new WGPUTexelCopyTextureInfo { texture = texture, aspect = WGPUTextureAspect.All, origin = new WGPUOrigin3D { x = (uint)ox, y = (uint)oy } };
                var glLayout = new WGPUTexelCopyBufferLayout { offset = 0, bytesPerRow = (uint)(width * bytesPerPixel), rowsPerImage = (uint)height };
                var glSize = new WGPUExtent3D { width = (uint)width, height = (uint)height, depthOrArrayLayers = 1 };
                fixed (byte* p = pixels)
                    wgpuQueueWriteTexture(_ctx.Queue, &glDest, p, (nuint)pixels.Length, &glLayout, &glSize);
                return;
            }
            int unpaddedBpr = width * bytesPerPixel;
            int alignedBpr = AlignUp(unpaddedBpr, 256);
            byte[] staging;
            if (alignedBpr == unpaddedBpr) staging = pixels;
            else
            {
                staging = new byte[alignedBpr * height];
                for (int r = 0; r < height; r++)
                    Buffer.BlockCopy(pixels, r * unpaddedBpr, staging, r * alignedBpr, unpaddedBpr);
            }
            int soff = AllocTexStaging(staging, alignedBpr * height);
            _pendingTexUploads.Add(new PendingTexUpload { StagingOffset = soff, Texture = texture, Width = width, Height = height, BytesPerRow = alignedBpr, OriginX = ox, OriginY = oy });
#endif
        }
    }
}
