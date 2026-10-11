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
//	Ivan N. Zlatev <contact@i-nz.net>
//


using System.Collections;
using System.ComponentModel;
using System.Drawing;

namespace System.Windows.Forms {

	public class DataGridViewComboBoxCell : DataGridViewCell {

		private bool autoComplete;
		private object dataSource;
		private string displayMember;
		private DataGridViewComboBoxDisplayStyle displayStyle;
		private bool displayStyleForCurrentCellOnly;
		private int dropDownWidth;
		private FlatStyle flatStyle;
		private ObjectCollection items;
		private int maxDropDownItems;
		private bool sorted;
		private string valueMember;
		private DataGridViewComboBoxColumn owningColumnTemlate;

		public DataGridViewComboBoxCell () : base() {
			autoComplete = true;
			dataSource = null;
			displayStyle = DataGridViewComboBoxDisplayStyle.DropDownButton;
			displayStyleForCurrentCellOnly = false;
			dropDownWidth = 1;
			flatStyle = FlatStyle.Standard;
			items = new ObjectCollection(this);
			maxDropDownItems = 8;
			sorted = false;
			owningColumnTemlate = null;
		}

		[DefaultValue (true)]
		public virtual bool AutoComplete {
			get { return autoComplete; }
			set { autoComplete = value; }
		}

		public virtual object DataSource {
			get { return dataSource; }
			set {
				if (value is IList || value is IListSource || value == null) {
					dataSource = value;
					return;
				}
				throw new Exception("Value is no IList, IListSource or null.");
			}
		}

		[DefaultValue ("")]
		public virtual string DisplayMember {
			get { return displayMember; }
			set { displayMember = value; }
		}

		[DefaultValue (DataGridViewComboBoxDisplayStyle.DropDownButton)]
		public DataGridViewComboBoxDisplayStyle DisplayStyle {
			get { return displayStyle; }
			set { displayStyle = value; }
		}

		[DefaultValue (false)]
		public bool DisplayStyleForCurrentCellOnly {
			get { return displayStyleForCurrentCellOnly; }
			set { displayStyleForCurrentCellOnly = value; }
		}

		[DefaultValue (1)]
		public virtual int DropDownWidth {
			get { return dropDownWidth; }
			set {
				if (value < 1) {
					throw new ArgumentOutOfRangeException("Value is less than 1.");
				}
				dropDownWidth = value;
			}
		}

		public override Type EditType {
			get { return typeof(DataGridViewComboBoxEditingControl); }
		}

		[DefaultValue (FlatStyle.Standard)]
		public FlatStyle FlatStyle {
			get { return flatStyle; }
			set {
				if (!Enum.IsDefined(typeof(FlatStyle), value)) {
					throw new InvalidEnumArgumentException("Value is not valid FlatStyle.");
				}
				flatStyle = value;
			}
		}

		public override Type FormattedValueType {
			get { return typeof(string); }
		}

		[Browsable (false)]
		public virtual ObjectCollection Items {
			get {
				// The data source FIRST: asking a grid for its BindingContext can create the form's, and
				// the change that raises had the grid rebind -- ending the edit this is being read for,
				// which left the combo box of an edited cell empty.
				if (DataSource != null && !String.IsNullOrEmpty (ValueMember)
				    && DataGridView != null && DataGridView.BindingContext != null) {
					items.ClearInternal ();
					CurrencyManager dataManager = (CurrencyManager) DataGridView.BindingContext[DataSource];
					if (dataManager != null && dataManager.Count > 0) {
						foreach (object item in dataManager.List)
							items.AddInternal (item);
					}
				}

				return items;
			}
		}

		[DefaultValue (8)]
		public virtual int MaxDropDownItems {
			get { return maxDropDownItems; }
			set {
				if (value < 1 || value > 100) {
					throw new ArgumentOutOfRangeException("Value is less than 1 or greater than 100.");
				}
				maxDropDownItems = value;
			}
		}

		[DefaultValue (false)]
		public virtual bool Sorted {
			get { return sorted; }
			set {
				/*
				if () {
					throw new ArgumentException("Cannot sort a cell attached to a data source.");
				}
				*/
				sorted = value;
			}
		}

