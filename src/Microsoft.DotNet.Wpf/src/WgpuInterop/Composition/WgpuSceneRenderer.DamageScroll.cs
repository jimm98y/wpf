// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// CONTENT SCROLL. A WinForms control does not scroll by moving a visual: it repaints, and its new
// scene is a fresh recording in which the rows that stayed on screen are drawn again one row-height
// further up. Nothing in the tree moved, every primitive is a new object -- so the structural scroll
// (TryScroll) cannot see it, and the reference diff calls all of it changed.
//
// This compares the two recordings by VALUE. Both are flattened to draw records (a primitive with
// its world translation, its device clip and its device box, in draw order). A record of the new
// frame that equals a record of the old one moved by one whole-pixel offset d -- the same geometry,
// brush, pen and text, every coordinate d further on -- is MOVED; one that equals an old record
// where it was is STILL; anything else is CHANGED. d is the offset most records agree on. Matches
// are taken in draw order only (a later record never matches an earlier one than the last match),
// so the moved records are drawn in the same order in both frames.
//
// The shift then runs as for the structural scroll (BuildShift), over the region R the moved
// records were clipped to in both frames, with the records' own damage:
//
//   * moved, inside R: nothing -- the shift puts its old pixels where its new ones go;
//   * moved, reaching outside R: where it is and where it was (outside R nothing is shifted);
//   * still: where it is and where the shift dragged it, unless it is a solid rectangle covering R;
//   * changed or new: where it is; gone: where it was and where the shift dragged it.
//
// Exact by the same argument as the structural scroll: a pixel of the shifted area outside all of
// that sees, in this frame, the moved records that it saw one step back in the last, in the same
// order, and nothing else but fills that paint every pixel of R alike.
//

using System;
using System.Collections.Generic;
using System.Numerics;
using Microsoft.Wpf.Interop.WebGpu.Composition.Text;

namespace Microsoft.Wpf.Interop.WebGpu.Composition
{
    internal sealed unsafe partial class WgpuSceneRenderer
    {
        internal sealed partial class DamageTracker
        {
            private struct DrawRecord
            {
                public DrawingPrimitive P;
                public Vector2 T;          // world translation (the world's linear part is the identity)
                public Scissor Clip;       // device scissor it is drawn under
                public Scissor Box;        // device box (clipped), as the tracker measured it
                public Vector2 Anchor;     // a point of the primitive, device
                public long Key;           // a hash of the primitive that ignores where it is
                public int Match;          // index of the matched old record, or -1
                public bool Moved;
            }

            private readonly List<DrawRecord> _oldRecords = new(), _newRecords = new();
            private int _whyCount;

            /// <summary>Whether <paramref name="v"/> looks like a fresh recording of what
            /// <paramref name="old"/> recorded: its first primitive is a new object.</summary>
            private static bool LooksRepainted(Rec old, SceneVisual v)
                => old.ContentCount > 0 && v.Content.Count > 0 && !ReferenceEquals(old.Content[0], v.Content[0]);

