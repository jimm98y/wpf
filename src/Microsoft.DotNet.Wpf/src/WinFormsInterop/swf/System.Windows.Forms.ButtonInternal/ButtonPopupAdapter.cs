// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// From .NET's System.Windows.Forms (dotnet/winforms), adapted to the port where noted.

using System.Drawing;

namespace System.Windows.Forms.ButtonInternal;

internal class ButtonPopupAdapter : ButtonBaseAdapter
{
	internal ButtonPopupAdapter(ButtonBase control)
		: base(control)
	{
	}

	internal override void PaintUp(PaintEventArgs e, CheckState state)
	{
		ColorData colorData = PaintPopupRender(e).Calculate();
		LayoutData layout = PaintPopupLayout(state == CheckState.Unchecked, 1).Layout();
		Rectangle clientRectangle = Control.ClientRectangle;
		if (state == CheckState.Indeterminate)
		{
			using Brush background = ButtonBaseAdapter.CreateDitherBrush(colorData.Highlight, colorData.ButtonFace);
			PaintButtonBackground(e, clientRectangle, background);
		}
		else
		{
			Control.PaintBackground(e, clientRectangle, IsHighContrastHighlighted() ? SystemColors.Highlight : Control.BackColor, clientRectangle.Location);
		}
		if (Control.IsDefault)
		{
			clientRectangle.Inflate(-1, -1);
		}
		PaintImage(e, layout);
		PaintField(e, layout, colorData, (state != CheckState.Indeterminate && IsHighContrastHighlighted()) ? SystemColors.HighlightText : colorData.WindowText, drawFocus: true);
		Color color = (colorData.Options.HighContrast ? colorData.WindowText : ButtonBaseAdapter.GetContrastingBorderColor(colorData.ButtonShadow));
		ButtonBaseAdapter.DrawDefaultBorder(e, clientRectangle, color, Control.IsDefault);
		if (state == CheckState.Unchecked)
		{
			ControlPaint.DrawBorderSimple(e, clientRectangle, color);
		}
		else
		{
			ButtonBaseAdapter.Draw3DLiteBorder(e, clientRectangle, colorData, up: false);
		}
	}

	internal override void PaintOver(PaintEventArgs e, CheckState state)
	{
		ColorData colorData = PaintPopupRender(e).Calculate();
		LayoutData layout = PaintPopupLayout(state == CheckState.Unchecked, (!SystemInformation.HighContrast) ? 1 : 2).Layout();
		Rectangle clientRectangle = Control.ClientRectangle;
		if (state == CheckState.Indeterminate)
		{
			using Brush background = ButtonBaseAdapter.CreateDitherBrush(colorData.Highlight, colorData.ButtonFace);
			PaintButtonBackground(e, clientRectangle, background);
		}
		else
		{
			Control.PaintBackground(e, clientRectangle, IsHighContrastHighlighted() ? SystemColors.Highlight : Control.BackColor, clientRectangle.Location);
		}
		if (Control.IsDefault)
		{
			clientRectangle.Inflate(-1, -1);
		}
		PaintImage(e, layout);
		PaintField(e, layout, colorData, IsHighContrastHighlighted() ? SystemColors.HighlightText : colorData.WindowText, drawFocus: true);
		ButtonBaseAdapter.DrawDefaultBorder(e, clientRectangle, colorData.Options.HighContrast ? colorData.WindowText : colorData.ButtonShadow, Control.IsDefault);
		if (SystemInformation.HighContrast)
		{
			Graphics graphicsInternal = e.GraphicsInternal;
			PenScope scope = colorData.WindowFrame.GetCachedPenScope();
			try
			{
				PenScope scope2 = colorData.Highlight.GetCachedPenScope();
				try
				{
					PenScope scope3 = colorData.ButtonShadow.GetCachedPenScope();
					try
					{
						graphicsInternal.DrawLine(scope, clientRectangle.Left + 1, clientRectangle.Top + 1, clientRectangle.Right - 2, clientRectangle.Top + 1);
						graphicsInternal.DrawLine(scope, clientRectangle.Left + 1, clientRectangle.Top + 1, clientRectangle.Left + 1, clientRectangle.Bottom - 2);
						graphicsInternal.DrawLine(scope, clientRectangle.Left, clientRectangle.Bottom - 1, clientRectangle.Right, clientRectangle.Bottom - 1);
						graphicsInternal.DrawLine(scope, clientRectangle.Right - 1, clientRectangle.Top, clientRectangle.Right - 1, clientRectangle.Bottom);
						graphicsInternal.DrawLine(scope2, clientRectangle.Left, clientRectangle.Top, clientRectangle.Right, clientRectangle.Top);
						graphicsInternal.DrawLine(scope2, clientRectangle.Left, clientRectangle.Top, clientRectangle.Left, clientRectangle.Bottom);
						graphicsInternal.DrawLine(scope3, clientRectangle.Left + 1, clientRectangle.Bottom - 2, clientRectangle.Right - 2, clientRectangle.Bottom - 2);
						graphicsInternal.DrawLine(scope3, clientRectangle.Right - 2, clientRectangle.Top + 1, clientRectangle.Right - 2, clientRectangle.Bottom - 2);
						clientRectangle.Inflate(-2, -2);
						return;
					}
					finally
					{
						scope3.Dispose();
					}
				}
				finally
				{
					scope2.Dispose();
				}
			}
			finally
			{
				scope.Dispose();
			}
		}
		ButtonBaseAdapter.Draw3DLiteBorder(e, clientRectangle, colorData, up: true);
	}

