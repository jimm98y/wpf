// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// From .NET's System.Windows.Forms (dotnet/winforms), adapted to the port where noted.

using System.Drawing;

namespace System.Windows.Forms.ButtonInternal;

internal sealed class CheckBoxStandardAdapter : CheckBoxBaseAdapter
{
	private new ButtonStandardAdapter ButtonAdapter => (ButtonStandardAdapter)base.ButtonAdapter;

	internal CheckBoxStandardAdapter(ButtonBase control)
		: base(control)
	{
	}

	internal override void PaintUp(PaintEventArgs e, CheckState state)
	{
		if (Control.Appearance == Appearance.Button)
		{
			ButtonAdapter.PaintUp(e, Control.CheckState);
			return;
		}
		ColorData colorData = PaintRender(e).Calculate();
		LayoutData layoutData = Layout(e).Layout();
		PaintButtonBackground(e, Control.ClientRectangle, null);
		if (!layoutData.Options.DotNetOneButtonCompat)
		{
			layoutData.TextBounds.Offset(-1, -1);
		}
		layoutData.ImageBounds.Offset(-1, -1);
		AdjustFocusRectangle(layoutData);
		if (!string.IsNullOrEmpty(Control.Text))
		{
			int num = layoutData.Focus.X & 1;
			if (!Application.RenderWithVisualStyles)
			{
				num = 1 - num;
			}
			layoutData.Focus.Offset(-(num + 1), -2);
			layoutData.Focus.Width = layoutData.TextBounds.Width + layoutData.ImageBounds.Width - 1;
			layoutData.Focus.Intersect(layoutData.TextBounds);
			if (layoutData.Options.TextAlign != (ContentAlignment)273 && layoutData.Options.UseCompatibleTextRendering && layoutData.Options.Font.Italic)
			{
				layoutData.Focus.Width += 2;
			}
		}
		PaintImage(e, layoutData);
		DrawCheckBox(e, layoutData);
		PaintField(e, layoutData, colorData, colorData.WindowText, drawFocus: true);
	}

	internal override void PaintDown(PaintEventArgs e, CheckState state)
	{
		if (Control.Appearance == Appearance.Button)
		{
			ButtonAdapter.PaintDown(e, Control.CheckState);
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
			ButtonAdapter.PaintOver(e, Control.CheckState);
		}
		else
		{
			PaintUp(e, state);
		}
	}

	internal override Size GetPreferredSizeCore(Size proposedSize)
	{
		if (Control.Appearance == Appearance.Button)
		{
			return new ButtonStandardAdapter(Control).GetPreferredSizeCore(proposedSize);
		}
		LayoutOptions layoutOptions = null;
		using (ScreenDcCache.ScreenDcScope scope = GdiCache.GetScreenHdc())
		{
			using PaintEventArgs e = new PaintEventArgs(scope, default);
			layoutOptions = Layout(e);
		}
		return layoutOptions.GetPreferredSizeCore(proposedSize);
	}

	protected override ButtonBaseAdapter CreateButtonAdapter()
	{
		return new ButtonStandardAdapter(Control);
	}

	protected override LayoutOptions Layout(PaintEventArgs e)
	{
		LayoutOptions layoutOptions = CommonLayout();
		layoutOptions.CheckPaddingSize = 1;
		layoutOptions.DotNetOneButtonCompat = !Application.RenderWithVisualStyles;
		if (Application.RenderWithVisualStyles)
		{
			using ScreenDcCache.ScreenDcScope scope = GdiCache.GetScreenHdc();
			layoutOptions.CheckSize = CheckBoxRenderer.GetGlyphSize(scope, ButtonAdapterRenderers.CheckBoxStateFrom(GetState(), isMixed: true, Control.MouseIsOver)).Width;
		}
		else
		{
			layoutOptions.CheckSize = (ScaleHelper.IsThreadPerMonitorV2Aware ? layoutOptions.CheckSize : ((int)((double)layoutOptions.CheckSize * GetDpiScaleRatio())));
		}
		return layoutOptions;
	}
}