            /// <summary>The visual's record if its subtree is the last frame's recording scrolled by
            /// whole device pixels (see the file comment), else null, with nothing recorded.</summary>
            private Rec? TryContentScroll(Rec old, SceneVisual v, Matrix3x2 parentWorld, Scissor parentClip, bool inSnapshot, bool allowShift)
            {
                if (inSnapshot) return null;
                Matrix3x2 world = v.LocalToParent * parentWorld;
                if (!IsTranslation(world)) return null;
                _oldRecords.Clear();
                if (!Flatten(old, parentWorld, parentClip, _oldRecords) || _oldRecords.Count < 8) return Why($"old not flat ({_oldRecords.Count})");

                Rec rec = Visit(null, v, parentWorld, parentClip, fresh: true, inSnapshot: false, keepNudge: true);
                _newRecords.Clear();
                if (!Flatten(rec, parentWorld, parentClip, _newRecords) || _newRecords.Count < 8) return Why($"new not flat ({_newRecords.Count})");

                Scissor r0 = Intersect(rec.HasClip ? Intersect(parentClip, RectBox(rec.Clip, world)) : parentClip,
                                      Intersect(old.HasClip ? Intersect(parentClip, RectBox(old.Clip, old.Local * parentWorld)) : parentClip, parentClip));
                {
                    // A repaint that drew (nearly) what was already there -- a control that
                    // invalidates itself on every wheel notch, scrolled or not: only what differs is
                    // damage. Before this, every record of it was a new object and so all of it was.
                    Scissor rs = r0;
                    Classify(0, 0, ref rs);
                    int same = 0;
                    foreach (DrawRecord n in _newRecords) if (n.Match >= 0) same++;
                    if (same * 10 >= _newRecords.Count * 9)
                    {
                        var seen = new bool[_oldRecords.Count];
                        foreach (DrawRecord n in _newRecords)
                        {
                            if (n.Match >= 0) seen[n.Match] = true;
                            else _raw.Add(n.Box);
                        }
                        for (int i = 0; i < _oldRecords.Count; i++) if (!seen[i]) _raw.Add(_oldRecords[i].Box);
                        if (s_scrollTrace)
                        {
                            var sb = new System.Text.StringBuilder($"[scroll] repaint: {same} of {_newRecords.Count} records the same; differ:");
                            int shown = 0;
                            foreach (DrawRecord n in _newRecords)
                                if (n.Match < 0 && shown++ < 8)
                                    sb.Append($" {n.P.GetType().Name}{(n.P is GlyphRunDraw g ? ":" + g.Text : "")}@({n.Anchor.X},{n.Anchor.Y})key={(n.Key == 0 ? "none" : "ok")}");
                            Console.Error.WriteLine(sb.ToString());
                        }
                        return rec;
                    }
                }
                if (!allowShift) return null;
                List<(int, int)> offsets = Vote();
                if (offsets.Count == 0) return Why($"no offset ({_newRecords.Count} records)");
                // Several offsets can look likely (a row's check box matches every other row's): the one
                // under which the most records really are the old ones moved.
                int dx = 0, dy = 0, moved = -1;
                Scissor r = r0;
                foreach ((int cx, int cy) in offsets)
                {
                    Scissor rc = r0;
                    int m = Classify(cx, cy, ref rc);
                    if (m > moved) { moved = m; dx = cx; dy = cy; }
                }
                r = r0;
                Classify(dx, dy, ref r);            // the winner's matches, which the others overwrote
                if (moved < 4 || moved * 4 < _newRecords.Count || r.IsEmpty) return Why($"d=({dx},{dy}) moved {moved} of {_newRecords.Count}, R=[{r.X},{r.Y} {r.W}x{r.H}]");
                if (Math.Abs(dx) >= r.W || Math.Abs(dy) >= r.H) return null;

                var inner = new List<Scissor>();
                var matchedOld = new bool[_oldRecords.Count];
                foreach (DrawRecord n in _newRecords)
                {
                    if (n.Match >= 0) matchedOld[n.Match] = true;
                    if (n.Match < 0)
                        inner.Add(n.Box);                                       // changed or new: where it is
                    else if (n.Moved)
                    {
                        if (!Inside(n.Box, r) || !Inside(_oldRecords[n.Match].Box, r))
                        {
                            inner.Add(n.Box);                                   // past R: not shifted there
                            inner.Add(_oldRecords[n.Match].Box);
                        }
                    }
                    else if (!CoversUniformly(n.P, Matrix3x2.CreateTranslation(n.T), n.Clip, r))
                    {
                        inner.Add(Intersect(n.Box, r));                         // still: where it is ...
                        Scissor dragged = Translate(n.Box, dx, dy);             // ... and where the shift put it
                        inner.Add(Intersect(dragged, r));
                        if (!Inside(n.Box, r)) inner.Add(n.Box);
                    }
                }
                for (int i = 0; i < _oldRecords.Count; i++)
                {
                    if (matchedOld[i]) continue;
                    Scissor b = _oldRecords[i].Box;                             // gone: where it was ...
                    inner.Add(b);
                    inner.Add(Intersect(Translate(b, dx, dy), r));              // ... and where the shift put it
                }

                _scrollRec = rec;
                _scrollOldBounds = old.Bounds;
                _scrollClip = r;
                _scrollDx = dx; _scrollDy = dy;
                _scrollInner.AddRange(inner);
                if (s_scrollTrace)
                {
                    var un = new Dictionary<string, int>();
                    foreach (DrawRecord n in _newRecords) if (n.Match < 0) { string k = n.P.GetType().Name + (n.Key == 0 ? "(nokey)" : "") + (n.P is GlyphRunDraw g ? ":" + g.Text : ""); un.TryGetValue(k, out int c); un[k] = c + 1; }
                    var sb = new System.Text.StringBuilder("[scroll]   unmatched:");
                    foreach (var kv in un) sb.Append($" {kv.Key}={kv.Value}");
                    Console.Error.WriteLine(sb.ToString());
                }
                if (s_scrollTrace)
                    Console.Error.WriteLine($"[scroll] content d=({dx},{dy}) R=[{r.X},{r.Y} {r.W}x{r.H}] records {_newRecords.Count} new / {_oldRecords.Count} old, {moved} moved, {inner.Count} damage boxes");
                return rec;
            }

