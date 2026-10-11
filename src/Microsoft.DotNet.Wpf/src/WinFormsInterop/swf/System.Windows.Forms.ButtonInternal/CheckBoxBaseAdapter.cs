// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// From .NET's System.Windows.Forms (dotnet/winforms), adapted to the port where noted.

using System.Drawing;

namespace System.Windows.Forms.ButtonInternal;

internal abstract class CheckBoxBaseAdapter : CheckableControlBaseAdapter
{
	protected const int FlatCheckSize = 11;

	[ThreadStatic]
	private static Bitmap? t_checkImageChecked;

	[ThreadStatic]
	private static Color t_checkImageCheckedBackColor;

	[ThreadStatic]
	private static Bitmap? t_checkImageIndeterminate;

	[ThreadStatic]
	private static Color t_checkImageIndeterminateBackColor;

	protected new CheckBox Control => (CheckBox)base.Control;

	internal CheckBoxBaseAdapter(ButtonBase control)
		: base(control)
	{
	}

	protected void DrawCheckFlat(PaintEventArgs e, LayoutData layout, Color checkColor, Color checkBackground, Color checkBorder, ColorData colors)
	{
		Rectangle checkBounds = layout.CheckBounds;
		if (!layout.Options.DotNetOneButtonCompat)
		{
			checkBounds.Width--;
			checkBounds.Height--;
		}
		using (DeviceContextHdcScope hdc = new DeviceContextHdcScope(e))
		{
			using CreatePenScope scope = new CreatePenScope(checkBorder);
			hdc.DrawRectangle(checkBounds, scope);
			if (layout.Options.DotNetOneButtonCompat)
			{
				checkBounds.Width--;
				checkBounds.Height--;
			}
			checkBounds.Inflate(-1, -1);
		}
		if (Control.CheckState == CheckState.Indeterminate)
		{
			checkBounds.Width++;
			checkBounds.Height++;
			ButtonBaseAdapter.DrawDitheredFill(e.Graphics, colors.ButtonFace, checkBackground, checkBounds);
		}
		else
		{
			using DeviceContextHdcScope hdc2 = new DeviceContextHdcScope(e);
			using CreateBrushScope scope2 = new CreateBrushScope(checkBackground);
			checkBounds.Width++;
			checkBounds.Height++;
			hdc2.FillRectangle(checkBounds, scope2);
		}
		DrawCheckOnly(e, layout, colors, checkColor);
	}

	internal static void DrawCheckBackground(bool controlEnabled, CheckState controlCheckState, IDeviceContext deviceContext, Rectangle bounds, Color checkBackground, bool disabledColors)
	{
		using DeviceContextHdcScope scope = deviceContext.ToHdcScope();
		Color color;
		if (!controlEnabled & disabledColors)
		{
			color = SystemColors.Control;
		}
		else if ((controlCheckState == CheckState.Indeterminate && checkBackground == SystemColors.Window) & disabledColors)
		{
			Color color2 = (SystemInformation.HighContrast ? SystemColors.ControlDark : SystemColors.Control);
			color = Color.FromArgb((byte)((color2.R + SystemColors.Window.R) / 2), (byte)((color2.G + SystemColors.Window.G) / 2), (byte)((color2.B + SystemColors.Window.B) / 2));
		}
		else
		{
			color = checkBackground;
		}
		using CreateBrushScope scope2 = new CreateBrushScope(color);
		scope.FillRectangle(bounds, scope2);
	}

	protected void DrawCheckBackground(PaintEventArgs e, Rectangle bounds, Color checkBackground, bool disabledColors, ColorData colors)
	{
		if (Control.CheckState == CheckState.Indeterminate)
		{
			ButtonBaseAdapter.DrawDitheredFill(e.GraphicsInternal, colors.ButtonFace, checkBackground, bounds);
		}
		else
		{
			DrawCheckBackground(Control.Enabled, Control.CheckState, e, bounds, checkBackground, disabledColors);
		}
	}

	protected void DrawCheckOnly(PaintEventArgs e, LayoutData layout, ColorData colors, Color checkColor)
	{
		DrawCheckOnly(11, Control.Checked, Control.Enabled, Control.CheckState, e.GraphicsInternal, layout, colors, checkColor);
	}

