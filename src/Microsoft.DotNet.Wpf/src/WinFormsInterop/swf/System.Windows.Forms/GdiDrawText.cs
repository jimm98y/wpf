// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// user32's DrawTextEx, in managed code. Every WinForms caption drawn or measured through TextRenderer
// is laid out by it on Windows -- line breaking, the word that does not fit, ellipses, mnemonic
// prefixes, tabs, alignment, the partly visible last line, DT_CALCRECT -- so the port runs the same
// algorithm, read out of user32.dll (DrawTextExWorker and its helpers, with Microsoft's public
// symbols): DT_InitDrawTextInfo, DT_GetLineBreak, GetNextWordbreak, DT_BreakAWord,
// DT_AdjustWhiteSpaces, DT_GetExtentMinusPrefixes, DT_DrawJustifiedLine, AddEllipsisAndDrawLine,
// NeedsEndEllipsis, GetPrefixCount and PSMTextOut. Only the device underneath is ours: string
// extents, the TEXTMETRIC values and TextOut come from ITextDevice.
//
// Left out: the LPK (complex-script) branches, right-to-left layout (GetLayout), full-width (FE)
// line breaking, and DT_PATH_ELLIPSIS's path shortening, which falls back to an end ellipsis.

using System.Drawing;
using System.Text;

namespace System.Windows.Forms
{
	internal static class GdiDrawText
	{
		internal interface ITextDevice
		{
			/// <summary>GetTextExtentPoint: the run's advance width in whole pixels.</summary>
			int Extent (string s, int start, int length);
			int Height { get; }             // tmHeight
			int Ascent { get; }             // tmAscent
			int ExternalLeading { get; }    // tmExternalLeading
			int AveCharWidth { get; }       // tmAveCharWidth
			int Overhang { get; }           // tmOverhang
			/// <summary>TextOut with TA_TOP | TA_LEFT: (x, y) is the top-left of the cell.</summary>
			void TextOut (int x, int y, string s);
			/// <summary>A solid fill in the text colour (the mnemonic underline).</summary>
			void FillRect (Rectangle r);
		}

		internal const uint DT_CENTER = 0x1, DT_RIGHT = 0x2, DT_VCENTER = 0x4, DT_BOTTOM = 0x8,
			DT_WORDBREAK = 0x10, DT_SINGLELINE = 0x20, DT_EXPANDTABS = 0x40, DT_TABSTOP = 0x80,
			DT_NOCLIP = 0x100, DT_EXTERNALLEADING = 0x200, DT_CALCRECT = 0x400, DT_NOPREFIX = 0x800,
			DT_EDITCONTROL = 0x2000, DT_PATH_ELLIPSIS = 0x4000, DT_END_ELLIPSIS = 0x8000,
			DT_MODIFYSTRING = 0x10000, DT_WORD_ELLIPSIS = 0x40000, DT_HIDEPREFIX = 0x100000,
			DT_PREFIXONLY = 0x200000;

		private const string Ellipsis = "...";

		private sealed class Data
		{
			internal int Left, Top, Right, Bottom;     // rcFormat: the rectangle less the margins
			internal int TabLength, LineHeight, MaxWidth, MaxExtent, RightMargin, Overhang;
			internal ITextDevice Dev;
			internal uint Flags;
		}