		[DefaultValue ("")]
		public virtual string ValueMember {
			get { return valueMember; }
			set { valueMember = value; }
		}

		public override Type ValueType {
			get { return typeof(string); }
		}

		// Valid only for template Cells and used as a bridge to push items
		internal DataGridViewComboBoxColumn OwningColumnTemplate {
			get { return owningColumnTemlate; }
			set { owningColumnTemlate = value; }
		}

		public override object Clone () {
			DataGridViewComboBoxCell cell = (DataGridViewComboBoxCell) base.Clone();
			cell.autoComplete = this.autoComplete;
			cell.dataSource = this.dataSource;
			cell.displayStyle = this.displayStyle;
			cell.displayMember = this.displayMember;
			cell.valueMember = this.valueMember;
			cell.displayStyleForCurrentCellOnly = this.displayStyleForCurrentCellOnly;
			cell.dropDownWidth = this.dropDownWidth;
			cell.flatStyle = this.flatStyle;
			cell.items.AddRangeInternal(this.items);
			cell.maxDropDownItems = this.maxDropDownItems;
			cell.sorted = this.sorted;
			return cell;
		}

		public override void DetachEditingControl () {
			this.DataGridView.EditingControlInternal = null;
		}

		public override void InitializeEditingControl (int rowIndex, object initialFormattedValue, DataGridViewCellStyle dataGridViewCellStyle) {
			base.InitializeEditingControl (rowIndex, initialFormattedValue, dataGridViewCellStyle);
			
			ComboBox editingControl = DataGridView.EditingControl as ComboBox;
			
			editingControl.DropDownStyle = ComboBoxStyle.DropDownList;
			editingControl.Sorted = Sorted;
			editingControl.DataSource = null;
			editingControl.ValueMember = null;
			editingControl.DisplayMember = null;
			editingControl.Items.Clear();
			editingControl.SelectedIndex = -1;

			if (DataSource != null) {
				editingControl.DataSource = DataSource;
				editingControl.ValueMember = ValueMember;
				editingControl.DisplayMember = DisplayMember;
			} else {
				editingControl.Items.AddRange (this.Items);
				if (initialFormattedValue != null && editingControl.Items.IndexOf (initialFormattedValue) != -1)
					editingControl.SelectedItem = initialFormattedValue;
			}
		}

		internal void SyncItems ()
		{
			if (DataSource != null || OwningColumnTemplate == null)
				return;

			if (OwningColumnTemplate.DataGridView != null) {
				DataGridViewComboBoxEditingControl editor = OwningColumnTemplate.DataGridView.EditingControl
									    as DataGridViewComboBoxEditingControl;
				if (editor != null) {
					object selectedItem = editor.SelectedItem;
					editor.Items.Clear ();
					editor.Items.AddRange (items);
					if (editor.Items.IndexOf (selectedItem) != -1)
						editor.SelectedItem = selectedItem;
				}
			}

			// Push the new items to the column
			OwningColumnTemplate.SyncItems (Items);
		}

		public override bool KeyEntersEditMode (KeyEventArgs e)
		{
			if (e.KeyCode == Keys.Space)
				return true;
			if ((int)e.KeyCode >= 48 && (int)e.KeyCode <= 90)
				return true;
			if ((int)e.KeyCode >= 96 && (int)e.KeyCode <= 111)
				return true;
			if (e.KeyCode == Keys.BrowserSearch || e.KeyCode == Keys.SelectMedia)
				return true;
			if ((int)e.KeyCode >= 186 && (int)e.KeyCode <= 229)
				return true;
			if (e.KeyCode == Keys.Attn || e.KeyCode == Keys.Packet)
				return true;
			if ((int)e.KeyCode >= 248 && (int)e.KeyCode <= 254)
				return true;
			if (e.KeyCode == Keys.F4)
				return true;
			if ((e.Modifiers == Keys.Alt) && (e.KeyCode == Keys.Down || e.KeyCode == Keys.Up))
				return true;

			return false;
		}

		public override object ParseFormattedValue (object formattedValue, DataGridViewCellStyle cellStyle, TypeConverter formattedValueTypeConverter, TypeConverter valueTypeConverter)
		{
			return base.ParseFormattedValue (formattedValue, cellStyle, formattedValueTypeConverter, valueTypeConverter);
		}

