// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Paginate a FlowDocument and read back what actually reached the page.
//
// Everything here goes through the public paginator (IDocumentPaginatorSource), so the tests are
// about rendered output rather than about ManagedFlowLayout's internals: a block type the layout
// does not model produces no glyphs, and that is exactly what shows up as missing text below.
//
// The text is recovered from the GlyphRun's Characters, which is the string the engine actually
// shaped -- not the string the document was built from. A cell whose content silently never got
// laid out cannot fake its way past that.
//

using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace Wpf.Document.Tests
{
    /// <summary>
    /// What a paginated page contains: the drawn text, and where each piece landed.
    ///
    /// The engine emits one GlyphRun PER WORD in the paginated path (it packs words into lines
    /// itself), so "top left" arrives as two runs with the space implied by their positions. The
    /// page text is therefore reconstructed rather than concatenated: runs are grouped into visual
    /// lines by their vertical position, ordered left to right, and joined with a single space.
    /// Searching that reconstruction is what lets a test ask for the phrase it actually wrote.
    /// </summary>
    internal sealed class PageContent
    {
        public List<(string Text, Rect Bounds)> Runs { get; } = new();
        public List<Rect> Rectangles { get; } = new();

        /// <summary>
        /// UIElements found in the page's visual tree, with the page-absolute box they occupy.
        ///
        /// A hosted element is not a drawing, so it never appears in Runs or Rectangles; the only
        /// evidence that BlockUIContainer/InlineUIContainer worked is that the element is actually
        /// IN the tree, which is also what makes it hit-test and take input.
        /// </summary>
        public List<(UIElement Element, Rect Bounds)> Elements { get; } = new();

        public Size PageSize { get; set; }

        public bool HostsElement(UIElement element) => ElementBounds(element).HasValue;

        public Rect? ElementBounds(UIElement element)
        {
            foreach ((UIElement e, Rect bounds) in Elements)
            {
                if (ReferenceEquals(e, element)) return bounds;
            }
            return null;
        }

        // Runs sharing a baseline within this many device pixels are one visual line. Generous
        // enough for the sub-pixel drift between runs of different sizes on the same line.
        private const double LineTolerance = 3.0;

        private List<(string Text, List<(int Start, int Length, Rect Bounds)> Words)>? _lines;

        private List<(string Text, List<(int Start, int Length, Rect Bounds)> Words)> Lines
        {
            get
            {
                if (_lines is not null) return _lines;

                var ordered = new List<(string Text, Rect Bounds)>(Runs);
                ordered.Sort((a, b) =>
                {
                    int byTop = a.Bounds.Top.CompareTo(b.Bounds.Top);
                    if (Math.Abs(a.Bounds.Top - b.Bounds.Top) <= LineTolerance) byTop = 0;
                    return byTop != 0 ? byTop : a.Bounds.Left.CompareTo(b.Bounds.Left);
                });

                _lines = new();
                double lineTop = double.NaN;
                StringBuilder? sb = null;
                List<(int, int, Rect)>? words = null;

                foreach ((string text, Rect bounds) in ordered)
                {
                    if (sb is null || Math.Abs(bounds.Top - lineTop) > LineTolerance)
                    {
                        if (sb is not null) _lines.Add((sb.ToString(), words!));
                        sb = new StringBuilder();
                        words = new();
                        lineTop = bounds.Top;
                    }
                    // The engine emits the inter-word spaces as runs of their own, so only supply a
                    // separator where the boundary does not already have one -- otherwise "top left"
                    // reconstructs as "top   left" and no test can match the phrase it wrote.
                    if (sb.Length > 0 && !char.IsWhiteSpace(sb[sb.Length - 1]) && !char.IsWhiteSpace(text[0]))
                        sb.Append(' ');
                    words!.Add((sb.Length, text.Length, bounds));
                    sb.Append(text);
                }
                if (sb is not null) _lines.Add((sb.ToString(), words!));
                return _lines;
            }
        }

        /// <summary>Every visual line's reconstructed text, newline-separated.</summary>
        public string AllText => string.Join("\n", Lines.ConvertAll(l => l.Text));

        public bool Contains(string needle) => Find(needle).HasValue;

        /// <summary>
        /// Bounds of the run(s) spelling <paramref name="needle"/>, unioned — so a phrase split
        /// across word runs still reports one box, and two phrases on the same line report their
        /// own boxes rather than the whole line's.
        /// </summary>
        public Rect? Find(string needle)
        {
            foreach ((string text, List<(int Start, int Length, Rect Bounds)> words) in Lines)
            {
                int at = text.IndexOf(needle, StringComparison.Ordinal);
                if (at < 0) continue;

                int end = at + needle.Length;
                Rect union = Rect.Empty;
                foreach ((int start, int length, Rect bounds) in words)
                {
                    if (start >= end || start + length <= at) continue;   // word outside the match
                    union = union.IsEmpty ? bounds : Rect.Union(union, bounds);
                }
                if (!union.IsEmpty) return union;
            }
            return null;
        }

        public override string ToString() => $"\"{AllText.Replace("\n", " | ")}\" ({Runs.Count} runs, {Rectangles.Count} rects)";
    }

    internal static class DocumentHarness
    {
        /// <summary>Lays a document out at a fixed page size and returns what the first page drew.</summary>
        public static PageContent Paginate(FlowDocument document, double width = 600, double height = 800)
        {
            document.PageWidth = width;
            document.PageHeight = height;
            document.PagePadding = new Thickness(0);
            document.ColumnWidth = width;   // one column: these tests are about blocks, not columns

            DocumentPaginator paginator = ((IDocumentPaginatorSource)document).DocumentPaginator;
            paginator.PageSize = new Size(width, height);
            paginator.ComputePageCount();

            DocumentPage page = paginator.GetPage(0);
            var content = new PageContent { PageSize = page.Size };
            Walk(page.Visual, Transform.Identity.Value, content);
            return content;
        }

        private static void Walk(Visual visual, Matrix transform, PageContent content)
        {
            if (visual is null) return;

            // A visual's own offset and transform both move its drawing and its children.
            Matrix local = transform;
            Vector offset = VisualTreeHelper.GetOffset(visual);
            local.Translate(offset.X, offset.Y);
            if (VisualTreeHelper.GetTransform(visual) is Transform t && t.Value is Matrix m && !m.IsIdentity)
            {
                local = Matrix.Multiply(m, local);
            }

            Walk(VisualTreeHelper.GetDrawing(visual), local, content);

            // A hosted element draws itself; record where it sits rather than what it painted.
            if (visual is UIElement element)
            {
                var box = new Rect(new Point(0, 0), element.RenderSize);
                box.Transform(local);
                content.Elements.Add((element, box));
            }

            int count = VisualTreeHelper.GetChildrenCount(visual);
            for (int i = 0; i < count; i++)
            {
                if (VisualTreeHelper.GetChild(visual, i) is Visual child) Walk(child, local, content);
            }
        }

        private static void Walk(Drawing? drawing, Matrix transform, PageContent content)
        {
            switch (drawing)
            {
                case DrawingGroup group:
                {
                    Matrix local = transform;
                    if (group.Transform is Transform gt && !gt.Value.IsIdentity)
                    {
                        local = Matrix.Multiply(gt.Value, local);
                    }
                    foreach (Drawing child in group.Children) Walk(child, local, content);
                    break;
                }

                case GlyphRunDrawing glyphDrawing when glyphDrawing.GlyphRun is GlyphRun run:
                {
                    string text = run.Characters is { Count: > 0 }
                        ? new string(System.Linq.Enumerable.ToArray(run.Characters))
                        : string.Empty;
                    if (text.Length == 0) break;

                    Rect box = glyphDrawing.Bounds;
                    box.Transform(transform);
                    content.Runs.Add((text, box));
                    break;
                }

                // Cell backgrounds and borders arrive as geometry, not text; a table that draws its
                // rules is visibly different from one that only stacks its cell text.
                case GeometryDrawing geometry when geometry.Geometry is not null:
                {
                    Rect box = geometry.Geometry.Bounds;
                    if (box.IsEmpty) break;
                    box.Transform(transform);
                    content.Rectangles.Add(box);
                    break;
                }
            }
        }

        /// <summary>
        /// Runs a test body on a dedicated STA thread.
        ///
        /// Text-only documents do not need one, but the moment a document hosts a control the
        /// InputManager is created, and it refuses to exist off an STA thread ("many UI components
        /// require this"). xunit runs tests on the thread pool, which is MTA, so any test that
        /// builds a Button has to bring its own thread.
        /// </summary>
        public static T Sta<T>(Func<T> body)
        {
            T result = default!;
            Exception? failure = null;

            var thread = new Thread(() =>
            {
                try { result = body(); }
                catch (Exception e) { failure = e; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
            thread.Join();

            if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
            return result;
        }

        /// <summary>A paragraph of one run, with an optional explicit font size.</summary>
        public static Paragraph Para(string text, double fontSize = 0)
        {
            var p = new Paragraph(new Run(text));
            if (fontSize > 0) p.FontSize = fontSize;
            p.Margin = new Thickness(0);
            return p;
        }
    }
}
