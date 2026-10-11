// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// From .NET's System.Windows.Forms (dotnet/winforms), adapted to the port where noted.

using System.Drawing;

namespace System.Windows.Forms.ButtonInternal;

internal abstract class CheckableControlBaseAdapter : ButtonBaseAdapter
{
	private const int StandardCheckSize = 13;

	private ButtonBaseAdapter? _buttonAdapter;

	protected ButtonBaseAdapter ButtonAdapter => _buttonAdapter ?? (_buttonAdapter = CreateButtonAdapter());

	private Appearance Appearance
	{
		get
		{
			if (Control is CheckBox checkBox)
			{
				return checkBox.Appearance;
			}
			if (Control is RadioButton radioButton)
			{
				return radioButton.Appearance;
			}
			return Appearance.Normal;
		}
	}

	internal CheckableControlBaseAdapter(ButtonBase control)
		: base(control)
	{
	}

	internal override Size GetPreferredSizeCore(Size proposedSize)
	{
		if (Appearance == Appearance.Button)
		{
			return ButtonAdapter.GetPreferredSizeCore(proposedSize);
		}
		LayoutOptions layoutOptions = null;
		using (ScreenDcCache.ScreenDcScope scope = GdiCache.GetScreenHdc())
		{
			using PaintEventArgs e = new PaintEventArgs(scope, default);
			layoutOptions = Layout(e);
		}
		return layoutOptions.GetPreferredSizeCore(proposedSize);
	}

	protected abstract ButtonBaseAdapter CreateButtonAdapter();

	internal override LayoutOptions CommonLayout()
	{
		LayoutOptions layoutOptions = base.CommonLayout();
		layoutOptions.GrowBorderBy1PxWhenDefault = false;
		layoutOptions.BorderSize = 0;
		layoutOptions.PaddingSize = 0;
		layoutOptions.MaxFocus = false;
		layoutOptions.FocusOddEvenFixup = true;
		layoutOptions.CheckSize = 13;
		return layoutOptions;
	}

	internal double GetDpiScaleRatio()
	{
		return GetDpiScaleRatio(Control);
	}

	internal static double GetDpiScaleRatio(Control? control)
	{
		return (double)((control != null && control.IsHandleCreated) ? ScaleHelper.InitialSystemDpi : ScaleHelper.InitialSystemDpi) / 96.0;
	}
}