            private static Rec? Why(string why)
            {
                if (s_scrollTrace) Console.Error.WriteLine("[scroll] content: no -- " + why);
                return null;
            }

            private static bool IsTranslation(Matrix3x2 m) => m.M11 == 1f && m.M12 == 0f && m.M21 == 0f && m.M22 == 1f;

            private static bool Inside(Scissor a, Scissor b)
                => a.IsEmpty || (a.X >= b.X && a.Y >= b.Y && a.X + a.W <= b.X + b.W && a.Y + a.H <= b.Y + b.H);

            /// <summary>Draw records of a measured subtree, in draw order; false when it holds
            /// something drawn as a picture (effect, mask, clip geometry, snapshot, group opacity) or
            /// under a transform that is not a translation.</summary>
            private static bool Flatten(Rec r, Matrix3x2 parentWorld, Scissor parentClip, List<DrawRecord> into)
            {
                Matrix3x2 world = r.Local * parentWorld;
                if (!IsTranslation(world)) return false;
                if (r.Effect != null || r.Mask != null || r.ClipGeometry != null || r.Snapshot != null || r.Opacity < 0.999)
                    return false;
                Scissor clip = r.HasClip ? Intersect(parentClip, RectBox(r.Clip, world)) : parentClip;
                var t = new Vector2(world.M31, world.M32);
                for (int i = 0; i < r.ContentCount; i++)
                {
                    DrawingPrimitive p = r.Content[i];
                    if ((r.Kinds[i] & (KindUnknown | KindLive | KindAlways)) != 0) return false;
                    var rec = new DrawRecord { P = p, T = t, Clip = clip, Box = r.Boxes[i], Match = -1 };
                    if (Signature(p, out Vector2 anchor, out long key)) { rec.Anchor = anchor + t; rec.Key = key; }
                    else rec.Key = 0;           // matches nothing
                    into.Add(rec);
                    if (into.Count > 20000) return false;
                }
                for (int i = 0; i < r.KidCount; i++)
                    if (r.Kids[i] is { } k && !Flatten(k, world, clip, into)) return false;
                return true;
            }

