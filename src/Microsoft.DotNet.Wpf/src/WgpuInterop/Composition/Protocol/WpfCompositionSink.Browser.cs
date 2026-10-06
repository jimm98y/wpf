// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Browser (WebAssembly) content-brush rasterization for WpfCompositionSink. Lives
// in its own NON-unsafe partial class part (`await` is illegal inside the main
// file's unsafe class declaration). See EnsureGpu: on the browser the engine's
// VisualRasterizerKeyed points here instead of the synchronous RenderToRgba.
//

#if WGPU_BROWSER

using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Microsoft.Wpf.Interop.WebGpu.Composition.Protocol
{
    internal sealed partial class WpfCompositionSink
    {
        // Async content-brush rasterization: the engine retries un-hashed brushes every
        // frame, so the first call kicks the render+readback and returns null (the brush
        // is blank for a frame); when the Promise completes the bytes are parked and a
        // re-render is poked; the retry consumes them and the engine caches the bitmap.
        private readonly Dictionary<(uint Key, int W, int H), byte[]> _brushReadbacksDone = new();
        private readonly HashSet<(uint Key, int W, int H)> _brushReadbacksPending = new();

        private byte[] RasterizeBrushBrowser(uint key, SceneVisual visual, int w, int h)
        {
            (uint, int, int) k = (key, w, h);
            if (_brushReadbacksDone.Remove(k, out byte[] px))
                return px;
            if (_brushReadbacksPending.Add(k))
                _ = RasterizeBrushBrowserAsync(k, visual, w, h);
            return null;
        }

        private async Task RasterizeBrushBrowserAsync((uint, int, int) k, SceneVisual visual, int w, int h)
        {
            try
            {
                // The render encodes/submits synchronously (the visual tree is safe to
                // touch afterwards); only the map-for-read awaits.
                byte[] px = await _renderer.RenderToRgbaAsync(visual, w, h, new RgbaColor(0, 0, 0, 0), srgbOutput: true);
                UnpremultiplyInPlace(px);
                _brushReadbacksDone[k] = px;
            }
            catch (Exception ex)
            {
                Log($"browser brush readback failed: {ex.Message}");
            }
            finally
            {
                _brushReadbacksPending.Remove(k);
            }
            if (!_disposed && _brushReadbacksDone.Count > 0)
                RenderTargets();
        }
    }
}

#endif
