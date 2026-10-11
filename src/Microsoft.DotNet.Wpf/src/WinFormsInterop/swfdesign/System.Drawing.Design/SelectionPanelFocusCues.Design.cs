// The System.Design half of SelectionPanelBase.SelectionPanelRadioButton (the shared parts are
// link-compiled from swf/System.Drawing.Design/SelectionPanelBase*.cs): .NET's
// "protected override bool ShowFocusCues => true". Control.ShowFocusCues is protected internal in
// System.Windows.Forms, which from another assembly is overridden as plain "protected".

namespace System.Drawing.Design
{
	internal abstract partial class SelectionPanelBase
	{
		protected partial class SelectionPanelRadioButton
		{
			protected override bool ShowFocusCues {
				get { return true; }
			}
		}
	}
}
