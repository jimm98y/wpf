// Implements the GPU-rasterization seam as PURE SCENE DATA: records System.Drawing.Graphics verbs
// (via IGpuSceneRecorder) into a WgpuInterop SceneVisual. NO GPU work happens here — no device, no
// readback. The driver keeps each window's recorded scene; the WebGPU present path composites all of
// them in ONE render pass on ONE device (no per-control readback / re-upload). Glyph rasterization
// happens at present time in the renderer that owns the font, so DrawText just records a GlyphRunDraw.

using System;
using System.Collections.Generic;
using System.Numerics;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using SceneBrush = Microsoft.Wpf.Interop.WebGpu.Composition.Brush;

namespace System.Drawing.WebGpuBackend
{
    internal sealed class SceneRecorder : IGpuSceneRecorder
    {
        private readonly SceneVisual _root = new SceneVisual();

        internal SceneVisual Scene => _root;

        public SceneRecorder() { }

        // ---- the state stack ---------------------------------------------------------------------
        //
        // What a GDI+ Graphics holds as STATE -- a world transform, a clip set in the device space
        // of the moment it was set, a snapshot being drawn -- the scene can only hold as NESTING:
        // a container visual per piece of state, primitives inside the innermost one. The two do not
        // map one to one. A clip outlives a later change of transform (GDI+ keeps it where it was
        // put), and a transform can be reset while a clip set under it is still in force, which in
        // a tree means pulling a container out from the middle of the stack.
        //
        // So the stack is described rather than built: a list of Specs, each one immutable, in the
        // order bottom to top -- [snapshots and what sits below them...] clip, clip, ..., world.
        // Every change computes the list it wants and Rebuild makes the containers match: the
        // longest prefix that is already right stays (it is the SAME spec objects), everything
        // above it is closed, and the rest is opened afresh as new sibling containers. Content drawn
        // so far stays where it was drawn, in the order it was drawn, and what comes next goes into
        // the new containers -- which is exactly the semantics of changing a Graphics' state.
        //
        // A clip carries the transform that was in force when it was set (its frame), and is built
        // as a container with that transform and the clip, and inside it one with the inverse, so
        // the clip lands where it was set while what is drawn inside it is placed only by the world
        // container above. For a plain translation both are Offsets, as they always were.

        private enum Kind { Clip, World, Snapshot }

        private sealed class Spec
        {
            public Kind Kind;
            public Matrix3x2 Frame = Matrix3x2.Identity;   // world, or a clip's frame, relative to the snapshot below
            public Rect? ClipRect;
            public PathGeometry ClipGeometry;
            public SceneSnapshot Snapshot;
            public float Opacity = 1f;
            public Matrix3x2 BaseWorld = Matrix3x2.Identity;   // a snapshot: the absolute world when it was pushed
        }

        private readonly struct Level
        {
            public readonly Spec Spec;
            public readonly SceneVisual Outer, Inner;
            public Level(Spec spec, SceneVisual outer, SceneVisual inner) { Spec = spec; Outer = outer; Inner = inner; }
        }

        private readonly List<Level> _levels = new();

        // The absolute world transform (GDI+'s, already including the page transform).
        private Matrix3x2 _world = Matrix3x2.Identity;

        private SceneVisual Target => _levels.Count == 0 ? _root : _levels[_levels.Count - 1].Inner;

        // Containers opened only to keep drawing ORDER: a visual draws its own content before its
        // children, so a primitive recorded after a clip was popped, appended to the parent's
        // content, drew UNDER everything drawn inside the clip. It goes into a trailing plain
        // container instead, which draws after the children before it.
        private readonly HashSet<SceneVisual> _continuations = new(ReferenceEqualityComparer.Instance);

        // Graphics.CompositingMode, applied to every primitive recorded from here on.
        private bool _sourceCopy;

        public void SetCompositingMode(bool sourceCopy) => _sourceCopy = sourceCopy;

        // Every primitive goes through here so the current compositing mode is stamped on it
        // exactly once, rather than being threaded through eleven separate append sites.
        private void Add(DrawingPrimitive p)
        {
            p.SourceCopy = _sourceCopy;
            SceneVisual t = Target;
            if (t.Children.Count > 0)
            {
                SceneVisual last = t.Children[t.Children.Count - 1];
                if (!_continuations.Contains(last))
                {
                    last = new SceneVisual();
                    t.Children.Add(last);
                    _continuations.Add(last);
                }
                last.Content.Add(p);
                return;
            }
            t.Content.Add(p);
        }

        private List<Spec> Specs()
        {
            var specs = new List<Spec>(_levels.Count);
            foreach (Level l in _levels) specs.Add(l.Spec);
            return specs;
        }