		public override string ToString () {
			return string.Format ("DataGridViewComboBoxCell {{ ColumnIndex={0}, RowIndex={1} }}", ColumnIndex, RowIndex);
		}

		protected override Rectangle GetContentBounds (Graphics graphics, DataGridViewCellStyle cellStyle, int rowIndex)
		{
			if (DataGridView == null)
				return Rectangle.Empty;

			object o = FormattedValue;
			Size s = Size.Empty;

			if (o != null)
				s = DataGridViewCell.MeasureTextSize (graphics, o.ToString (), cellStyle.Font, TextFormatFlags.Default);

			return new Rectangle (1, (OwningRow.Height - s.Height) / 2, s.Width - 3, s.Height);
		}

		protected override Rectangle GetErrorIconBounds (Graphics graphics, DataGridViewCellStyle cellStyle, int rowIndex)
		{
			if (DataGridView == null || string.IsNullOrEmpty (ErrorText))
				return Rectangle.Empty;

			Size error_icon = new Size (12, 11);
			return new Rectangle (new Point (Size.Width - error_icon.Width - 23, (Size.Height - error_icon.Height) / 2), error_icon);
		}

		protected override object GetFormattedValue (object value, int rowIndex, ref DataGridViewCellStyle cellStyle, TypeConverter valueTypeConverter, TypeConverter formattedValueTypeConverter, DataGridViewDataErrorContexts context)
		{
			return base.GetFormattedValue (value, rowIndex, ref cellStyle, valueTypeConverter, formattedValueTypeConverter, context);
		}

		protected override Size GetPreferredSize (Graphics graphics, DataGridViewCellStyle cellStyle, int rowIndex, Size constraintSize)
		{
			object o = FormattedValue;

			if (o != null) {
				Size s = DataGridViewCell.MeasureTextSize (graphics, o.ToString (), cellStyle.Font, TextFormatFlags.Default);
				s.Height = Math.Max (s.Height, 22);
				s.Width += 25;
				return s;
			} else
				return new Size (39, 22);
		}

		/// <summary>.NET's PositionEditingPanel: the editor goes inside the cell's borders, not over
		/// them -- over them it came out a pixel wide, its drop-down chevron a pixel right.</summary>
		public override void PositionEditingControl (bool setLocation, bool setSize, Rectangle cellBounds, Rectangle cellClip, DataGridViewCellStyle cellStyle, bool singleVerticalBorderAdded, bool singleHorizontalBorderAdded, bool isFirstDisplayedColumn, bool isFirstDisplayedRow)
		{
			base.PositionEditingControl (setLocation, setSize, CellValueBounds (cellBounds), cellClip, cellStyle,
				singleVerticalBorderAdded, singleHorizontalBorderAdded, isFirstDisplayedColumn, isFirstDisplayedRow);
		}

		protected override void OnDataGridViewChanged () {
			// Here we're supposed to do something with DataSource, etc, according to MSDN.
			base.OnDataGridViewChanged ();
		}

		// .NET: the click that makes a combo cell current only selects it; a click on the cell once
		// it IS current begins the edit, and drops the list when it lands on the drop-down button.
		// Ours never began an edit from the mouse at all.
		bool ignore_next_mouse_click;

		protected override void OnEnter (int rowIndex, bool throughMouseClick) {
			base.OnEnter (rowIndex, throughMouseClick);
			if (DataGridView != null && throughMouseClick && DataGridView.EditMode != DataGridViewEditMode.EditOnEnter)
				ignore_next_mouse_click = true;
		}

		protected override void OnLeave (int rowIndex, bool throughMouseClick) {
			base.OnLeave (rowIndex, throughMouseClick);
			ignore_next_mouse_click = false;
		}

		protected override void OnMouseDown (DataGridViewCellMouseEventArgs e) {
			base.OnMouseDown (e);

			// Not passed on to the editing combo box, as Mono did: there every press dropped its list.
			// .NET's cell drops the list only from OnMouseClick, and only on the drop-down button.
		}

