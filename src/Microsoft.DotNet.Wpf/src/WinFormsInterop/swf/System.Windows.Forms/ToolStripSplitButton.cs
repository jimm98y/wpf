//
// ToolStripSplitButton.cs
//
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
// Copyright (c) 2006 Jonathan Pobst
//
// Authors:
//	Jonathan Pobst (monkey@jpobst.com)
//

using System;
using System.Drawing;
using System.ComponentModel;
using System.Windows.Forms.Design;

namespace System.Windows.Forms
{
	[DefaultEvent ("ButtonClick")]
	[ToolStripItemDesignerAvailability (ToolStripItemDesignerAvailability.ToolStrip | ToolStripItemDesignerAvailability.StatusStrip)]
	public class ToolStripSplitButton : ToolStripDropDownItem
	{
		private bool button_pressed;
		private ToolStripItem default_item;
		private bool drop_down_button_selected;
		private int drop_down_button_width;
		
		#region Public Constructors
		public ToolStripSplitButton()
			: this (string.Empty, null, null, string.Empty)
		{
		}
		
		public ToolStripSplitButton (Image image)
			: this (string.Empty, image, null, string.Empty)
		{
		}
		
		public ToolStripSplitButton (string text)
			: this (text, null, null, string.Empty)
		{
		}
		
		public ToolStripSplitButton (string text, Image image)
			: this (text, image, null, string.Empty)
		{
		}
		
		public ToolStripSplitButton (string text, Image image, EventHandler onClick)
			: this (text, image, onClick, string.Empty)
		{
		}
		
		public ToolStripSplitButton (string text, Image image, params ToolStripItem[] dropDownItems)
			: base (text, image, dropDownItems)
		{
			this.ResetDropDownButtonWidth ();
		}

		public ToolStripSplitButton (string text, Image image, EventHandler onClick, string name)
			: base (text, image, onClick, name)
		{
			this.ResetDropDownButtonWidth ();
		}
		#endregion

		#region Public Properties
		[DefaultValue (true)]
		public new bool AutoToolTip {
			get { return base.AutoToolTip; }
			set { base.AutoToolTip = value; }
		}

		[Browsable (false)]
		public Rectangle ButtonBounds {
			get { CalculateLayout (out Rectangle button, out _, out _); return button; }
		}

		// .NET's CalculateLayout, in the item's own coordinates: the drop-down part takes
		// DropDownButtonWidth on the right, the splitter the pixel before it, the button the rest.
		private void CalculateLayout (out Rectangle button, out Rectangle splitter, out Rectangle dropDown)
		{
			const int splitterWidth = 1;
			dropDown = new Rectangle (Point.Empty, new Size (Math.Min (Width, drop_down_button_width), Height));
			button = new Rectangle (Point.Empty, new Size (Math.Max (0, Width - dropDown.Width), Math.Max (0, Height)));
			button.Width -= splitterWidth;
			if (RightToLeft == RightToLeft.No) {
				dropDown.Offset (button.Right + splitterWidth, 0);
				splitter = new Rectangle (button.Right, button.Top, splitterWidth, button.Height);
			} else {
				button.Offset (drop_down_button_width + splitterWidth, 0);
				splitter = new Rectangle (dropDown.Right, dropDown.Top, splitterWidth, dropDown.Height);
			}
		}

		// .NET's ToolStripSplitButtonButtonLayout: the item's layout, over the button part alone.
		private sealed class SplitButtonButtonLayout : ToolStripItemInternalLayout
		{
			private readonly ToolStripSplitButton _split;

			public SplitButtonButtonLayout (ToolStripSplitButton owner) : base (owner) { _split = owner; }

			protected override ToolStripItemLayoutOptions CommonLayoutOptions ()
			{
				ToolStripItemLayoutOptions options = base.CommonLayoutOptions ();
				options.Client = new Rectangle (Point.Empty, _split.ButtonBounds.Size);
				return options;
			}

			public override Rectangle ImageRectangle {
				get { Rectangle r = base.ImageRectangle; r.Offset (_split.ButtonBounds.Location); return r; }
			}

			public override Rectangle TextRectangle {
				get { Rectangle r = base.TextRectangle; r.Offset (_split.ButtonBounds.Location); return r; }
			}
		}

