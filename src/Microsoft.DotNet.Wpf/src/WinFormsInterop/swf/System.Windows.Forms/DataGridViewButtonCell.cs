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
// Copyright (c) 2005 Novell, Inc. (http://www.novell.com)
//
// Author:
//	Pedro Martínez Juliá <pedromj@gmail.com>
//

using System.Drawing;
using System.ComponentModel;
using System.Windows.Forms.VisualStyles;

namespace System.Windows.Forms {

	public class DataGridViewButtonCell : DataGridViewCell {

		private FlatStyle flatStyle;
		private bool useColumnTextForButtonValue;
		private PushButtonState button_state;
		
		public DataGridViewButtonCell ()
		{
			useColumnTextForButtonValue = false;
			button_state = PushButtonState.Normal;
		}

		public override Type EditType {
			get { return null; }
		}

		[DefaultValue (FlatStyle.Standard)]
		public FlatStyle FlatStyle {
			get { return flatStyle; }
			set {
				if (!Enum.IsDefined(typeof(FlatStyle), value)) {
					throw new InvalidEnumArgumentException("Value is not valid FlatStyle.");
				}
				if (value == FlatStyle.Popup) {
					throw new Exception("FlatStyle cannot be set to Popup in this control.");
				}
			}
		}

		public override Type FormattedValueType {
			get { return typeof (string); }
		}

		[DefaultValue (false)]
		public bool UseColumnTextForButtonValue {
			get { return useColumnTextForButtonValue; }
			set { useColumnTextForButtonValue = value; }
		}

		public override Type ValueType {
			get { return base.ValueType == null ? typeof (object) : base.ValueType; }
		}

		public override object Clone () {
			DataGridViewButtonCell result = (DataGridViewButtonCell) base.Clone();
			result.flatStyle = this.flatStyle;
			result.useColumnTextForButtonValue = this.useColumnTextForButtonValue;
			return result;
		}

		public override string ToString () {
			return GetType().Name + ": RowIndex: " + RowIndex.ToString() + "; ColumnIndex: " + ColumnIndex.ToString() + ";";
		}

		protected override AccessibleObject CreateAccessibilityInstance () {
			return new DataGridViewButtonCellAccessibleObject(this);
		}

		protected override Rectangle GetContentBounds (Graphics graphics, DataGridViewCellStyle cellStyle, int rowIndex)
		{
			if (DataGridView == null)
				return Rectangle.Empty;
				
			Rectangle retval = Rectangle.Empty;
			
			retval.Height = OwningRow.Height - 1;
			retval.Width = OwningColumn.Width - 1;
			
			return retval;
		}

		protected override Rectangle GetErrorIconBounds (Graphics graphics, DataGridViewCellStyle cellStyle, int rowIndex)
		{
			if (DataGridView == null || string.IsNullOrEmpty (ErrorText))
				return Rectangle.Empty;

			Size error_icon = new Size (12, 11);
			return new Rectangle (new Point (Size.Width - error_icon.Width - 5, (Size.Height - error_icon.Height) / 2), error_icon);
		}

		protected override Size GetPreferredSize (Graphics graphics, DataGridViewCellStyle cellStyle, int rowIndex, Size constraintSize)
		{
			object o = FormattedValue;

			if (o != null) {
				Size s = DataGridViewCell.MeasureTextSize (graphics, o.ToString (), cellStyle.Font, TextFormatFlags.Default);
				s.Height = Math.Max (s.Height, 21);
				s.Width += 10;
				return s;
			} else
				return new Size (21, 21);
		}

		protected override object GetValue (int rowIndex)
		{
			if (useColumnTextForButtonValue)
				return (OwningColumn as DataGridViewButtonColumn).Text;
				
			return base.GetValue (rowIndex);
		}

		protected override bool KeyDownUnsharesRow (KeyEventArgs e, int rowIndex)
		{
			// true if the user pressed the SPACE key without modifier keys; otherwise, false
			return e.KeyData == Keys.Space;
		}

		protected override bool KeyUpUnsharesRow (KeyEventArgs e, int rowIndex)
		{
			// true if the user released the SPACE key; otherwise false
			return e.KeyData == Keys.Space;
		}

		protected override bool MouseDownUnsharesRow (DataGridViewCellMouseEventArgs e) {
			return (e.Button == MouseButtons.Left);
		}

		protected override bool MouseEnterUnsharesRow (int rowIndex)
		{
			// true if the cell was the last cell receiving a mouse click; otherwise, false.
			return false;
		}

		protected override bool MouseLeaveUnsharesRow (int rowIndex)
		{
			// true if the button displayed by the cell is in the pressed state; otherwise, false.
			return button_state == PushButtonState.Pressed;
		}

		protected override bool MouseUpUnsharesRow (DataGridViewCellMouseEventArgs e)
		{
			// true if the mouse up was caused by the release of the left mouse button; otherwise false.
			return e.Button == MouseButtons.Left;
		}

		protected override void OnKeyDown (KeyEventArgs e, int rowIndex)
		{
			// when activated by the SPACE key, this method updates the cell's user interface
			if ((e.KeyData & Keys.Space) == Keys.Space) {
				button_state = PushButtonState.Pressed;
				DataGridView.InvalidateCell (this);
			}
		}

