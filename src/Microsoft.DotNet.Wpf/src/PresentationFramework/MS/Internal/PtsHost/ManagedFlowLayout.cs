// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The fully-managed FlowDocument layout: the replacement for the native PTS engine
// (PresentationNative_cor3.dll), which this port does not ship on any platform. It
// takes over PTS's paragraph/track/page machinery so RichTextBox / FlowDocument*
// controls render. Each Paragraph is laid out with FormattedText (which runs on the
// fork's managed text stack), giving wrapping + per-run font/weight/style/colour.
//
// Two layout paths, chosen by the caller:
//   * bottomless (RichTextBox / scroll viewer) - whole-paragraph boxes stacked in one
//     column, cached per block;
//   * paginated (FlowDocumentPageViewer / DocumentViewer) - per-line boxes packed into
//     finite columns, with justification.
//
// Block coverage: Paragraph, Section, List, Table (columns, column/row spans, cell
// padding, borders and backgrounds) and BlockUIContainer (a hosted UIElement, laid out
// through a UIElementIsland so it renders and takes input like an element anywhere else).
//
// Layout is INCREMENTAL: the laid-out box of each top-level block (its FormattedText
// + height) is cached by block reference. On a content change only the blocks the
// caller reports dirty (mapped from StructuralCache's dirty text ranges) are
// re-measured; every other block reuses its cached FormattedText and is merely
// repositioned, so typing does not re-lay-out the whole document.
//
// Not modelled: floating figures/floaters are stacked in place rather than floated, a
// table does not split across a column boundary, and editing hit-testing is
// paragraph-level. This is layout + rendering, not every last PTS behaviour.
//

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using MS.Internal.Documents;   // UIElementIsland

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
            // Cell backgrounds and borders: everything in a block that is drawn but is not text.
            // Kept separate from Items because they paint UNDER the text and carry no caret/hit-test
            // meaning, and because most blocks have none at all.
            public readonly List<Deco> Decos = new();
            // UIElements the document hosts (BlockUIContainer / InlineUIContainer). These are not
            // drawn here at all: they are real elements, so they are parented into the page's visual
            // tree and arranged at these bounds, which is what makes them render AND take input.
            public readonly List<HostedBox> Hosted = new();
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

        // A filled and/or stroked rectangle, positioned like an Item (X absolute, Y relative to the
        // block top). Drawn before the text of the same block.
        private readonly struct Deco
        {
            public readonly Rect Bounds;
            public readonly Brush Fill;
            public readonly Brush Stroke;
            public readonly Thickness StrokeThickness;
            public Deco(Rect bounds, Brush fill, Brush stroke, Thickness strokeThickness)
            { Bounds = bounds; Fill = fill; Stroke = stroke; StrokeThickness = strokeThickness; }
        }

        // A UIElement the document hosts, and the box layout gave it. Positioned like a Deco while
        // it lives in a BlockBox; absolute once it reaches HostedElements.
        //
        // The element is wrapped in a UIElementIsland -- WPF's own container for an element embedded
        // in a document -- rather than being parented raw. The island is a ContainerVisual, which is
        // what the page's visual tree expects of its children (DestroyVisualLinks asserts on it), and
        // it owns the element's measure/arrange so the element behaves like it does anywhere else.
        internal readonly struct HostedBox
        {
            public readonly UIElementIsland Island;
            public readonly Rect Bounds;
            public HostedBox(UIElementIsland island, Rect bounds) { Island = island; Bounds = bounds; }
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
        private readonly List<Deco> _drawDecos = new();
        private readonly List<HostedBox> _hosted = new();
        private readonly List<ParaLayout> _paras = new();
        private double _lastWidth = double.NaN;

        /// <summary>Paragraph layouts (absolute), in document order — for the managed text view.</summary>
        internal IReadOnlyList<ParaLayout> Paragraphs => _paras;

        /// <summary>
        /// The UIElements this layout placed, with their absolute page bounds. The page parents them
        /// into its visual tree and arranges them there; they are elements, not drawings, so nothing
        /// in Render touches them.
        /// </summary>
        internal IReadOnlyList<HostedBox> HostedElements => _hosted;

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
            _drawDecos.Clear();
            _hosted.Clear();
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
                    foreach (Deco d in box.Decos)
                    {
                        var r = new Rect(d.Bounds.X + dx, d.Bounds.Y + colTop, d.Bounds.Width, d.Bounds.Height);
                        _drawDecos.Add(new Deco(r, d.Fill, d.Stroke, d.StrokeThickness));
                    }
                    foreach (HostedBox h in box.Hosted)
                    {
                        _hosted.Add(new HostedBox(h.Island,
                            new Rect(h.Bounds.X + dx, h.Bounds.Y + colTop, h.Bounds.Width, h.Bounds.Height)));
                    }
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
            // Decorations first: cell backgrounds and rules paint under the text they belong to.
            foreach (Deco d in _drawDecos)
                DrawDeco(dc, d);

            foreach (var d in _draw)
                dc.DrawText(d.Text, d.Origin);
        }

        // A cell's fill and its four edges. The edges are drawn as filled rectangles rather than with
        // a Pen so that each side can have its own thickness (Thickness is per-edge, a Pen is not) and
        // so the stroke sits INSIDE the cell bounds, which is where WPF puts a border.
        private static void DrawDeco(DrawingContext dc, Deco d)
        {
            Rect b = d.Bounds;
            if (b.Width <= 0 || b.Height <= 0) return;

            if (d.Fill != null)
                dc.DrawRectangle(d.Fill, null, b);

            if (d.Stroke == null) return;
            Thickness t = d.StrokeThickness;
            double left = Math.Max(0, t.Left), top = Math.Max(0, t.Top);
            double right = Math.Max(0, t.Right), bottom = Math.Max(0, t.Bottom);

            if (top > 0) dc.DrawRectangle(d.Stroke, null, new Rect(b.X, b.Y, b.Width, Math.Min(top, b.Height)));
            if (bottom > 0) dc.DrawRectangle(d.Stroke, null, new Rect(b.X, b.Bottom - Math.Min(bottom, b.Height), b.Width, Math.Min(bottom, b.Height)));
            if (left > 0) dc.DrawRectangle(d.Stroke, null, new Rect(b.X, b.Y, Math.Min(left, b.Width), b.Height));
            if (right > 0) dc.DrawRectangle(d.Stroke, null, new Rect(b.Right - Math.Min(right, b.Width), b.Y, Math.Min(right, b.Width), b.Height));
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
            // Non-text drawing for this box (table cell fills and rules), positioned like Words:
            // X column-relative, Y relative to the box's text top.
            public List<Deco> Decos;
            // Hosted UIElements in this box, positioned the same way.
            public List<HostedBox> Hosted;
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
            public readonly string Word;   // null => hard line break, or a hosted element when Host is set
            public readonly RunFmt Fmt;
            public readonly InlineUIContainer Host;   // non-null => an inline element occupies this slot
            public Tok(string w, RunFmt f) { Word = w; Fmt = f; Host = null; }
            public Tok(InlineUIContainer host, RunFmt f) { Word = null; Fmt = f; Host = host; }
        }

        // One packed item on a line under construction: either a shaped word or a hosted element.
        private readonly struct WordItem
        {
            public readonly FormattedText Ft;     // null for a hosted element
            public readonly double W;             // advance width
            public readonly double Baseline;      // ascent; for an element, its full height (it sits ON the baseline)
            public readonly double SpaceW;        // width of the space that follows it
            public readonly UIElementIsland Island;
            public readonly Size IslandSize;

            public WordItem(FormattedText ft, double w, double baseline, double spaceW)
            { Ft = ft; W = w; Baseline = baseline; SpaceW = spaceW; Island = null; IslandSize = default; }

            public WordItem(UIElementIsland island, Size size, double spaceW)
            { Ft = null; W = size.Width; Baseline = size.Height; SpaceW = spaceW; Island = island; IslandSize = size; }

            public double Height => Ft != null ? Fin(Ft.Height) : IslandSize.Height;
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
                if (line.Decos != null)
                {
                    foreach (Deco d in line.Decos)
                        _drawDecos.Add(new Deco(new Rect(colX + d.Bounds.X, textTop + d.Bounds.Y, d.Bounds.Width, d.Bounds.Height),
                                                d.Fill, d.Stroke, d.StrokeThickness));
                }
                if (line.Hosted != null)
                {
                    foreach (HostedBox h in line.Hosted)
                        _hosted.Add(new HostedBox(h.Island,
                            new Rect(colX + h.Bounds.X, textTop + h.Bounds.Y, h.Bounds.Width, h.Bounds.Height)));
                }
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
                case Table table: BuildTableLines(table, colW, extraLeft, outLines); break;
                case BlockUIContainer host: BuildHostedLines(host, colW, extraLeft, outLines); break;
            }
        }

        // A hosted UIElement enters the paginated path as its own box, so the line packer moves it to
        // the next column whole rather than slicing it.
        private void BuildHostedLines(BlockUIContainer host, double colW, double extraLeft, List<LineBox> outLines)
        {
            var box = new BlockBox();
            box.Height = AppendBlockUIContainer(host, box, 0, Math.Max(0, colW - extraLeft), 0);
            if (box.Hosted.Count == 0) return;

            var line = new LineBox { Height = box.Height, TopPad = 0, Baseline = 0, Hosted = new List<HostedBox>(box.Hosted.Count) };
            double right = 0;
            foreach (HostedBox h in box.Hosted)
            {
                line.Hosted.Add(new HostedBox(h.Island,
                    new Rect(h.Bounds.X + extraLeft, h.Bounds.Y, h.Bounds.Width, h.Bounds.Height)));
                if (h.Bounds.Right + extraLeft > right) right = h.Bounds.Right + extraLeft;
            }
            line.Right = right;
            outLines.Add(line);
        }

        // A table enters the paginated (line-packing) path as ONE atomic box.
        //
        // The line packer moves a box to the next column when it does not fit, which for a table
        // means the whole table moves rather than being split across a column boundary -- the same
        // whole-block rule the bottomless path already applies to any block. Splitting a table
        // between columns needs per-row break records, which this engine does not model.
        //
        // The cell layout itself is the ordinary block-box code, replayed into this box: a word's Y
        // is textTop + (line.Baseline - word.Baseline), so a zero line baseline and a NEGATIVE word
        // baseline places each cell's text at its own offset down the box.
        private void BuildTableLines(Table table, double colW, double extraLeft, List<LineBox> outLines)
        {
            var box = new BlockBox();
            double width = Math.Max(0, colW - extraLeft);
            box.Height = AppendTable(table, box, 0, width, 0, 0);
            if (box.Items.Count == 0 && box.Decos.Count == 0 && box.Hosted.Count == 0) return;

            var line = new LineBox
            {
                Height = box.Height,
                TopPad = 0,
                Baseline = 0,
                MarginOnly = false,
            };

            double right = 0;
            foreach (Item it in box.Items)
            {
                line.Words.Add(new WordPos(it.Text, it.Origin.X + extraLeft, -it.Origin.Y));
                if (it.Right + extraLeft > right) right = it.Right + extraLeft;
            }
            line.Right = right;

            if (box.Decos.Count > 0)
            {
                line.Decos = new List<Deco>(box.Decos.Count);
                foreach (Deco d in box.Decos)
                    line.Decos.Add(new Deco(new Rect(d.Bounds.X + extraLeft, d.Bounds.Y, d.Bounds.Width, d.Bounds.Height),
                                            d.Fill, d.Stroke, d.StrokeThickness));
            }

            // A cell can host a UIElement of its own; it rides along in the table's box.
            if (box.Hosted.Count > 0)
            {
                line.Hosted = new List<HostedBox>(box.Hosted.Count);
                foreach (HostedBox h in box.Hosted)
                    line.Hosted.Add(new HostedBox(h.Island,
                        new Rect(h.Bounds.X + extraLeft, h.Bounds.Y, h.Bounds.Width, h.Bounds.Height)));
            }

            outLines.Add(line);
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

            var cur = new List<WordItem>();
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
                WordItem item;
                if (t.Host != null)
                {
                    // An inline element packs like a word of its own width, so the text around it
                    // flows past it and it wraps to the next line when it no longer fits.
                    UIElement child = t.Host.Child;
                    if (child == null) continue;
                    UIElementIsland island = IslandFor(child);
                    Size size = LayoutIsland(island, new Size(avail > 0 ? avail : double.PositiveInfinity, double.PositiveInfinity),
                                             horizontalAutoSize: true);
                    item = new WordItem(island, size, SpaceWidth(t.Fmt));
                }
                else
                {
                    if (t.Word == null) { Flush(true); continue; }   // hard break
                    FormattedText ft = MakeWord(t.Word, t.Fmt);
                    item = new WordItem(ft, Fin(ft.WidthIncludingTrailingWhitespace), Fin(ft.Baseline), SpaceWidth(t.Fmt));
                }

                double add = cur.Count == 0 ? item.W : item.SpaceW + item.W;
                if (cur.Count > 0 && curWidth + add > avail + 0.5)
                {
                    Flush(false);
                    cur.Add(item);
                    curWidth = item.W;
                }
                else
                {
                    cur.Add(item);
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

        private void EmitParagraphLine(List<WordItem> words,
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
                double h = words[i].Height; if (h > maxH) maxH = h;
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
                if (words[i].Island != null)
                {
                    // Positioned exactly as a word is (see the render loop: y = textTop + Baseline -
                    // item baseline), which for an element whose "ascent" is its full height means it
                    // stands on the line's baseline.
                    (lb.Hosted ??= new List<HostedBox>()).Add(
                        new HostedBox(words[i].Island, new Rect(x, 0, words[i].IslandSize.Width, words[i].IslandSize.Height)));
                }
                else
                {
                    lb.Words.Add(new WordPos(words[i].Ft, x, words[i].Baseline));
                }
                x += words[i].W;
                if (i < gaps) x += words[i].SpaceW + gapExtra;
            }
            lb.Right = x;
            lb.Baseline = maxBase > 0 ? maxBase : maxH * 0.8;

            // The hosted boxes were collected before the baseline was known; drop them onto it now.
            if (lb.Hosted != null)
            {
                for (int i = 0; i < lb.Hosted.Count; i++)
                {
                    Rect b = lb.Hosted[i].Bounds;
                    lb.Hosted[i] = new HostedBox(lb.Hosted[i].Island,
                        new Rect(b.X, lb.Baseline - b.Height, b.Width, b.Height));
                }
            }
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
                        toks.Add(new Tok((string)null, default));
                        break;
                    case System.Windows.Documents.AnchoredBlock:
                        break;   // laid out separately by BuildAnchoredBlockLines
                    case InlineUIContainer host:
                        toks.Add(new Tok(host, RunFmtFor(host, 0, 0)));
                        break;
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
                case Table table:
                    return AppendTable(table, box, left, width, y, listDepth);
                case BlockUIContainer host:
                    return AppendBlockUIContainer(host, box, left, width, y);
                default:
                    return y;
            }
        }

        // A block that hosts a UIElement. The element is laid out HERE, during document layout, so the
        // document reserves its real height and the blocks below it sit in the right place; the page
        // then parents its island and positions it at these bounds. There is no other order available:
        // the height is not knowable until the element has seen its available width.
        private double AppendBlockUIContainer(BlockUIContainer host, BlockBox box, double left, double width, double y)
        {
            double mTop = Fin(host.Margin.Top), mLeft = Fin(host.Margin.Left);
            double mRight = Fin(host.Margin.Right), mBottom = Fin(host.Margin.Bottom);
            y += mTop;

            UIElement child = host.Child;
            if (child == null) return y + mBottom;

            UIElementIsland island = IslandFor(child);
            double availableWidth = width > 0 ? Math.Max(0, width - mLeft - mRight) : double.PositiveInfinity;

            // A BlockUIContainer stretches across the column, so the width is imposed rather than
            // asked for (horizontalAutoSize: false) and only the height comes from the element. With
            // no column width to impose -- a bottomless measure -- fall back to asking for both.
            bool haveWidth = double.IsFinite(availableWidth) && availableWidth > 0;
            Size islandSize = LayoutIsland(island,
                new Size(haveWidth ? availableWidth : double.PositiveInfinity, double.PositiveInfinity),
                horizontalAutoSize: !haveWidth);

            box.Hosted.Add(new HostedBox(island, new Rect(left + mLeft, y, islandSize.Width, islandSize.Height)));
            return y + islandSize.Height + mBottom;
        }

        // One island per hosted element, kept for the life of the layout: recreating it would reparent
        // the element on every reflow, which restarts its animations and drops its focus.
        private readonly Dictionary<UIElement, UIElementIsland> _islands = new();

        private UIElementIsland IslandFor(UIElement child)
        {
            if (!_islands.TryGetValue(child, out UIElementIsland island))
            {
                island = new UIElementIsland(child);
                _islands[child] = island;
            }
            return island;
        }

        // Guarded because a hosted UIElement is arbitrary application code, and a throwing Measure or
        // Arrange must not take the whole document's layout down with it.
        private static Size LayoutIsland(UIElementIsland island, Size available, bool horizontalAutoSize)
        {
            try
            {
                Size s = island.DoLayout(available, horizontalAutoSize, verticalAutoSize: true);
                return new Size(Fin(s.Width), Fin(s.Height));
            }
            catch (Exception e) when (!IsCriticalException(e))
            {
                return new Size(0, 0);
            }
        }

        private static bool IsCriticalException(Exception e)
            => e is OutOfMemoryException or StackOverflowException or System.Threading.ThreadAbortException;

        // ---- tables ----------------------------------------------------------------------------
        //
        // A grid of cells, laid out top-down: resolve the column widths once, then for each row lay
        // every cell's blocks out at that column's x and the row's top, and take the row height from
        // the tallest cell. Cells are top-aligned, which is WPF's default.
        //
        // Row spans are tracked as occupancy so the columns of later rows still line up (the common
        // reason a spanned table looks scrambled), and the spanned row is grown to contain the cell.
        //
        private double AppendTable(Table table, BlockBox box, double left, double width, double y, int listDepth)
        {
            double mTop = Fin(table.Margin.Top), mLeft = Fin(table.Margin.Left);
            double mRight = Fin(table.Margin.Right), mBottom = Fin(table.Margin.Bottom);
            y += mTop;
            double tableLeft = left + mLeft;
            double tableWidth = width > 0 ? Math.Max(0, width - mLeft - mRight) : 0;

            double spacing = Fin(table.CellSpacing);
            if (spacing < 0) spacing = 0;

            int columnCount = ColumnCount(table);
            if (columnCount == 0) return y + mBottom;

            double[] widths = ResolveColumnWidths(table, columnCount, tableWidth, spacing);
            double[] x = new double[columnCount];
            double runningX = tableLeft;
            for (int c = 0; c < columnCount; c++)
            {
                x[c] = runningX;
                runningX += widths[c] + spacing;
            }

            // Per-column count of rows still covered by a cell that started above, and how far down
            // that cell reaches, so a later row can be pushed past it.
            int[] spanLeft = new int[columnCount];
            double[] spanBottom = new double[columnCount];

            double top = y;
            foreach (TableRowGroup group in table.RowGroups)
            {
                foreach (TableRow row in group.Rows)
                {
                    double rowTop = top;
                    // A row cannot start above a cell that spans into it from an earlier row.
                    for (int c = 0; c < columnCount; c++)
                        if (spanLeft[c] > 0 && spanBottom[c] > rowTop) rowTop = spanBottom[c];

                    double rowBottom = rowTop;
                    int col = 0;
                    foreach (TableCell cell in row.Cells)
                    {
                        while (col < columnCount && spanLeft[col] > 0) col++;
                        if (col >= columnCount) break;

                        int span = Math.Max(1, cell.ColumnSpan);
                        if (col + span > columnCount) span = columnCount - col;

                        double cellWidth = 0;
                        for (int k = 0; k < span; k++) cellWidth += widths[col + k];
                        cellWidth += spacing * (span - 1);

                        double cellBottom = AppendCell(cell, box, x[col], cellWidth, rowTop, listDepth);

                        int rowSpan = Math.Max(1, cell.RowSpan);
                        if (rowSpan > 1)
                        {
                            // Reserve the columns for the rows below, and remember where the cell ends
                            // so those rows start clear of it.
                            for (int k = 0; k < span; k++)
                            {
                                spanLeft[col + k] = rowSpan - 1;
                                spanBottom[col + k] = cellBottom;
                            }
                        }
                        else if (cellBottom > rowBottom)
                        {
                            rowBottom = cellBottom;
                        }

                        col += span;
                    }

                    // An all-spanned row still advances, or the table would collapse onto itself.
                    if (rowBottom <= rowTop) rowBottom = rowTop;

                    for (int c = 0; c < columnCount; c++)
                    {
                        if (spanLeft[c] > 0)
                        {
                            spanLeft[c]--;
                            // The last row a cell spans has to contain it.
                            if (spanLeft[c] == 0 && spanBottom[c] > rowBottom) rowBottom = spanBottom[c];
                        }
                    }

                    top = rowBottom + spacing;
                }
            }

            // Trim the trailing inter-row spacing, which is between rows and not after the last one.
            if (top > y && spacing > 0) top -= spacing;
            return top + mBottom;
        }

        // Lay one cell's blocks out inside its column, inset by the cell's border and padding, and
        // emit its background/border behind them. Returns the cell's bottom.
        private double AppendCell(TableCell cell, BlockBox box, double cellX, double cellWidth, double cellTop, int listDepth)
        {
            Thickness border = cell.BorderThickness;
            Thickness pad = cell.Padding;
            double insetLeft = Fin(border.Left) + Fin(pad.Left);
            double insetRight = Fin(border.Right) + Fin(pad.Right);
            double insetTop = Fin(border.Top) + Fin(pad.Top);
            double insetBottom = Fin(border.Bottom) + Fin(pad.Bottom);

            double contentWidth = Math.Max(0, cellWidth - insetLeft - insetRight);
            double contentTop = cellTop + insetTop;

            double contentBottom = contentTop;
            foreach (Block b in cell.Blocks)
                contentBottom = AppendBlock(b, box, cellX + insetLeft, contentWidth, contentBottom, listDepth);

            double cellBottom = contentBottom + insetBottom;

            var background = cell.Background;
            var borderBrush = cell.BorderBrush;
            bool hasBorder = borderBrush != null &&
                             (Fin(border.Left) > 0 || Fin(border.Top) > 0 || Fin(border.Right) > 0 || Fin(border.Bottom) > 0);
            if (background != null || hasBorder)
            {
                double h = Math.Max(0, cellBottom - cellTop);
                box.Decos.Add(new Deco(new Rect(cellX, cellTop, cellWidth, h),
                                       background, hasBorder ? borderBrush : null, border));
            }

            return cellBottom;
        }

        /// <summary>Widest row's cell count, which is the table's column count.</summary>
        private static int ColumnCount(Table table)
        {
            int declared = table.Columns.Count;
            int widest = 0;
            foreach (TableRowGroup group in table.RowGroups)
            {
                foreach (TableRow row in group.Rows)
                {
                    int n = 0;
                    foreach (TableCell cell in row.Cells) n += Math.Max(1, cell.ColumnSpan);
                    if (n > widest) widest = n;
                }
            }
            return Math.Max(declared, widest);
        }

        // Absolute widths are taken as given; Star shares what is left in proportion; Auto behaves as
        // one Star, because measuring content to fit a column needs a second layout pass that this
        // engine does not do. Columns beyond the declared ones are Star too.
        private static double[] ResolveColumnWidths(Table table, int columnCount, double tableWidth, double spacing)
        {
            var widths = new double[columnCount];
            double available = tableWidth > 0 ? Math.Max(0, tableWidth - spacing * (columnCount - 1)) : 0;

            double fixedTotal = 0, starTotal = 0;
            var isStar = new bool[columnCount];

            for (int c = 0; c < columnCount; c++)
            {
                GridLength len = c < table.Columns.Count ? table.Columns[c].Width : GridLength.Auto;
                if (len.IsAbsolute && len.Value > 0)
                {
                    widths[c] = len.Value;
                    fixedTotal += widths[c];
                }
                else
                {
                    isStar[c] = true;
                    starTotal += len.IsStar && len.Value > 0 ? len.Value : 1.0;
                }
            }

            if (starTotal <= 0) return widths;

            // No usable width (a bottomless measure with no page width): give the star columns a
            // workable default rather than zero, which would swallow their text entirely.
            double remaining = available > 0 ? Math.Max(0, available - fixedTotal) : 0;
            if (remaining <= 0 && available <= 0) remaining = starTotal * DefaultStarColumnWidth;

            for (int c = 0; c < columnCount; c++)
            {
                if (!isStar[c]) continue;
                GridLength len = c < table.Columns.Count ? table.Columns[c].Width : GridLength.Auto;
                double weight = len.IsStar && len.Value > 0 ? len.Value : 1.0;
                widths[c] = remaining * (weight / starTotal);
            }
            return widths;
        }

        private const double DefaultStarColumnWidth = 96.0;

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

            FormattedText ft = BuildParagraphText(paragraph, w, out int textLen, out List<InlineHost> inlineHosts);
            if (ft != null)
            {
                box.Items.Add(new Item(ft, new Point(x, y), x + Fin(ft.WidthIncludingTrailingWhitespace), paragraph, textLen));

                // Place any InlineUIContainer at the run of blank characters reserved for it: the
                // character rectangle is where the text engine actually put that gap, so the element
                // lands where it would have been drawn had FormattedText been able to draw it.
                foreach (InlineHost h in inlineHosts)
                {
                    if (h.Start >= textLen) continue;
                    Rect r = CharRect(ft, h.Start);
                    box.Hosted.Add(new HostedBox(h.Island,
                        new Rect(x + r.Left, y + r.Top, h.Size.Width, h.Size.Height)));
                }

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

        /// <summary>An InlineUIContainer's island, and the span of blank characters standing in for it.</summary>
        private readonly struct InlineHost
        {
            public readonly UIElementIsland Island;
            public readonly int Start;
            public readonly Size Size;
            public InlineHost(UIElementIsland island, int start, Size size) { Island = island; Start = start; Size = size; }
        }

        // Concatenate the paragraph's inline text and apply per-run font/weight/style/colour.
        private FormattedText BuildParagraphText(Paragraph paragraph, double width, out int textLen,
                                                 out List<InlineHost> inlineHosts)
        {
            var sb = new System.Text.StringBuilder();
            var runs = new List<RunFmt>();
            inlineHosts = new List<InlineHost>();
            CollectInlines(paragraph.Inlines, sb, runs, inlineHosts, width);
            string text = sb.ToString();
            textLen = text.Length;
            if (text.Length == 0) text = " ";   // keep a line box (paragraph height)

            FormattedText ft = MakeText(text, paragraph, width);
            ft.TextAlignment = paragraph.TextAlignment;
            double lh = paragraph.LineHeight;
            if (!double.IsNaN(lh) && lh > 0) ft.LineHeight = lh;

            // An inline element taller than the text would otherwise overlap the line below, because
            // the reserved characters only carry the text's own height.
            double tallest = 0;
            foreach (InlineHost h in inlineHosts)
            {
                if (h.Size.Height > tallest) tallest = h.Size.Height;
            }
            if (tallest > 0 && (double.IsNaN(lh) || lh <= 0) && tallest > ft.Height) ft.LineHeight = tallest;

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

        private void CollectInlines(InlineCollection inlines, System.Text.StringBuilder sb, List<RunFmt> runs,
                                    List<InlineHost> inlineHosts, double width)
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
                        CollectInlines(span.Inlines, sb, runs, inlineHosts, width);
                        break;
                    case InlineUIContainer host:
                    {
                        // FormattedText has no concept of an embedded object, so the element's width is
                        // reserved as a run of spaces and the element is then positioned over them.
                        // Approximate by construction -- the gap is a whole number of spaces wide -- but
                        // it keeps the surrounding text out from under the element, which the single
                        // object-replacement character this used to append did not.
                        UIElement child = host.Child;
                        if (child == null) break;

                        UIElementIsland island = IslandFor(child);
                        Size size = LayoutIsland(island,
                            new Size(width > 0 ? width : double.PositiveInfinity, double.PositiveInfinity),
                            horizontalAutoSize: true);

                        RunFmt fmt = RunFmtFor(host, sb.Length, 0);
                        double space = SpaceWidth(fmt);
                        int count = space > 0 ? (int)Math.Ceiling(size.Width / space) : 1;
                        if (count < 1) count = 1;

                        int start = sb.Length;
                        sb.Append(' ', count);
                        runs.Add(new RunFmt(start, count, fmt.Family, fmt.Size, fmt.Weight, fmt.Style, null, false));
                        inlineHosts.Add(new InlineHost(island, start, size));
                        break;
                    }
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
