//
// ToolStrip.cs
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
using System.Runtime.InteropServices;
using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms.Layout;
using System.Collections.Generic;
using System.ComponentModel.Design.Serialization;

namespace System.Windows.Forms
{
	[ComVisible (true)]
	[ClassInterface (ClassInterfaceType.AutoDispatch)]
	[DefaultEvent ("ItemClicked")]
	[DefaultProperty ("Items")]
	[Designer ("System.Windows.Forms.Design.ToolStripDesigner, " + Consts.AssemblySystem_Design, "System.ComponentModel.Design.IDesigner")]
	[DesignerSerializer ("System.Windows.Forms.Design.ToolStripCodeDomSerializer, " + Consts.AssemblySystem_Design, "System.ComponentModel.Design.Serialization.CodeDomSerializer, " + Consts.AssemblySystem_Design)]
	public partial class ToolStrip : ScrollableControl, IComponent, IDisposable, IToolStripData, IArrangedContainer
	{
		// Draws into a memory DC on Windows (double-buffered), where win32k blends its text.
		internal override bool GdiTextOnMemorySurface => true;

		#region Private Variables
		private bool allow_item_reorder;
		private bool allow_merge;
		private Color back_color;
		private bool can_overflow;
		private ToolStrip currently_merged_with;
		private ToolStripDropDownDirection default_drop_down_direction;
		internal ToolStripItemCollection displayed_items;
		private Color fore_color;
		private Padding grip_margin;
		private ToolStripGripStyle grip_style;
		private List<ToolStripItem> hidden_merged_items;
		private ImageList image_list;
		private Size image_scaling_size;
		private bool is_currently_merged;
		private ToolStripItemCollection items;
		private bool keyboard_active;
		private LayoutEngine layout_engine;
		private LayoutSettings layout_settings;
		private ToolStripLayoutStyle layout_style;
		private Orientation orientation;
		private ToolStripOverflowButton overflow_button;
		private List<ToolStripItem> pre_merge_items;
		private ToolStripRenderer renderer;
		private ToolStripRenderMode render_mode;
		private ToolStripTextDirection text_direction;
		private Timer tooltip_timer;
		private ToolTip tooltip_window;
		private bool show_item_tool_tips;
		private bool stretch;

		private ToolStripItem mouse_currently_over;
		internal bool menu_selected;
		private ToolStripItem tooltip_currently_showing;
		private ToolTip.TipState tooltip_state;

		const int InitialToolTipDelay = 500;
		const int ToolTipDelay = 5000;
		#endregion

		#region Public Constructors
		public ToolStrip () : this (null)
		{
		}

		public ToolStrip (params ToolStripItem[] items) : base ()
		{
			SetStyle (ControlStyles.AllPaintingInWmPaint, true);
			SetStyle (ControlStyles.OptimizedDoubleBuffer, true);
			SetStyle (ControlStyles.Selectable, false);
			SetStyle (ControlStyles.SupportsTransparentBackColor, true);

			this.SuspendLayout ();
			
			this.items = new ToolStripItemCollection (this, items, true);
			this.allow_merge = true;
			base.AutoSize = true;
			this.SetAutoSizeMode (AutoSizeMode.GrowAndShrink);
			this.back_color = Control.DefaultBackColor;
			this.can_overflow = true;
			base.CausesValidation = false;
			this.default_drop_down_direction = ToolStripDropDownDirection.BelowRight;
			this.displayed_items = new ToolStripItemCollection (this, null, true);
			this.Dock = this.DefaultDock;
			// No font of its own: the getter answers ToolStripManager.DefaultFont, as .NET (a font set here
			// would be scaled by a DPI change, which .NET's default font is not).
			this.fore_color = Control.DefaultForeColor;
			this.grip_margin = this.DefaultGripMargin;
			this.grip_style = ToolStripGripStyle.Visible;
			// .NET: the icon size at the DPI the strip is created at.
			int iconSize = ScaleHelper.ScaleToDpi (16, DeviceDpi);
			this.image_scaling_size = new Size (iconSize, iconSize);
			this.layout_style = ToolStripLayoutStyle.HorizontalStackWithOverflow;
			this.orientation = Orientation.Horizontal;
			if (!(this is ToolStripDropDown))
				this.overflow_button = new ToolStripOverflowButton (this);
			this.renderer = null;
			this.render_mode = ToolStripRenderMode.ManagerRenderMode;
			this.show_item_tool_tips = this.DefaultShowItemToolTips;
			base.TabStop = false;
			this.text_direction = ToolStripTextDirection.Horizontal;
			this.ResumeLayout (false);
			
			// Register with the ToolStripManager
			ToolStripManager.AddToolStrip (this);
		}
		#endregion

		#region Public Properties
		// AllowDrop: the strip's own drag events (or an item's, where it allows drops); AllowItemReorder: Alt+drag
		// moves an item, here or to another strip that allows it. Not both, as in .NET.
		public override bool AllowDrop {
			get { return base.AllowDrop; }
			set {
				if (value && this.allow_item_reorder)
					throw new ArgumentException ("AllowItemReorder and AllowDrop cannot both be true.");
				base.AllowDrop = value;
				UpdateDropRegistration ();
			}
		}

		[DefaultValue (false)]
		public bool AllowItemReorder {
			get { return this.allow_item_reorder; }
			set {
				if (value && base.AllowDrop)
					throw new ArgumentException ("AllowItemReorder and AllowDrop cannot both be true.");
				this.allow_item_reorder = value;
				UpdateDropRegistration ();
			}
		}

		/// <summary>The strip a drop target while it reorders items, takes drops, or has an item that does.</summary>
		internal void UpdateDropRegistration ()
		{
			if (!IsHandleCreated)
				return;
			bool any = base.AllowDrop || this.allow_item_reorder;
			if (!any)
				foreach (ToolStripItem i in this.Items)
					any |= i.AllowDrop;
			XplatUI.SetAllowDrop (Handle, any);
		}

		// ---- dragging items: reordering, and an item's own drag and drop -----------------------------

		private ToolStripItem reorder_pending;	// an item pressed with Alt, not dragged far enough yet
		private Point reorder_from;
		private ToolStripItem item_dragging;	// the item whose DoDragDrop is under way
		private ToolStripItem drag_over_item;	// the item a drag is over, that takes drops
		private Rectangle insertion_mark = Rectangle.Empty;

		internal DragDropEffects DoItemDragDrop (ToolStripItem item, object data, DragDropEffects allowed)
		{
			item_dragging = item;
			try {
				return DoDragDrop (data, allowed);
			} finally {
				item_dragging = null;
			}
		}

		protected override void OnGiveFeedback (GiveFeedbackEventArgs gfbevent)
		{
			if (item_dragging != null)
				item_dragging.FireDrag (4, gfbevent);
			base.OnGiveFeedback (gfbevent);
		}

		protected override void OnQueryContinueDrag (QueryContinueDragEventArgs qcdevent)
		{
			if (item_dragging != null)
				item_dragging.FireDrag (5, qcdevent);
			base.OnQueryContinueDrag (qcdevent);
		}

		static bool IsReorderDrag (DragEventArgs e) => e.Data != null && e.Data.GetDataPresent (typeof (ToolStripItem));

		/// <summary>Where a dragged item would go at a point of the strip: the index among the items, and the mark
		/// between them.</summary>
		private int ReorderIndexAt (Point p, out Rectangle mark)
		{
			bool horizontal = this.Orientation == Orientation.Horizontal;
			int index = this.Items.Count;
			ToolStripItem after = null, before = null;
			for (int i = 0; i < this.Items.Count; i++) {
				ToolStripItem it = this.Items [i];
				if (!it.Available || it.Placement != ToolStripItemPlacement.Main)
					continue;
				Rectangle b = it.Bounds;
				int mid = horizontal ? b.X + b.Width / 2 : b.Y + b.Height / 2;
				if ((horizontal ? p.X : p.Y) < mid) {
					index = i;
					before = it;
					break;
				}
				after = it;
			}
			// the mark: an I-beam six pixels wide, on the line between the items
			const int beam = 6;
			Rectangle row = before != null ? before.Bounds : after != null ? after.Bounds : this.DisplayRectangle;
			int edge = before != null ? (horizontal ? before.Bounds.Left : before.Bounds.Top)
			                          : after != null ? (horizontal ? after.Bounds.Right : after.Bounds.Bottom)
			                          : (horizontal ? this.DisplayRectangle.Left : this.DisplayRectangle.Top);
			mark = horizontal ? new Rectangle (edge - beam / 2, row.Y, beam, row.Height)
			                  : new Rectangle (row.X, edge - beam / 2, row.Width, beam);
			return index;
		}

		private void SetInsertionMark (Rectangle mark)
		{
			if (mark == insertion_mark)
				return;
			if (!insertion_mark.IsEmpty)
				Invalidate (insertion_mark);
			insertion_mark = mark;
			if (!mark.IsEmpty)
				Invalidate (mark);
		}

