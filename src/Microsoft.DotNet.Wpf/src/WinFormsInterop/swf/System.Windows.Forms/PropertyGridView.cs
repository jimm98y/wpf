
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
// 
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
//
// Copyright (c) 2005-2008 Novell, Inc. (http://www.novell.com)
//
// Authors:
//      Jonathan Chambers	(jonathan.chambers@ansys.com)
//      Ivan N. Zlatev		(contact@i-nz.net)
//
//

// NOT COMPLETE

using System;
using System.Collections;
using System.ComponentModel.Design;
using System.Drawing;
using System.Drawing.Design;
using System.ComponentModel;
using System.Threading;
using System.Windows.Forms.Design;

namespace System.Windows.Forms.PropertyGridInternal {
	internal class PropertyGridView : ScrollableControl, IWindowsFormsEditorService {
		// Draws into a memory DC on Windows (double-buffered), where win32k blends its text.
		internal override bool GdiTextOnMemorySurface => true;


		#region Private Members
		private const char PASSWORD_PAINT_CHAR = '\u25cf'; // the dot char
		private const char PASSWORD_TEXT_CHAR = '*';
		private const int V_INDENT = 16;
		private const int ENTRY_SPACING = 2;
		/// <summary>How far a row's caption starts past the outline column, measured off a stock
		/// grid.</summary>
		private const int LABEL_PAD = 5;
		/// <summary>The cell the expander is drawn in.</summary>
		private const int PLUS_MINUS_SIZE = 8;
		private const int RESIZE_WIDTH = 3;
		private const int BUTTON_WIDTH = 25;
		private const int VALUE_PAINT_WIDTH = 19;
		private const int VALUE_PAINT_INDENT = 27;
		private double splitter_percent = .5;
		private int row_height;

		/// <summary>The gap Windows leaves between the frame around the list and its first row.
		/// Used by everything that turns a row into a position and back -- the painting, the hit
		/// testing, the editor's placement, scrolling one into view -- so they stay in step.
		/// </summary>
		internal const int TopMargin = 1;

		/// <summary>How tall one row is, and where the rows begin: what an assistive
		/// technology needs to say where a property sits.</summary>
		/// <summary>The strip one row occupies, which is a pixel shorter than the step from one
		/// row to the next: Windows leaves a hairline between them, as it does between the rows
		/// of a details view.</summary>
		internal int RowHeight {
			get { return Math.Max (1, row_height - 1); }
		}

		internal GridItem RootItem {
			get { return property_grid == null ? null : property_grid.RootGridItem; }
		}

		internal int RowWidth {
			get {
				int width = ClientRectangle.Width - 1;
				if (vbar != null && vbar.Visible)
					width -= vbar.Width;
				return Math.Max (0, width);
			}
		}
		private int font_height_padding = 3;
		private PropertyGridTextBox grid_textbox;
		private PropertyGrid property_grid;
		private bool resizing_grid;
		private PropertyGridDropDown dropdown_form;
		private Form dialog_form;
		private ImplicitVScrollBar vbar;
		private StringFormat string_format;
		private Font bold_font;
		private Brush inactive_text_brush;
		private ListBox dropdown_list;
		private Point last_click;
		private Padding dropdown_form_padding;
		#endregion

		#region Contructors
		public PropertyGridView (PropertyGrid propertyGrid) {
			property_grid = propertyGrid;

			string_format = new StringFormat ();
			string_format.FormatFlags = StringFormatFlags.NoWrap;
			string_format.Trimming = StringTrimming.None;

			grid_textbox = new PropertyGridTextBox ();
			grid_textbox.DropDownButtonClicked +=new EventHandler (DropDownButtonClicked);
			grid_textbox.DialogButtonClicked +=new EventHandler (DialogButtonClicked);

			dropdown_form = new PropertyGridDropDown ();
			dropdown_form.FormBorderStyle = FormBorderStyle.None;
			dropdown_form.StartPosition = FormStartPosition.Manual;
			dropdown_form.ShowInTaskbar = false;

			dialog_form = new Form ();
			dialog_form.StartPosition = FormStartPosition.Manual;
			dialog_form.FormBorderStyle = FormBorderStyle.None;
			dialog_form.ShowInTaskbar = false;

			dropdown_form_padding = new Padding (0, 0, 2, 2);

			row_height = Font.Height + font_height_padding;

			grid_textbox.Visible = false;
			grid_textbox.Font = this.Font;
			grid_textbox.BackColor = SystemColors.Window;
			grid_textbox.Validate += new CancelEventHandler (grid_textbox_Validate);
			grid_textbox.ToggleValue+=new EventHandler (grid_textbox_ToggleValue);
			grid_textbox.KeyDown+=new KeyEventHandler (grid_textbox_KeyDown);
			this.Controls.Add (grid_textbox);

			vbar = new ImplicitVScrollBar ();
			vbar.Visible = false;
			vbar.Value = 0;
			vbar.ValueChanged+=new EventHandler (VScrollBar_HandleValueChanged);
			vbar.Dock = DockStyle.Right;
			this.Controls.AddImplicit (vbar);

			resizing_grid = false;

			bold_font = new Font (this.Font, FontStyle.Bold);
			inactive_text_brush = new SolidBrush (ThemeEngine.Current.ColorGrayText);

			ForeColorChanged+=new EventHandler (RedrawEvent);
			BackColorChanged+=new System.EventHandler (RedrawEvent);
			FontChanged+=new EventHandler (RedrawEvent);
			
			SetStyle (ControlStyles.Selectable, true);
			SetStyle (ControlStyles.DoubleBuffer, true);
			SetStyle (ControlStyles.UserPaint, true);
			SetStyle (ControlStyles.AllPaintingInWmPaint, true);
			SetStyle (ControlStyles.ResizeRedraw, true);
		}

		#endregion

		private GridEntry RootGridItem {
			get { return (GridEntry)property_grid.RootGridItem; }
		}

		private GridEntry SelectedGridItem {
			get { return (GridEntry)property_grid.SelectedGridItem; }
			set { property_grid.SelectedGridItem = value; }
		}

		#region Protected Instance Methods

		protected override void OnFontChanged (EventArgs e) {
			base.OnFontChanged (e);

			bold_font = new Font (this.Font, FontStyle.Bold);
			row_height = Font.Height + font_height_padding;
		}

		private void InvalidateItemLabel (GridEntry item)
		{
			Invalidate (new Rectangle (0, ((GridEntry)item).Top, SplitterLocation, row_height));
		}

		private void InvalidateItem (GridEntry item)
		{
			if (item == null)
				return;

			Rectangle rect = new Rectangle (0, item.Top, Width, row_height);
			Invalidate (rect);

			if (item.Expanded) {
				rect = new Rectangle (0, item.Top + row_height, Width,
						      Height - (item.Top + row_height));
				Invalidate (rect);
			}
		}