        // Index of the first spec above the topmost snapshot: below it nothing is ever rebuilt.
        private static int Floor(List<Spec> specs)
        {
            for (int i = specs.Count - 1; i >= 0; i--)
                if (specs[i].Kind == Kind.Snapshot) return i + 1;
            return 0;
        }

        private Matrix3x2 BaseWorld(List<Spec> specs)
        {
            for (int i = specs.Count - 1; i >= 0; i--)
                if (specs[i].Kind == Kind.Snapshot) return specs[i].BaseWorld;
            return Matrix3x2.Identity;
        }

        private void Rebuild(List<Spec> desired)
        {
            int common = 0;
            while (common < _levels.Count && common < desired.Count && ReferenceEquals(_levels[common].Spec, desired[common]))
                common++;
            while (_levels.Count > common) _levels.RemoveAt(_levels.Count - 1);
            for (int i = common; i < desired.Count; i++) Open(desired[i]);
        }

        private void Open(Spec s)
        {
            SceneVisual parent = Target;
            SceneVisual outer, inner;
            switch (s.Kind)
            {
                case Kind.Snapshot:
                    outer = inner = new SceneVisual { Snapshot = s.Snapshot, Opacity = s.Opacity };
                    break;
                case Kind.World:
                    outer = inner = Placed(new SceneVisual(), s.Frame);
                    break;
                default:
                    outer = Placed(new SceneVisual { Clip = s.ClipRect, ClipGeometry = s.ClipGeometry }, s.Frame);
                    inner = outer;
                    if (!s.Frame.IsIdentity && Matrix3x2.Invert(s.Frame, out Matrix3x2 inv))
                    {
                        inner = Placed(new SceneVisual(), inv);
                        outer.Children.Add(inner);
                    }
                    break;
            }
            parent.Children.Add(outer);
            _levels.Add(new Level(s, outer, inner));
        }

        // A translation as an Offset, which is what every container here used to be: anything else
        // as the visual's transform.
        private static SceneVisual Placed(SceneVisual v, Matrix3x2 m)
        {
            if (m.M11 == 1f && m.M12 == 0f && m.M21 == 0f && m.M22 == 1f) v.Offset = new Vector2(m.M31, m.M32);
            else v.Transform = m;
            return v;
        }

        private static Matrix3x2 Relative(Matrix3x2 absolute, Matrix3x2 baseWorld)
            => baseWorld.IsIdentity ? absolute
             : Matrix3x2.Invert(baseWorld, out Matrix3x2 inv) ? absolute * inv : absolute;

        // The desired list with the world on top.
        private List<Spec> WithWorld(List<Spec> specs)
        {
            int floor = Floor(specs);
            for (int i = specs.Count - 1; i >= floor; i--)
                if (specs[i].Kind == Kind.World) specs.RemoveAt(i);
            Matrix3x2 rel = Relative(_world, BaseWorld(specs));
            if (!rel.IsIdentity) specs.Add(new Spec { Kind = Kind.World, Frame = rel });
            return specs;
        }

        /// <summary>The world transform (GDI+'s six elements, page transform included) of
        /// everything recorded from here on.</summary>
        public void SetWorldTransform(float m11, float m12, float m21, float m22, float dx, float dy)
        {
            var w = new Matrix3x2(m11, m12, m21, m22, dx, dy);
            if (w == _world) return;
            _world = w;
            Rebuild(WithWorld(Specs()));
        }

        private void PushClip(Rect? rect, PathGeometry geometry)
        {
            List<Spec> specs = Specs();
            var clip = new Spec
            {
                Kind = Kind.Clip, ClipRect = rect, ClipGeometry = geometry,
                Frame = Relative(_world, BaseWorld(specs)),
            };
            // Below the world container, so a later transform does not carry the clip with it.
            int at = specs.Count;
            if (at > Floor(specs) && specs[at - 1].Kind == Kind.World) at--;
            specs.Insert(at, clip);
            Rebuild(specs);
        }

        public void SetClipRect(float x, float y, float w, float h, bool exclude)
        {
            if (exclude)
            {
                // "everything except this rect": a huge outer rect + the excluded rect, even-odd ->
                // fills the ring. Used to gap the GroupBox border around its title.
                const float Big = 1 << 20;
                PushClip(null, new PathGeometry(FillRule.EvenOdd, new List<PathFigure>
                {
                    RectFigure(-Big, -Big, Big * 2, Big * 2),
                    RectFigure(x, y, w, h),
                }));
            }
            else
            {
                PushClip(new Rect(x, y, w, h), null);   // intersect (fast scissor)
            }
        }