		protected override void OnKeyUp (KeyEventArgs e, int rowIndex)
		{
			// when activated by the SPACE key, this method updates the cell's user interface
			if ((e.KeyData & Keys.Space) == Keys.Space) {
				button_state = PushButtonState.Normal;
				DataGridView.InvalidateCell (this);
			}
		}

		protected override void OnLeave (int rowIndex, bool throughMouseClick)
		{
			if (button_state != PushButtonState.Normal) {
				button_state = PushButtonState.Normal;
				DataGridView.InvalidateCell (this);
			}
		}

		protected override void OnMouseDown (DataGridViewCellMouseEventArgs e)
		{
			if ((e.Button & MouseButtons.Left) == MouseButtons.Left) {
				button_state = PushButtonState.Pressed;
				DataGridView.InvalidateCell (this);
			}
		}

		protected override void OnMouseLeave (int rowIndex)
		{
			if (button_state != PushButtonState.Normal) {
				button_state = PushButtonState.Normal;
				DataGridView.InvalidateCell (this);
			}
		}

		protected override void OnMouseMove (DataGridViewCellMouseEventArgs e)
		{
			if (button_state != PushButtonState.Normal && button_state != PushButtonState.Hot) {
				button_state = PushButtonState.Hot;
				DataGridView.InvalidateCell (this);
			}
		}

		protected override void OnMouseUp (DataGridViewCellMouseEventArgs e)
		{
			if ((e.Button & MouseButtons.Left) == MouseButtons.Left) {
				button_state = PushButtonState.Normal;
				DataGridView.InvalidateCell (this);
			}
		}

		protected override void Paint (Graphics graphics, Rectangle clipBounds, Rectangle cellBounds, int rowIndex, DataGridViewElementStates elementState, object value, object formattedValue, string errorText, DataGridViewCellStyle cellStyle, DataGridViewAdvancedBorderStyle advancedBorderStyle, DataGridViewPaintParts paintParts)
		{
			// The internal paint routines are overridden instead of
			// doing the custom paint logic here
			base.Paint (graphics, clipBounds, cellBounds, rowIndex, elementState, value, formattedValue, errorText, cellStyle, advancedBorderStyle, paintParts);
		}

		// .NET's PaintPrivate, themed: the cell's back colour and then the button over the value
		// rectangle -- the cell less its borders, so the grid lines stay clear of it.
		internal override void PaintPartBackground (Graphics graphics, Rectangle cellBounds, DataGridViewCellStyle style)
		{
			Rectangle value = CellValueBounds (cellBounds);
			if (value.Width <= 0 || value.Height <= 0)
				return;
			if (style.BackColor.A == 255)
				using (var b = new SolidBrush (style.BackColor))
					graphics.FillRectangle (b, value);
			ButtonRenderer.DrawButton (graphics, value, button_state);
		}

		internal override void PaintPartSelectionBackground (Graphics graphics, Rectangle cellBounds, DataGridViewElementStates cellState, DataGridViewCellStyle cellStyle)
		{
			cellBounds.Inflate (-2, -2);
			base.PaintPartSelectionBackground (graphics, cellBounds, cellState, cellStyle);
		}
		
		internal override void PaintPartContent (Graphics graphics, Rectangle cellBounds, int rowIndex, DataGridViewElementStates cellState, DataGridViewCellStyle cellStyle, object formattedValue)
		{
			// .NET's: the button part's content rectangle, two in and a row down, four narrower and
			// two shorter (and a pixel further for a pressed button), with the style's alignment, in
			// the button part's text colour.
			Rectangle value = CellValueBounds (cellBounds);
			string text = formattedValue?.ToString ();
			if (value.Width <= 0 || value.Height <= 0 || text == null)
				return;
			var renderer = new VisualStyles.VisualStyleRenderer (VisualStyles.VisualStyleElement.Button.PushButton.Normal);
			Rectangle r = renderer.GetBackgroundContentRectangle (graphics, value);
			r.Offset (2, 1);
			r.Width -= 4;
			r.Height -= 2;
			if (button_state == PushButtonState.Pressed) {
				r.Offset (1, 1);
				r.Width--;
				r.Height--;
			}
			if (r.Width <= 0 || r.Height <= 0)
				return;
			bool rtl = DataGridView != null && DataGridView.RightToLeft == RightToLeft.Yes;
			TextFormatFlags flags = DataGridViewUtilities.ComputeTextFormatFlagsForCellStyleAlignment (rtl, cellStyle.Alignment, cellStyle.WrapMode);
			Color color = renderer.GetColor (VisualStyles.ColorProperty.TextColor);
			if (color.A == 0)
				color = Selected ? cellStyle.SelectionForeColor : cellStyle.ForeColor;
			TextRenderer.DrawText (graphics, text, cellStyle.Font, r, color, flags);
		}
		
		protected class DataGridViewButtonCellAccessibleObject : DataGridViewCellAccessibleObject {

			public DataGridViewButtonCellAccessibleObject (DataGridViewCell owner) : base(owner) {
			}

			public override string DefaultAction {
				get {
					if (Owner.ReadOnly) {
						return "Press";
					}
					else {
						return "";
					}
				}
			}

			public override void DoDefaultAction () {
				// causes the button in the ButtonCell to be clicked
			}

			public override int GetChildCount () {
				return -1;
			}

		}

	}

}

