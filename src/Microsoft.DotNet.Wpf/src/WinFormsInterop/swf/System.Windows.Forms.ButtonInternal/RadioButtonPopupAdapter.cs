// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// From .NET's System.Windows.Forms (dotnet/winforms), adapted to the port where noted.

using System.Drawing;

namespace System.Windows.Forms.ButtonInternal;

internal class RadioButtonPopupAdapter : RadioButtonFlatAdapter
{
	internal RadioButtonPopupAdapter(ButtonBase control)
		: base(control)
	{
	}

	internal override void PaintUp(PaintEventArgs e, CheckState state)
	{
		if (Control.Appearance == Appearance.Button)
		{
			new ButtonPopupAdapter(Control).PaintUp(e, Control.Checked ? CheckState.Checked : CheckState.Unchecked);
			return;
		}
		ColorData colorData = PaintPopupRender(e).Calculate();
		LayoutData layoutData = Layout(e).Layout();
		PaintButtonBackground(e, Control.ClientRectangle, null);
		PaintImage(e, layoutData);
		DrawCheckBackgroundFlat(e, layoutData.CheckBounds, colorData.ButtonShadow, colorData.Options.HighContrast ? colorData.ButtonFace : colorData.Highlight);
		DrawCheckOnly(e, layoutData, colorData.WindowText, disabledColors: true);
		AdjustFocusRectangle(layoutData);
		PaintField(e, layoutData, colorData, colorData.WindowText, drawFocus: true);
	}

	internal override void PaintOver(PaintEventArgs e, CheckState state)
	{
		if (Control.Appearance == Appearance.Button)
		{
			new ButtonPopupAdapter(Control).PaintOver(e, Control.Checked ? CheckState.Checked : CheckState.Unchecked);
			return;
		}
		ColorData colorData = PaintPopupRender(e).Calculate();
		LayoutData layoutData = Layout(e).Layout();
		PaintButtonBackground(e, Control.ClientRectangle, null);
		PaintImage(e, layoutData);
		Color checkBackground = (colorData.Options.HighContrast ? colorData.ButtonFace : colorData.Highlight);
		DrawCheckBackground3DLite(e, layoutData.CheckBounds, checkBackground, colorData, disabledColors: true);
		DrawCheckOnly(e, layoutData, colorData.WindowText, disabledColors: true);
		AdjustFocusRectangle(layoutData);
		PaintField(e, layoutData, colorData, colorData.WindowText, drawFocus: true);
	}

	internal override void PaintDown(PaintEventArgs e, CheckState state)
	{
		if (Control.Appearance == Appearance.Button)
		{
			new ButtonPopupAdapter(Control).PaintDown(e, Control.Checked ? CheckState.Checked : CheckState.Unchecked);
			return;
		}
		ColorData colorData = PaintPopupRender(e).Calculate();
		LayoutData layoutData = Layout(e).Layout();
		PaintButtonBackground(e, Control.ClientRectangle, null);
		PaintImage(e, layoutData);
		DrawCheckBackground3DLite(e, layoutData.CheckBounds, colorData.Highlight, colorData, disabledColors: true);
		DrawCheckOnly(e, layoutData, colorData.ButtonShadow, disabledColors: true);
		AdjustFocusRectangle(layoutData);
		PaintField(e, layoutData, colorData, colorData.WindowText, drawFocus: true);
	}

	protected override ButtonBaseAdapter CreateButtonAdapter()
	{
		return new ButtonPopupAdapter(Control);
	}

	protected override LayoutOptions Layout(PaintEventArgs e)
	{
		LayoutOptions layoutOptions = base.Layout(e);
		if (!Control.MouseIsDown && !Control.MouseIsOver)
		{
			layoutOptions.ShadowedText = true;
		}
		return layoutOptions;
	}
}
