//
// ToolStripPanelRow.cs
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

using System.Drawing;
using System.Runtime.InteropServices;
using System.ComponentModel;
using System.Windows.Forms.Layout;
using System.Collections.Generic;

namespace System.Windows.Forms
{
	[ToolboxItem (false)]
	public class ToolStripPanelRow : Component, IComponent, IDisposable
	{
		private Rectangle bounds;
		internal List<Control> controls;
		// Where each strip wants to be along the row, from its start: a strip dragged along the row, or joined
		// at a point, stays there (pushed on by the strips before it, and back from the row's end); one with no
		// place of its own packs against the one before.
		internal readonly Dictionary<Control, int> offsets = new Dictionary<Control, int> ();
		private Padding margin;
		private Padding padding;
		private ToolStripPanel parent;

		#region Public Constructors
		public ToolStripPanelRow (ToolStripPanel parent)
		{
			this.bounds = Rectangle.Empty;
			this.controls = new List<Control> ();
			this.parent = parent;
		}
		#endregion

		#region Public Properties
		public virtual Rectangle Bounds {
			get { return this.bounds; }
		}

		[Browsable (false)]
		[DesignerSerializationVisibility (DesignerSerializationVisibility.Hidden)]
		public Control[] Controls {
			get { return this.controls.ToArray (); }
		}

		public Rectangle DisplayRectangle {
			get { return this.Bounds; }
		}

		public LayoutEngine LayoutEngine {
			get { 
				return DefaultLayout.Instance;
			}
		}

		public Padding Margin {
			get { return this.margin; }
			set { this.margin = value; }
		}

		public Orientation Orientation {
			get { return this.parent.Orientation; }
		}

		public virtual Padding Padding {
			get { return this.padding; }
			set { this.padding = value; }
		}

		public ToolStripPanel ToolStripPanel {
			get { return this.parent; }
		} 
		#endregion

		#region Protected Properties
		protected virtual Padding DefaultMargin { get { return Padding.Empty; } }
		protected virtual Padding DefaultPadding { get { return Padding.Empty; } }
		#endregion

		#region Public Methods
		public bool CanMove (ToolStrip toolStripToDrag)
		{
			// If something uses Stretch, it gets a whole Row to itself
			if (this.controls.Count > 0)
				if (toolStripToDrag.Stretch || (this.controls[0] as ToolStrip).Stretch)
					return false;
			
			int width = 0;
			
			foreach (ToolStrip ts in this.controls)
				width += (ts.Width + ts.Margin.Horizontal);
				
			if (width + toolStripToDrag.Width + toolStripToDrag.Margin.Horizontal <= this.bounds.Width)
				return true;
				
			return false;
		}
		#endregion

		#region Protected Methods
		protected override void Dispose (bool disposing)
		{
			base.Dispose (disposing);
		}
		
		protected void OnBoundsChanged (Rectangle oldBounds, Rectangle newBounds)
		{
		}
		
		protected internal virtual void OnControlAdded (Control control, int index)
		{
			control.SizeChanged += new EventHandler (control_SizeChanged);
			controls.Add (control);
			this.OnLayout (new LayoutEventArgs (control, string.Empty));
		}
		
		protected internal virtual void OnControlRemoved (Control control, int index)
		{
			control.SizeChanged -= new EventHandler (control_SizeChanged);
			controls.Remove (control);
			offsets.Remove (control);
			this.OnLayout (new LayoutEventArgs (control, string.Empty));
		}
		
		protected virtual void OnLayout (LayoutEventArgs e)
		{
			int height = 0;
			
			if (this.Orientation == Orientation.Horizontal) {
				foreach (ToolStrip ts in this.controls)
					if (ts.Height > height)
						height = ts.Height;
						
				if (height != this.bounds.Height)
					this.bounds.Height = height;
			} else {
				foreach (ToolStrip ts in this.controls)
					if (ts.GetPreferredSize (Size.Empty).Width > height)
						height = ts.GetPreferredSize (Size.Empty).Width;

				if (height != this.bounds.Width)
					this.bounds.Width = height;
			}
				
			this.Layout (this, e);
		}
		
