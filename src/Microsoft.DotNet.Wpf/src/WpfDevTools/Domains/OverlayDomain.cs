// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Overlay: hover-to-highlight, and the element picker.
//
// Both directions work:
//
//   panel -> app   Overlay.highlightNode draws the box model on the real window
//   app -> panel   Overlay.setInspectMode arms a picker; clicking an element in
//                  the app sends Overlay.inspectNodeRequested and the panel
//                  selects it
//
// The picker is where a socket-based inspector beats a screenshot: you are
// pointing at the actual running window with the actual mouse, and hit testing
// is WPF's own, so what you pick is what the app would have picked.
//

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Wpf.DevTools.Json;

namespace Microsoft.Wpf.DevTools.Domains
{
    internal sealed class OverlayDomain : ICdpDomain, IDisposable
    {
        private readonly CdpSession _session;
        private readonly Dictionary<UIElement, HighlightAdorner> _adorners = new Dictionary<UIElement, HighlightAdorner>();

        private bool _inspectMode;

        /// <summary>
        /// True while the picker is armed. The Input domain needs this: when the user drives
        /// the picker over the SCREENCAST rather than over the real window, the hover arrives
        /// as Input.dispatchMouseEvent and has to pick instead of click.
        /// </summary>
        internal bool InspectModeActive => _inspectMode;
        private readonly List<UIElement> _hookedRoots = new List<UIElement>();

        internal OverlayDomain(CdpSession session)
        {
            _session = session;
        }

        public bool TryHandle(string method, JsonElement p, Utf8JsonWriter w)
        {
            switch (method)
            {
                case "Overlay.enable":
                case "Overlay.disable":
                    if (method == "Overlay.disable")
                    {
                        SetInspectMode(false);
                        HideHighlight();
                    }
                    return true;

                case "Overlay.highlightNode":
                    HighlightNode(p);
                    return true;

                case "Overlay.hideHighlight":
                    HideHighlight();
                    return true;

                case "Overlay.setInspectMode":
                    SetInspectMode(CdpJson.GetString(p, "mode") == "searchForNode");
                    return true;

                // Configuration the panel pushes that has no counterpart here. Answering
                // rather than falling through keeps them out of the not-implemented path.
                case "Overlay.setShowViewportSizeOnResize":
                case "Overlay.setShowFPSCounter":
                case "Overlay.setShowPaintRects":
                case "Overlay.setShowDebugBorders":
                case "Overlay.setShowScrollBottleneckRects":
                case "Overlay.setShowHitTestBorders":
                case "Overlay.setShowGridOverlays":
                case "Overlay.setShowFlexOverlays":
                    return true;

                default:
                    return false;
            }
        }

        public void Dispose()
        {
            SetInspectMode(false);
            HideHighlight();

            foreach (KeyValuePair<UIElement, HighlightAdorner> pair in _adorners)
            {
                try { AdornerLayer.GetAdornerLayer(pair.Key)?.Remove(pair.Value); } catch { }
            }
            _adorners.Clear();
        }

        // ------------------------------------------------------------------
        // Highlight
        // ------------------------------------------------------------------

        /// <summary>
        /// Highlight whatever is under a page-space point and tell the frontend which node it
        /// is, so the Elements tree follows the cursor. This is the screencast half of the
        /// picker; the real-window half is the PreviewMouseMove hook below.
        /// </summary>
        internal void HighlightAt(Point point)
        {
            object? hit = VisualTreeModel.HitTest(point);
            if (hit == null)
            {
                HideHighlight();
                return;
            }

            Highlight(hit);
            int nodeId = _session.Model.IdOf(hit);
            _session.SendEvent("Overlay.nodeHighlightRequested", ev => ev.WriteNumber("nodeId", nodeId));
        }

        /// <summary>Commit the picker at a page-space point: select the node and disarm.</summary>
        internal void PickAt(Point point)
        {
            object? hit = VisualTreeModel.HitTest(point);
            if (hit == null)
                return;

            int nodeId = _session.Model.IdOf(hit);
            SetInspectMode(false);
            _session.SendEvent("Overlay.inspectNodeRequested", ev => ev.WriteNumber("backendNodeId", nodeId));
        }

        private void HighlightNode(JsonElement p)
        {
            object? node = _session.Dom.ResolveNode(_session.Dom.NodeIdFrom(p));
            if (node == null)
            {
                HideHighlight();
                return;
            }

            Highlight(node);
        }

