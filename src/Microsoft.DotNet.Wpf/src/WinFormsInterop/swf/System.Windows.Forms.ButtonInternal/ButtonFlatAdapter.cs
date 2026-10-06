// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// From .NET's System.Windows.Forms (dotnet/winforms), adapted to the port where noted.

using System.Drawing;

namespace System.Windows.Forms.ButtonInternal;

internal class ButtonFlatAdapter : ButtonBaseAdapter
{
	private const int BorderSize = 1;

	internal ButtonFlatAdapter(ButtonBase control)
		: base(control)
	{
	}

	private void PaintBackground(PaintEventArgs e, Rectangle r, Color backColor)
	{
		Rectangle rectangle = r;
		rectangle.Inflate(-Control.FlatAppearance.BorderSize, -Control.FlatAppearance.BorderSize);
		Control.PaintBackground(e, rectangle, backColor, rectangle.Location);
	}

	internal override void PaintUp(PaintEventArgs e, CheckState state)
	{
		bool flag = Control.FlatAppearance.BorderSize != 1 || !Control.FlatAppearance.BorderColor.IsEmpty;
		ColorData colorData = PaintFlatRender(e).Calculate();
		LayoutData layoutData = PaintFlatLayout(!Control.FlatAppearance.CheckedBackColor.IsEmpty || (SystemInformation.HighContrast ? (state != CheckState.Indeterminate) : (state == CheckState.Unchecked)), !flag && SystemInformation.HighContrast && state == CheckState.Checked, Control.FlatAppearance.BorderSize).Layout();
		if (!Control.FlatAppearance.BorderColor.IsEmpty)
		{
			colorData.WindowFrame = Control.FlatAppearance.BorderColor;
		}
		Rectangle clientRectangle = Control.ClientRectangle;
		Color color = Control.BackColor;
		if (!Control.FlatAppearance.CheckedBackColor.IsEmpty)
		{
			switch (state)
			{
			case CheckState.Checked:
				color = Control.FlatAppearance.CheckedBackColor;
				break;
			case CheckState.Indeterminate:
				color = Control.FlatAppearance.CheckedBackColor.MixColor(colorData.ButtonFace);
				break;
			}
		}
		else
		{
			switch (state)
			{
			case CheckState.Checked:
				color = colorData.Highlight;
				break;
			case CheckState.Indeterminate:
				color = colorData.Highlight.MixColor(colorData.ButtonFace);
				break;
			}
		}
		PaintBackground(e, clientRectangle, IsHighContrastHighlighted() ? SystemColors.Highlight : color);
		if (Control.IsDefault)
		{
			clientRectangle.Inflate(-1, -1);
		}
		PaintImage(e, layoutData);
		PaintField(e, layoutData, colorData, IsHighContrastHighlighted() ? SystemColors.HighlightText : colorData.WindowText, drawFocus: false);
		if (Control.Focused && Control.ShowFocusCues)
		{
			ButtonBaseAdapter.DrawFlatFocus(e, layoutData.Focus, colorData.Options.HighContrast ? colorData.WindowText : colorData.ContrastButtonShadow);
		}
		if (!Control.IsDefault || !Control.Focused || Control.FlatAppearance.BorderSize != 0)
		{
			ButtonBaseAdapter.DrawDefaultBorder(e, clientRectangle, colorData.WindowFrame, Control.IsDefault);
		}
		if (flag)
		{
			if (Control.FlatAppearance.BorderSize != 1)
			{
				ButtonBaseAdapter.DrawFlatBorderWithSize(e, clientRectangle, colorData.WindowFrame, Control.FlatAppearance.BorderSize);
			}
			else
			{
				ControlPaint.DrawBorderSimple(e, clientRectangle, colorData.WindowFrame);
			}
		}
		else if (state == CheckState.Checked && SystemInformation.HighContrast)
		{
			ControlPaint.DrawBorderSimple(e, clientRectangle, colorData.WindowFrame);
			ControlPaint.DrawBorderSimple(e, clientRectangle, colorData.ButtonShadow);
		}
		else if (state == CheckState.Indeterminate)
		{
			ButtonBaseAdapter.Draw3DLiteBorder(e, clientRectangle, colorData, up: false);
		}
		else
		{
			ControlPaint.DrawBorderSimple(e, clientRectangle, colorData.WindowFrame);
		}
	}

