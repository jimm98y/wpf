// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
//
// Ported from dotnet/winforms (System.ComponentModel.Design.ByteViewer): a hex dump of a byte array -- 16 bytes a
// line, the address, the hex and the printable characters, scrolled by a scroll bar -- or, in the Ansi and Unicode
// display modes, the bytes decoded into a read-only text box; Auto picks one from a byte-order mark or how
// printable the bytes are. Every member of the Mono stub threw NotImplementedException.

using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace System.ComponentModel.Design
{
	[DesignTimeVisible (false)]
	[ToolboxItem (false)]
	public class ByteViewer : TableLayoutPanel
	{
		private const int DEFAULT_COLUMN_COUNT = 16;
		private const int DEFAULT_ROW_COUNT = 25;
		private const int COLUMN_COUNT = 16;
		private const int BORDER_GAP = 2;
		private const int INSET_GAP = 3;
		private const int CELL_HEIGHT = 21;
		private const int CELL_WIDTH = 25;
		private const int CHAR_WIDTH = 8;
		private const int ADDRESS_WIDTH = 69;   // ceiling(width of "DDDDDDDD") + 1
		private const int HEX_WIDTH = CELL_WIDTH * COLUMN_COUNT;
		private const int DUMP_WIDTH = CHAR_WIDTH * COLUMN_COUNT;
		private const int HEX_DUMP_GAP = 5;
		private const int ADDRESS_START_X = BORDER_GAP + INSET_GAP;
		private const int CLIENT_START_Y = BORDER_GAP + INSET_GAP;
		private const int LINE_START_Y = CLIENT_START_Y + CELL_HEIGHT / 8;
		private const int HEX_START_X = ADDRESS_START_X + ADDRESS_WIDTH;
		private const int DUMP_START_X = HEX_START_X + HEX_WIDTH + HEX_DUMP_GAP;
		private const int SCROLLBAR_START_X = DUMP_START_X + DUMP_WIDTH + HEX_DUMP_GAP;

		private static readonly Font s_addressFont = new Font ("Microsoft Sans Serif", 8.0f);
		private static readonly Font s_hexDumpFont = new Font ("Courier New", 8.0f);

		private int _scrollbarWidth;
		private VScrollBar _scrollBar;
		private TextBox _edit;
		private readonly int _columnCount = DEFAULT_COLUMN_COUNT;
		private int _rowCount = DEFAULT_ROW_COUNT;
		private byte [] _dataBuf = new byte [0];
		private int _startLine;
		private int _displayLinesCount;
		private int _linesCount;
		private DisplayMode _displayMode;
		private DisplayMode _realDisplayMode;

		public ByteViewer ()
		{
			SuspendLayout ();
			CellBorderStyle = TableLayoutPanelCellBorderStyle.Inset;
			ColumnCount = 1;
			ColumnStyles.Add (new ColumnStyle (SizeType.Percent, 100F));
			RowCount = 1;
			RowStyles.Add (new RowStyle (SizeType.Percent, 100F));
			InitUI ();
			ResumeLayout ();
			_displayMode = DisplayMode.Hexdump;
			_realDisplayMode = DisplayMode.Hexdump;
			DoubleBuffered = true;
			SetStyle (ControlStyles.ResizeRedraw, true);
		}

		// From XmlScanner, as .NET: the encoding a byte-order mark (or the start of an XML declaration) names.
		private static int AnalyzeByteOrderMark (byte [] buffer, int index)
		{
			int c1 = buffer [index + 0] << 8 | buffer [index + 1];
			int c2 = buffer [index + 2] << 8 | buffer [index + 3];
			int c4 = GetEncodingIndex (c1);
			int c5 = GetEncodingIndex (c2);
			int [,] encodings = {
				{ 1, 5, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1 },
				{ 1, 1, 1, 11, 1, 10, 4, 1, 1, 1, 1, 1, 1 },
				{ 2, 9, 5, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2 },
				{ 3, 7, 3, 7, 3, 3, 3, 3, 3, 3, 3, 3, 3 },
				{ 14, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1 },
				{ 1, 6, 1, 1, 1, 1, 1, 3, 1, 1, 1, 1, 1 },
				{ 1, 8, 1, 1, 1, 1, 1, 1, 2, 1, 1, 1, 1 },
				{ 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1 },
				{ 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1 },
				{ 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 13, 1, 1 },
				{ 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1 },
				{ 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 12 },
				{ 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1 },
			};
			return encodings [c4, c5];
		}

		private static int GetEncodingIndex (int c1)
		{
			switch (c1) {
			case 0x0000: return 1;
			case 0xfeff: return 2;
			case 0xfffe: return 3;
			case 0xefbb: return 4;
			case 0x3c00: return 5;
			case 0x003c: return 6;
			case 0x3f00: return 7;
			case 0x003f: return 8;
			case 0x3c3f: return 9;
			case 0x786d: return 10;
			case 0x4c6f: return 11;
			case 0xa794: return 12;
			default: return 0;
			}
		}

		private void DrawAddress (Graphics g, int startLine, int line)
		{
			string hex = ((startLine + line) * _columnCount).ToString ("X8", CultureInfo.InvariantCulture);
			using (var foreground = new SolidBrush (ForeColor))
				g.DrawString (hex, s_addressFont, foreground, ADDRESS_START_X, LINE_START_Y + line * CELL_HEIGHT);
		}

		private void DrawClient (Graphics g)
		{
			int width = HEX_WIDTH + HEX_DUMP_GAP + DUMP_WIDTH + HEX_DUMP_GAP;
			using (Brush brush = new SolidBrush (SystemColors.ControlLightLight))
				g.FillRectangle (brush, new Rectangle (HEX_START_X, CLIENT_START_Y, width, _rowCount * CELL_HEIGHT));
			using (var pen = new Pen (SystemColors.ControlDark)) {
				g.DrawRectangle (pen, new Rectangle (HEX_START_X, CLIENT_START_Y, width - 1, _rowCount * CELL_HEIGHT - 1));
				g.DrawLine (pen, DUMP_START_X - HEX_DUMP_GAP, CLIENT_START_Y, DUMP_START_X - HEX_DUMP_GAP, CLIENT_START_Y + _rowCount * CELL_HEIGHT - 1);
			}
		}

		private static bool CharIsPrintable (char c)
		{
			UnicodeCategory uc = char.GetUnicodeCategory (c);
			return uc != UnicodeCategory.Control && uc != UnicodeCategory.Format && uc != UnicodeCategory.LineSeparator
				&& uc != UnicodeCategory.ParagraphSeparator && uc != UnicodeCategory.OtherNotAssigned;
		}

		private void DrawDump (Graphics g, byte [] buffer, int offset, int length, int line)
		{
			var chars = new char [length];
			for (int i = 0; i < length; i++) {
				char c = Convert.ToChar (buffer [offset + i]);
				chars [i] = CharIsPrintable (c) ? c : '.';
			}
			using (Brush foreground = new SolidBrush (ForeColor))
				g.DrawString (new string (chars), s_hexDumpFont, foreground, DUMP_START_X, LINE_START_Y + line * CELL_HEIGHT);
		}

		private void DrawHex (Graphics g, byte [] buffer, int offset, int length, int line)
		{
			var sb = new StringBuilder (length * 3 + 1);
			for (int i = 0; i < length; i++) {
				sb.Append (buffer [offset + i].ToString ("X2", CultureInfo.InvariantCulture));
				if (i == _columnCount / 2 - 1)
					sb.Append (' ');   // one more in the middle
			}
			using (Brush foreground = new SolidBrush (ForeColor))
				g.DrawString (sb.ToString (), s_hexDumpFont, foreground, HEX_START_X + BORDER_GAP, LINE_START_Y + line * CELL_HEIGHT);
		}

		private void DrawLines (Graphics g, int startLine, int linesCount)
		{
			for (int i = 0; i < linesCount; i++) {
				int offset = (startLine + i) * _columnCount;
				int length = offset + _columnCount > _dataBuf.Length ? _dataBuf.Length % _columnCount : _columnCount;
				DrawAddress (g, startLine, i);
				DrawHex (g, _dataBuf, offset, length, i);
				DrawDump (g, _dataBuf, offset, length, i);
			}
		}

		private DisplayMode GetAutoDisplayMode ()
		{
			if (_dataBuf.Length < 8)
				return DisplayMode.Hexdump;
			switch (AnalyzeByteOrderMark (_dataBuf, 0)) {
			case 2: return DisplayMode.Hexdump;    // big-endian Unicode
			case 3: return DisplayMode.Unicode;
			case 4: case 5: case 6: case 7: case 8: case 9: case 10: case 11: case 12:
				return DisplayMode.Hexdump;        // UCS-4 orders, EBCDIC
			case 13: case 14:
				return DisplayMode.Ansi;           // UTF-8
			default:
				int size = _dataBuf.Length > 1024 ? 512 : _dataBuf.Length / 2;
				int printablesCount = 0, unicodeCount = 0;
				for (int i = 0; i < size; i++) {
					char c = (char) _dataBuf [i];
					if (char.IsLetterOrDigit (c) || char.IsWhiteSpace (c))
						printablesCount++;
				}
				for (int i = 0; i < size; i += 2) {
					char unicodeChar = (char) (_dataBuf [i] | _dataBuf [i + 1] << 8);
					if (CharIsPrintable (unicodeChar))
						unicodeCount++;
				}
				if (unicodeCount * 100 / (size / 2) > 80)
					return DisplayMode.Unicode;
				if (printablesCount * 100 / size > 80)
					return DisplayMode.Ansi;
				return DisplayMode.Hexdump;
			}
		}

		public virtual byte [] GetBytes () => _dataBuf;

		public virtual DisplayMode GetDisplayMode () => _displayMode;

		[DllImport ("kernel32", ExactSpelling = true, CharSet = CharSet.Unicode)]
		private static extern int MultiByteToWideChar (uint codePage, uint flags, byte [] multiByte, int multiByteLength, [Out] char [] wideChar, int wideCharLength);

		/// <summary>The bytes in the system's ANSI code page (CP_ACP; Latin-1 where there is no such thing).</summary>
		private static string DecodeAnsi (byte [] data)
		{
			if (OperatingSystem.IsWindows () && data.Length > 0) {
				int size = MultiByteToWideChar (0, 0, data, data.Length, null, 0);
				var chars = new char [size];
				size = MultiByteToWideChar (0, 0, data, data.Length, chars, size);
				return new string (chars, 0, size);
			}
			return Encoding.Latin1.GetString (data);
		}

		private void InitAnsi ()
		{
			// NULs would end the text box's text: shown as vertical tabs, as .NET does.
			_edit.Text = DecodeAnsi (_dataBuf).Replace ('\0', '\v');
		}

		private void InitUnicode ()
		{
			_edit.Text = Encoding.Unicode.GetString (_dataBuf, 0, _dataBuf.Length / 2 * 2).Replace ('\0', '\v');
		}

		private void InitUI ()
		{
			_scrollbarWidth = SystemInformation.VerticalScrollBarWidth;
			// For backwards compatibility.
			Size = new Size (SCROLLBAR_START_X + _scrollbarWidth + BORDER_GAP + INSET_GAP, 2 * (BORDER_GAP + INSET_GAP) + _rowCount * CELL_HEIGHT);
			_scrollBar = new VScrollBar ();
			_scrollBar.ValueChanged += ScrollChanged;
			_scrollBar.TabStop = true;
			_scrollBar.TabIndex = 0;
			_scrollBar.Dock = DockStyle.Right;
			_scrollBar.Visible = false;
			_edit = new TextBox {
				AutoSize = false,
				BorderStyle = BorderStyle.None,
				Multiline = true,
				ReadOnly = true,
				ScrollBars = ScrollBars.Both,
				AcceptsTab = true,
				AcceptsReturn = true,
				Dock = DockStyle.Fill,
				Margin = Padding.Empty,
				WordWrap = false,
				Visible = false,
			};
			Controls.Add (_scrollBar, 0, 0);
			Controls.Add (_edit, 0, 0);
		}

		private void InitState ()
		{
			// Lines needed, the last partial one counted.
			_linesCount = (_dataBuf.Length + _columnCount - 1) / _columnCount;
			_startLine = 0;
			if (_linesCount > _rowCount) {
				_displayLinesCount = _rowCount;
				_scrollBar.Hide ();
				_scrollBar.Maximum = _linesCount - 1;
				_scrollBar.LargeChange = _rowCount;
				_scrollBar.Show ();
				_scrollBar.Enabled = true;
			} else {
				_displayLinesCount = _linesCount;
				_scrollBar.Hide ();
				_scrollBar.Maximum = _rowCount;
				_scrollBar.LargeChange = _rowCount;
				_scrollBar.Show ();
				_scrollBar.Enabled = false;
			}
			_scrollBar.Select ();
			Invalidate ();
		}

		protected override void OnKeyDown (KeyEventArgs e)
		{
			_scrollBar.Select ();
		}

		protected override void OnPaint (PaintEventArgs e)
		{
			base.OnPaint (e);
			Graphics g = e.Graphics;
			switch (_realDisplayMode) {
			case DisplayMode.Hexdump:
				SuspendLayout ();
				_edit.Hide ();
				_scrollBar.Show ();
				ResumeLayout ();
				DrawClient (g);
				DrawLines (g, _startLine, _displayLinesCount);
				break;
			case DisplayMode.Ansi:
			case DisplayMode.Unicode:
				_edit.Invalidate ();
				break;
			}
		}

		protected override void OnLayout (LayoutEventArgs e)
		{
			base.OnLayout (e);
			int rows = (ClientSize.Height - 2 * (BORDER_GAP + INSET_GAP)) / CELL_HEIGHT;
			if (rows >= 0 && rows != _rowCount)
				_rowCount = rows;
			else
				return;

			if (Dock == DockStyle.None)
				Size = new Size (SCROLLBAR_START_X + _scrollbarWidth + BORDER_GAP + INSET_GAP, 2 * (BORDER_GAP + INSET_GAP) + _rowCount * CELL_HEIGHT);

			if (_scrollBar != null) {
				if (_linesCount > _rowCount) {
					_scrollBar.Hide ();
					_scrollBar.Maximum = _linesCount - 1;
					_scrollBar.LargeChange = _rowCount;
					_scrollBar.Show ();
					_scrollBar.Enabled = true;
					_scrollBar.Select ();
				} else {
					_scrollBar.Enabled = false;
				}
			}
			_displayLinesCount = _startLine + _rowCount < _linesCount ? _rowCount : _linesCount - _startLine;
		}

		public virtual void SaveToFile (string path)
		{
			using (var currentFile = new FileStream (path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
				currentFile.Write (_dataBuf, 0, _dataBuf.Length);
		}

		protected virtual void ScrollChanged (object source, EventArgs e)
		{
			_startLine = _scrollBar.Value;
			Invalidate ();
		}

		public virtual void SetBytes (byte [] bytes)
		{
			_dataBuf = bytes ?? throw new ArgumentNullException (nameof (bytes));
			InitState ();
			SetDisplayMode (_displayMode);
		}

		public virtual void SetDisplayMode (DisplayMode mode)
		{
			if (!Enum.IsDefined (typeof (DisplayMode), mode))
				throw new InvalidEnumArgumentException (nameof (mode), (int) mode, typeof (DisplayMode));
			_displayMode = mode;
			_realDisplayMode = mode == DisplayMode.Auto ? GetAutoDisplayMode () : mode;
			switch (_realDisplayMode) {
			case DisplayMode.Ansi:
				InitAnsi ();
				SuspendLayout ();
				_edit.Show ();
				_scrollBar.Hide ();
				ResumeLayout ();
				Invalidate ();
				break;
			case DisplayMode.Unicode:
				InitUnicode ();
				SuspendLayout ();
				_edit.Show ();
				_scrollBar.Hide ();
				ResumeLayout ();
				Invalidate ();
				break;
			case DisplayMode.Hexdump:
				SuspendLayout ();
				_edit.Hide ();
				if (_linesCount > _rowCount && !_scrollBar.Visible) {
					_scrollBar.Show ();
					ResumeLayout ();
					_scrollBar.Invalidate ();
					_scrollBar.Select ();
				} else {
					ResumeLayout ();
				}
				break;
			}
		}

		public virtual void SetFile (string path)
		{
			SetBytes (File.ReadAllBytes (path));
		}

		public virtual void SetStartLine (int line)
		{
			_startLine = line < 0 || line >= _linesCount || line > _dataBuf.Length / _columnCount ? 0 : line;
		}

		/// <summary>The text an Ansi or Unicode display shows (tests).</summary>
		internal string DisplayedText => _edit.Text;
	}
}