		private void PaintInsertionMark (Graphics g)
		{
			if (insertion_mark.IsEmpty)
				return;
			Rectangle m = insertion_mark;
			Pen pen = SystemPens.ControlText;
			if (this.Orientation == Orientation.Horizontal) {
				int x = m.X + m.Width / 2 - 1;
				g.DrawLine (pen, x, m.Y, x, m.Bottom - 1);
				g.DrawLine (pen, x + 1, m.Y, x + 1, m.Bottom - 1);
				g.DrawLine (pen, m.X, m.Y, m.Right - 1, m.Y);
				g.DrawLine (pen, m.X + 1, m.Y + 1, m.Right - 2, m.Y + 1);
				g.DrawLine (pen, m.X, m.Bottom - 1, m.Right - 1, m.Bottom - 1);
				g.DrawLine (pen, m.X + 1, m.Bottom - 2, m.Right - 2, m.Bottom - 2);
			} else {
				int y = m.Y + m.Height / 2 - 1;
				g.DrawLine (pen, m.X, y, m.Right - 1, y);
				g.DrawLine (pen, m.X, y + 1, m.Right - 1, y + 1);
				g.DrawLine (pen, m.X, m.Y, m.X, m.Bottom - 1);
				g.DrawLine (pen, m.X + 1, m.Y + 1, m.X + 1, m.Bottom - 2);
				g.DrawLine (pen, m.Right - 1, m.Y, m.Right - 1, m.Bottom - 1);
				g.DrawLine (pen, m.Right - 2, m.Y + 1, m.Right - 2, m.Bottom - 2);
			}
		}

		/// <summary>The item that takes drops under a drag, told when the drag comes onto it and leaves it.</summary>
		private ToolStripItem TrackDropItem (DragEventArgs e)
		{
			Point p = PointToClient (new Point (e.X, e.Y));
			ToolStripItem under = GetItemAt (p.X, p.Y);
			if (under != null && !under.AllowDrop)
				under = null;
			if (under != drag_over_item) {
				drag_over_item?.FireDrag (3, EventArgs.Empty);
				drag_over_item = under;
				under?.FireDrag (0, e);
			}
			return under;
		}

		protected override void OnDragEnter (DragEventArgs drgevent)
		{
			if (this.allow_item_reorder && IsReorderDrag (drgevent)) {
				drgevent.Effect = drgevent.AllowedEffect & DragDropEffects.Move;
				ReorderIndexAt (PointToClient (new Point (drgevent.X, drgevent.Y)), out Rectangle mark);
				SetInsertionMark (mark);
				return;
			}
			if (TrackDropItem (drgevent) != null)
				return;
			base.OnDragEnter (drgevent);
		}

		protected override void OnDragOver (DragEventArgs drgevent)
		{
			if (this.allow_item_reorder && IsReorderDrag (drgevent)) {
				drgevent.Effect = drgevent.AllowedEffect & DragDropEffects.Move;
				ReorderIndexAt (PointToClient (new Point (drgevent.X, drgevent.Y)), out Rectangle mark);
				SetInsertionMark (mark);
				return;
			}
			ToolStripItem item = TrackDropItem (drgevent);
			if (item != null) {
				item.FireDrag (1, drgevent);
				return;
			}
			base.OnDragOver (drgevent);
		}

		protected override void OnDragLeave (EventArgs e)
		{
			SetInsertionMark (Rectangle.Empty);
			if (drag_over_item != null) {
				drag_over_item.FireDrag (3, e);
				drag_over_item = null;
				return;
			}
			base.OnDragLeave (e);
		}

		protected override void OnDragDrop (DragEventArgs drgevent)
		{
			SetInsertionMark (Rectangle.Empty);
			if (this.allow_item_reorder && IsReorderDrag (drgevent)) {
				ToolStripItem moved = (ToolStripItem)drgevent.Data.GetData (typeof (ToolStripItem));
				int index = ReorderIndexAt (PointToClient (new Point (drgevent.X, drgevent.Y)), out _);
				if (moved != null) {
					int from = this.Items.IndexOf (moved);
					if (from >= 0 && from < index)
						index--;	// it leaves its own place first
					if (from != index) {
						this.SuspendLayout ();
						this.Items.Insert (Math.Min (index, this.Items.Contains (moved) ? this.Items.Count - 1 : this.Items.Count), moved);
						this.ResumeLayout ();
					}
					drgevent.Effect = DragDropEffects.Move;
				}
				return;
			}
			ToolStripItem item = drag_over_item ?? TrackDropItem (drgevent);
			if (item != null) {
				drag_over_item = null;
				item.FireDrag (2, drgevent);
				return;
			}
			base.OnDragDrop (drgevent);
		}
		
		[DefaultValue (true)]
		public bool AllowMerge {
			get { return this.allow_merge; }
			set { this.allow_merge = value; }
		}
		
		public override AnchorStyles Anchor {
			get { return base.Anchor; }
			set { base.Anchor = value; }
		}

		[Browsable (false)]
		[EditorBrowsable (EditorBrowsableState.Never)]
		[DesignerSerializationVisibility (DesignerSerializationVisibility.Hidden)]
		public override bool AutoScroll {
			get { return base.AutoScroll; }
			set { base.AutoScroll = value; }
		}

		[Browsable (false)]
		[EditorBrowsable (EditorBrowsableState.Never)]
		[DesignerSerializationVisibility (DesignerSerializationVisibility.Hidden)]
		public new Size AutoScrollMargin {
			get { return base.AutoScrollMargin; }
			set { base.AutoScrollMargin = value; }
		}

		[Browsable (false)]
		[EditorBrowsable (EditorBrowsableState.Never)]
		[DesignerSerializationVisibility (DesignerSerializationVisibility.Hidden)]
		public new Size AutoScrollMinSize {
			get { return base.AutoScrollMinSize; }
			set { base.AutoScrollMinSize = value; }
		}

		[Browsable (false)]
		[EditorBrowsable (EditorBrowsableState.Never)]
		[DesignerSerializationVisibility (DesignerSerializationVisibility.Hidden)]
		public new Point AutoScrollPosition {
			get { return base.AutoScrollPosition; }
			set { base.AutoScrollPosition = value; }
		}

		[DesignerSerializationVisibility (DesignerSerializationVisibility.Visible)]
		[Browsable (true)]
		[EditorBrowsable (EditorBrowsableState.Always)]
		[DefaultValue (true)]
		public override bool AutoSize {
			get { return base.AutoSize; }
			set { base.AutoSize = value; }
		}
		
		new public Color BackColor {
			get { return this.back_color; }
			set { this.back_color = value; }
		}

		public override BindingContext BindingContext {
			get { return base.BindingContext; }
			set { base.BindingContext = value; }
		}
		
		[DefaultValue (true)]
		public bool CanOverflow {
			get { return this.can_overflow; }
			set { this.can_overflow = value; }
		}
		
		[Browsable (false)]
		[DefaultValue (false)]
		public new bool CausesValidation {
			get { return base.CausesValidation; }
			set { base.CausesValidation = value; }
		}
		
		[EditorBrowsable (EditorBrowsableState.Never)]
		[DesignerSerializationVisibility (DesignerSerializationVisibility.Hidden)]
		public new ControlCollection Controls {
			get { return base.Controls; }
		}

		[Browsable (false)]
		[DesignerSerializationVisibility (DesignerSerializationVisibility.Hidden)]
		public override Cursor Cursor {
			get { return base.Cursor; }
			set { base.Cursor = value; }
		}
		
		[Browsable (false)]
		public virtual ToolStripDropDownDirection DefaultDropDownDirection {
			get { return this.default_drop_down_direction; }
			set { 
				if (!Enum.IsDefined (typeof (ToolStripDropDownDirection), value))
					throw new InvalidEnumArgumentException (string.Format ("Enum argument value '{0}' is not valid for ToolStripDropDownDirection", value));
					
				this.default_drop_down_direction = value;
			}
		}

		// .NET's: the padded client area, less the grip's thickness and margins along the strip when the
		// grip shows (Flow and Table layouts never draw one).
		public override Rectangle DisplayRectangle {
			get {
				Rectangle r = new Rectangle (this.Padding.Left, this.Padding.Top, this.Width - this.Padding.Horizontal, this.Height - this.Padding.Vertical);
				if (this.grip_style == ToolStripGripStyle.Hidden || this.layout_style == ToolStripLayoutStyle.Flow || this.layout_style == ToolStripLayoutStyle.Table)
					return r;
				if (this.orientation == Orientation.Horizontal) {
					int taken = GripThickness + this.grip_margin.Horizontal;
					r.Width -= taken;
					if (this.RightToLeft != RightToLeft.Yes)
						r.X += taken;
				} else {
					int taken = GripThickness + this.grip_margin.Vertical;
					r.Y += taken;
					r.Height -= taken;
				}
				return r;
			}
		}

		// ToolStripGrip.GripThickness: five pixels under visual styles, three without.
		internal int GripThickness => ScaleHelper.ScaleToDpi (ToolStripManager.VisualStylesEnabled ? 5 : 3, ScaleHelper.InitialSystemDpi);

