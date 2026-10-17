// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// An immutable copy of a live scene tree, for a scene read by a thread that does not own it.
//
// MilcoreEngine keeps ONE SceneVisual per WPF visual for the life of the visual and updates it in place:
// its properties are reassigned, its Content and Children lists cleared and refilled, every frame that
// visual changes. With the render thread on, that happens on the render thread. A WPF window hosted by
// a WinForms ElementHost published that live tree to the WinForms UI thread, whose presenter walked it
// (the damage tracker, the renderer) while the render thread rebuilt the next frame in it -- the list
// shrank under the walk, and app died with an ArgumentOutOfRangeException in
// DamageTracker.PatchContent.
//
// A mirror is published instead. It is built on the thread that owns the live tree, as it publishes,
// and no node of it is ever changed afterwards: a visual that is the same as last frame (every property,
// the same primitives in the same order, the same mirrored children) keeps last frame's mirror node, so
// the reader's caches and its frame-to-frame diff still see an unchanged subtree as the same objects;
// anything else gets a new node. The reader may stamp its own bookkeeping (damage bounds, guideline
// memos, the scroll-snap nudge) on the nodes it is handed -- nothing on this side reads those.

using System;
using System.Collections.Generic;

namespace Microsoft.Wpf.Interop.WebGpu.Composition
{
    internal sealed class SceneMirror
    {
        // live visual -> the mirror node published for it last time
        private Dictionary<SceneVisual, SceneVisual> _last = new(ReferenceEqualityComparer.Instance);
        private Dictionary<SceneVisual, SceneVisual> _next = new(ReferenceEqualityComparer.Instance);

        /// <summary>Nodes made (not reused) by the last <see cref="Snapshot"/>, for tests.</summary>
        internal int LastMade { get; private set; }

        /// <summary>The mirror of <paramref name="live"/>, sharing every unchanged node with the last one.
        /// Call on the thread that owns the live tree.</summary>
        public SceneVisual Snapshot(SceneVisual live)
        {
            LastMade = 0;
            _next.Clear();
            _primNext.Clear();
            SceneVisual root = Mirror(live, 0);
            (_last, _next) = (_next, _last);
            _next.Clear();
            (_primLast, _primNext) = (_primNext, _primLast);
            _primNext.Clear();
            return root;
        }

        private SceneVisual Mirror(SceneVisual v, int depth)
        {
            if (_next.TryGetValue(v, out SceneVisual? done)) return done;   // shared below two parents
            // Taken once: the live lists are only ever changed by this thread, between snapshots.
            int nk = v.Children.Count;
            var kids = nk == 0 ? System.Array.Empty<SceneVisual>() : new SceneVisual[nk];
            // A cycle cannot be built through WPF, but the tree is decoded off a wire: bound the work.
            if (depth < 512)
                for (int i = 0; i < nk; i++) kids[i] = Mirror(v.Children[i], depth + 1);
            else
                nk = 0;
            int nc = v.Content.Count;
            var content = nc == 0 ? System.Array.Empty<DrawingPrimitive>() : new DrawingPrimitive[nc];
            for (int i = 0; i < nc; i++) content[i] = MirrorPrimitive(v.Content[i], depth);

            if (_last.TryGetValue(v, out SceneVisual? was) && Same(was, v, content, kids, nk))
            {
                _next[v] = was;
                return was;
            }
            var m = new SceneVisual
            {
                Id = v.Id,
                Offset = v.Offset,
                Transform = v.Transform,
                Opacity = v.Opacity,
                Clip = v.Clip,
                ClipGeometry = v.ClipGeometry,
                Effect = v.Effect,
                OpacityMask = v.OpacityMask,
                BitmapCached = v.BitmapCached,
                Snapshot = v.Snapshot,
                AliasedEdges = v.AliasedEdges,
                NearestBitmapScaling = v.NearestBitmapScaling,
                GuidelinesX = v.GuidelinesX,
                GuidelinesY = v.GuidelinesY,
            };
            m.Content.AddRange(content);
            for (int i = 0; i < nk; i++) m.Children.Add(kids[i]);
            LastMade++;
            _next[v] = m;
            return m;
        }

