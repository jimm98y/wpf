// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A WPF Viewport3D a headset shows alone (AndroidXrStereo3D, its fullscreen): drawn for the two eyes' cameras
// from its window's retained scene -- whoever presents that window (this sink, or a WinForms host its content is
// part of) is not drawing it meanwhile -- and published to the headset's panel with its depth, as a 3D engine's
// D3DImage is (SendSurface). The app's code asks through AppContext ("WpfWebGpu.Xr.RenderViewport3D": the
// window, the viewport's rectangle in its device pixels, the eyes' view and projection matrices, the image's
// size), being another assembly's.
//

using System;
using System.Collections.Generic;
using System.Numerics;

namespace Microsoft.Wpf.Interop.WebGpu.Composition.Protocol
{
    internal sealed unsafe partial class WpfCompositionSink
    {
        private static readonly List<WeakReference<WpfCompositionSink>> s_headsetSinks = new();

        /// <summary>This sink among those a headset's fullscreen 3D view may be drawn by.</summary>
        private void OfferHeadsetRendering()
        {
            lock (s_headsetSinks)
            {
                foreach (var r in s_headsetSinks) if (r.TryGetTarget(out var s) && ReferenceEquals(s, this)) return;
                s_headsetSinks.Add(new WeakReference<WpfCompositionSink>(this));
            }
            AppContext.SetData("WpfWebGpu.Xr.RenderViewport3D", (Func<IntPtr, float[], float[], int, int, bool>)RenderViewport3D);
            // every window drawn again, unchanged as it is: a headset that came later shows them as stereo pairs,
            // which a window becomes as it presents (WebXR's session begun)
            AppContext.SetData("WpfWebGpu.Xr.RedrawWindows", (Action)(() =>
            {
                WpfCompositionSink[] sinks;
                lock (s_headsetSinks)
                {
                    sinks = new WpfCompositionSink[s_headsetSinks.Count];
                    for (int i = 0; i < sinks.Length; i++) s_headsetSinks[i].TryGetTarget(out sinks[i]!);
                }
                foreach (var sink in sinks)
                {
                    if (sink == null) continue;
                    sink._presentedAllTargets = false;
                    sink.RenderTargets();
                }
            }));
            XrExternalPanel.Offer();
        }

        /// <summary>The Viewport3D of <paramref name="hwnd"/>'s window covering <paramref name="rect"/> (x, y, width,
        /// height: its device pixels) drawn for the eyes (<paramref name="cameras"/>: the left eye's view and projection,
        /// then the right's, 16 floats each, M11 to M44) and published; false when no sink has it.</summary>
        private static bool RenderViewport3D(IntPtr hwnd, float[] rect, float[] cameras, int width, int height)
        {
            WpfCompositionSink[] sinks;
            lock (s_headsetSinks)
            {
                s_headsetSinks.RemoveAll(r => !r.TryGetTarget(out _));
                sinks = new WpfCompositionSink[s_headsetSinks.Count];
                for (int i = 0; i < sinks.Length; i++) s_headsetSinks[i].TryGetTarget(out sinks[i]!);
            }
            foreach (var sink in sinks)
                if (sink != null && sink.RenderHeadset(hwnd, rect, cameras, width, height)) return true;
            return false;
        }

        private bool RenderHeadset(IntPtr hwnd, float[] rect, float[] cameras, int width, int height)
        {
            if (rect.Length < 4 || cameras.Length < 64) return false;
            SceneVisual? root = null;
            foreach (KeyValuePair<uint, MilTarget> kv in _engine.Targets)
                if (kv.Value.Hwnd == (ulong)hwnd && kv.Value.RootHandle != 0) { root = _engine.VisualByHandle(kv.Value.RootHandle); break; }
            if (root == null || _renderer == null || _ctx == null) return false;
            static Matrix4x4 M(float[] f, int at) => new(
                f[at], f[at + 1], f[at + 2], f[at + 3], f[at + 4], f[at + 5], f[at + 6], f[at + 7],
                f[at + 8], f[at + 9], f[at + 10], f[at + 11], f[at + 12], f[at + 13], f[at + 14], f[at + 15]);
            // drawn and published together: the panel draws the latest image under the same lock
            lock (XrExternalPanel.SyncRoot)
            {
                if (!_renderer.RenderHeadset3D(root, new Rect(rect[0], rect[1], rect[2], rect[3]),
                        M(cameras, 0), M(cameras, 16), M(cameras, 32), M(cameras, 48), width, height))
                    return false;
                if (_renderer.HeadsetImage is not { } image) return false;
                var (depth, format, samples) = _renderer.HeadsetDepth;
                XrExternalPanel.CopyDepth(_ctx, depth, format, samples, width, height);
                image.Bump();
                XrExternalPanel.Published(image);
            }
            return true;
        }
    }
}
