// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// From .NET's System.Windows.Forms (dotnet/winforms), adapted to the port where noted: the
// high-contrast and dark-mode override renderers are not carried over.

using System.Drawing;
using System.Windows.Forms.VisualStyles;

namespace System.Windows.Forms
{
	public class ToolStripSystemRenderer : ToolStripRenderer
	{
		[ThreadStatic]
		private static VisualStyleRenderer t_renderer;

		private static VisualStyleRenderer VisualStyleRenderer {
			get {
				if (Application.RenderWithVisualStyles) {
					if (t_renderer == null && VisualStyleRenderer.IsElementDefined (VisualStyleElement.ToolBar.Button.Normal))
						t_renderer = new VisualStyleRenderer (VisualStyleElement.ToolBar.Button.Normal);
				} else {
					t_renderer = null;
				}
				return t_renderer;
			}
		}

		public ToolStripSystemRenderer ()
		{
		}

		private static void FillBackground (Graphics g, Rectangle bounds, Color backColor)
		{
			using (var b = new SolidBrush (backColor))
				g.FillRectangle (b, bounds);
		}

		private static int GetItemState (ToolStripItem item) => (int) GetToolBarState (item);

		private static int GetSplitButtonDropDownItemState (ToolStripSplitButton item) => (int) GetSplitButtonToolBarState (item, dropDownButton: true);

		private static int GetSplitButtonItemState (ToolStripSplitButton item) => (int) GetSplitButtonToolBarState (item, dropDownButton: false);

		private static ToolBarState GetSplitButtonToolBarState (ToolStripSplitButton button, bool dropDownButton)
		{
			ToolBarState result = ToolBarState.Normal;
			if (button != null) {
				if (!button.Enabled)
					result = ToolBarState.Disabled;
				else if (dropDownButton) {
					if (button.DropDownButtonPressed || button.ButtonPressed)
						result = ToolBarState.Pressed;
					else if (button.DropDownButtonSelected || button.ButtonSelected)
						result = ToolBarState.Hot;
				} else if (button.ButtonPressed)
					result = ToolBarState.Pressed;
				else if (button.ButtonSelected)
					result = ToolBarState.Hot;
			}
			return result;
		}

		private static ToolBarState GetToolBarState (ToolStripItem item)
		{
			ToolBarState result = ToolBarState.Normal;
			if (!item.Enabled)
				result = ToolBarState.Disabled;
			if (item is ToolStripButton button && button.Checked)
				result = button.Selected ? ToolBarState.Hot : ToolBarState.Checked;
			else if (item.Pressed)
				result = ToolBarState.Pressed;
			else if (item.Selected)
				result = ToolBarState.Hot;
			return result;
		}

		// .NET's ToolStripRenderer.ShouldPaintBackground: no back colour or image of the strip's own.
		private static bool ShouldPaintBackground (Control control)
		{
			return control.background_color == Color.Empty && control.BackgroundImage == null;
		}

		protected override void OnRenderToolStripBackground (ToolStripRenderEventArgs e)
		{
			ToolStrip toolStrip = e.ToolStrip;
			Graphics graphics = e.Graphics;
			Rectangle bounds = e.AffectedBounds;
			if (!ShouldPaintBackground (toolStrip))
				return;
			if (toolStrip is StatusStrip)
				RenderStatusStripBackground (e);
			else if (SystemInformation.HighContrast)
				FillBackground (graphics, bounds, SystemColors.ButtonFace);
			else if (toolStrip.IsDropDown)
				FillBackground (graphics, bounds, ToolStripManager.VisualStylesEnabled ? SystemColors.Menu : e.BackColor);
			else if (toolStrip is MenuStrip)
				FillBackground (graphics, bounds, ToolStripManager.VisualStylesEnabled ? SystemColors.MenuBar : e.BackColor);
			else if (ToolStripManager.VisualStylesEnabled && VisualStyleRenderer.IsElementDefined (VisualStyleElement.Rebar.Band.Normal)) {
				VisualStyleRenderer renderer = VisualStyleRenderer;
				renderer.SetParameters (VisualStyleElement.ToolBar.Bar.Normal);
				renderer.DrawBackground (graphics, bounds);
			} else
				FillBackground (graphics, bounds, ToolStripManager.VisualStylesEnabled ? SystemColors.MenuBar : e.BackColor);
		}