		[DefaultValue (DockStyle.Top)]
		public override DockStyle Dock {
			get { return base.Dock; }
			set {
				if (base.Dock != value) {
					base.Dock = value;
					
					switch (value) {
						case DockStyle.Top:
						case DockStyle.Bottom:
						case DockStyle.None:
							this.LayoutStyle = ToolStripLayoutStyle.HorizontalStackWithOverflow;
							break;
						case DockStyle.Left:
						case DockStyle.Right:
							this.LayoutStyle = ToolStripLayoutStyle.VerticalStackWithOverflow;
							break;
					}
				}
			}
		}

		public override Font Font {
			// .NET: a strip does not take its parent's font; unless it is given one it has the
			// default menu font (ToolStripManager.DefaultFont).
			get { return font != null ? base.Font : ToolStripManager.DefaultFont; }
			set { 
				if (base.Font != value) {
					base.Font = value;
					
					foreach (ToolStripItem tsi in this.Items)
						tsi.OnOwnerFontChanged (EventArgs.Empty);
				}
			 }
		}
		
		[Browsable (false)]
		public new Color ForeColor {
			get { return this.fore_color; }
			set { 
				if (this.fore_color != value) {
					this.fore_color = value; 
					this.OnForeColorChanged (EventArgs.Empty); 
				}
			}
		}

		[Browsable (false)]
		public ToolStripGripDisplayStyle GripDisplayStyle {
			get { return this.orientation == Orientation.Vertical ? ToolStripGripDisplayStyle.Horizontal : ToolStripGripDisplayStyle.Vertical; }
		}

		public Padding GripMargin {
			get { return this.grip_margin; }
			set { 
				if (this.grip_margin != value) {
					this.grip_margin = value; 
					this.PerformLayout (); 
				}
			}
		}

		[Browsable (false)]
		public Rectangle GripRectangle {
			get {
				if (this.grip_style == ToolStripGripStyle.Hidden)
					return Rectangle.Empty;

				// ToolStrip.SetupGrip: the grip stands just before the display rectangle, inside
				// its margins.
				Rectangle display = this.DisplayRectangle;
				Rectangle grip = Rectangle.Empty;
				if (this.orientation == Orientation.Horizontal) {
					grip.X = Math.Max (0, display.X - GripThickness);
					grip.Y = Math.Max (0, display.Top - this.grip_margin.Top);
					grip.Width = GripThickness;
					grip.Height = display.Height;
					if (this.RightToLeft == RightToLeft.Yes)
						grip.X = this.ClientRectangle.Right - grip.Width - this.grip_margin.Horizontal + this.grip_margin.Left;
					else
						grip.X -= this.grip_margin.Right;
				} else {
					grip.X = display.Left;
					grip.Y = display.Top - (GripThickness + this.grip_margin.Bottom);
					grip.Width = display.Width;
					grip.Height = GripThickness;
				}
				return grip;
			}
		}

		[DefaultValue (ToolStripGripStyle.Visible)]
		public ToolStripGripStyle GripStyle {
			get { return this.grip_style; }
			set {
				if (this.grip_style != value) {
					if (!Enum.IsDefined (typeof (ToolStripGripStyle), value))
						throw new InvalidEnumArgumentException (string.Format ("Enum argument value '{0}' is not valid for ToolStripGripStyle", value));
					this.grip_style = value;
					this.PerformLayout (this, "GripStyle");
				}
			}
		}

		[Browsable (false)]
		[EditorBrowsable (EditorBrowsableState.Never)]
		[DesignerSerializationVisibility (DesignerSerializationVisibility.Hidden)]
		public new bool HasChildren {
			get { return base.HasChildren; }
		}

		[Browsable (false)]
		[EditorBrowsable (EditorBrowsableState.Never)]
		public new HScrollProperties HorizontalScroll {
			get { return base.HorizontalScroll; }
		}
		
		[Browsable (false)]
		[DefaultValue (null)]
		public ImageList ImageList {
			get { return this.image_list; }
			set { this.image_list = value; }
		}

		[DefaultValue ("{Width=16, Height=16}")]
		public Size ImageScalingSize {
			get { return this.image_scaling_size; }
			set { this.image_scaling_size = value; }
		}

		[Browsable (false)]
		[EditorBrowsable (EditorBrowsableState.Advanced)]
		public bool IsCurrentlyDragging {
			get { return grip_dragging; }
		}

		// ---- moving the strip by its grip --------------------------------------------------------
		//
		// A strip in a ToolStripPanel is moved by its grip: along its row, to another row, to a row of its own
		// above or below the others, or into another panel of the same ToolStripContainer -- BeginDrag when the
		// grip is pressed, EndDrag when it is let go, the four-way arrow over the grip.

		private bool grip_dragging;
		private Point grip_grab;		// where in the strip the pointer holds it
		private bool grip_cursor;		// the grip's cursor is up
		private Cursor cursor_before_grip;

		private bool GripDraggable =>
			this.grip_style == ToolStripGripStyle.Visible && this.Parent is ToolStripPanel && !(this is ToolStripDropDown)
			&& this.layout_style != ToolStripLayoutStyle.Flow && this.layout_style != ToolStripLayoutStyle.Table;

		private void ShowGripCursor (bool show)
		{
			if (show == grip_cursor)
				return;
			grip_cursor = show;
			if (show) {
				cursor_before_grip = this.Cursor;
				this.Cursor = Cursors.SizeAll;
			} else
				this.Cursor = cursor_before_grip;
		}

		/// <summary>The panel a dragged strip goes into at a point of the screen: its own, or another visible one
		/// of its container -- an empty one (no thickness) across a band at its edge.</summary>
		private ToolStripPanel PanelUnder (Point screen)
		{
			ToolStripPanel own = this.Parent as ToolStripPanel;
			var candidates = new System.Collections.Generic.List<ToolStripPanel> ();
			if (own != null)
				candidates.Add (own);
			if (own?.Parent != null)
				foreach (Control c in own.Parent.Controls)
					if (c is ToolStripPanel p && p != own && p.Visible && p.Enabled)
						candidates.Add (p);
			foreach (ToolStripPanel p in candidates) {
				Rectangle r = p.RectangleToScreen (p.ClientRectangle);
				bool horizontal = p.Orientation == Orientation.Horizontal;
				const int band = 6;
				if (horizontal && r.Height < band) {
					if (p.Dock == DockStyle.Bottom)
						r = new Rectangle (r.X, r.Bottom - band, r.Width, band);
					else
						r = new Rectangle (r.X, r.Y, r.Width, band);
				} else if (!horizontal && r.Width < band) {
					if (p.Dock == DockStyle.Right)
						r = new Rectangle (r.Right - band, r.Y, band, r.Height);
					else
						r = new Rectangle (r.X, r.Y, band, r.Height);
				}
				if (r.Contains (screen))
					return p;
			}
			return own;
		}
		
		[Browsable (false)]
		public bool IsDropDown {
			get {
				if (this is ToolStripDropDown)
					return true;

				return false;
			}
		}

		[MergableProperty (false)]
		[DesignerSerializationVisibility (DesignerSerializationVisibility.Content)]
		public virtual ToolStripItemCollection Items {
			get { return this.items; }
		}

		public override LayoutEngine LayoutEngine {
			get { 
				 if (layout_engine == null)
					this.layout_engine = new ToolStripSplitStackLayout ();
					
				 return this.layout_engine;
			}
		}

		[Browsable (false)]
		[DefaultValue (null)]
		[DesignerSerializationVisibility (DesignerSerializationVisibility.Hidden)]
		public LayoutSettings LayoutSettings {
			get { return this.layout_settings; }
			set { 
				if (this.layout_settings != value) {
					this.layout_settings = value;
					PerformLayout (this, "LayoutSettings");
				}
			}
		}
		
		[AmbientValue (ToolStripLayoutStyle.StackWithOverflow)]
		public ToolStripLayoutStyle LayoutStyle {
			get { return layout_style; }
			set {
				if (this.layout_style != value) {
					if (!Enum.IsDefined (typeof (ToolStripLayoutStyle), value))
						throw new InvalidEnumArgumentException (string.Format ("Enum argument value '{0}' is not valid for ToolStripLayoutStyle", value));

					this.layout_style = value;

					if (this.layout_style == ToolStripLayoutStyle.Flow)
						this.layout_engine = FlowLayout.Instance;
					else if (this.layout_style == ToolStripLayoutStyle.Table)
						this.layout_engine = TableLayout.Instance;
					else
						this.layout_engine = new ToolStripSplitStackLayout ();

					if (this.layout_style == ToolStripLayoutStyle.StackWithOverflow) {
						if (this.Dock == DockStyle.Left || this.Dock == DockStyle.Right)
							this.layout_style = ToolStripLayoutStyle.VerticalStackWithOverflow;
						else
							this.layout_style = ToolStripLayoutStyle.HorizontalStackWithOverflow;
					}

					if (this.layout_style == ToolStripLayoutStyle.HorizontalStackWithOverflow)
						this.orientation = Orientation.Horizontal;
					else if (this.layout_style == ToolStripLayoutStyle.VerticalStackWithOverflow)
						this.orientation = Orientation.Vertical;
						
					this.layout_settings = this.CreateLayoutSettings (value);
					
					this.PerformLayout (this, "LayoutStyle");
					this.OnLayoutStyleChanged (EventArgs.Empty);
				}
			}
		}