		protected override void OnMouseClick (DataGridViewCellMouseEventArgs e) {
			base.OnMouseClick (e);
			if (DataGridView == null)
				return;
			Point current = DataGridView.CurrentCellAddress;
			if (current.X != e.ColumnIndex || current.Y != e.RowIndex)
				return;
			if (ignore_next_mouse_click) {
				ignore_next_mouse_click = false;
				return;
			}
			var editor = DataGridView.EditingControl as ComboBox;
			if ((editor == null || !editor.DroppedDown) && DataGridView.EditMode != DataGridViewEditMode.EditProgrammatically
			    && DataGridView.BeginEdit (true)) {
				editor = DataGridView.EditingControl as ComboBox;
				// CheckDropDownList: on the button, the list comes down too.
				if (editor != null && DisplayStyle != DataGridViewComboBoxDisplayStyle.Nothing) {
					Rectangle value = CellValueBounds (new Rectangle (Point.Empty, Size));
					if (DropDownButtonBounds (value, InheritedStyle.Font).Contains (e.Location))
						editor.DroppedDown = true;
				}
			}
		}

		protected override void OnMouseEnter (int rowIndex) {
			base.OnMouseEnter (rowIndex);
		}

		protected override void OnMouseLeave (int rowIndex) {
			if (MouseInDropDownButton (rowIndex)) {
				s_hot_grid = null;
				if (rowIndex >= 0)
					DataGridView.InvalidateCell (ColumnIndex, rowIndex);
			}
			base.OnMouseLeave (rowIndex);
		}

		protected override void OnMouseMove (DataGridViewCellMouseEventArgs e) {
			// .NET lights the whole read-only face when the pointer is over the drop-down BUTTON, and
			// only then: PaintPrivate draws CP_READONLY in ComboBoxState.Hot while the pointer is
			// inside the button's rectangle, and OnMouseMove repaints the cell as it crosses it.
			if (DataGridView != null && e.RowIndex >= 0 && ThemedButtonCell) {
				Rectangle value = CellValueBounds (new Rectangle (Point.Empty, Size));
				// The hot zone: for a themed DropDownButton cell it is the whole value rectangle
				// (.NET's PaintPrivate sets dropDownButtonRect = valBounds there), so the face lights
				// wherever the pointer is on the cell; only for a ComboBox-style cell is it the button.
				Rectangle hot = DisplayStyle == DataGridViewComboBoxDisplayStyle.DropDownButton
					? value : DropDownButtonBounds (value, InheritedStyle.Font);
				bool inside = hot.Contains (e.Location);
				if (inside != MouseInDropDownButton (e.RowIndex)) {
					s_hot_grid = inside ? DataGridView : null;
					s_hot_row = e.RowIndex;
					s_hot_column = e.ColumnIndex;
					DataGridView.InvalidateCell (e.ColumnIndex, e.RowIndex);
				}
			}
			base.OnMouseMove (e);
		}

		// Static, as .NET's s_mouseInDropDownButtonBounds is, and keyed by the cell's address: a
		// click unshares the row and paints a CLONE of the cell the pointer entered.
		static DataGridView s_hot_grid;
		static int s_hot_row, s_hot_column;

		bool MouseInDropDownButton (int rowIndex)
			=> s_hot_grid != null && s_hot_grid == DataGridView && s_hot_row == rowIndex && s_hot_column == ColumnIndex;

		bool ThemedButtonCell => Application.RenderWithVisualStyles && (FlatStyle == FlatStyle.Standard || FlatStyle == FlatStyle.System);

		/// <summary>.NET's drop-down button: SM_CXHTHUMB wide at the value rectangle's right, and as
		/// tall as a line of text plus eight.</summary>
		static Rectangle DropDownButtonBounds (Rectangle value, Font font)
		{
			int num = Math.Min (SystemInformation.HorizontalScrollBarThumbWidth, value.Width - 6 - 1);
			int lineHeight = TextRenderer.MeasureText (" ", font, new Size (int.MaxValue, int.MaxValue), TextFormatFlags.Default).Height;
			int num2 = Math.Min (lineHeight + 8, value.Height);
			return new Rectangle (value.Right - num, value.Top, num, num2);
		}

