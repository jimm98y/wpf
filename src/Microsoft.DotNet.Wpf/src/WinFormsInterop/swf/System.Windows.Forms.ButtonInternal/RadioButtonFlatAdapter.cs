// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// From .NET's System.Windows.Forms (dotnet/winforms), adapted to the port where noted.

using System.Drawing;

namespace System.Windows.Forms.ButtonInternal;

internal class RadioButtonFlatAdapter : RadioButtonBaseAdapter
{
	protected const int FlatCheckSize = 12;

	internal RadioButtonFlatAdapter(ButtonBase control)
		: base(control)
	{
	}

	internal override void PaintDown(PaintEventArgs e, CheckState state)
	{
		if (Control.Appearance == Appearance.Button)
		{
			new ButtonFlatAdapter(Control).PaintDown(e, Control.Checked ? CheckState.Checked : CheckState.Unchecked);
			return;
		}
		ColorData colorData = PaintFlatRender(e).Calculate();
		if (Control.Enabled)
		{
			PaintFlatWorker(e, colorData.WindowText, colorData.Highlight, colorData.WindowFrame, colorData);
		}
		else
		{
			PaintFlatWorker(e, colorData.ButtonShadow, colorData.ButtonFace, colorData.ButtonShadow, colorData);
		}
	}

	internal override void PaintOver(PaintEventArgs e, CheckState state)
	{
		if (Control.Appearance == Appearance.Button)
		{
			new ButtonFlatAdapter(Control).PaintOver(e, Control.Checked ? CheckState.Checked : CheckState.Unchecked);
			return;
		}
		ColorData colorData = PaintFlatRender(e).Calculate();
		if (Control.Enabled)
		{
			PaintFlatWorker(e, colorData.WindowText, colorData.LowHighlight, colorData.WindowFrame, colorData);
		}
		else
		{
			PaintFlatWorker(e, colorData.ButtonShadow, colorData.ButtonFace, colorData.ButtonShadow, colorData);
		}
	}

	internal override void PaintUp(PaintEventArgs e, CheckState state)
	{
		if (Control.Appearance == Appearance.Button)
		{
			new ButtonFlatAdapter(Control).PaintUp(e, Control.Checked ? CheckState.Checked : CheckState.Unchecked);
			return;
		}
		ColorData colorData = PaintFlatRender(e).Calculate();
		if (Control.Enabled)
		{
			PaintFlatWorker(e, colorData.WindowText, colorData.Highlight, colorData.WindowFrame, colorData);
		}
		else
		{
			PaintFlatWorker(e, colorData.ButtonShadow, colorData.ButtonFace, colorData.ButtonShadow, colorData);
		}
	}

	private void PaintFlatWorker(PaintEventArgs e, Color checkColor, Color checkBackground, Color checkBorder, ColorData colors)
	{
		LayoutData layout = Layout(e).Layout();
		PaintButtonBackground(e, Control.ClientRectangle, null);
		PaintImage(e, layout);
		DrawCheckFlat(e, layout, checkColor, colors.Options.HighContrast ? colors.ButtonFace : checkBackground, checkBorder);
		AdjustFocusRectangle(layout);
		PaintField(e, layout, colors, checkColor, drawFocus: true);
	}

	protected override ButtonBaseAdapter CreateButtonAdapter()
	{
		return new ButtonFlatAdapter(Control);
	}

	protected override LayoutOptions Layout(PaintEventArgs e)
	{
		LayoutOptions layoutOptions = CommonLayout();
		layoutOptions.CheckSize = (int)(12.0 * GetDpiScaleRatio());
		layoutOptions.ShadowedText = false;
		return layoutOptions;
	}
}