		[Browsable (false)]
		public Orientation Orientation {
			get { return this.orientation; }
		}

		[Browsable (false)]
		[EditorBrowsable (EditorBrowsableState.Advanced)]
		public ToolStripOverflowButton OverflowButton {
			get { return this.overflow_button; }
		}
		
		[Browsable (false)]
		[DesignerSerializationVisibility (DesignerSerializationVisibility.Hidden)]
		public ToolStripRenderer Renderer {
			get { 
				if (this.render_mode == ToolStripRenderMode.ManagerRenderMode)
					return ToolStripManager.Renderer;
					
				return this.renderer; 
			}
			set { 
				if (this.renderer != value) {
					this.renderer = value; 
					this.render_mode = ToolStripRenderMode.Custom;
					this.PerformLayout (this, "Renderer");
					this.OnRendererChanged (EventArgs.Empty);
				}
			}
		}

		public ToolStripRenderMode RenderMode {
			get { return this.render_mode; }
			set {
				if (!Enum.IsDefined (typeof (ToolStripRenderMode), value))
					throw new InvalidEnumArgumentException (string.Format ("Enum argument value '{0}' is not valid for ToolStripRenderMode", value));

				if (value == ToolStripRenderMode.Custom && this.renderer == null)
					throw new NotSupportedException ("Must set Renderer property before setting RenderMode to Custom");
				else if (value == ToolStripRenderMode.Professional)
					this.Renderer = new ToolStripProfessionalRenderer ();
				else if (value == ToolStripRenderMode.System)
					this.Renderer = new ToolStripSystemRenderer ();
					
				this.render_mode = value;
			}
		}

		[DefaultValue (true)]
		public bool ShowItemToolTips {
			get { return this.show_item_tool_tips; }
			set { this.show_item_tool_tips = value; }
		}
		
		[DefaultValue (false)]
		public bool Stretch {
			get { return this.stretch; }
			set { this.stretch = value; }
		}
		
		[DefaultValue (false)]
		[DispId(-516)]
		public new bool TabStop {
			get { return base.TabStop; }
			set { 
				base.TabStop = value;
				SetStyle (ControlStyles.Selectable, value);
			}
		}

		[DefaultValue (ToolStripTextDirection.Horizontal)]
		public virtual ToolStripTextDirection TextDirection {
			get { return this.text_direction; }
			set {
				if (!Enum.IsDefined (typeof (ToolStripTextDirection), value))
					throw new InvalidEnumArgumentException (string.Format ("Enum argument value '{0}' is not valid for ToolStripTextDirection", value));

				if (this.text_direction != value) {
					this.text_direction = value;
					
					this.PerformLayout (this, "TextDirection");
						
					this.Invalidate ();
				}
			}
		}

		[Browsable (false)]
		[EditorBrowsable (EditorBrowsableState.Never)]
		public new VScrollProperties VerticalScroll {
			get { return base.VerticalScroll; }
		}
		#endregion

		#region Protected Properties
		protected virtual DockStyle DefaultDock { get { return DockStyle.Top; } }
		// .NET scales these to the DPI the strip is created at (and a form's auto-scaling then
		// scales the padding again, as there).
		protected virtual Padding DefaultGripMargin { get { return ScaleHelper.ScaleToDpi (new Padding (2), ScaleHelper.InitialSystemDpi); } }
		protected override Padding DefaultMargin { get { return Padding.Empty; } }
		protected override Padding DefaultPadding { get { return ScaleHelper.ScaleToDpi (new Padding (0, 0, 1, 0), ScaleHelper.InitialSystemDpi); } }
		protected virtual bool DefaultShowItemToolTips { get { return true; } }
		protected override Size DefaultSize { get { return ScaleHelper.IsThreadPerMonitorV2Aware ? ScaleHelper.ScaleToDpi (new Size (100, 25), DeviceDpi) : new Size (100, 25); } }
		/// <summary>The size this kind of strip is by default, for the layout engine to floor its
		/// height at. A strip never shrinks below it however small its items are.</summary>
		internal Size DefaultSizeInternal { get { return DefaultSize; } }
		protected internal virtual ToolStripItemCollection DisplayedItems { get { return this.displayed_items; } }
		protected internal virtual Size MaxItemSize {
			get { return new Size (Width - (GripStyle == ToolStripGripStyle.Hidden ? 1 : 8), Height); }
		}
		#endregion

		#region Public Methods
		[EditorBrowsable (EditorBrowsableState.Never)]
		public new Control GetChildAtPoint (Point point)
		{
			return base.GetChildAtPoint (point);
		}

		[EditorBrowsable (EditorBrowsableState.Never)]
		public new Control GetChildAtPoint (Point pt, GetChildAtPointSkip skipValue)
		{
			return base.GetChildAtPoint (pt, skipValue);
		}
		
		public ToolStripItem GetItemAt (Point point)
		{
			foreach (ToolStripItem tsi in this.displayed_items)
				if (tsi.Visible && tsi.Bounds.Contains (point))
					return tsi;

			return null;
		}

		public ToolStripItem GetItemAt (int x, int y)
		{
			return GetItemAt (new Point (x, y));
		}

		public virtual ToolStripItem GetNextItem (ToolStripItem start, ArrowDirection direction)
		{
			if (!Enum.IsDefined (typeof (ArrowDirection), direction))
				throw new InvalidEnumArgumentException (string.Format ("Enum argument value '{0}' is not valid for ArrowDirection", direction));

			ToolStripItem current_best = null;
			int current_best_point;
			
			switch (direction) {
				case ArrowDirection.Right:
					current_best_point = int.MaxValue;

					if (start != null)
						foreach (ToolStripItem loop_tsi in this.DisplayedItems)
							if (loop_tsi.Left >= start.Right && loop_tsi.Left < current_best_point && loop_tsi.Visible && loop_tsi.CanSelect) {
								current_best = loop_tsi;
								current_best_point = loop_tsi.Left;
							}
							
					if (current_best == null)
						foreach (ToolStripItem loop_tsi in this.DisplayedItems)
							if (loop_tsi.Left < current_best_point && loop_tsi.Visible && loop_tsi.CanSelect) {
								current_best = loop_tsi;
								current_best_point = loop_tsi.Left;
							}
							
					break;
				case ArrowDirection.Up:
					current_best_point = int.MinValue;

					if (start != null)
						foreach (ToolStripItem loop_tsi in this.DisplayedItems)
							if (loop_tsi.Bottom <= start.Top && loop_tsi.Top > current_best_point && loop_tsi.Visible && loop_tsi.CanSelect) {
								current_best = loop_tsi;
								current_best_point = loop_tsi.Top;
							}

					if (current_best == null)
						foreach (ToolStripItem loop_tsi in this.DisplayedItems)
							if (loop_tsi.Top > current_best_point && loop_tsi.Visible && loop_tsi.CanSelect) {
								current_best = loop_tsi;
								current_best_point = loop_tsi.Top;
							}

					break;
				case ArrowDirection.Left:
					current_best_point = int.MinValue;

					if (start != null)
						foreach (ToolStripItem loop_tsi in this.DisplayedItems)
							if (loop_tsi.Right <= start.Left && loop_tsi.Left > current_best_point && loop_tsi.Visible && loop_tsi.CanSelect) {
								current_best = loop_tsi;
								current_best_point = loop_tsi.Left;
							}

					if (current_best == null)
						foreach (ToolStripItem loop_tsi in this.DisplayedItems)
							if (loop_tsi.Left > current_best_point && loop_tsi.Visible && loop_tsi.CanSelect) {
								current_best = loop_tsi;
								current_best_point = loop_tsi.Left;
							}

					break;
				case ArrowDirection.Down:
					current_best_point = int.MaxValue;

					if (start != null) 
						foreach (ToolStripItem loop_tsi in this.DisplayedItems)
							if (loop_tsi.Top >= start.Bottom && loop_tsi.Bottom < current_best_point && loop_tsi.Visible && loop_tsi.CanSelect) {
								current_best = loop_tsi;
								current_best_point = loop_tsi.Top;
							}

					if (current_best == null)
						foreach (ToolStripItem loop_tsi in this.DisplayedItems)
							if (loop_tsi.Top < current_best_point && loop_tsi.Visible && loop_tsi.CanSelect) {
								current_best = loop_tsi;
								current_best_point = loop_tsi.Top;
							}

					break;
			}

			return current_best;
		}

