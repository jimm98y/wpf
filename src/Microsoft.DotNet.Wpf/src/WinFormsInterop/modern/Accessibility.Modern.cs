// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// The accessible objects .NET's WinForms has that Mono's did not, ported from .NET's: a text box cell's and a combo box
// cell's (a data item, a combo box while it edits), and a tool strip's hosted control's, whose parent and siblings are
// its tool strip item's.

using System.Drawing;

namespace System.Windows.Forms
{
	public partial class DataGridViewTextBoxCell
	{
		protected override AccessibleObject CreateAccessibilityInstance () => new DataGridViewTextBoxCellAccessibleObject (this);

		protected class DataGridViewTextBoxCellAccessibleObject : DataGridViewCellAccessibleObject
		{
			public DataGridViewTextBoxCellAccessibleObject (DataGridViewCell owner) : base (owner)
			{
			}
		}
	}

	public partial class DataGridViewComboBoxCell
	{
		protected override AccessibleObject CreateAccessibilityInstance () => new DataGridViewComboBoxCellAccessibleObject (this);

		protected class DataGridViewComboBoxCellAccessibleObject : DataGridViewCellAccessibleObject
		{
			private readonly DataGridViewComboBoxCell _owningComboBoxCell;

			public DataGridViewComboBoxCellAccessibleObject (DataGridViewCell owner) : base (owner)
			{
				_owningComboBoxCell = owner as DataGridViewComboBoxCell;
			}

			// a combo box while its editing control is up, a cell of the table otherwise
			public override AccessibleRole Role =>
				_owningComboBoxCell?.DataGridView?.EditingControl is ComboBox && _owningComboBoxCell.DataGridView.CurrentCell == _owningComboBoxCell
					? AccessibleRole.ComboBox : base.Role;
		}
	}

	public partial class ToolStripControlHost
	{
		/// <summary>A control a tool strip hosts: its parent and siblings are its tool strip item's.</summary>
		public class ToolStripHostedControlAccessibleObject : Control.ControlAccessibleObject
		{
			private readonly ToolStripControlHost _toolStripControlHost;
			private readonly Control _toolStripHostedControl;

			public ToolStripHostedControlAccessibleObject (Control toolStripHostedControl, ToolStripControlHost toolStripControlHost) : base (toolStripHostedControl)
			{
				_toolStripControlHost = toolStripControlHost;
				_toolStripHostedControl = toolStripHostedControl;
			}

			public override AccessibleObject Parent =>
				_toolStripControlHost != null ? _toolStripControlHost.AccessibilityObject.Parent : base.Parent;

			public override AccessibleObject Navigate (AccessibleNavigation navdir)
			{
				if (_toolStripHostedControl != null && _toolStripControlHost != null) {
					switch (navdir) {
					case AccessibleNavigation.Previous:
					case AccessibleNavigation.Next:
					case AccessibleNavigation.Up:
					case AccessibleNavigation.Down:
					case AccessibleNavigation.Left:
					case AccessibleNavigation.Right:
						return _toolStripControlHost.AccessibilityObject.Navigate (navdir);
					}
				}
				return base.Navigate (navdir);
			}
		}
	}
}