	internal static void DrawCheckOnly(int checkSize, bool controlChecked, bool controlEnabled, CheckState controlCheckState, Graphics g, LayoutData layout, ColorData colors, Color checkColor)
	{
		if (controlChecked)
		{
			if (!controlEnabled)
			{
				checkColor = colors.ButtonShadow;
			}
			else if (controlCheckState == CheckState.Indeterminate)
			{
				checkColor = (SystemInformation.HighContrast ? colors.Highlight : colors.ButtonShadow);
			}
			Rectangle checkBounds = layout.CheckBounds;
			if (checkBounds.Width == checkSize)
			{
				checkBounds.Width++;
				checkBounds.Height++;
			}
			checkBounds.Width++;
			checkBounds.Height++;
			// .NET renders DrawFrameControl(DFC_MENU, DFCS_MENUCHECK) into a bitmap the size of
			// checkBounds and draws it recoloured there; the glyph is drawn there directly instead.
			checkBounds.Y -= (layout.Options.DotNetOneButtonCompat ? 1 : 2);
			ButtonAdapterRenderers.DrawMenuCheck(g, checkBounds, checkColor);
		}
	}

	internal static Rectangle DrawPopupBorder(Graphics g, Rectangle r, ColorData colors)
	{
		using DeviceContextHdcScope scope = new DeviceContextHdcScope(g);
		return DrawPopupBorder(scope, r, colors);
	}

	internal static Rectangle DrawPopupBorder(PaintEventArgs e, Rectangle r, ColorData colors)
	{
		using DeviceContextHdcScope scope = new DeviceContextHdcScope(e);
		return DrawPopupBorder(scope, r, colors);
	}

	internal static Rectangle DrawPopupBorder(HDC hdc, Rectangle r, ColorData colors)
	{
		using CreatePenScope scope = new CreatePenScope(colors.Highlight);
		using CreatePenScope scope2 = new CreatePenScope(colors.ButtonShadow);
		using CreatePenScope scope3 = new CreatePenScope(colors.ButtonFace);
		hdc.DrawLine(scope, r.Right - 1, r.Top, r.Right - 1, r.Bottom);
		hdc.DrawLine(scope, r.Left, r.Bottom - 1, r.Right, r.Bottom - 1);
		hdc.DrawLine(scope2, r.Left, r.Top, r.Left, r.Bottom);
		hdc.DrawLine(scope2, r.Left, r.Top, r.Right - 1, r.Top);
		hdc.DrawLine(scope3, r.Right - 2, r.Top + 1, r.Right - 2, r.Bottom - 1);
		hdc.DrawLine(scope3, r.Left + 1, r.Bottom - 2, r.Right - 1, r.Bottom - 2);
		r.Inflate(-1, -1);
		return r;
	}

	protected ButtonState GetState()
	{
		ButtonState buttonState = ButtonState.Normal;
		buttonState = ((Control.CheckState != CheckState.Unchecked) ? (buttonState | ButtonState.Checked) : (buttonState | ButtonState.Normal));
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
		ButtonState state = GetState();
		if (Control.CheckState == CheckState.Indeterminate)
		{
			if (Application.RenderWithVisualStyles)
			{
				ButtonAdapterRenderers.DrawCheckBox(e, new Point(layout.CheckBounds.Left, layout.CheckBounds.Top), ButtonAdapterRenderers.CheckBoxStateFrom(state, isMixed: true, Control.MouseIsOver));
			}
			else
			{
				ControlPaint.DrawMixedCheckBox(e.GraphicsInternal, layout.CheckBounds, state);
			}
		}
		else if (Application.RenderWithVisualStyles)
		{
			ButtonAdapterRenderers.DrawCheckBox(e, new Point(layout.CheckBounds.Left, layout.CheckBounds.Top), ButtonAdapterRenderers.CheckBoxStateFrom(state, isMixed: false, Control.MouseIsOver));
		}
		else
		{
			ControlPaint.DrawCheckBox(e.GraphicsInternal, layout.CheckBounds, state);
		}
	}


	protected void AdjustFocusRectangle(LayoutData layout)
	{
		if (string.IsNullOrEmpty(Control.Text))
		{
			layout.Focus = (Control.AutoSize ? Rectangle.Inflate(layout.CheckBounds, -2, -2) : layout.Field);
		}
	}

	internal override LayoutOptions CommonLayout()
	{
		LayoutOptions layoutOptions = base.CommonLayout();
		layoutOptions.CheckAlign = Control.CheckAlign;
		layoutOptions.TextOffset = false;
		layoutOptions.ShadowedText = !Control.Enabled;
		layoutOptions.LayoutRTL = Control.RightToLeft == RightToLeft.Yes;
		return layoutOptions;
	}
}
