// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// From .NET's System.Windows.Forms (dotnet/winforms), adapted to the port where noted.

using System.Drawing;

namespace System.Windows.Forms.ButtonInternal;

internal abstract class RadioButtonBaseAdapter : CheckableControlBaseAdapter
{
	protected new RadioButton Control => (RadioButton)base.Control;

	internal RadioButtonBaseAdapter(ButtonBase control)
		: base(control)
	{
	}

	protected void DrawCheckFlat(PaintEventArgs e, LayoutData layout, Color checkColor, Color checkBackground, Color checkBorder)
	{
		DrawCheckBackgroundFlat(e, layout.CheckBounds, checkBorder, checkBackground);
		DrawCheckOnly(e, layout, checkColor, disabledColors: true);
	}

	protected void DrawCheckBackground3DLite(PaintEventArgs e, Rectangle bounds, Color checkBackground, ColorData colors, bool disabledColors)
	{
		Graphics graphicsInternal = e.GraphicsInternal;
		Color color = checkBackground;
		if (!Control.Enabled & disabledColors)
		{
			color = SystemColors.Control;
		}
		SolidBrushScope scope = color.GetCachedSolidBrushScope();
		try
		{
			PenScope scope2 = colors.ButtonShadow.GetCachedPenScope();
			try
			{
				PenScope scope3 = colors.ButtonFace.GetCachedPenScope();
				try
				{
					PenScope scope4 = colors.Highlight.GetCachedPenScope();
					try
					{
						bounds.Width--;
						bounds.Height--;
						graphicsInternal.DrawPie(scope2, bounds, 136f, 88f);
						graphicsInternal.DrawPie(scope2, bounds, 226f, 88f);
						graphicsInternal.DrawPie(scope4, bounds, 316f, 88f);
						graphicsInternal.DrawPie(scope4, bounds, 46f, 88f);
						bounds.Inflate(-1, -1);
						graphicsInternal.FillEllipse((SolidBrush)scope, bounds);
						graphicsInternal.DrawEllipse(scope3, bounds);
					}
					finally
					{
						scope4.Dispose();
					}
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

	protected void DrawCheckBackgroundFlat(PaintEventArgs e, Rectangle bounds, Color borderColor, Color checkBackground)
	{
		Color color = checkBackground;
		Color color2 = borderColor;
		if (!Control.Enabled)
		{
			if (!SystemInformation.HighContrast)
			{
				color2 = ControlPaint.ContrastControlDark;
			}
			color = SystemColors.Control;
		}
		double dpiScaleRatio = GetDpiScaleRatio();
		using DeviceContextHdcScope scope = new DeviceContextHdcScope(e);
		using CreatePenScope scope2 = new CreatePenScope(color2);
		using CreateBrushScope scope3 = new CreateBrushScope(color);
		if (dpiScaleRatio > 1.1)
		{
			bounds.Width--;
			bounds.Height--;
			scope.DrawAndFillEllipse(scope2, scope3, bounds);
			bounds.Inflate(-1, -1);
		}
		else
		{
			DrawAndFillEllipse(scope, scope2, scope3, bounds);
		}
	}

	private static void DrawAndFillEllipse(HDC hdc, HPEN borderPen, HBRUSH fieldBrush, Rectangle bounds)
	{
		if (!hdc.IsNull)
		{
			hdc.FillRectangle(fieldBrush, new Rectangle(bounds.X + 2, bounds.Y + 2, 8, 8));
			hdc.FillRectangle(fieldBrush, new Rectangle(bounds.X + 4, bounds.Y + 1, 4, 10));
			hdc.FillRectangle(fieldBrush, new Rectangle(bounds.X + 1, bounds.Y + 4, 10, 4));
			hdc.DrawLine(borderPen, new Point(bounds.X + 4, bounds.Y), new Point(bounds.X + 8, bounds.Y));
			hdc.DrawLine(borderPen, new Point(bounds.X + 4, bounds.Y + 11), new Point(bounds.X + 8, bounds.Y + 11));
			hdc.DrawLine(borderPen, new Point(bounds.X + 2, bounds.Y + 1), new Point(bounds.X + 4, bounds.Y + 1));
			hdc.DrawLine(borderPen, new Point(bounds.X + 8, bounds.Y + 1), new Point(bounds.X + 10, bounds.Y + 1));
			hdc.DrawLine(borderPen, new Point(bounds.X + 2, bounds.Y + 10), new Point(bounds.X + 4, bounds.Y + 10));
			hdc.DrawLine(borderPen, new Point(bounds.X + 8, bounds.Y + 10), new Point(bounds.X + 10, bounds.Y + 10));
			hdc.DrawLine(borderPen, new Point(bounds.X, bounds.Y + 4), new Point(bounds.X, bounds.Y + 8));
			hdc.DrawLine(borderPen, new Point(bounds.X + 11, bounds.Y + 4), new Point(bounds.X + 11, bounds.Y + 8));
			hdc.DrawLine(borderPen, new Point(bounds.X + 1, bounds.Y + 2), new Point(bounds.X + 1, bounds.Y + 4));
			hdc.DrawLine(borderPen, new Point(bounds.X + 1, bounds.Y + 8), new Point(bounds.X + 1, bounds.Y + 10));
			hdc.DrawLine(borderPen, new Point(bounds.X + 10, bounds.Y + 2), new Point(bounds.X + 10, bounds.Y + 4));
			hdc.DrawLine(borderPen, new Point(bounds.X + 10, bounds.Y + 8), new Point(bounds.X + 10, bounds.Y + 10));
		}
	}

	private static int GetScaledNumber(int n, double scale)
	{
		return (int)((double)n * scale);
	}

	protected void DrawCheckOnly(PaintEventArgs e, LayoutData layout, Color checkColor, bool disabledColors)
	{
		if (!Control.Checked)
		{
			return;
		}
		if (!Control.Enabled & disabledColors)
		{
			checkColor = SystemColors.ControlDark;
		}
		double dpiScaleRatio = GetDpiScaleRatio();
		using DeviceContextHdcScope hdc = new DeviceContextHdcScope(e);
		using CreateBrushScope scope = new CreateBrushScope(checkColor);
		int num = 5;
		Rectangle rectangle = new Rectangle(layout.CheckBounds.X + GetScaledNumber(num, dpiScaleRatio), layout.CheckBounds.Y + GetScaledNumber(num - 1, dpiScaleRatio), GetScaledNumber(2, dpiScaleRatio), GetScaledNumber(4, dpiScaleRatio));
		hdc.FillRectangle(rectangle, scope);
		Rectangle rectangle2 = new Rectangle(layout.CheckBounds.X + GetScaledNumber(num - 1, dpiScaleRatio), layout.CheckBounds.Y + GetScaledNumber(num, dpiScaleRatio), GetScaledNumber(4, dpiScaleRatio), GetScaledNumber(2, dpiScaleRatio));
		hdc.FillRectangle(rectangle2, scope);
	}

	protected ButtonState GetState()
	{
		ButtonState buttonState = ButtonState.Normal;
		buttonState = ((!Control.Checked) ? (buttonState | ButtonState.Normal) : (buttonState | ButtonState.Checked));
		if (!Control.Enabled)
		{
			buttonState |= ButtonState.Inactive;
		}
		if (Control.MouseIsDown)
		{
			buttonState |= ButtonState.Pushed;
		}
		return buttonState;
	}

	protected void DrawCheckBox(PaintEventArgs e, LayoutData layout)
	{
		Rectangle checkBounds = layout.CheckBounds;
		if (!Application.RenderWithVisualStyles)
		{
			checkBounds.X--;
		}
		ButtonState state = GetState();
		if (Application.RenderWithVisualStyles)
		{
			{
				ButtonAdapterRenderers.DrawRadioButton(e, new Point(checkBounds.Left, checkBounds.Top), ButtonAdapterRenderers.RadioButtonStateFrom(state, Control.MouseIsOver));
				return;
			}
		}
		ControlPaint.DrawRadioButton(e.GraphicsInternal, checkBounds, state);
	}

	protected void AdjustFocusRectangle(LayoutData layout)
	{
		if (string.IsNullOrEmpty(Control.Text))
		{
			layout.Focus = (Control.AutoSize ? layout.CheckBounds : layout.Field);
		}
	}

	internal override LayoutOptions CommonLayout()
	{
		LayoutOptions layoutOptions = base.CommonLayout();
		layoutOptions.CheckAlign = Control.CheckAlign;
		return layoutOptions;
	}
}