		/// <summary>DrawTextEx. <paramref name="rect"/> is left, top, right, bottom; with DT_CALCRECT
		/// its right and bottom come back as the text's extent. Returns the height of the text drawn
		/// (1 when that is zero), or 0 when nothing could be laid out.</summary>
		internal static int DrawTextEx (ITextDevice dev, string text, ref Rectangle rect, uint flags,
		                                int leftMargin, int rightMargin, int tabLength, Action<Rectangle> clip)
		{
			if (text == null)
				return 0;
			int count = text.Length;
			if (count == 0)
				return 1;

			var d = new Data { Dev = dev, Flags = flags };
			// DT_InitDrawTextInfo.
			d.Overhang = dev.Overhang;
			d.LineHeight = dev.Height + ((flags & DT_EXTERNALLEADING) != 0 ? dev.ExternalLeading : 0);
			d.TabLength = dev.AveCharWidth * (tabLength != 0 ? tabLength : 8);
			d.Left = rect.Left + leftMargin;
			d.Top = rect.Top;
			d.Right = rect.Right - rightMargin;
			d.Bottom = rect.Bottom;
			d.RightMargin = rightMargin;
			d.MaxWidth = d.Right - d.Left;
			d.MaxExtent = 0;

			if (d.MaxWidth <= 0 && (flags & DT_WORDBREAK) != 0)
				return 1;

			if ((flags & DT_NOCLIP) == 0)
				clip?.Invoke (Rectangle.FromLTRB (rect.Left, rect.Top, rect.Right, rect.Bottom));

			int start = 0, end = count, p = 0, y;
			int lines;
			while (true) {
				y = rect.Top;
				lines = 0;
				p = start;
				if ((flags & DT_SINGLELINE) == 0) {
					int extra = (flags & DT_EDITCONTROL) != 0 ? d.LineHeight : 0;
					int remaining = count;
					bool last = false;
					while (p < end && !last) {
						int next;
						if ((flags & DT_CALCRECT) == 0 && (flags & DT_NOCLIP) == 0
						    && rect.Bottom < d.LineHeight + extra + y) {
							// The last line that will show, or (DT_EDITCONTROL) the one after it.
							last = true;
							if ((flags & (DT_PATH_ELLIPSIS | DT_END_ELLIPSIS)) != 0) {
								int drawn = AddEllipsisAndDrawLine (d, y, text, p, remaining);
								next = p + drawn;
								lines++; y += d.LineHeight; p = next;
								continue;
							}
						}
						next = GetLineBreak (d, text, p, remaining, out int lineLen);
						if ((flags & DT_WORD_ELLIPSIS) == 0 && (next < end || (flags & (DT_PATH_ELLIPSIS | DT_END_ELLIPSIS)) == 0))
							DrawJustifiedLine (d, y, text, p, lineLen);
						else
							AddEllipsisAndDrawLine (d, y, text, p, lineLen);
						remaining -= next - p;
						lines++;
						y += d.LineHeight;
						p = next;
					}
					// A trailing line break is a line of its own (not in an edit control).
					if ((flags & DT_EDITCONTROL) == 0 && end > start && (text [end - 1] == '\r' || text [end - 1] == '\n'))
						y += d.LineHeight;
				} else {
					lines = 1;
					if ((flags & (DT_VCENTER | DT_BOTTOM)) == DT_VCENTER)
						y += ((rect.Bottom - d.LineHeight) - y) / 2;
					else if ((flags & (DT_VCENTER | DT_BOTTOM)) == DT_BOTTOM)
						y = rect.Bottom - d.LineHeight;
					int drawn = AddEllipsisAndDrawLine (d, y, text, start, count);
					p = start + drawn;
					y += d.LineHeight;
				}

				if ((flags & DT_CALCRECT) == 0)
					break;
				// DT_CALCRECT: the rectangle is the widest line and the lines' height. A multi-line
				// text whose widest line overran the width is laid out again at that width, so its
				// breaks are the ones it will really be drawn with.
				int right = d.Left + d.MaxExtent;
				if (lines < 2 || d.MaxExtent <= d.MaxWidth) {
					rect = Rectangle.FromLTRB (rect.Left, rect.Top, right + d.RightMargin, y);
					break;
				}
				d.Right = right;
				d.MaxWidth = d.MaxExtent;
			}

			int height = y - rect.Top;
			return height == 0 ? 1 : height;
		}

		// ---- DT_GetLineBreak and GetNextWordbreak ------------------------------------------------

