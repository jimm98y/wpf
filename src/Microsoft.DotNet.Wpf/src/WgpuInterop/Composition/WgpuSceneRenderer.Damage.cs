// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// PARTIAL REDRAW. A frame re-renders only the device rectangles that changed since the previous one
// and keeps every other pixel from that previous frame.
//
// Three parts:
//
//   DamageTracker   diffs this frame's SceneVisual tree against a record of the last one and returns
//                   the changed device rectangles: for every visual whose own state or content
//                   changed, where its subtree was (old bounds) and where it is now (new bounds).
//                   It knows nothing about who built the tree, so WPF's milcore scene, the WinForms
//                   driver's window scenes, the caret, rubber bands and WinForms-in-WPF / WPF-in-
//                   WinForms embedding are all covered by one mechanism.
//
//   RenderFrameDamaged  renders into a PERSISTENT texture. The main pass LOADS it instead of
//                   clearing; a SourceCopy quad of the background clears just the damage; and every
//                   draw is scissored to the damage rectangles at record time. Everything upstream
//                   of the main pass -- layer bakes, blurs, masks, 3D, content brushes, the ClearType
//                   paper decision -- runs exactly as in a full frame, so the pixels inside the damage
//                   are the full frame's. Subtrees and primitives whose conservative bounds miss the
//                   damage are not collected at all (that is where the CPU time goes); if skipping
//                   one could have changed a ClearType paper answer the frame is collected again
//                   without skipping (see DrawData.Culled).
//
//   PartialTarget   owns the persistent texture for one presentable surface, runs the tracker,
//                   renders, and blits the texture to the swap chain with a full-target textured
//                   triangle -- a swap chain's contents do not survive a present, and GL/ANGLE/VirGL
//                   swap chains cannot be a copy destination, so a blit is the portable way.
//
// WGPU_DAMAGE=0 renders every frame in full. WGPU_DAMAGE_VERIFY=1 renders each frame both ways and
// logs every pixel that differs (WGPU_DAMAGE_LOG=<file> to also append the log to a file).
//

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using static Microsoft.Wpf.Interop.WebGpu.Wgpu;

namespace Microsoft.Wpf.Interop.WebGpu.Composition
{
    internal sealed unsafe partial class WgpuSceneRenderer
    {
        // ---- culling (partial frames only) -------------------------------------------------------

        // The main pass's DrawData while a partial frame is being collected with culling, else null.
        private DrawData? _cullData;
        private List<Scissor>? _cullDamage;
        private int _cullStamp;

        [ThreadStatic] internal static int PerfCulledVisuals, PerfCulledPrimitives, PerfCullRetries;

        /// <summary>WGPU_DAMAGE_CULL=0 keeps partial frames but collects everything (scissor only).</summary>
        private static readonly bool s_damageCull = Environment.GetEnvironmentVariable("WGPU_DAMAGE_CULL") != "0";

        private bool TouchesDamage(Scissor b)
        {
            if (b.IsEmpty) return false;
            foreach (Scissor d in _cullDamage!)
                if (b.X < d.X + d.W && d.X < b.X + b.W && b.Y < d.Y + d.H && d.Y < b.Y + b.H) return true;
            return false;
        }

        /// <summary>Skip a whole visual on a partial frame when nothing it draws can reach the damage.
        /// What it may draw is bounded by the tracker's measurement of it (when that is this frame's),
        /// or by its own rectangular clip -- every draw of the subtree is scissored to that.</summary>
        private bool CullVisual(SceneVisual v, Scissor clip)
        {
            Scissor b;
            if (_cullStamp != 0 && v.DamageStamp == _cullStamp)
                b = Intersect(clip, new Scissor(v.DamageX, v.DamageY, v.DamageW, v.DamageH));
            else if (v.Clip.HasValue)
                b = clip;
            else
                return false;
            if (TouchesDamage(b)) return false;
            if (!b.IsEmpty) _cullData!.Culled!.Add(b);
            PerfCulledVisuals++;
            return true;
        }

        private bool CullPrimitive(DrawingPrimitive p, Matrix3x2 world, Scissor clip)
        {
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            if (!PrimitiveBox(p, world, ref minX, ref minY, ref maxX, ref maxY)) return false;
            Scissor b = minX > maxX ? new Scissor(0, 0, 0, 0) : Intersect(clip, ToScissor(minX, minY, maxX, maxY, ContentMargin));
            if (TouchesDamage(b)) return false;
            if (!b.IsEmpty) _cullData!.Culled!.Add(b);
            PerfCulledPrimitives++;
            return true;
        }

        // ---- conservative bounds -----------------------------------------------------------------

        /// <summary>Device pixels added around every primitive's box: antialiasing, the ClearType
        /// filter's spill (one pixel left, two right), guideline snapping, hinting moving a glyph
        /// and a shifted layer's whole-pixel snap all stay inside it.</summary>
        private const int ContentMargin = 4;

        private static Scissor ToScissor(float minX, float minY, float maxX, float maxY, int margin)
        {
            // Clamped well inside int range: a degenerate transform must not overflow the arithmetic.
            const float Lim = 1 << 28;
            minX = Math.Clamp(minX, -Lim, Lim); maxX = Math.Clamp(maxX, -Lim, Lim);
            minY = Math.Clamp(minY, -Lim, Lim); maxY = Math.Clamp(maxY, -Lim, Lim);
            int x0 = (int)MathF.Floor(minX) - margin, y0 = (int)MathF.Floor(minY) - margin;
            int x1 = (int)MathF.Ceiling(maxX) + margin, y1 = (int)MathF.Ceiling(maxY) + margin;
            return new Scissor(x0, y0, x1 - x0, y1 - y0);
        }

        private static Scissor Union(Scissor a, Scissor b)
        {
            if (a.IsEmpty) return b;
            if (b.IsEmpty) return a;
            int x0 = Math.Min(a.X, b.X), y0 = Math.Min(a.Y, b.Y);
            int x1 = Math.Max(a.X + a.W, b.X + b.W), y1 = Math.Max(a.Y + a.H, b.Y + b.H);
            return new Scissor(x0, y0, x1 - x0, y1 - y0);
        }

        private static Scissor Inflate(Scissor a, int l, int t, int r, int b)
            => a.IsEmpty ? a : new Scissor(a.X - l, a.Y - t, a.W + l + r, a.H + t + b);

        private static float WorldScale(Matrix3x2 m)
            => MathF.Max(MathF.Abs(m.M11) + MathF.Abs(m.M21), MathF.Abs(m.M12) + MathF.Abs(m.M22));

        /// <summary>Accumulates a primitive's conservative device box. False when the primitive is of
        /// a kind whose extent is not known here -- the caller then must not skip it, and the tracker
        /// treats it as touching the whole target.</summary>
        private static bool PrimitiveBox(DrawingPrimitive p, Matrix3x2 world,
            ref float minX, ref float minY, ref float maxX, ref float maxY)
        {
            switch (p)
            {
                case GeometryFill f:
                    AccGeometry(f.Geometry, world, 0f, ref minX, ref minY, ref maxX, ref maxY);
                    return true;
                case GeometryStroke s:
                    AccGeometry(s.Geometry, world, StrokePad(s.Style, world), ref minX, ref minY, ref maxX, ref maxY);
                    return true;
                case GeometryDrawing d:
                    AccGeometry(d.Geometry, world, d.Stroke is null ? 0f : StrokePad(d.StrokeStyle, world),
                        ref minX, ref minY, ref maxX, ref maxY);
                    return true;
                case GlyphRunDraw g:
                    TextBox(g, world, ref minX, ref minY, ref maxX, ref maxY);
                    return true;
                case WpfTextRunDraw t:
                    // The outline fills are where the glyphs are; the natural ClearType draw differs
                    // from them by hinting and filter spill, which the margin covers. The pen
                    // positions bound the run too, for one whose fallback is empty.
                    foreach (DrawingPrimitive f in t.Fallback)
                        if (!PrimitiveBox(f, world, ref minX, ref minY, ref maxX, ref maxY)) return false;
                    for (int i = 0; i < t.X.Length && i < t.Y.Length; i++)
                    {
                        float gx = t.Origin.X + t.X[i], gy = t.Origin.Y + t.Y[i], em = t.EmSize;
                        AccRect(gx - em, gy - 2f * em, 3f * em, 3f * em, world, 0f, ref minX, ref minY, ref maxX, ref maxY);
                    }
                    return true;
                case GdiPlusTextDraw gp:
                    TextBox(gp.Fallback, world, ref minX, ref minY, ref maxX, ref maxY);
                    if (gp.Run is { } run)
                    {
                        // The GDI+ layout is in device units from the run's own origin.
                        float em = run.Em, ox = run.OriginX + world.M31, oy = run.OriginY + world.M32;
                        float w = (run.Glyphs.Length + 2) * em * 1.5f;
                        minX = MathF.Min(minX, ox - 2f * em); maxX = MathF.Max(maxX, ox + w);
                        minY = MathF.Min(minY, oy - 2f * em); maxY = MathF.Max(maxY, oy + 2f * em);
                    }
                    return true;
                case NestedVisualDraw n:
                {
                    Scissor sb = SubtreeBox(n.Visual, n.Visual.LocalToParent * world, out bool known);
                    if (!known) return false;
                    if (!sb.IsEmpty)
                    {
                        minX = MathF.Min(minX, sb.X); minY = MathF.Min(minY, sb.Y);
                        maxX = MathF.Max(maxX, sb.X + sb.W); maxY = MathF.Max(maxY, sb.Y + sb.H);
                    }
                    return true;
                }
                default:
                    // Viewport3DDraw and anything added later.
                    return false;
            }
        }

        /// <summary>A brush whose pixels are rendered live from another visual every frame: what it
        /// paints can change with nothing in this tree changing.</summary>
        private static ImageBrush? LiveBrush(Brush? b) => b is ImageBrush { SourceVisual: not null } ib ? ib : null;

        /// <summary>The live (GPU-rendered each frame) brush a primitive paints with, if any. What it
        /// paints is bounded by the primitive's geometry, but can change with the primitive unchanged:
        /// the tracker diffs the brush's source visual separately.</summary>
        private static ImageBrush? LiveBrushOf(DrawingPrimitive p) => p switch
        {
            GeometryFill f => LiveBrush(f.Brush),
            GeometryStroke s => LiveBrush(s.Brush),
            GeometryDrawing d => LiveBrush(d.Fill) ?? LiveBrush(d.Stroke),
            _ => null,
        };

        private static float StrokePad(StrokeStyle st, Matrix3x2 world)
        {
            // Half the pen, as far as a miter can reach, through the world's largest stretch.
            double miter = st.Join == LineJoin.Miter ? Math.Max(1.0, st.MiterLimit) : 1.0;
            return (float)(Math.Abs(st.Thickness) * 0.5 * Math.Max(miter, 1.5)) * WorldScale(world) + 1f;
        }

        /// <summary>A string run's box, generously: up to one and a half ems an advance from its
        /// origin (plus two of slack), two ems above the baseline and one below.</summary>
        private static void TextBox(GlyphRunDraw g, Matrix3x2 world,
            ref float minX, ref float minY, ref float maxX, ref float maxY)
        {
            float em = Math.Max(g.EmSize, 1f);
            int n = g.Text?.Length ?? 0;
            float x0 = g.Origin.X - 2f * em, x1 = g.Origin.X + (n + 2) * em * 1.5f;
            float y0 = g.Origin.Y - 2f * em, y1 = g.Origin.Y + em;
            AccRect(x0, y0, x1 - x0, y1 - y0, world, 0f, ref minX, ref minY, ref maxX, ref maxY);
        }

        /// <summary>A whole subtree's conservative device box, its own clip and effects applied
        /// (unbounded by any ancestor). <paramref name="known"/> is false when something in it has an
        /// extent that is not known here.</summary>
        private static Scissor SubtreeBox(SceneVisual v, Matrix3x2 world, out bool known)
        {
            known = true;
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            Scissor b = new Scissor(0, 0, 0, 0);
            if (v.Snapshot is { } snap)
            {
                b = SnapshotBox(snap, world);
            }
            else
            {
                foreach (DrawingPrimitive p in v.Content)
                    if (!PrimitiveBox(p, world, ref minX, ref minY, ref maxX, ref maxY)) known = false;
                if (minX <= maxX) b = ToScissor(minX, minY, maxX, maxY, ContentMargin);
                foreach (SceneVisual c in v.Children)
                {
                    b = Union(b, SubtreeBox(c, c.LocalToParent * world, out bool k));
                    if (!k) known = false;
                }
                b = EffectSpread(v, b);
            }
            if (v.Clip is Rect cr)
                b = Intersect(b, RectBox(cr, world));
            return b;
        }