	internal override void PaintDown(PaintEventArgs e, CheckState state)
	{
		bool flag = Control.FlatAppearance.BorderSize != 1 || !Control.FlatAppearance.BorderColor.IsEmpty;
		ColorData colorData = PaintFlatRender(e).Calculate();
		LayoutData layoutData = PaintFlatLayout(!Control.FlatAppearance.CheckedBackColor.IsEmpty || (SystemInformation.HighContrast ? (state != CheckState.Indeterminate) : (state == CheckState.Unchecked)), !flag && SystemInformation.HighContrast && state == CheckState.Checked, Control.FlatAppearance.BorderSize).Layout();
		if (!Control.FlatAppearance.BorderColor.IsEmpty)
		{
			colorData.WindowFrame = Control.FlatAppearance.BorderColor;
		}
		Rectangle clientRectangle = Control.ClientRectangle;
		Color backColor = Control.BackColor;
		if (!Control.FlatAppearance.MouseDownBackColor.IsEmpty)
		{
			backColor = Control.FlatAppearance.MouseDownBackColor;
		}
		else
		{
			switch (state)
			{
			case CheckState.Unchecked:
			case CheckState.Checked:
				backColor = (colorData.Options.HighContrast ? colorData.ButtonShadow : colorData.LowHighlight);
				break;
			case CheckState.Indeterminate:
				backColor = colorData.ButtonFace.MixColor(colorData.Options.HighContrast ? colorData.ButtonShadow : colorData.LowHighlight);
				break;
			}
		}
		PaintBackground(e, clientRectangle, backColor);
		if (Control.IsDefault)
		{
			clientRectangle.Inflate(-1, -1);
		}
		PaintImage(e, layoutData);
		PaintField(e, layoutData, colorData, colorData.WindowText, drawFocus: false);
		if (Control.Focused && Control.ShowFocusCues)
		{
			ButtonBaseAdapter.DrawFlatFocus(e, layoutData.Focus, colorData.Options.HighContrast ? colorData.WindowText : colorData.ContrastButtonShadow);
		}
		if (!Control.IsDefault || !Control.Focused || Control.FlatAppearance.BorderSize != 0)
		{
			ButtonBaseAdapter.DrawDefaultBorder(e, clientRectangle, colorData.WindowFrame, Control.IsDefault);
		}
		if (flag)
		{
			if (Control.FlatAppearance.BorderSize != 1)
			{
				ButtonBaseAdapter.DrawFlatBorderWithSize(e, clientRectangle, colorData.WindowFrame, Control.FlatAppearance.BorderSize);
			}
			else
			{
				ControlPaint.DrawBorderSimple(e, clientRectangle, colorData.WindowFrame);
			}
		}
		else if (state == CheckState.Checked && SystemInformation.HighContrast)
		{
			ControlPaint.DrawBorderSimple(e, clientRectangle, colorData.WindowFrame);
			ControlPaint.DrawBorderSimple(e, clientRectangle, colorData.ButtonShadow);
		}
		else if (state == CheckState.Indeterminate)
		{
			ButtonBaseAdapter.Draw3DLiteBorder(e, clientRectangle, colorData, up: false);
		}
		else
		{
			ControlPaint.DrawBorderSimple(e, clientRectangle, colorData.WindowFrame);
		}
	}