		private SplitButtonButtonLayout split_layout;
		private SplitButtonButtonLayout SplitLayout => split_layout ??= new SplitButtonButtonLayout (this);

		[Browsable (false)]
		public bool ButtonPressed {
			get { return this.button_pressed; }
		}

		[Browsable (false)]
		public bool ButtonSelected {
			get { return base.Selected; }
		}

		[Browsable (false)]
		[DefaultValue (null)]
		public ToolStripItem DefaultItem {
			get { return this.default_item; }
			set {
				if (this.default_item != value) {
					this.default_item = value;
					this.OnDefaultItemChanged (EventArgs.Empty);
				}
			}
		}
		
		[Browsable (false)]
		public Rectangle DropDownButtonBounds {
			get { CalculateLayout (out _, out _, out Rectangle dropDown); return dropDown; }
		}

		[Browsable (false)]
		public bool DropDownButtonPressed {
			get { return this.drop_down_button_selected || (this.HasDropDownItems && this.DropDown.Visible); }
		}

		[Browsable (false)]
		public bool DropDownButtonSelected {
			get { return base.Selected; }
		}
		
		public int DropDownButtonWidth {
			get { return this.drop_down_button_width; }
			set { 
				if (value < 0)
					throw new ArgumentOutOfRangeException ();
				if (this.drop_down_button_width != value) {
					this.drop_down_button_width = value;
					CalculateAutoSize ();
				}
			}
		}

		[Browsable (false)]
		public Rectangle SplitterBounds {
			get { CalculateLayout (out _, out Rectangle splitter, out _); return splitter; }
		}
		#endregion

		#region Protected Properties
		protected override bool DefaultAutoToolTip {
			get { return true; }
		}

		protected internal override bool DismissWhenClicked {
			get { return true; }
		}
		#endregion

		#region Public Methods
		public override Size GetPreferredSize (Size constrainingSize)
		{
			// .NET's: the button part's own preferred size, then the drop-down part and the splitter.
			Size s = SplitLayout.GetPreferredSize (constrainingSize);
			s.Width += drop_down_button_width + 1 + Padding.Horizontal;
			return s;
		}
		
		public virtual void OnButtonDoubleClick (EventArgs e)
		{
			EventHandler eh = (EventHandler)(Events [ButtonDoubleClickEvent]);
			if (eh != null)
				eh (this, e);
		}
		
		public void PerformButtonClick ()
		{
			if (this.Enabled)
				this.OnButtonClick (EventArgs.Empty);
		}
		
		[EditorBrowsable (EditorBrowsableState.Never)]
		public virtual void ResetDropDownButtonWidth ()
		{
			this.DropDownButtonWidth = 11;
		}
		#endregion

		#region Protected Methods
		protected override AccessibleObject CreateAccessibilityInstance ()
		{
			return new ToolStripSplitButtonAccessibleObject (this);
		}
		
		protected override ToolStripDropDown CreateDefaultDropDown ()
		{
			ToolStripDropDownMenu tsddm = new ToolStripDropDownMenu ();
			tsddm.OwnerItem = this;
			return tsddm;
		}
		
		protected virtual void OnButtonClick (EventArgs e)
		{
			EventHandler eh = (EventHandler)Events [ButtonClickEvent];
			if (eh != null)
				eh (this, e);
		}
		
		protected virtual void OnDefaultItemChanged (EventArgs e)
		{
			EventHandler eh = (EventHandler)Events [DefaultItemChangedEvent];
			if (eh != null)
				eh (this, e);
		}

		protected override void OnMouseDown (MouseEventArgs e)
		{
			// The port hands items the tool strip's coordinates; these bounds are the item's own.
			Point at = new Point (e.X - Bounds.X, e.Y - Bounds.Y);
			if (this.ButtonBounds.Contains (at))
			{
				this.button_pressed = true;
				this.Invalidate ();
				base.OnMouseDown (e);
			}
			else if (this.DropDownButtonBounds.Contains (at))
			{
				if (this.DropDown.Visible)
					this.HideDropDown (ToolStripDropDownCloseReason.ItemClicked);
				else
					this.ShowDropDown ();
			
				this.Invalidate ();
				base.OnMouseDown (e);
			}
		}

