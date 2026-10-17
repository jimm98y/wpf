// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// app crashed in DamageTracker.PatchContent (ArgumentOutOfRangeException) from the WinForms present
// path. A WPF window hosted by an ElementHost published its LIVE scene tree to the WinForms UI thread, and
// the WPF render thread rebuilt the next frame in that same tree (MilcoreEngine reuses one SceneVisual per
// WPF visual, clearing and refilling its lists) while the WinForms presenter's damage tracker walked it.
//
// Now the host is handed an immutable mirror (SceneMirror), sharing unchanged nodes frame to frame, and a
// damage walk that fails for any reason draws the whole frame instead of throwing.
//

using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using Xunit;

namespace WgpuInterop.Tests.Rendering
{
    public sealed class HostedSceneRaceTests
    {
        private static GeometryFill Box(float x, float y) =>
            new(new RectangleGeometry(new Rect(x, y, 10, 10)), new SolidColorBrush(new RgbaColor(0, 0, 1, 1)));

        /// <summary>A small live tree: a root with <paramref name="kids"/> children of a few boxes each.</summary>
        private static SceneVisual LiveTree(int kids, out List<SceneVisual> leaves)
        {
            var root = new SceneVisual();
            leaves = new List<SceneVisual>();
            for (int i = 0; i < kids; i++)
            {
                var k = new SceneVisual { Offset = new System.Numerics.Vector2(i * 12, 0) };
                for (int j = 0; j < 4; j++) k.Content.Add(Box(0, j * 12));
                root.Children.Add(k);
                leaves.Add(k);
            }
            return root;
        }

        [Fact]
        public void AMirror_IsNotChangedByTheLiveTree_AndSharesWhatDidNotChange()
        {
            var live = LiveTree(3, out var leaves);
            var mirror = new SceneMirror();
            SceneVisual a = mirror.Snapshot(live);
            Assert.NotSame(live, a);
            Assert.Equal(4, a.Children[0].Content.Count);

            // the next frame, rebuilt in place as the engine does it: one visual's content shrinks
            leaves[0].Content.Clear();
            leaves[0].Content.Add(Box(1, 1));
            Assert.Equal(4, a.Children[0].Content.Count);          // what was published stays as it was

            SceneVisual b = mirror.Snapshot(live);
            Assert.Equal(1, b.Children[0].Content.Count);
            Assert.NotSame(a.Children[0], b.Children[0]);          // the changed visual is a new node
            Assert.Same(a.Children[1], b.Children[1]);             // the unchanged ones are shared
            Assert.Same(a.Children[2], b.Children[2]);
            Assert.NotSame(a, b);                                   // and so the root above the change is new

            // nothing changed: the whole mirror is the last one
            SceneVisual c = mirror.Snapshot(live);
            Assert.Same(b, c);
            Assert.Equal(0, mirror.LastMade);
        }

        [Fact]
        public void ALiveBrushSource_IsMirroredToo()
        {
            var source = new SceneVisual();
            source.Content.Add(Box(0, 0));
            var live = new SceneVisual();
            var brush = new ImageBrush(source, 7, 16, 16, 0, 0, 1, 1, 10, 10, 1f);
            live.Content.Add(new GeometryFill(new RectangleGeometry(new Rect(0, 0, 10, 10)), brush));
            var mirror = new SceneMirror();
            SceneVisual a = mirror.Snapshot(live);
            var fill = Assert.IsType<GeometryFill>(a.Content[0]);
            SceneVisual mirroredSource = ((ImageBrush)fill.Brush).SourceVisual!;
            Assert.NotSame(source, mirroredSource);
            source.Content.Clear();
            Assert.Single(mirroredSource.Content);
            // unchanged source: the same re-pointed primitive next time
            SceneVisual b = mirror.Snapshot(live);
            SceneVisual c = mirror.Snapshot(live);
            Assert.Same(b.Content[0], c.Content[0]);
        }

        /// <summary>The hosted window's case: a render thread rebuilds the live tree frame after frame and
        /// publishes it; the host's thread diffs whatever was published last. With the live tree
        /// published this died within a few hundred frames.</summary>
        [Fact]
        public void TheHostDiffsThePublishedScene_WhileTheLiveTreeIsRebuilt()
        {
            IntPtr hwnd = (IntPtr)0x7A5E;
            HostedWpfContent.Claim(hwnd);
            try
            {
                var live = LiveTree(20, out var leaves);
                HostedWpfContent.Publish((ulong)hwnd, live, 400, 100);
                Exception? failure = null;
                using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                var render = new Thread(() =>
                {
                    var rng = new Random(1);
                    try
                    {
                        while (!stop.IsCancellationRequested)
                        {
                            foreach (SceneVisual k in leaves)
                            {
                                k.Content.Clear();
                                int n = rng.Next(0, 8);
                                for (int j = 0; j < n; j++) k.Content.Add(Box(0, j * 12));
                            }
                            HostedWpfContent.Publish((ulong)hwnd, live, 400, 100);
                        }
                    }
                    catch (Exception ex) { failure = ex; }
                });
                render.Start();
                var tracker = new WgpuSceneRenderer.DamageTracker();
                var rects = new List<WgpuSceneRenderer.Scissor>();
                int frames = 0;
                while (!stop.IsCancellationRequested)
                {
                    var scene = (SceneVisual)HostedWpfContent.GetScene(hwnd, out _, out _, out _)!;
                    tracker.Compute(scene, 400, 100, rects);
                    Assert.NotEqual("damage tracking failed", tracker.FullReason);   // the mirror never changes under it
                    frames++;
                }
                render.Join();
                Assert.Null(failure);
                Assert.True(frames > 10);
            }
            finally { HostedWpfContent.Release(hwnd); }
        }

        /// <summary>Whatever a scene does under the walk, the tracker does not throw: it draws the whole
        /// frame and starts over.</summary>
        [Fact]
        public void ADamageWalkOverAChangingTree_DrawsTheWholeFrame_RatherThanThrowing()
        {
            var live = LiveTree(30, out var leaves);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var mutator = new Thread(() =>
            {
                var rng = new Random(2);
                while (!stop.IsCancellationRequested)
                    foreach (SceneVisual k in leaves)
                    {
                        try
                        {
                            if (k.Content.Count > 0 && rng.Next(2) == 0) k.Content.RemoveAt(k.Content.Count - 1);
                            else if (k.Content.Count < 12) k.Content.Add(Box(0, 0));
                        }
                        catch (Exception) { }   // the list itself is not thread-safe; only the walk is under test
                    }
            });
            mutator.Start();
            var tracker = new WgpuSceneRenderer.DamageTracker();
            var rects = new List<WgpuSceneRenderer.Scissor>();
            while (!stop.IsCancellationRequested)
                tracker.Compute(live, 400, 100, rects);   // must never throw
            mutator.Join();
        }
    }
}