        /// <summary>Clip to a path's region (GDI+'s points and type bytes); with
        /// <paramref name="exclude"/>, to everything outside it.</summary>
        public void SetClipPath(float[] xy, byte[] types, bool nonZero, bool exclude)
        {
            PathGeometry geo = PathData(xy, types, nonZero, closeAll: true);
            if (geo == null) { PushClip(new Rect(0, 0, 0, 0), null); return; }
            if (exclude)
            {
                const float Big = 1 << 20;
                var figures = new List<PathFigure> { RectFigure(-Big, -Big, Big * 2, Big * 2) };
                figures.AddRange(geo.Figures);
                geo = new PathGeometry(FillRule.EvenOdd, figures);
            }
            PushClip(null, geo);
        }

        /// <summary>Removes the clip set last (the counterpart of one SetClip*).</summary>
        public void ClearClip()
        {
            List<Spec> specs = Specs();
            for (int i = specs.Count - 1; i >= Floor(specs); i--)
            {
                if (specs[i].Kind != Kind.Clip) continue;
                specs.RemoveAt(i);
                Rebuild(specs);
                return;
            }
        }

        /// <summary>Removes every clip (above the innermost snapshot): Graphics.ResetClip on a
        /// surface that has no clip of its own beneath what the caller set.</summary>
        public void ResetAllClips()
        {
            List<Spec> specs = Specs();
            int floor = Floor(specs);
            bool any = false;
            for (int i = specs.Count - 1; i >= floor; i--)
                if (specs[i].Kind == Kind.Clip) { specs.RemoveAt(i); any = true; }
            if (any) Rebuild(specs);
        }

        // Kept for callers that think in translations: the world transform moved by (dx, dy) in
        // its own space, which is what GDI+'s prepended TranslateTransform does.
        public void PushTranslate(float dx, float dy)
        {
            Matrix3x2 w = Matrix3x2.CreateTranslation(dx, dy) * _world;
            SetWorldTransform(w.M11, w.M12, w.M21, w.M22, w.M31, w.M32);
        }

        public void ResetTransform() => SetWorldTransform(1, 0, 0, 1, 0, 0);

        public void PushSnapshot(float sx, float sy, float sw, float sh, float dx, float dy, float dw, float dh, float opacity, bool gdiStretch, bool windowBlend)
        {
            List<Spec> specs = Specs();
            specs.Add(new Spec
            {
                Kind = Kind.Snapshot,
                Snapshot = new SceneSnapshot { Source = new Rect(sx, sy, sw, sh), Dest = new Rect(dx, dy, dw, dh), GdiStretch = gdiStretch, WindowBlend = windowBlend },
                Opacity = Math.Clamp(opacity, 0f, 1f),
                BaseWorld = _world,
            });
            Rebuild(specs);
        }

        public void PopSnapshot()
        {
            // Unwind to (and including) the innermost snapshot container, and the world goes back
            // to the one it was pushed under.
            List<Spec> specs = Specs();
            int floor = Floor(specs);
            if (floor == 0) return;
            _world = specs[floor - 1].BaseWorld;
            specs.RemoveRange(floor - 1, specs.Count - (floor - 1));
            Rebuild(specs);
        }

        public void GetTranslation(out float x, out float y)
        {
            // The absolute translation: the world's, inside every snapshot below it.
            Matrix3x2 m = Matrix3x2.Identity;
            for (int i = _levels.Count - 1; i >= 0; i--)
            {
                Level l = _levels[i];
                Matrix3x2 level = ReferenceEquals(l.Inner, l.Outer)
                    ? l.Outer.LocalToParent
                    : l.Inner.LocalToParent * l.Outer.LocalToParent;
                m *= level;
            }
            x = m.M31; y = m.M32;
        }

        private readonly List<(List<Spec> Specs, Matrix3x2 World, bool SourceCopy)> _saved = new();

        public int SaveState()
        {
            _saved.Add((Specs(), _world, _sourceCopy));
            return _saved.Count;
        }

        public void RestoreState(int state)
        {
            if (state < 1 || state > _saved.Count) return;
            (List<Spec> specs, Matrix3x2 world, bool sourceCopy) = _saved[state - 1];
            _saved.RemoveRange(state - 1, _saved.Count - (state - 1));
            // A snapshot that has since been popped is not re-opened: it was a picture, and the
            // picture is finished. Restore as far as the current snapshots agree.
            List<Spec> current = Specs();
            int floorNow = Floor(current);
            for (int i = 0; i < floorNow; i++)
                if (i >= specs.Count || !ReferenceEquals(specs[i], current[i])) return;
            for (int i = floorNow; i < specs.Count; i++)
                if (specs[i].Kind == Kind.Snapshot) { specs = specs.GetRange(0, i); break; }
            _world = world;
            Rebuild(specs);
            _sourceCopy = sourceCopy;
        }

        private static PathFigure RectFigure(float x, float y, float w, float h)
        {
            var f = new PathFigure(new Vector2(x, y)) { Closed = true };
            f.Segments.Add(new LineSegment(new Vector2(x + w, y)));
            f.Segments.Add(new LineSegment(new Vector2(x + w, y + h)));
            f.Segments.Add(new LineSegment(new Vector2(x, y + h)));
            return f;
        }