        private static Scissor SnapshotBox(SceneSnapshot snap, Matrix3x2 world)
            => Inflate(RectBox(snap.Dest, world), 2, 2, 2, 2);

        /// <summary>A rectangle's device box, computed EXACTLY as <see cref="DeviceBounds"/> computes a
        /// clip's scissor (corners summed in double, then transformed): a clip's box here must be the
        /// renderer's scissor to the pixel. Summing in float instead ended a clip at 22.37 + 211.63
        /// a row short of the renderer's scissor, and a shadow drawn on that last row was never
        /// damaged when it moved away.</summary>
        private static Scissor RectBox(Rect r, Matrix3x2 world)
        {
            Vector2 p0 = Vector2.Transform(new Vector2((float)r.X, (float)r.Y), world);
            Vector2 p1 = Vector2.Transform(new Vector2((float)(r.X + r.Width), (float)r.Y), world);
            Vector2 p2 = Vector2.Transform(new Vector2((float)(r.X + r.Width), (float)(r.Y + r.Height)), world);
            Vector2 p3 = Vector2.Transform(new Vector2((float)r.X, (float)(r.Y + r.Height)), world);
            float minX = MathF.Min(MathF.Min(p0.X, p1.X), MathF.Min(p2.X, p3.X));
            float minY = MathF.Min(MathF.Min(p0.Y, p1.Y), MathF.Min(p2.Y, p3.Y));
            float maxX = MathF.Max(MathF.Max(p0.X, p1.X), MathF.Max(p2.X, p3.X));
            float maxY = MathF.Max(MathF.Max(p0.Y, p1.Y), MathF.Max(p2.Y, p3.Y));
            return ToScissor(minX, minY, maxX, maxY, 0);
        }

        /// <summary>Where a visual's effect can paint, given where its subtree draws.</summary>
        private static Scissor EffectSpread(SceneVisual v, Scissor b)
        {
            if (b.IsEmpty) return b;
            switch (v.Effect)
            {
                case BlurEffect be:
                {
                    int m = (int)Math.Ceiling(Math.Abs(be.Radius)) + 4;
                    return Inflate(b, m, m, m + 8, m + 8);
                }
                case DropShadowEffect de:
                {
                    int m = (int)Math.Ceiling(Math.Abs(de.BlurRadius)) + 4;
                    Scissor shadow = new Scissor(b.X + (int)Math.Floor(de.OffsetX), b.Y + (int)Math.Floor(de.OffsetY), b.W + 1, b.H + 1);
                    return Inflate(Union(b, shadow), m, m, m + 8, m + 8);
                }
                case null:
                    // An opacity mask / clip-geometry / opacity layer is sized to its content and
                    // padded to a multiple of eight; nothing is drawn in the padding.
                    return b;
                default:
                    // A pixel shader may write anywhere in its layer, padding included.
                    return Inflate(b, 2, 2, 10, 10);
            }
        }

        /// <summary>Spread damage that arose INSIDE a visual's subtree as that visual's effect
        /// spreads it. Applied to <paramref name="list"/> from index <paramref name="from"/>.</summary>
        private static void SpreadDamage(SceneVisual v, List<Scissor> list, int from, Scissor whole)
        {
            if (from >= list.Count) return;
            bool wholeLayer = v.Snapshot != null || (v.Effect != null && v.Effect is not BlurEffect && v.Effect is not DropShadowEffect);
            if (wholeLayer)
            {
                // A snapshot draws its subtree somewhere else entirely (stretched into Dest), and a
                // pixel shader can move pixels anywhere in its layer: anything changing inside
                // changes the whole of it.
                list.RemoveRange(from, list.Count - from);
                list.Add(whole);
                return;
            }
            if (v.Effect is BlurEffect or DropShadowEffect)
                for (int i = from; i < list.Count; i++) list[i] = EffectSpread(v, list[i]);
        }

        // ---- the tracker ------------------------------------------------------------------------

        private static int s_damageStamp;

        /// <summary>Diffs a scene tree against the previous frame's. One per presentable target.</summary>
        internal sealed partial class DamageTracker
        {
            private sealed class Rec
            {
                public Matrix3x2 Local;
                public double Opacity;
                public bool HasClip;
                public Rect Clip;
                public object? ClipGeometry, Effect, Mask, Snapshot;
                public Rect SnapSource, SnapDest;
                public bool SnapStretch, SnapBlend;
                public bool Aliased, Nearest;
                public float[]? GuidesX, GuidesY;
                public DrawingPrimitive[] Content = Array.Empty<DrawingPrimitive>();
                public Scissor[] Boxes = Array.Empty<Scissor>();   // per content primitive, device, clipped
                public byte[] Kinds = Array.Empty<byte>();          // per content primitive: KindText / KindUnknown
                public int ContentCount;
                public Rec?[] Kids = Array.Empty<Rec?>();
                public int KidCount;
                public Scissor Own;       // union of Boxes
                public Scissor Bounds;    // whole subtree (device, effect spread, clipped)
                public bool Volatile;     // own content can change without the tree changing
                public bool Clipped;      // something in the subtree is cut by the clip it is measured under
            }

            private const byte KindText = 1, KindUnknown = 2, KindLive = 4, KindAlways = 8, KindCut = 16;

            // Live brush sources (ImageBrush.SourceVisual), each diffed by a tracker of its own; a
            // source that changed damages every primitive painting with it.
            private readonly Dictionary<SceneVisual, DamageTracker> _sources = new(ReferenceEqualityComparer.Instance);
            private readonly HashSet<SceneVisual> _sourcesSeen = new(ReferenceEqualityComparer.Instance);
            private readonly Dictionary<SceneVisual, bool> _sourceChanged = new(ReferenceEqualityComparer.Instance);
            private readonly List<Scissor> _scratch = new();

            private bool SourceChanged(ImageBrush b)
            {
                SceneVisual src = b.SourceVisual!;
                if (_sourceChanged.TryGetValue(src, out bool known)) return known;
                _sourcesSeen.Add(src);
                if (!_sources.TryGetValue(src, out DamageTracker? t)) _sources[src] = t = new DamageTracker(allowShift: false);
                bool changed = t.Compute(src, Math.Max(1, b.SourceTexW), Math.Max(1, b.SourceTexH), _scratch) || _scratch.Count > 0;
                _sourceChanged[src] = changed;
                return changed;
            }

            private void CheckLive(Rec r, bool addDamage)
            {
                for (int i = 0; i < r.ContentCount; i++)
                {
                    if ((r.Kinds[i] & KindLive) != 0 && LiveBrushOf(r.Content[i]) is { } b && SourceChanged(b) && addDamage)
                        _raw.Add(r.Boxes[i]);
                    // A 3D viewport's cameras, models and materials are not in the 2D tree at all:
                    // what it shows is redrawn on every frame, but only where it is.
                    else if ((r.Kinds[i] & KindAlways) != 0 && addDamage)
                        _raw.Add(r.Boxes[i]);
                }
            }

            private Rec? _root;
            private int _w, _h;
            private readonly List<Scissor> _raw = new();
            private readonly List<Scissor> _texts = new();
            private bool _forceFull;

            /// <summary>Why the last Compute asked for a full frame (diagnostics).</summary>
            public string FullReason { get; private set; } = "";

            /// <summary>The stamp of the most recent <see cref="Compute"/>; visuals it measured carry it.</summary>
            public int Stamp { get; private set; }

            /// <summary>Forget the previous frame: the next Compute reports full damage.</summary>
            public void Reset() => _root = null;

            /// <summary>Records <paramref name="root"/> as the current frame and returns whether the
            /// whole target must be redrawn. Otherwise <paramref name="rects"/> holds the disjoint
            /// device rectangles that changed (possibly none).</summary>
            internal bool Compute(SceneVisual root, int width, int height, List<Scissor> rects)
            {
                Stamp = Interlocked.Increment(ref s_damageStamp);
                if (Stamp == 0) Stamp = Interlocked.Increment(ref s_damageStamp);
                _raw.Clear();
                _texts.Clear();
                _sourcesSeen.Clear();
                _sourceChanged.Clear();
                _forceFull = false;
                rects.Clear();
                Shift = null;
                _scrollRec = null;
                _scrollInner.Clear();
                Rec? old = (width == _w && height == _h) ? _root : null;
                _w = width; _h = height;
                _root = Visit(old, root, Matrix3x2.Identity, new Scissor(0, 0, width, height), fresh: false, inSnapshot: false,
                    shiftOk: s_scroll && _allowShift);
                if (_sources.Count > _sourcesSeen.Count)
                {
                    var gone = new List<SceneVisual>();
                    foreach (SceneVisual s in _sources.Keys) if (!_sourcesSeen.Contains(s)) gone.Add(s);
                    foreach (SceneVisual s in gone) _sources.Remove(s);
                }
                if (old == null) { FullReason = "first frame"; return true; }
                if (_forceFull) { FullReason = "volatile content (3D / live brush / unknown primitive)"; return true; }
                long target = Math.Max(1L, (long)width * height);
                if (_scrollRec != null)
                {
                    // Two ways to draw a scroll: shift the pixels and redraw what the shift cannot
                    // produce, or redraw the moved subtree where it was and where it is, as any other
                    // change. Whichever touches fewer pixels (a shift also copies its rectangle, once
                    // to a scratch texture and once back, at a fraction of a draw's cost per pixel).
                    var plain = new List<Scissor>(_raw) { _scrollOldBounds, _scrollRec.Bounds };
                    var plainRects = new List<Scissor>();
                    long plainArea = Settle(plain, plainRects, width, height);
                    if (BuildShift(width, height))
                    {
                        long shiftArea = Settle(_raw, rects, width, height);
                        if (s_scrollTrace)
                        {
                            var sb = new System.Text.StringBuilder($"[scroll] settled {shiftArea}:");
                            foreach (Scissor q in rects) sb.Append($" [{q.X},{q.Y} {q.W}x{q.H}]");
                            Console.Error.WriteLine(sb.ToString());
                        }
                        long copy = (long)Shift!.Value.Src.W * Shift.Value.Src.H;
                        _scrollWhy = $"scroll: shift {shiftArea} px + copy {copy} px vs redraw {plainArea} px";
                        if (shiftArea >= 0 && (plainArea < 0 || shiftArea + copy / 4 < plainArea))
                            return Decide(shiftArea, target);
                        Shift = null;
                    }
                    rects.Clear();
                    rects.AddRange(plainRects);
                    if (plainArea < 0) { FullReason = $"{plain.Count} changes"; return true; }
                    return Decide(plainArea, target);
                }
                long area = Settle(_raw, rects, width, height);
                // So many separate changes that sorting them out would cost more than drawing them.
                if (area < 0) { FullReason = $"{_raw.Count} changes"; return true; }
                return Decide(area, target);
            }

            private bool Decide(long area, long target)
            {
                // Most of the target changed: one full frame is no more work and needs no scissors.
                FullReason = $"{area * 100 / target}% of the target changed";
                if (_scrollWhy != null) { FullReason += "; " + _scrollWhy; _scrollWhy = null; }
                bool full = area * 10 > target * 7;
                if (full) Shift = null;
                return full;
            }

            /// <summary>Spread <paramref name="raw"/> to the text it touches and normalize it into
            /// <paramref name="rects"/>; their area, or -1 when there are too many to sort out.</summary>
            private long Settle(List<Scissor> raw, List<Scissor> rects, int width, int height)
            {
                if (raw.Count > MaxRawRects || !SpreadToText(raw, _texts)) return -1;
                Normalize(raw, rects, width, height);
                long area = 0;
                foreach (Scissor r in rects) area += (long)r.W * r.H;
                return area;
            }