        // A primitive painting with a GPU-live brush (a VisualBrush's source drawn each frame) points at a
        // LIVE visual: it is re-pointed at that visual's mirror. Remembered per primitive, so an unchanged
        // source keeps the same re-pointed primitive (and the reader's diff sees it unchanged).
        private Dictionary<DrawingPrimitive, (SceneVisual Source, DrawingPrimitive Mirrored)> _primLast = new(ReferenceEqualityComparer.Instance);
        private Dictionary<DrawingPrimitive, (SceneVisual Source, DrawingPrimitive Mirrored)> _primNext = new(ReferenceEqualityComparer.Instance);

        private static ImageBrush? Live(Brush? b) => b is ImageBrush { SourceVisual: not null } ib ? ib : null;

        private DrawingPrimitive MirrorPrimitive(DrawingPrimitive p, int depth)
        {
            ImageBrush? live = p switch
            {
                GeometryFill f => Live(f.Brush),
                GeometryStroke s => Live(s.Brush),
                GeometryDrawing d => Live(d.Fill) ?? Live(d.Stroke),
                _ => null,
            };
            if (live == null) return p;
            SceneVisual src = Mirror(live.SourceVisual!, depth + 1);
            if (_primLast.TryGetValue(p, out var had) && ReferenceEquals(had.Source, src))
            {
                _primNext[p] = had;
                return had.Mirrored;
            }
            Brush? Re(Brush? b) => Live(b) is { } ib
                ? new ImageBrush(src, ib.SourceId, ib.SourceTexW, ib.SourceTexH, ib.U0, ib.V0, ib.U1, ib.V1, ib.TileWidth, ib.TileHeight, ib.Opacity)
                : b;
            DrawingPrimitive mirrored = p switch
            {
                GeometryFill f => new GeometryFill(f.Geometry, Re(f.Brush)!, f.IsGlyph, f.BaselineAnchor) { PixelAligned = f.PixelAligned },
                GeometryStroke s => new GeometryStroke(s.Geometry, Re(s.Brush)!, s.Style),
                GeometryDrawing d => new GeometryDrawing(d.Geometry, Re(d.Fill), Re(d.Stroke), d.StrokeStyle),
                _ => p,
            };
            mirrored.SourceCopy = p.SourceCopy;
            _primNext[p] = (src, mirrored);
            return mirrored;
        }

        private static bool Same(SceneVisual m, SceneVisual v, DrawingPrimitive[] content, SceneVisual[] kids, int nk)
        {
            if (m.Id != v.Id || m.Offset != v.Offset || m.Transform != v.Transform || m.Opacity != v.Opacity
                || !Nullable.Equals(m.Clip, v.Clip) || !ReferenceEquals(m.ClipGeometry, v.ClipGeometry) || !ReferenceEquals(m.Effect, v.Effect)
                || !ReferenceEquals(m.OpacityMask, v.OpacityMask) || m.BitmapCached != v.BitmapCached
                || !ReferenceEquals(m.Snapshot, v.Snapshot) || m.AliasedEdges != v.AliasedEdges
                || m.NearestBitmapScaling != v.NearestBitmapScaling || !ReferenceEquals(m.GuidelinesX, v.GuidelinesX)
                || !ReferenceEquals(m.GuidelinesY, v.GuidelinesY))
                return false;
            List<DrawingPrimitive> mc = m.Content;
            if (mc.Count != content.Length) return false;
            for (int i = 0; i < content.Length; i++) if (!ReferenceEquals(mc[i], content[i])) return false;
            if (m.Children.Count != nk) return false;
            for (int i = 0; i < nk; i++) if (!ReferenceEquals(m.Children[i], kids[i])) return false;
            return true;
        }
    }
}