		/// <summary>The end of the next "word": up to and including one space or tab after it (with
		/// DT_WORDBREAK), or up to a line break. An ampersand (not DT_NOPREFIX) marks the character
		/// after it, which then cannot be a break.</summary>
		private static int NextWordbreak (string s, int p, int end, uint flags)
		{
			bool inPrefix = false;
			int take = 1;
			for (; p < end; p++) {
				char c = s [p];
				if (c == '\n' || c == '\r')
					return p;
				if (c == '\t' || c == ' ' || c == '') {
					if ((flags & DT_WORDBREAK) != 0)
						return p + take;
					take = 0;
					continue;
				}
				if (c == '&' && (flags & DT_NOPREFIX) == 0) {
					inPrefix = !inPrefix;
					take = inPrefix ? 1 : 0;
					continue;
				}
				take = 0;
			}
			return p;
		}

		/// <summary>Where the line starting at <paramref name="p"/> ends: returns where the next line
		/// starts, and the characters this one draws in <paramref name="lineLen"/>.</summary>
		private static int GetLineBreak (Data d, string s, int p, int count, out int lineLen)
		{
			uint flags = d.Flags;
			int start = p, end = p + count;
			int prevWidth = 0;
			int lastBreak = p;
			int cur = p;
			bool adjust = false;
			while (true) {
				if (cur >= end) {
					adjust = false;
					cur = lastBreak;
					break;
				}
				int next = NextWordbreak (s, cur, end, flags);
				int w = LineExtent (d, s, start, next - start);
				lastBreak = next;
				if ((flags & DT_WORDBREAK) != 0 && d.MaxWidth < d.Overhang + w) {
					if (cur != start) {
						// This word goes to the next line.
						adjust = true;
						lastBreak = cur;
						break;
					}
					// The first word does not fit on its own.
					if ((flags & DT_EDITCONTROL) != 0 && (flags & DT_WORD_ELLIPSIS) == 0) {
						lastBreak = BreakAWord (d, s, cur, next - cur, d.MaxWidth - prevWidth);
						cur = lastBreak;
						break;
					}
					adjust = true;
					cur = next;
					if ((flags & DT_WORD_ELLIPSIS) == 0 || next >= end || (s [next] != '\r' && s [next] != '\n'))
						break;
					// ...and a word ellipsis on a line that ends in a line break takes the break too.
					goto LineBreak;
				}
				cur = next;
				prevWidth = w;
				if (next < end && (s [next] == '\r' || s [next] == '\n'))
					goto LineBreak;
				continue;
			LineBreak:
				char c = s [next];
				lastBreak = next + 1;
				// CR LF and LF CR are one break.
				if (lastBreak < end && s [lastBreak] == (char) (c ^ 7))
					lastBreak++;
				adjust = false;
				cur = next;
				break;
			}
			lineLen = cur - start;
			if (adjust && lastBreak < end)
				lastBreak = AdjustWhiteSpaces (s, lastBreak, ref lineLen, flags);
			return lastBreak;
		}

		/// <summary>DT_AdjustWhiteSpaces: the space a line was broken at. Left-aligned, the next
		/// line starts after it; centred, it is also taken off this line; right-aligned, only that.</summary>
		private static int AdjustWhiteSpaces (string s, int p, ref int lineLen, uint flags)
		{
			switch (flags & 3) {
			case 1:
				if (s [p - 1] == ' ' || s [p - 1] == '\t')
					lineLen--;
				goto case 0;
			case 0:
				if (s [p] == ' ' || s [p] == '\t')
					p++;
				return p;
			case 2:
				if (s [p - 1] == ' ' || s [p - 1] == '\t')
					lineLen--;
				return p;
			default:
				return p;
			}
		}

		/// <summary>DT_BreakAWord: as many characters of a word too long for the line as fit (at
		/// least one), found by bisection.</summary>
		private static int BreakAWord (Data d, string s, int p, int len, int avail)
		{
			int lo = 0, hi = len, n = 0;
			if (len > 1) {
				do {
					int mid = lo + (hi - lo) / 2;
					int w = ExtentMinusPrefixes (d, s, p, mid);
					if (avail < w) {
						n = lo;
						hi = mid;
					} else {
						n = mid;
					}
					lo = n;
				} while (hi - n > 1);
				if (n != 0)
					return p + n;
			}
			return p + (len != 0 ? 1 : 0);
		}

		// ---- extents -----------------------------------------------------------------------------