            /// <summary>A ClearType run is blended against the paper under its WHOLE box (PaperUnder):
            /// a change under any part of it can change every pixel of it. So damage that touches a
            /// run's box takes in all of the box.
            /// <para>Once, not to a fixed point: the box taken in is redrawn exactly as the full frame
            /// draws it (every draw there is collected), and the paper of another run whose box it
            /// overlaps is read from the draws under THAT box, earlier text skipped -- so it changed
            /// only if a change touches that box too. Chaining run to overlapping run made a column of
            /// text lines one piece of damage: a caret in one line of a list redrew the list.</para></summary>
            private static bool SpreadToText(List<Scissor> raw, List<Scissor> texts)
            {
                int n = raw.Count;
                if (n == 0 || texts.Count == 0) return true;
                if (n > MaxRawRects || (long)n * texts.Count > 4_000_000) return false;
                foreach (Scissor tb in texts)
                {
                    for (int i = 0; i < n; i++)
                    {
                        Scissor d = raw[i];
                        if (tb.X < d.X + d.W && d.X < tb.X + tb.W && tb.Y < d.Y + d.H && d.Y < tb.Y + tb.H)
                        {
                            raw.Add(tb);
                            break;
                        }
                    }
                }
                return raw.Count <= MaxRawRects;
            }
            // ---- scrolling ------------------------------------------------------------------------
            //
            // A scroll moves a whole subtree by whole device pixels inside a clip that stays put. The
            // persistent texture already holds those pixels, one scroll step away, so the frame
            // SHIFTS them (ScrollShift) and redraws only what that cannot produce:
            //
            //   * the strip the scroll exposed;
            //   * whatever did not move but draws inside the clip -- an overlay, a badge, a caret --
            //     both where it is (the shift moved other pixels under it) and one step along (the
            //     shift dragged its old pixels there). A solid rectangle covering the whole clip is
            //     the exception: it paints every pixel there alike, so moving them changes nothing
            //     (a window or panel background);
            //   * anything that changed inside the moved subtree, where it was (shifted) and where it is;
            //   * every other change in the frame, where it is, where it was, and where the shift put
            //     what was there.
            //
            // A pixel outside all of that sees, in this frame, exactly the draws it saw one step back
            // in the last one, in the same order, translated: so it is the full frame's pixel. A
            // ClearType run takes its paper from what lies under its box, which is the same draws again
            // unless something still overlaps it -- then that is damage, and SpreadToText takes the run.
            //
            // Not attempted (the subtree is damaged where it was and is, as any change): a fractional
            // or non-integer move, a move that also scales, rotates or skews, a subtree under an
            // effect, mask, clip geometry, snapshot or group opacity (those pixels are not where their
            // content put them), a moving subtree that is itself one of those, holds a snapshot, or
            // holds a layer too big for a region-sized bake (the bake is then cut to the clip and is
            // not the same picture one step along).

            /// <summary>WGPU_DAMAGE_SCROLL=0 never shifts: a scroll is damage like any other move.</summary>
            private static readonly bool s_scroll = Environment.GetEnvironmentVariable("WGPU_DAMAGE_SCROLL") != "0";
            private static readonly bool s_scrollTrace = Environment.GetEnvironmentVariable("WGPU_DAMAGE_SCROLL_TRACE") == "1";

            private readonly bool _allowShift;

            internal DamageTracker(bool allowShift = true) => _allowShift = allowShift;

            /// <summary>The pixels the last Compute wants shifted before its damage is drawn, or null.</summary>
            internal ScrollShift? Shift { get; private set; }

            private string? _scrollWhy;
            private Rec? _scrollRec;           // the moved subtree, measured where it is now
            private Scissor _scrollOldBounds;  // ... and where it was
            private Scissor _scrollClip;       // the device clip it moved inside
            private int _scrollDx, _scrollDy;
            private readonly List<Scissor> _scrollInner = new();

            /// <summary>The moved subtree's record if <paramref name="v"/> only moved by whole device
            /// pixels since the last frame, else null (and nothing recorded).</summary>
            private Rec? TryScroll(Rec old, SceneVisual v, Matrix3x2 parentWorld, Scissor parentClip, bool inSnapshot)
            {
                if (inSnapshot || parentClip.IsEmpty) return null;
                Matrix3x2 lo = old.Local, ln = v.LocalToParent;
                if (lo.M11 != ln.M11 || lo.M12 != ln.M12 || lo.M21 != ln.M21 || lo.M22 != ln.M22) return null;
                if (!SameState(old, v, ignoreTranslation: true) || !SameContent(old, v)) return null;
                if (v.Effect != null || v.OpacityMask != null || v.ClipGeometry != null || v.Snapshot != null
                    || v.Opacity < 0.999) return null;
                Matrix3x2 wo = lo * parentWorld, wn = ln * parentWorld;
                float fx = wn.M31 - wo.M31, fy = wn.M32 - wo.M32;
                int dx = (int)MathF.Round(fx), dy = (int)MathF.Round(fy);
                if (dx == 0 && dy == 0) return null;
                // Whole device pixels. The two translations are absolute device coordinates in float,
                // so a whole-pixel scroll can differ from its integer by a few ulps.
                const float Ulps = 1f / 2048f;
                if (MathF.Abs(fx - dx) > Ulps || MathF.Abs(fy - dy) > Ulps) return null;
                if (Math.Abs(dx) >= parentClip.W || Math.Abs(dy) >= parentClip.H) return null;
                if (!ShiftableSubtree(v, wn, _w, _h)) return null;

                Rec rec = Visit(null, v, parentWorld, parentClip, fresh: true, inSnapshot: false);
                _scrollRec = rec;
                _scrollOldBounds = old.Bounds;
                _scrollClip = parentClip;
                _scrollDx = dx; _scrollDy = dy;
                int mark = _raw.Count;
                CompareMoved(old, rec, top: true);
                // CheckLive (above) reported live content where it is now; with the rest, it is
                // damage inside the moved subtree.
                for (int i = mark; i < _raw.Count; i++) _scrollInner.Add(_raw[i]);
                _raw.RemoveRange(mark, _raw.Count - mark);
                return rec;
            }

            /// <summary>Whether a moved subtree renders the same picture one whole pixel step along:
            /// no snapshot in it, and no layer too big to be baked whole.</summary>
            private static bool ShiftableSubtree(SceneVisual v, Matrix3x2 world, int width, int height)
            {
                if (v.Snapshot != null) return false;
                if (v.Effect != null || v.OpacityMask != null || v.ClipGeometry != null || v.Opacity < 0.999)
                {
                    // CollectVisual bakes a layer whole only when its region fits the target; a bigger
                    // one is cut to the live clip, so what it shows depends on where it is.
                    Scissor b = SubtreeBox(v, world, out _);
                    if (b.W + 16 > width || b.H + 16 > height) return false;
                }
                foreach (SceneVisual c in v.Children)
                    if (!ShiftableSubtree(c, c.LocalToParent * world, width, height)) return false;
                return true;
            }

            /// <summary>Damage inside a moved subtree: <paramref name="o"/> is its last record, in the
            /// old position; <paramref name="n"/> the fresh one. Added to _raw in NEW device
            /// coordinates (an old box is shifted to where the scroll puts its pixels).</summary>
            private void CompareMoved(Rec o, Rec n, bool top)
            {
                int mark = _raw.Count;
                if (!SameRec(o, n, ignoreTranslation: top))
                {
                    _raw.Add(Translate(o.Bounds, _scrollDx, _scrollDy));
                    _raw.Add(n.Bounds);
                    return;
                }
                if (n.Volatile) _forceFull = true;
                CheckLive(n, addDamage: true);
                int common = Math.Min(o.KidCount, n.KidCount);
                for (int i = 0; i < common; i++)
                {
                    Rec? ok = o.Kids[i], nk = n.Kids[i];
                    if (ok != null && nk != null) CompareMoved(ok, nk, top: false);
                    else { if (ok != null) _raw.Add(Translate(ok.Bounds, _scrollDx, _scrollDy)); if (nk != null) _raw.Add(nk.Bounds); }
                }
                for (int i = common; i < o.KidCount; i++)
                    if (o.Kids[i] is { } gone) _raw.Add(Translate(gone.Bounds, _scrollDx, _scrollDy));
                for (int i = common; i < n.KidCount; i++)
                    if (n.Kids[i] is { } added) _raw.Add(added.Bounds);
                // A change inside a layer changes the layer: an opacity group can stop being one, an
                // effect spreads it, a mask is mapped to the content's bounds.
                if (_raw.Count > mark && (n.Effect != null || n.Mask != null || n.ClipGeometry != null || n.Opacity < 0.999))
                {
                    _raw.RemoveRange(mark, _raw.Count - mark);
                    _raw.Add(Translate(o.Bounds, _scrollDx, _scrollDy));
                    _raw.Add(n.Bounds);
                }
            }

            private static Scissor Translate(Scissor s, int dx, int dy) => s.IsEmpty ? s : new Scissor(s.X + dx, s.Y + dy, s.W, s.H);

            /// <summary>Turns the frame's scroll candidate into a shift and the damage that goes with
            /// it; false when it does not pay or cannot be done.</summary>
            private bool BuildShift(int width, int height)
            {
                Scissor clip = Intersect(_scrollClip, new Scissor(0, 0, width, height));
                int dx = _scrollDx, dy = _scrollDy;
                Scissor dst = Intersect(clip, Translate(clip, dx, dy));
                if (dst.IsEmpty) return false;
                var damage = new List<Scissor>(_raw.Count * 2 + _scrollInner.Count + 8);
                // What the scroll exposed: the clip less the shifted pixels (up to two strips).
                if (dst.Y > clip.Y) damage.Add(new Scissor(clip.X, clip.Y, clip.W, dst.Y - clip.Y));
                if (dst.Y + dst.H < clip.Y + clip.H) damage.Add(new Scissor(clip.X, dst.Y + dst.H, clip.W, clip.Y + clip.H - dst.Y - dst.H));
                if (dst.X > clip.X) damage.Add(new Scissor(clip.X, dst.Y, dst.X - clip.X, dst.H));
                if (dst.X + dst.W < clip.X + clip.W) damage.Add(new Scissor(dst.X + dst.W, dst.Y, clip.X + clip.W - dst.X - dst.W, dst.H));
                // Every other change: where it is and was, and where the shift moved what was there.
                foreach (Scissor r in _raw)
                {
                    damage.Add(r);
                    Scissor moved = Intersect(Translate(r, dx, dy), dst);
                    if (!moved.IsEmpty) damage.Add(moved);
                }
                // (Not cut to the clip: a content scroll's moved records can reach past it.)
                foreach (Scissor r in _scrollInner)
                    if (!r.IsEmpty) damage.Add(r);
                // A run cut by the clip on a side the scroll moves it across: its paper is read under
                // the CLIPPED box, which is not the same box one step back. (A run the scroll brings
                // out from under the far edge touches the exposed strip and is taken by SpreadToText.)
                foreach (Scissor t in _texts)
                {
                    if (!Touches(t, clip)) continue;
                    bool cut = (dy != 0 && (t.Y <= clip.Y || t.Y + t.H >= clip.Y + clip.H))
                            || (dx != 0 && (t.X <= clip.X || t.X + t.W >= clip.X + clip.W));
                    if (cut) damage.Add(Intersect(t, clip));
                }
                // What did not move but draws over the shifted pixels.
                int beforeStill = damage.Count;
                AddStill(_root!, Matrix3x2.Identity, new Scissor(0, 0, width, height), clip, dst, damage);
                if (s_scrollTrace)
                {
                    var sb = new System.Text.StringBuilder($"[scroll] clip [{clip.X},{clip.Y} {clip.W}x{clip.H}] d=({dx},{dy}) raw={_raw.Count} inner={_scrollInner.Count} still={damage.Count - beforeStill}:");
                    for (int i = 0; i < damage.Count; i++) sb.Append($" [{damage[i].X},{damage[i].Y} {damage[i].W}x{damage[i].H}]");
                    Console.Error.WriteLine(sb.ToString());
                }
                if (damage.Count > MaxRawRects) return false;
                _raw.Clear();
                _raw.AddRange(damage);
                Shift = new ScrollShift(Translate(dst, -dx, -dy), dx, dy);
                return true;
            }

            private void AddStill(Rec r, Matrix3x2 parentWorld, Scissor parentClip, Scissor clip, Scissor dst, List<Scissor> damage)
            {
                if (ReferenceEquals(r, _scrollRec) || !Touches(r.Bounds, clip)) return;
                Matrix3x2 world = r.Local * parentWorld;
                Scissor nodeClip = r.HasClip ? Intersect(parentClip, RectBox(r.Clip, world)) : parentClip;
                if (r.Snapshot != null || r.Effect != null || r.Mask != null || r.ClipGeometry != null || r.Opacity < 0.999)
                {
                    // Drawn as one picture: all of it, unless the moved subtree is inside it (then it
                    // is an ancestor, which TryScroll only allows without any of these).
                    StillBox(r.Bounds, clip, dst, damage);
                    return;
                }
                for (int i = 0; i < r.ContentCount; i++)
                {
                    Scissor b = r.Boxes[i];
                    if (!Touches(b, clip)) continue;
                    if (CoversUniformly(r.Content[i], world, nodeClip, clip)) continue;
                    if (r.GuidesX == null && r.GuidesY == null && TightFillBox(r.Content[i], world) is { } tight)
                    {
                        b = Intersect(tight, nodeClip);
                        if (!Touches(b, clip)) continue;
                    }
                    if (s_scrollTrace) Console.Error.WriteLine($"[scroll] still {r.Content[i].GetType().Name} [{b.X},{b.Y} {b.W}x{b.H}]");
                    StillBox(b, clip, dst, damage);
                }
                for (int i = 0; i < r.KidCount; i++)
                    if (r.Kids[i] is { } k) AddStill(k, world, nodeClip, clip, dst, damage);
            }