        public void FillRect(float x, float y, float w, float h, int argb)
            => Add(new GeometryFill(new RectangleGeometry(new Rect(x, y, w, h)), Rgba(argb)));

        public void FillGradient(GradientShape shape, float x, float y, float w, float h, float[] polyXY, GradientDesc g)
        {
            Geometry geo = shape switch
            {
                GradientShape.Ellipse => new EllipseGeometry(new Vector2(x + w / 2f, y + h / 2f), w / 2f, h / 2f),
                GradientShape.Polygon => new PolygonGeometry(ToVecs(polyXY)),
                _ => new RectangleGeometry(new Rect(x, y, w, h)),
            };
            var stops = new GradientStop[g.Offsets.Length];
            for (int i = 0; i < stops.Length; i++) stops[i] = new GradientStop(g.Offsets[i], Rgba(g.Argb[i]));
            Add(g.Radial
                ? new GeometryFill(geo, new RadialGradientBrush(new Vector2(g.Sx, g.Sy), g.Ex, g.Ey, stops))
                // SIXTEEN BANDS: a GDI+ LinearGradientBrush is a staircase, not a smooth ramp, and
                // System.Drawing has to be GDI+. See Scene.LinearGradientBrush.Bands.
                : new GeometryFill(geo, new LinearGradientBrush(new Vector2(g.Sx, g.Sy), new Vector2(g.Ex, g.Ey), stops,
                                                                GradientSpreadMethod.Pad, bands: 16)));
        }

        private static Vector2[] ToVecs(float[] xy)
        {
            var pts = new Vector2[xy.Length / 2];
            for (int i = 0; i < pts.Length; i++) pts[i] = new Vector2(xy[i * 2], xy[i * 2 + 1]);
            return pts;
        }

        public void FillEllipse(float x, float y, float w, float h, int argb)
            => Add(new GeometryFill(
                new EllipseGeometry(new Vector2(x + w / 2f, y + h / 2f), w / 2f, h / 2f), Rgba(argb)));

        public void FillHatch(GradientShape shape, float x, float y, float w, float h, float[] polyXY,
                              byte[] tileRgba, int tileW, int tileH, float tileSize)
        {
            Geometry geo = shape switch
            {
                GradientShape.Ellipse => new EllipseGeometry(new Vector2(x + w / 2f, y + h / 2f), w / 2f, h / 2f),
                GradientShape.Polygon => new PolygonGeometry(ToVecs(polyXY)),
                _ => new RectangleGeometry(new Rect(x, y, w, h)),
            };
            // sRGB tile bytes -> uploaded as an sRGB texture (hardware decodes on sample), so no
            // sRGB->linear conversion here (unlike solid fills), matching the DrawImage path.
            Add(new GeometryFill(geo,
                new ImageBrush(tileRgba, tileW, tileH, TileMode.Tile, tileSize, tileSize)));
        }

        public void FillPolygon(float[] xy, int argb)
        {
            var pts = new Vector2[xy.Length / 2];
            for (int i = 0; i < pts.Length; i++) pts[i] = new Vector2(xy[i * 2], xy[i * 2 + 1]);
            Add(new GeometryFill(new PolygonGeometry(pts), Rgba(argb)));
        }

        public void FillContours(float[][] contours, bool nonZero, int argb)
        {
            PathGeometry geo = Contours(contours, nonZero);
            if (geo != null) Add(new GeometryFill(geo, Rgba(argb)));
        }

        public void FillContoursGradient(float[][] contours, bool nonZero, GradientDesc g)
        {
            PathGeometry geo = Contours(contours, nonZero);
            if (geo == null) return;
            var stops = new GradientStop[g.Offsets.Length];
            for (int i = 0; i < stops.Length; i++) stops[i] = new GradientStop(g.Offsets[i], Rgba(g.Argb[i]));
            Add(g.Radial
                ? new GeometryFill(geo, new RadialGradientBrush(new Vector2(g.Sx, g.Sy), g.Ex, g.Ey, stops))
                : new GeometryFill(geo, new LinearGradientBrush(new Vector2(g.Sx, g.Sy), new Vector2(g.Ex, g.Ey), stops,
                                                                GradientSpreadMethod.Pad, bands: 16)));
        }

        private static PathGeometry Contours(float[][] contours, bool nonZero)
        {
            var figures = new System.Collections.Generic.List<PathFigure>();
            foreach (float[] xy in contours)
            {
                if (xy == null || xy.Length < 6) continue;
                var f = new PathFigure(new Vector2(xy[0], xy[1]));
                for (int i = 2; i + 1 < xy.Length; i += 2)
                    f.Segments.Add(new LineSegment(new Vector2(xy[i], xy[i + 1])));
                f.Closed = true;
                figures.Add(f);
            }
            return figures.Count == 0 ? null : new PathGeometry(nonZero ? FillRule.NonZero : FillRule.EvenOdd, figures);
        }