		protected override void OnRenderToolStripBorder (ToolStripRenderEventArgs e)
		{
			ToolStrip toolStrip = e.ToolStrip;
			Rectangle client = toolStrip.ClientRectangle;
			if (toolStrip is StatusStrip)
				RenderStatusStripBorder (e);
			else if (toolStrip is ToolStripDropDown dropDown) {
				if (dropDown.DropShadowEnabled && ToolStripManager.VisualStylesEnabled) {
					client.Width--;
					client.Height--;
					e.Graphics.DrawRectangle (SystemPens.ControlDark, client);
				} else
					ControlPaint.DrawBorder3D (e.Graphics, client, Border3DStyle.Raised);
			} else if (ToolStripManager.VisualStylesEnabled) {
				e.Graphics.DrawLine (SystemPens.ButtonHighlight, 0, client.Bottom - 1, client.Width, client.Bottom - 1);
				e.Graphics.DrawLine (SystemPens.InactiveBorder, 0, client.Bottom - 2, client.Width, client.Bottom - 2);
			} else {
				e.Graphics.DrawLine (SystemPens.ButtonHighlight, 0, client.Bottom - 1, client.Width, client.Bottom - 1);
				e.Graphics.DrawLine (SystemPens.ButtonShadow, 0, client.Bottom - 2, client.Width, client.Bottom - 2);
			}
		}

		protected override void OnRenderGrip (ToolStripGripRenderEventArgs e)
		{
			Graphics graphics = e.Graphics;
			Rectangle bounds = new Rectangle (Point.Empty, e.GripBounds.Size);
			bool vertical = e.GripDisplayStyle == ToolStripGripDisplayStyle.Vertical;
			if (ToolStripManager.VisualStylesEnabled && VisualStyleRenderer.IsElementDefined (VisualStyleElement.Rebar.Gripper.Normal)) {
				VisualStyleRenderer renderer = VisualStyleRenderer;
				if (vertical) {
					renderer.SetParameters (VisualStyleElement.Rebar.Gripper.Normal);
					bounds.Height = (bounds.Height - 2) / 4 * 4;
					bounds.Y = Math.Max (0, (e.GripBounds.Height - bounds.Height - 2) / 2);
				} else {
					renderer.SetParameters (VisualStyleElement.Rebar.GripperVertical.Normal);
				}
				renderer.DrawBackground (graphics, bounds);
				return;
			}
			FillBackground (graphics, bounds, e.ToolStrip.BackColor);
			if (vertical) {
				if (bounds.Height >= 4)
					bounds.Inflate (0, -2);
				bounds.Width = 3;
			} else {
				if (bounds.Width >= 4)
					bounds.Inflate (-2, 0);
				bounds.Height = 3;
			}
			RenderSmall3DBorderInternal (graphics, bounds, ToolBarState.Hot, e.ToolStrip.RightToLeft == RightToLeft.Yes);
		}

		protected override void OnRenderItemBackground (ToolStripItemRenderEventArgs e)
		{
		}

		protected override void OnRenderImageMargin (ToolStripRenderEventArgs e)
		{
		}

		protected override void OnRenderButtonBackground (ToolStripItemRenderEventArgs e)
		{
			RenderItemInternal (e);
		}

		protected override void OnRenderDropDownButtonBackground (ToolStripItemRenderEventArgs e)
		{
			RenderItemInternal (e);
		}

		protected override void OnRenderOverflowButtonBackground (ToolStripItemRenderEventArgs e)
		{
			ToolStripItem item = e.Item;
			Graphics graphics = e.Graphics;
			if (ToolStripManager.VisualStylesEnabled && VisualStyleRenderer.IsElementDefined (VisualStyleElement.Rebar.Chevron.Normal)) {
				VisualStyleElement chevron = VisualStyleElement.Rebar.Chevron.Normal;
				VisualStyleRenderer renderer = VisualStyleRenderer;
				renderer.SetParameters (chevron.ClassName, chevron.Part, GetItemState (item));
				renderer.DrawBackground (graphics, new Rectangle (Point.Empty, item.Size));
			} else {
				RenderItemInternal (e);
				Color arrowColor = item.Enabled ? SystemColors.ControlText : SystemColors.ControlDark;
				DrawArrow (new ToolStripArrowRenderEventArgs (graphics, item, new Rectangle (Point.Empty, item.Size), arrowColor, ArrowDirection.Down));
			}
		}