		[EditorBrowsable (EditorBrowsableState.Never)]
		public void ResetMinimumSize ()
		{
			this.MinimumSize = new Size (-1, -1);
		}

		[EditorBrowsable (EditorBrowsableState.Never)]
		public new void SetAutoScrollMargin (int x, int y)
		{
			base.SetAutoScrollMargin (x, y);
		}
		
		public override string ToString ()
		{
			return String.Format ("{0}, Name: {1}, Items: {2}", base.ToString(), this.Name, this.items.Count.ToString ());
		}
		#endregion

		#region Protected Methods
		protected override AccessibleObject CreateAccessibilityInstance ()
		{
			return new ToolStripAccessibleObject (this);
		}
		
		protected override ControlCollection CreateControlsInstance ()
		{
			return base.CreateControlsInstance ();
		}

		protected internal virtual ToolStripItem CreateDefaultItem (string text, Image image, EventHandler onClick)
		{
			if (text == "-")
				return new ToolStripSeparator ();

			if (this is ToolStripDropDown)
				return new ToolStripMenuItem (text, image, onClick);
				
			return new ToolStripButton (text, image, onClick);
		}

		protected virtual LayoutSettings CreateLayoutSettings (ToolStripLayoutStyle layoutStyle)
		{
			switch (layoutStyle) {
				case ToolStripLayoutStyle.Flow:
					return new FlowLayoutSettings (this);
				case ToolStripLayoutStyle.Table:
					return new TableLayoutSettings (this);
				case ToolStripLayoutStyle.StackWithOverflow:
				case ToolStripLayoutStyle.HorizontalStackWithOverflow:
				case ToolStripLayoutStyle.VerticalStackWithOverflow:
				default:
					return null;
			}
		}
		
		protected override void Dispose (bool disposing)
		{
			if (!IsDisposed) {

				if(disposing) {
					// Event Handler must be stopped before disposing Items.
					Events.Dispose();

					// ToolStripItem.Dispose modifes the collection,
					// so we iterate it in reverse order
					for (int i = Items.Count - 1; i >= 0; i--)
						Items [i].Dispose ();

					if (this.overflow_button != null && this.overflow_button.drop_down != null)
						this.overflow_button.drop_down.Dispose ();

					ToolStripManager.RemoveToolStrip (this);

					if (tooltip_timer != null)
						tooltip_timer.Dispose();

					if (tooltip_window != null)
						tooltip_window.Dispose();
				}
				base.Dispose (disposing);
			}
		}

		protected virtual void OnBeginDrag (EventArgs e)
		{
			EventHandler eh = (EventHandler)(Events[BeginDragEvent]);
			if (eh != null)
				eh (this, e);
		}
		
		protected override void OnDockChanged (EventArgs e)
		{
			base.OnDockChanged (e);
		}

		protected virtual void OnEndDrag (EventArgs e)
		{
			EventHandler eh = (EventHandler)(Events[EndDragEvent]);
			if (eh != null)
				eh (this, e);
		}

		protected override bool IsInputChar (char charCode)
		{
			return base.IsInputChar (charCode);
		}

		protected override bool IsInputKey (Keys keyData)
		{
			return base.IsInputKey (keyData);
		}
		
		protected override void OnEnabledChanged (EventArgs e)
		{
			base.OnEnabledChanged (e);
			
			foreach (ToolStripItem tsi in this.Items)
				tsi.OnParentEnabledChanged (EventArgs.Empty);
		}

		protected override void OnFontChanged (EventArgs e)
		{
			base.OnFontChanged (e);
		}

		protected override void OnHandleCreated (EventArgs e)
		{
			base.OnHandleCreated (e);
			UpdateDropRegistration ();
		}

		protected override void OnHandleDestroyed (EventArgs e)
		{
			base.OnHandleDestroyed (e);
		}

		protected override void OnInvalidated (InvalidateEventArgs e)
		{
			base.OnInvalidated (e);
		}

		protected internal virtual void OnItemAdded (ToolStripItemEventArgs e)
		{
			if (e.Item.AllowDrop)
				UpdateDropRegistration ();
			if (e.Item.InternalVisible)
				e.Item.Available = true;
				
			e.Item.SetPlacement (ToolStripItemPlacement.Main);
			
			if (this.Created)
				this.PerformLayout ();
			
			ToolStripItemEventHandler eh = (ToolStripItemEventHandler)(Events [ItemAddedEvent]);
			if (eh != null)
				eh (this, e);
		}

		protected virtual void OnItemClicked (ToolStripItemClickedEventArgs e)
		{
			if (this.KeyboardActive)
				ToolStripManager.SetActiveToolStrip (null, false);
			
			ToolStripItemClickedEventHandler eh = (ToolStripItemClickedEventHandler)(Events [ItemClickedEvent]);
			if (eh != null)
				eh (this, e);
		}

		protected internal virtual void OnItemRemoved (ToolStripItemEventArgs e)
		{
			ToolStripItemEventHandler eh = (ToolStripItemEventHandler)(Events [ItemRemovedEvent]);
			if (eh != null)
				eh (this, e);
		}

		protected override void OnLayout (LayoutEventArgs e)
		{
			base.OnLayout (e);
			this.SetDisplayedItems ();
			this.OnLayoutCompleted (EventArgs.Empty);
			this.Invalidate ();
		}

		protected virtual void OnLayoutCompleted (EventArgs e)
		{
			EventHandler eh = (EventHandler)(Events [LayoutCompletedEvent]);
			if (eh != null)
				eh (this, e);
		}

		protected virtual void OnLayoutStyleChanged (EventArgs e)
		{
			EventHandler eh = (EventHandler)(Events[LayoutStyleChangedEvent]);
			if (eh != null)
				eh (this, e);
		}

		protected override void OnLeave (EventArgs e)
		{
			base.OnLeave (e);
		}

		protected override void OnLostFocus (EventArgs e)
		{
			base.OnLostFocus (e);
		}

		protected override void OnMouseCaptureChanged (EventArgs e)
		{
			base.OnMouseCaptureChanged (e);
		}
		
		protected override void OnMouseDown (MouseEventArgs mea)
		{
			// Alt and a press on an item: the start of moving it (AllowItemReorder)
			if (mea.Button == MouseButtons.Left && this.allow_item_reorder && (Control.ModifierKeys & Keys.Alt) != 0) {
				ToolStripItem pressed = this.GetItemAt (mea.X, mea.Y);
				if (pressed != null && pressed != this.overflow_button) {
					reorder_pending = pressed;
					reorder_from = mea.Location;
					return;
				}
			}

			if (mea.Button == MouseButtons.Left && GripDraggable && this.GripRectangle.Contains (mea.Location)) {
				grip_dragging = true;
				grip_grab = mea.Location;
				this.Capture = true;
				OnBeginDrag (EventArgs.Empty);
				return;
			}

			if (mouse_currently_over != null)
			{
				ToolStripItem focused = GetCurrentlyFocusedItem ();

				if (focused != null && focused != mouse_currently_over)
					this.FocusInternal (true);

				if (this is MenuStrip && !menu_selected) {
					(this as MenuStrip).FireMenuActivate ();
					menu_selected = true;				
				}
					
				mouse_currently_over.FireEvent (mea, ToolStripItemEventType.MouseDown);
				
				if (this is MenuStrip && mouse_currently_over is ToolStripMenuItem && !(mouse_currently_over as ToolStripMenuItem).HasDropDownItems)
					return;
			} else {
				this.Dismiss (ToolStripDropDownCloseReason.AppClicked);
			}
			
			if (this is MenuStrip)
				this.Capture = false;

			base.OnMouseDown (mea);
		}

		protected override void OnMouseLeave (EventArgs e)
		{
			if (!grip_dragging)
				ShowGripCursor (false);
			if (mouse_currently_over != null) {
				MouseLeftItem (mouse_currently_over);
				mouse_currently_over.FireEvent (e, ToolStripItemEventType.MouseLeave);
				mouse_currently_over = null;
			}

			base.OnMouseLeave (e);
		}