            /// <summary>The offset most new records agree they moved by, if any whole-pixel one.</summary>
            private List<(int, int)> Vote()
            {
                var result = new List<(int, int)>();
                var byKey = new Dictionary<long, List<int>>();
                for (int i = 0; i < _oldRecords.Count; i++)
                {
                    long k = _oldRecords[i].Key;
                    if (k == 0) continue;
                    if (!byKey.TryGetValue(k, out List<int>? l)) byKey[k] = l = new List<int>(1);
                    if (l.Count < 8) l.Add(i);
                }
                var votes = new Dictionary<(int, int), int>();
                foreach (DrawRecord n in _newRecords)
                {
                    if (n.Key == 0 || !byKey.TryGetValue(n.Key, out List<int>? l)) continue;
                    foreach (int i in l)
                    {
                        Vector2 d = n.Anchor - _oldRecords[i].Anchor;
                        int ix = (int)MathF.Round(d.X), iy = (int)MathF.Round(d.Y);
                        if ((ix == 0 && iy == 0) || MathF.Abs(d.X - ix) > 1e-3f || MathF.Abs(d.Y - iy) > 1e-3f) continue;
                        // Even only, as TryScroll: positions round half to even.
                        if (((ix | iy) & 1) != 0) continue;
                        // A record drawn once (a row's text) says where it went; one drawn in every
                        // row (a check box, a grid line) votes for every multiple of the row height.
                        votes.TryGetValue((ix, iy), out int c);
                        votes[(ix, iy)] = c + (l.Count == 1 ? 8 : 1);
                    }
                }
                var ranked = new List<KeyValuePair<(int, int), int>>(votes);
                ranked.Sort((a, b) => b.Value.CompareTo(a.Value));
                for (int i = 0; i < ranked.Count && result.Count < 4; i++)
                    if (ranked[i].Value >= 16) result.Add(ranked[i].Key);
                if (s_scrollTrace)
                {
                    var top = new List<KeyValuePair<(int, int), int>>(votes);
                    top.Sort((a, b) => b.Value.CompareTo(a.Value));
                    var sb = new System.Text.StringBuilder("[scroll]   votes:");
                    for (int i = 0; i < Math.Min(6, top.Count); i++) sb.Append($" ({top[i].Key.Item1},{top[i].Key.Item2})x{top[i].Value}");
                    var kinds = new Dictionary<string, int>();
                    foreach (DrawRecord n in _newRecords) { string k = n.P.GetType().Name + (n.Key == 0 ? "?" : ""); kinds.TryGetValue(k, out int c); kinds[k] = c + 1; }
                    foreach (var kv in kinds) sb.Append($" {kv.Key}={kv.Value}");
                    Console.Error.WriteLine(sb.ToString());
                }
                return result;
            }

            /// <summary>Match every new record to an old one, moved by (dx, dy) or still, in draw
            /// order; narrows <paramref name="r"/> to the clips the moved records were drawn under in
            /// both frames. Returns how many moved.</summary>
            private int Classify(int dx, int dy, ref Scissor r)
            {
                _whyCount = 0;
                for (int j = 0; j < _newRecords.Count; j++) { DrawRecord q = _newRecords[j]; q.Match = -1; q.Moved = false; _newRecords[j] = q; }
                var byKey = new Dictionary<long, List<int>>();
                for (int i = 0; i < _oldRecords.Count; i++)
                {
                    long k = _oldRecords[i].Key;
                    if (k == 0) continue;
                    if (!byKey.TryGetValue(k, out List<int>? l)) byKey[k] = l = new List<int>(1);
                    l.Add(i);
                }
                var d = new Vector2(dx, dy);
                int last = -1, moved = 0;
                for (int j = 0; j < _newRecords.Count; j++)
                {
                    DrawRecord n = _newRecords[j];
                    if (n.Key == 0 || !byKey.TryGetValue(n.Key, out List<int>? l)) continue;
                    foreach (int i in l)
                    {
                        if (i <= last) continue;
                        DrawRecord o = _oldRecords[i];
                        bool isMoved;
                        if (Near(n.Anchor, o.Anchor + d)) isMoved = true;
                        else if (Near(n.Anchor, o.Anchor)) isMoved = false;
                        else continue;
                        Vector2 shift = isMoved ? d : Vector2.Zero;
                        // Local coordinates: new = old + (old translation + shift - new translation).
                        Vector2 delta = o.T + shift - n.T;
                        if (!SameMoved(n.P, o.P, delta)) { if (s_scrollTrace && isMoved && _whyCount++ < 12) Console.Error.WriteLine($"[scroll]   differ: {n.P.GetType().Name} delta=({delta.X},{delta.Y}) anchors n=({n.Anchor.X},{n.Anchor.Y}) o=({o.Anchor.X},{o.Anchor.Y})"); continue; }
                        Scissor movedClip = Translate(o.Clip, (int)shift.X, (int)shift.Y);
                        bool clipMoved = SameScissor(n.Clip, movedClip);
                        bool clipStill = SameScissor(n.Clip, o.Clip);
                        if (!clipMoved && !clipStill) { if (s_scrollTrace && _whyCount++ < 12) Console.Error.WriteLine($"[scroll]   clip: n=[{n.Clip.X},{n.Clip.Y} {n.Clip.W}x{n.Clip.H}] o=[{o.Clip.X},{o.Clip.Y} {o.Clip.W}x{o.Clip.H}]"); continue; }
                        n.Match = i; n.Moved = isMoved;
                        _newRecords[j] = n;
                        last = i;
                        if (isMoved)
                        {
                            moved++;
                            // A moved record clipped by a clip that did NOT move: only inside that
                            // clip are its pixels where the shift puts them.
                            if (!clipMoved) r = Intersect(r, n.Clip);
                        }
                        break;
                    }
                }
                return moved;
            }

