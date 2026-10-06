// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// From .NET's System.Windows.Forms (dotnet/winforms), adapted to the port where noted.

namespace System.Windows.Forms.ButtonInternal;

internal class RadioButtonStandardAdapter : RadioButtonBaseAdapter
{
	private new ButtonStandardAdapter ButtonAdapter => (ButtonStandardAdapter)base.ButtonAdapter;

	internal RadioButtonStandardAdapter(ButtonBase control)
		: base(control)
	{
	}

	internal override void PaintUp(PaintEventArgs e, CheckState state)
	{
		if (Control.Appearance == Appearance.Button)
		{
			ButtonAdapter.PaintUp(e, Control.Checked ? CheckState.Checked : CheckState.Unchecked);
			return;
		}
		ColorData colorData = PaintRender(e).Calculate();
		LayoutData layout = Layout(e).Layout();
		PaintButtonBackground(e, Control.ClientRectangle, null);
		PaintImage(e, layout);
		DrawCheckBox(e, layout);
		AdjustFocusRectangle(layout);
		PaintField(e, layout, colorData, colorData.WindowText, drawFocus: true);
	}

	internal override void PaintDown(PaintEventArgs e, CheckState state)
	{
		if (Control.Appearance == Appearance.Button)
		{
			ButtonAdapter.PaintDown(e, Control.Checked ? CheckState.Checked : CheckState.Unchecked);
		}
		else
		{
			PaintUp(e, state);
		}
	}

	internal override void PaintOver(PaintEventArgs e, CheckState state)
	{
		if (Control.Appearance == Appearance.Button)
		{
			ButtonAdapter.PaintOver(e, Control.Checked ? CheckState.Checked : CheckState.Unchecked);
		}
		else
		{
			PaintUp(e, state);
		}
	}

	protected override ButtonBaseAdapter CreateButtonAdapter()
	{
		return new ButtonStandardAdapter(Control);
	}

	protected override LayoutOptions Layout(PaintEventArgs e)
	{
		LayoutOptions layoutOptions = CommonLayout();
		layoutOptions.HintTextUp = false;
		layoutOptions.DotNetOneButtonCompat = !Application.RenderWithVisualStyles;
		if (Application.RenderWithVisualStyles)
		{
			ButtonBase control = Control;
			using ScreenDcCache.ScreenDcScope scope = GdiCache.GetScreenHdc();
			layoutOptions.CheckSize = RadioButtonRenderer.GetGlyphSize(scope, ButtonAdapterRenderers.RadioButtonStateFrom(GetState(), control.MouseIsOver)).Width;
		}
		else
		{
			layoutOptions.CheckSize = (int)((double)layoutOptions.CheckSize * GetDpiScaleRatio());
		}
		return layoutOptions;
	}
}