            /// <summary>The pixels an axis-aligned rectangle fill can write, without the margin every
            /// other box carries: the antialiased edge reaches half a pixel out. The margin is for
            /// glyph hinting, filter spill and stroke joins; a fill has none of them, and a panel that
            /// ends where the scroll viewer begins (a header, a scroll bar) must not count as over it
            /// -- it would be damage on every scrolled frame, across the viewport's whole width, and
            /// every text run that damage touched besides. Not for a visual with guidelines, whose
            /// snapping moves edges.</summary>
            private static Scissor? TightFillBox(DrawingPrimitive p, Matrix3x2 world)
            {
                Rect rect;
                switch (p)
                {
                    case GeometryFill { Geometry: RectangleGeometry rg, IsGlyph: false }: rect = rg.Rect; break;
                    case GeometryFill { Geometry: RoundedRectangleGeometry rr, IsGlyph: false }: rect = rr.Rect; break;
                    case GeometryDrawing { Geometry: RectangleGeometry rg, Stroke: null }: rect = rg.Rect; break;
                    case GeometryDrawing { Geometry: RoundedRectangleGeometry rr, Stroke: null }: rect = rr.Rect; break;
                    default: return null;
                }
                if (world.M12 != 0f || world.M21 != 0f) return null;
                Vector2 a = Vector2.Transform(new Vector2((float)rect.X, (float)rect.Y), world);
                Vector2 b = Vector2.Transform(new Vector2((float)(rect.X + rect.Width), (float)(rect.Y + rect.Height)), world);
                // An edge on a pixel boundary writes nothing past it (a pixel whose centre is half a
                // pixel out has coverage 0); any other edge is given the half pixel.
                static float Lo(float v) => MathF.Abs(v - MathF.Round(v)) < 1e-3f ? MathF.Round(v) : v - 0.5f;
                static float Hi(float v) => MathF.Abs(v - MathF.Round(v)) < 1e-3f ? MathF.Round(v) : v + 0.5f;
                return ToScissor(Lo(MathF.Min(a.X, b.X)), Lo(MathF.Min(a.Y, b.Y)), Hi(MathF.Max(a.X, b.X)), Hi(MathF.Max(a.Y, b.Y)), 0);
            }

            private void StillBox(Scissor b, Scissor clip, Scissor dst, List<Scissor> damage)
            {
                Scissor at = Intersect(b, clip);
                if (!at.IsEmpty) damage.Add(at);
                Scissor dragged = Intersect(Translate(b, _scrollDx, _scrollDy), dst);
                if (!dragged.IsEmpty) damage.Add(dragged);
            }

            private static bool Touches(Scissor a, Scissor b)
                => !a.IsEmpty && !b.IsEmpty && a.X < b.X + b.W && b.X < a.X + a.W && a.Y < b.Y + b.H && b.Y < a.Y + a.H;

            /// <summary>A solid axis-aligned rectangle that paints every pixel of <paramref name="clip"/>
            /// fully, so every one of them alike: shifting pixels within the clip cannot change what it
            /// contributes. Its edges must clear the clip by a pixel (antialiasing, snapping) wherever
            /// the clip is not at the target's edge.</summary>
            private bool CoversUniformly(DrawingPrimitive p, Matrix3x2 world, Scissor nodeClip, Scissor clip)
            {
                Rect rect;
                switch (p)
                {
                    case GeometryFill { Geometry: RectangleGeometry rg, Brush: SolidColorBrush, IsGlyph: false }: rect = rg.Rect; break;
                    case GeometryDrawing { Geometry: RectangleGeometry rg, Fill: SolidColorBrush, Stroke: null }: rect = rg.Rect; break;
                    default: return false;
                }
                if (world.M12 != 0f || world.M21 != 0f) return false;
                if (nodeClip.X > clip.X || nodeClip.Y > clip.Y || nodeClip.X + nodeClip.W < clip.X + clip.W
                    || nodeClip.Y + nodeClip.H < clip.Y + clip.H) return false;
                Vector2 a = Vector2.Transform(new Vector2((float)rect.X, (float)rect.Y), world);
                Vector2 b = Vector2.Transform(new Vector2((float)(rect.X + rect.Width), (float)(rect.Y + rect.Height)), world);
                float x0 = MathF.Min(a.X, b.X), x1 = MathF.Max(a.X, b.X), y0 = MathF.Min(a.Y, b.Y), y1 = MathF.Max(a.Y, b.Y);
                // An edge on a pixel boundary at or beyond the clip's edge leaves every pixel inside
                // fully covered; any other edge must clear it by a pixel (antialiasing, snapping).
                static bool OnGrid(float v) => MathF.Abs(v - MathF.Round(v)) < 1e-3f;
                bool l = x0 <= clip.X - 1 || (OnGrid(x0) && x0 <= clip.X) || (clip.X <= 0 && x0 <= 0);
                bool t = y0 <= clip.Y - 1 || (OnGrid(y0) && y0 <= clip.Y) || (clip.Y <= 0 && y0 <= 0);
                bool r = x1 >= clip.X + clip.W + 1 || (OnGrid(x1) && x1 >= clip.X + clip.W) || (clip.X + clip.W >= _w && x1 >= _w);
                bool btm = y1 >= clip.Y + clip.H + 1 || (OnGrid(y1) && y1 >= clip.Y + clip.H) || (clip.Y + clip.H >= _h && y1 >= _h);
                return l && t && r && btm;
            }

            private const int MaxRawRects = 512;

            private Rec Visit(Rec? old, SceneVisual v, Matrix3x2 parentWorld, Scissor parentClip, bool fresh, bool inSnapshot, bool shiftOk = false)
            {
                Matrix3x2 world = v.LocalToParent * parentWorld;
                Scissor clip = v.Clip is Rect cr ? Intersect(parentClip, RectBox(cr, world)) : parentClip;
                bool kidsInSnapshot = inSnapshot || v.Snapshot != null;

                bool sameState = old != null && !fresh && SameState(old, v);
                // A subtree that only MOVED, by whole device pixels, inside a clip that did not: its
                // pixels can be shifted rather than redrawn (see TryScroll).
                if (!sameState && old != null && !fresh && shiftOk && _scrollRec == null
                    && TryScroll(old, v, parentWorld, parentClip, inSnapshot) is { } moved)
                    return moved;
                bool sameContent = sameState && SameContent(old!, v);
                // A fresh recording of what was drawn here before, scrolled (a WinForms control's
                // repaint): see TryContentScroll.
                if (s_scrollTrace && sameState && !sameContent && v.Clip.HasValue)
                    Console.Error.WriteLine($"[scroll] candidate content={v.Content.Count} kids={v.Children.Count} shiftOk={shiftOk} repainted={LooksRepainted(old!, v)}");
                if (sameState && !sameContent && s_scroll && v.Clip.HasValue && LooksRepainted(old!, v)
                    && TryContentScroll(old!, v, parentWorld, parentClip, inSnapshot, allowShift: shiftOk && _scrollRec == null) is { } scrolled)
                    return scrolled;
                // A content change in a visual that is drawn whole -- an opacity group (whose being a
                // layer at all depends on how much it draws), a masked visual (its mask is mapped to its
                // content's bounds), a snapshot -- changes all of it.
                bool contentPatchable = sameState && !sameContent
                    && v.Opacity >= 0.999 && v.OpacityMask == null && v.Snapshot == null;

                if (!sameState || (!sameContent && !contentPatchable))
                {
                    Scissor was = old?.Bounds ?? default;
                    Rec rec = new Rec();
                    Capture(rec, v);
                    MeasureOwn(rec, v, world, clip, 0, v.Content.Count);
                    AddTexts(rec, inSnapshot);
                    CheckLive(rec, addDamage: false);   // keeps the sources' trackers current
                    int n = v.Children.Count;
                    rec.Kids = n == 0 ? Array.Empty<Rec?>() : new Rec?[n];
                    rec.KidCount = n;
                    // A changed visual's whole subtree is damaged where it was and where it is, so the
                    // children only need measuring, not diffing.
                    int mark = _raw.Count;
                    for (int i = 0; i < n; i++)
                        rec.Kids[i] = Visit(null, v.Children[i], world, clip, fresh: true, kidsInSnapshot);
                    _raw.RemoveRange(mark, _raw.Count - mark);
                    rec.Bounds = Finish(rec, v, world, clip);
                    if (!fresh)
                    {
                        if (old != null) _raw.Add(was);
                        _raw.Add(rec.Bounds);
                    }
                    return rec;
                }

                Scissor before = old!.Bounds;
                int mark2 = _raw.Count;
                if (contentPatchable) PatchContent(old, v, world, clip);
                if (old.Volatile) _forceFull = true;
                AddTexts(old, inSnapshot);
                CheckLive(old, addDamage: true);
                int nk = v.Children.Count;
                // Pixels under an effect, a mask, a clip geometry, a snapshot or a group opacity are
                // not where their content put them, so nothing below one is shifted.
                bool kidsShiftOk = shiftOk && !kidsInSnapshot && v.Effect == null && v.OpacityMask == null
                    && v.ClipGeometry == null && v.Opacity >= 0.999;
                if (old.Kids.Length < nk) Array.Resize(ref old.Kids, nk);
                for (int i = 0; i < nk; i++)
                    old.Kids[i] = Visit(i < old.KidCount ? old.Kids[i] : null, v.Children[i], world, clip, fresh: false, kidsInSnapshot, kidsShiftOk);
                for (int i = nk; i < old.KidCount; i++)
                {
                    if (old.Kids[i] is { } gone) _raw.Add(gone.Bounds);
                    old.Kids[i] = null;
                }
                old.KidCount = nk;
                old.Bounds = Finish(old, v, world, clip);
                if (_raw.Count > mark2)
                {
                    // An opacity group composites its children as one layer only when it draws more
                    // than one thing; a change below may flip that and re-round every pixel of it.
                    if (v.Opacity < 0.999)
                    {
                        _raw.RemoveRange(mark2, _raw.Count - mark2);
                        _raw.Add(before);
                        _raw.Add(old.Bounds);
                    }
                    else if (v.Effect != null && old.Clipped)
                    {
                        // A change cut away by the clip can still show through the effect's spread.
                        _raw.RemoveRange(mark2, _raw.Count - mark2);
                        _raw.Add(before);
                        _raw.Add(old.Bounds);
                    }
                    else
                        SpreadDamage(v, _raw, mark2, Union(before, old.Bounds));
                }
                return old;
            }

            private void AddTexts(Rec r, bool inSnapshot)
            {
                // Inside a snapshot the runs are drawn into the snapshot's own picture, not here.
                if (inSnapshot) return;
                for (int i = 0; i < r.ContentCount; i++)
                    if ((r.Kinds[i] & KindText) != 0 && !r.Boxes[i].IsEmpty) _texts.Add(r.Boxes[i]);
            }

            /// <summary>Same visual, same state, different content: damage only what differs. The
            /// lists are aligned on their common prefix and suffix (by reference); every primitive
            /// outside both -- removed, added or replaced -- is damaged where it was and where it is.
            /// A pixel no changed primitive touches sees the same primitives in the same order.</summary>
            private void PatchContent(Rec r, SceneVisual v, Matrix3x2 world, Scissor clip)
            {
                List<DrawingPrimitive> c = v.Content;
                int on = r.ContentCount, nn = c.Count;
                int pre = 0;
                while (pre < on && pre < nn && ReferenceEquals(r.Content[pre], c[pre])) pre++;
                int suf = 0;
                while (suf < on - pre && suf < nn - pre && ReferenceEquals(r.Content[on - 1 - suf], c[nn - 1 - suf])) suf++;
                for (int i = pre; i < on - suf; i++) _raw.Add(r.Boxes[i]);

                // Keep the suffix's measurements, then take the new content.
                var tailBoxes = new Scissor[suf];
                var tailKinds = new byte[suf];
                Array.Copy(r.Boxes, on - suf, tailBoxes, 0, suf);
                Array.Copy(r.Kinds, on - suf, tailKinds, 0, suf);
                CaptureContent(r, v);
                Array.Copy(tailBoxes, 0, r.Boxes, nn - suf, suf);
                Array.Copy(tailKinds, 0, r.Kinds, nn - suf, suf);
                MeasureOwn(r, v, world, clip, pre, nn - suf);
                for (int i = pre; i < nn - suf; i++) _raw.Add(r.Boxes[i]);
            }

