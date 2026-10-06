// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
//
// .NET's CustomColorDialog runs comdlg32's ChooseColor fully open (CC_FULLOPEN) from a dialog
// template copied from VB6 (colordlg.data, CC_ENABLETEMPLATEHANDLE), and hooks it: on
// WM_INITDIALOG it zeroes the edit margins of the six number boxes, disables and hides the
// "Color|Solid" swatch (COLOR_MIX) and the OK button, and starts on Color.Empty; on WM_COMMAND from
// "Add to Custom Colors" (COLOR_ADD) it reads the red/green/blue boxes, takes that as the colour and
// posts IDOK -- so the add button is the dialog's OK.
//
// The port's ColorDialog is a managed dialog, with no template and no hook procedure, so none of
// that reshaping can be applied: this opens it fully open on the same starting colour, and the
// colour the user accepts with OK comes back exactly as .NET's does. GAP: the dialog's layout is
// the port's ColorDialog's, not the VB6 template's, and OK rather than "Add to Custom Colors"
// accepts.

using System.Windows.Forms;

namespace System.Drawing.Design
{
	public partial class ColorEditor
	{
		private class CustomColorDialog : ColorDialog
		{
			public CustomColorDialog ()
			{
				// CC_FULLOPEN
				AllowFullOpen = true;
				FullOpen = true;
				// .NET's hook sets Color = Color.Empty on WM_INITDIALOG (which ColorDialog reads as black).
				Color = Color.Empty;
			}
		}
	}
}
