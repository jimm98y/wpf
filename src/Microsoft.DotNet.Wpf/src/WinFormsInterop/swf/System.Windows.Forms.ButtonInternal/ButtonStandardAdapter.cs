// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// From .NET's System.Windows.Forms (dotnet/winforms), adapted to the port where noted.

using System.Drawing;
using System.Windows.Forms.VisualStyles;

namespace System.Windows.Forms.ButtonInternal;

internal class ButtonStandardAdapter : ButtonBaseAdapter
{
	private const int BorderWidth = 2;

	internal ButtonStandardAdapter(ButtonBase control)
		: base(control)
	{
	}

	private PushButtonState DetermineState(bool up)
	{
		PushButtonState result = PushButtonState.Normal;
		if (!up)
		{
			result = PushButtonState.Pressed;
		}
		else if (Control.MouseIsOver)
		{
			result = PushButtonState.Hot;
		}
		else if (!Control.Enabled)
		{
			result = PushButtonState.Disabled;
		}
		else if (Control.Focused || Control.IsDefault)
		{
			result = PushButtonState.Default;
		}
		return result;
	}

	internal override void PaintUp(PaintEventArgs e, CheckState state)
	{
		PaintWorker(e, up: true, state);
	}

	internal override void PaintDown(PaintEventArgs e, CheckState state)
	{
		PaintWorker(e, up: false, state);
	}

	internal override void PaintOver(PaintEventArgs e, CheckState state)
	{
		PaintUp(e, state);
	}

	private void PaintThemedButtonBackground(PaintEventArgs e, Rectangle bounds, bool up)
	{
		PushButtonState state = DetermineState(up);
		if (ButtonRenderer.IsBackgroundPartiallyTransparent(state))
		{
			ButtonRenderer.DrawParentBackground(e.Graphics, bounds, Control);
		}
		ButtonAdapterRenderers.DrawButtonForHandle(e, Control.ClientRectangle, focused: false, state);
		bounds.Inflate(-4, -4);
		if (!Control.UseVisualStyleBackColor)
		{
			bool flag = up && IsHighContrastHighlighted();
			Color color = (flag ? SystemColors.Highlight : Control.BackColor);
			if (color.HasTransparency())
			{
				SolidBrushScope scope = color.GetCachedSolidBrushScope();
				try
				{
					e.GraphicsInternal.FillRectangle((SolidBrush)scope, bounds);
				}
				finally
				{
					scope.Dispose();
				}
			}
			else
			{
				using DeviceContextHdcScope hdc = new DeviceContextHdcScope(e);
				hdc.FillRectangle(bounds, new HBRUSH(flag ? SystemColors.Highlight : Control.BackColor));
			}
		}
		if (Control.BackgroundImage != null && !DisplayInformation.HighContrast)
		{
			ControlPaint.DrawBackgroundImage(e.GraphicsInternal, Control.BackgroundImage, Color.Transparent, Control.BackgroundImageLayout, Control.ClientRectangle, bounds, Control.DisplayRectangle.Location, Control.RightToLeft);
		}
	}

	private void PaintWorker(PaintEventArgs e, bool up, CheckState state)
	{
		up = up && state == CheckState.Unchecked;
		ColorData colorData = PaintRender(e).Calculate();
		LayoutData layoutData = ((!Application.RenderWithVisualStyles) ? PaintLayout(up).Layout() : PaintLayout(up: true).Layout());
		_ = Control;
		if (Application.RenderWithVisualStyles)
		{
			PaintThemedButtonBackground(e, Control.ClientRectangle, up);
		}
		else
		{
			Brush brush = null;
			if (state == CheckState.Indeterminate)
			{
				brush = ButtonBaseAdapter.CreateDitherBrush(colorData.Highlight, colorData.ButtonFace);
			}
			try
			{
				Rectangle clientRectangle = Control.ClientRectangle;
				if (up)
				{
					clientRectangle.Inflate(-2, -2);
				}
				else
				{
					clientRectangle.Inflate(-1, -1);
				}
				PaintButtonBackground(e, clientRectangle, brush);
			}
			finally
			{
				brush?.Dispose();
			}
		}
		PaintImage(e, layoutData);
		if (Application.RenderWithVisualStyles && Control.FlatStyle != FlatStyle.Standard)
		{
			layoutData.Focus.Inflate(1, 1);
		}
		if (up & IsHighContrastHighlighted())
		{
			Color highlightText = SystemColors.HighlightText;
			PaintField(e, layoutData, colorData, highlightText, drawFocus: false);
			if (Control.Focused && Control.ShowFocusCues)
			{
				ControlPaint.DrawHighContrastFocusRectangle(e.GraphicsInternal, layoutData.Focus, highlightText);
			}
		}
		else if (up & IsHighContrastHighlighted())
		{
			PaintField(e, layoutData, colorData, SystemColors.HighlightText, drawFocus: true);
		}
		else
		{
			PaintField(e, layoutData, colorData, colorData.WindowText, drawFocus: true);
		}
		if (!Application.RenderWithVisualStyles)
		{
			Rectangle clientRectangle2 = Control.ClientRectangle;
			if (Control.IsDefault)
			{
				clientRectangle2.Inflate(-1, -1);
			}
			ButtonBaseAdapter.DrawDefaultBorder(e, clientRectangle2, colorData.WindowFrame, Control.IsDefault);
			if (up)
			{
				Draw3DBorder(e, clientRectangle2, colorData, up);
			}
			else
			{
				ControlPaint.DrawBorderSimple(e, clientRectangle2, colorData.ButtonShadow);
			}
		}
	}

	protected override LayoutOptions Layout(PaintEventArgs e)
	{
		return PaintLayout(up: false);
	}

	private LayoutOptions PaintLayout(bool up)
	{
		LayoutOptions layoutOptions = CommonLayout();
		layoutOptions.TextOffset = !up;
		layoutOptions.DotNetOneButtonCompat = !Application.RenderWithVisualStyles;
		return layoutOptions;
	}
}