        public void DrawLine(float x1, float y1, float x2, float y2, int argb, float width = 1f)
        {
            RgbaColor c = Rgba(argb);

            // One pixel is the overwhelming case, and it is drawn as the exact integer rectangle the
            // line covers -- crisp, and identical to what GDI+ puts down. Anything else is stroked to
            // its real width: a pen thinner than a pixel comes out as partial coverage, which is how
            // a tick or a hairline is meant to read, and a wider one actually gets wider.
            bool hairline = width > 0.99f && width < 1.01f;
            float half = System.Math.Max(width, 0.01f) * 0.5f;

            if (y1 == y2)        // horizontal
            {
                float y = hairline ? y1 : y1 + 0.5f - half;
                Add(new GeometryFill(new RectangleGeometry(
                    new Rect(Min(x1, x2), y, Abs(x2 - x1) + 1, hairline ? 1 : width)), c));
            }
            else if (x1 == x2)   // vertical
            {
                float x = hairline ? x1 : x1 + 0.5f - half;
                Add(new GeometryFill(new RectangleGeometry(
                    new Rect(x, Min(y1, y2), hairline ? 1 : width, Abs(y2 - y1) + 1)), c));
            }
            else                 // diagonal: a quad along the segment
            {
                float dx = x2 - x1, dy = y2 - y1, len = (float)System.Math.Sqrt(dx * dx + dy * dy);
                float nx = -dy / len * half, ny = dx / len * half;
                Add(new GeometryFill(new PolygonGeometry(new[]
                {
                    new Vector2(x1 + nx, y1 + ny), new Vector2(x2 + nx, y2 + ny),
                    new Vector2(x2 - nx, y2 - ny), new Vector2(x1 - nx, y1 - ny),
                }), c));
            }
        }

        // A dashed line has to stay a STROKE: the scene's stroke style carries a dash array that the
        // renderer honours, whereas the solid path above collapses a line to a filled 1px rect and
        // would lose the pattern entirely. The forms designer draws its 8x8 dot grid as horizontal
        // lines dashed {1, 7}, so losing it turned the design surface into solid stripes.
        /// <summary>WPF_DASH=line: anchor a dash pattern to the LINE rather than to the device
        /// grid, starting on the gap. The one model that fits all three connector measurements.
        /// </summary>
        private static readonly bool s_dashLineRelative =
            System.Environment.GetEnvironmentVariable("WPF_DASH") == "line";

        public void DrawDashedLine(float x1, float y1, float x2, float y2, int argb, float width, float[] dashPattern)
        {
            if (dashPattern == null || dashPattern.Length == 0)
            {
                DrawLine(x1, y1, x2, y2, argb);
                return;
            }

            float w = width <= 0 ? 1f : width;
            // The pattern's anchor, taken BEFORE the half-pixel nudge below moves the coordinates.
            float anchorX = x1, anchorY = y1;

            // A one-pixel stroke is centred on the line it is given, so an axis-aligned one at a
            // whole coordinate lands half in each of two pixels and fills neither -- a tree view's
            // connectors came out two faint columns wide where Windows draws one solid. Half a pixel
            // back puts it inside a single row or column. The solid path above does not need this:
            // it collapses to a filled rectangle rather than a stroke.
            if (w <= 1.5f)
            {
                if (x1 == x2) { x1 -= 0.5f; x2 -= 0.5f; }
                else if (y1 == y2) { y1 -= 0.5f; y2 -= 0.5f; }
            }

            var dashes = new double[dashPattern.Length];
            for (int i = 0; i < dashPattern.Length; i++)
                dashes[i] = dashPattern[i];      // stroke dashes are in multiples of thickness, as in GDI+

            // The pattern is anchored to the DEVICE GRID, not to where this particular line starts.
            // GDI's dotted pen is a brush pinned to the surface, so two dotted lines that meet end to
            // end carry on the same dots; ours restarted the pattern per call, and a tree view's
            // connector -- drawn as two segments, one either side of the node -- changed phase in the
            // middle of what reads as a single line.
            double period = 0.0;
            foreach (double d in dashes) period += d;
            double offset = 0.0;
            if (period > 0.0 && w > 0f && (x1 == x2 || y1 == y2))
            {
                // THE VARYING AXIS ONLY. A checkerboard -- ink where x + y is odd -- was tried here
                // and is NOT the rule, though one connector makes it look like it: a stock tree's
                // upright at column 589 inks rows 404 and 406 (sum odd) but its deeper upright at
                // column 608 inks rows 444, 446 and 448 (sum EVEN). Both ink even rows whatever
                // column they are in, so the anchor is the axis the line runs along.
                //
                // What is still unexplained is the ARM: at row 414 Windows inks columns 591, 593,
                // 595, 597 -- the odd parity -- where this gives it the even one. So verticals want
                // phase 0 and that horizontal wants phase 1, which no single anchor produces.
                // THREE MODELS TRIED, ALL THREE WRONG:
                //     device grid, varying axis   1,376,484   <- here, and the best of them
                //     checkerboard (x + y)        1,380,450
                //     line-relative, gap first    1,380,426   (WPF_DASH=line)
                // I talked myself into the last one on the grounds that it "fits all three
                // observations". It does not, and the measurement says so: it costs what the
                // checkerboard costs because it inverts the same two uprights, which means those
                // uprights START ON AN EVEN ROW -- not the odd one that argument needed. An
                // assumption never checked, inside a sentence that sounded like a deduction.
                // Whatever puts that one arm on the odd columns, it is none of these three.
                offset = s_dashLineRelative
                    ? (dashes.Length > 0 ? dashes[0] : 0.0)      // line-relative, first unit OFF
                    : ((x1 == x2 ? anchorY : anchorX) / w) % period;
                if (offset < 0.0) offset += period;
            }

            var fig = new PathFigure(new Vector2(x1, y1)) { Closed = false };
            fig.Segments.Add(new LineSegment(new Vector2(x2, y2)));
            var geo = new PathGeometry(FillRule.NonZero, new List<PathFigure> { fig });
            Add(new GeometryStroke(geo, Rgba(argb),
                new StrokeStyle(w, LineCap.Butt, LineJoin.Miter, 10.0, dashes, offset)));
        }

