// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The rectangle you see on the real window when you hover a node in the panel.
//
// One adorner per ROOT, not one per highlighted element. The boxes are drawn in
// the root's coordinate space -- which is the space TryGetBounds already reports
// in -- so highlighting a different element is an InvalidateVisual rather than
// tearing down an adorner and building another somewhere else in the tree.
//
// It is a Visual in the tree it is drawing on, which would make the inspector
// show up in its own output; VisualTreeModel.IsInspectorOwned filters this
// namespace out of the walk for exactly that reason.
//

using System;
using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace Microsoft.Wpf.DevTools
{
    internal sealed class HighlightAdorner : Adorner
    {
        // The DevTools box-model palette, so the colours mean the same thing here as
        // they do in the panel.
        private static readonly Brush ContentBrush = Frozen(Color.FromArgb(0xA8, 0x6F, 0xA8, 0xDC));
        private static readonly Brush PaddingBrush = Frozen(Color.FromArgb(0xA8, 0x93, 0xC4, 0x7D));
        private static readonly Brush BorderBrush2 = Frozen(Color.FromArgb(0xA8, 0xFF, 0xE5, 0x99));
        private static readonly Brush MarginBrush = Frozen(Color.FromArgb(0xA8, 0xF6, 0xB2, 0x6B));
        private static readonly Brush LabelBackground = Frozen(Color.FromArgb(0xE6, 0x1E, 0x1E, 0x1E));
        private static readonly Brush LabelForeground = Frozen(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));

        private bool _visible;
        private Rect _content, _padding, _border, _margin;
        private string _label = string.Empty;

        internal HighlightAdorner(UIElement adornedElement)
            : base(adornedElement)
        {
            // The highlight must never eat input: in inspect mode the mouse has to
            // reach the app underneath so the next hover hit-tests the real tree.
            IsHitTestVisible = false;
        }

        private static Brush Frozen(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        internal void Show(Rect content, Rect padding, Rect border, Rect margin, string label)
        {
            _content = content;
            _padding = padding;
            _border = border;
            _margin = margin;
            _label = label;
            _visible = true;
            InvalidateVisual();
        }

        internal void Hide()
        {
            if (!_visible)
                return;

            _visible = false;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            if (!_visible)
                return;

            // Rings, outermost first, so each band shows only where it is not covered
            // by the next one in. Filling them solid and letting the translucency stack
            // would make every band a different colour than the legend says.
            DrawRing(drawingContext, MarginBrush, _margin, _border);
            DrawRing(drawingContext, BorderBrush2, _border, _padding);
            DrawRing(drawingContext, PaddingBrush, _padding, _content);
            drawingContext.DrawRectangle(ContentBrush, null, _content);

            DrawLabel(drawingContext);
        }

        private static void DrawRing(DrawingContext dc, Brush brush, Rect outer, Rect inner)
        {
            if (outer.IsEmpty || outer.Width <= 0 || outer.Height <= 0)
                return;

            if (inner.IsEmpty || inner.Width <= 0 || inner.Height <= 0)
            {
                dc.DrawRectangle(brush, null, outer);
                return;
            }

            Geometry ring = Geometry.Combine(
                new RectangleGeometry(outer),
                new RectangleGeometry(inner),
                GeometryCombineMode.Exclude,
                null);

            dc.DrawGeometry(brush, null, ring);
        }

        private void DrawLabel(DrawingContext dc)
        {
            if (_label.Length == 0)
                return;

            FormattedText text;
            try
            {
                text = new FormattedText(
                    _label,
                    CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    new Typeface("Segoe UI"),
                    12,
                    LabelForeground,
                    1.0);
            }
            catch
            {
                // Font resolution differs per head and a missing face must not take the
                // highlight with it.
                return;
            }

            const double PadX = 6, PadY = 3;
            double width = text.Width + PadX * 2;
            double height = text.Height + PadY * 2;

            // Above the element by preference, inside it when there is no room -- the
            // same placement rule the browser's own badge uses.
            double x = _margin.Left;
            double y = _margin.Top - height - 2;
            if (y < 0)
                y = _margin.Top + 2;

            dc.DrawRectangle(LabelBackground, null, new Rect(x, y, width, height));
            dc.DrawText(text, new Point(x + PadX, y + PadY));
        }
    }
}