            private static bool Near(Vector2 a, Vector2 b) => MathF.Abs(a.X - b.X) <= 1e-3f && MathF.Abs(a.Y - b.Y) <= 1e-3f;
            private static bool Near(float a, float b) => MathF.Abs(a - b) <= 1e-3f;
            private static bool SameScissor(Scissor a, Scissor b) => a.X == b.X && a.Y == b.Y && a.W == b.W && a.H == b.H;

            // ---- signatures: what a primitive is, wherever it is ----------------------------------

            private static long Mix(long h, long x) => (h ^ x) * 1099511628211L;
            private static long MixF(long h, float f) => Mix(h, BitConverter.SingleToInt32Bits(f));

            /// <summary>A point of the primitive (local) and a hash of everything about it but where
            /// it is. False for a kind this does not compare.</summary>
            private static bool Signature(DrawingPrimitive p, out Vector2 anchor, out long key)
            {
                long h = unchecked((long)1469598103934665603UL);
                h = Mix(h, p.SourceCopy ? 7 : 3);
                anchor = default;
                switch (p)
                {
                    case GeometryFill f:
                        if (!GeoSig(f.Geometry, ref h, out anchor)) { key = 0; return false; }
                        h = Mix(h, 1); h = Mix(h, f.IsGlyph ? 1 : 0); h = Mix(h, f.PixelAligned ? 1 : 0);
                        if (!BrushSig(f.Brush, ref h)) { key = 0; return false; }
                        break;
                    case GeometryStroke s:
                        if (!GeoSig(s.Geometry, ref h, out anchor)) { key = 0; return false; }
                        h = Mix(h, 2); h = MixF(h, (float)s.Style.Thickness);
                        if (!BrushSig(s.Brush, ref h)) { key = 0; return false; }
                        break;
                    case GeometryDrawing g:
                        if (!GeoSig(g.Geometry, ref h, out anchor)) { key = 0; return false; }
                        h = Mix(h, 3);
                        if (g.Fill != null && !BrushSig(g.Fill, ref h)) { key = 0; return false; }
                        if (g.Stroke != null && !BrushSig(g.Stroke, ref h)) { key = 0; return false; }
                        break;
                    case GlyphRunDraw t:
                        anchor = t.Origin;
                        h = Mix(h, 4); h = Mix(h, t.Text.GetHashCode()); h = MixF(h, t.EmSize);
                        h = MixF(h, t.Color.R); h = MixF(h, t.Color.G); h = MixF(h, t.Color.B); h = MixF(h, t.Color.A);
                        break;
                    case GdiPlusTextDraw gp when gp.Run is { } run:
                        anchor = new Vector2(run.OriginX, run.OriginY);
                        h = Mix(h, 5); h = Mix(h, gp.Argb); h = Mix(h, run.Glyphs.Length); h = MixF(h, run.Em);
                        foreach (ushort gl in run.Glyphs) h = Mix(h, gl);
                        if (run.Pre is { } pre) { h = Mix(h, (int)run.PreKey); h = Mix(h, (int)(run.PreKey >> 32)); h = Mix(h, pre.Width); h = Mix(h, pre.Height); }
                        break;
                    default:
                        key = 0;
                        return false;
                }
                key = h == 0 ? 1 : h;
                return true;
            }

