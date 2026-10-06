// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Partial redraw (WgpuSceneRenderer.PartialTarget) must be pixel-identical to rendering the frame in
// full. Each test renders a sequence of frames through a PartialTarget into a stand-in "swap chain"
// texture, and after each frame compares it, byte for byte, with the same scene rendered in full by
// a fresh renderer. It also checks the frame really WAS partial -- a test that silently fell back to
// full frames would prove nothing.
//

using System;
using System.Collections.Generic;
using System.Numerics;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using WgpuInterop.Tests.Harness;
using Xunit;
using static Microsoft.Wpf.Interop.WebGpu.Wgpu;

namespace WgpuInterop.Tests.Rendering
{
    public sealed class PartialRedrawTests : RendererTestBase
    {
        public PartialRedrawTests(GpuFixture gpu) : base(gpu) { }

        private const int W = 320, H = 240;

        /// <summary>Drives one PartialTarget over a sequence of frames and checks each one.</summary>
        private sealed class Harness : IDisposable
        {
            private readonly PartialRedrawTests _t;
            internal readonly WgpuSceneRenderer Renderer;
            internal readonly WgpuSceneRenderer.PartialTarget Target;
            private readonly IntPtr _tex, _view;
            private readonly Func<WgpuSceneRenderer> _reference;
            private readonly RgbaColor _bg;
            private readonly bool _transparent;

            internal Harness(PartialRedrawTests t, Func<WgpuSceneRenderer> make, RgbaColor bg, bool transparent = false)
            {
                _t = t;
                Renderer = make();
                _reference = make;
                _bg = bg;
                _transparent = transparent;
                Target = new WgpuSceneRenderer.PartialTarget(Renderer);
                (_tex, _view) = Renderer.CreatePersistentTarget(W, H, WGPUTextureFormat.RGBA8Unorm);
            }

            /// <summary>Render a frame partially and assert it equals the full render.</summary>
            internal void Frame(SceneVisual root, bool expectPartial, string what)
            {
                Renderer.BeginFrame();
                Target.Render(root, _view, WGPUTextureFormat.RGBA8Unorm, W, H, _bg, _transparent);
                Renderer.EndFrame();
                if (expectPartial)
                    Assert.False(Target.LastFull, $"{what}: expected a partial frame");
                byte[] got = Renderer.ReadTexture(_tex, W, H);
                WgpuSceneRenderer reference = _reference();
                byte[] want = reference.RenderToRgba(root, W, H, _bg, transparentTarget: _transparent);
                reference.Dispose();
                AssertSame(want, got, what);
            }

            public void Dispose()
            {
                Target.Dispose();
                wgpuTextureViewRelease(_view);
                wgpuTextureRelease(_tex);
                Renderer.Dispose();
            }
        }

        private static void AssertSame(byte[] want, byte[] got, string what)
        {
            Assert.Equal(want.Length, got.Length);
            int diff = 0, first = -1;
            for (int i = 0; i < want.Length; i++)
                if (want[i] != got[i]) { diff++; if (first < 0) first = i; }
            if (diff == 0) return;
            int p = first / 4;
            Assert.Fail($"{what}: {diff} bytes differ; first at ({p % W},{p / W}) " +
                        $"want {want[p * 4]},{want[p * 4 + 1]},{want[p * 4 + 2]},{want[p * 4 + 3]} " +
                        $"got {got[p * 4]},{got[p * 4 + 1]},{got[p * 4 + 2]},{got[p * 4 + 3]}");
        }

        private static GeometryFill Box(float x, float y, float w, float h, byte r, byte g, byte b, byte a = 255)
            => new(new RectangleGeometry(new Rect(x, y, w, h)), RgbaColor.FromBytes(r, g, b, a));

        /// <summary>A busy background: tiles of many colours, so a wrong pixel anywhere shows.</summary>
        private static SceneVisual Tiles()
        {
            var v = new SceneVisual();
            for (int y = 0; y < H; y += 20)
                for (int x = 0; x < W; x += 20)
                    v.Content.Add(Box(x + 1, y + 1, 18, 18, (byte)(x * 3), (byte)(y * 4), (byte)((x + y) % 256)));
            return v;
        }

