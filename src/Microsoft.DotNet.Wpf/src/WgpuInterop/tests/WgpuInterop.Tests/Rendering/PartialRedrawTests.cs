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
            int diff = 0, first = -1, x0 = W, y0 = H, x1 = -1, y1 = -1;
            for (int i = 0; i < want.Length; i++)
                if (want[i] != got[i])
                {
                    diff++; if (first < 0) first = i;
                    int q = i / 4;
                    x0 = Math.Min(x0, q % W); x1 = Math.Max(x1, q % W); y0 = Math.Min(y0, q / W); y1 = Math.Max(y1, q / W);
                }
            if (diff == 0) return;
            if (Environment.GetEnvironmentVariable("PARTIAL_TEST_DUMP") is { } dump)
            {
                PngWriter.Write(System.IO.Path.Combine(dump, "want.png"), want, W, H, int.MaxValue);
                PngWriter.Write(System.IO.Path.Combine(dump, "got.png"), got, W, H, int.MaxValue);
            }
            int p = first / 4;
            Assert.Fail($"{what}: {diff} bytes differ in ({x0},{y0})-({x1},{y1}); first at ({p % W},{p / W}) " +
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

        /// <summary>The gallery's fps badge: a translucent rounded panel with a text run, pinned
        /// top-right as the LAST child (top of the z-order), over content that animates beneath it,
        /// its text changing on its own clock. Damage under it must redraw it on top, and its text
        /// changing must damage its real device rectangle.</summary>
        [Fact]
        public void AnOverlayOverAnimatedContentIsExact()
        {
            var font = TestFonts.Load();
            using var h = new Harness(this, () => NewRenderer(font), White);
            var root = new SceneVisual();
            var scroller = new SceneVisual { Clip = new Rect(0, 0, W, H) };
            var content = new SceneVisual();
            content.Children.Add(Tiles());
            scroller.Children.Add(content);
            var card = new SceneVisual { Offset = new Vector2(200, 4) };
            card.Content.Add(Box(0, 0, 90, 40, 30, 160, 90));
            content.Children.Add(card);
            root.Children.Add(scroller);
            // Right/top aligned with a margin, as layout would place it.
            var badge = new SceneVisual { Offset = new Vector2(W - 14 - 70, 12) };
            badge.Content.Add(new GeometryFill(new RoundedRectangleGeometry(new Rect(0, 0, 70, 24), 8, 8),
                RgbaColor.FromBytes(0x11, 0x18, 0x27, 0xB8)));
            var badgeText = new SceneVisual { Offset = new Vector2(10, 4) };
            badgeText.Content.Add(new GlyphRunDraw("66 fps", new Vector2(0, 13), 13f, RgbaColor.FromBytes(255, 255, 255, 255)));
            badge.Children.Add(badgeText);
            root.Children.Add(badge);

            h.Frame(root, expectPartial: false, "first frame");
            for (int i = 1; i <= 6; i++)
            {
                // Something animating straight through the badge's rectangle, beneath it.
                card.Offset = new Vector2(200 + i * 4.5f, 4 + i * 2);
                h.Frame(root, expectPartial: true, $"under the badge {i}");
                if (i % 2 == 0)
                {
                    badgeText.Content[0] = new GlyphRunDraw($"{60 + i * 7} fps", new Vector2(0, 13), 13f, RgbaColor.FromBytes(255, 255, 255, 255));
                    h.Frame(root, expectPartial: true, $"badge text {i}");
                }
            }
            // The text changing with nothing else moving.
            badgeText.Content[0] = new GlyphRunDraw("128 fps", new Vector2(0, 13), 13f, RgbaColor.FromBytes(255, 255, 255, 255));
            h.Frame(root, expectPartial: true, "badge text alone");
            Assert.True(h.Target.LastPixels > 0 && h.Target.LastPixels < W * H / 4, $"badge damage {h.Target.LastPixels} px");
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

        // ---- scrolling -------------------------------------------------------------------------

        /// <summary>A scroll viewer as WPF builds one: a window background, a clipped viewport at a
        /// FRACTIONAL position (a header of measured text height above it), and in it a long content
        /// visual of rows -- rounded fills, ClearType text, a drop-shadowed card every few rows -- that
        /// the scroll moves. Over it all, last in z-order, a translucent badge that does not move.</summary>
        private sealed class ScrollScene
        {
            internal readonly SceneVisual Root = new(), Viewport, Content = new(), Badge, Row3 = new();
            internal const float Top = 22.37f;

            internal ScrollScene(bool withText)
            {
                Root.Content.Add(Box(0, 0, W, H, 236, 239, 243));                  // the window background
                var header = new SceneVisual();
                header.Content.Add(Box(0, 0, W, Top, 40, 60, 90));
                Root.Children.Add(header);
                Viewport = new SceneVisual { Offset = new Vector2(0, Top), Clip = new Rect(8, 0, W - 30, H - Top - 6) };
                for (int i = 0; i < 24; i++)
                {
                    SceneVisual row = i == 3 ? Row3 : new SceneVisual();
                    row.Offset = new Vector2(12, 4 + i * 27.5f);
                    row.Content.Add(new GeometryFill(new RoundedRectangleGeometry(new Rect(0, 0, 240, 23), 5, 5),
                        RgbaColor.FromBytes((byte)(250 - i * 5), 250, (byte)(200 + i * 2), 255)));
                    if (withText)
                        row.Content.Add(new GlyphRunDraw($"Row {i}: scrolled, not redrawn", new Vector2(8, 16), 13f, RgbaColor.FromBytes(20, 20, 30, 255)));
                    if (i % 5 == 2)
                    {
                        var card = new SceneVisual { Offset = new Vector2(180, 2), Effect = new DropShadowEffect(RgbaColor.FromBytes(0, 0, 0, 120), 4, 2, 2) };
                        card.Content.Add(Box(0, 0, 40, 17, 60, 120, 220));
                        row.Children.Add(card);
                    }
                    Content.Children.Add(row);
                }
                Viewport.Children.Add(Content);
                Root.Children.Add(Viewport);
                // A scroll bar thumb: outside the viewport, and it moves -- ordinary damage.
                Root.Children.Add(new SceneVisual());
                Badge = new SceneVisual { Offset = new Vector2(W - 100, 40) };
                Badge.Content.Add(new GeometryFill(new RoundedRectangleGeometry(new Rect(0, 0, 70, 24), 8, 8),
                    RgbaColor.FromBytes(0x11, 0x18, 0x27, 0xB8)));
                Root.Children.Add(Badge);
                ScrollTo(0);
            }

            internal void ScrollTo(float offset)
            {
                Content.Offset = new Vector2(0, -offset);
                SceneVisual thumb = Root.Children[2];
                thumb.Content.Clear();
                thumb.Content.Add(Box(W - 18, Top + 4 + offset * 0.25f, 10, 40, 140, 140, 150));
            }
        }

        [Fact]
        public void AWholePixelScrollShiftsAndIsExact()
        {
            var font = TestFonts.Load();
            using var h = new Harness(this, () => NewRenderer(font), White);
            var s = new ScrollScene(withText: true);
            h.Frame(s.Root, expectPartial: false, "first frame");
            int shifted = 0;
            // Even steps shift. An odd one does not (positions round half to even, so content on a
            // tie would land a pixel differently): it is redrawn, still exactly.
            float[] steps = { 8, 12, 2, 30, -12, -4, 24, 3, -10, 14, 8, -8 };
            float off = 0;
            foreach (float step in steps)
            {
                off += step;
                s.ScrollTo(off);
                bool odd = ((int)step & 1) != 0;
                h.Frame(s.Root, expectPartial: !odd, $"scroll to {off}");
                if (odd) Assert.Null(h.Target.LastShift);
                if (h.Target.LastShift is { } sh)
                {
                    shifted++;
                    Assert.Equal(0, sh.Dx);
                    Assert.Equal(-(int)step, sh.Dy);
                    // Less than the viewport: the exposed strip, the header's edge, the badge (where it
                    // is and where the shift dragged it) and the text runs those touch -- which in a
                    // viewport this small, with a run's box three ems tall, is still a good part of it.
                    Assert.True(h.Target.LastPixels < 290 * 212, $"scroll {off}: damage {h.Target.LastPixels} px");
                }
            }
            Assert.True(shifted >= steps.Length - 1, $"only {shifted} of {steps.Length} scrolls were shifted");
        }

        [Fact]
        public void AScrollWithAChangeInsideAndAMovingOverlayIsExact()
        {
            var font = TestFonts.Load();
            using var h = new Harness(this, () => NewRenderer(font), White);
            var s = new ScrollScene(withText: true);
            h.Frame(s.Root, expectPartial: false, "first frame");
            int shifted = 0;
            for (int i = 1; i <= 8; i++)
            {
                s.ScrollTo(i * 6);
                // A row's content changing as it scrolls (a hover, a selection) ...
                s.Row3.Content[0] = new GeometryFill(new RoundedRectangleGeometry(new Rect(0, 0, 240, 23), 5, 5),
                    RgbaColor.FromBytes((byte)(i * 30), 120, 200, 255));
                // ... a row added at the end, and the badge drifting over the viewport.
                if (i == 4)
                {
                    var extra = new SceneVisual { Offset = new Vector2(12, 4 + 24 * 27.5f) };
                    extra.Content.Add(Box(0, 0, 240, 23, 200, 0, 0));
                    s.Content.Children.Add(extra);
                }
                if (i % 3 == 0) s.Badge.Offset = new Vector2(W - 100 - i * 3, 40 + i);
                h.Frame(s.Root, expectPartial: false, $"scroll+change {i}");
                if (h.Target.LastShift != null) shifted++;
            }
            // Exact on every frame; and with this much changing besides, a shift on some of them
            // (it is taken only when it touches fewer pixels than redrawing the move would).
            Assert.True(shifted > 0, "no frame was shifted");
        }

        [Fact]
        public void ScrollsThatCannotBeShiftedAreStillExact()
        {
            var font = TestFonts.Load();
            using var h = new Harness(this, () => NewRenderer(font), White);
            var s = new ScrollScene(withText: true);
            h.Frame(s.Root, expectPartial: false, "first frame");
            // Fractional: every edge lands on a new sub-pixel phase; nothing is shifted.
            s.ScrollTo(3.5f);
            h.Frame(s.Root, expectPartial: false, "fractional");
            Assert.Null(h.Target.LastShift);
            s.ScrollTo(10.25f);
            h.Frame(s.Root, expectPartial: false, "fractional again");
            Assert.Null(h.Target.LastShift);
            // A move that also scales.
            s.Content.Transform = Matrix3x2.CreateScale(1f, 1.5f) * Matrix3x2.CreateTranslation(0, -20);
            h.Frame(s.Root, expectPartial: false, "scaled");
            Assert.Null(h.Target.LastShift);
            s.Content.Transform = Matrix3x2.Identity;
            s.ScrollTo(20);
            h.Frame(s.Root, expectPartial: false, "back to plain");
            // Under an effect: the blurred pixels are not where the content put them.
            s.Viewport.Effect = new BlurEffect(3);
            h.Frame(s.Root, expectPartial: false, "blurred viewport");
            s.ScrollTo(30);
            h.Frame(s.Root, expectPartial: false, "scroll under a blur");
            Assert.Null(h.Target.LastShift);
            s.Viewport.Effect = null;
            h.Frame(s.Root, expectPartial: false, "blur gone");
            // Under a group opacity.
            s.Viewport.Opacity = 0.8;
            h.Frame(s.Root, expectPartial: false, "translucent viewport");
            s.ScrollTo(42);
            h.Frame(s.Root, expectPartial: false, "scroll under opacity");
            Assert.Null(h.Target.LastShift);
            s.Viewport.Opacity = 1;
            h.Frame(s.Root, expectPartial: false, "opaque again");
            // And whole pixels once more: shifted.
            s.ScrollTo(50);
            h.Frame(s.Root, expectPartial: true, "whole pixels");
            Assert.NotNull(h.Target.LastShift);
        }

        [Fact]
        public void AScrollOverAPatternedBackgroundIsExact()
        {
            // Nothing uniform under the viewport: the tiles behind it do not move, so every tile the
            // viewport shows is damage, twice. Exact whatever it decides.
            using var h = new Harness(this, NewRenderer, White);
            var root = new SceneVisual();
            root.Children.Add(Tiles());
            var viewport = new SceneVisual { Clip = new Rect(40, 30, 200, 150) };
            var content = new SceneVisual();
            for (int i = 0; i < 12; i++)
                content.Content.Add(Box(50 + (i % 3) * 60, 35 + i * 25, 50, 18, 20, (byte)(i * 20), 200, 160));
            viewport.Children.Add(content);
            root.Children.Add(viewport);
            h.Frame(root, expectPartial: false, "first frame");
            for (int i = 1; i <= 6; i++)
            {
                content.Offset = new Vector2(i % 2 == 0 ? 0 : 3, -i * 11);
                h.Frame(root, expectPartial: false, $"scroll {i}");
            }
        }

        // ---- scroll snapping: whole pixels while moving, the true offset once settled ----------

        private static long Ms(int ms) => ms * System.Diagnostics.Stopwatch.Frequency / 1000;

        /// <summary>World translation of the scroll content as drawn.</summary>
        private static Vector2 DrawnAt(ScrollScene s)
        {
            Matrix3x2 w = s.Content.LocalToParent * s.Viewport.LocalToParent;
            return new Vector2(w.M31, w.M32);
        }

        [Fact]
        public void AFractionalScrollInMotionIsDrawnOnWholePixelsAndShifted()
        {
            var font = TestFonts.Load();
            using var h = new Harness(this, () => NewRenderer(font), White);
            long now = Ms(1000);
            h.Target.SnapScroll = true;
            h.Target.Clock = () => now;
            var s = new ScrollScene(withText: true);
            h.Frame(s.Root, expectPartial: false, "first frame");
            float rest = DrawnAt(s).Y;              // a fractional phase: the viewport is at 22.37
            int shifted = 0;
            float off = 0;
            for (int i = 1; i <= 10; i++)
            {
                now += Ms(16);
                off += 3.37f;                     // a trackpad's sub-pixel steps
                s.ScrollTo(off);
                // Exact against a full render of what is drawn -- the snapped scene.
                h.Frame(s.Root, expectPartial: true, $"moving to {off}");
                // Drawn an even number of whole pixels from where it rested, within a pixel of where
                // it really is.
                float moved = DrawnAt(s).Y - rest;
                Assert.True(MathF.Abs(moved - 2 * MathF.Round(moved / 2)) < 1e-3f, $"drawn {moved} px from rest");
                Assert.True(MathF.Abs(s.Content.SnapNudge.Y) <= 1f + 1e-4f, $"nudge {s.Content.SnapNudge.Y}");
                Assert.NotEqual(0, h.Target.SettleDue);
                if (h.Target.LastShift != null) shifted++;
            }
            Assert.True(shifted >= 9, $"only {shifted} of 10 moving frames were shifted");
        }

        [Fact]
        public void ASettledScrollIsDrawnAtItsTrueOffsetExactly()
        {
            var font = TestFonts.Load();
            using var h = new Harness(this, () => NewRenderer(font), White);
            long now = Ms(1000);
            h.Target.SnapScroll = true;
            h.Target.Clock = () => now;
            var s = new ScrollScene(withText: true);
            h.Frame(s.Root, expectPartial: false, "first frame");
            for (int i = 1; i <= 6; i++)
            {
                now += Ms(16);
                s.ScrollTo(i * 2.71f);
                h.Frame(s.Root, expectPartial: true, $"moving {i}");
            }
            // Still, but not for long enough: still snapped, nothing to draw.
            now += Ms(40);
            h.Frame(s.Root, expectPartial: true, "still, not settled");
            Assert.NotEqual(0f, s.Content.SnapNudge.Y);
            Assert.Equal(0, h.Target.LastPixels);
            // The settle frame: the true, fractional offset. Harness.Frame compares it byte for byte
            // with a fresh renderer drawing the scene with no nudge -- a never-snapped render.
            now = h.Target.SettleDue;
            h.Frame(s.Root, expectPartial: false, "settled");
            Assert.Equal(Vector2.Zero, s.Content.SnapNudge);
            Assert.Null(h.Target.LastShift);
            Assert.Equal(0, h.Target.SettleDue);
            Vector2 at = DrawnAt(s);
            Assert.True(MathF.Abs(at.Y - MathF.Round(at.Y)) > 1e-3f, "the true offset is fractional");
            // And a target that never snapped, shown the same final scene, has the same pixels.
            using var never = new Harness(this, () => NewRenderer(font), White);
            never.Frame(s.Root, expectPartial: false, "never snapped");
            // Settled and nothing moves: nothing more is drawn, and the image stays the true one.
            now += Ms(500);
            h.Frame(s.Root, expectPartial: true, "after settling");
            Assert.Equal(0, h.Target.LastPixels);
        }

        [Fact]
        public void SnappingIsRenderOnlyAndHitTestingSeesWhatIsDrawn()
        {
            var font = TestFonts.Load();
            using var h = new Harness(this, () => NewRenderer(font), White);
            long now = Ms(1000);
            h.Target.SnapScroll = true;
            h.Target.Clock = () => now;
            var s = new ScrollScene(withText: false);
            h.Frame(s.Root, expectPartial: false, "first frame");
            now += Ms(16);
            s.ScrollTo(5.4f);
            h.Frame(s.Root, expectPartial: true, "moving");
            // The scene still says where layout put it; only what is drawn moved, by at most one
            // device pixel (an even number of pixels from rest: 5.4 -> 6, 0.6 px).
            Assert.Equal(new Vector2(0, -5.4f), s.Content.Offset);
            Assert.True(MathF.Abs(s.Content.SnapNudge.Y) <= 1f, $"nudge {s.Content.SnapNudge.Y}");
            // The renderer's hit test reads the same transforms the frame was drawn with: a pixel
            // inside a row as drawn is that row, while it moves.
            uint id = 0;
            foreach (SceneVisual row in s.Content.Children) row.Id = ++id;
            SceneVisual row1 = s.Content.Children[1];
            Matrix3x2 drawn = row1.LocalToParent * s.Content.LocalToParent * s.Viewport.LocalToParent;
            int px = (int)(drawn.M31 + 100), py = (int)MathF.Floor(drawn.M32 + 0.5f);   // the row's first drawn line
            Assert.Equal(row1.Id, h.Renderer.HitTest(s.Root, px, py, W, H));
            // Settled, nothing is nudged and the same point is hit-tested against the true offset.
            now += Ms(1000);
            h.Frame(s.Root, expectPartial: false, "settled");
            Assert.Equal(Vector2.Zero, s.Content.SnapNudge);
        }

        // ---- WinForms-style repaints: fresh recordings compared by value ------------------------

        /// <summary>A grid as a WinForms control records it on every paint: a window visual with
        /// its clip, a background fill, then per row a clipped cell container holding a fill, a grid
        /// line and the row's text; a header on top. EVERY object is new on every call, as a repaint
        /// makes them. <paramref name="first"/> is the first row shown; rows are 20 px.</summary>
        private static SceneVisual RecordedGrid(int first, Func<int, RgbaColor>? rowColour = null, string? header = "Header")
        {
            var window = new SceneVisual { Offset = new Vector2(20, 16), Clip = new Rect(0, 0, 260, 200) };
            window.Content.Add(Box(0, 0, 260, 200, 255, 255, 255));
            for (int i = first; i < first + 12; i++)
            {
                float y = 24 + (i - first) * 20;
                RgbaColor c = rowColour?.Invoke(i) ?? RgbaColor.FromBytes((byte)(200 + i % 5 * 10), 240, (byte)(180 + i * 7 % 70), 255);
                var cell = new SceneVisual { Clip = new Rect(0, y, 240, 20) };
                cell.Content.Add(new GeometryFill(new RectangleGeometry(new Rect(2, y + 1, 236, 18)), c));
                cell.Content.Add(new GeometryFill(new RectangleGeometry(new Rect(0, y + 19, 240, 1)), RgbaColor.FromBytes(160, 160, 160, 255)));
                cell.Content.Add(new GlyphRunDraw($"row {i}", new Vector2(8, y + 15), 12f, RgbaColor.FromBytes(10, 10, 10, 255)));
                window.Children.Add(cell);
            }
            // Drawn after the rows: a header that does not scroll, and a scroll bar.
            var top = new SceneVisual();
            top.Content.Add(Box(0, 0, 260, 24, 210, 220, 235));
            if (header != null) top.Content.Add(new GlyphRunDraw(header, new Vector2(8, 17), 12f, RgbaColor.FromBytes(0, 0, 0, 255)));
            top.Content.Add(Box(242, 24 + first * 3, 16, 30, 120, 120, 120));
            window.Children.Add(top);
            return window;
        }

        /// <summary>The host's stable root, with each paint's recording put in it.</summary>
        private sealed class GridHost
        {
            private readonly SceneVisual _root = new();
            internal GridHost() => _root.Content.Add(Box(0, 0, W, H, 230, 230, 230));
            internal SceneVisual Show(SceneVisual window) { _root.Children.Clear(); _root.Children.Add(window); return _root; }
        }

        [Fact]
        public void ARepaintedScrollingGridShiftsAndIsExact()
        {
            var font = TestFonts.Load();
            using var h = new Harness(this, () => NewRenderer(font), White);
            var host = new GridHost();
            h.Frame(host.Show(RecordedGrid(0)), expectPartial: false, "first frame");
            int shifted = 0;
            int[] firsts = { 1, 2, 4, 3, 6, 5, 5, 9, 8 };
            foreach (int f in firsts)
            {
                h.Frame(host.Show(RecordedGrid(f)), expectPartial: true, $"rows from {f}");
                if (h.Target.LastShift is { } sh)
                {
                    shifted++;
                    Assert.Equal(0, sh.Dx);
                    Assert.True(sh.Dy % 20 == 0, $"shift {sh.Dy}");
                    // One row a step: the exposed row, the header and the scroll bar, not the window.
                    if (f == 2) Assert.True(h.Target.LastPixels < 260 * 200 / 2, $"rows from {f}: damage {h.Target.LastPixels} px");
                }
            }
            Assert.True(shifted >= 4, $"only {shifted} repaints were shifted");
        }

        [Fact]
        public void ARepaintOfTheSameThingDamagesOnlyWhatDiffers()
        {
            var font = TestFonts.Load();
            using var h = new Harness(this, () => NewRenderer(font), White);
            var host = new GridHost();
            h.Frame(host.Show(RecordedGrid(3)), expectPartial: false, "first frame");
            // Every object new, every value the same: nothing to draw.
            h.Frame(host.Show(RecordedGrid(3)), expectPartial: true, "identical repaint");
            Assert.Equal(0, h.Target.LastPixels);
            // One row's colour changed: that row, not the window.
            h.Frame(host.Show(RecordedGrid(3, i => i == 6 ? RgbaColor.FromBytes(250, 120, 120, 255) : RgbaColor.FromBytes((byte)(200 + i % 5 * 10), 240, (byte)(180 + i * 7 % 70), 255))),
                expectPartial: true, "one row recoloured");
            Assert.True(h.Target.LastPixels > 0 && h.Target.LastPixels < 260 * 200 / 3, $"one row: damage {h.Target.LastPixels} px");
            // The header text changed.
            h.Frame(host.Show(RecordedGrid(3, header: "Header 2")), expectPartial: true, "header text");
            h.Frame(host.Show(RecordedGrid(3, header: null)), expectPartial: true, "header text gone");
        }

        [Fact]
        public void RepaintsThatAreNotScrollsAreStillExact()
        {
            var font = TestFonts.Load();
            using var h = new Harness(this, () => NewRenderer(font), White);
            var host = new GridHost();
            h.Frame(host.Show(RecordedGrid(0)), expectPartial: false, "first frame");
            // Uniform rows: every row looks alike but its text, so many offsets look plausible.
            for (int f = 1; f <= 6; f++)
                h.Frame(host.Show(RecordedGrid(f, _ => RgbaColor.FromBytes(240, 240, 255, 255))), expectPartial: false, $"uniform rows from {f}");
            // A jump further than the window: nothing in common.
            h.Frame(host.Show(RecordedGrid(40)), expectPartial: false, "jump");
            // And back to coloured rows, one step.
            h.Frame(host.Show(RecordedGrid(41)), expectPartial: false, "coloured again");
            h.Frame(host.Show(RecordedGrid(42)), expectPartial: false, "one step");
        }

        /// <summary>A shadowed card entirely outside a viewport's clip still casts its shadow into it
        /// (the effect spreads the content before the clip). A partial frame whose damage is under
        /// that shadow must not skip the card.</summary>
        [Fact]
        public void AShadowCastFromBeyondTheClipIsExact()
        {
            using var h = new Harness(this, NewRenderer, White);
            var root = new SceneVisual();
            root.Content.Add(Box(0, 0, W, H, 236, 239, 243));
            var viewport = new SceneVisual { Clip = new Rect(20, 60, 260, 150) };
            var card = new SceneVisual { Offset = new Vector2(40, 20), Effect = new DropShadowEffect(RgbaColor.FromBytes(0, 0, 0, 200), 8, 0, 12) };
            card.Content.Add(Box(0, 0, 120, 36, 255, 255, 255));        // ends at y=56: above the clip
            viewport.Children.Add(card);
            var mover = new SceneVisual { Offset = new Vector2(60, 62) };
            mover.Content.Add(Box(0, 0, 10, 6, 200, 40, 40));
            viewport.Children.Add(mover);
            root.Children.Add(viewport);
            h.Frame(root, expectPartial: false, "first frame");
            for (int i = 1; i <= 4; i++)
            {
                mover.Offset = new Vector2(60 + i * 13, 62 + (i % 2));
                h.Frame(root, expectPartial: true, $"under the shadow {i}");
            }
            // The card moving while wholly clipped away: its shadow inside moves.
            card.Offset = new Vector2(46, 18);
            h.Frame(root, expectPartial: true, "clipped card moved");
        }

        [Fact]
        public void DamageRectanglesAreDisjointAndCoverEverything()
        {
            var rng = new Random(7);
            for (int trial = 0; trial < 300; trial++)
            {
                var raw = new List<WgpuSceneRenderer.Scissor>();
                int n = rng.Next(1, 60);
                for (int i = 0; i < n; i++)
                {
                    // Long strips across, small boxes, overlapping and nested ones.
                    int kind = rng.Next(3);
                    int x = rng.Next(-20, W), y = rng.Next(-20, H);
                    int w = kind == 0 ? W : rng.Next(1, 120), hh = kind == 1 ? rng.Next(1, 6) : rng.Next(1, 90);
                    raw.Add(new WgpuSceneRenderer.Scissor(x, y, w, hh));
                }
                var rects = new List<WgpuSceneRenderer.Scissor>();
                WgpuSceneRenderer.DamageTracker.Normalize(raw, rects, W, H);
                var cover = new int[W * H];
                foreach (var r in rects)
                {
                    Assert.True(r.X >= 0 && r.Y >= 0 && r.X + r.W <= W && r.Y + r.H <= H, $"trial {trial}: outside the target");
                    for (int y = r.Y; y < r.Y + r.H; y++)
                        for (int x = r.X; x < r.X + r.W; x++)
                            cover[y * W + x]++;
                }
                foreach (var r in raw)
                    for (int y = Math.Max(0, r.Y); y < Math.Min(H, r.Y + r.H); y++)
                        for (int x = Math.Max(0, r.X); x < Math.Min(W, r.X + r.W); x++)
                            Assert.True(cover[y * W + x] > 0, $"trial {trial}: ({x},{y}) lost");
                foreach (int c in cover) Assert.True(c <= 1, $"trial {trial}: rectangles overlap");
                Assert.True(rects.Count <= 16, $"trial {trial}: {rects.Count} rectangles");
            }
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