            private static bool GeoSig(Geometry g, ref long h, out Vector2 anchor)
            {
                switch (g)
                {
                    case RectangleGeometry r:
                        anchor = new Vector2((float)r.Rect.X, (float)r.Rect.Y);
                        h = Mix(h, 21); h = MixF(h, (float)r.Rect.Width); h = MixF(h, (float)r.Rect.Height);
                        return true;
                    case RoundedRectangleGeometry rr:
                        anchor = new Vector2((float)rr.Rect.X, (float)rr.Rect.Y);
                        h = Mix(h, 22); h = MixF(h, (float)rr.Rect.Width); h = MixF(h, (float)rr.Rect.Height);
                        h = MixF(h, rr.RadiusX); h = MixF(h, rr.RadiusY);
                        return true;
                    case EllipseGeometry e:
                        anchor = e.Center;
                        h = Mix(h, 23); h = MixF(h, e.RadiusX); h = MixF(h, e.RadiusY);
                        return true;
                    case PolygonGeometry pg when pg.Points.Length > 0:
                        anchor = pg.Points[0];
                        h = Mix(h, 24); h = Mix(h, pg.Points.Length);
                        foreach (Vector2 q in pg.Points) { h = MixF(h, q.X - anchor.X); h = MixF(h, q.Y - anchor.Y); }
                        return true;
                    case PathGeometry path when path.Figures.Count > 0:
                        anchor = path.Figures[0].Start;
                        h = Mix(h, 25); h = Mix(h, (long)path.FillRule); h = Mix(h, path.Figures.Count);
                        foreach (PathFigure f in path.Figures)
                        {
                            h = Mix(h, f.Segments.Count); h = Mix(h, f.Closed ? 1 : 0);
                            h = MixF(h, f.Start.X - anchor.X); h = MixF(h, f.Start.Y - anchor.Y);
                        }
                        return true;
                    default:
                        anchor = default;
                        return false;
                }
            }

            private static bool BrushSig(Brush b, ref long h)
            {
                switch (b)
                {
                    case SolidColorBrush s:
                        h = Mix(h, 11); h = MixF(h, s.Color.R); h = MixF(h, s.Color.G); h = MixF(h, s.Color.B); h = MixF(h, s.Color.A);
                        return true;
                    case LinearGradientBrush lg:
                        h = Mix(h, 12); h = Mix(h, lg.Stops.Length); h = MixF(h, lg.End.X - lg.Start.X); h = MixF(h, lg.End.Y - lg.Start.Y);
                        return true;
                    case RadialGradientBrush rg:
                        h = Mix(h, 13); h = Mix(h, rg.Stops.Length); h = MixF(h, rg.RadiusX); h = MixF(h, rg.RadiusY);
                        return true;
                    case ImageBrush ib when ib.SourceVisual == null && ib.TileMode == TileMode.None:
                        h = Mix(h, 14); h = Mix(h, ib.PixelWidth); h = Mix(h, ib.PixelHeight);
                        return true;
                    default:
                        return false;
                }
            }

            // ---- equality: the same primitive, every coordinate moved by delta -------------------

            /// <summary>Whether <paramref name="n"/> draws exactly what <paramref name="o"/> draws,
            /// moved by <paramref name="delta"/> (local units). Anything not compared field by field
            /// is not equal.</summary>
            private static bool SameMoved(DrawingPrimitive n, DrawingPrimitive o, Vector2 delta)
            {
                if (n.SourceCopy != o.SourceCopy) return false;
                switch (n)
                {
                    case GeometryFill fn when o is GeometryFill fo:
                        return fn.IsGlyph == fo.IsGlyph && fn.PixelAligned == fo.PixelAligned
                            && SameAnchor(fn.BaselineAnchor, fo.BaselineAnchor, delta)
                            && SameGeo(fn.Geometry, fo.Geometry, delta) && SameBrush(fn.Brush, fo.Brush, delta);
                    case GeometryStroke sn when o is GeometryStroke so:
                        return SameStyle(sn.Style, so.Style) && SameGeo(sn.Geometry, so.Geometry, delta) && SameBrush(sn.Brush, so.Brush, delta);
                    case GeometryDrawing dn when o is GeometryDrawing dd:
                        return SameGeo(dn.Geometry, dd.Geometry, delta)
                            && (dn.Fill == null ? dd.Fill == null : dd.Fill != null && SameBrush(dn.Fill, dd.Fill, delta))
                            && (dn.Stroke == null ? dd.Stroke == null : dd.Stroke != null && SameBrush(dn.Stroke, dd.Stroke, delta)
                                && SameStyle(dn.StrokeStyle, dd.StrokeStyle));
                    case GlyphRunDraw tn when o is GlyphRunDraw to:
                        return SameText(tn, to, delta);
                    case GdiPlusTextDraw gn when o is GdiPlusTextDraw go:
                        return gn.Run is { } rn && go.Run is { } ro && gn.FontFamily == go.FontFamily && gn.Style == go.Style
                            && gn.Argb == go.Argb && SameRun(rn, ro, delta) && SameText(gn.Fallback, go.Fallback, delta);
                    default:
                        return false;
                }
            }