		protected override void Paint (Graphics graphics, Rectangle clipBounds, Rectangle cellBounds, int rowIndex, DataGridViewElementStates elementState, object value, object formattedValue, string errorText, DataGridViewCellStyle cellStyle, DataGridViewAdvancedBorderStyle advancedBorderStyle, DataGridViewPaintParts paintParts)
		{
			// The internal paint routines are overridden instead of
			// doing the custom paint logic here
			base.Paint (graphics, clipBounds, cellBounds, rowIndex, elementState, value, formattedValue, errorText, cellStyle, advancedBorderStyle, paintParts);
		}

		internal override void PaintPartContent (Graphics graphics, Rectangle cellBounds, int rowIndex, DataGridViewElementStates cellState, DataGridViewCellStyle cellStyle, object formattedValue)
		{
			Color color = Selected ? cellStyle.SelectionForeColor : cellStyle.ForeColor;
			TextFormatFlags flags = TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter | TextFormatFlags.TextBoxControl;
	
			Rectangle text_area = ContentBounds;
			text_area.X += cellBounds.X;
			text_area.Y += cellBounds.Y;

			if (Application.RenderWithVisualStyles && (FlatStyle == FlatStyle.Standard || FlatStyle == FlatStyle.System)) {
				// .NET's PaintPrivate for a themed drop-down-button cell (post-XP themes): the
				// combo box's read-only face over the value rectangle, the drop-down button part
				// SM_CXHTHUMB wide at its right and as tall as a line of text plus eight, and the
				// text in the rectangle it leaves -- two in, a row down, a row short.
				Rectangle value = CellValueBounds (cellBounds);
				if (value.Width > 0 && value.Height > 0) {
					Rectangle button = DropDownButtonBounds (value, cellStyle.Font);
					int num = button.Width, num2 = button.Height;
					// Hot (2) only while the pointer is over the button; the button part itself
					// stays Normal whatever the face does.
					// Under the face: not the cell's (selection) background but a ControlLightLight
					// outline of the value rectangle -- .NET's PaintPrivate for a drop-down-button
					// cell -- which is what the face's part-transparent corners stand on.
					graphics.DrawRectangle (SystemPens.ControlLightLight, new Rectangle (value.X, value.Y, value.Width - 1, value.Height - 1));
					var readOnly = new VisualStyles.VisualStyleRenderer (VisualStyles.VisualStyleElement.CreateElement ("COMBOBOX", 5, MouseInDropDownButton (rowIndex) ? 2 : 1));
					readOnly.DrawBackground (graphics, value);
					if (num > 0 && num2 > 0)
						new VisualStyles.VisualStyleRenderer (VisualStyles.VisualStyleElement.CreateElement ("COMBOBOX", 1, 1))
							.DrawBackground (graphics, button);
					// The text is the theme's text colour, selected or not: the face is the
					// theme's, so .NET asks the theme and never uses SelectionForeColor here.
					color = readOnly.GetColor (VisualStyles.ColorProperty.TextColor);
					if (color.IsEmpty)
						color = SystemColors.ControlText;
					Rectangle text = Rectangle.Inflate (value, -2, -2);
					text.X--;
					text.Width++;
					text.Width -= Math.Max (num, 0);
					text.Offset (-1, 1);
					text.Width++;
					text.Height--;
					if (formattedValue is string str && text.Width > 0 && text.Height > 0)
						TextRenderer.DrawText (graphics, str, cellStyle.Font, text, color,
							TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix
							| TextFormatFlags.PreserveGraphicsClipping | TextFormatFlags.EndEllipsis);
				}
				return;
			}

			{
				Rectangle button_area = CalculateButtonArea (cellBounds);

				// The background of the dropdown button should be gray, not
				// the background color of the cell.
				graphics.FillRectangle (SystemBrushes.Control, button_area);
				ThemeEngine.Current.CPDrawComboButton (graphics, button_area, ButtonState.Normal);
			}

			if (formattedValue != null)
				TextRenderer.DrawText (graphics, formattedValue.ToString (), cellStyle.Font, text_area, color, flags);
		}
		
		private const int ThemedDropWidth = 17;