        // Arc as a stroked path sampled along the ellipse (handles the full-circle radio/checkbox
        // ring at 0..359 as well as partial arcs).
        public void DrawArc(float x, float y, float w, float h, float startDeg, float sweepDeg, int argb, float thickness)
        {
            float cx = x + w / 2f, cy = y + h / 2f, rx = w / 2f, ry = h / 2f;
            int n = System.Math.Max(8, (int)(System.Math.Abs(sweepDeg) / 8f));
            var fig = new PathFigure(ArcPt(cx, cy, rx, ry, startDeg));
            for (int i = 1; i <= n; i++)
                fig.Segments.Add(new LineSegment(ArcPt(cx, cy, rx, ry, startDeg + sweepDeg * i / n)));
            fig.Closed = System.Math.Abs(sweepDeg) >= 359f;
            var geo = new PathGeometry(FillRule.NonZero, new List<PathFigure> { fig });
            Add(new GeometryStroke(geo, Rgba(argb), new StrokeStyle(thickness < 1f ? 1f : thickness)));
        }

        private static Vector2 ArcPt(float cx, float cy, float rx, float ry, float deg)
        {
            double a = deg * System.Math.PI / 180.0;   // GDI+ degrees; y-down makes +angle clockwise (matches)
            return new Vector2(cx + rx * (float)System.Math.Cos(a), cy + ry * (float)System.Math.Sin(a));
        }

        public void DrawImage(byte[] rgba, int pw, int ph, float dx, float dy, float dw, float dh)
            // ImageBrush maps its pixel dimensions onto the geometry bounds (stretched to the dest rect).
            => Add(new GeometryFill(
                new RectangleGeometry(new Rect(dx, dy, dw, dh)), new ImageBrush(rgba, pw, ph)));

        public void DrawText(string text, float x, float y, float emPx, int argb, int simulations, string fontFamily)
            // GDI+ top-left origin -> GlyphRunDraw baseline (drop by ~ascent). Glyphs are rasterized
            // at present time by the renderer that owns the font.
            => Add(new GlyphRunDraw(text, new Vector2(x, y + emPx * 0.8f), emPx, Rgba(argb), simulations, fontFamily));

        public void DrawGdiPlusText(object layout, string text, int argb, int simulations, string fontFamily)
        {
            var run = (Microsoft.Wpf.Interop.WebGpu.Composition.Text.GdiPlusText.Run)layout;
            // The same string as an ordinary run on the same baseline: bounds, hashing, hit testing,
            // and the drawing wherever the device transform is not one GDI+'s fast path takes.
            var fallback = new GlyphRunDraw(text, new Vector2(run.OriginX, run.OriginY), run.Em, Rgba(argb),
                                            simulations, fontFamily);
            Add(new GdiPlusTextDraw(run, fontFamily, simulations & 3, argb, fallback));
        }