		// [+] expanding is handled in OnMouseDown, so in order to prevent 
		// duplicate expanding ignore it here.
		// 
		protected override void OnDoubleClick (EventArgs e) 
		{
			if (this.SelectedGridItem != null && this.SelectedGridItem.Expandable && 
			    !this.SelectedGridItem.PlusMinusBounds.Contains (last_click))
				this.SelectedGridItem.Expanded = !this.SelectedGridItem.Expanded;
			else
				ToggleValue (this.SelectedGridItem);
		}

		protected override void OnPaint (PaintEventArgs e) 
		{
			// Background
			e.Graphics.FillRectangle (ThemeEngine.Current.ResPool.GetSolidBrush (BackColor), ClientRectangle);
			
			int yLoc = TopMargin - vbar.Value*row_height;
			if (this.RootGridItem != null) {
				DrawGridItems (this.RootGridItem.GridItems, e, 1, ref yLoc);
				// .NET closes the last row with one more rule.
				if (yLoc > TopMargin && yLoc - 1 < ClientRectangle.Height)
					using (Pen pen = new Pen (property_grid.LineColor))
						e.Graphics.DrawLine (pen, 0, yLoc - 1, ClientRectangle.Width - (vbar.Visible ? vbar.Width : 0), yLoc - 1);
			}

			UpdateScrollBar ();
		}

		protected override void OnMouseWheel (MouseEventArgs e) 
		{
			if (vbar == null || !vbar.Visible)
				return;
			if (e.Delta < 0)
				vbar.Value = Math.Min (vbar.Maximum - GetVisibleRowsCount () + 1, vbar.Value + SystemInformation.MouseWheelScrollLines);
			else
				vbar.Value = Math.Max (0, vbar.Value - SystemInformation.MouseWheelScrollLines);
			base.OnMouseWheel (e);
			// Taken here: the notch does not go on to whatever this sits in.
			HandleMouseWheel (e);
		}


		protected override void OnMouseMove (MouseEventArgs e) {
			if (this.RootGridItem == null)
				return;

			if (resizing_grid) {
				int loc = Math.Max (e.X,2*V_INDENT);
				SplitterPercent = 1.0*loc/Width;
			}
			if (e.X > SplitterLocation - RESIZE_WIDTH && e.X < SplitterLocation + RESIZE_WIDTH) 
				this.Cursor = Cursors.SizeWE;
			else
				this.Cursor = Cursors.Default;
			base.OnMouseMove (e);
		}

		protected override void OnMouseDown (MouseEventArgs e) 
		{
			base.OnMouseDown (e);
			// Clicking in the grid is what puts the keyboard in it, and the row's value field only
			// appears once it is there.
			if (CanSelect && !ContainsFocus)
				Focus ();
			last_click = e.Location;
			if (this.RootGridItem == null)
				return;

			if (e.X > SplitterLocation - RESIZE_WIDTH && e.X < SplitterLocation + RESIZE_WIDTH) {
				resizing_grid = true;
			}
			else {
				int offset = TopMargin - vbar.Value*row_height;
				GridItem foundItem = GetSelectedGridItem (this.RootGridItem.GridItems, e.Y, ref offset);

				if (foundItem != null) {
					if (foundItem.Expandable && ((GridEntry)foundItem).PlusMinusBounds.Contains (e.X, e.Y))
						foundItem.Expanded = !foundItem.Expanded;
					
					this.SelectedGridItem = (GridEntry)foundItem;
					if (!GridLabelHitTest (e.X)) {
						// send mouse down so we get the carret under cursor
						grid_textbox.SendMouseDown (PointToScreen (e.Location));
					}
				}
			}
		}

		protected override void OnMouseUp (MouseEventArgs e) {
			resizing_grid = false;
			base.OnMouseUp (e);
		}

		protected override void OnResize (EventArgs e) {
			base.OnResize (e);
			if (this.SelectedGridItem != null) // initialized already
				UpdateView ();
		}

		private void UnfocusSelection ()
		{
			Select (this);
		}

		private void FocusSelection ()
		{
			Select (grid_textbox);
		}

		protected override bool ProcessDialogKey (Keys keyData) {
			GridEntry selectedItem = this.SelectedGridItem;
			// F4 opens the selected entry's editor, the drop-down or else the dialog -- .NET's
			// F4Selection. Without it a keyboard could reach an entry and never open its editor.
			if (keyData == Keys.F4 && selectedItem != null && selectedItem.GridItemType != GridItemType.Category) {
				if (grid_textbox.DropDownButtonVisible)
					DropDownEdit ();
				else if (grid_textbox.DialogButtonVisible)
					DialogButtonClicked (this, EventArgs.Empty);
				return true;
			}
			if (selectedItem != null
			    && grid_textbox.Visible) {
				switch (keyData) {
				case Keys.Enter:
					if (TrySetEntry (selectedItem, grid_textbox.Text))
						UnfocusSelection ();
					return true;
				case Keys.Escape:
					if (selectedItem.IsEditable)
						UpdateItem (selectedItem); // reset value
					UnfocusSelection ();
					return true;
				case Keys.Tab:
					FocusSelection ();
					return true;
				default:
					return false;
				}
			}
			return base.ProcessDialogKey (keyData);
		}

		/// <summary>The text a value was last refused for, and whether the complaint is on screen.
		/// Both keep one bad value from asking over and over.</summary>
		private string rejected_text;
		private bool showing_error;

		private bool TrySetEntry (GridEntry entry, object value)
		{
			if (entry == null || grid_textbox.Text.Equals (entry.ValueText))
				return true;

			if (entry.IsEditable || !entry.IsEditable && (entry.HasCustomEditor || entry.AcceptedValues != null) ||
			    !entry.IsMerged || entry.HasMergedValue || 
			    (!entry.HasMergedValue && grid_textbox.Text != String.Empty)) {
				string error = null;
				bool changed = entry.SetValue (value, out error);
				if (!changed && error != null) {
					// Say it once for a given text. Refusing the value cancels the validation, which brings
					// the field straight back for another go -- so answering OK put the same complaint up
					// again, and again, with nothing but Cancel to escape it. The text is left as it is for
					// correcting; the message returns as soon as it changes and is still wrong.
					if (showing_error || string.Equals (rejected_text, grid_textbox.Text))
						return false;
					showing_error = true;
					try {
						if (property_grid.ShowError (error, MessageBoxButtons.OKCancel) == DialogResult.Cancel) {
							rejected_text = null;
							UpdateItem (entry); // restore value, repaint, etc
							UnfocusSelection ();
						} else {
							rejected_text = grid_textbox.Text;
						}
					} finally {
						showing_error = false;
					}
					return false;
				}
			}
			rejected_text = null;
			UpdateItem (entry); // restore value, repaint, etc
			return true;
		}

		protected override bool IsInputKey (Keys keyData) {
			switch (keyData) {
			case Keys.Left:
			case Keys.Right:
			case Keys.Enter:
			case Keys.Escape:
			case Keys.Up:
			case Keys.Down:
			case Keys.PageDown:
			case Keys.PageUp:
			case Keys.Home:
			case Keys.End:
				return true;
			default:
				return false;
			}
		}