	internal override void PaintDown(PaintEventArgs e, CheckState state)
	{
		ColorData colorData = PaintPopupRender(e).Calculate();
		LayoutData layout = PaintPopupLayout(up: false, (!SystemInformation.HighContrast) ? 1 : 2).Layout();
		Rectangle clientRectangle = Control.ClientRectangle;
		PaintButtonBackground(e, clientRectangle, null);
		if (Control.IsDefault)
		{
			clientRectangle.Inflate(-1, -1);
		}
		clientRectangle.Inflate(-1, -1);
		PaintImage(e, layout);
		PaintField(e, layout, colorData, colorData.WindowText, drawFocus: true);
		clientRectangle.Inflate(1, 1);
		ButtonBaseAdapter.DrawDefaultBorder(e, clientRectangle, colorData.Options.HighContrast ? colorData.WindowText : colorData.WindowFrame, Control.IsDefault);
		ControlPaint.DrawBorderSimple(e, clientRectangle, colorData.Options.HighContrast ? colorData.WindowText : ButtonBaseAdapter.GetContrastingBorderColor(colorData.ButtonShadow));
	}

	protected override LayoutOptions Layout(PaintEventArgs e)
	{
		return PaintPopupLayout(up: false, 0);
	}

	internal static LayoutOptions PaintPopupLayout(bool up, int paintedBorder, Rectangle clientRectangle, Padding padding, bool isDefault, Font font, string text, bool enabled, ContentAlignment textAlign, RightToLeft rtl)
	{
		LayoutOptions layoutOptions = ButtonBaseAdapter.CommonLayout(clientRectangle, padding, isDefault, font, text, enabled, textAlign, rtl);
		layoutOptions.BorderSize = paintedBorder;
		layoutOptions.PaddingSize = 2 - paintedBorder;
		layoutOptions.HintTextUp = false;
		layoutOptions.TextOffset = !up;
		layoutOptions.ShadowedText = SystemInformation.HighContrast;
		return layoutOptions;
	}

	private LayoutOptions PaintPopupLayout(bool up, int paintedBorder)
	{
		LayoutOptions layoutOptions = CommonLayout();
		layoutOptions.BorderSize = paintedBorder;
		layoutOptions.PaddingSize = 2 - paintedBorder;
		layoutOptions.HintTextUp = false;
		layoutOptions.TextOffset = !up;
		layoutOptions.ShadowedText = SystemInformation.HighContrast;
		return layoutOptions;
	}
}