            private static bool SameAnchor(Vector2? a, Vector2? b, Vector2 delta)
                => a is { } x ? b is { } y && Near(x, y + delta) : b == null;

            private static bool SameText(GlyphRunDraw n, GlyphRunDraw o, Vector2 delta)
                => n.Text == o.Text && Near(n.Origin, o.Origin + delta) && n.EmSize == o.EmSize && SameColour(n.Color, o.Color)
                   && n.Simulations == o.Simulations && n.FontFamily == o.FontFamily;

            private static bool SameRun(GdiPlusText.Run n, GdiPlusText.Run o, Vector2 delta)
            {
                // Levels the engine composed: the same shape, moved by the delta.
                if (n.Pre is not null || o.Pre is not null)
                    return n.Pre is { } np && o.Pre is { } op && n.PreKey == o.PreKey && n.Mode == o.Mode && n.Contrast == o.Contrast
                           && np.Width == op.Width && np.Height == op.Height && np.Grey == op.Grey
                           && Near(np.Left, op.Left + delta.X) && Near(np.Top, op.Top + delta.Y)
                           && np.Index.AsSpan().SequenceEqual(op.Index);
                if (!Near(n.OriginX, o.OriginX + delta.X) || !Near(n.OriginY, o.OriginY + delta.Y)) return false;
                if (n.RoundOrigin != o.RoundOrigin || n.LeadOffset != o.LeadOffset || n.Lead != o.Lead || n.LastAdvance != o.LastAdvance
                    || n.Em != o.Em || n.Mode != o.Mode || n.Hint != o.Hint || n.FixedFilter != o.FixedFilter || n.Contrast != o.Contrast
                    || n.Sx != o.Sx || n.Sy != o.Sy || n.HasClip != o.HasClip) return false;
                if (n.HasClip && (!Near(n.ClipX, o.ClipX + delta.X) || !Near(n.ClipY, o.ClipY + delta.Y) || n.ClipW != o.ClipW || n.ClipH != o.ClipH))
                    return false;
                return n.Glyphs.AsSpan().SequenceEqual(o.Glyphs) && n.Advances.AsSpan().SequenceEqual(o.Advances);
            }

            private static bool SameColour(RgbaColor a, RgbaColor b) => a.R == b.R && a.G == b.G && a.B == b.B && a.A == b.A;

            private static bool SameStyle(StrokeStyle a, StrokeStyle b)
            {
                if (a.Thickness != b.Thickness || a.Cap != b.Cap || a.Join != b.Join || a.MiterLimit != b.MiterLimit || a.DashOffset != b.DashOffset)
                    return false;
                if (a.DashArray == null || b.DashArray == null) return a.DashArray == null && b.DashArray == null;
                return a.DashArray.AsSpan().SequenceEqual(b.DashArray);
            }

            private static bool SameRect(Rect n, Rect o, Vector2 delta)
                => Near((float)n.X, (float)o.X + delta.X) && Near((float)n.Y, (float)o.Y + delta.Y) && n.Width == o.Width && n.Height == o.Height;