		private GridEntry MoveUpFromItem (GridEntry item, int up_count)
		{
			GridItemCollection items;
			int index;

			/* move back up the visible rows (and up the hierarchy as necessary) until
			   up_count == 0, or we reach the top of the display */
			while (up_count > 0) {
				items = item.Parent != null ? item.Parent.GridItems : this.RootGridItem.GridItems;
				index = items.IndexOf (item);

				if (index == 0) {
					if (item.Parent.GridItemType == GridItemType.Root) // we're at the top row
						return item;
					item = (GridEntry)item.Parent;
					up_count --;
				}
				else {
					GridEntry prev_item = (GridEntry)items[index-1];
					if (prev_item.Expandable && prev_item.Expanded) {
						item = (GridEntry)prev_item.GridItems[prev_item.GridItems.Count - 1];
					}
					else {
						item = prev_item;
					}
					up_count --;
				}
			}
			return item;
		}

		private GridEntry MoveDownFromItem (GridEntry item, int down_count)
		{
			while (down_count > 0) {
				/* if we're a parent node and we're expanded, move to our first child */
				if (item.Expandable && item.Expanded) {
					item = (GridEntry)item.GridItems[0];
					down_count--;
				}
				else {
					GridItem searchItem = item;
					GridItemCollection searchItems = searchItem.Parent.GridItems;
					int searchIndex = searchItems.IndexOf (searchItem);

					while (searchIndex == searchItems.Count - 1) {
						searchItem = searchItem.Parent;

						if (searchItem == null || searchItem.Parent == null)
							break;

						searchItems = searchItem.Parent.GridItems;
						searchIndex = searchItems.IndexOf (searchItem);
					}

					if (searchIndex == searchItems.Count - 1) {
						/* if we got all the way back to the root with no nodes after
						   us, the original item was the last one */
						return item;
					}
					else {
						item = (GridEntry)searchItems[searchIndex+1];
						down_count--;
					}
				}
			}

			return item;
		}

		protected override void OnKeyDown (KeyEventArgs e) 
		{
			GridEntry selectedItem = this.SelectedGridItem;

			if (selectedItem == null) {
				/* XXX not sure what MS does, but at least we shouldn't crash */
				base.OnKeyDown (e);
				return;
			}

			switch (e.KeyData & Keys.KeyCode) {
			case Keys.Left:
				if (e.Control) {
					if (SplitterLocation > 2 * V_INDENT)
						SplitterPercent -= 0.01;

					e.Handled = true;
					break;
				}
				else {
					/* if the node is expandable and is expanded, collapse it.
					   otherwise, act just like the user pressed up */
					if (selectedItem.Expandable && selectedItem.Expanded) {
						selectedItem.Expanded = false;
						e.Handled = true;
						break;
					}
					else
						goto case Keys.Up;
				}
			case Keys.Right:
				if (e.Control) {
					if (SplitterLocation < Width)
						SplitterPercent += 0.01;

					e.Handled = true;
					break;
				}
				else {
					/* if the node is expandable and not expanded, expand it.
					   otherwise, act just like the user pressed down */
					if (selectedItem.Expandable && !selectedItem.Expanded) {
						selectedItem.Expanded = true;
						e.Handled = true;
						break;
					}
					else
						goto case Keys.Down;
				}
			case Keys.Enter:
				/* toggle the expanded state of the selected item */
				if (selectedItem.Expandable) {
					selectedItem.Expanded = !selectedItem.Expanded;
				}
				e.Handled = true;
				break;
			case Keys.Up:
				this.SelectedGridItem = MoveUpFromItem (selectedItem, 1);
				e.Handled = true;
				break;
			case Keys.Down:
				this.SelectedGridItem = MoveDownFromItem (selectedItem, 1);
				e.Handled = true;
				break;
			case Keys.PageUp:
				this.SelectedGridItem = MoveUpFromItem (selectedItem, vbar.LargeChange);
				e.Handled = true;
				break;
			case Keys.PageDown:
				this.SelectedGridItem = MoveDownFromItem (selectedItem, vbar.LargeChange);
				e.Handled = true;
				break;
			case Keys.End:
				/* find the last, most deeply nested visible item */
				GridEntry item = (GridEntry)this.RootGridItem.GridItems[this.RootGridItem.GridItems.Count - 1];
				while (item.Expandable && item.Expanded)
					item = (GridEntry)item.GridItems[item.GridItems.Count - 1];
				this.SelectedGridItem = item;
				e.Handled = true;
				break;
			case Keys.Home:
				this.SelectedGridItem = (GridEntry)this.RootGridItem.GridItems[0];
				e.Handled = true;
				break;
			}

			base.OnKeyDown (e);
		}

		#endregion

		#region Private Helper Methods

		private int SplitterLocation {
			get {
				// Against the room the ROWS have, which is the width less the scroll bar -- and less it
				// whether or not the bar is showing, so the column does not jump the moment a property is
				// added. Measured against a stock grid: 260 wide, its divider stands at 121, which is half
				// of what is left after the bar. Taken against the whole width ours stood at 130.
				int usable = Width - (vbar != null ? vbar.Width : 0);
				return (int)(splitter_percent * usable);
			}
		}

		private double SplitterPercent {
			set {
				int old_splitter_location = SplitterLocation;
				
				splitter_percent = Math.Max (Math.Min (value, .9),.1);

				if (old_splitter_location != SplitterLocation) {
					int x = old_splitter_location > SplitterLocation ? SplitterLocation : old_splitter_location;
					Invalidate (new Rectangle (x, 0, Width - x - (vbar.Visible ? vbar.Width : 0), Height));
					UpdateItem (this.SelectedGridItem);
				}
			}
			get {
				return splitter_percent;
			}
		}

		private bool GridLabelHitTest (int x)
		{
			if (0 <= x && x <= splitter_percent * this.Width)
				return true;
			return false;
		}

		private GridItem GetSelectedGridItem (GridItemCollection grid_items, int y, ref int current) {
			foreach (GridItem child_grid_item in grid_items) {
				if (y > current && y < current + row_height) {
					return child_grid_item;
				}
				current += row_height;
				if (child_grid_item.Expanded) {
					GridItem foundItem = GetSelectedGridItem (child_grid_item.GridItems, y, ref current);
					if (foundItem != null)
						return foundItem;
				}
			}
			return null;
		}

		private int GetVisibleItemsCount (GridEntry entry)
		{
			if (entry == null)
				return 0;

			int count = 0;
			foreach (GridEntry e in entry.GridItems) {
				count += 1;
				if (e.Expandable && e.Expanded)
					count += GetVisibleItemsCount (e);
			}
			return count;
		}

		private int GetVisibleRowsCount ()
		{
			return this.Height / row_height;
		}

		private void UpdateScrollBar ()
		{
			if (this.RootGridItem == null)
				return;

			int visibleRows = GetVisibleRowsCount ();
			int openedItems = GetVisibleItemsCount (this.RootGridItem);
			if (openedItems > visibleRows) {
				vbar.Visible = true;
				vbar.SmallChange = 1;
				vbar.LargeChange = visibleRows;
				vbar.Maximum = Math.Max (0, openedItems - 1);
			} else {
				vbar.Value = 0;
				vbar.Visible = false;
			}
			UpdateGridTextBoxBounds (this.SelectedGridItem);
		}