        private void Highlight(object node)
        {
            // AsVisual, not a cast: on the composition document the node is a SceneVisual,
            // and the thing to draw a box around is the element it was decoded from.
            if (VisualTreeModel.AsVisual(node) is not Visual visual ||
                !VisualTreeModel.TryGetBounds(node, out Rect border))
            {
                HideHighlight();
                return;
            }

            if (VisualTreeModel.RootOf(visual) is not UIElement root)
            {
                HideHighlight();
                return;
            }

            HighlightAdorner? adorner = GetAdorner(root);
            if (adorner == null)
                return;

            Thickness margin = VisualTreeModel.MarginOf(node);
            Thickness borderThickness = VisualTreeModel.BorderOf(node);
            Thickness padding = VisualTreeModel.PaddingOf(node);

            Rect marginBox = Expand(border, margin);
            Rect paddingBox = Shrink(border, borderThickness);
            Rect contentBox = Shrink(paddingBox, padding);

            string label = string.Format(
                CultureInfo.InvariantCulture,
                "{0}  {1:0.##} x {2:0.##}",
                DomDomain.DescriptionOf(node),
                border.Width,
                border.Height);

            // Everything else stops showing a box, or you end up with one highlight
            // per window you have hovered over this session.
            foreach (KeyValuePair<UIElement, HighlightAdorner> pair in _adorners)
            {
                if (!ReferenceEquals(pair.Value, adorner))
                    pair.Value.Hide();
            }

            adorner.Show(contentBox, paddingBox, border, marginBox, label);
        }

        private void HideHighlight()
        {
            foreach (KeyValuePair<UIElement, HighlightAdorner> pair in _adorners)
                pair.Value.Hide();
        }

        /// <summary>
        /// The adorner for a root, created on first use. Returns null when the root has
        /// no adorner layer, which happens for a root whose template has no
        /// AdornerDecorator -- rare, but a Popup's child can be one.
        /// </summary>
        private HighlightAdorner? GetAdorner(UIElement root)
        {
            if (_adorners.TryGetValue(root, out HighlightAdorner? existing))
                return existing;

            AdornerLayer? layer;
            try
            {
                layer = AdornerLayer.GetAdornerLayer(root);
            }
            catch
            {
                return null;
            }

            if (layer == null)
                return null;

            var adorner = new HighlightAdorner(root);
            layer.Add(adorner);
            _adorners[root] = adorner;
            return adorner;
        }

        private static Rect Expand(Rect r, Thickness t)
            => new Rect(r.X - t.Left, r.Y - t.Top,
                        Math.Max(0, r.Width + t.Left + t.Right),
                        Math.Max(0, r.Height + t.Top + t.Bottom));

        private static Rect Shrink(Rect r, Thickness t)
            => new Rect(r.X + t.Left, r.Y + t.Top,
                        Math.Max(0, r.Width - t.Left - t.Right),
                        Math.Max(0, r.Height - t.Top - t.Bottom));

        // ------------------------------------------------------------------
        // Inspect mode
        // ------------------------------------------------------------------

        private void SetInspectMode(bool enabled)
        {
            if (_inspectMode == enabled)
                return;

            _inspectMode = enabled;

            if (enabled)
            {
                foreach (Visual root in VisualTreeModel.VisualRoots())
                {
                    if (root is not UIElement element)
                        continue;

                    element.PreviewMouseMove += OnPreviewMouseMove;
                    element.PreviewMouseLeftButtonDown += OnPreviewMouseDown;
                    _hookedRoots.Add(element);
                }
            }
            else
            {
                foreach (UIElement element in _hookedRoots)
                {
                    element.PreviewMouseMove -= OnPreviewMouseMove;
                    element.PreviewMouseLeftButtonDown -= OnPreviewMouseDown;
                }
                _hookedRoots.Clear();
                HideHighlight();
            }
        }

        private void OnPreviewMouseMove(object sender, MouseEventArgs e)
        {
            object? hit = HitTest(sender, e);
            if (hit != null)
                Highlight(hit);
        }

        private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            object? hit = HitTest(sender, e);
            if (hit == null)
                return;

            // Swallow the click. In inspect mode the point is to pick the element, not
            // to press the button that happens to be under the cursor.
            e.Handled = true;

            int nodeId = _session.Model.IdOf(hit);
            SetInspectMode(false);

            _session.SendEvent("Overlay.inspectNodeRequested", ev => ev.WriteNumber("backendNodeId", nodeId));
        }

        private object? HitTest(object sender, MouseEventArgs e)
        {
            if (sender is not Visual root)
                return null;

            try
            {
                Point point = e.GetPosition((IInputElement)root);
                HitTestResult result = VisualTreeHelper.HitTest(root, point);

                DependencyObject? hit = result?.VisualHit;

                // The hit can land on the highlight's own adorner layer or on a visual
                // this inspector owns; walk out to the first node that belongs to the app.
                while (hit != null && VisualTreeModel.IsInspectorOwned(hit))
                    hit = VisualTreeHelper.GetParent(hit);

                return hit;
            }
            catch
            {
                return null;
            }
        }
    }
}
