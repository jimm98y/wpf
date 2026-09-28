// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// The internal entry points of .NET's ButtonRenderer / CheckBoxRenderer / RadioButtonRenderer that
// the adapters call, onto the port's renderers (which draw the one managed Windows 11 theme), plus
// the one GDI frame control the flat check box uses.

using System.Drawing;
using System.Windows.Forms.VisualStyles;

namespace System.Windows.Forms.ButtonInternal
{
	internal static class ButtonAdapterRenderers
	{
		internal static CheckBoxState CheckBoxStateFrom (ButtonState state, bool isMixed, bool isHot)
		{
			if (isMixed) {
				if ((state & ButtonState.Pushed) == ButtonState.Pushed) return CheckBoxState.MixedPressed;
				if ((state & ButtonState.Inactive) == ButtonState.Inactive) return CheckBoxState.MixedDisabled;
				return isHot ? CheckBoxState.MixedHot : CheckBoxState.MixedNormal;
			}
			if ((state & ButtonState.Checked) == ButtonState.Checked) {
				if ((state & ButtonState.Pushed) == ButtonState.Pushed) return CheckBoxState.CheckedPressed;
				if ((state & ButtonState.Inactive) == ButtonState.Inactive) return CheckBoxState.CheckedDisabled;
				return isHot ? CheckBoxState.CheckedHot : CheckBoxState.CheckedNormal;
			}
			if ((state & ButtonState.Pushed) == ButtonState.Pushed) return CheckBoxState.UncheckedPressed;
			if ((state & ButtonState.Inactive) == ButtonState.Inactive) return CheckBoxState.UncheckedDisabled;
			return isHot ? CheckBoxState.UncheckedHot : CheckBoxState.UncheckedNormal;
		}

		internal static RadioButtonState RadioButtonStateFrom (ButtonState state, bool isHot)
		{
			if ((state & ButtonState.Checked) == ButtonState.Checked) {
				if ((state & ButtonState.Pushed) == ButtonState.Pushed) return RadioButtonState.CheckedPressed;
				if ((state & ButtonState.Inactive) == ButtonState.Inactive) return RadioButtonState.CheckedDisabled;
				return isHot ? RadioButtonState.CheckedHot : RadioButtonState.CheckedNormal;
			}
			if ((state & ButtonState.Pushed) == ButtonState.Pushed) return RadioButtonState.UncheckedPressed;
			if ((state & ButtonState.Inactive) == ButtonState.Inactive) return RadioButtonState.UncheckedDisabled;
			return isHot ? RadioButtonState.UncheckedHot : RadioButtonState.UncheckedNormal;
		}

		private static Graphics GraphicsOf (IDeviceContext dc) => dc as Graphics ?? (dc as PaintEventArgs)?.Graphics;

		internal static void DrawCheckBox (IDeviceContext dc, Point glyphLocation, CheckBoxState state)
		{
			Graphics g = GraphicsOf (dc);
			if (g != null)
				CheckBoxRenderer.DrawCheckBox (g, glyphLocation, state);
		}

		internal static void DrawRadioButton (IDeviceContext dc, Point glyphLocation, RadioButtonState state)
		{
			Graphics g = GraphicsOf (dc);
			if (g != null)
				RadioButtonRenderer.DrawRadioButton (g, glyphLocation, state);
		}

		/// <summary>ButtonRenderer.DrawButtonForHandle: the push button part, no focus cue.</summary>
		internal static void DrawButtonForHandle (IDeviceContext dc, Rectangle bounds, bool focused, PushButtonState state)
		{
			Graphics g = GraphicsOf (dc);
			if (g == null)
				return;
			if (Application.RenderWithVisualStyles) {
				new VisualStyleRenderer (ButtonElement (state)).DrawBackground (g, bounds);
			} else {
				ControlPaint.DrawButton (g, bounds, state == PushButtonState.Pressed ? ButtonState.Pushed
				                                   : state == PushButtonState.Disabled ? ButtonState.Inactive : ButtonState.Normal);
			}
			if (focused)
				ControlPaint.DrawFocusRectangle (g, Rectangle.Inflate (bounds, -3, -3));
		}

		private static VisualStyleElement ButtonElement (PushButtonState state)
		{
			switch (state) {
			case PushButtonState.Hot: return VisualStyleElement.Button.PushButton.Hot;
			case PushButtonState.Pressed: return VisualStyleElement.Button.PushButton.Pressed;
			case PushButtonState.Disabled: return VisualStyleElement.Button.PushButton.Disabled;
			case PushButtonState.Default: return VisualStyleElement.Button.PushButton.Default;
			default: return VisualStyleElement.Button.PushButton.Normal;
			}
		}

		/// <summary>DrawFrameControl(DFC_MENU, DFCS_MENUCHECK): the Marlett check mark ('a') in a
		/// font whose cell is the largest square centred in the rectangle, drawn with TextOut at that
		/// square's top-left. Marlett's cell is its em (win ascent 2048, descent 0, 2048 units).</summary>
		internal static void DrawMenuCheck (Graphics g, Rectangle bounds, Color color)
		{
			int side = Math.Min (bounds.Width, bounds.Height);
			if (side <= 0)
				return;
			var square = new Rectangle (bounds.X + (bounds.Width - side) / 2, bounds.Y + (bounds.Height - side) / 2, side, side);
			using (var marlett = new Font ("Marlett", side, FontStyle.Regular, GraphicsUnit.Pixel))
				TextRenderer.DrawText (g, "a", marlett, square.Location, color,
				                       TextFormatFlags.NoPadding | TextFormatFlags.NoClipping | TextFormatFlags.NoPrefix);
		}
	}
}
