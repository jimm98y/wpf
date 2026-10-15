// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
// 
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
//
// Copyright (c) 2004 Novell, Inc.
//
// Authors:
//	Peter Bartok	pbartok@novell.com
//
//


// COMPLETE

using System.ComponentModel;

namespace System.Windows.Forms {
	public sealed class LayoutEventArgs : EventArgs {
		private string	affected_property;
		private IComponent affected_component;

		#region Public Constructors
		// .NET: both constructors keep the component, and AffectedControl is that component as a
		// Control, so a layout raised with an IComponent still names the control that caused it.
		public LayoutEventArgs(Control affectedControl, string affectedProperty)
			: this ((IComponent) affectedControl, affectedProperty)
		{
		}

		public LayoutEventArgs (IComponent affectedComponent, string affectedProperty)
		{
			this.affected_component = affectedComponent;
			this.affected_property = affectedProperty;
		}
		#endregion	// Public Constructors

		#region Public Instance Properties
		public IComponent AffectedComponent {
			get { return this.affected_component; }
		}

		public Control AffectedControl {
			get { return this.affected_component as Control; }
		}

		public string AffectedProperty {
			get {
				return this.affected_property;
			}
		}
		#endregion	// Public Instance Properties
	}
}