		protected internal virtual void OnOrientationChanged ()
		{
		}
		#endregion

		#region Private/Internal Methods
		internal void SetBounds (Rectangle bounds)
		{
			if (this.bounds != bounds) {
				Rectangle old_bounds = this.bounds;
				this.bounds = bounds;
				this.OnBoundsChanged (old_bounds, bounds);
				this.OnLayout (new LayoutEventArgs (null, "Bounds"));
			}
		}
		
		/// <summary>Puts <paramref name="control"/> <paramref name="offset"/> along the row from its start (it is laid
		/// out there, or as near as the strips before it and the row's end allow).</summary>
		internal void SetOffset (Control control, int offset)
		{
			offsets [control] = Math.Max (0, offset);
		}

		internal void Relayout ()
		{
			this.OnLayout (new LayoutEventArgs (null, string.Empty));
		}

		private bool Layout (object container, LayoutEventArgs args)
		{
			ToolStripPanelRow tspr = (ToolStripPanelRow)container;
			bool horizontal = Orientation == Orientation.Horizontal;
			int start = horizontal ? tspr.DisplayRectangle.X : tspr.DisplayRectangle.Y;
			int length = horizontal ? this.bounds.Width : this.bounds.Height;

			// In the order they want to be in: a strip with a place of its own where that is, the rest after the
			// one before them (a stable order, so strips at the same place keep theirs).
			var want = new Dictionary<Control, int> ();
			int packed = 0;
			foreach (Control c in this.controls) {
				ToolStrip t = (ToolStrip)c;
				int w = offsets.TryGetValue (c, out int o) ? o : packed;
				want [c] = w;
				Size pref = t.GetPreferredSize (Size.Empty);
				packed = w + (horizontal ? pref.Width + t.Margin.Horizontal : pref.Height + t.Margin.Vertical);
			}
			var order = new List<Control> (System.Linq.Enumerable.OrderBy (this.controls, c => want [c]));
			if (!System.Linq.Enumerable.SequenceEqual (order, this.controls)) {
				this.controls.Clear ();
				this.controls.AddRange (order);
			}

			// each strip's size, then its place: where it wants to be, past the one before it
			var at = new int [order.Count];
			var size = new int [order.Count];
			int next = 0;
			for (int i = 0; i < order.Count; i++) {
				ToolStrip ts = (ToolStrip)order [i];
				if (horizontal) {
					if (ts.Stretch)
						ts.Width = this.bounds.Width - ts.Margin.Horizontal - this.Padding.Horizontal;
					else
						ts.Width = ts.GetPreferredSize (Size.Empty).Width;
					size [i] = ts.Width + ts.Margin.Horizontal;
				} else {
					if (ts.Stretch)
						ts.Size = new Size (ts.GetPreferredSize (Size.Empty).Width, this.bounds.Height - ts.Margin.Vertical - this.Padding.Vertical);
					else
						ts.Size = ts.GetPreferredSize (Size.Empty);
					size [i] = ts.Height + ts.Margin.Vertical;
				}
				at [i] = Math.Max (next, want [ts]);
				next = at [i] + size [i];
			}
			// and back from the row's end, as far as its start allows: a strip moved to the end pushes the ones
			// before it back in rather than off the row
			int limit = length;
			for (int i = order.Count - 1; i >= 0; i--) {
				if (at [i] + size [i] > limit)
					at [i] = Math.Max (0, limit - size [i]);
				limit = at [i];
			}
			for (int i = 0; i < order.Count; i++) {
				ToolStrip ts = (ToolStrip)order [i];
				if (horizontal)
					ts.Location = new Point (start + at [i] + ts.Margin.Left, tspr.DisplayRectangle.Y + ts.Margin.Top);
				else
					ts.Location = new Point (tspr.DisplayRectangle.X + ts.Margin.Left, start + at [i] + ts.Margin.Top);
			}
			return false;
		}

		void control_SizeChanged (object sender, EventArgs e)
		{
			this.OnLayout (new LayoutEventArgs ((Control)sender, string.Empty));
		}
		#endregion
	}
}
