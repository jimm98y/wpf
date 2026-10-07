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

        private static Scissor RectBox(Rect r, Matrix3x2 world)
        {
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            AccRect(r.X, r.Y, r.Width, r.Height, world, 0f, ref minX, ref minY, ref maxX, ref maxY);
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
        internal sealed class DamageTracker
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
            }

            private const byte KindText = 1, KindUnknown = 2, KindLive = 4, KindAlways = 8;

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
                if (!_sources.TryGetValue(src, out DamageTracker? t)) _sources[src] = t = new DamageTracker();
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
                Rec? old = (width == _w && height == _h) ? _root : null;
                _w = width; _h = height;
                _root = Visit(old, root, Matrix3x2.Identity, new Scissor(0, 0, width, height), fresh: false, inSnapshot: false);
                if (_sources.Count > _sourcesSeen.Count)
                {
                    var gone = new List<SceneVisual>();
                    foreach (SceneVisual s in _sources.Keys) if (!_sourcesSeen.Contains(s)) gone.Add(s);
                    foreach (SceneVisual s in gone) _sources.Remove(s);
                }
                if (old == null) { FullReason = "first frame"; return true; }
                if (_forceFull) { FullReason = "volatile content (3D / live brush / unknown primitive)"; return true; }
                // So many separate changes that sorting them out would cost more than drawing them.
                if (_raw.Count > MaxRawRects || !SpreadToText()) { FullReason = $"{_raw.Count} changes"; return true; }
                Normalize(_raw, rects, width, height);
                long area = 0;
                foreach (Scissor r in rects) area += (long)r.W * r.H;
                // Most of the target changed: one full frame is no more work and needs no scissors.
                FullReason = $"{area * 100 / Math.Max(1L, (long)width * height)}% of the target changed";
                return area * 10 > (long)width * height * 7;
            }

            /// <summary>A ClearType run is blended against the paper under its WHOLE box (PaperUnder):
            /// a change under any part of it can change every pixel of it. So damage that touches a
            /// run's box takes in all of the box -- repeated, since that box may touch another run.</summary>
            private bool SpreadToText()
            {
                if (_raw.Count == 0 || _texts.Count == 0) return true;
                bool grew = true;
                while (grew)
                {
                    grew = false;
                    if (_raw.Count > MaxRawRects || (long)_raw.Count * _texts.Count > 4_000_000) return false;
                    for (int t = _texts.Count - 1; t >= 0; t--)
                    {
                        Scissor tb = _texts[t];
                        foreach (Scissor d in _raw)
                        {
                            if (tb.X < d.X + d.W && d.X < tb.X + tb.W && tb.Y < d.Y + d.H && d.Y < tb.Y + tb.H)
                            {
                                _raw.Add(tb);
                                _texts.RemoveAt(t);
                                grew = true;
                                break;
                            }
                        }
                    }
                }
                return true;
            }

            private const int MaxRawRects = 512;

            private Rec Visit(Rec? old, SceneVisual v, Matrix3x2 parentWorld, Scissor parentClip, bool fresh, bool inSnapshot)
            {
                Matrix3x2 world = v.LocalToParent * parentWorld;
                Scissor clip = v.Clip is Rect cr ? Intersect(parentClip, RectBox(cr, world)) : parentClip;
                bool kidsInSnapshot = inSnapshot || v.Snapshot != null;

                bool sameState = old != null && !fresh && SameState(old, v);
                bool sameContent = sameState && SameContent(old!, v);
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
                if (old.Kids.Length < nk) Array.Resize(ref old.Kids, nk);
                for (int i = 0; i < nk; i++)
                    old.Kids[i] = Visit(i < old.KidCount ? old.Kids[i] : null, v.Children[i], world, clip, fresh: false, kidsInSnapshot);
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
                Scissor b;
                if (v.Snapshot is { } snap)
                    b = SnapshotBox(snap, world);
                else
                {
                    b = rec.Own;
                    for (int i = 0; i < rec.KidCount; i++)
                        if (rec.Kids[i] is { } k) b = Union(b, k.Bounds);
                    b = EffectSpread(v, b);
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
                    rec.Boxes[i] = minX <= maxX ? Intersect(clip, ToScissor(minX, minY, maxX, maxY, ContentMargin)) : new Scissor(0, 0, 0, 0);
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

            private static bool SameState(Rec r, SceneVisual v)
            {
                if (r.Local != v.LocalToParent || r.Opacity != v.Opacity) return false;
                if (r.HasClip != v.Clip.HasValue || (v.Clip is Rect c && !SameRect(c, r.Clip))) return false;
                if (!ReferenceEquals(r.ClipGeometry, v.ClipGeometry) || !ReferenceEquals(r.Effect, v.Effect)
                    || !ReferenceEquals(r.Mask, v.OpacityMask) || !ReferenceEquals(r.Snapshot, v.Snapshot)) return false;
                if (v.Snapshot is { } s && (!SameRect(s.Source, r.SnapSource) || !SameRect(s.Dest, r.SnapDest)
                    || s.GdiStretch != r.SnapStretch || s.WindowBlend != r.SnapBlend)) return false;
                if (r.Aliased != v.AliasedEdges || r.Nearest != v.NearestBitmapScaling) return false;
                return ReferenceEquals(r.GuidesX, v.GuidelinesX) && ReferenceEquals(r.GuidesY, v.GuidelinesY);
            }

            private static bool SameContent(Rec r, SceneVisual v)
            {
                List<DrawingPrimitive> c = v.Content;
                if (c.Count != r.ContentCount) return false;
                for (int i = 0; i < c.Count; i++)
                    if (!ReferenceEquals(c[i], r.Content[i])) return false;
                return true;
            }

            /// <summary>Clamp to the target, drop the empty, and merge until the rectangles are
            /// DISJOINT (the main pass draws each item once per rectangle, so an overlap would blend
            /// twice) and few. Near neighbours are merged too: a draw per rectangle costs more than
            /// the few pixels between them.</summary>
            internal static void Normalize(List<Scissor> raw, List<Scissor> outRects, int width, int height)
            {
                const int MaxRects = 8, Near = 8;
                var target = new Scissor(0, 0, width, height);
                outRects.Clear();
                foreach (Scissor r in raw)
                {
                    Scissor c = Intersect(r, target);
                    if (!c.IsEmpty) outRects.Add(c);
                }
                bool changed = true;
                while (changed)
                {
                    changed = false;
                    // One sweep merges every near pair it meets (a grown rectangle keeps absorbing);
                    // sweeps repeat until one merges nothing.
                    for (int i = 0; i < outRects.Count; i++)
                        for (int j = outRects.Count - 1; j > i; j--)
                        {
                            Scissor a = outRects[i], b = outRects[j];
                            if (!Intersect(Inflate(a, Near, Near, Near, Near), b).IsEmpty)
                            {
                                outRects[i] = Union(a, b);
                                outRects.RemoveAt(j);
                                changed = true;
                            }
                        }
                    if (!changed && outRects.Count > 4 * MaxRects)
                    {
                        // Scattered all over: their bounding box.
                        Scissor all = outRects[0];
                        foreach (Scissor r in outRects) all = Union(all, r);
                        outRects.Clear();
                        outRects.Add(all);
                    }
                    if (!changed && outRects.Count > MaxRects)
                    {
                        // Merge the pair whose bounding box adds the least area.
                        long best = long.MaxValue; int bi = 0, bj = 1;
                        for (int i = 0; i < outRects.Count; i++)
                            for (int j = i + 1; j < outRects.Count; j++)
                            {
                                Scissor u = Union(outRects[i], outRects[j]);
                                long grow = (long)u.W * u.H - (long)outRects[i].W * outRects[i].H - (long)outRects[j].W * outRects[j].H;
                                if (grow < best) { best = grow; bi = i; bj = j; }
                            }
                        outRects[bi] = Union(outRects[bi], outRects[bj]);
                        outRects.RemoveAt(bj);
                        changed = true;
                    }
                }
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
            IntPtr blitView = default, WGPUTextureFormat blitFormat = default, IntPtr blitBindGroup = default)
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
                    foreach (LayerPass lp in plan) ExecutePass(encoder, lp, atlasView);
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

            private readonly WgpuSceneRenderer _r;
            private readonly DamageTracker _tracker = new();
            private readonly List<Scissor> _rects = new();
            private IntPtr _tex, _view, _blitBg, _vTex, _vView;
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

                bool full = _tracker.Compute(root, width, height, _rects);
                _valid = true;
                List<Scissor>? damage = full ? null : _rects;
                LastFull = full;
                LastRects = full ? 1 : _rects.Count;
                LastPixels = 0;
                if (full) LastPixels = (long)width * height;
                else foreach (Scissor r in _rects) LastPixels += (long)r.W * r.H;
                if (s_trace)
                    Log($"[damage]{_name} frame {_frames}: {(full ? "FULL (" + (_valid0 ? _tracker.FullReason : "target (re)created / background changed") + ")" : $"{_rects.Count} rects {LastPixels} px")} {Describe(damage)}");

                if (Verify)
                {
                    _r.RenderFrameDamaged(root, _view, fmt, width, height, background, transparentTarget, damage, _tracker.Stamp);
                    VerifyAgainstFull(root, width, height, background, transparentTarget, damage);
                    _r.BlitToView(view, viewFormat, _blitBg);
                    return;
                }
                _r.RenderFrameDamaged(root, _view, fmt, width, height, background, transparentTarget, damage, _tracker.Stamp,
                    view, viewFormat, _blitBg);
            }

#if WGPU_BROWSER
            private void VerifyAgainstFull(SceneVisual root, int width, int height, RgbaColor background, bool transparentTarget,
                List<Scissor>? damage)
            {
                // The browser cannot map a buffer synchronously. Both textures are copied out NOW (the
                // copies are queued ahead of the next frame's render, so they see this frame), and the
                // comparison runs in JS when the maps resolve: two whole frames marshalled into managed
                // arrays every frame would be ~10 MB of garbage per frame. The partial frame is what
                // gets shown; after a miss the next frame is drawn in full so the miss is not carried
                // forward.
                if (_vTex == IntPtr.Zero) (_vTex, _vView) = _r.CreatePersistentTarget(width, height, _fmt);
                _r.RenderFrameDamaged(root, _vView, _fmt, width, height, background, transparentTarget, null, 0);
                long frame = _frames;
                List<Scissor>? damageCopy = damage == null ? null : new List<Scissor>(damage);
                _r.CompareTexturesAsync(_tex, _vTex, width, height).ContinueWith(t =>
                {
                    if (t.IsFaulted) { Log($"[damage-verify]{_name} readback failed: {t.Exception?.GetBaseException().Message}"); return; }
                    if (!ReportSummary(t.Result, damageCopy, frame)) _valid = false;
                }, System.Threading.Tasks.TaskScheduler.Default);
            }

            /// <summary>Reports wgpu-interop.js compareTextures' summary:
            /// "diff minX minY maxX maxY maxDelta" then "|x,y,got RGBA,want RGBA" per sample.</summary>
            private bool ReportSummary(string summary, List<Scissor>? damage, long frame)
            {
                string[] parts = summary.Split('|');
                string[] head = parts[0].Split(' ');
                int diff = int.Parse(head[0]);
                var samples = new System.Text.StringBuilder();
                for (int i = 1; i < parts.Length; i++)
                {
                    string[] v = parts[i].Split(',');
                    int x = int.Parse(v[0]), y = int.Parse(v[1]);
                    samples.Append($" ({x},{y}) got {v[2]},{v[3]},{v[4]},{v[5]} want {v[6]},{v[7]},{v[8]},{v[9]}"
                        + (InDamage(damage, x, y) ? " IN-DAMAGE" : " outside"));
                }
                return Report(diff, int.Parse(head[1]), int.Parse(head[2]), int.Parse(head[3]), int.Parse(head[4]),
                    int.Parse(head[5]), samples, damage, frame);
            }
#else
            private void VerifyAgainstFull(SceneVisual root, int width, int height, RgbaColor background, bool transparentTarget,
                List<Scissor>? damage)
            {
                if (_vTex == IntPtr.Zero) (_vTex, _vView) = _r.CreatePersistentTarget(width, height, _fmt);
                _r.RenderFrameDamaged(root, _vView, _fmt, width, height, background, transparentTarget, null, 0);
                byte[] got = _r.ReadTexture(_tex, width, height);
                byte[] want = _r.ReadTexture(_vTex, width, height);
                if (Compare(got, want, width, damage, _frames)) return;
                // Show the full frame and carry on from it, so a miss is reported once rather than
                // persisting into every later frame's comparison.
                (_tex, _vTex) = (_vTex, _tex);
                (_view, _vView) = (_vView, _view);
                if (_blitBg != IntPtr.Zero) wgpuBindGroupRelease(_blitBg);
                _blitBg = _r.CreateBlitBindGroup(_view, _blitFmt);
            }
#endif

            /// <summary>Compares a partial frame with the full one and logs; true when identical.</summary>
            private bool Compare(byte[] got, byte[] want, int width, List<Scissor>? damage, long frame)
            {
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
                return Report(diff, minX, minY, maxX, maxY, maxDelta, samples, damage, frame);
            }

            private bool Report(int diff, int minX, int minY, int maxX, int maxY, int maxDelta, System.Text.StringBuilder samples,
                List<Scissor>? damage, long frame)
            {
                _verified++;
                if (diff == 0)
                {
                    if (_verified % 60 == 1)
                        Log($"[damage-verify]{_name} {_verified} frames verified, {_mismatched} mismatched; last: {(damage == null ? "FULL" : $"{damage.Count} rects")}");
                    return true;
                }
                _mismatched++;
                Log($"[damage-verify]{_name} MISMATCH frame {frame}: {diff} px differ in ({minX},{minY})-({maxX},{maxY}) max delta {maxDelta}; "
                    + $"damage {Describe(damage)};{samples}");
                return false;
            }

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
            }

            public void Dispose() => ReleaseTextures();
        }
    }
}