            /// <summary>The subtree's bounds from its own box and its children's, stamped on the visual
            /// for the renderer's culling.</summary>
            private Scissor Finish(Rec rec, SceneVisual v, Matrix3x2 world, Scissor clip)
            {
                bool cut = false;
                for (int i = 0; i < rec.ContentCount && !cut; i++) cut = (rec.Kinds[i] & KindCut) != 0;
                for (int i = 0; i < rec.KidCount && !cut; i++) cut = rec.Kids[i] is { Clipped: true };
                rec.Clipped = cut;
                Scissor b;
                if (v.Snapshot is { } snap)
                    b = SnapshotBox(snap, world);
                else
                {
                    b = rec.Own;
                    for (int i = 0; i < rec.KidCount; i++)
                        if (rec.Kids[i] is { } k) b = Union(b, k.Bounds);
                    b = EffectSpread(v, b);
                    // The boxes above are cut to the clip, but an effect spreads what its content draws
                    // BEFORE the clip: a card just above a scroll viewer's edge, all of it clipped away,
                    // still casts its shadow down into the viewport. Its box measured that way was empty,
                    // so a partial frame skipped it and lost the shadow.
                    if (v.Effect != null && rec.Clipped)
                        b = Union(b, SubtreeBox(v, world, out _));
                }
                b = Intersect(b, clip);
                v.DamageStamp = Stamp;
                v.DamageX = b.X; v.DamageY = b.Y; v.DamageW = b.W; v.DamageH = b.H;
                return b;
            }

            /// <summary>Measures content primitives [from, to) into Boxes/Kinds, then recomputes Own
            /// and Volatile over all of them.</summary>
            private void MeasureOwn(Rec rec, SceneVisual v, Matrix3x2 world, Scissor clip, int from, int to)
            {
                List<DrawingPrimitive> c = v.Content;
                for (int i = from; i < to; i++)
                {
                    DrawingPrimitive p = c[i];
                    float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
                    byte kind = 0;
                    bool known;
                    if (p is Viewport3DDraw v3)
                    {
                        // An empty viewport means "the whole target"; otherwise the scene projects into it.
                        known = v3.Viewport.Width > 0 && v3.Viewport.Height > 0;
                        if (known) AccRect(v3.Viewport.X, v3.Viewport.Y, v3.Viewport.Width, v3.Viewport.Height, world, 0f,
                                           ref minX, ref minY, ref maxX, ref maxY);
                        kind |= KindAlways;
                    }
                    else known = PrimitiveBox(p, world, ref minX, ref minY, ref maxX, ref maxY);
                    Scissor whole = minX <= maxX ? ToScissor(minX, minY, maxX, maxY, ContentMargin) : new Scissor(0, 0, 0, 0);
                    rec.Boxes[i] = Intersect(clip, whole);
                    if (!whole.IsEmpty && (rec.Boxes[i].W != whole.W || rec.Boxes[i].H != whole.H)) kind |= KindCut;
                    if (!known) { kind |= KindUnknown; rec.Boxes[i] = clip; }
                    if (p is GlyphRunDraw or WpfTextRunDraw or GdiPlusTextDraw) kind |= KindText;
                    if (LiveBrushOf(p) != null) kind |= KindLive;
                    rec.Kinds[i] = kind;
                }
                Scissor own = new Scissor(0, 0, 0, 0);
                bool vol = false;
                for (int i = 0; i < rec.ContentCount; i++)
                {
                    own = Union(own, rec.Boxes[i]);
                    if ((rec.Kinds[i] & KindUnknown) != 0) vol = true;
                }
                rec.Own = own;
                // A 3D viewport, anything whose extent or change is not visible
                // from here: redraw the whole target whenever it is in the tree.
                rec.Volatile = vol;
                if (vol) _forceFull = true;
            }

            private static void Capture(Rec r, SceneVisual v)
            {
                r.Local = v.LocalToParent;
                r.Opacity = v.Opacity;
                r.HasClip = v.Clip.HasValue;
                r.Clip = v.Clip ?? default;
                r.ClipGeometry = v.ClipGeometry;
                r.Effect = v.Effect;
                r.Mask = v.OpacityMask;
                r.Snapshot = v.Snapshot;
                if (v.Snapshot is { } s)
                {
                    r.SnapSource = s.Source; r.SnapDest = s.Dest; r.SnapStretch = s.GdiStretch; r.SnapBlend = s.WindowBlend;
                }
                r.Aliased = v.AliasedEdges;
                r.Nearest = v.NearestBitmapScaling;
                r.GuidesX = v.GuidelinesX;
                r.GuidesY = v.GuidelinesY;
                CaptureContent(r, v);
            }

            private static void CaptureContent(Rec r, SceneVisual v)
            {
                int n = v.Content.Count;
                if (r.Content.Length < n)
                {
                    Array.Resize(ref r.Content, n);
                    Array.Resize(ref r.Boxes, n);
                    Array.Resize(ref r.Kinds, n);
                }
                for (int i = 0; i < n; i++) r.Content[i] = v.Content[i];
                for (int i = n; i < r.ContentCount; i++) r.Content[i] = null!;
                r.ContentCount = n;
            }

            private static bool SameRect(Rect a, Rect b)
                => a.X == b.X && a.Y == b.Y && a.Width == b.Width && a.Height == b.Height;

            private static bool SameState(Rec r, SceneVisual v, bool ignoreTranslation = false)
            {
                Matrix3x2 l = v.LocalToParent;
                if (ignoreTranslation ? !SameLinear(r.Local, l) : r.Local != l) return false;
                if (r.Opacity != v.Opacity) return false;
                if (r.HasClip != v.Clip.HasValue || (v.Clip is Rect c && !SameRect(c, r.Clip))) return false;
                if (!ReferenceEquals(r.ClipGeometry, v.ClipGeometry) || !ReferenceEquals(r.Effect, v.Effect)
                    || !ReferenceEquals(r.Mask, v.OpacityMask) || !ReferenceEquals(r.Snapshot, v.Snapshot)) return false;
                if (v.Snapshot is { } s && (!SameRect(s.Source, r.SnapSource) || !SameRect(s.Dest, r.SnapDest)
                    || s.GdiStretch != r.SnapStretch || s.WindowBlend != r.SnapBlend)) return false;
                if (r.Aliased != v.AliasedEdges || r.Nearest != v.NearestBitmapScaling) return false;
                return ReferenceEquals(r.GuidesX, v.GuidelinesX) && ReferenceEquals(r.GuidesY, v.GuidelinesY);
            }

            private static bool SameLinear(Matrix3x2 a, Matrix3x2 b)
                => a.M11 == b.M11 && a.M12 == b.M12 && a.M21 == b.M21 && a.M22 == b.M22;

            /// <summary>Two records of one visual, a frame apart: the same state and the same content
            /// (by reference).</summary>
            private static bool SameRec(Rec o, Rec n, bool ignoreTranslation)
            {
                if (ignoreTranslation ? !SameLinear(o.Local, n.Local) : o.Local != n.Local) return false;
                if (o.Opacity != n.Opacity || o.HasClip != n.HasClip || (n.HasClip && !SameRect(o.Clip, n.Clip))) return false;
                if (!ReferenceEquals(o.ClipGeometry, n.ClipGeometry) || !ReferenceEquals(o.Effect, n.Effect)
                    || !ReferenceEquals(o.Mask, n.Mask) || !ReferenceEquals(o.Snapshot, n.Snapshot)) return false;
                if (n.Snapshot != null && (!SameRect(o.SnapSource, n.SnapSource) || !SameRect(o.SnapDest, n.SnapDest)
                    || o.SnapStretch != n.SnapStretch || o.SnapBlend != n.SnapBlend)) return false;
                if (o.Aliased != n.Aliased || o.Nearest != n.Nearest) return false;
                if (!ReferenceEquals(o.GuidesX, n.GuidesX) || !ReferenceEquals(o.GuidesY, n.GuidesY)) return false;
                if (o.ContentCount != n.ContentCount) return false;
                for (int i = 0; i < n.ContentCount; i++)
                    if (!ReferenceEquals(o.Content[i], n.Content[i])) return false;
                return true;
            }

            private static bool SameContent(Rec r, SceneVisual v)
            {
                List<DrawingPrimitive> c = v.Content;
                if (c.Count != r.ContentCount) return false;
                for (int i = 0; i < c.Count; i++)
                    if (!ReferenceEquals(c[i], r.Content[i])) return false;
                return true;
            }

            /// <summary>WGPU_DAMAGE_RECT_COST=<pixels>: also merge damage rectangles while the cheapest
            /// merge adds fewer pixels than this (each rectangle re-issues the draws it touches). Off
            /// by default: measured on the gallery's scroll, any value that merged at all chained
            /// merges into boxes over 70% of the target, i.e. full frames, and was no faster.</summary>
            private static readonly long s_rectCost =
                long.TryParse(Environment.GetEnvironmentVariable("WGPU_DAMAGE_RECT_COST"), out long rc) ? rc : 0;

            /// <summary>Clamp to the target, drop the empty, and reduce to few DISJOINT rectangles
            /// (the main pass draws each item once per rectangle, so an overlap would blend twice).
            /// <para>Two rectangles are merged into their bounding box only when that costs few pixels
            /// over the two (near neighbours: a draw per rectangle costs more than the pixels between
            /// them). Overlaps that would cost many -- a full-width strip across a card -- are cut
            /// apart instead. Only past <c>MaxRects</c> are pairs merged at a real cost, cheapest first.
            /// Plain bounding-box merging turned a scroll's strip, overlay and a few changing cards
            /// into the whole viewport.</para></summary>
            internal static void Normalize(List<Scissor> raw, List<Scissor> outRects, int width, int height)
            {
                const int MaxRects = 16, Near = 8, Scattered = 256;
                var target = new Scissor(0, 0, width, height);
                outRects.Clear();
                foreach (Scissor r in raw)
                {
                    Scissor c = Intersect(r, target);
                    if (!c.IsEmpty) outRects.Add(c);
                }
                if (outRects.Count > Scattered)
                {
                    // Scattered all over: their bounding box.
                    Scissor all = outRects[0];
                    foreach (Scissor r in outRects) all = Union(all, r);
                    outRects.Clear();
                    outRects.Add(all);
                    return;
                }
                for (int round = 0; ; round++)
                {
                    MergeCheap(outRects, Near);
                    MakeDisjoint(outRects);
                    if (outRects.Count <= 1) return;
                    if (round == 64)
                    {
                        // Cutting keeps making pieces faster than merging removes them.
                        Scissor all = outRects[0];
                        foreach (Scissor r in outRects) all = Union(all, r);
                        outRects.Clear();
                        outRects.Add(all);
                        return;
                    }
                    // Merge the pair whose bounding box adds the least area, then cut again -- while
                    // there are too many, or while that costs fewer pixels than a rectangle does: every
                    // draw the rectangle touches is issued again for it (RectCost).
                    long best = long.MaxValue; int bi = 0, bj = 1;
                    for (int i = 0; i < outRects.Count; i++)
                        for (int j = i + 1; j < outRects.Count; j++)
                        {
                            long grow = Area(Union(outRects[i], outRects[j])) - Area(outRects[i]) - Area(outRects[j]);
                            if (grow < best) { best = grow; bi = i; bj = j; }
                        }
                    if (outRects.Count <= MaxRects && best > s_rectCost) return;
                    outRects[bi] = Union(outRects[bi], outRects[bj]);
                    outRects.RemoveAt(bj);
                }
            }

            private static long Area(Scissor s) => s.IsEmpty ? 0 : (long)s.W * s.H;

            /// <summary>Merge pairs whose bounding box covers little beyond the two: contained,
            /// overlapping or within <paramref name="near"/> pixels, with the box adding at most
            /// a near-wide band along the smaller one's edge.</summary>
            private static void MergeCheap(List<Scissor> rects, int near)
            {
                bool changed = true;
                while (changed)
                {
                    changed = false;
                    for (int i = 0; i < rects.Count; i++)
                        for (int j = rects.Count - 1; j > i; j--)
                        {
                            Scissor a = rects[i], b = rects[j];
                            if (Intersect(Inflate(a, near, near, near, near), b).IsEmpty) continue;
                            Scissor u = Union(a, b);
                            long waste = Area(u) - Area(a) - Area(b) + Area(Intersect(a, b));
                            long allowance = (long)near * (Math.Min(a.W, b.W) + Math.Min(a.H, b.H)) * 2;
                            if (waste > allowance) continue;
                            rects[i] = u;
                            rects.RemoveAt(j);
                            changed = true;
                        }
                }
            }

            /// <summary>Cut overlapping rectangles apart: each keeps only what no earlier one covers.</summary>
            private static void MakeDisjoint(List<Scissor> rects)
            {
                var result = new List<Scissor>(rects.Count);
                var pieces = new List<Scissor>();
                var next = new List<Scissor>();
                foreach (Scissor r in rects)
                {
                    pieces.Clear();
                    pieces.Add(r);
                    foreach (Scissor k in result)
                    {
                        next.Clear();
                        foreach (Scissor p in pieces) Subtract(p, k, next);
                        pieces.Clear();
                        pieces.AddRange(next);
                        if (pieces.Count == 0) break;
                    }
                    result.AddRange(pieces);
                }
                rects.Clear();
                rects.AddRange(result);
            }