        [Fact]
        public void AMovedRectangleRedrawsOnlyWhereItWasAndIs()
        {
            using var h = new Harness(this, NewRenderer, White);
            var root = new SceneVisual();
            root.Children.Add(Tiles());
            var mover = new SceneVisual { Offset = new Vector2(30, 30) };
            mover.Content.Add(Box(0, 0, 25, 17, 200, 30, 30, 200));     // translucent: blends over the tiles
            root.Children.Add(mover);

            h.Frame(root, expectPartial: false, "first frame");
            for (int i = 1; i <= 6; i++)
            {
                mover.Offset = new Vector2(30 + i * 13.5f, 30 + i * 7.25f);   // fractional: AA edges
                h.Frame(root, expectPartial: true, $"move {i}");
                Assert.True(h.Target.LastPixels < W * H / 4, $"move {i}: damage {h.Target.LastPixels} px is not small");
            }
            // Nothing changed: nothing is redrawn, and the frame is still right.
            h.Frame(root, expectPartial: true, "unchanged");
            Assert.Equal(0, h.Target.LastPixels);
        }

        [Fact]
        public void ACaretBlinkingOnAndOffIsExact()
        {
            using var h = new Harness(this, NewRenderer, White);
            var root = new SceneVisual();
            root.Children.Add(Tiles());
            h.Frame(root, expectPartial: false, "first frame");
            for (int i = 0; i < 4; i++)
            {
                // A fresh caret visual each frame, as the hosts build it.
                var caret = new SceneVisual();
                caret.Content.Add(Box(101 + i * 7, 40, 1, 15, 0, 0, 0));
                root.Children.Add(caret);
                h.Frame(root, expectPartial: true, $"caret on {i}");
                root.Children.Remove(caret);
                h.Frame(root, expectPartial: true, $"caret off {i}");
            }
        }

        [Fact]
        public void ABlurNextToTheDamageIsExact()
        {
            using var h = new Harness(this, NewRenderer, White);
            var root = new SceneVisual();
            root.Children.Add(Tiles());
            var blurred = new SceneVisual { Effect = new BlurEffect(6) };
            blurred.Content.Add(Box(120, 80, 60, 40, 20, 20, 160));
            var blurredChild = new SceneVisual { Offset = new Vector2(130, 90) };
            blurredChild.Content.Add(Box(0, 0, 10, 10, 250, 250, 0));
            blurred.Children.Add(blurredChild);
            root.Children.Add(blurred);
            var shadowed = new SceneVisual { Effect = new DropShadowEffect(RgbaColor.FromBytes(0, 0, 0, 160), 5, 6, 4) };
            shadowed.Content.Add(Box(200, 150, 50, 30, 250, 250, 120));
            var shadowedChild = new SceneVisual { Offset = new Vector2(205, 155) };
            shadowedChild.Content.Add(Box(0, 0, 8, 8, 0, 0, 0));
            shadowed.Children.Add(shadowedChild);
            root.Children.Add(shadowed);
            var mover = new SceneVisual { Offset = new Vector2(100, 70) };
            mover.Content.Add(Box(0, 0, 12, 12, 0, 140, 0));
            root.Children.Add(mover);

            h.Frame(root, expectPartial: false, "first frame");
            // Something small moving through the blur's halo, and the blurred content itself changing:
            // the second must spread the damage by the blur radius, the shadow's by its offset too.
            for (int i = 1; i <= 5; i++)
            {
                mover.Offset = new Vector2(100 + i * 9, 70 + i * 3);
                h.Frame(root, expectPartial: true, $"mover {i}");
            }
            // Something INSIDE an effect changing: its damage spreads by the blur, and a shadow's
            // by its offset as well.
            for (int i = 1; i <= 3; i++)
            {
                blurredChild.Offset = new Vector2(130 + i * 12, 90 + i * 6);
                shadowedChild.Offset = new Vector2(205 + i * 9, 155 + i * 5);
                h.Frame(root, expectPartial: true, $"inside effect {i}");
            }
            blurred.Content[0] = Box(122, 80, 60, 40, 160, 20, 20);
            h.Frame(root, expectPartial: true, "blurred content changed");
            shadowed.Content[0] = Box(204, 150, 50, 30, 120, 250, 250);
            h.Frame(root, expectPartial: true, "shadowed content changed");
        }