		private Rectangle CalculateButtonArea (Rectangle cellBounds)
		{
			Rectangle button_area, text_area;
			int border = ThemeEngine.Current.Border3DSize.Width;
			const int button_width = 16;

			text_area = cellBounds;

			button_area = cellBounds;
			button_area.X = text_area.Right - button_width - border;
			button_area.Y = text_area.Y + border;
			button_area.Width = button_width;
			button_area.Height = text_area.Height - 2 * border;
			
			return button_area;
		}

		// IMPORTANT: Only call the internal methods from within DataGridViewComboBoxCell
		// for adding/removing/clearing because the other methods invoke an update of the 
		// column items collection and you might end up in an endless loop.
		//
		[ListBindable (false)]
		public class ObjectCollection : IList, ICollection, IEnumerable {

			private ArrayList list;
			private DataGridViewComboBoxCell owner;

			public ObjectCollection (DataGridViewComboBoxCell owner)
			{
				this.owner = owner;
				list = new ArrayList();
			}

			public int Count {
				get { return list.Count; }
			}

			bool IList.IsFixedSize {
				get { return list.IsFixedSize; }
			}

			public bool IsReadOnly {
				get { return list.IsReadOnly; }
			}

			bool ICollection.IsSynchronized {
				get { return list.IsSynchronized; }
			}

			object ICollection.SyncRoot {
				get { return list.SyncRoot; }
			}

			public virtual object this [int index] {
				get { return list[index]; }
				set {
					ThrowIfOwnerIsDataBound ();
					list[index] = value;
				}
			}

			public int Add (object item)
			{
				ThrowIfOwnerIsDataBound ();
				int index = AddInternal (item);
				SyncOwnerItems ();
				return index;
			}
			
			internal int AddInternal (object item)
			{
				return list.Add (item);
			}

			internal void AddRangeInternal (ICollection items)
			{
				list.AddRange (items);
			}

			public void AddRange (ObjectCollection value)
			{
				ThrowIfOwnerIsDataBound ();
				AddRangeInternal (value);
				SyncOwnerItems ();
			}

			private void SyncOwnerItems ()
			{
				ThrowIfOwnerIsDataBound ();
				if (owner != null)
					owner.SyncItems ();
			}

			public void ThrowIfOwnerIsDataBound ()
			{
				if (owner != null && owner.DataGridView != null && owner.DataSource != null)
					throw new ArgumentException ("Cannot modify collection if the cell is data bound.");
			}

			public void AddRange (params object[] items)
			{
				ThrowIfOwnerIsDataBound ();
				AddRangeInternal (items);
				SyncOwnerItems ();
			}

			public void Clear ()
			{
				ThrowIfOwnerIsDataBound ();
				ClearInternal ();
				SyncOwnerItems ();
			}

			internal void ClearInternal ()
			{
				list.Clear ();
			}

			public bool Contains (object value)
			{
				return list.Contains(value);
			}

			void ICollection.CopyTo (Array destination, int index)
			{
				CopyTo ((object[]) destination, index);
			}

			public void CopyTo (object[] destination, int arrayIndex)
			{
				list.CopyTo (destination, arrayIndex);
			}

			public IEnumerator GetEnumerator ()
			{
				return list.GetEnumerator();
			}

			public int IndexOf (object value)
			{
				return list.IndexOf(value);
			}

			public void Insert (int index, object item)
			{
				ThrowIfOwnerIsDataBound ();
				InsertInternal (index, item);
				SyncOwnerItems ();
			}

			internal void InsertInternal (int index, object item)
			{
				list.Insert (index, item);
			}

			public void Remove (object value)
			{
				ThrowIfOwnerIsDataBound ();
				RemoveInternal (value);
				SyncOwnerItems ();
			}

			internal void RemoveInternal (object value)
			{
				list.Remove (value);
			}

			public void RemoveAt (int index)
			{
				ThrowIfOwnerIsDataBound ();
				RemoveAtInternal (index);
				SyncOwnerItems ();
			}

			internal void RemoveAtInternal (int index)
			{
				list.RemoveAt (index);
			}

			int IList.Add (object item)
			{
				return Add (item);
			}

		}

	}

}

