// The text of WinForms' edit controls (TextBox, RichTextBox, MaskedTextBox: TextBoxBase) for the backends' Text
// pattern questions: the text, the selection, the lines and where each character is drawn.
//
// The answers are read off the box's own layout (its Document), not worked out again from the string, because the
// lines a reader steps through have to be the ones on the screen: a wrapped paragraph is several lines there, and a
// line of the text that is wider than the box is one line however long it is. TextBoxBase's public geometry is not
// usable for this as it stands. Its GetPositionFromCharIndex adds the scroll offset where the box takes it away and
// leaves out the frame the text is inset by, and GetCharIndexFromPosition expects a point in the document rather
// than in the box -- so a box scrolled by a line answered for the line below. What is used instead is what the box
// itself draws the caret and hit-tests the mouse with (Document.SetCaretPos, FindCursor): a character sits at the
// frame's inset, plus its cell in the line, less the scroll.
//
// A password box has no text to give: its characters are what it hides.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace WinFormsWebGpu.Accessibility
{
    internal sealed partial class AutomationSource
    {
        /// <summary>The edit control behind an element whose text can be read: a TextBoxBase that is not hiding
        /// its characters.</summary>
        private static TextBoxBase EditOf(object element)
        {
            if (element is not TextBoxBase edit || edit.IsDisposed || IsPassword(edit)) return null;
            return edit;
        }

        internal static bool IsPassword(Control c) => c switch
        {
            TextBox t => t.UseSystemPasswordChar || t.PasswordChar != '\0',
            MaskedTextBox m => m.UseSystemPasswordChar || m.PasswordChar != '\0',
            _ => false,
        };

        /// <summary>The box's lines as it has laid them out, each with the offset it starts at. A line's text
        /// carries its own break, so the starts add up to the offsets of the text the box reports.</summary>
        private static List<(Line Line, int Start)> LinesOf(TextBoxBase edit)
        {
            var lines = new List<(Line, int)>();
            Document doc = edit.document;
            if (doc == null) return lines;
            int start = 0;
            for (int i = 1; i <= doc.Lines; i++)
            {
                Line line = doc.GetLine(i);
                if (line == null) break;
                lines.Add((line, start));
                start += line.text.Length;
            }
            return lines;
        }

        /// <summary>The text as the box holds it, which is what TextBoxBase.Text reads too: every line with its
        /// break but the last. Read from the lines rather than from Text so that its offsets are the layout's
        /// (a masked box's Text can leave out the prompt characters it shows).</summary>
        private static string TextOf(TextBoxBase edit)
        {
            Document doc = edit.document;
            if (doc == null) return edit.Text ?? string.Empty;
            var sb = new System.Text.StringBuilder();
            for (int i = 1; i <= doc.Lines; i++)
            {
                Line line = doc.GetLine(i);
                if (line == null) break;
                sb.Append(i == doc.Lines ? line.TextWithoutEnding() : line.text.ToString());
            }
            return sb.ToString();
        }

        /// <summary>The line holding <paramref name="offset"/>; the last line for the end of the text.</summary>
        private static int LineIndexAt(List<(Line Line, int Start)> lines, int offset)
        {
            for (int i = 0; i < lines.Count; i++)
                if (offset < lines[i].Start + lines[i].Line.text.Length) return i;
            return lines.Count - 1;
        }

        // the left and right edges of a character's cell in its line, in the line's own coordinates; a line laid
        // out in visual order (mixed directions) keeps each character's cell where it is drawn
        private static (float Left, float Right) CellOf(Line line, int pos)
        {
            if (line.Visual is Line.VisualLayout v && pos >= 0 && pos < v.Left.Length)
                return (v.Left[pos], v.Left[pos] + v.Width[pos]);
            float[] widths = line.widths;
            int last = widths.Length - 1;
            int a = Math.Clamp(pos, 0, last), b = Math.Clamp(pos + 1, 0, last);
            return (widths[a], widths[b]);
        }

        /// <summary>A stretch of a line, in the box's client coordinates (where SetCaretPos puts the caret).</summary>
        private static Rectangle ClientRectOf(Document doc, Line line, float left, float right)
        {
            int x = (int)Math.Floor(doc.OffsetX + line.X + left - doc.ViewPortX);
            int r = (int)Math.Ceiling(doc.OffsetX + line.X + right - doc.ViewPortX);
            int y = doc.OffsetY + line.Y - doc.ViewPortY;
            return new Rectangle(x, y, Math.Max(1, r - x), Math.Max(1, line.height));
        }

        public bool TryGetText(int id, out string text)
        {
            text = string.Empty;
            TextBoxBase edit = EditOf(ElementOf(id));
            if (edit == null) return false;
            try { text = TextOf(edit); return true; }
            catch (Exception) { return false; }
        }

        public bool TryGetTextSelection(int id, out int start, out int length)
        {
            start = length = 0;
            TextBoxBase edit = EditOf(ElementOf(id));
            if (edit == null) return false;
            try
            {
                start = edit.SelectionStart;
                length = edit.SelectionLength;
                return true;
            }
            catch (Exception) { return false; }
        }

        public bool SetTextSelection(int id, int start, int length)
        {
            TextBoxBase edit = EditOf(ElementOf(id));
            if (edit == null || start < 0 || length < 0) return false;
            return OnUiThread(edit, () =>
            {
                int all = TextOf(edit).Length;
                int s = Math.Min(start, all);
                edit.Select(s, Math.Min(length, all - s));
            });
        }

        public bool TryGetTextRangeBounds(int id, int start, int length, out double x, out double y, out double width, out double height)
        {
            x = y = width = height = 0;
            TextBoxBase edit = EditOf(ElementOf(id));
            IA11yHostSite site = edit == null ? null : SiteOf(edit);
            if (site == null || start < 0) return false;
            try
            {
                Document doc = edit.document;
                List<(Line Line, int Start)> lines = LinesOf(edit);
                if (doc == null || lines.Count == 0) return false;
                int end = start + Math.Max(length, 0);
                Rectangle union = Rectangle.Empty;
                if (length <= 0)
                {
                    // a caret, or a range of nothing: as narrow as a caret, where the caret would be drawn
                    var (line, lineStart) = lines[LineIndexAt(lines, start)];
                    float at = line.CaretX(Math.Min(start - lineStart, line.text.Length));
                    union = ClientRectOf(doc, line, at, at + 1);
                }
                else
                {
                    foreach (var (line, lineStart) in lines)
                    {
                        int chars = line.TextWithoutEnding().Length;
                        int from = Math.Max(start, lineStart) - lineStart, to = Math.Min(end, lineStart + chars) - lineStart;
                        if (to <= from)
                        {
                            // a range that covers only a line's break still shows on that line, as its end
                            if (start <= lineStart + chars && end > lineStart + chars && from <= chars)
                            {
                                float at = line.CaretX(chars);
                                Rectangle stub = ClientRectOf(doc, line, at, at + 1);
                                union = union.IsEmpty ? stub : Rectangle.Union(union, stub);
                            }
                            continue;
                        }
                        float left = float.MaxValue, right = float.MinValue;
                        for (int p = from; p < to; p++)
                        {
                            var (l, r) = CellOf(line, p);
                            left = Math.Min(left, Math.Min(l, r));
                            right = Math.Max(right, Math.Max(l, r));
                        }
                        Rectangle part = ClientRectOf(doc, line, left, right);
                        union = union.IsEmpty ? part : Rectangle.Union(union, part);
                    }
                }
                if (union.IsEmpty) return false;
                Point origin = A11y.ClientOrigin(edit);
                union.Offset(origin);
                return site.TryMapToScreen(union, out x, out y, out width, out height);
            }
            catch (Exception) { return false; }
        }

        public bool TryGetTextLine(int id, int offset, out int lineStart, out int lineLength, out int lineNumber)
        {
            lineStart = lineLength = lineNumber = 0;
            TextBoxBase edit = EditOf(ElementOf(id));
            if (edit == null || offset < 0) return false;
            try
            {
                List<(Line Line, int Start)> lines = LinesOf(edit);
                if (lines.Count == 0)
                {
                    // no layout to ask: the text's own breaks
                    string text = edit.Text ?? string.Empty;
                    (lineStart, lineLength, lineNumber) = HardLine(text, Math.Min(offset, text.Length));
                    return true;
                }
                int i = LineIndexAt(lines, offset);
                lineStart = lines[i].Start;
                lineLength = i == lines.Count - 1 ? lines[i].Line.TextWithoutEnding().Length : lines[i].Line.text.Length;
                lineNumber = i;
                return true;
            }
            catch (Exception) { return false; }
        }

        /// <summary>The line of <paramref name="text"/> the offset is in, by its breaks (\r\n, \n, \r): its start, its
        /// length with the break, and its number. Only for a box that has no layout to ask.</summary>
        private static (int Start, int Length, int Number) HardLine(string text, int offset)
        {
            int start = 0, number = 0;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c != '\n' && c != '\r') continue;
                int next = c == '\r' && i + 1 < text.Length && text[i + 1] == '\n' ? i + 2 : i + 1;
                if (offset < next) return (start, next - start, number);
                start = next;
                number++;
                i = next - 1;
            }
            return (start, text.Length - start, number);
        }

        public int GetTextOffsetAtPoint(int id, double screenX, double screenY)
        {
            TextBoxBase edit = EditOf(ElementOf(id));
            IA11yHostSite site = edit == null ? null : SiteOf(edit);
            if (site == null) return -1;
            try
            {
                Document doc = edit.document;
                if (doc == null || !site.TryMapFromScreen(screenX, screenY, out Point driver)) return -1;
                Point origin = A11y.ClientOrigin(edit);
                var client = new Point(driver.X - origin.X, driver.Y - origin.Y);
                if (!new Rectangle(Point.Empty, edit.ClientSize).Contains(client)) return -1;
                // the mouse's way into the layout: the point in the document is the client point plus the scroll
                // (TextBoxBase's mouse handlers), and FindCursor takes the frame's inset off. It answers where a click
                // puts the caret -- after a character for a click on its right half -- and the question is which
                // character is under the point, so within the line the cell holding the point is looked for.
                LineTag tag = doc.FindCursor(client.X + doc.ViewPortX, client.Y + doc.ViewPortY, out int pos);
                if (tag == null) return -1;
                Line line = tag.Line;
                int chars = line.TextWithoutEnding().Length;
                float x = client.X + doc.ViewPortX - doc.OffsetX - line.X;
                for (int p = 0; p < chars; p++)
                {
                    var (l, r) = CellOf(line, p);
                    if (x >= Math.Min(l, r) && x < Math.Max(l, r)) { pos = p; break; }
                }
                int offset = doc.LineTagToCharIndex(line, Math.Min(pos, chars));
                return Math.Clamp(offset, 0, TextOf(edit).Length);
            }
            catch (Exception) { return -1; }
        }

        public bool TryGetVisibleTextRange(int id, out int start, out int length)
        {
            start = length = 0;
            TextBoxBase edit = EditOf(ElementOf(id));
            if (edit == null) return false;
            try
            {
                Document doc = edit.document;
                List<(Line Line, int Start)> lines = LinesOf(edit);
                if (doc == null || lines.Count == 0) return false;
                int viewWidth = doc.ViewPortWidth > 0 ? doc.ViewPortWidth : edit.ClientSize.Width;
                int viewHeight = doc.ViewPortHeight > 0 ? doc.ViewPortHeight : edit.ClientSize.Height;
                if (!edit.Multiline)
                {
                    // one line, scrolled sideways: the characters whose cells are inside the view
                    var (line, _) = lines[0];
                    int chars = line.TextWithoutEnding().Length;
                    float from = doc.ViewPortX - line.X, to = from + viewWidth;
                    int first = -1, last = -1;
                    for (int p = 0; p < chars; p++)
                    {
                        var (l, r) = CellOf(line, p);
                        if (Math.Max(l, r) > from && Math.Min(l, r) < to)
                        {
                            if (first < 0) first = p;
                            last = p;
                        }
                    }
                    if (first < 0) { start = Math.Min(chars, Math.Max(0, edit.SelectionStart)); return true; }
                    start = first;
                    length = last + 1 - first;
                    return true;
                }
                // several lines, scrolled up and down: the lines any of which is inside the view
                int top = doc.ViewPortY, bottom = top + viewHeight;
                int firstLine = -1, lastLine = -1;
                for (int i = 0; i < lines.Count; i++)
                {
                    Line line = lines[i].Line;
                    if (line.Y + line.height > top && line.Y < bottom)
                    {
                        if (firstLine < 0) firstLine = i;
                        lastLine = i;
                    }
                }
                if (firstLine < 0) return true;
                start = lines[firstLine].Start;
                int end = lines[lastLine].Start + (lastLine == lines.Count - 1 ? lines[lastLine].Line.TextWithoutEnding().Length : lines[lastLine].Line.text.Length);
                length = end - start;
                return true;
            }
            catch (Exception) { return false; }
        }
    }
}