		protected override void OnMouseMove (MouseEventArgs mea)
		{
			if (reorder_pending != null) {
				Size slop = SystemInformation.DragSize;
				if ((mea.Button & MouseButtons.Left) == 0) {
					reorder_pending = null;
				} else if (Math.Abs (mea.X - reorder_from.X) > slop.Width / 2 || Math.Abs (mea.Y - reorder_from.Y) > slop.Height / 2) {
					ToolStripItem moving = reorder_pending;
					reorder_pending = null;
					var data = new DataObject ();
					data.SetData (typeof (ToolStripItem), moving);
					moving.DoDragDrop (data, DragDropEffects.Move);
				}
				return;
			}

			if (grip_dragging) {
				Point screen = PointToScreen (mea.Location);
				ToolStripPanel panel = PanelUnder (screen);
				if (panel != null) {
					Point pointer = panel.PointToClient (screen);
					if (panel != this.Parent)
						panel.Join (this, pointer);
					panel.MoveTo (this, new Point (pointer.X - grip_grab.X, pointer.Y - grip_grab.Y), pointer);
				}
				return;
			}
			ShowGripCursor (GripDraggable && this.GripRectangle.Contains (mea.Location));

			ToolStripItem tsi;
			// Find the item we are now 
			if (this.overflow_button != null && this.overflow_button.Visible && this.overflow_button.Bounds.Contains (mea.Location))
				tsi = this.overflow_button;
			else
				tsi = this.GetItemAt (mea.X, mea.Y);

			if (tsi != null) {
				// If we were already hovering on this item, just send a mouse move
				if (tsi == mouse_currently_over) 
					tsi.FireEvent (mea, ToolStripItemEventType.MouseMove);
				else {
					// If we were over a different item, fire a mouse leave on it
					if (mouse_currently_over != null) {
						MouseLeftItem (tsi);
						mouse_currently_over.FireEvent (mea, ToolStripItemEventType.MouseLeave);
					}
					
					// Set the new item we are currently over
					mouse_currently_over = tsi;
					
					// Fire mouse enter and mouse move
					tsi.FireEvent (mea, ToolStripItemEventType.MouseEnter);
					MouseEnteredItem (tsi);
					tsi.FireEvent (mea, ToolStripItemEventType.MouseMove);

					// If we're over something with a drop down, show it
					if (menu_selected && mouse_currently_over.Enabled && mouse_currently_over is ToolStripDropDownItem && (mouse_currently_over as ToolStripDropDownItem).HasDropDownItems)
						(mouse_currently_over as ToolStripDropDownItem).ShowDropDown ();
				}
			} else {
				// We're not over anything now, just fire the mouse leave on what we used to be over
				if (mouse_currently_over != null) {
					MouseLeftItem (tsi);
					mouse_currently_over.FireEvent (mea, ToolStripItemEventType.MouseLeave);
					mouse_currently_over = null;
				}
			}
			
			base.OnMouseMove (mea);
		}

		protected override void OnMouseUp (MouseEventArgs mea)
		{
			if (reorder_pending != null) {
				reorder_pending = null;
				return;
			}

			if (grip_dragging) {
				grip_dragging = false;
				this.Capture = false;
				OnEndDrag (EventArgs.Empty);
				return;
			}

			// If we're currently over an item (set in MouseMove)
			if (mouse_currently_over != null && !(mouse_currently_over is ToolStripControlHost) && mouse_currently_over.Enabled) {
				// Fire our ItemClicked event, but only for a left mouse click.
				if (mea.Button == MouseButtons.Left)
					OnItemClicked (new ToolStripItemClickedEventArgs (mouse_currently_over));
					
				// Fire the item's MouseUp event
				if (mouse_currently_over != null)
					mouse_currently_over.FireEvent (mea, ToolStripItemEventType.MouseUp);

				// The event handler may have blocked until the mouse moved off of the ToolStripItem
				if (mouse_currently_over == null)
					return;
			}

			base.OnMouseUp (mea);
		}

		protected override void OnPaint (PaintEventArgs e)
		{
			base.OnPaint (e);

			// Draw the grip
			this.OnPaintGrip (e);

			// Make each item draw itself
			for (int i = 0; i < displayed_items.Count; i++) {
				ToolStripItem tsi = displayed_items[i];
				
				if (tsi.Visible) {
					e.Graphics.TranslateTransform (tsi.Bounds.Left, tsi.Bounds.Top);
					tsi.FireEvent (e, ToolStripItemEventType.Paint);
					e.Graphics.ResetTransform ();
				}
			}

			// Paint the Overflow button if it's visible
			if (this.overflow_button != null && this.overflow_button.Visible) {
				e.Graphics.TranslateTransform (this.overflow_button.Bounds.Left, this.overflow_button.Bounds.Top);
				this.overflow_button.FireEvent (e, ToolStripItemEventType.Paint);
				e.Graphics.ResetTransform ();
			}

			// where a dragged item would go (AllowItemReorder)
			PaintInsertionMark (e.Graphics);

			Rectangle affected_bounds = new Rectangle (Point.Empty, this.Size);

			ToolStripRenderEventArgs pevent = new ToolStripRenderEventArgs (e.Graphics, this, affected_bounds, Color.Empty);
			pevent.InternalConnectedArea = CalculateConnectedArea ();

			this.Renderer.DrawToolStripBorder (pevent);

			// .NET erases these corners with the parent's background and clips them out of all the
			// painting after; filling them last leaves the same pixels.
			if (this.Renderer is ToolStripProfessionalRenderer pro && pro.GetTransparentRects (this) is Rectangle [] corners)
				using (var parent = new SolidBrush (Parent.BackColor))
					foreach (Rectangle c in corners)
						e.Graphics.FillRectangle (parent, c);
		}

		[EditorBrowsable (EditorBrowsableState.Advanced)]
		protected override void OnPaintBackground (PaintEventArgs e)
		{
			base.OnPaintBackground (e);

			Rectangle affected_bounds = new Rectangle (Point.Empty, this.Size);
			ToolStripRenderEventArgs tsrea = new ToolStripRenderEventArgs (e.Graphics, this, affected_bounds, BackColor);

			this.Renderer.DrawToolStripBackground (tsrea);
		}

		protected internal virtual void OnPaintGrip (PaintEventArgs e)
		{
			// Never draw a grip with these two layouts
			if (this.layout_style == ToolStripLayoutStyle.Flow || this.layout_style == ToolStripLayoutStyle.Table)
				return;
			
			PaintEventHandler eh = (PaintEventHandler)(Events [PaintGripEvent]);
			if (eh != null)
				eh (this, e);

			// .NET paints the grip as an item, in its own coordinates.
			Rectangle grip = this.GripRectangle;
			var state = e.Graphics.Save ();
			e.Graphics.TranslateTransform (grip.X, grip.Y);
			this.Renderer.DrawGrip (new ToolStripGripRenderEventArgs (e.Graphics, this, grip, this.GripDisplayStyle, this.grip_style));
			e.Graphics.Restore (state);
		}

		protected virtual void OnRendererChanged (EventArgs e)
		{
			EventHandler eh = (EventHandler)(Events [RendererChangedEvent]);
			if (eh != null)
				eh (this, e);
		}

		[EditorBrowsable (EditorBrowsableState.Advanced)]
		protected override void OnRightToLeftChanged (EventArgs e)
		{
			base.OnRightToLeftChanged (e);

			foreach (ToolStripItem tsi in this.Items)
				tsi.OnParentRightToLeftChanged (e);
		}

		protected override void OnScroll (ScrollEventArgs se)
		{
			base.OnScroll (se);
		}
		
		protected override void OnTabStopChanged (EventArgs e)
		{
			base.OnTabStopChanged (e);
		}

		protected override void OnVisibleChanged (EventArgs e)
		{
			if (!Visible)
				CloseToolTip (null);

			base.OnVisibleChanged (e);
		}

		protected override bool ProcessCmdKey (ref Message m, Keys keyData)
		{
			return base.ProcessCmdKey (ref m, keyData);
		}

		protected override bool ProcessDialogKey (Keys keyData)
		{
			if (!this.KeyboardActive)
				return false;
				
			// Give each item a chance to handle the key
			foreach (ToolStripItem tsi in this.Items)
				if (tsi.ProcessDialogKey (keyData))
					return true;
			
			// See if I want to handle it
			if (this.ProcessArrowKey (keyData))
				return true;
			
			ToolStrip ts = null;
			
			switch (keyData) {
				case Keys.Escape:
					this.Dismiss (ToolStripDropDownCloseReason.Keyboard);
					return true;
			
				case Keys.Control | Keys.Tab:
					ts = ToolStripManager.GetNextToolStrip (this, true);
					
					if (ts != null) {
						foreach (ToolStripItem tsi in this.Items)
							tsi.Dismiss (ToolStripDropDownCloseReason.Keyboard);

						ToolStripManager.SetActiveToolStrip (ts, true);
						ts.SelectNextToolStripItem (null, true);
					}
					
					return true;
				case Keys.Control | Keys.Shift | Keys.Tab:
					ts = ToolStripManager.GetNextToolStrip (this, false);

					if (ts != null) {
						foreach (ToolStripItem tsi in this.Items)
							tsi.Dismiss (ToolStripDropDownCloseReason.Keyboard);

						ToolStripManager.SetActiveToolStrip (ts, true);
						ts.SelectNextToolStripItem (null, true);
					}
					
					return true;
				case Keys.Down:
				case Keys.Up:
				case Keys.Left:
				case Keys.Right:
					if (GetCurrentlySelectedItem () is ToolStripControlHost)
						return false;
					break;
			}

			return base.ProcessDialogKey (keyData);
		}

		protected override bool ProcessMnemonic (char charCode)
		{
			// If any item has an explicit mnemonic, it gets the message
			foreach (ToolStripItem tsi in this.Items)
				if (tsi.Enabled && tsi.Visible && !string.IsNullOrEmpty (tsi.Text) && Control.IsMnemonic (charCode, tsi.Text))
					return tsi.ProcessMnemonic (charCode);

			// Do not try to match any further here.  See Xamarin bug 23532.

			return base.ProcessMnemonic (charCode);
		}