            /// <summary><paramref name="a"/> less <paramref name="b"/>, as up to four rectangles.</summary>
            private static void Subtract(Scissor a, Scissor b, List<Scissor> into)
            {
                Scissor o = Intersect(a, b);
                if (o.IsEmpty) { into.Add(a); return; }
                int ax1 = a.X + a.W, ay1 = a.Y + a.H, ox1 = o.X + o.W, oy1 = o.Y + o.H;
                if (o.Y > a.Y) into.Add(new Scissor(a.X, a.Y, a.W, o.Y - a.Y));                // above
                if (oy1 < ay1) into.Add(new Scissor(a.X, oy1, a.W, ay1 - oy1));                // below
                if (o.X > a.X) into.Add(new Scissor(a.X, o.Y, o.X - a.X, o.H));                // left
                if (ox1 < ax1) into.Add(new Scissor(ox1, o.Y, ax1 - ox1, o.H));                // right
            }
        }

        // ---- rendering into a persistent target --------------------------------------------------

        /// <summary>
        /// Render <paramref name="root"/> into <paramref name="targetView"/>, a texture that holds the
        /// previous frame. <paramref name="damage"/> null renders the whole target (cleared first);
        /// otherwise only those disjoint rectangles are cleared and redrawn and every other pixel is
        /// left as it was. An empty list renders nothing. <paramref name="cullStamp"/> is the
        /// tracker stamp whose bounds may be used to skip subtrees (0 = none).
        /// </summary>
        internal void RenderFrameDamaged(SceneVisual root, IntPtr targetView, WGPUTextureFormat format, int width, int height,
            RgbaColor background, bool transparentTarget, List<Scissor>? damage, int cullStamp,
            IntPtr blitView = default, WGPUTextureFormat blitFormat = default, IntPtr blitBindGroup = default,
            ScrollShift? shift = null, IntPtr scratchView = default)
        {
            try
            {
                _srgbOutput = format is WGPUTextureFormat.RGBA8UnormSrgb or WGPUTextureFormat.BGRA8UnormSrgb;
                _transparentTarget = transparentTarget;
                bool partial = damage != null;
                IntPtr encoder = IntPtr.Zero;
                if (!partial || damage!.Count > 0)
                {
                    long c0 = System.Diagnostics.Stopwatch.GetTimestamp();
                    long ca0 = GC.GetAllocatedBytesForCurrentThread();
                    List<LayerPass> plan = _plan; plan.Clear();
                    _contentTexFrame.Clear();
                    bool cull = partial && s_damageCull;
                    DrawData mainData;
                    while (true)
                    {
                        mainData = RentDrawData();
                        if (background.A >= 0.999f) mainData.ClearPaper = background;
                        if (partial) AddDamageClears(mainData, damage!, background, width, height);
                        if (cull)
                        {
                            mainData.Culled ??= new List<Scissor>();
                            _cullData = mainData; _cullDamage = damage; _cullStamp = cullStamp;
                        }
                        try
                        {
                            CollectVisual(root, Matrix3x2.Identity, 1.0, new Scissor(0, 0, width, height), mainData, plan, width, height, format);
                        }
                        finally { _cullData = null; _cullDamage = null; _cullStamp = 0; }
                        // A skipped subtree could have changed a ClearType paper answer: collect the
                        // frame again without skipping. The plan so far is kept -- the layers it bakes
                        // went into the cache and the second collect composites them.
                        if (cull && mainData.CullConflict) { cull = false; PerfCullRetries++; continue; }
                        break;
                    }
                    PerfCollectTicks += System.Diagnostics.Stopwatch.GetTimestamp() - c0;
                    long ca1 = GC.GetAllocatedBytesForCurrentThread();
                    PerfCollectAlloc += ca1 - ca0;

                    IntPtr atlasView = EnsureAtlasView(AnyText(mainData, plan));
                    encoder = wgpuDeviceCreateCommandEncoder(_ctx.Device, IntPtr.Zero);
                    long e0 = System.Diagnostics.Stopwatch.GetTimestamp();
                    BuildBatchedGeometry(plan, mainData);
                    BuildBatchedStorage();
                    FlushPendingTexUploads(encoder);
                    if (partial && shift is { } sh && scratchView != IntPtr.Zero) RecordShift(encoder, targetView, scratchView, format, sh);
                    foreach (LayerPass lp in plan) ExecutePass(encoder, lp, atlasView);
                    _damagePassW = width; _damagePassH = height;
                    ExecutePass(encoder, new LayerPass(targetView, false, background, mainData, format)
                        { LoadPreserve = partial, Damage = damage }, atlasView);
                    PerfExecAlloc += GC.GetAllocatedBytesForCurrentThread() - ca1;
                    PerfEncodeTicks += System.Diagnostics.Stopwatch.GetTimestamp() - e0;
                }
                if (blitView != IntPtr.Zero)
                {
                    if (encoder == IntPtr.Zero) encoder = wgpuDeviceCreateCommandEncoder(_ctx.Device, IntPtr.Zero);
                    RecordBlit(encoder, blitView, blitFormat, blitBindGroup);
                }
                if (encoder != IntPtr.Zero)
                {
                    IntPtr commandBuffer = wgpuCommandEncoderFinish(encoder, IntPtr.Zero);
                    IntPtr* cmds = stackalloc IntPtr[1];
                    cmds[0] = commandBuffer;
                    long s0 = System.Diagnostics.Stopwatch.GetTimestamp();
                    wgpuQueueSubmit(_ctx.Queue, 1, cmds);
                    PerfSubmitTicks += System.Diagnostics.Stopwatch.GetTimestamp() - s0;
                    DeferReleaseEncoder(encoder);
                    DeferReleaseCmdBuffer(commandBuffer);
                }
            }
            finally
            {
                FlushFrameReleases();
            }
            _idScene = root; _idW = width; _idH = height; _idValid = false;
        }

        // The main pass's target size, for VertexBox (its LayerPass carries no TexW/TexH).
        private int _damagePassW, _damagePassH;

        /// <summary>The pixels a run of indexed triangles can cover, in the pass's own pixel
        /// coordinates: their vertices' box, a pixel wider each way. Rasterization writes only
        /// inside the triangles, so a damage rectangle outside this box gets nothing from the run.</summary>
        private static void VertexBox(DrawData data, uint firstIndex, uint indexCount, int originX, int originY, int w, int h,
            out int x0, out int y0, out int x1, out int y1)
        {
            List<uint> idx = data.Indices;
            List<float> v = data.Verts;
            float nx0 = float.MaxValue, ny0 = float.MaxValue, nx1 = float.MinValue, ny1 = float.MinValue;
            uint end = Math.Min(firstIndex + indexCount, (uint)idx.Count);
            for (uint k = firstIndex; k < end; k++)
            {
                int at = (int)idx[(int)k] * FloatsPerVertex;
                if (at + 1 >= v.Count) continue;
                float px = v[at], py = v[at + 1];
                if (px < nx0) nx0 = px; if (px > nx1) nx1 = px;
                if (py < ny0) ny0 = py; if (py > ny1) ny1 = py;
            }
            if (nx0 > nx1 || w <= 0 || h <= 0) { x0 = y0 = int.MinValue / 4; x1 = y1 = int.MaxValue / 4; return; }
            // NDC to pixels: x right, y DOWN.
            x0 = (int)MathF.Floor((nx0 + 1f) * 0.5f * w) - 1;
            x1 = (int)MathF.Ceiling((nx1 + 1f) * 0.5f * w) + 1;
            y0 = (int)MathF.Floor((1f - ny1) * 0.5f * h) - 1;
            y1 = (int)MathF.Ceiling((1f - ny0) * 0.5f * h) + 1;
        }

        /// <summary>The partial frame's clear: a SourceCopy quad of the background over each damage
        /// rectangle, writing the colour exactly as a clear would (not premultiplied: the clear value is
        /// not). Marked as writing no paper, so the ClearType paper search sees through them to
        /// <see cref="DrawData.ClearPaper"/> just as it does in a full frame.</summary>
        private void AddDamageClears(DrawData data, List<Scissor> damage, RgbaColor bg, int width, int height)
        {
            foreach (Scissor d in damage)
            {
                uint baseVertex = (uint)(data.Verts.Count / FloatsPerVertex);
                float x0 = d.X, y0 = d.Y, x1 = d.X + d.W, y1 = d.Y + d.H;
                AddVertex(data.Verts, ToNdc(new Vector2(x0, y0), width, height), bg.R, bg.G, bg.B, bg.A, 0f, 0f);
                AddVertex(data.Verts, ToNdc(new Vector2(x1, y0), width, height), bg.R, bg.G, bg.B, bg.A, 0f, 0f);
                AddVertex(data.Verts, ToNdc(new Vector2(x1, y1), width, height), bg.R, bg.G, bg.B, bg.A, 0f, 0f);
                AddVertex(data.Verts, ToNdc(new Vector2(x0, y1), width, height), bg.R, bg.G, bg.B, bg.A, 0f, 0f);
                uint firstIndex = (uint)data.Indices.Count;
                AddQuadIndices(data.Indices, baseVertex);
                data.Draws.Add(new DrawItem(firstIndex, 6, d, FillKind.Solid, IntPtr.Zero, sourceCopy: true));
                (data.ClearDraws ??= new())[data.Draws.Count - 1] = true;
            }
        }

        // ---- blit --------------------------------------------------------------------------------

        private const string BlitWgsl = @"
@group(0) @binding(0) var src : texture_2d<f32>;
@vertex fn vs_main(@builtin(vertex_index) i : u32) -> @builtin(position) vec4<f32> {
    let x = f32((i << 1u) & 2u) * 2.0 - 1.0;
    let y = f32(i & 2u) * 2.0 - 1.0;
    return vec4<f32>(x, y, 0.0, 1.0);
}
@fragment fn fs_main(@builtin(position) p : vec4<f32>) -> @location(0) vec4<f32> {
    return textureLoad(src, vec2<i32>(floor(p.xy)), 0);
}
";
        private IntPtr _blitModule, _blitIndices;
        private readonly Dictionary<WGPUTextureFormat, IntPtr> _blitPipelines = new();

        private IntPtr BlitPipeline(WGPUTextureFormat format)
        {
            if (_blitPipelines.TryGetValue(format, out IntPtr p)) return p;
            if (_blitModule == IntPtr.Zero) _blitModule = CompileWgsl(BlitWgsl);
            byte[] vsEntry = System.Text.Encoding.UTF8.GetBytes("vs_main");
            byte[] fsEntry = System.Text.Encoding.UTF8.GetBytes("fs_main");
            fixed (byte* pVs = vsEntry)
            fixed (byte* pFs = fsEntry)
            {
                // No blending: the blit REPLACES the swap chain image, alpha included.
                var colorTarget = new WGPUColorTargetState { format = format, blend = null, writeMask = WGPUColorWriteMask_All };
                var fragment = new WGPUFragmentState
                {
                    module = _blitModule,
                    entryPoint = new WGPUStringView { data = pFs, length = (nuint)fsEntry.Length },
                    targetCount = 1,
                    targets = &colorTarget,
                };
                var desc = new WGPURenderPipelineDescriptor
                {
                    layout = IntPtr.Zero,
                    vertex = new WGPUVertexState
                    {
                        module = _blitModule,
                        entryPoint = new WGPUStringView { data = pVs, length = (nuint)vsEntry.Length },
                        bufferCount = 0,
                        buffers = null,
                    },
                    primitive = new WGPUPrimitiveState
                    {
                        topology = WGPUPrimitiveTopology.TriangleList,
                        frontFace = WGPUFrontFace.CCW,
                        cullMode = WGPUCullMode.None,
                    },
                    multisample = new WGPUMultisampleState { count = 1, mask = 0xFFFFFFFF },
                    fragment = &fragment,
                };
                p = wgpuDeviceCreateRenderPipeline(_ctx.Device, &desc);
            }
            _blitPipelines[format] = p;
            return p;
        }

        // ---- scroll shift ------------------------------------------------------------------------

        /// <summary>Pixels of the persistent target to move before a frame's damage is drawn: the
        /// rectangle <see cref="Src"/> goes to <see cref="Src"/> + (<see cref="Dx"/>, <see cref="Dy"/>).</summary>
        internal readonly struct ScrollShift
        {
            public readonly Scissor Src;
            public readonly int Dx, Dy;
            public ScrollShift(Scissor src, int dx, int dy) { Src = src; Dx = dx; Dy = dy; }
            public Scissor Dst => new Scissor(Src.X + Dx, Src.Y + Dy, Src.W, Src.H);
        }