	internal override void PaintOver(PaintEventArgs e, CheckState state)
	{
		if (SystemInformation.HighContrast)
		{
			PaintUp(e, state);
			return;
		}
		bool flag = Control.FlatAppearance.BorderSize != 1 || !Control.FlatAppearance.BorderColor.IsEmpty;
		ColorData colorData = PaintFlatRender(e).Calculate();
		LayoutData layoutData = PaintFlatLayout(!Control.FlatAppearance.CheckedBackColor.IsEmpty || state == CheckState.Unchecked, check: false, Control.FlatAppearance.BorderSize).Layout();
		if (!Control.FlatAppearance.BorderColor.IsEmpty)
		{
			colorData.WindowFrame = Control.FlatAppearance.BorderColor;
		}
		Rectangle clientRectangle = Control.ClientRectangle;
		Color color;
		if (!Control.FlatAppearance.MouseOverBackColor.IsEmpty)
		{
			color = Control.FlatAppearance.MouseOverBackColor;
		}
		else
		{
			Color color2;
			if (!Control.FlatAppearance.CheckedBackColor.IsEmpty)
			{
				bool flag2 = (uint)(state - 1) <= 1u;
				color2 = (flag2 ? Control.FlatAppearance.CheckedBackColor.MixColor(colorData.LowButtonFace) : colorData.LowButtonFace);
			}
			else
			{
				color2 = ((state == CheckState.Indeterminate) ? colorData.ButtonFace.MixColor(colorData.LowButtonFace) : colorData.LowButtonFace);
			}
			color = color2;
		}
		Color color3 = color;
		PaintBackground(e, clientRectangle, IsHighContrastHighlighted() ? SystemColors.Highlight : color3);
		if (Control.IsDefault)
		{
			clientRectangle.Inflate(-1, -1);
		}
		PaintImage(e, layoutData);
		PaintField(e, layoutData, colorData, IsHighContrastHighlighted() ? SystemColors.HighlightText : colorData.WindowText, drawFocus: false);
		if (Control.Focused && Control.ShowFocusCues)
		{
			ButtonBaseAdapter.DrawFlatFocus(e, layoutData.Focus, colorData.ContrastButtonShadow);
		}
		if (!Control.IsDefault || !Control.Focused || Control.FlatAppearance.BorderSize != 0)
		{
			ButtonBaseAdapter.DrawDefaultBorder(e, clientRectangle, colorData.WindowFrame, Control.IsDefault);
		}
		if (flag)
		{
			if (Control.FlatAppearance.BorderSize != 1)
			{
				ButtonBaseAdapter.DrawFlatBorderWithSize(e, clientRectangle, colorData.WindowFrame, Control.FlatAppearance.BorderSize);
			}
			else
			{
				ControlPaint.DrawBorderSimple(e, clientRectangle, colorData.WindowFrame);
			}
		}
		else if (state == CheckState.Unchecked)
		{
			ControlPaint.DrawBorderSimple(e, clientRectangle, colorData.WindowFrame);
		}
		else
		{
			ButtonBaseAdapter.Draw3DLiteBorder(e, clientRectangle, colorData, up: false);
		}
	}

	protected override LayoutOptions Layout(PaintEventArgs e)
	{
		return PaintFlatLayout(up: false, check: true, Control.FlatAppearance.BorderSize);
	}

	internal static LayoutOptions PaintFlatLayout(bool up, bool check, int borderSize, Rectangle clientRectangle, Padding padding, bool isDefault, Font font, string text, bool enabled, ContentAlignment textAlign, RightToLeft rtl)
	{
		LayoutOptions layoutOptions = ButtonBaseAdapter.CommonLayout(clientRectangle, padding, isDefault, font, text, enabled, textAlign, rtl);
		layoutOptions.BorderSize = borderSize + (check ? 1 : 0);
		layoutOptions.PaddingSize = (check ? 1 : 2);
		layoutOptions.FocusOddEvenFixup = false;
		layoutOptions.TextOffset = !up;
		layoutOptions.ShadowedText = SystemInformation.HighContrast;
		return layoutOptions;
	}

	private LayoutOptions PaintFlatLayout(bool up, bool check, int borderSize)
	{
		LayoutOptions layoutOptions = CommonLayout();
		layoutOptions.BorderSize = borderSize + (check ? 1 : 0);
		layoutOptions.PaddingSize = (check ? 1 : 2);
		layoutOptions.FocusOddEvenFixup = false;
		layoutOptions.TextOffset = !up;
		layoutOptions.ShadowedText = SystemInformation.HighContrast;
		return layoutOptions;
	}
}