		private IntPtr hwnd_that_lost_focus;

		// The focus back where it was before the strip took it (a menu's keyboard mode ending, as .NET's).
		[EditorBrowsable (EditorBrowsableState.Advanced)]
		protected virtual void RestoreFocus ()
		{
			IntPtr was = hwnd_that_lost_focus;
			hwnd_that_lost_focus = IntPtr.Zero;
			if (was == IntPtr.Zero || (IsHandleCreated && was == Handle))
				return;
			Control c = Control.FromHandle (was);
			if (c != null && c.Visible && c.CanFocus)
				c.Focus ();
		}

		protected override void Select (bool directed, bool forward)
		{
			foreach (ToolStripItem tsi in this.DisplayedItems)
				if (tsi.CanSelect) {
					tsi.Select ();
					break;
				}
		}
		
		protected override void SetBoundsCore (int x, int y, int width, int height, BoundsSpecified specified)
		{
			base.SetBoundsCore (x, y, width, height, specified);
		}

		protected virtual void SetDisplayedItems ()
		{
			this.displayed_items.ClearInternal ();
			
			foreach (ToolStripItem tsi in this.items)
				if (tsi.Placement == ToolStripItemPlacement.Main && tsi.Available) {
					this.displayed_items.AddNoOwnerOrLayout (tsi);
					tsi.Parent = this; 
				}
				else if (tsi.Placement == ToolStripItemPlacement.Overflow)
					tsi.Parent = this.OverflowButton.DropDown; 
			
			if (this.OverflowButton != null)
				this.OverflowButton.DropDown.SetDisplayedItems ();
		}

		protected internal void SetItemLocation (ToolStripItem item, Point location)
		{
			if (item == null)
				throw new ArgumentNullException ("item");
				
			if (item.Owner != this)
				throw new NotSupportedException ("The item is not owned by this ToolStrip");
				
			item.SetBounds (new Rectangle (location, item.Size));
		}
		
		protected internal static void SetItemParent (ToolStripItem item, ToolStrip parent)
		{
			if (item.Owner != null) {
				item.Owner.Items.RemoveNoOwnerOrLayout (item);

				if (item.Owner is ToolStripOverflow)
					(item.Owner as ToolStripOverflow).ParentToolStrip.Items.RemoveNoOwnerOrLayout (item);
			}
			
			parent.Items.AddNoOwnerOrLayout (item);
			item.Parent = parent;
		}

		protected override void SetVisibleCore (bool visible)
		{
			base.SetVisibleCore (visible);
		}

		protected override void WndProc (ref Message m)
		{
			// the window the strip took the focus from (WM_SETFOCUS's wParam), for RestoreFocus
			if (m.Msg == (int)Msg.WM_SETFOCUS && m.WParam != Handle)
				hwnd_that_lost_focus = m.WParam;
			base.WndProc (ref m);
		}
		#endregion

		#region Public Events
		static object BeginDragEvent = new object ();
		static object EndDragEvent = new object ();
		static object ItemAddedEvent = new object ();
		static object ItemClickedEvent = new object ();
		static object ItemRemovedEvent = new object ();
		static object LayoutCompletedEvent = new object ();
		static object LayoutStyleChangedEvent = new object ();
		static object PaintGripEvent = new object ();
		static object RendererChangedEvent = new object ();

		[Browsable (true)]
		[EditorBrowsable (EditorBrowsableState.Always)]
		public new event EventHandler AutoSizeChanged {
			add { base.AutoSizeChanged += value; }
			remove { base.AutoSizeChanged -= value; }
		}

		public event EventHandler BeginDrag {
			add { Events.AddHandler (BeginDragEvent, value); }
			remove { Events.RemoveHandler (BeginDragEvent, value); }
		}

		[Browsable (false)]
		public new event EventHandler CausesValidationChanged {
			add { base.CausesValidationChanged += value; }
			remove { base.CausesValidationChanged -= value; }
		}

		[Browsable (false)]
		[EditorBrowsable (EditorBrowsableState.Never)]
		public new event ControlEventHandler ControlAdded {
			add { base.ControlAdded += value; }
			remove { base.ControlAdded -= value; }
		}

		[Browsable (false)]
		[EditorBrowsable (EditorBrowsableState.Never)]
		public new event ControlEventHandler ControlRemoved {
			add { base.ControlRemoved += value; }
			remove { base.ControlRemoved -= value; }
		}
		
		[Browsable (false)]
		public new event EventHandler CursorChanged {
			add { base.CursorChanged += value; }
			remove { base.CursorChanged -= value; }
		}

		public event EventHandler EndDrag {
			add { Events.AddHandler (EndDragEvent, value); }
			remove { Events.RemoveHandler (EndDragEvent, value); }
		}

		[Browsable (false)]
		public new event EventHandler ForeColorChanged {
			add { base.ForeColorChanged += value; }
			remove { base.ForeColorChanged -= value; }
		}

		public event ToolStripItemEventHandler ItemAdded {
			add { Events.AddHandler (ItemAddedEvent, value); }
			remove { Events.RemoveHandler (ItemAddedEvent, value); }
		}

		public event ToolStripItemClickedEventHandler ItemClicked {
			add { Events.AddHandler (ItemClickedEvent, value); }
			remove { Events.RemoveHandler (ItemClickedEvent, value); }
		}

		public event ToolStripItemEventHandler ItemRemoved {
			add { Events.AddHandler (ItemRemovedEvent, value); }
			remove { Events.RemoveHandler (ItemRemovedEvent, value); }
		}

		public event EventHandler LayoutCompleted {
			add { Events.AddHandler (LayoutCompletedEvent, value); }
			remove { Events.RemoveHandler (LayoutCompletedEvent, value); }
		}

		public event EventHandler LayoutStyleChanged {
			add { Events.AddHandler (LayoutStyleChangedEvent, value); }
			remove { Events.RemoveHandler (LayoutStyleChangedEvent, value); }
		}

		public event PaintEventHandler PaintGrip {
			add { Events.AddHandler (PaintGripEvent, value); }
			remove { Events.RemoveHandler (PaintGripEvent, value); }
		}

		public event EventHandler RendererChanged {
			add { Events.AddHandler (RendererChangedEvent, value); }
			remove { Events.RemoveHandler (RendererChangedEvent, value); }
		}
		#endregion

		#region Internal Properties
		internal virtual bool KeyboardActive
		{
			get { return this.keyboard_active; }
			set {
				if (this.keyboard_active != value) {
					this.keyboard_active = value;
					
					if (value)
						Application.KeyboardCapture = this;
					else if (Application.KeyboardCapture == this) {
						Application.KeyboardCapture = null;
						ToolStripManager.ActivatedByKeyboard = false;
					}
					// leaving the keyboard mode with the focus: it goes back where it came from
					if (!value && this.Focused)
						RestoreFocus ();
					
					// Redraw for mnemonic underlines
					this.Invalidate ();
				}
			}
		}
		#endregion
		
		#region Private Methods
		internal virtual Rectangle CalculateConnectedArea ()
		{
			return Rectangle.Empty;
		}
		
		internal void ChangeSelection (ToolStripItem nextItem)
		{
			if (Application.KeyboardCapture != this)
				ToolStripManager.SetActiveToolStrip (this, ToolStripManager.ActivatedByKeyboard);
				
			foreach (ToolStripItem tsi in this.Items)
				if (tsi != nextItem)
					tsi.Dismiss (ToolStripDropDownCloseReason.Keyboard);

			ToolStripItem current = GetCurrentlySelectedItem ();

			if (current != null && !(current is ToolStripControlHost))
				this.FocusInternal (true);

			if (nextItem is ToolStripControlHost)
				(nextItem as ToolStripControlHost).Focus ();

			nextItem.Select ();
			
			if (nextItem.Parent is MenuStrip && (nextItem.Parent as MenuStrip).MenuDroppedDown)
				(nextItem as ToolStripMenuItem).HandleAutoExpansion ();
		}
		
		internal virtual void Dismiss ()
		{
			this.Dismiss (ToolStripDropDownCloseReason.AppClicked);
		}
		
		internal virtual void Dismiss (ToolStripDropDownCloseReason reason)
		{
			// Release our stranglehold on the keyboard
			this.KeyboardActive = false;
			
			// Set our drop down flag to false;
			this.menu_selected = false;
			
			// Make sure all of our items are deselected and repainted
			foreach (ToolStripItem tsi in this.Items)
				tsi.Dismiss (reason);
				
			// We probably need to redraw for mnemonic underlines
			this.Invalidate ();
		}

		internal ToolStripItem GetCurrentlySelectedItem ()
		{
			foreach (ToolStripItem tsi in this.DisplayedItems)
				if (tsi.Selected)
					return tsi;
					
			return null;
		}
		