		// private bool GetScrollBarVisible ()
		// {
		// 	if (this.RootGridItem == null)
		// 		return false;
                // 
		// 	int visibleRows = GetVisibleRowsCount ();
		// 	int openedItems = GetVisibleItemsCount (this.RootGridItem);
		// 	if (openedItems > visibleRows)
		// 		return true;
		// 	return false;
		// }
		#region Drawing Code

		// .NET's PropertyGridView.OnPaint / DrawLabel / DrawValue and GridEntry.PaintLabel / PaintValue,
		// in this view's coordinates -- the border host puts it a pixel inside the frame, where .NET's
		// view draws its own, so everything here is .NET's position less (1, 1). Row k's label and
		// value rectangles start at 1 + k * (RowHeight + 1) and are RowHeight tall; the rule above
		// each row is drawn at the row's top less one; the divider at the label width.
		private const int OutlineIconSize = 16, OutlineIconPadding = 5, ValuePaintIndent = 26, ValuePaintWidth = 20;

		private void DrawGridItems (GridItemCollection grid_items, PaintEventArgs pevent, int depth, ref int yLoc) {
			foreach (GridItem grid_item in grid_items) {
				DrawGridItem ((GridEntry)grid_item, pevent, depth, ref yLoc);
				if (grid_item.Expanded)
					DrawGridItems (grid_item.GridItems, pevent, (grid_item.GridItemType == GridItemType.Category) ? depth : depth+1, ref yLoc);
			}
		}

		// .NET's _propertyDepth: a category and the properties directly under it are both depth 0.
		private static int StockDepth (int depth)
		{
			return Math.Max (0, depth - 1);
		}

		private static int LabelIndent (GridEntry item, int depth)
		{
			int icon = OutlineIconSize + OutlineIconPadding;
			return item.GridItemType == GridItemType.Category
				? 1 + icon + StockDepth (depth) * 10
				: (StockDepth (depth) + 1) * icon + 1;
		}

		private const TextFormatFlags LabelFlags = TextFormatFlags.PreserveGraphicsClipping | TextFormatFlags.PreserveGraphicsTranslateTransform;

		private void DrawGridItemLabel (GridEntry grid_item, PaintEventArgs pevent, int depth, Rectangle rect) {
			Graphics g = pevent.Graphics;
			bool category = grid_item.GridItemType == GridItemType.Category;
			bool selected = grid_item == this.SelectedGridItem && !category;
			bool focus = Focused || ContainsFocus;
			Color line = property_grid.LineColor;
			Color back = category ? line : BackColor;
			Color fill = selected ? (focus ? SystemColors.Highlight : line) : back;
			int icon = OutlineIconSize + OutlineIconPadding;
			int indent = LabelIndent (grid_item, depth);
			Font font = category ? bold_font : this.Font;

			using (var b = new SolidBrush (fill))
				g.FillRectangle (b, rect);
			using (var b = new SolidBrush (line))
				g.FillRectangle (b, rect.X, rect.Y, icon, rect.Height);
			if (selected && focus)
				g.FillRectangle (SystemBrushes.Highlight, rect.X + indent, rect.Y, rect.Width - indent - 1, rect.Height);

			string label = grid_item.Label ?? string.Empty;
			int textWidth = TextRenderer.MeasureText (g, label, font, new Size (int.MaxValue, int.MaxValue), LabelFlags).Width;
			var textRect = new Rectangle (rect.X + indent, rect.Y + 1, Math.Min (rect.Width - indent - 1, textWidth + 2), rect.Height - 1);
			Color color = selected && focus ? SystemColors.HighlightText
				: category ? property_grid.CategoryForeColor
				: grid_item.IsReadOnly ? ThemeEngine.Current.ColorGrayText : ForeColor;
			if (textRect.Width > 0 && textRect.Height > 0) {
				System.Drawing.Drawing2D.GraphicsState state = g.Save ();
				g.IntersectClip (textRect);
				TextRenderer.DrawText (g, label, font, textRect, color, LabelFlags);
				g.Restore (state);
			}
			if (category && grid_item == this.SelectedGridItem && focus)
				ControlPaint.DrawFocusRectangle (g, new Rectangle (rect.X + indent - 2, rect.Y, textWidth + 3, rect.Height - 1));
		}

		private void DrawGridItemValue (GridEntry grid_item, PaintEventArgs pevent, int depth, Rectangle rect)
		{
			Graphics g = pevent.Graphics;
			bool category = grid_item.GridItemType == GridItemType.Category;
			using (var b = new SolidBrush (category ? property_grid.LineColor : BackColor))
				g.FillRectangle (b, rect);
			if (category || grid_item.PropertyDescriptor == null)
				return;

			int num = 0;
			if (grid_item.PaintValueSupported) {
				num = ValuePaintIndent;
				var swatch = new Rectangle (rect.X + 1, rect.Y + 1, ValuePaintWidth, RowHeight - 2);
				grid_item.PaintValue (g, swatch);
				swatch.Width--;
				swatch.Height--;
				g.DrawRectangle (SystemPens.WindowText, swatch);
			}

			string valueText = String.Empty;
			if (!grid_item.IsMerged || grid_item.IsMerged && grid_item.HasMergedValue) {
				if (grid_item.IsPassword)
					valueText = new String (PASSWORD_PAINT_CHAR, grid_item.ValueText.Length);
				else
					valueText = grid_item.ValueText;
			}
			if (string.IsNullOrEmpty (valueText))
				return;
			Font font = (grid_item.IsResetable || !grid_item.HasDefaultValue) ? bold_font : this.Font;
			Color fore = grid_item.IsReadOnly ? ThemeEngine.Current.ColorGrayText : ForeColor;
			// rect less the painted swatch, then (1, 2) in for painting in place, then the text box
			// a pixel back up and left and four narrower.
			var bounds = new Rectangle (rect.X + num, rect.Y + 1, rect.Width - num - 4, rect.Height);
			TextRenderer.DrawText (g, valueText, font, bounds, fore, BackColor,
				TextFormatFlags.TextBoxControl | TextFormatFlags.ExpandTabs | TextFormatFlags.NoClipping
				| TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | LabelFlags);
		}

