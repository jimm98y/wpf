// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A minimal, fully-managed FlowDocument layout used where the native PTS engine
// (PresentationNative_cor3.dll) is unavailable — i.e. off-Windows. It replaces
// PTS's paragraph/track/page machinery for the common bottomless case (a single
// column of stacked blocks) so RichTextBox / FlowDocument* controls render instead
// of throwing DllNotFoundException. Each Paragraph is laid out with FormattedText
// (which runs on the fork's managed text stack), giving wrapping + per-run
// font/weight/style/colour.
//
// Layout is INCREMENTAL: the laid-out box of each top-level block (its FormattedText
// + height) is cached by block reference. On a content change only the blocks the
// caller reports dirty (mapped from StructuralCache's dirty text ranges) are
// re-measured; every other block reuses its cached FormattedText and is merely
// repositioned, so typing does not re-lay-out the whole document.
//
// Tables/floaters/figures and full editing hit-testing are not modelled here; this is
// layout + rendering, not the whole PTS feature set.
//

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace MS.Internal.PtsHost
{
    internal sealed class ManagedFlowLayout
    {
        private const double DefaultPixelsPerDip = 1.0;

        // Cached layout of one top-level block: FormattedText items (positioned relative to the block's
        // top, y=0) plus the block's total height. Reused across formats until the block is dirtied.
        private sealed class BlockBox
        {
            public readonly List<Item> Items = new();
            public double Height;
        }

        private readonly struct Item
        {
            public readonly FormattedText Text;
            public readonly Point Origin;    // X absolute (left-inset), Y relative to the block top
            public readonly double Right;
            public readonly Paragraph Para;  // source paragraph (null for list markers) -> caret/hit-test
            public readonly int Length;      // FormattedText char count (paragraph items)
            public Item(FormattedText text, Point origin, double right, Paragraph para, int length) { Text = text; Origin = origin; Right = right; Para = para; Length = length; }
        }

        // A paragraph's FormattedText placed at an absolute origin — used by the managed text view for
        // caret rectangles and point hit-testing.
        internal readonly struct ParaLayout
        {
            public readonly Paragraph Para;
            public readonly FormattedText Text;
            public readonly Point Origin;    // absolute
            public readonly int Length;      // FormattedText char count
            public ParaLayout(Paragraph para, FormattedText text, Point origin, int length) { Para = para; Text = text; Origin = origin; Length = length; }
            public double Height => Text.Height;
        }

        private readonly Dictionary<Block, BlockBox> _cache = new();
        // Per-character bounds cache, keyed by the paragraph's FormattedText. Building a highlight
        // geometry per character (CharRect) is costly and is hit hard during drag-select / caret
        // hit-testing (once per character, per mouse move). Cache the whole row of char rects on first
        // use; a ConditionalWeakTable drops the entry automatically when the FormattedText is replaced
        // on reflow, so no manual eviction is needed.
        private readonly System.Runtime.CompilerServices.ConditionalWeakTable<FormattedText, Rect[]> _charRectCache = new();
        private readonly List<(FormattedText Text, Point Origin)> _draw = new();
        private readonly List<ParaLayout> _paras = new();
        private double _lastWidth = double.NaN;

        /// <summary>Paragraph layouts (absolute), in document order — for the managed text view.</summary>
        internal IReadOnlyList<ParaLayout> Paragraphs => _paras;

        /// <summary>Total content size (page width x laid-out height).</summary>
        public Size Size { get; private set; }

        /// <summary>
        /// Lay the document's blocks into a single bottomless column. <paramref name="dirty"/> lists the
        /// top-level blocks whose content changed since the last call (null on the first call / a full
        /// relayout); every other block reuses its cached FormattedText.
        /// </summary>
        public void Format(FlowDocument document, Size pageSize, Thickness pageMargin, HashSet<Block> dirty)
        {
            double pageWidth = double.IsFinite(pageSize.Width) ? pageSize.Width : 0;
            double ml = Fin(pageMargin.Left), mr = Fin(pageMargin.Right), mt = Fin(pageMargin.Top), mb = Fin(pageMargin.Bottom);
            double contentWidth = pageWidth > 0 ? Math.Max(0, pageWidth - ml - mr) : 0;   // 0 => don't wrap

            // A width change reflows everything; drop the cache so all blocks re-measure.
            if (!DoubleClose(contentWidth, _lastWidth))
            {
                _cache.Clear();
                _lastWidth = contentWidth;
            }

            _draw.Clear();
            _paras.Clear();
            double y = mt;
            double maxRight = ml;
            var present = document != null ? new HashSet<Block>() : null;

            if (document != null)
            {
                foreach (Block block in document.Blocks)
                {
                    present.Add(block);
                    if (!_cache.TryGetValue(block, out BlockBox box) || (dirty != null && dirty.Contains(block)))
                    {
                        box = LayoutBlock(block, ml, contentWidth);
                        _cache[block] = box;
                    }
                    foreach (Item it in box.Items)
                    {
                        var abs = new Point(it.Origin.X, it.Origin.Y + y);
                        _draw.Add((it.Text, abs));
                        if (it.Para != null) _paras.Add(new ParaLayout(it.Para, it.Text, abs, it.Length));
                        if (it.Right > maxRight) maxRight = it.Right;
                    }
                    y += box.Height;
                }
                // Evict blocks no longer in the document.
                if (_cache.Count > present.Count)
                {
                    var stale = new List<Block>();
                    foreach (Block b in _cache.Keys) if (!present.Contains(b)) stale.Add(b);
                    foreach (Block b in stale) _cache.Remove(b);
                }
            }

            double width = pageWidth > 0 ? pageWidth : maxRight + mr;
            double height = Math.Max(y + mb, mt + mb + 16);   // slack so an empty doc keeps a caret-height page
            Size = new Size(Fin(width, 0), Fin(height, mt + mb + 16));
        }

        /// <summary>Render the laid-out content. Called with the page visual's DrawingContext.</summary>
        public void Render(DrawingContext dc)
        {
            foreach (var d in _draw)
                dc.DrawText(d.Text, d.Origin);
        }

        /// <summary>Caret rectangle (absolute page coords, zero width) for a character offset within a
        /// laid-out paragraph, or null if the paragraph isn't laid out.</summary>
        public Rect? CaretRect(Paragraph para, int charOffset)
        {
            foreach (ParaLayout pl in _paras)
            {
                if (pl.Para != para) continue;
                double x, top, height;
                int len = pl.Length;
                charOffset = Math.Max(0, Math.Min(charOffset, len));
                if (len == 0)
                {
                    x = pl.Origin.X; top = pl.Origin.Y; height = pl.Text.Height;
                }
                else if (charOffset < len)
                {
                    Rect r = CharRects(pl.Text, len)[charOffset];
                    x = pl.Origin.X + r.Left; top = pl.Origin.Y + r.Top; height = r.Height;
                }
                else
                {
                    Rect r = CharRects(pl.Text, len)[len - 1];
                    x = pl.Origin.X + r.Right; top = pl.Origin.Y + r.Top; height = r.Height;
                }
                return new Rect(x, top, 0, height > 0 ? height : pl.Text.Height);
            }
            return null;
        }

        /// <summary>Hit-test a page point to a paragraph + character offset. False if no paragraph.</summary>
        public bool HitTest(Point p, out Paragraph para, out int charOffset)
        {
            para = null; charOffset = 0;
            ParaLayout best = default; bool found = false; double bestDist = double.MaxValue;
            foreach (ParaLayout pl in _paras)
            {
                double top = pl.Origin.Y, bot = pl.Origin.Y + pl.Height;
                if (p.Y >= top && p.Y <= bot) { best = pl; found = true; break; }
                double d = p.Y < top ? top - p.Y : p.Y - bot;
                if (d < bestDist) { bestDist = d; best = pl; found = true; }
            }
            if (!found) return false;
            para = best.Para;
            double localX = p.X - best.Origin.X;
            double localY = p.Y - best.Origin.Y;
            int len = best.Length;

            // A single paragraph's FormattedText may wrap across several visual lines. Resolve the
            // offset on the visual line nearest localY (grouping chars that share a line by their
            // rect.Top), then pick the X position within that line. Using X alone would always map a
            // click on line 2+ back onto line 1.
            Rect[] rects = CharRects(best.Text, len);
            charOffset = len;
            double bestRowDy = double.MaxValue;
            int i = 0;
            while (i < len)
            {
                Rect r0 = rects[i];
                double top = r0.Top, bot = r0.Bottom;
                int lineStart = i, j = i;
                while (j < len)
                {
                    Rect rj = rects[j];
                    if (rj.Top > top + 0.5) break;   // start of the next visual line
                    if (rj.Bottom > bot) bot = rj.Bottom;
                    j++;
                }
                double dy = localY < top ? top - localY : (localY > bot ? localY - bot : 0);
                if (dy < bestRowDy)
                {
                    bestRowDy = dy;
                    int cand = j;   // past the last char on this line
                    for (int k = lineStart; k < j; k++)
                    {
                        if (localX < rects[k].Left + rects[k].Width / 2) { cand = k; break; }
                    }
                    charOffset = cand;
                }
                i = j;
            }
            return true;
        }

        /// <summary>Highlight geometry (absolute) for a character range within a laid-out paragraph.</summary>
        // viewportOffset is baked into the geometry's points (the origin) rather than applied as a
        // Geometry.Transform: the WebGPU compositor ignores Geometry.Transform (MilcoreEngine drops the
        // PathGeometry hTransform), so the selection highlight would otherwise render page-absolute and
        // not track the text as it scrolls. See TextDocumentView.ManagedSelectionGeometry.
        public Geometry ParagraphHighlight(Paragraph para, int startChar, int endChar, Vector viewportOffset)
        {
            foreach (ParaLayout pl in _paras)
            {
                if (pl.Para != para) continue;
                int len = pl.Length;
                int s = Math.Max(0, Math.Min(startChar, len));
                int e = Math.Max(s, Math.Min(endChar, len));
                if (e <= s) return null;
                var origin = new Point(pl.Origin.X - viewportOffset.X, pl.Origin.Y - viewportOffset.Y);
                return pl.Text.BuildHighlightGeometry(origin, s, e - s);
            }
            return null;
        }

        /// <summary>Char length of a laid-out paragraph, or -1 if not laid out.</summary>
        public int ParagraphLength(Paragraph para)
        {
            foreach (ParaLayout pl in _paras) if (pl.Para == para) return pl.Length;
            return -1;
        }

        private static Rect CharRect(FormattedText ft, int index)
        {
            Geometry g = ft.BuildHighlightGeometry(new Point(0, 0), index, 1);
            Rect r = g != null ? g.Bounds : Rect.Empty;
            return r.IsEmpty ? new Rect(0, 0, 0, ft.Height) : r;
        }

        // Cached row of per-character rects for a laid-out paragraph's FormattedText.
        private Rect[] CharRects(FormattedText ft, int len)
        {
            if (!_charRectCache.TryGetValue(ft, out Rect[] arr))
            {
                arr = new Rect[len];
                for (int i = 0; i < len; i++) arr[i] = CharRect(ft, i);
                _charRectCache.Add(ft, arr);
            }
            return arr;
        }

        // ---- per-block layout (produces a cacheable BlockBox, positions relative to the block top) ----

        private BlockBox LayoutBlock(Block block, double left, double width)
        {
            var box = new BlockBox();
            box.Height = AppendBlock(block, box, left, width, 0, 0);
            return box;
        }

        private double AppendBlock(Block block, BlockBox box, double left, double width, double y, int listDepth)
        {
            switch (block)
            {
                case Paragraph paragraph:
                    return AppendParagraph(paragraph, box, left, width, y);
                case System.Windows.Documents.Section section:
                    foreach (Block b in section.Blocks) y = AppendBlock(b, box, left, width, y, listDepth);
                    return y;
                case List list:
                    return AppendList(list, box, left, width, y, listDepth);
                default:
                    return y;   // Table / BlockUIContainer / other: not modelled
            }
        }

        private double AppendList(List list, BlockBox box, double left, double width, double y, int listDepth)
        {
            const double marker = 18.0;
            int index = list.StartIndex;
            foreach (ListItem li in list.ListItems)
            {
                double itemTop = y;
                foreach (Block b in li.Blocks)
                    y = AppendBlock(b, box, left + marker, Math.Max(0, width - marker), y, listDepth + 1);
                string mark = list.MarkerStyle == System.Windows.TextMarkerStyle.Decimal ? (index + ".") : "•";
                FormattedText fm = MakeText(mark, list, marker);
                box.Items.Add(new Item(fm, new Point(left, itemTop), left + Fin(fm.WidthIncludingTrailingWhitespace), null, 0));
                index++;
            }
            return y;
        }

        private double AppendParagraph(Paragraph paragraph, BlockBox box, double left, double width, double y)
        {
            double mTop = Fin(paragraph.Margin.Top), mLeft = Fin(paragraph.Margin.Left);
            double mRight = Fin(paragraph.Margin.Right), mBottom = Fin(paragraph.Margin.Bottom);
            y += mTop;
            double x = left + mLeft;
            double w = width > 0 ? Math.Max(0, width - mLeft - mRight) : 0;

            FormattedText ft = BuildParagraphText(paragraph, w, out int textLen);
            if (ft != null)
            {
                box.Items.Add(new Item(ft, new Point(x, y), x + Fin(ft.WidthIncludingTrailingWhitespace), paragraph, textLen));
                y += Fin(ft.Height);
            }
            return y + mBottom;
        }

        // Concatenate the paragraph's inline text and apply per-run font/weight/style/colour.
        private FormattedText BuildParagraphText(Paragraph paragraph, double width, out int textLen)
        {
            var sb = new System.Text.StringBuilder();
            var runs = new List<RunFmt>();
            CollectInlines(paragraph.Inlines, sb, runs);
            string text = sb.ToString();
            textLen = text.Length;
            if (text.Length == 0) text = " ";   // keep a line box (paragraph height)

            FormattedText ft = MakeText(text, paragraph, width);
            ft.TextAlignment = paragraph.TextAlignment;
            double lh = paragraph.LineHeight;
            if (!double.IsNaN(lh) && lh > 0) ft.LineHeight = lh;

            foreach (RunFmt r in runs)
            {
                if (r.Length <= 0 || r.Start >= text.Length) continue;
                int len = Math.Min(r.Length, text.Length - r.Start);
                if (r.Family != null) ft.SetFontFamily(r.Family, r.Start, len);
                if (r.Size > 0) ft.SetFontSize(r.Size, r.Start, len);
                ft.SetFontWeight(r.Weight, r.Start, len);
                ft.SetFontStyle(r.Style, r.Start, len);
                if (r.Foreground != null) ft.SetForegroundBrush(r.Foreground, r.Start, len);
                if (r.Underline) ft.SetTextDecorations(TextDecorations.Underline, r.Start, len);
            }
            return ft;
        }

        private readonly struct RunFmt
        {
            public readonly int Start, Length;
            public readonly FontFamily Family;
            public readonly double Size;
            public readonly FontWeight Weight;
            public readonly FontStyle Style;
            public readonly Brush Foreground;
            public readonly bool Underline;
            public RunFmt(int start, int length, FontFamily family, double size, FontWeight weight, FontStyle style, Brush fg, bool underline)
            { Start = start; Length = length; Family = family; Size = size; Weight = weight; Style = style; Foreground = fg; Underline = underline; }
        }

        private static void CollectInlines(InlineCollection inlines, System.Text.StringBuilder sb, List<RunFmt> runs)
        {
            foreach (Inline inline in inlines)
            {
                switch (inline)
                {
                    case Run run:
                    {
                        int start = sb.Length;
                        sb.Append(run.Text ?? string.Empty);
                        int len = sb.Length - start;
                        if (len > 0) runs.Add(RunFmtFor(run, start, len));
                        break;
                    }
                    case LineBreak:
                        sb.Append('\n');
                        break;
                    case System.Windows.Documents.Span span:   // covers Bold, Italic, Underline, Hyperlink
                        CollectInlines(span.Inlines, sb, runs);
                        break;
                    case InlineUIContainer:
                        sb.Append('￼');  // object replacement char, keeps spacing
                        break;
                }
            }
        }

        private static RunFmt RunFmtFor(TextElement e, int start, int len)
        {
            var family = (FontFamily)e.GetValue(TextElement.FontFamilyProperty);
            double size = (double)e.GetValue(TextElement.FontSizeProperty);
            var weight = (FontWeight)e.GetValue(TextElement.FontWeightProperty);
            var style = (FontStyle)e.GetValue(TextElement.FontStyleProperty);
            var fg = e.GetValue(TextElement.ForegroundProperty) as Brush;
            bool underline = false;
            for (DependencyObject d = e; d is Inline inl; d = inl.Parent)
                if (inl.TextDecorations != null && inl.TextDecorations.Count > 0) { underline = true; break; }
            return new RunFmt(start, len, family, size, weight, style, fg, underline);
        }

        private static FormattedText MakeText(string text, TextElement props, double maxWidth)
        {
            var family = (FontFamily)props.GetValue(TextElement.FontFamilyProperty);
            double size = (double)props.GetValue(TextElement.FontSizeProperty);
            var weight = (FontWeight)props.GetValue(TextElement.FontWeightProperty);
            var style = (FontStyle)props.GetValue(TextElement.FontStyleProperty);
            var stretch = (FontStretch)props.GetValue(TextElement.FontStretchProperty);
            var fg = props.GetValue(TextElement.ForegroundProperty) as Brush ?? Brushes.Black;
            var typeface = new Typeface(family, style, weight, stretch);

            var ft = new FormattedText(text ?? string.Empty, CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight, typeface, size <= 0 ? 12.0 : size, fg, DefaultPixelsPerDip);
            if (maxWidth > 0) ft.MaxTextWidth = maxWidth;
            return ft;
        }

        private static double Fin(double v, double fallback = 0) => double.IsFinite(v) ? v : fallback;
        private static bool DoubleClose(double a, double b) => (double.IsNaN(a) && double.IsNaN(b)) || Math.Abs(a - b) < 0.01;
    }
}