		internal ToolStripItem GetCurrentlyFocusedItem ()
		{
			foreach (ToolStripItem tsi in this.DisplayedItems)
				if ((tsi is ToolStripControlHost) && (tsi as ToolStripControlHost).Control.Focused)
					return tsi;

			return null;
		}

		internal override Size GetPreferredSizeCore (Size proposedSize)
		{
			return LayoutEngine.GetPreferredSize (this, proposedSize - Padding.Size) + Padding.Size;
		}
		
		internal virtual ToolStrip GetTopLevelToolStrip ()
		{
			return this;
		}
		
		internal virtual void HandleItemClick (ToolStripItem dismissingItem)
		{
			this.GetTopLevelToolStrip ().Dismiss (ToolStripDropDownCloseReason.ItemClicked);
		}
		
		internal void NotifySelectedChanged (ToolStripItem tsi)
		{
			foreach (ToolStripItem tsi2 in this.DisplayedItems)
				if (tsi != tsi2)
					if (tsi2 is ToolStripDropDownItem)
						(tsi2 as ToolStripDropDownItem).HideDropDown (ToolStripDropDownCloseReason.Keyboard);

			if (this.OverflowButton != null) {
				ToolStripItemCollection tsic = this.OverflowButton.DropDown.DisplayedItems;
				
				foreach (ToolStripItem tsi2 in tsic)
					if (tsi != tsi2)
						if (tsi2 is ToolStripDropDownItem)
							(tsi2 as ToolStripDropDownItem).HideDropDown (ToolStripDropDownCloseReason.Keyboard);
			
				this.OverflowButton.HideDropDown ();
			}
			
			foreach (ToolStripItem tsi2 in this.Items)
				if (tsi != tsi2)
					tsi2.Dismiss (ToolStripDropDownCloseReason.Keyboard);
		}
		
		internal virtual bool OnMenuKey ()
		{
			return false;
		}

		internal virtual bool ProcessArrowKey (Keys keyData)
		{
			ToolStripItem tsi;
			
			switch (keyData) {
				case Keys.Right:
					tsi = this.GetCurrentlySelectedItem ();
					
					if (tsi is ToolStripControlHost)
						return false;
					
					tsi = this.SelectNextToolStripItem (tsi, true);
					
					if (tsi is ToolStripControlHost)
						(tsi as ToolStripControlHost).Focus ();
						
					return true;
				case Keys.Tab:
					tsi = this.GetCurrentlySelectedItem ();

					tsi = this.SelectNextToolStripItem (tsi, true);

					if (tsi is ToolStripControlHost)
						(tsi as ToolStripControlHost).Focus ();
						
					return true;
				case Keys.Left:
					tsi = this.GetCurrentlySelectedItem ();

					if (tsi is ToolStripControlHost)
						return false;

					tsi = this.SelectNextToolStripItem (tsi, false);

					if (tsi is ToolStripControlHost)
						(tsi as ToolStripControlHost).Focus ();

					return true;
				case Keys.Shift | Keys.Tab:
					tsi = this.GetCurrentlySelectedItem ();
					
					tsi = this.SelectNextToolStripItem (tsi, false);

					if (tsi is ToolStripControlHost)
						(tsi as ToolStripControlHost).Focus ();

					return true;
			}

			return false;
		}

		internal virtual ToolStripItem SelectNextToolStripItem (ToolStripItem start, bool forward)
		{
			ToolStripItem next_item = this.GetNextItem (start, forward ? ArrowDirection.Right : ArrowDirection.Left);
			
			if (next_item == null)
				return next_item;
				
			this.ChangeSelection (next_item);

			if (next_item is ToolStripControlHost)
				(next_item as ToolStripControlHost).Focus ();
		
			return next_item;
		}

		ArrangedElementCollection IArrangedContainer.Controls {
			get { return Items; }
		}

		#region Stuff for ToolTips
		private void MouseEnteredItem (ToolStripItem item)
		{
			if (this.show_item_tool_tips && !(item is ToolStripTextBox)) {
				ToolTipTimer.Interval = InitialToolTipDelay;
				tooltip_state = ToolTip.TipState.Initial;
				tooltip_currently_showing = item;
				ToolTipTimer.Start ();
			}
		}
	
		private void CloseToolTip (ToolStripItem item)
		{
			if (tooltip_timer != null)
				tooltip_timer.Stop ();
			if (tooltip_window != null)
				tooltip_window.Hide (this);
			tooltip_currently_showing = null;
			tooltip_state = ToolTip.TipState.Down;
		}

		private void MouseLeftItem (ToolStripItem item)
		{
			CloseToolTip (item);
		}

		private Timer ToolTipTimer {
			get {
				if (tooltip_timer == null) {
					tooltip_timer = new Timer ();
					tooltip_timer.Enabled = false;
					tooltip_timer.Interval = InitialToolTipDelay;
					tooltip_timer.Tick += new EventHandler (ToolTipTimer_Tick);
				}
				
				return tooltip_timer;
			}
		}
		
		private ToolTip ToolTipWindow {
			get {
				if (tooltip_window == null)
					tooltip_window = new ToolTip ();
					
				return tooltip_window;
			}
		}
		
		private void ShowToolTip ()
		{
			string tooltip = tooltip_currently_showing.GetToolTip ();
			
			if (!string.IsNullOrEmpty (tooltip)) {
				ToolTipWindow.Present (this, tooltip);
				ToolTipTimer.Interval = ToolTipDelay;
				ToolTipTimer.Start ();
				tooltip_state = ToolTip.TipState.Show;
			}

			tooltip_currently_showing.FireEvent (EventArgs.Empty, ToolStripItemEventType.MouseHover);
		}

		private void ToolTipTimer_Tick (object o, EventArgs args)
		{
			ToolTipTimer.Stop ();

			switch (tooltip_state) {
				case ToolTip.TipState.Initial:
					ShowToolTip ();
					break;
				case ToolTip.TipState.Show:
					CloseToolTip (null);
					break;
			}
		}
		#endregion

		#region Stuff for Merging
		internal ToolStrip CurrentlyMergedWith {
			get { return this.currently_merged_with; }
			set { this.currently_merged_with = value; }
		}
		
		internal List<ToolStripItem> HiddenMergedItems {
			get {
				if (this.hidden_merged_items == null)
					this.hidden_merged_items = new List<ToolStripItem> ();
					
				return this.hidden_merged_items;
			}
		}
		
		internal bool IsCurrentlyMerged {
			get { return this.is_currently_merged; }
			set { 
				this.is_currently_merged = value; 
				
				if (!value && this is MenuStrip) 
					foreach (ToolStripMenuItem tsmi in this.Items)
						tsmi.DropDown.IsCurrentlyMerged = value;
			 }
		}
		
		internal void BeginMerge ()
		{
			if (!IsCurrentlyMerged) {
				IsCurrentlyMerged = true;
				
				if (this.pre_merge_items == null) {
					this.pre_merge_items = new List<ToolStripItem> ();
			
				foreach (ToolStripItem tsi in this.Items)
					this.pre_merge_items.Add (tsi);
				}
			}
		}
		
		internal void RevertMergeItem (ToolStripItem item)
		{
			int index = 0;

			// Remove it from it's current Parent
			if (item.Parent != null && item.Parent != this) {
				if (item.Parent is ToolStripOverflow)
					(item.Parent as ToolStripOverflow).ParentToolStrip.Items.RemoveNoOwnerOrLayout (item);
				else
					item.Parent.Items.RemoveNoOwnerOrLayout (item);

				item.Parent = item.Owner;	
			}
			
			// Find where the item was before the merge
			index = item.Owner.pre_merge_items.IndexOf (item);

			// Find the first pre-merge item that was after this item, that
			// is currently in the Items collection.  Insert our item before
			// that one.
			for (int i = index; i < this.pre_merge_items.Count; i++) {
				if (this.Items.Contains (this.pre_merge_items[i])) {
					item.Owner.Items.InsertNoOwnerOrLayout (this.Items.IndexOf (this.pre_merge_items[i]), item);
					return;
				}
			}
			
			// There aren't any items that are supposed to be after this item,
			// so just append it to the end.
			item.Owner.Items.AddNoOwnerOrLayout (item);
		}
		#endregion
		#endregion

		#region ToolStripAccessibleObject
		[ComVisible (true)]
		public class ToolStripAccessibleObject : ControlAccessibleObject
		{
			#region Public Constructor
			public ToolStripAccessibleObject (ToolStrip owner) : base (owner)
			{
			}
			#endregion
			
			#region Public Properties
			public override AccessibleRole Role {
				get { return AccessibleRole.ToolBar; }
			}
			#endregion

			#region Public Methods
			public override AccessibleObject GetChild (int index)
			{
				return base.GetChild (index);
			}

			public override int GetChildCount ()
			{
				return (owner as ToolStrip).Items.Count;
			}

			public override AccessibleObject HitTest (int x, int y)
			{
				return base.HitTest (x, y);
			}
			#endregion
		}
		#endregion
	}
}