		protected override void OnMouseLeave (EventArgs e)
		{
			this.drop_down_button_selected = false;
			this.button_pressed = false;
			
			this.Invalidate ();
			
			base.OnMouseLeave (e);
		}

		protected override void OnMouseUp (MouseEventArgs e)
		{
			this.button_pressed = false;
			this.Invalidate ();
			
			base.OnMouseUp (e);
		}
		
		protected override void OnPaint (PaintEventArgs e)
		{
			base.OnPaint (e);

			if (this.Owner != null) {
				Color font_color = this.Enabled ? this.ForeColor : SystemColors.GrayText;
				Image draw_image = this.Enabled ? this.Image : ToolStripRenderer.CreateDisabledImage (this.Image);

				this.Owner.Renderer.DrawSplitButton (new System.Windows.Forms.ToolStripItemRenderEventArgs (e.Graphics, this));

				// .NET's: the button part's layout; the renderer's DrawSplitButton has already drawn
				// the arrow in DropDownButtonBounds.
				if ((DisplayStyle & ToolStripItemDisplayStyle.Image) != 0 && draw_image != null)
					this.Owner.Renderer.DrawItemImage (new System.Windows.Forms.ToolStripItemImageRenderEventArgs (e.Graphics, this, draw_image, SplitLayout.ImageRectangle));
				if ((DisplayStyle & ToolStripItemDisplayStyle.Text) != 0)
					this.Owner.Renderer.DrawItemText (new System.Windows.Forms.ToolStripItemTextRenderEventArgs (e.Graphics, this, this.Text, SplitLayout.TextRectangle, font_color, this.Font, SplitLayout.TextFormat));

				return;
			}
		}

		protected override void OnRightToLeftChanged (EventArgs e)
		{
			base.OnRightToLeftChanged (e);
		}
		
		protected internal override bool ProcessDialogKey (Keys keyData)
		{
			if (this.Selected && keyData == Keys.Enter && this.DefaultItem != null) {
				this.DefaultItem.FireEvent (EventArgs.Empty, ToolStripItemEventType.Click);
				return true;
			}

			return base.ProcessDialogKey (keyData);
		}

		protected internal override bool ProcessMnemonic (char charCode)
		{
			if (!this.Selected)
				this.Parent.ChangeSelection (this);

			if (this.HasDropDownItems)
				this.ShowDropDown ();
			else
				this.PerformClick ();

			return true;
		}
		#endregion

		#region Internal Methods
		internal override void HandleClick (int mouse_clicks, EventArgs e)
		{
			base.HandleClick (mouse_clicks, e);

			MouseEventArgs mea = e as MouseEventArgs;
			
			if (mea != null)
				if (ButtonBounds.Contains (new Point (mea.X - Bounds.X, mea.Y - Bounds.Y)))
					OnButtonClick (EventArgs.Empty);
		}
		#endregion
		
		#region Public Events
		static object ButtonClickEvent = new object ();
		static object ButtonDoubleClickEvent = new object ();
		static object DefaultItemChangedEvent = new object ();

		public event EventHandler ButtonClick {
			add { Events.AddHandler (ButtonClickEvent, value); }
			remove {Events.RemoveHandler (ButtonClickEvent, value); }
		}
		public event EventHandler ButtonDoubleClick {
			add { Events.AddHandler (ButtonDoubleClickEvent, value); }
			remove {Events.RemoveHandler (ButtonDoubleClickEvent, value); }
		}
		public event EventHandler DefaultItemChanged {
			add { Events.AddHandler (DefaultItemChangedEvent, value); }
			remove {Events.RemoveHandler (DefaultItemChangedEvent, value); }
		}
		#endregion

		#region ToolStripSplitButtonAccessibleObject Class
		public class ToolStripSplitButtonAccessibleObject : ToolStripItemAccessibleObject
		{
			#region Public Constructor
			public ToolStripSplitButtonAccessibleObject (ToolStripSplitButton item) : base (item)
			{
			}
			#endregion

			#region Public Method
			public override void DoDefaultAction ()
			{
				(owner_item as ToolStripSplitButton).PerformButtonClick ();
			}
			#endregion
		}
		#endregion
	}
}