        // ---- a GDI+ path as it stands: what a printed page needs ---------------------------------
        //
        // The screen verbs above flatten curves and collapse lines to pixel rectangles, both right
        // for a window drawn at one resolution and both wrong for a page that is going to a 600 dpi
        // printer or into a PDF any reader can zoom. These keep the path GDI+ was given -- its
        // Beziers as Beziers, a stroke as a stroke with its pen -- so the page is vector all the way.

        // GDI+ path point types.
        private const byte PtStart = 0, PtLine = 1, PtBezier = 3, PtTypeMask = 7, PtClose = 0x80;

        /// <summary>A GDI+ path (points as x,y pairs and a type byte each) as scene geometry.
        /// <paramref name="closeAll"/> closes every figure, as a fill and a clip do.</summary>
        internal static PathGeometry PathData(float[] xy, byte[] types, bool nonZero, bool closeAll)
        {
            if (xy == null || types == null) return null;
            int n = Math.Min(types.Length, xy.Length / 2);
            var figures = new List<PathFigure>();
            PathFigure fig = null;
            for (int i = 0; i < n; i++)
            {
                var p = new Vector2(xy[i * 2], xy[i * 2 + 1]);
                int t = types[i] & PtTypeMask;
                if (t == PtStart || fig == null)
                {
                    fig = new PathFigure(p) { Closed = closeAll };
                    figures.Add(fig);
                }
                else if (t == PtBezier && i + 2 < n)
                {
                    var c2 = new Vector2(xy[(i + 1) * 2], xy[(i + 1) * 2 + 1]);
                    var e = new Vector2(xy[(i + 2) * 2], xy[(i + 2) * 2 + 1]);
                    fig.Segments.Add(new CubicBezierSegment(p, c2, e));
                    i += 2;
                }
                else
                {
                    fig.Segments.Add(new LineSegment(p));
                }
                if ((types[i] & PtClose) != 0) { fig.Closed = true; fig = null; }
            }
            figures.RemoveAll(f => f.Segments.Count == 0);
            return figures.Count == 0 ? null : new PathGeometry(nonZero ? FillRule.NonZero : FillRule.EvenOdd, figures);
        }

        private static SceneBrush GradientBrush(GradientDesc g)
        {
            var stops = new GradientStop[g.Offsets.Length];
            for (int i = 0; i < stops.Length; i++) stops[i] = new GradientStop(g.Offsets[i], Rgba(g.Argb[i]));
            return g.Radial
                ? new RadialGradientBrush(new Vector2(g.Sx, g.Sy), g.Ex, g.Ey, stops)
                : new LinearGradientBrush(new Vector2(g.Sx, g.Sy), new Vector2(g.Ex, g.Ey), stops, GradientSpreadMethod.Pad, bands: 0);
        }

        /// <summary>Fills a GDI+ path keeping its curves, with a solid colour or, when
        /// <paramref name="gradient"/> is set, that gradient (smooth: a page is not a screen).</summary>
        public void FillPathData(float[] xy, byte[] types, bool nonZero, int argb, GradientDesc? gradient)
        {
            PathGeometry geo = PathData(xy, types, nonZero, closeAll: true);
            if (geo == null) return;
            Add(gradient is GradientDesc g ? new GeometryFill(geo, GradientBrush(g)) : new GeometryFill(geo, Rgba(argb)));
        }

        /// <summary>A gradient fill of a rectangle or ellipse, smooth rather than in GDI+'s sixteen
        /// screen bands.</summary>
        public void FillShapeGradientSmooth(GradientShape shape, float x, float y, float w, float h, GradientDesc g)
        {
            Geometry geo = shape == GradientShape.Ellipse
                ? new EllipseGeometry(new Vector2(x + w / 2f, y + h / 2f), w / 2f, h / 2f)
                : new RectangleGeometry(new Rect(x, y, w, h));
            Add(new GeometryFill(geo, GradientBrush(g)));
        }

        /// <summary>Strokes a GDI+ path with a pen: its width, caps (0 flat, 1 square, 2 round),
        /// join (0 miter, 1 bevel, 2 round) and dash pattern in multiples of the width.</summary>
        public void StrokePathData(float[] xy, byte[] types, int argb, float width, float[] dash, float dashOffset,
                                   int cap, int join, float miterLimit)
        {
            PathGeometry geo = PathData(xy, types, nonZero: true, closeAll: false);
            if (geo == null) return;
            double[] dashes = null;
            if (dash != null && dash.Length > 0)
            {
                dashes = new double[dash.Length];
                for (int i = 0; i < dash.Length; i++) dashes[i] = dash[i];
            }
            LineCap lc = cap == 2 ? LineCap.Round : cap == 1 ? LineCap.Square : LineCap.Butt;
            LineJoin lj = join == 2 ? LineJoin.Round : join == 1 ? LineJoin.Bevel : LineJoin.Miter;
            Add(new GeometryStroke(geo, Rgba(argb),
                new StrokeStyle(width, lc, lj, miterLimit > 0 ? miterLimit : 10.0, dashes, dashOffset)));
        }