		/// <summary>DT_GetExtentMinusPrefixes: the run's extent, less one ampersand's width (less
		/// the overhang) for every prefix it holds, unless DT_NOPREFIX.</summary>
		private static int ExtentMinusPrefixes (Data d, string s, int p, int len)
		{
			int prefixes = 0;
			for (int i = p, n = len; n > 0 && i < s.Length; i++, n--) {
				char c = s [i];
				if (c == '\0')
					break;
				if (c == '&') {
					prefixes++;
					if (i + 1 < s.Length && s [i + 1] == '&' && n > 1) {
						i++;
						n--;
					}
				} else if (c == '\x1e') {
					prefixes++;
				} else if (c == '\x1f') {
					prefixes++;
					if (n > 1) {
						i++;
						n--;
						prefixes++;
					}
				}
			}
			int removed = 0;
			if ((d.Flags & DT_NOPREFIX) == 0 && prefixes != 0)
				removed = (d.Dev.Extent ("&", 0, 1) - d.Overhang) * prefixes;
			return d.Dev.Extent (s, p, len) - removed;
		}

		/// <summary>A line's width as DrawTextEx measures it: without the overhang, and with tabs
		/// expanded when DT_EXPANDTABS.</summary>
		private static int LineExtent (Data d, string s, int p, int len)
		{
			if ((d.Flags & DT_EXPANDTABS) == 0)
				return ExtentMinusPrefixes (d, s, p, len) - d.Overhang;
			int left = d.Left, w = left;
			int i = p, n = len;
			while (n > 0) {
				int seg = 0;
				while (seg < n && s [i + seg] != '\t')
					seg++;
				if (seg != 0)
					w += ExtentMinusPrefixes (d, s, i, seg) - d.Overhang;
				i += seg;
				n -= seg;
				if (n > 0) {           // the tab
					i++;
					n--;
					if (d.TabLength != 0)
						w = (w - left) / d.TabLength * d.TabLength + d.TabLength + left;
				}
			}
			return w - left;
		}

		// ---- drawing -----------------------------------------------------------------------------

		/// <summary>DT_DrawJustifiedLine: one line, aligned, drawn (unless DT_CALCRECT), and its width
		/// counted toward the widest.</summary>
		private static void DrawJustifiedLine (Data d, int y, string s, int p, int len)
		{
			int x = d.Left;
			int w = -1;
			if ((d.Flags & (DT_CENTER | DT_RIGHT)) != 0) {
				w = LineExtent (d, s, p, len);
				x = d.Right - (d.Overhang + w);
				if ((d.Flags & DT_CENTER) != 0)
					x = d.Left + ((x - d.Left) >> 1);
			}
			Output (d, x, y, s.Substring (p, len));
			if (w < 0)
				w = LineExtent (d, s, p, len);
			if (d.MaxExtent < d.Overhang + w)
				d.MaxExtent = d.Overhang + w;
		}

		/// <summary>AddEllipsisAndDrawLine: the line, cut short with an ellipsis where it overruns
		/// (DT_END_ELLIPSIS, DT_WORD_ELLIPSIS; a path ellipsis is cut the same way here), aligned
		/// and drawn. Returns the characters of the original text it covered.</summary>
		private static int AddEllipsisAndDrawLine (Data d, int y, string s, int p, int len)
		{
			string line = s.Substring (p, len);
			int covered = len;
			if ((d.Flags & (DT_END_ELLIPSIS | DT_WORD_ELLIPSIS | DT_PATH_ELLIPSIS)) != 0) {
				int keep = len;
				if (NeedsEndEllipsis (d, s, p, ref keep))
					line = s.Substring (p, keep) + Ellipsis;
			}
			int x = d.Left;
			int w = -1;
			if ((d.Flags & (DT_CENTER | DT_RIGHT)) != 0) {
				w = LineExtent (d, line, 0, line.Length);
				if ((d.Flags & DT_CENTER) == 0)
					x = d.Right - (d.Overhang + w);
				else
					x = d.Left + (((d.Right - d.Left) - (d.Overhang + w)) >> 1);
			}
			Output (d, x, y, line);
			if (w < 0)
				w = LineExtent (d, line, 0, line.Length);
			if (d.MaxExtent < d.Overhang + w)
				d.MaxExtent = d.Overhang + w;
			return covered;
		}