        // A texel copy at an integer offset, drawn as the blit's full-target triangle under a scissor.
        // Not copyTextureToTexture: a texture cannot be copied onto itself where the regions overlap
        // -- they always do in a scroll -- so the pixels go through a scratch texture either way, and
        // a draw needs nothing (CopyDst, a binding) that the blit does not already use everywhere.
        private const string ShiftWgsl = @"
@group(0) @binding(0) var src : texture_2d<f32>;
@group(0) @binding(1) var<uniform> off : vec4<i32>;
@vertex fn vs_main(@builtin(vertex_index) i : u32) -> @builtin(position) vec4<f32> {
    let x = f32((i << 1u) & 2u) * 2.0 - 1.0;
    let y = f32(i & 2u) * 2.0 - 1.0;
    return vec4<f32>(x, y, 0.0, 1.0);
}
@fragment fn fs_main(@builtin(position) p : vec4<f32>) -> @location(0) vec4<f32> {
    return textureLoad(src, vec2<i32>(floor(p.xy)) - off.xy, 0);
}
";
        private IntPtr _shiftModule;
        private readonly Dictionary<WGPUTextureFormat, IntPtr> _shiftPipelines = new();

        private IntPtr ShiftPipeline(WGPUTextureFormat format)
        {
            if (_shiftPipelines.TryGetValue(format, out IntPtr p)) return p;
            if (_shiftModule == IntPtr.Zero) _shiftModule = CompileWgsl(ShiftWgsl);
            byte[] vsEntry = System.Text.Encoding.UTF8.GetBytes("vs_main");
            byte[] fsEntry = System.Text.Encoding.UTF8.GetBytes("fs_main");
            fixed (byte* pVs = vsEntry)
            fixed (byte* pFs = fsEntry)
            {
                var colorTarget = new WGPUColorTargetState { format = format, blend = null, writeMask = WGPUColorWriteMask_All };
                var fragment = new WGPUFragmentState
                {
                    module = _shiftModule,
                    entryPoint = new WGPUStringView { data = pFs, length = (nuint)fsEntry.Length },
                    targetCount = 1,
                    targets = &colorTarget,
                };
                var desc = new WGPURenderPipelineDescriptor
                {
                    layout = IntPtr.Zero,
                    vertex = new WGPUVertexState
                    {
                        module = _shiftModule,
                        entryPoint = new WGPUStringView { data = pVs, length = (nuint)vsEntry.Length },
                        bufferCount = 0,
                        buffers = null,
                    },
                    primitive = new WGPUPrimitiveState
                    {
                        topology = WGPUPrimitiveTopology.TriangleList,
                        frontFace = WGPUFrontFace.CCW,
                        cullMode = WGPUCullMode.None,
                    },
                    multisample = new WGPUMultisampleState { count = 1, mask = 0xFFFFFFFF },
                    fragment = &fragment,
                };
                p = wgpuDeviceCreateRenderPipeline(_ctx.Device, &desc);
            }
            _shiftPipelines[format] = p;
            return p;
        }

        /// <summary>One draw of the shift: <paramref name="dstView"/>'s pixels inside
        /// <paramref name="scissor"/> become <paramref name="srcView"/>'s (dx, dy) back.</summary>
        private void RecordShiftDraw(IntPtr encoder, IntPtr srcView, IntPtr dstView, WGPUTextureFormat format,
            Scissor scissor, int dx, int dy, bool load)
        {
            IntPtr pipeline = ShiftPipeline(format);
            ReadOnlySpan<int> off = stackalloc int[] { dx, dy, 0, 0 };
            IntPtr buf = _ctx.CreateBufferMapped(System.Runtime.InteropServices.MemoryMarshal.AsBytes(off), WGPUBufferUsage.Uniform);
            IntPtr layout = wgpuRenderPipelineGetBindGroupLayout(pipeline, 0);
            var entries = stackalloc WGPUBindGroupEntry[2];
            entries[0] = new WGPUBindGroupEntry { binding = 0, textureView = srcView };
            entries[1] = new WGPUBindGroupEntry { binding = 1, buffer = buf, offset = 0, size = 16 };
            var desc = new WGPUBindGroupDescriptor { layout = layout, entryCount = 2, entries = entries };
            IntPtr bg = wgpuDeviceCreateBindGroup(_ctx.Device, &desc);
            wgpuBindGroupLayoutRelease(layout);

            IntPtr pass = load ? BeginLoadPass(encoder, dstView) : BeginClearPass(encoder, dstView, new RgbaColor(0, 0, 0, 0));
            wgpuRenderPassEncoderSetPipeline(pass, pipeline);
            wgpuRenderPassEncoderSetBindGroup(pass, 0, bg, 0, null);
            wgpuRenderPassEncoderSetScissorRect(pass, (uint)scissor.X, (uint)scissor.Y, (uint)scissor.W, (uint)scissor.H);
            if (_blitIndices == IntPtr.Zero)
            {
                ReadOnlySpan<uint> idx = stackalloc uint[] { 0, 1, 2, 0 };
                _blitIndices = _ctx.CreateBufferMapped(System.Runtime.InteropServices.MemoryMarshal.AsBytes(idx), WGPUBufferUsage.Index);
            }
            wgpuRenderPassEncoderSetIndexBuffer(pass, _blitIndices, WGPUIndexFormat.Uint32, 0, 16);
            wgpuRenderPassEncoderDrawIndexed(pass, 3, 1, 0, 0, 0);
            wgpuRenderPassEncoderEnd(pass);
            DeferReleasePass(pass);
            DeferReleaseBindGroup(bg);
            DeferReleaseBuffer(buf);
        }

        /// <summary>Move <paramref name="shift"/>'s pixels within <paramref name="targetView"/>, by way
        /// of <paramref name="scratchView"/> (a texture of the target's size and format).</summary>
        private void RecordShift(IntPtr encoder, IntPtr targetView, IntPtr scratchView, WGPUTextureFormat format, ScrollShift shift)
        {
            RecordShiftDraw(encoder, targetView, scratchView, format, shift.Src, 0, 0, load: false);
            RecordShiftDraw(encoder, scratchView, targetView, format, shift.Dst, shift.Dx, shift.Dy, load: true);
            PerfShiftedPixels += (long)shift.Src.W * shift.Src.H;
        }

        [ThreadStatic] internal static long PerfShiftedPixels;

        /// <summary>A bind group that lets <see cref="RecordBlit"/> read <paramref name="sourceView"/>
        /// into a <paramref name="format"/> target. Owned by the caller.</summary>
        internal IntPtr CreateBlitBindGroup(IntPtr sourceView, WGPUTextureFormat format)
        {
            IntPtr layout = wgpuRenderPipelineGetBindGroupLayout(BlitPipeline(format), 0);
            var entries = stackalloc WGPUBindGroupEntry[1];
            entries[0] = new WGPUBindGroupEntry { binding = 0, textureView = sourceView };
            var desc = new WGPUBindGroupDescriptor { layout = layout, entryCount = 1, entries = entries };
            IntPtr bg = wgpuDeviceCreateBindGroup(_ctx.Device, &desc);
            wgpuBindGroupLayoutRelease(layout);
            return bg;
        }

        private void RecordBlit(IntPtr encoder, IntPtr view, WGPUTextureFormat format, IntPtr bindGroup)
        {
            IntPtr pass = BeginClearPass(encoder, view, new RgbaColor(0, 0, 0, 0));
            wgpuRenderPassEncoderSetPipeline(pass, BlitPipeline(format));
            wgpuRenderPassEncoderSetBindGroup(pass, 0, bindGroup, 0, null);
            // Indexed only because that is the one draw call every binding (the browser's included)
            // has; the three indices are the triangle's three vertices.
            if (_blitIndices == IntPtr.Zero)
            {
                ReadOnlySpan<uint> idx = stackalloc uint[] { 0, 1, 2, 0 };
                _blitIndices = _ctx.CreateBufferMapped(System.Runtime.InteropServices.MemoryMarshal.AsBytes(idx), WGPUBufferUsage.Index);
            }
            wgpuRenderPassEncoderSetIndexBuffer(pass, _blitIndices, WGPUIndexFormat.Uint32, 0, 16);
            wgpuRenderPassEncoderDrawIndexed(pass, 3, 1, 0, 0, 0);
            wgpuRenderPassEncoderEnd(pass);
            DeferReleasePass(pass);
        }

        /// <summary>Copy a persistent target to a presentable view and submit.</summary>
        internal void BlitToView(IntPtr view, WGPUTextureFormat format, IntPtr bindGroup)
        {
            try
            {
                IntPtr encoder = wgpuDeviceCreateCommandEncoder(_ctx.Device, IntPtr.Zero);
                RecordBlit(encoder, view, format, bindGroup);
                IntPtr commandBuffer = wgpuCommandEncoderFinish(encoder, IntPtr.Zero);
                IntPtr* cmds = stackalloc IntPtr[1];
                cmds[0] = commandBuffer;
                wgpuQueueSubmit(_ctx.Queue, 1, cmds);
                DeferReleaseEncoder(encoder);
                DeferReleaseCmdBuffer(commandBuffer);
            }
            finally { FlushFrameReleases(); }
        }

        internal (IntPtr Tex, IntPtr View) CreatePersistentTarget(int width, int height, WGPUTextureFormat format)
        {
            var texDesc = new WGPUTextureDescriptor
            {
                usage = WGPUTextureUsage.RenderAttachment | WGPUTextureUsage.TextureBinding | WGPUTextureUsage.CopySrc,
                dimension = WGPUTextureDimension._2D,
                size = new WGPUExtent3D { width = (uint)width, height = (uint)height, depthOrArrayLayers = 1 },
                format = format,
                mipLevelCount = 1,
                sampleCount = 1,
            };
            IntPtr tex = wgpuDeviceCreateTexture(_ctx.Device, &texDesc);
            return (tex, wgpuTextureCreateView(tex, IntPtr.Zero));
        }

#if !WGPU_BROWSER
        /// <summary>Read a CopySrc RGBA8-family texture back, tightly packed. Blocking.</summary>
        internal byte[] ReadTexture(IntPtr tex, int width, int height)
        {
            try
            {
                int bytesPerRow = AlignUp(width * 4, 256);
                ulong size = (ulong)bytesPerRow * (ulong)height;
                IntPtr readback = _ctx.CreateBuffer(size, WGPUBufferUsage.CopyDst | WGPUBufferUsage.MapRead);
                IntPtr encoder = wgpuDeviceCreateCommandEncoder(_ctx.Device, IntPtr.Zero);
                var copySrc = new WGPUTexelCopyTextureInfo { texture = tex, aspect = WGPUTextureAspect.All };
                var copyDst = new WGPUTexelCopyBufferInfo
                {
                    layout = new WGPUTexelCopyBufferLayout { offset = 0, bytesPerRow = (uint)bytesPerRow, rowsPerImage = (uint)height },
                    buffer = readback,
                };
                var extent = new WGPUExtent3D { width = (uint)width, height = (uint)height, depthOrArrayLayers = 1 };
                wgpuCommandEncoderCopyTextureToBuffer(encoder, &copySrc, &copyDst, &extent);
                IntPtr commandBuffer = wgpuCommandEncoderFinish(encoder, IntPtr.Zero);
                IntPtr* cmds = stackalloc IntPtr[1];
                cmds[0] = commandBuffer;
                wgpuQueueSubmit(_ctx.Queue, 1, cmds);
                byte[] padded = _ctx.MapRead(readback, size);
                var pixels = new byte[width * height * 4];
                for (int row = 0; row < height; row++)
                    Buffer.BlockCopy(padded, row * bytesPerRow, pixels, row * width * 4, width * 4);
                DeferReleaseEncoder(encoder);
                DeferReleaseCmdBuffer(commandBuffer);
                DeferReleaseBuffer(readback);
                return pixels;
            }
            finally { FlushFrameReleases(); }
        }
#endif

        // ---- one presentable target --------------------------------------------------------------

        /// <summary>
        /// Partial redraw for one presentable surface: keeps the persistent texture the frames are
        /// rendered into and the tracker that diffs them, renders each frame's damage, and blits the
        /// result to the swap chain view. Used by the WinForms presenter and the WPF composition sink.
        /// </summary>
        internal sealed class PartialTarget : IDisposable
        {
            /// <summary>WGPU_DAMAGE=0: every frame in full, straight into the swap chain, as before.</summary>
            internal static readonly bool Enabled = Environment.GetEnvironmentVariable("WGPU_DAMAGE") != "0";

            /// <summary>WGPU_DAMAGE_VERIFY=1: also render each frame in full and log every pixel that
            /// differs from the partial frame (then show the full one, so one miss does not persist).</summary>
            internal static readonly bool Verify = Environment.GetEnvironmentVariable("WGPU_DAMAGE_VERIFY") == "1";