        [Fact]
        public void ClearTypeTextOverABackgroundBeyondTheDamageIsExact()
        {
            var font = TestFonts.Load();
            using var h = new Harness(this, () => NewRenderer(font), White);
            var root = new SceneVisual();
            // The paper under the text reaches far outside any damage, and a sibling window's worth of
            // something else sits right beside the run -- what the paper search must still see.
            var panel = new SceneVisual();
            panel.Content.Add(Box(10, 10, 300, 60, 236, 233, 216));
            // A band of another colour under the END of the run, drawn between the paper and the text
            // by a visual of its own: far from the caret, so a partial frame skips it -- and the run's
            // paper is not one colour because of it, which only a frame that walked it knows.
            var band = new SceneVisual();
            // Rounded, so the paper search cannot resolve it pixel by pixel: the whole run's blend
            // changes with it, the part beside the caret included.
            band.Content.Add(new GeometryFill(new RoundedRectangleGeometry(new Rect(230, 30, 80, 20), 4, 4),
                RgbaColor.FromBytes(200, 220, 250, 255)));
            panel.Children.Add(band);
            var text = new SceneVisual();
            text.Content.Add(new GlyphRunDraw("Partial redraw keeps ClearType paper", new Vector2(16, 44), 14f, RgbaColor.FromBytes(0, 0, 0, 255)));
            panel.Children.Add(text);
            root.Children.Add(panel);
            var neighbour = new SceneVisual { Clip = new Rect(10, 72, 300, 40) };
            neighbour.Content.Add(Box(10, 72, 300, 40, 40, 90, 160));
            neighbour.Content.Add(new GlyphRunDraw("A second window under the first", new Vector2(16, 98), 13f, RgbaColor.FromBytes(255, 255, 255, 255)));
            root.Children.Add(neighbour);
            h.Frame(root, expectPartial: false, "first frame");
            int retries = WgpuSceneRenderer.PerfCullRetries, culled = WgpuSceneRenderer.PerfCulledVisuals;
            for (int i = 0; i < 4; i++)
            {
                var caret = new SceneVisual();
                caret.Content.Add(Box(60 + i * 31, 30, 1, 17, 0, 0, 0));
                root.Children.Add(caret);
                h.Frame(root, expectPartial: true, $"caret in text {i}");
                root.Children.Remove(caret);
                h.Frame(root, expectPartial: true, $"caret gone {i}");
            }
            // The caret's damage takes in the whole run (its paper decides every pixel of it), and
            // through it the band; the window below is still skipped. Because the damage covers every
            // run it touches, no paper question reaches a skipped visual and nothing is collected twice.
            Assert.Equal(retries, WgpuSceneRenderer.PerfCullRetries);
            Assert.True(WgpuSceneRenderer.PerfCulledVisuals > culled, "nothing was skipped");
            // The band going away and coming back square: what is under the END of the run decides
            // how ALL of it is blended, the part far from the band included.
            band.Content.Clear();
            h.Frame(root, expectPartial: true, "band gone");
            band.Content.Add(Box(230, 30, 80, 20, 200, 220, 250));
            h.Frame(root, expectPartial: true, "band square");
            band.Content[0] = new GeometryFill(new RoundedRectangleGeometry(new Rect(230, 30, 80, 20), 4, 4),
                RgbaColor.FromBytes(200, 220, 250, 255));
            h.Frame(root, expectPartial: true, "band rounded");
            // The text itself changing colour.
            text.Content[0] = new GlyphRunDraw("Partial redraw keeps ClearType paper", new Vector2(16, 44), 14f, RgbaColor.FromBytes(120, 0, 0, 255));
            h.Frame(root, expectPartial: true, "text recoloured");
        }

        [Fact]
        public void ATransparentTargetIsExact()
        {
            using var h = new Harness(this, NewRenderer, new RgbaColor(0, 0, 0, 0), transparent: true);
            var root = new SceneVisual();
            var card = new SceneVisual();
            card.Content.Add(Box(20, 20, 200, 150, 250, 250, 250, 230));
            card.Content.Add(Box(40, 40, 50, 50, 30, 120, 220, 128));
            root.Children.Add(card);
            var mover = new SceneVisual { Offset = new Vector2(60, 60), Opacity = 0.6 };
            mover.Content.Add(Box(0, 0, 30, 30, 200, 0, 0));
            root.Children.Add(mover);
            h.Frame(root, expectPartial: false, "first frame");
            for (int i = 1; i <= 4; i++)
            {
                // Out over the transparent part too: the cleared damage there must be exactly 0,0,0,0.
                mover.Offset = new Vector2(60 + i * 45.5f, 60 + i * 20);
                h.Frame(root, expectPartial: true, $"move {i}");
            }
        }

