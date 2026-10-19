// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// An image whose pixels live in a GPU texture the compositor does not upload from bytes: a D3DImage's
// back buffer (Documentation/d3d11-webgpu-shim.md, section 8). One texture per D3DImage resource, kept
// across frames of the same size and written in place -- by a GPU pass from the Direct3D shim's texture
// when the shim shares the compositor's device (zero-copy), or by queue.writeTexture of the dirty
// rectangle's pixels otherwise (the copy path). Each present bumps Version; the scene's ImageBrush
// carries the version it was made at, so the damage tracker and the scene reuse see a new frame
// without looking at its pixels.
//
// The texture is straight (non-premultiplied) BGRA: sRGB-encoded for a display target (sampled to
// linear), plain for a linear one. The CPU paths that read an image's bytes (rare: a hit test's
// alpha, an image layer merge) read Placeholder, which is transparent.
//

using System;
using System.Threading;
using static Microsoft.Wpf.Interop.WebGpu.Wgpu;

namespace Microsoft.Wpf.Interop.WebGpu.Composition
{
    internal sealed class ExternalImage
    {
        public IntPtr Texture { get; }
        public IntPtr View { get; }
        public int Width { get; }
        public int Height { get; }
        public WGPUTextureFormat Format { get; }
        private long _version;
        private byte[]? _placeholder;

        public ExternalImage(IntPtr texture, IntPtr view, int width, int height, WGPUTextureFormat format)
        {
            Texture = texture; View = view; Width = width; Height = height; Format = format;
        }

        public long Version => Interlocked.Read(ref _version);

        public long Bump() => Interlocked.Increment(ref _version);

        /// <summary>The bytes the CPU paths read: transparent, the image's size.</summary>
        public byte[] Placeholder => _placeholder ??= new byte[Width * Height * 4];
    }
}