		/// <summary>NeedsEndEllipsis: when the run overruns the width, the most characters that
		/// leave room for "..." (at least one), by bisection.</summary>
		private static bool NeedsEndEllipsis (Data d, string s, int p, ref int len)
		{
			if (len == 0)
				return false;
			int full = ExtentMinusPrefixes (d, s, p, len);
			if (full <= d.MaxWidth)
				return false;
			int avail = d.Overhang - d.Dev.Extent (Ellipsis, 0, 3) + d.MaxWidth;
			int keep = 1;
			if (avail > 0) {
				int lo = 0, hi = len;
				keep = 0;
				while (lo < hi) {
					int mid = (hi + lo + 1) / 2;
					int e = ExtentMinusPrefixes (d, s, p, mid);
					if (avail <= e) {
						if (e <= avail) {
							lo = mid;
							break;
						}
						hi = mid - 1;
					} else {
						lo = mid;
					}
				}
				keep = lo;
				if (keep < 1)
					keep = 1;
			}
			len = keep;
			return true;
		}

		/// <summary>The line through the text-out DrawTextEx chose: PSMTextOut, which strips the
		/// mnemonic prefixes and underlines the marked character, or a plain TextOut with
		/// DT_NOPREFIX. Nothing is drawn for DT_CALCRECT. Tabs are expanded for DT_EXPANDTABS.</summary>
		private static void Output (Data d, int x, int y, string line)
		{
			if ((d.Flags & DT_CALCRECT) != 0 || line.Length == 0)
				return;
			if ((d.Flags & DT_NOPREFIX) != 0) {
				TabbedOut (d, x, y, line);
				return;
			}
			string text = StripPrefixes (line, out int mnemonic);
			if ((d.Flags & DT_PREFIXONLY) == 0)
				TabbedOut (d, x, y, text);
			if (mnemonic >= 0 && mnemonic < text.Length && (d.Flags & DT_HIDEPREFIX) == 0) {
				int ux = x;
				if (mnemonic > 0)
					ux += d.Dev.Extent (text, 0, mnemonic) - d.Overhang;
				int cw = d.Dev.Extent (text, mnemonic, 1);
				int top = y + d.Dev.Ascent + 1;
				d.Dev.FillRect (Rectangle.FromLTRB (ux, top, ux + cw - d.Overhang / 2, top + 1));
			}
		}

		private static void TabbedOut (Data d, int x, int y, string text)
		{
			if ((d.Flags & DT_EXPANDTABS) == 0 || text.IndexOf ('\t') < 0 || d.TabLength == 0) {
				d.Dev.TextOut (x, y, text);
				return;
			}
			int origin = x, pos = x;
			foreach (string seg in text.Split ('\t')) {
				if (pos != x || seg.Length != 0) { }
				if (seg.Length != 0)
					d.Dev.TextOut (pos, y, seg);
				pos += seg.Length != 0 ? d.Dev.Extent (seg, 0, seg.Length) - d.Overhang : 0;
				pos = origin + ((pos - origin) / d.TabLength + 1) * d.TabLength;
			}
		}

		/// <summary>GetPrefixCount: the text without its prefix characters ("&amp;&amp;" is a literal
		/// ampersand), and where the marked mnemonic landed in it (-1 for none).</summary>
		internal static string StripPrefixes (string s, out int mnemonic)
		{
			var sb = new StringBuilder (s.Length);
			mnemonic = -1;
			for (int i = 0; i < s.Length; i++) {
				char c = s [i];
				if (c == '\0')
					break;
				if (c == '&') {
					if (i + 1 < s.Length && s [i + 1] == '&') {
						sb.Append ('&');
						i++;
					} else {
						mnemonic = sb.Length;
					}
				} else if (c == '\x1e') {
					mnemonic = sb.Length;
				} else if (c == '\x1f') {
					i++;
				} else {
					sb.Append (c);
				}
			}
			return sb.ToString ();
		}
	}
}