		private void DrawGridItem (GridEntry grid_item, PaintEventArgs pevent, int depth, ref int yLoc) {
			if (yLoc > -row_height && yLoc < ClientRectangle.Height) {
				int divider = SplitterLocation;
				int width = ClientRectangle.Width - (vbar.Visible ? vbar.Width : 0);
				using (Pen pen = new Pen (property_grid.LineColor))
					pevent.Graphics.DrawLine (pen, 0, yLoc - 1, width, yLoc - 1);
				DrawGridItemValue (grid_item, pevent, depth,
						  new Rectangle (divider + 1, yLoc, width - divider - 1, RowHeight));
				DrawGridItemLabel (grid_item, pevent, depth, new Rectangle (0, yLoc, divider, RowHeight));
				using (Pen pen = new Pen (property_grid.LineColor))
					pevent.Graphics.DrawLine (pen, divider, yLoc - 1, divider, yLoc + RowHeight);
				// A category after the first carries the splitter colour along its top.
				if (grid_item.GridItemType == GridItemType.Category && grid_item.Parent != null
				    && grid_item.Parent.GridItems.IndexOf (grid_item) > 0)
					using (Pen pen = new Pen (property_grid.CategorySplitterColor))
						pevent.Graphics.DrawLine (pen, 0, yLoc - 1, width, yLoc - 1);

				if (grid_item.Expandable) {
					// GridEntry.OutlineRectangle: 16 square, OutlineIconPadding / 2 in per depth step,
					// centred in the row; the glyph centred in that.
					int d = StockDepth (depth);
					var outline = new Rectangle (d * (OutlineIconSize + OutlineIconPadding) + OutlineIconPadding / 2,
								     yLoc + (RowHeight - OutlineIconSize) / 2, OutlineIconSize, OutlineIconSize);
					if (VisualStyles.VisualStyleRenderer.IsSupported) {
						PaintOutlineWithExplorerTreeStyle (pevent.Graphics, outline, grid_item.Expanded);
						grid_item.PlusMinusBounds = new Rectangle (outline.X + (OutlineIconSize - PLUS_MINUS_SIZE) / 2,
											   outline.Y + (OutlineIconSize - PLUS_MINUS_SIZE) / 2, PLUS_MINUS_SIZE, PLUS_MINUS_SIZE);
					} else
						grid_item.PlusMinusBounds = DrawPlusMinus (pevent.Graphics,
										   outline.X + (OutlineIconSize - PLUS_MINUS_SIZE) / 2,
										   outline.Y + (OutlineIconSize - PLUS_MINUS_SIZE) / 2,
										   grid_item.Expanded, grid_item.GridItemType == GridItemType.Category);
				}
			}
			grid_item.Top = yLoc;
			yLoc += row_height;
		}