		protected override void OnRenderLabelBackground (ToolStripItemRenderEventArgs e)
		{
			RenderLabelInternal (e);
		}

		protected override void OnRenderMenuItemBackground (ToolStripItemRenderEventArgs e)
		{
			ToolStripMenuItem item = e.Item as ToolStripMenuItem;
			Graphics graphics = e.Graphics;
			if (item is MdiControlStrip.SystemMenuItem || item == null)
				return;
			Rectangle bounds = new Rectangle (Point.Empty, item.Size);
			if (!item.IsOnDropDown && !ToolStripManager.VisualStylesEnabled) {   // .NET's IsTopLevel
				if (item.BackgroundImage != null)
					ControlPaint.DrawBackgroundImage (graphics, item.BackgroundImage, item.BackColor, item.BackgroundImageLayout, item.ContentRectangle, item.ContentRectangle);
				else if (item.RawBackColor != Color.Empty)
					FillBackground (graphics, item.ContentRectangle, item.BackColor);
				RenderSmall3DBorderInternal (graphics, bounds, GetToolBarState (item), item.RightToLeft == RightToLeft.Yes);
				return;
			}
			Rectangle rect = new Rectangle (Point.Empty, item.Size);
			if (item.IsOnDropDown) {
				rect.X += 2;
				rect.Width -= 3;
			}
			if (item.Selected || item.Pressed) {
				if (item.Enabled)
					graphics.FillRectangle (SystemBrushes.Highlight, rect);
				using (Pen pen = new Pen (ToolStripManager.VisualStylesEnabled ? SystemColors.Highlight : new ProfessionalColorTable ().MenuItemBorder))
					graphics.DrawRectangle (pen, bounds.X, bounds.Y, bounds.Width - 1, bounds.Height - 1);
				return;
			}
			if (item.BackgroundImage != null)
				ControlPaint.DrawBackgroundImage (graphics, item.BackgroundImage, item.BackColor, item.BackgroundImageLayout, item.ContentRectangle, rect);
			else if (!ToolStripManager.VisualStylesEnabled && item.RawBackColor != Color.Empty)
				FillBackground (graphics, rect, item.BackColor);
		}

		protected override void OnRenderSeparator (ToolStripSeparatorRenderEventArgs e)
		{
			RenderSeparatorInternal (e.Graphics, e.Item, new Rectangle (Point.Empty, e.Item.Size), e.Vertical);
		}

		protected override void OnRenderToolStripStatusLabelBackground (ToolStripItemRenderEventArgs e)
		{
			RenderLabelInternal (e);
			if (e.Item is ToolStripStatusLabel label)
				ControlPaint.DrawBorder3D (e.Graphics, new Rectangle (0, 0, label.Width - 1, label.Height - 1), label.BorderStyle, (Border3DSide) label.BorderSides);
		}

