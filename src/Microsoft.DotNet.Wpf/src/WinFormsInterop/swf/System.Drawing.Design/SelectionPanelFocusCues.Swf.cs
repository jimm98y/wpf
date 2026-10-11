// The System.Windows.Forms half of SelectionPanelBase.SelectionPanelRadioButton: .NET's
// "protected override bool ShowFocusCues => true". Control.ShowFocusCues is protected internal
// here, so inside this assembly the override keeps "internal". System.Design has its own copy
// (swfdesign/System.Drawing.Design/SelectionPanelFocusCues.Design.cs) spelled "protected".

namespace System.Drawing.Design
{
	internal abstract partial class SelectionPanelBase
	{
		protected partial class SelectionPanelRadioButton
		{
			protected internal override bool ShowFocusCues {
				get { return true; }
			}
		}
	}
}