		// .NET's GridEntry.PaintOutlineWithExplorerTreeStyle: the opened glyph drawn as it is; the
		// closed one into a bitmap of the line colour, then every pixel far enough from that colour in
		// luminosity inverted (RedrawExplorerTreeViewClosedGlyph, ControlPaint.InvertForeColorIfNeeded).
		private void PaintOutlineWithExplorerTreeStyle (Graphics g, Rectangle outline, bool expanded)
		{
			if (expanded) {
				VisualStyles.Win11Frames.Draw (g, VisualStyles.Win11Frames.ExplorerTreeGlyph (true), outline);
				return;
			}
			Color line = property_grid.LineColor;
			int [] px = VisualStyles.Win11Frames.Composite (VisualStyles.Win11Frames.ExplorerTreeGlyph (false),
									 outline.Width, outline.Height, line);
			ControlPaint.InvertForeColorIfNeeded (px, line);
			using (var bmp = new Bitmap (outline.Width, outline.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb)) {
				var data = bmp.LockBits (new Rectangle (0, 0, outline.Width, outline.Height),
							 System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
				for (int y = 0; y < outline.Height; y++)
					System.Runtime.InteropServices.Marshal.Copy (px, y * outline.Width, data.Scan0 + y * data.Stride, outline.Width);
				bmp.UnlockBits (data);
				g.DrawImage (bmp, outline, 0, 0, outline.Width, outline.Height, GraphicsUnit.Pixel);
			}
		}

		private Rectangle DrawPlusMinus (Graphics g, int x, int y, bool expanded, bool category) {
			Rectangle bounds = new Rectangle (x, y, PLUS_MINUS_SIZE, PLUS_MINUS_SIZE);
			ThemeEngine.Current.DrawPropertyGridExpander (g, bounds, expanded, category, property_grid.ViewForeColor);
			return bounds;
		}

		#endregion

		#region Event Handling
		private void RedrawEvent (object sender, System.EventArgs e) 
		{
			Refresh ();
		}

		#endregion

		/// <summary>.NET's LogicalMaxListBoxHeight: a list of standard values stops growing here.</summary>
		private const int MaxListBoxHeight = 200;

		/// <summary>tmMaxCharWidth of the grid's font, which .NET adds to a list's width as padding.</summary>
		private int MaxCharWidth ()
		{
			// The widest a character of the face gets; "W" stands for it closely enough in the fonts a
			// grid is set in, and the measurement is GDI's (no padding).
			return TextRenderer.MeasureText ("W", Font, new Size (int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding).Width;
		}

		/// <summary>.NET's GetRectangle (row, RowValue): the value cell of an entry's row.</summary>
		private Rectangle ValueRectangle (GridEntry entry)
		{
			int width = ClientRectangle.Width - (vbar.Visible ? vbar.Width : 0);
			return new Rectangle (SplitterLocation + 1, entry.Top, width - SplitterLocation - 1, RowHeight);
		}

		private void listBox_DrawItem (object sender, DrawItemEventArgs e)
		{
			if (e.Index < 0 || !(sender is ListBox list))
				return;
			e.DrawBackground ();
			e.DrawFocusRectangle ();
			// .NET: bounds a pixel down and a pixel left, then GridEntry.PaintValue -- the text where
			// the grid puts a value's text in its cell.
			Rectangle bounds = e.Bounds;
			bounds.Y += 1;
			bounds.X -= 1;
			string text = list.Items [e.Index].ToString ();
			bool selected = (e.State & DrawItemState.Selected) != 0;
			var text_bounds = new Rectangle (bounds.X, bounds.Y, bounds.Width - 4, bounds.Height);
			TextRenderer.DrawText (e.Graphics, text, Font, text_bounds, selected ? SystemColors.HighlightText : ForeColor,
				TextFormatFlags.TextBoxControl | TextFormatFlags.ExpandTabs | TextFormatFlags.NoClipping
				| TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | LabelFlags);
		}

		private void listBox_MouseUp (object sender, MouseEventArgs e) {
			AcceptListBoxSelection (sender);
		}

		private void listBox_KeyDown (object sender, KeyEventArgs e)
		{
			switch (e.KeyData & Keys.KeyCode) {
			case Keys.Enter:
				AcceptListBoxSelection (sender);
				return;
			case Keys.Escape:
				CloseDropDown ();
				return;
			}
		}

		void AcceptListBoxSelection (object sender) 
		{
			GridEntry entry = this.SelectedGridItem as GridEntry;
			if (entry != null) {
				grid_textbox.Text = (string) ((ListBox) sender).SelectedItem;
				CloseDropDown ();
				if (TrySetEntry (entry, grid_textbox.Text))
					UnfocusSelection ();
			}
		}

		private void DropDownButtonClicked (object sender, EventArgs e) 
		{
			DropDownEdit ();
		}

		private void DropDownEdit ()
		{
			GridEntry entry = this.SelectedGridItem as GridEntry;
			if (entry == null)
				return;

			if (entry.HasCustomEditor) {
				entry.EditValue ((IWindowsFormsEditorService) this);
			} else {
				if (dropdown_form.Visible) {
					CloseDropDown ();
				}
				else {
					ICollection std_values = entry.AcceptedValues;
					if (std_values != null) {
						if (dropdown_list == null) {
							// .NET's GridViewListBox: no border of its own (the holder has the one
							// line round it), owner drawn, on the grid's own background.
							dropdown_list = new ListBox {
								BorderStyle = BorderStyle.None,
								IntegralHeight = false,
								DrawMode = DrawMode.OwnerDrawFixed,
							};
							dropdown_list.KeyDown += new KeyEventHandler (listBox_KeyDown);
							dropdown_list.MouseUp += new MouseEventHandler (listBox_MouseUp);
							dropdown_list.DrawItem += listBox_DrawItem;
						}
						dropdown_list.BackColor = BackColor;
						dropdown_list.Font = Font;
						dropdown_list.Items.Clear ();
						// A row of the list is a row of the grid's strip (RowHeight), a pixel under the
						// grid's pitch; left to size itself from the font it came out four pixels short.
						dropdown_list.ItemHeight = RowHeight;
						// Nothing selected when the value is none of the list's (a flags value such as
						// "Cheese, Basil"): .NET's GetCurrentValueIndex answers -1 and the list keeps it.
						int selected_index = -1;
						int i = 0;
						string valueText = entry.ValueText;
						foreach (object obj in std_values) {
							dropdown_list.Items.Add (obj);
							if (valueText != null && valueText.Equals (obj))
								selected_index = i;
							i++;
						}
						// .NET: as tall as its rows up to a limit (a line of text at the least), as wide as
						// the widest value plus border, padding (the widest character) and a scroll bar,
						// and never narrower than the value column.
						int widest = 0;
						foreach (object item in dropdown_list.Items)
							widest = Math.Max (widest, TextRenderer.MeasureText (item.ToString (), Font,
								new Size (int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width);
						widest += 2 + MaxCharWidth () + SystemInformation.VerticalScrollBarWidth;
						// PreferredHeight: the rows plus the allowance ListBox makes for a border
						// (BorderSize * 4 + 3), which .NET's list keeps -- its BorderStyle stays Fixed3D,
						// only the window style loses the border. Stock's 2, 5 and 7 rows: 43, 97, 133.
						int preferred = RowHeight * dropdown_list.Items.Count + SystemInformation.BorderSize.Height * 4 + 3;
						dropdown_list.Height = Math.Max (Font.Height + 2, Math.Min (MaxListBoxHeight, preferred));
						dropdown_list.Width = Math.Max (widest, ValueRectangle (entry).Width);
						if (selected_index != -1)
							dropdown_list.SelectedIndex = selected_index;
						DropDownControl (dropdown_list);
					}
				}
			}
		}

		private void DialogButtonClicked (object sender, EventArgs e) 
		{
			GridEntry entry = this.SelectedGridItem as GridEntry;
			if (entry != null && entry.HasCustomEditor)
				entry.EditValue ((IWindowsFormsEditorService) this);
		}

		private void VScrollBar_HandleValueChanged (object sender, EventArgs e) 
		{
			UpdateView ();
		}

		private void grid_textbox_ToggleValue (object sender, EventArgs args) 
		{
			ToggleValue (this.SelectedGridItem);
		}

		private void grid_textbox_KeyDown (object sender, KeyEventArgs e) 
		{
			switch (e.KeyData & Keys.KeyCode) {
			case Keys.Down:
				if (e.Alt) {
					DropDownEdit ();
					e.Handled = true;
				}
				break;
			}
		}

		private void grid_textbox_Validate (object sender, CancelEventArgs args)
		{
			if (!TrySetEntry (this.SelectedGridItem, grid_textbox.Text))
				args.Cancel = true;
		}

		private void ToggleValue (GridEntry entry)
		{
			if (entry != null && !entry.IsReadOnly && entry.GridItemType == GridItemType.Property)
				entry.ToggleValue ();
		}

		protected override void OnGotFocus (EventArgs e)
		{
			base.OnGotFocus (e);
			UpdateItem (this.SelectedGridItem);
			Invalidate ();
		}

		protected override void OnLostFocus (EventArgs e)
		{
			base.OnLostFocus (e);
			// Only when the keyboard has left the grid altogether -- the value field is a child of
			// this control, so focus moving into it is not focus leaving.
			if (!ContainsFocus)
				UpdateItem (this.SelectedGridItem);
			Invalidate ();
		}

		internal void UpdateItem (GridEntry entry)
		{
			if (entry == null || entry.GridItemType == GridItemType.Category || 
			    entry.GridItemType == GridItemType.Root) {
				grid_textbox.Visible = false;
				InvalidateItem (entry);
				return;
			}

			// Nothing to edit with until the grid has the keyboard: Windows shows the field and its
			// button on the row being worked on, not on whatever happens to be selected in a grid
			// nobody has touched.
			if (this.SelectedGridItem == entry && (Focused || ContainsFocus)) {
				SuspendLayout ();
				grid_textbox.Visible = false;
				if (entry.IsResetable || !entry.HasDefaultValue)
					grid_textbox.Font = bold_font;
				else
					grid_textbox.Font = this.Font;

				if (entry.IsReadOnly) {
					grid_textbox.DropDownButtonVisible = false;
					grid_textbox.DialogButtonVisible = false;
					grid_textbox.ReadOnly = true;
					grid_textbox.ForeColor = SystemColors.GrayText;
				} else {
					grid_textbox.DropDownButtonVisible = entry.AcceptedValues != null || 
						entry.EditorStyle == UITypeEditorEditStyle.DropDown;
					grid_textbox.DialogButtonVisible = entry.EditorStyle == UITypeEditorEditStyle.Modal;
					grid_textbox.ForeColor = SystemColors.ControlText;
					grid_textbox.ReadOnly = !entry.IsEditable;
				}
				UpdateGridTextBoxBounds (entry);
				grid_textbox.PasswordChar = entry.IsPassword ? PASSWORD_TEXT_CHAR : '\0';
				grid_textbox.Text = entry.IsMerged && !entry.HasMergedValue ? String.Empty : entry.ValueText;
				grid_textbox.Visible = true;
				InvalidateItem (entry);
				ResumeLayout (false);
			} else {
				grid_textbox.Visible = false;
			}
		}

		private void UpdateGridTextBoxBounds (GridEntry entry)
		{
			if (entry == null || this.RootGridItem == null)
				return;

			int y = TopMargin - vbar.Value*row_height;
			CalculateItemY (entry, this.RootGridItem.GridItems, ref y);
			int x = SplitterLocation + ENTRY_SPACING + (entry.PaintValueSupported ? VALUE_PAINT_INDENT : 0);
			grid_textbox.SetBounds (x + ENTRY_SPACING, y + ENTRY_SPACING,
						ClientRectangle.Width - ENTRY_SPACING - x - (vbar.Visible ? vbar.Width : 0),
						row_height - ENTRY_SPACING);
		}

		// Calculates the sum of the heights of all items before the one
		//
		private bool CalculateItemY (GridEntry entry, GridItemCollection items, ref int y)
		{
			foreach (GridItem item in items) {
				if (item == entry)
					return true;
				y += row_height;
				if (item.Expandable && item.Expanded)
					if (CalculateItemY (entry, item.GridItems, ref y))
						return true;
			}
			return false;
		}

		private void ScrollToItem (GridEntry item)
		{
			if (item == null || this.RootGridItem == null)
				return;

			int itemY = TopMargin - vbar.Value*row_height;
			int value = vbar.Value;;
			CalculateItemY (item, this.RootGridItem.GridItems, ref itemY);
			if (itemY < 0) // the new item is above the viewable area
				value += itemY / row_height;
			else if (itemY + row_height > Height) // the new item is below the viewable area
				value += ((itemY + row_height) - Height) / row_height + 1;
			if (value >= vbar.Minimum && value <= vbar.Maximum)
				vbar.Value = value;
		}

		internal void SelectItem (GridEntry oldItem, GridEntry newItem) 
		{
			if (oldItem != null)
				InvalidateItemLabel (oldItem);
			if (newItem != null) {
				UpdateItem (newItem);
				ScrollToItem (newItem);
			} else {
				grid_textbox.Visible = false;
				vbar.Visible = false;
			}
		}

		internal void UpdateView ()
		{
			UpdateScrollBar ();
			Invalidate ();
			Update ();
			UpdateItem (this.SelectedGridItem);
		}

		internal void ExpandItem (GridEntry item)
		{
			UpdateItem (this.SelectedGridItem);
			Invalidate (new Rectangle (0, item.Top, Width, Height - item.Top));
		}

		internal void CollapseItem (GridEntry item)
		{
			UpdateItem (this.SelectedGridItem);
			Invalidate (new Rectangle (0, item.Top, Width, Height - item.Top));
		}

		/// <summary>A press anywhere but inside the drop-down takes it down, which is what Windows
		/// does with its mouse hook. The press is still delivered: a property grid is not a menu, and
		/// clicking another row has to select that row as well as close the drop-down.</summary>
		private void DropDownWatchClick (IntPtr window)
		{
			if (dropdown_form.Visible && !HwndInControl (dropdown_form, window))
				CloseDropDown ();
		}

		private void ShowDropDownControl (Control control, bool resizeable) 
		{
			// Parented first, with its handle, and only then measured -- .NET's SetDropDownControl.
			// A list that snaps to whole rows (IntegralHeight) has its real height only once it has a
			// handle: measured before that, the cursor editor's 310 made a holder 22 pixels taller
			// than the 288 of list it holds.
			dropdown_form.Controls.Add (control);
			// (Handle, not CreateControl: that skips a control that is not visible, and the holder
			// is not shown yet.)
			_ = dropdown_form.Handle;
			_ = control.Handle;
			control.Dock = DockStyle.None;

			// .NET's DropDownHolder: one line round the control, outside it (its WS_BORDER), and for a
			// resizable editor a bar the height of a scroll bar and a pixel more under it -- or over it,
			// when the drop-down has to open upwards -- with the grip in the corner. Ours was a
			// sizable window frame.
			dropdown_form.FormBorderStyle = FormBorderStyle.None;
			dropdown_form.SizeGripStyle = SizeGripStyle.Hide;
			// the grid's background under the bar, as .NET's holder takes the grid view's BackColor
			dropdown_form.BackColor = BackColor;
			int bar = resizeable ? PropertyGridDropDown.ResizeBarSize : 0;
			dropdown_form.Resizable = resizeable;
			dropdown_form.ResizeUp = false;
			dropdown_form.Padding = new Padding (1, 1, 1, 1 + bar);
			dropdown_form.Size = new Size (control.Width + 2, control.Height + 2 + bar);

			control.Dock = DockStyle.Fill;
			// .NET's DropDownControl: at least the value cell and a pixel wide, its right edge on the
			// cell's, and a pixel below the row.
			GridEntry selected_entry = SelectedGridItem as GridEntry;
			Rectangle value_rect = selected_entry != null ? ValueRectangle (selected_entry)
				: new Rectangle (grid_textbox.Left, grid_textbox.Top, grid_textbox.Width, row_height);
			dropdown_form.Width = Math.Max (value_rect.Width + 1, dropdown_form.Width);
			Point below = PointToScreen (new Point (value_rect.Right - dropdown_form.Width, value_rect.Bottom + 1));
			Rectangle work = Screen.FromControl (this).WorkingArea;
			if (work.Bottom < below.Y + dropdown_form.Height + grid_textbox.Height) {
				// No room below: above the row, the bar on top.
				below.Y = PointToScreen (new Point (0, value_rect.Top)).Y - dropdown_form.Height;
				dropdown_form.ResizeUp = true;
				if (bar > 0)
					dropdown_form.Padding = new Padding (1, 1 + bar, 1, 1);
			}
			below.X = Math.Min (work.Right - dropdown_form.Width, Math.Max (work.X, below.X));
			dropdown_form.Location = below;
			RepositionInScreenWorkingArea (dropdown_form);
			Point location = dropdown_form.Location;

			Form owner = FindForm ();
			owner.AddOwnedForm (dropdown_form);
			dropdown_form.Show ();
			if (Environment.GetEnvironmentVariable ("WF_TRACE_EDITOR") == "1")
				Console.Error.WriteLine ("[editor] dropdown control=" + control.GetType ().Name + " " +
					       control.Size + " form=" + dropdown_form.Bounds + " visible=" +
					       dropdown_form.Visible + " work=" + Screen.PrimaryScreen.WorkingArea);
			if (dropdown_form.Location != location)
				dropdown_form.Location = location;

			System.Windows.Forms.MSG msg = new MSG ();
			object queue_id = XplatUI.StartLoop (Thread.CurrentThread);
			// Deaf to the mouse for as long as the drop-down is up, as Windows makes it: the click that
			// dismisses the drop-down would otherwise be the click that opens it again.
			grid_textbox.IgnoreDropDownButtonMouse = true;
			XplatUI.MousePress += DropDownWatchClick;
			control.Focus ();
			while (dropdown_form.Visible && XplatUI.GetMessage (queue_id, ref msg, IntPtr.Zero, 0, 0)) {
				switch (msg.message) {
					case Msg.WM_NCLBUTTONDOWN:
				    case Msg.WM_NCMBUTTONDOWN:
				    case Msg.WM_NCRBUTTONDOWN:
				    case Msg.WM_LBUTTONDOWN:
				    case Msg.WM_MBUTTONDOWN:
				    case Msg.WM_RBUTTONDOWN:
				    	// The click that dismisses is dispatched as well, not swallowed. A property grid is not a
				    	// menu: clicking another row closes the drop-down AND selects that row, and eating the
				    	// click left the grid on the row before -- so the next drop-down opened was the previous
				    	// row's editor, a colour picker over a boolean.
				    	if (!HwndInControl (dropdown_form, msg.hwnd))
				    		CloseDropDown ();
					break;
					case Msg.WM_ACTIVATE:
					case Msg.WM_NCPAINT:
				 		if (owner.window.Handle == msg.hwnd)
							CloseDropDown ();
					break;						
				}
				XplatUI.TranslateMessage (ref msg);
				XplatUI.DispatchMessage (ref msg);
			}
			XplatUI.MousePress -= DropDownWatchClick;
			XplatUI.EndLoop (Thread.CurrentThread);
			grid_textbox.IgnoreDropDownButtonMouse = false;

			// However the loop ended -- a value picked, a click elsewhere, the window deactivated --
			// the drop-down is finished with. CloseDropDown is only one of the ways out, and the form
			// is reused, so a control left parented to it would still be there the next time one
			// opened.
			if (dropdown_form.Visible)
				dropdown_form.Hide ();
			dropdown_form.Controls.Clear ();
		}

		private void RepositionInScreenWorkingArea (Form form)
		{
			Rectangle workingArea = Screen.FromControl (form).WorkingArea;
			if (!workingArea.Contains (form.Bounds)) {
				int x, y;
				x = form.Location.X;
				y = form.Location.Y;

				if (form.Location.X < workingArea.X)
					x = workingArea.X;

				if (form.Location.Y + form.Size.Height > workingArea.Height) {
					Point aboveTextBox = PointToScreen (new Point (grid_textbox.Right - form.Width, grid_textbox.Location.Y));
					y = aboveTextBox.Y - form.Size.Height;
				}

				form.Location = new Point (x, y);
			}
		}

		private bool HwndInControl (Control c, IntPtr hwnd)
		{
			if (hwnd == c.window.Handle)
				return true;
			foreach (Control cc in c.Controls.GetAllControls ()) {
				if (HwndInControl (cc, hwnd))
					return true;
			}
			return false;
		}
		#endregion

		#region IWindowsFormsEditorService Members

		public void CloseDropDown () 
		{
			dropdown_form.Hide ();
			dropdown_form.Controls.Clear ();
		}

		public void DropDownControl (Control control) 
		{
			bool resizeable = this.SelectedGridItem != null ? SelectedGridItem.EditorResizeable : false;
			ShowDropDownControl (control, resizeable);
		}

		public System.Windows.Forms.DialogResult ShowDialog (Form dialog) {
			return dialog.ShowDialog (this);
		}

		#endregion

		internal class PropertyGridDropDown : Form 
		{
			/// <summary>.NET's s_resizeBarSize: a scroll bar's height and the divider.</summary>
			internal static int ResizeBarSize => SystemInformation.HorizontalScrollBarHeight + 1;
			internal bool Resizable;
			private bool resize_up;
			internal bool ResizeUp {
				get { return resize_up; }
				set { if (resize_up != value) { resize_up = value; grip?.Dispose (); grip = null; } }
			}
			private Bitmap grip;
			private Point drag_from;
			private Rectangle drag_bounds;
			private bool dragging;

			/// <summary>ControlPaint's size grip mirrored for the lower-left corner (and flipped for the
			/// upper-left), on a transparent ground -- .NET's GetSizeGripGlyph.</summary>
			private Bitmap Grip ()
			{
				int size = SystemInformation.HorizontalScrollBarHeight;
				if (grip != null)
					return grip;
				var plain = new Bitmap (size, size);
				using (Graphics g = Graphics.FromImage (plain))
					ControlPaint.DrawSizeGrip (g, BackColor, 0, 0, size, size);
				grip = new Bitmap (size, size);
				for (int y = 0; y < size; y++)
					for (int x = 0; x < size; x++) {
						Color c = plain.GetPixel (size - 1 - x, ResizeUp ? size - 1 - y : y);
						grip.SetPixel (x, y, c.ToArgb () == BackColor.ToArgb () ? Color.Transparent : c);
					}
				plain.Dispose ();
				return grip;
			}

			// Focus cues ON: .NET shows the holder with SW_SHOWNA, never activating it, so the
			// UIS_INITIALIZE that hides a window's cues never reaches it -- stock's lists draw the
			// focus rectangle on their current row however the drop-down was opened.
			public PropertyGridDropDown ()
			{
				show_focus_cues = true;
			}

			protected override void OnPaint (PaintEventArgs e)
			{
				base.OnPaint (e);
				if (Resizable) {
					// .NET draws in the client area inside its border; ours includes the border, a pixel
					// in. The grip and the divider sit where .NET's Height-relative sums put them.
					int size = SystemInformation.HorizontalScrollBarHeight;
					int inner = Height - 2;
					if (grip != null && grip.Height != size) { grip.Dispose (); grip = null; }
					var place = new Rectangle (1, 1 + (ResizeUp ? 0 : inner + 2 - size), size, size);
					var state = e.Graphics.Save ();
					e.Graphics.IntersectClip (new Rectangle (1, 1, Width - 2, Height - 2));
					e.Graphics.DrawImage (Grip (), place);
					int y = 1 + (ResizeUp ? ResizeBarSize - 1 : inner + 2 - ResizeBarSize);
					e.Graphics.DrawLine (SystemPens.ControlDark, 1, y, Width - 1, y);
					e.Graphics.Restore (state);
				}
				e.Graphics.DrawRectangle (SystemPens.WindowFrame, 0, 0, Width - 1, Height - 1);
			}

			private bool InBar (Point p)
				=> Resizable && (ResizeUp ? p.Y < 1 + ResizeBarSize : p.Y >= Height - 1 - ResizeBarSize);

			protected override void OnMouseDown (MouseEventArgs e)
			{
				base.OnMouseDown (e);
				if (e.Button == MouseButtons.Left && InBar (e.Location)) {
					dragging = true;
					drag_from = PointToScreen (e.Location);
					drag_bounds = Bounds;
					Capture = true;
				}
			}

			protected override void OnMouseMove (MouseEventArgs e)
			{
				base.OnMouseMove (e);
				Cursor = dragging || InBar (e.Location) ? Cursors.SizeNESW : Cursors.Default;
				if (!dragging)
					return;
				Point now = PointToScreen (e.Location);
				int dx = now.X - drag_from.X, dy = now.Y - drag_from.Y;
				int min_w = SystemInformation.VerticalScrollBarWidth * 4, min_h = SystemInformation.HorizontalScrollBarHeight * 4;
				int w = Math.Max (min_w, drag_bounds.Width - dx);
				int h = Math.Max (min_h, drag_bounds.Height + (ResizeUp ? -dy : dy));
				// The right edge stays put (the grip is on the left); the top too unless it opens upwards.
				int x = drag_bounds.Right - w;
				int top = ResizeUp ? drag_bounds.Bottom - h : drag_bounds.Y;
				SetBounds (x, top, w, h);
			}

			protected override void OnMouseUp (MouseEventArgs e)
			{
				base.OnMouseUp (e);
				if (dragging) {
					dragging = false;
					Capture = false;
				}
			}

			protected override CreateParams CreateParams {
				get {
					CreateParams cp = base.CreateParams;
					cp.Style = unchecked ((int)(WindowStyles.WS_POPUP | WindowStyles.WS_CLIPSIBLINGS | WindowStyles.WS_CLIPCHILDREN));
					cp.ExStyle |= (int)(WindowExStyles.WS_EX_TOPMOST);				
					return cp;
				}
			}

		}
	}
}
