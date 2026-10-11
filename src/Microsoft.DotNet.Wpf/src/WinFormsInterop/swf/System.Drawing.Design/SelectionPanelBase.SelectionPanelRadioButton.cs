// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
//
// Ported from dotnet/winforms (System.Windows.Forms.Design/src/System/Drawing/SelectionPanelBase.SelectionPanelRadioButton.cs).
// Shared by System.Windows.Forms and System.Design. The ShowFocusCues override lives in a
// per-assembly partial (SelectionPanelFocusCues.*.cs): Control.ShowFocusCues is protected
// INTERNAL in this port, so the override must be spelled "protected internal" inside
// System.Windows.Forms and "protected" everywhere else.

using System.Windows.Forms;

namespace System.Drawing.Design
{
	internal abstract partial class SelectionPanelBase
	{
		protected partial class SelectionPanelRadioButton : RadioButton
		{
			public SelectionPanelRadioButton ()
			{
				AutoCheck = false;
			}

			protected override bool IsInputKey (Keys keyData)
			{
				switch (keyData) {
				case Keys.Left:
				case Keys.Right:
				case Keys.Up:
				case Keys.Down:
				case Keys.Return:
					return true;
				default:
					return base.IsInputKey (keyData);
				}
			}
		}
	}
}