        /// <summary>Glyphs already chosen and placed (a printed DrawString): ids of
        /// <paramref name="font"/> (a TrueTypeFont), each at origin + (xs[i], ys[i]), em in local units.
        /// Recorded as a run so a printer or a PDF gets TEXT -- the glyph ids, the font, the
        /// characters -- with the outlines alongside for everything that only draws shapes.</summary>
        public void DrawGlyphs(object font, float em, ushort[] glyphs, float[] xs, float[] ys, float originX, float originY,
                               int argb, string family, int style, string chars, int[] clusters)
        {
            if (font is not Microsoft.Wpf.Interop.WebGpu.Composition.Text.TrueTypeFont face || glyphs == null || glyphs.Length == 0)
                return;
            RgbaColor color = Rgba(argb);
            var fallback = new List<DrawingPrimitive>();
            var fills = new List<Microsoft.Wpf.Interop.WebGpu.Composition.Text.GlyphFill>();
            float scale = em / face.PixelsPerEm;
            for (int i = 0; i < glyphs.Length; i++)
            {
                fills.Clear();
                float gx = originX + xs[i], gy = originY + ys[i];
                Microsoft.Wpf.Interop.WebGpu.Composition.Text.GlyphRunPainter.Paint(face, face, glyphs[i], scale, gx, gy, fills);
                foreach (var gf in fills)
                {
                    var geo = new PathGeometry(FillRule.NonZero, gf.Figures);
                    fallback.Add(gf.IsColorLayer
                        ? new GeometryFill(geo, gf.Brush ?? new SolidColorBrush(gf.Color ?? color))
                        : new GeometryFill(geo, new SolidColorBrush(color), isGlyph: true, baselineAnchor: new Vector2(gx, gy)));
                }
            }
            Add(new WpfTextRunDraw(face, em, glyphs, xs, ys, new Vector2(originX, originY), color, fallback)
            {
                SourceFamily = family, SourceStyle = style, Characters = chars, GlyphClusters = clusters,
            });
        }

        public void DrawScene(object scene, float sx, float sy, float sw, float sh, float dx, float dy, float dw, float dh)
        {
            if (scene is not SceneVisual page || sw <= 0 || sh <= 0) return;
            // Drawn in content order (a NestedVisualDraw), clipped to the source rectangle in the
            // page's own units, and mapped onto the destination.
            var frame = new SceneVisual
            {
                Transform = Matrix3x2.CreateTranslation(-sx, -sy) * Matrix3x2.CreateScale(dw / sw, dh / sh)
                          * Matrix3x2.CreateTranslation(dx, dy),
                Clip = new Rect(sx, sy, sw, sh),
            };
            frame.Children.Add(page);
            Add(new NestedVisualDraw(frame));
        }

        // A colour as WinForms states it. WHICH SPACE depends on the one the compositor blends in,
        // and the two have to agree or every blend is wrong.
        //
        // This used to linearise unconditionally, pairing with the sRGB surface the host always asked
        // for -- a linear pipeline, hardware-encoded on write. It matched Windows on every opaque fill
        // and lost to it on every BLEND, because a linear blend is not what GDI does. GDI works on
        // encoded bytes, and so does this renderer in its default gamma mode (its gradient Lerp
        // already treats stops as sRGB-encoded, and RenderToRgba already forces a UNORM target).
        // WinForms was the odd one out, and text paid for it: a glyph edge at 0.72 coverage over white
        // came out at 145 where GDI puts it at 72, and our text carried about a quarter less ink than
        // Windows' across the whole window.
        //
        // The linear branch is not dead: the sink turns gamma mode OFF for backends that cannot
        // present a pre-encoded UNORM swapchain faithfully (ANGLE, notably), and there the surface
        // stays sRGB and the colours have to be linear to match it. WgpuPresenter picks the surface
        // format off the same switch.
        private static RgbaColor Rgba(int argb)
        {
            float r = ((argb >> 16) & 0xff) / 255f, g = ((argb >> 8) & 0xff) / 255f, b = (argb & 0xff) / 255f;
            float a = ((argb >> 24) & 0xff) / 255f;
            return WgpuSceneRenderer.s_gammaComposite
                 ? new RgbaColor(r, g, b, a)
                 : new RgbaColor(SrgbToLinear(r), SrgbToLinear(g), SrgbToLinear(b), a);
        }

        private static float SrgbToLinear(float c)
            => c <= 0.04045f ? c / 12.92f : (float)System.Math.Pow((c + 0.055) / 1.055, 2.4);

        private static float Min(float a, float b) => a < b ? a : b;
        private static float Abs(float v) => v < 0 ? -v : v;
    }
}
