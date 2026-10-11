// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// From .NET's System.Windows.Forms (dotnet/winforms), adapted to the port where noted.

using System.Drawing;

namespace System.Windows.Forms.ButtonInternal;

internal class CheckBoxPopupAdapter : CheckBoxBaseAdapter
{
	internal CheckBoxPopupAdapter(ButtonBase control)
		: base(control)
	{
	}

	internal override void PaintUp(PaintEventArgs e, CheckState state)
	{
		if (Control.Appearance == Appearance.Button)
		{
			new ButtonPopupAdapter(Control).PaintUp(e, Control.CheckState);
			return;
		}
		ColorData colorData = PaintPopupRender(e).Calculate();
		LayoutData layoutData = PaintPopupLayout(show3D: false).Layout();
		PaintButtonBackground(e, Control.ClientRectangle, null);
		PaintImage(e, layoutData);
		DrawCheckBackground(e, layoutData.CheckBounds, colorData.Options.HighContrast ? colorData.ButtonFace : colorData.Highlight, disabledColors: true, colorData);
		ControlPaint.DrawBorderSimple(e, layoutData.CheckBounds, (colorData.Options.HighContrast && !Control.Enabled) ? colorData.WindowFrame : colorData.ButtonShadow);
		DrawCheckOnly(e, layoutData, colorData, colorData.WindowText);
		AdjustFocusRectangle(layoutData);
		PaintField(e, layoutData, colorData, colorData.WindowText, drawFocus: true);
	}

	internal override void PaintOver(PaintEventArgs e, CheckState state)
	{
		if (Control.Appearance == Appearance.Button)
		{
			new ButtonPopupAdapter(Control).PaintOver(e, Control.CheckState);
			return;
		}
		ColorData colorData = PaintPopupRender(e).Calculate();
		LayoutData layoutData = PaintPopupLayout(show3D: true).Layout();
		Control.PaintBackground(e, Control.ClientRectangle);
		PaintImage(e, layoutData);
		DrawCheckBackground(e, layoutData.CheckBounds, colorData.Options.HighContrast ? colorData.ButtonFace : colorData.Highlight, disabledColors: true, colorData);
		CheckBoxBaseAdapter.DrawPopupBorder(e, layoutData.CheckBounds, colorData);
		DrawCheckOnly(e, layoutData, colorData, colorData.WindowText);
		Region region = null;
		if (!string.IsNullOrEmpty(Control.Text))
		{
			region = e.GraphicsInternal.Clip;
			e.GraphicsInternal.ExcludeClip(layoutData.CheckArea);
		}
		AdjustFocusRectangle(layoutData);
		PaintField(e, layoutData, colorData, colorData.WindowText, drawFocus: true);
		if (region != null)
		{
			e.GraphicsInternal.Clip = region;
		}
	}

	internal override void PaintDown(PaintEventArgs e, CheckState state)
	{
		if (Control.Appearance == Appearance.Button)
		{
			new ButtonPopupAdapter(Control).PaintDown(e, Control.CheckState);
			return;
		}
		ColorData colorData = PaintPopupRender(e).Calculate();
		LayoutData layoutData = PaintPopupLayout(show3D: true).Layout();
		PaintButtonBackground(e, Control.ClientRectangle, null);
		PaintImage(e, layoutData);
		DrawCheckBackground(e, layoutData.CheckBounds, colorData.ButtonFace, disabledColors: true, colorData);
		CheckBoxBaseAdapter.DrawPopupBorder(e, layoutData.CheckBounds, colorData);
		DrawCheckOnly(e, layoutData, colorData, colorData.WindowText);
		AdjustFocusRectangle(layoutData);
		PaintField(e, layoutData, colorData, colorData.WindowText, drawFocus: true);
	}

	protected override ButtonBaseAdapter CreateButtonAdapter()
	{
		return new ButtonPopupAdapter(Control);
	}

	protected override LayoutOptions Layout(PaintEventArgs e)
	{
		return PaintPopupLayout(show3D: true);
	}

	internal static LayoutOptions PaintPopupLayout(bool show3D, int checkSize, Rectangle clientRectangle, Padding padding, bool isDefault, Font font, string text, bool enabled, ContentAlignment textAlign, RightToLeft rtl, Control? control = null)
	{
		LayoutOptions layoutOptions = ButtonBaseAdapter.CommonLayout(clientRectangle, padding, isDefault, font, text, enabled, textAlign, rtl);
		layoutOptions.ShadowedText = false;
		checkSize = (int)((double)checkSize * CheckableControlBaseAdapter.GetDpiScaleRatio(control));
		if (show3D)
		{
			layoutOptions.CheckSize = checkSize + 1;
		}
		else
		{
			layoutOptions.CheckSize = checkSize;
			layoutOptions.CheckPaddingSize = 1;
		}
		return layoutOptions;
	}

	private LayoutOptions PaintPopupLayout(bool show3D)
	{
		LayoutOptions layoutOptions = CommonLayout();
		layoutOptions.ShadowedText = false;
		int num = (int)(11.0 * GetDpiScaleRatio());
		if (show3D)
		{
			layoutOptions.CheckSize = num + 1;
		}
		else
		{
			layoutOptions.CheckSize = num;
			layoutOptions.CheckPaddingSize = 1;
		}
		return layoutOptions;
	}
}