		protected override void OnRenderSplitButtonBackground (ToolStripItemRenderEventArgs e)
		{
			if (!(e.Item is ToolStripSplitButton split))
				return;
			Graphics graphics = e.Graphics;
			bool rtl = split.RightToLeft == RightToLeft.Yes;
			Color arrowColor = split.Enabled ? SystemColors.ControlText : SystemColors.ControlDark;
			VisualStyleElement dropElement = rtl ? VisualStyleElement.ToolBar.SplitButton.Normal : VisualStyleElement.ToolBar.SplitButtonDropDown.Normal;
			VisualStyleElement buttonElement = rtl ? VisualStyleElement.ToolBar.DropDownButton.Normal : VisualStyleElement.ToolBar.SplitButton.Normal;
			if (ToolStripManager.VisualStylesEnabled && VisualStyleRenderer.IsElementDefined (dropElement) && VisualStyleRenderer.IsElementDefined (buttonElement)) {
				VisualStyleRenderer renderer = VisualStyleRenderer;
				renderer.SetParameters (buttonElement.ClassName, buttonElement.Part, GetSplitButtonItemState (split));
				Rectangle buttonBounds = split.ButtonBounds;
				if (rtl)
					buttonBounds.Inflate (2, 0);
				renderer.DrawBackground (graphics, buttonBounds);
				renderer.SetParameters (dropElement.ClassName, dropElement.Part, GetSplitButtonDropDownItemState (split));
				renderer.DrawBackground (graphics, split.DropDownButtonBounds);
				Rectangle content = split.ContentRectangle;
				if (split.BackgroundImage != null)
					ControlPaint.DrawBackgroundImage (graphics, split.BackgroundImage, split.BackColor, split.BackgroundImageLayout, content, content);
				RenderSeparatorInternal (graphics, split, split.SplitterBounds, vertical: true);
				if (rtl || split.BackgroundImage != null)
					DrawArrow (new ToolStripArrowRenderEventArgs (graphics, split, split.DropDownButtonBounds, arrowColor, ArrowDirection.Down));
				ToolBarState state = GetToolBarState (e.Item);
				if (SystemInformation.HighContrast || (state != ToolBarState.Hot && state != ToolBarState.Pressed && state != ToolBarState.Checked))
					return;
				ControlPaint.DrawBorderSimple (graphics, split.ClientBounds, SystemColors.Highlight);
				graphics.FillRectangle (SystemBrushes.Highlight, split.SplitterBounds);
				return;
			}
			Rectangle buttonBounds2 = split.ButtonBounds;
			if (split.BackgroundImage != null) {
				Rectangle all = new Rectangle (Point.Empty, split.Size);
				Rectangle clip = split.Selected ? split.ContentRectangle : all;
				ControlPaint.DrawBackgroundImage (graphics, split.BackgroundImage, split.BackColor, split.BackgroundImageLayout, all, clip);
			} else {
				FillBackground (graphics, buttonBounds2, split.BackColor);
			}
			RenderSmall3DBorderInternal (graphics, buttonBounds2, GetSplitButtonToolBarState (split, dropDownButton: false), rtl);
			Rectangle dropBounds = split.DropDownButtonBounds;
			if (split.BackgroundImage == null)
				FillBackground (graphics, dropBounds, split.BackColor);
			ToolBarState dropState = GetSplitButtonToolBarState (split, dropDownButton: true);
			if (dropState == ToolBarState.Hot || dropState == ToolBarState.Pressed)
				RenderSmall3DBorderInternal (graphics, dropBounds, dropState, rtl);
			DrawArrow (new ToolStripArrowRenderEventArgs (graphics, split, dropBounds, arrowColor, ArrowDirection.Down));
		}

		private static void RenderItemInternal (ToolStripItemRenderEventArgs e)
		{
			ToolStripItem item = e.Item;
			Graphics graphics = e.Graphics;
			ToolBarState state = GetToolBarState (item);
			VisualStyleElement element = VisualStyleElement.ToolBar.Button.Normal;
			if (ToolStripManager.VisualStylesEnabled && VisualStyleRenderer.IsElementDefined (element)) {
				VisualStyleRenderer renderer = VisualStyleRenderer;
				renderer.SetParameters (element.ClassName, element.Part, (int) state);
				renderer.DrawBackground (graphics, new Rectangle (Point.Empty, item.Size));
				if (!SystemInformation.HighContrast && (state == ToolBarState.Hot || state == ToolBarState.Pressed || state == ToolBarState.Checked)) {
					Rectangle client = item.ClientBounds;
					client.Height--;
					ControlPaint.DrawBorderSimple (graphics, client, SystemColors.Highlight);
				}
			} else {
				RenderSmall3DBorderInternal (graphics, new Rectangle (Point.Empty, item.Size), state, item.RightToLeft == RightToLeft.Yes);
			}
			Rectangle content = item.ContentRectangle;
			if (item.BackgroundImage != null) {
				ControlPaint.DrawBackgroundImage (graphics, item.BackgroundImage, item.BackColor, item.BackgroundImageLayout, content, content);
				return;
			}
			ToolStrip parent = item.GetCurrentParent ();
			if (parent != null && state != ToolBarState.Checked && item.BackColor != parent.BackColor)
				FillBackground (graphics, content, item.BackColor);
		}

