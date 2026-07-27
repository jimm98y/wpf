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
        // Per-block line layout (paginated path), keyed by block; column-independent (built at column width),
        // so it survives re-packing and is only rebuilt on a width change or when the block is dirtied.
        private readonly Dictionary<Block, List<LineBox>> _lineCache = new();
        // Space-advance width per (font family, size, weight, style), for justification spacing.
        private readonly Dictionary<(FontFamily, double, FontWeight, FontStyle), double> _spaceWidth = new();
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
        /// Lay the document's blocks into one or more columns. <paramref name="dirty"/> lists the top-level
        /// blocks whose content changed since the last call (null on the first call / a full relayout);
        /// every other block reuses its cached FormattedText. <paramref name="columnHeight"/> &gt; 0 enables
        /// paginated multi-column layout (a FlowDocument with a ColumnWidth narrower than the content flows
        /// down one column then into the next); 0 = single bottomless column (RichTextBox / scroll viewer).
        /// </summary>
        public void Format(FlowDocument document, Size pageSize, Thickness pageMargin, HashSet<Block> dirty, double columnHeight = 0)
        {
            double pageWidth = double.IsFinite(pageSize.Width) ? pageSize.Width : 0;
            double ml = Fin(pageMargin.Left), mr = Fin(pageMargin.Right), mt = Fin(pageMargin.Top), mb = Fin(pageMargin.Bottom);
            double contentWidth = pageWidth > 0 ? Math.Max(0, pageWidth - ml - mr) : 0;   // 0 => don't wrap

            // Column geometry: WPF fits N = floor((W + gap) / (colWidth + gap)) columns when ColumnWidth is
            // narrower than the content; flexible width (default) expands them to fill W. Only paginated
            // (finite-height) layout paginates into columns — bottomless stays single-column.
            int colCount = 1;
            double colGap = 0, colW = contentWidth;
            if (columnHeight > 0 && document != null && contentWidth > 0)
            {
                double cw = document.ColumnWidth;
                colGap = document.ColumnGap;
                if (double.IsNaN(colGap) || colGap < 0) colGap = 0;
                if (!double.IsNaN(cw) && cw > 0 && cw < contentWidth)
                {
                    colCount = Math.Max(1, (int)Math.Floor((contentWidth + colGap) / (cw + colGap)));
                    colW = document.IsColumnWidthFlexible
                        ? (contentWidth - (colCount - 1) * colGap) / colCount
                        : cw;
                }
            }

            // A layout-width change (incl. switching column count) reflows everything; drop the cache.
            if (!DoubleClose(colW, _lastWidth))
            {
                _cache.Clear();
                _lineCache.Clear();
                _lastWidth = colW;
            }

            _draw.Clear();
            _paras.Clear();

            // Paginated (finite-height) layout uses per-line layout: lines pack into columns so a column
            // fills completely before overflowing to the next, and each line is justified when the paragraph
            // asks for it (LineServices' justify is native-only, so we distribute space ourselves). Bottomless
            // (RichTextBox / scroll viewer, columnHeight == 0) keeps the simpler whole-paragraph path below.
            if (columnHeight > 0)
            {
                FormatPaginated(document, dirty, ml, mr, mt, mb, colW, colGap, colCount, columnHeight, pageWidth);
                return;
            }

            double maxRight = ml;
            var present = document != null ? new HashSet<Block>() : null;
            int col = 0;
            double colTop = mt;       // running fill height of the current column
            double bottomMost = mt;   // lowest column bottom used (for the single-column/bottomless page height)

            if (document != null)
            {
                foreach (Block block in document.Blocks)
                {
                    present.Add(block);
                    if (!_cache.TryGetValue(block, out BlockBox box) || (dirty != null && dirty.Contains(block)))
                    {
                        box = LayoutBlock(block, ml, colW);
                        _cache[block] = box;
                    }

                    // Column break: if this block won't fit in the column's remaining height and another
                    // column is available, move to the next. Whole-block packing (no mid-paragraph split):
                    // a block taller than a full column stays put and overflows rather than splitting.
                    if (col < colCount - 1 && colTop > mt && colTop + box.Height > mt + columnHeight)
                    {
                        col++;
                        colTop = mt;
                    }

                    double dx = col * (colW + colGap);
                    foreach (Item it in box.Items)
                    {
                        var abs = new Point(it.Origin.X + dx, it.Origin.Y + colTop);
                        _draw.Add((it.Text, abs));
                        if (it.Para != null) _paras.Add(new ParaLayout(it.Para, it.Text, abs, it.Length));
                        double r = it.Right + dx;
                        if (r > maxRight) maxRight = r;
                    }
                    colTop += box.Height;
                    if (colTop > bottomMost) bottomMost = colTop;
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
            double height = Math.Max(bottomMost + mb, mt + mb + 16);   // slack so an empty doc keeps a caret-height page
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

        // ---- per-line (paginated) layout ----------------------------------------------------------
        // Lays each paragraph out line-by-line (own word-level line breaking + justification), producing a
        // flat list of LineBoxes that pack into columns so a column fills completely before overflowing.

        private readonly struct WordPos
        {
            public readonly FormattedText Ft;
            public readonly double X;         // relative to the column's left edge
            public readonly double Baseline;  // this word's ascent (top -> baseline)
            public WordPos(FormattedText ft, double x, double baseline) { Ft = ft; X = x; Baseline = baseline; }
        }

        private sealed class LineBox
        {
            public readonly List<WordPos> Words = new();
            public double Height;     // full line-box height (incl. folded top/bottom margin)
            public double TopPad;     // blank space above the text within the box (folded top margin)
            public double Baseline;   // line baseline from the top of the text (max word ascent)
            public double Right;      // rightmost word extent (column-relative)
            public bool MarginOnly;   // pure spacer -> dropped at a column top
            public Paragraph Para;
            public bool ParaStart;
        }

        private readonly struct Tok
        {
            public readonly string Word;   // null => hard line break
            public readonly RunFmt Fmt;
            public Tok(string w, RunFmt f) { Word = w; Fmt = f; }
        }

        private void FormatPaginated(FlowDocument document, HashSet<Block> dirty,
            double ml, double mr, double mt, double mb, double colW, double colGap, int colCount,
            double columnHeight, double pageWidth)
        {
            var lines = new List<LineBox>();
            if (document != null)
            {
                var present = new HashSet<Block>();
                foreach (Block b in document.Blocks)
                {
                    present.Add(b);
                    if (!_lineCache.TryGetValue(b, out List<LineBox> bl) || (dirty != null && dirty.Contains(b)))
                    {
                        bl = new List<LineBox>();
                        BuildBlockLines(b, colW, 0, bl);
                        _lineCache[b] = bl;
                    }
                    lines.AddRange(bl);
                }
                if (_lineCache.Count > present.Count)
                {
                    var stale = new List<Block>();
                    foreach (Block b in _lineCache.Keys) if (!present.Contains(b)) stale.Add(b);
                    foreach (Block b in stale) _lineCache.Remove(b);
                }
            }

            double maxRight = ml;
            int col = 0;
            double colTop = mt, bottom = mt;
            foreach (LineBox line in lines)
            {
                if (line.MarginOnly && DoubleClose(colTop, mt)) continue;   // collapse leading margin at a column top
                if (col < colCount - 1 && colTop > mt && colTop + line.Height > mt + columnHeight)
                {
                    col++; colTop = mt;
                    if (line.MarginOnly) continue;
                }
                double colX = ml + col * (colW + colGap);
                double textTop = colTop + line.TopPad;
                foreach (WordPos w in line.Words)
                    _draw.Add((w.Ft, new Point(colX + w.X, textTop + (line.Baseline - w.Baseline))));
                if (line.Words.Count > 0)
                {
                    double r = colX + line.Right; if (r > maxRight) maxRight = r;
                    // Best-effort text view: one ParaLayout per paragraph at its first line (the viewer is
                    // read-only; per-line/across-column hit-testing is not modelled).
                    if (line.ParaStart && line.Para != null)
                        _paras.Add(new ParaLayout(line.Para, line.Words[0].Ft, new Point(colX + line.Words[0].X, textTop), 0));
                }
                colTop += line.Height;
                if (colTop > bottom) bottom = colTop;
            }

            double width = pageWidth > 0 ? pageWidth : maxRight + mr;
            double height = Math.Max(bottom + mb, mt + mb + 16);
            Size = new Size(Fin(width, 0), Fin(height, mt + mb + 16));
        }

        private void BuildBlockLines(Block block, double colW, double extraLeft, List<LineBox> outLines)
        {
            switch (block)
            {
                case Paragraph p: BuildParagraphLines(p, colW, extraLeft, outLines); break;
                case System.Windows.Documents.Section s: foreach (Block b in s.Blocks) BuildBlockLines(b, colW, extraLeft, outLines); break;
                case List list: BuildListLines(list, colW, extraLeft, outLines); break;
                // Table / BlockUIContainer / other: not modelled
            }
        }

        private void BuildListLines(List list, double colW, double extraLeft, List<LineBox> outLines)
        {
            const double marker = 18.0;
            int index = list.StartIndex;
            foreach (ListItem li in list.ListItems)
            {
                int startIdx = outLines.Count;
                foreach (Block b in li.Blocks) BuildBlockLines(b, colW, extraLeft + marker, outLines);
                if (outLines.Count > startIdx)
                {
                    string mark = list.MarkerStyle == System.Windows.TextMarkerStyle.Decimal ? (index + ".") : "•";
                    var ft = new FormattedText(mark, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                        new Typeface("Segoe UI"), 12.0, Brushes.Black, DefaultPixelsPerDip);
                    outLines[startIdx].Words.Insert(0, new WordPos(ft, extraLeft, Fin(ft.Baseline)));
                }
                index++;
            }
        }

        private void BuildParagraphLines(Paragraph paragraph, double colW, double extraLeft, List<LineBox> outLines)
        {
            double mTop = Fin(paragraph.Margin.Top), mLeft = Fin(paragraph.Margin.Left) + extraLeft;
            double mRight = Fin(paragraph.Margin.Right), mBottom = Fin(paragraph.Margin.Bottom);
            double avail = Math.Max(0, colW - mLeft - mRight);
            double indent = Fin(paragraph.TextIndent);
            double lineHeight = paragraph.LineHeight;
            TextAlignment align = paragraph.TextAlignment;

            // Figures/Floaters (AnchoredBlock inlines) carry blocks (e.g. the title) -> stack them first.
            BuildAnchoredBlockLines(paragraph.Inlines, colW, outLines);

            int firstLineIdx = outLines.Count;   // paragraph text starts here (margins fold into these lines)

            var toks = new List<Tok>();
            CollectWords(paragraph.Inlines, toks);

            var cur = new List<(FormattedText Ft, double W, double Baseline, double SpaceW)>();
            double curWidth = indent;   // first line carries the text indent
            bool firstLine = true;

            void Flush(bool ragged)
            {
                EmitParagraphLine(cur, avail, mLeft, firstLine ? indent : 0, align, ragged, lineHeight, paragraph, firstLine, outLines);
                cur.Clear();
                curWidth = 0;
                firstLine = false;
            }

            foreach (Tok t in toks)
            {
                if (t.Word == null) { Flush(true); continue; }   // hard break
                FormattedText ft = MakeWord(t.Word, t.Fmt);
                double ww = Fin(ft.WidthIncludingTrailingWhitespace);
                double sp = SpaceWidth(t.Fmt);
                double add = cur.Count == 0 ? ww : sp + ww;
                if (cur.Count > 0 && curWidth + add > avail + 0.5)
                {
                    Flush(false);
                    cur.Add((ft, ww, Fin(ft.Baseline), sp));
                    curWidth = ww;
                }
                else
                {
                    cur.Add((ft, ww, Fin(ft.Baseline), sp));
                    curWidth += add;
                }
            }
            if (cur.Count > 0) Flush(true);

            // Fold the paragraph's top/bottom margins into its first/last text line.
            if (outLines.Count > firstLineIdx)
            {
                LineBox first = outLines[firstLineIdx];
                first.TopPad += mTop; first.Height += mTop;
                outLines[outLines.Count - 1].Height += mBottom;
            }
            else if (mTop + mBottom > 0)
            {
                outLines.Add(new LineBox { Height = mTop + mBottom, MarginOnly = true });
            }
        }

        private void BuildAnchoredBlockLines(InlineCollection inlines, double colW, List<LineBox> outLines)
        {
            foreach (Inline inline in inlines)
            {
                switch (inline)
                {
                    case System.Windows.Documents.AnchoredBlock anchored:
                        foreach (Block b in anchored.Blocks) BuildBlockLines(b, colW, 0, outLines);
                        break;
                    case System.Windows.Documents.Span span:
                        BuildAnchoredBlockLines(span.Inlines, colW, outLines);
                        break;
                }
            }
        }

        private void EmitParagraphLine(List<(FormattedText Ft, double W, double Baseline, double SpaceW)> words,
            double avail, double mLeft, double lineIndent, TextAlignment align, bool ragged,
            double lineHeight, Paragraph para, bool paraStart, List<LineBox> outLines)
        {
            var lb = new LineBox { Para = para, ParaStart = paraStart };
            if (words.Count == 0)
            {
                double h0 = (double.IsFinite(lineHeight) && lineHeight > 0) ? lineHeight : 14.0;
                lb.Height = h0; lb.Baseline = h0 * 0.8;
                outLines.Add(lb);
                return;
            }

            double naturalWords = 0, naturalSpaces = 0, maxH = 0, maxBase = 0;
            for (int i = 0; i < words.Count; i++)
            {
                naturalWords += words[i].W;
                if (i < words.Count - 1) naturalSpaces += words[i].SpaceW;
                double h = Fin(words[i].Ft.Height); if (h > maxH) maxH = h;
                if (words[i].Baseline > maxBase) maxBase = words[i].Baseline;
            }
            double natural = lineIndent + naturalWords + naturalSpaces;
            double extra = avail - natural;
            int gaps = words.Count - 1;
            bool justify = align == TextAlignment.Justify && !ragged && gaps > 0 && extra > 0;
            double gapExtra = justify ? extra / gaps : 0;

            double startX = mLeft + lineIndent;
            if (!justify && extra > 0)
            {
                if (align == TextAlignment.Right) startX = mLeft + lineIndent + extra;
                else if (align == TextAlignment.Center) startX = mLeft + lineIndent + extra / 2;
            }

            double x = startX;
            for (int i = 0; i < words.Count; i++)
            {
                lb.Words.Add(new WordPos(words[i].Ft, x, words[i].Baseline));
                x += words[i].W;
                if (i < gaps) x += words[i].SpaceW + gapExtra;
            }
            lb.Right = x;
            lb.Baseline = maxBase > 0 ? maxBase : maxH * 0.8;
            lb.Height = Math.Max((double.IsFinite(lineHeight) && lineHeight > 0) ? lineHeight : maxH, maxH);
            outLines.Add(lb);
        }

        private static void CollectWords(InlineCollection inlines, List<Tok> toks)
        {
            foreach (Inline inline in inlines)
            {
                switch (inline)
                {
                    case Run run:
                    {
                        RunFmt f = RunFmtFor(run, 0, 0);
                        string s = run.Text ?? string.Empty;
                        int i = 0;
                        while (i < s.Length)
                        {
                            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
                            int start = i;
                            while (i < s.Length && !char.IsWhiteSpace(s[i])) i++;
                            if (i > start) toks.Add(new Tok(s.Substring(start, i - start), f));
                        }
                        break;
                    }
                    case LineBreak:
                        toks.Add(new Tok(null, default));
                        break;
                    case System.Windows.Documents.AnchoredBlock:
                        break;   // laid out separately by BuildAnchoredBlockLines
                    case System.Windows.Documents.Span span:
                        CollectWords(span.Inlines, toks);
                        break;
                }
            }
        }

        private FormattedText MakeWord(string text, RunFmt f)
        {
            var typeface = new Typeface(f.Family ?? new FontFamily("Segoe UI"), f.Style, f.Weight, FontStretches.Normal);
            var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                typeface, f.Size <= 0 ? 12.0 : f.Size, f.Foreground ?? Brushes.Black, DefaultPixelsPerDip);
            if (f.Underline) ft.SetTextDecorations(TextDecorations.Underline);
            return ft;
        }

        private double SpaceWidth(RunFmt f)
        {
            double size = f.Size <= 0 ? 12.0 : f.Size;
            var key = (f.Family, size, f.Weight, f.Style);
            if (!_spaceWidth.TryGetValue(key, out double w))
            {
                var typeface = new Typeface(f.Family ?? new FontFamily("Segoe UI"), f.Style, f.Weight, FontStretches.Normal);
                var ft = new FormattedText(" ", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                    typeface, size, Brushes.Black, DefaultPixelsPerDip);
                w = Fin(ft.WidthIncludingTrailingWhitespace);
                if (w <= 0) w = size * 0.28;
                _spaceWidth[key] = w;
            }
            return w;
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

            // Figures/Floaters (AnchoredBlock inlines) carry block content — e.g. this document's
            // "Cocoa & Chocolate" title lives in a Figure. We don't float/wrap them; lay their blocks
            // out stacked at the current position so the content is at least visible and correctly styled.
            y = AppendAnchoredBlocks(paragraph.Inlines, box, x, w, y);

            FormattedText ft = BuildParagraphText(paragraph, w, out int textLen);
            if (ft != null)
            {
                box.Items.Add(new Item(ft, new Point(x, y), x + Fin(ft.WidthIncludingTrailingWhitespace), paragraph, textLen));
                y += Fin(ft.Height);
            }
            return y + mBottom;
        }

        // Lay out the block content of any Figure/Floater (AnchoredBlock) inlines in a paragraph. These are
        // not floated here; they stack in document order (BuildParagraphText's CollectInlines skips them, so
        // their text is not also duplicated inline).
        private double AppendAnchoredBlocks(InlineCollection inlines, BlockBox box, double left, double width, double y)
        {
            foreach (Inline inline in inlines)
            {
                switch (inline)
                {
                    case System.Windows.Documents.AnchoredBlock anchored:
                        foreach (Block b in anchored.Blocks)
                            y = AppendBlock(b, box, left, width, y, 0);
                        break;
                    case System.Windows.Documents.Span span:   // a Figure may be nested inside a Bold/Span
                        y = AppendAnchoredBlocks(span.Inlines, box, left, width, y);
                        break;
                }
            }
            return y;
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