            private static bool SameGeo(Geometry n, Geometry o, Vector2 delta)
            {
                switch (n)
                {
                    case RectangleGeometry rn when o is RectangleGeometry ro:
                        return SameRect(rn.Rect, ro.Rect, delta);
                    case RoundedRectangleGeometry qn when o is RoundedRectangleGeometry qo:
                        return SameRect(qn.Rect, qo.Rect, delta) && qn.RadiusX == qo.RadiusX && qn.RadiusY == qo.RadiusY;
                    case EllipseGeometry en when o is EllipseGeometry eo:
                        return Near(en.Center, eo.Center + delta) && en.RadiusX == eo.RadiusX && en.RadiusY == eo.RadiusY;
                    case PolygonGeometry pn when o is PolygonGeometry po:
                        if (pn.Points.Length != po.Points.Length) return false;
                        for (int i = 0; i < pn.Points.Length; i++) if (!Near(pn.Points[i], po.Points[i] + delta)) return false;
                        return true;
                    case PathGeometry an when o is PathGeometry ao:
                        if (an.FillRule != ao.FillRule || an.Figures.Count != ao.Figures.Count) return false;
                        for (int f = 0; f < an.Figures.Count; f++)
                        {
                            PathFigure fn = an.Figures[f], fo = ao.Figures[f];
                            if (fn.Closed != fo.Closed || fn.ImpliedStart != fo.ImpliedStart || fn.Segments.Count != fo.Segments.Count
                                || !Near(fn.Start, fo.Start + delta)) return false;
                            for (int s = 0; s < fn.Segments.Count; s++)
                            {
                                switch (fn.Segments[s])
                                {
                                    case LineSegment ln when fo.Segments[s] is LineSegment lo:
                                        if (!Near(ln.Point, lo.Point + delta)) return false;
                                        break;
                                    case QuadraticBezierSegment qn2 when fo.Segments[s] is QuadraticBezierSegment qo2:
                                        if (!Near(qn2.Control, qo2.Control + delta) || !Near(qn2.Point, qo2.Point + delta)) return false;
                                        break;
                                    case CubicBezierSegment cn when fo.Segments[s] is CubicBezierSegment co:
                                        if (!Near(cn.Control1, co.Control1 + delta) || !Near(cn.Control2, co.Control2 + delta)
                                            || !Near(cn.Point, co.Point + delta)) return false;
                                        break;
                                    default:
                                        return false;
                                }
                            }
                        }
                        return true;
                    case CombinedGeometry cn2 when o is CombinedGeometry co2:
                        return cn2.Mode == co2.Mode && SameGeo(cn2.Geometry1, co2.Geometry1, delta) && SameGeo(cn2.Geometry2, co2.Geometry2, delta);
                    case GeometryGroup gn when o is GeometryGroup go:
                        if (gn.FillRule != go.FillRule || gn.Children.Count != go.Children.Count) return false;
                        for (int i = 0; i < gn.Children.Count; i++) if (!SameGeo(gn.Children[i], go.Children[i], delta)) return false;
                        return true;
                    default:
                        return false;
                }
            }

            private static bool SameStops(GradientStop[] a, GradientStop[] b)
            {
                if (a.Length != b.Length) return false;
                for (int i = 0; i < a.Length; i++)
                    if (a[i].Offset != b[i].Offset || !SameColour(a[i].Color, b[i].Color)) return false;
                return true;
            }

            private static bool SameBrush(Brush n, Brush o, Vector2 delta)
            {
                switch (n)
                {
                    case SolidColorBrush sn when o is SolidColorBrush so:
                        return SameColour(sn.Color, so.Color);
                    case LinearGradientBrush ln when o is LinearGradientBrush lo:
                        return Near(ln.Start, lo.Start + delta) && Near(ln.End, lo.End + delta) && ln.SpreadMethod == lo.SpreadMethod
                            && ln.Bands == lo.Bands && SameStops(ln.Stops, lo.Stops);
                    case RadialGradientBrush rn when o is RadialGradientBrush ro:
                        return Near(rn.Center, ro.Center + delta) && rn.RadiusX == ro.RadiusX && rn.RadiusY == ro.RadiusY
                            && rn.SpreadMethod == ro.SpreadMethod && SameStops(rn.Stops, ro.Stops);
                    case ImageBrush inn when o is ImageBrush io:
                        // Mapped to the geometry's bounds, so it moves with it; the pixels must be the same.
                        return inn.SourceVisual == null && io.SourceVisual == null && inn.PixelWidth == io.PixelWidth
                            && inn.PixelHeight == io.PixelHeight && inn.TileMode == io.TileMode && inn.TileWidth == io.TileWidth
                            && inn.TileHeight == io.TileHeight && inn.Opacity == io.Opacity
                            && (ReferenceEquals(inn.PixelsRgba, io.PixelsRgba) || inn.PixelsRgba.AsSpan().SequenceEqual(io.PixelsRgba));
                    default:
                        return false;
                }
            }
        }
    }
}