		private static void RenderSeparatorInternal (Graphics g, ToolStripItem item, Rectangle bounds, bool vertical)
		{
			VisualStyleElement element = vertical ? VisualStyleElement.ToolBar.SeparatorHorizontal.Normal : VisualStyleElement.ToolBar.SeparatorVertical.Normal;
			if (ToolStripManager.VisualStylesEnabled && VisualStyleRenderer.IsElementDefined (element)) {
				VisualStyleRenderer renderer = VisualStyleRenderer;
				renderer.SetParameters (element.ClassName, element.Part, GetItemState (item));
				renderer.DrawBackground (g, bounds);
				return;
			}
			using (Pen fore = new Pen (item.ForeColor)) {
				if (vertical) {
					if (bounds.Height >= 4)
						bounds.Inflate (0, -2);
					bool rtl = item.RightToLeft == RightToLeft.Yes;
					Pen first = rtl ? SystemPens.ButtonHighlight : fore;
					Pen second = rtl ? fore : SystemPens.ButtonHighlight;
					int x = bounds.Width / 2;
					g.DrawLine (first, x, bounds.Top, x, bounds.Bottom);
					x++;
					g.DrawLine (second, x, bounds.Top, x, bounds.Bottom);
				} else {
					if (bounds.Width >= 4)
						bounds.Inflate (-2, 0);
					int y = bounds.Height / 2;
					g.DrawLine (fore, bounds.Left, y, bounds.Right, y);
					y++;
					g.DrawLine (SystemPens.ButtonHighlight, bounds.Left, y, bounds.Right, y);
				}
			}
		}

		private static void RenderSmall3DBorderInternal (Graphics g, Rectangle bounds, ToolBarState state, bool rightToLeft)
		{
			if (state == ToolBarState.Hot || state == ToolBarState.Pressed || state == ToolBarState.Checked) {
				Pen top = state == ToolBarState.Hot ? SystemPens.ButtonHighlight : SystemPens.ButtonShadow;
				Pen bottom = state == ToolBarState.Hot ? SystemPens.ButtonShadow : SystemPens.ButtonHighlight;
				Pen left = rightToLeft ? bottom : top;
				Pen right = rightToLeft ? top : bottom;
				g.DrawLine (top, bounds.Left, bounds.Top, bounds.Right - 1, bounds.Top);
				g.DrawLine (left, bounds.Left, bounds.Top, bounds.Left, bounds.Bottom - 1);
				g.DrawLine (right, bounds.Right - 1, bounds.Top, bounds.Right - 1, bounds.Bottom - 1);
				g.DrawLine (bottom, bounds.Left, bounds.Bottom - 1, bounds.Right - 1, bounds.Bottom - 1);
			}
		}

		private static void RenderStatusStripBorder (ToolStripRenderEventArgs e)
		{
			if (!Application.RenderWithVisualStyles)
				e.Graphics.DrawLine (SystemPens.ButtonHighlight, 0, 0, e.ToolStrip.Width, 0);
		}

		private static void RenderStatusStripBackground (ToolStripRenderEventArgs e)
		{
			if (Application.RenderWithVisualStyles) {
				VisualStyleRenderer renderer = VisualStyleRenderer;
				renderer.SetParameters (VisualStyleElement.Status.Bar.Normal);
				renderer.DrawBackground (e.Graphics, new Rectangle (0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1));
			} else {
				e.Graphics.Clear (e.BackColor);
			}
		}

		private static void RenderLabelInternal (ToolStripItemRenderEventArgs e)
		{
			ToolStripItem item = e.Item;
			Graphics graphics = e.Graphics;
			Rectangle content = item.ContentRectangle;
			if (item.BackgroundImage != null)
				ControlPaint.DrawBackgroundImage (graphics, item.BackgroundImage, item.BackColor, item.BackgroundImageLayout, content, content);
			else if (VisualStyleRenderer == null || item.BackColor != SystemColors.Control)
				FillBackground (graphics, content, item.BackColor);
		}
	}
}
