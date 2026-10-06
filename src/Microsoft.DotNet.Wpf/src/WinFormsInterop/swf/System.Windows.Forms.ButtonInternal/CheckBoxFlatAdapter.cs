// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// From .NET's System.Windows.Forms (dotnet/winforms), adapted to the port where noted.

using System.Drawing;

namespace System.Windows.Forms.ButtonInternal;

internal class CheckBoxFlatAdapter : CheckBoxBaseAdapter
{
	private new ButtonFlatAdapter ButtonAdapter => (ButtonFlatAdapter)base.ButtonAdapter;

	internal CheckBoxFlatAdapter(ButtonBase control)
		: base(control)
	{
	}

	internal override void PaintDown(PaintEventArgs e, CheckState state)
	{
		if (Control.Appearance == Appearance.Button)
		{
			ButtonAdapter.PaintDown(e, Control.CheckState);
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
			ButtonAdapter.PaintOver(e, Control.CheckState);
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
			ButtonAdapter.PaintUp(e, Control.CheckState);
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
		DrawCheckFlat(e, layout, checkColor, colors.Options.HighContrast ? colors.ButtonFace : checkBackground, checkBorder, colors);
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
		layoutOptions.CheckSize = (int)(11.0 * GetDpiScaleRatio());
		layoutOptions.ShadowedText = false;
		return layoutOptions;
	}
}