        [Fact]
        public void AShiftedCachedLayerIsExact()
        {
            using var h = new Harness(this, NewRenderer, White);
            var root = new SceneVisual();
            root.Children.Add(Tiles());
            // A translucent group of two overlapping children is an offscreen layer, cached by
            // content and composited where the group now sits.
            var group = new SceneVisual { Offset = new Vector2(40, 40), Opacity = 0.5 };
            group.Content.Add(Box(0, 0, 50, 40, 255, 0, 0));
            group.Content.Add(Box(20, 15, 50, 40, 0, 0, 255));
            root.Children.Add(group);
            var clipGroup = new SceneVisual { Offset = new Vector2(180, 120), ClipGeometry = Circle(30, 30, 28) };
            clipGroup.Content.Add(Box(0, 0, 60, 60, 0, 160, 80));
            root.Children.Add(clipGroup);
            h.Frame(root, expectPartial: false, "first frame");
            for (int i = 1; i <= 4; i++)
            {
                group.Offset = new Vector2(40 + i * 11, 40 + i * 5);
                clipGroup.Offset = new Vector2(180 - i * 7, 120 + i * 4);
                h.Frame(root, expectPartial: true, $"shift {i}");
            }
        }

        [Fact]
        public void NestedClipsAreExact()
        {
            using var h = new Harness(this, NewRenderer, White);
            var root = new SceneVisual();
            root.Children.Add(Tiles());
            var outer = new SceneVisual { Offset = new Vector2(30, 30), Clip = new Rect(0, 0, 200, 120) };
            outer.Content.Add(Box(0, 0, 200, 120, 220, 220, 255));
            var inner = new SceneVisual { Offset = new Vector2(50, 20), Clip = new Rect(0, 0, 90, 60) };
            inner.Content.Add(Box(0, 0, 90, 60, 255, 240, 200));
            var child = new SceneVisual { Offset = new Vector2(70, 40) };
            child.Content.Add(Box(0, 0, 40, 30, 10, 10, 10));
            inner.Children.Add(child);
            outer.Children.Add(inner);
            root.Children.Add(outer);
            // A sibling the clips cut through, so a culled subtree sits beside the damage.
            var other = new SceneVisual { Offset = new Vector2(240, 160), Clip = new Rect(0, 0, 60, 60) };
            other.Content.Add(Box(-20, -20, 100, 100, 90, 0, 90));
            root.Children.Add(other);
            h.Frame(root, expectPartial: false, "first frame");
            for (int i = 1; i <= 5; i++)
            {
                // Drifting out past both clips: the damage is the child's old and new bounds, cut by
                // the clips, and nothing outside them may change.
                child.Offset = new Vector2(70 + i * 9, 40 + i * 6);
                h.Frame(root, expectPartial: true, $"child {i}");
            }
            inner.Clip = new Rect(0, 0, 120, 70);
            h.Frame(root, expectPartial: true, "inner clip grew");
            root.Children.Remove(other);
            h.Frame(root, expectPartial: true, "sibling removed");
        }

        private static PathGeometry Circle(float cx, float cy, float r)
        {
            const float k = 0.5522848f;
            var f = new PathFigure(new Vector2(cx + r, cy));
            f.Segments.Add(new CubicBezierSegment(new Vector2(cx + r, cy + k * r), new Vector2(cx + k * r, cy + r), new Vector2(cx, cy + r)));
            f.Segments.Add(new CubicBezierSegment(new Vector2(cx - k * r, cy + r), new Vector2(cx - r, cy + k * r), new Vector2(cx - r, cy)));
            f.Segments.Add(new CubicBezierSegment(new Vector2(cx - r, cy - k * r), new Vector2(cx - k * r, cy - r), new Vector2(cx, cy - r)));
            f.Segments.Add(new CubicBezierSegment(new Vector2(cx + k * r, cy - r), new Vector2(cx + r, cy - k * r), new Vector2(cx + r, cy)));
            return new PathGeometry(FillRule.NonZero, new List<PathFigure> { f });
        }
    }
}
