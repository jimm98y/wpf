// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Media Foundation's video renderer of a WPF window's handle: the frame over the window's composited root
// (EmbeddedContent.Compose), letterboxed in its target, and in that window's only.
//

using System;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using Xunit;

namespace WgpuInterop.Tests.Compositing
{
    public class VideoSurfaceComposeTests
    {
        [Fact]
        public void AWindowsVideo_IsComposedOverItsRootOnly()
        {
            var hwnd = (IntPtr)0x7E5701;
            var root = new SceneVisual();
            try
            {
                VideoSurfaces.Present(hwnd, new byte[64 * 48 * 4], 64, 48, 4f / 3);
                Assert.Same(root, EmbeddedContent.Compose(root, (IntPtr)0x7E5702, 800, 300));
                var composed = EmbeddedContent.Compose(root, hwnd, 800, 300);
                Assert.NotSame(root, composed);
                Assert.Same(root, composed.Children[0]);
                var video = composed.Children[1];
                var frame = Assert.Single(video.Children);
                // 4:3 in 800x300: 400x300 centred
                Assert.Equal(new System.Numerics.Vector2(200, 0), frame.Offset);
                Assert.Equal(1, VideoSurfaces.VersionOf(hwnd));
            }
            finally { VideoSurfaces.Remove(hwnd); }
            Assert.Same(root, EmbeddedContent.Compose(root, hwnd, 800, 300));
        }
    }
}