            private static readonly string? s_logFile = Environment.GetEnvironmentVariable("WGPU_DAMAGE_LOG");
            private static readonly bool s_trace = Environment.GetEnvironmentVariable("WGPU_DAMAGE_TRACE") == "1";
            private static readonly string? s_dumpDir = Environment.GetEnvironmentVariable("WGPU_DAMAGE_VERIFY_DUMP");

            private readonly WgpuSceneRenderer _r;
            private readonly DamageTracker _tracker = new();
            private readonly List<Scissor> _rects = new();
            private IntPtr _tex, _view, _blitBg, _vTex, _vView, _sTex, _sView;
            private WGPUTextureFormat _fmt, _blitFmt;
            private int _w, _h;
            private RgbaColor _bg;
            private bool _transparent, _valid;
            private readonly string _name;
            private long _frames, _verified, _mismatched;

            /// <summary>Damage of the last frame: whether it was full, how many rectangles and pixels.</summary>
            internal bool LastFull { get; private set; }
            internal int LastRects { get; private set; }
            internal long LastPixels { get; private set; }
            /// <summary>The pixels the last frame shifted instead of redrawing (a scroll), or null.</summary>
            internal ScrollShift? LastShift { get; private set; }

            /// <summary>Per-thread totals since the last reset, for the PERF logs: frames, how many of them were
            /// full or shifted, and the pixels drawn against the pixels a full frame would have drawn.</summary>
            [ThreadStatic] internal static long PerfPresents, PerfFullPresents, PerfShiftPresents, PerfPresentPixels, PerfTargetPixels, PerfTrackTicks,
                PerfRenderTicks, PerfSplitCollect, PerfSplitEncode, PerfSplitSubmit, PerfSplitDraws;

            internal static string PerfSummary()
            {
                if (PerfPresents == 0) return "presents=0";
                long n = PerfPresents;
                string Ms(long t) => (t * 1000.0 / System.Diagnostics.Stopwatch.Frequency / n).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
                string s = $"presents={PerfPresents} full={PerfFullPresents} shifted={PerfShiftPresents} " +
                    $"px/present={PerfPresentPixels / PerfPresents} ({PerfPresentPixels * 100 / Math.Max(1, PerfTargetPixels)}% of the target) " +
                    $"shifted px/present={PerfShiftedPixels / PerfPresents} track={Ms(PerfTrackTicks)}ms render={Ms(PerfRenderTicks)}ms (collect={Ms(PerfSplitCollect)} encode={Ms(PerfSplitEncode)} submit={Ms(PerfSplitSubmit)}) drawcalls={PerfSplitDraws / PerfPresents}";
                PerfPresents = PerfFullPresents = PerfShiftPresents = PerfPresentPixels = PerfTargetPixels = PerfTrackTicks = 0;
                PerfRenderTicks = PerfSplitCollect = PerfSplitEncode = PerfSplitSubmit = PerfSplitDraws = 0;
                PerfShiftedPixels = 0;
                return s;
            }

            internal PartialTarget(WgpuSceneRenderer renderer, string name = "")
            {
                _r = renderer;
                _name = name;
            }

            /// <summary>The next frame is drawn in full (contents lost, surface recreated, ...).</summary>
            internal void Invalidate() => _valid = false;

            /// <summary>Render <paramref name="root"/> and copy it into <paramref name="view"/>.</summary>
            internal void Render(SceneVisual root, IntPtr view, WGPUTextureFormat viewFormat, int width, int height,
                RgbaColor background, bool transparentTarget)
            {
                if (!Enabled)
                {
                    _r.RenderSceneToView(root, view, viewFormat, width, height, background, transparentTarget);
                    LastFull = true; LastRects = 1; LastPixels = (long)width * height;
                    return;
                }
                _frames++;
                // RGBA in place of BGRA: the same values, and a format every backend can render to and
                // sample (the GL backend has no BGRA textures of its own).
                WGPUTextureFormat fmt = viewFormat switch
                {
                    WGPUTextureFormat.BGRA8Unorm => WGPUTextureFormat.RGBA8Unorm,
                    WGPUTextureFormat.BGRA8UnormSrgb => WGPUTextureFormat.RGBA8UnormSrgb,
                    _ => viewFormat,
                };
                if (_tex == IntPtr.Zero || width != _w || height != _h || fmt != _fmt)
                {
                    ReleaseTextures();
                    (_tex, _view) = _r.CreatePersistentTarget(width, height, fmt);
                    _w = width; _h = height; _fmt = fmt;
                    _valid = false;
                }
                if (_blitBg == IntPtr.Zero || _blitFmt != viewFormat)
                {
                    if (_blitBg != IntPtr.Zero) wgpuBindGroupRelease(_blitBg);
                    _blitBg = _r.CreateBlitBindGroup(_view, viewFormat);
                    _blitFmt = viewFormat;
                }
                if (!SameColour(background, _bg) || transparentTarget != _transparent)
                {
                    _bg = background; _transparent = transparentTarget;
                    _valid = false;
                }
                bool _valid0 = _valid;
                if (!_valid) _tracker.Reset();

                long tc0 = System.Diagnostics.Stopwatch.GetTimestamp();
                bool full = _tracker.Compute(root, width, height, _rects);
                PerfTrackTicks += System.Diagnostics.Stopwatch.GetTimestamp() - tc0;
                _valid = true;
                List<Scissor>? damage = full ? null : _rects;
                ScrollShift? shift = full ? null : _tracker.Shift;
                LastShift = shift;
                if (shift != null && _sTex == IntPtr.Zero) (_sTex, _sView) = _r.CreatePersistentTarget(width, height, fmt);
                LastFull = full;
                LastRects = full ? 1 : _rects.Count;
                LastPixels = 0;
                if (full) LastPixels = (long)width * height;
                else foreach (Scissor r in _rects) LastPixels += (long)r.W * r.H;
                PerfPresents++; PerfPresentPixels += LastPixels; PerfTargetPixels += (long)width * height;
                if (full) PerfFullPresents++;
                if (shift != null) PerfShiftPresents++;
                if (s_trace)
                {
                    string what = full ? "FULL (" + (_valid0 ? _tracker.FullReason : "target (re)created / background changed") + ")"
                                       : $"{_rects.Count} rects {LastPixels} px";
                    if (shift is { } s0) what += $" shift ({s0.Dx},{s0.Dy}) of [{s0.Src.X},{s0.Src.Y} {s0.Src.W}x{s0.Src.H}]";
                    Log($"[damage]{_name} frame {_frames}: {what} {Describe(damage)}");
                }

#if !WGPU_BROWSER
                if (Verify)
                {
                    _r.RenderFrameDamaged(root, _view, fmt, width, height, background, transparentTarget, damage, _tracker.Stamp,
                        shift: shift, scratchView: _sView);
                    VerifyAgainstFull(root, width, height, background, transparentTarget, damage);
                    _r.BlitToView(view, viewFormat, _blitBg);
                    return;
                }
#endif
                long r0 = System.Diagnostics.Stopwatch.GetTimestamp(), c0 = PerfCollectTicks, e0 = PerfEncodeTicks, su0 = PerfSubmitTicks;
                int d0 = PerfDrawCalls;
                _r.RenderFrameDamaged(root, _view, fmt, width, height, background, transparentTarget, damage, _tracker.Stamp,
                    view, viewFormat, _blitBg, shift, _sView);
                PerfRenderTicks += System.Diagnostics.Stopwatch.GetTimestamp() - r0;
                PerfSplitCollect += PerfCollectTicks - c0; PerfSplitEncode += PerfEncodeTicks - e0; PerfSplitSubmit += PerfSubmitTicks - su0;
                PerfSplitDraws += PerfDrawCalls - d0;
            }

#if !WGPU_BROWSER
            private void VerifyAgainstFull(SceneVisual root, int width, int height, RgbaColor background, bool transparentTarget,
                List<Scissor>? damage)
            {
                if (_vTex == IntPtr.Zero) (_vTex, _vView) = _r.CreatePersistentTarget(width, height, _fmt);
                _r.RenderFrameDamaged(root, _vView, _fmt, width, height, background, transparentTarget, null, 0);
                byte[] got = _r.ReadTexture(_tex, width, height);
                byte[] want = _r.ReadTexture(_vTex, width, height);
                _verified++;
                int diff = 0, minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1, maxDelta = 0;
                var samples = new System.Text.StringBuilder();
                for (int i = 0; i < got.Length; i += 4)
                {
                    if (got[i] == want[i] && got[i + 1] == want[i + 1] && got[i + 2] == want[i + 2] && got[i + 3] == want[i + 3]) continue;
                    int p = i / 4, x = p % width, y = p / width;
                    diff++;
                    if (x < minX) minX = x; if (x > maxX) maxX = x; if (y < minY) minY = y; if (y > maxY) maxY = y;
                    for (int c = 0; c < 4; c++) maxDelta = Math.Max(maxDelta, Math.Abs(got[i + c] - want[i + c]));
                    if (diff <= 12)
                        samples.Append($" ({x},{y}) got {got[i]},{got[i + 1]},{got[i + 2]},{got[i + 3]} want {want[i]},{want[i + 1]},{want[i + 2]},{want[i + 3]}"
                            + (InDamage(damage, x, y) ? " IN-DAMAGE" : " outside"));
                }
                if (diff == 0)
                {
                    if (_verified % 60 == 1)
                        Log($"[damage-verify]{_name} {_verified} frames verified, {_mismatched} mismatched; last: {(damage == null ? "FULL" : $"{damage.Count} rects")}");
                    return;
                }
                _mismatched++;
                if (s_dumpDir != null && _mismatched <= 6)
                {
                    // WGPU_DAMAGE_VERIFY_DUMP=<dir>: the two frames as PNGs, full size, to look at.
                    try
                    {
                        PngWriter.Write(System.IO.Path.Combine(s_dumpDir, $"damage{_name.Trim().Replace(' ', '_')}_{_frames}_got.png"), got, width, height, int.MaxValue);
                        PngWriter.Write(System.IO.Path.Combine(s_dumpDir, $"damage{_name.Trim().Replace(' ', '_')}_{_frames}_want.png"), want, width, height, int.MaxValue);
                    }
                    catch { }
                }
                Log($"[damage-verify]{_name} MISMATCH frame {_frames}: {diff} px differ in ({minX},{minY})-({maxX},{maxY}) max delta {maxDelta}; "
                    + $"damage {Describe(damage)}{(LastShift is { } ls ? $" after a shift ({ls.Dx},{ls.Dy}) of [{ls.Src.X},{ls.Src.Y} {ls.Src.W}x{ls.Src.H}]" : "")};{samples}");
                // Show the full frame and carry on from it, so a miss is reported once rather than
                // persisting into every later frame's comparison.
                (_tex, _vTex) = (_vTex, _tex);
                (_view, _vView) = (_vView, _view);
                if (_blitBg != IntPtr.Zero) wgpuBindGroupRelease(_blitBg);
                _blitBg = _r.CreateBlitBindGroup(_view, _blitFmt);
            }
#endif

            private static bool InDamage(List<Scissor>? damage, int x, int y)
            {
                if (damage == null) return true;
                foreach (Scissor d in damage)
                    if (x >= d.X && x < d.X + d.W && y >= d.Y && y < d.Y + d.H) return true;
                return false;
            }

            private static string Describe(List<Scissor>? damage)
            {
                if (damage == null) return "full";
                var sb = new System.Text.StringBuilder();
                foreach (Scissor d in damage) sb.Append($"[{d.X},{d.Y} {d.W}x{d.H}]");
                return sb.ToString();
            }

            private static void Log(string line)
            {
                Console.Error.WriteLine(line);
                if (s_logFile != null)
                {
                    try { System.IO.File.AppendAllText(s_logFile, line + Environment.NewLine); } catch { }
                }
            }

            private static bool SameColour(RgbaColor a, RgbaColor b) => a.R == b.R && a.G == b.G && a.B == b.B && a.A == b.A;

            private void ReleaseTextures()
            {
                if (_blitBg != IntPtr.Zero) { wgpuBindGroupRelease(_blitBg); _blitBg = IntPtr.Zero; }
                if (_view != IntPtr.Zero) { wgpuTextureViewRelease(_view); _view = IntPtr.Zero; }
                if (_tex != IntPtr.Zero) { wgpuTextureRelease(_tex); _tex = IntPtr.Zero; }
                if (_vView != IntPtr.Zero) { wgpuTextureViewRelease(_vView); _vView = IntPtr.Zero; }
                if (_vTex != IntPtr.Zero) { wgpuTextureRelease(_vTex); _vTex = IntPtr.Zero; }
                if (_sView != IntPtr.Zero) { wgpuTextureViewRelease(_sView); _sView = IntPtr.Zero; }
                if (_sTex != IntPtr.Zero) { wgpuTextureRelease(_sTex); _sTex = IntPtr.Zero; }
            }

            public void Dispose() => ReleaseTextures();
        }
    }
}
